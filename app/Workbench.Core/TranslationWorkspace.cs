using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TSKHook.Workbench;

public sealed class TranslationWorkspace
{
    private readonly object gate = new();
    private Dictionary<string, TranslationEntry> entries = new(StringComparer.Ordinal);
    private Dictionary<string, (long Length, DateTime Time)> fingerprints = [];
    public string Root { get; }
    public string SourcePath => Path.Combine(Root, "translations/ui/source/ui_source.jsonl");
    public string CategoryDirectory => Path.Combine(Root, "translations/ui/source/by_category");
    public TranslationWorkspace(string root)
    {
        Root = Path.GetFullPath(root);
        Reload();
    }

    public void Reload()
    {
        lock (gate)
        {
            AtomicData.Recover(Root);
            var loaded = new Dictionary<string, TranslationEntry>(StringComparer.Ordinal);
            if (File.Exists(SourcePath))
                foreach (var row in ReadRows(SourcePath))
                    if (!loaded.TryAdd(row.Key, row)) throw new InvalidDataException("源文件存在重复 key：" + row.Key);
            var seen = new HashSet<string>();
            if (Directory.Exists(CategoryDirectory))
                foreach (var file in Directory.EnumerateFiles(CategoryDirectory, "*.jsonl").Order(StringComparer.Ordinal))
                    foreach (var row in ReadRows(file))
                    {
                        if (!seen.Add(row.Key)) throw new InvalidDataException("分类文件存在重复 key：" + row.Key);
                        if (!loaded.TryGetValue(row.Key, out var master) || master.SourceHash != row.SourceHash || master.Source != row.Source)
                            throw new InvalidDataException("分类文件与源文件不一致：" + row.Key);
                        if (row.Category != master.Category || Path.GetFileNameWithoutExtension(file) != row.Category)
                            throw new InvalidDataException("分类归属与源文件不一致：" + row.Key);
                        master.Translation = row.Translation;
                        master.Status = row.Status;
                    }
            if (loaded.Count != seen.Count) throw new InvalidDataException("分类译文文件不完整；请恢复缺失条目后再加载，避免使用旧主文件覆盖人工译文。");
            entries = loaded;
            RememberFiles();
        }
    }

    public List<TranslationEntry> Snapshot()
    {
        lock (gate) return entries.Values.Select(x => x.Copy()).ToList();
    }

    public void Edit(string key, string sourceHash, string translation, string status)
    {
        if (!new[] { "new", "translated", "reviewed", "verified", "needs_review", "excluded" }.Contains(status))
            throw new ArgumentException("不支持的审校状态。");
        lock (gate)
        {
            EnsureUnchanged();
            var row = entries[key];
            if (row.SourceHash != sourceHash) throw new InvalidOperationException("原文已变化，请刷新后再编辑。");
            if (row.Protected && translation != row.Source && !string.IsNullOrEmpty(translation))
                throw new InvalidOperationException("受保护字段只能保留原文。");
            var replacement = row.Copy(); replacement.Translation = translation; replacement.Status = status;
            if (status is "reviewed" or "verified" && ValidationRules.Check(replacement).Any(x => x.Fatal))
                throw new InvalidDataException("标签或占位符校验失败，不能标记为已审校。");
            SaveReplacements([replacement]);
        }
    }

    public int ApplyMachineResults(IReadOnlyList<TranslationEntry> originals, IReadOnlyDictionary<string, string> translated)
    {
        lock (gate)
        {
            EnsureUnchanged();
            var changes = new List<TranslationEntry>();
            foreach (var original in originals)
            {
                if (!entries.TryGetValue(original.Key, out var current) || current.SourceHash != original.SourceHash ||
                    current.Status != original.Status || current.Translation != original.Translation || current.Protected ||
                    current.Status is "reviewed" or "verified" || !translated.TryGetValue(current.Key, out var text)) continue;
                var next = current.Copy(); next.Translation = text;
                next.Status = ValidationRules.Check(next).Count == 0 ? "translated" : "needs_review";
                changes.Add(next);
            }
            SaveReplacements(changes);
            return changes.Count;
        }
    }

