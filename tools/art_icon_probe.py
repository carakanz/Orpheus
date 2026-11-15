"""Проба стиля значков (карточка A-005): сырые картинки Мака → значки 128 и 64 px → лист сравнения.

    python tools/art_icon_probe.py --raw ПАПКА [--only ключ …]

Сырые картинки — ПАПКА/<ключ>.png (их кладёт `tools/art_stage1.py collect --set a005`; в git не хранятся).
Фон — зелёный «хромакей»: альфа по «зелёности» (g − max(r, b)) с мягким краем, зелёный отсвет на краях гасится,
остаётся крупнейшая связная область (и всё, что рядом с ней). Предмет обрезается по силуэту и вписывается в квадрат
с полем 4 % (как у значков Factorio), уменьшение в предумноженной альфе + нерезкая маска (иначе мыльно).
Выход — art/style/probes/A-005/<ключ>_128.png, _64.png; лист — work/screens/A-005_probe.png.
В игру значки не подключаются (это после выбора Автора).
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "art" / "style" / "probes" / "A-005"
SHEET = ROOT / "work" / "screens" / "A-005_probe.png"
TEMP = ROOT / "client" / "Assets" / "Art" / "_TEMP"
UI_SHOT = ROOT / "work" / "screens" / "accept_E2-9_inv.png"
CELL_BOX = (93, 399, 161, 467)        # пустая ячейка сумки (4-й ряд) с краем панели, снимок 1080p
ITEMS = [("wood", "древесина"), ("stone", "камень"), ("copper_ingot", "медный слиток"),
         ("copper_pickaxe", "медная кирка"), ("stone_wall", "каменная стена"), ("workbench", "верстак")]
VARIANTS = [("a", "А — точно, без контура"), ("b", "Б — точно, тонкий контур"), ("c", "В — живописнее, блик")]
MARGIN = 0.04


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    for name in (("segoeuib.ttf" if bold else "segoeui.ttf"), "arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


def _components(mask: np.ndarray):
    """Связные области (4-связность) — через заливку Pillow, без scipy."""
    h, w = mask.shape
    lab = np.zeros((h, w), np.int32)
    im = Image.fromarray(np.where(mask, 255, 0).astype(np.uint8))
    n = 0
    ys, xs = np.nonzero(mask)
    for y, x in zip(ys, xs):
        if lab[y, x]:
            continue
        n += 1
        ImageDraw.floodfill(im, (int(x), int(y)), 100, thresh=0)
        reg = np.asarray(im) == 100
        lab[reg] = n
        im = Image.fromarray(np.where(reg, 0, np.asarray(im)).astype(np.uint8))
    return lab, n


def cut_green(src: Image.Image) -> Image.Image:
    rgb = np.asarray(src.convert("RGB")).astype(np.float32)
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    green = g - np.maximum(r, b)                       # у фона ≈ 150…255, у предмета ≤ 0
    alpha = np.clip((90 - green) / 60, 0, 1)            # мягкий край: 30 → непрозрачно, 90 → прозрачно
    # крупные области предмета (маска на уменьшенной копии — быстрее), всё прочее — прозрачно
    small = Image.fromarray((alpha > 0.5).astype(np.uint8) * 255).resize((256, 256), Image.NEAREST)
    lab, n = _components(np.asarray(small) > 0)
    if n:
        sizes = np.bincount(lab.ravel())[1:]
        keep = np.isin(lab, 1 + np.nonzero(sizes >= max(40, sizes.max() * 0.02))[0])
        keep = np.asarray(Image.fromarray(keep.astype(np.uint8) * 255).filter(ImageFilter.MaxFilter(5))
                          .resize(alpha.shape[::-1], Image.BILINEAR)) > 0
        alpha = alpha * keep
    # гашение зелёного отсвета: зелёный не выше max(r, b) там, где он торчит
    spill = np.maximum(r, b)
    g2 = np.where(g > spill, spill + (g - spill) * 0.15, g)
    out = np.dstack([r, g2, b, alpha * 255]).clip(0, 255).astype(np.uint8)
    return Image.fromarray(out, "RGBA")


def fit(cut: Image.Image, size: int) -> Image.Image:
    a = np.asarray(cut)[..., 3]
    ys, xs = np.nonzero(a > 8)
    box = (xs.min(), ys.min(), xs.max() + 1, ys.max() + 1)
    crop = cut.crop(box)
    side = max(crop.size)
    sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    sq.paste(crop, ((side - crop.size[0]) // 2, (side - crop.size[1]) // 2))
    inner = round(size * (1 - 2 * MARGIN))
    small = sq.convert("RGBa").resize((inner, inner), Image.LANCZOS).convert("RGBA")
    rgb = small.convert("RGB").filter(ImageFilter.UnsharpMask(radius=0.8 if size <= 64 else 1.0,
                                                             percent=90, threshold=1))
    small = Image.merge("RGBA", (*rgb.split(), small.getchannel("A")))
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(small, ((size - inner) // 2, (size - inner) // 2))
    return out


def current_icon(key: str) -> Image.Image:
    im = Image.open(TEMP / f"TEMP_item_{key}.png").convert("RGBA")
    px = im.load()
    for y in range(8):                                  # как в игре: пурпурный уголок не показывается
        for x in range(8):
            px[x, y] = (0, 0, 0, 0)
    return im


def build_sheet(icons: dict[tuple[str, str], tuple[Image.Image, Image.Image]]) -> None:
    shot = Image.open(UI_SHOT).convert("RGBA")
    cell = shot.crop(CELL_BOX)
    panel_bg = shot.getpixel((163, 430))                 # светлая охра панели между ячейками
    colw, rowh, left, top = 2 * 128 + 3 * 12, 128 + 24, 170, 78
    cols = [("cur", "нынешний (скрипт)")] + VARIANTS
    W = left + len(cols) * colw + 20
    ui_top = top + len(ITEMS) * rowh + 60
    cw = CELL_BOX[2] - CELL_BOX[0]
    H = ui_top + 40 + len(cols) * (cw + 14) + 30
    sheet = Image.new("RGBA", (W, H), panel_bg)
    d = ImageDraw.Draw(sheet)
    d.text((16, 14), "A-005 — проба стиля значков: форма как Factorio, цвет как Terraria (Z-Image base, 15.11)",
           fill=(50, 30, 20), font=font(26, True))
    for ci, (_, label) in enumerate(cols):
        d.text((left + ci * colw + 8, top - 16), label, fill=(50, 30, 20), font=font(18, True))
        d.text((left + ci * colw + 8, top + 6), "64 px          128 px", fill=(90, 60, 40), font=font(15))
    for ri, (key, label) in enumerate(ITEMS):
        y = top + 26 + ri * rowh
        d.text((16, y + 50), label, fill=(50, 30, 20), font=font(20))
        for ci, (v, _) in enumerate(cols):
            x = left + ci * colw + 12
            if v == "cur":
                im = current_icon(key)
                pair = (im, im.resize((128, 128), Image.NEAREST))
            else:
                pair = icons.get((key, v))
            d.rectangle((x - 4, y - 4, x + 64 + 4 + 12 + 128, y + 128 + 4), outline=(150, 110, 60))
            if pair is None:
                d.text((x + 10, y + 50), "нет картинки", fill=(150, 40, 40), font=font(18))
                continue
            sheet.alpha_composite(pair[0], (x, y + 32))
            sheet.alpha_composite(pair[1], (x + 64 + 12, y))
    # в интерфейсе игры, масштаб 1080p: ячейка как на снимке, значок 48 px (из 128)
    d.text((16, ui_top), "В ячейках сумки, 1:1 при 1080p (ячейка и фон — со снимка accept_E2-9_inv.png)",
           fill=(50, 30, 20), font=font(22, True))
    for ci, (v, label) in enumerate(cols):
        y = ui_top + 40 + ci * (cw + 14)
        d.text((16, y + 18), label, fill=(50, 30, 20), font=font(17))
        for ri, (key, _) in enumerate(ITEMS):
            x = 300 + ri * (cw + 8)
            sheet.alpha_composite(cell, (x, y))
            if v == "cur":
                ic = current_icon(key).resize((48, 48), Image.LANCZOS)
            else:
                pair = icons.get((key, v))
                if pair is None:
                    continue
                ic = fit_from(pair[1], 48)
            sheet.alpha_composite(ic, (x + (cw - 48) // 2, y + (cw - 48) // 2))
    SHEET.parent.mkdir(parents=True, exist_ok=True)
    sheet.convert("RGB").save(SHEET, optimize=True)
    print(f"лист: {SHEET}")


def fit_from(im128: Image.Image, size: int) -> Image.Image:
    small = im128.convert("RGBa").resize((size, size), Image.LANCZOS).convert("RGBA")
    rgb = small.convert("RGB").filter(ImageFilter.UnsharpMask(radius=0.7, percent=80, threshold=1))
    return Image.merge("RGBA", (*rgb.split(), small.getchannel("A")))


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--raw", required=True)
    p.add_argument("--only", nargs="*")
    a = p.parse_args()
    raw = Path(a.raw)
    OUT.mkdir(parents=True, exist_ok=True)
    icons = {}
    for key, _ in ITEMS:
        for v, _ in VARIANTS:
            k = f"{key}_{v}"
            src = raw / f"{k}.png"
            if src.exists() and (not a.only or k in a.only):
                cut = cut_green(Image.open(src))
                cut.save(raw / f"{k}_cut.png")
                big, small = fit(cut, 128), fit(cut, 64)
                big.save(OUT / f"{k}_128.png", optimize=True)
                small.save(OUT / f"{k}_64.png", optimize=True)
                print(f"{k}: готово")
            if (OUT / f"{k}_128.png").exists():
                icons[(key, v)] = (Image.open(OUT / f"{k}_64.png").convert("RGBA"),
                                   Image.open(OUT / f"{k}_128.png").convert("RGBA"))
    build_sheet(icons)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
