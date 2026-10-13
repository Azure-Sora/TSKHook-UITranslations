using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TSKHook.Workbench;

public sealed class ReleasePublisher(HttpClient client)
{
    public async Task<string> PublishAsync(TranslationWorkspace workspace, PackageResult package, string repository,
        string token, Action<string> progress, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("请配置具有此仓库 Contents 写入权限的 GitHub Token。");
        var expectedUrl = $"https://github.com/{repository}/releases/download/translations-{package.Manifest.Version}/ui_translations.jsonl";
        if (package.Manifest.DownloadUrl != expectedUrl) throw new InvalidOperationException("发布目标与已编译清单不一致，请重新编译。");
        TranslationPackageStore.Verify(package.Compilation.AssetPath, package.Manifest);
        if ((await Git(workspace.Root, cancellation, "branch", "--show-current")).Trim() != "main")
            throw new InvalidOperationException("请在翻译仓 main 分支发布。");
        var remote = (await Git(workspace.Root, cancellation, "remote", "get-url", "origin")).Trim().TrimEnd('/');
        if (remote.EndsWith(".git")) remote = remote[..^4];
        if (remote != "https://github.com/" + repository && remote != "git@github.com:" + repository)
            throw new InvalidOperationException("origin 与发布仓库不一致。");
        if (!string.IsNullOrWhiteSpace(await Git(workspace.Root, cancellation, "diff", "--cached", "--name-only")))
            throw new InvalidOperationException("Git 暂存区有其他更改，请先处理后发布。");
        await Git(workspace.Root, cancellation, "add", "--", "translations/ui");
        if (!string.IsNullOrWhiteSpace(await Git(workspace.Root, cancellation, "diff", "--cached", "--name-only")))
            await Git(workspace.Root, cancellation, "commit", "-m", "translations: publish " + package.Manifest.Version);
        await Git(workspace.Root, cancellation, "push", "origin", "HEAD:main");
        var commit = (await Git(workspace.Root, cancellation, "rev-parse", "HEAD")).Trim();
        progress("源数据已推送，正在上传翻译包。");
        var tag = "translations-" + package.Manifest.Version;
        // Create as draft; only publish after all assets have uploaded successfully.
        var created = await Api(HttpMethod.Post, $"https://api.github.com/repos/{repository}/releases", token,
            new { tag_name = tag, target_commitish = commit, name = "UI translations " + package.Manifest.Version, draft = true,
                body = $"{package.Manifest.Entries} 条 UI 译文。结构校验通过；保留 {package.Compilation.Issues.Count} 条数值、换行或历史原文核对提示，详见 validation.json。\n\nSHA-256: {package.Manifest.Sha256}" }, cancellation);
        var id = created.GetProperty("id").GetInt64();
        foreach (var name in new[] { "ui_translations.jsonl", "manifest.json", "validation.json" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://uploads.github.com/repos/{repository}/releases/{id}/assets?name={name}");
            Headers(request, token);
            request.Content = new StreamContent(File.OpenRead(Path.Combine(package.Directory, name)));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await client.SendAsync(request, cancellation);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"附件上传失败（HTTP {(int)response.StatusCode}）；草稿保留，请在 GitHub 检查后使用新版本号重试。");
        }
        var published = await Api(HttpMethod.Patch, $"https://api.github.com/repos/{repository}/releases/{id}", token,
            new { draft = false, make_latest = "true" }, cancellation);
        return published.GetProperty("html_url").GetString()!;
    }

    private async Task<JsonElement> Api(HttpMethod method, string url, string token, object data, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, url);
        Headers(request, token);
        request.Content = new StringContent(JsonData.Write(data), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub 请求失败（HTTP {(int)response.StatusCode}）；检查版本是否已存在及 Token 权限。");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        return doc.RootElement.Clone();
    }
    private static void Headers(HttpRequestMessage request, string token)
    { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); request.Headers.UserAgent.ParseAdd("TSKHook-UITranslations/0.1.0"); request.Headers.Accept.ParseAdd("application/vnd.github+json"); }
    private static async Task<string> Git(string root, CancellationToken cancellation, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Git。");
        using var registration = cancellation.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        var error = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        await error; // Avoid echoing credential-bearing remote URLs from Git errors.
        if (process.ExitCode != 0) throw new InvalidOperationException("Git " + args[0] + " 失败；请检查仓库状态、网络和 Git 凭据。");
        return await output;
    }
}
