using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using TSKHook.UiInspector;

var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}
void Reject(Action action, string message)
{
    try { action(); } catch { assertions++; return; }
    throw new Exception(message);
}
Check(ComponentClassifier.Classify(new[] { "TMPro.TextMeshProUGUI", "TMPro.TMP_Text", "UnityEngine.UI.MaskableGraphic" }) == UiComponentKind.Tmp,
    "TMP ancestry resolves without touching absent TextMesh type");
Check(ComponentClassifier.Classify(new[] { "Game.CustomImage", "UnityEngine.UI.Image", "UnityEngine.UI.MaskableGraphic" }) == UiComponentKind.Image,
    "Custom image subclass still resolves");
Check(ComponentClassifier.Classify(new[] { "UnityEngine.RectTransform", "UnityEngine.Transform", "UnityEngine.Component" }) == UiComponentKind.Other,
    "Non-text components never probe optional text wrappers");
Check(ComponentClassifier.Classify(new[] { "UnityEngine.TextMesh", "UnityEngine.Component" }) == UiComponentKind.TextMesh,
    "TextMesh is supported only when actually present in native ancestry");
var node = new UiNode { Scene = "Home", Path = "/Canvas[0]/Menu[2]" };
var text = new UiText { Component = "TMPro.TextMeshProUGUI", ComponentIndex = 1, CurrentText = "閉じる" };
node.Texts.Add(text);
var snapshot = new UiSnapshot { Nodes = new() { node } };
var template = OverrideRules.Template(snapshot);
Check(!OverrideRules.Matches(template.Entries[0], node, text), "Empty template must not change text");
template.Entries[0].Translation = "关闭";
var roundtrip = OverrideRules.Parse(JsonSerializer.Serialize(template, InspectionJson.Options));
var rule = roundtrip.Entries[0];
Check(OverrideRules.Matches(rule, node, text), "Exact scoped source should match");
text.CurrentText = "別の文字";
Check(!OverrideRules.Matches(rule, node, text), "Changed text must not be overwritten");
text.CurrentText = "閉じる";
node.Scene = "Battle";
Check(!OverrideRules.Matches(rule, node, text), "Same text in another scene must not match");
node.Scene = "Home"; node.Path += "/Other[0]";
Check(!OverrideRules.Matches(rule, node, text), "Same text on a different node must not match");
Check(OverrideRules.Segment("a/b[0]%", 1) == "a%2Fb%5B0%5D%25[1]", "Names cannot inject hierarchy segments");
roundtrip.Entries.Add(rule);
Reject(() => OverrideRules.Parse(JsonSerializer.Serialize(roundtrip, InspectionJson.Options)), "Duplicates must reject the whole file");
Reject(() => OverrideRules.Parse("{}"), "Missing schema must reject");
Reject(() => OverrideRules.Parse("{\"schemaVersion\":2,\"entries\":[]}"), "Unknown schema must reject");
Reject(() => OverrideRules.Parse("{\"schemaVersion\":1,\"entries\":[{}]}"), "Broad unscoped rule must reject");

var queue = new CommandQueue();
var calls = 0;
Task<BridgeReply> Execute(CommandRequest request) { calls++; return Task.FromResult(BridgeReply.Json(new { ok = true })); }
var cancelled = queue.Submit(new() { Id = "cancelled", Command = "click" });
Check(cancelled.Job.CancelPending(), "Pending job should cancel");
queue.Pump(Execute);
Check(calls == 0 && cancelled.Completion.IsCanceled, "Cancelled operation must never execute later");
var completed = queue.Submit(new() { Id = "once", Command = "click" });
queue.Pump(Execute);
await completed.Completion;
Check(calls == 1 && completed.Job.State == "completed", "Accepted operation runs once");
Reject(() => queue.Submit(new() { Id = "once", Command = "click" }), "Duplicate request cannot replay an action");
var waiter = new TaskCompletionSource<BridgeReply>();
var running = queue.Submit(new() { Id = "running", Command = "screenshot" });
queue.Pump(_ => waiter.Task);
Check(!running.Job.CancelPending() && running.Job.State == "running", "Started work must not claim to be cancelled");
waiter.SetResult(BridgeReply.Json(new { ok = true }));
await running.Completion;
Check(running.Job.State == "completed", "Async completion must be observable");
var failed = queue.Submit(new() { Id = "failure", Command = "click" });
queue.Pump(_ => throw new InvalidOperationException("blocked"));
try { await failed.Completion; } catch { }
Check(failed.Job.State == "failed" && failed.Job.Error == "blocked", "Failures must surface to caller and status");
var stopped = queue.Submit(new() { Id = "stopped", Command = "click" });
queue.Stop();
queue.Pump(Execute);
Check(stopped.Completion.IsCanceled && calls == 1, "Shutdown cancels pending actions");
Reject(() => queue.Submit(new() { Id = "after-stop", Command = "click" }), "Shutdown refuses new work");

