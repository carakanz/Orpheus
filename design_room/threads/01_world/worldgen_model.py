#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Модель генератора 01_world на макрокарте (ячейка 8 x 8 тайлов). Критик 01, 28.10.

Это не генератор, а проверка: сходятся ли этапы 1-13 и гарантии раздела 2 документа
C:\\Users\\carak\\source\\Orpheus\\docs\\01_world.md на мирах 1024, 2048, 4096, 8192.
Шум — решётчатый value-шум (значения в узлах из зерна, как в документе), периодичный по W,
квинтик-сглаживание; h — гребневой (1-|2v-1|)^2, 4 октавы 1024/512/256/128; m — плавный 512/256/128;
c — плавный 256/128/64; смещение поясов — 1D, 2048 и 1024 вдоль оси, пик ±350 (масштаб как R).
Нормировка полей в [0, 1] в документе не задана; здесь — деление на сумму амплитуд октав.

Запуск (Python 3.12 + numpy; pillow — только для --png):
  python C:\\Users\\carak\\source\\Orpheus\\design_room\\threads\\01_world\\worldgen_model.py --png
  (по умолчанию: 4096 — 40 зёрен, 2048 — 30, 8192 — 4, срез 1024 — 200; ключи --s4096 --s2048 --s8192 --s1024)
