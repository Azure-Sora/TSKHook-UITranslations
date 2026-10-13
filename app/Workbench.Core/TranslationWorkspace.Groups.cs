namespace TSKHook.Workbench;

public sealed partial class TranslationWorkspace
{
    public int EditGroup(IReadOnlyList<TranslationEntry> originals, string translation, string status)
    {
        if (originals.Count == 0) return 0;
        if (originals.Any(ReviewQueue.IsCaptureConflict)) throw new InvalidOperationException("捕获冲突请在“问题与冲突”中处理，不能直接标为审校通过。");
        if (status == "reviewed" && string.IsNullOrWhiteSpace(translation)) throw new InvalidOperationException("译文不能为空；不翻译请明确排除。");
        if (status is not ("reviewed" or "excluded")) throw new ArgumentException("组操作只支持审校或排除；实机验证按单个位置记录。");
        lock (gate)
        {
            EnsureUnchanged();
            var content = TranslationGroups.ContentKey(originals[0]);
            var template = NumberedNames.Template(originals[0]);
            var numbered = template != null && translation.Contains(NumberedNames.Token);
            if (!numbered && status != "excluded" && originals.Any(x => TranslationGroups.ContentKey(x) != content))
                throw new InvalidDataException("楼层组请填写包含 {floor} 的译文模板，例如：水神之塔{floor}F。");
            var changes = new List<TranslationEntry>();
            foreach (var original in originals)
            {
                if ((numbered || status == "excluded" && template != null ? NumberedNames.FamilyKey(original) != NumberedNames.FamilyKey(originals[0]) : TranslationGroups.ContentKey(original) != content) || !entries.TryGetValue(original.Key, out var current) ||
                    current.SourceHash != original.SourceHash || current.Status != original.Status || current.Translation != original.Translation)
                    throw new InvalidOperationException("本组内容已变化，请刷新后再保存。");
                var next = current.Copy(); next.Translation = status == "excluded" ? current.Source : numbered ? NumberedNames.Expand(translation, current) : translation; next.Status = status;
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
            var alreadyChanged = changes.Select(x => x.Key).ToHashSet();
            foreach (var family in entries.Values.Where(x => NumberedNames.Template(x) != null).GroupBy(NumberedNames.FamilyKey))
            {
                var ready = family.Where(TranslationGroups.IsReady).ToArray();
                if (ready.Select(x => x.Source).Distinct().Count() < 2 || ready.Any(x => ValidationRules.Check(x).Count != 0)) continue;
                var templates = ready.Select(NumberedNames.TranslationTemplate).Distinct().ToArray();
                if (templates.Length != 1 || string.IsNullOrEmpty(templates[0])) continue;
                foreach (var row in family.Where(x => x.Status is "new" or "stale" && !alreadyChanged.Contains(x.Key)))
                {
                    var next = row.Copy(); next.Translation = NumberedNames.Expand(templates[0]!, row); next.Status = "translated";
                    changes.Add(next);
                }
            }
            SaveReplacements(changes);
            return changes.Count;
        }
    }
}
