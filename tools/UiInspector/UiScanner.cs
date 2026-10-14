using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TSKHook.UiInspector;

internal sealed record TextTarget(UiNode Node, UiText Text, Func<bool> Alive, Func<string> Read, Action<string> Write);

internal static class UiScanner
{
    private static readonly Dictionary<string, UiComponentKind> ComponentKinds = new(StringComparer.Ordinal);
    internal static UiSnapshot Scan(InspectorPlugin plugin) => Scan(plugin, out _);

    // Only called from the injected behaviour's Update on the Unity main thread.
    internal static UiSnapshot Scan(InspectorPlugin plugin, out List<TextTarget> targets, ScanOptions options = null)
    {
        var limit = options?.MaxNodes ?? plugin.MaxNodes.Value;
        if (limit < 100 || limit > 50000) throw new InvalidDataException("maxNodes must be within 100..50000.");
        var result = new UiSnapshot
        {
            UnityVersion = Application.unityVersion, ScreenWidth = Screen.width,
            ScreenHeight = Screen.height, IncludeInactive = options?.IncludeInactive ?? plugin.IncludeInactive.Value
        };
        var collected = new List<TextTarget>();
        var visited = new HashSet<int>();
        var canvases = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Canvas>());
        var filter = options?.Canvas ?? plugin.CanvasFilter.Value;
        foreach (var candidate in canvases)
        {
            if (candidate == null) continue;
            var canvas = candidate.TryCast<Canvas>();
            if (canvas == null || !canvas.gameObject.scene.IsValid() || !canvas.gameObject.scene.isLoaded) continue;
            if (!result.IncludeInactive && !canvas.gameObject.activeInHierarchy) continue;
            if (string.IsNullOrEmpty(filter))
            {
                // Walk only outermost Canvas; nested canvases are visited as descendants.
                var parent = canvas.transform.parent;
                var nested = false;
                while (parent != null)
                {
                    if (parent.GetComponent<Canvas>() != null) { nested = true; break; }
                    parent = parent.parent;
                }
                if (nested) continue;
            }
            else if (canvas.name != filter) continue;
            Walk(canvas.transform, null, FullPath(canvas.transform), 0, canvas, limit, result, collected, visited);
        }
        targets = collected;
        return result;
    }

    private static void Walk(Transform tr, string parentPath, string path, int depth, Canvas canvas,
        int limit, UiSnapshot result, List<TextTarget> targets, HashSet<int> visited)
    {
        if (tr == null || (!result.IncludeInactive && !tr.gameObject.activeInHierarchy)) return;
        if (result.Nodes.Count >= limit || depth > 64) { result.Truncated = true; return; }
        if (!visited.Add(tr.GetInstanceID())) return;
        try
        {
            var ownCanvas = tr.GetComponent<Canvas>();
            if (ownCanvas != null) canvas = ownCanvas;
            var node = new UiNode
            {
                Scene = tr.gameObject.scene.name, Name = tr.name, Path = path, ParentPath = parentPath,
                InstanceId = tr.gameObject.GetInstanceID(), ActiveSelf = tr.gameObject.activeSelf,
                ActiveInHierarchy = tr.gameObject.activeInHierarchy
            };
            result.Nodes.Add(node);
            try
            {
                var rt = tr.TryCast<RectTransform>();
                if (rt != null) node.ScreenBox = ScreenBox(rt, canvas);
            }
            catch (Exception e) { result.Errors.Add(path + " bounds: " + e.Message); }
            var components = tr.GetComponents<Component>();
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                var type = component == null ? "<missing>" : component.GetIl2CppType().FullName;
                node.Components.Add(type);
                if (component == null) continue;
                try { InspectComponent(component, type, i, node, targets); }
                catch (Exception e) { result.Errors.Add(path + " " + type + ": " + e.Message); }
            }
        }
        catch (Exception e) { result.Errors.Add(path + ": " + e.Message); }
        for (var i = 0; i < tr.childCount; i++)
        {
            if (result.Nodes.Count >= limit) { result.Truncated = true; break; }
            var child = tr.GetChild(i);
            Walk(child, path, path + "/" + OverrideRules.Segment(child.name, child.GetSiblingIndex()), depth + 1,
                canvas, limit, result, targets, visited);
        }
    }

    private static void InspectComponent(Component component, string type, int index, UiNode node, List<TextTarget> targets)
    {
        if (!ComponentKinds.TryGetValue(type, out var kind))
        {
            var ancestry = new List<string>();
            for (var native = component.GetIl2CppType(); native != null; native = native.BaseType) ancestry.Add(native.FullName);
            kind = ComponentClassifier.Classify(ancestry);
            ComponentKinds.Add(type, kind);
        }
        var tmp = kind == UiComponentKind.Tmp ? component.Cast<TMP_Text>() : null;
        var text = kind == UiComponentKind.Text ? component.Cast<Text>() : null;
        var mesh = kind == UiComponentKind.TextMesh ? component.Cast<TextMesh>() : null;
        if (tmp != null)
            AddText(node, targets, type, index, tmp.enabled, tmp.text, tmp.font?.name, tmp.fontSize,
                () => tmp != null, () => tmp.text, value => tmp.text = value);
        else if (text != null)
            AddText(node, targets, type, index, text.enabled, text.text, text.font?.name, text.fontSize,
                () => text != null, () => text.text, value => text.text = value);
        else if (mesh != null)
            AddText(node, targets, type, index, true, mesh.text, mesh.font?.name, mesh.fontSize,
                () => mesh != null, () => mesh.text, value => mesh.text = value);

        var image = kind == UiComponentKind.Image ? component.Cast<Image>() : null;
        var raw = kind == UiComponentKind.RawImage ? component.Cast<RawImage>() : null;
        if (image != null)
        {
            var sprite = image.overrideSprite != null ? image.overrideSprite : image.sprite;
            var texture = sprite == null ? null : sprite.texture;
            node.Images.Add(new UiImage
            {
                Component = type, ComponentIndex = index, Enabled = image.enabled, Sprite = sprite?.name,
                Texture = texture?.name, TextureWidth = texture == null ? 0 : texture.width,
                TextureHeight = texture == null ? 0 : texture.height,
                // Do not use textureRect: packed sprites can throw in this game's RaycastMask path.
                SpriteRect = sprite == null ? null : RectArray(sprite.rect)
            });
        }
        else if (raw != null)
            node.Images.Add(new UiImage
            {
                Component = type, ComponentIndex = index, Enabled = raw.enabled, Texture = raw.texture?.name,
                TextureWidth = raw.texture == null ? 0 : raw.texture.width,
                TextureHeight = raw.texture == null ? 0 : raw.texture.height, UvRect = RectArray(raw.uvRect)
            });
    }

    private static void AddText(UiNode node, List<TextTarget> targets, string type, int index, bool enabled,
        string value, string font, float size, Func<bool> alive, Func<string> read, Action<string> write)
    {
        var text = new UiText { Component = type, ComponentIndex = index, Enabled = enabled, CurrentText = value, Font = font, FontSize = size };
        node.Texts.Add(text);
        targets.Add(new TextTarget(node, text, alive, read, write));
    }

    internal static string FullPath(Transform tr)
    {
        var parts = new List<string>();
        for (var current = tr; current != null; current = current.parent)
            parts.Add(OverrideRules.Segment(current.name, current.GetSiblingIndex()));
        parts.Reverse();
        return "/" + string.Join("/", parts);
    }

    private static float[] RectArray(Rect rect) => new[] { rect.x, rect.y, rect.width, rect.height };

    private static float[] ScreenBox(RectTransform rect, Canvas canvas)
    {
        var corners = new Il2CppStructArray<Vector3>(4);
        rect.GetWorldCorners(corners);
        var root = canvas.rootCanvas;
        var camera = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
        var minX = float.MaxValue; var minY = float.MaxValue;
        var maxX = float.MinValue; var maxY = float.MinValue;
        for (var i = 0; i < 4; i++)
        {
            var point = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
            minX = Math.Min(minX, point.x); maxX = Math.Max(maxX, point.x);
            minY = Math.Min(minY, point.y); maxY = Math.Max(maxY, point.y);
        }
        return new[] { minX, Screen.height - maxY, maxX, Screen.height - minY };
    }
}
