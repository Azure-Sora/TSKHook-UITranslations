namespace TSKHook.Workbench;

public enum ModelReviewMode { Unify, Check }
public sealed record ModelReviewEvidence(string Model, string Provider, string Decision, string Reason,
    string SourceHash, string TranslationHash, string[] AcknowledgedIssues, DateTime ReviewedAtUtc);
public sealed record ModelReviewTask(string Key, string SourceHash, ModelReviewMode Mode,
    TranslationEntry[] Members, TranslationEntry[] ContextMembers, string[] Candidates);
public sealed class ModelReviewDecision
{
    public string Key { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string Decision { get; set; } = "";
    public int Candidate { get; set; }
    public string Translation { get; set; } = "";
    public string Reason { get; set; } = "";
    public string[] AcknowledgedIssues { get; set; } = [];
}
public sealed record ModelReviewApplyResult(int SavedPositions, int ResolvedTasks, int DeferredTasks, int SkippedTasks);
public sealed record ModelReviewRunResult(int SavedPositions, int ResolvedTasks, int DeferredTasks, int FailedTasks, int SkippedTasks);

public static class ModelReviewTasks
{
    public static bool CanReview(TranslationEntry row) => ReviewQueue.CanRetranslate(row);
    public static bool CanProvideContext(TranslationEntry row) => !row.Protected && !ReviewQueue.IsCaptureConflict(row) &&
        row.Status != "excluded" && UiJsonTranslator.Sha256(row.Source) == row.SourceHash;

    public static List<ModelReviewTask> Create(IReadOnlyList<TranslationEntry> rows, ModelReviewMode mode, IReadOnlyList<TranslationEntry>? selected = null)
    {
        HashSet<string>? selectedKeys = null;
        if (selected != null)
        {
            var current = rows.ToDictionary(x => x.Key, StringComparer.Ordinal);
            if (selected.Count == 0 || selected.Any(x => !CanReview(x) || !current.TryGetValue(x.Key, out var row) || JsonData.Write(row) != JsonData.Write(x)))
                throw new InvalidOperationException("选中内容已变化，或包含人工确认、排除、冲突及失效原文，请刷新后重选。");
            selectedKeys = selected.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        }
        var tasks = new List<ModelReviewTask>();
        foreach (var family in rows.Where(CanProvideContext).GroupBy(TranslationGroups.ContentKey))
        {
            var context = family.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
            var editable = context.Where(CanReview).ToArray();
            if (selectedKeys != null && !editable.Any(x => selectedKeys.Contains(x.Key))) continue;
            if (mode == ModelReviewMode.Unify)
            {
                var candidates = context.Select(x => x.Translation).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (editable.Length == 0 || candidates.Length < 2) continue;
                tasks.Add(new(editable[0].Key, editable[0].SourceHash, mode, editable, context, candidates));
            }
            else
            {
                var hasVariants = context.Where(TranslationGroups.IsReady).Select(x => x.Translation).Distinct(StringComparer.Ordinal).Skip(1).Any();
                if (hasVariants) continue; // Resolve disagreements through the candidate-aware path first.
                foreach (var wording in editable.GroupBy(x => x.Translation))
                {
                    var members = wording.Where(x => selectedKeys == null ?
                        !ReviewQueue.HasCurrentModelReview(x) && (x.Status == "needs_review" || ValidationRules.Check(x).Count > 0) : selectedKeys.Contains(x.Key)).ToArray();
                    if (members.Length == 0) continue;
                    tasks.Add(new(members[0].Key, members[0].SourceHash, mode, members, members, []));
                }
            }
        }
        return tasks.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
    }

    public static void ValidateDecision(ModelReviewTask task, ModelReviewDecision decision)
    {
        if (decision.Key != task.Key || decision.SourceHash != task.SourceHash || string.IsNullOrWhiteSpace(decision.Reason) || decision.Reason.Length > 4000 ||
            decision.AcknowledgedIssues.Distinct(StringComparer.Ordinal).Count() != decision.AcknowledgedIssues.Length ||
            decision.AcknowledgedIssues.Any(x => x is not ("numbers" or "newlines")))
            throw new InvalidDataException("核对响应身份、理由或告警确认无效。");
        if (decision.Decision == "defer")
        {
            if (decision.Candidate != 0 || decision.Translation != "" || decision.AcknowledgedIssues.Length != 0)
                throw new InvalidDataException("暂不判断时不得返回译文、候选或告警确认。");
            return;
        }
        if (task.Mode == ModelReviewMode.Unify)
        {
            if (decision.Decision != "choose" || decision.Candidate < 1 || decision.Candidate > task.Candidates.Length ||
                decision.Translation != task.Candidates[decision.Candidate - 1])
                throw new InvalidDataException("统一只能选择本次提供的已有译文。");
            if (task.ContextMembers.Where(x => x.Status is "reviewed" or "verified").Any(x => x.Translation != decision.Translation))
                throw new InvalidDataException("所选译文与人工确认内容不一致，应保留人工内容并返回 defer。");
        }
        else if (decision.Candidate != 0 || decision.Decision is not ("accept" or "revise") ||
            (decision.Decision == "accept" && task.Members.Any(x => x.Translation != decision.Translation)))
            throw new InvalidDataException("核对只允许保留当前译文、修正或暂不判断。");
        if (string.IsNullOrWhiteSpace(decision.Translation)) throw new InvalidDataException("不能核对通过空译文。");
        foreach (var original in task.Members)
        {
            var next = original.Copy(); next.Translation = decision.Translation;
            var issues = ValidationRules.Check(next);
            if (issues.Any(x => x.Fatal || !decision.AcknowledgedIssues.Contains(x.Kind, StringComparer.Ordinal)))
                throw new InvalidDataException("核对结果未通过：" + string.Join("、", issues.Where(x => x.Fatal || !decision.AcknowledgedIssues.Contains(x.Kind, StringComparer.Ordinal)).Select(x => x.Kind).Distinct()));
        }
    }
}
