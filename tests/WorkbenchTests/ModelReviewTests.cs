using System.Net;
using System.Text;
using System.Text.Json;
using TSKHook;
using TSKHook.Workbench;

internal static class ModelReviewTests
{
    public static async Task<int> Run(string root)
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        void Reject(Action action, string message)
        { var rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Check(rejected, message); }
        var settings = new ModelSettings { Endpoint = "https://fixture.invalid/v1/chat/completions", Model = "review-model", BatchSize = 8, Concurrency = 2 };
        var first = Row("a", "同じ", "译文甲"); var second = Row("b", "同じ", "译文乙");
        var blank = Row("blank", "同じ", "", "new"); var excluded = Row("excluded", "同じ", "保留", "excluded");
        var work = Create(Path.Combine(root, "model-unify"), [first, second, blank, excluded]);
        var tasks = ModelReviewTasks.Create(work.Snapshot(), ModelReviewMode.Unify);
        Check(tasks.Count == 1 && tasks[0].Members.Length == 3 && tasks[0].Candidates.Length == 2, "unification gathers full editable content family and excludes excluded entries");
        var decision = Choose(tasks[0], "译文甲");
        var invented = Choose(tasks[0], "译文甲"); invented.Translation = "新造译文";
        Reject(() => ModelReviewTasks.ValidateDecision(tasks[0], invented), "unification cannot invent candidate text");
        var wrongHash = Choose(tasks[0], "译文甲"); wrongHash.SourceHash = new string('0', 64);
        Reject(() => ModelReviewTasks.ValidateDecision(tasks[0], wrongHash), "review output cannot modify source hash");
        Check(ModelReviewProtocol.Parse(JsonData.Write(new[] { new { id = 1, decision = "choose", candidate = 1, reason = "符合原文", acknowledgedIssues = Array.Empty<string>(), extra = true } }), tasks).Failures.Count == 1, "review protocol rejects extra fields");
        var applied = work.ApplyModelReviewBatch(tasks, [decision], "claude-fixture", "provider-a");
        var reread = new TranslationWorkspace(work.Root).Snapshot();
        Check(applied.ResolvedTasks == 1 && applied.SavedPositions == 3 && reread.Where(x => x.Key != "excluded").All(x => x.Translation == "译文甲" && x.Status == "translated" && ReviewQueue.HasCurrentModelReview(x)), "model decision persists as model review rather than human review");
        Check(reread.Single(x => x.Key == "excluded").Translation == "保留", "model review never changes exclusions");
        Check(Directory.GetFiles(Path.Combine(work.Root, ".local/model-reviews"), "*.json").Length == 1, "review writes recovery evidence atomically with results");
        Check(ModelReviewTasks.Create(reread, ModelReviewMode.Unify).Count == 0, "resolved family is not re-sent by batch unification");

        var human = Row("human", "同じ", "译文乙", "reviewed");
        var guarded = Create(Path.Combine(root, "model-human"), [first, human]);
        var guardedTask = ModelReviewTasks.Create(guarded.Snapshot(), ModelReviewMode.Unify).Single();
        Reject(() => ModelReviewTasks.ValidateDecision(guardedTask, Choose(guardedTask, "译文甲")), "human-confirmed wording cannot be replaced by model choice");
        guarded.ApplyModelReviewBatch([guardedTask], [Choose(guardedTask, "译文乙")], "fixture", "provider");
        Check(guarded.Snapshot().Single(x => x.Key == "human").Status == "reviewed" && guarded.Snapshot().Single(x => x.Key == "a").Translation == "译文乙", "machine positions may converge on authoritative human wording without mutating human row");

