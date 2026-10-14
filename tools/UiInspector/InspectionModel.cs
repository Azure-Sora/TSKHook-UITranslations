using System.Text.Encodings.Web;
using System.Text.Json;

namespace TSKHook.UiInspector;

public enum UiComponentKind { Other, Tmp, Text, TextMesh, Image, RawImage }

public static class ComponentClassifier
{
    // Inspect native ancestry before touching optional interop wrappers. A wrapper DLL can contain
    // TextMesh even when that class has been stripped from the actual IL2CPP player.
    public static UiComponentKind Classify(IEnumerable<string> ancestry)
    {
        foreach (var name in ancestry)
        {
            var kind = name switch
            {
                "TMPro.TMP_Text" => UiComponentKind.Tmp,
                "UnityEngine.UI.Text" => UiComponentKind.Text,
                "UnityEngine.TextMesh" => UiComponentKind.TextMesh,
                "UnityEngine.UI.Image" => UiComponentKind.Image,
                "UnityEngine.UI.RawImage" => UiComponentKind.RawImage,
                _ => UiComponentKind.Other
            };
            if (kind != UiComponentKind.Other) return kind;
        }
        return UiComponentKind.Other;
    }
}

public static class InspectionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };
}

public sealed class UiSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public string CapturedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string UnityVersion { get; set; }
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public bool IncludeInactive { get; set; }
    public bool Truncated { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<UiNode> Nodes { get; set; } = new();
}

public sealed class ScanOptions
{
    public string Canvas { get; set; }
    public bool? IncludeInactive { get; set; }
    public int? MaxNodes { get; set; }
}

// Flat nodes retain the hierarchy via parentPath; paths escape names and include sibling indices.
public sealed class UiNode
{
    public string Scene { get; set; }
    public string Path { get; set; }
    public string ParentPath { get; set; }
    public string Name { get; set; }
    public int InstanceId { get; set; }
    public bool ActiveSelf { get; set; }
    public bool ActiveInHierarchy { get; set; }
    public float[] ScreenBox { get; set; }
    public List<string> Components { get; set; } = new();
    public List<UiText> Texts { get; set; } = new();
    public List<UiImage> Images { get; set; } = new();
}

public sealed class UiText
{
    public string Component { get; set; }
    public int ComponentIndex { get; set; }
    public bool Enabled { get; set; }
    public string CurrentText { get; set; }
    public string Font { get; set; }
    public float FontSize { get; set; }
}

public sealed class UiImage
{
    public string Component { get; set; }
    public int ComponentIndex { get; set; }
    public bool Enabled { get; set; }
    public string Sprite { get; set; }
    public string Texture { get; set; }
    public int TextureWidth { get; set; }
    public int TextureHeight { get; set; }
    public float[] SpriteRect { get; set; }
    public float[] UvRect { get; set; }
}

public sealed class OverrideFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<TextOverride> Entries { get; set; } = new();
}

public sealed class TextOverride
{
    public string Scene { get; set; }
    public string Path { get; set; }
    public string Component { get; set; }
    public int ComponentIndex { get; set; }
    public string Source { get; set; }
    public string Translation { get; set; }
}

public static class OverrideRules
{
    public static string Segment(string name, int siblingIndex) => Uri.EscapeDataString(name) + "[" + siblingIndex + "]";

    public static OverrideFile Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("schemaVersion", out var version) ||
            !version.TryGetInt32(out var schema) || schema != 1)
            throw new InvalidDataException("Explicit schemaVersion 1 is required.");
        var file = JsonSerializer.Deserialize<OverrideFile>(json, InspectionJson.Options);
        if (file?.SchemaVersion != 1 || file.Entries == null || file.Entries.Count > 10000)
            throw new InvalidDataException("Expected schemaVersion 1 and at most 10000 entries.");
        var keys = new HashSet<(string, string, string, int, string)>();
        foreach (var row in file.Entries)
        {
            if (row == null || string.IsNullOrEmpty(row.Scene) || string.IsNullOrEmpty(row.Path) ||
                string.IsNullOrEmpty(row.Component) || row.ComponentIndex < 0 || string.IsNullOrEmpty(row.Source))
                throw new InvalidDataException("Every entry needs scene, path, component, componentIndex and source.");
            if (!keys.Add((row.Scene, row.Path, row.Component, row.ComponentIndex, row.Source)))
                throw new InvalidDataException("Duplicate override identity.");
        }
        return file;
    }

    public static bool Matches(TextOverride rule, UiNode node, UiText text) =>
        !string.IsNullOrWhiteSpace(rule.Translation) && rule.Scene == node.Scene && rule.Path == node.Path &&
        rule.Component == text.Component && rule.ComponentIndex == text.ComponentIndex && rule.Source == text.CurrentText;

    public static (string, string, string, int, string) Identity(TextOverride row) =>
        (row.Scene, row.Path, row.Component, row.ComponentIndex, row.Source);

    public static (string, string, string, int, string) Identity(UiNode node, UiText text) =>
        (node.Scene, node.Path, text.Component, text.ComponentIndex, text.CurrentText);

    public static OverrideFile Template(UiSnapshot snapshot) => new()
    {
        Entries = snapshot.Nodes.SelectMany(node => node.Texts
            .Where(text => !string.IsNullOrWhiteSpace(text.CurrentText))
            .Select(text => new TextOverride
            {
                Scene = node.Scene, Path = node.Path, Component = text.Component,
                ComponentIndex = text.ComponentIndex, Source = text.CurrentText, Translation = ""
            })).ToList()
    };
}
