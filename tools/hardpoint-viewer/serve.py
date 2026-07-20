#!/usr/bin/env python3
"""Launcher for the hardpoint viewer — a tiny stdlib-only static server.

Serves the viewer web app from this folder and exposes two extra endpoints so
the page can browse a folder of models:

    GET /list                -> JSON {root, files:[{name, path, size}, ...]}
    GET /asset/<relpath>     -> the bytes of a .glb under the asset root

Run it and a browser opens on the viewer; click a model in the Library, press
"Open .glb…", or drag a .glb onto the view.

    tools/hardpoint-viewer/serve.py                # library = <repo>/pick-assets
    tools/hardpoint-viewer/serve.py path/to/models # library = that folder
    tools/hardpoint-viewer/serve.py --port 8123 --no-open

No dependencies beyond Python 3 stdlib.
"""

import argparse
import http.server
import json
import os
import socketserver
import sys
import threading
import webbrowser
from pathlib import Path
from urllib.parse import unquote

TOOL_DIR = Path(__file__).resolve().parent
REPO_ROOT = TOOL_DIR.parent.parent


def find_free_port(preferred):
    import socket

    for port in [preferred] + list(range(preferred + 1, preferred + 40)):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            try:
                s.bind(("127.0.0.1", port))
                return port
            except OSError:
                continue
    raise SystemExit("No free port near %d" % preferred)


def make_handler(asset_root: Path):
    class Handler(http.server.SimpleHTTPRequestHandler):
        def __init__(self, *a, **k):
            super().__init__(*a, directory=str(TOOL_DIR), **k)

        def log_message(self, fmt, *args):
            pass  # quiet

        def _send_json(self, obj, code=200):
            body = json.dumps(obj).encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            path = self.path.split("?", 1)[0]
            if path == "/list":
                return self._serve_list()
            if path.startswith("/asset/"):
                return self._serve_asset(unquote(path[len("/asset/"):]))
            return super().do_GET()

        def _serve_list(self):
            files = []
            if asset_root.is_dir():
                for p in sorted(asset_root.rglob("*.glb")):
                    if not p.is_file():
                        continue
                    rel = p.relative_to(asset_root).as_posix()
                    files.append({"name": p.name, "path": rel, "size": p.stat().st_size})
            root_label = str(asset_root)
            try:
                root_label = os.path.relpath(asset_root, REPO_ROOT)
            except ValueError:
                pass
            self._send_json({"root": root_label, "files": files})

        def _serve_asset(self, rel):
            # Resolve within the asset root and reject any traversal escape.
            try:
                target = (asset_root / rel).resolve()
                target.relative_to(asset_root.resolve())
            except (ValueError, OSError):
                self.send_error(403, "Forbidden")
                return
            if not target.is_file():
                self.send_error(404, "Not found")
                return
            data = target.read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "model/gltf-binary")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

    return Handler


class ThreadingServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def main(argv):
    ap = argparse.ArgumentParser(description="Interactive GLB hardpoint viewer.")
    ap.add_argument("dir", nargs="?", default=str(REPO_ROOT / "pick-assets"),
                    help="folder of .glb models to list (default: <repo>/pick-assets)")
    ap.add_argument("--port", type=int, default=8777)
    ap.add_argument("--no-open", action="store_true", help="do not open a browser")
    args = ap.parse_args(argv)

    asset_root = Path(args.dir).expanduser().resolve()
    port = find_free_port(args.port)
    url = "http://127.0.0.1:%d/" % port

    httpd = ThreadingServer(("127.0.0.1", port), make_handler(asset_root))
    print("Hardpoint viewer")
    print("  serving app  : %s" % TOOL_DIR)
    print("  model library: %s%s" % (asset_root, "" if asset_root.is_dir() else "  (missing — use Open .glb…)"))
    print("  open          : %s" % url)
    print("  Ctrl-C to stop.")
    if not args.no_open:
        threading.Timer(0.4, lambda: webbrowser.open(url)).start()
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nstopped.")
        httpd.shutdown()


if __name__ == "__main__":
    main(sys.argv[1:])
