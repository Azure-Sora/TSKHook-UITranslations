using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    return RunSelfTest();
}

var masterPath = Path.GetFullPath(args.ElementAtOrDefault(0) ??
                                  "translations/ui/source/ui_source.jsonl");
var skillPath = Path.GetFullPath(args.ElementAtOrDefault(1) ??
                                 "translations/ui/source/by_category/skill.jsonl");
if (!File.Exists(masterPath) || !File.Exists(skillPath))
{
    Console.Error.WriteLine($"Input not found: master={masterPath}; skill={skillPath}");
    return 2;
}

var result = Generate(masterPath, skillPath);
Console.WriteLine($"Eligible colored translations: {result.Eligible}");
Console.WriteLine($"Existing plain sources: {result.Existing}");
Console.WriteLine($"Ambiguous plain translations: {result.Ambiguous}");
Console.WriteLine($"Generated plain translations: {result.Generated}");
return result.Ambiguous == 0 ? 0 : 1;

static GenerationResult Generate(string masterPath, string skillPath)
{
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
    var master = ReadRows(masterPath, options);
    var skills = ReadRows(skillPath, options);
    var allSources = new HashSet<string>(master.Select(row => row.Source), StringComparer.Ordinal);
    var eligible = skills.Where(IsEligible).ToList();
    var generated = new List<SourceEntry>();
    var existing = 0;
    var ambiguous = 0;

    foreach (var group in eligible.GroupBy(row => StripColor(row.Source), StringComparer.Ordinal)
                 .OrderBy(group => group.Key, StringComparer.Ordinal))
    {
        if (allSources.Contains(group.Key))
        {
            existing++;
            continue;
        }

        var translations = group.Select(row => StripColor(row.Translation))
            .Distinct(StringComparer.Ordinal).ToList();
        if (translations.Count != 1)
        {
            ambiguous++;
            continue;
        }

        var sourceRow = group.OrderBy(row => row.Key, StringComparer.Ordinal).First();
        var sourceHash = Sha256(group.Key);
        var field = string.IsNullOrEmpty(sourceRow.Field) ? "skill_detail" : sourceRow.Field;
        generated.Add(new SourceEntry
        {
            SchemaVersion = 1,
            Key = $"skill:generated_plain:source_hash={sourceHash}:{field}",
            Category = "skill",
            Field = field,
            Identity = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["generated_plain"] = "true",
                ["source_hash"] = sourceHash
            },
            Source = group.Key,
            SourceHash = sourceHash,
            Translation = translations[0],
            Status = "translated",
            Occurrences = 0,
            Origins = group.Select(row => "generated:plain-from-color:" + row.Key)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            FirstSeenUtc = sourceRow.FirstSeenUtc
        });
        allSources.Add(group.Key);
    }

    if (generated.Count > 0)
    {
        master.AddRange(generated);
        skills.AddRange(generated);
        WriteRows(masterPath, master.OrderBy(row => row.Category, StringComparer.Ordinal)
            .ThenBy(row => row.Key, StringComparer.Ordinal), options);
        WriteRows(skillPath, skills.OrderBy(row => row.Key, StringComparer.Ordinal), options);
    }

    return new GenerationResult(eligible.Count, existing, ambiguous, generated.Count);
}

static bool IsEligible(SourceEntry row)
{
    if (string.IsNullOrWhiteSpace(row.Source) || string.IsNullOrWhiteSpace(row.Translation) ||
        row.Source.IndexOf("<color", StringComparison.OrdinalIgnoreCase) < 0 ||
        row.Status is not ("translated" or "reviewed" or "verified"))
    {
        return false;
    }

    return ColorTags(row.Source).SequenceEqual(ColorTags(row.Translation), StringComparer.Ordinal);
}

static string StripColor(string value) => Regex.Replace(value,
    "</?color(?:=[^>]+)?>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

static IEnumerable<string> ColorTags(string value) => Regex.Matches(value,
        "</?color(?:=[^>]+)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    .Select(match => match.Value);

static string Sha256(string value) => Convert.ToHexString(
    SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

static List<SourceEntry> ReadRows(string path, JsonSerializerOptions options)
{
    var rows = new List<SourceEntry>();
    var lineNumber = 0;
    foreach (var line in File.ReadLines(path))
    {
        lineNumber++;
        try
        {
            var row = JsonSerializer.Deserialize<SourceEntry>(line, options);
            if (row == null || string.IsNullOrEmpty(row.Key) || string.IsNullOrEmpty(row.Source))
            {
                throw new InvalidDataException($"Missing key/source in {path}:{lineNumber}");
            }
            rows.Add(row);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid JSON in {path}:{lineNumber}", exception);
        }
    }
    return rows;
}

static void WriteRows(string path, IEnumerable<SourceEntry> rows, JsonSerializerOptions options)
{
    var tempPath = path + ".tmp";
    using (var writer = new StreamWriter(tempPath, false, new UTF8Encoding(false)))
    {
        foreach (var row in rows)
        {
            writer.WriteLine(JsonSerializer.Serialize(row, options));
        }
    }
    File.Move(tempPath, path, true);
}

static int RunSelfTest()
{
    var temp = Path.Combine(Path.GetTempPath(), "tsk-generate-plain-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var colored = new SourceEntry
        {
            SchemaVersion = 1, Key = "skill:test", Category = "skill", Field = "skill_detail",
            Source = "造成<color=#16C97B>120</color>%伤害", SourceHash = "old",
            Translation = "造成<color=#16C97B>120</color>%伤害", Status = "translated",
            Origins = Array.Empty<string>()
        };
        var masterPath = Path.Combine(temp, "ui_source.jsonl");
        var skillPath = Path.Combine(temp, "skill.jsonl");
        WriteRows(masterPath, new[] { colored }, options);
        WriteRows(skillPath, new[] { colored }, options);
        var first = Generate(masterPath, skillPath);
        var second = Generate(masterPath, skillPath);
        var rows = ReadRows(skillPath, options);
        var plain = rows.SingleOrDefault(row => row.Source == "造成120%伤害");
        var passed = first.Generated == 1 && first.Ambiguous == 0 && second.Generated == 0 &&
                     plain?.Translation == "造成120%伤害" && plain.Status == "translated" &&
                     plain.SourceHash == Sha256(plain.Source);
        Console.WriteLine(passed
            ? "PASS: deterministic color stripping and idempotent plain-text generation"
            : "FAIL: plain-text generation behavior");
        return passed ? 0 : 1;
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

internal sealed record GenerationResult(int Eligible, int Existing, int Ambiguous, int Generated);

internal sealed class SourceEntry
{
    public int SchemaVersion { get; set; }
    public string Key { get; set; }
    public string Category { get; set; }
    public string Field { get; set; }
    public SortedDictionary<string, string> Identity { get; set; }
    public string Source { get; set; }
    public string SourceHash { get; set; }
    public string PreviousSourceHash { get; set; }
    public string Translation { get; set; }
    public string Status { get; set; }
    public int Occurrences { get; set; }
    public string[] Origins { get; set; }
    public string FirstSeenUtc { get; set; }
}
