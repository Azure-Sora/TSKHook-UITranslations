namespace TSKHook.Workbench;

public enum ReviewKind { Pending, Variants, Revision, Conflict, Ready, Excluded }

public static class ReviewQueue
{
    public static ReviewKind Classify(TranslationGroup group, IReadOnlyList<ValidationIssue> issues)
    {
        var row = group.Representative;
        if (row.Status == "conflict") return ReviewKind.Conflict;
        if (row.Status == "excluded") return ReviewKind.Excluded;
        // A manual review acknowledges nonfatal warnings and intentional wording differences.
        if (row.Status is "reviewed" or "verified" && !issues.Any(x => x.Fatal)) return ReviewKind.Ready;
        if (group.HasVariants) return ReviewKind.Variants;
        if (HasCurrentModelReview(row, issues)) return ReviewKind.Ready;
        if (row.Status == "needs_review" || issues.Count > 0) return ReviewKind.Revision;
        return row.Status is "new" or "stale" ? ReviewKind.Pending : ReviewKind.Ready;
    }

    public static string Label(ReviewKind kind) => kind switch
    {
        ReviewKind.Pending => "可自动翻译", ReviewKind.Variants => "译文待统一",
        ReviewKind.Revision => "译文待修正", ReviewKind.Conflict => "捕获冲突",
        ReviewKind.Excluded => "已排除", _ => "已处理"
    };

    public static string ConflictKey(TranslationEntry row)
    {
        var suffix = "#" + row.SourceHash[..8];
        return row.Key.EndsWith(suffix, StringComparison.Ordinal) ? row.Key[..^suffix.Length] : row.Key;
    }

    public static bool IsCaptureConflict(TranslationEntry row) => row.Status == "conflict" || row.Key != ConflictKey(row) ||
        row.CaptureConflictExcluded;

    public static bool CanRetranslate(TranslationEntry row) => !row.Protected && !IsCaptureConflict(row) &&
        row.Status is "new" or "stale" or "needs_review" or "translated" &&
        UiJsonTranslator.Sha256(row.Source) == row.SourceHash;

    public static bool HasCurrentModelReview(TranslationEntry row, IReadOnlyList<ValidationIssue>? issues = null) =>
        row.Status == "translated" && row.ModelReview is { Decision: "choose" or "accept" or "revise" } review &&
        !string.IsNullOrWhiteSpace(row.Translation) && row.SourceHash == UiJsonTranslator.Sha256(row.Source) &&
        review.SourceHash == row.SourceHash && review.TranslationHash == UiJsonTranslator.Sha256(row.Translation) &&
        (issues ?? ValidationRules.Check(row)).All(x => !x.Fatal && review.AcknowledgedIssues.Contains(x.Kind, StringComparer.Ordinal));
}
