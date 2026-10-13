using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TSKHook.Workbench.Desktop;

public partial class MainWindow : Window
{
    private readonly string[] arguments;
    private readonly HttpClient modelClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
    private readonly HttpClient githubClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource lifetime = new();
    private LocalSettings settings = new();
    private TranslationWorkspace? workspace;
    private CancellationTokenSource? translationCancellation;
    private List<RowView> allRows = [];
    private TranslationEntry? selected;
    private PackageResult? package;
    private bool ready, binding, refreshing, dirty, busy, scanning, publishing;
    private int refreshVersion;
    private string lastAutomaticQueue = "";
    private Mutex? workspaceMutex;

    public MainWindow(string[] args)
    {
        arguments = args;
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
        Closing += OnClosing;
        timer.Tick += async (_, _) => await AutomaticTick();
    }

    private async Task InitializeAsync()
    {
        var smoke = arguments.Contains("--smoke");
        try
        {
            if (!smoke) settings = LocalSettings.Load();
            var index = Array.IndexOf(arguments, "--workspace");
            if (index >= 0 && index + 1 < arguments.Length) settings.Workspace = arguments[index + 1];
            if (string.IsNullOrWhiteSpace(settings.Workspace)) settings.Workspace = FindWorkspace();
            WorkspacePath.Text = settings.Workspace; GamePath.Text = settings.GameDirectory; CapturePath.Text = settings.CaptureDirectory;
            Repository.Text = settings.Repository; Endpoint.Text = settings.Model.Endpoint; ModelName.Text = settings.Model.Model;
            BatchSize.Text = settings.Model.BatchSize.ToString(); Concurrency.Text = settings.Model.Concurrency.ToString(); MaxTokens.Text = settings.Model.MaxOutputTokens.ToString();
            AutoImport.IsChecked = settings.AutoImport; AutoTranslate.IsChecked = !smoke && settings.AutoTranslate;
            Version.Text = DateTime.Now.ToString("yyyy.MM.dd") + ".1";
            if (!smoke)
            {
                try { var secrets = LocalSecrets.Load(); ModelKey.Password = secrets.GetValueOrDefault("model", ""); GitHubToken.Password = secrets.GetValueOrDefault("github", ""); }
                catch { Log("本机凭据无法解密，请重新输入并保存。原凭据文件未改动。"); }
            }
            ready = true;
            if (Directory.Exists(settings.Workspace)) await ReloadWorkspace();
            else Log("先在“模型与路径”中选择翻译仓库目录。");
            if (smoke)
            {
                LocalSecrets.SelfTest();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var outputIndex = Array.IndexOf(arguments, "--smoke");
                var output = outputIndex + 1 < arguments.Length ? arguments[outputIndex + 1] : Path.Combine(settings.Workspace, ".local/workbench.png");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                var visual = (FrameworkElement)Content;
                var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(output)) encoder.Save(file);
                for (var tab = 1; tab < Pages.Items.Count; tab++)
                {
                    Pages.SelectedIndex = tab;
                    await Dispatcher.InvokeAsync(() => { UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
                    var tabBitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    tabBitmap.Render(visual);
                    var tabEncoder = new PngBitmapEncoder(); tabEncoder.Frames.Add(BitmapFrame.Create(tabBitmap));
                    using var tabFile = File.Create(Path.ChangeExtension(output, $"tab-{tab}.png")); tabEncoder.Save(tabFile);
                }
                File.WriteAllText(output + ".json", JsonData.Write(new { loaded = workspace != null, rows = allRows.Count, visible = Rows.Items.Count }));
                Application.Current.Shutdown(workspace == null ? 1 : 0);
                return;
            }
            timer.Start();
        }
        catch (Exception e)
        {
            Log("初始化失败：" + e.Message);
            if (smoke) Application.Current.Shutdown(1);
        }
    }

    private async Task ReloadWorkspace()
    {
        var path = Path.GetFullPath(WorkspacePath.Text.Trim());
        if (!File.Exists(Path.Combine(path, "translations/ui/source/ui_source.jsonl")))
            throw new InvalidOperationException("此目录没有 translations/ui/source/ui_source.jsonl，请选择翻译仓库根目录。");
        if (translationCancellation != null) throw new InvalidOperationException("请先暂停翻译再切换工作目录。");
        var name = "Local\\TSKHook-UITranslations-" + UiJsonTranslator.Sha256(path.ToUpperInvariant());
        // Acquire on the dispatcher thread and release on the same thread.
        var mutex = new Mutex(false, name);
        bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { mutex.Dispose(); throw new InvalidOperationException("另一个工作台正在使用此目录。"); }
        try
        {
            var loaded = await Task.Run(() => new TranslationWorkspace(path));
            workspaceMutex?.ReleaseMutex(); workspaceMutex?.Dispose(); workspaceMutex = mutex;
            workspace = loaded; settings.Workspace = path; WorkspacePath.Text = path; dirty = false;
            await RefreshView(); Log("工作目录已加载：" + path);
        }
        catch { mutex.ReleaseMutex(); mutex.Dispose(); throw; }
    }

    private async Task RefreshView()
    {
        if (workspace == null) return;
        var version = ++refreshVersion;
        var current = workspace;
        var views = await Task.Run(() => current.Snapshot().Select(x => new RowView(x, ValidationRules.Check(x))).ToList());
        if (version != refreshVersion || lifetime.IsCancellationRequested) return;
        allRows = views;
        AllCount.Text = views.Count.ToString("N0");
        PendingCount.Text = views.Count(x => x.Data.Status is "new" or "stale").ToString("N0");
        ReviewCount.Text = views.Count(x => x.Problems.Count > 0 || x.Data.Status is "needs_review" or "conflict").ToString("N0");
        ReadyCount.Text = views.Count(x => x.Data.Status is "translated" or "reviewed" or "verified" && !string.IsNullOrWhiteSpace(x.Data.Translation)).ToString("N0");
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (!ready) return;
        IEnumerable<RowView> filtered = allRows;
        filtered = Filter.SelectedIndex switch
        {
            0 => filtered.Where(x => x.Data.Status is "new" or "stale" or "needs_review" or "conflict"),
            1 => filtered.Where(x => x.Problems.Count > 0 || x.Data.Status is "needs_review" or "conflict"),
            3 => filtered.Where(x => x.Data.Status is "reviewed" or "verified"),
            _ => filtered
        };
        var query = Search.Text.Trim();
        if (query.Length > 0) filtered = filtered.Where(x => x.Data.Source.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            x.Data.Translation.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Data.Key.Contains(query, StringComparison.OrdinalIgnoreCase));
        var matches = filtered.OrderBy(x => x.Data.Key, StringComparer.Ordinal).ToArray();
        var shown = matches.Take(1000).ToList();
        refreshing = true;
        Rows.ItemsSource = shown;
        Rows.SelectedItem = shown.FirstOrDefault(x => x.Data.Key == selected?.Key) ?? (dirty ? null : shown.FirstOrDefault());
        refreshing = false;
        ResultCount.Text = matches.Length > 1000 ? $"{matches.Length:N0} 条 · 显示前 1,000 条" : $"{matches.Length:N0} 条";
        if (!dirty && Rows.SelectedItem is RowView row) BindEntry(row);
        else if (!dirty && selected != null && Rows.SelectedItem == null) { selected = null; BindEmpty(); }
    }

    private void BindEntry(RowView row)
    {
        binding = true; selected = row.Data.Copy();
        Original.Text = row.Data.Source; EditedTranslation.Text = row.Data.Translation;
        EntryInfo.Text = row.Data.Key + "\n" + (row.Problems.Count == 0 ? string.Join(" · ", row.Data.Origins.Take(2)) : string.Join("；", row.Problems.Select(x => x.Message)));
        EntryInfo.ToolTip = EntryInfo.Text; Original.ToolTip = string.Join("\n", row.Data.Origins);
        dirty = false; binding = false;
    }
    private void BindEmpty() { binding = true; Original.Clear(); EditedTranslation.Clear(); EntryInfo.Text = "选择一条文本查看原文、上下文与译文。"; binding = false; }
    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || Rows.SelectedItem is not RowView row) return;
        if (dirty && selected?.Key != row.Data.Key && MessageBox.Show(this, "当前修订尚未保存，要放弃并切换吗？", "未保存修订", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        { refreshing = true; Rows.SelectedItem = Rows.Items.Cast<RowView>().FirstOrDefault(x => x.Data.Key == selected?.Key); refreshing = false; return; }
        BindEntry(row);
    }
    private void Editor_Changed(object sender, TextChangedEventArgs e) { if (!binding && selected != null) dirty = true; }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void SaveEntry_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        if (workspace == null || selected == null) return;
        var row = selected; var text = EditedTranslation.Text;
        await Task.Run(() => workspace.Edit(row.Key, row.SourceHash, text, "reviewed"));
        dirty = false; await RefreshView(); Log("已保存人工审校：" + row.Key);
    });
    private async void Exclude_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        if (workspace == null || selected == null) return;
        var row = selected; await Task.Run(() => workspace.Edit(row.Key, row.SourceHash, row.Source, "excluded"));
        dirty = false; await RefreshView(); Log("已排除此条翻译。");
    });
    private async void VerifyEntry_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        if (workspace == null || selected == null) return;
        if (string.IsNullOrWhiteSpace(EditedTranslation.Text)) throw new InvalidOperationException("译文不能为空。");
        var row = selected; var text = EditedTranslation.Text;
        await Task.Run(() => workspace.Edit(row.Key, row.SourceHash, text, "verified"));
        dirty = false; await RefreshView(); Log("已记录维护者的实机确认：" + row.Key);
    });

    private async Task ImportCaptured(bool manual)
    {
        if (workspace == null || scanning) return;
        var directory = CapturePath.Text.Trim();
        if (!Directory.Exists(directory)) { if (manual) Log("捕获目录不存在；请先在游戏中开启捕获或选择正确目录。"); return; }
        scanning = true;
        try
        {
            var changed = await Task.Run(() =>
            {
                var total = 0;
                foreach (var path in Directory.EnumerateFiles(directory, "ui_texts*.jsonl"))
                {
                    var result = workspace.ImportCapture(path);
                    total += result.Added + result.Changed + result.Conflicts;
                    if (result.Read > 0) Log($"捕获归档：新增 {result.Added}，原文变化 {result.Changed}，复用 {result.Reused}，冲突 {result.Conflicts}。");
                }
                return total;
            });
            if (changed > 0) await RefreshView();
            else if (manual) Log("捕获扫描完成，没有新的完整记录。");
        }
        finally { scanning = false; }
    }
    private async Task AutomaticTick()
    {
        if (busy || publishing || workspace == null || lifetime.IsCancellationRequested) return;
        try
        {
            if (AutoImport.IsChecked == true) await ImportCaptured(false);
            if (AutoTranslate.IsChecked == true && translationCancellation == null && !string.IsNullOrWhiteSpace(ModelKey.Password) && !string.IsNullOrWhiteSpace(ModelName.Text))
            {
                var queue = UiJsonTranslator.Sha256(string.Join("\n", allRows.Where(x => x.Data.Status is "new" or "stale").Select(x => x.Data.Key + x.Data.SourceHash)));
                if (queue != lastAutomaticQueue)
                {
                    lastAutomaticQueue = queue;
                    await RunTranslation(false);
                }
            }
        }
        catch (Exception e) { Log("自动任务暂停本轮：" + e.Message); }
    }
    private async Task RunTranslation(bool retry)
    {
        if (workspace == null) throw new InvalidOperationException("先加载工作目录。");
        if (translationCancellation != null || publishing) return;
        var model = ReadModel(); var key = ModelKey.Password;
        translationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        Log("开始处理待翻译条目。");
        try
        {
            var runner = new TranslationRunner(modelClient);
            var count = await runner.RunAsync(workspace, model, key, retry, Log, translationCancellation.Token);
            Log($"本轮完成，保存 {count} 条译文。");
        }
        catch (OperationCanceledException) { Log("翻译已暂停，已保存结果保留。"); }
        catch (Exception e) { Log("翻译未完成：" + e.Message); }
        finally
        {
            translationCancellation.Dispose(); translationCancellation = null;
            if (!lifetime.IsCancellationRequested) await RefreshView();
        }
    }
    private async void Translate_Click(object sender, RoutedEventArgs e) { try { await RunTranslation(false); } catch (Exception ex) { Log(ex.Message); } }
    private async void Retry_Click(object sender, RoutedEventArgs e) { try { await RunTranslation(true); } catch (Exception ex) { Log(ex.Message); } }
    private void Pause_Click(object sender, RoutedEventArgs e) { AutoTranslate.IsChecked = false; translationCancellation?.Cancel(); }
    private async void Import_Click(object sender, RoutedEventArgs e) => await Perform(() => ImportCaptured(true));
    private async void Reload_Click(object sender, RoutedEventArgs e) => await Perform(ReloadWorkspace);

    private async void SaveSettings_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        settings = new LocalSettings { Workspace = WorkspacePath.Text.Trim(), GameDirectory = GamePath.Text.Trim(), CaptureDirectory = CapturePath.Text.Trim(),
            Repository = Repository.Text.Trim(), Model = ReadModel(), AutoImport = AutoImport.IsChecked == true, AutoTranslate = AutoTranslate.IsChecked == true };
        settings.Save(); LocalSecrets.Save(ModelKey.Password, GitHubToken.Password);
        await ReloadWorkspace(); Log("本机设置已保存；凭据已使用 Windows 账户加密。");
    });
    private ModelSettings ReadModel() => new() { Endpoint = Endpoint.Text.Trim(), Model = ModelName.Text.Trim(), BatchSize = int.Parse(BatchSize.Text),
        Concurrency = int.Parse(Concurrency.Text), MaxOutputTokens = int.Parse(MaxTokens.Text) };
    private void WorkspaceBrowse_Click(object sender, RoutedEventArgs e) => Browse(WorkspacePath);
    private void GameBrowse_Click(object sender, RoutedEventArgs e)
    {
        Browse(GamePath);
        if (string.IsNullOrWhiteSpace(CapturePath.Text)) CapturePath.Text = Path.Combine(GamePath.Text, "BepInEx/plugins/TSKHook/ui_capture");
    }
    private void CaptureBrowse_Click(object sender, RoutedEventArgs e) => Browse(CapturePath);
    private void Browse(TextBox target) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName; }

    private async Task<PackageResult> PreparePackage()
    {
        if (workspace == null) throw new InvalidOperationException("先加载工作目录。");
        if (translationCancellation != null) throw new InvalidOperationException("请先暂停翻译再编译或发布。");
        if (dirty) throw new InvalidOperationException("当前修订尚未保存，请先保存或放弃。");
        var version = Version.Text.Trim(); var repository = Repository.Text.Trim();
        package = await Task.Run(() => workspace.BuildPackage(version, repository));
        PackageSummary.Text = $"版本：{package.Manifest.Version}\n译文：{package.Manifest.Entries:N0} 条\n大小：{package.Manifest.Size:N0} 字节\nSHA-256：{package.Manifest.Sha256}\n\n结构校验通过。需核对警告：{package.Compilation.Issues.Count:N0} 条（包括迁移前已有问题）\n" +
            string.Join("\n", package.Compilation.Issues.Take(30).Select(x => x.Key + " — " + x.Message)) + $"\n\n完整报告：{Path.Combine(package.Directory, "validation.json")}\n包目录：{package.Directory}";
        Log("翻译包已编译并通过插件读取校验。");
        return package;
    }
    private async void Compile_Click(object sender, RoutedEventArgs e) => await Perform(async () => { await PreparePackage(); });
    private async void Preview_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        var built = await PreparePackage();
        var plugins = Path.Combine(GamePath.Text.Trim(), "BepInEx/plugins");
        if (!Directory.Exists(plugins)) throw new InvalidOperationException("游戏目录中没有 BepInEx/plugins。");
        var destination = Path.Combine(plugins, "TSKHook/ui_preview.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        await Task.Run(() => { File.Copy(built.Compilation.AssetPath, temporary, true); TranslationPackageStore.Verify(temporary, built.Manifest); File.Move(temporary, destination, true); });
        Log("本地预览已安装。在游戏 F1 设置中开启“本地预览模式”，再重新进入要检查的页面。");
    });
    private async void Publish_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        var built = await PreparePackage();
        if (MessageBox.Show(this, $"将向 {Repository.Text.Trim()} 发布 {built.Manifest.Version}，共 {built.Manifest.Entries:N0} 条。\n包含 {built.Compilation.Issues.Count} 条需人工判断的警告，可在完整报告中检查。\n此操作会提交并推送 translations/ui 的更改，然后公开发布翻译包。\n\n确认发布？", "发布翻译更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var repository = Repository.Text.Trim(); var token = GitHubToken.Password;
        publishing = true;
        try { var url = await new ReleasePublisher(githubClient).PublishAsync(workspace!, built, repository, token, Log, lifetime.Token); Log("已发布：" + url); }
        finally { publishing = false; }
    });
    private void OpenPackage_Click(object sender, RoutedEventArgs e)
    {
        if (package != null && Directory.Exists(package.Directory)) Process.Start(new ProcessStartInfo(package.Directory) { UseShellExecute = true });
    }

    private async Task Perform(Func<Task> action)
    {
        if (busy || scanning) { Log("当前操作尚未完成，请稍候。"); return; }
        busy = true;
        try { await action(); } catch (Exception e) { Log("操作未完成：" + e.Message); } finally { busy = false; }
    }
    private void Log(string message)
    {
        if (lifetime.IsCancellationRequested) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Log(message)); return; }
        var lines = (LogOutput.Text + DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine).Split(Environment.NewLine);
        LogOutput.Text = string.Join(Environment.NewLine, lines.TakeLast(100)); LogOutput.ScrollToEnd();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (publishing) { e.Cancel = true; Log("正在提交发布，请等待完成后关闭。"); return; }
        if (dirty && MessageBox.Show(this, "当前修订尚未保存，仍要关闭吗？", "未保存修订", MessageBoxButton.YesNo) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        timer.Stop(); lifetime.Cancel(); translationCancellation?.Cancel();
        workspaceMutex?.ReleaseMutex(); workspaceMutex?.Dispose(); workspaceMutex = null;
    }
    private static string FindWorkspace()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "translations/ui/source/ui_source.jsonl"))) return directory.FullName;
        return "";
    }
    private sealed class RowView(TranslationEntry data, List<ValidationIssue> problems)
    {
        public TranslationEntry Data { get; } = data;
        public List<ValidationIssue> Problems { get; } = problems;
        public string Category => Data.Category;
        public string Source => Data.Source.Replace("\n", " ↵ ");
        public string Issues => Problems.Count == 0 ? "" : Problems.Count.ToString();
        public string StatusLabel => Data.Status switch { "new" => "待翻译", "stale" => "原文更新", "translated" => "机器初译", "reviewed" => "已审校", "verified" => "已实机确认", "needs_review" => "待核对", "conflict" => "身份冲突", "excluded" => "已排除", _ => Data.Status };
    }
}
