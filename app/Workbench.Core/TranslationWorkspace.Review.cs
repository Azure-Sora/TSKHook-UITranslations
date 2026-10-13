namespace TSKHook.Workbench;

public sealed partial class TranslationWorkspace
{
    public int RecheckMachineResults()
    {
        lock (gate)
        {
            EnsureUnchanged();
            var changes = entries.Values.Where(x => x.Status == "needs_review" && !x.Protected &&
                !string.IsNullOrWhiteSpace(x.Translation) && ValidationRules.Check(x).Count == 0)
                .Select(x => { var next = x.Copy(); next.Status = "translated"; return next; }).ToList();
            SaveReplacements(changes);
            return changes.Count;
        }
    }

    public int UnifyTranslation(IReadOnlyList<TranslationEntry> originals, string translation)
    {
        if (originals.Count == 0) return 0;
        var key = TranslationGroups.ContentKey(originals[0]);
        if (originals.Any(x => x.Protected || ReviewQueue.IsCaptureConflict(x) || x.Status == "excluded" || TranslationGroups.ContentKey(x) != key))
            throw new InvalidOperationException("只能统一相同分类、字段及原文的非冲突、未排除位置。");
        lock (gate)
        {
            var current = entries.Values.Where(x => !x.Protected && !ReviewQueue.IsCaptureConflict(x) && x.Status != "excluded" && TranslationGroups.ContentKey(x) == key);
            if (!current.Select(x => x.Key).Order().SequenceEqual(originals.Select(x => x.Key).Order()))
                throw new InvalidOperationException("同原文的位置已变化，请刷新后再统一。");
            return EditGroup(originals, translation, "reviewed");
        }
    }

    public int ExcludeConflicts(IReadOnlyList<TranslationEntry> originals)
    {
        lock (gate)
        {
            EnsureUnchanged();
            if (originals.Count == 0) return 0;
            var families = originals.Select(ReviewQueue.ConflictKey).ToHashSet(StringComparer.Ordinal);
            var current = entries.Values.Where(x => x.Status == "conflict" && families.Contains(ReviewQueue.ConflictKey(x))).ToArray();
            if (!current.Select(x => x.Key).Order().SequenceEqual(originals.Select(x => x.Key).Order()) || originals.Any(x =>
                !entries.TryGetValue(x.Key, out var row) || row.Status != "conflict" || row.Source != x.Source || row.SourceHash != x.SourceHash || row.Translation != x.Translation))
                throw new InvalidOperationException("冲突记录已变化，请刷新详情后再处理。");
            var changes = current.Select(x => { var next = x.Copy(); next.Status = "excluded"; next.CaptureConflictExcluded = true; return next; }).ToList();
            // Keep original text, identity and any draft translation for later investigation.
            SaveReplacements(changes);
            return changes.Count;
        }
    }
}
