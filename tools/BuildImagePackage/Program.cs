using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TSKHook;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: BuildImagePackage <workspace> [owner/repo] (local packaging only)");
    return 2;
}
var root = Path.GetFullPath(args[0]);
var repository = args.ElementAtOrDefault(1) ?? "Azure-Sora/TSKHook-UITranslations";
if (!System.Text.RegularExpressions.Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new ArgumentException("Invalid repository.");
var source = Path.Combine(root, "translations/images/ui_images");
var manifest = UiImageManifest.Read(source);
if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Version, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")) throw new ArgumentException("Invalid version.");
var files = manifest.Sprites.Select(x => x.File).Concat(manifest.Spine.SelectMany(x => x.Regions.Select(r => r.File)))
    .Append("manifest.json").Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
var directory = Path.Combine(root, ".local/image-packages", manifest.Version);
Directory.CreateDirectory(directory);
var archive = Path.Combine(directory, "ui_images.zip");
var temporary = archive + "." + Guid.NewGuid().ToString("N") + ".tmp";
try
{
    using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
        foreach (var name in files)
        {
            if (Path.GetFileName(name) != name) throw new InvalidDataException("Image packages require flat names.");
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var input = File.OpenRead(Path.Combine(source, name));
            using var output = entry.Open(); input.CopyTo(output);
        }
    File.Move(temporary, archive, true);
}
finally { if (File.Exists(temporary)) File.Delete(temporary); }
using var stream = File.OpenRead(archive);
using var sha = SHA256.Create();
var update = new ImagePackageManifest { Version = manifest.Version, Size = stream.Length,
    Sha256 = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant(),
    DownloadUrl = $"https://github.com/{repository}/releases/download/images-{manifest.Version}/ui_images.zip" };
update.Validate("1.6.0");
ImagePackageStore.VerifyArchive(archive, update);
var verification = Path.Combine(directory, Guid.NewGuid().ToString("N"));
try { ImagePackageStore.Extract(archive, verification); ImagePackageStore.VerifyDirectory(verification, update); }
finally { if (Directory.Exists(verification)) Directory.Delete(verification, true); }
File.WriteAllText(Path.Combine(directory, "image-manifest.json"), JsonSerializer.Serialize(update, RuntimeAsset.JsonOptions));
Console.WriteLine($"Image package {update.Version}: {files.Length} files, {update.Size} bytes, SHA-256 {update.Sha256}\n{directory}");
return 0;
