"""Пробы стиля на Маке (карточка A-001): наборы концептов через сервер ассетов, учёт, страница сравнения.

    python tools/mac_art.py queue art/style/probes/set1.json [--dry-run]   # поставить недостающие задания
    python tools/mac_art.py status                                          # что поставлено, что готово, очередь Мака
    python tools/mac_art.py collect                                         # скачать готовое, собрать index.html

Сервер ассетов Мака — http://192.168.88.252:8765 (space/docs/MAC_2D_SPEC.md, разд. 1): POST /api/concepts
{prompt, count, seed, style, name, client} → {id, status, picker_url}; GET /api/concepts/{id} → status, images.
Одна клетка набора (вариант стиля × предмет) — одно задание на `count` картинок. Задания встают в конец очереди
сервера: чужие (SpaceLions) не вытесняются. Модели на Маке скрипт не трогает (никаких /free).

Учёт — art/style/probes/ledger.json (в git): id задания, набор, вариант, предмет, промпт, зерно, стиль, статус.
Картинки — art/style/probes/<вариант>/<предмет>_<n>.png и уменьшения в _small/ (в .gitignore: пробы большие).
Страница — art/style/probes/index.html (в git): строки — предметы, столбцы — варианты, под картинкой — 48 и 96 px,
как на экране 1080p и 4K (docs/00_vision.md 5.1); у пола ещё кусок 3 × 3, чтобы видеть шов. Собирается из того,
что уже скачано: недостающие клетки показывают статус задания.
"""
import argparse
import html
import json
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
PROBES = ROOT / "art" / "style" / "probes"
LEDGER = PROBES / "ledger.json"
PAGE = PROBES / "index.html"
ASSETS = "http://192.168.88.252:8765"
CLIENT = "orpheus"
MIN_PER_IMAGE = 10  # Z-Image base, 1024 px (MAC_2D_SPEC, разд. 1)
SMALL = (48, 96)    # 1 тайл на экране 1080p и 4K

try:
    from PIL import Image
except ImportError:  # без Pillow страница уменьшает картинки сама (CSS)
    Image = None


# ---------------------------------------------------------------- сеть и учёт

def call(path, data=None, timeout=30):
    url = path if path.startswith("http") else ASSETS + path
    body = None if data is None else json.dumps(data).encode("utf-8")
    req = urllib.request.Request(url, body, {"Content-Type": "application/json"} if body else {})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        raw = r.read()
    return raw if path.endswith(".png") else json.loads(raw)


def load():
    return json.loads(LEDGER.read_text(encoding="utf-8")) if LEDGER.exists() else []


