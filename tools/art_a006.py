"""Ночная пачка арта A-006 (16→17.11): эскизы-силуэты, заказ на Мак (ComfyUI напрямую), приём и листы.

    python tools/art_a006.py sketches                 # нарисовать эскизы п. 1 в build/art_a006/sketch (и показать)
    python tools/art_a006.py list                     # задания пачки по порядку
    python tools/art_a006.py queue [ключ …]           # поставить пачку (или выбранные) в конец очереди ComfyUI
    python tools/art_a006.py collect                  # скачать готовое в build/art_a006/raw/<ключ>.png
    python tools/art_a006.py wait [--every 600]       # фоновый сборщик: collect раз в N секунд, пока очередь не пуста
    python tools/art_a006.py table                    # таблица заданий (markdown) для карточки
    python tools/art_a006.py sheets                   # листы work/screens/A-006_*.png, значки 128 и концепты 512 в work/art/
    python tools/art_a006.py install предмет=ключ …   # поставить выбранные значки п. 1 в игру (TEMP_, уголок, манифест)

Модель — Z-Image base (z_image_bf16), 30 шагов, cfg 4, граф как у tools/art_stage1.py. Значки п. 1 — img2img
по эскизу (LoadImage → VAEEncode → KSampler с denoise < 1): контролнета под Z-Image на Маке нет (ModelPatchLoader
пуст), поэтому форму задаёт эскиз, а denoise — насколько модель может от него отойти. Эскиз — плоские пятна цвета
с тёмным контуром и двумя тонами светотени на зелёном фоне, 768 px. Сырые картинки и учёт — вне git (build/).
"""
from __future__ import annotations

import argparse
import json
import math
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

from PIL import Image, ImageDraw

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
BUILD = ROOT / "build" / "art_a006"
RAW = BUILD / "raw"
SKETCH = BUILD / "sketch"
COMFY = "http://192.168.88.252:8188"
ICON_PX, CONCEPT_PX = 768, 1024

STYLE_B = ("highly detailed game inventory icon, almost photographic precise rendering, precise accurate shapes with true "
           "realistic proportions like Factorio item icons, instantly readable silhouette, clear readable material texture, "
           "rich saturated vivid fantasy colors like Terraria, realistic volume and soft shading, one key light from the upper "
           "left, a thin crisp dark brown outline of even weight around the outer silhouette only, clean digital painting")
CAM_ICON = ("three-quarter view from above like a Factorio item icon, a single object centered in the frame, the whole object "
            "fully visible with empty margin around it, the object fills about two thirds of the frame, isolated on a plain "
            "solid flat pure bright green chroma key background, uniform background without gradient, no cast shadow, no "
            "ground, no floor, no table, no reflection, no text, no letters, no watermark, no border, no frame")
CAM_ICON_WHITE = CAM_ICON.replace("pure bright green chroma key", "pure white")
NEG_ICON = ("cast shadow, drop shadow, floor, ground, table surface, scenery, several objects, duplicate, text, letters, "
            "watermark, frame, border, cropped, blurry, cartoon, chibi, toy, low detail, background gradient, vignette, "
            "green tint on the object, green reflections, thick outlines, bold lines, cel shading, flat colors, "
            "round badge, medallion, circle background, disc, plate, emblem, coin, app icon background, rounded square tile")
NEG_ICON_WHITE = NEG_ICON.replace("green tint on the object, green reflections, ", "")

STYLE_CHAR = ("detailed 2D fantasy game character concept art for a top-down action game sprite, seen from above at a "
              "three-quarter angle like Factorio and RimWorld, the camera tilted about 45 degrees down so we see the top of "
              "the head and shoulders and the front of the body, precise accurate shapes and realistic proportions, "
              "instantly readable silhouette, rich saturated vivid fantasy colors like Terraria, one key light from the upper "
              "left, a thin crisp dark brown outline around the silhouette, clean digital painting, ancient Greek "
              "Mediterranean myth setting")
CAM_CHAR = ("a single full-body figure centered in the frame, the whole body visible with empty margin around it, isolated "
            "on a plain flat light neutral grey background, uniform background without gradient, only a small soft contact "
            "shadow under it, no scenery, no text, no letters, no watermark, no border")
NEG_CHAR = ("several characters, crowd, duplicate, multiple views, character sheet, turnaround, text, letters, watermark, "
            "frame, border, cropped, scenery, landscape, buildings, blurry, chibi, cartoon, anime, photo, 3d render, "
            "straight side view, flat front view, low detail, background gradient, vignette")

