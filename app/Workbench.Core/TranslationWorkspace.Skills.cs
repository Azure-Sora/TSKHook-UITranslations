using System.Text.RegularExpressions;

namespace TSKHook.Workbench;

public sealed record SkillGenerationResult(int Eligible, int Generated, int Updated, int Existing, int Ambiguous);

public sealed partial class TranslationWorkspace
{
    private static readonly Regex ColorTags = new("</?color(?:=[^>]+)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public SkillGenerationResult GenerateSkillPlainText()
    {
        lock (gate)
        {
            EnsureUnchanged();
            var eligible = entries.Values.Where(x => x.Category == "skill" && TranslationGroups.IsReady(x) &&
                ColorTags.IsMatch(x.Source) && !ValidationRules.Check(x).Any(i => i.Fatal)).ToArray();
            var working = entries.ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.Ordinal);
            var byContent = working.Values.GroupBy(x => JsonData.Write(new[] { x.Category, x.Field, x.Source }))
                .ToDictionary(x => x.Key, x => x.ToList());
            int generated = 0, updated = 0, existing = 0, ambiguous = 0;
            foreach (var group in eligible.GroupBy(x => JsonData.Write(new[] { x.Field, ColorTags.Replace(x.Source, "") })))
            {
                var first = group.First();
                var plain = ColorTags.Replace(first.Source, "");
                var values = group.Select(x => ColorTags.Replace(x.Translation, "")).Distinct(StringComparer.Ordinal).ToArray();
                var matches = byContent.GetValueOrDefault(JsonData.Write(new[] { "skill", first.Field, plain }), []);
                var derived = matches.Where(x => x.Identity.GetValueOrDefault("generated_plain") == "true").ToArray();
                if (values.Length != 1)
                {
                    ambiguous++;
                    // A previously generated machine row must not silently publish one side of a new disagreement.
                    foreach (var row in derived.Where(x => x.Status == "translated")) { row.Status = "needs_review"; updated++; }
                    continue;
                }
                if (matches.Count > 0)
                {
                    existing++;
                    // Captured or manually reviewed plain text is authoritative and stays untouched.
                    foreach (var row in derived.Where(x => x.Status == "translated" && x.Translation != values[0]))
                    { row.Translation = values[0]; if (ValidationRules.Check(row).Count != 0) row.Status = "needs_review"; updated++; }
                    continue;
                }
                var hash = UiJsonTranslator.Sha256(plain);
                var next = new TranslationEntry { Key = $"skill:generated_plain:source_hash={hash}:{first.Field}",
                    Category = "skill", Field = first.Field, Source = plain, SourceHash = hash, Translation = values[0], Status = "translated",
                    Identity = new(StringComparer.Ordinal) { ["generated_plain"] = "true", ["source_hash"] = hash },
                    Origins = group.Select(x => "generated:plain-from-color:" + x.Key).Order(StringComparer.Ordinal).ToArray(),
                    FirstSeenUtc = first.FirstSeenUtc };
                if (ValidationRules.Check(next).Count != 0) next.Status = "needs_review";
                if (!working.TryAdd(next.Key, next)) { ambiguous++; continue; }
                generated++;
            }
            if (generated + updated > 0)
            {
                AtomicData.WriteBatch(Root, new Dictionary<string, string>
                {
                    ["translations/ui/source/ui_source.jsonl"] = Lines(working.Values.OrderBy(x => x.Category).ThenBy(x => x.Key, StringComparer.Ordinal)),
                    ["translations/ui/source/by_category/skill.jsonl"] = Lines(working.Values.Where(x => x.Category == "skill").OrderBy(x => x.Key, StringComparer.Ordinal))
                });
                entries = working;
                RememberFiles();
            }
            return new(eligible.Length, generated, updated, existing, ambiguous);
        }
    }
}
