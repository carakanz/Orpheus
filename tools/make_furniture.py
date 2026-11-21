"""Заглушки мебели «с высотой» (E2-17): верстак, сундук, кровать, костёр, факел — объёмные спрайты 3/4.

Ракурс — тот же, что у стен (ObjectMesh.WallHeight = 0,5): верх виден без сжатия (глубина 1:1), высота сжата вдвое
(«между Factorio и RimWorld»), у каждого предмета — верхняя грань и лицевая (южная) грань, опора — низ по центру.
Стиль — BRIEF.md п. 11g: форма точная, светотень с одного направления (свет слева сверху), материал читается
(дерево, железо, камень, ткань), без толстого контура — тонкая тёмная кромка; цвета яркие фэнтезийные.

Правило заглушек: файлы client/Assets/Art/_TEMP/TEMP_obj_<имя>.png (128 px на тайл), строка в art/manifest.csv со
статусом placeholder, пурпурный уголок #FF00FF 8 x 8 px слева вверху (в игре у объектов мира не рисуется —
ObjectAtlas.ClearMarks). Значки предметов этих построек по-прежнему делает tools/make_item_icons.py.

Запуск: python tools/make_furniture.py
"""
from __future__ import annotations

import csv
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

import furniture_detail as fd  # C-030: подробные верстак, сундук, двуспальная кровать

ROOT = Path(__file__).resolve().parent.parent
TEMP_DIR = ROOT / "client" / "Assets" / "Art" / "_TEMP"
MANIFEST = ROOT / "art" / "manifest.csv"

S = 4                 # рисуем в 4 раза крупнее и уменьшаем — ровные края
W, H = 128, 160       # 128 px на тайл; низ картинки — передняя кромка на земле
GROUND = 157          # передний край основания (px от верха), снизу 3 px запаса
V = 0.5               # сжатие высоты (ObjectMesh.WallHeight): предмет высотой 1 тайл рисуется в 64 px


def mix(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3)) + (255,)


def mul(c, k):
    return tuple(max(0, min(255, int(round(c[i] * k)))) for i in range(3)) + (255,)


