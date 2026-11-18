"""Ночная пачка концептов кандидатов из бестиария (D-001) на Маке, ComfyUI (Z-Image base).

Порядок: кандидаты с отметкой Автора «принято» в вики → боссы → элита → ярус 3 → остальные
(по одному на биом и архетип, для разнообразия). Промпты — из tagged.json / tagged_aon.json (Qwen),
стиль и камера — как у концептов врагов A-006. Картинки 768 px (~4–5 мин на Маке).

  python tools/art_bestiary.py queue --limit 110     # переключить Мак на comfy вручную или ключом --switch
  python tools/art_bestiary.py collect               # забрать готовые в work/art/bestiary/
  python tools/art_bestiary.py wait --every 600 --hours 12
  python tools/art_bestiary.py sheet                 # лист work/screens/D-001_concepts_<n>.png

Леджер: work/art/bestiary/ledger.json (атомарная запись).
"""
import argparse, json, os, sys, time, urllib.error, urllib.parse, urllib.request, uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import art_a006 as A  # noqa: E402  (STYLE_CHAR, CAM_CHAR, NEG_CHAR, call, graph)

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "work" / "art" / "bestiary"
SRC = [ROOT / "work/design/bestiary/tagged.json", ROOT / "work/design/bestiary/tagged_aon.json"]
NOTES = Path(r"D:\orpheus-wiki\notes.json")
SWITCH = "http://192.168.88.252:8090"
PX = 768


def load_items():
    items = []
    for p in SRC:
        d = json.loads(p.read_text(encoding="utf-8"))
        src = "aon" if "aon" in p.name else "dnd"
        raw = d.get("items", [])
        seq = ([{**v, "id": v.get("id", k)} for k, v in raw.items()] if isinstance(raw, dict) else raw)
        for it in seq:
            if isinstance(it, dict) and it.get("image_prompt"):
                items.append({**it, "src": src})
    return items


def accepted_ids():
    try:
        n = json.loads(NOTES.read_text(encoding="utf-8"))
    except OSError:
        return set()
    return {k.split("-", 1)[1] for k, v in n.items() if v.get("status") == "accepted" and "-" in k}


def ordered(items):
    acc = accepted_ids()
    tier_rank = {"boss": 0, "elite": 1, 3: 2, "3": 2, 2: 3, "2": 3, 1: 4, "1": 4}
    first = [i for i in items if i["id"] in acc]
    rest = [i for i in items if i["id"] not in acc]
    rest.sort(key=lambda i: (tier_rank.get(i.get("tier"), 5), i["id"]))
    # разнообразие: сначала по одному на (биом, архетип), потом остальные
    seen, a, b = set(), [], []
    for i in rest:
        key = ((i.get("biomes") or ["?"])[0], i.get("archetype"))
        (b if key in seen else a).append(i)
        seen.add(key)
    return first + a + b


def job(it, seed):
    subj = it["image_prompt"]
    return dict(key=f"{it['src']}_{it['id']}"[:80], prompt=f"{A.STYLE_CHAR}. {subj}. {A.CAM_CHAR}",
                neg=A.NEG_CHAR, w=PX, h=PX, seed=seed)


def ledger():
    p = OUT / "ledger.json"
    return json.loads(p.read_text(encoding="utf-8")) if p.exists() else {}


def save(led):
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = OUT / "ledger.tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(led, ensure_ascii=False, indent=1) + "\n")
        f.flush(); os.fsync(f.fileno())
    tmp.replace(OUT / "ledger.json")


def switch(target):
    key = os.environ.get("MAC_AI_KEY", "")
    body = json.dumps({"target": target, "force": False, "wait": True}).encode()
    req = urllib.request.Request(SWITCH + "/api/switch", data=body,
                                 headers={"Content-Type": "application/json", "Authorization": f"Bearer {key}", "X-API-Key": key})
    with urllib.request.urlopen(req, timeout=180) as r:
        print("switch:", r.status, r.read()[:200].decode("utf-8", "replace"))


