using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using Il2CppInterop.Runtime.Attributes;

namespace TSKHook.UiInspector;

[BepInPlugin("local.tskhook.uiinspector", "TSKHook UI Inspector", "0.1.7")]
public sealed class InspectorPlugin : BasePlugin
{
    internal static InspectorPlugin Instance;
    internal ConfigEntry<bool> IncludeInactive;
    internal ConfigEntry<int> MaxNodes;
    internal ConfigEntry<string> CanvasFilter;
    internal ConfigEntry<InspectorAction> Action;
    internal ConfigEntry<bool> BridgeEnabled;
    internal ConfigEntry<int> Port;

    public override void Load()
    {
        Instance = this;
        IncludeInactive = Config.Bind("Dump", "IncludeInactive", false, "包含已加载场景中的未激活 UI；不是可见性判断。");
        MaxNodes = Config.Bind("Dump", "MaxNodes", 10000, new ConfigDescription("单次遍历节点上限。", new AcceptableValueRange<int>(100, 50000)));
        CanvasFilter = Config.Bind("Dump", "CanvasName", "", "空值导出所有根 Canvas；否则只遍历名称完全相等的 Canvas。");
        BridgeEnabled = Config.Bind("Bridge", "Enabled", true, "启用仅本机访问的命令桥；修改后重启游戏。");
        Port = Config.Bind("Bridge", "Port", 27125, new ConfigDescription("本机命令端口；修改后重启游戏。", new AcceptableValueRange<int>(1024, 65535)));
        Action = Config.Bind("Commands", "Action", InspectorAction.None, "执行一次后复位；Dump 导出，ApplyPreview 重读并预览，RestorePreview 恢复未被游戏更新的文本。");
        Action.Value = InspectorAction.None;
        AddComponent<InspectorBehaviour>();
        Log.LogInfo("F9: dump UI; Ctrl+F9: apply local preview; Shift+F9: restore preview. No automatic replacement.");
    }
}

public enum InspectorAction { None, Dump, ApplyPreview, RestorePreview }

public sealed partial class InspectorBehaviour : MonoBehaviour
{
    private readonly List<AppliedText> applied = new();
    internal static string Root => Path.Combine(Paths.PluginPath, "TSKHook", "ui_inspector");

    private void Update()
    {
        PumpCapture();
        PumpClick();
        commands.Pump(Dispatch);
        var plugin = InspectorPlugin.Instance;
        var action = plugin.Action.Value;
        if (action != InspectorAction.None) plugin.Action.Value = InspectorAction.None;
        if (Input.GetKeyDown(KeyCode.F9))
        {
            action = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)
                ? InspectorAction.ApplyPreview
                : Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
                    ? InspectorAction.RestorePreview : InspectorAction.Dump;
        }
        if (action == InspectorAction.None) return;
        try
        {
            Directory.CreateDirectory(Root);
            switch (action)
            {
                case InspectorAction.Dump: Dump(); break;
                case InspectorAction.ApplyPreview: ApplyPreview(); break;
                case InspectorAction.RestorePreview: Restore(); break;
            }
        }
        catch (Exception e) { plugin.Log.LogError("[UI Inspector] " + e); }
    }

    [HideFromIl2Cpp]
    private object Dump(UiSnapshot snapshot = null)
    {
        snapshot ??= UiScanner.Scan(InspectorPlugin.Instance);
        var stem = Path.Combine(Root, "ui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Write(stem + ".json", snapshot);
        Write(stem + ".overrides.template.json", OverrideRules.Template(snapshot));
        InspectorPlugin.Instance.Log.LogInfo($"[UI Inspector] Dump: {stem}.json; nodes={snapshot.Nodes.Count}, texts={snapshot.Nodes.Sum(n => n.Texts.Count)}, images={snapshot.Nodes.Sum(n => n.Images.Count)}, truncated={snapshot.Truncated}, errors={snapshot.Errors.Count}. Current text may already be translated by other plugins.");
        return new { dump = stem + ".json", template = stem + ".overrides.template.json", nodes = snapshot.Nodes.Count, truncated = snapshot.Truncated, errors = snapshot.Errors };
    }

    [HideFromIl2Cpp]
    private object ApplyPreview(string json = null, ScanOptions options = null)
    {
        var path = Path.Combine(Root, "overrides.json");
        if (json == null && new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("overrides.json exceeds 4 MiB.");
        // Validate before restoring or mutating anything. Empty translations in templates are ignored.
        var rules = OverrideRules.Parse(json ?? File.ReadAllText(path));
        var lookup = rules.Entries.Where(row => !string.IsNullOrWhiteSpace(row.Translation))
            .ToDictionary(OverrideRules.Identity);
        // Scan-option validation and rule parsing must finish before changing existing previews.
        if (options?.MaxNodes is < 100 or > 50000) throw new InvalidDataException("maxNodes must be within 100..50000.");
        Restore();
        var snapshot = UiScanner.Scan(InspectorPlugin.Instance, out var targets, options);
        var changed = 0;
        var matchedRules = new HashSet<TextOverride>();
        foreach (var target in targets)
        {
            if (!lookup.TryGetValue(OverrideRules.Identity(target.Node, target.Text), out var rule) ||
                !OverrideRules.Matches(rule, target.Node, target.Text)) continue;
            matchedRules.Add(rule);
            if (rule.Translation == rule.Source) continue;
            try
            {
                // A scan is not a guarantee the component is still alive or still has that value.
                if (!target.Alive() || target.Read() != rule.Source) continue;
                target.Write(rule.Translation);
                var observed = target.Read();
                if (observed == rule.Translation)
                {
                    applied.Add(new AppliedText(target, rule.Source, observed));
                    changed++;
                }
                else InspectorPlugin.Instance.Log.LogWarning("[UI Inspector] Another writer changed preview text at " + target.Node.Path);
            }
            catch (Exception e) { snapshot.Errors.Add(target.Node.Path + ": " + e.Message); }
        }
        InspectorPlugin.Instance.Log.LogInfo($"[UI Inspector] Preview: {changed} verified writes, {rules.Entries.Count(r => !string.IsNullOrWhiteSpace(r.Translation)) - matchedRules.Count} unmatched rules, truncated={snapshot.Truncated}, errors={snapshot.Errors.Count}. Reapply after reopening a page; fonts are unchanged.");
        return new { verifiedWrites = changed, unmatchedRules = lookup.Count - matchedRules.Count, truncated = snapshot.Truncated, errors = snapshot.Errors };
    }

    [HideFromIl2Cpp]
    private object Restore()
    {
        var restored = 0;
        var skipped = 0;
        foreach (var entry in applied)
        {
            try
            {
                if (entry.Target.Alive() && entry.Target.Read() == entry.Applied)
                {
                    entry.Target.Write(entry.Original);
                    if (entry.Target.Read() == entry.Original) restored++; else skipped++;
                }
                else skipped++;
            }
            catch { skipped++; }
        }
        applied.Clear();
        InspectorPlugin.Instance.Log.LogInfo($"[UI Inspector] Restore: {restored} verified, {skipped} skipped/changed (other translation hooks may still apply).");
        return new { verifiedRestores = restored, skipped };
    }

    private static void Write<T>(string path, T value) => File.WriteAllText(path,
        System.Text.Json.JsonSerializer.Serialize(value, InspectionJson.Options), new System.Text.UTF8Encoding(false));

    private sealed record AppliedText(TextTarget Target, string Original, string Applied);
}