        var warning = Row("warn", "味方2人", "两名友方", "needs_review");
        var check = Create(Path.Combine(root, "model-check"), [warning]);
        var checkTask = ModelReviewTasks.Create(check.Snapshot(), ModelReviewMode.Check).Single();
        var accept = new ModelReviewDecision { Key = warning.Key, SourceHash = warning.SourceHash, Decision = "accept", Translation = warning.Translation, Reason = "日文数字2与中文两名含义一致。", AcknowledgedIssues = ["numbers"] };
        check.ApplyModelReviewBatch([checkTask], [accept], "claude-fixture", "provider");
        var accepted = new TranslationWorkspace(check.Root).Snapshot().Single();
        Check(ReviewQueue.Classify(new([accepted], false), ValidationRules.Check(accepted)) == ReviewKind.Ready && ValidationRules.Check(accepted).Count == 1, "model can explain a nonfatal false alarm while retaining diagnostics");
        accepted.Translation = "三名友方";
        Check(!ReviewQueue.HasCurrentModelReview(accepted), "review acknowledgement expires when translation changes");
        var invalidAccept = new ModelReviewDecision { Key = warning.Key, SourceHash = warning.SourceHash, Decision = "accept", Translation = "其他", Reason = "reason" };
        Reject(() => ModelReviewTasks.ValidateDecision(checkTask, invalidAccept), "accept must preserve current translation exactly");
        var noAcknowledgement = new ModelReviewDecision { Key = warning.Key, SourceHash = warning.SourceHash, Decision = "accept", Translation = warning.Translation, Reason = "看起来正确" };
        Reject(() => ModelReviewTasks.ValidateDecision(checkTask, noAcknowledgement), "remaining warning requires explicit acknowledgement");
        var fatal = Row("fatal", "<b>名前</b>", "名字", "needs_review");
        var fatalTask = ModelReviewTasks.Create([fatal], ModelReviewMode.Check).Single();
        Reject(() => ModelReviewTasks.ValidateDecision(fatalTask, new() { Key = fatal.Key, SourceHash = fatal.SourceHash, Decision = "accept", Translation = fatal.Translation, Reason = "格式无关" }), "model cannot waive fatal structural damage");
        var empty = Row("empty", "名前", "", "needs_review");
        var emptyTask = ModelReviewTasks.Create([empty], ModelReviewMode.Check).Single();
        Reject(() => ModelReviewTasks.ValidateDecision(emptyTask, new() { Key = empty.Key, SourceHash = empty.SourceHash, Decision = "accept", Translation = "", Reason = "无变化" }), "blank cannot become model reviewed");

        var fix = Row("fix", "ATK20%", "ATK21%", "needs_review");
        var revise = Create(Path.Combine(root, "model-revise"), [fix]);
        var fixture = new ReviewFixture { MakeDecision = row => new() { Key = "fixture-key", SourceHash = "fixture-hash", Decision = "revise", Translation = "ATK20%", Reason = "修正20被误译成21的数值。" } };
        var progress = new List<TranslationProgress>();
        using var http = new HttpClient(fixture);
        var result = await new ModelReviewRunner(http).RunAsync(revise, settings, "fixture-key", "fixture-provider", ModelReviewMode.Check, _ => { }, default, p => progress.Add(p));
        Check(result.ResolvedTasks == 1 && revise.Snapshot().Single().Translation == "ATK20%", "review runner applies a valid corrected translation");
        Check(fixture.Models.Single() == "review-model" && progress.Last().CompletedTasks == 1 && progress.Last().ActiveBatches == 0, "review routes selected model and reports completion");
        Check(File.ReadAllText(Path.Combine(revise.Root, ".local/model-usage.jsonl")).Contains("\"operation\":\"Check\""), "review usage is distinguished from initial translation");

        var deferWork = Create(Path.Combine(root, "model-defer"), [empty]);
        using var deferHttp = new HttpClient(new ReviewFixture());
        var deferResult = await new ModelReviewRunner(deferHttp).RunAsync(deferWork, settings, "fixture", "provider", ModelReviewMode.Check, _ => { }, default);
        Check(deferResult.DeferredTasks == 1 && deferWork.Snapshot().Single().Status == "needs_review" && !ReviewQueue.HasCurrentModelReview(deferWork.Snapshot().Single()), "uncertain decisions remain in review queue with reason");

