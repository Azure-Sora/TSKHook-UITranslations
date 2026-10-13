using TSKHook;
using TSKHook.Workbench;

internal static class ReviewTests
{
    public static async Task<int> Run(string root)
    {
        var checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        var width = Row("width", "２４時", "24时", "needs_review");
        var hp = Row("hp", "HP50％回復", "回复50%HP", "needs_review");
        var wrong = Row("wrong", "HP50％回復", "回复51%HP", "needs_review");
        var blank = Row("blank", "名前", "", "needs_review");
        Check(ValidationRules.Check(width).Count == 0 && ValidationRules.Check(hp).Count == 0, "digit width and percent HP are not structural errors");
        Check(ValidationRules.Check(wrong).Any(x => x.Kind == "numbers"), "real numeric change still flagged");
        Check(ValidationRules.Check(Row("format", "名前%s HP%d {amount}", "名字 HP {amount}", "translated")).Any(x => x.Kind == "placeholders"), "real printf placeholders still protected");
        Check(ValidationRules.Check(Row("format2", "%1$s {floor}", "%1$s {other}", "translated")).Any(x => x.Fatal), "named and positional placeholders retained");
        var recheck = Create(Path.Combine(root, "recheck"), [width, hp, wrong, blank]);
        Check(recheck.RecheckMachineResults() == 2, "recheck clears only nonempty now-valid machine results");
        var reread = new TranslationWorkspace(recheck.Root).Snapshot();
        Check(reread.Single(x => x.Key == "width").SourceHash == width.SourceHash && reread.Single(x => x.Key == "width").Translation == width.Translation, "recheck never rehashes or rewrites translation");
        Check(reread.Where(x => x.Key is "wrong" or "blank").All(x => x.Status == "needs_review"), "real issues and blanks remain pending review");
        var manual = wrong.Copy(); manual.Status = "reviewed";
        Check(ReviewQueue.Classify(new([manual], false), ValidationRules.Check(manual)) == ReviewKind.Ready, "manual acknowledgement closes nonfatal warning");
        var broken = Row("tag", "<b>名前</b>", "名字", "reviewed");
        Check(ReviewQueue.Classify(new([broken], false), ValidationRules.Check(broken)) == ReviewKind.Revision, "fatal issue cannot hide behind reviewed status");
        var a = Row("a", "同じ", "旧译一", "translated");
        var b = Row("b", "同じ", "旧译二", "reviewed");
        var c = Row("c", "同じ", "", "new");
        var excluded = Row("excluded", "同じ", "保留", "excluded");
        var unify = Create(Path.Combine(root, "unify"), [a, b, c, excluded]);
        var originals = unify.Snapshot().Where(x => x.Status != "excluded").ToArray();
        Check(unify.UnifyTranslation(originals, "统一译文") == 3, "explicit unification covers exact content family");
        Check(new TranslationWorkspace(unify.Root).Snapshot().Single(x => x.Key == "excluded").Translation == "保留", "unification preserves explicitly excluded rows");
        var rejected = false;
        try { unify.UnifyTranslation(originals, "过期操作"); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "stale candidate list cannot overwrite newer edits");
        rejected = false;
        try { unify.EditGroup(unify.Snapshot().Where(x => x.Key == "a").ToArray(), "", "reviewed"); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "empty text cannot become reviewed");
        var first = Row("collision", "甲", "草稿甲", "conflict"); first.Key += "#" + first.SourceHash[..8];
        var second = Row("collision", "乙", "草稿乙", "conflict"); second.Key += "#" + second.SourceHash[..8];
        var baseConflict = first.Copy(); baseConflict.Key = "collision";
        var conflict = Create(Path.Combine(root, "conflict-review"), [first, second, baseConflict, Row("ready", "通常", "正常", "translated")]);
        rejected = false;
        try { conflict.EditGroup([first], "甲", "reviewed"); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "conflict cannot be disguised as reviewed");
        rejected = false;
        try { conflict.ExcludeConflicts([first]); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "conflict action requires complete reviewed family snapshot");
        Check(conflict.ExcludeConflicts(conflict.Snapshot().Where(x => x.Status == "conflict").ToArray()) == 3, "explicit conflict exclusion handles entire family");
        var retained = new TranslationWorkspace(conflict.Root).Snapshot().Single(x => x.Key == first.Key);
        Check(retained.Status == "excluded" && retained.Source == first.Source && retained.Translation == first.Translation, "conflict exclusion preserves evidence and draft");
        var reopenedConflict = new TranslationWorkspace(conflict.Root);
        rejected = false;
        try { var entry = reopenedConflict.Snapshot().Single(x => x.Key == "collision"); reopenedConflict.Edit(entry.Key, entry.SourceHash, "不应通过", "reviewed"); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "excluded conflict provenance survives reload even for base key");
        Check(conflict.Compile(Path.Combine(root, "conflict-runtime.jsonl")).Entries == 1, "excluded conflict records never enter compiled package");
        var selected = Create(Path.Combine(root, "selected-review"), [a, b, c, Row("other", "別の文", "", "new")]);
        using var client = new HttpClient(new ModelFixture());
        var runner = new TranslationRunner(client);
        var settings = new ModelSettings { Endpoint = "https://fixture/chat", Model = "fixture" };
        Directory.CreateDirectory(Path.Combine(selected.Root, ".local"));
        File.WriteAllText(Path.Combine(selected.Root, ".local/translation-failures.json"), JsonData.Write(new Dictionary<string,string> { ["unrelated\0hash"] = "HTTP 401" }));
        Check(await runner.RunAsync(selected, settings, "fixture", false, _ => { }, default, selectedRows: [c]) == 1, "selected translation bypasses unrelated wording variants");
        Check(selected.Snapshot().Single(x => x.Key == "a").Translation == a.Translation && selected.Snapshot().Single(x => x.Key == "b").Translation == b.Translation && selected.Snapshot().Single(x => x.Key == "other").Status == "new", "selected run touches neither existing variants nor unrelated pending entries");
        Check(File.ReadAllText(Path.Combine(selected.Root, ".local/translation-failures.json")).Contains("unrelated"), "selected run preserves unrelated failure ledger");
        rejected = false;
        try { await runner.RunAsync(selected, settings, "fixture", false, _ => { }, default, selectedRows: [b]); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "selected retranslation cannot overwrite reviewed text");
        rejected = false;
        try { await runner.RunAsync(selected, settings, "fixture", false, _ => { }, default, selectedRows: [c]); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "stale selected snapshot rejected before request");
        Check(await runner.RunAsync(recheck, settings, "fixture", false, _ => { }, default, selectedRows: [blank]) == 1, "blank needs-review entry can be explicitly retranslated");
        return checks;
    }
    private static TranslationEntry Row(string key, string source, string translation, string status) => new()
    { Key = key, Category = "item", Field = "detail", Source = source, SourceHash = UiJsonTranslator.Sha256(source), Translation = translation, Status = status };
    private static TranslationWorkspace Create(string root, TranslationEntry[] rows)
    {
        var source = Path.Combine(root, "translations/ui/source"); Directory.CreateDirectory(Path.Combine(source, "by_category"));
        File.WriteAllText(Path.Combine(source, "ui_source.jsonl"), string.Join("\n", rows.Select(JsonData.Write)) + "\n");
        File.WriteAllText(Path.Combine(source, "by_category/item.jsonl"), string.Join("\n", rows.Select(JsonData.Write)) + "\n");
        return new(root);
    }
}