# ---------------------------------------------------------------- п. 1: перезаказ по эскизу
P1 = {
    "hammer": ("a primitive stone sledgehammer lying diagonally, one long straight light brown wooden handle from the lower "
               "left to the upper right, at its upper right end a rectangular block head of grey stone with two flat ends "
               "set across the handle and lashed to it with crossed leather cord, head and handle are one connected object, "
               "the whole hammer fully visible including the end of the handle",
               "pickaxe, spike, axe blade, claw, nails, floating head, cropped handle"),
    "wood_wall": ("a single straight section of a thin wooden house wall seen from above at a three-quarter angle, a long low "
                  "wall panel of horizontal sawn brown planks between two squared timber corner posts, the narrow top edge "
                  "shows the wall is thin, about twice as long as it is tall, standing upright, not a box, not a crate",
                  "box, crate, chest, cube, log pile, firewood stack, round log ends, roof, window, door, house"),
    "platform": ("a square wooden deck platform tile seen from high above, parallel brown wooden planks with thin dark gaps "
                 "between them laid on two cross beams, only short stubs of support posts visible at the front corners, "
                 "a flat dock floor piece, not a table, not a bench",
                 "table, bench, chair, legs, stairs, steps, stone base, water, railing, house, boat"),
    "door": ("a single closed wooden door set in a thick wooden door frame, two upright timber jambs and a heavy lintel beam "
             "on top around a door leaf of vertical brown planks, two black iron strap hinges and a round iron ring handle, "
             "a grey stone threshold at the bottom, the frame clearly visible all around the door, front three-quarter view",
             "wall, house, building, open door, window, steps, frameless door"),
    "arrows": ("a small bundle of exactly three wooden arrows lying diagonally side by side and parallel, straight light "
               "wooden shafts, sharp grey knapped stone arrowheads at the upper right, red and white feather fletching at "
               "the lower left, three arrowheads and three fletchings clearly visible",
               "bow, quiver, target, two arrows, four arrows, crossed arrows"),
}
DENOISE = (0.62, 0.72, 0.80, 0.88)     # 4 варианта по эскизу: от «держит эскиз» до «свободнее»


# ---------------------------------------------------------------- эскизы (PIL)
OUT_C = (58, 34, 20)
GREEN = (40, 220, 40)


def rrect(c, u, half_len, half_w):
    """Углы прямоугольника: центр c, направление длины u (единичное), полудлина, полуширина."""
    v = (-u[1], u[0])
    return [(c[0] + u[0] * a * half_len + v[0] * b * half_w, c[1] + u[1] * a * half_len + v[1] * b * half_w)
            for a, b in ((-1, -1), (1, -1), (1, 1), (-1, 1))]


def poly(d, pts, fill, w=7):
    d.polygon(pts, fill=fill)
    d.line(pts + [pts[0]], fill=OUT_C, width=w, joint="curve")


def unit(a, b):
    dx, dy = b[0] - a[0], b[1] - a[1]
    n = math.hypot(dx, dy)
    return dx / n, dy / n


def sk_hammer(d):
    a, b = (230, 820), (720, 330)
    u = unit(a, b)
    v = (-u[1], u[0])
    mid = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
    poly(d, rrect(mid, u, 360, 24), (196, 146, 86))
    d.line([(a[0] + v[0] * 10, a[1] + v[1] * 10), (b[0] + v[0] * 10, b[1] + v[1] * 10)], fill=(150, 100, 55), width=12)
    for t in (0.08, 0.14):   # обмотка у конца рукояти
        p = (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)
        poly(d, rrect(p, u, 14, 30), (120, 72, 40), 5)
    head_c = (b[0] - u[0] * 20, b[1] - u[1] * 20)
    top = rrect((head_c[0] - 18, head_c[1] - 22), v, 190, 92)      # верхняя грань (светлее), смещена вверх-влево
    poly(d, top, (196, 198, 204))
    poly(d, rrect(head_c, v, 190, 92), (150, 152, 162))
    poly(d, rrect((head_c[0] - u[0] * 30, head_c[1] - u[1] * 30), v, 186, 40), (172, 174, 184), 4)
    for s in (-1, 1):        # кожаная обвязка крест-накрест
        p = (head_c[0] + v[0] * 70 * s, head_c[1] + v[1] * 70 * s)
        poly(d, rrect(p, u, 100, 14), (130, 80, 44), 5)
    poly(d, rrect(head_c, u, 104, 30), (130, 80, 44), 5)


