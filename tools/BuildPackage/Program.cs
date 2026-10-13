using TSKHook.Workbench;
if (args.Length < 1) { Console.Error.WriteLine("Usage: BuildPackage <version> [workspace] [owner/repo] [--publish]"); return 2; }
try
{
    var workspace = new TranslationWorkspace(args.ElementAtOrDefault(1) ?? ".");
    var repository = args.ElementAtOrDefault(2) ?? "Azure-Sora/TSKHook-UITranslations";
    var package = workspace.BuildPackage(args[0], repository);
    Console.WriteLine($"Package: {package.Directory}; entries: {package.Manifest.Entries}; warnings: {package.Compilation.Issues.Count}; sha256: {package.Manifest.Sha256}");
    if (args.Contains("--publish"))
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var token = Environment.GetEnvironmentVariable("TSKHOOK_GITHUB_TOKEN") ?? "";
        Console.WriteLine(await new ReleasePublisher(client).PublishAsync(workspace, package, repository, token, Console.WriteLine, default));
    }
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
