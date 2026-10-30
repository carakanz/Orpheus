#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Модель генератора 01_world после правок [3] Дизайнера (28.10) — копия-надстройка над моделью Критика
worldgen_model.py (берёт из неё шум, разметку компонент на торе, метрики и полисы). Изменено то, что поменял документ:
  - период октавы = min(ячейка, W/2) (правка 1б);
  - биомы — доли площади (квантили по ключу), а не абсолютные пороги (1а); у набора «срез» своя таблица из 4 строк (1в);
  - биомы поясов — тоже доли: полосы набираются от оси наружу (ядро, затем края), зона 1200 / 1000 — только предел ширины; ключ полосы — расстояние до оси + шум края ±80;
    Перекрёсток — 600 × 500; форпостов поясов нет; гарантия — ближайший тайл каждого пояса ≤ 2000 от Очага;
  - котловина — только умеренная зона и влажная сторона жаркого пояса, d >= R/2 + 64; острова-штампы, пока «до суши» > 200 (6);
  - данжи ставятся до форпостов; штамп Грота приносит горное озеро, штамп Скирона — обрыв над водой (1д): им нужны только горы/пещеры;
  - мера темпа — «от точки области до её края <= 540» вместо диаметра (3);
  - механикус — список преображаемых биомов; волшебный лес — два очага и роща r = 120 у 40 закрытых порталов (2, 6).

Запуск (Python 3.12 + numpy; pillow — для --png):
  python C:\\Users\\carak\\source\\Orpheus\\design_room\\threads\\01_world\\worldgen_model_v2.py --png > C:\\Users\\carak\\source\\Orpheus\\design_room\\threads\\01_world\\result_v2.md
