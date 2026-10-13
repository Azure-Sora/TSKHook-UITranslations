using System.Security.Cryptography;
using TSKHook;
using TSKHook.Workbench;

internal static class IdentityRepairTests
{
    public static int Run(string root)
    {
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        var a = Enemy("ルネア"); var b = Enemy("ロロット");
        var known = a.Copy(); known.Key = "misc:known:boss_name"; known.Field = "boss_name";
        known.Translation = "露妮亚"; known.Status = "reviewed"; known.Origins = [];
        var excluded = Enemy("除外"); excluded.Status = "excluded"; excluded.CaptureConflictExcluded = true;
        var mixed = Enemy("混在"); mixed.Origins = [..mixed.Origins, "Other:$.result.character_name"];
        var wrong = Enemy("誤り"); wrong.Source = "破損";
        var work = Create(Path.Combine(root, "identity"), [a, b, known, excluded, mixed, wrong]);
        var ledgerFile = Path.Combine(work.Root, ".local/translation-failures.json"); Directory.CreateDirectory(Path.GetDirectoryName(ledgerFile)!);
        File.WriteAllText(ledgerFile, JsonData.Write(new Dictionary<string, string> { [a.Key + "\0" + a.SourceHash] = "failure", ["unrelated"] = "retain" }));
        var preview = work.RepairEnemyIdentities(true);
        Check(preview.Migrated == 3 && preview.Reused == 1 && preview.Skipped == 2, "dry run identifies safe migration and reuse");
        Check(work.Snapshot().Any(x => x.Key == a.Key), "dry run does not mutate data");
        var result = work.RepairEnemyIdentities();
        var migrated = new TranslationWorkspace(work.Root).Snapshot();
        var converted = migrated.Single(x => x.Source == a.Source && x.Field == "character_name");
        Check(converted.Key != a.Key && converted.Translation == "露妮亚" && converted.Status == "translated", "legacy name gets safe existing translation");
        Check(migrated.Single(x => x.Source == b.Source).Status == "new", "untranslated different name is independently pending");
        Check(migrated.Single(x => x.Source == excluded.Source).Status == "excluded", "manual exclusion survives migration");
        Check(migrated.Any(x => x.Key == mixed.Key) && migrated.Any(x => x.Key == wrong.Key), "mixed origins and invalid hash remain untouched");
        Check(work.RepairEnemyIdentities().Migrated == 0, "migration is idempotent");
        var backupSource = Path.Combine(result.BackupDirectory!, "translations/ui/source/ui_source.jsonl");
        var manifest = JsonData.Read<Dictionary<string, string>>(File.ReadAllText(Path.Combine(result.BackupDirectory!, "manifest.json")));
        Check(manifest["translations/ui/source/ui_source.jsonl"] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backupSource))).ToLowerInvariant(), "backup manifest hashes original bytes");
        Check(File.ReadAllText(backupSource).Contains(a.Key), "backup preserves old keys");
        var ledger = JsonData.Read<Dictionary<string, string>>(File.ReadAllText(ledgerFile));
        Check(ledger.ContainsKey(converted.Key + "\0" + a.SourceHash) && !ledger.ContainsKey(a.Key + "\0" + a.SourceHash) && ledger["unrelated"] == "retain", "failure ledger identities migrate without losing unrelated failures");
        work.Compile(Path.Combine(work.Root, "runtime.jsonl"));
        Check(File.ReadAllText(Path.Combine(work.Root, "runtime.jsonl")).Contains(converted.Key), "compiled runtime uses corrected key");
        var fresh = Create(Path.Combine(root, "identity-import"), []);
        var captureFile = Path.Combine(fresh.Root, "capture.jsonl");
        File.WriteAllText(captureFile, string.Join("\n", new[] { a, b }.Select(x => JsonData.Write(new CaptureRecord
        { SchemaVersion = 2, ApiName = "SubdueEventTop", Path = "$.result.enemy_data.character_name", Source = x.Source, SourceHash = x.SourceHash, Context = x.Identity.ToDictionary() }))) + "\n");
        var imported = fresh.ImportCapture(captureFile);
        Check(imported.Added == 2 && imported.Conflicts == 0, "fresh capture creates distinct enemies without conflict");
        var target = converted.Copy(); target.Translation = "另一译文";
        var old = a.Copy(); old.Translation = "旧译文";
        var competition = Create(Path.Combine(root, "identity-competing"), [old, target]);
        Check(competition.RepairEnemyIdentities().Migrated == 0 && competition.Snapshot().Count == 2, "competing translations are never overwritten");
        target.Translation = "旧译文";
        var merge = Create(Path.Combine(root, "identity-merge"), [old, target]);
        Check(merge.RepairEnemyIdentities().Migrated == 1 && merge.Snapshot().Count == 1, "matching old and new captures merge safely");
        var unit = new TranslationEntry { Key = "unit:unit_id=1001001:unit_illust_id=1001001:unit_name", Category = "unit", Field = "unit_name",
            Source = "流星の姫君", SourceHash = UiJsonTranslator.Sha256("流星の姫君"), Translation = "流星公主", Status = "conflict",
            Identity = new() { ["unit_id"] = "1001001", ["unit_illust_id"] = "1001001" }, Origins = ["CampTop:$.result.effect_unit_list[].unit_name"] };
        var duplicate = unit.Copy(); duplicate.Key += "#" + duplicate.SourceHash[..8]; duplicate.Translation = "";
        var sister = unit.Copy(); sister.Source = "シスター"; sister.SourceHash = UiJsonTranslator.Sha256(sister.Source); sister.Key += "#" + sister.SourceHash[..8];
        sister.Translation = "星骑士"; sister.Origins = ["SubdueEventTop:$.result.effect_sister_unit_list[].unit_name"];
        var tabs = new[] { "チャレンジ", "ストーリー" }.Select((source, i) => new TranslationEntry
        { Key = "misc:BossRushEventTop:event_id=6016:quest_id=600016:tab_name#" + UiJsonTranslator.Sha256(source)[..8], Source = source, SourceHash = UiJsonTranslator.Sha256(source),
            Field = "tab_name", Translation = i == 0 ? "挑战" : "剧情", Status = "conflict", Identity = new() { ["event_id"] = "6016", ["quest_id"] = "600016" },
            Origins = ["BossRushEventTop:$.result.stage_data[].tab_name"] }).ToArray();
        var variants = Create(Path.Combine(root, "captured-variants"), [unit, duplicate, sister, ..tabs]);
        Check(variants.RepairCapturedIdentities().Migrated == 5 && variants.Snapshot().Count == 4 && variants.Snapshot().All(x => x.Status == "translated"), "all proven variants migrate and duplicate original/conflict rows coalesce without losing translation");
        Check(variants.RepairCapturedIdentities().Migrated == 0, "general capture migration is idempotent");
        var asset = variants.Compile(Path.Combine(variants.Root, "runtime.jsonl"));
        var index = RuntimeAsset.Load(asset.AssetPath);
        var unitJson = "{\"result\":{\"effect_unit_list\":[{\"unit_id\":1001001,\"unit_illust_id\":1001001,\"unit_name\":\"流星の姫君\"},{\"unit_id\":1001001,\"unit_illust_id\":1001001,\"unit_name\":\"シスター\"}]}}";
        Check(UiJsonTranslator.TryTranslate("CampTop", unitJson, index, out _, out var unitCount, out var unitFallback) && unitCount == 2 && unitFallback == 0, "migrated unit variants compile into independent exact runtime matches");
        var tabJson = "{\"result\":{\"event_id\":6016,\"quest_id\":600016,\"stage_data\":[{\"tab_name\":\"チャレンジ\"},{\"tab_name\":\"ストーリー\"}]}}";
        Check(UiJsonTranslator.TryTranslate("BossRushEventTop", tabJson, index, out _, out var tabCount, out var tabFallback) && tabCount == 2 && tabFallback == 0, "migrated tabs compile into independent exact runtime matches");
        return checks;
    }

    private static TranslationEntry Enemy(string source)
    {
        var hash = UiJsonTranslator.Sha256(source);
        return new() { Key = "misc:SubdueEventTop:quest_id=300039:shop_id=2242:character_name#" + hash[..8],
            Field = "character_name", Source = source, SourceHash = hash, Status = "conflict",
            Identity = new() { ["quest_id"] = "300039", ["shop_id"] = "2242" },
            Origins = ["SubdueEventTop:$.result.stage_list[].enemy_data.character_name"] };
    }

    private static TranslationWorkspace Create(string root, TranslationEntry[] rows)
    {
        var source = Path.Combine(root, "translations/ui/source"); Directory.CreateDirectory(Path.Combine(source, "by_category"));
        var text = string.Join("\n", rows.Select(JsonData.Write)) + "\n";
        File.WriteAllText(Path.Combine(source, "ui_source.jsonl"), text);
        foreach (var category in rows.GroupBy(x => x.Category))
            File.WriteAllText(Path.Combine(source, "by_category/" + category.Key + ".jsonl"), string.Join("\n", category.Select(JsonData.Write)) + "\n");
        return new(root);
    }
}
