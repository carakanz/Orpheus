"""Обработка картинок нейросети в ассеты игры (карточка A-002; docs/08_roadmap.md 5.3 п. 5, форма — 5.1).

    python tools/art_process.py object SRC OUT [--size 128x192] [--base 184] [--no-shadow] [--temp]
    python tools/art_process.py floor  SRC OUT [--size 128] [--temp]
    python tools/art_process.py seam   PNG                 # проверка шва пола (отношение ≤ 1,5 — шва нет)
    python tools/art_process.py a002 [--candidates]        # эталоны → client/Assets/Art/_TEMP + лист сверки
    python tools/art_process.py palette                    # опорные цвета с эталонов (для STYLE.md)
    python tools/art_process.py assets SET.json [SET.json ...] [--sheet PNG]   # набор Мака → TEMP-ассеты (1-024)

Объект и персонаж (стиль Б: каждый предмет обведён тёмным сливовым контуром, фон — светлый серый, тень — серая):
  1. контур = пиксели темнее L 60; расширяем на 3 px, чтобы закрыть щели;
  2. заливка от краёв кадра по всему, что не контур, — это фон и нарисованная тень, они уходят в альфу;
  3. дыры внутри силуэта цвета фона (между веток, в лире) — тоже в альфу;
  4. маску сужаем обратно на 3 px, края — сглаживание 1 px; обрезка по силуэту;
  5. уменьшение с учётом альфы до размера ассета; опора — внизу по центру (`--base` — строка опоры, 00 5.1);
  6. своя тень: эллипс у опоры со сдвигом вправо-вниз, свет сверху-слева (08 5.1) — одна на все ассеты.
Пол: из картинки вырезается внутренняя часть «плитки в рамке» (модель рисует пол с рамкой, STYLE.md),
делается бесшовной (сдвиг на половину + смешивание краёв полосой 1/4) и уменьшается до 128 × 128.
`--temp` — пурпурный уголок #FF00FF 8 × 8 px слева сверху (правило временных ассетов, CLAUDE.md).
Покрытие `cover` — доля кадра под силуэтом после выреза. Меньше MIN_COVER (0,10) — ошибка: так выглядит
провал выреза (серый предмет на сером фоне: грани приняты за фон и дыры, остаётся 2–3 % кадра). Тонким
предметам (стрела, лук) порог задаётся явно: `--min-cover` или поле `min_cover` предмета в наборе.
Ассеты набора (`assets`, 1-024): у предмета набора — список `assets` с видом `mode`:
  object — персонаж или объект на тайле (как выше); fill — стена, забор, дверь: силуэт на всю ширину тайла
  (широкий — срезается по краям до пропорции), без своей тени; held — оружие в руке: длинная ось ставится
  вертикально, верхний на картинке конец — вверх, рукоять внизу по центру; icon — иконка: по центру, без тени;
  floor — пол. `scale` — доля от наибольшего размера (мелкий предмет на тайле).
"""
from __future__ import annotations

import argparse
import csv
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
CHOSEN = ROOT / "art" / "style" / "chosen"
PROBES = ROOT / "art" / "style" / "probes"
TEMP_DIR = ROOT / "client" / "Assets" / "Art" / "_TEMP"
MANIFEST = ROOT / "art" / "manifest.csv"
SHEET = ROOT / "art" / "style" / "sheet_a002.png"

OUTLINE_L = 60        # контур стиля Б ≈ (50, 12, 30), L < 40; тень и фон — L 100…190
CLOSE_PX = 3          # на сколько расширять контур, чтобы заливка фона не протекла в щель
HOLE_TOL = 22         # дыра внутри силуэта: цвет в пределах этого от цвета фона (по каждому каналу в среднем)
HOLE_MIN = 120        # и не меньше этой площади, px исходника 1024
MARK = (255, 0, 255, 255)
MARK_SIZE = 8
SHADOW_ALPHA = 0.32
SEAM_MAX = 1.5
MIN_COVER = 0.10      # меньше — вырез провалился (backlog «[арт]», П по A-002: серый валун на сером — 2,7 %)


class CoverError(ValueError):
    """Силуэт после выреза занимает слишком малую долю кадра — вырез провалился."""


def check_cover(info: dict, min_cover: float = MIN_COVER) -> None:
    if info["cover"] < min_cover:
        raise CoverError(f"cover {info['cover']:.3f} < {min_cover:.2f}: силуэт потерян при вырезе "
                         f"(фон {tuple(info['bg'])}, дыр {info['holes']}) — предмет цвета фона? "
                         f"перегенерировать на контрастном фоне")


def lum(a: np.ndarray) -> np.ndarray:
    return a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114


def gray(arr: np.ndarray) -> Image.Image:
    """Изображение L из массива копией: на Image.fromarray (Pillow 12) ImageDraw.floodfill молча ничего не делает."""
    a = np.ascontiguousarray(arr, dtype=np.uint8)
    return Image.frombytes("L", (a.shape[1], a.shape[0]), a.tobytes())


