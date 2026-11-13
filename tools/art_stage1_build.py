"""Набор заглушек этапа 1 (карточка E1-9): сырые картинки Мака → ассеты в client/Assets/Art/_TEMP, манифест, лист.

    python tools/art_stage1_build.py [--raw ПАПКА] [--only ключ …] [--no-sheet]

Сырые картинки — ПАПКА/<ключ>.png (их кладёт tools/art_stage1.py collect; в git не хранятся).
Что делает:
  земля и вода  — середина «плитки в рамке» → 512 × 512 → гашение световых пятен → бесшовность швом наименьшей
                  разницы (перекрытие краёв, путь по минимуму отличий) → TEMP_ground_*.png / TEMP_water_*.png, БЕЗ уголка;
  объекты       — фон и нарисованная тень → альфа (art_process.cut_object) → холст из таблицы OBJECTS, опора — низ
                  по центру, пурпурный уголок;
  обрыв         — из полосы «лицо обрыва» (бесшовной по горизонтали, 512 × 192) и текстуры верха (каменистая земля)
                  собираются сегменты 512 × 512 под сетку 4 × 4 тайла: маска площадки → лицо под южной кромкой →
                  контур, кромка, тень у подножия;
  лист          — work/screens/E1-9_sheet.png: сцена в экранном масштабе 30 px на тайл, весь набор мелко и в 100 %.
Нет сырой картинки — ассет рисуется скриптом (простые формы в палитре стиля), в манифесте пометка «скрипт».
"""
from __future__ import annotations

import argparse
import csv
import random
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parent))
import art_process as ap  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
TEMP_DIR = ROOT / "client" / "Assets" / "Art" / "_TEMP"
MANIFEST = ROOT / "art" / "manifest.csv"
SHEET = ROOT / "work" / "screens" / "E1-9_sheet.png"
TILE = 128
OUTLINE = (49, 11, 29)
OWNER = "А"

# ключ → (имя файла, подпись, запасной цвет A, запасной цвет B)
FLOORS = {
    "ground_grass": ("TEMP_ground_grass.png", "земля «трава луговая»", (112, 166, 42), (145, 214, 70)),
    "ground_drygrass": ("TEMP_ground_drygrass.png", "земля «сухая трава»", (201, 172, 78), (226, 200, 110)),
    "ground_dirt": ("TEMP_ground_dirt.png", "земля «голая земля»", (150, 96, 62), (172, 118, 78)),
    "ground_forest": ("TEMP_ground_forest.png", "земля «лесная подстилка»", (84, 92, 44), (128, 96, 48)),
    "ground_sand": ("TEMP_ground_sand.png", "земля «песок»", (232, 206, 140), (244, 224, 168)),
    "ground_rocky": ("TEMP_ground_rocky.png", "земля «каменистая»", (176, 168, 150), (204, 198, 180)),
    "water_deep": ("TEMP_water_deep.png", "вода глубокая", (22, 92, 104), (34, 116, 124)),
    "water_shallow": ("TEMP_water_shallow.png", "вода, отмель", (72, 176, 180), (120, 210, 206)),
}

# ключ → (имя файла, размер холста, подпись, вид запасной формы)
OBJECTS = {
    "tree_olive_a": ("TEMP_tree_olive_a.png", (256, 384), "дерево «олива», вариант 1", "tree"),
    "tree_olive_b": ("TEMP_tree_olive_b.png", (256, 384), "дерево «олива», вариант 2", "tree"),
    "tree_pine_a": ("TEMP_tree_pine_a.png", (256, 384), "дерево «пиния», вариант 1", "tree"),
    "tree_pine_b": ("TEMP_tree_pine_b.png", (256, 384), "дерево «пиния», вариант 2", "tree"),
    "tree_oak_a": ("TEMP_tree_oak_a.png", (256, 384), "дерево «дуб», вариант 1", "tree"),
    "tree_oak_b": ("TEMP_tree_oak_b.png", (256, 384), "дерево «дуб», вариант 2", "tree"),
    "tree_cypress_a": ("TEMP_tree_cypress_a.png", (256, 384), "дерево «кипарис», вариант 1", "cypress"),
    "tree_cypress_b": ("TEMP_tree_cypress_b.png", (256, 384), "дерево «кипарис», вариант 2", "cypress"),
    "tree_oak_autumn": ("TEMP_tree_oak_autumn.png", (256, 384), "дерево «дуб», осенний", "autumn"),
    "bush_a": ("TEMP_bush_a.png", (128, 128), "куст, вариант 1", "bush"),
    "bush_b": ("TEMP_bush_b.png", (128, 128), "куст с ягодами, вариант 2", "bush"),
    "boulder_a": ("TEMP_boulder_a.png", (192, 192), "валун средний", "rock"),
    "boulder_b": ("TEMP_boulder_b.png", (128, 128), "валун малый", "rock"),
    "boulder_c": ("TEMP_boulder_c.png", (256, 192), "валун широкий", "rock"),
    "ore_copper_a": ("TEMP_ore_copper_a.png", (128, 128), "медная залежь, вариант 1", "ore"),
    "ore_copper_b": ("TEMP_ore_copper_b.png", (192, 128), "медная залежь, вариант 2", "ore"),
    "stump": ("TEMP_stump.png", (128, 128), "пень", "stump"),
}

