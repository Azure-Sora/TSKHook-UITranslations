using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;
using UObject = UnityEngine.Object;

namespace TSKHook.UiInspector;

public sealed partial class InspectorBehaviour
{
    private readonly List<ImagePreview> imagePreviews = new();

    [HideFromIl2Cpp]
    private static Image FindImage(JsonElement args)
    {
        var scene = args.GetProperty("scene").GetString();
        var path = args.GetProperty("path").GetString();
        var id = args.GetProperty("instanceId").GetInt32();
        var index = args.GetProperty("componentIndex").GetInt32();
        foreach (var candidate in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Image>()))
        {
            var image = candidate == null ? null : candidate.TryCast<Image>();
            if (image == null || !image.gameObject.scene.IsValid() || !image.gameObject.scene.isLoaded ||
                image.gameObject.GetInstanceID() != id || image.gameObject.scene.name != scene ||
                UiScanner.FullPath(image.transform) != path) continue;
            var components = image.gameObject.GetComponents<Component>();
            if (index < 0 || index >= components.Length || components[index] == null ||
                components[index].GetInstanceID() != image.GetInstanceID()) continue;
            var sprite = image.overrideSprite != null ? image.overrideSprite : image.sprite;
            if (sprite == null || sprite.name != args.GetProperty("sprite").GetString())
                throw new InvalidOperationException("Current sprite no longer matches the request.");
            return image;
        }
        throw new InvalidOperationException("Exact scene/path/instance/component Image target no longer exists.");
    }

    // Reconstruct from mesh vertices and UVs, including tight/rotated atlas packing. Never call
    // textureRect (it can throw for this game's packed sprites). Readback also supports non-readable atlases.
    [HideFromIl2Cpp]
    private static BridgeReply ExportImage(JsonElement args)
    {
        var image = FindImage(args);
        var sprite = image.overrideSprite != null ? image.overrideSprite : image.sprite;
        var atlas = sprite.texture;
        var width = Mathf.RoundToInt(sprite.rect.width);
        var height = Mathf.RoundToInt(sprite.rect.height);
        if (atlas == null || width <= 0 || height <= 0 || width > 2048 || height > 2048 ||
            atlas.width > 8192 || atlas.height > 8192) throw new InvalidDataException("Unsupported image dimensions.");
        Texture2D readable = null, output = null;
        RenderTexture temporary = null;
        var previous = RenderTexture.active;
        try
        {
            temporary = RenderTexture.GetTemporary(atlas.width, atlas.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(atlas, temporary);
            RenderTexture.active = temporary;
            readable = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false, false);
            readable.ReadPixels(new Rect(0, 0, atlas.width, atlas.height), 0, 0, false);
            readable.Apply(false, false);
            var source = readable.GetPixels32();
            var pixels = new Il2CppStructArray<Color32>(width * height);
            var vertices = sprite.vertices;
            var uv = sprite.uv;
            var triangles = sprite.triangles;
            var pivot = sprite.pivot;
            var ppu = sprite.pixelsPerUnit;
            for (var t = 0; t < triangles.Length; t += 3)
            {
                var ia = triangles[t]; var ib = triangles[t + 1]; var ic = triangles[t + 2];
                var a = vertices[ia] * ppu + pivot;
                var b = vertices[ib] * ppu + pivot;
                var c = vertices[ic] * ppu + pivot;
                var denominator = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
                if (Math.Abs(denominator) < 0.00001f) continue;
                var minX = Math.Max(0, Mathf.FloorToInt(Math.Min(a.x, Math.Min(b.x, c.x))));
                var maxX = Math.Min(width - 1, Mathf.CeilToInt(Math.Max(a.x, Math.Max(b.x, c.x))));
                var minY = Math.Max(0, Mathf.FloorToInt(Math.Min(a.y, Math.Min(b.y, c.y))));
                var maxY = Math.Min(height - 1, Mathf.CeilToInt(Math.Max(a.y, Math.Max(b.y, c.y))));
                for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                {
                    var wa = ((b.y - c.y) * (x + 0.5f - c.x) + (c.x - b.x) * (y + 0.5f - c.y)) / denominator;
                    var wb = ((c.y - a.y) * (x + 0.5f - c.x) + (a.x - c.x) * (y + 0.5f - c.y)) / denominator;
                    var wc = 1 - wa - wb;
                    if (wa < -0.00001f || wb < -0.00001f || wc < -0.00001f) continue;
                    var coordinate = uv[ia] * wa + uv[ib] * wb + uv[ic] * wc;
                    var sx = Math.Clamp(Mathf.FloorToInt(coordinate.x * atlas.width), 0, atlas.width - 1);
                    var sy = Math.Clamp(Mathf.FloorToInt(coordinate.y * atlas.height), 0, atlas.height - 1);
                    pixels[y * width + x] = source[sy * atlas.width + sx];
                }
            }
            output = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            output.SetPixels32(pixels);
            output.Apply(false, false);
            var encoded = ImageConversion.EncodeToPNG(output);
            var bytes = new byte[encoded.Length];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = encoded[i];
            return new BridgeReply(bytes, "image/png");
        }
        finally
        {
            RenderTexture.active = previous;
            if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
            if (readable != null) UObject.Destroy(readable);
            if (output != null) UObject.Destroy(output);
        }
    }

    [HideFromIl2Cpp]
    private object ApplyImage(JsonElement args)
    {
        var image = FindImage(args);
        if (imagePreviews.Any(p => p.Image != null && p.Image.GetInstanceID() == image.GetInstanceID()))
            throw new InvalidOperationException("Restore the existing preview before applying another.");
        var originalSlot = image.overrideSprite;
        var original = originalSlot != null ? originalSlot : image.sprite;
        var bytes = Convert.FromBase64String(args.GetProperty("pngBase64").GetString());
        if (bytes.Length < 24 || bytes.Length > 4 * 1024 * 1024 ||
            !bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Expected PNG, at most 4 MiB.");
        var width = Mathf.RoundToInt(original.rect.width);
        var height = Mathf.RoundToInt(original.rect.height);
        uint ReadDimension(int offset) => ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        if (ReadDimension(16) != width || ReadDimension(20) != height)
            throw new InvalidDataException($"Replacement PNG must be exactly {width}x{height}.");
        Texture2D texture = null;
        Sprite replacement = null;
        try
        {
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
            {
                name = original.name + "__preview_texture", hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp, filterMode = original.texture.filterMode
            };
            if (!ImageConversion.LoadImage(texture, (Il2CppStructArray<byte>)bytes, false) ||
                texture.width != width || texture.height != height) throw new InvalidDataException("PNG decoding failed.");
            replacement = Sprite.Create(texture, new Rect(0, 0, width, height),
                new Vector2(original.pivot.x / width, original.pivot.y / height), original.pixelsPerUnit,
                0, SpriteMeshType.FullRect, original.border, false);
            replacement.name = original.name + "__zh_preview";
            replacement.hideFlags = HideFlags.HideAndDontSave;
            image.overrideSprite = replacement;
            if (image.overrideSprite != replacement) throw new InvalidOperationException("Image write could not be verified.");
            imagePreviews.Add(new ImagePreview(image, originalSlot, replacement, texture));
            return new { applied = true, sprite = replacement.name, width, height, path = UiScanner.FullPath(image.transform) };
        }
        catch
        {
            if (image != null && replacement != null && image.overrideSprite == replacement) image.overrideSprite = originalSlot;
            if (replacement != null) UObject.Destroy(replacement);
            if (texture != null) UObject.Destroy(texture);
            throw;
        }
    }

    [HideFromIl2Cpp]
    private object RestoreImages()
    {
        var restored = 0; var skipped = 0;
        foreach (var preview in imagePreviews)
        {
            if (preview.Image != null && preview.Image.overrideSprite == preview.Replacement)
            {
                preview.Image.overrideSprite = preview.OriginalSlot;
                if (preview.Image.overrideSprite != preview.Replacement) restored++; else skipped++;
            }
            else skipped++;
            if (preview.Image != null && preview.Image.overrideSprite == preview.Replacement) continue;
            if (preview.Replacement != null) UObject.Destroy(preview.Replacement);
            if (preview.Texture != null) UObject.Destroy(preview.Texture);
        }
        imagePreviews.RemoveAll(p => p.Image == null || p.Image.overrideSprite != p.Replacement);
        return new { restored, skipped };
    }

    private sealed record ImagePreview(Image Image, Sprite OriginalSlot, Sprite Replacement, Texture2D Texture);
}