class Pen:
    """Рисование в координатах 128-px картинки (умножаются на S)."""

    def __init__(self, w=W, h=H):
        self.img = Image.new("RGBA", (w * S, h * S), (0, 0, 0, 0))
        self.d = ImageDraw.Draw(self.img)

    def rect(self, x0, y0, x1, y1, top, bottom=None, edge=None):
        """Прямоугольник с вертикальным переходом цвета (top — верх, bottom — низ) и тонкой кромкой."""
        bottom = bottom or top
        X0, Y0, X1, Y1 = int(x0 * S), int(y0 * S), int(x1 * S), int(y1 * S)
        n = max(1, Y1 - Y0)
        for i in range(n):
            self.d.line([(X0, Y0 + i), (X1 - 1, Y0 + i)], fill=mix(top, bottom, i / n))
        if edge:
            self.d.rectangle([X0, Y0, X1 - 1, Y1 - 1], outline=edge, width=S)

    def poly(self, pts, fill, edge=None, width=1.0):
        P = [(x * S, y * S) for x, y in pts]
        self.d.polygon(P, fill=fill)
        if edge:
            self.d.line(P + [P[0]], fill=edge, width=max(1, int(width * S)), joint="curve")

    def line(self, pts, fill, width=1.0):
        self.d.line([(x * S, y * S) for x, y in pts], fill=fill, width=max(1, int(width * S)), joint="curve")

    def ell(self, x0, y0, x1, y1, fill, edge=None, width=1.0):
        self.d.ellipse([x0 * S, y0 * S, x1 * S, y1 * S], fill=fill, outline=edge, width=max(1, int(width * S)) if edge else 0)

    def glow(self, cx, cy, r, color, alpha):
        """Мягкое свечение (огонь): размытое пятно под рисунком."""
        layer = Image.new("RGBA", self.img.size, (0, 0, 0, 0))
        ImageDraw.Draw(layer).ellipse([(cx - r) * S, (cy - r) * S, (cx + r) * S, (cy + r) * S], fill=color[:3] + (alpha,))
        layer = layer.filter(ImageFilter.GaussianBlur(r * S * 0.45))
        self.img = Image.alpha_composite(layer, self.img)
        self.d = ImageDraw.Draw(self.img)

    def done(self) -> Image.Image:
        img = self.img.resize((self.img.width // S, self.img.height // S), Image.LANCZOS)
        ImageDraw.Draw(img).rectangle([0, 0, 7, 7], fill=(255, 0, 255, 255))  # пурпурный уголок заглушки
        return img


def box(p: Pen, x0, x1, depth, height, top, face, edge, top_dark=None):
    """Брусок: основание x0..x1, глубина depth (px), высота height (px, уже сжатая). Возвращает (y верха спереди, y верха сзади)."""
    yf = GROUND - height          # передняя кромка верха
    yb = yf - depth               # задняя кромка верха
    p.rect(x0, yf, x1, GROUND, mul(face, 1.06), mul(face, 0.78))
    p.rect(x0, yb, x1, yf, mul(top, 1.04), top_dark or mul(top, 0.9))
    p.line([(x0, yf), (x1, yf)], mul(top, 1.18), 1.2)                           # светлая кромка верх/лицо
    p.d.rectangle([x0 * S, yb * S, x1 * S - 1, GROUND * S - 1], outline=edge, width=S)
    return yf, yb


# ---------- сундук ----------

def chest() -> Image.Image:
    p = Pen()
    wood_t, wood_f = (236, 156, 74), (150, 80, 34)
    iron, iron_l, gold = (70, 74, 98), (156, 164, 192), (255, 204, 64)
    edge = (70, 36, 18, 255)
    x0, x1 = 18, 110
    depth, height = 60, 36
    yf, yb = box(p, x0, x1, depth, height, wood_t, wood_f, edge)
    # Крышка: доски вдоль, середина выпуклая — светлая полоса.
    for k in range(1, 4):
        y = yb + depth * k / 4
        p.line([(x0 + 1, y), (x1 - 1, y)], mul(wood_t, 0.72), 1.0)
    p.rect(x0 + 2, yb + depth * 0.38, x1 - 2, yb + depth * 0.5, mul(wood_t, 1.12), mul(wood_t, 1.02))
    # Лицо: доски поперёк.
    p.line([(x0 + 1, yf + height * 0.5), (x1 - 1, yf + height * 0.5)], mul(wood_f, 0.66), 1.0)
    # Оковка: полосы через крышку и лицо, уголки.
    for bx in (30, 98):
        p.rect(bx - 4, yb, bx + 4, yf, iron_l, iron)
        p.rect(bx - 4, yf, bx + 4, GROUND, mul(iron, 1.15), mul(iron, 0.8))
        p.line([(bx - 3, yb + 1), (bx - 3, yf - 1)], (196, 204, 230, 255), 0.8)
        for ry in (yb + 6, yf - 6, yf + 8, GROUND - 6):
            p.ell(bx - 1.5, ry - 1.5, bx + 1.5, ry + 1.5, (210, 214, 230, 255))
    # Замок.
    p.rect(57, yf - 4, 71, yf + 14, (255, 226, 110), (214, 150, 30), edge=(110, 66, 10, 255))
    p.rect(62.5, yf + 3, 65.5, yf + 9, (40, 24, 12))
    p.line([(x0, GROUND), (x1, GROUND)], (40, 22, 12, 255), 1.5)                # тень у земли
    return p.done()


# ---------- верстак ----------

def workbench() -> Image.Image:
    p = Pen()
    top_c, face_c, leg_c = (238, 178, 104), (176, 108, 54), (150, 88, 44)
    edge = (70, 36, 18, 255)
    x0, x1 = 6, 122
    depth, height, slab = 66, 46, 9
    yf = GROUND - height
    yb = yf - depth
    # Под столешницей: тень, задние ножки, полка.
    p.rect(x0 + 8, yf + slab, x1 - 8, GROUND - 6, (70, 42, 26), (44, 26, 16))
    for lx in (16, 104):
        p.rect(lx + 2, yf + slab, lx + 8, GROUND - 14, mul(leg_c, 0.6), mul(leg_c, 0.45))
    p.rect(x0 + 10, GROUND - 17, x1 - 10, GROUND - 11, (214, 150, 84), (160, 100, 50), edge=edge)   # нижняя полка
    for lx in (9, 107):                                                                   # передние ножки
        p.rect(lx, yf + slab, lx + 12, GROUND, mul(leg_c, 1.1), mul(leg_c, 0.8), edge=edge)
    # Столешница: верх и торец.
    p.rect(x0, yb, x1, yf, mul(top_c, 1.04), mul(top_c, 0.9))
    for k in range(1, 4):
        y = yb + depth * k / 4
        p.line([(x0 + 1, y), (x1 - 1, y)], mul(top_c, 0.74), 1.0)
    p.rect(x0, yf, x1, yf + slab, mul(face_c, 1.08), mul(face_c, 0.85))
    p.line([(x0, yf), (x1, yf)], mul(top_c, 1.2), 1.2)
    p.d.rectangle([x0 * S, yb * S, x1 * S - 1, (yf + slab) * S - 1], outline=edge, width=S)
    # Тиски на правом переднем углу.
    p.rect(100, yf - 14, 118, yf - 4, (196, 202, 222), (120, 126, 150), edge=(40, 42, 56, 255))
    p.rect(104, yf - 4, 114, yf + 8, (150, 156, 180), (96, 100, 122), edge=(40, 42, 56, 255))
    # На столе: молот, медный слиток, пила.
    p.line([(26, yf - 14), (54, yb + 18)], (122, 74, 40, 255), 4.0)                    # рукоять
    p.line([(26, yf - 14), (54, yb + 18)], (176, 118, 66, 255), 2.0)
    p.poly([(48, yb + 10), (62, yb + 16), (58, yb + 26), (44, yb + 20)], (150, 156, 178, 255), (40, 42, 56, 255), 1.0)
    p.poly([(68, yf - 24), (90, yf - 28), (94, yf - 18), (72, yf - 14)], (240, 128, 60, 255), (110, 46, 18, 255), 1.0)
    p.line([(71, yf - 22), (90, yf - 25)], (255, 196, 130, 255), 1.5)
    p.poly([(62, yb + 8), (96, yb + 4), (98, yb + 10), (64, yb + 14)], (206, 212, 230, 255), (60, 62, 80, 255), 1.0)
    p.line([(64, yb + 14), (98, yb + 10)], (90, 94, 112, 255), 0.8)
    p.rect(96, yb + 2, 108, yb + 14, (200, 120, 60), (150, 84, 40), edge=edge)       # ручка пилы
    p.line([(x0, GROUND), (x1, GROUND)], (40, 22, 12, 255), 1.0)
    return p.done()


# ---------- кровать ----------

def bed() -> Image.Image:
    p = Pen()
    frame, frame_d = (176, 92, 48), (120, 58, 30)
    sheet, blanket, blanket_l, pillow = (244, 238, 224), (54, 112, 214), (120, 170, 240), (255, 252, 242)
    edge = (66, 30, 14, 255)
    x0, x1 = 24, 104
    depth, height = 100, 22
    yf = GROUND - height
    yb = yf - depth
    # Изголовье: доска у дальнего края, выше матраса.
    head = 26
    p.rect(x0 - 2, yb - head, x1 + 2, yb + 6, mul(frame, 1.12), mul(frame, 0.9), edge=edge)
    p.rect(x0 + 6, yb - head + 5, x1 - 6, yb - 2, mul(frame, 0.92), mul(frame, 0.8))
    for cx in (x0 + 1, x1 - 1):
        p.ell(cx - 5, yb - head - 5, cx + 5, yb - head + 3, mul(frame, 1.25), edge, 0.8)   # навершия столбиков
    # Рама и лицо (изножье).
    p.rect(x0, yb, x1, yf, mul(frame, 1.0), mul(frame, 0.9))
    p.rect(x0, yf, x1, GROUND, mul(frame_d, 1.15), mul(frame_d, 0.82), edge=edge)
    # Матрас с простынёй, подушка, одеяло.
    p.rect(x0 + 5, yb + 4, x1 - 5, yf - 3, sheet, mul(sheet, 0.9))
    p.rect(x0 + 12, yb + 9, x1 - 12, yb + 30, pillow, (226, 220, 206), edge=(170, 160, 146, 255))
    p.line([(x0 + 16, yb + 26), (x1 - 16, yb + 26)], (210, 202, 186, 255), 0.8)
    p.rect(x0 + 3, yb + 40, x1 - 3, yf + 4, blanket, mul(blanket, 0.78), edge=mul(blanket, 0.5))
    p.rect(x0 + 3, yb + 40, x1 - 3, yb + 50, blanket_l, mul(blanket_l, 0.92))      # отворот
    p.line([(x0 + 3, yb + 50), (x1 - 3, yb + 50)], mul(blanket, 0.6), 0.8)
    for fx in (46, 82):                                                            # складки
        p.line([(fx, yb + 56), (fx - 2, yf - 4)], mul(blanket, 0.82), 1.0)
    p.rect(x0 + 3, yf + 4, x1 - 3, yf + 10, mul(blanket, 0.7), mul(blanket, 0.55))   # свисает к изножью
    for lx in (x0, x1 - 7):                                                        # ножки
        p.rect(lx, GROUND - 6, lx + 7, GROUND, mul(frame_d, 0.9), mul(frame_d, 0.7))
    p.line([(x0, GROUND), (x1, GROUND)], (40, 20, 10, 255), 1.0)
    return p.done()


# ---------- костёр ----------

def flame(p: Pen, cx, base, hgt, wid):
    """Пламя слоями: красно-оранжевое, оранжевое, жёлтое, белое ядро."""
    layers = (((236, 70, 26), 1.0), ((255, 140, 30), 0.78), ((255, 214, 80), 0.55), ((255, 250, 210), 0.3))
    for col, k in layers:
        h, w = hgt * k, wid * k
        pts = []
        for i in range(25):
            t = i / 24 * math.pi * 2
            # капля: острый верх, круглый низ, лёгкие языки по краям
            sx = math.sin(t)
            sy = -math.cos(t)
            r = 1 + 0.12 * math.sin(3 * t + k * 5)
            y = base - (sy + 1) / 2 * h if sy > 0 else base - (sy + 1) / 2 * h * 0.25
            x = cx + sx * w * 0.5 * r * (1 - max(0, sy) ** 1.6 * 0.95)
            pts.append((x, y))
        p.poly(pts, col + (255,))


def campfire() -> Image.Image:
    p = Pen()
    cx, cy, r = 64, GROUND - 40, 40          # круг основания (центр, радиус): глубина 1:1
    p.ell(cx - r + 6, cy - r + 6, cx + r - 6, cy + r - 6, (62, 48, 44, 255))       # кострище — зола
    p.ell(cx - 22, cy - 18, cx + 22, cy + 18, (110, 52, 28, 255))                  # угли
    p.ell(cx - 14, cy - 11, cx + 14, cy + 11, (230, 96, 32, 255))
    stones = []
    for i in range(11):
        a = i / 11 * math.pi * 2 + 0.2
        stones.append((cx + math.cos(a) * r * 0.86, cy + math.sin(a) * r * 0.86, 8.5 + (i * 37 % 5) * 0.6))
    rock_t, rock_f, edge = (196, 196, 214), (122, 120, 144), (52, 50, 66, 255)

    def stone(x, y, s):
        p.rect(x - s, y - s * 0.6, x + s, y + s * 0.6 + 6, rock_f, mul(rock_f, 0.7), edge=edge)   # лицо
        p.ell(x - s, y - s - 2, x + s, y + s * 0.7, rock_t, edge, 0.8)                          # верх
        p.ell(x - s * 0.6, y - s * 0.8, x + s * 0.1, y - s * 0.2, (230, 230, 242, 255))

    for x, y, s in sorted(stones, key=lambda q: q[1]):
        if y < cy:
            stone(x, y, s)
    # Поленья крест-накрест: верх светлее, торцы — кольца.
    for (ax, ay, bx, by) in ((cx - 26, cy + 10, cx + 24, cy - 12), (cx - 24, cy - 12, cx + 26, cy + 10)):
        p.line([(ax, ay + 3), (bx, by + 3)], (70, 40, 20, 255), 11)
        p.line([(ax, ay), (bx, by)], (150, 92, 50, 255), 9)
        p.line([(ax, ay - 2), (bx, by - 2)], (196, 132, 76, 255), 3)
        for ex, ey in ((ax, ay), (bx, by)):
            p.ell(ex - 5, ey - 5, ex + 5, ey + 5, (226, 182, 120, 255), (90, 52, 26, 255), 1.0)
    flame(p, cx, cy + 4, 70, 40)
    for x, y, s in sorted(stones, key=lambda q: q[1]):
        if y >= cy:
            stone(x, y, s)
    return p.done()


# ---------- факел ----------

def torch_obj() -> Image.Image:
    p = Pen()
    cx = 64
    # Каменное основание.
    p.rect(cx - 13, GROUND - 10, cx + 13, GROUND, (128, 124, 146), (88, 84, 104), edge=(46, 44, 58, 255))
    p.rect(cx - 13, GROUND - 22, cx + 13, GROUND - 10, (210, 208, 222), (176, 174, 192), edge=(46, 44, 58, 255))
    # Древко: свет слева.
    top = 58
    p.rect(cx - 4, top, cx + 4, GROUND - 16, (150, 92, 48), (110, 64, 32), edge=(66, 34, 16, 255))
    p.line([(cx - 2, top + 2), (cx - 2, GROUND - 18)], (206, 146, 90, 255), 1.2)
    # Чаша-обмотка.
    p.rect(cx - 8, top - 8, cx + 8, top + 4, (110, 114, 138), (70, 72, 92), edge=(36, 36, 50, 255))
    p.line([(cx - 8, top - 4), (cx + 8, top - 4)], (180, 186, 210, 255), 1.0)
    flame(p, cx, top - 4, 46, 24)
    return p.done()


# ---------- E2-16: многоклеточная мебель и повороты ----------
# Кровать 1 x 2 тайла, верстак 2 x 2 (docs/05 1.3), поворот 0/90/180/270 (R). Картинка — на весь предмет: ширина = тайлы
# по X x 128 px, высота = тайлы по Y x 128 + запас под высоту; опора — низ по центру (передняя кромка на земле), как у 1 x 1.
# Вид «боком» (90°) — отдельная картинка (_e); 270° — она же зеркально (в игре flip); 180° — своя картинка (_s / _n).

def tall_pen(tiles_w, tiles_h, extra):
    """Pen под предмет tiles_w x tiles_h тайлов: высота картинки = глубина + запас extra; GROUND — передний край."""
    global GROUND
    w, h = 128 * tiles_w, 128 * tiles_h + extra
    GROUND = h - 3
    return Pen(w, h)


def end_pen():
    global GROUND
    GROUND = 157


def bed_parts():
    return dict(frame=(176, 92, 48), frame_d=(120, 58, 30), sheet=(244, 238, 224), blanket=(54, 112, 214),
                blanket_l=(120, 170, 240), pillow=(255, 252, 242), edge=(66, 30, 14, 255))


def bed_vertical(head_north: bool) -> Image.Image:
    """Кровать вдоль Y (1 x 2 тайла): 0° — изголовье на севере (дальний край), 180° — на юге (у зрителя)."""
    c = bed_parts()
    p = tall_pen(1, 2, 48)
    x0, x1 = 22, 106
    height = 22
    yf = GROUND - height
    yb = yf - 228
    head = 26
    frame, frame_d, edge = c["frame"], c["frame_d"], c["edge"]
    if head_north:
        p.rect(x0 - 2, yb - head, x1 + 2, yb + 6, mul(frame, 1.12), mul(frame, 0.9), edge=edge)
        p.rect(x0 + 6, yb - head + 5, x1 - 6, yb - 2, mul(frame, 0.92), mul(frame, 0.8))
        for cx in (x0 + 1, x1 - 1):
            p.ell(cx - 5, yb - head - 5, cx + 5, yb - head + 3, mul(frame, 1.25), edge, 0.8)
    p.rect(x0, yb, x1, yf, frame, mul(frame, 0.9))
    p.rect(x0, yf, x1, GROUND, mul(frame_d, 1.15), mul(frame_d, 0.82), edge=edge)
    p.rect(x0 + 5, yb + 4, x1 - 5, yf - 3, c["sheet"], mul(c["sheet"], 0.9))
    if head_north:
        p.rect(x0 + 12, yb + 10, x1 - 12, yb + 40, c["pillow"], (226, 220, 206), edge=(170, 160, 146, 255))
        p.rect(x0 + 3, yb + 56, x1 - 3, yf + 4, c["blanket"], mul(c["blanket"], 0.78), edge=mul(c["blanket"], 0.5))
        p.rect(x0 + 3, yb + 56, x1 - 3, yb + 68, c["blanket_l"], mul(c["blanket_l"], 0.92))
        for fx in (46, 82):
            p.line([(fx, yb + 76), (fx - 2, yf - 4)], mul(c["blanket"], 0.82), 1.0)
        p.rect(x0 + 3, yf + 4, x1 - 3, yf + 10, mul(c["blanket"], 0.7), mul(c["blanket"], 0.55))
    else:
        # Изголовье у зрителя: одеяло от дальнего края, подушка у ближнего, спинка закрывает низ матраса.
        p.rect(x0 + 3, yb + 2, x1 - 3, yf - 60, c["blanket"], mul(c["blanket"], 0.78), edge=mul(c["blanket"], 0.5))
        p.rect(x0 + 3, yf - 72, x1 - 3, yf - 60, c["blanket_l"], mul(c["blanket_l"], 0.92))
        for fx in (46, 82):
            p.line([(fx, yb + 10), (fx + 2, yf - 76)], mul(c["blanket"], 0.82), 1.0)
        p.rect(x0 + 12, yf - 52, x1 - 12, yf - 22, c["pillow"], (226, 220, 206), edge=(170, 160, 146, 255))
        p.rect(x0 - 2, yf - head + 4, x1 + 2, GROUND, mul(frame, 1.12), mul(frame, 0.86), edge=edge)
        p.rect(x0 + 6, yf - head + 9, x1 - 6, GROUND - 6, mul(frame, 0.92), mul(frame, 0.8))
        for cx in (x0 + 1, x1 - 1):
            p.ell(cx - 5, yf - head - 1, cx + 5, yf - head + 7, mul(frame, 1.25), edge, 0.8)
    for lx in (x0, x1 - 7):
        p.rect(lx, GROUND - 6, lx + 7, GROUND, mul(frame_d, 0.9), mul(frame_d, 0.7))
    p.line([(x0, GROUND), (x1, GROUND)], (40, 20, 10, 255), 1.0)
    img = p.done()
    end_pen()
    return img


def bed_side() -> Image.Image:
    """Кровать вдоль X (2 x 1 тайла), изголовье на востоке (90°); 270° — зеркально."""
    c = bed_parts()
    p = tall_pen(2, 1, 40)
    x0, x1 = 14, 230
    height = 22
    yf = GROUND - height
    yb = yf - 92
    frame, frame_d, edge = c["frame"], c["frame_d"], c["edge"]
    p.rect(x0, yb, x1, yf, frame, mul(frame, 0.9))
    p.rect(x0, yf, x1, GROUND, mul(frame_d, 1.15), mul(frame_d, 0.82), edge=edge)
    p.rect(x0 + 4, yb + 5, x1 - 4, yf - 3, c["sheet"], mul(c["sheet"], 0.9))
    p.rect(x1 - 52, yb + 14, x1 - 16, yf - 12, c["pillow"], (226, 220, 206), edge=(170, 160, 146, 255))
    p.rect(x0 + 2, yb + 3, x1 - 66, yf + 6, c["blanket"], mul(c["blanket"], 0.78), edge=mul(c["blanket"], 0.5))
    p.rect(x1 - 78, yb + 3, x1 - 66, yf + 6, c["blanket_l"], mul(c["blanket_l"], 0.92))
    for fy in (yb + 30, yb + 60):
        p.line([(x0 + 10, fy), (x1 - 84, fy + 2)], mul(c["blanket"], 0.82), 1.0)
    # Изголовье — спинка на восточном торце, выше матраса.
    head = 34
    p.rect(x1 - 8, yb - head, x1 + 6, GROUND, mul(frame, 1.12), mul(frame, 0.84), edge=edge)
    p.ell(x1 - 6, yb - head - 6, x1 + 4, yb - head + 4, mul(frame, 1.25), edge, 0.8)
    p.rect(x0 - 4, yb - 8, x0 + 6, GROUND, mul(frame, 1.0), mul(frame, 0.8), edge=edge)   # низкая спинка в ногах
    for lx in (x0 + 8, x1 - 20):
        p.rect(lx, GROUND - 6, lx + 7, GROUND, mul(frame_d, 0.9), mul(frame_d, 0.7))
    p.line([(x0, GROUND), (x1, GROUND)], (40, 20, 10, 255), 1.0)
    img = p.done()
    end_pen()
    return img


def bench_big(face: str) -> Image.Image:
    """Верстак 2 x 2 тайла. face: 's' — рабочий край к зрителю (0°), 'n' — от зрителя (180°), 'e' — к востоку (90°)."""
    p = tall_pen(2, 2, 64)
    top_c, face_c, leg_c = (238, 178, 104), (176, 108, 54), (150, 88, 44)
    edge = (70, 36, 18, 255)
    x0, x1 = 10, 246
    height, slab = 46, 10
    yf = GROUND - height
    yb = yf - 210
    p.rect(x0 + 10, yf + slab, x1 - 10, GROUND - 6, (70, 42, 26), (44, 26, 16))
    for lx in (22, 222):
        p.rect(lx + 2, yf + slab, lx + 10, GROUND - 14, mul(leg_c, 0.6), mul(leg_c, 0.45))
    p.rect(x0 + 12, GROUND - 18, x1 - 12, GROUND - 11, (214, 150, 84), (160, 100, 50), edge=edge)
    for lx in (13, 229):
        p.rect(lx, yf + slab, lx + 14, GROUND, mul(leg_c, 1.1), mul(leg_c, 0.8), edge=edge)
    p.rect(x0, yb, x1, yf, mul(top_c, 1.04), mul(top_c, 0.9))
    if face == 'e':
        for k in range(1, 6):
            x = x0 + (x1 - x0) * k / 6
            p.line([(x, yb + 1), (x, yf - 1)], mul(top_c, 0.74), 1.0)
    else:
        for k in range(1, 6):
            y = yb + (yf - yb) * k / 6
            p.line([(x0 + 1, y), (x1 - 1, y)], mul(top_c, 0.74), 1.0)
    p.rect(x0, yf, x1, yf + slab, mul(face_c, 1.08), mul(face_c, 0.85))
    p.line([(x0, yf), (x1, yf)], mul(top_c, 1.2), 1.2)
    p.d.rectangle([x0 * S, yb * S, x1 * S - 1, (yf + slab) * S - 1], outline=edge, width=S)
    iron, iron_d, iron_e = (196, 202, 222), (120, 126, 150), (40, 42, 56, 255)
    # Тиски — на рабочем крае: юг — правый передний угол, север — правый дальний, восток — правый край посередине.
    if face == 's':
        p.rect(212, yf - 16, 236, yf - 4, iron, iron_d, edge=iron_e)
        p.rect(217, yf - 4, 231, yf + 9, (150, 156, 180), (96, 100, 122), edge=iron_e)
        ty = yb + 40
    elif face == 'n':
        p.rect(212, yb - 10, 236, yb + 4, iron, iron_d, edge=iron_e)
        ty = yb + 120
    else:
        p.rect(x1 - 18, yb + 92, x1 + 2, yb + 120, iron, iron_d, edge=iron_e)
        ty = yb + 70
    # Инструменты на столе: молот, слиток, пила (сдвинуты к рабочему краю).
    p.line([(50, ty + 60), (96, ty + 10)], (122, 74, 40, 255), 5.0)
    p.line([(50, ty + 60), (96, ty + 10)], (176, 118, 66, 255), 2.5)
    p.poly([(88, ty), (108, ty + 8), (102, ty + 22), (82, ty + 14)], (150, 156, 178, 255), iron_e, 1.0)
    p.poly([(130, ty + 40), (162, ty + 34), (168, ty + 48), (136, ty + 54)], (240, 128, 60, 255), (110, 46, 18, 255), 1.0)
    p.poly([(118, ty - 6), (170, ty - 12), (172, ty - 4), (120, ty + 2)], (206, 212, 230, 255), (60, 62, 80, 255), 1.0)
    p.rect(170, ty - 16, 186, ty + 2, (200, 120, 60), (150, 84, 40), edge=edge)
    # C-011: рабочая сторона видна издалека — ящики с ручками у рабочего края, высокая доска с инструментами напротив него.
    if face == 's':
        bench_drawers(p, 96, 216, yf + slab, edge)
        bench_rack_back(p, x0 + 6, x1 - 6, yb, edge)
    elif face == 'n':
        bench_rack_front(p, x0 + 6, x1 - 6, yf + slab, edge)
    else:
        bench_rack_west(p, x0, yb, yf, edge)
        for k, yy in enumerate((yb + 140, yb + 178)):                               # ящики на восточном крае: торцы ручек
            p.rect(x1 - 30, yy, x1 - 4, yy + 26, (214, 146, 78), (170, 104, 52), edge=edge)
            p.rect(x1 - 12, yy + 9, x1 - 6, yy + 17, (255, 222, 96), (200, 150, 40), edge=(110, 66, 10, 255))
    p.line([(x0, GROUND), (x1, GROUND)], (40, 22, 12, 255), 1.0)
    img = p.done()
    end_pen()
    return img


RACK, RACK_D, PEG = (122, 74, 40), (88, 52, 28), (60, 34, 18)


def rack_face(p: Pen, x0, x1, y0, y1, edge):
    """Лицо доски с инструментами: доска, ряды отверстий, висящие молот, пила, клещи (силуэтами)."""
    p.rect(x0, y0, x1, y1, mul(RACK, 1.1), mul(RACK, 0.85), edge=edge)
    for yy in (y0 + 8, y1 - 8):
        for xx in range(int(x0) + 10, int(x1) - 6, 14):
            p.ell(xx - 1.5, yy - 1.5, xx + 1.5, yy + 1.5, PEG + (255,))
    w = x1 - x0
    hx = x0 + w * 0.2                                                               # молот
    p.rect(hx - 2, y0 + 10, hx + 2, y1 - 6, (176, 118, 66), (122, 74, 40))
    p.rect(hx - 9, y0 + 8, hx + 9, y0 + 16, (170, 176, 198), (110, 116, 140), edge=(40, 42, 56, 255))
    sx = x0 + w * 0.45                                                              # пила
    p.poly([(sx, y0 + 10), (sx + 34, y0 + 14), (sx + 34, y1 - 10), (sx, y1 - 6)], (210, 216, 232, 255), (60, 62, 80, 255), 1.0)
    p.rect(sx - 10, y0 + 8, sx + 2, y1 - 12, (200, 120, 60), (150, 84, 40), edge=edge)
    kx = x0 + w * 0.8                                                               # клещи
    p.line([(kx - 6, y0 + 10), (kx + 4, y1 - 6)], (90, 94, 116, 255), 3.0)
    p.line([(kx + 6, y0 + 10), (kx - 4, y1 - 6)], (90, 94, 116, 255), 3.0)


def bench_rack_back(p: Pen, x0, x1, yb, edge):
    """Доска с инструментами вдоль дальнего (северного) края: стоит на столе, лицом к зрителю."""
    hr, th = 44, 8
    p.rect(x0, yb - hr - th, x1, yb - hr, mul(RACK, 1.25), mul(RACK, 1.05), edge=edge)
    rack_face(p, x0, x1, yb - hr, yb + 2, edge)


def bench_rack_front(p: Pen, x0, x1, ybase, edge):
    """180°: доска вдоль ближнего края — к зрителю видна её задняя сторона (гладкая, с планками), стол за ней."""
    hr, th = 44, 8
    p.rect(x0, ybase - hr - th - 12, x1, ybase - hr - 12, mul(RACK, 1.25), mul(RACK, 1.05), edge=edge)
    p.rect(x0, ybase - hr - 12, x1, ybase - 12, mul(RACK_D, 1.15), mul(RACK_D, 0.9), edge=edge)
    for k in range(1, 4):
        x = x0 + (x1 - x0) * k / 4
        p.line([(x, ybase - hr - 10), (x, ybase - 14)], mul(RACK_D, 0.7), 1.2)
    p.line([(x0 + 2, ybase - 34), (x1 - 2, ybase - 34)], mul(RACK_D, 0.7), 2.0)


def bench_rack_west(p: Pen, x0, yb, yf, edge):
    """90°: доска вдоль западного края — сверху видна толстая планка с ручками инструментов, у зрителя — её торец."""
    hr, wd = 44, 22
    p.rect(x0, yf - hr, x0 + wd, yf, mul(RACK, 1.0), mul(RACK, 0.8), edge=edge)        # торец
    p.rect(x0, yb - hr, x0 + wd, yf - hr, mul(RACK, 1.25), mul(RACK, 1.05), edge=edge)  # верх доски
    for k, yy in enumerate(range(int(yb - hr) + 20, int(yf - hr) - 10, 34)):
        c = ((176, 118, 66), (170, 176, 198), (200, 120, 60))[k % 3]
        p.rect(x0 + wd - 2, yy, x0 + wd + 12, yy + 8, c, mul(c, 0.8), edge=(40, 42, 56, 255))   # торчат к столу


def bench_drawers(p: Pen, x0, x1, ytop, edge):
    """Два ящика с латунными ручками под столешницей на рабочем (южном) крае."""
    h = 22
    mid = (x0 + x1) / 2
    for a, b in ((x0, mid - 2), (mid + 2, x1)):
        p.rect(a, ytop, b, ytop + h, (214, 146, 78), (170, 104, 52), edge=edge)
        c = (a + b) / 2
        p.rect(c - 7, ytop + 8, c + 7, ytop + 14, (255, 222, 96), (200, 150, 40), edge=(110, 66, 10, 255))


def chest_turned(face: str) -> Image.Image:
    """Сундук 1 x 1 повёрнутый: 'n' — замок от зрителя (180°, видна задняя стенка с петлями), 'e' — замок к востоку (90°)."""
    p = Pen()
    wood_t, wood_f = (236, 156, 74), (150, 80, 34)
    iron, iron_l = (70, 74, 98), (156, 164, 192)
    edge = (70, 36, 18, 255)
    x0, x1 = 18, 110
    depth, height = 60, 36
    if face == 'e':
        x0, x1 = 30, 98
        depth = 84
    yf, yb = box(p, x0, x1, depth, height, wood_t, wood_f, edge)
    if face == 'e':
        for k in range(1, 4):
            x = x0 + (x1 - x0) * k / 4
            p.line([(x, yb + 1), (x, yf - 1)], mul(wood_t, 0.72), 1.0)
        for by in (yb + 12, yf - 12):
            p.rect(x0, by - 4, x1, by + 4, iron_l, iron)
        p.rect(x1 - 6, yb + depth * 0.4, x1 + 2, yb + depth * 0.62, (255, 226, 110), (214, 150, 30), edge=(110, 66, 10, 255))
        for bx in (x0 + 10, x1 - 10):
            p.rect(bx - 4, yf, bx + 4, GROUND, mul(iron, 1.15), mul(iron, 0.8))
    else:
        for k in range(1, 4):
            y = yb + depth * k / 4
            p.line([(x0 + 1, y), (x1 - 1, y)], mul(wood_t, 0.72), 1.0)
        p.line([(x0 + 1, yf + height * 0.5), (x1 - 1, yf + height * 0.5)], mul(wood_f, 0.66), 1.0)
        for bx in (30, 98):
            p.rect(bx - 4, yb, bx + 4, yf, iron_l, iron)
            p.rect(bx - 4, yf, bx + 4, GROUND, mul(iron, 1.15), mul(iron, 0.8))
        for hx in (46, 82):                                                          # петли крышки у зрителя
            p.rect(hx - 6, yf - 3, hx + 6, yf + 6, (110, 114, 136), (70, 74, 98), edge=(40, 42, 56, 255))
        p.rect(58, yb - 3, 70, yb + 5, (255, 226, 110), (214, 150, 30), edge=(110, 66, 10, 255))   # замок на дальнем крае
    p.line([(x0, GROUND), (x1, GROUND)], (40, 22, 12, 255), 1.5)
    return p.done()


FURNITURE = {
    # C-030: верстак, сундук и двуспальная кровать — подробные, tools/furniture_detail.py (волокно, доски, железо, ткань,
    # меандр); кровать 2 x 2 тайла 256x340, верстак 2 x 2 256x376 (выше — доска с инструментами), сундук 128x160.
    "workbench": (lambda: fd.bench_image('s'), "объект «верстак» 2 x 2 тайла 256x376, рабочий край к зрителю (0°), подробный: столешница из досок с волокном и сучками, тиски, ящики с латунными ручками, доска с инструментами, пила, киянка, стамески, рубанок, стружка (C-030); 3/4 с высотой (верх 1:1, высота x0,5), опора — низ по центру; скрипт tools/furniture_detail.py"),
    "chest": (lambda: fd.chest_image('s'), "объект «сундук» 128x160, замок к зрителю (0°), подробный: доски, крышка-бочка, железные полосы с заклёпками, уголки, золотой замок (C-030); 3/4 с высотой, опора — низ по центру; скрипт tools/furniture_detail.py"),
    "bed": (lambda: fd.bed_image(0), "объект «кровать» двуспальная 2 x 2 тайла 256x340, изголовье к северу (0°): резные спинки с меандром и розетками, столбики с навершиями, две подушки, стёганое одеяло с золотой каймой, дорожка в ногах (C-030); 3/4 с высотой, опора — низ по центру; скрипт tools/furniture_detail.py"),
    "bed_s": (lambda: fd.bed_image(2), "объект «кровать» двуспальная 2 x 2, поворот 180° — изголовье к зрителю, 256x340 (C-030); скрипт tools/furniture_detail.py"),
    "bed_e": (lambda: fd.bed_image(1), "объект «кровать» двуспальная 2 x 2, поворот 90° — изголовье на восток (270° — зеркально), 256x340 (C-030); скрипт tools/furniture_detail.py"),
    "workbench_n": (lambda: fd.bench_image('n'), "объект «верстак» 2 x 2, поворот 180° — рабочий край от зрителя, 256x376 (C-030); скрипт tools/furniture_detail.py"),
    "workbench_e": (lambda: fd.bench_image('e'), "объект «верстак» 2 x 2, поворот 90° — рабочий край к востоку (270° — зеркально), 256x376 (C-030); скрипт tools/furniture_detail.py"),
    "chest_n": (lambda: fd.chest_image('n'), "объект «сундук», поворот 180° — замок от зрителя, петли к зрителю, 128x160 (C-030); скрипт tools/furniture_detail.py"),
    "chest_e": (lambda: fd.chest_image('e'), "объект «сундук», поворот 90° — замок к востоку (270° — зеркально), ручка-кольцо на торце, 128x160 (C-030); скрипт tools/furniture_detail.py"),
    # C-041: старый размер кровати 1 x 2 (записы старых сохранений) — рисунки E2-16, когда окно 2 x 2 занято соседом.
    "bed_1x2": (lambda: bed_vertical(True), "объект «кровать» старый размер 1 x 2 тайла 128x304, изголовье к северу (0°) — для записей старых сохранений, когда восточная колонка окна 2 x 2 занята (C-041); 3/4 с высотой, опора — низ по центру; скрипт tools/make_furniture.py"),
    "bed_1x2_s": (lambda: bed_vertical(False), "объект «кровать» старый размер 1 x 2, поворот 180° — изголовье к зрителю, 128x304 (C-041); скрипт tools/make_furniture.py"),
    "bed_1x2_e": (lambda: bed_side(), "объект «кровать» старый размер 2 x 1, поворот 90° — изголовье на восток (270° — зеркально), 256x168 (C-041); скрипт tools/make_furniture.py"),
    "campfire": (campfire, "объект «костёр» 128x160, кольцо камней с гранями, поленья, пламя слоями, опора — низ по центру (E2-17); скрипт tools/make_furniture.py"),
    "torch": (torch_obj, "объект «факел на земле» 128x160, каменное основание, древко, чаша, пламя, опора — низ по центру (E2-17); скрипт tools/make_furniture.py"),
}


def main() -> None:
    with MANIFEST.open(encoding="utf-8", newline="") as f:
        rows = list(csv.reader(f))
    for key, (draw, notes) in FURNITURE.items():
        rel = f"client/Assets/Art/_TEMP/TEMP_obj_{key}.png"
        draw().save(ROOT / rel, optimize=False)
        for r in rows:
            if r and r[0] == rel:
                r[1], r[3] = "placeholder", notes
                break
        else:
            rows.append([rel, "placeholder", "К", notes])
        print(rel)
    with MANIFEST.open("w", encoding="utf-8", newline="") as f:
        csv.writer(f, lineterminator="\n").writerows(rows)


if __name__ == "__main__":
    main()
