"""D-004: быстрый просмотр сборки дракона из частей без Unity (нейтральные позы трёх видов) — для доводки рисунка.

    python tools/art_d004_preview.py   → build/art_d004/preview.png

Та же схема, что клиент (DragonRig.cs): части — с опорой в суставе, кости вдоль +x, углы в градусах (экран: x вправо,
y вверх), точки крепления из TEMP_dragon_rig.json. Позы здесь — только нейтральные, анимация — в клиенте.
"""
from __future__ import annotations

import json
import math
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "client" / "Assets" / "Art" / "_TEMP" / "Resources" / "D004"
OUT = ROOT / "build" / "art_d004"

RIG = json.loads((SRC / "TEMP_dragon_rig.json").read_text(encoding="utf-8"))
PPT = RIG["ppt"]
PARTS = {p["name"]: p for p in RIG["parts"]}
IMG = {n: Image.open(SRC / f"{p['file']}.png").convert("RGBA") for n, p in PARTS.items()}


def anchor(part, name):
    for a in PARTS[part]["anchors"]:
        if a["name"] == name:
            return a["x"], a["y"]
    raise KeyError(f"{part}.{name}")


class Canvas:
    def __init__(self, w_t, h_t, origin):
        self.img = Image.new("RGBA", (int(w_t * PPT), int(h_t * PPT)), (150, 190, 110, 255))
        self.ox, self.oy = origin
        self.items = []

    def to_px(self, x, y):
        return (self.ox + x) * PPT, self.img.height - (self.oy + y) * PPT

    def put(self, part, pos, ang, order, tint=1.0, flip=False, sx=1.0, sy=1.0):
        self.items.append((order, part, pos, ang, tint, flip, sx, sy))

    def draw(self):
        for order, part, pos, ang, tint, flip, sx, sy in sorted(self.items, key=lambda t: t[0]):
            im = IMG[part]
            p = PARTS[part]
            px, py = p["pivotX"] * im.width, (1 - p["pivotY"]) * im.height
            if sx != 1 or sy != 1:
                im = im.resize((max(1, int(im.width * abs(sx))), max(1, int(im.height * abs(sy)))), Image.LANCZOS)
                px, py = px * abs(sx), py * abs(sy)
            if flip:
                im = im.transpose(Image.FLIP_TOP_BOTTOM)
                py = im.height - py
            if tint != 1.0:
                r, g, b, a = im.split()
                r, g, b = (c.point(lambda v: int(v * tint)) for c in (r, g, b))
                im = Image.merge("RGBA", (r, g, b, a))
            # опору — в центр, поворот вокруг центра
            W = int(2 * max(px, im.width - px)) + 2
            H = int(2 * max(py, im.height - py)) + 2
            pad = Image.new("RGBA", (W, H), (0, 0, 0, 0))
            pad.paste(im, (int(W / 2 - px), int(H / 2 - py)))
            rot = pad.rotate(ang, resample=Image.BICUBIC, expand=True)
            cx, cy = self.to_px(*pos)
            self.img.alpha_composite(rot, (int(cx - rot.width / 2), int(cy - rot.height / 2)))


def rot(v, a):
    c, s = math.cos(math.radians(a)), math.sin(math.radians(a))
    return v[0] * c - v[1] * s, v[0] * s + v[1] * c


def add(a, b):
    return a[0] + b[0], a[1] + b[1]


def chain(cv, parts, start, angles, order0, step=1, tint=1.0, flip=False, top_order=None):
    p = start
    for i, (part, a) in enumerate(zip(parts, angles)):
        cv.put(part, p, a, order0 + i * step, tint, flip)
        p = add(p, rot(anchor(part, "next"), a))
    return p


def ik2(hip, foot, l1, l2, bend):
    dx, dy = foot[0] - hip[0], foot[1] - hip[1]
    d = min(math.hypot(dx, dy), l1 + l2 - 1e-4)
    a = math.atan2(dy, dx)
    c = (l1 * l1 + d * d - l2 * l2) / (2 * l1 * d)
    a1 = a + bend * math.acos(max(-1, min(1, c)))
    knee = (hip[0] + l1 * math.cos(a1), hip[1] + l1 * math.sin(a1))
    a2 = math.atan2(foot[1] - knee[1], foot[0] - knee[0])
    return math.degrees(a1), math.degrees(a2)


def leg(cv, hip, foot_target, up, lo, ft, order, tint, bend, flip=False):
    l1, l2 = anchor(up, "next")[0], anchor(lo, "next")[0]
    sole = anchor(ft, "sole")
    ankle = (foot_target[0] - sole[0], foot_target[1] - sole[1])
    a1, a2 = ik2(hip, ankle, l1, l2, bend)
    cv.put(up, hip, a1, order + 1, tint, flip)
    knee = add(hip, rot((l1, 0), a1))
    cv.put(lo, knee, a2, order, tint, flip)
    cv.put(ft, ankle, 0, order + 2, tint, flip)


