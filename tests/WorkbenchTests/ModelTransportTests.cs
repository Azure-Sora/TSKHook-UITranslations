using TSKHook;
using System.Net;
using System.Text;
using TSKHook.Workbench;

static class ModelTransportTests
{
    public static async Task<int> Run(string root)
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        var settings = new ModelSettings { Endpoint = "https://fixture.invalid/v1/chat/completions", Model = "test" };
        using var production = ModelCompletionClient.CreateHttpClient();
        Check(production.Timeout == Timeout.InfiniteTimeSpan && ModelCompletionClient.DefaultRequestTimeout == TimeSpan.FromMinutes(15), "production client delegates full response deadline to transport");
        using var slow = new HttpClient(new Handler(async token => { await Task.Delay(100, token); return Reply(); }));
        var completion = await new ModelCompletionClient(slow, TimeSpan.FromSeconds(3)).SendAsync(settings, "fixture", "", new { }, default);
        Check(completion.Content == "[]", "delayed nonstreaming response completes within full request deadline");
        using var hanging = new HttpClient(new Handler(async token => { await Task.Delay(Timeout.Infinite, token); return Reply(); }));
        try { await new ModelCompletionClient(hanging, TimeSpan.FromMilliseconds(30)).SendAsync(settings, "fixture", "", new { }, default); throw new Exception("expected timeout"); }
        catch (ModelTransportException e) { Check(e.TimedOut, "header deadline is explicitly classified as unknown remote outcome"); }
        using var body = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream()) })));
        try { await new ModelCompletionClient(body, TimeSpan.FromMilliseconds(30)).SendAsync(settings, "fixture", "", new { }, default); throw new Exception("expected body timeout"); }
        catch (ModelTransportException e) { Check(e.TimedOut, "deadline also covers response body after headers"); }
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        try { await new ModelCompletionClient(hanging).SendAsync(settings, "fixture", "", new { }, cancel.Token); throw new Exception("expected cancellation"); }
        catch (OperationCanceledException) { Check(cancel.IsCancellationRequested, "user cancellation remains cancellation"); }
        foreach (var kind in new[] { "timeout", "network", "http504" })
        {
            var source = Path.Combine(root, "transport-" + kind, "translations/ui/source");
            Directory.CreateDirectory(Path.Combine(source, "by_category"));
            var row = new TranslationEntry { Key = "test", Source = "テスト", SourceHash = UiJsonTranslator.Sha256("テスト"), Translation = "测试", Status = "needs_review", Category = "misc" };
            File.WriteAllText(Path.Combine(source, "ui_source.jsonl"), JsonData.Write(row) + "\n");
            File.WriteAllText(Path.Combine(source, "by_category/misc.jsonl"), JsonData.Write(row) + "\n");
            var workspace = new TranslationWorkspace(Path.Combine(root, "transport-" + kind));
            var handler = new Handler(_ => kind switch
            {
                "timeout" => Task.FromException<HttpResponseMessage>(new TaskCanceledException("fixture-secret")),
                "network" => Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture-secret")),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout))
            });
            using var client = new HttpClient(handler); var logs = new List<string>();
            var result = await new ModelReviewRunner(client).RunAsync(workspace, settings, "fixture-secret", "test", ModelReviewMode.Check, logs.Add, default);
            Check(handler.Calls == 1 && result.FailedTasks == 1 && result.SavedPositions == 0, kind + " does not duplicate billable request");
            Check(workspace.Snapshot().Single().Translation == "测试" && !string.Join("", logs).Contains("fixture-secret") && string.Join("", logs).Contains("未自动重发"), kind + " preserves translation and reports safe actionable failure");
        }
        return checks;
    }
    private static HttpResponseMessage Reply() => new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"[]\"}}]}", Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return respond(token); }
    }
    private sealed class SlowStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}