    public ImportResult ImportCapture(string path)
    {
        lock (gate)
        {
            EnsureUnchanged();
            path = Path.GetFullPath(path);
            var cursorPath = Path.Combine(Root, ".local/imports.json");
            var cursors = File.Exists(cursorPath) ? JsonData.Read<Dictionary<string, CaptureCursor>>(File.ReadAllText(cursorPath)) : [];
            cursors.TryGetValue(path, out var cursor);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            cursor ??= new CaptureCursor();
            if (stream.Length < cursor.Offset || (cursor.PrefixLength > 0 && PrefixHash(stream, cursor.PrefixLength) != cursor.PrefixHash)) cursor = new();
            if (stream.Length == cursor.Offset) return new(0, 0, 0, 0, 0);
            stream.Position = cursor.Offset;
            // Process bounded chunks; uncompleted trailing records remain for the next scan.
            var data = new byte[(int)Math.Min(stream.Length - cursor.Offset, 32 * 1024 * 1024)];
            var length = stream.ReadAtLeast(data, data.Length, false);
            var lastNewline = Array.LastIndexOf(data, (byte)'\n', length - 1, length);
            if (lastNewline < 0)
            {
                if (length == data.Length && data.Length == 32 * 1024 * 1024) throw new InvalidDataException("单条捕获超过 32 MiB。");
                return new(0, 0, 0, 0, 0);
            }
            var captured = new List<(CaptureRecord Row, UiTextKeyDescriptor Key)>();
            foreach (var line in Encoding.UTF8.GetString(data, 0, lastNewline + 1).TrimStart('\ufeff').Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var row = JsonData.Read<CaptureRecord>(line);
                if (row.SchemaVersion != 2 || string.IsNullOrWhiteSpace(row.ApiName) || string.IsNullOrWhiteSpace(row.Path) ||
                    !RuntimeAsset.IsSha256(row.SourceHash) || UiJsonTranslator.Sha256(row.Source) != row.SourceHash)
                    throw new InvalidDataException("捕获记录版本或原文哈希无效。");
                if (!CaptureTextFilter.ShouldCapture(row.Path, row.Source)) continue;
                captured.Add((row, UiTextKeyBuilder.Build(row.ApiName, row.Path, row.Source, row.SourceHash, row.Context)));
            }
            var working = entries.ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.Ordinal);
            var reusable = entries.Values.Where(x => x.Status is "reviewed" or "verified")
                .Where(x => UiJsonTranslator.Sha256(x.Source) == x.SourceHash)
                .GroupBy(x => x.Category + "\0" + x.Field + "\0" + x.SourceHash)
                .Where(g => g.Select(x => x.Translation).Distinct().Count() == 1 && !string.IsNullOrWhiteSpace(g.First().Translation))
                .ToDictionary(g => g.Key, g => g.First().Translation);
            int added = 0, changed = 0, reused = 0, conflicts = 0;
            foreach (var group in captured.GroupBy(x => x.Key.Key))
            {
                var variants = group.GroupBy(x => x.Row.SourceHash).ToArray();
                if (variants.Length > 1 && working.TryGetValue(group.Key, out var existing)) existing.Status = "conflict";
                foreach (var variant in variants)
                {
                    var (row, key) = variant.First();
                    var identity = variants.Length > 1 ? key.Key + "#" + row.SourceHash[..8] : key.Key;
                    if (working.TryGetValue(identity, out var old) && old.SourceHash == row.SourceHash)
                    {
                        // Recapture repairs historical display corruption without changing the runtime identity.
                        if (old.Source != row.Source) { old.Source = row.Source; changed++; }
                        continue;
                    }
                    var next = new TranslationEntry { Key = identity, Category = key.Category, Field = key.Field, Identity = key.Identity,
                        Source = row.Source, SourceHash = row.SourceHash, FirstSeenUtc = row.FirstSeenUtc, Occurrences = variant.Count(),
                        Origins = variant.Select(x => x.Row.ApiName + ":" + x.Row.Path).Distinct().Order(StringComparer.Ordinal).ToArray() };
                    if (old != null)
                    { next.Translation = old.Translation; next.PreviousSourceHash = old.SourceHash; next.Status = "stale"; changed++; }
                    else added++;
                    if (variants.Length > 1) { next.Status = "conflict"; conflicts++; }
                    else if (next.Protected) next.Status = "excluded";
                    else if (old == null && reusable.TryGetValue(next.Category + "\0" + next.Field + "\0" + next.SourceHash, out var reusedText))
                    { next.Translation = reusedText; next.Status = "translated"; reused++; }
                    working[identity] = next;
                }
            }
            cursor.Offset += lastNewline + 1;
            cursor.PrefixLength = (int)Math.Min(stream.Length, 256);
            cursor.PrefixHash = PrefixHash(stream, cursor.PrefixLength);
            cursors[path] = cursor;
            var files = new Dictionary<string, string> { [".local/imports.json"] = JsonData.Write(cursors) };
            if (added + changed + conflicts > 0)
            {
                files["translations/ui/source/ui_source.jsonl"] = Lines(working.Values.OrderBy(x => x.Category).ThenBy(x => x.Key, StringComparer.Ordinal));
                foreach (var category in working.Values.GroupBy(x => x.Category))
                    files["translations/ui/source/by_category/" + category.Key + ".jsonl"] = Lines(category.OrderBy(x => x.Key, StringComparer.Ordinal));
                files["translations/ui/source/ui_conflicts.jsonl"] = Lines(working.Values.Where(x => x.Status == "conflict").OrderBy(x => x.Key));
            }
            AtomicData.WriteBatch(Root, files);
            entries = working;
            RememberFiles();
            return new(added, changed, reused, conflicts, captured.Count);
        }
    }

    public CompileResult Compile(string path)
    {
        lock (gate)
        {
            EnsureUnchanged();
            var eligible = entries.Values.Where(x => x.Status is "translated" or "reviewed" or "verified")
                .Where(x => !string.IsNullOrWhiteSpace(x.Translation)).OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
            var issues = eligible.SelectMany(ValidationRules.Check).ToList();
            if (issues.Any(x => x.Fatal)) throw new InvalidDataException($"编译被 {issues.Count(x => x.Fatal)} 个结构错误阻止：{issues.First(x => x.Fatal).Key}");
            if (eligible.Length == 0) throw new InvalidDataException("没有可发布的译文。");
            var text = string.Join(Environment.NewLine, eligible.Select(x => JsonData.Write(new RuntimeRow
            { Key = x.Key, SourceHash = x.SourceHash, Translation = x.Translation }))) + Environment.NewLine;
            AtomicData.Write(path, text);
            RuntimeAsset.Load(path);
            AtomicData.Write(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "validation.json"), JsonData.Write(issues));
            return new(path, eligible.Length, issues);
        }
    }

    public PackageResult BuildPackage(string version, string repository)
    {
        if (!Regex.IsMatch(version, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$") || !Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new ArgumentException("版本或 GitHub 仓库名无效。");
        var directory = Path.Combine(Root, ".local/packages", version);
        var result = Compile(Path.Combine(directory, "ui_translations.jsonl"));
        using var stream = File.OpenRead(result.AssetPath);
        var manifest = new TranslationManifest { Version = version, Size = stream.Length, Entries = result.Entries,
            Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            DownloadUrl = $"https://github.com/{repository}/releases/download/translations-{version}/ui_translations.jsonl" };
        AtomicData.Write(Path.Combine(directory, "manifest.json"), JsonData.Write(manifest));
        return new(directory, manifest, result);
    }

    private void SaveReplacements(List<TranslationEntry> changes)
    {
        if (changes.Count == 0) return;
        var replacement = changes.ToDictionary(x => x.Key);
        var files = new Dictionary<string, string>();
        foreach (var category in changes.Select(x => x.Category).Distinct())
            files["translations/ui/source/by_category/" + category + ".jsonl"] = Lines(entries.Values.Where(x => x.Category == category)
                .Select(x => replacement.GetValueOrDefault(x.Key, x)).OrderBy(x => x.Key, StringComparer.Ordinal));
        AtomicData.WriteBatch(Root, files);
        foreach (var row in changes) entries[row.Key] = row;
        RememberFiles();
    }
    private static IEnumerable<TranslationEntry> ReadRows(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var row = JsonData.Read<TranslationEntry>(line);
            // Seventeen legacy records have corrupted source display text but valid historical
            // runtime hashes. Preserve those identifiers and expose a review warning; never rehash them.
            if (string.IsNullOrWhiteSpace(row.Key) || !RuntimeAsset.IsSha256(row.SourceHash) || !Regex.IsMatch(row.Category, "^[a-z]+$"))
                throw new InvalidDataException("无效的翻译记录：" + row.Key);
            yield return row;
        }
    }
    private IEnumerable<string> DataFiles() => (File.Exists(SourcePath) ? new[] { SourcePath } : [])
        .Concat(Directory.Exists(CategoryDirectory) ? Directory.EnumerateFiles(CategoryDirectory, "*.jsonl") : []);
    private void RememberFiles() => fingerprints = DataFiles().ToDictionary(x => x, x => { var f = new FileInfo(x); return (f.Length, f.LastWriteTimeUtc); });
    private void EnsureUnchanged()
    {
        var current = DataFiles().ToArray();
        if (current.Length != fingerprints.Count || current.Any(x => !fingerprints.TryGetValue(x, out var old) ||
            new FileInfo(x).Length != old.Length || File.GetLastWriteTimeUtc(x) != old.Time))
            throw new InvalidOperationException("工作文件已被其他程序修改，请先重新加载工作目录。");
    }
    private static string Lines(IEnumerable<TranslationEntry> rows) => string.Join(Environment.NewLine, rows.Select(JsonData.Write)) + Environment.NewLine;
    private static string PrefixHash(Stream stream, int count)
    { stream.Position = 0; var bytes = new byte[count]; var read = stream.ReadAtLeast(bytes, count, false); return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, read))); }
    private sealed class CaptureCursor { public long Offset { get; set; } public int PrefixLength { get; set; } public string PrefixHash { get; set; } = ""; }
}
