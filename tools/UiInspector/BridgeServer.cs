using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TSKHook.UiInspector;

internal sealed class BridgeServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly CommandQueue queue;
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim clients = new(8);
    private readonly CancellationTokenSource lifetime = new();

    internal BridgeServer(CommandQueue queue, int port, string discovery)
    {
        this.queue = queue;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(8);
        try
        {
            File.WriteAllText(discovery, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, port, token, pid = Environment.ProcessId, plugin = "TSKHook.UiInspector", version = "0.1.2"
            }, InspectionJson.Options), new UTF8Encoding(false));
            _ = AcceptAsync();
        }
        catch { listener.Stop(); throw; }
    }

    private async Task AcceptAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                if (!clients.Wait(0)) { client.Dispose(); continue; }
                _ = Task.Run(() => Serve(client));
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception e) { InspectorPlugin.Instance.Log.LogWarning("[UI Inspector] Listener stopped: " + e.Message); break; }
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 10000;
                var request = HttpCommandReader.Read(stream);
                if (request.Headers.ContainsKey("Origin") || !request.Headers.TryGetValue("X-UI-Inspector-Token", out var supplied) ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(supplied)))
                {
                    Send(stream, 403, BridgeReply.Json(new { error = "Local discovery token required; browser-origin requests are rejected." }));
                    return;
                }
                var command = JsonSerializer.Deserialize<CommandRequest>(request.Body, InspectionJson.Options)
                    ?? throw new InvalidDataException("Missing command.");
                if (command.Command == "status")
                {
                    var id = command.Arguments.GetProperty("requestId").GetString();
                    Send(stream, 200, BridgeReply.Json(queue.Status(id)));
                    return;
                }
                var ticket = queue.Submit(command);
                try
                {
                    var reply = ticket.Completion.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token).GetAwaiter().GetResult();
                    Send(stream, 200, reply);
                }
                catch (Exception e) when (e is TimeoutException or OperationCanceledException)
                {
                    var cancelled = ticket.Job.CancelPending() || ticket.Job.State == "cancelled";
                    Send(stream, cancelled ? 504 : 409, BridgeReply.Json(new
                    {
                        error = cancelled ? "Pending operation cancelled; it will not execute later." : "Operation already started; query status, do not blindly retry.",
                        requestId = command.Id, state = ticket.Job.State
                    }));
                }
                catch (Exception e) { Send(stream, 422, BridgeReply.Json(new { error = e.Message, requestId = command.Id, state = ticket.Job.State })); }
            }
            catch (Exception e)
            {
                try { Send(client.GetStream(), 400, BridgeReply.Json(new { error = e.Message })); } catch { /* Disconnected caller. */ }
            }
            finally { clients.Release(); }
        }
    }

    private static void Send(Stream stream, int status, BridgeReply reply)
    {
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Type: {reply.ContentType}\r\nContent-Length: {reply.Body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        stream.Write(header);
        stream.Write(reply.Body);
    }

    public void Dispose()
    {
        lifetime.Cancel();
        listener.Stop();
        queue.Stop();
    }
}

// A small bounded HTTP/1 reader; no URLACL/admin rights or external server package needed.
public static class HttpCommandReader
{
    public sealed record Request(Dictionary<string, string> Headers, byte[] Body);

    public static Request Read(Stream stream)
    {
        using var header = new MemoryStream();
        var tail = 0;
        while (tail != 0x0d0a0d0a)
        {
            var value = stream.ReadByte();
            if (value < 0) throw new EndOfStreamException("Incomplete HTTP header.");
            header.WriteByte((byte)value);
            tail = unchecked((tail << 8) | value);
            if (header.Length > 16384) throw new InvalidDataException("HTTP header exceeds 16 KiB.");
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
        if (lines[0] != "POST /command HTTP/1.1") throw new InvalidDataException("Only POST /command HTTP/1.1 is supported.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim()))
                throw new InvalidDataException("Invalid or duplicate HTTP header.");
        }
        if (headers.ContainsKey("Transfer-Encoding") || !headers.TryGetValue("Content-Length", out var lengthText) ||
            !int.TryParse(lengthText, out var length) || length < 0 || length > 4 * 1024 * 1024)
            throw new InvalidDataException("Content-Length must be 0..4 MiB; chunked requests are unsupported.");
        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var count = stream.Read(body, offset, length - offset);
            if (count <= 0) throw new EndOfStreamException("Incomplete HTTP body.");
            offset += count;
        }
        return new Request(headers, body);
    }
}
