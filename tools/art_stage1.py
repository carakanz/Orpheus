"""Набор заглушек этапа 1 (карточка E1-9): заказ картинок на Мак (ComfyUI напрямую) и их приём.

    python tools/art_stage1.py queue <ключ> [<ключ> …] [--raw ПАПКА]   # поставить задания в конец очереди
    python tools/art_stage1.py collect [--raw ПАПКА]                   # скачать готовое в ПАПКА/<ключ>.png
    python tools/art_stage1.py list                                    # ключи набора

Набор — art/style/sets/e19.json: стиль, камеры, негатив и список предметов (ключ, вид, размер, зерно, промпт).
Мак: ComfyUI http://192.168.88.252:8188, Z-Image base (z_image_bf16), 30 шагов, cfg 4 — граф тот же, что у
сервера ассетов (space/docs/MAC_2D_SPEC.md), но со своим размером кадра: мелкие объекты — 768 px, это быстрее.
Задания встают в конец очереди ComfyUI; модели не трогаются (никаких /free).
Сырые картинки и учёт заданий (ledger.json) лежат вне git в папке --raw (по умолчанию build/art_e19/raw).
Обработка сырых картинок в ассеты — tools/art_stage1_build.py.
"""
import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
SET = ROOT / "art" / "style" / "sets" / "e19.json"
COMFY = "http://192.168.88.252:8188"
MAX_QUEUE = 60   # очередь ComfyUI идёт по одному; весь набор ставится сразу (слово Автора 13.11)


def call(path, body=None, timeout=30):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(COMFY + path, data=data, headers={"Content-Type": "application/json"} if data else {})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        raw = r.read()
    return json.loads(raw) if raw[:1] in (b"{", b"[") else raw


def graph(prompt, negative, width, height, seed, prefix):
    return {
        "28": {"class_type": "UNETLoader", "inputs": {"unet_name": "z_image_bf16.safetensors", "weight_dtype": "default"}},
        "30": {"class_type": "CLIPLoader", "inputs": {"clip_name": "qwen_3_4b.safetensors", "type": "lumina2", "device": "default"}},
        "29": {"class_type": "VAELoader", "inputs": {"vae_name": "ae.safetensors"}},
        "11": {"class_type": "ModelSamplingAuraFlow", "inputs": {"model": ["28", 0], "shift": 3.0}},
        "27": {"class_type": "CLIPTextEncode", "inputs": {"text": prompt, "clip": ["30", 0]}},
        "33": {"class_type": "CLIPTextEncode", "inputs": {"text": negative, "clip": ["30", 0]}},
        "13": {"class_type": "EmptySD3LatentImage", "inputs": {"width": width, "height": height, "batch_size": 1}},
        "3": {"class_type": "KSampler", "inputs": {"model": ["11", 0], "seed": seed, "steps": 30, "cfg": 4.0,
                                                   "sampler_name": "res_multistep", "scheduler": "simple",
                                                   "positive": ["27", 0], "negative": ["33", 0],
                                                   "latent_image": ["13", 0], "denoise": 1.0}},
        "8": {"class_type": "VAEDecodeTiled", "inputs": {"samples": ["3", 0], "vae": ["29", 0], "tile_size": 768,
                                                         "overlap": 64, "temporal_size": 64, "temporal_overlap": 8}},
        "9": {"class_type": "SaveImage", "inputs": {"filename_prefix": prefix, "images": ["8", 0]}},
    }


def load_set():
    data = json.loads(SET.read_text(encoding="utf-8"))
    return data, {s["key"]: s for s in data["subjects"]}


def build_prompt(data, s):
    return f"{data['style']}. {s['prompt']}. {data['camera'][s['kind']]}"


def ledger_path(raw):
    return raw / "ledger.json"


def load_ledger(raw):
    p = ledger_path(raw)
    return json.loads(p.read_text(encoding="utf-8")) if p.exists() else {}


def save_ledger(raw, led):
    raw.mkdir(parents=True, exist_ok=True)
    ledger_path(raw).write_text(json.dumps(led, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n")


def queue_len():
    q = call("/queue", timeout=15)
    return len(q.get("queue_running") or []) + len(q.get("queue_pending") or [])


def cmd_queue(keys, raw, force):
    data, subj = load_set()
    led = load_ledger(raw)
    for k in keys:
        if k not in subj:
            raise SystemExit(f"нет ключа {k} в {SET}")
        if not force and ((raw / f"{k}.png").exists() or (k in led and led[k].get("status") == "queued")):
            print(f"{k}: уже есть или в очереди — пропуск")
            continue
        n = queue_len()
        if n >= MAX_QUEUE:
            print(f"{k}: в очереди Мака уже {n} — не ставлю (предел {MAX_QUEUE})")
            continue
        s = subj[k]
        prompt = build_prompt(data, s)
        g = graph(prompt, data["negative"][s["kind"]], s["w"], s["h"], s["seed"], f"orpheus/e19_{k}")
        pid = call("/prompt", {"prompt": g, "client_id": str(uuid.uuid4())})["prompt_id"]
        led[k] = {"pid": pid, "status": "queued", "seed": s["seed"], "w": s["w"], "h": s["h"], "prompt": prompt,
                  "queued": time.strftime("%Y-%m-%d %H:%M:%S")}
        save_ledger(raw, led)
        print(f"{k}: {pid}")


def cmd_collect(raw):
    led = load_ledger(raw)
    left = 0
    for k, j in led.items():
        if j.get("status") != "queued":
            continue
        entry = call(f"/history/{j['pid']}").get(j["pid"])
        if not entry:
            left += 1
            continue
        status = entry.get("status", {})
        if status.get("status_str") == "error":
            j["status"], j["error"] = "error", json.dumps(status.get("messages", [])[-1:], ensure_ascii=False)[:600]
            print(f"{k}: ошибка {j['error']}")
            continue
        images = entry.get("outputs", {}).get("9", {}).get("images")
        if not images:
            left += 1
            continue
        img = images[0]
        q = urllib.parse.urlencode({"filename": img["filename"], "subfolder": img["subfolder"], "type": img["type"]})
        (raw / f"{k}.png").write_bytes(call(f"/view?{q}", timeout=120))
        j["status"], j["done"] = "done", time.strftime("%Y-%m-%d %H:%M:%S")
        print(f"{k}: скачано")
    save_ledger(raw, led)
    print(f"ждут: {left}")
    return left


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cmd", choices=["queue", "collect", "list", "wait"])
    ap.add_argument("keys", nargs="*")
    ap.add_argument("--raw", default=str(ROOT / "build" / "art_e19" / "raw"))
    ap.add_argument("--force", action="store_true", help="перезаказать, даже если картинка уже есть")
    a = ap.parse_args()
    raw = Path(a.raw)
    try:
        if a.cmd == "list":
            data, subj = load_set()
            for k, s in subj.items():
                print(f"{k:<22} {s['kind']:<7} {s['w']}x{s['h']} seed {s['seed']}")
        elif a.cmd == "queue":
            cmd_queue(a.keys, raw, a.force)
        elif a.cmd == "collect":
            cmd_collect(raw)
        else:  # wait: ждать, пока всё поставленное не скачается (для фоновой команды)
            while cmd_collect(raw):
                time.sleep(30)
    except (urllib.error.URLError, OSError) as e:
        raise SystemExit(f"Мак недоступен: {e}")


if __name__ == "__main__":
    main()
