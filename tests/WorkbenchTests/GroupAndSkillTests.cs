using TSKHook;
using TSKHook.Workbench;

internal static class GroupAndSkillTests
{
    public static async Task<int> Run(string root)
    {
        int assertions = 0;
        void Check(bool ok, string message) { assertions++; if (!ok) throw new Exception(message); }
        var rows = Enumerable.Range(0, 10).Select(i => Row("equip:" + i, "ATKアップ", "", "new", "equip", "skill_name")).ToArray();
        var ws = Create(Path.Combine(root, "groups"), rows);
        Check(TranslationGroups.Create(ws.Snapshot()).Single().Members.Length == 10, "ten identities shown as one task");
        Check(TranslationGroups.Create(ws.Snapshot(), false).Count == 10, "individual position view retained");
        var editing = Create(Path.Combine(root, "group-edit"), rows);
        Check(editing.EditGroup(editing.Snapshot(), "ATK上升", "reviewed") == 10 &&
            new TranslationWorkspace(editing.Root).Snapshot().All(x => x.Translation == "ATK上升" && x.Status == "reviewed"), "group review persisted for every identity");
        var fixture = new ModelFixture();
        using var client = new HttpClient(fixture);
        var settings = new ModelSettings { Endpoint = "https://fixture/chat", Model = "fixture" };
        var count = await new TranslationRunner(client).RunAsync(ws, settings, "fixture", false, _ => { }, default);
        Check(count == 10 && fixture.RowsReceived == 1, "one provider item fans out to ten runtime identities");
        Check(ws.Compile(Path.Combine(root, "groups-runtime/ui_translations.jsonl")).Entries == 10, "runtime keeps every identity");
        var original = ws.Snapshot();
        ws.Edit(original[0].Key, original[0].SourceHash, "独立人工译文", "reviewed");
        bool staleRejected = false;
        try { ws.EditGroup(original, "错误覆盖", "reviewed"); } catch (InvalidOperationException) { staleRejected = true; }
        Check(staleRejected && ws.Snapshot().Count(x => x.Translation == "测试译文") == 9, "group edit aborts atomically after member changes");
        Check(TranslationGroups.Create(ws.Snapshot()).All(x => x.HasVariants), "different existing translations flagged, never silently merged");
        var concurrent = Create(Path.Combine(root, "concurrent"), rows);
        var inflight = new ModelFixture { BeforeResponse = () => concurrent.Edit(rows[0].Key, rows[0].SourceHash, "人工优先", "reviewed") };
        using var concurrentClient = new HttpClient(inflight);
        Check(await new TranslationRunner(concurrentClient).RunAsync(concurrent, settings, "fixture", false, _ => { }, default) == 9 &&
            concurrent.Snapshot().Single(x => x.Key == rows[0].Key).Translation == "人工优先", "human edits beat in-flight grouped model results");
        var reuse = Create(Path.Combine(root, "reuse"), [Row("a", "ATKアップ", "ATK上升", "reviewed"), Row("b", "ATKアップ", "", "new")]);
        Check(reuse.ReuseTranslations() == 1 && reuse.Snapshot().Single(x => x.Key == "b").Status == "translated", "existing consistent translations reused without claiming human verification");
        var mixed = Create(Path.Combine(root, "mixed"), [Row("a", "ATKアップ", "甲", "reviewed"), Row("b", "ATKアップ", "乙", "translated"), Row("c", "ATKアップ", "", "new")]);
        Check(mixed.ReuseTranslations() == 0, "ambiguous existing translations not reused");
        var mixedFixture = new ModelFixture(); using var mixedClient = new HttpClient(mixedFixture);
        Check(await new TranslationRunner(mixedClient).RunAsync(mixed, settings, "fixture", false, _ => { }, default) == 0 && mixedFixture.RowsReceived == 0, "ambiguous source withheld from automatic model work");
        var protectedA = Row("p1", "ATKアップ", "", "new"); protectedA.Origins = ["BattleEnd:$.retry_data.cost_name"];
        var protectedB = Row("p2", "ATKアップ", "", "new"); protectedB.Origins = protectedA.Origins;
        Check(TranslationGroups.Create([protectedA, protectedB]).Count == 2, "protected game identifiers never share a translation task");

        var colored = Row("skill:preview", "攻撃<color=#abc>120</color>%", "攻击<color=#abc>120</color>%", "translated", "skill", "skill_detail");
        var skills = Create(Path.Combine(root, "skills"), [colored]);
        var generated = skills.GenerateSkillPlainText();
        var plain = skills.Snapshot().Single(x => x.Key != colored.Key);
        Check(generated.Generated == 1 && plain.Source == "攻撃120%" && plain.Translation == "攻击120%" && plain.SourceHash == UiJsonTranslator.Sha256(plain.Source), "plain skill derives exact observed numbers and hash");
        Check(skills.GenerateSkillPlainText().Generated == 0 && new TranslationWorkspace(skills.Root).Snapshot().Count == 2, "skill generation atomic and idempotent");
        skills.Edit(colored.Key, colored.SourceHash, "造成<color=#abc>120</color>%伤害", "reviewed");
        Check(skills.GenerateSkillPlainText().Updated == 1 && skills.Snapshot().Single(x => x.Key == plain.Key).Translation == "造成120%伤害", "preview correction synchronizes generated machine text");
        skills.Edit(plain.Key, plain.SourceHash, "人工伤害120%", "reviewed");
        skills.Edit(colored.Key, colored.SourceHash, "伤害<color=#abc>120</color>%", "reviewed");
        Check(skills.GenerateSkillPlainText().Updated == 0 && skills.Snapshot().Single(x => x.Key == plain.Key).Translation == "人工伤害120%", "reviewed plain text not overwritten");
        var other = Row("skill:other", "攻撃<color=#def>120</color>%", "另一效果<color=#def>120</color>%", "translated", "skill", "skill_detail");
        var ambiguous = Create(Path.Combine(root, "skill-ambiguous"), [colored, other]);
        Check(ambiguous.GenerateSkillPlainText().Ambiguous == 1 && ambiguous.Snapshot().Count == 2, "conflicting preview translations block derived text");
        var oldGenerated = plain.Copy(); oldGenerated.Status = "translated";
        var changedPreview = Create(Path.Combine(root, "changed-preview"), [colored, other, oldGenerated]);
        Check(changedPreview.GenerateSkillPlainText().Updated == 1 && changedPreview.Snapshot().Single(x => x.Key == plain.Key).Status == "needs_review", "new preview disagreement quarantines old machine-generated plain text");
        var invalid = colored.Copy(); invalid.Key = "bad"; invalid.Translation = "攻击120%";
        Check(Create(Path.Combine(root, "invalid-skill"), [invalid]).GenerateSkillPlainText().Generated == 0, "broken rich text cannot generate plain skill");
        var capturedPlain = Row("skill:captured", "攻撃120%", "现有正式译文120%", "reviewed", "skill", "skill_detail");
        var existing = Create(Path.Combine(root, "existing-skill"), [colored, capturedPlain]);
        Check(existing.GenerateSkillPlainText().Generated == 0 && existing.Snapshot().Single(x => x.Key == capturedPlain.Key).Translation == capturedPlain.Translation, "captured plain source preserved");
        var floors = Enumerable.Range(1, 230).Select(i => Row("tower:" + i, $"水神の塔{i}F", "", "new", "misc", "stage_name")).ToArray();
        var tower = Create(Path.Combine(root, "tower"), floors);
        var towerGroup = TranslationGroups.Create(tower.Snapshot()).Single();
        Check(towerGroup.Members.Length == 230 && towerGroup.Representative.Source == "水神の塔{floor}F", "230 floors become one placeholder task");
        Check(TranslationGroups.Create(tower.Snapshot(), false).Count == 230, "numbered names can still be inspected individually");
        var floorFixture = new ModelFixture { Translate = s => s.Replace("の", "之") };
        using var floorClient = new HttpClient(floorFixture);
        Check(await new TranslationRunner(floorClient).RunAsync(tower, settings, "fixture", false, _ => { }, default) == 230 && floorFixture.RowsReceived == 1, "one template model request covers all floors");
        Check(tower.Snapshot().All(x => x.Translation == x.Source.Replace("の", "之") && !x.Translation.Contains('{')), "each runtime row keeps original floor number");
        Check(tower.Compile(Path.Combine(root, "tower-package/ui_translations.jsonl")).Entries == 230, "all floor keys and hashes remain readable by plugin");
        var manualTower = Create(Path.Combine(root, "manual-tower"), floors);
        Check(manualTower.EditGroup(manualTower.Snapshot(), "水神之塔{floor}F", "reviewed") == 230 && manualTower.Snapshot().Last().Translation == "水神之塔230F", "manual template fills every floor");
        bool wrongNumberRejected = false;
        try { manualTower.EditGroup(manualTower.Snapshot(), "水神之塔1F", "reviewed"); } catch (InvalidDataException) { wrongNumberRejected = true; }
        Check(wrongNumberRejected, "fixed-number bulk overwrite rejected");
        Check(NumberedNames.Template(Row("skill-num", "水神の塔1F", "", "new", "skill", "skill_detail")) == null &&
            NumberedNames.Template(Row("multi", "水神の塔1-2F", "", "new", "misc", "stage_name")) == null, "skill and multi-number text excluded from heuristic");
        var reuseFloors = floors.Take(3).Select(x => x.Copy()).ToArray();
        foreach (var x in reuseFloors.Take(2)) { x.Translation = x.Source.Replace("の", "之"); x.Status = "reviewed"; }
        var towerReuse = Create(Path.Combine(root, "tower-reuse"), reuseFloors);
        Check(towerReuse.ReuseTranslations() == 1 && towerReuse.Snapshot().Single(x => x.Key == "tower:3").Translation == "水神之塔3F", "two consistent existing floors safely seed later floor reuse");
        reuseFloors[1].Translation = "不同的塔2F";
        Check(Create(Path.Combine(root, "tower-variants"), reuseFloors).ReuseTranslations() == 0, "conflicting tower translations are not propagated");
        var badTower = Create(Path.Combine(root, "bad-tower"), floors);
        var badFixture = new ModelFixture(); using var badClient = new HttpClient(badFixture);
        Check(await new TranslationRunner(badClient).RunAsync(badTower, settings, "fixture", false, _ => { }, default) == 0 && badTower.Snapshot().All(x => x.Status == "new"), "missing model placeholder cannot silently create wrong floors");
        var uncertain = Create(Path.Combine(root, "uncertain"), [Row("sure", "攻撃", "", "new"), Row("unsure", "不明名称", "", "new")]);
        var uncertainFixture = new ModelFixture { Translate = s => s == "不明名称" ? "" : "攻击" };
        using var uncertainClient = new HttpClient(uncertainFixture);
        Check(await new TranslationRunner(uncertainClient).RunAsync(uncertain, settings, "fixture", false, _ => { }, default) == 2 &&
            uncertain.Snapshot().Single(x => x.Key == "unsure").Status == "needs_review" &&
            uncertain.Snapshot().Single(x => x.Key == "sure").Translation == "攻击", "uncertain text is quarantined without discarding valid batch results");
        var uncertainTower = Create(Path.Combine(root, "uncertain-tower"), floors);
        using var uncertainTowerClient = new HttpClient(new ModelFixture { Translate = _ => "" });
        Check(await new TranslationRunner(uncertainTowerClient).RunAsync(uncertainTower, settings, "fixture", false, _ => { }, default) == 230 &&
            uncertainTower.Snapshot().All(x => x.Status == "needs_review" && x.Translation == ""), "empty numbered-template result safely defers all members");
        var invalidContract = Create(Path.Combine(root, "invalid-contract"), [Row("bad-contract", "攻撃", "", "new")]);
        using var invalidContractClient = new HttpClient(new ModelFixture { RewriteOutput = text => text.Replace("\"translation\":", "\"status\":\"translated\",\"translation\":") });
        Check(await new TranslationRunner(invalidContractClient).RunAsync(invalidContract, settings, "fixture", false, _ => { }, default) == 0 &&
            invalidContract.Snapshot().Single().Status == "new", "legacy status output rejected without modifying data");
        return assertions;
    }

    private static TranslationEntry Row(string key, string source, string translation, string status, string category = "equip", string field = "skill_name") =>
        new() { Key = key, Source = source, SourceHash = UiJsonTranslator.Sha256(source), Translation = translation, Status = status, Category = category, Field = field };

    private static TranslationWorkspace Create(string root, TranslationEntry[] rows)
    {
        Directory.CreateDirectory(Path.Combine(root, "translations/ui/source/by_category"));
        File.WriteAllLines(Path.Combine(root, "translations/ui/source/ui_source.jsonl"), rows.Select(JsonData.Write));
        foreach (var category in rows.GroupBy(x => x.Category))
            File.WriteAllLines(Path.Combine(root, "translations/ui/source/by_category", category.Key + ".jsonl"), category.Select(JsonData.Write));
        return new(root);
    }
}
