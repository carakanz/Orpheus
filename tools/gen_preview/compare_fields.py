#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Сверка полей генератора v2 на C# (этапы 1–2, карточка 1-015) с моделью design_room/threads/01_world/worldgen_model_v2.py
на тех же номерах зёрен. Зёрна numpy и SplitMix64 разные, поэтому сравниваются распределения, а не значения:
  - квантили 10/50/90 % полей h, m, c, края (среднее по зёрнам и разброс);
  - 99-й процентиль |разницы| соседних ячеек: все пары и пары через шов (x = M−1 ↔ 0, y — так же),
    отношение «шов / все» (`01` приёмка 2 п. 6: отличаются не больше чем на 10 %);
  - для полного набора — доли зон этапа 1 (жаркая, холодная, Перекрёсток, Предвратие) и пик смещения осей.

Дампы пишет gen_preview:
  dotnet run -c Release --project tools/gen_preview -- --macro --size 1024 --seeds 1-30 --dump
Запуск (Python 3.12 + numpy; из корня рабочей копии):
  python tools/gen_preview/compare_fields.py --size 1024 --seeds 1-30
Вывод — markdown в stdout.
"""
import argparse, os, sys
import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, 'design_room', 'threads', '01_world'))
import worldgen_model as B          # noqa: E402
import worldgen_model_v2 as V2      # noqa: E402


def read_dump(path):
    raw = open(path, 'rb').read()
    assert raw[:4] == b'OFLD', path
    W, = np.frombuffer(raw, '<i4', 1, 4)
    seed, = np.frombuffer(raw, '<u8', 1, 8)
    fset, M = np.frombuffer(raw, '<i4', 2, 16)
    at = 24; n = M * M; out = dict(W=int(W), seed=int(seed), full=bool(fset), M=int(M))
    for k in ('h', 'm', 'c', 'e'):
        out[k] = np.frombuffer(raw, '<u2', n, at).reshape(M, M) / 65536.0; at += 2 * n
    for k in ('shH', 'shC'):
        out[k] = np.frombuffer(raw, '<i4', M, at).astype(float); at += 4 * M
    for k in ('d', 'dH', 'dC', 'dA'):
        out[k] = np.frombuffer(raw, '<i4', n, at).reshape(M, M).astype(float); at += 4 * n
    out['zone'] = np.frombuffer(raw, 'u1', n, at).reshape(M, M); at += n
    if at < len(raw):                    # с 1-016: биом этапа 3 (номера как в модели, 255 — не размечено)
        out['biome'] = np.frombuffer(raw, 'u1', n, at).reshape(M, M); at += n
    assert at == len(raw), (path, at, len(raw))
    return out


def model_fields(W, seed):
    coords, h, m, c, shH, shC = V2.fields2(W, seed)
    # Шум края в модели не отдельное поле (служит c); здесь — отдельное, как в `01` 2: ячейка 256, 2 октавы, поле 6.
    eff = lambda cell: min(cell, W / 2)
    a = np.zeros_like(h); s = 0.0
    for o, cell in enumerate([256, 128]):
        w = 0.5 ** o
        a += w * B.octave2d(W, eff(cell), np.random.default_rng([seed, 6, o]), coords)
        s += w
    return dict(coords=coords, h=h, m=m, c=c, e=a / s, shH=shH, shC=shC)


def neighbour_diffs(f):
    dx = np.abs(np.roll(f, -1, 1) - f)   # (x, x+1) по тору; столбец M−1 — пара через шов
    dy = np.abs(np.roll(f, -1, 0) - f)
    allp = np.concatenate([dx.ravel(), dy.ravel()])
    seam = np.concatenate([dx[:, -1], dy[-1, :]])
    h = f.shape[0] // 2 - 1              # пары через x = W/2: та же фаза решётки, что у шва (узел каждой октавы)
    mid = np.concatenate([dx[:, h], dy[h, :]])
    return allp, seam, mid


def zones_model(W, mf, full):
    X, d, dH, dC, dA = B.geometry(W, mf['coords'], mf['shH'], mf['shC'])
    k = W / 4096.0
    return dict(hot=dH < 600 * k, cold=dC < 500 * k, cross=(dH < 300 * k) & (dC < 250 * k), gate=dA < 64,
                ring0=d < 256, ring3=d >= W / 2)


def zones_cs(z, W, d):
    return dict(hot=(z & 4) > 0, cold=(z & 8) > 0, cross=(z & 16) > 0, gate=(z & 32) > 0,
                ring0=(z & 3) == 0, ring3=(z & 3) == 3)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--size', type=int, default=1024)
    ap.add_argument('--seeds', default='1-30')
    ap.add_argument('--dir', default=os.path.join(ROOT, 'build', 'gen_preview', 'macro'))
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding='utf-8')
    lo, hi = (int(x) for x in a.seeds.split('-'))
    seeds = list(range(lo, hi + 1)); W = a.size
    keys = ('h', 'm', 'c', 'e'); names = dict(h='h', m='m', c='c', e='край')
    q = {s: {k: [] for k in keys} for s in ('cs', 'py')}
    diffs = {s: {k: ([], [], []) for k in keys} for s in ('cs', 'py')}
    zs = {s: {} for s in ('cs', 'py')}; shmax = {'cs': [], 'py': []}; full = False
    for seed in seeds:
        cs = read_dump(os.path.join(a.dir, f'fields_{W}_{seed}.bin'))
        py = model_fields(W, seed)
        full = cs['full']
        for src, f in (('cs', cs), ('py', py)):
            for k in keys:
                q[src][k].append(np.quantile(f[k], [0.1, 0.5, 0.9]))
                for j, part in enumerate(neighbour_diffs(f[k])):
                    diffs[src][k][j].append(part)
            shmax[src].append(max(np.abs(f['shH']).max(), np.abs(f['shC']).max()))
        for src, zz in (('cs', zones_cs(cs['zone'], W, cs['d'])), ('py', zones_model(W, py, full))):
            for nm, msk in zz.items():
                zs[src].setdefault(nm, []).append(100.0 * msk.mean())

    P = print
    P(f'### Сверка полей C# ↔ модель v2: {W}², зёрна {lo}–{hi} ({len(seeds)}), набор {"полный" if full else "срез"}\n')
    P('| Поле | Квантиль | C#: среднее (мин–макс) | Модель: среднее (мин–макс) | Разница средних |')
    P('|---|---|---|---|---|')
    for k in keys:
        a_cs = np.array(q['cs'][k]); a_py = np.array(q['py'][k])
        for j, pct in enumerate((10, 50, 90)):
            P(f'| {names[k]} | {pct} % | {a_cs[:, j].mean():.3f} ({a_cs[:, j].min():.3f}–{a_cs[:, j].max():.3f}) | '
              f'{a_py[:, j].mean():.3f} ({a_py[:, j].min():.3f}–{a_py[:, j].max():.3f}) | {a_cs[:, j].mean() - a_py[:, j].mean():+.3f} |')
    P('\n| Поле | p99 соседей ×1000, C#: все / шов / x = W/2; шов÷все, шов÷(W/2) | Модель: то же |')
    P('|---|---|---|')
    worst_all = -1.0; worst_mid = 0.0
    for k in keys:
        row = []
        for src in ('cs', 'py'):
            al, se, mi = (np.percentile(np.concatenate(diffs[src][k][j]), 99) for j in range(3))
            row.append(f'{1000 * al:.2f} / {1000 * se:.3f} / {1000 * mi:.3f}; {se / al:.3f}, {se / mi:.3f}')
            if src == 'cs' and k != 'e':
                worst_all = max(worst_all, se / al - 1)
                worst_mid = max(worst_mid, abs(se / mi - 1))
        P(f'| {names[k]} | {row[0]} | {row[1]} |')
    P(f'\nC#, h, m, c: шов не грубее всех пар — p99 шва / p99 всех − 1 = {100 * worst_all:+.1f} % (≤ +10 %); '
      f'шов против линии x = W/2 той же фазы решётки — худшее отличие {100 * worst_mid:.1f} % (≤ 10 %). '
      'Шов и линия W/2 лежат на узлах всех октав, где квинтик плоский, поэтому их p99 в сотни раз меньше p99 всех пар — и в модели тоже.')
    P(f'\nПик |смещения оси|: C# {np.mean(shmax["cs"]):.0f}, модель {np.mean(shmax["py"]):.1f} тайлов (норма 350·W/4096 = {350 * W / 4096:.1f}).')
    P('\n| Зона этапа 1, % площади | C#: среднее (мин–макс) | Модель: среднее (мин–макс) |')
    P('|---|---|---|')
    for nm in ('ring0', 'ring3', 'hot', 'cold', 'cross', 'gate'):
        c_ = np.array(zs['cs'][nm]); p_ = np.array(zs['py'][nm])
        P(f'| {nm} | {c_.mean():.2f} ({c_.min():.2f}–{c_.max():.2f}) | {p_.mean():.2f} ({p_.min():.2f}–{p_.max():.2f}) |')
    if not full:
        P('\n(в срезе зон поясов нет: у C# они пустые по `01` 1 «Края»; у модели — посчитаны для справки)')


if __name__ == '__main__':
    main()
