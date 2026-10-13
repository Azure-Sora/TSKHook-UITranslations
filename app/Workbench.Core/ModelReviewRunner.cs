using System.Collections.Concurrent;

namespace TSKHook.Workbench;

public sealed class ModelReviewRunner(HttpClient client)
{
    public async Task<ModelReviewRunResult> RunAsync(TranslationWorkspace workspace, ModelSettings settings, string apiKey,
        string provider, ModelReviewMode mode, Action<string> log, CancellationToken token,
        Action<TranslationProgress>? report = null, IReadOnlyList<TranslationEntry>? selected = null)
    {
        ModelCompletionClient.Validate(settings, apiKey);
        token.ThrowIfCancellationRequested();
        var tasks = ModelReviewTasks.Create(workspace.Snapshot(), mode, selected);
        var instructions = ModelReviewProtocol.BuildInstructions(workspace.Root, mode);
        var batches = ModelReviewProtocol.Batch(tasks, settings.BatchSize, instructions.Length).Select((rows, i) => (Rows: rows, Number: i + 1)).ToArray();
        var failurePath = Path.Combine(workspace.Root, ".local/model-review-failures.json");
        var failures = new ConcurrentDictionary<string, string>(File.Exists(failurePath) ?
            JsonData.Read<Dictionary<string, string>>(File.ReadAllText(failurePath)) : new Dictionary<string, string>());
        int completed = 0, completedBatches = 0, active = 0, saved = 0, resolved = 0, deferred = 0, failed = 0, skipped = 0;
        var stateGate = new object(); var writeGate = new object();
        void Report(string message)
        {
            lock (stateGate) report?.Invoke(new(tasks.Count, completed, batches.Length, completedBatches, active, saved, failed + skipped, message));
        }
        Report(tasks.Count == 0 ? "没有符合条件的核对任务。" : $"已整理 {tasks.Count:N0} 个任务，按最多 {Math.Min(settings.BatchSize, ModelReviewProtocol.MaximumBatchSize)} 项及文本长度拆为 {batches.Length} 批；核对失败不自动重发。");
        if (tasks.Count == 0) return new(0, 0, 0, 0, 0);
        await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = settings.Concurrency, CancellationToken = token }, async (batch, cancellation) =>
        {
            lock (stateGate) active++;
            Report($"核对第 {batch.Number}/{batches.Length} 批：等待 {settings.Model} 回复（{batch.Rows.Length} 个任务）。");
            var finished = false;
            var responseId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N");
            var diagnosticPath = Path.Combine(workspace.Root, ".local/model-review-responses", responseId + ".json");
            try
            {
                var payload = batch.Rows.Select((task, index) => ModelReviewProtocol.Payload(task, index + 1)).ToArray();
                if (instructions.Length + ModelCompletionClient.SerializeRequestData(payload).Length > ModelReviewProtocol.MaximumRequestCharacters)
                {
                    foreach (var task in batch.Rows) failures[Identity(task)] = "单个任务或核对参考资料超过请求文本上限；未调用模型";
                    lock (stateGate) failed += batch.Rows.Length;
                    log($"核对第 {batch.Number} 批：内容超过安全分批上限，未调用模型，请单独检查长文本或参考资料。");
                    finished = true; return;
                }
                // One outbound call per batch. Invalid output never triggers a billable replay.
                var completion = await new ModelCompletionClient(client).SendAsync(settings, apiKey, instructions, payload, cancellation);
                lock (writeGate)
                {
                    var usagePath = Path.Combine(workspace.Root, ".local/model-usage.jsonl");
                    Directory.CreateDirectory(Path.GetDirectoryName(usagePath)!);
                    File.AppendAllText(usagePath, JsonData.Write(new { time = DateTime.UtcNow, settings.Model, provider, operation = mode.ToString(),
                        batch = batch.Rows.Length, attempt = 1, responseId, promptTokens = completion.InputTokens,
                        completionTokens = completion.OutputTokens, totalTokens = completion.TotalTokens, completion.FinishReason }) + Environment.NewLine);
                    // Preserve paid model content before parsing. Never store credentials, headers or provider error bodies.
                    AtomicData.Write(diagnosticPath, JsonData.Write(new { protocol = 2, time = DateTime.UtcNow, settings.Model, provider, mode,
                        payload, mapping = batch.Rows.Select((task, index) => new { id = index + 1, task.Key, task.SourceHash }),
                        content = completion.Content.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal), completion.FinishReason,
                        completion.InputTokens, completion.OutputTokens, completion.TotalTokens }));
                }
                var parsed = ModelReviewProtocol.Parse(completion.Content, batch.Rows);
                var diagnosticSummary = new
                {
                    valid = parsed.Tasks.Length, failures = parsed.Failures, parsed.Diagnostics, completion.FinishReason,
                    ids = batch.Rows.Select((task, index) => new { id = index + 1, task.Key })
                };
                AtomicData.Write(diagnosticPath + ".validation.json", JsonData.Write(diagnosticSummary));
                foreach (var task in batch.Rows)
                {
                    if (parsed.Failures.TryGetValue(task.Key, out var reason)) failures[Identity(task)] = reason;
                    else failures.TryRemove(Identity(task), out _);
                }
                cancellation.ThrowIfCancellationRequested();
                var result = parsed.Tasks.Length == 0 ? new ModelReviewApplyResult(0, 0, 0, 0)
                    : workspace.ApplyModelReviewBatch(parsed.Tasks, parsed.Decisions, settings.Model, provider);
                lock (stateGate)
                {
                    saved += result.SavedPositions; resolved += result.ResolvedTasks; deferred += result.DeferredTasks; skipped += result.SkippedTasks;
                    failed += parsed.Failures.Count;
                }
                log($"核对第 {batch.Number} 批：解决 {result.ResolvedTasks}，暂缓 {result.DeferredTasks}，内容变化跳过 {result.SkippedTasks}，校验失败 {parsed.Failures.Count}；有效结果已保存，不自动重发。");
                foreach (var group in parsed.Failures.GroupBy(x => x.Value))
                    log($"第 {batch.Number} 批校验：{group.Count()} 项 · {group.Key}");
                foreach (var note in parsed.Diagnostics) log($"第 {batch.Number} 批：{note}");
                if (parsed.Failures.Count > 0 || parsed.Diagnostics.Length > 0)
                    log($"响应及逐项诊断：{diagnosticPath}；结束原因：{completion.FinishReason ?? "供应商未提供"}。");
                finished = true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                var safeError = ModelCompletionClient.SafeError(e);
                foreach (var task in batch.Rows) failures[Identity(task)] = safeError;
                lock (stateGate) failed += batch.Rows.Length;
                finished = true;
                log($"核对第 {batch.Number} 批未完成（{safeError}）；不自动重发。已接收响应（若有）保留在 {diagnosticPath}。");
            }
            finally
            {
                lock (writeGate) AtomicData.Write(failurePath, JsonData.Write(failures.ToDictionary(x => x.Key, x => x.Value)));
                lock (stateGate)
                {
                    active--;
                    if (finished) { completed += batch.Rows.Length; completedBatches++; }
                }
                Report($"已解决 {resolved:N0} 个任务，暂缓 {deferred:N0}，失败 {failed:N0}，内容变化跳过 {skipped:N0}。");
            }
        });
        return new(saved, resolved, deferred, failed, skipped);
    }

    private static string Identity(ModelReviewTask task) => task.Mode + ":" + task.Key + "\0" + task.SourceHash;
}
