using TSKHook;

namespace TSKHook.Workbench;

/// <summary>Small hand-maintained UI literals. Kept separate from network capture and workbench queues.</summary>
public static class StaticUiCatalog
{
    public const string RelativePath = "translations/ui/static-ui.jsonl";

    public static IReadOnlyList<StaticUiEntry> Read(string root)
    {
        var path = Path.Combine(root, RelativePath);
        if (!File.Exists(path)) return [];
        var rows = new List<StaticUiEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var row = JsonData.Read<StaticUiEntry>(line);
            if (row.SchemaVersion != 1 || string.IsNullOrWhiteSpace(row.Source) ||
                row.SourceHash != UiJsonTranslator.Sha256(row.Source) ||
                row.Status is not ("translated" or "needs_review" or "excluded") || !seen.Add(row.SourceHash))
                throw new InvalidDataException($"静态 UI 记录或原文哈希无效/重复，第 {lineNumber} 行。");
            if (row.Status != "translated") continue;
            if (string.IsNullOrWhiteSpace(row.Translation)) throw new InvalidDataException($"静态 UI 译文为空，第 {lineNumber} 行。");
            if (row.Template) _ = new UiTextFormatPattern(row.Source, row.Translation);
            rows.Add(row);
        }
        if (rows.Count(x => x.Template) > 64) throw new InvalidDataException("静态 UI 模板不能超过 64 条。");
        return rows;
    }
}

public sealed class StaticUiEntry
{
    public int SchemaVersion { get; set; }
    public string Source { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string Translation { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Template { get; set; }
    public string[] Origins { get; set; } = [];
    public string Key => "static-ui:" + (Template ? "template:" : "text:") + SourceHash;
    public TranslationEntry ForValidation() => new()
    {
        Key = Key, Category = "staticui", Field = "text", Source = Source, SourceHash = SourceHash,
        Translation = Translation, Status = Status, Origins = Origins
    };
    public RuntimeRow Runtime() => new()
    {
        Key = Key, SourceHash = SourceHash, Translation = Translation, RenderedOnly = true,
        SourceTemplate = Template ? Source : null
    };
}
