"""Генератор временных картинок-заглушек клиента (правило временных ассетов, CLAUDE.md).

Каждая заглушка помечена трижды:
  1) лежит в client/Assets/Art/_TEMP/;
  2) имя файла начинается с TEMP_;
  3) строка в art/manifest.csv со статусом placeholder.
На самой картинке — пурпурный уголок #FF00FF 8 x 8 px в левом верхнем углу.

Размеры — по docs/00_vision.md, 5.1: тайл 128 x 128, персонаж 128 x 192.
Картинки детерминированы (фиксированное зерно): повторный запуск даёт те же байты.

Запуск из корня репозитория:  python tools/make_temp_assets.py
"""

from __future__ import annotations

import csv
import random
import sys
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
TEMP_DIR = ROOT / "client" / "Assets" / "Art" / "_TEMP"
MANIFEST = ROOT / "art" / "manifest.csv"

MARK_COLOR = (255, 0, 255, 255)  # #FF00FF
MARK_SIZE = 8                    # px, левый верхний угол
OWNER = "К"                      # поток «Клиент»


def mark_corner(img: Image.Image) -> None:
    """Пурпурный уголок 8 x 8 px — общий для всех заглушек."""
    ImageDraw.Draw(img).rectangle([0, 0, MARK_SIZE - 1, MARK_SIZE - 1], fill=MARK_COLOR)


def jitter(rng: random.Random, color: tuple, amount: int) -> tuple:
    return tuple(max(0, min(255, c + rng.randint(-amount, amount))) for c in color[:3]) + (255,)


def make_meadow(rng: random.Random) -> Image.Image:
    """Тайл луга: зелёный фон с пятнами и травинками."""
    img = Image.new("RGBA", (128, 128), (86, 140, 62, 255))
    d = ImageDraw.Draw(img)
    for _ in range(60):
        x, y, r = rng.randrange(128), rng.randrange(128), rng.randint(3, 9)
        d.ellipse([x - r, y - r, x + r, y + r], fill=jitter(rng, (86, 140, 62), 14))
    for _ in range(90):
        x, y = rng.randrange(128), rng.randrange(128)
        h = rng.randint(3, 7)
        d.line([x, y, x + rng.randint(-2, 2), y - h], fill=jitter(rng, (120, 178, 80), 18), width=1)
    return img


def make_rock(rng: random.Random) -> Image.Image:
    """Тайл скалы: серый камень с плитами и трещинами."""
    img = Image.new("RGBA", (128, 128), (112, 110, 106, 255))
    d = ImageDraw.Draw(img)
    for _ in range(14):
        x, y = rng.randrange(-16, 128), rng.randrange(-16, 128)
        w, h = rng.randint(24, 56), rng.randint(18, 44)
        d.rectangle([x, y, x + w, y + h], fill=jitter(rng, (118, 116, 112), 16),
                    outline=(78, 76, 72, 255))
    for _ in range(6):
        x, y = rng.randrange(128), rng.randrange(128)
        pts = [(x, y)]
        for _ in range(4):
            x += rng.randint(-14, 14)
            y += rng.randint(-14, 14)
            pts.append((x, y))
        d.line(pts, fill=(58, 56, 54, 255), width=2)
    return img


def make_character(rng: random.Random) -> Image.Image:
    """Персонаж-заглушка 1 x 1,5 тайла: прозрачный фон, силуэт с головой, ноги внизу по центру."""
    img = Image.new("RGBA", (128, 192), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    outline = (30, 24, 40, 255)
    # тень на земле
    d.ellipse([34, 172, 94, 190], fill=(0, 0, 0, 90))
    # ноги
    d.rectangle([48, 130, 60, 182], fill=(60, 50, 90, 255), outline=outline)
    d.rectangle([68, 130, 80, 182], fill=(60, 50, 90, 255), outline=outline)
    # туловище (хитон)
    d.polygon([(40, 140), (88, 140), (80, 76), (48, 76)], fill=(222, 206, 160, 255), outline=outline)
    # руки
    d.rectangle([32, 80, 44, 128], fill=(214, 170, 130, 255), outline=outline)
    d.rectangle([84, 80, 96, 128], fill=(214, 170, 130, 255), outline=outline)
    # голова
    d.ellipse([44, 28, 84, 72], fill=(214, 170, 130, 255), outline=outline)
    d.pieslice([42, 24, 86, 60], 180, 360, fill=(70, 46, 30, 255))
    # лира в руке — опознавательный знак Орфея
    d.arc([90, 96, 118, 132], 200, 340, fill=(200, 160, 40, 255), width=3)
    d.line([96, 104, 96, 126], fill=(200, 160, 40, 255), width=1)
    d.line([104, 100, 104, 126], fill=(200, 160, 40, 255), width=1)
    d.line([112, 104, 112, 126], fill=(200, 160, 40, 255), width=1)
    return img


# имя файла → (генератор, примечание для манифеста)
ASSETS = {
    "TEMP_tile_meadow.png": (make_meadow, "тайл луга 128x128 (00 5.1)"),
    "TEMP_tile_rock.png": (make_rock, "тайл скалы 128x128 (00 5.1)"),
    "TEMP_char_orpheus.png": (make_character, "персонаж-заглушка 128x192, опора внизу по центру (00 5.1)"),
}


def rel(p: Path) -> str:
    return p.relative_to(ROOT).as_posix()


def update_manifest(paths_notes: list[tuple[str, str]]) -> int:
    """Добавляет недостающие строки в art/manifest.csv; существующие не трогает."""
    rows: list[dict] = []
    if MANIFEST.exists():
        with MANIFEST.open(encoding="utf-8", newline="") as f:
            rows = list(csv.DictReader(f))
    known = {r["path"] for r in rows}
    added = 0
    for path, notes in paths_notes:
        if path not in known:
            rows.append({"path": path, "status": "placeholder", "owner": OWNER, "notes": notes})
            added += 1
    MANIFEST.parent.mkdir(parents=True, exist_ok=True)
    with MANIFEST.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=["path", "status", "owner", "notes"], lineterminator="\n")
        w.writeheader()
        w.writerows(rows)
    return added


def main() -> int:
    TEMP_DIR.mkdir(parents=True, exist_ok=True)
    entries = []
    for i, (name, (gen, notes)) in enumerate(ASSETS.items()):
        img = gen(random.Random(20261030 + i))
        mark_corner(img)
        out = TEMP_DIR / name
        img.save(out, format="PNG", optimize=False)
        entries.append((rel(out), notes))
        print(f"{rel(out)}  {img.width}x{img.height}")
    added = update_manifest(entries)
    print(f"готово: {len(entries)} картинок, в манифест добавлено строк: {added}")
    return 0


if __name__ == "__main__":
    # консоль Windows в cp1252 не печатает кириллицу
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.exit(main())