def grow(mask: np.ndarray, px: int) -> np.ndarray:
    if px <= 0:  # MaxFilter(1) / MinFilter(1) роняют Pillow 12 делением на ноль
        return mask
    im = Image.fromarray((mask * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(px * 2 + 1))
    return np.asarray(im) > 127


def shrink(mask: np.ndarray, px: int) -> np.ndarray:
    if px <= 0:
        return mask
    im = Image.fromarray((mask * 255).astype(np.uint8)).filter(ImageFilter.MinFilter(px * 2 + 1))
    return np.asarray(im) > 127


def components(mask: np.ndarray, min_area: int):
    """Связные области маски не меньше min_area (заливкой PIL; мелочь сначала срезается открытием 3 × 3)."""
    work = grow(shrink(mask, 1), 1) if min_area > 9 else mask
    im = gray(np.where(work, 255, 0))
    out = []
    flat = np.asarray(im)
    while True:
        idx = np.flatnonzero(flat == 255)
        if idx.size == 0:
            break
        y, x = divmod(int(idx[0]), im.width)
        ImageDraw.floodfill(im, (x, y), 128, thresh=0)
        flat = np.asarray(im)
        comp = flat == 128
        if comp.sum() >= min_area:
            out.append(comp)
        im.paste(0, mask=Image.fromarray((comp * 255).astype(np.uint8)))
        flat = np.asarray(im)
    return out


def cut_object(src: Image.Image) -> tuple[Image.Image, dict]:
    """Фон и нарисованная тень → альфа. Возвращает RGBA того же размера и сведения для учёта."""
    rgb = np.asarray(src.convert("RGB")).astype(np.float32)
    h, w = rgb.shape[:2]
    barrier = grow(lum(rgb) < OUTLINE_L, CLOSE_PX)
    # заливка фона от краёв: поле с рамкой в 1 px, чтобы фон был связным по краю
    field = np.full((h + 2, w + 2), 255, np.uint8)
    field[1:-1, 1:-1] = np.where(barrier, 0, 255)
    im = gray(field)
    ImageDraw.floodfill(im, (0, 0), 128, thresh=0)
    bg = np.asarray(im)[1:-1, 1:-1] == 128
    bg_color = np.median(rgb[bg], axis=0) if bg.any() else np.array([180, 175, 170], np.float32)
    obj = ~bg
    # дыры цвета фона внутри силуэта
    hole_like = obj & ~barrier & (np.abs(rgb - bg_color).mean(axis=2) < HOLE_TOL)
    holes = 0
    for comp in components(hole_like, HOLE_MIN):
        obj &= ~grow(comp, CLOSE_PX)  # дыра вместе с полосой, которую отрезало расширение контура
        holes += 1
    obj = shrink(obj, CLOSE_PX)
    # убрать крошки: открытие 5 × 5 и только области ≥ 0,05 % кадра
    obj = grow(shrink(obj, 2), 2)
    keep = np.zeros_like(obj)
    for comp in components(obj, int(h * w * 0.0005)):
        keep |= comp
    alpha = Image.fromarray((keep * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.8))
    out = src.convert("RGB").convert("RGBA")
    out.putalpha(alpha)
    info = {"bg": [int(c) for c in bg_color], "holes": holes, "cover": round(float(keep.mean()), 3)}
    return out, info


def resize_rgba(im: Image.Image, size: tuple[int, int]) -> Image.Image:
    return im.convert("RGBa").resize(size, Image.LANCZOS).convert("RGBA")


def fit_object(cut: Image.Image, size: tuple[int, int], base: int, shadow: bool, margin: int = 2,
               scale: float = 1.0) -> Image.Image:
    """Обрезка по силуэту, уменьшение, опора внизу по центру на строке base, своя тень вниз-вправо."""
    bbox = cut.getchannel("A").point(lambda v: 255 if v > 24 else 0).getbbox()
    crop = cut.crop(bbox)
    W, H = size
    k = min((W - 2 * margin) / crop.width, (base - margin) / crop.height) * scale
    nw, nh = max(1, round(crop.width * k)), max(1, round(crop.height * k))
    small = resize_rgba(crop, (nw, nh))
    a = np.asarray(small.getchannel("A")).astype(np.float32) / 255
    # центр по опоре: середина нижних 12 % силуэта, а не всей рамки (лира, ветки сбоку не сдвигают ноги)
    rows = max(2, nh // 8)
    cols = np.flatnonzero(a[-rows:].max(axis=0) > 0.5)
    foot_cx = (cols[0] + cols[-1]) / 2 if cols.size else nw / 2
    foot_w = (cols[-1] - cols[0] + 1) if cols.size else nw
    x0 = round(W / 2 - foot_cx)
    x0 = min(max(x0, margin - 0), W - nw - margin) if nw <= W - 2 * margin else (W - nw) // 2
    y0 = base - nh
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    if shadow:
        sh = Image.new("L", (W * 4, H * 4), 0)
        ew, eh = max(foot_w * 1.15, W * 0.35), max(foot_w * 0.32, 8)
        cx, cy = x0 + foot_cx + foot_w * 0.12, base - eh * 0.25
        ImageDraw.Draw(sh).ellipse([(cx - ew / 2) * 4, (cy - eh / 2) * 4, (cx + ew / 2) * 4, (cy + eh / 2) * 4],
                                   fill=int(255 * SHADOW_ALPHA))
        sh = sh.resize(size, Image.LANCZOS).filter(ImageFilter.GaussianBlur(1.5))
        layer = Image.new("RGBA", size, (24, 10, 20, 0))
        layer.putalpha(sh)
        canvas = Image.alpha_composite(canvas, layer)
    piece = Image.new("RGBA", size, (0, 0, 0, 0))
    piece.paste(small, (x0, y0))
    return Image.alpha_composite(canvas, piece)


def floor_interior(src: Image.Image, inset: float = 0.06) -> Image.Image:
    """Внутренняя часть «плитки в рамке»: рамка картинки = всё, что не фон; отступ внутрь — inset стороны."""
    rgb = np.asarray(src.convert("RGB")).astype(np.float32)
    h, w = rgb.shape[:2]
    border = np.concatenate([rgb[:6].reshape(-1, 3), rgb[-6:].reshape(-1, 3), rgb[:, :6].reshape(-1, 3),
                             rgb[:, -6:].reshape(-1, 3)])
    bg = np.median(border, axis=0)
    chroma = rgb.max(axis=2) - rgb.min(axis=2)
    bg_chroma = float(bg.max() - bg.min())
    # фон серый: пол — всё, что заметно цветнее или далеко от цвета фона
    ground = (chroma > bg_chroma + 25) | (np.abs(rgb - bg).mean(axis=2) > 45)
    rows = np.flatnonzero(ground.mean(axis=1) > 0.6)
    cols = np.flatnonzero(ground.mean(axis=0) > 0.6)
    if rows.size == 0 or cols.size == 0 or (rows[-1] - rows[0]) > h * 0.98 and (cols[-1] - cols[0]) > w * 0.98:
        y0, y1, x0, x1 = 0, h, 0, w  # пол во весь кадр — рамки нет
    else:
        y0, y1, x0, x1 = rows[0], rows[-1] + 1, cols[0], cols[-1] + 1
    side = min(y1 - y0, x1 - x0)
    pad = int(side * inset)
    side -= 2 * pad
    cy, cx = (y0 + y1) // 2, (x0 + x1) // 2
    return src.convert("RGB").crop((cx - side // 2, cy - side // 2, cx - side // 2 + side, cy - side // 2 + side))


def make_seamless(tile: Image.Image, band: float = 0.25) -> Image.Image:
    """Сдвиг на половину и смешивание: у краёв — сдвинутая копия (она бесшовна по краям), в середине — исходник."""
    a = np.asarray(tile.convert("RGB")).astype(np.float32)
    n = a.shape[0]
    rolled = np.roll(a, (n // 2, n // 2), axis=(0, 1))
    d = np.minimum(np.arange(n), n - 1 - np.arange(n)) / (n * band)
    t = np.clip(d, 0, 1)
    s = t * t * (3 - 2 * t)                       # smoothstep
    m = np.outer(s, s)[..., None]                 # 0 у краёв, 1 внутри
    out = rolled * (1 - m) + a * m
    return Image.fromarray(np.clip(out + 0.5, 0, 255).astype(np.uint8))


def seam_ratio(im: Image.Image) -> float:
    """Перепад через край (последний столбец → первый, низ → верх) к среднему перепаду соседних столбцов внутри."""
    rgba = np.asarray(im.convert("RGBA"))
    a = rgba[..., :3].astype(np.float32)
    # пурпурный уголок заглушки (8 × 8 слева сверху) — не пол: строки и столбцы с ним не сравниваем
    k = MARK_SIZE if (rgba[:MARK_SIZE, :MARK_SIZE] == MARK).all() else 0
    inner = (np.abs(np.diff(a[k:], axis=1)).mean() + np.abs(np.diff(a[:, k:], axis=0)).mean()) / 2
    edge = (np.abs(a[k:, -1] - a[k:, 0]).mean() + np.abs(a[-1, k:] - a[0, k:]).mean()) / 2
    return float(edge / max(inner, 1e-6))


def flatten_light(im: Image.Image, strength: float = 0.9, radius: int = 12) -> Image.Image:
    """Убрать крупные световые пятна (модель кладёт на пол «сияние»): вычесть отклонение размытой копии
    от среднего. Плоский цвет стиля Б и меньше заметен повтор тайла."""
    a = np.asarray(im.convert("RGB")).astype(np.float32)
    low = np.asarray(im.convert("RGB").filter(ImageFilter.GaussianBlur(radius))).astype(np.float32)
    out = a - strength * (low - low.reshape(-1, 3).mean(axis=0))
    return Image.fromarray(np.clip(out + 0.5, 0, 255).astype(np.uint8))


def make_floor(src: Image.Image, size: int = 128) -> Image.Image:
    inner = floor_interior(src)
    work = flatten_light(inner.resize((512, 512), Image.LANCZOS))
    tile = make_seamless(work).resize((size, size), Image.LANCZOS)
    return tile.convert("RGBA")


def mark(im: Image.Image) -> Image.Image:
    im = im.copy()
    ImageDraw.Draw(im).rectangle([0, 0, MARK_SIZE - 1, MARK_SIZE - 1], fill=MARK)
    return im


def parse_size(s: str) -> tuple[int, int]:
    if "x" in s:
        w, h = s.lower().split("x")
        return int(w), int(h)
    return int(s), int(s)


# ---------------------------------------------------------------- рецепт A-002

# заглушка → (источник, вид, размер, строка опоры). Источник — эталон из art/style/chosen (в git):
# ассет воспроизводится из репозитория. Опора у объектов на тайле — центр нижнего тайла 128 × 192 (pivot y = 1/3),
# поэтому подошва объекта ниже центра: валун занимает тайл почти целиком, ствол — в нижней половине тайла.
A002 = {
    "TEMP_tile_meadow.png": ("chosen/ref_meadow.png", "floor", (128, 128), None),
    "TEMP_char_orpheus.png": ("chosen/ref_orpheus.png", "object", (128, 192), 186),
    "TEMP_obj_tree.png": ("chosen/ref_tree.png", "object", (128, 192), 176),
    "TEMP_obj_rock.png": ("chosen/ref_rock.png", "object", (128, 192), 182),
}
# лишь для листа сверки (в игре волка пока нет)
EXTRA = {"wolf": ("chosen/ref_wolf.png", "object", (128, 128), 122)}
SUBJECT = {"TEMP_tile_meadow.png": "meadow", "TEMP_char_orpheus.png": "orpheus", "TEMP_obj_tree.png": "tree",
           "TEMP_obj_rock.png": "rock", "TEMP_tile_stone.png": "stone"}
NOTE = {"meadow": "пол «луг» 128x128", "orpheus": "персонаж 128x192, опора внизу по центру",
        "tree": "объект «дерево» 128x192, опора — центр нижнего тайла",
        "rock": "объект «скала» 128x192, опора — центр нижнего тайла", "stone": "пол «камень» 128x128"}


def src_path(rel: str) -> Path:
    p = Path(rel)
    if p.is_absolute():
        return p
    for base in (ROOT / "art" / "style", ROOT):
        if (base / rel).exists():
            return base / rel
    return ROOT / "art" / "style" / rel


def process(src: Path, kind: str, size: tuple[int, int], base: int | None,
            min_cover: float = MIN_COVER) -> tuple[Image.Image, dict]:
    im = Image.open(src)
    if kind == "floor":
        out = make_floor(im, size[0])
        return out, {"seam": round(seam_ratio(out), 2)}
    cut, info = cut_object(im)
    check_cover(info, min_cover)
    return fit_object(cut, size, base or size[1] - 4, shadow=True), info


def set_manifest(path: str, note: str) -> None:
    with MANIFEST.open(encoding="utf-8", newline="") as f:
        rows = list(csv.DictReader(f))
    for r in rows:
        if r["path"] == path:
            r["notes"] = note
            r["status"] = "placeholder"
    with MANIFEST.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=["path", "status", "owner", "notes"], lineterminator="\n")
        w.writeheader()
        w.writerows(rows)


def candidates() -> dict[str, list[tuple[str, Path]]]:
    """Новые варианты с Мака (набор a002 и пол set2) — art/style/probes/b_inkcel/*@a002_N.png, если скачаны."""
    found: dict[str, list[tuple[str, Path]]] = {}
    d = PROBES / "b_inkcel"
    for subj in ("orpheus", "meadow", "rock", "tree", "wolf", "stone"):
        files = sorted(d.glob(f"{subj}@a002_[0-9].png"))
        if subj == "meadow":
            files = sorted(d.glob("meadow@set2_[0-9].png")) + files
        if subj != "stone":
            files = [p for p in sorted(d.glob(f"{subj}_[0-9].png")) if p.name != f"{subj}_1.png"] + files
        found[subj] = [(p.stem, p) for p in files]
    return found


SPEC = {"orpheus": ("object", (128, 192), 186), "tree": ("object", (128, 192), 176),
        "rock": ("object", (128, 192), 182), "wolf": ("object", (128, 128), 122),
        "meadow": ("floor", (128, 128), None), "stone": ("floor", (128, 128), None)}


def cmd_a002(with_candidates: bool, stone: str | None) -> int:
    log = {}
    made: dict[str, Image.Image] = {}
    recipe = dict(A002)
    if stone:
        recipe["TEMP_tile_stone.png"] = (stone, "floor", (128, 128), None)
    for name, (rel, kind, size, base) in recipe.items():
        src = src_path(rel)
        out, info = process(src, kind, size, base)
        made[SUBJECT[name]] = out
        final = mark(out)
        final.save(TEMP_DIR / name, format="PNG")
        srel = src.relative_to(ROOT).as_posix()
        set_manifest(f"client/Assets/Art/_TEMP/{name}",
                     f"{NOTE[SUBJECT[name]]}; стиль Б, A-002, источник {srel}, обработка tools/art_process.py")
        log[name] = {"src": srel, **info}
        print(f"{name}: {srel} {info}")
    for subj, (rel, kind, size, base) in EXTRA.items():
        made[subj], info = process(src_path(rel), kind, size, base)
        print(f"{subj} (только лист): {info}")
    seam = seam_ratio(made["meadow"])
    print(f"шов луга: {seam:.2f} (≤ {SEAM_MAX} — шва нет)")
    cand = {}
    if with_candidates:
        for subj, files in candidates().items():
            for stem, p in files:
                kind, size, base = SPEC[subj]
                try:
                    out, info = process(p, kind, size, base)
                except Exception as e:  # кандидат не обязан проходить конвейер — это и проверяем
                    print(f"кандидат {stem}: не обработан ({e})")
                    continue
                cand.setdefault(subj, []).append((stem, out))
                print(f"кандидат {subj}/{stem}: {info}")
    build_sheet(made, cand)
    print(f"лист сверки: {SHEET.relative_to(ROOT).as_posix()}")
    return 0 if seam <= SEAM_MAX else 1


# ---------------------------------------------------------------- лист сверки (08 5.3 п. 7)

def on_floor(asset: Image.Image, floor: Image.Image, tile_px: int, kind: str) -> Image.Image:
    """Ассет на полу луга при tile_px на тайл: пол 3 × 2 тайла (3 × 3 — для пола), ассет на среднем тайле."""
    k = tile_px / 128
    ft = floor.convert("RGB").resize((tile_px, tile_px), Image.LANCZOS)
    cols, rows = 3, 3 if kind == "floor" else 2 + (asset.height > 128)
    bg = Image.new("RGB", (cols * tile_px, rows * tile_px))
    src_tile = asset.convert("RGB").resize((tile_px, tile_px), Image.LANCZOS) if kind == "floor" else ft
    for y in range(rows):
        for x in range(cols):
            bg.paste(src_tile, (x * tile_px, y * tile_px))
    if kind != "floor":
        a = resize_rgba(asset, (round(asset.width * k), round(asset.height * k)))
        # опора объекта — центр нижнего тайла (128 × 192) или низ (128 × 128): ставим на тайл (1, rows-1)
        bg.paste(a, (tile_px, rows * tile_px - a.height), a)
    return bg


def build_sheet(made: dict[str, Image.Image], cand: dict[str, list[tuple[str, Image.Image]]]) -> None:
    from PIL import ImageFont
    try:
        font = ImageFont.truetype("arial.ttf", 15)
        small = ImageFont.truetype("arial.ttf", 12)
    except OSError:
        font = small = ImageFont.load_default()
    floor = made["meadow"]
    order = ["meadow", "stone", "orpheus", "tree", "rock", "wolf"]
    refs = {"meadow": "ref_meadow", "orpheus": "ref_orpheus", "tree": "ref_tree", "rock": "ref_rock", "wolf": "ref_wolf"}
    pad, ref_px = 12, 240
    rows = []
    for subj in order:
        if subj not in made and subj not in cand:
            continue
        kind = SPEC[subj][0]
        cells = []
        if subj in refs:
            cells.append(("эталон " + refs[subj], Image.open(CHOSEN / f"{refs[subj]}.png").convert("RGB")
                          .resize((ref_px, ref_px), Image.LANCZOS)))
        if subj in made:
            cells.append(("ассет 48 px", on_floor(made[subj], floor, 48, kind)))
            cells.append(("ассет 96 px", on_floor(made[subj], floor, 96, kind)))
        for stem, im in cand.get(subj, []):
            cells.append((f"{stem} 48 px", on_floor(im, floor, 48, kind)))
            cells.append((f"{stem} 96 px", on_floor(im, floor, 96, kind)))
        rows.append((subj, cells))
    probe = ImageDraw.Draw(Image.new("RGB", (1, 1)))
    width = max(pad + 110 + sum(max(c.width, int(probe.textlength(t, font=small))) + pad for t, c in cells)
                for _, cells in rows)
    heights = [max(c.height for _, c in cells) + 24 for _, cells in rows]  # без уменьшения: 96 px — это 96 px
    top = pad + 44
    sheet = Image.new("RGB", (max(width, 900), top + sum(h + pad for h in heights)), (244, 241, 234))
    d = ImageDraw.Draw(sheet)
    d.text((pad, pad), "A-002 · стиль Б · ассеты на полу луга при 48 и 96 px на тайл (экран 1080p и 4K) рядом с эталонами",
           fill=(43, 38, 32), font=font)
    d.text((pad, pad + 20), "подписи с именем файла — другие картинки модели (set1 _0, set2, набор a002) через тот же конвейер",
           fill=(122, 113, 102), font=small)
    y = top
    for (subj, cells), rh in zip(rows, heights):
        d.text((pad, y + 4), subj, fill=(43, 38, 32), font=font)
        x = pad + 110
        for label, im in cells:
            sheet.paste(im, (x, y + rh - 22 - im.height))
            d.text((x, y + rh - 18), label, fill=(122, 113, 102), font=small)
            x += max(im.width, int(d.textlength(label, font=small))) + pad
        y += rh + pad
    SHEET.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(SHEET, optimize=True)


# ---------------------------------------------------------------- ассеты набора (1-024)

def bbox_crop(cut: Image.Image) -> Image.Image:
    return cut.crop(cut.getchannel("A").point(lambda v: 255 if v > 24 else 0).getbbox())


def fit_fill(cut: Image.Image, size: tuple[int, int], base: int, fill_h: int, fit: str = "crop") -> Image.Image:
    """Стена, забор, дверь: силуэт на всю ширину тайла, низ — на строке base, высота fill_h, без своей тени.
    fit=crop — широкий силуэт срезается по краям до пропорции W : fill_h (кусок стены соединяется с соседним);
    fit=stretch — сжимается целиком (забор: столбы по краям должны остаться)."""
    crop = bbox_crop(cut)
    W, H = size
    want = W / fill_h
    if fit == "crop" and crop.width / crop.height > want:
        w = round(crop.height * want)
        x0 = (crop.width - w) // 2
        crop = crop.crop((x0, 0, x0 + w, crop.height))
    piece = resize_rgba(crop, (W, fill_h))
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    canvas.paste(piece, (0, base - fill_h))
    return canvas


def upright(cut: Image.Image) -> Image.Image:
    """Длинную ось силуэта — вертикально (главные оси маски), верхний на картинке конец — вверх.
    Модель рисует предмет по диагонали рукоятью вниз-влево: поворот на 45° ставит рукоять вниз."""
    a = np.asarray(cut.getchannel("A").resize((256, 256), Image.BILINEAR)) > 128
    ys, xs = np.nonzero(a)
    if xs.size < 10:
        return cut
    vals, vecs = np.linalg.eigh(np.cov(np.stack([xs, ys]).astype(np.float64)))
    vx, vy = vecs[:, int(np.argmax(vals))] * np.array([cut.width / 256, cut.height / 256])
    if vy > 0 or (abs(vy) < 1e-9 and vx < 0):
        vx, vy = -vx, -vy  # ось — к верхнему концу (y вниз)
    phi = np.degrees(np.arctan2(-vy, vx))
    rot = cut.convert("RGBa").rotate(90 - phi, resample=Image.BICUBIC, expand=True).convert("RGBA")
    return rot


def fit_center(cut: Image.Image, size: tuple[int, int], margin: int, scale: float, anchor: str) -> Image.Image:
    """Силуэт в рамку с отступом margin, доля scale; anchor=center — по центру, bottom — низ на нижнем отступе."""
    crop = bbox_crop(cut)
    W, H = size
    k = min((W - 2 * margin) / crop.width, (H - 2 * margin) / crop.height) * scale
    small = resize_rgba(crop, (max(1, round(crop.width * k)), max(1, round(crop.height * k))))
    x0 = (W - small.width) // 2
    y0 = H - margin - small.height if anchor == "bottom" else (H - small.height) // 2
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    canvas.paste(small, (x0, y0))
    return canvas


def render_asset(src_im: Image.Image, cut: Image.Image | None, spec: dict) -> Image.Image:
    size = tuple(spec["size"])
    mode = spec["mode"]
    scale = float(spec.get("scale", 1.0))
    if mode == "floor":
        return make_floor(src_im, size[0])
    if mode == "object":
        return fit_object(cut, size, spec.get("base") or size[1] - 4, shadow=spec.get("shadow", True), scale=scale)
    if mode == "fill":
        return fit_fill(cut, size, spec.get("base", size[1]), spec.get("fill_h", size[1]), spec.get("fit", "crop"))
    if mode == "held":
        return fit_center(upright(cut), size, 2, scale, spec.get("anchor", "bottom"))
    if mode == "icon":
        return fit_center(cut, size, 6, scale, "center")
    raise ValueError(f"неизвестный mode {mode!r}")


def upsert_manifest(path: str, note: str, owner: str = "А") -> None:
    with MANIFEST.open(encoding="utf-8", newline="") as f:
        rows = list(csv.DictReader(f))
    for r in rows:
        if r["path"] == path:
            r.update(status="placeholder", notes=note)
            break
    else:
        rows.append({"path": path, "status": "placeholder", "owner": owner, "notes": note})
    with MANIFEST.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=["path", "status", "owner", "notes"], lineterminator="\n")
        w.writeheader()
        w.writerows(rows)


def load_set_file(path: str) -> dict:
    p = Path(path)
    if not p.is_absolute() and not p.exists():
        p = ROOT / p
    data = json.loads(p.read_text(encoding="utf-8"))
    data["_file"] = p.resolve().relative_to(ROOT).as_posix()
    return data


def set_image(data: dict, subj: dict, n: int) -> Path:
    """Картинка набора, скачанная tools/mac_art.py collect: probes/<вариант>/<предмет>@<набор>_<n>.png."""
    return PROBES / data["variants"][0]["key"] / f"{subj['key']}@{data['set']}_{n}.png"


def cmd_assets(set_files: list[str], sheet: Path, review: Path | None) -> int:
    ledger = json.loads((PROBES / "ledger.json").read_text(encoding="utf-8")) if (PROBES / "ledger.json").exists() else []
    job = {(j["set"], j["subject"]): j for j in ledger if j.get("status") != "error"}
    made: list[tuple[str, dict, Image.Image]] = []
    waiting: list[tuple[str, dict]] = []
    failed: list[str] = []
    cand: list[tuple[str, list[tuple[str, Image.Image]]]] = []
    for f in set_files:
        data = load_set_file(f)
        for subj in data["subjects"]:
            group = subj.get("group") or data["set"]
            pick = int(subj.get("pick", 0))
            src = set_image(data, subj, pick)
            j = job.get((data["set"], subj["key"]), {})
            if review is not None:
                alts = []
                for n in range(int(data.get("count", 1))):
                    p = set_image(data, subj, n)
                    if not p.exists():
                        continue
                    try:
                        im = Image.open(p)
                        c = None
                        if subj["kind"] != "floor":
                            c, info = cut_object(im)
                            check_cover(info, float(subj.get("min_cover", MIN_COVER)))
                        alts.append((f"_{n}", render_asset(im, c, subj["assets"][0])))
                    except CoverError as e:
                        print(f"  кандидат {subj['key']}_{n}: {e}")
                if alts:
                    cand.append((subj["key"], alts))
            if not src.exists():
                for spec in subj["assets"]:
                    waiting.append((group, spec))
                print(f"{subj['key']}: ждёт Мака ({j.get('id', 'нет задания')}, {j.get('status', '—')})")
                continue
            im = Image.open(src)
            cut = None
            if subj["kind"] != "floor":
                cut, info = cut_object(im)
                try:
                    check_cover(info, float(subj.get("min_cover", MIN_COVER)))
                except CoverError as e:
                    failed.append(f"{subj['key']}: {e}")
                    print(f"{subj['key']}: ОШИБКА {e}")
                    for spec in subj["assets"]:
                        waiting.append((group, spec))
                    continue
            else:
                info = {}
            srel = src.relative_to(ROOT).as_posix()
            for spec in subj["assets"]:
                out = render_asset(im, cut, spec)
                if spec["mode"] == "floor":
                    info = {"seam": round(seam_ratio(out), 2)}
                mark(out).save(TEMP_DIR / spec["name"], format="PNG")
                upsert_manifest(f"client/Assets/Art/_TEMP/{spec['name']}",
                                f"{spec['note']}; стиль Б, 1-024, источник {srel} (Мак, задание {j.get('id', '?')}, "
                                f"зерно {subj['seed']}, набор {data['_file']}), обработка tools/art_process.py")
                made.append((group, spec, out))
                print(f"{spec['name']}: {srel} {info}")
    build_sheet_1024(made, waiting, sheet)
    print(f"готово ассетов: {len(made)}, ждут Мака: {len(waiting)}, ошибок: {len(failed)}; лист: {sheet}")
    if review is not None:
        build_review(cand, review)
        print(f"кандидаты: {review}")
    return 1 if failed else 0


def _font(px: int):
    from PIL import ImageFont
    try:
        return ImageFont.truetype("arial.ttf", px)
    except OSError:
        return ImageFont.load_default()


def scene(assets: list[Image.Image], floor: Image.Image, tile_px: int, cols: int | None = None) -> Image.Image:
    """Ассеты в ряд на полу луга при tile_px на тайл, по одному на тайл (опора — середина нижнего тайла)."""
    n = len(assets)
    cols = cols or n + 2
    rows = 1 + max(1, max(-(-a.height // 128) for a in assets))
    ft = floor.convert("RGB").resize((tile_px, tile_px), Image.LANCZOS)
    bg = Image.new("RGB", (cols * tile_px, rows * tile_px))
    for y in range(rows):
        for x in range(cols):
            bg.paste(ft, (x * tile_px, y * tile_px))
    k = tile_px / 128
    x0 = (cols - n) // 2
    for i, a in enumerate(assets):
        s = resize_rgba(a, (round(a.width * k), round(a.height * k)))
        bg.paste(s, ((x0 + i) * tile_px, (rows - 1) * tile_px + tile_px - s.height), s)
    return bg


def build_sheet_1024(made: list[tuple[str, dict, Image.Image]], waiting: list[tuple[str, dict]], path: Path) -> None:
    """Лист сверки 1-024 (карточка, шаг 4): все заглушки при 48 px на тайл (экран 1080p) на полу луга."""
    t = 48
    floor = Image.open(TEMP_DIR / "TEMP_tile_meadow.png").convert("RGBA")
    font, small = _font(15), _font(12)
    by = {spec["name"]: im for _, spec, im in made}
    orpheus = Image.open(TEMP_DIR / "TEMP_char_orpheus.png").convert("RGBA")

    def get(name):
        return by.get(name)

    def lab(name):
        return name.removeprefix("TEMP_").removesuffix(".png")

    sections: list[tuple[str, list[tuple[str, Image.Image | None]]]] = []
    names = [spec["name"] for _, spec, _ in made] + [spec["name"] for _, spec in waiting]
    bandits = [n for n in names if n.startswith("TEMP_char_bandit")]
    cells = [("Орфей (A-002, для масштаба)", scene([orpheus], floor, t, 3))]
    for who in ("sword", "archer"):
        row = [get(f"TEMP_char_bandit_{who}_{d}.png") for d in ("down", "up", "side")]
        have = [r for r in row if r is not None]
        side = get(f"TEMP_char_bandit_{who}_side.png")
        if side is not None:
            have.append(side.transpose(Image.FLIP_LEFT_RIGHT))
        ru = {"sword": "мечник", "archer": "лучник"}[who]
        cells.append((f"{ru}: вниз, вверх, вбок, зеркало" if have else f"{ru} — ждёт Мака",
                      scene(have, floor, t) if have else None))
    if bandits:
        mixed = [x for x in (get("TEMP_char_bandit_sword_down.png"), get("TEMP_char_bandit_archer_down.png"),
                             orpheus, get("TEMP_char_bandit_sword_side.png"), get("TEMP_char_bandit_archer_side.png"))
                 if x is not None]
        cells.append(("рядом: мечник, лучник, Орфей", scene(mixed, floor, t)))
    sections.append(("Разбойники", cells))

    def singles(prefix_list):
        out = []
        for n in names:
            if any(n.startswith(p) for p in prefix_list):
                im = get(n)
                out.append((lab(n), scene([im], floor, t, 3) if im is not None else None))
        return out

    sections.append(("Оружие и инструменты в руке", singles(["TEMP_held_", "TEMP_proj_"])))
    icons = []
    for n in names:
        if n.startswith("TEMP_icon_"):
            im = get(n)
            if im is None:
                icons.append((lab(n), None))
                continue
            panel = Image.new("RGB", (t + 16, t + 16), (58, 42, 50))  # тёмная ячейка интерфейса
            s = resize_rgba(im, (t, t))
            panel.paste(s, (8, 8), s)
            icons.append((lab(n), panel))
    sections.append(("Иконки (48 px в ячейке интерфейса)", icons))
    build = []
    for n in ("TEMP_tile_floor_wood.png", "TEMP_tile_floor_stone.png"):
        if n in names:
            im = get(n)
            if im is None:
                build.append((lab(n), None))
            else:
                ft = im.convert("RGB").resize((t, t), Image.LANCZOS)
                bg = Image.new("RGB", (3 * t, 3 * t))
                for y in range(3):
                    for x in range(3):
                        bg.paste(ft, (x * t, y * t))
                build.append((lab(n) + " 3×3", bg))
    for n in ("TEMP_obj_wall_wood.png", "TEMP_obj_wall_stone.png", "TEMP_obj_fence.png"):
        if n in names:
            im = get(n)
            build.append((lab(n) + " ×3 в ряд", scene([im] * 3, floor, t, 5) if im is not None else None))
    wall, door = get("TEMP_obj_wall_wood.png"), get("TEMP_obj_door_closed.png")
    dopen = get("TEMP_obj_door_open.png")
    if wall is not None and door is not None and dopen is not None:
        build.append(("стена, дверь закрыта, открыта, стена", scene([wall, door, dopen, wall], floor, t, 6)))
    build += [x for x in singles(["TEMP_obj_door", "TEMP_obj_torch", "TEMP_obj_debris"])]
    sections.append(("Постройки", build))
    nature = singles(["TEMP_obj_bush", "TEMP_obj_granite", "TEMP_obj_copper"])
    ref = [Image.open(TEMP_DIR / n).convert("RGBA") for n in ("TEMP_obj_rock.png", "TEMP_obj_tree.png")]
    nature.append(("скала и олива (A-002) для сравнения", scene(ref, floor, t, 4)))
    sections.append(("Природа (1-017)", nature))

    pad, maxw = 12, 1500
    probe = ImageDraw.Draw(Image.new("RGB", (1, 1)))
    blank = (t * 3, t * 3)
    layout = []  # (section, [(x, y, label, im)]), height
    y = pad + 44
    for title, cells in sections:
        y += 24
        x, rowh, placed = pad, 0, []
        for label, im in cells:
            w = max((im.width if im else blank[0]), int(probe.textlength(label, font=small)))
            h = (im.height if im else blank[1]) + 20
            if x + w > maxw and placed:
                x, y = pad, y + rowh + pad
                rowh = 0
            placed.append((x, y, label, im, h))
            x += w + pad
            rowh = max(rowh, h)
        layout.append((title, placed))
        y += rowh + pad
    sheet = Image.new("RGB", (maxw + pad, y), (244, 241, 234))
    d = ImageDraw.Draw(sheet)
    d.text((pad, pad), "1-024 · стиль Б · заглушки вехи 1 при 48 px на тайл (экран 1080p), на полу луга",
           fill=(43, 38, 32), font=font)
    d.text((pad, pad + 20), f"готово {len(made)}, ждёт Мака {len(waiting)} · пурпурный уголок 8 px — пометка "
           "заглушки (на 48 px — 3 px) · источник — наборы art/style/sets/1024a.json, 1024b.json",
           fill=(122, 113, 102), font=small)
    for (title, placed) in layout:
        if placed:
            d.text((pad, placed[0][1] - 22), title, fill=(43, 38, 32), font=font)
        for x, yy, label, im, h in placed:
            if im is None:
                d.rectangle([x, yy, x + blank[0] - 1, yy + blank[1] - 1], outline=(190, 180, 168))
                d.text((x + 8, yy + blank[1] // 2 - 6), "ждёт Мака", fill=(150, 140, 128), font=small)
                ih = blank[1]
            else:
                sheet.paste(im, (x, yy))
                ih = im.height
            d.text((x, yy + ih + 3), label, fill=(122, 113, 102), font=small)
    path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(path, optimize=True)


def build_review(cand: list[tuple[str, list[tuple[str, Image.Image]]]], path: Path) -> None:
    """Все скачанные варианты каждого предмета при 96 px на тайл — чтобы выбрать `pick` в наборе."""
    floor = Image.open(TEMP_DIR / "TEMP_tile_meadow.png").convert("RGBA")
    font = _font(12)
    rows = [(k, [(n, scene([im], floor, 96, 2)) for n, im in alts]) for k, alts in cand]
    w = max(220 + sum(c.width + 10 for _, c in cells) for _, cells in rows) if rows else 400
    h = sum(max(c.height for _, c in cells) + 30 for _, cells in rows) + 20 if rows else 100
    sheet = Image.new("RGB", (w, h), (244, 241, 234))
    d = ImageDraw.Draw(sheet)
    y = 10
    for key, cells in rows:
        d.text((10, y + 4), key, fill=(43, 38, 32), font=font)
        x = 220
        for n, c in cells:
            sheet.paste(c, (x, y))
            d.text((x, y + c.height + 2), n, fill=(122, 113, 102), font=font)
            x += c.width + 10
        y += max(c.height for _, c in cells) + 30
    path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(path)


# ---------------------------------------------------------------- палитра (для STYLE.md)

def cmd_palette(k: int = 12) -> None:
    pix = []
    for p in sorted(CHOSEN.glob("ref_*.png")):
        im = Image.open(p)
        if "meadow" in p.name:
            a = np.asarray(floor_interior(im).resize((128, 128))).reshape(-1, 3)
        else:
            cut, _ = cut_object(im)
            arr = np.asarray(cut.resize((256, 256)))
            a = arr[arr[..., 3] > 200][:, :3]
        pix.append(a.astype(np.float32))
    x = np.concatenate(pix)
    rng = np.random.default_rng(1)
    c = x[rng.choice(len(x), k, replace=False)]
    for _ in range(40):
        lab = np.argmin(((x[:, None] - c[None]) ** 2).sum(-1), axis=1)
        c = np.array([x[lab == i].mean(0) if (lab == i).any() else c[i] for i in range(k)])
    share = np.bincount(lab, minlength=k) / len(x)
    for i in np.argsort(-share):
        r, g, b = (int(v) for v in c[i])
        print(f"#{r:02X}{g:02X}{b:02X}  {share[i] * 100:4.1f} %")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    o = sub.add_parser("object")
    o.add_argument("src"); o.add_argument("out")
    o.add_argument("--size", default="128x192"); o.add_argument("--base", type=int)
    o.add_argument("--no-shadow", action="store_true"); o.add_argument("--temp", action="store_true")
    o.add_argument("--min-cover", type=float, default=MIN_COVER, help="порог покрытия (по умолчанию 0,10)")
    f = sub.add_parser("floor")
    f.add_argument("src"); f.add_argument("out")
    f.add_argument("--size", default="128"); f.add_argument("--temp", action="store_true")
    s = sub.add_parser("seam"); s.add_argument("png")
    a = sub.add_parser("a002")
    a.add_argument("--candidates", action="store_true", help="добавить на лист новые варианты Мака")
    a.add_argument("--stone", help="источник пола «камень» (иначе TEMP_tile_stone не трогается)")
    sub.add_parser("palette")
    st = sub.add_parser("assets", help="набор Мака → TEMP-ассеты, манифест, лист сверки (1-024)")
    st.add_argument("sets", nargs="+", help="файлы наборов, например art/style/sets/1024a.json")
    st.add_argument("--sheet", default="work/screens/1-024_sheet.png", help="лист сверки при 48 px")
    st.add_argument("--review", help="ещё лист всех вариантов предмета (выбор pick), вне git")
    args = ap.parse_args()
    if args.cmd == "object":
        size = parse_size(args.size)
        cut, info = cut_object(Image.open(args.src))
        try:
            check_cover(info, args.min_cover)
        except CoverError as e:
            print(f"ошибка: {e}", file=sys.stderr)
            print(json.dumps(info, ensure_ascii=False))
            return 1
        out = fit_object(cut, size, args.base or size[1] - 4, shadow=not args.no_shadow)
        (mark(out) if args.temp else out).save(args.out)
        print(json.dumps(info, ensure_ascii=False))
    elif args.cmd == "floor":
        out = make_floor(Image.open(args.src), parse_size(args.size)[0])
        (mark(out) if args.temp else out).save(args.out)
        print(f"шов {seam_ratio(out):.2f}")
    elif args.cmd == "seam":
        r = seam_ratio(Image.open(args.png))
        print(f"шов {r:.2f} ({'нет' if r <= SEAM_MAX else 'ЕСТЬ'})")
        return 0 if r <= SEAM_MAX else 1
    elif args.cmd == "a002":
        return cmd_a002(args.candidates, args.stone)
    elif args.cmd == "assets":
        sheet = Path(args.sheet) if Path(args.sheet).is_absolute() else ROOT / args.sheet
        return cmd_assets(args.sets, sheet, Path(args.review) if args.review else None)
    else:
        cmd_palette()
    return 0


if __name__ == "__main__":
    sys.exit(main())
