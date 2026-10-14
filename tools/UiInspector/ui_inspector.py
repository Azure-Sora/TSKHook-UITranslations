"""Local CLI for TSKHook.UiInspector. Python 3 standard library only; never retries actions."""
import argparse
import json
import os
from pathlib import Path
import sys
import urllib.error
import urllib.request
import uuid


def parser():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--game-path", default=os.environ.get("TSK_GAME_PATH"))
    p.add_argument("--discovery", type=Path, help="Explicit path to ui_inspector/bridge.json")
    commands = p.add_subparsers(dest="command", required=True)
    commands.add_parser("ping")
    commands.add_parser("reload-tskhook-config", help="Reload the installed TSKHook config on Unity's thread; no arbitrary path or code")
    ui = commands.add_parser("ui", help="Read the current UI tree without writing inside the game directory")
    ui.add_argument("--output", type=Path)
    catalog = commands.add_parser("catalog", help="Read the game's static TextType catalog; does not open pages or perform actions")
    catalog.add_argument("--output", type=Path)
    dump = commands.add_parser("dump", help="Save tree and override template inside the game directory")
    shot = commands.add_parser("screenshot")
    shot.add_argument("--output", type=Path, required=True)
    for name in ("image-export", "image-apply"):
        action = commands.add_parser(name, help="Export or temporarily replace one exact UI Image sprite")
        action.add_argument("--scene", required=True)
        action.add_argument("--path", required=True)
        action.add_argument("--instance-id", type=int, required=True)
        action.add_argument("--component-index", type=int, required=True)
        action.add_argument("--sprite", required=True, help="Expected current sprite name; stale-resource guard")
        if name == "image-export":
            action.add_argument("--output", type=Path, required=True)
        else:
            action.add_argument("--file", type=Path, required=True)
    commands.add_parser("image-restore", help="Restore image previews still owned by this tool")
    spine = commands.add_parser("spine", help="Read active SkeletonGraphic data, slots and atlas provenance")
    spine.add_argument("--filter", default="", help="Case-sensitive substring of the exact escaped UI path")
    spine.add_argument("--output", type=Path)
    spine_export = commands.add_parser("spine-export", help="Export one exact SkeletonGraphic atlas page as PNG")
    spine_export.add_argument("--scene", required=True)
    spine_export.add_argument("--path", required=True)
    spine_export.add_argument("--instance-id", type=int, required=True)
    spine_export.add_argument("--texture", required=True, help="Expected current atlas texture name")
    spine_export.add_argument("--page", type=int, default=0)
    spine_export.add_argument("--output", type=Path, required=True)
    spine_apply = commands.add_parser("spine-apply", help="Preview a same-layout atlas on one exact SkeletonGraphic")
    spine_apply.add_argument("--scene", required=True)
    spine_apply.add_argument("--path", required=True)
    spine_apply.add_argument("--instance-id", type=int, required=True)
    spine_apply.add_argument("--texture", required=True)
    spine_apply.add_argument("--page", type=int, default=0)
    spine_apply.add_argument("--file", type=Path, required=True)
    commands.add_parser("spine-restore", help="Restore Spine atlas previews still owned by this tool")
    commands.add_parser("unhover", help="Send pointer exit for the tool's last hover")
    for name in ("click", "scroll", "hover"):
        action = commands.add_parser(name)
        target = action.add_mutually_exclusive_group(required=True)
        target.add_argument("--path", help="Exact escaped node path from the current UI tree")
        target.add_argument("--xy", nargs=2, type=float, metavar=("X", "Y"), help="Framebuffer pixels, top-left origin")
        action.add_argument("--scene", help="Required with --path")
        action.add_argument("--instance-id", type=int, help="Optional stale-node guard from the UI dump")
        if name == "scroll":
            action.add_argument("--delta", type=float, required=True, help="Vertical scroll delta, -20..20")
    apply = commands.add_parser("apply", help="Apply a local rules file once, or read game-side overrides.json")
    apply.add_argument("--file", type=Path)
    for scan in (ui, dump, apply):
        scan.add_argument("--canvas", help="Exact Canvas name; empty string selects all Canvas roots")
        scan.add_argument("--include-inactive", action=argparse.BooleanOptionalAction, default=None)
        scan.add_argument("--max-nodes", type=int, help="100..50000 nodes")
    commands.add_parser("restore")
    status = commands.add_parser("status", help="Inspect a recent request; never executes it again")
    status.add_argument("request_id")
    return p


