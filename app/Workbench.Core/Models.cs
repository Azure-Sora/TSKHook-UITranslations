using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSKHook.Workbench;

public sealed class TranslationEntry
{
    public int SchemaVersion { get; set; } = 1;
    public string Key { get; set; } = "";
    public string Category { get; set; } = "misc";
    public string Field { get; set; } = "";
    public SortedDictionary<string, string> Identity { get; set; } = new(StringComparer.Ordinal);
    public string Source { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string? PreviousSourceHash { get; set; }
    public string Translation { get; set; } = "";
    public string Status { get; set; } = "new";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CaptureConflictExcluded { get; set; }
    public ModelReviewEvidence? ModelReview { get; set; }
    public int Occurrences { get; set; }
    public string[] Origins { get; set; } = [];
    public string? FirstSeenUtc { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    public TranslationEntry Copy() => (TranslationEntry)MemberwiseClone();
    [JsonIgnore] public bool Protected => Origins.Any(origin =>
    {
        var separator = origin.IndexOf(':');
        return separator > 0 && TranslationProtection.IsProtectedPath(origin[..separator], origin[(separator + 1)..]);
    });
}

public sealed class CaptureRecord
{
    public int SchemaVersion { get; set; }
    public string ApiName { get; set; } = "";
    public string Path { get; set; } = "";
    public Dictionary<string, string> Context { get; set; } = [];
    public string Source { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string? FirstSeenUtc { get; set; }
}

public sealed record ValidationIssue(string Key, string Kind, string Message, bool Fatal);
public sealed record ImportResult(int Added, int Changed, int Reused, int Conflicts, int Read);
public sealed record CompileResult(string AssetPath, int Entries, IReadOnlyList<ValidationIssue> Issues);
public sealed record PackageResult(string Directory, TranslationManifest Manifest, CompileResult Compilation);

public sealed class ModelSettings
{
    public string Endpoint { get; set; } = "https://api.deepseek.com/v1/chat/completions";
    public string Model { get; set; } = "";
    public int BatchSize { get; set; } = 8;
    public int Concurrency { get; set; } = 2;
    public int MaxOutputTokens { get; set; } = 8192;
}

public static class JsonData
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    public static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options)
        ?? throw new InvalidDataException("JSON 内容为空。");
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
