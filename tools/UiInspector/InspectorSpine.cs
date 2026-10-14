using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Spine;
using Spine.Unity;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace TSKHook.UiInspector;

public sealed partial class InspectorBehaviour
{
    [HideFromIl2Cpp]
    private static object ReadSpine(JsonElement args)
    {
        var filter = args.TryGetProperty("filter", out var value) ? value.GetString() ?? "" : "";
        var entries = new List<object>();
        var errors = new List<string>();
        foreach (var candidate in Resources.FindObjectsOfTypeAll(Il2CppType.Of<SkeletonGraphic>()))
        {
            var graphic = candidate == null ? null : candidate.TryCast<SkeletonGraphic>();
            if (graphic == null || !graphic.enabled || !graphic.gameObject.activeInHierarchy ||
                !graphic.gameObject.scene.IsValid() || !graphic.gameObject.scene.isLoaded) continue;
            var path = UiScanner.FullPath(graphic.transform);
            if (!path.Contains(filter, StringComparison.Ordinal)) continue;
            try
            {
                var asset = graphic.SkeletonDataAsset;
                var data = graphic.SkeletonData;
                var atlas = new List<object>();
                var page = 0;
                foreach (var atlasBase in asset.atlasAssets)
                {
                    var atlasAsset = atlasBase == null ? null : atlasBase.TryCast<SpineAtlasAsset>();
                    if (atlasAsset == null) continue;
                    foreach (var material in atlasAsset.materials)
                    {
                        var texture = material == null ? null : material.mainTexture;
                        atlas.Add(new { page = page++, asset = atlasAsset.name, atlasFile = atlasAsset.atlasFile?.name,
                            atlasText = atlasAsset.atlasFile?.text, material = material?.name, shader = material?.shader?.name,
                            texture = texture?.name, width = texture == null ? 0 : texture.width, height = texture == null ? 0 : texture.height });
                    }
                }
                var slots = new List<object>();
                var skeleton = graphic.Skeleton;
                if (skeleton != null)
                for (var i = 0; i < skeleton.Slots.Count; i++)
                {
                    var slot = skeleton.Slots.Items[i];
                    var attachment = slot.Attachment;
                    var regionAttachment = attachment == null ? null : attachment.TryCast<RegionAttachment>();
                    var meshAttachment = attachment == null ? null : attachment.TryCast<MeshAttachment>();
                    var renderer = regionAttachment?.RendererObject ?? meshAttachment?.RendererObject;
                    var region = renderer == null ? null : renderer.TryCast<AtlasRegion>();
                    slots.Add(new { slot = slot.Data.Name, alpha = slot.A, attachment = attachment?.Name,
                        type = attachment?.GetIl2CppType().FullName, path = regionAttachment?.Path ?? meshAttachment?.Path,
                        region = region == null ? null : new { name = region.name, x = region.x, y = region.y,
                            width = region.width, height = region.height, originalWidth = region.originalWidth,
                            originalHeight = region.originalHeight, offsetX = region.offsetX, offsetY = region.offsetY,
                            rotate = region.rotate, degrees = region.degrees, page = region.page?.name } });
                }
                var animations = new List<string>();
                if (data != null) for (var i = 0; i < data.Animations.Count; i++) animations.Add(data.Animations.Items[i].Name);
                var current = graphic.AnimationState?.GetCurrent(0);
                var source = asset.skeletonJSON;
                var text = source?.text;
                var renderTextures = new List<string>();
                for (var i = 0; i < graphic.submeshGraphics.Count; i++)
                    if (graphic.submeshGraphics[i] != null && graphic.submeshGraphics[i].gameObject.activeInHierarchy)
                        renderTextures.Add(graphic.submeshGraphics[i].mainTexture?.name);
                var overrides = new List<object>();
                foreach (var atlasBase in asset.atlasAssets)
                {
                    var atlasAsset = atlasBase == null ? null : atlasBase.TryCast<SpineAtlasAsset>();
                    if (atlasAsset == null) continue;
                    foreach (var mat in atlasAsset.materials)
                    {
                        var sourceTexture = mat?.mainTexture;
                        if (sourceTexture != null && graphic.CustomTextureOverride.TryGetValue(sourceTexture, out var mapped))
                            overrides.Add(new { source = sourceTexture.name, replacement = mapped?.name });
                    }
                }
                var renderingMaterial = graphic.material;
                entries.Add(new { scene = graphic.gameObject.scene.name, path, instanceId = graphic.gameObject.GetInstanceID(),
                    skeletonDataAsset = asset.name, skeletonVersion = data?.Version, skeletonSource = source?.name,
                    skeletonJson = text != null && text.TrimStart().StartsWith("{") ? text : null,
                    sourceBytes = source?.bytes?.Length, atlas, slots, animations, currentAnimation = current?.Animation?.Name,
                    trackTime = current?.TrackTime, mainTexture = graphic.mainTexture?.name, overrideTexture = graphic.OverrideTexture?.name,
                    renderingShader = renderingMaterial?.shader?.name, multipleRenderers = graphic.allowMultipleCanvasRenderers,
                    shaderKeywords = renderingMaterial?.shaderKeywords?.ToArray(),
                    straightAlphaInput = renderingMaterial != null && renderingMaterial.HasProperty("_StraightAlphaInput")
                        ? renderingMaterial.GetFloat("_StraightAlphaInput") : (float?)null,
                    renderTextures, overrides });
            }
            catch (Exception e) { errors.Add(path + ": " + e.Message); }
        }
        return new { schemaVersion = 1, entries, errors };
    }

    [HideFromIl2Cpp]
    private static BridgeReply ExportSpineTexture(JsonElement args)
    {
        var scene = args.GetProperty("scene").GetString();
        var path = args.GetProperty("path").GetString();
        var id = args.GetProperty("instanceId").GetInt32();
        var requestedPage = args.GetProperty("page").GetInt32();
        var expectedTexture = args.GetProperty("texture").GetString();
        foreach (var candidate in Resources.FindObjectsOfTypeAll(Il2CppType.Of<SkeletonGraphic>()))
        {
            var graphic = candidate == null ? null : candidate.TryCast<SkeletonGraphic>();
            if (graphic == null || !graphic.gameObject.scene.IsValid() || !graphic.gameObject.scene.isLoaded ||
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
                    if (texture == null || texture.name != expectedTexture) throw new InvalidOperationException("Atlas texture changed.");
                    return ReadSpineTexture(texture);
                }
            }
            throw new InvalidOperationException("Atlas page does not exist.");
        }
        throw new InvalidOperationException("Exact SkeletonGraphic target no longer exists.");
    }

    [HideFromIl2Cpp]
    private static BridgeReply ReadSpineTexture(Texture texture)
    {
        if (texture.width <= 0 || texture.height <= 0 || texture.width > 8192 || texture.height > 8192)
            throw new InvalidDataException("Unsupported atlas dimensions.");
        var previous = RenderTexture.active;
        RenderTexture temporary = null;
        Texture2D readable = null;
        try
        {
            temporary = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(texture, temporary);
            RenderTexture.active = temporary;
            readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, false);
            readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
            readable.Apply(false, false);
            var encoded = ImageConversion.EncodeToPNG(readable);
            var bytes = new byte[encoded.Length];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = encoded[i];
            return new BridgeReply(bytes, "image/png");
        }
        finally
        {
            RenderTexture.active = previous;
            if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
            if (readable != null) UObject.Destroy(readable);
        }
    }
}
