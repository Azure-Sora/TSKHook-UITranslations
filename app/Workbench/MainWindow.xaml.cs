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
    private readonly HttpClient modelClient = ModelCompletionClient.CreateHttpClient();
    private readonly HttpClient githubClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer translationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch translationWatch = new();
    private TranslationProgress? translationProgress;
    private readonly CancellationTokenSource lifetime = new();
    private LocalSettings settings = new();
    private TranslationWorkspace? workspace;
    private CancellationTokenSource? translationCancellation;
    private List<RowView> allRows = [];
    private Dictionary<string, TranslationEntry[]> relatedByContent = [];
    private Dictionary<string, TranslationEntry[]> relatedByConflict = [];
    private TranslationEntry[] relatedMembers = [], conflictMembers = [];
    private TranslationEntry? selected;
    private TranslationEntry[] selectedMembers = [];
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
        translationTimer.Tick += (_, _) => TranslationElapsed.Text = $"已用时 {translationWatch.Elapsed:hh\\:mm\\:ss}";
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
            Repository.Text = settings.Repository;
            BatchSize.Text = settings.Model.BatchSize.ToString(); Concurrency.Text = settings.Model.Concurrency.ToString(); MaxTokens.Text = settings.Model.MaxOutputTokens.ToString();
            AutoImport.IsChecked = settings.AutoImport; AutoTranslate.IsChecked = !smoke && settings.AutoTranslate;
            Version.Text = DateTime.Now.ToString("yyyy.MM.dd") + ".1";
            InitializeProviders(smoke);
            ready = true;
            if (Directory.Exists(settings.Workspace)) await ReloadWorkspace();
            else Log("先在“模型与路径”中选择翻译仓库目录。");
            if (smoke)
            {
                var searchIndex = Array.IndexOf(arguments, "--smoke-search");
                if (searchIndex >= 0 && searchIndex + 1 < arguments.Length) Search.Text = arguments[searchIndex + 1];
                var filterIndex = Array.IndexOf(arguments, "--smoke-filter");
                if (filterIndex >= 0 && filterIndex + 1 < arguments.Length) Filter.SelectedIndex = int.Parse(arguments[filterIndex + 1]);
                LocalSecrets.SelfTest();
                VerifyProviderBindingsForSmoke();
                if (arguments.Contains("--smoke-progress"))
                {
                    ShowTranslationProgress(new(480, 96, 20, 4, 2, 325, 24, "第 6/20 批：等待模型回复（24 个任务）。"));
                    TranslationElapsed.Text = "已用时 00:02:15";
                    TranslateButton.IsEnabled = RetryButton.IsEnabled = false; PauseButton.IsEnabled = true;
                }
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var outputIndex = Array.IndexOf(arguments, "--smoke");
                var output = outputIndex + 1 < arguments.Length ? arguments[outputIndex + 1] : Path.Combine(settings.Workspace, ".local/workbench.png");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                var visual = (FrameworkElement)Content;
                var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(output)) encoder.Save(file);
                for (var reviewTab = 1; reviewTab < ReviewTabs.Items.Count; reviewTab++)
                {
                    ReviewTabs.SelectedIndex = reviewTab;
                    if (reviewTab == 1 && CandidateList.Items.Count > 0) CandidateList.SelectedIndex = 0;
                    await Dispatcher.InvokeAsync(() => { UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
                    await Task.Delay(120);
                    var reviewBitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    reviewBitmap.Render(visual);
                    var reviewEncoder = new PngBitmapEncoder(); reviewEncoder.Frames.Add(BitmapFrame.Create(reviewBitmap));
                    using var reviewFile = File.Create(Path.ChangeExtension(output, $"review-{reviewTab}.png")); reviewEncoder.Save(reviewFile);
                }
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
        var merge = MergeDuplicates.IsChecked == true;
        List<TranslationEntry> snapshot = [];
        var views = await Task.Run(() => { snapshot = current.Snapshot(); return TranslationGroups.Create(snapshot, merge).Select(g =>
        {
            var problems = ValidationRules.Check(g.Representative);
            if (g.HasVariants) problems.Add(new(g.Representative.Key, "translation_variants", "相同原文存在不同译文，请按位置核对；不会自动覆盖或复用。", false));
            return new RowView(g.Representative, problems, g.Members, ReviewQueue.Classify(g, problems));
        }).ToList(); });
        if (version != refreshVersion || lifetime.IsCancellationRequested) return;
        allRows = views;
        relatedByContent = snapshot.GroupBy(TranslationGroups.ContentKey).ToDictionary(g => g.Key, g => g.ToArray());
        relatedByConflict = snapshot.Where(x => x.Status == "conflict").GroupBy(ReviewQueue.ConflictKey).ToDictionary(g => g.Key, g => g.ToArray());
        AllCount.Text = views.Sum(x => x.Members.Length).ToString("N0");
        PendingCount.Text = views.Count(x => x.Kind == ReviewKind.Pending).ToString("N0");
        ReviewCount.Text = views.Count(x => x.Kind is ReviewKind.Variants or ReviewKind.Revision or ReviewKind.Conflict).ToString("N0");
        QueueSummary.Text = $"可自动翻译 {views.Count(x => x.Kind == ReviewKind.Pending):N0} · 译文待统一 {views.Count(x => x.Kind == ReviewKind.Variants):N0} · 译文待修正 {views.Count(x => x.Kind == ReviewKind.Revision):N0} · 捕获冲突 {views.Count(x => x.Kind == ReviewKind.Conflict):N0}";
        ReadyCount.Text = views.Where(x => x.Data.Status is "translated" or "reviewed" or "verified" && !string.IsNullOrWhiteSpace(x.Data.Translation)).Sum(x => x.Members.Length).ToString("N0");
        ApplyFilter();
        SetReviewButtons();
    }

    private void ApplyFilter()
    {
        if (!ready) return;
        IEnumerable<RowView> filtered = allRows;
        filtered = Filter.SelectedIndex switch
        {
            0 => filtered.Where(x => x.Kind is ReviewKind.Pending or ReviewKind.Variants or ReviewKind.Revision or ReviewKind.Conflict),
            1 => filtered.Where(x => x.Kind == ReviewKind.Pending),
            2 => filtered.Where(x => x.Kind == ReviewKind.Variants),
            3 => filtered.Where(x => x.Kind == ReviewKind.Revision),
            4 => filtered.Where(x => x.Kind == ReviewKind.Conflict),
            5 => filtered.Where(x => x.Data.Status is "reviewed" or "verified"),
            6 => filtered.Where(x => x.Kind == ReviewKind.Excluded),
            8 => filtered.Where(x => x.Kind == ReviewKind.Ready && ReviewQueue.HasCurrentModelReview(x.Data)),
            _ => filtered
        };
        var query = Search.Text.Trim();
        if (query.Length > 0) filtered = filtered.Where(x => x.Data.Source.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            x.Data.Translation.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Members.Any(m => m.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Source.Contains(query, StringComparison.OrdinalIgnoreCase)));
        var matches = filtered.OrderBy(x => x.Data.Key, StringComparer.Ordinal).ToArray();
        var shown = matches.Take(1000).ToList();
        refreshing = true;
        Rows.ItemsSource = shown;
        Rows.SelectedItem = shown.FirstOrDefault(x => x.Data.Key == selected?.Key) ?? (dirty ? null : shown.FirstOrDefault());
        refreshing = false;
        ResultCount.Text = $"{matches.Length:N0} 组 / {matches.Sum(x => x.Members.Length):N0} 个位置" + (matches.Length > 1000 ? " · 前 1,000 组" : "");
        if (!dirty && Rows.SelectedItem is RowView row) BindEntry(row);
        else if (!dirty && selected != null && Rows.SelectedItem == null) { selected = null; BindEmpty(); }
    }

    private void BindEntry(RowView row)
    {
        binding = true; selected = row.Data.Copy();
        selectedMembers = row.Members.Select(x => x.Copy()).ToArray();
        Original.Text = row.Data.Source; EditedTranslation.Text = row.Data.Translation;
        EntryInfo.Text = $"本组 {row.Members.Length:N0} 个位置；保存审校只作用于本组。" +
            (row.Data.Source.Contains(NumberedNames.Token) ? " 楼层模板须保留 {floor}。" : "");
        EntryInfo.ToolTip = EntryInfo.Text; Original.ToolTip = string.Join("\n", row.Members.Take(30).Select(x => x.Source + "\n" + x.Key + "\n" + string.Join(" · ", x.Origins)));
        BindReviewDetails(row);
        dirty = false; binding = false;
        SetReviewButtons();
    }
    private void BindReviewDetails(RowView row)
    {
        relatedMembers = relatedByContent.GetValueOrDefault(TranslationGroups.ContentKey(row.Members[0]), [])
            .Where(x => !x.Protected && !ReviewQueue.IsCaptureConflict(x) && x.Status != "excluded").Select(x => x.Copy()).ToArray();
        conflictMembers = row.Data.Status == "conflict" ? row.Members.Select(ReviewQueue.ConflictKey).Distinct()
            .SelectMany(key => relatedByConflict.GetValueOrDefault(key, [])).DistinctBy(x => x.Key).Select(x => x.Copy()).ToArray() : [];
        CandidateList.ItemsSource = relatedMembers.Where(x => !string.IsNullOrWhiteSpace(x.Translation))
            .GroupBy(x => x.Translation).Select(g => new TranslationChoice(g.Key, g.Count(), string.Join("、", g.Select(x => StatusName(x.Status)).Distinct()),
                string.Join("\n", g.Select(x => x.Key)))).OrderByDescending(x => x.Count).ToArray();
        CandidateList.SelectedIndex = -1;
        CandidateSummary.Text = relatedMembers.Length == 0 ? "没有可选的同原文译文。" :
            $"同原文共 {relatedMembers.Length:N0} 个位置。模型统一只改非人工位置；手工统一会覆盖这些位置并标为已审校。";
        NextStep.Text = row.Kind switch
        {
            ReviewKind.Variants => "同一原文存在不同译文：可让核对模型选择并统一，或打开“已有译文”手工处理。",
            ReviewKind.Conflict => "同一匹配位置捕获到不同原文：打开“问题与冲突”对照来源，或暂不发布该冲突组。",
            ReviewKind.Revision => string.IsNullOrWhiteSpace(row.Data.Translation) ? "译文为空：可重译当前组，或填写译文后保存审校。" :
                row.Problems.Count == 0 ? "新版校验已无问题：可点“重新校验”恢复为机器初译，无需重新请求模型。" : "可让核对模型判断是否需要修正，也可手工修订后保存审校。",
            ReviewKind.Pending => "可自动翻译；也可以只翻译当前组。此前失败的项目请点“重试失败任务”。",
            _ => "可查看、修订译文；人工审校结果不会被选中项重译覆盖。"
        };
        var details = new System.Text.StringBuilder();
        if (row.Data.ModelReview is { } modelReview)
            details.AppendLine($"模型核对：{modelReview.Provider} / {modelReview.Model}").AppendLine($"判断：{modelReview.Decision} · {modelReview.ReviewedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}")
                .AppendLine("理由：" + modelReview.Reason).AppendLine(ReviewQueue.HasCurrentModelReview(row.Data) ? "此判断对应当前原文与译文。" : "该判断未关闭当前待办，或内容已变化。").AppendLine();
        details.AppendLine(NextStep.Text).AppendLine();
        foreach (var issue in row.Problems) details.AppendLine((issue.Fatal ? "必须修复：" : "核对提示：") + issue.Message);
        if (string.IsNullOrWhiteSpace(row.Data.Translation)) details.AppendLine("当前没有译文。");
        if (conflictMembers.Length > 0)
        {
            details.AppendLine().AppendLine($"本次冲突详情包含 {conflictMembers.Length} 条记录。不会自动选择其中一个原文，也不会删除捕获记录。");
            foreach (var entry in conflictMembers.OrderBy(ReviewQueue.ConflictKey).ThenBy(x => x.Key))
                details.AppendLine().AppendLine("原文：" + entry.Source).AppendLine("已有译文：" + (entry.Translation.Length == 0 ? "（无）" : entry.Translation))
                    .AppendLine("位置：" + entry.Key).AppendLine("来源：" + string.Join("；", entry.Origins)).AppendLine("首次捕获：" + entry.FirstSeenUtc);
        }
        else details.AppendLine().AppendLine("当前位置与来源：").AppendLine(string.Join("\n\n", row.Members.Select(x => x.Key + "\n" + string.Join("；", x.Origins))));
        IssueDetails.Text = details.ToString();
        SaveReviewButton.IsEnabled = ExcludeEntryButton.IsEnabled = !ReviewQueue.IsCaptureConflict(row.Data);
        RetranslateButton.IsEnabled = translationCancellation == null && row.Members.All(ReviewQueue.CanRetranslate);
        ExcludeConflictButton.IsEnabled = conflictMembers.Length > 0 && translationCancellation == null;
        AdoptCandidateButton.IsEnabled = UnifyCandidateButton.IsEnabled = false;
    }
    private static string StatusName(string status) => status switch
    { "new" => "未译", "stale" => "原文更新", "translated" => "机器初译", "reviewed" => "已审校", "verified" => "已实机确认", "needs_review" => "待核对", _ => status };
    private void Candidate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AdoptCandidateButton == null) return;
        AdoptCandidateButton.IsEnabled = CandidateList.SelectedItem is TranslationChoice;
        UnifyCandidateButton.IsEnabled = CandidateList.SelectedItem is TranslationChoice && relatedMembers.Length > 0 &&
            selected?.Source.Contains(NumberedNames.Token) != true && translationCancellation == null;
    }
    private void AdoptCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is not TranslationChoice choice) return;
        EditedTranslation.Text = choice.Text;
        ReviewTabs.SelectedIndex = 0;
        Log("已填入候选译文；请检查后保存审校。尚未修改其他位置。");
    }
    private async void UnifyCandidate_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        if (workspace == null || CandidateList.SelectedItem is not TranslationChoice choice) return;
        if (translationCancellation != null || dirty) throw new InvalidOperationException("请先暂停翻译并保存当前修订。");
        var members = relatedMembers; var text = choice.Text;
        var manual = members.Count(x => x.Status is "reviewed" or "verified");
        if (MessageBox.Show(this, $"将采用下列译文，统一同分类、字段及原文的 {members.Length} 个位置（其中 {manual} 个已人工确认），全部标为已审校。\n实机确认需在修改后重新记录。已排除及冲突位置不参与。\n\n{text}\n\n确定统一？", "统一同原文译文", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var count = await Task.Run(() => workspace.UnifyTranslation(members, text));
        dirty = false; await RefreshView(); Log($"已统一并审校 {count:N0} 个位置。");
    });
    private async void RepairIdentity_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        if (workspace == null) return;
        if (translationCancellation != null || dirty) throw new InvalidOperationException("请先暂停翻译并保存当前修订。");
        var result = await Task.Run(() => workspace.RepairCapturedIdentities());
        await RefreshView();
        Log($"捕获身份修复：迁移 {result.Migrated:N0} 个位置，复用 {result.Reused:N0} 个译文，保留待核对 {result.Skipped:N0} 个位置。" +
            (result.BackupDirectory == null ? "" : " 备份：" + result.BackupDirectory));
    });
    private async void Recheck_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        if (workspace == null) return;
        if (translationCancellation != null || dirty) throw new InvalidOperationException("请先暂停翻译并保存当前修订。");
        var count = await Task.Run(() => workspace.RecheckMachineResults());
        await RefreshView(); Log($"重新校验完成：{count:N0} 个位置已恢复为机器初译；仍有问题或空译文的条目保留待核对。未调用模型。");
    });
    private async void Retranslate_Click(object sender, RoutedEventArgs e)
    {
        if (dirty) { Log("请先保存当前修订再重译。"); return; }
        if (selectedMembers.Length == 0) return;
        await RunTranslation(false, selectedMembers.Select(x => x.Copy()).ToArray());
    }
    private async void ExcludeConflict_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        if (workspace == null || conflictMembers.Length == 0) return;
        if (translationCancellation != null || dirty) throw new InvalidOperationException("请先暂停翻译并保存当前修订。");
        var members = conflictMembers;
        if (MessageBox.Show(this, $"将暂不发布详情中 {members.Length} 条冲突记录。原文、已有译文及来源仍保留，可在“已排除”中查看。\n这不会修复插件匹配规则。确定继续？", "暂不发布冲突组", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var count = await Task.Run(() => workspace.ExcludeConflicts(members));
        await RefreshView(); Log($"已暂不发布 {count:N0} 条冲突记录，原始信息保留。");
    });
    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    { try { Clipboard.SetText(IssueDetails.Text); Log("详情已复制。"); } catch (Exception ex) { Log("复制失败：" + ex.Message); } }
    private sealed record TranslationChoice(string Text, int Count, string States, string Locations)
    { public string Summary => $"{Count} 个位置 · {States}"; }
    private void BindEmpty()
    {
        binding = true; selectedMembers = relatedMembers = conflictMembers = [];
        Original.Clear(); EditedTranslation.Clear(); IssueDetails.Clear(); CandidateList.ItemsSource = null;
        EntryInfo.Text = "选择一条文本查看原文、上下文与译文。"; NextStep.Text = "当前分类没有匹配条目，可切换其他处理队列。"; CandidateSummary.Text = "";
        SaveReviewButton.IsEnabled = ExcludeEntryButton.IsEnabled = RetranslateButton.IsEnabled = ExcludeConflictButton.IsEnabled = false;
        binding = false;
    }
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
    private async void Merge_Changed(object sender, RoutedEventArgs e)
    {
        if (dirty) { MergeDuplicates.IsChecked = MergeDuplicates.IsChecked != true; Log("请先保存当前修订再切换合并视图。"); return; }
        await RefreshView();
    }

    private async void SaveEntry_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        if (workspace == null || selected == null) return;
        var members = selectedMembers; var text = EditedTranslation.Text;
        var count = await Task.Run(() => workspace.EditGroup(members, text, "reviewed"));
        dirty = false; await RefreshView(); Log($"已保存人工审校，覆盖 {count:N0} 个位置。");
    });
    private async void Exclude_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        if (workspace == null || selected == null) return;
        var row = selected; var members = selectedMembers; await Task.Run(() => workspace.EditGroup(members, row.Source, "excluded"));
        dirty = false; await RefreshView(); Log("已排除此条翻译。");
    });
    private async void VerifyEntry_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        if (workspace == null || selected == null) return;
        if (selectedMembers.Length > 1 || selected.Source.Contains(NumberedNames.Token)) throw new InvalidOperationException("实机确认按实际位置记录，请先取消“合并重复原文”再选择已验证的位置。");
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
            if (AutoTranslate.IsChecked == true && translationCancellation == null && !string.IsNullOrWhiteSpace(ReadModelKey()) && !string.IsNullOrWhiteSpace(settings.Models.Translation.Model))
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
    private void ShowTranslationProgress(TranslationProgress value)
    {
        TranslationDetails.Visibility = Visibility.Visible;
        translationProgress = value;
        TranslationBar.IsIndeterminate = false;
        TranslationBar.Maximum = Math.Max(1, value.TotalTasks);
        TranslationBar.Value = value.CompletedTasks;
        TranslationCounts.Text = $"已处理 {value.CompletedTasks:N0}/{value.TotalTasks:N0} 个任务 · 批次 {value.CompletedBatches}/{value.TotalBatches} · 进行中 {value.ActiveBatches} · 已保存 {value.SavedPositions:N0} 个位置 · 失败 {value.FailedTasks:N0} 个任务";
        TranslationActivity.Text = value.Message;
        if (translationCancellation?.IsCancellationRequested != true)
            TranslationStatus.Text = activeReviewMode != null ? "正在模型核对 · 等待回复 / 处理结果" : value.ActiveBatches > 0 ? "正在翻译 · 等待模型 / 处理结果" : "正在处理翻译队列";
    }
    private async Task RunTranslation(bool retry, TranslationEntry[]? selectedRows = null)
    {
        if (translationCancellation != null) { Log("翻译已在运行，可在上方查看进度。"); return; }
        if (busy || scanning || dirty) { Log("请先保存修订并等待当前操作完成。"); return; }
        if (publishing) { TranslationStatus.Text = "暂不能开始翻译"; TranslationActivity.Text = "请等待当前发布完成。"; return; }
        translationProgress = null;
        TranslationDetails.Visibility = Visibility.Visible;
        TranslationStatus.Text = "准备翻译 · 正在整理待办";
        TranslationCounts.Text = "正在复用已有译文、合并重复任务并统计本轮数量…";
        TranslationActivity.Text = "准备完成后会显示总任务数；随后等待模型逐批返回。";
        TranslationBar.IsIndeterminate = true; TranslationBar.Value = 0;
        TranslateButton.IsEnabled = RetryButton.IsEnabled = RetranslateButton.IsEnabled = UnifyCandidateButton.IsEnabled = ExcludeConflictButton.IsEnabled = false; PauseButton.IsEnabled = true;
        translationWatch.Restart(); translationTimer.Start(); TranslationElapsed.Text = "已用时 00:00:00";
        translationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        SetReviewButtons();
        try
        {
            if (workspace == null) throw new InvalidOperationException("先加载工作目录。");
            var current = workspace; var model = ReadModel(); var key = ReadModelKey();
            var cancellation = translationCancellation.Token;
            Log($"开始翻译：{model.Model}，每批 {model.BatchSize}，并发 {model.Concurrency}。");
            var count = await Task.Run(() => new TranslationRunner(modelClient).RunAsync(current, model, key, retry, Log, cancellation,
                value => Dispatcher.Invoke(() => ShowTranslationProgress(value)), selectedRows));
            TranslationStatus.Text = translationProgress?.TotalTasks == 0 ? "没有可翻译的待办" :
                translationProgress?.FailedTasks > 0 ? "本轮结束 · 有失败任务" : "本轮翻译完成";
            TranslationActivity.Text = translationProgress?.TotalTasks == 0 ? translationProgress.Message :
                $"已保存 {count:N0} 个位置。" + (translationProgress?.FailedTasks > 0 ? "失败项可点击“重试失败任务”继续。" : "机器译文仍需核对。") + " 已保存位置包含待核对结果。";
            Log(TranslationStatus.Text + "；" + TranslationActivity.Text);
        }
        catch (OperationCanceledException) { TranslationStatus.Text = "翻译已暂停"; TranslationActivity.Text = "已保存结果保留；未完成任务可继续翻译。"; Log(TranslationActivity.Text); }
        catch (Exception e) { if (translationProgress == null) TranslationCounts.Text = "尚未开始发送翻译任务。"; TranslationStatus.Text = "翻译未能完成"; TranslationActivity.Text = e.Message; Log("翻译未完成：" + e.Message); }
        finally
        {
            translationTimer.Stop(); translationWatch.Stop();
            TranslationElapsed.Text = $"已用时 {translationWatch.Elapsed:hh\\:mm\\:ss}";
            TranslationBar.IsIndeterminate = false;
            TranslateButton.IsEnabled = RetryButton.IsEnabled = true; PauseButton.IsEnabled = false;
            translationCancellation.Dispose(); translationCancellation = null; SetReviewButtons();
            if (!lifetime.IsCancellationRequested)
                try { await RefreshView(); } catch (Exception e) { Log("刷新列表失败：" + e.Message); }
        }
    }
    private async void Translate_Click(object sender, RoutedEventArgs e) => await RunTranslation(false);
    private async void Retry_Click(object sender, RoutedEventArgs e) => await RunTranslation(true);
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        AutoTranslate.IsChecked = false;
        if (translationCancellation == null) return;
        TranslationStatus.Text = "正在暂停模型任务";
        TranslationActivity.Text = "正在取消未完成请求，已保存结果会保留。";
        PauseButton.IsEnabled = false; translationCancellation.Cancel();
    }
    private async void Import_Click(object sender, RoutedEventArgs e) => await Perform(() => ImportCaptured(true));
    private async void Reuse_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        if (workspace == null) return;
        if (dirty || translationCancellation != null) throw new InvalidOperationException("请先保存修订并暂停翻译。");
        var count = await Task.Run(() => workspace.ReuseTranslations());
        await RefreshView(); Log($"复用已有一致译文，覆盖 {count:N0} 个位置；不产生模型用量。");
    });
    private async void GenerateSkills_Click(object sender, RoutedEventArgs e) => await Perform(GenerateSkills);
    private async Task GenerateSkills()
    {
        if (workspace == null) throw new InvalidOperationException("先加载工作目录。");
        if (dirty || translationCancellation != null) throw new InvalidOperationException("请先保存修订并暂停翻译。");
        var result = await Task.Run(() => workspace.GenerateSkillPlainText());
        await RefreshView();
        Log($"技能正式文本：新增 {result.Generated}，同步 {result.Updated}，已有 {result.Existing}，译文分歧 {result.Ambiguous} 组。仅移除 color 标签，不推算数值。");
    }
    private async void Reload_Click(object sender, RoutedEventArgs e) => await Perform(ReloadWorkspace);

    private async void SaveSettings_Click(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        PersistSettings();
        await ReloadWorkspace(); Log("本机设置已保存；凭据已使用 Windows 账户加密。");
    });
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
        await GenerateSkills();
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
        translationTimer.Stop(); timer.Stop(); lifetime.Cancel(); translationCancellation?.Cancel();
        workspaceMutex?.ReleaseMutex(); workspaceMutex?.Dispose(); workspaceMutex = null;
    }
    private static string FindWorkspace()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "translations/ui/source/ui_source.jsonl"))) return directory.FullName;
        return "";
    }
    private sealed class RowView(TranslationEntry data, List<ValidationIssue> problems, TranslationEntry[] members, ReviewKind kind)
    {
        public ReviewKind Kind { get; } = kind;
        public string ActionLabel => ReviewQueue.Label(Kind);
        public TranslationEntry[] Members { get; } = members;
        public int PositionCount => Members.Length;
        public TranslationEntry Data { get; } = data;
        public List<ValidationIssue> Problems { get; } = problems;
        public string Category => Data.Category;
        public string Source => Data.Source.Replace("\n", " ↵ ");
        public string Issues => Problems.Count == 0 ? "" : Problems.Count.ToString();
        public string StatusLabel => ReviewQueue.HasCurrentModelReview(Data) ? "模型已核对" : Data.Status switch { "new" => "待翻译", "stale" => "原文更新", "translated" => "机器初译", "reviewed" => "已审校", "verified" => "已实机确认", "needs_review" => "待核对", "conflict" => "身份冲突", "excluded" => "已排除", _ => Data.Status };
    }
}
