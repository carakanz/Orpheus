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

sys.path.insert(0, str(Path(__file__).resolve().parent))
import art_process as ap  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "art" / "style" / "probes" / "A-005"
SHEET = ROOT / "work" / "screens" / "A-005_probe.png"
TEMP = ROOT / "client" / "Assets" / "Art" / "_TEMP"
GRASS_SHOT = ROOT / "work" / "screens" / "accept_E2-11_wall_in_hand.png"
GRASS_BOX = (1300, 90, 1780, 610)    # чистая трава без объектов, снимок 1080p
CELL, ICON = 48, 40                  # окна C-008: ячейка 48 px, значок в ней
CELL_FILL, CELL_EDGE = (16, 12, 10, 150), (235, 225, 200, 170)   # полупрозрачная тёмная подложка, светлая рамка
NOTES: dict[str, str] = {}           # пометки к строкам листа (например, «форма не удалась»), --note ключ=текст
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


def cut_green(src: Image.Image) -> Image.Image:
    rgb = np.asarray(src.convert("RGB")).astype(np.float32)
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    green = g - np.maximum(r, b)                       # у фона ≈ 150…255, у предмета ≤ 0
    alpha = np.clip((90 - green) / 60, 0, 1)            # мягкий край: 30 → непрозрачно, 90 → прозрачно
    # фон бывает не чисто зелёным (желто-зелёный у варианта В) — ещё и по близости к цвету краёв кадра
    border = np.concatenate([rgb[:8].reshape(-1, 3), rgb[-8:].reshape(-1, 3),
                             rgb[:, :8].reshape(-1, 3), rgb[:, -8:].reshape(-1, 3)])
    dist = np.linalg.norm(rgb - np.median(border, axis=0), axis=2)
    alpha = np.minimum(alpha, np.clip((dist - 40) / 50, 0, 1))   # ≤ 40 от цвета фона → прозрачно, ≥ 90 → непрозрачно
    # крупные области предмета (маска на уменьшенной копии — быстрее), всё прочее — прозрачно
    small = Image.fromarray((alpha > 0.5).astype(np.uint8) * 255).resize((256, 256), Image.NEAREST)
    comps = sorted(ap.components(np.asarray(small) > 0, 1), key=lambda c: -int(c.sum()))
    if comps:
        big = int(comps[0].sum())
        keep = np.zeros_like(comps[0])
        for c in comps:
            if c.sum() >= max(40, big * 0.02):
                keep |= c
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


def grass(w: int, h: int) -> Image.Image:
    tile = Image.open(GRASS_SHOT).convert("RGBA").crop(GRASS_BOX)
    out = Image.new("RGBA", (w, h))
    for y in range(0, h, tile.size[1]):
        for x in range(0, w, tile.size[0]):
            out.paste(tile, (x, y))
    return out


def cell_with(icon: Image.Image | None) -> Image.Image:
    c = Image.new("RGBA", (CELL, CELL), CELL_FILL)
    ImageDraw.Draw(c).rectangle((0, 0, CELL - 1, CELL - 1), outline=CELL_EDGE)
    if icon is not None:
        c.alpha_composite(icon, ((CELL - icon.size[0]) // 2, (CELL - icon.size[1]) // 2))
    return c


def build_sheet(icons: dict[tuple[str, str], tuple[Image.Image, Image.Image]]) -> None:
    panel_bg = (236, 222, 196, 255)
    colw, rowh, left, top = 2 * 128 + 3 * 12, 128 + 24, 190, 78
    cols = [("cur", "нынешний (скрипт)")] + VARIANTS
    W = left + len(cols) * colw + 20
    ui_top = top + len(ITEMS) * rowh + 40
    ui_h = 60 + len(cols) * (CELL + 22) + 20
    H = ui_top + ui_h
    sheet = Image.new("RGBA", (W, H), panel_bg)
    d = ImageDraw.Draw(sheet)
    d.text((16, 14), "A-005 — проба стиля значков: форма как Factorio, цвет как Terraria (Z-Image base, 15.11)",
           fill=(50, 30, 20), font=font(26, True))
    for ci, (_, label) in enumerate(cols):
        d.text((left + ci * colw + 8, top - 24), label, fill=(50, 30, 20), font=font(18, True))
        d.text((left + ci * colw + 8, top - 2), "64 px          128 px", fill=(90, 60, 40), font=font(15))
    for ri, (key, label) in enumerate(ITEMS):
        y = top + 26 + ri * rowh
        d.text((16, y + 44), label, fill=(50, 30, 20), font=font(20))
        if key in NOTES:
            d.multiline_text((16, y + 72), NOTES[key], fill=(170, 40, 30), font=font(15), spacing=2)
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
    # в окне игры (C-008), 1:1 при 1080p: трава мира, полупрозрачная ячейка 48 px, значок 40 px (из 128)
    sheet.alpha_composite(grass(W, ui_h), (0, ui_top))
    plate = Image.new("RGBA", (W, 44), (16, 12, 10, 170))
    sheet.alpha_composite(plate, (0, ui_top))
    d.text((16, ui_top + 8), "В ячейках окна игры (C-008), 1:1 при 1080p: ячейка 48 px поверх травы мира, значок 40 px",
           fill=(245, 235, 215), font=font(22, True))
    for ci, (v, label) in enumerate(cols):
        y = ui_top + 60 + ci * (CELL + 22)
        lab = Image.new("RGBA", (250, 30), (16, 12, 10, 160))
        sheet.alpha_composite(lab, (12, y + 9))
        d.text((20, y + 12), label, fill=(245, 235, 215), font=font(17))
        for ri, (key, _) in enumerate(ITEMS):
            x = 300 + ri * (CELL + 6)
            if v == "cur":
                ic = current_icon(key).resize((ICON, ICON), Image.LANCZOS)
            else:
                pair = icons.get((key, v))
                ic = fit_from(pair[1], ICON) if pair else None
            sheet.alpha_composite(cell_with(ic), (x, y))
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
    p.add_argument("--note", action="append", default=[], help="пометка к строке: ключ=текст (| — перенос строки)")
    a = p.parse_args()
    for n in a.note:
        k, _, txt = n.partition("=")
        NOTES[k] = txt.replace("|", "\n")
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
