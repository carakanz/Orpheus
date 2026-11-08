#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Сверка этапа 3 «Биомы долями» генератора v2 на C# (карточка 1-016, приёмка `08` 1.8) с моделью
design_room/threads/01_world/worldgen_model_v2.py (classify2, full=False) на тех же номерах зёрен, набор «срез».
Зёрна numpy и SplitMix64 разные, поэтому сравниваются распределения по зёрнам:
  - доли биомов после этапа 3 (приёмка 1.8: разница средних ≤ 2 п. п.) и допуск `01` 3 в каждом зерне
    (±25 % доли, но не уже ±1 п. п.);
  - те же доли после «луга в d ≤ 150» (так делает one_world2 модели; в C# это этап 7 — потом) — для справки;
  - ближайшая к Очагу ячейка каждого биома и самый дальний из четырёх (`01` 2 п. 4; модель считает после «луга в d ≤ 150»);
  - число 4-связных областей биома на торе и средняя площадь области — форма карты, а не только доли.

Дампы пишет gen_preview (с 1-016 в дампе есть слой биома):
  dotnet run -c Release --project tools/gen_preview -- --macro --size 1024 --seeds 1-30 --dump
Запуск (Python 3.12 + numpy + pillow; из корня рабочей копии):
  python tools/gen_preview/compare_biomes.py --size 1024 --seeds 1-30 [--png 1,2,3 --png-dir work/screens]
Вывод — markdown в stdout; с --png — картинки «C# слева, модель справа» <png-dir>/1-016_<W>_<зерно>.png.
"""
import argparse, os, sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(ROOT, 'design_room', 'threads', '01_world'))
import worldgen_model as B          # noqa: E402
import worldgen_model_v2 as V2      # noqa: E402
from compare_fields import read_dump  # noqa: E402

SLICE = [B.MEADOW, B.FOREST, B.MOUNT, B.CAVE]
TARGET = {B.CAVE: 8.0, B.MOUNT: 14.0, B.FOREST: 33.0, B.MEADOW: 45.0}   # луга — остаток 100 − 8 − 14 − 33


def model_world(W, seed):
    coords, h, m, c, shH, shC = V2.fields2(W, seed)
    X, d, dH, dC, dA = B.geometry(W, coords, shH, shC)
    b, _, _ = V2.classify2(W, h, m, c, dH, dC, dA, full=False)
    return b.astype(np.int16), d


def shares(b):
    n = b.size
    return {cl: 100.0 * float((b == cl).sum()) / n for cl in SLICE}


def hearth_meadow(b, d):
    b = b.copy()
    b[(d <= 150) & (b != B.LAKE)] = B.MEADOW
    return b


def regions(b):
    lab, comps = B.label(b)
    out = {}
    for cl in SLICE:
        sizes = [len(mem) for c_, mem in comps if c_ == cl]
        out[cl] = (len(sizes), float(np.mean(sizes)) if sizes else 0.0)
    return out


def tol_ok(share, target):
    return abs(share - target) <= max(1.0, 0.25 * target)


def stat(v):
    v = np.asarray(v, float)
    return f'{v.mean():.2f} ({v.min():.2f}–{v.max():.2f})'


def png_pair(path, W, seed, b_cs, b_py):
    from PIL import Image, ImageDraw, ImageFont
    M = b_cs.shape[0]; s = max(1, 512 // M); pad = 24
    def img(b):
        a = np.zeros((M, M, 3), np.uint8)
        a[:] = (255, 0, 255)
        for cl, col in B.COL.items():
            a[b == cl] = col
        im = Image.fromarray(a).resize((M * s, M * s), Image.NEAREST)
        dr = ImageDraw.Draw(im); k = s / B.CELL; c0 = W / 2 * k
        for r in (256, W / 4, W / 2):
            dr.ellipse([c0 - r * k, c0 - r * k, c0 + r * k, c0 + r * k], outline=(0, 0, 0))
        return im
    out = Image.new('RGB', (2 * M * s + pad, M * s + pad), (255, 255, 255))
    out.paste(img(b_cs), (0, pad)); out.paste(img(b_py), (M * s + pad, pad))
    try:
        font = ImageFont.truetype('C:/Windows/Fonts/arial.ttf', 16)
    except OSError:
        font = ImageFont.load_default()
    dr = ImageDraw.Draw(out)
    dr.text((4, 3), f'C#, {W}², зерно {seed} (этап 3)', fill=(0, 0, 0), font=font)
    dr.text((M * s + pad + 4, 3), f'модель classify2, зерно {seed}', fill=(0, 0, 0), font=font)
    out.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--size', type=int, default=1024)
    ap.add_argument('--seeds', default='1-30')
    ap.add_argument('--dir', default=os.path.join(ROOT, 'build', 'gen_preview', 'macro'))
    ap.add_argument('--png', default='')
    ap.add_argument('--png-dir', default=os.path.join(ROOT, 'work', 'screens'))
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding='utf-8')
    lo, hi = (int(x) for x in a.seeds.split('-'))
    seeds = list(range(lo, hi + 1)); W = a.size
    pngs = {int(x) for x in a.png.split(',') if x}
    sh = {s: {cl: [] for cl in SLICE} for s in ('cs', 'py')}
    sh8 = {s: {cl: [] for cl in SLICE} for s in ('cs', 'py')}
    mind = {s: {cl: [] for cl in SLICE} for s in ('cs', 'py')}
    far = {'cs': [], 'py': []}
    reg = {s: {cl: ([], []) for cl in SLICE} for s in ('cs', 'py')}
    bad = {'cs': 0, 'py': 0}; allb = {'cs': 0, 'py': 0}; unl = 0
    for seed in seeds:
        cs = read_dump(os.path.join(a.dir, f'fields_{W}_{seed}.bin'))
        assert 'biome' in cs, 'старый дамп без слоя биома — перезапустить gen_preview --dump'
        b_cs = cs['biome'].astype(np.int16)
        unl += int((b_cs == 255).sum())
        b_py, d_py = model_world(W, seed)
        if seed in pngs:
            png_pair(os.path.join(a.png_dir, f'1-016_{W}_{seed}.png'), W, seed, b_cs, b_py)
        for src, b, d in (('cs', b_cs, cs['d']), ('py', b_py, d_py)):
            s3 = shares(b)
            bad[src] += any(not tol_ok(s3[cl], TARGET[cl]) for cl in SLICE)
            allb[src] += all(s3[cl] > 0 for cl in SLICE)
            b8 = hearth_meadow(b, d)
            s8 = shares(b8)
            for cl in SLICE:
                sh[src][cl].append(s3[cl]); sh8[src][cl].append(s8[cl])
                mind[src][cl].append(B.near_biome_min_d(b8, d, cl))
            far[src].append(max(mind[src][cl][-1] for cl in SLICE))
            rg = regions(b)
            for cl in SLICE:
                reg[src][cl][0].append(rg[cl][0]); reg[src][cl][1].append(rg[cl][1])

    P = print; n = len(seeds)
    P(f'### Сверка биомов C# ↔ модель v2 (classify2, срез): {W}², зёрна {lo}–{hi} ({n})\n')
    P(f'Не размеченных ячеек у C#: {unl}. Все 4 биома: C# — в {allb["cs"]} из {n}, модель — в {allb["py"]} из {n}. '
      f'Зёрен с долей вне допуска `01` 3: C# — {bad["cs"]}, модель — {bad["py"]}.\n')
    P('| Биом | Цель, % | Этап 3: C#, % | Этап 3: модель, % | Разница средних, п. п. | После «луга в d ≤ 150»: C# / модель, % |')
    P('|---|---|---|---|---|---|')
    worst = 0.0
    for cl in (B.CAVE, B.MOUNT, B.FOREST, B.MEADOW):
        dlt = np.mean(sh['cs'][cl]) - np.mean(sh['py'][cl]); worst = max(worst, abs(dlt))
        P(f'| {B.NAMES[cl]} | {TARGET[cl]:.0f} | {stat(sh["cs"][cl])} | {stat(sh["py"][cl])} | {dlt:+.3f} | '
          f'{np.mean(sh8["cs"][cl]):.2f} / {np.mean(sh8["py"][cl]):.2f} |')
    P(f'\nПриёмка 1.8: худшая разница средних долей — {worst:.3f} п. п. (норма ≤ 2).\n')
    P('| Биом | Ближайшая к Очагу ячейка, тайлов: C# медиана (макс) | Модель медиана (макс) | Областей: C# среднее (мин–макс) | Модель | Средняя площадь области, ячеек: C# / модель |')
    P('|---|---|---|---|---|---|')
    for cl in (B.CAVE, B.MOUNT, B.FOREST, B.MEADOW):
        P(f'| {B.NAMES[cl]} | {np.median(mind["cs"][cl]):.0f} ({max(mind["cs"][cl]):.0f}) | '
          f'{np.median(mind["py"][cl]):.0f} ({max(mind["py"][cl]):.0f}) | {stat(reg["cs"][cl][0])} | {stat(reg["py"][cl][0])} | '
          f'{np.mean(reg["cs"][cl][1]):.1f} / {np.mean(reg["py"][cl][1]):.1f} |')
    P(f'\nСамый дальний из четырёх биомов от Очага (после «луга в d ≤ 150»): C# — медиана {np.median(far["cs"]):.0f}, '
      f'худший {max(far["cs"]):.0f}; модель — {np.median(far["py"]):.0f}, {max(far["py"]):.0f} тайлов (норма `01` 2 п. 4: ≤ 1080).')


if __name__ == '__main__':
    main()
