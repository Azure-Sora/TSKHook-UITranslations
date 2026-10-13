using System.Windows;
using System.Windows.Controls;

namespace TSKHook.Workbench.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<string, string> localCredentials = new(StringComparer.Ordinal);
    private ModelProvider? editingProvider;
    private bool bindingProviders, credentialsReadable = true;
    private sealed record ModelOption(string ProviderId, string Model, string Label);

    private void InitializeProviders(bool smoke)
    {
        if (smoke)
        {
            settings.Model.Model = "gemini-3.8-flash";
            settings.Models.Providers = [new() { Id = "smoke", Name = "测试供应商", Endpoint = "https://fixture.invalid/v1/chat/completions", Models = ["gemini-3.8-flash", "claude-sonnet-5-5"] }];
            settings.Models.Translation = new() { ProviderId = "smoke", Model = "gemini-3.8-flash" };
            settings.Models.Review = new() { ProviderId = "smoke", Model = "claude-sonnet-5-5" };
        }
        settings.Models.MigrateLegacy(settings.Model);
        if (!smoke)
        {
            try
            {
                foreach (var pair in LocalSecrets.Load()) localCredentials[pair.Key] = pair.Value;
                if (!localCredentials.ContainsKey("provider:legacy") && localCredentials.TryGetValue("model", out var legacy))
                    localCredentials["provider:legacy"] = legacy;
                GitHubToken.Password = localCredentials.GetValueOrDefault("github", "");
            }
            catch { credentialsReadable = false; Log("本机凭据无法解密，已保留原文件；请先恢复凭据再保存供应商设置。"); }
        }
        bindingProviders = true;
        ProviderList.ItemsSource = settings.Models.Providers;
        ProviderList.SelectedIndex = 0;
        bindingProviders = false;
        ShowProvider(ProviderList.SelectedItem as ModelProvider);
        RefreshModelChoices();
    }

    private void CaptureProviderEditor()
    {
        if (editingProvider == null || bindingProviders) return;
        editingProvider.Name = ProviderName.Text.Trim();
        editingProvider.Endpoint = Endpoint.Text.Trim();
        editingProvider.Models = SavedModelNames.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        localCredentials["provider:" + editingProvider.Id] = ModelKey.Password;
    }

    private void ShowProvider(ModelProvider? provider)
    {
        editingProvider = provider;
        ProviderName.Text = provider?.Name ?? "";
        Endpoint.Text = provider?.Endpoint ?? "";
        SavedModelNames.Text = string.Join(Environment.NewLine, provider?.Models ?? []);
        ModelKey.Password = provider == null ? "" : localCredentials.GetValueOrDefault("provider:" + provider.Id, "");
    }

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (bindingProviders) return;
        CaptureProviderEditor();
        ShowProvider(ProviderList.SelectedItem as ModelProvider);
        RefreshModelChoices();
    }

    private void RefreshModelChoices()
    {
        bindingProviders = true;
        var choices = settings.Models.Providers.SelectMany(p => p.Models.Select(m => new ModelOption(p.Id, m, p.Name + " / " + m))).ToArray();
        if (choices.Length > 0)
        {
            if (string.IsNullOrWhiteSpace(settings.Models.Translation.Model)) settings.Models.Translation = new() { ProviderId = choices[0].ProviderId, Model = choices[0].Model };
            if (string.IsNullOrWhiteSpace(settings.Models.Review.Model)) settings.Models.Review = new() { ProviderId = choices[0].ProviderId, Model = choices[0].Model };
        }
        InitialModelChoice.ItemsSource = choices; ReviewModelChoice.ItemsSource = choices;
        InitialModelChoice.SelectedItem = choices.FirstOrDefault(x => x.ProviderId == settings.Models.Translation.ProviderId && x.Model == settings.Models.Translation.Model);
        ReviewModelChoice.SelectedItem = choices.FirstOrDefault(x => x.ProviderId == settings.Models.Review.ProviderId && x.Model == settings.Models.Review.Model);
        ProviderList.Items.Refresh();
        bindingProviders = false;
        UpdateModelSummary();
    }

    private void RoleModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (bindingProviders) return;
        if (InitialModelChoice.SelectedItem is ModelOption initial) settings.Models.Translation = new() { ProviderId = initial.ProviderId, Model = initial.Model };
        if (ReviewModelChoice.SelectedItem is ModelOption review) settings.Models.Review = new() { ProviderId = review.ProviderId, Model = review.Model };
        UpdateModelSummary();
    }

    private void UpdateModelSummary()
    {
        if (ModelRoleSummary == null) return;
        ModelRoleSummary.Text = "初翻：" + (InitialModelChoice.SelectedItem is ModelOption initial ? initial.Label : "未选择") +
            "   ·   核对：" + (ReviewModelChoice.SelectedItem is ModelOption review ? review.Label : "未选择");
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        CaptureProviderEditor();
        var provider = new ModelProvider { Name = "新供应商 " + (settings.Models.Providers.Count + 1) };
        settings.Models.Providers.Add(provider);
        bindingProviders = true; ProviderList.Items.Refresh(); ProviderList.SelectedItem = provider; bindingProviders = false;
        ShowProvider(provider); RefreshModelChoices();
    }

    private async void SaveProvider_Click(object sender, RoutedEventArgs e) => await Perform(() =>
    {
        PersistSettings();
        Log("供应商、模型列表及初翻/核对选择已保存。");
        return Task.CompletedTask;
    });

    private ModelSettings ReadLimits() => new() { BatchSize = int.Parse(BatchSize.Text), Concurrency = int.Parse(Concurrency.Text), MaxOutputTokens = int.Parse(MaxTokens.Text) };
    private ModelSettings ReadModel(bool review = false)
    {
        CaptureProviderEditor();
        return settings.Models.Resolve(review ? settings.Models.Review : settings.Models.Translation, ReadLimits());
    }
    private string ReadModelKey(bool review = false)
    {
        CaptureProviderEditor();
        return localCredentials.GetValueOrDefault("provider:" + (review ? settings.Models.Review : settings.Models.Translation).ProviderId, "");
    }
    private string ReviewProviderName() => settings.Models.Providers.Single(x => x.Id == settings.Models.Review.ProviderId).Name;

    private void PersistSettings()
    {
        if (!credentialsReadable) throw new InvalidOperationException("原凭据无法解密，未覆盖本机设置；请先恢复 credentials.bin。");
        CaptureProviderEditor(); RefreshModelChoices(); settings.Models.Validate();
        var model = ReadModel();
        ModelCompletionClient.Validate(model, ReadModelKey());
        ModelCompletionClient.Validate(ReadModel(true), ReadModelKey(true));
        settings.Workspace = WorkspacePath.Text.Trim(); settings.GameDirectory = GamePath.Text.Trim();
        settings.CaptureDirectory = CapturePath.Text.Trim(); settings.Repository = Repository.Text.Trim();
        settings.Model = model; settings.AutoImport = AutoImport.IsChecked == true; settings.AutoTranslate = AutoTranslate.IsChecked == true;
        localCredentials["github"] = GitHubToken.Password;
        // Retain the legacy credential for older local versions; provider-specific keys are authoritative here.
        localCredentials["model"] = ReadModelKey();
        LocalSecrets.Save(localCredentials); settings.Save();
    }

    private void VerifyProviderBindingsForSmoke()
    {
        // Exercise provider and role selection without reading or writing real credentials.
        var primary = settings.Models.Providers[0];
        localCredentials["provider:" + primary.Id] = "fixture-primary";
        ShowProvider(primary);
        var other = new ModelProvider { Id = "smoke-other", Name = "另一测试供应商", Endpoint = "https://other.invalid/v1/chat/completions", Models = ["claude-sonnet-5-5"] };
        settings.Models.Providers.Add(other); localCredentials["provider:" + other.Id] = "fixture-other";
        bindingProviders = true; ProviderList.Items.Refresh(); bindingProviders = false;
        ProviderList.SelectedItem = other;
        ReviewModelChoice.SelectedItem = ReviewModelChoice.Items.Cast<ModelOption>().Single(x => x.ProviderId == other.Id);
        if (ReadModel(true).Endpoint != other.Endpoint || ReadModelKey(true) != "fixture-other" || ReadModelKey() != "fixture-primary")
            throw new InvalidOperationException("供应商模型与凭据路由自检失败。");
        ProviderList.SelectedItem = primary;
        SavedModelNames.Text += "\nclaude-sonnet-5-5\nextra-fixture-model";
        CaptureProviderEditor(); RefreshModelChoices();
        if (primary.Models.Count != 3) throw new InvalidOperationException("供应商模型列表保存自检失败。");
        primary.Models.Remove("extra-fixture-model"); settings.Models.Providers.Remove(other);
        settings.Models.Review = new() { ProviderId = primary.Id, Model = "claude-sonnet-5-5" };
        localCredentials.Clear(); ShowProvider(primary); RefreshModelChoices();
    }
}
