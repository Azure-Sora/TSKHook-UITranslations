namespace TSKHook.Workbench;

public sealed partial class TranslationWorkspace
{
    public ModelReviewApplyResult ApplyModelReviewBatch(IReadOnlyList<ModelReviewTask> tasks,
        IReadOnlyList<ModelReviewDecision> decisions, string model, string provider)
    {
        if (tasks.Count != decisions.Count) throw new InvalidDataException("核对响应数量不一致。");
        for (var i = 0; i < tasks.Count; i++) ModelReviewTasks.ValidateDecision(tasks[i], decisions[i]);
        lock (gate)
        {
            EnsureUnchanged();
            var changes = new List<TranslationEntry>();
            var audits = new List<object>();
            int resolved = 0, deferred = 0, skipped = 0;
            for (var i = 0; i < tasks.Count; i++)
            {
                var task = tasks[i]; var decision = decisions[i];
                var unchanged = task.ContextMembers.All(x => entries.TryGetValue(x.Key, out var current) && JsonData.Write(current) == JsonData.Write(x));
                if (task.Mode == ModelReviewMode.Unify)
                {
                    var content = TranslationGroups.ContentKey(task.Members[0]);
                    var currentKeys = entries.Values.Where(x => ModelReviewTasks.CanProvideContext(x) && TranslationGroups.ContentKey(x) == content).Select(x => x.Key).Order(StringComparer.Ordinal);
                    unchanged &= currentKeys.SequenceEqual(task.ContextMembers.Select(x => x.Key).Order(StringComparer.Ordinal));
                }
                if (!unchanged) { skipped++; continue; }
                foreach (var row in task.Members)
                {
                    if (!ModelReviewTasks.CanReview(row)) throw new InvalidOperationException("核对不能覆盖人工确认、排除或冲突内容。");
                    var next = row.Copy();
                    if (decision.Decision != "defer") { next.Translation = decision.Translation; next.Status = "translated"; }
                    next.ModelReview = new(model, provider, decision.Decision, decision.Reason, row.SourceHash,
                        UiJsonTranslator.Sha256(next.Translation), decision.AcknowledgedIssues, DateTime.UtcNow);
                    changes.Add(next);
                }
                audits.Add(new { mode = task.Mode.ToString(), model, provider, decision, before = task.Members, context = task.ContextMembers });
                if (decision.Decision == "defer") deferred++; else resolved++;
            }
            if (changes.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != changes.Count)
                throw new InvalidDataException("核对批次包含重叠写入。");
            var audit = ".local/model-reviews/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N") + ".json";
            SaveReplacements(changes, new Dictionary<string, string> { [audit] = JsonData.Write(new { time = DateTime.UtcNow, audits }) });
            return new(changes.Count, resolved, deferred, skipped);
        }
    }
}
