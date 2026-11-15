"""Атлас Орфея — сервер мини-вики.

Отдаёт статику из своей папки и хранит отметки Автора:
  GET  /api/notes  -> {id: запись}
  POST /api/notes  -> тело {id, status?, comment?, title?, kind?}; поля сливаются в запись, ответ — сохранённая запись.
Файл отметок пишется атомарно (временный файл + fsync + os.replace): у Автора нет ИБП.
Только стандартная библиотека.

Запуск: python server.py [--host 127.0.0.1] [--port 8095] [--data ПАПКА]
"""
import argparse
import json
import os
import sys
import tempfile
import threading
from datetime import datetime, timezone
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

ROOT = os.path.dirname(os.path.abspath(__file__))
LOCK = threading.Lock()
NOTES_PATH = os.path.join(ROOT, "notes.json")
FIELDS = ("status", "comment", "title", "kind")
MAX_BODY = 256 * 1024


def load_notes():
    try:
        with open(NOTES_PATH, encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except FileNotFoundError:
        return {}


def save_notes(data):
    folder = os.path.dirname(NOTES_PATH)
    fd, tmp = tempfile.mkstemp(prefix=".notes-", suffix=".tmp", dir=folder)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
            json.dump(data, f, ensure_ascii=False, indent=1, sort_keys=True)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, NOTES_PATH)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise
    if hasattr(os, "O_DIRECTORY"):  # Linux/Docker: запись о переименовании тоже на диск; на Windows так нельзя
        dfd = os.open(folder, os.O_DIRECTORY)
        try:
            os.fsync(dfd)
        finally:
            os.close(dfd)


class Handler(SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=ROOT, **kw)

    def _json(self, code, obj):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path.rstrip("/").endswith("/api/notes") or path == "/api/notes":
            with LOCK:
                return self._json(200, load_notes())
        name = os.path.basename(path)
        if name in ("server.py", "notes.json") or name.startswith(".notes-"):
            return self._json(404, {"error": "not found"})
        return super().do_GET()

    def do_POST(self):
        path = self.path.split("?", 1)[0]
        if not (path == "/api/notes" or path.rstrip("/").endswith("/api/notes")):
            return self._json(404, {"error": "not found"})
        try:
            n = int(self.headers.get("Content-Length") or 0)
            if n <= 0 or n > MAX_BODY:
                return self._json(413, {"error": "bad size"})
            req = json.loads(self.rfile.read(n).decode("utf-8"))
            key = req.get("id")
            if not isinstance(key, str) or not key or len(key) > 200:
                return self._json(400, {"error": "id"})
        except (ValueError, UnicodeDecodeError):
            return self._json(400, {"error": "json"})
        with LOCK:
            data = load_notes()
            rec = dict(data.get(key) or {})
            for f in FIELDS:
                if f in req and isinstance(req[f], str):
                    rec[f] = req[f][:20000]
            rec["updatedAt"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
            data[key] = rec
            save_notes(data)
        return self._json(200, rec)

    def log_message(self, fmt, *args):
        if "/api/notes" in (self.path or "") and self.command == "POST":
            super().log_message(fmt, *args)


def main():
    global NOTES_PATH
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8095)
    ap.add_argument("--data", default=ROOT, help="папка для notes.json")
    a = ap.parse_args()
    for s in (sys.stdout, sys.stderr):  # Windows без консоли — cp1252, русский вывод уронил бы сервер
        try:
            s.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    os.makedirs(a.data, exist_ok=True)
    NOTES_PATH = os.path.join(a.data, "notes.json")
    srv = ThreadingHTTPServer((a.host, a.port), Handler)
    print(f"Атлас Орфея: http://{a.host}:{a.port}/  отметки: {NOTES_PATH}", flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()