def make_arguments(args, p):
    values = {}
    for attr, key in (("canvas", "canvas"), ("include_inactive", "includeInactive"), ("max_nodes", "maxNodes")):
        if getattr(args, attr, None) is not None:
            values[key] = getattr(args, attr)
    if args.command == "spine":
        values["filter"] = args.filter
    elif args.command in ("spine-export", "spine-apply"):
        values.update(scene=args.scene, path=args.path, instanceId=args.instance_id, texture=args.texture, page=args.page)
        if args.command == "spine-apply":
            import base64
            data = args.file.read_bytes()
            if len(data) > 2 * 1024 * 1024 or not data.startswith(b"\x89PNG\r\n\x1a\n"):
                p.error("Expected an atlas PNG of at most 2 MiB")
            values["pngBase64"] = base64.b64encode(data).decode("ascii")
    elif args.command in ("image-export", "image-apply"):
        values.update(scene=args.scene, path=args.path, instanceId=args.instance_id,
                      componentIndex=args.component_index, sprite=args.sprite)
        if args.command == "image-apply":
            import base64
            data = args.file.read_bytes()
            if len(data) > 4 * 1024 * 1024 or not data.startswith(b"\x89PNG\r\n\x1a\n"):
                p.error("Expected a PNG of at most 4 MiB")
            values["pngBase64"] = base64.b64encode(data).decode("ascii")
    elif args.command in ("click", "scroll", "hover"):
        if args.path:
            if not args.scene:
                p.error("--scene is required with --path")
            values.update(path=args.path, scene=args.scene)
            if args.instance_id is not None:
                values["instanceId"] = args.instance_id
        else:
            values.update(zip(("x", "y"), args.xy))
            if args.instance_id is not None or args.scene:
                p.error("--instance-id and --scene require --path")
        if args.command == "scroll":
            values["delta"] = args.delta
    elif args.command == "apply" and args.file:
        values["rules"] = json.loads(args.file.read_text(encoding="utf-8-sig"))
    elif args.command == "status":
        values["requestId"] = args.request_id
    return values


def invoke(discovery, command, arguments, request_id):
    info = json.loads(discovery.read_text(encoding="utf-8-sig"))
    port = int(info["port"])
    if not 1024 <= port <= 65535:
        raise ValueError("Invalid discovery port")
    payload = json.dumps({"id": request_id, "command": command, "arguments": arguments}, ensure_ascii=False, allow_nan=False).encode("utf-8")
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}/command", data=payload, method="POST",
        headers={"Content-Type": "application/json", "X-UI-Inspector-Token": info["token"]},
    )
    # Ignore machine/browser proxy settings; the bridge only ever lives on loopback.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(request, timeout=20) as response:
        return response.headers.get_content_type(), response.read()


def main(argv=None):
    p = parser()
    args = p.parse_args(argv)
    if not args.discovery and not args.game_path:
        p.error("Set --game-path, TSK_GAME_PATH or --discovery")
    discovery = args.discovery or Path(args.game_path) / "BepInEx/plugins/TSKHook/ui_inspector/bridge.json"
    request_id = str(uuid.uuid4())
    try:
        content_type, body = invoke(discovery, args.command, make_arguments(args, p), request_id)
        output = getattr(args, "output", None)
        if args.command in ("screenshot", "image-export", "spine-export") and content_type != "image/png":
            raise ValueError("Expected PNG image response")
        if output:
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_bytes(body)
            print(json.dumps({"requestId": request_id, "output": str(output.resolve()), "bytes": len(body)}, ensure_ascii=False))
        else:
            result = json.loads(body)
            print(json.dumps({"requestId": request_id, "result": result}, ensure_ascii=False, indent=2))
        return 0
    except urllib.error.HTTPError as error:
        print(json.dumps({"requestId": request_id, "httpStatus": error.code, "response": error.read().decode("utf-8", errors="replace")}, ensure_ascii=False), file=sys.stderr)
    except (OSError, ValueError, KeyError, urllib.error.URLError) as error:
        print(json.dumps({"requestId": request_id, "error": str(error), "hint": "No automatic retry. For a possibly submitted action, use status REQUEST_ID before deciding what to do next."}, ensure_ascii=False), file=sys.stderr)
    return 2


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    raise SystemExit(main())
