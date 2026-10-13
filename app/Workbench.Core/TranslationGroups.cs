namespace TSKHook.Workbench;

public sealed record TranslationGroup(TranslationEntry[] Members, bool HasVariants)
{
    public TranslationEntry Representative => Members[0];
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
        return result;
    }
}
