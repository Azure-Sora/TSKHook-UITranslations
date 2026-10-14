using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Spine.Unity;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace TSKHook.UiInspector;

public sealed partial class InspectorBehaviour
{
    private readonly List<SpinePreview> spinePreviews = new();

    [HideFromIl2Cpp]
    private static (SkeletonGraphic Graphic, Texture Texture) FindSpinePage(JsonElement args)
    {
        var scene = args.GetProperty("scene").GetString();
        var path = args.GetProperty("path").GetString();
        var id = args.GetProperty("instanceId").GetInt32();
        var requestedPage = args.GetProperty("page").GetInt32();
        var expected = args.GetProperty("texture").GetString();
        foreach (var candidate in Resources.FindObjectsOfTypeAll(Il2CppType.Of<SkeletonGraphic>()))
        {
            var graphic = candidate == null ? null : candidate.TryCast<SkeletonGraphic>();
            if (graphic == null || !graphic.enabled || !graphic.gameObject.activeInHierarchy ||
                !graphic.gameObject.scene.IsValid() || !graphic.gameObject.scene.isLoaded ||
                graphic.gameObject.GetInstanceID() != id || graphic.gameObject.scene.name != scene ||
                UiScanner.FullPath(graphic.transform) != path) continue;
            var page = 0;
            foreach (var atlasBase in graphic.SkeletonDataAsset.atlasAssets)
            {
                var atlas = atlasBase == null ? null : atlasBase.TryCast<SpineAtlasAsset>();
                if (atlas == null) continue;
                foreach (var material in atlas.materials)
                {
                    if (page++ != requestedPage) continue;
                    var texture = material?.mainTexture;
                    if (texture == null || texture.name != expected) throw new InvalidOperationException("Source atlas changed.");
                    if (!graphic.allowMultipleCanvasRenderers && graphic.baseTexture != texture)
                        throw new InvalidOperationException("Single renderer is not currently using this atlas page.");
                    return (graphic, texture);
                }
            }
            throw new InvalidOperationException("Atlas page does not exist.");
        }
        throw new InvalidOperationException("Exact active SkeletonGraphic no longer exists.");
    }

    [HideFromIl2Cpp]
    private object ApplySpineTexture(JsonElement args)
    {
        var (graphic, source) = FindSpinePage(args);
        if (spinePreviews.Any(p => p.Graphic != null && p.Graphic.GetInstanceID() == graphic.GetInstanceID()))
            throw new InvalidOperationException("Restore this graphic's preview before applying another.");
        var bytes = Convert.FromBase64String(args.GetProperty("pngBase64").GetString());
        if (bytes.Length < 24 || bytes.Length > 2 * 1024 * 1024 ||
            !bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Expected atlas PNG of at most 2 MiB.");
        uint Dimension(int offset) => ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        if (Dimension(16) != source.width || Dimension(20) != source.height)
            throw new InvalidDataException($"Atlas PNG must be exactly {source.width}x{source.height}.");
        var multiple = graphic.allowMultipleCanvasRenderers;
        var hadMapping = graphic.CustomTextureOverride.TryGetValue(source, out var originalMapping);
        var originalOverride = graphic.OverrideTexture;
        Texture2D replacement = null;
        try
        {
            replacement = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
            {
                name = source.name + "__zh_atlas_preview", hideFlags = HideFlags.HideAndDontSave,
                filterMode = source.filterMode, wrapMode = source.wrapMode
            };
            if (!ImageConversion.LoadImage(replacement, (Il2CppStructArray<byte>)bytes, false) ||
                replacement.width != source.width || replacement.height != source.height)
                throw new InvalidDataException("Atlas PNG decoding failed.");
            // Spine 4.0 single-renderer mainTexture reads OverrideTexture; only its multiple-renderer
            // mesh update consults CustomTextureOverride. Use the actual installed runtime's two paths.
            if (multiple) graphic.CustomTextureOverride[source] = replacement;
            else graphic.OverrideTexture = replacement;
            graphic.UpdateMesh(true);
            var preview = new SpinePreview(graphic, source, replacement, multiple, hadMapping, originalMapping, originalOverride);
            if (!OwnsSpinePreview(preview)) throw new InvalidOperationException("Atlas preview write was not retained.");
            spinePreviews.Add(preview);
            return new { applied = true, route = multiple ? "CustomTextureOverride" : "OverrideTexture",
                source = source.name, replacement = replacement.name, width = replacement.width, height = replacement.height };
        }
        catch
        {
            if (multiple)
            {
                if (replacement != null && graphic.CustomTextureOverride.TryGetValue(source, out var current) && current == replacement)
                {
                    if (hadMapping) graphic.CustomTextureOverride[source] = originalMapping;
                    else graphic.CustomTextureOverride.Remove(source);
                }
            }
            else if (replacement != null && graphic.OverrideTexture == replacement) graphic.OverrideTexture = originalOverride;
            try { graphic.UpdateMesh(true); } catch { }
            if (replacement != null) UObject.Destroy(replacement);
            throw;
        }
    }

    [HideFromIl2Cpp]
    private static bool OwnsSpinePreview(SpinePreview preview) => preview.Graphic != null &&
        (preview.Multiple ? preview.Graphic.CustomTextureOverride.TryGetValue(preview.Source, out var current) && current == preview.Replacement
            : preview.Graphic.OverrideTexture == preview.Replacement);

    [HideFromIl2Cpp]
    private object RestoreSpineTextures()
    {
        var restored = 0; var skipped = 0;
        foreach (var preview in spinePreviews)
        {
            if (OwnsSpinePreview(preview))
            {
                if (preview.Multiple)
                {
                    if (preview.HadMapping) preview.Graphic.CustomTextureOverride[preview.Source] = preview.OriginalMapping;
                    else preview.Graphic.CustomTextureOverride.Remove(preview.Source);
                }
                else preview.Graphic.OverrideTexture = preview.OriginalOverride;
                preview.Graphic.UpdateMesh(true);
                if (!OwnsSpinePreview(preview)) restored++; else skipped++;
            }
            else skipped++;
            if (!OwnsSpinePreview(preview) && preview.Replacement != null) UObject.Destroy(preview.Replacement);
        }
        spinePreviews.RemoveAll(p => !OwnsSpinePreview(p));
        return new { restored, skipped };
    }

    private sealed record SpinePreview(SkeletonGraphic Graphic, Texture Source, Texture2D Replacement,
        bool Multiple, bool HadMapping, Texture OriginalMapping, Texture OriginalOverride);
}