"""
import argparse, heapq, math, os, sys, time
from collections import Counter, defaultdict
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import worldgen_model as B
from worldgen_model import (CELL, NAMES, MEADOW, FOREST, JUNGLE, SWAMP, MOUNT, CAVE, DESERT, CITY,
                            TUNDRA, GLACIER, LAVA, LAKE, GATE, ACT1)

HOT_W, COLD_W = 1200, 1000                    # предел ширины зоны пояса на 4096²; полосы набираются долями от оси
EDGE = 160                                    # шум края полосы: ключ = расстояние до оси + EDGE × (c − 0,5); в модели шумом служит поле c
SHARE = {CAVE: 6, MOUNT: 12, LAVA: 3, JUNGLE: 6, DESERT: 7, GLACIER: 5, TUNDRA: 7, LAKE: 3, SWAMP: 6, FOREST: 17}
SLICE_SHARE = {CAVE: 8, MOUNT: 14, FOREST: 33}  # луга — остаток
TEMPERATE = [MEADOW, FOREST, MOUNT, CAVE, SWAMP, CITY]
BELT = [JUNGLE, DESERT, LAVA, TUNDRA, GLACIER]
MAGIC, OCEAN, MECH = 13, 14, 15
NM = NAMES + ['волшебный лес', 'океан', 'механикус']


def tol_ok(share, target):
    return abs(share - target) <= max(1.0, 0.25 * target)


# ---------------------------------------------------------------- поля (правка 1б)
def fields2(W, seed):
    M = W // CELL
    coords = np.arange(M) * CELL + CELL / 2.0
    R = lambda field, o: np.random.default_rng([seed, field, o])
    eff = lambda cell: min(cell, W / 2)
    h = np.zeros((M, M)); ws = 0.0
    for o, cell in enumerate([1024, 512, 256, 128]):
        w = 0.5 ** o
        v = B.octave2d(W, eff(cell), R(1, o), coords)
        h += w * (1.0 - np.abs(2.0 * v - 1.0)) ** 2
        ws += w
    h /= ws

    def smooth(field, cells):
        a = np.zeros((M, M)); s = 0.0
        for o, cell in enumerate(cells):
            w = 0.5 ** o
            a += w * B.octave2d(W, eff(cell), R(field, o), coords)
            s += w
        return a / s
    m = smooth(2, [512, 256, 128])
    c = smooth(3, [256, 128, 64])
    k = W / 4096.0

    def shift(field):
        s = np.zeros(M); t = 0.0
        for o, cell in enumerate([2048 * k, 1024 * k]):
            w = 0.5 ** o
            s += w * B.octave1d(W, cell, R(field, o), coords)
            t += w
        s = 2.0 * s / t - 1.0
        mx = np.abs(s).max()
        return (s / mx if mx > 1e-9 else s) * 350.0 * k
    return coords, h, m, c, shift(4), shift(5)


# ---------------------------------------------------------------- этап 3: доли (правка 1а, 1в)
def classify2(W, h, m, c, dH, dC, dA, full=True):
    k = W / 4096.0
    M = h.shape[0]; N = M * M
    b = np.full((M, M), -1, np.int16)
    un = np.ones((M, M), bool)
    bf = b.ravel(); uf = un.ravel()

    def take(allowed, key, n, largest):
        idx = np.flatnonzero((un & allowed).ravel())
        n = int(min(max(n, 0), idx.size))
        if n == 0:
            return idx[:0]
        kv = key.ravel()[idx]
        part = np.argpartition(-kv if largest else kv, n - 1)[:n]
        return idx[part]

    def assign(sel, val):
        bf[sel] = val; uf[sel] = False

    cnt = lambda val: int((bf == val).sum())
    T = SHARE if full else SLICE_SHARE
    if full:
        hot = dH < (HOT_W / 2) * k; cold = dC < (COLD_W / 2) * k
    else:
        hot = np.zeros_like(h, bool); cold = np.zeros_like(h, bool)
    cross = ((dH < 300 * k) & (dC < 250 * k)) if full else np.zeros_like(h, bool)
    ones = np.ones_like(un)
    if full:
        assign(np.flatnonzero((cross & (dA < 64)).ravel()), GATE)
    assign(take(h >= np.quantile(h, 0.75), c, round(T[CAVE] / 100 * N), True), CAVE)
    assign(take(ones, h, round(T[MOUNT] / 100 * N), True), MOUNT)
    if full:
        cr = un & cross
        if cr.any():
            med = np.median(c[cr])
            assign(np.flatnonzero((cr & (c >= med)).ravel()), LAVA)
            assign(np.flatnonzero((un & cross).ravel()), GLACIER)
        kH = dH + EDGE * k * (c - 0.5); kC = dC + EDGE * k * (c - 0.5)
        assign(take(hot, kH, round(T[LAVA] / 100 * N) - cnt(LAVA), False), LAVA)
        assign(take(cold, kC, round(T[GLACIER] / 100 * N) - cnt(GLACIER), False), GLACIER)
        wh = m >= np.median(m[un & hot])
        assign(take(hot & wh, kH, round(T[JUNGLE] / 100 * N), False), JUNGLE)
        assign(take(hot & ~wh, kH, round(T[DESERT] / 100 * N), False), DESERT)
        assign(take(cold, kC, round(T[TUNDRA] / 100 * N), False), TUNDRA)
        wet = m >= np.median(m[un])
        assign(take(wet, h, round(T[LAKE] / 100 * N), False), LAKE)
        assign(take(wet, h, round(T[SWAMP] / 100 * N), False), SWAMP)
    assign(take(ones, m, round(T[FOREST] / 100 * N), True), FOREST)
    assign(np.flatnonzero(un.ravel()), MEADOW)
    return b, hot, cold


# ---------------------------------------------------------------- котловина (правка 6)
def kotlovina2(b, h, d, W, rng):
    M = b.shape[0]; Rr = W / 2.0
    allowed = np.isin(b, [MEADOW, FOREST, SWAMP, LAKE, JUNGLE]) & (d >= Rr / 2 + 64)
    low = allowed & (h < np.quantile(h[allowed], 0.25))
    lab, comps = B.label(np.where(low, 1, 0).astype(np.int16), mask=low)
    al = allowed.ravel(); hf = h.ravel()
    target = int(0.12 * M * M)

    def nbs(u):
        x = u % M; y = u // M
        return (y * M + (x + 1) % M, y * M + (x - 1) % M, ((y + 1) % M) * M + x, ((y - 1) % M) * M + x)

    def grow(seed_cells):
        kk = np.zeros(M * M, bool); kk[seed_cells] = True
        inq = kk.copy(); heap = []
        for u in seed_cells:
            for v in nbs(u):
                if not inq[v] and al[v]:
                    inq[v] = True; heapq.heappush(heap, (hf[v], v))
        count = len(seed_cells)
        while count < target and heap:
            _, u = heapq.heappop(heap)
            kk[u] = True; count += 1
            for v in nbs(u):
                if not inq[v] and al[v]:
                    inq[v] = True; heapq.heappush(heap, (hf[v], v))
        return kk, count
    best = None; tries = 0
    for _, mem in sorted(comps, key=lambda t: -len(t[1]))[:12]:   # затравки по убыванию размера: первая, что дорастает до доли
        tries += 1
        kk, cnt_ = grow(mem)
        if best is None or cnt_ > best[1]:
            best = (kk, cnt_)
        if cnt_ >= target:
            break
    kot = best[0].reshape(M, M)
    comp = Counter(b[kot].tolist())
    before = 100.0 * kot.sum() / kot.size
    # острова-штампы: первый — холм «Храм на холме» r = 30, остальные r = 12–24, пока до суши > 200
    ir = B.inradius_map(np.where(kot, 1, 0).astype(np.int16))
    dist = np.where(kot, ir * CELL, 0.0)
    max0 = float(dist.max())
    co = np.arange(M) * CELL + CELL / 2.0
    islands = []
    while dist.max() > 200 and len(islands) < 400:
        u = int(np.argmax(dist)); cy, cx = divmod(u, M)
        px = cx * CELL + 4.0; py = cy * CELL + 4.0
        rad = 30.0 if not islands else float(rng.uniform(12, 24))
        islands.append((px, py, rad))
        dd = B.tdist(co[None, :], co[:, None], px, py, W)
        kot &= ~(dd <= rad)
        dist = np.where(kot, np.minimum(dist, np.maximum(dd - rad, 0.0)), 0.0)
    land = ~kot & (b != LAKE)
    _, comps2 = B.label(np.where(land, 1, 0).astype(np.int16), mask=land)
    lsz = sorted((len(cm[1]) for cm in comps2), reverse=True)
    _, kc = B.label(np.where(kot, 1, 0).astype(np.int16), mask=kot)
    ksz = sorted((len(cm[1]) for cm in kc), reverse=True)
    info = dict(share=before, tries=tries, comp={i: 100.0 * comp.get(i, 0) / b.size for i in range(len(NAMES))},
                max_before=max0, max_after=float(dist.max()), islands=len(islands),
                land_main=(lsz[0] / land.sum() if lsz else 0), kot_main=(ksz[0] / kot.sum() if ksz else 0),
                hill=islands[0] if islands else None)
    return kot, info


# ---------------------------------------------------------------- данжи и входы (правки 1д, 10)
def dungeons2(b, d, kot, W, rng, polis, hot):
    M = b.shape[0]; k = W / 4096.0; Rr = W / 2.0
    sep = max(150.0, 300.0 * k)
    bandits = ['Перифет', 'Синис', 'Керкион', 'Скирон']
    nb = 2 if W == 4096 else (1 if W == 2048 else 4)
    pick = list(rng.choice(bandits, nb, replace=False))
    near_kot = np.zeros_like(kot)
    for dx in range(-3, 4):
        for dy in range(-3, 4):
            near_kot |= np.roll(np.roll(kot, dx, 1), dy, 0)
    shore = near_kot & ~kot & (b != LAKE)
    S = lambda lo, hi: (lo * k, hi * k)
    spec = [('Прокруст', np.isin(b, [MEADOW, FOREST]), S(400, 900), 48),
            ('Перифет', b == MEADOW, S(600, 1000), 40),
            ('Синис', b == FOREST, S(1024, 1500), 64),
            ('Керкион', np.isin(b, [MEADOW, DESERT]), S(1024, 1800), 40),
            ('Скирон', b == MOUNT, S(1024, 1800), 64),
            ('Лабиринт', b == DESERT, S(1100, 1900), 96),
            ('Гнездо', b == SWAMP, S(1200, 2000), 64),
            ('Грот', np.isin(b, [CAVE, MOUNT]), S(1200, 2047), 32),
            ('Древний дуб', b == FOREST, S(1024, 2047), 8),
            ('Круг камней', b == MEADOW, S(1024, 2047), 8),
            ('Жерло', (b == MOUNT) & hot, S(1024, 2896), 8),
            ('Старый маяк', shore, S(1024, 2896), 8)]
    placed = []
    for p in polis:
        if p['main']:
            placed.append(('Храм Горгоны', p['cx'], p['cy'], 48))
    res = {}
    for name, base_mask, (lo, hi), size in spec:
        if name in bandits and name not in pick:
            continue
        mask = base_mask & (d >= lo) & (d <= hi)
        if name != 'Старый маяк':
            mask &= ~kot
        status = 'нет места'
        for attempt in range(2):
            idx = np.flatnonzero(mask)
            rng.shuffle(idx)
            done = False
            for u in idx[:6000]:
                cx = (u % M) * CELL + 4.0; cy = (u // M) * CELL + 4.0
                if all(B.tdist(cx, cy, px, py, W) >= sep + (size + ps) / 2 for _, px, py, ps in placed):
                    placed.append((name, cx, cy, size)); done = True; break
            if done:
                status = 'да' if attempt == 0 else 'да, запасной биом'
                break
            if name == 'Лабиринт':
                mask = (b == MEADOW) & (d >= lo) & (d <= hi) & ~kot
            elif name == 'Жерло':    # запасной биом — лава жаркого пояса
                mask = (b == LAVA) & (d >= lo) & (d <= hi)
            elif name == 'Гнездо':   # штамп приносит свою топь 64 × 64
                mask = np.isin(b, [MEADOW, FOREST]) & (d >= lo) & (d <= hi) & ~kot
            else:
                break
        res[name] = status
    return res, placed


# ---------------------------------------------------------------- преображения (правки 2, 6)
def transforms2(b, d, kot, polis, hot, W, seed):
    M = b.shape[0]; Rr = W / 2.0
    rng = np.random.default_rng([seed, 56])
    co = np.arange(M) * CELL + CELL / 2.0
    out = {}
    a2 = b.copy()
    a2[kot] = OCEAN
    # волшебный лес: два очага r = 400 в лесу/джунглях кольца 2 + 40 порталов r = 120
    cand = np.flatnonzero(np.isin(a2, [FOREST, JUNGLE]) & (d >= Rr / 2) & (d < Rr))
    rng.shuffle(cand)
    cs = []
    for u in cand:
        cx = (u % M) * CELL + 4.0; cy = (u // M) * CELL + 4.0
        if all(B.tdist(cx, cy, x, y, W) >= 800 for x, y in cs):
            cs.append((cx, cy))
        if len(cs) == 2:
            break
    conv = np.isin(a2, [FOREST, JUNGLE, MEADOW])
    core = np.zeros_like(kot)
    for cx, cy in cs:
        core |= B.tdist(co[None, :], co[:, None], cx, cy, W) <= 400
    out['magic_core'] = 100.0 * (core & conv).sum() / b.size
    land = np.flatnonzero(~np.isin(a2, [OCEAN, LAKE, LAVA]) & (d >= 128))
    port = np.zeros_like(kot)
    for u in rng.choice(land, 40, replace=False):
        cx = (u % M) * CELL + 4.0; cy = (u // M) * CELL + 4.0
        port |= B.tdist(co[None, :], co[:, None], cx, cy, W) <= 120
    mg = (core | port) & conv
    out['magic_40'] = 100.0 * mg.sum() / b.size
    out['magic_comp'] = Counter(a2[mg].tolist())
    a2[mg] = MAGIC
    out['act2'] = a2.copy()
    # механикус: оба полиса + два круга в вулканическом хребте; преображаются только горы, пещеры, лава, пустыня, луга
    cands = np.flatnonzero((a2 == MOUNT) & (d >= Rr / 2) & hot)
    rng.shuffle(cands)
    centers = []
    for u in cands:
        cx = (u % M) * CELL + 4.0; cy = (u // M) * CELL + 4.0
        if all(B.tdist(cx, cy, x, y, W) >= 1080 for x, y in centers):
            centers.append((cx, cy))
        if len(centers) == 2:
            break
    lst = np.isin(a2, [MOUNT, CAVE, LAVA, DESERT, MEADOW])
    dmin = np.full(a2.shape, 1e9)
    for cx, cy in centers:
        dmin = np.minimum(dmin, B.tdist(co[None, :], co[:, None], cx, cy, W))
    city = a2 == CITY
    need = int(0.09 * a2.size) - int(city.sum())            # механикус — 9 % мира вместе с полисами
    idx = np.flatnonzero(lst.ravel())
    order = idx[np.argsort(dmin.ravel()[idx], kind='stable')][:max(need, 0)]
    mech = city.copy().ravel(); mech[order] = True; mech = mech.reshape(a2.shape)
    out['mech_9'] = 100.0 * mech.sum() / b.size
    out['mech_r'] = float(dmin.ravel()[order[-1]]) if order.size else 0.0
    out['mech_comp_9'] = Counter(a2[mech].tolist())
    a3 = a2.copy(); a3[mech] = MECH
    out['act3'] = a3
    return out


# ---------------------------------------------------------------- один мир
def one_world2(W, seed, full=True, do_kot=True, do_dung=True, do_regions=True, do_tr=True):
    M = W // CELL; k = W / 4096.0; Rr = W / 2.0
    coords, h, m, c, shH, shC = fields2(W, seed)
    X, d, dH, dC, dA = B.geometry(W, coords, shH, shC)
    b, hot, cold = classify2(W, h, m, c, dH, dC, dA, full)
    rng = np.random.default_rng([seed, 99])
    b[(d <= 150) & (b != LAKE)] = MEADOW                       # этап 8: луга Ø 300 у Очага
    polis = []
    if full and W >= 2048:                                     # главный полис не масштабируется (правка 10)
        ii, jj, cx, cy, r, sco = B.place_polis(b, W, rng, 700, 1000, 450)
        b[np.ix_(jj, ii)] = CITY
        polis.append(dict(cx=cx, cy=cy, r=r, side=450, main=True))
        for _ in range(0 if W == 2048 else (1 if W == 4096 else 3)):
            ii, jj, cx, cy, r, sco = B.place_polis(b, W, rng, Rr / 2 + 200 * k, Rr * 1.3, 300 * k)
            b[np.ix_(jj, ii)] = CITY
            polis.append(dict(cx=cx, cy=cy, r=r, side=300 * k, main=False))
    out = dict(W=W, seed=seed, b=b, d=d, hot=hot, polis=polis)
    out['shares'] = B.shares(b)
    cls = (TEMPERATE + BELT) if full else [MEADOW, FOREST, MOUNT, CAVE]
    out['mind'] = {cl: B.near_biome_min_d(b, d, cl) for cl in cls}
    if full:
        beltd = {}
        for nm_, zone in (('жаркий', np.isin(b, [JUNGLE, DESERT, LAVA])), ('холодный', np.isin(b, [TUNDRA, GLACIER]))):
            beltd[nm_] = float(d[zone].min()) if zone.any() else float('inf')
        out['beltd'] = beltd
    kot = np.zeros_like(b, bool)
    if full and W >= 2048 and do_kot:
        kot, out['kot'] = kotlovina2(b, h, d, W, rng)
    out['kotmask'] = kot
    out['outposts'] = [cl for cl in TEMPERATE if out['mind'].get(cl, 0) > 1024] if full else []
    if full and W in (2048, 4096) and do_dung:
        out['dung'], out['placed'] = dungeons2(b, d, kot, W, rng, polis, hot)
    if do_regions:
        out['regions'] = B.regions(b, W)
    if full and W == 4096 and do_tr:
        out['tr'] = transforms2(b, d, kot, polis, hot, W, seed)
    return out


# ---------------------------------------------------------------- картинка
def png2(res, path):
    from PIL import Image, ImageDraw
    b = res['b']; M = b.shape[0]; W = res['W']
    img = np.zeros((M, M, 3), np.uint8)
    for cl, col in B.COL.items():
        img[b == cl] = col
    kot = res['kotmask']
    if kot.any():
        img[kot] = (img[kot] * 0.45 + np.array([20, 40, 140]) * 0.55).astype(np.uint8)
    im = Image.fromarray(img).resize((M * 2, M * 2), Image.NEAREST)
    dr = ImageDraw.Draw(im)
    s = 2 / CELL; c0 = W / 2 * s
    for r in (W / 16, W / 4, W / 2):
        dr.ellipse([c0 - r * s, c0 - r * s, c0 + r * s, c0 + r * s], outline=(0, 0, 0))
    for name, cx, cy, size in res.get('placed', []):
        rr = max(size / 2, 24) * s
        dr.ellipse([cx * s - rr, cy * s - rr, cx * s + rr, cy * s + rr], fill=(0, 0, 0), outline=(255, 255, 255))
    im.save(path)


# ---------------------------------------------------------------- отчёт
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--s4096', type=int, default=30)
    ap.add_argument('--s2048', type=int, default=10)
    ap.add_argument('--s8192', type=int, default=2)
    ap.add_argument('--s1024', type=int, default=100)
    ap.add_argument('--png', action='store_true')
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding='utf-8')
    t0 = time.time(); P = print
    P('# Модель генератора 01_world после правок [3] — результат\n')
    P(f'Зёрна: 4096² — {a.s4096}, 2048² — {a.s2048}, 8192² — {a.s8192}, срез 1024² — {a.s1024}. '
      f'Пояса {HOT_W} и {COLD_W} (на 4096²), биомы — доли площади, период октавы min(ячейка, W/2).\n')

    # --- срез
    P('## A. Срез 1024², набор «срез» (4 строки: пещеры 8, горы 14, лес 33, луга — остаток)\n')
    allp = 0; far = []; shm = defaultdict(list)
    for s in range(1, a.s1024 + 1):
        r = one_world2(1024, s, full=False, do_regions=False)
        ok = all(np.isfinite(r['mind'][cl]) for cl in (MEADOW, FOREST, MOUNT, CAVE))
        allp += ok
        far.append(max(r['mind'][cl] for cl in (MEADOW, FOREST, MOUNT, CAVE)))
        for i in (MEADOW, FOREST, MOUNT, CAVE, SWAMP, LAKE):
            shm[i].append(r['shares'][i])
    P(f'Все 4 биома есть в {allp} из {a.s1024} зёрен; самый дальний из четырёх от Очага — медиана {np.median(far):.0f}, '
      f'худший {max(far):.0f} тайлов (3 мин бега = 1080). Доли, среднее: ' +
      ', '.join(f'{NM[i]} {np.mean(shm[i]):.1f} %' for i in (MEADOW, FOREST, MOUNT, CAVE, SWAMP, LAKE)) + '.\n')

    # --- 4096
    P('## B. 4096², полный набор\n')
    sh = defaultdict(list); inr = defaultdict(list); mind = defaultdict(list); belt = defaultdict(list)
    outp = []; kots = []; dung = defaultdict(Counter); trs = []; keep = None
    for s in range(1, a.s4096 + 1):
        r = one_world2(4096, s)
        for i, v in r['shares'].items():
            sh[i].append(v)
        for cl, lst in r['regions'].items():
            inr[cl].append(max((t[2] for t in lst), default=0))
        for cl, v in r['mind'].items():
            mind[cl].append(v)
        for nm_, v in r['beltd'].items():
            belt[nm_].append(v)
        outp.append(len(r['outposts']))
        if 'kot' in r:
            kots.append(r['kot'])
        for nm_, st in r.get('dung', {}).items():
            dung[nm_][st] += 1
        if 'tr' in r:
            trs.append(r['tr'])
        if s == 1:
            keep = r
    P('| Биом | Цель, % | Модель, % (мин–макс) | Допуск ±25 %, не меньше 1 п. п. | Ближайшая область от Очага: медиана / худший | Наиб. «до края» по зёрнам: медиана / худший |')
    P('|---|---|---|---|---|---|')
    tgt = dict(SHARE); tgt[CITY] = 2
    for i in [MEADOW, FOREST, MOUNT, CAVE, SWAMP, LAKE, JUNGLE, DESERT, TUNDRA, GLACIER, LAVA, CITY]:
        v = sh[i]; t = tgt.get(i)
        okc = ('—' if t is None else ('да' if all(tol_ok(x, t) for x in v) else f'нет в {sum(not tol_ok(x, t) for x in v)} зёрнах'))
        md = mind.get(i, [])
        P(f'| {NM[i]} | {t if t is not None else "остаток"} | {np.mean(v):.1f} ({min(v):.1f}–{max(v):.1f}) | {okc} | '
          f'{(f"{np.median(md):.0f} / {max(md):.0f}" if md else "—")} | {np.median(inr[i]):.0f} / {max(inr[i]):.0f} |')
    P(f'\nКрай пояса от Очага: жаркий — медиана {np.median(belt["жаркий"]):.0f}, худший {max(belt["жаркий"]):.0f}; '
      f'холодный — медиана {np.median(belt["холодный"]):.0f}, худший {max(belt["холодный"]):.0f} (гарантия ≤ 2000). '
      f'Форпостов умеренных биомов понадобилось: {sum(outp)} на {a.s4096} миров.\n')
    if kots:
        g = lambda key: np.array([x[key] for x in kots])
        P('**Котловина:** доля до островов — минимум {:.1f} %; до суши до островов — медиана {:.0f}, после — худший {:.0f} тайлов '
          '(нужно ≤ 200); островов — медиана {:.0f} (мин {:.0f}, макс {:.0f}); котловина — одна область: {:.1f} % воды в главной компоненте (худший); '
          'суша вне котловины и озёр — главная компонента {:.1f} % суши (худший).'.format(
              g('share').min(), np.median(g('max_before')), g('max_after').max(), np.median(g('islands')),
              g('islands').min(), g('islands').max(), 100 * g('kot_main').min(), 100 * g('land_main').min()))
        keys = [SWAMP, MEADOW, FOREST, JUNGLE, LAKE, DESERT, TUNDRA, GLACIER, LAVA, MOUNT, CAVE, CITY]
        P('\n| Уходит под океан, % мира | ' + ' | '.join(NM[i] for i in keys) + ' |')
        P('|---|' + '---|' * len(keys))
        P('| среднее по зёрнам | ' + ' | '.join(f'{np.mean([x["comp"][i] for x in kots]):.1f}' for i in keys) + ' |\n')
    if dung:
        P('**Данжи и входы, 4096²** (разнос ≥ 300; ни один, кроме Маяка, не в котловине): ' +
          '; '.join(f'{nm_}: ' + ', '.join(f'{st} {n}' for st, n in cnt.items()) for nm_, cnt in dung.items()) + '.\n')
    if trs:
        mc = np.array([t['magic_core'] for t in trs]); m40 = np.array([t['magic_40'] for t in trs])
        P(f'**Волшебный лес:** очаги — {np.mean(mc):.1f} % мира ({mc.min():.1f}–{mc.max():.1f}); очаги + 40 порталов r = 120 — '
          f'{np.mean(m40):.1f} % ({m40.min():.1f}–{m40.max():.1f}).')
        v = np.array([t['mech_9'] for t in trs]); rr = np.array([t['mech_r'] for t in trs])
        comp = defaultdict(list)
        for t in trs:
            for i in (CITY, MOUNT, CAVE, LAVA, DESERT, MEADOW):
                comp[i].append(100.0 * t['mech_comp_9'].get(i, 0) / (512 * 512))
        P(f'**Механикус долей 9 %** (оба полиса + горы, пещеры, лава, пустыня, луга от двух очагов наружу): {np.mean(v):.1f} % мира '
          f'({v.min():.1f}–{v.max():.1f}); дальняя ячейка от очага — медиана {np.median(rr):.0f}, худший {rr.max():.0f} тайлов; состав: '
          + ', '.join(f'{NM[i]} {np.mean(comp[i]):.1f}' for i in comp) + '.')
        # таблица долей по актам
        acc = defaultdict(lambda: [[], [], []])
        for t, ss in zip(trs, range(1, len(trs) + 1)):
            for ai, arr in enumerate((None, t['act2'], t['act3'])):
                if arr is None:
                    continue
                cn = np.bincount(arr.ravel().astype(np.int64), minlength=len(NM))
                for i in range(len(NM)):
                    acc[i][ai].append(100.0 * cn[i] / arr.size)
        for i in range(len(NM)):
            acc[i][0] = sh.get(i, [0])
        P('\n| Биом | Акт I | Акт II | Акт III |')
        P('|---|---|---|---|')
        for i in [MEADOW, FOREST, MOUNT, CAVE, SWAMP, JUNGLE, DESERT, TUNDRA, GLACIER, LAVA, CITY, LAKE, GATE, MAGIC, OCEAN, MECH]:
            row = [np.mean(acc[i][j]) if acc[i][j] else 0.0 for j in range(3)]
            P(f'| {NM[i]} | {row[0]:.1f} | {row[1]:.1f} | {row[2]:.1f} |')
        P('')

    # --- 2048 и 8192
    P('## C. Другие размеры\n')
    for W, n in ((2048, a.s2048), (8192, a.s8192)):
        miss = Counter(); bd = defaultdict(list); dn = defaultdict(Counter)
        for s in range(1, n + 1):
            r = one_world2(W, s, do_kot=(W == 2048), do_regions=False, do_tr=False, do_dung=(W == 2048))
            for cl in r['outposts']:
                miss[cl] += 1
            for nm_, v in r['beltd'].items():
                bd[nm_].append(v)
            for nm_, st in r.get('dung', {}).items():
                dn[nm_][st] += 1
        P(f'- {W}²: умеренный биом или город дальше 1024 — ' + (', '.join(f'{NM[i]} в {v} из {n}' for i, v in miss.items()) or f'ни разу из {n}') +
          f'; край жаркого пояса — медиана {np.median(bd["жаркий"]):.0f} (худший {max(bd["жаркий"]):.0f}), холодного — '
          f'{np.median(bd["холодный"]):.0f} (худший {max(bd["холодный"]):.0f})' +
          ('; данжи: ' + '; '.join(f'{nm_} — ' + ', '.join(f'{st} {c_}' for st, c_ in cnt.items()) for nm_, cnt in dn.items()) if dn else '') + '.')
    P(f'\nВремя прогона: {time.time() - t0:.0f} с.')
    if a.png and keep is not None:
        pth = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'map_4096_seed1_v2.png')
        png2(keep, pth)
        P(f'\nКарта зерна 1 (4096², макрокарта ×2; кольца 256/1024/2048 — чёрные; котловина затемнена синим; данжи и входы — чёрные точки): {pth}')


if __name__ == '__main__':
    main()
