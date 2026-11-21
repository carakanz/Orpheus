"""Подробная мебель C-030 (слово Автора 17.11: «Кровать стрём. Как и верстак. Как и сундук. Должно быть больше деталей.
Кровать 2-местную делай сразу»): двуспальная кровать 2 x 2 тайла, верстак 2 x 2, сундук 1 x 1 — все повороты.

Рисуется процедурно (Мак на 17.11 недоступен; к тому же четыре поворота одного предмета должны совпадать по форме до
пикселя — по эскизу img2img их не держит): та же проекция, что у E2-17 (tools/make_furniture.py) — глубина 1:1, высота
x0,5 (ObjectMesh.WallHeight), опора — передний край основания внизу по центру, 128 px на тайл, рисунок x4 и уменьшение.
Стиль Б (BRIEF 11g): точная форма, материал читается — дерево с волокном, досками, сучками и фаской, железо с заклёпками,
ткань со стёжкой и складками, золотой меандр (греческий мотив); свет слева сверху, тонкая тёмная кромка; цвета яркие.

Каждая поверхность — прямоугольник (в этой проекции верх и южная грань предмета прямоугольны): верх точки (x, d, h) —
y = GROUND - d - h * 0,5, где d — глубина от переднего края основания (к северу), h — высота (px при 128 px на тайл).

Запуск — через tools/make_furniture.py (он пишет TEMP_obj_*.png и строки манифеста).
"""
from __future__ import annotations

import math
import random

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

S = 4          # рисуем в 4 раза крупнее
V = 0.5        # сжатие высоты

EDGE = (58, 28, 14, 255)
EDGE_IRON = (34, 36, 48, 255)


def mix(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3)) + (255,)


def mul(c, k):
    return tuple(max(0, min(255, int(round(c[i] * k)))) for i in range(3)) + (255,)


def rgba(c, a=255):
    return tuple(c[:3]) + (a,)


class Canvas:
    """Холст предмета w x h px (128 на тайл); ground — y переднего края основания."""

    def __init__(self, w, h, seed=1):
        self.w, self.h = w, h
        self.ground = h - 3
        self.img = Image.new("RGBA", (w * S, h * S), (0, 0, 0, 0))
        self.d = ImageDraw.Draw(self.img)
        self.rng = random.Random(seed)

    # ---------- проекция ----------
    def y(self, d, h=0.0):
        return self.ground - d - h * V

    # ---------- примитивы ----------
    def _box(self, x0, y0, x1, y1):
        return int(round(x0 * S)), int(round(y0 * S)), int(round(x1 * S)), int(round(y1 * S))

    def paste(self, arr, x0, y0, mask=None):
        """Вставить массив RGB(A) (в больших px) в точку (x0, y0) px холста."""
        im = Image.fromarray(arr.astype(np.uint8), "RGBA" if arr.shape[2] == 4 else "RGB").convert("RGBA")
        X, Y = int(round(x0 * S)), int(round(y0 * S))
        layer = Image.new("RGBA", self.img.size, (0, 0, 0, 0))
        layer.paste(im, (X, Y))
        if mask is not None:
            a = np.array(layer)
            a[..., 3] = (a[..., 3].astype(np.float32) * mask).astype(np.uint8)
            layer = Image.fromarray(a, "RGBA")
        self.img = Image.alpha_composite(self.img, layer)
        self.d = ImageDraw.Draw(self.img)

    def rect(self, x0, y0, x1, y1, top, bottom=None, edge=None, ew=1.0):
        bottom = bottom or top
        X0, Y0, X1, Y1 = self._box(x0, y0, x1, y1)
        n = max(1, Y1 - Y0)
        for i in range(n):
            self.d.line([(X0, Y0 + i), (X1 - 1, Y0 + i)], fill=mix(top, bottom, i / n))
        if edge:
            self.d.rectangle([X0, Y0, X1 - 1, Y1 - 1], outline=edge, width=max(1, int(ew * S)))

    def outline(self, x0, y0, x1, y1, edge=EDGE, ew=1.0):
        X0, Y0, X1, Y1 = self._box(x0, y0, x1, y1)
        self.d.rectangle([X0, Y0, X1 - 1, Y1 - 1], outline=edge, width=max(1, int(ew * S)))

    def line(self, pts, fill, width=1.0):
        self.d.line([(x * S, y * S) for x, y in pts], fill=rgba(fill) if len(fill) == 3 else fill,
                    width=max(1, int(round(width * S))), joint="curve")

    def poly(self, pts, fill, edge=None, width=1.0):
        P = [(x * S, y * S) for x, y in pts]
        self.d.polygon(P, fill=rgba(fill) if len(fill) == 3 else fill)
        if edge:
            self.d.line(P + [P[0]], fill=edge, width=max(1, int(width * S)), joint="curve")

    def ell(self, x0, y0, x1, y1, fill, edge=None, width=1.0):
        x0, x1 = min(x0, x1), max(x0, x1)
        y0, y1 = min(y0, y1), max(y0, y1)
        self.d.ellipse([x0 * S, y0 * S, x1 * S, y1 * S], fill=rgba(fill) if fill and len(fill) == 3 else fill,
                       outline=edge, width=max(1, int(width * S)) if edge else 0)

    def rrect(self, x0, y0, x1, y1, r, fill, edge=None, width=1.0):
        self.d.rounded_rectangle([x0 * S, y0 * S, x1 * S, y1 * S], radius=r * S,
                                 fill=None if fill is None else rgba(fill) if len(fill) == 3 else fill, outline=edge,
                                 width=max(1, int(width * S)) if edge else 0)

    def soft(self, draw_fn, blur, alpha=1.0):
        """Мягкий слой (тень, свет): draw_fn(ImageDraw, S) рисует на прозрачном слое, слой размывается."""
        layer = Image.new("RGBA", self.img.size, (0, 0, 0, 0))
        draw_fn(ImageDraw.Draw(layer), S)
        layer = layer.filter(ImageFilter.GaussianBlur(blur * S))
        if alpha != 1.0:
            a = np.array(layer)
            a[..., 3] = (a[..., 3] * alpha).astype(np.uint8)
            layer = Image.fromarray(a, "RGBA")
        self.img = Image.alpha_composite(self.img, layer)
        self.d = ImageDraw.Draw(self.img)

    def shade_rect(self, x0, y0, x1, y1, color, alpha, blur):
        """Мягкая тень/свет прямоугольником (контакт, затенение под нависанием)."""
        def fn(d, s):
            d.rectangle([x0 * s, y0 * s, x1 * s, y1 * s], fill=rgba(color, int(255 * alpha)))
        self.soft(fn, blur)

    def done(self) -> Image.Image:
        img = self.img.resize((self.w, self.h), Image.LANCZOS)
        ImageDraw.Draw(img).rectangle([0, 0, 7, 7], fill=(255, 0, 255, 255))  # пурпурный уголок заглушки
        return img


# ---------------------------------------------------------------- текстуры

def noise(w, h, cells_x, cells_y, rng):
    """Плавный шум 0…1 размером w x h (бикубически растянутая сетка)."""
    small = np.array([[rng.random() for _ in range(max(2, cells_x))] for _ in range(max(2, cells_y))], dtype=np.float32)
    im = Image.fromarray((small * 255).astype(np.uint8), "L").resize((max(1, w), max(1, h)), Image.BICUBIC)
    return np.asarray(im, dtype=np.float32) / 255.0


