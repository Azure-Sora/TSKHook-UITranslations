using System.Net;
using System.Text;
using System.Text.Json;
using TSKHook;
using TSKHook.Workbench;

static class ModelReviewProtocolTests
{
    public static async Task<int> Run(string root)
    {
        int count = 0;
        void Check(bool ok, string message) { count++; if (!ok) throw new Exception(message); }
        TranslationEntry Row(string key, string source, string translation) => new() { Key = key, Source = source,
            SourceHash = UiJsonTranslator.Sha256(source), Translation = translation, Status = "translated", Category = "misc", Field = "detail" };
        var rows = new[] { Row("a", "ATK20%", "ATK提升20%"), Row("b", "ATK20%", "攻击力提升20%"),
            Row("c", "<b>名前</b>", "名字"), Row("d", "<b>名前</b>", "名称") };
        var tasks = ModelReviewTasks.Create(rows, ModelReviewMode.Unify);
        object Choice(int id, int candidate = 1) => new { id, decision = "choose", candidate, reason = "忠实保留原意。", acknowledgedIssues = Array.Empty<string>() };
        var partial = ModelReviewProtocol.Parse(JsonData.Write(new[] { Choice(2), Choice(1) }), tasks);
        Check(partial.Tasks.Length == 1 && partial.Tasks[0].Key == "a" && partial.Failures["c"].Contains("tags"), "one fatal candidate is isolated while valid out-of-order neighbour survives");
        Check(partial.Decisions[0].Translation == tasks[0].Candidates[0] && partial.Decisions[0].SourceHash == tasks[0].SourceHash, "candidate text and identity derive locally rather than model transcription");
        var duplicate = ModelReviewProtocol.Parse(JsonData.Write(new[] { Choice(1), Choice(1), new { } as object }), tasks);
        Check(duplicate.Tasks.Length == 0 && duplicate.Failures["a"].Contains("重复") && duplicate.Failures["c"].Contains("缺少"), "duplicate and missing IDs never bind by array position");
        var unknown = ModelReviewProtocol.Parse(JsonData.Write(new[] { Choice(999), Choice(1) }), tasks);
        Check(unknown.Tasks.Length == 1 && unknown.Diagnostics.Length == 1 && unknown.Failures.Count == 1, "unknown id cannot clobber valid expected item");
        foreach (var bad in new[] { "", "[", "{}", "[{\"id\":\"1\"}]", "[{\"id\":1,\"id\":1}]" })
            Check(ModelReviewProtocol.Parse(bad, tasks).Failures.Count == tasks.Count, "malformed output is rejected with per-task reasons");
        var invented = ModelReviewProtocol.Parse("[{\"id\":1,\"decision\":\"choose\",\"candidate\":1,\"translation\":\"invented\",\"reason\":\"x\",\"acknowledgedIssues\":[]}]", tasks);
        Check(invented.Tasks.Length == 0 && invented.Failures["a"].Contains("字段"), "compact selection rejects model-authored text");
        using var payload = JsonDocument.Parse(ModelCompletionClient.SerializeRequestData(ModelReviewProtocol.Payload(tasks[1], 2)));
        Check(payload.RootElement.GetProperty("candidates").EnumerateArray().All(x => x.GetProperty("issues").EnumerateArray().Any(i => i.GetProperty("kind").GetString() == "tags")), "each candidate carries its own fatal validation evidence");
        Check(!payload.RootElement.TryGetProperty("key", out _) && !payload.RootElement.TryGetProperty("sourceHash", out _), "long opaque identifiers stay local");
        var warning = Row("warn", "味方2人", "两名友方"); warning.Status = "needs_review";
        var checkTasks = ModelReviewTasks.Create([warning], ModelReviewMode.Check);
        var accepted = ModelReviewProtocol.Parse("[{\"id\":1,\"decision\":\"accept\",\"reason\":\"2人等于两名。\",\"acknowledgedIssues\":[\"numbers\"]}]", checkTasks);
        Check(accepted.Tasks.Length == 1 && accepted.Decisions[0].Translation == warning.Translation, "accept uses exact current text and explicit semantic acknowledgement");
        var unack = ModelReviewProtocol.Parse("[{\"id\":1,\"decision\":\"accept\",\"reason\":\"准确\",\"acknowledgedIssues\":[]}]", checkTasks);
        Check(unack.Tasks.Length == 0 && unack.Failures["warn"].Contains("numbers"), "nonfatal warnings still require explicit evidence");
        var instructionRoot = Path.Combine(root, "protocol-instructions"); Directory.CreateDirectory(Path.Combine(instructionRoot, "translations/ui"));
        File.WriteAllText(Path.Combine(instructionRoot, "translations/ui/LLM_TRANSLATION_GUIDE.md"), "CONFLICTING_THREE_FIELD_PROTOCOL");
        File.WriteAllText(Path.Combine(instructionRoot, "translations/ui/glossary.md"), "ATK=攻击力");
        var prompt = ModelReviewProtocol.BuildInstructions(instructionRoot, ModelReviewMode.Unify);
        Check(!prompt.Contains("CONFLICTING_THREE_FIELD_PROTOCOL") && prompt.Contains("ATK=攻击力") && prompt.Contains("不要返回translation"), "review prompt excludes initial-output protocol and keeps terminology");
        var wire = ModelCompletionClient.SerializeRequestData(new { text = "日本語与中文" });
        Check(wire.Contains("日本語与中文") && !wire.Contains("\\u"), "request JSON transmits CJK as readable Unicode");
        var many = Enumerable.Range(0, 21).Select(i => tasks[0] with { Key = "task-" + i }).ToArray();
        var batches = ModelReviewProtocol.Batch(many, 32, 100);
        Check(batches.Select(x => x.Length).SequenceEqual(new[] { 8, 8, 5 }), "review item cap does not depend on initial-translation batch setting");
        var sized = ModelReviewProtocol.Batch(tasks, 8, ModelReviewProtocol.MaximumRequestCharacters - ModelCompletionClient.SerializeRequestData(ModelReviewProtocol.Payload(tasks[0], 1)).Length - 3);
        Check(sized.Length == 2, "character budget splits independently of item count");
        var settings = new ModelSettings { Endpoint = "https://fixture.invalid/v1/chat/completions", Model = "review-test", BatchSize = 8, Concurrency = 1 };
        foreach (var malformed in new[] { false, true })
        {
            var workspace = Create(Path.Combine(root, "partial-run-" + malformed), rows);
            var handler = new Fixture(malformed ? "[" : JsonData.Write(new[] { Choice(1), Choice(2) }), malformed ? "length" : "stop");
            using var client = new HttpClient(handler); var logs = new List<string>();
            var result = await new ModelReviewRunner(client).RunAsync(workspace, settings, "private-fixture-key", "provider", ModelReviewMode.Unify, logs.Add, default);
            Check(handler.Calls == 1 && result.FailedTasks == (malformed ? 2 : 1), "invalid output never triggers a second billable call");
            Check(result.ResolvedTasks == (malformed ? 0 : 1) && workspace.Snapshot().Single(x => x.Key == "c").Translation == "名字", "valid task saves while rejected task remains unchanged");
            var evidence = Directory.GetFiles(Path.Combine(workspace.Root, ".local/model-review-responses"), "*.json");
            Check(evidence.Length == 2 && evidence.Any(x => File.ReadAllText(x).Contains(malformed ? "\"finishReason\":\"length\"" : "\"finishReason\":\"stop\"")) && evidence.All(x => !File.ReadAllText(x).Contains("private-fixture-key")), "raw paid response and diagnostic survive with finish reason and no credential");
            Check(logs.Any(x => x.Contains(malformed ? "JSON" : "tags")), "logs identify exact local rejection category");
        }
        var oversized = new[] { Row("large-a", new string('日', 45000), "甲"), Row("large-b", new string('日', 45000), "乙") };
        var largeWork = Create(Path.Combine(root, "large-review"), oversized);
        var unused = new Fixture("[]"); using var unusedClient = new HttpClient(unused);
        var tooLarge = await new ModelReviewRunner(unusedClient).RunAsync(largeWork, settings, "fixture", "provider", ModelReviewMode.Unify, _ => { }, default);
        Check(unused.Calls == 0 && tooLarge.FailedTasks == 1, "oversized single task fails locally without billable request");
        return count;
    }
    private static TranslationWorkspace Create(string path, TranslationEntry[] rows)
    {
        var source = Path.Combine(path, "translations/ui/source"); Directory.CreateDirectory(Path.Combine(source, "by_category"));
        var text = string.Join("\n", rows.Select(JsonData.Write)) + "\n";
        File.WriteAllText(Path.Combine(source, "ui_source.jsonl"), text); File.WriteAllText(Path.Combine(source, "by_category/misc.jsonl"), text); return new(path);
    }
    private sealed class Fixture(string output, string finishReason = "stop") : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonData.Write(new
            { choices = new[] { new { message = new { content = output }, finish_reason = finishReason } }, usage = new { prompt_tokens = 100, completion_tokens = 20, total_tokens = 120 } }), Encoding.UTF8, "application/json") });
        }
    }
}