        var stale = Create(Path.Combine(root, "model-stale"), [fix]);
        var staleFixture = new ReviewFixture { MakeDecision = fixture.MakeDecision, BeforeResponse = () => stale.Edit(fix.Key, fix.SourceHash, "人工结果20%", "reviewed") };
        using var staleHttp = new HttpClient(staleFixture);
        var staleResult = await new ModelReviewRunner(staleHttp).RunAsync(stale, settings, "fixture", "provider", ModelReviewMode.Check, _ => { }, default);
        Check(staleResult.SkippedTasks == 1 && stale.Snapshot().Single().Translation == "人工结果20%", "late model response cannot overwrite human edit");

        var cancelWork = Create(Path.Combine(root, "model-cancel"), [fix]);
        using var cancellation = new CancellationTokenSource();
        var cancelFixture = new ReviewFixture { MakeDecision = fixture.MakeDecision, BeforeResponse = cancellation.Cancel };
        using var cancelHttp = new HttpClient(cancelFixture);
        var canceled = false;
        try { await new ModelReviewRunner(cancelHttp).RunAsync(cancelWork, settings, "fixture", "provider", ModelReviewMode.Check, _ => { }, cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && cancelWork.Snapshot().Single().Translation == fix.Translation, "cancellation prevents response writeback");

        var unauthorizedWork = Create(Path.Combine(root, "model-fail"), [fix]);
        var failFixture = new ReviewFixture { Status = HttpStatusCode.Unauthorized };
        using var failedHttp = new HttpClient(failFixture);
        var logs = new List<string>();
        var failedResult = await new ModelReviewRunner(failedHttp).RunAsync(unauthorizedWork, settings, "fixture-secret", "provider", ModelReviewMode.Check, logs.Add, default);
        Check(failedResult.FailedTasks == 1 && failFixture.Models.Count == 1 && unauthorizedWork.Snapshot().Single().Translation == fix.Translation, "nontransient HTTP error fails without altering translations or repeat requests");
        Check(!string.Join("\n", logs).Contains("fixture-secret") && !File.ReadAllText(Path.Combine(unauthorizedWork.Root, ".local/model-review-failures.json")).Contains("fixture-secret"), "provider bodies and credentials never enter error logs");

        var unifyRun = Create(Path.Combine(root, "model-unify-run"), [first, second]);
        var chooser = new ReviewFixture { MakeDecision = row => new()
        { Key = "fixture-key", SourceHash = "fixture-hash", Decision = "choose", Candidate = 1,
            Translation = row.GetProperty("candidates")[0].GetProperty("translation").GetString()!, Reason = "候选准确且自然。" } };
        using var chooseHttp = new HttpClient(chooser);
        var chosen = await new ModelReviewRunner(chooseHttp).RunAsync(unifyRun, settings, "fixture", "provider", ModelReviewMode.Unify, _ => { }, default);
        Check(chosen.ResolvedTasks == 1 && unifyRun.Snapshot().Select(x => x.Translation).Distinct().Count() == 1, "candidate-aware model request unifies through full runner");
        var growing = Create(Path.Combine(root, "model-growing-family"), [first, second]);
        var capturePath = Path.Combine(growing.Root, "capture.jsonl");
        File.WriteAllText(capturePath, JsonData.Write(new CaptureRecord { SchemaVersion = 2, ApiName = "Other", Path = "$.result.detail", Context = new() { ["id"] = "99" }, Source = first.Source, SourceHash = first.SourceHash }) + "\n");
        var growingFixture = new ReviewFixture { MakeDecision = chooser.MakeDecision, BeforeResponse = () => growing.ImportCapture(capturePath) };
        using var growingHttp = new HttpClient(growingFixture);
        var growingResult = await new ModelReviewRunner(growingHttp).RunAsync(growing, settings, "fixture", "provider", ModelReviewMode.Unify, _ => { }, default);
        Check(growingResult.SkippedTasks == 1 && growing.Snapshot().Single(x => x.Key == second.Key).Translation == second.Translation, "new same-source capture invalidates pending family-wide decision");

        var catalog = new ModelCatalog(); catalog.MigrateLegacy(settings);
        Check(catalog.Providers.Count == 1 && catalog.Translation.Model == settings.Model && catalog.Review.Model == settings.Model, "legacy model config migrates without replacing endpoint or model");
        catalog.Providers[0].Models.Add("initial-model"); catalog.Translation.Model = "initial-model";
        Check(catalog.Resolve(catalog.Translation, settings).Model == "initial-model" && catalog.Resolve(catalog.Review, settings).Model == "review-model", "one provider stores independent initial and review models");
        var secondProvider = new ModelProvider { Name = "other", Endpoint = "https://other.invalid/v1/chat/completions", Models = ["review-model"] };
        catalog.Providers.Add(secondProvider); catalog.Review.ProviderId = secondProvider.Id; catalog.Validate();
        Check(catalog.Resolve(catalog.Review, settings).Endpoint == secondProvider.Endpoint && catalog.Resolve(catalog.Translation, settings).Endpoint == settings.Endpoint, "same model name stays bound to its selected provider");
        var roundtrip = JsonData.Read<ModelCatalog>(JsonData.Write(catalog));
        Check(roundtrip.Resolve(roundtrip.Review, settings).Model == "review-model" && roundtrip.Providers.Count == 2, "saved provider catalog roundtrips with role choices");
        return checks;
    }

