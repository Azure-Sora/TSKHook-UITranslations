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