# сегменты обрыва: имя → подпись. Площадка — «плато» P; земля — всё остальное (прозрачно).
CLIFFS = {
    "s": "прямой горизонтальный, лицом на юг",
    "n": "прямой горизонтальный, задняя (северная) кромка",
    "w": "прямой вертикальный, западная сторона",
    "e": "прямой вертикальный, восточная сторона",
    "outer_sw": "внешний угол юго-западный", "outer_se": "внешний угол юго-восточный",
    "outer_nw": "внешний угол северо-западный", "outer_ne": "внешний угол северо-восточный",
    "inner_sw": "внутренний угол, земля на юго-западе", "inner_se": "внутренний угол, земля на юго-востоке",
    "inner_nw": "внутренний угол, земля на северо-западе", "inner_ne": "внутренний угол, земля на северо-востоке",
    "end_w": "торец: стенка сходит на нет к западу", "end_e": "торец: стенка сходит на нет к востоку",
}
FACE_H = 192          # высота стенки, 1,5 тайла
EDGE = 128            # кромка площадки — в тайле от края ячейки
CELL = 512
TOP_GAIN, TOP_LIFT = 1.06, 6.0   # верх площадки чуть светлее каменистой земли внизу


# ---------------------------------------------------------------- бесшовность швом наименьшей разницы