Вывод — markdown в stdout; прогон критика лежит рядом в result.md.
"""
import argparse, heapq, math, os, sys, time
from collections import Counter, defaultdict
import numpy as np

CELL = 8
NAMES = ['луга', 'лес', 'джунгли', 'болота', 'горы', 'пещеры', 'пустыня', 'город',
         'тундра', 'ледник', 'лавовый', 'озеро', 'Предвратие']
MEADOW, FOREST, JUNGLE, SWAMP, MOUNT, CAVE, DESERT, CITY, TUNDRA, GLACIER, LAVA, LAKE, GATE = range(13)
ACT1 = [MEADOW, FOREST, JUNGLE, SWAMP, MOUNT, CAVE, DESERT, CITY, TUNDRA, GLACIER, LAVA]
TARGET = {MEADOW: 20, FOREST: 17, JUNGLE: 6, SWAMP: 6, MOUNT: 12, CAVE: 6, DESERT: 7, CITY: 2,
          TUNDRA: 7, GLACIER: 5, LAVA: 3, LAKE: 4}
DOC_REGIONS = {MEADOW: (12, 16), FOREST: (14, 20), MOUNT: (10, 14), CAVE: (12, 18), SWAMP: (8, 12),
               JUNGLE: (6, 10), DESERT: (6, 10), TUNDRA: (6, 10), GLACIER: (4, 8), LAVA: (4, 8)}
RAW = dict(h_hi=0.68, c_pocket=0.80, h_karst=0.56, c_karst=0.62, h_lake=0.14, m_lake=0.55,
           h_swamp=0.30, m_swamp=0.62, m_forest=0.50, m_jungle=0.50, c_cross=0.50)


# ---------------------------------------------------------------- шум
def fade(t):
    return t * t * t * (t * (t * 6 - 15) + 10)


def octave2d(W, cell, rng, coords):
    n = max(1, int(W // cell))         # узлов на ось; при cell >= W — один узел (константа)
    nodes = rng.random((n, n))
    u = coords / cell
    i0 = np.floor(u).astype(np.int64)
    f = fade(u - i0)
    i0 %= n
    i1 = (i0 + 1) % n
    A = nodes[i0[:, None], i0[None, :]]
    B = nodes[i0[:, None], i1[None, :]]
    C = nodes[i1[:, None], i0[None, :]]
    D = nodes[i1[:, None], i1[None, :]]
    fx, fy = f[None, :], f[:, None]
    top = A + (B - A) * fx
    bot = C + (D - C) * fx
    return top + (bot - top) * fy


def octave1d(W, cell, rng, coords):
    n = max(1, int(W // cell))
    nodes = rng.random(n)
    u = coords / cell
    i0 = np.floor(u).astype(np.int64)
    f = fade(u - i0)
    i0 %= n
    i1 = (i0 + 1) % n
    return nodes[i0] + (nodes[i1] - nodes[i0]) * f


def fields(W, seed):
    M = W // CELL
    coords = np.arange(M) * CELL + CELL / 2.0
    R = lambda field, o: np.random.default_rng([seed, field, o])
    h = np.zeros((M, M)); ws = 0.0
    for o, cell in enumerate([1024, 512, 256, 128]):
        w = 0.5 ** o
        v = octave2d(W, cell, R(1, o), coords)
        h += w * (1.0 - np.abs(2.0 * v - 1.0)) ** 2
        ws += w
    h /= ws

    def smooth(field, cells):
        a = np.zeros((M, M)); s = 0.0
        for o, cell in enumerate(cells):
            w = 0.5 ** o
            a += w * octave2d(W, cell, R(field, o), coords)
            s += w
        return a / s
    m = smooth(2, [512, 256, 128])
    c = smooth(3, [256, 128, 64])
    k = W / 4096.0

    def shift(field):
        s = np.zeros(M); t = 0.0
        for o, cell in enumerate([2048 * k, 1024 * k]):
            w = 0.5 ** o
            s += w * octave1d(W, cell, R(field, o), coords)
            t += w
        s = 2.0 * s / t - 1.0
        mx = np.abs(s).max()
        return (s / mx if mx > 1e-9 else s) * 350.0 * k
    return coords, h, m, c, shift(4), shift(5)


def wrap(d, W):
    return (d + W / 2.0) % W - W / 2.0


# ---------------------------------------------------------------- зоны и биомы
def geometry(W, coords, shH, shC):
    X = coords - W / 2.0                       # координаты игрока (Очаг в центре)
    d = np.sqrt(X[None, :] ** 2 + X[:, None] ** 2)
    dH = np.abs(wrap(coords[None, :] - shH[:, None], W))   # ось жаркого пояса: x = shift(y) в хранении (X = ±R)
    dC = np.abs(wrap(coords[:, None] - shC[None, :], W))   # ось холодного: y = shift(x)
    dA = np.sqrt(wrap(coords[None, :], W) ** 2 + wrap(coords[:, None], W) ** 2)  # до Изнанки (0,0)
    return X, d, dH, dC, dA


def calibrate(h, m, c, hot, cold, k, dH):
    q = np.quantile
    T = dict(h_hi=q(h, 1 - 0.135), c_pocket=q(c, 0.90), h_karst=q(h, 1 - 0.25), c_karst=q(c, 0.60),
             h_lake=q(h, 0.07), m_lake=q(m, 0.45), h_swamp=q(h, 0.25), m_swamp=q(m, 0.58),
             c_cross=q(c, 0.5))
    temp = ~hot & ~cold
    rest = temp & (h < T['h_karst']) & ~((h < T['h_lake']) & (m >= T['m_lake'])) & ~((h < T['h_swamp']) & (m >= T['m_swamp']))
    T['m_forest'] = q(m[rest], 1 - 17 / 37) if rest.any() else 0.5
    hz = hot & ~cold & (dH >= 60 * k) & (h < T['h_karst'])
    T['m_jungle'] = q(m[hz], 7 / 13) if hz.any() else 0.5
    return T


def classify(W, h, m, c, dH, dC, dA, T, full=True):
    k = W / 4096.0
    M = h.shape[0]
    if full:
        hot = dH < 300 * k; cold = dC < 250 * k
    else:
        hot = np.zeros_like(h, bool); cold = np.zeros_like(h, bool)
    cross = hot & cold
    b = np.full((M, M), -1, np.int16)
    un = np.ones((M, M), bool)

    def put(mask, val):
        mm = mask & un
        b[mm] = val
        un[mm] = False
    put(cross & (dA < 64), GATE)
    put(cross & (h >= T['h_hi']), MOUNT)
    put(cross & (c >= T['c_cross']), LAVA)
    put(cross, GLACIER)
    put((h >= T['h_hi']) & (c >= T['c_pocket']), CAVE)
    put(h >= T['h_hi'], MOUNT)
    put((h >= T['h_karst']) & (c >= T['c_karst']), CAVE)
    put(hot & (dH < 60 * k), LAVA)
    put(hot & (m >= T['m_jungle']), JUNGLE)
    put(hot, DESERT)
    put(cold & (dC < 100 * k), GLACIER)
    put(cold, TUNDRA)
    put((h < T['h_lake']) & (m >= T['m_lake']), LAKE)
    put((h < T['h_swamp']) & (m >= T['m_swamp']), SWAMP)
    put(m >= T['m_forest'], FOREST)
    put(un, MEADOW)
    return b, hot, cold


def shares(b):
    n = b.size
    cnt = np.bincount(b.ravel().astype(np.int64), minlength=len(NAMES))
    return {i: 100.0 * cnt[i] / n for i in range(len(NAMES))}


# ---------------------------------------------------------------- компоненты на торе
def label(b, mask=None):
    """4-связные компоненты одинакового значения на торе. mask — какие ячейки учитывать."""
    M = b.shape[0]
    fl = b.ravel().tolist()
    ok = [True] * (M * M) if mask is None else mask.ravel().tolist()
    lab = [-1] * (M * M)
    comps = []
    for s in range(M * M):
        if lab[s] != -1 or not ok[s]:
            continue
        cl = fl[s]; cid = len(comps)
        lab[s] = cid; st = [s]; mem = [s]
        while st:
            u = st.pop()
            x = u % M; y = u // M
            for v in (y * M + (x + 1) % M, y * M + (x - 1) % M, ((y + 1) % M) * M + x, ((y - 1) % M) * M + x):
                if lab[v] == -1 and ok[v] and fl[v] == cl:
                    lab[v] = cid; st.append(v); mem.append(v)
        comps.append((cl, mem))
    return np.array(lab, dtype=np.int64).reshape(M, M), comps


def tdist(ax, ay, bx, by, W):
    dx = np.abs(ax - bx); dx = np.minimum(dx, W - dx)
    dy = np.abs(ay - by); dy = np.minimum(dy, W - dy)
    return np.sqrt(dx * dx + dy * dy)


def comp_metrics(mem, M, W):
    """Диаметр двойным проходом (нижняя оценка, точна для выпуклых) и признак «замкнута вокруг планеты»."""
    idx = np.array(mem)
    xs = (idx % M) * CELL + CELL / 2.0
    ys = (idx // M) * CELL + CELL / 2.0
    d0 = tdist(xs, ys, xs[0], ys[0], W)
    f = int(np.argmax(d0))
    d1 = tdist(xs, ys, xs[f], ys[f], W)
    diam = float(d1.max()) + CELL
    wraps = (len(np.unique(idx % M)) == M) or (len(np.unique(idx // M)) == M)
    return diam, wraps


def inradius_map(b):
    """Расстояние (в ячейках) от ячейки до ближайшей ячейки другого класса; шаги 1 и √2."""
    INF = 1e9
    d = np.full(b.shape, INF)
    edge = np.zeros(b.shape, bool)
    for sh, ax in ((1, 0), (-1, 0), (1, 1), (-1, 1)):
        edge |= np.roll(b, sh, ax) != b
    d[edge] = 0.5
    steps = [((1, 0), 1.0), ((-1, 0), 1.0), ((0, 1), 1.0), ((0, -1), 1.0),
             ((1, 1), 1.4142), ((1, -1), 1.4142), ((-1, 1), 1.4142), ((-1, -1), 1.4142)]
    same = [np.roll(b, s, (0, 1)) == b for s, _ in steps]
    while True:
        old = d.copy()
        for (s, cost), sm in zip(steps, same):
            nd = np.roll(d, s, (0, 1)) + cost
            upd = sm & (nd < d)
            d[upd] = nd[upd]
        if np.array_equal(d, old):
            break
    return d


# ---------------------------------------------------------------- штампы
def place_polis(b, W, rng, r_lo, r_hi, side, tries=200, prefer=(MEADOW, FOREST)):
    M = b.shape[0]
    best = None
    s = max(1, int(round(side / CELL)))
    for _ in range(tries):
        th = rng.uniform(0, 2 * math.pi); r = rng.uniform(r_lo, r_hi)
        cx = (W / 2 + r * math.cos(th)) % W; cy = (W / 2 + r * math.sin(th)) % W
        i0 = int(cx // CELL) - s // 2; j0 = int(cy // CELL) - s // 2
        ii = (np.arange(s) + i0) % M; jj = (np.arange(s) + j0) % M
        blk = b[np.ix_(jj, ii)]
        score = np.isin(blk, prefer).mean()
        if best is None or score > best[0]:
            best = (score, ii, jj, cx, cy, r)
    score, ii, jj, cx, cy, r = best
    return ii, jj, cx, cy, r, score


def near_biome_min_d(b, d, cls):
    m = b == cls
    return float(d[m].min()) if m.any() else float('inf')


def disc_mask(M, W, cx, cy, rad):
    co = np.arange(M) * CELL + CELL / 2.0
    dd = tdist(co[None, :], co[:, None], cx, cy, W)
    return dd <= rad


# ---------------------------------------------------------------- один мир
def one_world(W, seed, full=True, calib=True, polis_scaled=True, do_regions=True, do_kotl=True,
              do_dungeons=True, guarantee_r=1024.0):
    M = W // CELL; k = W / 4096.0; Rr = W / 2.0
    coords, h, m, c, shH, shC = fields(W, seed)
    X, d, dH, dC, dA = geometry(W, coords, shH, shC)
    hot0 = (dH < 300 * k) if full else np.zeros_like(h, bool)
    cold0 = (dC < 250 * k) if full else np.zeros_like(h, bool)
    T = calibrate(h, m, c, hot0, cold0, k, dH) if calib else RAW
    b, hot, cold = classify(W, h, m, c, dH, dC, dA, T, full)
    out = dict(W=W, seed=seed, T=T, hotzone=hot)
    rng = np.random.default_rng([seed, 99])
    # этап 8 (упрощённо): луга O 300 у Очага
    b[(d <= 150) & (b != LAKE)] = MEADOW
    # этап 5: полисы
    polis = []
    if full and W >= 2048:
        sc = k if polis_scaled else 1.0
        ii, jj, cx, cy, r, sco = place_polis(b, W, rng, 700 * sc, 1000 * sc, 450 * sc)
        b[np.ix_(jj, ii)] = CITY
        polis.append(dict(cx=cx, cy=cy, r=r, side=450 * sc, main=True))
        n_small = 0 if W == 2048 else (1 if W == 4096 else 3)
        for _ in range(n_small):
            ii, jj, cx, cy, r, sco = place_polis(b, W, rng, Rr / 2 + 200 * k, Rr * 1.3, 300 * sc)
            b[np.ix_(jj, ii)] = CITY
            polis.append(dict(cx=cx, cy=cy, r=r, side=300 * sc, main=False))
    out['shares'] = shares(b)
    out['b'] = b
    out['d'] = d
    # гарантия «каждый биом акта I в d <= 1024» — до форпостов
    act1 = ACT1 if full else [MEADOW, FOREST, MOUNT, CAVE]
    mind = {cl: near_biome_min_d(b, d, cl) for cl in act1}
    out['mind'] = mind
    missing = [cl for cl in act1 if mind[cl] > guarantee_r]
    out['missing'] = missing
    # этап 4: котловина
    kot = np.zeros_like(b, bool)
    if do_kotl and full and W >= 2048:
        kot, kinfo = kotlovina(b, h, d, W)
        out['kot'] = kinfo
    out['kotmask'] = kot
    # этап 7: форпосты (для города форпоста нет)
    outposts, fails = [], []
    for cl in missing:
        if cl == CITY:
            fails.append(cl); continue
        ok = False
        for _ in range(3000):
            th = rng.uniform(0, 2 * math.pi); r = rng.uniform(500, 1000)
            cx = (W / 2 + r * math.cos(th)) % W; cy = (W / 2 + r * math.sin(th)) % W
            if any(tdist(cx, cy, o[1], o[2], W) < 400 for o in outposts):      # край к краю >= 200 при O 200
                continue
            if any(abs(wrap(cx - p['cx'], W)) < p['side'] / 2 + 100 and abs(wrap(cy - p['cy'], W)) < p['side'] / 2 + 100 for p in polis):
                continue
            ci = int(cx // CELL) % M; cj = int(cy // CELL) % M
            if b[cj, ci] == LAKE:
                continue
            outposts.append((cl, cx, cy)); ok = True
            break
        if not ok:
            fails.append(cl)
    out['outposts'] = outposts
    out['outpost_fail'] = fails
    for cl, cx, cy in outposts:
        b[disc_mask(M, W, cx, cy, 100)] = cl
    out['shares_after'] = shares(b)
    # этап 9: данжи (после форпостов, как в документе)
    if do_dungeons and full and W == 4096:
        out['dung'] = dungeons(b, d, kot, W, rng, polis, outposts)
    if do_regions:
        out['regions'] = regions(b, W)
    return out


def kotlovina(b, h, d, W):
    M = b.shape[0]; Rr = W / 2.0
    low = (h < np.quantile(h, 0.20)) & (d >= Rr / 2 + CELL) & ~np.isin(b, [GATE, CITY])
    lab, comps = label(np.where(low, 1, 0).astype(np.int16), mask=low)
    if not comps:
        return np.zeros_like(b, bool), dict(ok=False)
    big = max(comps, key=lambda t: len(t[1]))[1]
    nat = len(big) / b.size
    kot = np.zeros(M * M, bool); kot[big] = True
    allowed = ((d >= Rr / 2 + CELL) & ~np.isin(b, [GATE])).ravel()
    hf = h.ravel()
    target = int(0.12 * b.size)
    heap = []
    inq = kot.copy()

    def nbs(u):
        x = u % M; y = u // M
        return (y * M + (x + 1) % M, y * M + (x - 1) % M, ((y + 1) % M) * M + x, ((y - 1) % M) * M + x)
    for u in big:
        for v in nbs(u):
            if not inq[v] and allowed[v]:
                inq[v] = True; heapq.heappush(heap, (hf[v], v))
    count = len(big)
    while count < target and heap:
        _, u = heapq.heappop(heap)
        kot[u] = True; count += 1
        for v in nbs(u):
            if not inq[v] and allowed[v]:
                inq[v] = True; heapq.heappush(heap, (hf[v], v))
    kot = kot.reshape(M, M)
    comp = Counter(b[kot].tolist())
    n = kot.sum()
    co = np.arange(M) * CELL + CELL / 2.0 - W / 2.0
    qx = np.broadcast_to(co[None, :] > 0, (M, M)); qy = np.broadcast_to(co[:, None] > 0, (M, M))
    qid = (qx * 1 + qy * 2)[kot]
    qshare = np.bincount(qid, minlength=4).max() / n
    ir = inradius_map(np.where(kot, 1, 0).astype(np.int16))
    inr = float(ir[kot].max()) * CELL
    land = ~kot & (b != LAKE)
    _, comps2 = label(np.where(land, 1, 0).astype(np.int16), mask=land)
    lsz = sorted((len(cm[1]) for cm in comps2), reverse=True)
    land_main = lsz[0] / land.sum() if lsz else 0
    info = dict(ok=True, natural=nat, share=n / b.size,
                comp={NAMES[i]: 100.0 * comp.get(i, 0) / b.size for i in range(len(NAMES))},
                quad=qshare, inradius=inr, land_main=land_main, reached=(count >= target))
    return kot, info


def dungeons(b, d, kot, W, rng, polis, outposts):
    M = b.shape[0]
    # «у озера» — озеро вне котловины в 80 тайлах; «на берегу котловины» — котловина в 32 тайлах (мягкое прочтение)
    lake = (b == LAKE) & ~kot
    near_lake = np.zeros_like(lake)
    for dx in range(-10, 11):
        for dy in range(-10, 11):
            if dx * dx + dy * dy <= 100:
                near_lake |= np.roll(np.roll(lake, dx, 1), dy, 0)
    near_kot = np.zeros_like(kot)
    for dx in range(-4, 5):
        for dy in range(-4, 5):
            near_kot |= np.roll(np.roll(kot, dx, 1), dy, 0)
    bandits = ['Перифет', 'Синис', 'Керкион', 'Скирон']
    pick = list(rng.choice(bandits, 2, replace=False))
    spec = [('Прокруст', {MEADOW, FOREST}, (400, 900), 48, None),
            ('Перифет', {MEADOW}, (700, 1300), 40, None),
            ('Синис', {FOREST}, (900, 1500), 64, None),
            ('Керкион', {MEADOW, DESERT}, (1000, 1800), 40, None),
            ('Скирон', {MOUNT}, (1000, 1800), 56, 'kot_edge'),
            ('Лабиринт', {DESERT}, (1100, 1900), 96, 'fallback_meadow'),
            ('Гнездо', {SWAMP}, (1200, 2000), 64, None),
            ('Грот', {CAVE, MOUNT}, (1200, 2047), 8, 'near_lake')]
    placed = []
    if polis:
        p = polis[0]; placed.append(('Храм', p['cx'], p['cy'], 48))
    for o in outposts:
        placed.append(('форпост', o[1], o[2], -1))
    res = {}
    for name, biomes, (lo, hi), size, flag in spec:
        if name in bandits and name not in pick:
            continue
        mask = np.isin(b, list(biomes)) & (d >= lo) & (d <= hi)
        mask = mask & (near_kot & ~kot) if flag == 'kot_edge' else mask & ~kot
        if flag == 'near_lake':
            mask &= near_lake
        status = 'нет места'
        for attempt in range(2):
            idx = np.flatnonzero(mask)
            rng.shuffle(idx)
            done = False
            for u in idx[:4000]:
                cx = (u % M) * CELL + 4.0; cy = (u // M) * CELL + 4.0
                okk = True
                for pn, px, py, ps in placed:
                    need = (100 + 200 + size / 2) if ps < 0 else (300 + (size + ps) / 2)
                    if tdist(cx, cy, px, py, W) < need:
                        okk = False; break
                if okk:
                    placed.append((name, cx, cy, size)); done = True; break
            if done:
                status = 'да' if attempt == 0 else 'да, на лугах'
                break
            if flag == 'fallback_meadow':
                mask = (b == MEADOW) & (d >= lo) & (d <= hi) & ~kot
            else:
                break
        res[name] = status
    return res


def regions(b, W):
    M = b.shape[0]
    lab, comps = label(b)
    ir = inradius_map(b).ravel()
    per = defaultdict(list)
    for cl, mem in comps:
        if len(mem) < 2:
            continue
        diam, wraps = comp_metrics(mem, M, W)
        inr = float(ir[np.array(mem)].max()) * CELL
        per[cl].append((diam, wraps, inr, len(mem)))
    return per


# ---------------------------------------------------------------- интересное (этап 10)
def poi_grid(W, step, jit, seed, exact=False):
    rng = np.random.default_rng([seed, 7, int(step)])
    if exact:
        n = int(round(W / step)); st = W / n
        cx = (np.arange(n) + 0.5) * st
        P = [(x + rng.uniform(-jit, jit), y + rng.uniform(-jit, jit)) for x in cx for y in cx]
    else:
        n = int(math.ceil(W / step))
        edges = [min(i * step, W) for i in range(n + 1)]
        P = []
        for i in range(n):
            for j in range(n):
                x0, x1 = edges[i], edges[i + 1]; y0, y1 = edges[j], edges[j + 1]
                P.append(((x0 + x1) / 2 + rng.uniform(-jit, jit), (y0 + y1) / 2 + rng.uniform(-jit, jit)))
    P = np.array(P) % W
    g = np.arange(0, W, 8) + 4.0
    gx, gy = np.meshgrid(g, g)
    gx = gx.ravel(); gy = gy.ravel()
    best = np.full(gx.size, 1e9)
    for px, py in P:
        np.minimum(best, tdist(gx, gy, px, py, W), out=best)
    return float(best.max()), float((best > 360).mean()), len(P)


def poi_polis(W, seed, step=300, jit=100, side=450):
    """Клетки сетки, чей центр под полисом 450 x 450, POI не получают; «до полиса» — до центра или до края."""
    rng = np.random.default_rng([seed, 8])
    n = int(math.ceil(W / step)); edges = [min(i * step, W) for i in range(n + 1)]
    th = rng.uniform(0, 2 * math.pi); r = rng.uniform(700, 1000)
    pcx = (W / 2 + r * math.cos(th)) % W; pcy = (W / 2 + r * math.sin(th)) % W
    P = []
    for i in range(n):
        for j in range(n):
            cx = (edges[i] + edges[i + 1]) / 2; cy = (edges[j] + edges[j + 1]) / 2
            x = cx + rng.uniform(-jit, jit); y = cy + rng.uniform(-jit, jit)
            if abs(wrap(cx - pcx, W)) < side / 2 and abs(wrap(cy - pcy, W)) < side / 2:
                continue
            P.append((x % W, y % W))
    P = np.array(P)
    g = np.arange(0, W, 8) + 4.0
    gx, gy = np.meshgrid(g, g); gx = gx.ravel(); gy = gy.ravel()
    best = np.full(gx.size, 1e9)
    for px, py in P:
        np.minimum(best, tdist(gx, gy, px, py, W), out=best)
    dxp = np.abs(wrap(gx - pcx, W)); dyp = np.abs(wrap(gy - pcy, W))
    inside = (dxp <= side / 2) & (dyp <= side / 2)
    to_center = np.sqrt(dxp ** 2 + dyp ** 2)
    to_edge = np.sqrt(np.maximum(dxp - side / 2, 0) ** 2 + np.maximum(dyp - side / 2, 0) ** 2)
    a = np.minimum(best, to_center)[~inside]
    e = np.minimum(best, to_edge)
    return float(a.max()), float((a > 360).mean()), float(e.max())


# ---------------------------------------------------------------- преображения (4096)
def transforms(res, W, seed):
    b = res['b']; d = res['d']; kot = res['kotmask']; M = b.shape[0]; k = W / 4096.0; Rr = W / 2.0
    rng = np.random.default_rng([seed, 55])
    out = {}
    cands = np.flatnonzero((b == MOUNT) & (d >= Rr / 2) & res['hotzone'])
    rng.shuffle(cands)
    centers = []
    for u in cands:
        cx = (u % M) * CELL + 4.0; cy = (u // M) * CELL + 4.0
        if all(tdist(cx, cy, a, bb, W) >= 940 * k for a, bb in centers):
            centers.append((cx, cy))
        if len(centers) == 2:
            break
    if len(centers) == 2:
        mk = disc_mask(M, W, *centers[0], 470 * k) | disc_mask(M, W, *centers[1], 470 * k)
        comp = Counter(b[mk].tolist())
        out['mech'] = {NAMES[i]: 100.0 * comp.get(i, 0) / b.size for i in range(len(NAMES))}
        out['mech_kot'] = 100.0 * (mk & kot).sum() / b.size
    cand = np.flatnonzero(np.isin(b, [FOREST, JUNGLE]) & (d >= Rr / 2) & (d < Rr) & ~kot)
    rng.shuffle(cand)
    cs = []
    for u in cand:
        cx = (u % M) * CELL + 4.0; cy = (u // M) * CELL + 4.0
        if all(tdist(cx, cy, a, bb, W) >= 800 * k for a, bb in cs):
            cs.append((cx, cy))
        if len(cs) == 2:
            break
    if len(cs) == 2:
        mk = disc_mask(M, W, *cs[0], 400 * k) | disc_mask(M, W, *cs[1], 400 * k)
        tr = mk & np.isin(b, [FOREST, JUNGLE, MEADOW]) & ~kot
        out['magic'] = 100.0 * tr.sum() / b.size
    return out


# ---------------------------------------------------------------- картинка
COL = {MEADOW: (150, 200, 90), FOREST: (40, 110, 50), JUNGLE: (20, 150, 90), SWAMP: (110, 100, 60),
       MOUNT: (130, 130, 130), CAVE: (60, 50, 70), DESERT: (230, 205, 120), CITY: (235, 235, 235),
       TUNDRA: (200, 215, 225), GLACIER: (150, 200, 245), LAVA: (210, 60, 20), LAKE: (60, 110, 200),
       GATE: (90, 90, 90)}


def png(res, path):
    from PIL import Image, ImageDraw
    b = res['b']; M = b.shape[0]; W = res['W']
    img = np.zeros((M, M, 3), np.uint8)
    for cl, col in COL.items():
        img[b == cl] = col
    kot = res['kotmask']
    if kot.any():
        img[kot] = (img[kot] * 0.55 + np.array([20, 40, 120]) * 0.45).astype(np.uint8)
    im = Image.fromarray(img).resize((M * 2, M * 2), Image.NEAREST)
    dr = ImageDraw.Draw(im)
    s = 2 / CELL
    c0 = W / 2 * s
    for r in (W / 16, W / 4, W / 2):
        dr.ellipse([c0 - r * s, c0 - r * s, c0 + r * s, c0 + r * s], outline=(0, 0, 0))
    dr.ellipse([c0 - 1024 * s, c0 - 1024 * s, c0 + 1024 * s, c0 + 1024 * s], outline=(255, 0, 255))
    for cl, cx, cy in res['outposts']:
        dr.ellipse([cx * s - 100 * s, cy * s - 100 * s, cx * s + 100 * s, cy * s + 100 * s], outline=(255, 0, 255), width=2)
    im.save(path)


# ---------------------------------------------------------------- отчёт
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--s4096', type=int, default=40)
    ap.add_argument('--s2048', type=int, default=30)
    ap.add_argument('--s8192', type=int, default=4)
    ap.add_argument('--s1024', type=int, default=200)
    ap.add_argument('--png', action='store_true')
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    t0 = time.time()
    P = print
    P('# Модель генератора 01_world — результат\n')
    P(f'Зёрна: 4096² — {a.s4096}, 2048² — {a.s2048}, 8192² — {a.s8192}, срез 1024² — {a.s1024}. Ячейка макрокарты 8 × 8.\n')

    # --- A. пороги документа как есть
    P('## A. Пороги таблицы биома «как написано»\n')
    acc = defaultdict(list)
    for s in range(1, 11):
        r = one_world(4096, s, calib=False, do_regions=False, do_kotl=False, do_dungeons=False)
        for i, v in r['shares'].items():
            acc[i].append(v)
    P('| Биом | Цель, % | 4096², пороги документа: среднее по 10 зёрнам (мин–макс), % |')
    P('|---|---|---|')
    for i in [MEADOW, FOREST, MOUNT, CAVE, SWAMP, LAKE, JUNGLE, DESERT, TUNDRA, GLACIER, LAVA, CITY]:
        v = acc[i]
        P(f'| {NAMES[i]} | {TARGET.get(i, "-")} | {np.mean(v):.1f} ({min(v):.1f}–{max(v):.1f}) |')
    P('')
    mt, cv, sw = [], [], []
    for s in range(1, a.s1024 + 1):
        r = one_world(1024, s, full=False, calib=False, do_regions=False, do_kotl=False, do_dungeons=False)
        sh = r['shares']
        mt.append(sh[MOUNT]); cv.append(sh[CAVE]); sw.append(sh[SWAMP])
    mt = np.array(mt); cv = np.array(cv); sw = np.array(sw)
    P(f'**Срез 1024², набор «срез», пороги документа ({a.s1024} зёрен):** горы — медиана {np.median(mt):.1f} %, '
      f'5–95 перцентили {np.percentile(mt, 5):.1f}–{np.percentile(mt, 95):.1f} %; зёрен без гор: {100 * (mt == 0).mean():.0f} %; '
      f'без пещер: {100 * (cv == 0).mean():.0f} %; болота (в срезе их быть не должно) есть в {100 * (sw > 0).mean():.0f} % зёрен, '
      f'медиана {np.median(sw):.1f} %. Октава h 1024 на мире 1024 — один узел решётки: константа, сдвигающая h всего мира.\n')
    mt2, all4, d4 = [], 0, []
    for s in range(1, 41):
        r = one_world(1024, s, full=False, calib=True, do_regions=False, do_kotl=False, do_dungeons=False)
        mt2.append(r['shares'][MOUNT])
        all4 += all(np.isfinite(r['mind'][cl]) for cl in (MEADOW, FOREST, MOUNT, CAVE))
        d4.append(max(r['mind'][cl] for cl in (MEADOW, FOREST, MOUNT, CAVE)))
    P(f'С порогами-квантилями (горы — верхние 13,5 % h) 40 зёрен 1024²: горы {np.min(mt2):.1f}–{np.max(mt2):.1f} %; '
      f'все 4 биома среза есть в {all4} из 40; самый дальний из четырёх — до {max(d4):.0f} тайлов от Очага '
      f'(наибольшее расстояние на 1024² — 724).\n')

    # --- B. 4096², калиброванные пороги
    P('## B. 4096², пороги подобраны квантилями под таблицу (лучшее, что может сделать разработчик)\n')
    sh_acc = defaultdict(list); sh_after = defaultdict(list)
    miss_cnt = Counter(); nmiss = []; fails = Counter()
    reg = defaultdict(lambda: defaultdict(list))
    kot_rows = []; dung = defaultdict(Counter); tr_rows = []
    keep = None
    for s in range(1, a.s4096 + 1):
        r = one_world(4096, s, calib=True)
        for i, v in r['shares'].items():
            sh_acc[i].append(v)
        for i, v in r['shares_after'].items():
            sh_after[i].append(v)
        nmiss.append(len([x for x in r['missing'] if x != CITY]))
        for x in r['missing']:
            miss_cnt[x] += 1
        for x in r['outpost_fail']:
            fails[x] += 1
        for cl, lst in r['regions'].items():
            big = [t for t in lst if t[0] >= 64]
            reg[cl]['n'].append(len(big))
            reg[cl]['gt'].append(sum(1 for t in big if t[0] > 1080))
            reg[cl]['wrap'].append(sum(1 for t in big if t[1]))
            reg[cl]['maxd'].append(max((t[0] for t in big), default=0))
            reg[cl]['inr'].append(max((t[2] for t in big), default=0))
        if r.get('kot', {}).get('ok'):
            kot_rows.append(r['kot'])
        for nm, st in r.get('dung', {}).items():
            dung[nm][st] += 1
        tr_rows.append(transforms(r, 4096, s))
        if s == 1:
            keep = r
    P('| Биом | Цель, % | До форпостов, % | После форпостов, % | Областей Ø ≥ 64 (медиана) | В документе | Областей Ø > 1080 на мир (медиана) | Наиб. Ø (медиана) | Наиб. «до края» (медиана) | Замкнутых вокруг планеты (среднее) |')
    P('|---|---|---|---|---|---|---|---|---|---|')
    for i in [MEADOW, FOREST, MOUNT, CAVE, SWAMP, LAKE, JUNGLE, DESERT, TUNDRA, GLACIER, LAVA, CITY]:
        rg = reg[i]
        doc = DOC_REGIONS.get(i)
        med = lambda key: (np.median(rg[key]) if rg[key] else 0)
        P(f'| {NAMES[i]} | {TARGET.get(i, "-")} | {np.mean(sh_acc[i]):.1f} | {np.mean(sh_after[i]):.1f} | '
          f'{med("n"):.0f} | {f"{doc[0]}–{doc[1]}" if doc else "-"} | {med("gt"):.0f} | {med("maxd"):.0f} | '
          f'{med("inr"):.0f} | {(np.mean(rg["wrap"]) if rg["wrap"] else 0):.1f} |')
    belt = sum(np.mean(sh_acc[i]) for i in [JUNGLE, DESERT, TUNDRA, GLACIER, LAVA])
    P(f'\nПять биомов поясов вместе: {belt:.1f} % при цели 28 %. Геометрия: жаркий пояс 600/4096 = 14,6 %, холодный 500/4096 = 12,2 %, '
      f'объединение 25,1 % площади, горы и пещеры внутри поясов забирают свою долю.\n')
    P(f'**Каждый биом акта I в d ≤ 1024 до форпостов, 4096²:** форпостов на мир — медиана {np.median(nmiss):.0f}, '
      f'мин {min(nmiss)}, макс {max(nmiss)}. Доля зёрен, где биома нет ближе 1024: ' +
      ', '.join(f'{NAMES[i]} {100 * miss_cnt[i] / a.s4096:.0f} %' for i in ACT1 if miss_cnt[i]) +
      f'. Форпост не встал (500–1000, разнос 200): {sum(fails.values())} раз на {a.s4096} миров' +
      (f' ({", ".join(NAMES[i] + " " + str(n) for i, n in fails.items())})' if fails else '') + '.\n')
    if kot_rows:
        P('**Котловина (этап 4), 4096²:**\n')
        nat = np.array([r['natural'] for r in kot_rows]) * 100
        quad = np.array([r['quad'] for r in kot_rows]) * 100
        inr = np.array([r['inradius'] for r in kot_rows])
        lm = np.array([r['land_main'] for r in kot_rows]) * 100
        P(f'- самая большая связная низина (нижние 20 % h) в кольцах 2–3 до роста: медиана {np.median(nat):.1f} % площади ({np.min(nat):.1f}–{np.max(nat):.1f}); рост до 12 % дошёл в {sum(r["reached"] for r in kot_rows)} из {len(kot_rows)};')
        P(f'- доля котловины в одном квадранте: медиана {np.median(quad):.0f} % (мин {np.min(quad):.0f} %); ≥ 90 % в одном квадранте — {100 * np.mean(quad >= 90):.0f} % зёрен;')
        P(f'- наибольшее расстояние от воды котловины до суши: медиана {np.median(inr):.0f} тайлов (документ: острова не реже чем каждые 400, то есть ≤ 200);')
        P(f'- суша без котловины и озёр: главная компонента — {np.median(lm):.1f} % суши (медиана).')
        keys = ['болота', 'луга', 'лес', 'джунгли', 'озеро', 'пустыня', 'тундра', 'ледник', 'лавовый', 'горы', 'пещеры', 'город']
        P('\n| Уходит под океан, % площади мира | ' + ' | '.join(keys) + ' |')
        P('|---|' + '---|' * len(keys))
        P('| модель, среднее | ' + ' | '.join(f'{np.mean([r["comp"][kk] for r in kot_rows]):.1f}' for kk in keys) + ' |')
        P('| таблица документа (акт II) | 4 | 4 | 2 | 1 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |')
        P('')
    if dung:
        P('**Данжи акта I (этап 9 после форпостов), 4096²:** ' + '; '.join(
            f'{nm}: ' + ', '.join(f'{st} {n}' for st, n in cnt.items()) for nm, cnt in dung.items()) + '.\n')
    mech = [t['mech'] for t in tr_rows if 'mech' in t]
    if mech:
        keys = ['город', 'горы', 'лавовый', 'пустыня', 'луга', 'джунгли', 'лес', 'болота', 'пещеры', 'тундра', 'ледник', 'озеро']
        P('**Механикус: два круга r = 470 в вулканическом хребте, 4096²** (без полисов; % площади мира):\n')
        P('| ' + ' | '.join(keys) + ' | всего |')
        P('|' + '---|' * (len(keys) + 1))
        P('| ' + ' | '.join(f'{np.mean([mm[kk] for mm in mech]):.2f}' for kk in keys) + f' | {np.mean([sum(mm.values()) for mm in mech]):.1f} |')
        P(f'\nТаблица документа ждёт от кругов: горы 3, лава 1, пустыня 2, луга 2; джунгли, лес, болота — 0. '
          f'Кругов, задевших котловину: в {sum(1 for t in tr_rows if t.get("mech_kot", 0) > 0)} мирах из {len(tr_rows)}.\n')
    mg = [t['magic'] for t in tr_rows if 'magic' in t]
    if mg:
        P(f'**Волшебный лес, два очага r = 400 в лесу/джунглях кольца 2:** преображается {np.mean(mg):.1f} % мира (мин {np.min(mg):.1f}, макс {np.max(mg):.1f}); документ: 4 %.\n')

    # --- C. другие размеры
    P('## C. Гарантия «11 биомов в 1024 от Очага» на других размерах\n')
    for W, n in ((2048, a.s2048), (8192, a.s8192)):
        for scaled in ((True, False) if W == 8192 else (True,)):
            nm = []; mc = Counter(); city_far = 0
            for s in range(1, n + 1):
                r = one_world(W, s, calib=True, polis_scaled=scaled, do_regions=False, do_kotl=False, do_dungeons=False)
                nm.append(len([x for x in r['missing'] if x != CITY]))
                for x in r['missing']:
                    mc[x] += 1
                city_far += CITY in r['missing']
            lbl = '' if W == 2048 else (' (полис как R: 1400–2000, 900 × 900)' if scaled else ' (полис не масштабирован: 700–1000, 450 × 450)')
            P(f'- {W}²{lbl}: форпостов на мир — медиана {np.median(nm):.0f} (мин {min(nm)}, макс {max(nm)}); '
              f'город дальше 1024 — {100 * city_far / n:.0f} % зёрен (форпоста для города нет); '
              + ', '.join(f'{NAMES[i]} {100 * v / n:.0f} %' for i, v in mc.items() if i != CITY) + '.')
    P('')

    # --- D. интересное
    P('## D. Интересное: сетка этапа 10\n')
    P('| Мир | Сетка | POI | Наиб. расстояние до ближайшего POI (худшее из 20 зёрен), тайлов | Доля точек дальше 360 |')
    P('|---|---|---|---|---|')
    for W in (1024, 2048, 4096):
        for step, jit, exact, lbl in ((300, 100, False, '300 ±100, крайний столбец уже'),
                                       (W / round(W / 300), 100, True, f'{W / round(W / 300):.1f} ±100 (W / {round(W / 300)})'),
                                       (256, 64, True, '256 ±64')):
            worst = 0; frac = 0; npo = 0
            for s in range(1, 21):
                mx, fr, npo = poi_grid(W, step, jit, s, exact)
                worst = max(worst, mx); frac = max(frac, fr)
            P(f'| {W}² | {lbl} | {npo} | {worst:.0f} | {100 * frac:.2f} % |')
    wc, wf, we = 0, 0, 0
    for s in range(1, 21):
        c_, f_, e_ = poi_polis(4096, s)
        wc = max(wc, c_); wf = max(wf, f_); we = max(we, e_)
    P(f'\nПолис 450 × 450 занимает клетки сетки (POI там нет): если «до полиса» мерить до центра — худшее {wc:.0f} тайлов, '
      f'доля точек дальше 360 — до {100 * wf:.1f} %; если до края — {we:.0f}.\n')
    P(f'Время прогона модели: {time.time() - t0:.0f} с (Python, не генератор).')
    if a.png and keep is not None:
        pth = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'map_4096_seed1.png')
        png(keep, pth)
        P(f'\nКартинка зерна 1 (4096², макрокарта ×2; кольца 256/1024/2048 — чёрные, радиус гарантии 1024 — пурпурный, '
          f'форпосты — пурпурные круги, котловина затемнена синим): {pth}')


if __name__ == '__main__':
    main()
