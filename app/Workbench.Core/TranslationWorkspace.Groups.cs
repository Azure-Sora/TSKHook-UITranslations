namespace TSKHook.Workbench;

public sealed partial class TranslationWorkspace
{
    public int EditGroup(IReadOnlyList<TranslationEntry> originals, string translation, string status)
    {
        if (originals.Count == 0) return 0;
        if (status is not ("reviewed" or "excluded")) throw new ArgumentException("组操作只支持审校或排除；实机验证按单个位置记录。");
        lock (gate)
        {
            EnsureUnchanged();
            var content = TranslationGroups.ContentKey(originals[0]);
            var changes = new List<TranslationEntry>();
            foreach (var original in originals)
            {
                if (TranslationGroups.ContentKey(original) != content || !entries.TryGetValue(original.Key, out var current) ||
                    current.SourceHash != original.SourceHash || current.Status != original.Status || current.Translation != original.Translation)
                    throw new InvalidOperationException("本组内容已变化，请刷新后再保存。");
                var next = current.Copy(); next.Translation = translation; next.Status = status;
                if (next.Protected && translation != next.Source && translation.Length > 0)
                    throw new InvalidOperationException("受保护字段只能保留原文。");
                if (status == "reviewed" && ValidationRules.Check(next).Any(x => x.Fatal))
                    throw new InvalidDataException("标签或占位符校验失败。");
                changes.Add(next);
            }
            SaveReplacements(changes);
            return changes.Count;
        }
    }

    public int ReuseTranslations()
    {
        lock (gate)
        {
            EnsureUnchanged();
            var changes = new List<TranslationEntry>();
            foreach (var group in entries.Values.GroupBy(TranslationGroups.ContentKey))
            {
                var ready = group.Where(TranslationGroups.IsReady).ToArray();
                var texts = ready.Select(x => x.Translation).Distinct(StringComparer.Ordinal).ToArray();
                if (texts.Length != 1 || ready.Any(x => ValidationRules.Check(x).Count != 0)) continue;
                foreach (var row in group.Where(x => !x.Protected && x.Status is "new" or "stale"))
                {
                    var next = row.Copy(); next.Translation = texts[0]; next.Status = "translated";
                    changes.Add(next);
                }
            }
            SaveReplacements(changes);
            return changes.Count;
        }
    }
}
