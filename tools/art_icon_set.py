"""Весь набор значков предметов в стиле Б (карточка A-005): сырые картинки Мака → значки 128/64 px → заглушки игры и лист.

    python tools/art_icon_set.py --raw ПАПКА --probe-raw ПАПКА_ПРОБЫ [--compare ФАЙЛ] [--install] [--sheet]

Сырые картинки набора — ПАПКА/s_<предмет>[_2].png (`tools/art_stage1.py collect --set a005`), пробы — ПАПКА_ПРОБЫ/<предмет>_b.png;
в git не хранятся (воспроизводятся по зерну и промпту из art/style/sets/a005.json). Выбор варианта — CHOICE ниже.
Вырезание и уменьшение — как у пробы (tools/art_icon_probe.py: фон по «зелёности» и по цвету краёв кадра, поле 4 %,
предумноженная альфа + нерезкая маска).
  --compare ФАЙЛ   все варианты рядом (для выбора), 128 px на светлом и в ячейке на траве;
  --install        значки → art/style/icons_b/<предмет>_128.png, _64.png и client/Assets/Art/_TEMP/TEMP_item_<предмет>.png
                   (64 × 64, пурпурный уголок 8 px как у прочих заглушек — в игре он не рисуется, C-008),
                   строки art/manifest.csv: статус placeholder, источник «Мак, стиль Б»;
  --sheet          лист work/screens/A-005_set.png: значки по разделам окна изготовления в 64 px и в ячейках окна на траве.
"""
from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path

from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
import art_icon_probe as probe  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
ICONS = ROOT / "art" / "style" / "icons_b"
TEMP = ROOT / "client" / "Assets" / "Art" / "_TEMP"
MANIFEST = ROOT / "art" / "manifest.csv"
SHEET = ROOT / "work" / "screens" / "A-005_set.png"

# Разделы окна изготовления (CraftShelf) и подписи; сырьё, которое не делается, — в «Материалах», мясо — в «Еде».
SECTIONS = [
    ("Здания", [("wood_floor", "пол деревянный"), ("platform", "настил"), ("wood_wall", "стена деревянная"),
                ("wood_fence", "забор"), ("door", "дверь"), ("stone_floor", "пол каменный"), ("stone_wall", "стена каменная")]),
    ("Мебель", [("workbench", "верстак"), ("campfire", "костёр"), ("chest", "сундук"), ("bed", "кровать"), ("torch", "факел")]),
    ("Инструменты", [("stone_pickaxe", "каменная кирка"), ("copper_pickaxe", "медная кирка"), ("stone_axe", "каменный топор"),
                     ("copper_axe", "медный топор"), ("hammer", "молот")]),
    ("Оружие", [("wooden_sword", "деревянный меч"), ("copper_sword", "медный меч"), ("simple_bow", "простой лук"),
                ("copper_bow", "медный лук"), ("arrows", "стрелы")]),
    ("Материалы", [("wood", "древесина"), ("stone", "камень"), ("resin", "смола"), ("fiber", "волокно"),
                   ("copper_ore", "медная руда"), ("copper_ingot", "медный слиток"), ("rope", "верёвка")]),
    ("Еда", [("meat", "мясо"), ("roast_meat", "жареное мясо")]),
]
LABEL = {k: n for _, items in SECTIONS for k, n in items}

# Источник значка: «probe:<ключ>» — сырая картинка пробы, иначе ключ набора. По умолчанию — s_<предмет>.
CHOICE = {
    "wood": "probe:wood_b", "stone": "probe:stone_b", "copper_ingot": "probe:copper_ingot_b",
    "copper_pickaxe": "probe:copper_pickaxe_b", "stone_wall": "probe:stone_wall_b", "workbench": "probe:workbench_b",
    # выбор из двух вариантов (15.11): у первого — плашка, два обуха, нет тетивы и т. п. (карточка, «Набор 15.11»)
    "stone_floor": "s_stone_floor_2", "door": "s_door_2", "stone_axe": "s_stone_axe_2", "copper_axe": "s_copper_axe_2",
    # перезаказ с поправкой (_r)
    "platform": "s_platform_r", "wood_wall": "s_wood_wall_r", "copper_ore": "s_copper_ore_r",
}
# Не удались все попытки — остаётся прежняя заглушка скрипта (молот: 2 варианта и перезаказ — на плашке / голова отдельно).
KEEP = set()
# Перезаказаны в A-006 по эскизу (tools/art_a006.py install) — этот скрипт их не перезаписывает.
A006 = {"hammer", "wood_wall", "platform", "door", "arrows"}


