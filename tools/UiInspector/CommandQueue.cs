using System.Collections.Concurrent;
using System.Text.Json;

namespace TSKHook.UiInspector;

public sealed class CommandRequest
{
    public string Id { get; set; }
    public string Command { get; set; }
    public JsonElement Arguments { get; set; }
}

public sealed record BridgeReply(byte[] Body, string ContentType)
{
    public static BridgeReply Json(object value) => new(JsonSerializer.SerializeToUtf8Bytes(value, InspectionJson.Options), "application/json; charset=utf-8");
}

// No Unity dependencies: timeout cancellation and duplicate suppression are testable outside the game.
public sealed class CommandJob
{
    private int state; // 0 queued, 1 running, 2 completed, 3 cancelled, 4 failed
    private TaskCompletionSource<BridgeReply> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DateTime deadline = DateTime.UtcNow.AddSeconds(12);
    public CommandRequest Request { get; }
    public string Error { get; private set; }
    public string State => Volatile.Read(ref state) switch { 0 => "queued", 1 => "running", 2 => "completed", 3 => "cancelled", _ => "failed" };
    public bool Finished => Volatile.Read(ref state) >= 2;
    public Task<BridgeReply> Completion => completion.Task;

    public CommandJob(CommandRequest request) => Request = request;

    public bool CancelPending()
    {
        if (Interlocked.CompareExchange(ref state, 3, 0) != 0) return false;
        Request.Arguments = default;
        Interlocked.Exchange(ref completion, null)?.TrySetCanceled();
        return true;
    }

    public void Start(Func<CommandRequest, Task<BridgeReply>> execute)
    {
        if (DateTime.UtcNow >= deadline) { CancelPending(); return; }
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
        // execute() starts here on the main thread. Only completion bookkeeping may resume elsewhere.
        _ = FinishAsync(execute);
    }

    private async Task FinishAsync(Func<CommandRequest, Task<BridgeReply>> execute)
    {
        try
        {
            var reply = await execute(Request).ConfigureAwait(false);
            Volatile.Write(ref state, 2);
            Interlocked.Exchange(ref completion, null)?.TrySetResult(reply);
        }
        catch (Exception e)
        {
            Error = e.Message;
            Volatile.Write(ref state, 4);
            Interlocked.Exchange(ref completion, null)?.TrySetException(e);
        }
        finally { Request.Arguments = default; } // History retains status, not multi-megabyte rule payloads.
    }

    public object Status() => new { requestId = Request.Id, command = Request.Command, state = State, error = Error };
}

public sealed class CommandQueue
{
    private readonly ConcurrentQueue<CommandJob> pending = new();
    private readonly Dictionary<string, CommandJob> history = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private bool stopped;

    public (CommandJob Job, Task<BridgeReply> Completion) Submit(CommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 80 || string.IsNullOrWhiteSpace(request.Command) || request.Command.Length > 64)
            throw new InvalidDataException("id (max 80 characters) and command are required.");
        lock (gate)
        {
            if (stopped) throw new InvalidOperationException("Bridge stopped.");
            if (history.TryGetValue(request.Id, out var previous))
                throw new InvalidOperationException("Duplicate request id; query status instead. Previous state: " + previous.State);
            if (history.Count >= 256)
            {
                foreach (var key in history.Where(p => p.Value.Finished).Take(64).Select(p => p.Key).ToArray()) history.Remove(key);
                if (history.Count >= 256) throw new InvalidOperationException("Too many outstanding requests.");
            }
            if (pending.Count >= 32) throw new InvalidOperationException("Main-thread queue is full.");
            var job = new CommandJob(request);
            var task = job.Completion;
            history.Add(request.Id, job);
            pending.Enqueue(job);
            return (job, task);
        }
    }

    public object Status(string id)
    {
        lock (gate) return history.TryGetValue(id ?? "", out var job) ? job.Status() : new { requestId = id, state = "unknown" };
    }

    public void Pump(Func<CommandRequest, Task<BridgeReply>> execute)
    {
        for (var i = 0; i < 4 && pending.TryDequeue(out var job); i++) job.Start(execute);
    }

    public void Stop()
    {
        lock (gate)
        {
            stopped = true;
            while (pending.TryDequeue(out var job)) job.CancelPending();
        }
    }
}
