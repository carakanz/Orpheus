"""Временные значки предметов (E2-1, E2-2, E2-4): 64 x 64, «тёмный контур, плоский цвет». С E2-4 — ещё и картинки
станков и факела: TEMP_obj_workbench / campfire / torch, 128 x 160, вид сверху с наклоном, опора — низ по центру.

Правило заглушек (CLAUDE.md): файл в client/Assets/Art/_TEMP/, имя TEMP_item_<ключ>.png, строка в
art/manifest.csv со статусом placeholder, пурпурный уголок #FF00FF 8 x 8 px в левом верхнем углу.
Ключ — ItemDef.Key из src/Orpheus.Core/Items/ItemTable.cs; item_generic — мешочек для предметов без своего значка.
Картинки детерминированы: повторный запуск даёт те же байты.

E2-5: сундук и кровать (TEMP_obj_chest / bed, 128 x 160, с уголком) и плитки построек 128 x 128 (TILES): полы
деревянный, каменный, настил (TEMP_floor_*), стены деревянная и каменная, забор, дверь закрытая и открытая
(TEMP_obj_wall_* / fence / door*). Плитки кладутся встык по сетке — пурпурный уголок был бы решёткой на всём доме,
поэтому у них его нет (исключение E1-9 «бесшовные текстуры», слова «без уголка» в notes манифеста).

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


def chest() -> Image.Image:
    img, d = canvas(OBJ_W, OBJ_H)
    body, lid, side, iron = (150, 98, 54, 255), (186, 128, 72, 255), (112, 70, 40, 255), (92, 92, 104, 255)
    poly(d, p(14, 96, 114, 96, 114, 150, 14, 150), body)               # короб к зрителю
    for y in (114, 132):                                               # доски короба
        d.line(p(16, y, 112, y), fill=side, width=2 * S)
    poly(d, p(22, 58, 106, 58, 116, 96, 12, 96), lid)                  # крышка сверху (дальний край уже)
    d.line(p(26, 64, 102, 64), fill=(214, 160, 100, 255), width=2 * S)
    for x0, x1 in ((34, 30), (94, 98)):                                # окованные углы крышки и короба
        d.line(p(x0, 58, x1, 96), fill=iron, width=5 * S)
    for x in (24, 98):
        d.rectangle([x * S, 96 * S, (x + 8) * S, 150 * S], fill=iron, outline=OUT, width=W)
    d.rectangle([56 * S, 90 * S, 72 * S, 112 * S], fill=(214, 182, 72, 255), outline=OUT, width=W)  # замок
    d.rectangle([62 * S, 100 * S, 66 * S, 106 * S], fill=OUT)
    return img


def bed() -> Image.Image:
    img, d = canvas(OBJ_W, OBJ_H)
    frame, frame_d = (140, 92, 52, 255), (104, 66, 38, 255)
    sheet, blanket, blanket_d = (236, 228, 210, 255), (64, 116, 168, 255), (44, 86, 130, 255)
    poly(d, p(18, 132, 110, 132, 110, 152, 18, 152), frame_d)          # царга к зрителю
    poly(d, p(24, 20, 104, 20, 112, 132, 16, 132), frame)              # рама сверху
    poly(d, p(30, 28, 98, 28, 104, 124, 24, 124), sheet)               # простыня
    poly(d, p(36, 34, 92, 34, 94, 54, 34, 54), (250, 246, 236, 255))   # подушка
    poly(d, p(27, 66, 101, 66, 104, 124, 24, 124), blanket)            # одеяло
    d.line(p(27, 74, 101, 74), fill=(110, 158, 204, 255), width=3 * S) # отворот
    for x0, x1 in ((50, 48), (78, 80)):
        d.line(p(x0, 78, x1, 122), fill=blanket_d, width=2 * S)
    poly(d, p(20, 8, 108, 8, 108, 24, 20, 24), frame_d)                # изголовье
    return img


OBJECTS = {
    "chest": (chest, "объект «сундук» 128x160, вид сверху с наклоном, опора — низ по центру (E2-5); скрипт tools/make_item_icons.py"),
    "bed": (bed, "объект «кровать» 128x160 (в ядре 1 x 1, в `05` 1 x 2), опора — низ по центру (E2-5); скрипт tools/make_item_icons.py"),
    "workbench": (workbench, "объект «верстак» 128x160, вид сверху с наклоном, опора — низ по центру (E2-4); скрипт tools/make_item_icons.py"),
    "campfire": (campfire, "объект «костёр» 128x160, опора — низ по центру (E2-4); скрипт tools/make_item_icons.py"),
    "torch": (torch_obj, "объект «факел на земле» 128x160, опора — низ по центру (E2-4); скрипт tools/make_item_icons.py"),
}


T = 128  # плитка постройки: тайл целиком


def tile_canvas() -> tuple[Image.Image, ImageDraw.ImageDraw]:
    return canvas(T, T)


def floor_wood() -> Image.Image:
    img, d = tile_canvas()
    tones = [(170, 118, 70, 255), (156, 106, 62, 255), (182, 128, 78, 255), (162, 112, 66, 255)]
    for i in range(4):                                                 # четыре доски поперёк, швы со сдвигом
        y0 = i * 32
        d.rectangle([0, y0 * S, T * S, (y0 + 32) * S], fill=tones[i])
        d.line(p(0, y0 + 1, T, y0 + 1), fill=(96, 62, 36, 255), width=3 * S)
        cut = (24, 88, 56, 112)[i]
        d.line(p(cut, y0, cut, y0 + 32), fill=(96, 62, 36, 255), width=3 * S)
        d.line(p(4, y0 + 10, cut - 6, y0 + 10), fill=(196, 146, 94, 255), width=2 * S)   # волокна
        d.line(p(cut + 6, y0 + 20, T - 4, y0 + 20), fill=(140, 94, 54, 255), width=2 * S)
        d.ellipse([(cut + 20) * S, (y0 + 12) * S, (cut + 26) * S, (y0 + 18) * S], fill=(120, 78, 44, 255))  # сучок
    return img


def floor_stone() -> Image.Image:
    img, d = tile_canvas()
    d.rectangle([0, 0, T * S, T * S], fill=(92, 90, 96, 255))         # раствор
    slabs = ((2, 2, 62, 46), (66, 2, 126, 46), (2, 50, 40, 126), (44, 50, 126, 88), (44, 92, 86, 126), (90, 92, 126, 126))
    tones = ((160, 158, 162), (148, 146, 152), (170, 168, 170), (154, 152, 158), (142, 140, 146), (166, 164, 168))
    for (x0, y0, x1, y1), c in zip(slabs, tones):                      # плиты разного размера
        d.rounded_rectangle([x0 * S, y0 * S, x1 * S, y1 * S], radius=4 * S, fill=c + (255,), outline=(60, 58, 64, 255), width=2 * S)
        d.line(p(x0 + 5, y0 + 5, x1 - 8, y0 + 5), fill=(196, 194, 198, 255), width=2 * S)  # блик верхней кромки
    return img


def floor_platform() -> Image.Image:
    img, d = tile_canvas()
    d.rectangle([0, 0, T * S, T * S], fill=(64, 52, 40, 255))         # щели между досками
    for i in range(4):                                                 # доски вдоль, с зазорами — над водой
        x0 = i * 32 + 3
        d.rectangle([x0 * S, 0, (x0 + 26) * S, T * S], fill=((132, 98, 60, 255), (120, 88, 54, 255))[i % 2], outline=OUT, width=2 * S)
        for y in (18, 110):                                            # гвозди поперечин
            d.ellipse([(x0 + 10) * S, (y - 3) * S, (x0 + 16) * S, (y + 3) * S], fill=(70, 70, 80, 255))
    d.rectangle([0, 12 * S, T * S, 24 * S], fill=(104, 74, 44, 255))  # поперечины
    d.rectangle([0, 104 * S, T * S, 116 * S], fill=(104, 74, 44, 255))
    return img


def wall_wood() -> Image.Image:
    img, d = tile_canvas()
    d.rectangle([0, 0, T * S, T * S], fill=(118, 78, 44, 255))
    for i in range(4):                                                 # брёвна торцами вверх — сруб сверху
        x0 = i * 32
        d.rounded_rectangle([(x0 + 2) * S, 2 * S, (x0 + 30) * S, 102 * S], radius=12 * S, fill=(156, 104, 58, 255), outline=OUT, width=W)
        d.line(p(x0 + 10, 14, x0 + 10, 90), fill=(188, 136, 84, 255), width=3 * S)
    d.rectangle([0, 102 * S, T * S, T * S], fill=(78, 52, 32, 255))   # тень у подножия (лицо к зрителю)
    d.line(p(0, 102, T, 102), fill=OUT, width=W)
    return img


def wall_stone() -> Image.Image:
    img, d = tile_canvas()
    d.rectangle([0, 0, T * S, T * S], fill=(84, 82, 90, 255))
    rows = ((0, 34, (4, 64)), (34, 68, (36, 96)), (68, 102, (20, 80)))
    for y0, y1, cuts in rows:                                          # кладка со сдвигом швов
        xs = [0, *cuts, T]
        for a, b in zip(xs, xs[1:]):
            d.rounded_rectangle([(a + 2) * S, (y0 + 2) * S, (b - 2) * S, (y1 - 2) * S], radius=4 * S,
                                fill=(150 + (a % 3) * 8, 148 + (a % 3) * 8, 156, 255), outline=(50, 48, 56, 255), width=2 * S)
    d.rectangle([0, 102 * S, T * S, T * S], fill=(64, 62, 70, 255))   # лицо кладки к зрителю, в тени
    d.line(p(0, 102, T, 102), fill=OUT, width=W)
    for x in (16, 56, 96):
        d.line(p(x, 108, x + 18, 108), fill=(96, 94, 102, 255), width=2 * S)
    return img


def fence() -> Image.Image:
    img, d = tile_canvas()
    for y in (40, 84):                                                 # две жерди поперёк тайла
        d.rectangle([0, (y - 7) * S, T * S, (y + 7) * S], fill=(150, 100, 58, 255), outline=OUT, width=W)
    for x in (22, 106):                                                # столбики
        d.rounded_rectangle([(x - 10) * S, 14 * S, (x + 10) * S, 116 * S], radius=6 * S, fill=(126, 84, 48, 255), outline=OUT, width=W)
        d.ellipse([(x - 6) * S, 16 * S, (x + 6) * S, 28 * S], fill=(186, 136, 84, 255))
    return img


def door(open_: bool) -> Image.Image:
    img, d = tile_canvas()
    d.rectangle([0, 0, 12 * S, T * S], fill=(96, 64, 38, 255), outline=OUT, width=W)          # косяки
    d.rectangle([(T - 12) * S, 0, T * S, T * S], fill=(96, 64, 38, 255), outline=OUT, width=W)
    if open_:
        poly(d, p(12, 4, 34, 14, 34, 118, 12, 124), (170, 118, 66, 255))                     # створка распахнута к стене
        d.line(p(23, 16, 23, 114), fill=(126, 84, 48, 255), width=2 * S)
        d.rectangle([12 * S, 0, (T - 12) * S, 6 * S], fill=(78, 52, 32, 255))
    else:
        d.rectangle([12 * S, 4 * S, (T - 12) * S, 124 * S], fill=(170, 118, 66, 255), outline=OUT, width=W)
        for x in (40, 64, 88):
            d.line(p(x, 8, x, 120), fill=(126, 84, 48, 255), width=2 * S)
        for y in (30, 96):                                                                     # петли и перекладины
            d.rectangle([16 * S, (y - 5) * S, (T - 16) * S, (y + 5) * S], fill=(92, 92, 104, 255), outline=OUT, width=2 * S)
        ell(d, (94, 58, 104, 68), (214, 182, 72, 255))                                          # ручка
    return img


TILES = {
    "floor_wood": (floor_wood, "пол «деревянный» 128x128 (доски), плитка встык, без уголка (E2-5, исключение E1-9: решётка уголков на всём полу); скрипт tools/make_item_icons.py"),
    "floor_stone": (floor_stone, "пол «каменный» 128x128 (плиты), плитка встык, без уголка (E2-5, исключение E1-9); скрипт tools/make_item_icons.py"),
    "floor_platform": (floor_platform, "пол «настил над водой» 128x128, плитка встык, без уголка (E2-5, исключение E1-9); скрипт tools/make_item_icons.py"),
    "obj_wall_wood": (wall_wood, "стена деревянная 128x128 (сруб сверху, тень к зрителю), плитка встык, без уголка (E2-5, исключение E1-9: стены идут сплошной линией); скрипт tools/make_item_icons.py"),
    "obj_wall_stone": (wall_stone, "стена каменная 128x128 (кладка), плитка встык, без уголка (E2-5, исключение E1-9); скрипт tools/make_item_icons.py"),
    "obj_fence": (fence, "забор деревянный 128x128 (жерди и столбики), плитка встык, без уголка (E2-5, исключение E1-9); скрипт tools/make_item_icons.py"),
    "obj_door": (lambda: door(False), "дверь деревянная закрытая 128x128, плитка в ряду стены, без уголка (E2-5, исключение E1-9); скрипт tools/make_item_icons.py"),
    "obj_door_open": (lambda: door(True), "дверь деревянная открытая 128x128, плитка в ряду стены, без уголка (E2-5, исключение E1-9); скрипт tools/make_item_icons.py"),
}


# E2-11: постройки — предметы. Значок предмета-постройки 64 x 64 — её плитка или картинка объекта, уменьшенная в рамку:
# плитка (пол, стена, забор, дверь) — «стопкой» из двух со сдвигом (предмет, а не тайл мира), объект — по высоте.
def from_tile(draw, stack: bool = True) -> Image.Image:
    img, d = canvas()
    tile = draw()
    side = 44 * S
    small = tile.resize((side, side), Image.LANCZOS)
    if stack:
        d.rounded_rectangle([14 * S, 6 * S, 14 * S + side, 6 * S + side], radius=3 * S, fill=(0, 0, 0, 0), outline=OUT, width=W)
        img.alpha_composite(small, (14 * S, 6 * S))
        d.rectangle([14 * S, 6 * S, 14 * S + side, 6 * S + side], outline=OUT, width=W)
    x, y = (6 * S, 14 * S) if stack else (10 * S, 10 * S)
    img.alpha_composite(small, (x, y))
    d.rectangle([x, y, x + side, y + side], outline=OUT, width=W)
    return img


def from_obj(draw) -> Image.Image:
    img, _ = canvas()
    obj = draw()
    h = 60 * S
    w = int(obj.width * h / obj.height)
    small = obj.resize((w, h), Image.LANCZOS)
    img.alpha_composite(small, ((64 * S - w) // 2, 2 * S))
    return img


FURNITURE_KEYS = {"workbench", "chest", "bed", "campfire", "torch"}  # E2-17: TEMP_obj_* — tools/make_furniture.py

BUILD_NOTE = "значок предмета-постройки «{0}» 64x64 из её плитки / картинки, контур и плоский цвет (E2-11); скрипт tools/make_item_icons.py"
BUILD_ICONS = {
    "wood_floor": (lambda: from_tile(floor_wood), BUILD_NOTE.format("пол деревянный")),
    "stone_floor": (lambda: from_tile(floor_stone), BUILD_NOTE.format("пол каменный")),
    "platform": (lambda: from_tile(floor_platform), BUILD_NOTE.format("настил")),
    "wood_wall": (lambda: from_tile(wall_wood), BUILD_NOTE.format("стена деревянная")),
    "stone_wall": (lambda: from_tile(wall_stone), BUILD_NOTE.format("стена каменная")),
    "wood_fence": (lambda: from_tile(fence), BUILD_NOTE.format("забор")),
    "door": (lambda: from_tile(lambda: door(False), stack=False), BUILD_NOTE.format("дверь")),
    "campfire": (lambda: from_obj(campfire), BUILD_NOTE.format("костёр")),
    "workbench": (lambda: from_obj(workbench), BUILD_NOTE.format("верстак")),
    "chest": (lambda: from_obj(chest), BUILD_NOTE.format("сундук")),
    "bed": (lambda: from_obj(bed), BUILD_NOTE.format("кровать")),
}


def main() -> None:
    TEMP_DIR.mkdir(parents=True, exist_ok=True)
    with MANIFEST.open(encoding="utf-8", newline="") as f:
        rows = list(csv.reader(f))
    known = {r[0] for r in rows if r}
    for key, (draw, notes) in {**ICONS, **BUILD_ICONS}.items():
        img = draw().resize((64, 64), Image.LANCZOS)
        ImageDraw.Draw(img).rectangle([0, 0, 7, 7], fill=(255, 0, 255, 255))  # пурпурный уголок заглушки
        rel = f"client/Assets/Art/_TEMP/TEMP_item_{key}.png"
        img.save(ROOT / rel, optimize=False)
        if rel not in known:
            rows.append([rel, "placeholder", "К", notes])
        print(rel)
    for key, (draw, notes) in OBJECTS.items():
        if key in FURNITURE_KEYS:
            continue  # E2-17: картинки мебели «с высотой» делает tools/make_furniture.py; здесь — только значки из старых рисунков
        img = draw().resize((OBJ_W, OBJ_H), Image.LANCZOS)
        ImageDraw.Draw(img).rectangle([0, 0, 7, 7], fill=(255, 0, 255, 255))  # пурпурный уголок заглушки
        rel = f"client/Assets/Art/_TEMP/TEMP_obj_{key}.png"
        img.save(ROOT / rel, optimize=False)
        if rel not in known:
            rows.append([rel, "placeholder", "К", notes])
        print(rel)
    for key, (draw, notes) in TILES.items():
        img = draw().resize((T, T), Image.LANCZOS)                     # без уголка — см. шапку
        rel = f"client/Assets/Art/_TEMP/TEMP_{key}.png"
        img.save(ROOT / rel, optimize=False)
        if rel not in known:
            rows.append([rel, "placeholder", "К", notes])
        print(rel)
    with MANIFEST.open("w", encoding="utf-8", newline="") as f:
        csv.writer(f, lineterminator="\n").writerows(rows)


if __name__ == "__main__":
    main()
