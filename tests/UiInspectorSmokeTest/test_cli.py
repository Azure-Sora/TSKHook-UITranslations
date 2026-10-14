"""CLI transport integration against a local fixture server; no actual game interactions."""
import importlib.util
import json
from pathlib import Path
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer


script = Path(__file__).resolve().parents[2] / "tools/UiInspector/ui_inspector.py"
spec = importlib.util.spec_from_file_location("inspector_cli", script)
cli = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cli)


class CliTest(unittest.TestCase):
    def test_utf8_authenticated_command_and_binary_screenshot(self):
        received = []

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                received.append((self.path, self.headers["X-UI-Inspector-Token"], json.loads(self.rfile.read(int(self.headers["Content-Length"])))))
                png = b"\x89PNG\r\n\x1a\nfixture"
                self.send_response(200)
                self.send_header("Content-Type", "image/png")
                self.send_header("Content-Length", str(len(png)))
                self.end_headers()
                self.wfile.write(png)

            def log_message(self, *args):
                pass

        server = HTTPServer(("127.0.0.1", 0), Handler)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            with tempfile.TemporaryDirectory() as temp:
                discovery = Path(temp) / "bridge.json"
                discovery.write_text(json.dumps({"port": server.server_port, "token": "fixture-secret"}), encoding="utf-8")
                kind, body = cli.invoke(discovery, "apply", {"source": "閉じる", "translation": "关闭"}, "unique-id")
                self.assertEqual(kind, "image/png")
                self.assertEqual(body, b"\x89PNG\r\n\x1a\nfixture")
                self.assertEqual(received, [("/command", "fixture-secret", {"id": "unique-id", "command": "apply", "arguments": {"source": "閉じる", "translation": "关闭"}})])
        finally:
            server.shutdown()
            server.server_close()
            worker.join()

    def test_click_and_scroll_arguments_keep_exact_identity(self):
        p = cli.parser()
        args = p.parse_args(["--game-path", "fixture", "click", "--path", "/Canvas[0]/Button[1]", "--scene", "Home", "--instance-id", "123"])
        self.assertEqual(cli.make_arguments(args, p), {"path": "/Canvas[0]/Button[1]", "scene": "Home", "instanceId": 123})
        args = p.parse_args(["--game-path", "fixture", "scroll", "--xy", "100", "200", "--delta", "-3"])
        self.assertEqual(cli.make_arguments(args, p), {"x": 100.0, "y": 200.0, "delta": -3.0})

    def test_scan_filters_are_per_request(self):
        p = cli.parser()
        args = p.parse_args(["ui", "--canvas", "MenuCanvas", "--include-inactive", "--max-nodes", "20000"])
        self.assertEqual(cli.make_arguments(args, p), {"canvas": "MenuCanvas", "includeInactive": True, "maxNodes": 20000})
        self.assertEqual(cli.make_arguments(p.parse_args(["ui"]), p), {})

    def test_catalog_is_read_only_and_has_local_output(self):
        p = cli.parser()
        args = p.parse_args(["catalog", "--output", "catalog.json"])
        self.assertEqual(cli.make_arguments(args, p), {})
        self.assertEqual(args.output, Path("catalog.json"))

    def test_hover_and_exit_are_separate_from_click(self):
        p = cli.parser()
        args = p.parse_args(["hover", "--path", "/Canvas[0]/Button[1]", "--scene", "Main", "--instance-id", "15"])
        self.assertEqual(args.command, "hover")
        self.assertEqual(cli.make_arguments(args, p), {"path": "/Canvas[0]/Button[1]", "scene": "Main", "instanceId": 15})
        self.assertEqual(cli.make_arguments(p.parse_args(["unhover"]), p), {})

    def test_image_preview_preserves_exact_identity_and_binary_png(self):
        import base64
        p = cli.parser()
        target = ["--scene", "Main", "--path", "/Canvas[0]/Icon[2]", "--instance-id", "15",
                  "--component-index", "1", "--sprite", "icon_home_notice"]
        with tempfile.TemporaryDirectory() as temp:
            png = Path(temp) / "localized.png"
            payload = b"\x89PNG\r\n\x1a\nfixture"
            png.write_bytes(payload)
            args = p.parse_args(["image-apply", *target, "--file", str(png)])
            values = cli.make_arguments(args, p)
            self.assertEqual(base64.b64decode(values.pop("pngBase64")), payload)
            self.assertEqual(values, {"scene": "Main", "path": "/Canvas[0]/Icon[2]", "instanceId": 15,
                                      "componentIndex": 1, "sprite": "icon_home_notice"})
            args = p.parse_args(["image-export", *target, "--output", str(png)])
            self.assertEqual(cli.make_arguments(args, p), values)
            self.assertEqual(cli.make_arguments(p.parse_args(["image-restore"]), p), {})

    def test_image_preview_rejects_non_png_before_transport(self):
        p = cli.parser()
        with tempfile.TemporaryDirectory() as temp:
            file = Path(temp) / "wrong.png"
            file.write_bytes(b"not a PNG")
            args = p.parse_args(["image-apply", "--scene", "Main", "--path", "/Icon[0]",
                                 "--instance-id", "1", "--component-index", "1", "--sprite", "icon",
                                 "--file", str(file)])
            with self.assertRaises(SystemExit):
                cli.make_arguments(args, p)

    def test_spine_read_only_commands_keep_atlas_identity(self):
        p = cli.parser()
        args = p.parse_args(["spine", "--filter", "QuestTopView", "--output", "spine.json"])
        self.assertEqual(cli.make_arguments(args, p), {"filter": "QuestTopView"})
        args = p.parse_args(["spine-export", "--scene", "Main", "--path", "/Card[0]",
                             "--instance-id", "-7", "--texture", "quest_atlas", "--page", "1",
                             "--output", "atlas.png"])
        self.assertEqual(cli.make_arguments(args, p), {"scene": "Main", "path": "/Card[0]",
                                                      "instanceId": -7, "texture": "quest_atlas", "page": 1})

    def test_spine_preview_keeps_identity_and_binary_png(self):
        import base64
        p = cli.parser()
        with tempfile.TemporaryDirectory() as temp:
            file = Path(temp) / "atlas.png"
            payload = b"\x89PNG\r\n\x1a\nfixture"
            file.write_bytes(payload)
            args = p.parse_args(["spine-apply", "--scene", "Main", "--path", "/Card[0]",
                                 "--instance-id", "-7", "--texture", "atlas", "--file", str(file)])
            values = cli.make_arguments(args, p)
            self.assertEqual(base64.b64decode(values.pop("pngBase64")), payload)
            self.assertEqual(values, {"scene": "Main", "path": "/Card[0]", "instanceId": -7,
                                      "texture": "atlas", "page": 0})
            self.assertEqual(cli.make_arguments(p.parse_args(["spine-restore"]), p), {})

    def test_spine_preview_rejects_oversized_png_before_transport(self):
        p = cli.parser()
        with tempfile.TemporaryDirectory() as temp:
            file = Path(temp) / "atlas.png"
            file.write_bytes(b"\x89PNG\r\n\x1a\n" + b"0" * (2 * 1024 * 1024))
            args = p.parse_args(["spine-apply", "--scene", "Main", "--path", "/Card[0]",
                                 "--instance-id", "-7", "--texture", "atlas", "--file", str(file)])
            with self.assertRaises(SystemExit):
                cli.make_arguments(args, p)


if __name__ == "__main__":
    unittest.main()
