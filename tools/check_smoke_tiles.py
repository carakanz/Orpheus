"""Сверка тайлов клиента с картинкой gen_preview того же зерна (карточка 0-010, чек-лист: «мир из ядра виден»).

Smoke клиента (`Orpheus.exe --smoke`) пишет в лог строки
    [smoke] sample x=<тайл X> y=<тайл Y> kind=<tree|rock|stone|grass> ...
— тайлы разных видов, которые он нашёл в Tilemap окна и сверил с миром ядра. Этот скрипт берёт те же тайлы
с картинки `tools/gen_preview` (пиксель = тайл, цвета — в tools/gen_preview/Program.cs) и проверяет, что вид совпал.
Так клиент сверяется с независимым инструментом, а не только сам с собой.

Запуск из корня репозитория (после tools/build_client.ps1 -Smoke):
    python tools/check_smoke_tiles.py [--log build/logs/smoke.log] [--size 1024] [--seed 15007751]
Картинка строится вызовом `dotnet run --project tools/gen_preview` в build/gen_preview, если её ещё нет.
Выход 0 — все образцы совпали (и их не меньше 4), 1 — иначе.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent

# Цвета gen_preview (tools/gen_preview/Program.cs): луга, скалы (пол), дерево, скала-объект.
COLORS = {
    (0x6A, 0x9E, 0x45): "grass",
    (0x8C, 0x88, 0x80): "stone",
    (0x1E, 0x4A, 0x1C): "tree",
    (0x45, 0x42, 0x3E): "rock",
}
SAMPLE = re.compile(r"^\[smoke\] sample x=(\d+) y=(\d+) kind=(\w+)")
DEFAULT_SEED = 0x0E50007  # ClientArgs.DefaultSeed и gen_preview по умолчанию


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", default=str(ROOT / "build" / "logs" / "smoke.log"))
    ap.add_argument("--size", type=int, default=1024)
    ap.add_argument("--seed", type=int, default=DEFAULT_SEED)
    a = ap.parse_args()

    samples = []
    with open(a.log, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = SAMPLE.match(line.strip())
            if m:
                samples.append((int(m.group(1)), int(m.group(2)), m.group(3)))
    if len(samples) < 4:
        print(f"образцов в {a.log}: {len(samples)} < 4")
        return 1

    out = ROOT / "build" / "gen_preview"
    png = out / f"map_{a.size}_{a.seed}.png"
    if not png.exists():
        subprocess.run(["dotnet", "run", "-c", "Release", "--project", str(ROOT / "tools" / "gen_preview"), "--",
                        "--size", str(a.size), "--seed", str(a.seed), "--out", str(out)], check=True)

    img = Image.open(png).convert("RGB")
    bad = 0
    for x, y, kind in samples:
        got = COLORS.get(img.getpixel((x, y)), "?")
        ok = got == kind
        bad += 0 if ok else 1
        print(f"  tile ({x}, {y}): клиент {kind}, gen_preview {got}  {'ok' if ok else 'НЕ СОВПАЛО'}")
    print(f"образцов {len(samples)}, расхождений {bad} ({png.relative_to(ROOT).as_posix()})")
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.exit(main())
