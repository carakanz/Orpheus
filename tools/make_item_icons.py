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

WOOD = (150, 100, 58, 255)
WOOD_L = (196, 148, 92, 255)
ROCK = (150, 152, 160, 255)
ROCK_L = (196, 198, 206, 255)
COPPER = (212, 122, 62, 255)
COPPER_L = (240, 168, 104, 255)


def stick(d: ImageDraw.ImageDraw, x0, y0, x1, y1, fill=WOOD, w=5) -> None:
    """Палка (рукоять): тёмная линия потолще и цветная поверх."""
    d.line(p(x0, y0, x1, y1), fill=OUT, width=(w + 5) * S)
    d.line(p(x0, y0, x1, y1), fill=fill, width=w * S)


def pickaxe(head, light) -> Image.Image:
    img, d = canvas()
    stick(d, 16, 54, 44, 18)
    poly(d, p(22, 8, 40, 10, 54, 22, 58, 40, 50, 28, 40, 20, 28, 16, 14, 18), head)  # дуга клюва
    d.line(p(28, 12, 42, 15, 51, 24), fill=light, width=2 * S)
    return img


def stone_pickaxe() -> Image.Image:
    return pickaxe(ROCK, ROCK_L)


def copper_pickaxe() -> Image.Image:
    return pickaxe(COPPER, COPPER_L)


def stone_axe() -> Image.Image:
    img, d = canvas()
    stick(d, 18, 56, 40, 12)
    poly(d, p(34, 10, 50, 8, 58, 20, 54, 34, 42, 36, 36, 26), ROCK)  # лезвие
    d.line(p(50, 12, 55, 21, 52, 31), fill=ROCK_L, width=2 * S)
    return img


def hammer() -> Image.Image:
    img, d = canvas()
    stick(d, 18, 56, 40, 22)
    poly(d, p(26, 22, 38, 8, 56, 22, 44, 36), ROCK)  # боёк — брусок поперёк рукояти
    d.line(p(38, 12, 51, 22), fill=ROCK_L, width=2 * S)
    return img


def wooden_sword() -> Image.Image:
    img, d = canvas()
    poly(d, p(52, 8, 56, 12, 28, 42, 22, 36), WOOD_L)  # клинок
    d.line(p(50, 12, 28, 36), fill=(226, 190, 136, 255), width=2 * S)
    stick(d, 16, 34, 30, 48, fill=(110, 72, 44, 255), w=4)  # гарда
    stick(d, 20, 44, 10, 54, fill=WOOD, w=4)  # рукоять
    return img


def simple_bow() -> Image.Image:
    img, d = canvas()
    d.line(p(46, 8, 46, 56), fill=OUT, width=2 * S)  # тетива
    d.arc([6 * S, 8 * S, 58 * S, 56 * S], 90, 270, fill=OUT, width=9 * S)
    d.arc([8 * S, 10 * S, 56 * S, 54 * S], 92, 268, fill=WOOD, width=5 * S)
    d.line(p(32, 9, 46, 9), fill=OUT, width=4 * S)
    d.line(p(32, 55, 46, 55), fill=OUT, width=4 * S)
    return img


def torch() -> Image.Image:
    img, d = canvas()
    stick(d, 32, 58, 32, 30, w=6)
    poly(d, p(32, 6, 44, 22, 42, 32, 32, 36, 22, 32, 20, 22), (236, 120, 40, 255))  # пламя
    poly(d, p(32, 16, 38, 26, 32, 32, 26, 26), (252, 214, 96, 255))
    return img


def arrows() -> Image.Image:
    img, d = canvas()
    for dx in (-12, 0, 12):  # три стрелы веером
        x0, x1 = 32 + dx * 0.5, 32 + dx
        stick(d, x0, 56, x1, 18, fill=WOOD_L, w=2)
        poly(d, p(x1, 6, x1 + 6, 20, x1 - 6, 20), ROCK)  # наконечник
        d.line(p(x0 - 4, 50, x0, 56, x0 + 4, 50), fill=(214, 70, 60, 255), width=2 * S)  # оперение
    return img


ICONS = {
    "wood": (wood, "значок предмета «древесина» на земле 64x64, контур и плоский цвет (E2-1); скрипт tools/make_item_icons.py"),
    "stone": (stone, "значок предмета «камень» на земле 64x64 (E2-1); скрипт tools/make_item_icons.py"),
    "copper_ore": (copper_ore, "значок предмета «медная руда» на земле 64x64 (E2-1); скрипт tools/make_item_icons.py"),
    "fiber": (fiber, "значок предмета «волокно» на земле 64x64 (E2-1); скрипт tools/make_item_icons.py"),
    "resin": (resin, "значок предмета «смола» на земле 64x64 (E2-1); скрипт tools/make_item_icons.py"),
    "generic": (generic, "значок «мешочек» для предметов без своего значка на земле 64x64 (E2-1); скрипт tools/make_item_icons.py"),
    "stone_pickaxe": (stone_pickaxe, "значок предмета «каменная кирка» 64x64, контур и плоский цвет (E2-2); скрипт tools/make_item_icons.py"),
    "stone_axe": (stone_axe, "значок предмета «каменный топор» 64x64 (E2-2); скрипт tools/make_item_icons.py"),
    "hammer": (hammer, "значок предмета «молот» 64x64 (E2-2); скрипт tools/make_item_icons.py"),
    "wooden_sword": (wooden_sword, "значок предмета «деревянный меч» 64x64 (E2-2); скрипт tools/make_item_icons.py"),
    "simple_bow": (simple_bow, "значок предмета «простой лук» 64x64 (E2-2); скрипт tools/make_item_icons.py"),
    "torch": (torch, "значок предмета «факел» 64x64 (E2-2); скрипт tools/make_item_icons.py"),
    "copper_pickaxe": (copper_pickaxe, "значок предмета «медная кирка» 64x64 (E2-2); скрипт tools/make_item_icons.py"),
    "arrows": (arrows, "значок предмета «стрелы» 64x64 (E2-2); скрипт tools/make_item_icons.py"),
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
