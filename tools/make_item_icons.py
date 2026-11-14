"""Временные значки предметов (E2-1, E2-2, E2-4): 64 x 64, «тёмный контур, плоский цвет». С E2-4 — ещё и картинки
станков и факела: TEMP_obj_workbench / campfire / torch, 128 x 160, вид сверху с наклоном, опора — низ по центру.

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


def canvas(w: int = 64, h: int = 64) -> tuple[Image.Image, ImageDraw.ImageDraw]:
    img = Image.new("RGBA", (w * S, h * S), (0, 0, 0, 0))
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


def axe(head, light) -> Image.Image:
    img, d = canvas()
    stick(d, 18, 56, 40, 12)
    poly(d, p(34, 10, 50, 8, 58, 20, 54, 34, 42, 36, 36, 26), head)  # лезвие
    d.line(p(50, 12, 55, 21, 52, 31), fill=light, width=2 * S)
    return img


def stone_axe() -> Image.Image:
    return axe(ROCK, ROCK_L)


def copper_axe() -> Image.Image:
    return axe(COPPER, COPPER_L)


def hammer() -> Image.Image:
    img, d = canvas()
    stick(d, 18, 56, 40, 22)
    poly(d, p(26, 22, 38, 8, 56, 22, 44, 36), ROCK)  # боёк — брусок поперёк рукояти
    d.line(p(38, 12, 51, 22), fill=ROCK_L, width=2 * S)
    return img


def sword(blade, light) -> Image.Image:
    img, d = canvas()
    poly(d, p(52, 8, 56, 12, 28, 42, 22, 36), blade)  # клинок
    d.line(p(50, 12, 28, 36), fill=light, width=2 * S)
    stick(d, 16, 34, 30, 48, fill=(110, 72, 44, 255), w=4)  # гарда
    stick(d, 20, 44, 10, 54, fill=WOOD, w=4)  # рукоять
    return img


def wooden_sword() -> Image.Image:
    return sword(WOOD_L, (226, 190, 136, 255))


def copper_sword() -> Image.Image:
    return sword(COPPER, COPPER_L)


def bow(limb, tips=None) -> Image.Image:
    img, d = canvas()
    d.line(p(46, 8, 46, 56), fill=OUT, width=2 * S)  # тетива
    d.arc([6 * S, 8 * S, 58 * S, 56 * S], 90, 270, fill=OUT, width=9 * S)
    d.arc([8 * S, 10 * S, 56 * S, 54 * S], 92, 268, fill=limb, width=5 * S)
    d.line(p(32, 9, 46, 9), fill=OUT, width=4 * S)
    d.line(p(32, 55, 46, 55), fill=OUT, width=4 * S)
    if tips is not None:  # медные накладки на плечах и рукояти
        for (a0, a1) in ((100, 128), (166, 194), (232, 260)):
            d.arc([8 * S, 10 * S, 56 * S, 54 * S], a0, a1, fill=tips, width=5 * S)
    return img


def simple_bow() -> Image.Image:
    return bow(WOOD)


def copper_bow() -> Image.Image:
    return bow(WOOD, COPPER)


def copper_ingot() -> Image.Image:
    img, d = canvas()
    poly(d, p(6, 44, 58, 44, 58, 52, 6, 52), (160, 86, 44, 255))      # бок слитка
    poly(d, p(6, 44, 16, 26, 48, 26, 58, 44), COPPER)                 # скос к зрителю
    poly(d, p(16, 26, 22, 16, 42, 16, 48, 26), COPPER_L)              # верхняя грань
    d.line(p(18, 33, 46, 33), fill=COPPER_L, width=2 * S)
    return img


def rope() -> Image.Image:
    img, d = canvas()
    tan, tan_l = (196, 160, 96, 255), (232, 204, 146, 255)
    stick(d, 44, 42, 56, 56, fill=tan, w=4)                           # свободный конец
    ell(d, (8, 10, 52, 54), tan)                                      # моток
    d.ellipse([21 * S, 23 * S, 39 * S, 41 * S], fill=(0, 0, 0, 0), outline=OUT, width=W)
    for (x0, y0, x1, y1) in ((11, 26, 21, 30), (18, 15, 25, 24), (33, 11, 34, 23), (45, 19, 38, 27), (51, 34, 39, 34), (40, 49, 35, 40), (23, 50, 26, 40), (10, 39, 21, 35)):
        d.line(p(x0, y0, x1, y1), fill=OUT, width=2 * S)              # витки
    d.arc([13 * S, 15 * S, 47 * S, 49 * S], 200, 250, fill=tan_l, width=2 * S)
    return img


def meat(flesh, fat, bone=(240, 230, 204, 255)) -> Image.Image:
    img, d = canvas()
    stick(d, 40, 40, 51, 51, fill=bone, w=4)                          # кость
    ell(d, (45, 45, 58, 58), bone)
    poly(d, p(10, 30, 16, 16, 32, 10, 46, 18, 50, 34, 42, 48, 24, 52, 12, 44), flesh)  # кусок
    poly(d, p(20, 28, 26, 20, 36, 20, 40, 30, 34, 40, 24, 40), fat)
    return img


def raw_meat() -> Image.Image:
    return meat((204, 72, 70, 255), (238, 150, 140, 255))


def roast_meat() -> Image.Image:
    return meat((150, 84, 44, 255), (206, 140, 76, 255))


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
    "copper_ingot": (copper_ingot, "значок предмета «медный слиток» 64x64 (E2-4); скрипт tools/make_item_icons.py"),
    "rope": (rope, "значок предмета «верёвка» 64x64 (E2-4); скрипт tools/make_item_icons.py"),
    "copper_axe": (copper_axe, "значок предмета «медный топор» 64x64 (E2-4); скрипт tools/make_item_icons.py"),
    "copper_sword": (copper_sword, "значок предмета «медный меч» 64x64 (E2-4); скрипт tools/make_item_icons.py"),
    "copper_bow": (copper_bow, "значок предмета «медный лук» 64x64 (E2-4); скрипт tools/make_item_icons.py"),
    "meat": (raw_meat, "значок предмета «мясо» 64x64 (E2-4); скрипт tools/make_item_icons.py"),
    "roast_meat": (roast_meat, "значок предмета «жареное мясо» 64x64 (E2-4); скрипт tools/make_item_icons.py"),
}


# ---------- объекты мира (E2-4): вид сверху с наклоном, 128 px на тайл, опора — низ по центру ----------

OBJ_W, OBJ_H = 128, 160
FIRE, FIRE_L = (236, 120, 40, 255), (252, 214, 96, 255)


def workbench() -> Image.Image:
    img, d = canvas(OBJ_W, OBJ_H)
    plank, plank_l, side = (176, 124, 72, 255), (206, 158, 100, 255), (132, 88, 52, 255)
    for x in (16, 100):                                              # ножки
        d.rectangle([x * S, 112 * S, (x + 12) * S, 154 * S], fill=(110, 72, 44, 255), outline=OUT, width=W)
    poly(d, p(8, 92, 120, 92, 120, 120, 8, 120), side)               # царга к зрителю
    d.line(p(8, 106, 120, 106), fill=OUT, width=2 * S)
    poly(d, p(18, 40, 110, 40, 122, 92, 6, 92), plank)               # столешница (дальний край уже)
    for x0, x1 in ((40, 34), (64, 64), (88, 94)):                    # швы досок
        d.line(p(x0, 42, x1, 90), fill=side, width=2 * S)
    d.line(p(22, 46, 106, 46), fill=plank_l, width=2 * S)
    stick(d, 30, 80, 56, 58, fill=WOOD, w=4)                         # на столе: молот и медная заготовка
    poly(d, p(50, 50, 62, 46, 68, 60, 56, 66), ROCK)
    poly(d, p(78, 62, 104, 58, 108, 72, 82, 78), COPPER)
    d.line(p(82, 64, 102, 61), fill=COPPER_L, width=2 * S)
    d.rectangle([84 * S, 26 * S, 100 * S, 44 * S], fill=(120, 116, 118, 255), outline=OUT, width=W)  # упор на дальнем краю
    return img


def campfire() -> Image.Image:
    img, d = canvas(OBJ_W, OBJ_H)
    ell(d, (14, 96, 114, 152), (70, 58, 52, 255))                    # кострище — зола
    for (x, y, r) in ((34, 104, 10), (64, 98, 10), (94, 104, 10), (22, 122, 12), (106, 122, 12), (38, 140, 12), (90, 140, 12), (64, 146, 13)):
        ell(d, (x - r, y - r * 0.8, x + r, y + r * 0.8), ROCK if (x + y) % 3 else ROCK_L)   # камни кольцом
    stick(d, 34, 132, 92, 108, fill=WOOD, w=8)                       # поленья крест-накрест
    stick(d, 36, 108, 94, 132, fill=(124, 82, 48, 255), w=8)
    poly(d, p(64, 22, 84, 62, 92, 92, 80, 118, 64, 124, 48, 118, 36, 92, 44, 62), FIRE)     # пламя
    poly(d, p(64, 56, 76, 84, 74, 104, 64, 112, 54, 104, 52, 84), FIRE_L)
    return img


def torch_obj() -> Image.Image:
    img, d = canvas(OBJ_W, OBJ_H)
    ell(d, (46, 138, 82, 156), (96, 84, 70, 255))                    # холмик у основания
    stick(d, 64, 146, 64, 72, w=8)                                   # древко
    d.rectangle([55 * S, 60 * S, 73 * S, 80 * S], fill=(110, 72, 44, 255), outline=OUT, width=W)   # обмотка
    poly(d, p(64, 12, 86, 44, 82, 62, 64, 70, 46, 62, 42, 44), FIRE)
    poly(d, p(64, 32, 75, 50, 64, 62, 53, 50), FIRE_L)
    return img


OBJECTS = {
    "workbench": (workbench, "объект «верстак» 128x160, вид сверху с наклоном, опора — низ по центру (E2-4); скрипт tools/make_item_icons.py"),
    "campfire": (campfire, "объект «костёр» 128x160, опора — низ по центру (E2-4); скрипт tools/make_item_icons.py"),
    "torch": (torch_obj, "объект «факел на земле» 128x160, опора — низ по центру (E2-4); скрипт tools/make_item_icons.py"),
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
    for key, (draw, notes) in OBJECTS.items():
        img = draw().resize((OBJ_W, OBJ_H), Image.LANCZOS)
        ImageDraw.Draw(img).rectangle([0, 0, 7, 7], fill=(255, 0, 255, 255))  # пурпурный уголок заглушки
        rel = f"client/Assets/Art/_TEMP/TEMP_obj_{key}.png"
        img.save(ROOT / rel, optimize=False)
        if rel not in known:
            rows.append([rel, "placeholder", "К", notes])
        print(rel)
    with MANIFEST.open("w", encoding="utf-8", newline="") as f:
        csv.writer(f, lineterminator="\n").writerows(rows)


if __name__ == "__main__":
    main()
