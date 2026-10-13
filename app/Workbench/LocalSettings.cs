using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TSKHook.Workbench.Desktop;

public sealed class LocalSettings
{
    public string Workspace { get; set; } = "";
    public string CaptureDirectory { get; set; } = "";
    public string GameDirectory { get; set; } = "";
    public string Repository { get; set; } = "Azure-Sora/TSKHook-UITranslations";
    public ModelSettings Model { get; set; } = new();
    public ModelCatalog Models { get; set; } = new();
    public bool AutoImport { get; set; } = true;
    public bool AutoTranslate { get; set; }
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TSKHook-UITranslations");
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public static LocalSettings Load()
    {
        var settings = File.Exists(FilePath) ? JsonData.Read<LocalSettings>(File.ReadAllText(FilePath)) : new();
        settings.Models.MigrateLegacy(settings.Model);
        return settings;
    }
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonData.Write(this));
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true);
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

// Windows DPAPI encrypts saved credentials for the current Windows account.
internal static class LocalSecrets
{
    public static void SelfTest()
    {
        const string expected = "fixture credential: 本地加密";
        var encrypted = Transform(Encoding.UTF8.GetBytes(expected), true);
        if (Encoding.UTF8.GetString(Transform(encrypted, false)) != expected) throw new InvalidOperationException("DPAPI roundtrip failed.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    public static Dictionary<string, string> Load()
    {
        var path = Path.Combine(LocalSettings.DirectoryPath, "credentials.bin");
        return File.Exists(path) ? JsonData.Read<Dictionary<string, string>>(Encoding.UTF8.GetString(Transform(File.ReadAllBytes(path), false))) : [];
    }
    public static void Save(string model, string github)
        => Save(new Dictionary<string, string> { ["model"] = model, ["github"] = github });

    public static void Save(Dictionary<string, string> secrets)
    {
        Directory.CreateDirectory(LocalSettings.DirectoryPath);
        var bytes = Transform(Encoding.UTF8.GetBytes(JsonData.Write(secrets)), true);
        var path = Path.Combine(LocalSettings.DirectoryPath, "credentials.bin");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static byte[] Transform(byte[] data, bool encrypt)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = encrypt ? CryptProtectData(ref input, "TSKHook translation credentials", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); Array.Clear(data); }
    }
}