def source(item: str, raw: Path, praw: Path) -> Path:
    c = CHOICE.get(item, f"s_{item}")
    return praw / f"{c[6:]}.png" if c.startswith("probe:") else raw / f"{c}.png"


def make(src: Path) -> tuple[Image.Image, Image.Image]:
    cut = probe.cut_green(Image.open(src))
    return probe.fit(cut, 128), probe.fit(cut, 64)


def on_grass(icons: list[Image.Image | None], per_row: int) -> Image.Image:
    rows = (len(icons) + per_row - 1) // per_row
    w, h = 16 + per_row * (probe.CELL + 6), 16 + rows * (probe.CELL + 6)
    g = probe.grass(w, h)
    for i, ic in enumerate(icons):
        small = probe.fit_from(ic, probe.ICON) if ic is not None else None
        g.alpha_composite(probe.cell_with(small), (8 + (i % per_row) * (probe.CELL + 6), 8 + (i // per_row) * (probe.CELL + 6)))
    return g


def compare(raw: Path, praw: Path, out: Path) -> None:
    keys = sorted(p.stem for p in raw.glob("s_*.png") if not p.stem.endswith("_cut") and "@" not in p.stem)
    cols = 6
    tile_w, tile_h = 200, 220
    rows = (len(keys) + cols - 1) // cols
    sheet = Image.new("RGBA", (cols * tile_w, rows * tile_h), (236, 222, 196, 255))
    d = ImageDraw.Draw(sheet)
    for i, k in enumerate(keys):
        x, y = (i % cols) * tile_w, (i // cols) * tile_h
        big, _ = make(raw / f"{k}.png")
        sheet.alpha_composite(big, (x + 8, y + 24))
        sheet.alpha_composite(on_grass([big], 1).crop((0, 0, 64, 64)), (x + 136, y + 24))
        d.text((x + 6, y + 4), k, fill=(50, 30, 20), font=probe.font(15))
    sheet.convert("RGB").save(out)
    print(f"сравнение: {out}")


def install(raw: Path, praw: Path) -> None:
    ICONS.mkdir(parents=True, exist_ok=True)
    with MANIFEST.open(encoding="utf-8", newline="") as f:
        rows = list(csv.reader(f))
    notes = {}
    for item in LABEL:
        if item in A006:
            print(f"{item}: A-006 (tools/art_a006.py) — пропуск")
            continue
        if item in KEEP:
            print(f"{item}: прежняя заглушка (KEEP)")
            continue
        src = source(item, raw, praw)
        if not src.exists():
            print(f"{item}: нет {src} — пропуск")
            continue
        big, small = make(src)
        big.save(ICONS / f"{item}_128.png", optimize=True)
        small.save(ICONS / f"{item}_64.png", optimize=True)
        temp = small.copy()
        ImageDraw.Draw(temp).rectangle([0, 0, 7, 7], fill=(255, 0, 255, 255))  # пурпурный уголок заглушки
        temp.save(TEMP / f"TEMP_item_{item}.png", optimize=True)
        c = CHOICE.get(item, f"s_{item}")
        notes[f"client/Assets/Art/_TEMP/TEMP_item_{item}.png"] = (
            f"значок предмета «{LABEL[item]}» 64x64; Мак, стиль Б (A-005: Z-Image base, набор art/style/sets/a005.json, "
            f"ключ {c.replace('probe:', '')}); 128 px — art/style/icons_b/{item}_128.png; финальный — после приёмки Автора; "
            f"скрипт tools/art_icon_set.py")
        print(f"{item}: {src.name}")
    rows = [[r[0], "placeholder", "А", notes[r[0]]] if r and r[0] in notes else r for r in rows]
    with MANIFEST.open("w", encoding="utf-8", newline="") as f:
        csv.writer(f, lineterminator="\n").writerows(rows)


def sheet() -> None:
    per_row = 7
    colw, rowh, left = 132, 104, 170
    W = left + per_row * colw + 60
    top = 70
    H_list = top + len(SECTIONS) * rowh + 20
    grass_rows = [(name, items) for name, items in SECTIONS]
    H_grass = 60 + len(grass_rows) * (probe.CELL + 14) + 20
    img = Image.new("RGBA", (W, H_list + H_grass), (236, 222, 196, 255))
    d = ImageDraw.Draw(img)
    d.text((16, 14), "A-005 — значки предметов, стиль Б (Мак, Z-Image base): разделы окна изготовления, 64 px",
           fill=(50, 30, 20), font=probe.font(24, True))
    for si, (name, items) in enumerate(SECTIONS):
        y = top + si * rowh
        d.text((16, y + 22), name, fill=(50, 30, 20), font=probe.font(20, True))
        for i, (k, label) in enumerate(items):
            x = left + i * colw
            p = ICONS / f"{k}_64.png"
            d.rectangle((x + 30, y + 2, x + 30 + 68, y + 2 + 68), outline=(170, 130, 80))
            if p.exists():
                img.alpha_composite(Image.open(p).convert("RGBA"), (x + 32, y + 4))
            else:   # нет значка Мака — прежняя заглушка скрипта с пометкой
                img.alpha_composite(probe.current_icon(k), (x + 32, y + 4))
                label += " (заглушка)"
            tw = d.textlength(label, font=probe.font(14))
            d.text((x + 64 - tw / 2, y + 76), label, fill=(170, 40, 30) if not p.exists() else (70, 45, 30), font=probe.font(14))
    gy = H_list
    img.alpha_composite(probe.grass(W, H_grass), (0, gy))
    img.alpha_composite(Image.new("RGBA", (W, 44), (16, 12, 10, 170)), (0, gy))
    d.text((16, gy + 8), "В ячейках окна игры (C-008), 1:1 при 1080p: ячейка 48 px поверх травы мира, значок 40 px",
           fill=(245, 235, 215), font=probe.font(22, True))
    for si, (name, items) in enumerate(grass_rows):
        y = gy + 60 + si * (probe.CELL + 14)
        img.alpha_composite(Image.new("RGBA", (150, 30), (16, 12, 10, 160)), (12, y + 9))
        d.text((20, y + 12), name, fill=(245, 235, 215), font=probe.font(17))
        for i, (k, _) in enumerate(items):
            p = ICONS / f"{k}_128.png"
            ic = (probe.fit_from(Image.open(p).convert("RGBA"), probe.ICON) if p.exists()
                  else probe.current_icon(k).resize((probe.ICON, probe.ICON), Image.LANCZOS))
            img.alpha_composite(probe.cell_with(ic), (left + i * (probe.CELL + 6), y))
    SHEET.parent.mkdir(parents=True, exist_ok=True)
    img.convert("RGB").save(SHEET, optimize=True)
    print(f"лист: {SHEET}")


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--raw", required=True)
    p.add_argument("--probe-raw", required=True)
    p.add_argument("--compare")
    p.add_argument("--install", action="store_true")
    p.add_argument("--sheet", action="store_true")
    p.add_argument("--choose", action="append", default=[], help="предмет=ключ (перекрыть CHOICE)")
    a = p.parse_args()
    for c in a.choose:
        k, _, v = c.partition("=")
        CHOICE[k] = v
    raw, praw = Path(a.raw), Path(a.probe_raw)
    if a.compare:
        compare(raw, praw, Path(a.compare))
    if a.install:
        install(raw, praw)
    if a.sheet:
        sheet()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