def wood(c: Canvas, x0, y0, x1, y1, base, along="h", plank=None, light=(1.08, 0.86), seams=True, knots=0,
         grain=1.0, edge=EDGE, ew=1.0, bevel=True):
    """
    Деревянная поверхность: доски (plank — ширина доски в px поперёк волокна; None — цельная), разный тон досок, волокно
    вдоль `along` ('h' — по горизонтали, 'v' — по вертикали) с извивами, сучки, швы с фаской; свет сверху вниз `light`.
    """
    X0, Y0, X1, Y1 = c._box(x0, y0, x1, y1)
    W, H = max(1, X1 - X0), max(1, Y1 - Y0)
    rng = c.rng
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    al, ac = (xx, yy) if along == "h" else (yy, xx)          # вдоль и поперёк волокна, большие px
    across_len = H if along == "h" else W
    along_len = W if along == "h" else H
    tone = np.ones((H, W), np.float32)
    if plank:
        pw = plank * S
        idx = np.floor(ac / pw).astype(int)
        nplanks = int(across_len // pw) + 2
        var = np.array([1.0 + (rng.random() - 0.5) * 0.16 for _ in range(nplanks)], np.float32)
        tone *= var[np.clip(idx, 0, nplanks - 1)]
        # стык доски по длине (торцевой шов) — тон меняется
    warp = noise(W, H, max(2, along_len // (40 * S)), max(2, across_len // (6 * S)), rng) if along == "h" else \
        noise(W, H, max(2, W // (6 * S)), max(2, H // (40 * S)), rng)
    fine = noise(W, H, max(2, W // (3 * S)), max(2, H // (3 * S)), rng)
    phase = ac * (2 * math.pi / (2.6 * S)) + warp * 9.0
    streak = 0.5 + 0.5 * np.sin(phase)
    tone *= 1.0 - grain * (0.10 * streak ** 6 + 0.04 * (fine - 0.5))
    grad = np.linspace(light[0], light[1], H, dtype=np.float32)[:, None]
    tone *= grad
    rgb = np.clip(np.stack([tone * base[i] for i in range(3)], -1), 0, 255)
    arr = np.concatenate([rgb, np.full((H, W, 1), 255, np.float32)], -1)
    c.paste(arr, X0 / S, Y0 / S)
    # Сучки.
    for _ in range(knots):
        kx = x0 + rng.uniform(0.15, 0.85) * (x1 - x0)
        ky = y0 + rng.uniform(0.2, 0.8) * (y1 - y0)
        rw, rh = (rng.uniform(2.0, 3.4), rng.uniform(1.1, 1.6)) if along == "h" else (rng.uniform(1.1, 1.6), rng.uniform(2.0, 3.4))
        c.ell(kx - rw, ky - rh, kx + rw, ky + rh, mul(base, 0.62), edge=mul(base, 0.45), width=0.5)
        c.ell(kx - rw * 0.4, ky - rh * 0.4, kx + rw * 0.4, ky + rh * 0.4, mul(base, 0.5))
    # Швы между досками: тёмная щель и светлая фаска за ней (свет сверху/слева).
    if plank and seams:
        n = int((y1 - y0 if along == "h" else x1 - x0) // plank)
        for k in range(1, n + 1):
            if along == "h":
                yy_ = y0 + k * plank
                if yy_ >= y1 - 0.5:
                    break
                c.line([(x0 + 0.5, yy_), (x1 - 0.5, yy_)], mul(base, 0.52), 0.7)
                c.line([(x0 + 0.5, yy_ + 0.6), (x1 - 0.5, yy_ + 0.6)], mul(base, 1.14), 0.4)
            else:
                xx_ = x0 + k * plank
                if xx_ >= x1 - 0.5:
                    break
                c.line([(xx_, y0 + 0.5), (xx_, y1 - 0.5)], mul(base, 0.52), 0.7)
                c.line([(xx_ + 0.6, y0 + 0.5), (xx_ + 0.6, y1 - 0.5)], mul(base, 1.14), 0.4)
    if bevel:
        c.line([(x0 + 0.6, y0 + 0.6), (x1 - 0.6, y0 + 0.6)], mul(base, 1.25), 0.6)   # светлая кромка сверху
        c.line([(x0 + 0.6, y0 + 0.6), (x0 + 0.6, y1 - 0.6)], mul(base, 1.15), 0.5)   # и слева
    if edge:
        c.outline(x0, y0, x1, y1, edge, ew)


def iron_band(c: Canvas, x0, y0, x1, y1, rivets=2, vertical=True, base=(84, 88, 112)):
    """Железная полоса со светлой гранью и заклёпками."""
    c.rect(x0, y0, x1, y1, mul(base, 1.35), mul(base, 0.85), edge=EDGE_IRON, ew=0.6)
    if vertical:
        c.line([(x0 + 0.8, y0 + 0.8), (x0 + 0.8, y1 - 0.8)], (214, 220, 240), 0.5)
        ys = [y0 + (y1 - y0) * (k + 0.5) / rivets for k in range(rivets)]
        for ry in ys:
            rivet(c, (x0 + x1) / 2, ry, min(1.5, (x1 - x0) * 0.22))
    else:
        c.line([(x0 + 0.8, y0 + 0.8), (x1 - 0.8, y0 + 0.8)], (214, 220, 240), 0.5)
        xs = [x0 + (x1 - x0) * (k + 0.5) / rivets for k in range(rivets)]
        for rx in xs:
            rivet(c, rx, (y0 + y1) / 2, min(1.5, (y1 - y0) * 0.22))


def rivet(c: Canvas, x, y, r=1.4):
    c.ell(x - r, y - r, x + r, y + r, (60, 62, 78), edge=EDGE_IRON, width=0.3)
    c.ell(x - r * 0.75, y - r * 0.85, x + r * 0.2, y + r * 0.1, (226, 230, 246))


def meander(c: Canvas, x0, x1, ymid, hgt, color, back=None, step=None):
    """Греческий меандр (ключ) полосой: высота hgt, шаг step; back — фон полосы."""
    if back:
        c.rect(x0, ymid - hgt / 2, x1, ymid + hgt / 2, back)
    step = step or hgt * 1.25
    u = hgt / 4.0
    w = max(0.45, u * 0.55)
    x = x0 + u * 0.5
    top, bot = ymid - hgt / 2 + u * 0.5, ymid + hgt / 2 - u * 0.5
    while x + step <= x1 + 0.1:
        a = x
        pts = [(a, bot), (a, top), (a + step * 0.75, top), (a + step * 0.75, bot - u * 1.5),
               (a + step * 0.35, bot - u * 1.5), (a + step * 0.35, top + u * 1.4)]
        c.line(pts, color, w)
        c.line([(a, bot), (a + step, bot)], color, w)
        x += step


def cloth(c: Canvas, x0, y0, x1, y1, base, quilt=None, folds=True, light=(1.06, 0.9), along="h"):
    """Ткань: мягкий свет, складки (низкочастотный шум), стёжка ромбами шагом quilt px (светлая нить и тень)."""
    X0, Y0, X1, Y1 = c._box(x0, y0, x1, y1)
    W, H = max(1, X1 - X0), max(1, Y1 - Y0)
    rng = c.rng
    tone = np.linspace(light[0], light[1], H, dtype=np.float32)[:, None] * np.ones((H, W), np.float32)
    if folds:
        n = noise(W, H, 4, 3, rng) if along == "h" else noise(W, H, 3, 4, rng)
        n2 = noise(W, H, 9, 2, rng) if along == "h" else noise(W, H, 2, 9, rng)
        tone *= 0.93 + 0.12 * n + 0.06 * (n2 - 0.5)
    rgb = np.clip(np.stack([tone * base[i] for i in range(3)], -1), 0, 255)
    arr = np.concatenate([rgb, np.full((H, W, 1), 255, np.float32)], -1)
    c.paste(arr, X0 / S, Y0 / S)
    if quilt:
        q = quilt
        hi, lo = mul(base, 1.22), mul(base, 0.72)
        span = (x1 - x0) + (y1 - y0)
        k = -span
        while k < span:
            for sgn in (1, -1):
                # линия x - sgn*y = k (ромбы), обрезанная прямоугольником
                pts = []
                for t in np.linspace(0, 1, 2):
                    pass
                ax = x0 + k if sgn == 1 else x0 + k
                p0 = (x0 + k, y0) if sgn == 1 else (x0 + k, y1)
                p1 = (x0 + k + (y1 - y0), y1) if sgn == 1 else (x0 + k + (y1 - y0), y0)
                seg = clip_seg(p0, p1, x0 + 0.8, y0 + 0.8, x1 - 0.8, y1 - 0.8)
                if seg:
                    dash(c, seg[0], seg[1], lo, 0.55, 2.2, 1.4, offset=(0.35, 0.35))
                    dash(c, seg[0], seg[1], hi, 0.45, 2.2, 1.4)
            k += q
        # бугорки стёжки: мягкие светлые пятна в середине ромбов
    return None


def clip_seg(p0, p1, x0, y0, x1, y1):
    """Отрезок, обрезанный прямоугольником (Лян — Барски)."""
    (ax, ay), (bx, by) = p0, p1
    dx, dy = bx - ax, by - ay
    t0, t1 = 0.0, 1.0
    for p, q in ((-dx, ax - x0), (dx, x1 - ax), (-dy, ay - y0), (dy, y1 - ay)):
        if p == 0:
            if q < 0:
                return None
            continue
        r = q / p
        if p < 0:
            t0 = max(t0, r)
        else:
            t1 = min(t1, r)
        if t0 > t1:
            return None
    return (ax + t0 * dx, ay + t0 * dy), (ax + t1 * dx, ay + t1 * dy)


def dash(c: Canvas, a, b, color, width, on, off, offset=(0.0, 0.0)):
    (ax, ay), (bx, by) = a, b
    L = math.hypot(bx - ax, by - ay)
    if L < 0.1:
        return
    ux, uy = (bx - ax) / L, (by - ay) / L
    t = 0.0
    while t < L:
        e = min(L, t + on)
        c.line([(ax + ux * t + offset[0], ay + uy * t + offset[1]), (ax + ux * e + offset[0], ay + uy * e + offset[1])], color, width)
        t += on + off


def finial(c: Canvas, cx, ytop, r, base):
    """Шар-навершие столбика: тень, тело, блик."""
    c.ell(cx - r, ytop, cx + r, ytop + 2 * r, mul(base, 0.95), edge=EDGE, width=0.6)
    c.ell(cx - r * 0.7, ytop + r * 0.25, cx + r * 0.25, ytop + r * 1.1, mul(base, 1.3))
    c.ell(cx - r * 0.5, ytop + r * 0.35, cx - r * 0.1, ytop + r * 0.7, mul(base, 1.6))


def post(c: Canvas, x0, x1, ytop, ybot, base, ball=True):
    """Столбик кровати: точёный — лицо с полосами-кольцами, навершие."""
    wood(c, x0, ytop, x1, ybot, base, along="v", light=(1.12, 0.8), grain=0.6, ew=0.8)
    for k in (0.18, 0.62):
        yk = ytop + (ybot - ytop) * k
        c.rect(x0 - 0.8, yk, x1 + 0.8, yk + 2.2, mul(base, 1.18), mul(base, 0.78), edge=EDGE, ew=0.5)
    if ball:
        r = (x1 - x0) * 0.62
        finial(c, (x0 + x1) / 2, ytop - 2 * r + 1.2, r, base)


# ---------------------------------------------------------------- кровать 2 x 2

FRAME = (196, 104, 52)       # вишня/орех, светлее
FRAME_D = (138, 66, 32)
SHEET = (248, 242, 226)
BLANKET = (40, 92, 206)
BLANKET_D = (28, 62, 150)
GOLD = (252, 204, 72)
RUNNER = (196, 46, 52)
PILLOW = (255, 251, 240)

BED_H = dict(rail=44, mattress=52, blanket=56, head=128, foot=78, post_head=146, post_foot=92)


def bed_image(rot: int) -> Image.Image:
    """Двуспальная кровать 2 x 2 тайла. rot: 0 — изголовье на север, 1 — на восток (3 — зеркально в игре), 2 — на юг."""
    c = Canvas(256, 256 + 84, seed=300 + rot)
    if rot == 1:
        _bed_side(c)
    else:
        _bed_long(c, head_north=(rot == 0))
    return c.done()


def _pillow(c: Canvas, x0, y0, x1, y1, vertical=False):
    """Подушка: мягкий прямоугольник, тень снизу, вмятина, кант."""
    c.soft(lambda d, s: d.rounded_rectangle([(x0 + 1) * s, (y0 + 3) * s, (x1 + 2) * s, (y1 + 3) * s], radius=6 * s,
                                             fill=(40, 30, 60, 120)), 2.0)
    c.rrect(x0, y0, x1, y1, 7, mul(PILLOW, 0.9), edge=(150, 130, 120, 255), width=0.6)
    c.rrect(x0 + 2, y0 + 1.5, x1 - 3, y1 - 3, 6, PILLOW)
    # вмятина и складки
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    c.soft(lambda d, s: d.ellipse([(cx - (x1 - x0) * 0.28) * s, (cy - (y1 - y0) * 0.2) * s, (cx + (x1 - x0) * 0.22) * s,
                                    (cy + (y1 - y0) * 0.22) * s], fill=(190, 176, 186, 60)), 3.5)
    c.line([(x0 + 5, y0 + 4), (x0 + 11, y0 + 7)], (210, 200, 196), 0.5)
    c.line([(x1 - 6, y1 - 6), (x1 - 12, y1 - 9)], (210, 200, 196), 0.5)
    # синий кант
    c.rrect(x0 + 0.8, y0 + 0.8, x1 - 0.8, y1 - 0.8, 6.5, None, edge=rgba(BLANKET, 255), width=0.6)


def _blanket(c: Canvas, x0, y0, x1, y1, fold, along="h", fold_side="top"):
    """Одеяло со стёжкой, золотым меандром у отворота и по краям, отворот простыни, дорожка в ногах."""
    cloth(c, x0, y0, x1, y1, BLANKET, quilt=14, along=along)
    # объём: края одеяла свисают — темнее, середина — светлее; мягкие складки по диагонали
    if along == "h":
        c.shade_rect(x0 - 4, y0, x0 + 8, y1, (10, 20, 60), 0.45, 4.0)
        c.shade_rect(x1 - 8, y0, x1 + 4, y1, (10, 20, 60), 0.55, 4.0)
    else:
        c.shade_rect(x0, y1 - 8, x1, y1 + 4, (10, 20, 60), 0.55, 4.0)
        c.shade_rect(x0, y0 - 4, x1, y0 + 6, (10, 20, 60), 0.4, 4.0)
    cxm, cym = (x0 + x1) / 2, (y0 + y1) / 2
    c.soft(lambda d, s_: d.ellipse([(cxm - (x1 - x0) * 0.32) * s_, (cym - (y1 - y0) * 0.3) * s_, (cxm + (x1 - x0) * 0.2) * s_,
                                    (cym + (y1 - y0) * 0.22) * s_], fill=(120, 170, 255, 26)), 12.0)
    rng = c.rng
    for _ in range(4):
        fx, fy = rng.uniform(x0 + 20, x1 - 20), rng.uniform(y0 + 20, y1 - 20)
        L = rng.uniform(18, 34)
        c.soft(lambda d, s_, fx=fx, fy=fy, L=L: d.line([(fx * s_, fy * s_), ((fx + L * 0.7) * s_, (fy + L * 0.5) * s_)],
                                                       fill=(10, 24, 80, 120), width=int(2.5 * s_)), 1.8)
        c.soft(lambda d, s_, fx=fx, fy=fy, L=L: d.line([((fx - 1.5) * s_, (fy - 2) * s_), ((fx + L * 0.7 - 1.5) * s_, (fy + L * 0.5 - 2) * s_)],
                                                       fill=(170, 200, 255, 90), width=int(1.6 * s_)), 1.2)
    if along == "h":
        # отворот простыни у изголовья
        if fold_side == "top":
            fy0, fy1 = y0, y0 + fold
            border_y = fy1 + 4
        else:
            fy0, fy1 = y1 - fold, y1
            border_y = fy0 - 4
        cloth(c, x0, fy0, x1, fy1, SHEET, quilt=None, light=(1.04, 0.92))
        c.line([(x0, fy1 if fold_side == "top" else fy0), (x1, fy1 if fold_side == "top" else fy0)], mul(SHEET, 0.7), 0.8)
        for k in range(int((x1 - x0) // 6)):                                        # строчка на отвороте
            xk = x0 + 4 + k * 6
            yk = (fy0 + fy1) / 2
            c.line([(xk, yk), (xk + 3, yk)], mul(SHEET, 0.78), 0.4)
        sy = fy1 if fold_side == "top" else fy0
        c.shade_rect(x0, sy if fold_side == "top" else sy - 4, x1, sy + 4 if fold_side == "top" else sy, (10, 20, 60), 0.5, 1.5)
        meander(c, x0 + 2, x1 - 2, border_y, 5.0, GOLD, back=BLANKET_D)
        # кайма по бокам
        for xs in (x0 + 3, x1 - 3):
            c.line([(xs, min(fy1, fy0) if fold_side == "bottom" else fy1 + 7, ), (xs, y1 - 3 if fold_side == "top" else y0 + 3)], GOLD, 1.0)
        # дорожка в ногах
        ry = (y1 - 46) if fold_side == "top" else (y0 + 22)
        c.rect(x0, ry, x1, ry + 22, mul(RUNNER, 1.1), mul(RUNNER, 0.85))
        for dy in (3, 19):
            c.line([(x0, ry + dy), (x1, ry + dy)], GOLD, 0.8)
        meander(c, x0 + 4, x1 - 4, ry + 11, 7.0, GOLD)
        for xs in range(int(x0) + 2, int(x1) - 1, 4):                               # бахрома по краю дорожки
            c.line([(xs, ry + 22), (xs, ry + 24.5)], mul(RUNNER, 0.8), 0.5)
    else:
        if fold_side == "right":
            fx0, fx1 = x1 - fold, x1
            border_x = fx0 - 4
        else:
            fx0, fx1 = x0, x0 + fold
            border_x = fx1 + 4
        cloth(c, fx0, y0, fx1, y1, SHEET, quilt=None, light=(1.04, 0.92), along="v")
        c.line([(fx0 if fold_side == "right" else fx1, y0), (fx0 if fold_side == "right" else fx1, y1)], mul(SHEET, 0.7), 0.8)
        c.rect(border_x - 2.5, y0, border_x + 2.5, y1, BLANKET_D)
        for k in range(int((y1 - y0) // 5)):
            yk = y0 + 2 + k * 5
            c.line([(border_x - 1.6, yk), (border_x + 1.6, yk + 2.5)], GOLD, 0.6)
        for ys in (y0 + 3, y1 - 3):
            c.line([(x0 + 3, ys), (fx0 - 6 if fold_side == "right" else x1 - 3, ys)], GOLD, 1.0)
        rx = (x0 + 22) if fold_side == "right" else (x1 - 44)
        c.rect(rx, y0, rx + 22, y1, mul(RUNNER, 1.1), mul(RUNNER, 0.85))
        for dx in (3, 19):
            c.line([(rx + dx, y0), (rx + dx, y1)], GOLD, 0.8)
        for k in range(int((y1 - y0) // 8)):
            yk = y0 + 3 + k * 8
            c.line([(rx + 7, yk), (rx + 15, yk + 4), (rx + 7, yk + 8)], GOLD, 0.6)


def _headboard_face(c: Canvas, x0, x1, ytop, ybot, carved=True):
    """Лицо спинки: рама, две филёнки, резной меандр сверху, розетки."""
    wood(c, x0, ytop, x1, ybot, FRAME, along="h", light=(1.12, 0.82), grain=0.8, knots=1)
    pad = 6
    mid = (x0 + x1) / 2
    band = 9 if carved else 0
    for a, b in ((x0 + pad, mid - 3), (mid + 3, x1 - pad)):
        py0, py1 = ytop + pad + band, ybot - pad
        if py1 - py0 < 6 or b - a < 6:
            continue
        c.rect(a, py0, b, py1, mul(FRAME_D, 1.0), mul(FRAME_D, 0.86), edge=EDGE, ew=0.6)
        wood(c, a + 2, py0 + 2, b - 2, py1 - 2, mul(FRAME, 1.04)[:3], along="v", light=(1.1, 0.9), grain=0.9, edge=None,
             bevel=False)
        c.line([(a + 0.6, py1 - 0.6), (b - 0.6, py1 - 0.6)], mul(FRAME, 1.3), 0.5)   # светлая нижняя фаска филёнки
        cx, cy = (a + b) / 2, (py0 + py1) / 2
        r = min(6.0, (py1 - py0) * 0.28)
        c.ell(cx - r, cy - r, cx + r, cy + r, mul(FRAME_D, 0.9), edge=EDGE, width=0.5)  # розетка
        for k in range(8):
            ang = k * math.pi / 4
            c.line([(cx, cy), (cx + math.cos(ang) * r * 0.85, cy + math.sin(ang) * r * 0.85)], mul(FRAME, 1.25), 0.5)
        c.ell(cx - r * 0.3, cy - r * 0.3, cx + r * 0.3, cy + r * 0.3, GOLD)
    if carved and x1 - x0 > 30:
        meander(c, x0 + pad, x1 - pad, ytop + pad + band / 2 - 1, 6.0, mul(FRAME_D, 0.75), back=mul(FRAME, 1.12))
    c.line([(x0, ytop), (x1, ytop)], mul(FRAME, 1.35), 0.8)


def _bed_long(c: Canvas, head_north: bool):
    """0° (изголовье на севере, дальний край) или 180° (на юге, у зрителя). Основание 2 x 2: x 6…250, d 4…252."""
    H = BED_H
    x0, x1 = 6, 250
    db, df = 252, 4                     # дальний и передний край
    hb = H["head"] if head_north else H["foot"]   # спинка на дальнем крае
    hf = H["foot"] if head_north else 104         # спинка у зрителя (изголовье на юге ниже, чтобы подушки было видно)
    pb = H["post_head"] if head_north else H["post_foot"]
    pf = H["post_foot"] if head_north else 118
    # --- дальняя спинка (лицо к зрителю) и её столбики
    yb_g = c.y(db - 10)
    _headboard_face(c, x0 + 12, x1 - 12, c.y(db - 10, hb), yb_g, carved=head_north)
    wood(c, x0 + 12, c.y(db, hb), x1 - 12, c.y(db - 10, hb), mul(FRAME, 1.12)[:3], along="h", plank=None, grain=0.5, ew=0.6)
    for px in (x0, x1 - 14):
        wood(c, px, c.y(db, pb), px + 14, c.y(db - 12, pb), mul(FRAME, 1.15)[:3], grain=0.4, ew=0.6)
        post(c, px, px + 14, c.y(db - 12, pb), yb_g, FRAME)
    # --- матрас и боковые царги (верх)
    my0, my1 = c.y(db - 12, H["mattress"]), c.y(df + 12, H["mattress"])
    for rx in (x0 + 2, x1 - 14):
        wood(c, rx, c.y(db - 12, H["rail"]), rx + 12, c.y(df + 12, H["rail"]), FRAME, along="v", grain=0.6, ew=0.6)
    c.shade_rect(x0 + 12, my0, x1 - 12, my0 + 10, (30, 16, 10), 0.55, 3.0)          # тень от дальней спинки
    cloth(c, x0 + 12, my0, x1 - 12, my1, SHEET, light=(0.92, 1.0))
    # --- подушки и одеяло
    if head_north:
        py0 = my0 + 6
        for a, b in ((x0 + 22, 124), (132, x1 - 22)):
            _pillow(c, a, py0, b, py0 + 46)
        _blanket(c, x0 + 8, py0 + 52, x1 - 8, my1 + 4, fold=14, fold_side="top")
    else:
        # изголовье на юге: подушки у переднего края, одеяло к северу, отворот — со стороны подушек (снизу)
        py1 = my1 - 6
        _blanket(c, x0 + 8, my0 + 6, x1 - 8, py1 - 52, fold=14, fold_side="bottom")
        for a, b in ((x0 + 22, 124), (132, x1 - 22)):
            _pillow(c, a, py1 - 46, b, py1)
    # --- передняя спинка, столбики, ножки
    yf_g = c.y(df)
    wood(c, x0 + 12, c.y(df + 10, hf), x1 - 12, c.y(df, hf), mul(FRAME, 1.12)[:3], along="h", grain=0.5, ew=0.6)
    _headboard_face(c, x0 + 12, x1 - 12, c.y(df, hf), yf_g, carved=not head_north)
    for px in (x0, x1 - 14):
        wood(c, px, c.y(df + 12, pf), px + 14, c.y(df, pf), mul(FRAME, 1.15)[:3], grain=0.4, ew=0.6)
        post(c, px, px + 14, c.y(df, pf), yf_g, FRAME)
    c.line([(x0, yf_g), (x1, yf_g)], (40, 20, 10), 1.0)


def _bed_side(c: Canvas):
    """90°: изголовье на востоке, ноги на западе; видна передняя (южная) царга во всю длину."""
    H = BED_H
    x0, x1 = 4, 252
    db, df = 250, 6
    # --- северная (дальняя) царга — видна только полоской верха за матрасом
    wood(c, x0 + 12, c.y(db, H["rail"]), x1 - 12, c.y(db - 10, H["rail"]), FRAME, along="h", grain=0.6, ew=0.6)
    # --- матрас
    my0, my1 = c.y(db - 10, H["mattress"]), c.y(df + 10, H["mattress"])
    cloth(c, x0 + 12, my0, x1 - 12, my1, SHEET, light=(0.92, 1.0), along="v")
    c.shade_rect(x0 + 12, my0, x1 - 12, my0 + 6, (30, 16, 10), 0.4, 2.5)
    # --- подушки вдоль изголовья (восток), две: север и юг
    px1 = x1 - 20
    for a, b in ((my0 + 6, (my0 + my1) / 2 - 3), ((my0 + my1) / 2 + 3, my1 - 6)):
        _pillow(c, px1 - 50, a, px1, b)
    _blanket(c, x0 + 14, my0 + 2, px1 - 56, my1 + 2, fold=14, along="v", fold_side="right")
    # --- изголовье на востоке: верх (сдвинут вверх по высоте) и южный торец
    hx0, hx1 = x1 - 16, x1
    wood(c, hx0, c.y(db, H["head"]), hx1, c.y(df + 12, H["head"]), mul(FRAME, 1.12)[:3], along="v", grain=0.6, ew=0.7)
    c.line([(hx0 + 3, c.y(db, H["head"]) + 3), (hx0 + 3, c.y(df + 12, H["head"]) - 2)], mul(FRAME, 1.35), 0.6)
    c.line([(hx1 - 4, c.y(db, H["head"]) + 3), (hx1 - 4, c.y(df + 12, H["head"]) - 2)], mul(FRAME_D, 0.8), 0.6)
    # ножная спинка (запад), ниже
    fx0, fx1 = x0, x0 + 14
    wood(c, fx0, c.y(db, H["foot"]), fx1, c.y(df + 12, H["foot"]), mul(FRAME, 1.12)[:3], along="v", grain=0.6, ew=0.7)
    # дальние столбики (север) — навершия над спинками
    finial(c, (hx0 + hx1) / 2, c.y(db - 6, H["post_head"]) - 10, 8, FRAME)
    finial(c, (fx0 + fx1) / 2, c.y(db - 6, H["post_foot"]) - 10, 7, FRAME)
    # --- передняя (южная) царга во всю длину: лицо с филёнками и меандром
    fy0, fy1 = c.y(df, H["rail"] + 6), c.y(df)
    wood(c, x0 + 12, c.y(df + 10, H["rail"] + 6), x1 - 12, fy0, mul(FRAME, 1.12)[:3], along="h", grain=0.5, ew=0.6)
    wood(c, x0 + 12, fy0, x1 - 12, fy1 - 4, FRAME, along="h", light=(1.12, 0.8), grain=0.8, knots=2)
    meander(c, x0 + 18, x1 - 18, (fy0 + fy1 - 4) / 2, 7.0, GOLD, back=mul(FRAME_D, 0.9))
    c.outline(x0 + 12, fy0, x1 - 12, fy1 - 4, EDGE, 0.7)
    # торцы спинок у зрителя и передние столбики
    post(c, hx0, hx1, c.y(df, H["post_head"]), fy1, FRAME)
    _headboard_face(c, hx0, hx1, c.y(df, H["head"]), fy1, carved=False)
    post(c, fx0, fx1, c.y(df, H["post_foot"]), fy1, FRAME)
    c.line([(x0, fy1), (x1, fy1)], (40, 20, 10), 1.0)


# ---------------------------------------------------------------- верстак 2 x 2

OAK = (232, 168, 96)
OAK_FACE = (176, 106, 52)
LEG = (150, 86, 42)
IRON = (96, 100, 124)
STEEL = (206, 212, 230)


def bench_image(face: str) -> Image.Image:
    """Верстак 2 x 2: 's' — рабочий край к зрителю (0°), 'e' — к востоку (90°, 270° — зеркально), 'n' — от зрителя (180°)."""
    c = Canvas(256, 256 + 120, seed={"s": 11, "e": 12, "n": 13}[face])
    x0, x1 = 10, 246
    db, df = 246, 10
    h = 92                  # высота столешницы (px, x0,5 на экране)
    slab = 12               # толщина столешницы
    top0, top1 = c.y(db, h), c.y(df, h)
    yf_g = c.y(df)
    # --- под столешницей: задняя тень, ножки, нижняя полка с досками и поленьями
    c.rect(x0 + 10, top1 + slab / 2, x1 - 10, yf_g - 4, (64, 38, 22), (40, 24, 14))
    for lx in (24, 214):                                                             # дальние ножки
        c.rect(lx, top1, lx + 16, yf_g - 16, mul(LEG, 0.6), mul(LEG, 0.45))
    shelf_y = c.y(df + 4, 22)
    wood(c, x0 + 14, c.y(db - 30, 22), x1 - 14, shelf_y, mul(OAK, 0.92)[:3], along="h", plank=12, grain=0.7, ew=0.6)
    for k, lx in enumerate((40, 70, 98)):                                            # поленья на полке
        ly = shelf_y - 14 - k % 2 * 3
        c.rect(lx, ly, lx + 26, ly + 10, (150, 92, 52), (110, 64, 34), edge=EDGE, ew=0.5)
        c.ell(lx + 22, ly, lx + 30, ly + 10, (226, 178, 120), edge=EDGE, width=0.5)
        for r in (1.5, 3.0):
            c.ell(lx + 26 - r, ly + 5 - r, lx + 26 + r, ly + 5 + r, None, edge=rgba((176, 122, 70)), width=0.3)
    c.rect(150, shelf_y - 9, 200, shelf_y - 2, (210, 150, 90), (170, 112, 60), edge=EDGE, ew=0.5)   # доски
    c.rect(154, shelf_y - 15, 196, shelf_y - 9, (226, 168, 104), (182, 124, 70), edge=EDGE, ew=0.5)
    wood(c, x0 + 14, shelf_y, x1 - 14, shelf_y + 5, OAK_FACE, along="h", grain=0.5, ew=0.5)
    # --- столешница: толстые доски с волокном, сучками, пятнами
    along = "v" if face == "e" else "h"
    wood(c, x0, top0, x1, top1, OAK, along=along, plank=26 if along == "h" else 26, grain=1.0, knots=4, light=(1.1, 0.92))
    rng = c.rng
    for _ in range(3):                                                               # пятна и подпалины
        sx, sy = rng.uniform(x0 + 30, x1 - 30), rng.uniform(top0 + 30, top1 - 30)
        r = rng.uniform(6, 11)
        c.soft(lambda d, s, sx=sx, sy=sy, r=r: d.ellipse([(sx - r) * s, (sy - r * 0.7) * s, (sx + r) * s, (sy + r * 0.7) * s],
                                                         fill=(140, 80, 30, 34)), 2.5)
    for _ in range(7):                                                               # царапины от работы
        sx, sy = rng.uniform(x0 + 20, x1 - 20), rng.uniform(top0 + 20, top1 - 20)
        ang = rng.uniform(-0.5, 0.5) + (math.pi / 2 if along == "v" else 0)
        L = rng.uniform(6, 14)
        c.line([(sx, sy), (sx + math.cos(ang) * L, sy + math.sin(ang) * L)], mul(OAK, 0.72), 0.4)
    # --- лицевая кромка столешницы (юг) и ножки у зрителя
    wood(c, x0, top1, x1, top1 + slab, OAK_FACE, along="h", grain=0.6, light=(1.15, 0.85))
    for lx in (12, 228):
        wood(c, lx, top1 + slab, lx + 16, yf_g, LEG, along="v", grain=0.6, light=(1.15, 0.8), ew=0.8)
        c.rect(lx - 1, yf_g - 4, lx + 17, yf_g, mul(LEG, 0.75), mul(LEG, 0.6), edge=EDGE, ew=0.5)
    # --- по стороне: доска с инструментами (задник), ящики, тиски, инструменты на столе
    if face == "s":
        _rack(c, x0 + 8, x1 - 8, c.y(db, h + 104), c.y(db - 4, h), back=False)
        _drawers(c, 92, 214, top1 + slab, top1 + slab + 26)
        _vise(c, 206, top1, "s")
        _tools_on_top(c, x0, x1, top0, top1, face)
    elif face == "n":
        _tools_on_top(c, x0, x1, top0, top1, face)
        _vise(c, 206, top0, "n")
        _rack(c, x0 + 8, x1 - 8, c.y(df + 4, h + 56), c.y(df, h), back=True)
    else:
        _rack_side(c, x0, top0, top1, h)
        _tools_on_top(c, x0, x1, top0, top1, face)
        _vise(c, x1 - 4, (top0 + top1) / 2 - 10, "e")
    c.line([(x0, yf_g), (x1, yf_g)], (40, 22, 12), 1.0)
    return c.done()


def _drawers(c: Canvas, x0, x1, y0, y1):
    w = (x1 - x0) / 2
    for k in range(2):
        a = x0 + k * w + 2
        wood(c, a, y0, a + w - 4, y1, mul(OAK_FACE, 1.15)[:3], along="h", grain=0.6, light=(1.12, 0.85), ew=0.7)
        cx = a + (w - 4) / 2
        c.rrect(cx - 8, y0 + 9, cx + 8, y0 + 15, 2.5, (255, 214, 92), edge=(110, 66, 10, 255), width=0.5)  # латунная ручка
        c.line([(cx - 6, y0 + 10), (cx + 6, y0 + 10)], (255, 246, 190), 0.5)
        rivet(c, cx - 10, y0 + 12, 1.0)
        rivet(c, cx + 10, y0 + 12, 1.0)


def _vise(c: Canvas, x, y, face):
    """Тиски: железные губки, винт, рукоять с шариками."""
    if face == "e":
        c.rect(x - 14, y, x + 2, y + 26, mul(IRON, 1.3), mul(IRON, 0.9), edge=EDGE_IRON, ew=0.6)
        c.rect(x - 22, y + 4, x - 14, y + 22, mul(IRON, 1.1), mul(IRON, 0.8), edge=EDGE_IRON, ew=0.5)
        c.line([(x + 2, y + 13), (x + 12, y + 13)], STEEL, 2.0)
        c.line([(x + 12, y + 2), (x + 12, y + 24)], STEEL, 1.4)
        for yy in (y + 2, y + 24):
            c.ell(x + 10, yy - 2, x + 14, yy + 2, STEEL, edge=EDGE_IRON, width=0.4)
        return
    yy = y - 14 if face == "s" else y - 10
    c.rect(x - 14, yy, x + 18, yy + 14, mul(IRON, 1.35), mul(IRON, 0.9), edge=EDGE_IRON, ew=0.6)   # губки
    c.line([(x + 1, yy + 1), (x + 1, yy + 13)], (30, 30, 40), 0.8)
    for k in range(5):
        c.line([(x - 12 + k * 2.5, yy + 2), (x - 12 + k * 2.5, yy + 6)], mul(IRON, 0.7), 0.3)
    if face == "s":
        c.rect(x - 4, yy + 14, x + 8, yy + 30, mul(IRON, 1.1), mul(IRON, 0.75), edge=EDGE_IRON, ew=0.5)
        c.line([(x + 2, yy + 30), (x + 2, yy + 38)], STEEL, 2.0)                      # винт
        c.line([(x - 10, yy + 38), (x + 14, yy + 38)], STEEL, 1.4)                    # вороток
        for xx in (x - 10, x + 14):
            c.ell(xx - 2, yy + 36, xx + 2, yy + 40, STEEL, edge=EDGE_IRON, width=0.4)
    rivet(c, x - 9, yy + 10, 1.1)
    rivet(c, x + 13, yy + 10, 1.1)


def _saw(c: Canvas, x, y, L, ang):
    ca, sa = math.cos(ang), math.sin(ang)
    bw = 11
    p0, p1 = (x, y), (x + ca * L, y + sa * L)
    nx, ny = -sa, ca
    blade = [p0, p1, (p1[0] + nx * bw * 0.6, p1[1] + ny * bw * 0.6), (p0[0] + nx * bw, p0[1] + ny * bw)]
    c.poly(blade, STEEL, edge=EDGE_IRON, width=0.5)
    c.line([(p0[0] + nx * 1.2, p0[1] + ny * 1.2), (p1[0] + nx * 1.0, p1[1] + ny * 1.0)], (244, 246, 255), 0.5)
    n = int(L / 2.2)
    for k in range(n):                                                               # зубья
        t = k / n
        bx, by = p0[0] + (p1[0] - p0[0]) * t + nx * bw * (1 - 0.4 * t), p0[1] + (p1[1] - p0[1]) * t + ny * bw * (1 - 0.4 * t)
        c.line([(bx, by), (bx + nx * 1.4 + ca, by + ny * 1.4 + sa)], (120, 124, 146), 0.4)
    hx, hy = x - ca * 18, y - sa * 18
    c.poly([(x + nx * -2, y + ny * -2), (x + nx * (bw + 3), y + ny * (bw + 3)),
            (hx + nx * (bw + 3), hy + ny * (bw + 3)), (hx + nx * -2, hy + ny * -2)], (176, 92, 44), edge=EDGE, width=0.6)
    c.ell(hx + ca * 4 + nx * 2, hy + sa * 4 + ny * 2, hx + ca * 8 + nx * 6, hy + sa * 8 + ny * 6, (96, 50, 24))


def _mallet(c: Canvas, x, y, ang):
    ca, sa = math.cos(ang), math.sin(ang)
    hx, hy = x + ca * 40, y + sa * 40
    c.soft(lambda d, s_: d.line([((x + 2) * s_, (y + 3) * s_), ((hx + 2) * s_, (hy + 3) * s_)], fill=(60, 30, 10, 110),
                                width=int(5 * s_)), 1.5)
    c.line([(x, y), (hx, hy)], (120, 70, 36), 5.0)
    c.line([(x, y), (hx, hy)], (196, 132, 74), 2.4)
    nx, ny = -sa, ca
    head = [(hx + nx * 12 - ca * 6, hy + ny * 12 - sa * 6), (hx + nx * 12 + ca * 9, hy + ny * 12 + sa * 9),
            (hx - nx * 12 + ca * 9, hy - ny * 12 + sa * 9), (hx - nx * 12 - ca * 6, hy - ny * 12 - sa * 6)]
    c.poly(head, (186, 120, 64), edge=EDGE, width=0.7)
    c.line([head[0], head[1]], (226, 168, 110), 0.6)
    for t in (0.3, 0.7):
        a = (head[0][0] + (head[3][0] - head[0][0]) * t, head[0][1] + (head[3][1] - head[0][1]) * t)
        b = (head[1][0] + (head[2][0] - head[1][0]) * t, head[1][1] + (head[2][1] - head[1][1]) * t)
        c.line([a, b], (140, 84, 40), 0.4)


def _chisels(c: Canvas, x, y):
    # кожаный свёрток-скатка со стамесками
    c.rrect(x - 4, y - 3, x + 52, y + 27, 3, (150, 86, 46), edge=EDGE, width=0.6)
    c.rrect(x - 2, y - 1, x + 50, y + 25, 2, (186, 112, 62))
    for k in range(5):
        c.line([(x + 2, y + 3 + k * 5), (x + 46, y + 3 + k * 5)], (140, 80, 40), 0.4)
    for k, (col, L) in enumerate((((214, 74, 52), 28), ((52, 130, 214), 22), ((236, 190, 60), 26), ((60, 160, 90), 18))):
        yy = y + 1 + k * 6
        c.rrect(x, yy, x + 14, yy + 5, 2.0, col, edge=EDGE, width=0.4)
        c.line([(x + 2, yy + 1.2), (x + 11, yy + 1.2)], mul(col, 1.4), 0.5)
        c.line([(x + 14, yy + 2.5), (x + 14 + L, yy + 2.5)], STEEL, 2.0)
        c.line([(x + 14, yy + 1.8), (x + 14 + L, yy + 1.8)], (246, 248, 255), 0.5)
        c.line([(x + 14 + L, yy + 1.2), (x + 16.5 + L, yy + 2.5), (x + 14 + L, yy + 3.8)], (150, 156, 176), 0.6)


def _square(c: Canvas, x, y):
    c.poly([(x, y), (x + 44, y), (x + 44, y + 6), (x + 6, y + 6), (x + 6, y + 32), (x, y + 32)], STEEL, edge=EDGE_IRON, width=0.6)
    c.line([(x + 6, y + 1), (x + 43, y + 1)], (250, 252, 255), 0.5)
    for k in range(1, 10):
        c.line([(x + 4 * k + 4, y), (x + 4 * k + 4, y + (3 if k % 2 else 4.5))], (80, 84, 100), 0.4)
    c.rect(x - 4, y - 1, x + 7, y + 33, (170, 100, 50), (120, 68, 32), edge=EDGE, ew=0.5)
    rivet(c, x + 1.5, y + 8, 1.1)
    rivet(c, x + 1.5, y + 24, 1.1)


def _plane(c: Canvas, x, y):
    c.soft(lambda d, s_: d.rounded_rectangle([(x + 2) * s_, (y + 4) * s_, (x + 48) * s_, (y + 22) * s_], radius=4 * s_,
                                             fill=(60, 30, 10, 110)), 1.5)
    c.rrect(x, y, x + 46, y + 17, 4, (210, 140, 74), edge=EDGE, width=0.7)
    c.rrect(x + 2, y + 2, x + 44, y + 8, 3, (236, 170, 100))
    c.rect(x + 18, y + 3, x + 27, y + 14, (60, 62, 76), (40, 42, 56), edge=EDGE_IRON, ew=0.4)
    c.line([(x + 19, y + 4), (x + 26, y + 4)], STEEL, 0.8)
    c.ell(x + 32, y + 3, x + 42, y + 14, (176, 104, 50), edge=EDGE, width=0.5)
    c.ell(x + 4, y + 5, x + 12, y + 12, (196, 120, 60), edge=EDGE, width=0.4)


def _shavings(c: Canvas, x, y, n=6):
    rng = c.rng
    for _ in range(n):
        sx, sy = x + rng.uniform(-14, 14), y + rng.uniform(-8, 8)
        r = rng.uniform(3.0, 5.5)
        c.d.arc([(sx - r) * S, (sy - r) * S, (sx + r) * S, (sy + r) * S], start=rng.uniform(0, 180), end=rng.uniform(250, 400),
                fill=(250, 214, 150, 255), width=int(1.2 * S))
        c.d.arc([(sx - r) * S, (sy - r) * S, (sx + r) * S, (sy + r) * S], start=rng.uniform(0, 90), end=rng.uniform(150, 250),
                fill=(196, 140, 80, 255), width=int(0.4 * S))


def _nails_pot(c: Canvas, x, y):
    r = 11
    c.soft(lambda d, s_: d.ellipse([(x - r + 2) * s_, (y + 8) * s_, (x + r + 4) * s_, (y + 20) * s_], fill=(60, 30, 10, 120)), 1.5)
    c.rect(x - r, y, x + r, y + 14, (206, 100, 58), (150, 62, 36))
    c.ell(x - r, y + 9, x + r, y + 18, (150, 62, 36))
    c.rect(x - r, y + 4, x + r, y + 9, (40, 30, 28), (30, 22, 20))                 # чёрнофигурный пояс
    meander(c, x - r + 1, x + r - 1, y + 6.5, 4.0, (232, 150, 90))
    c.ell(x - r, y - 4, x + r, y + 4, (232, 136, 82), edge=EDGE, width=0.6)
    c.ell(x - r + 2, y - 2.5, x + r - 2, y + 2.5, (56, 36, 26))
    for k in range(8):
        c.line([(x - 6 + k * 1.7, y - 1), (x - 8 + k * 2.2, y - 7)], (186, 192, 210), 0.7)
        c.ell(x - 8.6 + k * 2.2, y - 8, x - 7.4 + k * 2.2, y - 6.6, (220, 224, 238))
    c.line([(x - r + 2, y + 1), (x - r + 2, y + 12)], (246, 170, 120), 0.6)


def _board(c: Canvas, x, y, w, h, vertical=False):
    """Заготовка: светлая доска с разметкой карандашом и тенью."""
    c.soft(lambda d, s_: d.rectangle([(x + 2) * s_, (y + 3) * s_, (x + w + 3) * s_, (y + h + 4) * s_], fill=(60, 30, 10, 110)), 1.6)
    wood(c, x, y, x + w, y + h, (246, 206, 150), along="v" if vertical else "h", grain=0.8, ew=0.6, light=(1.06, 0.92))
    if vertical:
        c.line([(x + w * 0.5, y + 6), (x + w * 0.5, y + h - 6)], (70, 60, 70), 0.4)
        for k in range(1, 4):
            c.line([(x + w * 0.35, y + h * k / 4), (x + w * 0.65, y + h * k / 4)], (70, 60, 70), 0.4)
    else:
        c.line([(x + 6, y + h * 0.5), (x + w - 6, y + h * 0.5)], (70, 60, 70), 0.4)
        for k in range(1, 4):
            c.line([(x + w * k / 4, y + h * 0.3), (x + w * k / 4, y + h * 0.7)], (70, 60, 70), 0.4)


def _ingot(c: Canvas, ix, iy):
    c.soft(lambda d, s_: d.polygon([((ix + 2) * s_, (iy + 4) * s_), ((ix + 32) * s_, (iy + 2) * s_), ((ix + 36) * s_, (iy + 16) * s_),
                                    ((ix + 6) * s_, (iy + 18) * s_)], fill=(60, 30, 10, 110)), 1.5)
    c.poly([(ix, iy + 3), (ix + 30, iy), (ix + 34, iy + 12), (ix + 4, iy + 15)], (220, 110, 50), edge=(110, 46, 18, 255), width=0.6)
    c.poly([(ix + 3, iy + 3), (ix + 28, iy + 1), (ix + 30, iy + 8), (ix + 5, iy + 10)], (250, 156, 84))
    c.line([(ix + 5, iy + 3.5), (ix + 26, iy + 1.8)], (255, 214, 170), 0.8)


def _tools_on_top(c: Canvas, x0, x1, top0, top1, face):
    """Инструменты на столешнице: заготовка с разметкой, пила, киянка, скатка стамесок, угольник, рубанок, стружка,
    горшок с гвоздями (чёрнофигурный), медный слиток."""
    if face == "e":
        _board(c, x0 + 96, top0 + 40, 40, 150, vertical=True)
        _saw(c, x0 + 176, top0 + 40, 96, math.pi / 2 - 0.1)
        _mallet(c, x0 + 40, top0 + 186, -1.0)
        _chisels(c, x0 + 30, top0 + 110)
        _plane(c, x0 + 150, top0 + 170)
        _shavings(c, x0 + 196, top0 + 200, 9)
        _square(c, x0 + 30, top0 + 30)
        _nails_pot(c, x0 + 204, top0 + 24)
        _ingot(c, x0 + 96, top0 + 200)
        return
    near = top1 - 74 if face == "s" else top0 + 22
    far = top0 + 26 if face == "s" else top1 - 92
    _saw(c, x0 + 34, far + 16, 100, -0.1)
    _square(c, x0 + 152, far - 4)
    _nails_pot(c, x0 + 216, far + 10)
    _board(c, x0 + 60, (far + near) / 2 + 8, 130, 30)
    _ingot(c, x0 + 196, (far + near) / 2 + 14)
    _mallet(c, x0 + 22, near + 50, -0.6)
    _chisels(c, x0 + 84, near + 16)
    _plane(c, x0 + 150, near + 26)
    _shavings(c, x0 + 206, near + 46, 9)


def _rack(c: Canvas, x0, x1, ytop, ybot, back: bool):
    """Доска с инструментами (лицо к зрителю) на двух стойках; back — видна обратная сторона (180°): доски и распорки."""
    for sx in (x0, x1 - 10):
        wood(c, sx, ytop - 4, sx + 10, ybot, LEG, along="v", grain=0.5, light=(1.12, 0.82), ew=0.7)
    a, b = x0 + 10, x1 - 10
    if back:
        wood(c, a, ytop + 4, b, ybot, (150, 92, 50), along="h", plank=10, grain=0.8, knots=2, light=(1.08, 0.85))
        for k in range(3):
            xx = a + (b - a) * (k + 0.5) / 3
            c.line([(xx - 18, ybot - 3), (xx + 18, ytop + 7)], (110, 62, 30), 2.0)
            c.line([(xx - 18, ybot - 3), (xx + 18, ytop + 7)], (176, 112, 60), 0.8)
        return
    wood(c, a, ytop + 4, b, ybot, (162, 100, 54), along="h", plank=11, grain=0.8, knots=2, light=(1.1, 0.82))
    c.line([(a, ytop + 4), (b, ytop + 4)], (222, 160, 100), 0.8)
    # колышки и висящие инструменты: топор, пила-лучковка, клещи, моток верёвки, ножницы
    rng = c.rng
    for k in range(12):
        px = a + 8 + k * (b - a - 16) / 11
        c.ell(px - 1.4, ytop + 9, px + 1.4, ytop + 11.8, (60, 34, 18))
    yb = ytop + 10
    # топор
    c.line([(a + 18, yb + 2), (a + 18, yb + 38)], (176, 112, 60), 2.4)
    c.poly([(a + 18, yb + 3), (a + 32, yb + 1), (a + 34, yb + 14), (a + 18, yb + 12)], STEEL, edge=EDGE_IRON, width=0.5)
    c.line([(a + 33, yb + 2), (a + 34, yb + 13)], (246, 248, 255), 0.6)
    # лучковая пила
    c.line([(a + 50, yb + 4), (a + 50, yb + 34)], (176, 112, 60), 1.8)
    c.line([(a + 86, yb + 4), (a + 86, yb + 34)], (176, 112, 60), 1.8)
    c.line([(a + 50, yb + 19), (a + 86, yb + 19)], (140, 84, 40), 1.4)
    c.line([(a + 50, yb + 33), (a + 86, yb + 33)], STEEL, 1.0)
    c.line([(a + 50, yb + 5), (a + 86, yb + 5)], (210, 190, 150), 0.5)
    # клещи
    cx = a + 108
    c.line([(cx, yb + 2), (cx - 4, yb + 34)], (110, 114, 136), 1.6)
    c.line([(cx, yb + 2), (cx + 4, yb + 34)], (110, 114, 136), 1.6)
    rivet(c, cx, yb + 10, 1.4)
    # моток верёвки
    rx, ry = a + 140, yb + 18
    for r, col in ((13, (186, 150, 90)), (10, (214, 178, 112)), (7, (186, 150, 90)), (4, (160, 124, 70))):
        c.ell(rx - r, ry - r * 0.9, rx + r, ry + r * 0.9, None, edge=rgba(col), width=1.4)
    c.line([(rx, yb + 2), (rx, ry - 12)], (160, 124, 70), 1.0)
    # молоток
    c.line([(a + 172, yb + 4), (a + 172, yb + 38)], (176, 112, 60), 2.2)
    c.rect(a + 164, yb + 2, a + 182, yb + 9, mul(IRON, 1.4), mul(IRON, 0.9), edge=EDGE_IRON, ew=0.5)
    # циркуль и линейка
    c.line([(a + 196, yb + 3), (a + 191, yb + 32)], STEEL, 1.0)
    c.line([(a + 196, yb + 3), (a + 201, yb + 32)], STEEL, 1.0)
    c.ell(a + 194, yb + 1, a + 198, yb + 5, (255, 214, 92))
    c.rect(a + 206, yb + 2, a + 212, yb + 40, (244, 214, 140), (214, 180, 100), edge=EDGE, ew=0.4)
    for k in range(9):
        c.line([(a + 206, yb + 5 + k * 4), (a + 209, yb + 5 + k * 4)], (110, 80, 40), 0.3)


def _rack_side(c: Canvas, x0, top0, top1, h):
    """90°: доска с инструментами вдоль западного края — видна сверху (кромка) и торцом у зрителя."""
    hgt = 100
    yt0, yt1 = top0 - hgt * V, top1 - hgt * V
    wood(c, x0, yt0, x0 + 12, yt1 + 6, (162, 100, 54), along="v", grain=0.6, ew=0.7)
    for k in range(9):                                                              # торчащие ручки инструментов над кромкой
        yy = yt0 + 14 + k * (yt1 - yt0 - 28) / 8
        col = [(176, 112, 60), STEEL, (214, 74, 52), (110, 114, 136)][k % 4]
        c.line([(x0 + 12, yy), (x0 + 20, yy + 1)], col, 1.6)
        c.ell(x0 + 18, yy - 1.5, x0 + 22, yy + 2.5, col, edge=EDGE, width=0.3)
    wood(c, x0, yt1 + 6, x0 + 12, top1 + 12, LEG, along="v", grain=0.6, light=(1.12, 0.82), ew=0.7)


# ---------------------------------------------------------------- сундук 1 x 1

CH_WOOD = (214, 128, 58)
CH_WOOD_T = (240, 162, 82)


def chest_image(face: str) -> Image.Image:
    """Сундук 1 x 1: 's' — замок к зрителю (0°), 'e' — к востоку (90°, 270° — зеркально), 'n' — от зрителя (180°)."""
    c = Canvas(128, 160, seed={"s": 21, "e": 22, "n": 23}[face])
    if face == "e":
        x0, x1, df, db = 30, 98, 8, 92
    else:
        x0, x1, df, db = 14, 114, 22, 86
    hb, hl = 48, 72                                     # высота корпуса и верха крышки
    yg = c.y(df)
    # --- крышка-«бочка»: верх полосами (выпуклость к свету), передний скат
    body_top = c.y(df, hb)
    lid_top0, lid_top1 = c.y(db, hl), c.y(df + 10, hl)
    along = "v" if face == "e" else "h"
    wood(c, x0, lid_top0, x1, lid_top1, CH_WOOD_T, along=along, plank=8 if along == "h" else 10, grain=0.9, knots=1,
         light=(0.86, 1.16))
    c.shade_rect(x0 + 1, lid_top0 + 1, x1 - 1, lid_top0 + 8, (60, 20, 0), 0.4, 2.0)   # дальний скат темнее
    c.soft(lambda d, s_: d.rectangle([(x0 + 2) * s_, (lid_top1 - 10) * s_, (x1 - 2) * s_, (lid_top1 - 4) * s_],
                                     fill=(255, 240, 200, 70)), 2.0)                   # блик на изгибе
    # передний скат крышки (между верхом и корпусом) — светлый на изгибе
    wood(c, x0, lid_top1, x1, body_top, mul(CH_WOOD_T, 0.94)[:3], along="h", grain=0.7, light=(1.18, 0.86))
    c.line([(x0 + 1, lid_top1 + 1), (x1 - 1, lid_top1 + 1)], (255, 214, 150), 0.7)
    # --- корпус (лицо)
    wood(c, x0, body_top, x1, yg, CH_WOOD, along="h", plank=10, grain=0.9, knots=1, light=(1.04, 0.78))
    c.shade_rect(x0, body_top, x1, body_top + 4, (40, 14, 0), 0.5, 1.2)            # тень под крышкой
    c.line([(x0, body_top), (x1, body_top)], (70, 30, 10), 1.0)                     # щель крышки
    c.line([(x0, body_top - 1.2), (x1, body_top - 1.2)], (255, 206, 140), 0.5)
    # --- окованные углы и полосы
    bands = [x0 + 8, x1 - 16] if face != "e" else [x0 + 6, x1 - 14]
    if face == "e":
        for by in (lid_top0 + 14, lid_top1 - 18):                                   # полосы поперёк (вдоль x)
            iron_band(c, x0, by, x1, by + 7, rivets=3, vertical=False)
        for bx in (x0 + 4, x1 - 12):
            iron_band(c, bx, body_top, bx + 8, yg - 1, rivets=2)
    else:
        for bx in bands:
            iron_band(c, bx, lid_top0, bx + 8, lid_top1, rivets=3)
            iron_band(c, bx, lid_top1, bx + 8, body_top, rivets=1)
            iron_band(c, bx, body_top, bx + 8, yg - 1, rivets=2)
    for cx in (x0, x1 - 7):                                                          # уголки
        c.poly([(cx, yg - 9), (cx + 7, yg - 1), (cx, yg - 1)] if cx == x0 else [(cx + 7, yg - 9), (cx + 7, yg - 1), (cx, yg - 1)],
               (110, 114, 136), edge=EDGE_IRON, width=0.4)
    # --- замок / петли / ручка
    if face == "s":
        mx = (x0 + x1) / 2
        c.rrect(mx - 8, body_top - 7, mx + 8, body_top + 13, 2.5, (255, 212, 80), edge=(110, 66, 10, 255), width=0.6)
        c.rrect(mx - 6, body_top - 5, mx + 6, body_top + 11, 2, (232, 170, 40))
        c.line([(mx - 5, body_top - 4), (mx + 4, body_top - 4)], (255, 244, 180), 0.5)
        c.ell(mx - 2, body_top + 1, mx + 2, body_top + 5, (40, 24, 12))
        c.poly([(mx - 1, body_top + 4), (mx + 1, body_top + 4), (mx + 1.6, body_top + 9), (mx - 1.6, body_top + 9)], (40, 24, 12))
        for rx in (mx - 5.5, mx + 5.5):
            rivet(c, rx, body_top + 9.5, 0.8)
    elif face == "n":
        for hx in ((x0 + x1) / 2 - 20, (x0 + x1) / 2 + 20):                         # петли у зрителя
            c.rect(hx - 6, lid_top1 - 2, hx + 6, body_top + 7, (110, 114, 136), (70, 74, 98), edge=EDGE_IRON, ew=0.5)
            c.rect(hx - 6, body_top - 1, hx + 6, body_top + 1.5, (176, 182, 204))
            rivet(c, hx, body_top + 4, 0.9)
        mx = (x0 + x1) / 2                                                           # замок на дальнем скате
        c.rrect(mx - 6, lid_top0 - 3, mx + 6, lid_top0 + 6, 2, (255, 212, 80), edge=(110, 66, 10, 255), width=0.5)
    else:
        c.rrect(x1 - 6, (lid_top0 + lid_top1) / 2 - 8, x1 + 2, (lid_top0 + lid_top1) / 2 + 8, 2, (255, 212, 80),
                edge=(110, 66, 10, 255), width=0.5)
        c.ell(x1 - 4, (lid_top0 + lid_top1) / 2 - 2, x1, (lid_top0 + lid_top1) / 2 + 2, (40, 24, 12))
        mx = (x0 + x1) / 2                                                           # ручка-кольцо на торце
        c.rect(mx - 6, body_top + 6, mx + 6, body_top + 10, (110, 114, 136), (70, 74, 98), edge=EDGE_IRON, ew=0.4)
        c.d.arc([(mx - 7) * S, (body_top + 6) * S, (mx + 7) * S, (body_top + 22) * S], 0, 180, fill=(60, 62, 80, 255),
                width=int(2.2 * S))
        c.d.arc([(mx - 7) * S, (body_top + 6) * S, (mx + 7) * S, (body_top + 22) * S], 20, 120, fill=(200, 206, 226, 255),
                width=int(0.8 * S))
    c.outline(x0, lid_top0, x1, yg, EDGE, 0.9)
    c.line([(x0, yg), (x1, yg)], (40, 22, 12), 1.4)
    return c.done()