def save(jobs):
    LEDGER.parent.mkdir(parents=True, exist_ok=True)
    LEDGER.write_text(json.dumps(jobs, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def load_set(path):
    p = Path(path)
    if not p.is_absolute() and not p.exists():
        p = ROOT / p
    data = json.loads(p.read_text(encoding="utf-8"))
    data["_file"] = p.resolve().relative_to(ROOT).as_posix()
    return data


def build_prompt(data, variant, subject):
    camera = data["common"]["camera_floor" if subject["kind"] == "floor" else "camera_object"]
    return f"{variant['prompt']}. {subject['prompt']}. {camera}"


def server_queue():
    """Очередь сервера ассетов: (идёт, ждут) — или None, если сервер не ответил."""
    try:
        q = call("/api/queue", timeout=15)
    except (urllib.error.URLError, OSError):
        return None
    return q.get("running"), q.get("pending") or []


def refresh(job):
    """Подтянуть статус задания с сервера; False — сервер недоступен."""
    try:
        s = call(f"/api/concepts/{job['id']}", timeout=20)
    except urllib.error.HTTPError as e:
        if e.code == 404:
            job["status"], job["error"] = "error", "набора нет на сервере (404)"
            return True
        return False
    except (urllib.error.URLError, OSError):
        return False
    job["status"] = s.get("status") or job.get("status")
    job["images"] = s.get("images") or []
    if s.get("error"):
        job["error"] = s["error"]
    job["checked"] = time.strftime("%Y-%m-%d %H:%M")
    return True


def live(job):
    return job.get("status") != "error"


# ---------------------------------------------------------------- команды

def cmd_queue(set_path, dry_run):
    data = load_set(set_path)
    jobs = load()
    have = {(j["set"], j["variant"], j["subject"]) for j in jobs if live(j)}
    todo = [(v, s) for s in data["subjects"] for v in data["variants"]
            if (data["set"], v["key"], s["key"]) not in have]
    if not todo:
        print("всё уже поставлено:", data["set"])
        return 0
    if dry_run:
        for v, s in todo:
            print(f"--- {v['key']}/{s['key']} seed {s['seed']}\n{build_prompt(data, v, s)}\n")
        print(f"заданий {len(todo)}, картинок {len(todo) * data['count']} (не поставлено: --dry-run)")
        return 0
    q = server_queue()
    if q is None:
        raise SystemExit(f"Мак недоступен: {ASSETS}/api/queue не отвечает")
    running, pending = q
    ahead = (1 if running else 0) + len(pending)
    if ahead:
        print(f"на сервере уже {ahead} чужих заданий — ставлю в конец, не вытесняя")
    n = 0
    for v, s in todo:
        prompt = build_prompt(data, v, s)
        body = {"prompt": prompt, "count": data["count"], "seed": s["seed"], "style": data["style"],
                "name": f"{data['set']}_{v['key']}_{s['key']}", "client": CLIENT}
        try:
            r = call("/api/concepts", body)
        except urllib.error.HTTPError as e:
            raise SystemExit(f"сервер отказал ({v['key']}/{s['key']}): HTTP {e.code} "
                             f"{e.read().decode('utf-8', 'replace')[:500]}")
        except (urllib.error.URLError, OSError) as e:
            raise SystemExit(f"сервер недоступен ({v['key']}/{s['key']}): {e}")
        if not isinstance(r, dict) or not r.get("id"):
            raise SystemExit(f"сервер ответил без id ({v['key']}/{s['key']}): {r!r:.500}")
        jobs.append({"id": r["id"], "set": data["set"], "set_file": data["_file"], "variant": v["key"],
                     "subject": s["key"], "seed": s["seed"], "count": data["count"], "style": data["style"],
                     "prompt": prompt, "status": r.get("status", "queued"), "picker_url": r.get("picker_url"),
                     "queued": time.strftime("%Y-%m-%d %H:%M")})
        save(jobs)  # после каждого: обрыв посередине не даст дублей при повторе
        n += 1
        print(f"{v['key']}/{s['key']}: {r['id']}", flush=True)
    imgs = n * data["count"] + ahead * 4  # чужое — примерно по 4 картинки
    print(f"поставлено заданий: {n}, картинок: {n * data['count']}; "
          f"ориентировочно готово через ~{imgs * MIN_PER_IMAGE / 60:.1f} ч")
    return n


def cmd_status():
    jobs = load()
    q = server_queue()
    if q is None:
        print("Мак недоступен — показываю учёт без обновления")
    else:
        running, pending = q
        rid = running.get("id") if isinstance(running, dict) else running
        print(f"сервер ассетов: идёт {rid or '—'}, ждут {len(pending)}")
        for j in jobs:
            if live(j) and j.get("status") != "done":
                refresh(j)
        save(jobs)
    by = {}
    for j in jobs:
        by[j.get("status")] = by.get(j.get("status"), 0) + 1
        err = f"  ({j['error'][:80]})" if j.get("status") == "error" and j.get("error") else ""
        print(f"{j['set']} {j['variant']:<13} {j['subject']:<8} {j['id']}  {j.get('status')}{err}")
    left = sum(j["count"] for j in jobs if j.get("status") in ("queued", "running"))
    print("итого:", ", ".join(f"{k} {v}" for k, v in sorted(by.items(), key=lambda kv: str(kv[0]))) or "пусто",
          f"| осталось картинок {left} (~{left * MIN_PER_IMAGE / 60:.1f} ч)")


def small(src, px, tile=False):
    """Уменьшение до px (у пола — кусок 3 × 3 по px) в _small/; None — нет Pillow."""
    if Image is None:
        return None
    rel = src.relative_to(PROBES)
    dst = PROBES / "_small" / rel.parent / f"{src.stem}_{px}{'_tile' if tile else ''}.png"
    if not dst.exists() or dst.stat().st_mtime < src.stat().st_mtime:
        dst.parent.mkdir(parents=True, exist_ok=True)
        im = Image.open(src).convert("RGB").resize((px, px), Image.LANCZOS)
        if tile:
            sheet = Image.new("RGB", (px * 3, px * 3))
            for y in range(3):
                for x in range(3):
                    sheet.paste(im, (x * px, y * px))
            im = sheet
        im.save(dst)
    return dst.relative_to(PROBES).as_posix()


def cmd_collect():
    jobs = load()
    online = server_queue() is not None
    got = 0
    for j in jobs:
        if online and live(j) and j.get("status") != "done":
            refresh(j)
        if j.get("status") != "done":
            continue
        out = PROBES / j["variant"]
        out.mkdir(parents=True, exist_ok=True)
        for n, url in enumerate(j.get("images") or []):
            path = out / f"{j['subject']}_{n}.png"
            if path.exists():
                continue
            if not online:
                break
            try:
                path.write_bytes(call(url, timeout=120))
                got += 1
            except (urllib.error.URLError, OSError) as e:
                print(f"не скачалось {url}: {e}")
    save(jobs)
    build_page(jobs)
    print(f"скачано новых картинок: {got}; страница: {PAGE}" + ("" if online else " (Мак недоступен)"))


# ---------------------------------------------------------------- страница

CSS = """
:root { --bg:#f4f1ea; --fg:#2b2620; --muted:#7a7166; --cell:#fffdf8; --line:#d9d1c3; --grass:#6f8f4e; }
@media (prefers-color-scheme: dark) { :root { --bg:#1d1b18; --fg:#ece6da; --muted:#a39a8c; --cell:#27241f; --line:#3d3830; } }
* { box-sizing:border-box; }
body { margin:0; padding:16px; background:var(--bg); color:var(--fg); font:14px/1.4 system-ui, sans-serif; }
h1 { font-size:20px; margin:0 0 4px; } p.meta { color:var(--muted); margin:0 0 16px; }
.wrap { overflow-x:auto; }
table { border-collapse:collapse; min-width:900px; }
th, td { border:1px solid var(--line); padding:8px; vertical-align:top; background:var(--cell); }
th { text-align:left; } th small { display:block; font-weight:normal; color:var(--muted); }
.pics { display:flex; gap:10px; flex-wrap:wrap; }
figure { margin:0; } figure > a > img.big { width:280px; height:280px; object-fit:contain; display:block; background:#ddd; }
.sm { display:flex; gap:8px; align-items:flex-end; margin-top:6px; }
.sm span { display:flex; flex-direction:column; align-items:center; font-size:11px; color:var(--muted); }
.sm .on { padding:4px; background:var(--grass); }
.css48 { width:48px; height:48px; } .css96 { width:96px; height:96px; }
.wait { width:280px; height:120px; display:flex; align-items:center; justify-content:center; color:var(--muted);
        border:1px dashed var(--line); }
details { margin-top:6px; max-width:580px; color:var(--muted); font-size:12px; }
"""


def thumb_html(path, kind):
    rel = path.relative_to(PROBES).as_posix()
    parts = []
    for px in SMALL:
        s = small(path, px)
        img = (f'<img src="{s}" width="{px}" height="{px}">' if s
               else f'<img src="{rel}" class="css{px}">')
        parts.append(f'<span><span class="on">{img}</span>{px} px</span>')
    if kind == "floor":
        s = small(path, SMALL[0], tile=True)
        if s:
            parts.append(f'<span><img src="{s}">шов 3×3, 48 px</span>')
    return (f'<figure><a href="{rel}"><img class="big" src="{rel}"></a>'
            f'<div class="sm">{"".join(parts)}</div></figure>')


def build_page(jobs):
    # Наборы — из учёта и все set*.json рядом: страница собирается и до первого задания
    files = {j.get("set_file") for j in jobs if j.get("set_file")}
    files |= {p.relative_to(ROOT).as_posix() for p in PROBES.glob("set*.json")}
    sets, variants, subjects = [], [], []
    for f in sorted(files):
        if not (ROOT / f).exists():
            continue
        d = json.loads((ROOT / f).read_text(encoding="utf-8"))
        sets.append((d["set"], f))
        variants += [v for v in d["variants"] if v["key"] not in {x["key"] for x in variants}]
        subjects += [s for s in d["subjects"] if s["key"] not in {x["key"] for x in subjects}]
    cell = {}
    for j in jobs:  # живое задание клетки главнее упавшего
        k = (j["variant"], j["subject"])
        if k not in cell or (live(j) and not live(cell[k])):
            cell[k] = j
    total = sum(j["count"] for j in cell.values() if live(j))
    have = 0
    rows = []
    head = "".join(f'<th>{html.escape(v["title"])}<small>{html.escape(v.get("note", ""))}</small></th>'
                   for v in variants)
    for s in subjects:
        tds = []
        for v in variants:
            j = cell.get((v["key"], s["key"]))
            files = sorted((PROBES / v["key"]).glob(f"{s['key']}_[0-9].png"))
            have += len(files)
            pics = [thumb_html(p, s["kind"]) for p in files]
            if not files:
                st = j.get("status", "нет задания") if j else "нет задания"
                pics.append(f'<div class="wait">{html.escape(st)}</div>')
            info = ""
            if j:
                info = (f'<details><summary>id {j["id"]}, seed {j["seed"]}</summary>'
                        f'{html.escape(j["prompt"])}</details>')
            tds.append(f'<td><div class="pics">{"".join(pics)}</div>{info}</td>')
        rows.append(f'<tr><th>{html.escape(s["title"])}</th>{"".join(tds)}</tr>')
    body = (f'<h1>Пробы стиля Orpheus</h1><p class="meta">Наборы: {", ".join(html.escape(s) for s, _ in sets) or "—"}'
            f' · готово картинок {have} из {total} · собрано {time.strftime("%Y-%m-%d %H:%M")}'
            f' · под картинкой — 48 и 96 px (тайл на экране 1080p и 4K) на траве</p>'
            f'<div class="wrap"><table><tr><th></th>{head}</tr>{"".join(rows)}</table></div>')
    page = (f'<!doctype html><html lang="ru"><head><meta charset="utf-8">'
            f'<meta name="viewport" content="width=device-width, initial-scale=1">'
            f'<title>Пробы стиля Orpheus</title><style>{CSS}</style></head><body>{body}</body></html>\n')
    PAGE.write_text(page, encoding="utf-8", newline="\n")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    q = sub.add_parser("queue", help="поставить недостающие задания набора")
    q.add_argument("set", help="файл набора, например art/style/probes/set1.json")
    q.add_argument("--dry-run", action="store_true", help="только показать промпты")
    sub.add_parser("status", help="статус заданий и очереди Мака")
    sub.add_parser("collect", help="скачать готовое и собрать страницу сравнения")
    a = ap.parse_args()
    if a.cmd == "queue":
        cmd_queue(a.set, a.dry_run)
    elif a.cmd == "status":
        cmd_status()
    else:
        cmd_collect()


if __name__ == "__main__":
    main()
