using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TSKHook;
using TSKHook.Workbench;

var root = Path.Combine(Path.GetTempPath(), "tsk-workbench-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var tests = 0;
try
{
    var workspace = new TranslationWorkspace(root);
    var capture = Path.Combine(root, "capture.jsonl");
    File.WriteAllText(capture, Capture(1, "リンゴ") + "\n" + Capture(2, "リンゴ") + "\n");
    Check(workspace.ImportCapture(capture).Added == 2, "distinct identities with same text retained");
    Check(workspace.ImportCapture(capture).Read == 0, "cursor makes repeated scan a no-op");
    var row = workspace.Snapshot().Single(x => x.Identity["item_id"] == "1");
    workspace.Edit(row.Key, row.SourceHash, "苹果", "reviewed");
    File.AppendAllText(capture, Capture(3, "リンゴ") + "\n");
    var reuse = workspace.ImportCapture(capture);
    Check(reuse.Reused == 1 && workspace.Snapshot().Count == 3, "contextual reuse preserves all previous identities");
    Check(workspace.Snapshot().Single(x => x.Identity["item_id"] == "3").Status == "translated", "reuse is not promoted to reviewed");
    File.AppendAllText(capture, Capture(1, "大きなリンゴ") + "\n");
    Check(workspace.ImportCapture(capture).Changed == 1, "changed source recognized");
    var stale = workspace.Snapshot().Single(x => x.Key == row.Key);
    Check(stale.Status == "stale" && stale.Translation == "苹果" && stale.PreviousSourceHash == row.SourceHash, "changed source retains old translation and provenance");
    var partial = Capture(4, "果物");
    File.AppendAllText(capture, partial[..(partial.Length / 2)]);
    Check(workspace.ImportCapture(capture).Read == 0, "incomplete tail waits");
    File.AppendAllText(capture, partial[(partial.Length / 2)..] + "\n");
    Check(workspace.ImportCapture(capture).Added == 1, "completed tail imported once");
    var reopened = new TranslationWorkspace(root);
    Check(reopened.Snapshot().Count == 4, "workspace roundtrip after import");
    File.AppendAllText(capture, Capture(5, "果物") + "\n" + Capture(5, "果実") + "\n");
    Check(workspace.ImportCapture(capture).Conflicts == 2 && workspace.Snapshot().Count(x => x.Status == "conflict") == 2, "ambiguous stable identity quarantined");
    var before = workspace.Snapshot().Single(x => x.Identity["item_id"] == "2");
    workspace.Edit(before.Key, before.SourceHash, "人工苹果", "reviewed");
    Check(workspace.ApplyMachineResults([before], new Dictionary<string, string> { [before.Key] = "机器苹果" }) == 0, "human edit wins over in-flight model result");
    var draft = workspace.Snapshot().Single(x => x.Identity["item_id"] == "4");
    Check(workspace.ApplyMachineResults([draft], new Dictionary<string, string> { [draft.Key] = "水果99" }) == 1, "machine result persisted");
    Check(workspace.Snapshot().Single(x => x.Key == draft.Key).Status == "needs_review", "number differences quarantined");
    var compiled = workspace.Compile(Path.Combine(root, "compiled/ui_translations.jsonl"));
    Check(compiled.Entries == 2, "stale, conflict and needs_review excluded from runtime");
    var package = workspace.BuildPackage("test-1", "Azure-Sora/TSKHook-UITranslations");
    TranslationPackageStore.Verify(package.Compilation.AssetPath, package.Manifest);
    Check(package.Manifest.Entries == 2, "workbench package accepted by plugin verifier");
    using var modelClient = new HttpClient(new ModelFixture());
    var runner = new TranslationRunner(modelClient);
    var states = new List<TranslationProgress>();
    var saved = await runner.RunAsync(workspace, new ModelSettings { Endpoint = "https://fixture/chat", Model = "fixture", Concurrency = 1 }, "fixture-not-a-secret", false, _ => { }, default, states.Add);
    Check(states.Any(x => x.ActiveBatches == 1 && x.CompletedTasks == 0), "progress reports waiting before response");
    Check(states[^1] is { TotalTasks: 1, CompletedTasks: 1, CompletedBatches: 1, ActiveBatches: 0, SavedPositions: 1, FailedTasks: 0 }, "completion progress matches persisted positions");
    Check(saved == 1 && workspace.Snapshot().Single(x => x.Key == stale.Key).Status == "translated", "model protocol end-to-end translates only eligible rows");
    Check(File.ReadAllText(Path.Combine(root, ".local/model-usage.jsonl")).Contains("\"promptTokens\":100"), "provider-reported usage recorded separately");
    File.AppendAllText(capture, Capture(6, "星") + "\n"); workspace.ImportCapture(capture);
    using var denied = new HttpClient(new ModelFixture { Status = HttpStatusCode.Unauthorized });
    var deniedRunner = new TranslationRunner(denied);
    states.Clear();
    Check(await deniedRunner.RunAsync(workspace, new ModelSettings { Endpoint = "https://fixture/chat", Model = "fixture" }, "fixture", false, _ => { }, default, states.Add) == 0, "provider rejection preserves untranslated text");
    Check(states[^1] is { CompletedTasks: 1, FailedTasks: 1, SavedPositions: 0, ActiveBatches: 0 }, "failure completes processing but does not claim saved translation");
    Check(File.ReadAllText(Path.Combine(root, ".local/translation-failures.json")).Contains("HTTP 401"), "failed queue persisted without response body");
    states.Clear();
    Check(await runner.RunAsync(workspace, new ModelSettings { Endpoint = "https://fixture/chat", Model = "fixture" }, "fixture", false, _ => { }, default, states.Add) == 0, "automatic run does not repeatedly bill failed rows");
    Check(states.Single().TotalTasks == 0, "empty queue reports explicit zero without model request");
    using var cancel = new CancellationTokenSource();
    states.Clear();
    var cancelled = false;
    try { await runner.RunAsync(workspace, new ModelSettings { Endpoint = "https://fixture/chat", Model = "fixture" }, "fixture", true, _ => { }, cancel.Token,
        state => { states.Add(state); if (state.ActiveBatches > 0) cancel.Cancel(); }); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && states[^1] is { CompletedTasks: 0, ActiveBatches: 0, FailedTasks: 0 }, "cancellation clears in-flight progress without counting failure or completion");
    var attempt = 0;
    using var retryClient = new HttpClient(new ModelFixture { RewriteOutput = text => ++attempt == 1 ? "invalid json" : text });
    states.Clear();
    await new TranslationRunner(retryClient).RunAsync(workspace, new ModelSettings { Endpoint = "https://fixture/chat", Model = "fixture" }, "fixture", true, _ => { }, default, states.Add);
    Check(attempt == 2 && states.Any(x => x.Message.Contains("秒后重试")) && states[^1].CompletedTasks == 1, "retry is visible and tasks are counted only once");
    File.AppendAllText(capture, Capture(7, "月") + "\n"); workspace.ImportCapture(capture);
    Check(await runner.RunAsync(workspace, new ModelSettings { Endpoint = "https://fixture/chat", Model = "fixture" }, "fixture", true, _ => { }, default) == 1, "explicit retry resumes failed work");
    var category = Directory.GetFiles(workspace.CategoryDirectory).First();
    File.AppendAllText(category, "\n");
    var rejected = false;
    try { workspace.Compile(Path.Combine(root, "after-edit.jsonl")); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "external edits cannot be overwritten by a stale workspace");
    if (args.Length > 0)
    {
        var actual = new TranslationWorkspace(args[0]);
        var output = Path.Combine(root, "baseline/ui_translations.jsonl");
        var result = actual.Compile(output);
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[0], "docs/migration-baseline.json")));
        var isBaseline = provenance.RootElement.GetProperty("files").EnumerateArray().Where(x => x.GetProperty("path").GetString()!.StartsWith("translations/ui/source/"))
            .All(x => File.Exists(Path.Combine(args[0], x.GetProperty("path").GetString()!)) &&
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(args[0], x.GetProperty("path").GetString()!)))).Equals(x.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase));
        if (isBaseline)
        {
            Check(result.Entries == 45414, "real migration entry count");
            var original = Path.Combine(args[0], "translations/ui/runtime/ui_translations.jsonl");
            Check(File.ReadAllText(original).Replace("\r\n", "\n") == File.ReadAllText(output).Replace("\r\n", "\n"), "new compiler matches migration baseline (platform newlines normalized)");
        }
        else Check(result.Entries > 0, "current translation workspace compiles after baseline evolves");
        var staticIndex = RuntimeAsset.Load(output);
        foreach (var song in actual.Snapshot().Where(x => x.Category == "item" && x.Field == "item_name" &&
                     x.Identity.GetValueOrDefault("item_type") == "23"))
        {
            Check(song.Translation == song.Source && song.Status is "reviewed" or "verified",
                "song titles retain reviewed original names: " + song.Key);
            Check(!UiRenderedTextTranslator.TryTranslate(song.Source, staticIndex, out _),
                "music titles cannot inherit translated record names through rendered hash fallback: " + song.Key);
        }
        foreach (var staticCatalogRow in StaticUiCatalog.Read(args[0]))
        {
            var sample = staticCatalogRow.Source; var expected = staticCatalogRow.Translation;
            if (staticCatalogRow.Template)
                for (var parameter = 0; parameter < 4; parameter++)
                {
                    sample = sample.Replace("{" + parameter + "}", (37 + parameter).ToString());
                    expected = expected.Replace("{" + parameter + "}", (37 + parameter).ToString());
                }
            Check(UiRenderedTextTranslator.TryTranslate(sample, staticIndex, out var rendered) && rendered == expected,
                "real static UI catalog row roundtrip: " + staticCatalogRow.Source);
        }
    }
    tests += await GroupAndSkillTests.Run(root);
    tests += await ReviewTests.Run(root);
    tests += IdentityRepairTests.Run(root);
    tests += StaticUiTests.Run(root);
    tests += await ModelReviewTests.Run(root);
    tests += await ModelTransportTests.Run(root);
    tests += await ModelReviewProtocolTests.Run(root);
    Console.WriteLine($"PASS: {tests} workbench assertions; model and HTTP use fixtures, no live API calls");
}
finally { Directory.Delete(root, true); }
void Check(bool value, string message) { tests++; if (!value) throw new Exception(message); }
string Capture(int id, string text) => JsonData.Write(new CaptureRecord { SchemaVersion = 2, ApiName = "ItemList", Path = "$.result.item_list[].item_name",
    Context = new() { ["item_id"] = id.ToString() }, Source = text, SourceHash = UiJsonTranslator.Sha256(text), FirstSeenUtc = DateTime.UtcNow.ToString("O") });
sealed class ModelFixture : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public int RowsReceived;
    public Action? BeforeResponse;
    public Func<string, string> Translate { get; set; } = _ => "测试译文";
    public Func<string, string> RewriteOutput { get; set; } = text => text;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var requestJson = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var rows = JsonData.Read<List<TranslationEntry>>(requestJson.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        RowsReceived += rows.Count;
        BeforeResponse?.Invoke();
        var translated = JsonData.Write(rows.Select(x => new { x.Key, x.SourceHash, translation = Translate(x.Source) }));
        return new HttpResponseMessage(Status) { RequestMessage = request, Content = new StringContent(JsonData.Write(new
        { choices = new[] { new { message = new { content = RewriteOutput(translated) } } }, usage = new { prompt_tokens = 100, completion_tokens = 20, total_tokens = 120 } }), Encoding.UTF8, "application/json") };
    }
}
