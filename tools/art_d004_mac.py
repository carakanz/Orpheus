"""D-004 шаг 1а — дракон: заказ картинок на Мак (ComfyUI, Z-Image base) и приём.

    python tools/art_d004_mac.py queue concept            # концепты дракона txt2img (side/front/back)
    python tools/art_d004_mac.py queue paint              # img2img листов частей (build/art_d004/sheet_*.png) — «прокраска»
    python tools/art_d004_mac.py collect                  # скачать готовое в build/art_d004/raw/<ключ>.png
    python tools/art_d004_mac.py status

Граф и модель — как tools/art_a006.py (Z-Image base, 30 шагов, cfg 4; img2img = LoadImage → VAEEncode → KSampler
с denoise < 1). Сырые картинки и учёт — вне git (build/art_d004/raw).
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.parse
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import art_a006 as a6  # noqa: E402  (call, upload, graph)

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
BUILD = ROOT / "build" / "art_d004"
RAW = BUILD / "raw"

STYLE = ("detailed 2D fantasy game creature art for a top-down action game, seen from above at a small tilt like Factorio "
         "and RimWorld, the camera looks down at about 50 degrees so we see the top of the back and the side, precise "
         "accurate shapes and realistic proportions, instantly readable silhouette, rich saturated vivid fantasy colors "
         "like Terraria, one key light from the upper left, a thin crisp dark plum-brown outline around the silhouette, "
         "clean digital painting, fairy-tale ancient Greek myth setting")
DRAGON = ("a single fairy-tale dragon, emerald green scales with bronze and gold belly plates and bronze horns, bronze "
          "spines along the back, four strong legs with claws, two large bat-like wings with bronze wing bones and green "
          "membranes, long neck, long tail with a spade tip, amber eyes")
CAM = ("the whole creature visible with empty margin around it, isolated on a plain flat light neutral grey background, "
       "uniform background without gradient, only a small soft contact shadow, no scenery, no text, no letters, "
       "no watermark, no border")
NEG = ("several creatures, duplicate, multiple views, character sheet, text, letters, watermark, frame, border, cropped, "
       "scenery, landscape, blurry, chibi, cartoon, anime, photo, 3d render, low detail, background gradient, vignette, "
       "folded wings, wings overlapping body, extra legs, missing legs, extra heads")

VIEWS = {
    "side": "full body side view facing right, neutral pose for animation: wings spread wide open up and out, legs set "
            "apart, neck stretched forward, tail stretched straight back, nothing overlapping",
    "front": "full body front view, the dragon walks toward the viewer, head at the front looking at the viewer, wings "
             "spread wide open to the left and right, front legs apart, neutral symmetrical pose for animation",
    "back": "full body back view, the dragon walks away from the viewer, we see its back, the spine ridge and the tail "
            "coming toward us, wings spread wide open to the left and right, neutral symmetrical pose for animation",
}

PAINT = ("a game sprite sheet of separate body parts of one fairy-tale dragon laid out apart on a flat green background, "
         "each part a separate isolated piece, emerald green scales, bronze and gold belly plates, bronze horns, spines and "
         "claws, bronze wing bones and green leathery membranes, fine scale texture, crisp thin dark plum-brown outline "
         "around every piece, rich saturated vivid fantasy colors like Terraria, light from the upper left, clean digital "
         "painting, precise shapes")
PAINT_NEG = ("text, letters, numbers, watermark, frame, grid lines, merged pieces, extra pieces, blurry, photo, 3d render, "
             "background gradient, green tint on the parts, cast shadow")


def jobs():
    J = []
    for v, txt in VIEWS.items():
        for i, seed in enumerate((4101, 4102, 4103)):
            J.append(dict(key=f"concept_{v}_{i + 1}", group="concept", prompt=f"{STYLE}, {DRAGON}, {txt}, {CAM}",
                          neg=NEG, w=1152, h=896, seed=seed + 10 * list(VIEWS).index(v)))
    for p in sorted(BUILD.glob("sheet_*.png")):
        for dn in (0.38, 0.5):
            J.append(dict(key=f"paint_{p.stem[6:]}_{int(dn * 100)}", group="paint", prompt=PAINT, neg=PAINT_NEG,
                          w=0, h=0, seed=5150, sketch=str(p), denoise=dn))
    return J


def load_ledger():
    p = RAW / "ledger.json"
    return json.loads(p.read_text(encoding="utf-8")) if p.exists() else {}


def save_ledger(led):
    RAW.mkdir(parents=True, exist_ok=True)
    tmp = RAW / "ledger.tmp"
    tmp.write_text(json.dumps(led, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n")
    tmp.replace(RAW / "ledger.json")


def cmd_queue(groups, keys):
    led = load_ledger()
    for j in jobs():
        k = j["key"]
        if groups and j["group"] not in groups and k not in keys:
            continue
        if (RAW / f"{k}.png").exists() or led.get(k, {}).get("status") == "queued":  # lost — перепоставить
            print(f"{k}: уже есть или в очереди")
            continue
        img = None
        if j.get("sketch"):
            img = a6.upload(Path(j["sketch"]))
        g = a6.graph({**j, "w": j["w"] or 1024, "h": j["h"] or 1024}, img)
        g["9"]["inputs"]["filename_prefix"] = f"orpheus/d004_{k}"
        pid = a6.call("/prompt", {"prompt": g, "client_id": str(uuid.uuid4())})["prompt_id"]
        led[k] = {**{x: j.get(x) for x in ("group", "seed", "w", "h", "prompt", "neg", "sketch", "denoise")},
                  "pid": pid, "status": "queued", "queued": time.strftime("%Y-%m-%d %H:%M:%S")}
        save_ledger(led)
        print(f"{k}: {pid}", flush=True)


def cmd_collect():
    led = load_ledger()
    left = 0
    for k, j in led.items():
        if j.get("status") != "queued":
            continue
        entry = a6.call(f"/history/{j['pid']}").get(j["pid"])
        if not entry:
            left += 1
            continue
        st = entry.get("status", {})
        if st.get("status_str") == "error":
            j["status"] = "error"
            j["error"] = json.dumps(st.get("messages", [])[-1:], ensure_ascii=False)[:600]
            print(f"{k}: ошибка {j['error']}")
            continue
        images = entry.get("outputs", {}).get("9", {}).get("images")
        if not images:
            left += 1
            continue
        img = images[0]
        q = urllib.parse.urlencode({"filename": img["filename"], "subfolder": img["subfolder"], "type": img["type"]})
        (RAW / f"{k}.png").write_bytes(a6.call(f"/view?{q}", timeout=120))
        j["status"], j["done"] = "done", time.strftime("%Y-%m-%d %H:%M:%S")
        print(f"{k}: скачано", flush=True)
    save_ledger(led)
    return left


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cmd", choices=["queue", "collect", "status"])
    ap.add_argument("what", nargs="*")
    a = ap.parse_args()
    if a.cmd == "queue":
        groups = [w for w in a.what if w in ("concept", "paint")]
        cmd_queue(groups, a.what)
    elif a.cmd == "collect":
        print("ждут:", cmd_collect())
    else:
        for k, j in load_ledger().items():
            print(k, j.get("status"), j.get("denoise") or "")


if __name__ == "__main__":
    main()