    private static TranslationEntry Row(string key, string source, string translation, string status = "translated") =>
        new() { Key = key, Field = "detail", Category = "misc", Source = source, SourceHash = UiJsonTranslator.Sha256(source), Translation = translation, Status = status };
    private static ModelReviewDecision Choose(ModelReviewTask task, string text) => new()
    { Key = task.Key, SourceHash = task.SourceHash, Decision = "choose", Candidate = Array.IndexOf(task.Candidates, text) + 1, Translation = text, Reason = "该候选忠实且符合术语。" };
    private static TranslationWorkspace Create(string root, TranslationEntry[] rows)
    {
        var source = Path.Combine(root, "translations/ui/source"); Directory.CreateDirectory(Path.Combine(source, "by_category"));
        var text = string.Join("\n", rows.Select(JsonData.Write)) + "\n";
        File.WriteAllText(Path.Combine(source, "ui_source.jsonl"), text); File.WriteAllText(Path.Combine(source, "by_category/misc.jsonl"), text);
        return new(root);
    }
    private sealed class ReviewFixture : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public List<string> Models = [];
        public Action? BeforeResponse;
        public Func<JsonElement, ModelReviewDecision> MakeDecision = row => new()
        { Key = "fixture-key", SourceHash = "fixture-hash", Decision = "defer", Reason = "缺少语境，保留待核对。" };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Models.Add(json.RootElement.GetProperty("model").GetString()!);
            using var rows = JsonDocument.Parse(json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var output = rows.RootElement.EnumerateArray().Select(row =>
            {
                var decision = MakeDecision(row);
                var wire = new Dictionary<string, object> { ["id"] = row.GetProperty("id").GetInt32(), ["decision"] = decision.Decision,
                    ["reason"] = decision.Reason, ["acknowledgedIssues"] = decision.AcknowledgedIssues };
                if (row.GetProperty("candidates").GetArrayLength() > 0) wire["candidate"] = decision.Candidate;
                else if (decision.Decision == "revise") wire["translation"] = decision.Translation;
                return wire;
            }).ToArray(); BeforeResponse?.Invoke();
            return new(Status) { Content = new StringContent(Status == HttpStatusCode.OK ? JsonData.Write(new
            { choices = new[] { new { message = new { content = JsonData.Write(output) } } }, usage = new { prompt_tokens = 100, completion_tokens = 40, total_tokens = 140 } }) : "fixture-secret provider body", Encoding.UTF8, "application/json") };
        }
    }
}
