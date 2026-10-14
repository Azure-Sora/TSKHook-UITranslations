using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using System.Text.Json;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UObject = UnityEngine.Object;

namespace TSKHook.UiInspector;

public sealed partial class InspectorBehaviour
{
    private readonly CommandQueue commands = new();
    private BridgeServer server;
    private Texture2D captureTexture;
    private int captureFrames;
    private TaskCompletionSource<BridgeReply> captureCompletion;
    private PendingClick pendingClick;
    private PendingClick hoverPointer;

    private void Awake()
    {
        try
        {
            Directory.CreateDirectory(Root);
            var plugin = InspectorPlugin.Instance;
            if (!plugin.BridgeEnabled.Value) return;
            server = new BridgeServer(commands, plugin.Port.Value, Path.Combine(Root, "bridge.json"));
            plugin.Log.LogInfo($"[UI Inspector] Local command bridge on 127.0.0.1:{plugin.Port.Value}; discovery: {Path.Combine(Root, "bridge.json")}");
        }
        catch (Exception e) { InspectorPlugin.Instance.Log.LogError("[UI Inspector] Bridge startup failed; hotkeys remain available: " + e.Message); }
    }

    private void OnDestroy()
    {
        RestoreImages();
        RestoreSpineTextures();
        server?.Dispose();
        commands.Stop();
        ClearHover();
        captureCompletion?.TrySetException(new OperationCanceledException("Inspector stopped."));
        if (pendingClick != null)
        {
            ReleasePointer(pendingClick);
            pendingClick.Completion.TrySetException(new OperationCanceledException("Inspector stopped."));
            pendingClick = null;
        }
        if (captureTexture != null) UObject.Destroy(captureTexture);
        captureTexture = null;
    }

    [HideFromIl2Cpp]
    private Task<BridgeReply> Dispatch(CommandRequest request)
    {
        var args = request.Arguments;
        ScanOptions options = null;
        if (request.Command is "ui" or "dump" or "apply" && args.ValueKind == JsonValueKind.Object)
            options = JsonSerializer.Deserialize<ScanOptions>(args.GetRawText(), InspectionJson.Options);
        object result;
        switch (request.Command)
        {
            case "ping":
                result = new { pong = true, inspectorVersion = typeof(InspectorPlugin).Assembly.GetName().Version?.ToString(), pid = Environment.ProcessId, unity = Application.unityVersion, scene = SceneManager.GetActiveScene().name, width = Screen.width, height = Screen.height };
                break;
            case "ui": result = UiScanner.Scan(InspectorPlugin.Instance, out _, options); break;
            case "catalog": result = ReadGameTextCatalog(); break;
            case "dump": result = Dump(UiScanner.Scan(InspectorPlugin.Instance, out _, options)); break;
            case "apply":
                result = ApplyPreview(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("rules", out var rules) ? rules.GetRawText() : null, options);
                break;
            case "restore": result = Restore(); break;
            case "click": return (Task<BridgeReply>)DispatchPointer(args, false);
            case "scroll": result = DispatchPointer(args, true); break;
            case "hover": result = DispatchPointer(args, false, true); break;
            case "unhover": ClearHover(); result = new { cleared = true }; break;
            case "screenshot": return StartCapture();
            case "image-export": return Task.FromResult(ExportImage(args));
            case "image-apply": result = ApplyImage(args); break;
            case "image-restore": result = RestoreImages(); break;
            case "spine": result = ReadSpine(args); break;
            case "spine-export": return Task.FromResult(ExportSpineTexture(args));
            case "spine-apply": result = ApplySpineTexture(args); break;
            case "spine-restore": result = RestoreSpineTextures(); break;
            default: throw new InvalidDataException("Unknown command: " + request.Command);
        }
        return Task.FromResult(BridgeReply.Json(result));
    }

    [HideFromIl2Cpp]
    private static object ReadGameTextCatalog()
    {
        var entries = new List<object>();
        var errors = new List<string>();
        // TextManager is the game's read-only localization provider. Retain format placeholders
        // instead of inventing sample quantities or calling any view/account/gameplay methods.
        var arguments = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(
            Enumerable.Range(0, 16).Select(i => "{" + i + "}").ToArray());
        foreach (TextType type in Enum.GetValues(typeof(TextType)))
        {
            try
            {
                var source = TextManager.GetText(type, arguments);
                if (!string.IsNullOrWhiteSpace(source)) entries.Add(new { id = (int)type, name = type.ToString(), source });
            }
            catch (Exception e) { errors.Add(type + ": " + e.Message); }
        }
        return new { schemaVersion = 1, entries, errors };
    }

    [HideFromIl2Cpp]
    private Task<BridgeReply> StartCapture()
    {
        if (captureCompletion != null) throw new InvalidOperationException("Screenshot already in progress.");
        captureTexture = ScreenCapture.CaptureScreenshotAsTexture();
        if (captureTexture == null) throw new InvalidOperationException("No screenshot texture available.");
        captureFrames = 2;
        captureCompletion = new TaskCompletionSource<BridgeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        return captureCompletion.Task;
    }

