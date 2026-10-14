using System.Text.Json;
using TSKHook;
using TSKHook.Workbench;

internal static class StaticUiTests
{
    internal static int Run(string parent)
    {
        var assertions = 0;
        void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
        var root = Path.Combine(parent, "static-ui");
        var path = Path.Combine(root, StaticUiCatalog.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rows = new[]
        {
            new StaticUiEntry { SchemaVersion = 1, Source = "メニュー", SourceHash = UiJsonTranslator.Sha256("メニュー"), Translation = "菜单", Status = "translated" },
            new StaticUiEntry { SchemaVersion = 1, Source = "残り{0}回", SourceHash = UiJsonTranslator.Sha256("残り{0}回"), Translation = "剩余{0}次", Template = true, Status = "translated" },
            new StaticUiEntry { SchemaVersion = 1, Source = "保留", SourceHash = UiJsonTranslator.Sha256("保留"), Status = "needs_review" }
        };
        void Write() => File.WriteAllText(path, string.Join("\n", rows.Select(JsonData.Write)) + "\n");
        Write();
        var workspace = new TranslationWorkspace(root);
        Check(workspace.Snapshot().Count == 0, "static UI is not imported into the network/workbench review queue");
        var package = workspace.BuildPackage("fixture-static-ui", "fixture/repo");
        Check(package.Manifest.Entries == 2 && package.Manifest.MinimumPluginVersion == "1.4.6", "static UI publication requires plugin1.4.6 and excludes unreviewed data");
        var lines = File.ReadAllLines(package.Compilation.AssetPath).Select(line => JsonDocument.Parse(line)).ToArray();
        try { Check(lines.All(line => line.RootElement.GetProperty("renderedOnly").GetBoolean()), "every static UI runtime row has display-only scope"); }
        finally { foreach (var line in lines) line.Dispose(); }
        var index = RuntimeAsset.Load(package.Compilation.AssetPath);
        Check(UiRenderedTextTranslator.TryTranslate("残り７回", index, out var output) && output == "剩余７次", "compiled templates preserve current full-width digits");
        var json = "{\"result\":{\"label\":\"メニュー\"}}";
        Check(!UiJsonTranslator.TryTranslate("Fixture", json, index, out var unchanged, out _, out _) && unchanged == json,
            "published static catalog cannot rewrite network JSON");
        File.AppendAllText(path, "\n");
        var externalRejected = false;
        try { workspace.Compile(Path.Combine(root, "stale.jsonl")); } catch (InvalidOperationException) { externalRejected = true; }
        Check(externalRejected, "static catalog external edits participate in workspace fingerprints");
        rows[1].Translation = "剩余{1}次"; Write();
        var badTemplateRejected = false;
        try { new TranslationWorkspace(root).Compile(Path.Combine(root, "bad.jsonl")); } catch (ArgumentException) { badTemplateRejected = true; }
        Check(badTemplateRejected, "publication rejects mismatched template tokens");
        rows[1].Translation = "剩余{0}次"; rows[0].SourceHash = UiJsonTranslator.Sha256("changed"); Write();
        var changedHashRejected = false;
        try { StaticUiCatalog.Read(root); } catch (InvalidDataException) { changedHashRejected = true; }
        Check(changedHashRejected, "publication rejects changed static source hashes");
        return assertions;
    }
}
