using TSKHook.Workbench;
try
{
    var root = Path.GetFullPath(args.ElementAtOrDefault(0) ?? ".");
    var output = Path.GetFullPath(args.ElementAtOrDefault(1) ?? ".local/compiled/ui_translations.jsonl");
    var result = new TranslationWorkspace(root).Compile(output);
    Console.WriteLine($"Runtime entries: {result.Entries}; validation warnings: {result.Issues.Count}; asset: {result.AssetPath}");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
