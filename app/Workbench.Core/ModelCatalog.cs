namespace TSKHook.Workbench;

public sealed class ModelProvider
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新供应商";
    public string Endpoint { get; set; } = "https://api.example.com/v1/chat/completions";
    public List<string> Models { get; set; } = [];
}

public sealed class ModelSelection
{
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class ModelCatalog
{
    public List<ModelProvider> Providers { get; set; } = [];
    public ModelSelection Translation { get; set; } = new();
    public ModelSelection Review { get; set; } = new();

    public void MigrateLegacy(ModelSettings legacy)
    {
        if (Providers.Count != 0) return;
        var provider = new ModelProvider { Id = "legacy", Name = "现有供应商", Endpoint = legacy.Endpoint,
            Models = string.IsNullOrWhiteSpace(legacy.Model) ? [] : [legacy.Model.Trim()] };
        Providers.Add(provider);
        Translation = new() { ProviderId = provider.Id, Model = legacy.Model.Trim() };
        Review = new() { ProviderId = provider.Id, Model = legacy.Model.Trim() };
    }

    public ModelSettings Resolve(ModelSelection selection, ModelSettings limits)
    {
        var provider = Providers.SingleOrDefault(x => x.Id == selection.ProviderId)
            ?? throw new InvalidOperationException("请先选择模型供应商。");
        if (string.IsNullOrWhiteSpace(selection.Model) || !provider.Models.Contains(selection.Model, StringComparer.Ordinal))
            throw new InvalidOperationException("请选择此供应商已保存的模型名称。");
        return new() { Endpoint = provider.Endpoint, Model = selection.Model, BatchSize = limits.BatchSize,
            Concurrency = limits.Concurrency, MaxOutputTokens = limits.MaxOutputTokens };
    }

    public void Validate()
    {
        if (Providers.Count == 0 || Providers.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != Providers.Count)
            throw new InvalidOperationException("供应商标识为空或重复。");
        foreach (var provider in Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Id) || string.IsNullOrWhiteSpace(provider.Name)) throw new InvalidOperationException("供应商名称不能为空。");
            ModelCompletionClient.ValidateEndpoint(provider.Endpoint);
            provider.Models = provider.Models.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (provider.Models.Count == 0) throw new InvalidOperationException("每个供应商至少保存一个模型名称。");
        }
        _ = Resolve(Translation, new()); _ = Resolve(Review, new());
    }
}