def _blur(a: np.ndarray, radius: float) -> np.ndarray:
    """Размытие массива 0…1 (Pillow не размывает режим F — через 8 бит)."""
    im = Image.fromarray(np.clip(a * 255 + 0.5, 0, 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(radius))
    return np.asarray(im).astype(np.float32) / 255


def _seam_path(cost: np.ndarray, cyclic: bool) -> np.ndarray:
    """Путь сверху вниз по матрице cost (строки × ширина полосы), шаг в сторону не больше 1 px."""
    h, b = cost.shape
    c = cost.astype(np.float64).copy()
    if cyclic:  # начало и конец — в середине полосы: путь замкнётся через край
        big = c.max() * h + 1
        for r in (0, h - 1):
            c[r, : b // 2] += big
            c[r, b // 2 + 1:] += big
    acc = c.copy()
    for y in range(1, h):
        prev = acc[y - 1]
        left = np.concatenate([[np.inf], prev[:-1]])
        right = np.concatenate([prev[1:], [np.inf]])
        acc[y] += np.minimum(prev, np.minimum(left, right))
    path = np.zeros(h, np.int64)
    path[-1] = int(np.argmin(acc[-1]))
    for y in range(h - 2, -1, -1):
        x = path[y + 1]
        lo, hi = max(0, x - 1), min(b, x + 2)
        path[y] = lo + int(np.argmin(acc[y, lo:hi]))
    return path


def _wrap_x(a: np.ndarray, n: int, cyclic: bool, feather: float = 1.5) -> np.ndarray:
    """a шириной n + b → ширина n, бесшовно по горизонтали: в полосе слева продолжение правого края (a[:, n:])
    сменяется началом (a[:, :b]) вдоль пути наименьшей разницы."""
    b = a.shape[1] - n
    A, B = a[:, n:], a[:, :b]
    diff = ((A - B) ** 2).sum(axis=2)
    diff = _blur(diff / max(float(diff.max()), 1e-6), 2)
    path = _seam_path(diff, cyclic)
    m = (np.arange(b)[None, :] >= path[:, None]).astype(np.float32)   # 0 — A, 1 — B
    m = _blur(m, feather)
    m[:, 0], m[:, -1] = 0.0, 1.0
    out = a[:, :n].copy()
    out[:, :b] = A * (1 - m[..., None]) + B * m[..., None]
    return out


def seamless(src: Image.Image, n: int = 512, band: int = 96, vertical: bool = True) -> Image.Image:
    """Квадрат (n + band) → n × n, бесшовный по обеим осям (vertical=False — только по горизонтали)."""
    a = np.asarray(src.convert("RGB")).astype(np.float32)
    a = _wrap_x(a, n, cyclic=False)
    if vertical:
        a = _wrap_x(a.transpose(1, 0, 2), n, cyclic=True).transpose(1, 0, 2)
    return Image.fromarray(np.clip(a + 0.5, 0, 255).astype(np.uint8))


def make_floor(src: Image.Image, inset: float = 0.06, flatten: float = 0.8) -> Image.Image:
    inner = ap.floor_interior(src, inset)
    band = 96
    work = inner.resize((512 + band, 512 + band), Image.LANCZOS)
    if flatten:
        work = ap.flatten_light(work, strength=flatten, radius=64)
    return seamless(work, 512, band)


def tile2x2(im: Image.Image) -> Image.Image:
    w, h = im.size
    out = Image.new("RGB", (w * 2, h * 2))
    for y in range(2):
        for x in range(2):
            out.paste(im.convert("RGB"), (x * w, y * h))
    return out


# ---------------------------------------------------------------- запасные картинки скриптом

def _blob(d, rng, cx, cy, rx, ry, fill, n=7):
    for _ in range(n):
        x, y = cx + rng.uniform(-rx, rx) * 0.5, cy + rng.uniform(-ry, ry) * 0.5
        r1, r2 = rx * rng.uniform(0.45, 0.7), ry * rng.uniform(0.45, 0.7)
        d.ellipse([x - r1, y - r2, x + r1, y + r2], fill=fill)


def fallback_floor(key: str, rng: random.Random) -> Image.Image:
    _, _, c1, c2 = FLOORS[key]
    n = 512
    im = Image.new("RGB", (n, n), c1)
    d = ImageDraw.Draw(im)
    for _ in range(46):
        x, y = rng.randrange(n), rng.randrange(n)
        rx, ry = rng.randint(18, 46), rng.randint(10, 26)
        for ox in (-n, 0, n):          # рисуем с переносом через край — бесшовно
            for oy in (-n, 0, n):
                d.ellipse([x - rx + ox, y - ry + oy, x + rx + ox, y + ry + oy], fill=c2)
    return im


def fallback_object(key: str, rng: random.Random) -> Image.Image:
    _, (W, H), _, kind = OBJECTS[key]
    S = 4
    im = Image.new("RGBA", (W * S, H * S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    w, h = W * S, H * S
    pal = {"tree": ((39, 103, 77), (112, 166, 42)), "cypress": ((24, 72, 62), (39, 103, 77)),
           "autumn": ((149, 57, 32), (235, 178, 41)), "bush": ((39, 103, 77), (112, 166, 42)),
           "rock": ((122, 132, 140), (222, 216, 196)), "ore": ((100, 77, 76), (59, 147, 133)),
           "stump": ((100, 62, 40), (224, 186, 120))}[kind]
    dark, light = pal
    mask = Image.new("L", (w, h), 0)
    md = ImageDraw.Draw(mask)
    if kind in ("tree", "autumn", "cypress"):
        tw = w * 0.07
        md.rectangle([w / 2 - tw, h * 0.6, w / 2 + tw, h * 0.985], fill=255)
        rx, ry = (w * 0.2, h * 0.36) if kind == "cypress" else (w * 0.44, h * 0.3)
        _blob(md, rng, w / 2, h * 0.4, rx, ry, 255, 9)
    elif kind == "stump":
        md.rectangle([w * 0.2, h * 0.45, w * 0.8, h * 0.9], fill=255)
        md.ellipse([w * 0.2, h * 0.3, w * 0.8, h * 0.6], fill=255)
        md.ellipse([w * 0.08, h * 0.72, w * 0.92, h * 0.98], fill=255)
    else:
        _blob(md, rng, w / 2, h * 0.62, w * 0.44, h * 0.34, 255, 8)
    m = np.asarray(mask) > 127
    ys, xs = np.nonzero(m)
    top, bot = ys.min(), ys.max()
    yy, xx = np.mgrid[0:h, 0:w]
    lit = (yy - top) / max(1, bot - top) + (xx / w - 0.5) * 0.5 < 0.45
    rgb = np.zeros((h, w, 4), np.uint8)
    rgb[m & lit] = (*light, 255)
    rgb[m & ~lit] = (*dark, 255)
    if kind in ("tree", "autumn", "cypress"):
        trunk = m & (yy > h * 0.62) & (np.abs(xx - w / 2) < w * 0.075)
        rgb[trunk] = (161, 126, 98, 255)
    if kind == "ore":
        for _ in range(7):
            x, y = rng.uniform(0.25, 0.75) * w, rng.uniform(0.4, 0.85) * h
            sel = m & ((xx - x) ** 2 / (w * 0.07) ** 2 + (yy - y) ** 2 / (h * 0.04) ** 2 < 1)
            rgb[sel] = (*rng.choice([(113, 217, 167), (214, 120, 48)]), 255)
    edge = ap.grow(m, 2 * S) & ~ap.shrink(m, 1 * S)
    rgb[edge] = (*OUTLINE, 255)
    return Image.fromarray(rgb).convert("RGBa").resize((W, H), Image.LANCZOS).convert("RGBA")


def fallback_face(rng: random.Random) -> Image.Image:
    im = Image.new("RGB", (CELL, FACE_H), (176, 140, 100))
    d = ImageDraw.Draw(im)
    x = 0
    while x < CELL:
        wd = rng.randint(40, 90)
        col = rng.choice([(196, 160, 116), (176, 140, 100), (150, 116, 88)])
        d.rectangle([x, 0, min(CELL, x + wd) - 1, FACE_H], fill=col)
        d.line([x, 0, x + rng.randint(-6, 6), FACE_H], fill=OUTLINE, width=4)
        x += wd
    return im


# ---------------------------------------------------------------- объекты

def make_object(src: Image.Image, size: tuple[int, int], holes: bool) -> tuple[Image.Image, dict]:
    """holes — искать ли просветы цвета фона внутри силуэта (нужно только кронам: у светлого камня на светлом
    фоне за «дыру» принимается верхняя грань — слабое место конвейера, STYLE.md п. 11)."""
    old = ap.HOLE_TOL
    ap.HOLE_TOL = 12 if holes else -1
    try:
        cut, info = ap.cut_object(src)
    finally:
        ap.HOLE_TOL = old
    W, H = size
    out = ap.fit_object(cut, size, base=H - 2, shadow=False, margin=3)
    return out, info


# ---------------------------------------------------------------- обрыв

def _wob(t: np.ndarray, seed: int, amp: float = 7.0) -> np.ndarray:
    """Волна кромки: целые частоты по ячейке → на границах ячеек ноль, сегменты стыкуются."""
    rng = random.Random(seed)
    out = np.zeros_like(t, dtype=np.float32)
    for k in (2, 3, 5, 8):
        out += rng.uniform(0.4, 1.0) / (k ** 0.5) * np.sin(2 * np.pi * k * t / CELL) * rng.choice((-1, 1))
    return out * amp


PAD = 64   # поле вокруг ячейки: кромка и тень считаются с продолжением к соседям, потом обрезаются


def plateau_mask(name: str) -> np.ndarray:
    """Маска площадки на поле (CELL + 2·PAD)²: формулы кромок продолжаются за ячейку так же, как у соседа."""
    n = CELL + 2 * PAD
    yy, xx = np.mgrid[0:n, 0:n].astype(np.float32) - PAD
    s = yy < EDGE + _wob(xx, 11)              # площадка севернее южной кромки
    nn = yy >= EDGE + _wob(xx, 12)
    w = xx >= EDGE + _wob(yy, 13, 3.0)        # площадка восточнее западной кромки
    e = xx < CELL - EDGE + _wob(yy, 14, 3.0)
    m = {"s": s, "n": nn, "w": w, "e": e,
         "outer_sw": s & w, "outer_se": s & e, "outer_nw": nn & w, "outer_ne": nn & e,
         "inner_sw": s | w, "inner_se": s | e, "inner_nw": nn | w, "inner_ne": nn | e,
         "end_w": s, "end_e": s}[name]
    # скруглить углы: размытие и порог
    big = np.pad(m.astype(np.float32), 48, mode="edge")
    return _blur(big, 14)[48:-48, 48:-48] > 0.5


def make_cliff(name: str, top: Image.Image, face: Image.Image) -> Image.Image:
    P = plateau_mask(name)
    n = P.shape[0]
    idx = (np.arange(n) - PAD) % CELL
    topa = np.asarray(top.convert("RGB")).astype(np.float32)[idx][:, idx]
    facea = np.asarray(face.convert("RGB")).astype(np.float32)[:, idx]
    xx = np.arange(n) - PAD
    # высота стенки по столбцам: у торцов сходит на нет
    hcol = np.full(n, FACE_H, np.float32)
    if name == "end_w":
        hcol = np.clip((xx - 24) / (CELL - 24), 0, 1) ** 0.8 * FACE_H
    if name == "end_e":
        hcol = np.clip((CELL - 24 - xx) / (CELL - 24), 0, 1) ** 0.8 * FACE_H
    # расстояние вниз от южной кромки площадки: d[y, x] = на сколько px выше лежит ближайший пиксель площадки
    d = np.full((n, n), 9999, np.int32)
    last = np.full(n, -9999, np.int32)
    for y in range(n):
        last = np.where(P[y], y, last)
        d[y] = y - last
    is_face = (~P) & (d >= 1) & (d <= hcol[None, :])
    body = P | is_face
    out = np.zeros((n, n, 4), np.float32)
    # тень: у подножия стенки и справа от восточной кромки (свет сверху слева)
    sh = np.zeros((n, n), bool)
    sh[26:, 22:] = body[:-26, :-22]
    shadow = sh & ~body
    out[shadow] = (24, 10, 20, 255 * 0.34)
    # верх
    lit_top = np.clip(topa * TOP_GAIN + TOP_LIFT, 0, 255)
    out[P, :3] = lit_top[P]
    out[P, 3] = 255
    # лицо: строка полосы — по глубине (растянуто на высоту столбца), нижняя часть темнее (вторая ступень)
    ys, xs = np.nonzero(is_face)
    relv = (d[ys, xs] - 1) / np.maximum(hcol[xs], 1)
    v = np.clip(relv * (FACE_H - 1), 0, FACE_H - 1).astype(np.int32)
    out[ys, xs, :3] = facea[v, xs] * np.where(relv > 0.62, 0.78, 1.0)[:, None]
    out[ys, xs, 3] = 255
    # светлая кромка у южного края площадки и контур
    lip = P.copy()
    lip[:-10] &= ~P[10:]
    lip[-10:] = False
    out[lip, :3] = np.clip(out[lip, :3] * 1.12 + 14, 0, 255)
    edge_p = P & ~ap.shrink(P, 2)             # край площадки, 2 px внутрь
    edge_b = body & ~ap.shrink(body, 2)       # низ и бока стенки
    brow = is_face & (d <= 3)                 # линия под кромкой
    line = edge_p | edge_b | brow
    out[line, :3] = OUTLINE
    out[line, 3] = 255
    out = out[PAD:-PAD, PAD:-PAD]
    return Image.fromarray(np.clip(out + 0.5, 0, 255).astype(np.uint8), "RGBA")


def make_face(src: Image.Image | None, rng: random.Random) -> Image.Image:
    if src is None:
        return fallback_face(rng)
    band = 96
    work = src.convert("RGB").resize((CELL + band, FACE_H), Image.LANCZOS)
    return seamless(work, CELL, band, vertical=False)


# ---------------------------------------------------------------- манифест, сохранение

def set_manifest(entries: list[tuple[str, str]]) -> None:
    with MANIFEST.open(encoding="utf-8", newline="") as f:
        rows = list(csv.DictReader(f))
    by = {r["path"]: r for r in rows}
    for path, note in entries:
        if path in by:
            by[path]["notes"] = note
        else:
            rows.append({"path": path, "status": "placeholder", "owner": OWNER, "notes": note})
    with MANIFEST.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=["path", "status", "owner", "notes"], lineterminator="\n")
        w.writeheader()
        w.writerows(rows)


def rel(p: Path) -> str:
    return p.relative_to(ROOT).as_posix()


# ---------------------------------------------------------------- лист набора

def _font(px):
    try:
        return ImageFont.truetype("arial.ttf", px)
    except OSError:
        return ImageFont.load_default()


def small(im: Image.Image, k: float) -> Image.Image:
    size = (max(1, round(im.width * k)), max(1, round(im.height * k)))
    return im.convert("RGBa").resize(size, Image.LANCZOS).convert("RGBA")


def build_scene(made: dict[str, Image.Image], px: int = 30, tw: int = 64, th: int = 36) -> Image.Image:
    """Проба в экранном масштабе: 64 × 36 тайлов по 30 px. Раскладка от руки, зерно постоянное."""
    k = px / TILE
    rng = random.Random(19)
    W, H = tw * px, th * px
    cell = round(CELL * k)

    def tex(name):
        t = small(made[name], k).convert("RGB")
        out = Image.new("RGB", (W, H))
        for y in range(0, H, t.height):
            for x in range(0, W, t.width):
                out.paste(t, (x, y))
        return out

    # карта видов земли по тайлам
    kind = [["ground_grass"] * tw for _ in range(th)]
    for y in range(th):
        for x in range(tw):
            if x < 14 - y * 0.25:
                kind[y][x] = "water_deep" if x < 9 - y * 0.25 else "water_shallow"
            elif x < 17 - y * 0.25:
                kind[y][x] = "ground_sand"
            elif x > 46 and y > 20:
                kind[y][x] = "ground_forest"
            elif 24 < x < 40 and y > 24:
                kind[y][x] = "ground_drygrass"
            elif 20 < x < 30 and 12 < y < 20:
                kind[y][x] = "ground_dirt"
            elif x > 50 and y < 8:
                kind[y][x] = "ground_rocky"
    scene = Image.new("RGB", (W, H))
    layers = {n: tex(n) for n in FLOORS}
    for y in range(th):
        for x in range(tw):
            box = (x * px, y * px, (x + 1) * px, (y + 1) * px)
            scene.paste(layers[kind[y][x]].crop(box), box)
    scene = scene.convert("RGBA")

    def put(name, tx, ty):   # опора — низ по центру, в точке (tx, ty) в тайлах
        s = small(made[name], k)
        scene.alpha_composite(s, (max(0, round(tx * px - s.width / 2)), max(0, round(ty * px - s.height))))

    def put_cell(name, cx, cy):   # сегмент обрыва: левый верхний угол ячейки в тайлах
        scene.alpha_composite(small(made["cliff_" + name], k), (cx * px, cy * px))

    # плато 3 × 2 ячейки с выемкой: внешние углы, прямые, один внутренний угол, торец
    gx, gy = 28, 1
    grid = [["outer_nw", "n", "n", "outer_ne"],
            ["w", "full", "inner_se", "outer_se"],
            ["outer_sw", "s", "outer_se", None]]   # выемка на юго-востоке: внутренний угол между «s» справа и «e» снизу
    for r, row in enumerate(grid):
        for c, name in enumerate(row):
            if name == "full":
                box = ((gx + c * 4) * px, (gy + r * 4) * px, (gx + c * 4 + 4) * px, (gy + r * 4 + 4) * px)
                scene.paste(small(made["cliff_top"], k).convert("RGB").crop((8, 8, cell, cell)), (box[0] + 8, box[1] + 8))
            elif name:
                put_cell(name, gx + c * 4, gy + r * 4)
    put_cell("end_w", 46, 12)
    put_cell("s", 50, 12)
    put_cell("end_e", 54, 12)
    objs = []
    trees = [n for n in OBJECTS if n.startswith("tree")]
    for _ in range(70):
        x, y = rng.uniform(18, 63), rng.uniform(3, 35.5)
        if 27 < x < 45 and y < 15 or (45 < x < 59 and 11 < y < 18):
            continue
        if x > 46 and y > 20:
            name = rng.choice(["tree_oak_a", "tree_oak_b", "tree_pine_a", "tree_pine_b", "tree_oak_autumn", "bush_a"])
        else:
            name = rng.choice(trees + ["bush_a", "bush_b", "boulder_a", "boulder_b", "boulder_c", "ore_copper_a",
                                       "ore_copper_b", "stump"])
        objs.append((y, x, name))
    for i, n in enumerate(OBJECTS):   # каждый объект хотя бы раз — рядком на лугу
        objs.append((22.5 if i < 9 else 24.0, 19.5 + (i % 9) * 2.4 + (0 if i < 9 else 1), n))
    for y, x, name in sorted(objs):
        put(name, x, y)
    if "char" in made:
        for x, y in ((22, 10), (44, 19), (36, 30)):
            s = small(made["char"], k)
            scene.alpha_composite(s, (round(x * px - s.width / 2), round(y * px - s.height)))
    return scene.convert("RGB")


def build_sheet(made: dict[str, Image.Image], scripted: set[str]) -> None:
    green = (111, 143, 78)
    k = 30 / TILE
    f, fb = _font(20), _font(30)
    groups = [("Земля и вода, 512 × 512 (4 × 4 тайла), бесшовные", list(FLOORS)),
              ("Деревья 256 × 384", [n for n in OBJECTS if n.startswith("tree")]),
              ("Кусты, валуны, медь, пень", [n for n in OBJECTS if not n.startswith("tree")]),
              ("Обрыв: сегменты 512 × 512, полоса лица 512 × 192, верх", ["cliff_" + n for n in CLIFFS][:7]),
              ("", ["cliff_" + n for n in CLIFFS][7:] + ["cliff_face", "cliff_top"])]
    scene = build_scene(made)
    gap = 16
    rows = []
    for title, names in groups:
        hmax = max(made[n].height for n in names)
        rows.append((title, names, hmax))
    width = max(scene.width, max(sum(made[n].width + gap for n in names) for _, names, _ in rows)) + 2 * gap
    height = 60 + scene.height + sum(44 + round(h * k) + 30 + h + 30 for _, _, h in rows) + 2 * gap
    sheet = Image.new("RGB", (width, height), green)
    d = ImageDraw.Draw(sheet)
    d.text((gap, 12), "E1-9: набор этапа 1. Сцена в экранном масштабе — 30 px на тайл, 64 × 36 тайлов", fill="white", font=fb)
    sheet.paste(scene, (gap, 56))
    y = 56 + scene.height + gap
    for title, names, hmax in rows:
        if title:
            d.text((gap, y + 6), title + " — сверху 30 px на тайл, ниже 100 %", fill="white", font=fb)
        y += 44
        x = gap
        for n in names:
            im = made[n].convert("RGBA")
            s = small(im, k)
            sheet.paste(s, (x, y + round(hmax * k) - s.height), s)
            sheet.paste(im, (x, y + round(hmax * k) + 30 + hmax - im.height), im)
            label = n + (" (скрипт)" if n in scripted else "")
            d.text((x, y + round(hmax * k) + 30 + hmax + 4), label, fill="white", font=f)
            x += im.width + gap
        y += round(hmax * k) + 30 + hmax + 30
    SHEET.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(SHEET, optimize=True)
    print(f"лист: {SHEET} {sheet.width}x{sheet.height}")


# ---------------------------------------------------------------- сборка

def main() -> int:
    arg = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    arg.add_argument("--raw", default=str(ROOT / "build" / "art_e19" / "raw"))
    arg.add_argument("--only", nargs="*", help="только эти ключи (лист не собирается)")
    arg.add_argument("--no-sheet", action="store_true")
    arg.add_argument("--preview", help="папка для проверочных картинок (склейки 2 × 2 земли)")
    a = arg.parse_args()
    raw = Path(a.raw)
    TEMP_DIR.mkdir(parents=True, exist_ok=True)
    made: dict[str, Image.Image] = {}
    scripted: set[str] = set()
    entries: list[tuple[str, str]] = []
    want = set(a.only) if a.only else None

    def source(key):
        p = raw / f"{key}.png"
        return Image.open(p) if p.exists() else None

    for i, (key, (fname, title, _, _)) in enumerate(FLOORS.items()):
        if want and key not in want:
            continue
        src = source(key)
        if src is None:
            im = fallback_floor(key, random.Random(1900 + i))
            scripted.add(key)
            how = "скрипт (tools/art_stage1_build.py), простые пятна"
        else:
            im = make_floor(src)
            how = f"стиль Б, Мак (Z-Image base), набор art/style/sets/e19.json, ключ {key}; шов — обработкой"
        im.save(TEMP_DIR / fname)
        made[key] = im
        ratio = ap.seam_ratio(im)
        entries.append((rel(TEMP_DIR / fname), f"{title} 512x512 (4x4 тайла), бесшовная, без уголка (E1-9); {how}"))
        print(f"{fname}: шов {ratio:.2f}" + (" (скрипт)" if key in scripted else ""))
        if a.preview:
            Path(a.preview).mkdir(parents=True, exist_ok=True)
            tile2x2(im).save(Path(a.preview) / f"{key}_2x2.png")

    for i, (key, (fname, size, title, _)) in enumerate(OBJECTS.items()):
        if want and key not in want:
            continue
        src = source(key)
        if src is None:
            im = fallback_object(key, random.Random(1950 + i))
            scripted.add(key)
            how = "скрипт (tools/art_stage1_build.py), простая форма"
        else:
            im, info = make_object(src, size, holes=key.startswith("tree"))
            how = f"стиль Б, Мак (Z-Image base), набор art/style/sets/e19.json, ключ {key}"
            print(f"{fname}: фон {info['bg']}, дыр {info['holes']}, покрытие {info['cover']}")
        im = ap.mark(im)
        im.save(TEMP_DIR / fname)
        made[key] = im
        entries.append((rel(TEMP_DIR / fname),
                        f"{title} {size[0]}x{size[1]}, опора — низ по центру, без тени (E1-9); {how}"))

    if not want or any(k.startswith("cliff") for k in want):
        top = made.get("ground_rocky") or Image.open(TEMP_DIR / FLOORS["ground_rocky"][0])
        fsrc = source("cliff_face_strip")
        face = make_face(fsrc, random.Random(1971))
        if fsrc is None:
            scripted.add("cliff_face")
        fhow = ("полоса лица — Мак (Z-Image base), вырезка и шов обработкой" if fsrc is not None
                else "полоса лица — скрипт")
        face_m = ap.mark(face.convert("RGBA"))
        face_m.save(TEMP_DIR / "TEMP_cliff_face.png")
        made["cliff_face"] = face_m
        entries.append((rel(TEMP_DIR / "TEMP_cliff_face.png"),
                        f"обрыв: полоса «лицо» 512x192, бесшовная по горизонтали (E1-9); {fhow}"))
        topa = np.asarray(top.convert("RGB")).astype(np.float32)
        full = Image.fromarray(np.clip(topa * TOP_GAIN + TOP_LIFT + 0.5, 0, 255).astype(np.uint8)).convert("RGBA")
        full = ap.mark(full)
        full.save(TEMP_DIR / "TEMP_cliff_top.png")
        made["cliff_top"] = full
        entries.append((rel(TEMP_DIR / "TEMP_cliff_top.png"),
                        "обрыв: середина площадки 512x512 (4x4 тайла), та же текстура, что верх сегментов (E1-9); "
                        "из TEMP_ground_rocky, чуть светлее"))
        for name, title in CLIFFS.items():
            im = ap.mark(make_cliff(name, top, face))
            fname = f"TEMP_cliff_{name}.png"
            im.save(TEMP_DIR / fname)
            made["cliff_" + name] = im
            entries.append((rel(TEMP_DIR / fname),
                            f"обрыв: {title}, 512x512 (4x4 тайла), земля прозрачная (E1-9); собран скриптом "
                            f"tools/art_stage1_build.py из TEMP_cliff_face и TEMP_ground_rocky; {fhow}"))
        print(f"обрыв: {len(CLIFFS)} сегментов + полоса лица")

    set_manifest(entries)
    if not want and not a.no_sheet:
        char = TEMP_DIR / "TEMP_char_orpheus.png"
        if char.exists():
            made["char"] = Image.open(char).convert("RGBA")
        build_sheet(made, scripted)
    print(f"готово: картинок {len(entries)}, из них скриптом: {sorted(scripted) or 'нет'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
