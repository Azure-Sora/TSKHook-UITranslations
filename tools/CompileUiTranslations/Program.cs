using TSKHook.Workbench;
try
{
    var input = Path.GetFullPath(args.ElementAtOrDefault(0) ?? ".local/ui_texts_v2.jsonl");
    var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? ".");
    var result = new TranslationWorkspace(root).ImportCapture(input);
    Console.WriteLine($"Read {result.Read}; added {result.Added}; changed {result.Changed}; reused {result.Reused}; conflicts {result.Conflicts}");
    return result.Conflicts == 0 ? 0 : 1;
}
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