def side(cv, at):
    bx, by = at
    body = (bx, by + 0.70)
    A = lambda n: add(body, anchor("body_side", n))
    cv.put("wing_arm", A("wing_far"), 116, 0, RIG["far"], flip=True)
    wrist = add(A("wing_far"), rot(anchor("wing_arm", "next"), 116))
    cv.put("wing_hand", wrist, 84, 1, RIG["far"], flip=True)
    leg(cv, A("shoulder_far"), (bx + 0.56, by + 0.14), "fore_upper", "fore_lower", "fore_foot", 2, RIG["far"], -1)
    leg(cv, A("hip_far"), (bx - 0.34, by + 0.14), "hind_upper", "hind_lower", "hind_foot", 5, RIG["far"], 1)
    tail = [f"tail_{i}" for i in range(7)]
    end = chain(cv, tail, A("tail"), [188, 185, 180, 176, 172, 170, 168], 20, -1)
    cv.put("tail_tip", end, 168, 12)
    cv.put("body_side", body, 0, 30)
    neck = [f"neck_{i}" for i in range(4)]
    end = chain(cv, neck, A("neck"), [52, 44, 34, 22], 31)
    jaw = add(end, rot(anchor("head_side", "jaw"), -8))
    cv.put("mouth_side", jaw, -8, 38)
    cv.put("jaw_side", jaw, -8, 39)
    cv.put("head_side", end, -8, 40)
    leg(cv, A("shoulder_near"), (bx + 0.50, by), "fore_upper", "fore_lower", "fore_foot", 50, 1.0, -1)
    leg(cv, A("hip_near"), (bx - 0.40, by), "hind_upper", "hind_lower", "hind_foot", 53, 1.0, 1)
    cv.put("wing_arm", A("wing_near"), 124, 60, flip=True)
    wrist = add(A("wing_near"), rot(anchor("wing_arm", "next"), 124))
    cv.put("wing_hand", wrist, 92, 61, flip=True)


def front(cv, at):
    bx, by = at
    body = (bx, by + 0.62)
    A = lambda n: add(body, anchor("body_front", n))
    tail = [f"tail_top_{i}" for i in range(7)]
    end = chain(cv, tail, A("tail"), [96, 100, 104, 100, 92, 84, 80], 0)
    cv.put("tail_tip_top", end, 80, 8)
    for sgn, wn in ((1, "wing_r"), (-1, "wing_l")):
        a = 20 if sgn > 0 else 160
        cv.put("wing_arm", A(wn), a, 10, flip=sgn < 0)
        wrist = add(A(wn), rot(anchor("wing_arm", "next"), a))
        cv.put("wing_hand", wrist, a - 30 * sgn, 11, flip=sgn < 0)
    for sgn, hn in ((1, "hip_r"), (-1, "hip_l")):
        leg(cv, A(hn), (bx + 0.36 * sgn, by + 0.50), "leg_top_upper", "leg_top_lower", "foot_front", 14, 0.85, sgn)
    cv.put("body_front", body, 0, 20)
    for sgn, sn in ((1, "shoulder_r"), (-1, "shoulder_l")):
        leg(cv, A(sn), (bx + 0.36 * sgn, by - 0.02), "leg_top_upper", "leg_top_lower", "foot_front", 22, 1.0, sgn)
    neck = [f"neck_top_{i}" for i in range(2)]
    end = chain(cv, neck, A("neck"), [-80, -90], 30, 1)
    end = add(end, (0, 0.18))  # голова поднята над шеей (высота)
    cv.put("mouth_front", add(end, anchor("head_front", "jaw")), 0, 38)
    cv.put("jaw_front", add(end, anchor("head_front", "jaw")), 0, 39)
    cv.put("head_front", end, 0, 40)


def back(cv, at):
    bx, by = at
    body = (bx, by + 0.62)
    A = lambda n: add(body, anchor("body_back", n))
    neck = [f"neck_top_{i}" for i in range(4)]
    end = chain(cv, neck, A("neck"), [90, 88, 86, 84], 0, -1)
    cv.put("head_back", add(end, (0, 0.10)), 0, 2)
    for sgn, sn in ((1, "shoulder_r"), (-1, "shoulder_l")):
        leg(cv, A(sn), (bx + 0.36 * sgn, by + 0.50), "leg_top_upper", "leg_top_lower", "foot_front", 6, 0.8, sgn)
    cv.put("body_back", body, 0, 20)
    for sgn, wn in ((1, "wing_r"), (-1, "wing_l")):
        a = 20 if sgn > 0 else 160
        cv.put("wing_arm", A(wn), a, 24, flip=sgn < 0)
        wrist = add(A(wn), rot(anchor("wing_arm", "next"), a))
        cv.put("wing_hand", wrist, a - 30 * sgn, 25, flip=sgn < 0)
    for sgn, hn in ((1, "hip_r"), (-1, "hip_l")):
        leg(cv, A(hn), (bx + 0.36 * sgn, by - 0.16), "leg_top_upper", "leg_top_lower", "foot_front", 26, 0.9, sgn)
    tail = [f"tail_top_{i}" for i in range(7)]
    end = chain(cv, tail, A("tail"), [-80, -60, -40, -20, -5, 10, 25], 30)
    cv.put("tail_tip_top", end, 25, 40)


def main():
    cv = Canvas(9.0, 3.2, (0.0, 0.0))
    side(cv, (1.9, 0.5))
    front(cv, (4.9, 0.9))
    back(cv, (7.4, 0.9))
    cv.draw()
    d = ImageDraw.Draw(cv.img)
    for gx in range(10):
        d.line([cv.to_px(gx, 0), cv.to_px(gx, 3.2)], fill=(120, 160, 90, 255))
    OUT.mkdir(parents=True, exist_ok=True)
    cv.img.convert("RGB").save(OUT / "preview.png")
    print(OUT / "preview.png")


if __name__ == "__main__":
    main()
