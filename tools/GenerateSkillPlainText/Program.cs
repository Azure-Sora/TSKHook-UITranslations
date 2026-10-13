using TSKHook;
using TSKHook.Workbench;

if (args.Contains("--self-test"))
{
    var temporary = Path.Combine(Path.GetTempPath(), "tsk-skill-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(Path.Combine(temporary, "translations/ui/source/by_category"));
        var row = new TranslationEntry { Key = "skill:test", Category = "skill", Field = "skill_detail",
            Source = "攻撃<color=#abc>120</color>%", Translation = "攻击<color=#abc>120</color>%", Status = "translated" };
        row.SourceHash = UiJsonTranslator.Sha256(row.Source);
        var text = JsonData.Write(row) + Environment.NewLine;
        File.WriteAllText(Path.Combine(temporary, "translations/ui/source/ui_source.jsonl"), text);
        File.WriteAllText(Path.Combine(temporary, "translations/ui/source/by_category/skill.jsonl"), text);
        var test = new TranslationWorkspace(temporary);
        if (test.GenerateSkillPlainText().Generated != 1 || test.GenerateSkillPlainText().Generated != 0 ||
            test.Snapshot().Single(x => x.Key != row.Key).Translation != "攻击120%") throw new Exception("Skill generation failed");
        Console.WriteLine("PASS: shared workbench generator, exact color stripping and idempotence");
        return 0;
    }
    finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
}

// Accept the historical master/skill paths as well as the repository root.
var input = Path.GetFullPath(args.ElementAtOrDefault(0) ?? ".");
var root = input.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(input)!, "../../..")) : input;
if (input.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
    (!string.Equals(input, Path.Combine(root, "translations/ui/source/ui_source.jsonl"), StringComparison.OrdinalIgnoreCase) ||
     args.Length > 1 && !string.Equals(Path.GetFullPath(args[1]), Path.Combine(root, "translations/ui/source/by_category/skill.jsonl"), StringComparison.OrdinalIgnoreCase)))
    throw new ArgumentException("Use a translation repository root, or its standard master/skill paths.");
if (!File.Exists(Path.Combine(root, "translations/ui/source/ui_source.jsonl")))
    throw new DirectoryNotFoundException("Translation workspace not found: " + root);
var result = new TranslationWorkspace(root).GenerateSkillPlainText();
Console.WriteLine(JsonData.Write(result));
return result.Ambiguous == 0 ? 0 : 1;