def cmd_queue(limit):
    led = ledger()
    n = 0
    for k, it in enumerate(ordered(load_items())):
        j = job(it, 5100 + k)
        if j["key"] in led:
            continue
        g = A.graph(j)
        g["9"]["inputs"]["filename_prefix"] = f"orpheus/best_{j['key']}"
        pid = A.call("/prompt", {"prompt": g, "client_id": str(uuid.uuid4())})["prompt_id"]
        led[j["key"]] = {"id": it["id"], "src": it["src"], "tier": it.get("tier"), "act": it.get("act"),
                         "name": it.get("name_ru") or it["id"], "seed": j["seed"], "prompt": j["prompt"],
                         "pid": pid, "status": "queued", "queued": time.strftime("%Y-%m-%d %H:%M:%S")}
        save(led)
        n += 1
        print(f"{j['key']}: {pid}", flush=True)
        if n >= limit:
            break
    print(f"поставлено {n}")


def cmd_collect():
    led = ledger(); left = 0
    for k, j in led.items():
        if j.get("status") != "queued":
            continue
        e = A.call(f"/history/{j['pid']}").get(j["pid"])
        if not e:
            left += 1; continue
        imgs = e.get("outputs", {}).get("9", {}).get("images")
        if e.get("status", {}).get("status_str") == "error":
            j["status"] = "error"; continue
        if not imgs:
            left += 1; continue
        im = imgs[0]
        q = urllib.parse.urlencode({"filename": im["filename"], "subfolder": im["subfolder"], "type": im["type"]})
        OUT.mkdir(parents=True, exist_ok=True)
        (OUT / f"{k}.png").write_bytes(A.call(f"/view?{q}", timeout=120))
        j["status"], j["done"] = "done", time.strftime("%Y-%m-%d %H:%M:%S")
        print(f"{k}: скачано", flush=True)
    save(led)
    return left


def cmd_wait(every, hours, back_to_llm):
    end = time.time() + hours * 3600
    while True:
        try:
            left = cmd_collect()
        except (urllib.error.URLError, OSError) as e:
            print(time.strftime("%H:%M"), "Мак не ответил:", e, flush=True); left = -1
        done = sum(1 for j in ledger().values() if j.get("status") == "done")
        print(time.strftime("%H:%M"), f"готово {done}, ждут {left}", flush=True)
        if left == 0:
            if back_to_llm:
                try:
                    switch("llm")
                except Exception as e:  # noqa: BLE001
                    print("не переключил на llm:", e)
            return 0
        if time.time() > end:
            return 1
        time.sleep(every)


def cmd_sheet():
    from PIL import Image, ImageDraw, ImageFont
    led = ledger()
    done = [(k, j) for k, j in led.items() if j.get("status") == "done" and (OUT / f"{k}.png").exists()]
    cell, cols = 220, 8
    font = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 15)
    for page in range(0, len(done), 48):
        chunk = done[page:page + 48]
        rows = (len(chunk) + cols - 1) // cols
        sheet = Image.new("RGB", (cols * cell, rows * (cell + 24)), (236, 228, 210))
        d = ImageDraw.Draw(sheet)
        for i, (k, j) in enumerate(chunk):
            im = Image.open(OUT / f"{k}.png").convert("RGB").resize((cell - 10, cell - 10))
            x, y = (i % cols) * cell, (i // cols) * (cell + 24)
            sheet.paste(im, (x + 5, y + 5))
            d.text((x + 6, y + cell - 2), f"{j['name']} · {j.get('tier')}"[:30], fill=(40, 30, 20), font=font)
        p = ROOT / "work" / "screens" / f"D-001_concepts_{page // 48 + 1}.png"
        sheet.save(p)
        print(p)


def main():
    ap = argparse.ArgumentParser()
    sp = ap.add_subparsers(dest="cmd", required=True)
    q = sp.add_parser("queue"); q.add_argument("--limit", type=int, default=110); q.add_argument("--switch", action="store_true")
    sp.add_parser("collect")
    w = sp.add_parser("wait"); w.add_argument("--every", type=int, default=600); w.add_argument("--hours", type=float, default=12)
    w.add_argument("--back-to-llm", action="store_true")
    sp.add_parser("sheet")
    a = ap.parse_args()
    if a.cmd == "queue":
        if a.switch:
            switch("comfy")
        cmd_queue(a.limit)
    elif a.cmd == "collect":
        print("ждут:", cmd_collect())
    elif a.cmd == "wait":
        sys.exit(cmd_wait(a.every, a.hours, a.back_to_llm))
    else:
        cmd_sheet()


if __name__ == "__main__":
    main()
