using System.Windows;

namespace TSKHook.Workbench.Desktop;

public partial class MainWindow
{
    private ModelReviewMode? activeReviewMode;
    private async void BulkUnify_Click(object sender, RoutedEventArgs e) => await RunModelReview(ModelReviewMode.Unify);
    private async void BulkCheck_Click(object sender, RoutedEventArgs e) => await RunModelReview(ModelReviewMode.Check);
    private async void SelectedUnify_Click(object sender, RoutedEventArgs e) => await RunModelReview(ModelReviewMode.Unify, selectedMembers);
    private async void SelectedCheck_Click(object sender, RoutedEventArgs e) => await RunModelReview(ModelReviewMode.Check, selectedMembers);

    private void SetReviewButtons()
    {
        var idle = translationCancellation == null;
        BulkUnifyButton.IsEnabled = BulkCheckButton.IsEnabled = idle;
        SelectedUnifyButton.IsEnabled = SelectedCheckButton.IsEnabled = idle && selectedMembers.Length > 0 && selectedMembers.All(ModelReviewTasks.CanReview);
    }

    private async Task RunModelReview(ModelReviewMode mode, TranslationEntry[]? selectedRows = null)
    {
        if (translationCancellation != null || busy || scanning || publishing) { Log("请等待当前任务完成，或先暂停正在运行的模型任务。"); return; }
        if (dirty) { Log("请先保存当前修订，再进行模型核对。"); return; }
        if (workspace == null) { Log("先加载工作目录。"); return; }
        activeReviewMode = mode; translationProgress = null;
        translationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var title = mode == ModelReviewMode.Unify ? "模型统一译文" : "模型核对修正";
        TranslationDetails.Visibility = Visibility.Visible; TranslationStatus.Text = title + " · 正在整理任务";
        TranslationCounts.Text = "正在整理可核对内容…"; TranslationActivity.Text = "结果自动保存；判断理由可在“问题与冲突”查看。";
        TranslationBar.IsIndeterminate = true; TranslationBar.Value = 0;
        TranslateButton.IsEnabled = RetryButton.IsEnabled = RetranslateButton.IsEnabled = UnifyCandidateButton.IsEnabled = ExcludeConflictButton.IsEnabled = false;
        PauseButton.IsEnabled = true; SetReviewButtons(); translationWatch.Restart(); translationTimer.Start();
        try
        {
            var current = workspace; var model = ReadModel(true); var key = ReadModelKey(true); var provider = ReviewProviderName();
            var cancellation = translationCancellation.Token;
            Log($"{title}：{provider} / {model.Model}，每批最多 {Math.Min(model.BatchSize, ModelReviewProtocol.MaximumBatchSize)}，并发 {model.Concurrency}。" +
                (selectedRows == null ? "范围为整个工作目录的相应待办。" : "范围为当前选中内容；统一会覆盖同原文的非人工位置。"));
            Log("每批最多等待 15 分钟（含接收响应）；核对请求均不自动重发；逐项保存有效结果，失败响应留存诊断。暂停后供应商仍可能继续处理已发送请求。");
            var result = await Task.Run(() => new ModelReviewRunner(modelClient).RunAsync(current, model, key, provider, mode, Log, cancellation,
                value => Dispatcher.Invoke(() => ShowTranslationProgress(value)), selectedRows));
            TranslationStatus.Text = translationProgress?.TotalTasks == 0 ? "没有符合条件的核对任务" : title + (result.FailedTasks > 0 ? "结束 · 有失败任务" : "完成");
            TranslationActivity.Text = $"解决 {result.ResolvedTasks:N0} 个任务；暂缓 {result.DeferredTasks:N0}，失败 {result.FailedTasks:N0}，因内容变化跳过 {result.SkippedTasks:N0}。保存 {result.SavedPositions:N0} 个位置的结果与理由。";
            Log(TranslationActivity.Text);
        }
        catch (OperationCanceledException) { TranslationStatus.Text = title + "已暂停"; TranslationActivity.Text = "已保存结果保留；未完成内容仍在原队列。"; Log(TranslationActivity.Text); }
        catch (Exception e) { TranslationStatus.Text = title + "未完成"; TranslationActivity.Text = e.Message; Log(TranslationActivity.Text); }
        finally
        {
            translationTimer.Stop(); translationWatch.Stop(); TranslationElapsed.Text = $"已用时 {translationWatch.Elapsed:hh\\:mm\\:ss}";
            TranslationBar.IsIndeterminate = false;
            translationCancellation.Dispose(); translationCancellation = null; activeReviewMode = null;
            TranslateButton.IsEnabled = RetryButton.IsEnabled = true; PauseButton.IsEnabled = false; SetReviewButtons();
            if (!lifetime.IsCancellationRequested)
                try { await RefreshView(); } catch (Exception e) { Log("刷新列表失败：" + e.Message); }
        }
    }
}