    private void PumpCapture()
    {
        if (captureCompletion == null || --captureFrames > 0) return;
        var completion = captureCompletion;
        captureCompletion = null;
        try
        {
            var encoded = ImageConversion.EncodeToPNG(captureTexture);
            var bytes = new byte[encoded.Length];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = encoded[i];
            completion.TrySetResult(new BridgeReply(bytes, "image/png"));
        }
        catch (Exception e) { completion.TrySetException(e); }
        finally
        {
            if (captureTexture != null) UObject.Destroy(captureTexture);
            captureTexture = null;
        }
    }

    [HideFromIl2Cpp]
    private object DispatchPointer(JsonElement args, bool scroll, bool hover = false)
    {
        var eventSystem = EventSystem.current;
        if (eventSystem == null) throw new InvalidOperationException("No active EventSystem.");
        GameObject requested = null;
        Vector2 point;
        if (args.TryGetProperty("path", out var pathValue))
        {
            var path = pathValue.GetString();
            var scene = args.GetProperty("scene").GetString();
            var matches = new List<GameObject>();
            foreach (var candidate in Resources.FindObjectsOfTypeAll(Il2CppType.Of<RectTransform>()))
            {
                var tr = candidate?.TryCast<RectTransform>();
                if (tr == null || !tr.gameObject.scene.IsValid() || !tr.gameObject.scene.isLoaded || !tr.gameObject.activeInHierarchy) continue;
                if (tr.gameObject.scene.name == scene && UiScanner.FullPath(tr) == path) matches.Add(tr.gameObject);
            }
            if (matches.Count != 1) throw new InvalidOperationException($"Expected exactly one active node, found {matches.Count}. Refresh UI tree.");
            requested = matches[0];
            if (args.TryGetProperty("instanceId", out var instance) && requested.GetInstanceID() != instance.GetInt32())
                throw new InvalidOperationException("Node instance changed. Refresh UI tree.");
            var rect = requested.GetComponent<RectTransform>();
            var canvas = requested.GetComponentInParent<Canvas>()?.rootCanvas;
            var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
        }
        else
        {
            var x = args.GetProperty("x").GetSingle();
            var y = args.GetProperty("y").GetSingle();
            if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0 || y < 0 || x >= Screen.width || y >= Screen.height)
                throw new InvalidDataException("Coordinates must be inside the game framebuffer (top-left origin).");
            point = new Vector2(x, Screen.height - y);
        }
        if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || point.x < 0 || point.y < 0 || point.x >= Screen.width || point.y >= Screen.height)
            throw new InvalidOperationException("Target center is outside the framebuffer; scroll it into view first.");
        var pointer = new PointerEventData(eventSystem) { position = point, button = PointerEventData.InputButton.Left };
        var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        eventSystem.RaycastAll(pointer, hits);
        if (hits.Count == 0) throw new InvalidOperationException("No raycast target at position.");
        var hit = hits[0].gameObject;
        GameObject handler;
        if (hover)
        {
            if (pendingClick != null) throw new InvalidOperationException("Click already in progress.");
            handler = ExecuteEvents.GetEventHandler<IPointerEnterHandler>(hit);
            if (handler == null) throw new InvalidOperationException("Top raycast hit has no hover handler.");
            if (requested != null && ExecuteEvents.GetEventHandler<IPointerEnterHandler>(requested)?.GetInstanceID() != handler.GetInstanceID())
                throw new InvalidOperationException("Requested hover target is obscured or has no matching handler.");
            ClearHover();
            pointer.pointerId = -1;
            pointer.pointerCurrentRaycast = hits[0];
            pointer.pointerEnter = hit;
            hoverPointer = new PendingClick { Pointer = pointer, Hit = hit };
            // Deliberately no down/up/click: this exposes rollover descriptions without activating a choice.
            ExecuteEvents.ExecuteHierarchy<IPointerEnterHandler>(hit, pointer, ExecuteEvents.pointerEnterHandler);
        }
        else if (scroll)
        {
            var delta = args.GetProperty("delta").GetSingle();
            if (!float.IsFinite(delta) || Math.Abs(delta) > 20) throw new InvalidDataException("Scroll delta must be finite and within -20..20.");
            handler = ExecuteEvents.GetEventHandler<IScrollHandler>(hit);
            if (handler == null) throw new InvalidOperationException("Top raycast hit has no scroll handler.");
            if (requested != null && ExecuteEvents.GetEventHandler<IScrollHandler>(requested)?.GetInstanceID() != handler.GetInstanceID())
                throw new InvalidOperationException("Requested scroll target is obscured or has no matching handler.");
            pointer.scrollDelta = new Vector2(0, delta);
            ExecuteEvents.Execute<IScrollHandler>(handler, pointer, ExecuteEvents.scrollHandler);
        }
        else
        {
            if (pendingClick != null) throw new InvalidOperationException("Click already in progress.");
            handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit);
            if (handler == null) throw new InvalidOperationException("Top raycast hit has no click handler.");
            if (requested != null && ExecuteEvents.GetEventHandler<IPointerClickHandler>(requested)?.GetInstanceID() != handler.GetInstanceID())
                throw new InvalidOperationException("Requested click target is obscured or has no matching handler.");
            var button = handler.GetComponent<Button>();
            if (button != null && !button.IsInteractable()) throw new InvalidOperationException("Button is not interactable.");
            ClearHover();
            var state = new PendingClick
            {
                Pointer = pointer, Handler = handler, Hit = hit, EventSystem = eventSystem,
                HandlerPath = UiScanner.FullPath(handler.transform), HitPath = UiScanner.FullPath(hit.transform)
            };
            pointer.pointerId = -1;
            pointer.clickCount = 1;
            pointer.clickTime = Time.unscaledTime;
            pointer.eligibleForClick = true;
            pointer.pressPosition = point;
            pointer.pointerCurrentRaycast = hits[0];
            pointer.pointerPressRaycast = hits[0];
            pointer.pointerEnter = hit;
            // Some game wrappers subscribe to enter/down/up observables instead of pointerClick alone.
            ExecuteEvents.ExecuteHierarchy<IPointerEnterHandler>(hit, pointer, ExecuteEvents.pointerEnterHandler);
            state.Press = ExecuteEvents.ExecuteHierarchy<IPointerDownHandler>(hit, pointer, ExecuteEvents.pointerDownHandler) ?? handler;
            pointer.pointerPress = state.Press;
            pointer.rawPointerPress = hit;
            pendingClick = state;
            return state.Completion.Task;
        }
        var handlerPath = UiScanner.FullPath(handler.transform);
        InspectorPlugin.Instance.Log.LogInfo($"[UI Inspector] Dispatched {(hover ? "hover" : "scroll")}: {handlerPath}");
        return new { dispatched = true, handlerPath, hitPath = UiScanner.FullPath(hit.transform), x = point.x, y = Screen.height - point.y };
    }

    [HideFromIl2Cpp]
    private void ClearHover()
    {
        var previous = hoverPointer;
        hoverPointer = null;
        if (previous != null) ReleasePointer(previous, false);
    }

    [HideFromIl2Cpp]
    private void PumpClick()
    {
        var state = pendingClick;
        if (state == null || --state.Frames > 0) return;
        pendingClick = null;
        try
        {
            var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            state.EventSystem.RaycastAll(state.Pointer, hits);
            var releasedHandler = hits.Count == 0 ? null : ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject);
            var stillTarget = state.Handler != null && releasedHandler != null && state.Handler.GetInstanceID() == releasedHandler.GetInstanceID();
            if (state.Press != null) ExecuteEvents.Execute<IPointerUpHandler>(state.Press, state.Pointer, ExecuteEvents.pointerUpHandler);
            var clicked = stillTarget && state.Handler != null && state.Handler.activeInHierarchy;
            if (clicked) ExecuteEvents.Execute<IPointerClickHandler>(state.Handler, state.Pointer, ExecuteEvents.pointerClickHandler);
            InspectorPlugin.Instance.Log.LogInfo($"[UI Inspector] Click sequence: {state.HandlerPath}; clickDispatched={clicked}");
            state.Completion.TrySetResult(BridgeReply.Json(new
            {
                dispatched = clicked, sequence = "enter/down/up/click", handlerPath = state.HandlerPath,
                hitPath = state.HitPath, x = state.Pointer.position.x, y = Screen.height - state.Pointer.position.y
            }));
        }
        catch (Exception e) { state.Completion.TrySetException(e); }
        finally { ReleasePointer(state, false); }
    }

    [HideFromIl2Cpp]
    private static void ReleasePointer(PendingClick state, bool release = true)
    {
        try
        {
            if (release && state.Press != null) ExecuteEvents.Execute<IPointerUpHandler>(state.Press, state.Pointer, ExecuteEvents.pointerUpHandler);
            if (state.Hit != null) ExecuteEvents.ExecuteHierarchy<IPointerExitHandler>(state.Hit, state.Pointer, ExecuteEvents.pointerExitHandler);
        }
        catch { /* Native objects may already be destroyed during shutdown. */ }
        state.Pointer.eligibleForClick = false;
        state.Pointer.pointerPress = null;
        state.Pointer.rawPointerPress = null;
    }

    private sealed class PendingClick
    {
        internal PointerEventData Pointer;
        internal GameObject Handler, Hit, Press;
        internal EventSystem EventSystem;
        internal string HandlerPath, HitPath;
        internal int Frames = 2;
        internal readonly TaskCompletionSource<BridgeReply> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