def sk_wood_wall(d):
    # длинный низкий кусок стены (≈ 3 : 1) на каменном цоколе, тонкая верхняя кромка, высокие угловые столбы
    x0, x1, y0, y1, th = 130, 890, 470, 700, 34
    poly(d, [(x0 - 10, y1), (x1 + 10, y1), (x1 + 10, y1 + 60), (x0 - 10, y1 + 60)], (150, 148, 140))   # цоколь
    for xx in range(x0 + 60, x1, 110):
        d.line([(xx, y1 + 4), (xx, y1 + 56)], fill=OUT_C, width=4)
    poly(d, [(x0, y0), (x1, y0), (x1 + 24, y0 - th), (x0 + 24, y0 - th)], (214, 162, 98))   # тонкая верхняя кромка
    poly(d, [(x0, y0), (x1, y0), (x1, y1), (x0, y1)], (160, 104, 56))
    n = 4
    for i in range(n):
        y = y0 + (y1 - y0) * i / n
        d.rectangle((x0 + 4, y + 4, x1 - 4, y + (y1 - y0) / n - 6), fill=(176 - 14 * (i % 2), 116 - 10 * (i % 2), 62))
        d.line([(x0, y), (x1, y)], fill=OUT_C, width=5)
    for px in (x0 - 20, (x0 + x1) // 2 - 22, x1 - 26):     # столбы выше стены: по краям и посередине
        poly(d, [(px + 24, y0 - th - 110), (px + 70, y0 - th - 110), (px + 46, y0 - 120), (px, y0 - 120)], (206, 150, 86))
        poly(d, [(px, y0 - 120), (px + 46, y0 - 120), (px + 46, y1 + 4), (px, y1 + 4)], (126, 80, 42))


def sk_platform(d):
    # настил сверху с наклоном: трапеция (дальний край уже), доски поперёк с зелёными просветами, торец и короткие сваи
    yt, yb = 260, 690
    xl_t, xr_t, xl_b, xr_b = 280, 744, 190, 834
    for px in (215, 470, 775):
        poly(d, [(px, yb + 20), (px + 34, yb + 20), (px + 34, yb + 110), (px, yb + 110)], (110, 70, 38))
    poly(d, [(xl_b, yb), (xr_b, yb), (xr_b, yb + 40), (xl_b, yb + 40)], (128, 82, 44))   # торец настила
    n = 7
    for i in range(n):
        t0, t1 = i / n, (i + 1) / n
        ya, yb2 = yt + (yb - yt) * t0 + (6 if i else 0), yt + (yb - yt) * t1 - 6
        la = xl_t + (xl_b - xl_t) * t0; ra = xr_t + (xr_b - xr_t) * t0
        lb = xl_t + (xl_b - xl_t) * t1; rb = xr_t + (xr_b - xr_t) * t1
        poly(d, [(la, ya), (ra, ya), (rb, yb2), (lb, yb2)], (204 - 12 * (i % 3), 146 - 8 * (i % 3), 82), 6)
        for fx in (0.12, 0.88):
            cx = la + (ra - la) * fx; cy = (ya + yb2) / 2
            d.ellipse((cx - 6, cy - 5, cx + 6, cy + 5), fill=(70, 60, 55))


def sk_door(d):
    poly(d, [(270, 860), (754, 860), (774, 900), (250, 900)], (170, 168, 160))              # порог
    poly(d, [(290, 150), (734, 150), (760, 120), (316, 120)], (190, 134, 76))               # верх притолоки
    poly(d, [(290, 150), (734, 150), (734, 230), (290, 230)], (122, 78, 40))                 # притолока
    for xa in (300, 664):
        poly(d, [(xa, 230), (xa + 60, 230), (xa + 60, 860), (xa, 860)], (128, 82, 44))       # косяки
    poly(d, [(734, 150), (760, 120), (760, 830), (734, 860)], (96, 60, 30))                  # боковая грань рамы
    x0, x1 = 360, 664
    w = (x1 - x0) / 4
    for i in range(4):
        poly(d, [(x0 + i * w, 230), (x0 + (i + 1) * w, 230), (x0 + (i + 1) * w, 860), (x0 + i * w, 860)],
             (176 - 10 * (i % 2), 116 - 6 * (i % 2), 60), 5)
    for y in (330, 720):                                                                     # петли
        poly(d, [(x0 - 6, y), (x0 + 200, y), (x0 + 200, y + 26), (x0 - 6, y + 26)], (40, 38, 40), 4)
    d.ellipse((598, 520, 646, 568), outline=(40, 38, 40), width=10)                          # кольцо-ручка


def arrow(d, a, b, head=(150, 150, 160), head_hi=(196, 196, 206)):
    u = unit(a, b)
    v = (-u[1], u[0])
    mid = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
    L = math.dist(a, b) / 2
    poly(d, rrect(mid, u, L, 9), (214, 176, 116), 5)
    tip = (b[0] + u[0] * 70, b[1] + u[1] * 70)
    poly(d, [tip, (b[0] + v[0] * 30, b[1] + v[1] * 30), (b[0] - u[0] * 10, b[1] - u[1] * 10),
             (b[0] - v[0] * 30, b[1] - v[1] * 30)], head)
    poly(d, [tip, (b[0] + v[0] * 30, b[1] + v[1] * 30), (b[0] - u[0] * 10, b[1] - u[1] * 10)], head_hi, 4)
    for s, col in ((1, (200, 40, 36)), (-1, (240, 236, 226))):
        p0 = (a[0] + u[0] * 10, a[1] + u[1] * 10)
        p1 = (a[0] + u[0] * 120, a[1] + u[1] * 120)
        poly(d, [p0, p1, (p1[0] + v[0] * 14 * s, p1[1] + v[1] * 14 * s),
                 (p0[0] + v[0] * 40 * s - u[0] * 20, p0[1] + v[1] * 40 * s - u[1] * 20)], col, 5)


def sk_arrows(d, copper=False):
    head = ((214, 110, 48), (250, 170, 100)) if copper else ((150, 150, 160), (196, 196, 206))
    for off in (-70, 0, 70):
        arrow(d, (230 + off, 790 + off), (690 + off, 330 + off), *head)


SKETCHES = {"hammer": sk_hammer, "wood_wall": sk_wood_wall, "platform": sk_platform, "door": sk_door,
            "arrows": sk_arrows, "copper_arrows": lambda d: sk_arrows(d, True)}


def make_sketches():
    SKETCH.mkdir(parents=True, exist_ok=True)
    out = []
    for k, fn in SKETCHES.items():
        im = Image.new("RGB", (1024, 1024), GREEN)
        fn(ImageDraw.Draw(im))
        p = SKETCH / f"sk_{k}.png"
        im.resize((ICON_PX, ICON_PX), Image.LANCZOS).save(p)
        out.append(p)
    sheet = Image.new("RGB", (256 * len(out), 256), (255, 255, 255))
    for i, p in enumerate(out):
        sheet.paste(Image.open(p).resize((256, 256)), (i * 256, 0))
    sheet.save(SKETCH / "_sketches.png")
    return out


# ---------------------------------------------------------------- пачка
def jobs():
    J = []
    seed = 6001
    # 1. п. 1 — 4 варианта по эскизу + 1 строгим промптом без эскиза
    for k, (subj, neg) in P1.items():
        for i, dn in enumerate(DENOISE):
            J.append(dict(key=f"p1_{k}_{i + 1}", group="1", subject=k, prompt=f"{STYLE_B}. {subj}. {CAM_ICON}",
                          neg=f"{NEG_ICON}, {neg}", w=ICON_PX, h=ICON_PX, seed=seed, sketch=f"sk_{k}", denoise=dn))
            seed += 1
        J.append(dict(key=f"p1_{k}_t", group="1", subject=k,
                      prompt=f"{STYLE_B}. {subj}, a single object, no medallion, no round badge background. {CAM_ICON}",
                      neg=f"{NEG_ICON}, {neg}", w=ICON_PX, h=ICON_PX, seed=seed))
        seed += 1
    # 3. враги этапа 3 — по 3 варианта
    enemies = {
        "swordsman": [
            "a Greek bandit swordsman, a lean rough man with a short dark beard, a faded red cloth hood, a padded linen tunic "
            "under a brown leather cuirass, leather bracers, sandals with leg wraps, a short leaf-shaped bronze sword in the "
            "right hand and a small round wooden shield on the left arm, menacing stance ready to strike",
            "a Greek bandit swordsman, a stocky brute with a shaved head and a scarred face, a ragged dark blue cloak over "
            "bare shoulders, a studded leather kilt and belt, a curved bronze kopis sword held forward in the right hand, "
            "wide aggressive stance",
            "a Greek bandit swordsman with a dark cloth mask over the lower face and a brown hood, a patched ochre tunic and "
            "leather shoulder guard on one side, a straight bronze short sword raised, a coil of rope and a pouch on the "
            "belt, sneaking forward"],
        "archer": [
            "a Greek bandit archer, a slim young man in a brown and ochre hooded cloak, a leather quiver full of arrows on "
            "the back, holding a wooden recurve bow drawn with an arrow nocked, aiming forward",
            "a Greek bandit archer, a wiry woman with braided dark hair and a red headband, a short leather vest over a "
            "linen tunic, a bow held low with an arrow ready, a quiver on the hip, alert stance",
            "a Greek bandit archer in a hooded dark grey cloak with a tattered hem, a face half hidden in shadow, a long "
            "bow drawn to the cheek, a quiver of red-fletched arrows on the back, kneeling on one knee to shoot"],
        "wolf": [
            "a grey wolf, a lean fast predator with thick grey and tawny fur, yellow eyes and bared fangs, crouched low "
            "ready to leap, the whole quadruped body visible",
            "a grey wolf in mid-run, long legs stretched, fur ruffled by the wind, dark grey back fading to a pale belly, "
            "ears back and teeth showing, the whole quadruped body visible",
            "a large dark grey wolf with a scarred muzzle, standing with head lowered and hackles raised, snarling, glowing "
            "amber eyes, the whole quadruped body visible"],
        "shade": [
            "a shade of the dead from the Greek underworld, a tall gaunt ghostly human figure in a tattered flowing shroud, "
            "translucent smoky dark indigo and violet body that fades into wisps at the bottom instead of legs, pale glowing "
            "cyan eyes, long clawed hands reaching forward, a faint cold blue glow around it",
            "a hunched ghost of a dead warrior, a translucent blue-grey spectral body in broken bronze armor and a cracked "
            "crested helmet, empty glowing eye holes, the lower body dissolving into dark smoke, floating forward fast",
            "a shadow wraith, a pitch black hooded shape of living darkness with ragged edges like torn cloth, two bright "
            "white glowing eyes, thin long arms with sharp fingers, purple embers drifting from it, floating above the ground"],
    }
    for name, variants in enemies.items():
        for i, subj in enumerate(variants):
            J.append(dict(key=f"en_{name}_{i + 1}", group="3", subject=name, prompt=f"{STYLE_CHAR}. {subj}. {CAM_CHAR}",
                          neg=NEG_CHAR, w=CONCEPT_PX, h=CONCEPT_PX, seed=seed))
            seed += 1
    # 2. значки этапа 3 — по 2 варианта
    icons = {
        "copper_helmet": ("a copper helmet in ancient Greek Corinthian style, polished bright orange copper with a nose guard "
                          "and cheek guards, a short red horsehair crest on top, empty helmet, no head", "head, face, person"),
        "copper_chestplate": ("a copper muscle cuirass breastplate in ancient Greek style, polished bright orange copper chest "
                              "armor shaped like a muscular torso, leather shoulder straps and a short skirt of leather strips "
                              "below, empty armor, no person", "person, head, arms, mannequin"),
        "copper_greaves": ("a pair of copper greaves, two shin guards of polished bright orange copper shaped to the calf with "
                           "leather straps and buckles, standing upright side by side, empty, no legs",
                           "legs, feet, person, boots, shoes"),
        "copper_arrows": ("a small bundle of exactly three wooden arrows lying diagonally side by side and parallel, straight "
                          "light wooden shafts, sharp shiny polished bright orange copper arrowheads at the upper right, white "
                          "and red feather fletching at the lower left", "bow, quiver, target, two arrows, crossed arrows"),
        "bandage": ("a rolled linen bandage, a tight roll of clean off-white cloth strip with one loose end unrolled, tied "
                    "with a thin red thread", "blood, wound, person, hand, paper roll, toilet paper"),
        "healing_herb": ("a small bunch of medicinal healing herbs, fresh vivid green leaves and small white and yellow "
                         "flowers on stems, tied together with a twine string", "pot, vase, soil, roots, garden"),
        "torch_box": ("a standing floor torch to light a yard: a wooden torch post stuck upright in a small square wooden "
                      "crate filled with grey stones, a bright warm orange and yellow flame burning on top",
                      "wall bracket, candle, smoke cloud, lantern"),
        "wood_spikes": ("a wooden spike trap, a low square wooden frame base with a dense row of sharpened wooden stakes "
                        "pointing up and outward, rawhide lashings, a placeable trap object", "metal, blood, person, fence"),
    }
    for name, (subj, neg) in icons.items():
        white = name == "healing_herb"   # зелёную траву на зелёном фоне не вырезать — белый фон
        for i in range(2):
            j = dict(key=f"ic_{name}_{i + 1}", group="2", subject=name,
                     prompt=f"{STYLE_B}. {subj}. {CAM_ICON_WHITE if white else CAM_ICON}",
                     neg=f"{NEG_ICON_WHITE if white else NEG_ICON}, {neg}", w=ICON_PX, h=ICON_PX, seed=seed)
            if name == "copper_arrows" and i == 1:
                j.update(sketch="sk_copper_arrows", denoise=0.78)
            J.append(j)
            seed += 1
    # 4. Цербер — 4 концепта
    cer = [
        "styled after ancient Greek black-figure pottery: a glossy black body with terracotta orange and red accents and "
        "incised fine lines",
        "a naturalistic huge dark hound with coarse black and charcoal fur, scars and bronze spiked collars on every neck",
        "a hellish beast with smouldering ember cracks in its black hide, glowing red eyes, green poisonous foam dripping "
        "from the right head's jaws",
        "a sleek giant black mastiff with a bronze harness, the middle head howling with jaws wide open, the left head "
        "snarling, the right head drooling foam"]
    for i, look in enumerate(cer):
        subj = ("Cerberus, the giant three-headed hound guarding the gates of Hades, a massive muscular dog with three "
                "heads on thick necks, manes of writhing snakes on the necks, a long serpent for a tail, the whole beast "
                f"seen from above at a three-quarter angle, a huge boss creature about three times the size of a man, {look}")
        J.append(dict(key=f"cer_{i + 1}", group="4", subject="cerberus", prompt=f"{STYLE_CHAR}. {subj}. {CAM_CHAR}",
                      neg=NEG_CHAR + ", two heads, one head, four heads", w=CONCEPT_PX, h=CONCEPT_PX, seed=seed))
        seed += 1
    return J


# ---------------------------------------------------------------- ComfyUI
def call(path, body=None, timeout=30, raw_data=None, headers=None):
    data = json.dumps(body).encode() if body is not None else raw_data
    h = headers or ({"Content-Type": "application/json"} if body is not None else {})
    req = urllib.request.Request(COMFY + path, data=data, headers=h)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        raw = r.read()
    return json.loads(raw) if raw[:1] in (b"{", b"[") else raw


def upload(path: Path) -> str:
    """POST /upload/image (multipart) → имя файла во входной папке ComfyUI."""
    bnd = uuid.uuid4().hex
    name = f"orpheus_a006_{path.name}"
    parts = [f"--{bnd}\r\nContent-Disposition: form-data; name=\"overwrite\"\r\n\r\ntrue\r\n".encode(),
             f"--{bnd}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"{name}\"\r\n"
             f"Content-Type: image/png\r\n\r\n".encode() + path.read_bytes() + b"\r\n",
             f"--{bnd}--\r\n".encode()]
    r = call("/upload/image", raw_data=b"".join(parts), headers={"Content-Type": f"multipart/form-data; boundary={bnd}"},
             timeout=60)
    return r["name"]


def graph(j, image_name=None):
    g = {
        "28": {"class_type": "UNETLoader", "inputs": {"unet_name": "z_image_bf16.safetensors", "weight_dtype": "default"}},
        "30": {"class_type": "CLIPLoader", "inputs": {"clip_name": "qwen_3_4b.safetensors", "type": "lumina2", "device": "default"}},
        "29": {"class_type": "VAELoader", "inputs": {"vae_name": "ae.safetensors"}},
        "11": {"class_type": "ModelSamplingAuraFlow", "inputs": {"model": ["28", 0], "shift": 3.0}},
        "27": {"class_type": "CLIPTextEncode", "inputs": {"text": j["prompt"], "clip": ["30", 0]}},
        "33": {"class_type": "CLIPTextEncode", "inputs": {"text": j["neg"], "clip": ["30", 0]}},
        "13": {"class_type": "EmptySD3LatentImage", "inputs": {"width": j["w"], "height": j["h"], "batch_size": 1}},
        "3": {"class_type": "KSampler", "inputs": {"model": ["11", 0], "seed": j["seed"], "steps": 30, "cfg": 4.0,
                                                   "sampler_name": "res_multistep", "scheduler": "simple",
                                                   "positive": ["27", 0], "negative": ["33", 0],
                                                   "latent_image": ["13", 0], "denoise": 1.0}},
        "8": {"class_type": "VAEDecodeTiled", "inputs": {"samples": ["3", 0], "vae": ["29", 0], "tile_size": 768,
                                                         "overlap": 64, "temporal_size": 64, "temporal_overlap": 8}},
        "9": {"class_type": "SaveImage", "inputs": {"filename_prefix": f"orpheus/a006_{j['key']}", "images": ["8", 0]}},
    }
    if image_name:
        del g["13"]
        g["50"] = {"class_type": "LoadImage", "inputs": {"image": image_name}}
        g["51"] = {"class_type": "VAEEncode", "inputs": {"pixels": ["50", 0], "vae": ["29", 0]}}
        g["3"]["inputs"]["latent_image"] = ["51", 0]
        g["3"]["inputs"]["denoise"] = j["denoise"]
    return g


def load_ledger():
    p = RAW / "ledger.json"
    return json.loads(p.read_text(encoding="utf-8")) if p.exists() else {}


def save_ledger(led):
    RAW.mkdir(parents=True, exist_ok=True)
    tmp = RAW / "ledger.tmp"
    tmp.write_text(json.dumps(led, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n")
    tmp.replace(RAW / "ledger.json")


def cmd_queue(keys):
    led = load_ledger()
    uploaded = {}
    for j in jobs():
        k = j["key"]
        if keys and k not in keys:
            continue
        if (RAW / f"{k}.png").exists() or led.get(k, {}).get("status") == "queued":
            print(f"{k}: уже есть или в очереди")
            continue
        img = None
        if j.get("sketch"):
            if j["sketch"] not in uploaded:
                p = SKETCH / f"{j['sketch']}.png"
                if not p.exists():
                    make_sketches()
                uploaded[j["sketch"]] = upload(p)
            img = uploaded[j["sketch"]]
        pid = call("/prompt", {"prompt": graph(j, img), "client_id": str(uuid.uuid4())})["prompt_id"]
        led[k] = {**{x: j[x] for x in ("group", "subject", "seed", "w", "h", "prompt", "neg")},
                  "sketch": j.get("sketch"), "denoise": j.get("denoise"), "pid": pid, "status": "queued",
                  "queued": time.strftime("%Y-%m-%d %H:%M:%S")}
        save_ledger(led)
        print(f"{k}: {pid}", flush=True)


def cmd_collect():
    led = load_ledger()
    left = 0
    for k, j in led.items():
        if j.get("status") != "queued":
            continue
        entry = call(f"/history/{j['pid']}").get(j["pid"])
        if not entry:
            left += 1
            continue
        st = entry.get("status", {})
        if st.get("status_str") == "error":
            j["status"], j["error"] = "error", json.dumps(st.get("messages", [])[-1:], ensure_ascii=False)[:600]
            print(f"{k}: ошибка {j['error']}")
            continue
        images = entry.get("outputs", {}).get("9", {}).get("images")
        if not images:
            left += 1
            continue
        img = images[0]
        q = urllib.parse.urlencode({"filename": img["filename"], "subfolder": img["subfolder"], "type": img["type"]})
        (RAW / f"{k}.png").write_bytes(call(f"/view?{q}", timeout=120))
        j["status"], j["done"] = "done", time.strftime("%Y-%m-%d %H:%M:%S")
        print(f"{k}: скачано {j['done']}", flush=True)
    save_ledger(led)
    return left


def cmd_wait(every, limit_h):
    t_end = time.time() + limit_h * 3600
    while True:
        try:
            left = cmd_collect()
        except (urllib.error.URLError, OSError) as e:
            print(f"{time.strftime('%H:%M')} Мак не ответил: {e}", flush=True)
            left = -1
        done = sum(1 for j in load_ledger().values() if j.get("status") == "done")
        print(f"{time.strftime('%H:%M')} готово {done}, ждут {left}", flush=True)
        if left == 0:
            return 0
        if time.time() > t_end:
            print("предел времени сборщика — выход", flush=True)
            return 1
        time.sleep(every)


def cmd_table():
    led = load_ledger()
    print("| Ключ | id задания | seed | эскиз / denoise | Статус |\n|---|---|---|---|---|")
    for k, j in led.items():
        sk = f"{j['sketch']} / {j['denoise']}" if j.get("sketch") else "—"
        print(f"| `{k}` | `{j['pid']}` | {j['seed']} | {sk} | {j.get('status')} |")


# ---------------------------------------------------------------- листы
P1_LABEL = {"hammer": "молот", "wood_wall": "стена деревянная", "platform": "настил", "door": "дверь", "arrows": "стрелы"}
IC_LABEL = {"copper_helmet": "медный шлем", "copper_chestplate": "медный доспех", "copper_greaves": "медные поножи",
            "copper_arrows": "медные стрелы", "bandage": "повязка", "healing_herb": "лечебная трава",
            "torch_box": "факел-ящик", "wood_spikes": "шипы деревянные"}
EN_LABEL = {"swordsman": "разбойник-мечник", "archer": "разбойник-лучник", "wolf": "волк", "shade": "тень"}
ICONS_OUT = ROOT / "work" / "art" / "icons_a006"
SCREENS = ROOT / "work" / "screens"


def icon_pair(key):
    """Сырая картинка → (128, 64) с вырезанным фоном; None — нет картинки."""
    import numpy as np
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import art_icon_probe as probe
    src = RAW / f"{key}.png"
    if not src.exists():
        return None
    im = Image.open(src).convert("RGB")
    cut = probe.cut_green(im)
    border = np.concatenate([np.asarray(im)[:8].reshape(-1, 3), np.asarray(im)[-8:].reshape(-1, 3)])
    med = np.median(border, axis=0)
    if not (med[1] > med[0] + 60 and med[1] > med[2] + 60):     # фон не зелёный: цвет предмета не гасить
        cut = Image.merge("RGBA", (*im.split(), cut.getchannel("A")))
    return probe.fit(cut, 128), probe.fit(cut, 64)


def label_font(size, bold=False):
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import art_icon_probe as probe
    return probe.font(size, bold)


def grass_cell(ic128):
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import art_icon_probe as probe
    g = probe.grass(probe.CELL + 16, probe.CELL + 16)
    g.alpha_composite(probe.cell_with(probe.fit_from(ic128, probe.ICON) if ic128 is not None else None), (8, 8))
    return g


def icon_sheet(title, rows, out, before=None):
    """rows: [(подпись, [ключи])]; before: {подпись: картинка 128 «до»}."""
    led = load_ledger()
    ICONS_OUT.mkdir(parents=True, exist_ok=True)
    ncol = max(len(k) for _, k in rows) + (2 if before else 0)
    cw, rh, left, top = 150, 230, 200, 60
    W, H = left + ncol * cw + 20, top + len(rows) * rh + 10
    sh = Image.new("RGBA", (W, H), (236, 222, 196, 255))
    d = ImageDraw.Draw(sh)
    d.text((16, 14), title, fill=(50, 30, 20), font=label_font(24, True))
    for ri, (lab, keys) in enumerate(rows):
        y = top + ri * rh
        d.text((16, y + 50), lab, fill=(50, 30, 20), font=label_font(20, True))
        cells = []
        if before:
            cells.append(("было", before[lab][0]))
            cells.append(("эскиз", before[lab][1]))
        for k in keys:
            pair = icon_pair(k)
            if pair:
                pair[0].save(ICONS_OUT / f"{k}_128.png", optimize=True)
            j = led.get(k, {})
            note = f"d={j['denoise']}" if j.get("denoise") else ("без эскиза" if k.startswith("p1_") else "")
            cells.append((f"{k.rsplit('_', 1)[-1]} {note}".strip(), pair[0] if pair else None))
        for ci, (cap, im) in enumerate(cells):
            x = left + ci * cw
            d.rectangle((x, y, x + 132, y + 132), outline=(170, 130, 80))
            if im is None:
                d.text((x + 10, y + 56), "нет", fill=(170, 40, 30), font=label_font(18))
            else:
                im = im.convert("RGBA")
                if im.size != (128, 128):
                    im = im.resize((128, 128), Image.LANCZOS)
                sh.alpha_composite(im, (x + 2, y + 2))
                if cap != "эскиз":
                    sh.alpha_composite(grass_cell(im), (x + 34, y + 140))
            d.text((x + 2, y + 208), cap, fill=(70, 45, 30), font=label_font(14))
    SCREENS.mkdir(parents=True, exist_ok=True)
    sh.convert("RGB").save(out, optimize=True)
    print("лист:", out)


def concept_sheet(title, rows, out, px, dst_dir):
    dst_dir.mkdir(parents=True, exist_ok=True)
    ncol = max(len(k) for _, k in rows)
    left, top = 220, 60
    W, H = left + ncol * (px + 12) + 10, top + len(rows) * (px + 40)
    sh = Image.new("RGB", (W, H), (236, 222, 196))
    d = ImageDraw.Draw(sh)
    d.text((16, 14), title, fill=(50, 30, 20), font=label_font(24, True))
    for ri, (lab, keys) in enumerate(rows):
        y = top + ri * (px + 40)
        d.text((16, y + px // 2 - 12), lab, fill=(50, 30, 20), font=label_font(22, True))
        for ci, k in enumerate(keys):
            x = left + ci * (px + 12)
            src = RAW / f"{k}.png"
            if src.exists():
                im = Image.open(src).convert("RGB")
                im.resize((512, 512), Image.LANCZOS).save(dst_dir / f"{k}.png", optimize=True)
                sh.paste(im.resize((px, px), Image.LANCZOS), (x, y))
            else:
                d.rectangle((x, y, x + px, y + px), outline=(170, 130, 80))
                d.text((x + 20, y + px // 2), "нет картинки", fill=(170, 40, 30), font=label_font(18))
            d.text((x + 4, y + px + 6), k, fill=(70, 45, 30), font=label_font(15))
    sh.save(out, optimize=True)
    print("лист:", out)


def cmd_sheets():
    icons_b = ROOT / "art" / "style" / "icons_b"
    temp = ROOT / "client" / "Assets" / "Art" / "_TEMP"
    before = {}
    for k, lab in P1_LABEL.items():
        b = icons_b / f"{k}_128.png"
        old = Image.open(b) if b.exists() else Image.open(temp / f"TEMP_item_{k}.png").resize((128, 128), Image.NEAREST)
        before[lab] = (old, Image.open(SKETCH / f"sk_{k}.png").convert("RGBA"))
    icon_sheet("A-006 п. 1 — перезаказ значков по эскизу (img2img Z-Image, denoise) и строгим промптом",
               [(lab, [f"p1_{k}_{i}" for i in ("1", "2", "3", "4", "t")]) for k, lab in P1_LABEL.items()],
               SCREENS / "A-006_icons_p1.png", before)
    icon_sheet("A-006 п. 2 — значки этапа 3 (стиль Б), по 2 варианта",
               [(lab, [f"ic_{k}_1", f"ic_{k}_2"]) for k, lab in IC_LABEL.items()], SCREENS / "A-006_icons_stage3.png")
    concept_sheet("A-006 п. 3 — концепты врагов этапа 3 (вид сверху 3/4)",
                  [(lab, [f"en_{k}_{i}" for i in (1, 2, 3)]) for k, lab in EN_LABEL.items()],
                  SCREENS / "A-006_enemies.png", 384, ROOT / "work" / "art" / "enemies")
    concept_sheet("A-006 п. 4 — Цербер, концепты босса (вид сверху 3/4)",
                  [("Цербер", [f"cer_{i}" for i in (1, 2, 3, 4)])], SCREENS / "A-006_cerberus.png", 448,
                  ROOT / "work" / "art" / "cerberus")



def cmd_install(pairs):
    """предмет=ключ … → art/style/icons_b/<предмет>_128/_64.png, TEMP_item_<предмет>.png (64, пурпурный уголок), манифест."""
    import csv
    icons_b = ROOT / "art" / "style" / "icons_b"
    temp = ROOT / "client" / "Assets" / "Art" / "_TEMP"
    man = ROOT / "art" / "manifest.csv"
    led = load_ledger()
    with man.open(encoding="utf-8", newline="") as f:
        rows = list(csv.reader(f))
    notes = {}
    for pr in pairs:
        item, _, key = pr.partition("=")
        big, small = icon_pair(key)
        big.save(icons_b / f"{item}_128.png", optimize=True)
        small.save(icons_b / f"{item}_64.png", optimize=True)
        t = small.copy()
        ImageDraw.Draw(t).rectangle([0, 0, 7, 7], fill=(255, 0, 255, 255))
        t.save(temp / f"TEMP_item_{item}.png", optimize=True)
        j = led[key]
        how = f"img2img по эскизу {j['sketch']}, denoise {j['denoise']}" if j.get("sketch") else "без эскиза"
        notes[f"client/Assets/Art/_TEMP/TEMP_item_{item}.png"] = (
            f"значок предмета «{P1_LABEL.get(item, item)}» 64x64; Мак, стиль Б (A-006: Z-Image base, {how}, seed {j['seed']}, "
            f"ключ {key}); 128 px — art/style/icons_b/{item}_128.png; финальный — после приёмки Автора; скрипт tools/art_a006.py")
        print(f"{item}: {key}")
    rows = [[r[0], "placeholder", "А", notes[r[0]]] if r and r[0] in notes else r for r in rows]
    with man.open("w", encoding="utf-8", newline="") as f:
        csv.writer(f, lineterminator="\n").writerows(rows)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cmd", choices=["sketches", "list", "queue", "collect", "wait", "table", "sheets", "install"])
    ap.add_argument("keys", nargs="*")
    ap.add_argument("--every", type=int, default=600)
    ap.add_argument("--limit-h", type=float, default=11)
    a = ap.parse_args()
    if a.cmd == "sketches":
        for p in make_sketches():
            print(p)
    elif a.cmd == "list":
        J = jobs()
        for j in J:
            print(f"{j['key']:<26} гр.{j['group']} {j['w']}px seed {j['seed']}"
                  + (f" эскиз {j['sketch']} d={j['denoise']}" if j.get("sketch") else ""))
        print("всего", len(J))
    elif a.cmd == "queue":
        cmd_queue(a.keys)
    elif a.cmd == "collect":
        print("ждут:", cmd_collect())
    elif a.cmd == "wait":
        sys.exit(cmd_wait(a.every, a.limit_h))
    elif a.cmd == "install":
        cmd_install(a.keys)
    elif a.cmd == "sheets":
        cmd_sheets()
    else:
        cmd_table()


if __name__ == "__main__":
    main()
