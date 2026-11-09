#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Сверка мира генератора v1 на C# (карточка 1-017) с моделью design_room/threads/01_world/worldgen_model_v2.py на тех же
номерах зёрен, набор «срез». Модель доходит до этапа 3 и «луга в d ≤ 150» (one_world2), этапов 11–12 в ней нет — поэтому:
  - макрокарта после этапа 7 (C#: луга в d ≤ 150 + штамп леса в 200) против classify2 + «луга в d ≤ 150» модели: доли,
    ближайшая к Очагу ячейка каждого биома, самый дальний из четырёх (`01` 2 п. 4), лес в 200, число областей;
  - тайлы C# (CSV gen_preview --world) — против чисел документа: доли тайлов против макрокарты, деревья на 1000 тайлов лугов
    (8) и леса (120), скальная масса гор (45 %), медь в 600 (≥ 8), связность (≥ 99 %).
Зёрна numpy и SplitMix64 разные — сравниваются распределения по зёрнам.

Данные пишет gen_preview:
  dotnet run -c Release --project tools/gen_preview -- --world --size 1024 --seeds 1-30 --dump > build/gen_preview/world/stats_1024.csv
Запуск (Python 3.12 + numpy; из корня рабочей копии):
  python tools/gen_preview/compare_world.py --size 1024 --seeds 1-30
"""
import argparse, csv, os, sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(ROOT, 'design_room', 'threads', '01_world'))
import worldgen_model as B          # noqa: E402
import worldgen_model_v2 as V2      # noqa: E402

SLICE = [B.MEADOW, B.FOREST, B.MOUNT, B.CAVE]


def model_world(W, seed):
    coords, h, m, c, shH, shC = V2.fields2(W, seed)
    X, d, dH, dC, dA = B.geometry(W, coords, shH, shC)
    b, _, _ = V2.classify2(W, h, m, c, dH, dC, dA, full=False)
    b = b.astype(np.int16)
    b[(d <= 150) & (b != B.LAKE)] = B.MEADOW
    return b, d


def geometry_d(W):
    M = W // B.CELL
    co = np.arange(M) * B.CELL + B.CELL / 2.0
    return np.sqrt((co[None, :] - W / 2) ** 2 + (co[:, None] - W / 2) ** 2)


def shares(b):
    return {cl: 100.0 * float((b == cl).sum()) / b.size for cl in SLICE}


def regions(b):
    lab, comps = B.label(b)
    return {cl: sum(1 for c_, mem in comps if c_ == cl) for cl in SLICE}


def stat(v):
    v = np.asarray(v, float)
    return f'{v.mean():.2f} ({v.min():.2f}–{v.max():.2f})'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--size', type=int, default=1024)
    ap.add_argument('--seeds', default='1-30')
    ap.add_argument('--dir', default=os.path.join(ROOT, 'build', 'gen_preview', 'world'))
    ap.add_argument('--csv', default='')
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding='utf-8')
    lo, hi = (int(x) for x in a.seeds.split('-'))
    seeds = list(range(lo, hi + 1)); W = a.size; M = W // B.CELL
    rows = {}
    with open(a.csv or os.path.join(a.dir, f'stats_{W}.csv'), encoding='utf-8') as f:
        for r in csv.DictReader(line for line in f if line[:1].isdigit() or line.startswith('seed')):
            rows[int(r['seed'])] = r
    d_cs = geometry_d(W)
    sh = {s: {cl: [] for cl in SLICE} for s in ('cs', 'py')}
    mind = {s: {cl: [] for cl in SLICE} for s in ('cs', 'py')}
    reg = {s: {cl: [] for cl in SLICE} for s in ('cs', 'py')}
    far = {'cs': [], 'py': []}; forest200 = {'cs': 0, 'py': 0}
    for seed in seeds:
        b_cs = np.fromfile(os.path.join(a.dir, f'world_{W}_{seed}.bin'), np.uint8).reshape(M, M).astype(np.int16)
        b_py, d_py = model_world(W, seed)
        for src, b, d in (('cs', b_cs, d_cs), ('py', b_py, d_py)):
            s = shares(b); rg = regions(b)
            for cl in SLICE:
                sh[src][cl].append(s[cl]); reg[src][cl].append(rg[cl])
                mind[src][cl].append(B.near_biome_min_d(b, d, cl))
            far[src].append(max(mind[src][cl][-1] for cl in SLICE))
            forest200[src] += mind[src][B.FOREST][-1] <= 200
    n = len(seeds); P = print
    stamps = sum(int(rows[s]['forest_stamp']) for s in seeds)
    P(f'### Сверка мира C# v1 ↔ модель v2 (срез, после «луга в d ≤ 150»): {W}², зёрна {lo}–{hi} ({n})\n')
    P('| Биом | Макрокарта C#, % | Модель, % | Разница средних, п. п. | Ближайшая ячейка: C# медиана (макс) | Модель медиана (макс) | Областей: C# / модель, среднее |')
    P('|---|---|---|---|---|---|---|')
    for cl in (B.CAVE, B.MOUNT, B.FOREST, B.MEADOW):
        P(f'| {B.NAMES[cl]} | {stat(sh["cs"][cl])} | {stat(sh["py"][cl])} | {np.mean(sh["cs"][cl]) - np.mean(sh["py"][cl]):+.3f} | '
          f'{np.median(mind["cs"][cl]):.0f} ({max(mind["cs"][cl]):.0f}) | {np.median(mind["py"][cl]):.0f} ({max(mind["py"][cl]):.0f}) | '
          f'{np.mean(reg["cs"][cl]):.2f} / {np.mean(reg["py"][cl]):.2f} |')
    P(f'\nСамый дальний из четырёх биомов от Очага: C# — медиана {np.median(far["cs"]):.0f}, худший {max(far["cs"]):.0f}; '
      f'модель — {np.median(far["py"]):.0f}, {max(far["py"]):.0f} тайлов (норма `01` 2 п. 4: ≤ 1080). '
      f'Лес в 200 от Очага: C# — {forest200["cs"]} из {n} (штамп леса понадобился в {stamps}), модель без штампа — {forest200["py"]} из {n}.\n')

    col = lambda k: np.array([float(rows[s][k]) for s in seeds])
    P('| Тайлы C# (этапы 11–12) | Документ | Среднее (мин–макс) |')
    P('|---|---|---|')
    for k, name, doc in (('meadow_pct', 'луга, % тайлов', '≈ макрокарте'), ('forest_pct', 'лес, % тайлов', '≈ макрокарте'),
                         ('mount_pct', 'горы, % тайлов', '≈ 14'), ('cave_pct', 'пещеры, % тайлов', '≈ 8'),
                         ('meadow_trees_k', 'деревья на 1000 тайлов лугов', '8 (приёмка 6–10)'),
                         ('forest_trees_k', 'деревья на 1000 тайлов леса', '120'),
                         ('mount_rock_pct', 'скальная масса гор, % (с залежами внутри — меньше)', '45'),
                         ('cave_rock_pct', 'скалы пещер (масса + сталагмиты), %', '35 + сталагмиты'),
                         ('copper600', 'залежей меди в 600 от Очага', '≥ 8'),
                         ('reach_pct', 'лугов и леса достижимо от Очага, %', '≥ 99'),
                         ('joined', 'компонент присоединено этапом 12', '—'), ('carved', 'тайлов прорублено этапом 12', '—'),
                         ('ms_total', 'время мира, мс (Release)', '≤ 3000')):
        P(f'| {name} | {doc} | {stat(col(k))} |')
    P(f'\nШтамп меди понадобился в {sum(int(rows[s]["copper_stamp"]) for s in seeds)} зёрнах из {n}.')


if __name__ == '__main__':
    main()
