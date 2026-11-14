"""Временные значки предметов на земле (E2-1, шаг 2.1): 64 x 64, «тёмный контур, плоский цвет».

Правило заглушек (CLAUDE.md): файл в client/Assets/Art/_TEMP/, имя TEMP_item_<ключ>.png, строка в
art/manifest.csv со статусом placeholder, пурпурный уголок #FF00FF 8 x 8 px в левом верхнем углу.
Ключ — ItemDef.Key из src/Orpheus.Core/Items/ItemTable.cs; item_generic — мешочек для предметов без своего значка.
Картинки детерминированы: повторный запуск даёт те же байты.

Запуск из корня репозитория:  python tools/make_item_icons.py
"""

from __future__ import annotations

import csv
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
TEMP_DIR = ROOT / "client" / "Assets" / "Art" / "_TEMP"
MANIFEST = ROOT / "art" / "manifest.csv"

S = 4                      # рисуем в 4 раза крупнее и уменьшаем — ровные края
OUT = (38, 28, 24, 255)    # тёмный контур
W = 3 * S                  # толщина контура


def canvas() -> tuple[Image.Image, ImageDraw.ImageDraw]:
    img = Image.new("RGBA", (64 * S, 64 * S), (0, 0, 0, 0))
    return img, ImageDraw.Draw(img)


def p(*xy: float) -> list[tuple[float, float]]:
    return [(xy[i] * S, xy[i + 1] * S) for i in range(0, len(xy), 2)]


def poly(d: ImageDraw.ImageDraw, pts, fill) -> None:
    d.polygon(pts, fill=fill)
    d.line(pts + [pts[0]], fill=OUT, width=W, joint="curve")


def ell(d: ImageDraw.ImageDraw, box, fill) -> None:
    x0, y0, x1, y1 = [v * S for v in box]
    d.ellipse([x0, y0, x1, y1], fill=fill, outline=OUT, width=W)


def wood() -> Image.Image:
    img, d = canvas()
    for (x, y) in ((8, 36), (26, 36), (17, 20)):  # три бревна пирамидой
        d.rounded_rectangle([x * S, y * S, (x + 30) * S, (y + 18) * S], radius=9 * S, fill=(150, 100, 58, 255), outline=OUT, width=W)
        ell(d, (x + 16, y + 1, x + 30, y + 17), (214, 170, 110, 255))
        ell(d, (x + 20, y + 6, x + 26, y + 12), (176, 128, 78, 255))
    return img


def stone() -> Image.Image:
    img, d = canvas()
    poly(d, p(8, 50, 12, 32, 26, 24, 38, 30, 40, 50), (138, 140, 146, 255))
    poly(d, p(30, 54, 34, 38, 46, 32, 56, 40, 56, 54), (164, 166, 172, 255))
    d.line(p(16, 36, 26, 30), fill=(196, 198, 204, 255), width=2 * S)
    d.line(p(38, 42, 46, 38), fill=(212, 214, 220, 255), width=2 * S)
    return img


def copper_ore() -> Image.Image:
    img, d = canvas()
    poly(d, p(8, 52, 12, 30, 28, 20, 46, 24, 56, 40, 52, 54), (120, 116, 118, 255))
    for (x, y, r) in ((22, 34, 6), (38, 32, 5), (32, 44, 6), (46, 46, 4)):
        ell(d, (x - r, y - r, x + r, y + r), (212, 122, 62, 255))
    return img


def fiber() -> Image.Image:
    img, d = canvas()
    for (x0, x1) in ((20, 10), (26, 22), (32, 32), (38, 42), (44, 54)):  # пучок стеблей
        d.line(p(x0, 52, x1, 12), fill=OUT, width=7 * S)
        d.line(p(x0, 52, x1, 12), fill=(150, 176, 86, 255), width=4 * S)
    d.rounded_rectangle([18 * S, 38 * S, 46 * S, 46 * S], radius=3 * S, fill=(196, 160, 96, 255), outline=OUT, width=W)  # перевязь
    return img


def resin() -> Image.Image:
    img, d = canvas()
    poly(d, p(32, 10, 46, 34, 44, 46, 32, 54, 20, 46, 18, 34), (226, 150, 38, 255))  # капля
    poly(d, p(28, 26, 32, 20, 34, 30, 30, 38), (252, 208, 110, 255))
    return img


def generic() -> Image.Image:
    img, d = canvas()
    poly(d, p(14, 54, 12, 34, 22, 24, 42, 24, 52, 34, 50, 54), (176, 140, 92, 255))  # мешочек
    poly(d, p(24, 24, 22, 14, 32, 18, 42, 14, 40, 24), (196, 160, 110, 255))
    d.line(p(22, 25, 42, 25), fill=OUT, width=W)
    return img


ICONS = {
    "wood": (wood, "значок предмета «древесина» на земле 64x64, контур и плоский цвет (E2-1); скрипт tools/make_item_icons.py"),
    "stone": (stone, "значок предмета «камень» на земле 64x64 (E2-1); скрипт tools/make_item_icons.py"),
    "copper_ore": (copper_ore, "значок предмета «медная руда» на земле 64x64 (E2-1); скрипт tools/make_item_icons.py"),
    "fiber": (fiber, "значок предмета «волокно» на земле 64x64 (E2-1); скрипт tools/make_item_icons.py"),
    "resin": (resin, "значок предмета «смола» на земле 64x64 (E2-1); скрипт tools/make_item_icons.py"),
    "generic": (generic, "значок «мешочек» для предметов без своего значка на земле 64x64 (E2-1); скрипт tools/make_item_icons.py"),
}


def main() -> None:
    TEMP_DIR.mkdir(parents=True, exist_ok=True)
    with MANIFEST.open(encoding="utf-8", newline="") as f:
        rows = list(csv.reader(f))
    known = {r[0] for r in rows if r}
    for key, (draw, notes) in ICONS.items():
        img = draw().resize((64, 64), Image.LANCZOS)
        ImageDraw.Draw(img).rectangle([0, 0, 7, 7], fill=(255, 0, 255, 255))  # пурпурный уголок заглушки
        rel = f"client/Assets/Art/_TEMP/TEMP_item_{key}.png"
        img.save(ROOT / rel, optimize=False)
        if rel not in known:
            rows.append([rel, "placeholder", "К", notes])
        print(rel)
    with MANIFEST.open("w", encoding="utf-8", newline="") as f:
        csv.writer(f, lineterminator="\n").writerows(rows)


if __name__ == "__main__":
    main()
