"""D-004 шаг 1а — дракон «вырезкой»: части тела кодом (PIL) + описание скелета для клиента.

    python tools/art_d004_parts.py              # части → client/Assets/Art/_TEMP/Resources/D004/TEMP_dragon_*.png,
                                                # скелет → .../D004/TEMP_dragon_rig.json, лист частей и эскизы поз
                                                # для прокраски на Маке → build/art_d004/
    python tools/art_d004_parts.py --painted    # то же, но заливка частей берётся из прокраски Мака
                                                # (build/art_d004/raw/paint_<лист>_<d>.png), контур — свой

Части рисуются в своих координатах (тайлы, опора — сустав к родителю, кость вдоль +x), стиль Б (11a/11g):
тонкий сливово-коричневый контур, плоская заливка с двумя ступенями тени от света слева сверху и бликом,
чешуя узором. Ракурс — сверху под небольшим углом (как у стен E2-17): у тела видна спина (светлая полоса
с гребнем), дальние лапы и крыло темнее и выше по экрану. Скелет (суставы, точки крепления, порядок
отрисовки по видам side/front/back) — в JSON рядом, клиент собирает иерархию по нему (DragonRig.cs).
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "client" / "Assets" / "Art" / "_TEMP" / "Resources" / "D004"
BUILD = ROOT / "build" / "art_d004"
PPT = 256            # пикселей на тайл в готовой части (клиент: Sprite.Create с тем же числом)
SS = 3               # сверхвыборка при рисовании
S = PPT * SS
OUTLINE = 6          # толщина контура, px сверхвыборки (≈ 2 px в части)

# ---------------------------------------------------------------- палитра (стиль Б: яркая, тёплая)
INK = (52, 28, 40)
GREEN = dict(base=(48, 152, 92), shade=(30, 108, 78), deep=(22, 78, 64), light=(96, 196, 112), hi=(176, 236, 150))
GREEN_TOP = dict(base=(74, 178, 104), shade=(48, 140, 90), deep=(30, 104, 76), light=(124, 214, 124), hi=(196, 244, 168))
BRONZE = dict(base=(200, 142, 62), shade=(146, 94, 42), deep=(104, 62, 32), light=(238, 192, 104), hi=(255, 236, 170))
GOLD = dict(base=(230, 190, 100), shade=(186, 136, 64), deep=(140, 94, 46), light=(250, 220, 140), hi=(255, 246, 200))
MEMB = dict(base=(62, 150, 118), shade=(40, 112, 98), deep=(28, 84, 80), light=(96, 184, 134), hi=(150, 220, 160))
HORN = dict(base=(214, 170, 96), shade=(160, 112, 58), deep=(112, 72, 40), light=(244, 214, 150), hi=(255, 244, 210))
MOUTH = dict(base=(150, 40, 46), shade=(104, 24, 36), deep=(70, 16, 28), light=(190, 70, 70), hi=(220, 110, 100))
TOOTH = dict(base=(246, 240, 222), shade=(206, 196, 170), deep=(170, 158, 130), light=(255, 255, 245), hi=(255, 255, 255))
EYE = (255, 196, 44)
FAR = 0.68           # дальние лапы и крыло — темнее (клиент умножает цвет)


# ---------------------------------------------------------------- геометрия
def cr(points, closed=True, n=10):
    """Сглаженный контур Катмулла — Рома через точки."""
    P = [np.array(p, float) for p in points]
    m = len(P)
    out = []
    rng = range(m) if closed else range(m - 1)
    for i in rng:
        p0 = P[(i - 1) % m] if closed else P[max(i - 1, 0)]
        p1, p2 = P[i], P[(i + 1) % m]
        p3 = P[(i + 2) % m] if closed else P[min(i + 2, m - 1)]
        for k in range(n):
            t = k / n
            t2, t3 = t * t, t * t * t
            q = 0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3)
            out.append((float(q[0]), float(q[1])))
    if not closed:
        out.append(tuple(P[-1]))
    return out


def capsule(x0, x1, r0, r1, n=24, y=0.0):
    """Капсула вдоль +x: окружности r0 в x0 и r1 в x1, касательные между ними."""
    pts = []
    for k in range(n + 1):
        a = math.pi / 2 + math.pi * k / n
        pts.append((x0 + r0 * math.cos(a), y + r0 * math.sin(a)))
    for k in range(n + 1):
        a = -math.pi / 2 + math.pi * k / n
        pts.append((x1 + r1 * math.cos(a), y + r1 * math.sin(a)))
    return pts


def ellipse(cx, cy, rx, ry, n=40, rot=0.0):
    c, s = math.cos(rot), math.sin(rot)
    return [(cx + rx * math.cos(2 * math.pi * k / n) * c - ry * math.sin(2 * math.pi * k / n) * s,
             cy + rx * math.cos(2 * math.pi * k / n) * s + ry * math.sin(2 * math.pi * k / n) * c) for k in range(n)]


def mirror_x(pts):
    return [(-x, y) for x, y in pts]


def spike(base_a, base_b, h, lean=0.25):
    """Треугольный шип на отрезке base_a→base_b, высота h наружу (влево от направления), с наклоном назад."""
    ax, ay = base_a
    bx, by = base_b
    dx, dy = bx - ax, by - ay
    L = math.hypot(dx, dy) or 1
    nx, ny = -dy / L, dx / L
    mx, my = (ax + bx) / 2 - dx * lean, (ay + by) / 2 - dy * lean
    return [base_a, (mx + nx * h, my + ny * h), base_b]


def along(curve, t):
    """Точка и касательная на ломаной по доле длины."""
    seg = [math.dist(curve[i], curve[i + 1]) for i in range(len(curve) - 1)]
    total = sum(seg)
    d = t * total
    for i, s in enumerate(seg):
        if d <= s or i == len(seg) - 1:
            u = d / s if s else 0
            p = (curve[i][0] + (curve[i + 1][0] - curve[i][0]) * u, curve[i][1] + (curve[i + 1][1] - curve[i][1]) * u)
            return p, ((curve[i + 1][0] - curve[i][0]) / (s or 1), (curve[i + 1][1] - curve[i][1]) / (s or 1))
        d -= s
    return curve[-1], (1, 0)


# ---------------------------------------------------------------- отрисовка
class Part:
    """Часть: слои (форма, материал, детали) в координатах тайлов, опора (0,0); точки крепления детей."""

    def __init__(self, name):
        self.name = name
        self.layers = []      # (pts, mat, opts)
        self.anchors = {}     # имя → (x, y) в тайлах от опоры
        self.lines = []       # (pts, color, width_px_ss, closed) — поверх слоёв (вены, складки)

    def add(self, pts, mat, **opts):
        self.layers.append((pts, mat, opts))
        return self

    def bounds(self):
        xs = [x for pts, _, _ in self.layers for x, _ in pts]
        ys = [y for pts, _, _ in self.layers for _, y in pts]
        return min(xs), min(ys), max(xs), max(ys)


def to_px(pts, ox, oy):
    return [((x - ox) * S, (oy - y) * S) for x, y in pts]


def mask_of(size, pts_px):
    m = Image.new("L", size, 0)
    ImageDraw.Draw(m).polygon(pts_px, fill=255)
    return m


def shift(m, dx, dy):
    return ImageChops.offset(m, int(dx), int(dy))


def dilate(m, r):
    # круглое расширение маски: размытие + порог (быстрее MaxFilter на больших размерах)
    if r <= 0:
        return m
    b = m.filter(ImageFilter.GaussianBlur(r * 0.55))
    return b.point(lambda v: 255 if v > 18 else 0)


def scales_pattern(size, cell, color, alpha, seed=0):
    """Узор чешуи: ряды дуг «U» со сдвигом через ряд."""
    w, h = size
    img = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cw, ch = cell, cell * 0.72
    row = 0
    y = -ch
    while y < h + ch:
        off = (row % 2) * cw / 2
        x = -cw + off
        while x < w + cw:
            d.arc([x, y - ch * 0.6, x + cw, y + ch * 0.9], 20, 160, fill=color + (alpha,), width=max(2, int(cell / 9)))
            x += cw
        y += ch
        row += 1
    return img


def render(part: Part, margin=0.04):
    x0, y0, x1, y1 = part.bounds()
    x0 -= margin; y0 -= margin; x1 += margin; y1 += margin
    W, H = int(math.ceil((x1 - x0) * S)), int(math.ceil((y1 - y0) * S))
    # опора — в целом пикселе готовой части
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    union = Image.new("L", (W, H), 0)
    for pts, mat, o in part.layers:
        px = to_px(pts, x0, y1)
        m = mask_of((W, H), px)
        if o.get("clip"):
            m = ImageChops.multiply(m, union)
        if not o.get("no_outline", False):
            ow = o.get("outline", OUTLINE)
            ring = dilate(m, ow)
            img.paste(Image.new("RGBA", (W, H), INK + (255,)), (0, 0), ring)
        shaded = shade(m, mat, o, (W, H))
        img.alpha_composite(shaded)
        union = ImageChops.lighter(union, dilate(m, o.get("outline", OUTLINE)) if not o.get("no_outline") else m)
    for pts, color, wpx, closed in part.lines:
        px = to_px(pts, x0, y1)
        line = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        d = ImageDraw.Draw(line)
        if closed:
            px = px + [px[0]]
        d.line(px, fill=color, width=int(wpx), joint="curve")
        a = ImageChops.multiply(line.getchannel("A"), img.getchannel("A"))
        line.putalpha(a)
        img.alpha_composite(line)
    out = img.resize((max(1, W // SS), max(1, H // SS)), Image.LANCZOS)
    pivot = ((0 - x0) / (x1 - x0), (0 - y0) / (y1 - y0))   # доли от левого НИЖНЕГО угла (как у Unity)
    return out, pivot


def shade(m, mat, o, size):
    """Заливка: основа, тень справа снизу в две ступени, светлая кромка слева сверху, блик; чешуя по желанию."""
    W, H = size
    k = o.get("k", 1.0)            # масштаб смещений (мелким частям — меньше)
    lx, ly = o.get("light", (-1.0, 1.0))  # откуда свет (экран: x вправо, y вверх)
    # смещение в px: тень — сдвиг формы к свету, разность = кромка с теневой стороны
    sd = 0.055 * S * k
    sx, sy = lx * sd, -ly * sd
    layer = Image.new("RGBA", size, mat["base"] + (255,))
    out = Image.new("RGBA", size, (0, 0, 0, 0))
    out.paste(layer, (0, 0), m)
    if o.get("flat"):
        return out
    shade1 = ImageChops.subtract(m, shift(m, sx, sy))
    shade1 = shade1.filter(ImageFilter.GaussianBlur(1.5))
    out.paste(Image.new("RGBA", size, mat["shade"] + (255,)), (0, 0), ImageChops.multiply(shade1, m))
    shade2 = ImageChops.subtract(m, shift(m, sx * 0.45, sy * 0.45))
    out.paste(Image.new("RGBA", size, mat["deep"] + (255,)), (0, 0), ImageChops.multiply(shade2, m))
    if o.get("scales"):
        cell = o["scales"] * S
        pat = scales_pattern(size, cell, mat["deep"], 150)
        pa = ImageChops.multiply(pat.getchannel("A"), m)
        pat.putalpha(pa)
        out.alpha_composite(pat)
        pat2 = scales_pattern(size, cell, mat["light"], 90)
        pat2 = ImageChops.offset(pat2, 0, -int(cell * 0.08))
        pa2 = ImageChops.multiply(pat2.getchannel("A"), ImageChops.subtract(m, shade1))
        pat2.putalpha(pa2)
        out.alpha_composite(pat2)
    if o.get("bands"):
        # поперечные пластины (брюхо): линии через каждые bands тайла вдоль x
        step = o["bands"] * S
        bl = Image.new("L", size, 0)
        d = ImageDraw.Draw(bl)
        x = 0.0
        while x < W:
            d.line([(x, 0), (x + o.get("band_slant", 0.0) * H, H)], fill=200, width=max(2, int(S * 0.012)))
            x += step
        out.paste(Image.new("RGBA", size, mat["deep"] + (255,)), (0, 0), ImageChops.multiply(bl, m))
    lit = ImageChops.subtract(m, shift(m, -sx * 0.7, -sy * 0.7))
    lit = lit.filter(ImageFilter.GaussianBlur(2))
    out.paste(Image.new("RGBA", size, mat["light"] + (255,)), (0, 0), ImageChops.multiply(lit, m).point(lambda v: int(v * 0.9)))
    if not o.get("no_hi"):
        hi = ImageChops.subtract(m, shift(m, -sx * 0.25, -sy * 0.25))
        hi = ImageChops.subtract(hi, shift(m, sx * 1.6, -sy * 0.2))  # только короткая дуга у верхнего-левого края
        hi = hi.filter(ImageFilter.GaussianBlur(1.2))
        out.paste(Image.new("RGBA", size, mat["hi"] + (255,)), (0, 0), ImageChops.multiply(hi, m).point(lambda v: int(v * 0.8)))
    return out


# ---------------------------------------------------------------- части: вид сбоку (смотрит вправо)
def body_side():
    p = Part("body_side")
    top = [(0.74, 0.20), (0.58, 0.34), (0.30, 0.40), (-0.05, 0.37), (-0.40, 0.30), (-0.66, 0.20), (-0.82, 0.08)]
    bottom = [(-0.82, -0.08), (-0.62, -0.24), (-0.28, -0.32), (0.10, -0.32), (0.44, -0.27), (0.68, -0.14), (0.80, 0.02)]
    body = cr(top + bottom, n=8)
    p.add(body, GREEN, scales=0.075)
    # спина (видна сверху): светлая полоса между верхним контуром и линией гребня
    ridge = [(0.70, 0.18), (0.40, 0.25), (0.05, 0.24), (-0.35, 0.18), (-0.66, 0.08), (-0.84, 0.0)]
    back = cr(top + ridge[::-1], closed=True, n=6)
    p.add(back, GREEN_TOP, clip=True, no_outline=True, scales=0.07, k=0.5)
    # брюхо — золотые пластины
    belly = cr([(-0.70, -0.14), (-0.40, -0.22), (0.0, -0.24), (0.40, -0.20), (0.70, -0.06), (0.74, -0.16),
                (0.44, -0.29), (0.10, -0.34), (-0.28, -0.34), (-0.62, -0.26)], n=6)
    p.add(belly, GOLD, clip=True, outline=4, bands=0.075, band_slant=-0.15, k=0.5)
    # гребень: бронзовые шипы вдоль линии хребта
    rc = cr(ridge, closed=False, n=8)
    for i in range(9):
        t = 0.06 + i * 0.105
        (x, y), (tx, ty) = along(rc, t)
        h = 0.10 * (1.0 - abs(t - 0.4) * 0.9)
        a, b = (x - tx * 0.05, y - ty * 0.05), (x + tx * 0.05, y + ty * 0.05)
        p.add(spike(b, a, h, lean=0.35), BRONZE, outline=4, k=0.3, no_hi=True)
    p.anchors = dict(neck=(0.70, 0.14), tail=(-0.78, 0.02), shoulder_near=(0.44, -0.10), shoulder_far=(0.50, 0.06),
                     hip_near=(-0.46, -0.10), hip_far=(-0.40, 0.06), wing_near=(0.22, 0.22), wing_far=(0.30, 0.30))
    return p


def neck_seg(name, r0, r1, length, top=False, belly=True):
    """Сегмент шеи/хвоста: капсула вдоль +x, опора в начале, конец — точка «next»."""
    p = Part(name)
    over = max(r0, r1) * 0.45
    body = capsule(0.0, length + over * 0.4, r0, r1)
    p.add(body, GREEN, scales=0.06 * (r0 / 0.13) ** 0.5 + 0.02, k=0.45 + r0)
    if top:
        # вид сверху: гребень по оси, без брюха
        p.add(capsule(0.0, length + over * 0.4, r0 * 0.42, r1 * 0.42), GREEN_TOP, clip=True, no_outline=True, k=0.3)
        L = length + over * 0.4
        n = 2
        for i in range(n):
            x = L * (0.25 + 0.5 * i)
            h = (r0 + (r1 - r0) * x / L) * 0.95
            p.add([(x - h * 0.35, 0.0), (x + h * 0.55, h * 0.18), (x + h * 0.85, 0.0), (x + h * 0.55, -h * 0.18)], BRONZE,
                  outline=4, k=0.25, no_hi=True)
    else:
        p.add(capsule(0.0, length + over * 0.4, r0 * 0.9, r1 * 0.9, y=r0 * 0.35), GREEN_TOP, clip=True, no_outline=True, k=0.3)
        if belly:
            p.add(capsule(0.0, length + over * 0.4, r0 * 0.6, r1 * 0.6, y=-r0 * 0.62), GOLD, clip=True, outline=3,
                  bands=max(0.035, r0 * 0.4), k=0.3)
        L = length + over * 0.4
        x = L * 0.45
        h = (r0 + (r1 - r0) * 0.45) * 0.75
        ry = r0 + (r1 - r0) * 0.45
        p.add(spike((x + h * 0.4, ry * 0.92), (x - h * 0.4, ry * 0.92), h, lean=0.45), BRONZE, outline=4, k=0.25, no_hi=True)
    p.anchors = dict(next=(length, 0.0))
    return p


def tail_tip(top=False):
    p = Part("tail_tip_top" if top else "tail_tip")
    p.add(capsule(0.0, 0.10, 0.045, 0.035), GREEN, k=0.3)
    if top:
        spade = [(0.06, 0.0), (0.14, 0.10), (0.30, 0.07), (0.38, 0.0), (0.30, -0.07), (0.14, -0.10)]
    else:
        spade = [(0.06, 0.0), (0.12, 0.11), (0.28, 0.10), (0.38, 0.0), (0.26, -0.06), (0.12, -0.08)]
    p.add(cr(spade, n=6), BRONZE, k=0.4)
    p.lines.append(([(0.08, 0.0), (0.34, 0.0)], BRONZE["deep"] + (255,), 5, False))
    p.anchors = {}
    return p


def head_side():
    p = Part("head_side")
    # дальний рог (темнее, за черепом)
    far = cr([(0.08, 0.12), (-0.06, 0.25), (-0.24, 0.33), (-0.30, 0.34), (-0.20, 0.27), (-0.04, 0.16), (0.04, 0.08)], n=6)
    p.add([(x + 0.06, y + 0.03) for x, y in far], dict((k, tuple(int(c * 0.72) for c in v)) for k, v in HORN.items()), k=0.4)
    skull = cr([(-0.06, 0.02), (0.02, 0.15), (0.16, 0.20), (0.30, 0.16), (0.46, 0.11), (0.60, 0.08), (0.66, 0.03),
                (0.64, -0.03), (0.50, -0.04), (0.30, -0.05), (0.12, -0.07), (0.0, -0.08)], n=8)
    p.add(skull, GREEN, scales=0.05, k=0.7)
    p.add(cr([(0.0, 0.10), (0.16, 0.20), (0.30, 0.16), (0.46, 0.11), (0.60, 0.08), (0.40, 0.06), (0.16, 0.08)], n=6),
          GREEN_TOP, clip=True, no_outline=True, k=0.3)
    # надбровье и бронзовые пластины носа
    p.add(cr([(0.14, 0.15), (0.24, 0.20), (0.36, 0.17), (0.30, 0.13), (0.20, 0.12)], n=5), BRONZE, outline=4, k=0.3)
    p.add(cr([(0.42, 0.10), (0.52, 0.11), (0.62, 0.08), (0.56, 0.06), (0.46, 0.07)], n=5), BRONZE, outline=3, k=0.2, no_hi=True)
    # ближний рог
    near = cr([(0.06, 0.12), (-0.08, 0.26), (-0.26, 0.36), (-0.34, 0.37), (-0.22, 0.28), (-0.06, 0.15), (0.02, 0.06)], n=6)
    p.add(near, HORN, k=0.45)
    p.lines.append((cr([(0.0, 0.14), (-0.10, 0.24), (-0.24, 0.33)], closed=False, n=4), HORN["shade"] + (255,), 4, False))
    # гребешок-«уши» на затылке
    p.add(cr([(-0.02, 0.0), (-0.16, 0.06), (-0.22, -0.04), (-0.12, -0.08), (-0.02, -0.06)], n=5), BRONZE, k=0.3, outline=4)
    # зубы верхней челюсти
    for i in range(5):
        x = 0.22 + i * 0.085
        p.add([(x, -0.035), (x + 0.03, -0.035), (x + 0.012, -0.085)], TOOTH, outline=3, k=0.15, no_hi=True)
    # глаз
    p.add(ellipse(0.25, 0.095, 0.042, 0.03, rot=-0.15), dict(base=EYE, shade=(220, 140, 20), deep=(180, 100, 10),
                                                             light=(255, 230, 120), hi=(255, 250, 220)), outline=4, k=0.15)
    p.add(ellipse(0.257, 0.095, 0.009, 0.026), dict(base=INK, shade=INK, deep=INK, light=INK, hi=INK), no_outline=True, flat=True)
    p.add(ellipse(0.245, 0.108, 0.007, 0.006), dict(base=(255, 255, 240), shade=(255, 255, 240), deep=(255, 255, 240),
                                                    light=(255, 255, 240), hi=(255, 255, 240)), no_outline=True, flat=True)
    # ноздря
    p.lines.append(([(0.585, 0.055), (0.615, 0.065)], INK + (255,), 5, False))
    p.anchors = dict(jaw=(0.06, -0.06), mouth=(0.66, -0.06))
    return p


def mouth_side():
    """Пасть изнутри — позади челюсти и головы, видна, когда челюсть открыта (опора — шарнир челюсти)."""
    p = Part("mouth_side")
    p.add(cr([(-0.02, 0.06), (0.24, 0.07), (0.50, 0.06), (0.50, -0.02), (0.30, -0.10), (0.06, -0.08)], n=6), MOUTH, k=0.4, no_hi=True)
    p.lines.append((cr([(0.04, -0.01), (0.22, -0.03), (0.40, 0.0)], closed=False, n=4), (210, 90, 100, 255), 8, False))
    p.anchors = {}
    return p


def jaw_side():
    p = Part("jaw_side")
    # опора — шарнир челюсти; челюсть вдоль +x, чуть вниз
    for i in range(4):
        x = 0.18 + i * 0.085
        p.add([(x, 0.0), (x + 0.03, 0.0), (x + 0.014, 0.045)], TOOTH, outline=3, k=0.15, no_hi=True)
    jaw = cr([(-0.02, 0.02), (0.20, 0.01), (0.44, 0.0), (0.56, -0.01), (0.54, -0.05), (0.36, -0.08), (0.14, -0.09),
              (0.0, -0.06)], n=6)
    p.add(jaw, GREEN, scales=0.05, k=0.5)
    p.add(cr([(0.05, -0.04), (0.30, -0.06), (0.50, -0.035), (0.36, -0.08), (0.14, -0.09)], n=5), GOLD, clip=True, outline=3, k=0.2,
          bands=0.04)
    p.anchors = {}
    return p


def leg_upper(name, length, r0, r1, thigh=False):
    p = Part(name)
    if thigh:
        shape = cr([(-0.14, 0.02), (-0.06, 0.16), (0.10, 0.17), (0.24, 0.10), (length + 0.05, 0.05),
                    (length + 0.06, -0.05), (0.20, -0.10), (0.02, -0.14), (-0.12, -0.10)], n=8)
    else:
        shape = capsule(0.0, length, r0, r1)
    p.add(shape, GREEN, scales=0.055, k=0.6)
    if thigh:
        p.lines.append((cr([(0.0, 0.08), (0.14, 0.06), (0.26, 0.02)], closed=False, n=4), GREEN["deep"] + (200,), 5, False))
    p.anchors = dict(next=(length, 0.0))
    return p


def leg_lower(name, length, r0, r1):
    p = Part(name)
    p.add(capsule(0.0, length, r0, r1), GREEN, scales=0.045, k=0.4)
    p.add(capsule(0.0, length, r0 * 0.45, r1 * 0.45, y=-r0 * 0.45), GOLD, clip=True, outline=3, k=0.2, bands=0.04)
    p.anchors = dict(next=(length, 0.0))
    return p


def foot_side(name, size=1.0):
    p = Part(name)
    s = size
    claws = []
    for i, (dx, dy) in enumerate(((0.20, -0.055), (0.16, -0.06), (0.11, -0.06))):
        claws.append((dx * s, dy * s))
    # дальние пальцы (темнее) — позади
    p.add(cr([(-0.02 * s, 0.02 * s), (0.10 * s, 0.01 * s), (0.17 * s, -0.04 * s), (0.10 * s, -0.06 * s), (-0.03 * s, -0.05 * s)], n=5),
          dict((k, tuple(int(c * 0.75) for c in v)) for k, v in GREEN.items()), k=0.3, no_hi=True)
    pad = cr([(-0.06 * s, 0.04 * s), (0.04 * s, 0.05 * s), (0.14 * s, 0.01 * s), (0.20 * s, -0.035 * s), (0.18 * s, -0.06 * s),
              (0.02 * s, -0.065 * s), (-0.07 * s, -0.04 * s)], n=6)
    p.add(pad, GREEN, scales=0.04, k=0.35)
    for x, y in ((0.20, -0.04), (0.13, -0.05)):
        c = [(x * s, (y + 0.02) * s), ((x + 0.07) * s, (y - 0.015) * s), ((x + 0.02) * s, (y - 0.025) * s)]
        p.add(c, HORN, outline=3, k=0.15, no_hi=True)
    p.add([(-0.06 * s, 0.0), (-0.11 * s, -0.04 * s), (-0.05 * s, -0.03 * s)], HORN, outline=3, k=0.12, no_hi=True)
    p.anchors = dict(sole=(0.04 * s, -0.065 * s))
    return p


def wing_arm(name="wing_arm"):
    p = Part(name)
    memb = cr([(0.02, -0.01), (0.36, -0.02), (0.70, -0.02), (0.68, -0.18), (0.52, -0.32), (0.30, -0.36), (0.10, -0.30),
               (-0.02, -0.14)], n=6)
    p.add(memb, MEMB, k=0.8)
    p.lines.append((cr([(0.05, -0.04), (0.20, -0.18), (0.26, -0.33)], closed=False, n=4), MEMB["deep"] + (220,), 5, False))
    p.lines.append((cr([(0.30, -0.04), (0.42, -0.18), (0.50, -0.30)], closed=False, n=4), MEMB["deep"] + (220,), 5, False))
    p.add(capsule(0.0, 0.70, 0.055, 0.04), BRONZE, k=0.3)
    p.add(ellipse(0.70, 0.0, 0.06, 0.055), BRONZE, k=0.3, outline=4)
    p.anchors = dict(next=(0.70, 0.0))
    return p


def wing_hand(name="wing_hand"):
    p = Part(name)
    tips = [(0.92, 0.12), (0.86, -0.30), (0.62, -0.62), (0.26, -0.70)]
    # перепонка с вогнутыми краями между пальцами
    edge = [(0.0, 0.03), tips[0]]
    prev = tips[0]
    for t in tips[1:]:
        mx, my = (prev[0] + t[0]) / 2, (prev[1] + t[1]) / 2
        # прогиб к запястью
        edge.append((mx * 0.82, my * 0.82))
        edge.append(t)
        prev = t
    edge += [(0.12, -0.52), (-0.02, -0.40), (-0.04, -0.06)]
    p.add(cr(edge, n=6), MEMB, k=0.9)
    # кости пальцев (бронза) поверх
    for t in tips:
        L = math.hypot(*t)
        a = math.atan2(t[1], t[0])
        cap = capsule(0.0, L, 0.034, 0.014)
        cap = [(x * math.cos(a) - y * math.sin(a), x * math.sin(a) + y * math.cos(a)) for x, y in cap]
        p.add(cap, BRONZE, outline=4, k=0.2, no_hi=True)
    # коготь большого пальца
    p.add([(0.0, 0.03), (0.10, 0.13), (0.05, 0.02)], HORN, outline=4, k=0.2, no_hi=True)
    p.add(ellipse(0.0, 0.0, 0.055, 0.05), BRONZE, outline=4, k=0.3)
    p.anchors = {}
    return p


# ---------------------------------------------------------------- вид спереди / сзади (сверху под углом)
def body_front():
    p = Part("body_front")
    shape = cr([(0.0, 0.52), (0.30, 0.46), (0.46, 0.22), (0.50, -0.06), (0.40, -0.30), (0.20, -0.44), (0.0, -0.48),
                (-0.20, -0.44), (-0.40, -0.30), (-0.50, -0.06), (-0.46, 0.22), (-0.30, 0.46)], n=8)
    p.add(shape, GREEN, scales=0.075)
    back = cr([(0.0, 0.50), (0.26, 0.44), (0.36, 0.20), (0.30, -0.02), (0.0, -0.08), (-0.30, -0.02), (-0.36, 0.20), (-0.26, 0.44)], n=6)
    p.add(back, GREEN_TOP, clip=True, no_outline=True, scales=0.07, k=0.5)
    chest = cr([(0.0, -0.10), (0.24, -0.16), (0.30, -0.32), (0.16, -0.44), (0.0, -0.47), (-0.16, -0.44), (-0.30, -0.32),
                (-0.24, -0.16)], n=6)
    p.add(chest, GOLD, clip=True, outline=4, k=0.5)
    for y in (-0.20, -0.28, -0.36):
        p.lines.append((cr([(-0.26 + abs(y + 0.28) * 0.6, y + 0.02), (0.0, y - 0.02), (0.26 - abs(y + 0.28) * 0.6, y + 0.02)],
                          closed=False, n=4), GOLD["deep"] + (255,), 4, False))
    for i in range(5):
        y = 0.40 - i * 0.11
        h = 0.07 + 0.02 * (i < 3)
        p.add([(-0.04, y - 0.05), (0.0, y + h - 0.02), (0.04, y - 0.05), (0.0, y - 0.08)], BRONZE, outline=4, k=0.25, no_hi=True)
    p.anchors = dict(neck=(0.0, -0.36), tail=(0.0, 0.44), shoulder_r=(0.34, -0.24), shoulder_l=(-0.34, -0.24),
                     hip_r=(0.32, 0.22), hip_l=(-0.32, 0.22), wing_r=(0.20, 0.06), wing_l=(-0.20, 0.06))
    return p


def body_back():
    p = Part("body_back")
    shape = cr([(0.0, 0.50), (0.26, 0.46), (0.46, 0.26), (0.50, 0.0), (0.44, -0.26), (0.26, -0.44), (0.0, -0.50),
                (-0.26, -0.44), (-0.44, -0.26), (-0.50, 0.0), (-0.46, 0.26), (-0.26, 0.46)], n=8)
    p.add(shape, GREEN, scales=0.075)
    back = cr([(0.0, 0.48), (0.24, 0.42), (0.38, 0.12), (0.34, -0.20), (0.18, -0.36), (0.0, -0.40), (-0.18, -0.36),
               (-0.34, -0.20), (-0.38, 0.12), (-0.24, 0.42)], n=6)
    p.add(back, GREEN_TOP, clip=True, no_outline=True, scales=0.07, k=0.5)
    for i in range(7):
        y = 0.42 - i * 0.12
        h = 0.08
        p.add([(-0.04, y - 0.04), (0.0, y + h - 0.02), (0.04, y - 0.04), (0.0, y - 0.08)], BRONZE, outline=4, k=0.25, no_hi=True)
    p.anchors = dict(neck=(0.0, 0.40), tail=(0.0, -0.42), shoulder_r=(0.34, 0.24), shoulder_l=(-0.34, 0.24),
                     hip_r=(0.34, -0.20), hip_l=(-0.34, -0.20), wing_r=(0.20, 0.16), wing_l=(-0.20, 0.16))
    return p


def head_front():
    p = Part("head_front")
    for sgn in (1, -1):
        horn = cr([(0.06, -0.04), (0.16, 0.06), (0.26, 0.20), (0.32, 0.32), (0.34, 0.36), (0.30, 0.26), (0.24, 0.12), (0.16, -0.02)], n=6)
        p.add([(x * sgn, y) for x, y in horn], HORN, k=0.4)
    for sgn in (1, -1):
        fr = cr([(0.14, -0.10), (0.30, -0.08), (0.34, -0.16), (0.22, -0.20)], n=5)
        p.add([(x * sgn, y) for x, y in fr], BRONZE, k=0.3, outline=4)
    head = cr([(0.0, 0.04), (0.16, 0.0), (0.22, -0.12), (0.18, -0.26), (0.12, -0.40), (0.06, -0.47), (0.0, -0.48),
               (-0.06, -0.47), (-0.12, -0.40), (-0.18, -0.26), (-0.22, -0.12), (-0.16, 0.0)], n=8)
    p.add(head, GREEN, scales=0.05, k=0.7)
    p.add(cr([(0.0, 0.02), (0.12, -0.02), (0.10, -0.24), (0.05, -0.40), (0.0, -0.42), (-0.05, -0.40), (-0.10, -0.24),
              (-0.12, -0.02)], n=6), GREEN_TOP, clip=True, no_outline=True, k=0.3)
    p.add(cr([(0.0, -0.20), (0.05, -0.28), (0.04, -0.40), (0.0, -0.42), (-0.04, -0.40), (-0.05, -0.28)], n=5), BRONZE, outline=4, k=0.25)
    for sgn in (1, -1):
        p.add(ellipse(0.13 * sgn, -0.16, 0.040, 0.030, rot=0.5 * sgn), dict(base=EYE, shade=(220, 140, 20), deep=(180, 100, 10),
                                                                            light=(255, 230, 120), hi=(255, 250, 220)), outline=4, k=0.15)
        p.add(ellipse(0.13 * sgn, -0.16, 0.009, 0.024), dict(base=INK, shade=INK, deep=INK, light=INK, hi=INK), no_outline=True, flat=True)
        p.add(cr([(0.08 * sgn, -0.10), (0.20 * sgn, -0.12), (0.17 * sgn, -0.135)], n=3), BRONZE, outline=3, k=0.2, no_hi=True)
        p.lines.append(([(0.03 * sgn, -0.445), (0.05 * sgn, -0.43)], INK + (255,), 6, False))
    for x in (-0.07, -0.025, 0.025, 0.07):
        p.add([(x - 0.012, -0.46 + abs(x) * 0.4), (x + 0.012, -0.46 + abs(x) * 0.4), (x, -0.50 + abs(x) * 0.4)], TOOTH, outline=3,
              k=0.1, no_hi=True)
    p.anchors = dict(jaw=(0.0, -0.40), mouth=(0.0, -0.50))
    return p


def mouth_front():
    p = Part("mouth_front")
    p.add(cr([(-0.11, 0.02), (0.11, 0.02), (0.09, -0.10), (0.0, -0.13), (-0.09, -0.10)], n=5), MOUTH, k=0.4, no_hi=True)
    p.anchors = {}
    return p


def jaw_front():
    p = Part("jaw_front")
    for x in (-0.06, 0.0, 0.06):
        p.add([(x - 0.012, -0.05), (x + 0.012, -0.05), (x, -0.015)], TOOTH, outline=3, k=0.1, no_hi=True)
    jaw = cr([(-0.12, -0.03), (0.12, -0.03), (0.10, -0.10), (0.04, -0.14), (-0.04, -0.14), (-0.10, -0.10)], n=5)
    p.add(jaw, GOLD, k=0.4, bands=0.035)
    p.anchors = {}
    return p


def head_back():
    p = Part("head_back")
    # морда торчит за черепом (дальше от зрителя — выше)
    p.add(cr([(-0.08, 0.20), (0.08, 0.20), (0.06, 0.36), (0.0, 0.40), (-0.06, 0.36)], n=5), GREEN, k=0.4, scales=0.04)
    for sgn in (1, -1):
        horn = cr([(0.08, 0.12), (0.20, 0.08), (0.30, -0.04), (0.34, -0.12), (0.26, -0.04), (0.16, 0.02), (0.06, 0.04)], n=6)
        p.add([(x * sgn, y) for x, y in horn], HORN, k=0.4)
    skull = cr([(0.0, 0.26), (0.16, 0.22), (0.20, 0.08), (0.14, -0.04), (0.0, -0.06), (-0.14, -0.04), (-0.20, 0.08),
                (-0.16, 0.22)], n=8)
    p.add(skull, GREEN, scales=0.05, k=0.6)
    p.add(cr([(0.0, 0.24), (0.12, 0.20), (0.14, 0.08), (0.0, 0.02), (-0.14, 0.08), (-0.12, 0.20)], n=6), GREEN_TOP, clip=True,
          no_outline=True, k=0.3)
    for sgn in (1, -1):
        fr = cr([(0.12, 0.02), (0.26, -0.06), (0.24, -0.14), (0.10, -0.06)], n=5)
        p.add([(x * sgn, y) for x, y in fr], BRONZE, k=0.3, outline=4)
    for y in (0.16, 0.06):
        p.add([(-0.03, y - 0.03), (0.0, y + 0.05), (0.03, y - 0.03), (0.0, y - 0.06)], BRONZE, outline=3, k=0.2, no_hi=True)
    p.anchors = dict(jaw=(0.0, 0.3), mouth=(0.0, 0.40))
    return p


def leg_top(name, length, r0, r1):
    p = Part(name)
    p.add(capsule(0.0, length, r0, r1), GREEN, scales=0.05, k=0.5, light=(-1.0, 0.3))
    p.anchors = dict(next=(length, 0.0))
    return p


def foot_front(name, s=1.0):
    p = Part(name)
    pad = cr([(-0.09 * s, 0.03 * s), (0.09 * s, 0.03 * s), (0.11 * s, -0.04 * s), (0.06 * s, -0.08 * s), (-0.06 * s, -0.08 * s),
              (-0.11 * s, -0.04 * s)], n=5)
    p.add(pad, GREEN, scales=0.04, k=0.3)
    for x in (-0.06, 0.0, 0.06):
        p.add([((x - 0.02) * s, -0.07 * s), ((x + 0.02) * s, -0.07 * s), (x * s, -0.12 * s)], HORN, outline=3, k=0.12, no_hi=True)
    p.anchors = dict(sole=(0.0, -0.08 * s))
    return p


# ---------------------------------------------------------------- сборка
def build_parts():
    P = [body_side(), head_side(), mouth_side(), jaw_side(), mouth_front(), tail_tip(), tail_tip(top=True),
         leg_upper("fore_upper", 0.26, 0.10, 0.075), leg_lower("fore_lower", 0.26, 0.07, 0.055), foot_side("fore_foot", 1.0),
         leg_upper("hind_upper", 0.26, 0.12, 0.08, thigh=True), leg_lower("hind_lower", 0.28, 0.07, 0.05), foot_side("hind_foot", 1.1),
         wing_arm(), wing_hand(),
         body_front(), body_back(), head_front(), jaw_front(), head_back(),
         leg_top("leg_top_upper", 0.24, 0.10, 0.075), leg_top("leg_top_lower", 0.24, 0.07, 0.055), foot_front("foot_front")]
    # шея: 4 сегмента 0,2 т, хвост: 7 сегментов, сужаются
    for i in range(4):
        r0 = 0.15 - i * 0.02
        P.append(neck_seg(f"neck_{i}", r0, r0 - 0.02, 0.20))
        P.append(neck_seg(f"neck_top_{i}", r0, r0 - 0.02, 0.20, top=True))
    for i in range(7):
        r0 = 0.15 - i * 0.017
        P.append(neck_seg(f"tail_{i}", r0, r0 - 0.017, 0.21, belly=i < 5))
        P.append(neck_seg(f"tail_top_{i}", r0, r0 - 0.017, 0.21, top=True))
    return P


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--painted", action="store_true")
    a = ap.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    BUILD.mkdir(parents=True, exist_ok=True)
    rig = {"ppt": PPT, "far": FAR, "parts": []}
    sheet_items = []
    for part in build_parts():
        img, pivot = render(part)
        # пурпурный уголок заглушки (CLAUDE.md): 3×3 px в правом верхнем углу — на теле не виден
        px = img.load()
        for yy in range(3):
            for xx in range(img.width - 3, img.width):
                px[xx, yy] = (255, 0, 255, 255)
        fn = f"TEMP_dragon_{part.name}.png"
        img.save(OUT / fn)
        rig["parts"].append({"name": part.name, "file": fn[:-4], "pivotX": round(pivot[0], 5), "pivotY": round(pivot[1], 5),
                             "anchors": [{"name": k, "x": round(v[0], 4), "y": round(v[1], 4)} for k, v in part.anchors.items()]})
        sheet_items.append((part.name, img))
    (OUT / "TEMP_dragon_rig.json").write_text(json.dumps(rig, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n")
    # лист частей для просмотра
    W = 1800
    x = y = 10
    rowh = 0
    pos = []
    for n, im in sheet_items:
        if x + im.width + 10 > W:
            x = 10
            y += rowh + 10
            rowh = 0
        pos.append((x, y, im))
        x += im.width + 10
        rowh = max(rowh, im.height)
    sheet = Image.new("RGBA", (W, y + rowh + 10), (196, 214, 170, 255))
    for x, y, im in pos:
        sheet.alpha_composite(im, (x, y))
    sheet.convert("RGB").save(BUILD / "parts_sheet.png")
    print(f"частей {len(sheet_items)} → {OUT}; лист {BUILD / 'parts_sheet.png'}")


if __name__ == "__main__":
    main()
