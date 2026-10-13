using System.Security.Cryptography;
using System.Text;

namespace TSKHook.Workbench;

internal static class AtomicData
{
    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // A committed journal allows startup to finish a multi-file import after a crash.
    public static void WriteBatch(string root, IReadOnlyDictionary<string, string> files)
    {
        Recover(root);
        var directory = Path.Combine(root, ".local", "transactions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var journal = new List<JournalFile>();
        foreach (var (relative, text) in files)
        {
            _ = Target(root, relative);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            var staged = journal.Count.ToString() + ".data";
            File.WriteAllBytes(Path.Combine(directory, staged), bytes);
            journal.Add(new JournalFile(relative, staged, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        Write(Path.Combine(directory, "commit.json"), JsonData.Write(journal));
        Finish(root, directory, journal);
    }

    public static void Recover(string root)
    {
        var transactions = Path.Combine(root, ".local", "transactions");
        if (!Directory.Exists(transactions)) return;
        foreach (var directory in Directory.EnumerateDirectories(transactions))
        {
            var path = Path.Combine(directory, "commit.json");
            if (File.Exists(path)) Finish(root, directory, JsonData.Read<List<JournalFile>>(File.ReadAllText(path)));
        }
    }

    private static void Finish(string root, string directory, List<JournalFile> journal)
    {
        foreach (var file in journal)
        {
            if (Path.GetFileName(file.Staged) != file.Staged) throw new InvalidDataException("Invalid journal path.");
            var staged = Path.Combine(directory, file.Staged);
            var target = Target(root, file.Relative);
            var present = File.Exists(staged) ? staged : target;
            using var stream = File.OpenRead(present);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (hash != file.Hash) throw new InvalidDataException("归档事务文件损坏，请恢复备份后重试。");
            stream.Dispose();
            if (!File.Exists(staged)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(staged, target, true);
        }
        Directory.Delete(directory, true);
    }

    private static string Target(string root, string relative)
    {
        var absolute = Path.GetFullPath(Path.Combine(root, relative));
        if (!absolute.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("归档路径超出工作目录。");
        return absolute;
    }
    private sealed record JournalFile(string Relative, string Staged, string Hash);
}
