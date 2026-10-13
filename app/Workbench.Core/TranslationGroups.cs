namespace TSKHook.Workbench;

public sealed record TranslationGroup(TranslationEntry[] Members, bool HasVariants, string? Template = null)
{
    public TranslationEntry Representative
    {
        get
        {
            if (Template == null) return Members[0];
            var row = Members[0].Copy(); row.Source = Template; row.SourceHash = UiJsonTranslator.Sha256(Template);
            row.Translation = NumberedNames.TranslationTemplate(Members[0]) ?? "";
            return row;
        }
    }
    public string Expand(string text, TranslationEntry member) => string.IsNullOrWhiteSpace(text) ? "" : Template == null ? text : NumberedNames.Expand(text, member);
}

public static class TranslationGroups
{
    // Retain every runtime identity. Only translation work is shared.
    public static string ContentKey(TranslationEntry row) => JsonData.Write(new[]
        { row.Category, row.Field, row.SourceHash, row.Source, row.Protected ? row.Key : "" });

    public static bool IsReady(TranslationEntry row) => row.Status is "translated" or "reviewed" or "verified" &&
        !row.Protected && !string.IsNullOrWhiteSpace(row.Translation) && UiJsonTranslator.Sha256(row.Source) == row.SourceHash;

    public static List<TranslationGroup> Create(IEnumerable<TranslationEntry> rows, bool merge = true)
    {
        var result = new List<TranslationGroup>();
        foreach (var content in rows.GroupBy(ContentKey))
        {
            var variants = content.Where(IsReady).Select(x => x.Translation).Distinct(StringComparer.Ordinal).Skip(1).Any();
            // Do not collapse deliberate overrides, review states or stale reference translations.
            foreach (var group in content.GroupBy(x => merge ? JsonData.Write(new[] { x.Status, x.Translation }) : x.Key))
                result.Add(new(group.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(), variants));
        }
        if (!merge) return result;
        var eligible = result.Where(g => !g.HasVariants && g.Representative.Status is "new" or "stale" or "translated" or "reviewed" or "verified")
            .Where(g => NumberedNames.Template(g.Representative) != null && NumberedNames.TranslationTemplate(g.Representative) != null).ToArray();
        foreach (var family in eligible.GroupBy(g => NumberedNames.FamilyKey(g.Representative)))
        {
            if (family.Select(g => g.Representative.Source).Distinct().Count() < 3) continue;
            var variants = family.Where(g => IsReady(g.Representative)).Select(g => NumberedNames.TranslationTemplate(g.Representative)).Distinct().Skip(1).Any();
            foreach (var state in family.GroupBy(g => JsonData.Write(new[] { g.Representative.Status, NumberedNames.TranslationTemplate(g.Representative) })))
            {
                var members = state.SelectMany(g => g.Members).OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
                foreach (var old in state) result.Remove(old);
                result.Add(new(members, variants, NumberedNames.Template(members[0])));
            }
        }
        return result;
    }
}
