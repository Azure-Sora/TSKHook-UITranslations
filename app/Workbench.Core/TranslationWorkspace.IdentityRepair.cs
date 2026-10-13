using System.Security.Cryptography;

namespace TSKHook.Workbench;

public sealed record IdentityRepairResult(int Migrated, int Reused, int Skipped, string? BackupDirectory);

public sealed partial class TranslationWorkspace
{
    // Both runtime matching and this migration use the shared key builder.
    public IdentityRepairResult RepairEnemyIdentities(bool dryRun = false)
        => RepairCapturedIdentities(dryRun, enemyOnly: true);

    public IdentityRepairResult RepairCapturedIdentities(bool dryRun = false, bool enemyOnly = false)
    {
        lock (gate)
        {
            EnsureUnchanged();
            var proposals = new Dictionary<string, UiTextKeyDescriptor>(StringComparer.Ordinal);
            var skipped = 0;
            foreach (var row in entries.Values)
            {
                if (enemyOnly && !row.Origins.Any(x => x.Contains(".enemy_data.", StringComparison.OrdinalIgnoreCase)) ||
                    row.Identity.TryGetValue("source_hash", out var identityHash) && identityHash == row.SourceHash) continue;
                UiTextKeyDescriptor? target = null;
                var valid = !row.Protected && UiJsonTranslator.Sha256(row.Source) == row.SourceHash;
                foreach (var origin in row.Origins)
                {
                    var colon = origin.IndexOf(':');
                    if (colon < 1 || !UiTextKeyBuilder.TryUpgradeCapturedKey(origin[..colon], origin[(colon + 1)..],
                        row.Source, row.SourceHash, row.Identity, row.Key, out var descriptor) ||
                        (target != null && target.Key != descriptor.Key)) { valid = false; break; }
                    target = descriptor;
                }
                if (valid && target != null) proposals[row.Key] = target;
                else if (row.Status == "conflict" || enemyOnly) skipped++;
            }
            var working = entries.ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.Ordinal);
            var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
            var reused = 0;
            foreach (var group in proposals.GroupBy(x => x.Value.Key))
            {
                var originals = group.Select(x => entries[x.Key]).ToList();
                if (entries.TryGetValue(group.Key, out var existing)) originals.Add(existing);
                var texts = originals.Select(x => x.Translation).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
                if (texts.Length > 1 || originals.Select(x => (x.Source, x.SourceHash)).Distinct().Count() != 1 ||
                    originals.Select(x => x.Status == "excluded").Distinct().Count() > 1)
                { skipped += group.Count(); continue; }
                var next = originals.OrderByDescending(x => x.Status == "verified" ? 3 : x.Status == "reviewed" ? 2 : 1).First().Copy();
                var descriptor = group.First().Value;
                next.Key = descriptor.Key; next.Identity = descriptor.Identity;
                next.Category = descriptor.Category; next.Field = descriptor.Field;
                next.Origins = originals.SelectMany(x => x.Origins).Distinct().Order(StringComparer.Ordinal).ToArray();
                next.Occurrences = originals.Max(x => x.Occurrences);
                next.Translation = texts.FirstOrDefault() ?? "";
                next.CaptureConflictExcluded = false;
                if (next.Status == "conflict") next.Status = string.IsNullOrWhiteSpace(next.Translation) ? "new" :
                    ValidationRules.Check(next).Count == 0 ? "translated" : "needs_review";
                if (next.Status != "excluded" && string.IsNullOrWhiteSpace(next.Translation))
                {
                    var candidates = entries.Values.Where(x => TranslationGroups.IsReady(x) && !x.Protected &&
                        !ReviewQueue.IsCaptureConflict(x) && x.Source == next.Source && x.SourceHash == next.SourceHash &&
                        (IsEnemyName(next.Field) ? IsEnemyName(x.Field) : TranslationGroups.ContentKey(x) == TranslationGroups.ContentKey(next)))
                        .Where(x => ValidationRules.Check(x).Count == 0).Select(x => x.Translation).Distinct().ToArray();
                    if (candidates.Length == 1)
                    {
                        next.Translation = candidates[0];
                        if (ValidationRules.Check(next).Count == 0) { next.Status = "translated"; reused++; }
                        else next.Translation = "";
                    }
                }
                foreach (var old in group) { working.Remove(old.Key); mapping[old.Key] = next.Key; }
                working[next.Key] = next;
            }
            if (dryRun || mapping.Count == 0) return new(mapping.Count, reused, skipped, null);
            var files = new Dictionary<string, string>
            {
                ["translations/ui/source/ui_source.jsonl"] = Lines(working.Values.OrderBy(x => x.Key, StringComparer.Ordinal)),
                ["translations/ui/source/ui_conflicts.jsonl"] = Lines(working.Values.Where(x => x.Status == "conflict").OrderBy(x => x.Key, StringComparer.Ordinal))
            };
            foreach (var category in working.Values.GroupBy(x => x.Category))
                files["translations/ui/source/by_category/" + category.Key + ".jsonl"] = Lines(category.OrderBy(x => x.Key, StringComparer.Ordinal));
            const string ledgerPath = ".local/translation-failures.json";
            if (File.Exists(Path.Combine(Root, ledgerPath)))
            {
                var ledger = JsonData.Read<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Root, ledgerPath)));
                foreach (var pair in mapping)
                {
                    var suffix = "\0" + entries[pair.Key].SourceHash;
                    if (ledger.Remove(pair.Key + suffix, out var failure)) ledger.TryAdd(pair.Value + suffix, failure);
                }
                files[ledgerPath] = JsonData.Write(ledger);
            }
            var backup = Path.Combine(Root, ".local/backups/capture-identity-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
            Directory.CreateDirectory(backup);
            var manifest = new Dictionary<string, string>();
            foreach (var relative in files.Keys)
            {
                var original = Path.Combine(Root, relative);
                if (!File.Exists(original)) continue;
                var destination = Path.Combine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(original, destination);
                manifest[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))).ToLowerInvariant();
            }
            File.WriteAllText(Path.Combine(backup, "manifest.json"), JsonData.Write(manifest));
            File.WriteAllText(Path.Combine(backup, "key-mapping.json"), JsonData.Write(mapping));
            EnsureUnchanged();
            AtomicData.WriteBatch(Root, files);
            entries = working; RememberFiles();
            return new(mapping.Count, reused, skipped, backup);
        }
    }

    private static bool IsEnemyName(string field) => field is "character_name" or "unit_name" or "boss_name" or "full_name" or "character_name_kana";
}