HttpCommandReader.Request ReadHttp(string message) => HttpCommandReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(message)));
var body = "{\"command\":\"ui\",\"text\":\"閉じる\"}";
var request = ReadHttp($"POST /command HTTP/1.1\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nX-UI-Inspector-Token: abc\r\n\r\n{body}");
Check(Encoding.UTF8.GetString(request.Body) == body, "HTTP length counts UTF8 bytes");
Reject(() => ReadHttp("POST /command HTTP/1.1\r\nContent-Length: 10\r\n\r\n{}"), "Truncated HTTP bodies must reject");
Reject(() => ReadHttp("POST /command HTTP/1.1\r\nContent-Length: -1\r\n\r\n"), "Negative length must reject");
Reject(() => ReadHttp("POST /command HTTP/1.1\r\nContent-Length: 4194305\r\n\r\n"), "Oversized body must reject before allocation");
Reject(() => ReadHttp("POST /command HTTP/1.1\r\nContent-Length: 0\r\ncontent-length: 0\r\n\r\n"), "Ambiguous duplicated length must reject");
Reject(() => ReadHttp("POST /command HTTP/1.1\r\nContent-Length: 0\r\nTransfer-Encoding: chunked\r\n\r\n"), "Chunked bodies must reject");

// Real loopback HTTP with the actual server, using a simulated game-thread pump.
var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();
var discovery = Path.GetTempFileName();
var liveQueue = new CommandQueue();
using (var bridge = new BridgeServer(liveQueue, port, discovery))
using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }))
{
    using var info = JsonDocument.Parse(File.ReadAllText(discovery));
    var token = info.RootElement.GetProperty("token").GetString();
    async Task<HttpResponseMessage> Post(string id, string command, string suppliedToken, bool origin = false, string arguments = "{}")
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/command");
        message.Headers.Add("X-UI-Inspector-Token", suppliedToken);
        if (origin) message.Headers.Add("Origin", "https://example.invalid");
        message.Content = new StringContent($"{{\"id\":\"{id}\",\"command\":\"{command}\",\"arguments\":{arguments}}}", Encoding.UTF8, "application/json");
        return await http.SendAsync(message);
    }
    using var denied = await Post("bad", "ping", "wrong");
    Check(denied.StatusCode == HttpStatusCode.Forbidden, "Actual server refuses wrong token");
    using var originDenied = await Post("origin", "ping", token, true);
    Check(originDenied.StatusCode == HttpStatusCode.Forbidden, "Browser-origin command is refused even with token");
    var liveCalls = 0;
    using var pumpLifetime = new CancellationTokenSource();
    var pump = Task.Run(async () =>
    {
        while (!pumpLifetime.IsCancellationRequested)
        {
            liveQueue.Pump(_ => { liveCalls++; return Task.FromResult(BridgeReply.Json(new { pong = true })); });
            await Task.Delay(5);
        }
    });
    using var response = await Post("live", "ping", token);
    Check(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("pong"), "Actual authenticated HTTP request reaches pump");
    using var duplicate = await Post("live", "ping", token);
    Check(!duplicate.IsSuccessStatusCode && liveCalls == 1, "Actual repeated HTTP request cannot replay operation");
    using var statusResponse = await Post("status-query", "status", token, arguments: "{\"requestId\":\"live\"}");
    Check((await statusResponse.Content.ReadAsStringAsync()).Contains("completed") && liveCalls == 1, "Actual status request does not execute an action");
    pumpLifetime.Cancel();
    await pump;
    using var expired = await Post("expired", "click", token);
    Check(expired.StatusCode == HttpStatusCode.GatewayTimeout, "Unpumped HTTP command must time out as cancelled");
    liveQueue.Pump(_ => { liveCalls++; return Task.FromResult(BridgeReply.Json(new { ok = true })); });
    Check(liveCalls == 1, "Timed-out HTTP action must not execute when the main-thread pump resumes");
}
File.Delete(discovery);
Console.WriteLine($"UiInspector: {assertions} assertions passed.");

// Server logging stand-in; no Unity game process or native API is loaded by these checks.
namespace TSKHook.UiInspector
{
    internal sealed class InspectorPlugin
    {
        internal static InspectorPlugin Instance = new();
        internal FixtureLog Log = new();
    }
    internal sealed class FixtureLog { public void LogWarning(string value) => Console.Error.WriteLine(value); }
}
