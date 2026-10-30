#!/usr/bin/env python3
"""Модель бюджетов сервера и сети к docs/07_multiplayer_tech.md (Дизайнер, смена 28.10).

Запуск (Python 3.12 + numpy; модель мира 01 подключается сама из соседней папки):
    python C:\\Users\\carak\\source\\Orpheus\\design_room\\threads\\07_multiplayer_tech\\net_budget.py > C:\\Users\\carak\\source\\Orpheus\\design_room\\threads\\07_multiplayer_tech\\net_budget.md
    python net_budget.py --budget 900 --cap 96 --slow 3     # другие прикидки

Что считает:
  1. Размер чанка 32 x 32 по сети и в сохранении: синтетические тайлы по макрокарте модели 01
     (worldgen_model_v2.py, зерно 1, 4096²) и плотностям объектов из карточек биомов 01, сжатие zlib (Deflate).
  2. Макрокарта и маска разведки при подключении: только разведанные ячейки (решение 07), 3 байта на ячейку у клиента.
  3. Вход в мир, подгрузка чанков на бегу, вход в отдельный данж, Разлив Стикса.
  4. Снимки и трафик на клиента, исходящий трафик сервера и хоста для 1/4/8/16/32 игроков: врозь, вместе ночью, худший случай.
  5. Процессор сервера на такт 30 Гц: замеры net_bench (A*, шаг движения, Deflate) + прикидки на логику.
  6. Память сервера по размерам мира и числу игроков; размер сохранения.
  7. Какой лимит вступает первым.

Откуда числа:
  00 5.1: тайл, чанк 32, мир 1024/2048/4096/8192, бег 6 т/с, зум до x0,6 (67 x 37,5 тайла на экране).
  01: окно подгрузки 6 x 4 чанка; макрокарта 512² x 6 байт; ячейка разведки 8 x 8; отдельная карта <= 32 чанка;
      Разлив: фронт 1 тайл за 2 с, 2000 чанков за 10 мин; плотности объектов — карточки биомов (раздел 3).
  02: лимит врагов на экран день 5 / ночь 8, группа x(1 + 0,5(n - 1)) x плотность (0,8-3,0); бюджет союзных 64;
      подручных до 6; снаряды лука 28 т/с на 30 т, пулемёта 10 выстр./с 60 т/с на 35 т, стрелка врага 10 т/с на 16 т,
      раз в 2 с; ИИ 10 Гц; пересчёт пути раз в 0,5 с, не больше 40 поисков за такт; интерполяция 100 мс.
  04: волны событий считают N не больше 8; призванные боссом x(1 + 0,25(N - 1)).
  05: спутников в походе 5/9/12 по актам, 13 в эпилоге; спутников у персонажа до 2.
  net_bench/result.md: A* 10 / 18 / 104 мкс (поле / пещера / цель в коробке), шаг движения 0,03 мкс, Deflate 6-21 мкс.
Всё остальное — параметры с пометкой «прикидка» в --help.
"""
import argparse
import math
import os
import sys
import zlib

import numpy as np

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "01_world"))

P = argparse.ArgumentParser(description="Бюджеты сервера и сети, 07_multiplayer_tech")
P.add_argument("--seed", type=int, default=1, help="зерно мира модели 01")
P.add_argument("--W", type=int, default=4096, help="размер мира для замеров чанков и макрокарты")
P.add_argument("--samples", type=int, default=600, help="сколько случайных чанков сжать")
P.add_argument("--tick", type=float, default=30.0, help="такт симуляции, Гц (решение 07, подтверждает 02)")
P.add_argument("--snap", type=float, default=30.0, help="частота снимков, Гц (решение 07)")
P.add_argument("--inp", type=float, default=60.0, help="частота кадров ввода, Гц (02)")
P.add_argument("--budget", type=int, default=1200, help="полезная нагрузка снимка, байт: без дробления пакета UDP (решение 07)")
P.add_argument("--delta", type=float, default=6.0, help="байт на движущуюся сущность в дельте (прикидка: индекс 1,5 + маска 1 + позиция 2 + прочее 1,5)")
P.add_argument("--full", type=float, default=26.0, help="байт на сущность, входящую в обзор (прикидка)")
P.add_argument("--overhead", type=float, default=32.0, help="заголовки IPv4 + UDP + LiteNetLib на пакет, байт")
P.add_argument("--cap", type=int, default=128, help="технический потолок обычных врагов на группу, в весе (решение 07, -> 02)")
P.add_argument("--density", type=float, default=1.2, help="типичная плотность биома (01: лес 1,2)")
P.add_argument("--density-max", type=float, default=3.0, help="наибольшая плотность (01: Предвратие ночью)")
P.add_argument("--entity-us", type=float, default=2.0, help="мкс на сущность за такт со всей логикой (прикидка: замер движения 0,03 x 60)")
P.add_argument("--ai-us", type=float, default=15.0, help="мкс на решение ИИ, 10 Гц (прикидка)")
P.add_argument("--player-us", type=float, default=5.0, help="мкс на кадр ввода персонажа (прикидка)")
P.add_argument("--astar", type=str, default="10.1,18.0,104.3", help="мкс на поиск: поле, пещера, отказ (net_bench)")
P.add_argument("--astar-mix", type=str, default="0.6,0.3,0.1", help="доли поисков: поле, пещера, отказ (прикидка)")
P.add_argument("--slow", type=float, default=2.0, help="во сколько раз эталонный хост медленнее машины бенча (прикидка)")
P.add_argument("--slow-laptop", type=float, default=3.0, help="то же для старого ноутбука-сервера (прикидка)")
P.add_argument("--path-ms", type=float, default=3.0, help="потолок времени на поиск пути за такт, мс на работающей машине (решение 07)")
P.add_argument("--bulk", type=float, default=256.0, help="КБ/с на вход в мир — потолок, чтобы не душить остальных (решение 07)")
P.add_argument("--mg", action="store_true", help="в худшем случае все стреляют из пулемёта (02 4.1)")
A = P.parse_args()

TICK, SNAP, INP = A.tick, A.snap, A.inp
AST = [float(x) for x in A.astar.split(",")]
AMIX = [float(x) for x in A.astar_mix.split(",")]
PATH_US = sum(a * b for a, b in zip(AST, AMIX))


def kb(x):
    return x / 1024.0


def fmt(x, nd=1):
    return f"{x:.{nd}f}"


# ------------------------------------------------------------------ 1. мир модели 01
try:
    import worldgen_model_v2 as M
    from worldgen_model import (MEADOW, FOREST, JUNGLE, SWAMP, MOUNT, CAVE, DESERT, CITY, TUNDRA, GLACIER,
                                LAVA, LAKE, GATE, NAMES)
    res = M.one_world2(A.W, A.seed, full=True, do_kot=True, do_dung=False, do_regions=False, do_tr=False)
    BIOME = res["b"].astype(np.int16)
    KOT = res["kotmask"]
    _, H, MM, CC, _, _ = M.fields2(A.W, A.seed)
    SOURCE = f"модель 01 (worldgen_model_v2.py), зерно {A.seed}, {A.W}²"
except Exception as e:  # модель 01 недоступна — синтетика
    MEADOW, FOREST, JUNGLE, SWAMP, MOUNT, CAVE, DESERT, CITY, TUNDRA, GLACIER, LAVA, LAKE, GATE = range(13)
    NAMES = ["луга", "лес", "джунгли", "болота", "горы", "пещеры", "пустыня", "город", "тундра", "ледник",
             "лавовый", "озёра", "Предвратие"]
    rng0 = np.random.default_rng(A.seed)
    MC = A.W // 8
    BIOME = (rng0.random((MC // 16, MC // 16)) * 12).astype(np.int16).repeat(16, 0).repeat(16, 1)
    KOT = np.zeros_like(BIOME, bool)
    H = MM = CC = rng0.random((MC, MC))
    SOURCE = f"синтетика (модель 01 не подключилась: {e})"

MCELLS = BIOME.shape[0]
CH = 32


def vnoise(xs, ys, cell, seed):
    """Value-шум по мировым координатам тайлов (numpy-массивы), 0..1."""
    cx, cy = np.floor_divide(xs, cell), np.floor_divide(ys, cell)
    fx, fy = (xs % cell) / cell, (ys % cell) / cell

    def h(a, b):
        v = (a.astype(np.int64) * 374761393 + b.astype(np.int64) * 668265263 + seed * 1442695041) & 0xFFFFFFFF
        v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
        v ^= v >> 16
        return (v & 0xFFFFFF) / float(0x1000000)

    sx, sy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    a, b, c, d = h(cx, cy), h(cx + 1, cy), h(cx, cy + 1), h(cx + 1, cy + 1)
    return (a * (1 - sx) + b * sx) * (1 - sy) + (c * (1 - sx) + d * sx) * sy


# пол (u8) и объекты (u16) — коды условные, важна только статистика для сжатия
GRASS, NEEDLES, MOSS, MUD, SHALLOW, DEEP, STONE, WETSTONE, SAND, PLATES, SNOW, ICE, CHASM, ASH, LAVAF, ASPH, QUICK = range(1, 18)
# объекты на биом: (код, плотность на 1000 тайлов, ширина, высота)
OBJ = {
    MEADOW: [(100, 8, 1, 1), (120, 6, 2, 2), (130, 10, 1, 1), (140, 12, 1, 1), (141, 1, 1, 1), (150, 40, 1, 1)],
    FOREST: [(101, 70, 1, 1), (102, 50, 1, 1), (120, 8, 2, 2), (140, 15, 1, 1), (121, 4, 1, 3), (141, 2, 1, 1)],
    JUNGLE: [(103, 100, 1, 1), (151, 20, 1, 1), (142, 8, 1, 1)],
    SWAMP: [(153, 60, 1, 1), (104, 50, 1, 1), (143, 10, 1, 1), (122, 3, 3, 3)],
    MOUNT: [(160, 12, 1, 1), (161, 6, 1, 1), (162, 8, 1, 1), (163, 1.5, 1, 1)],
    CAVE: [(134, 30, 1, 1), (144, 30, 1, 1), (164, 3, 1, 1), (160, 10, 1, 1), (161, 10, 1, 1), (162, 6, 1, 1), (163, 2, 1, 1)],
    DESERT: [(145, 10, 1, 1), (135, 40, 1, 1)],
    CITY: [(137, 10, 1, 1), (124, 2, 2, 2)],
    TUNDRA: [(105, 40, 1, 1), (120, 12, 2, 2), (130, 30, 1, 1), (125, 8, 2, 2)],
    GLACIER: [(139, 5, 1, 1)],
    LAVA: [],
    LAKE: [],
    GATE: [],
}
# скопления: (доля площади, ячейка шума, [(код, доля в скоплении)])
CLUSTER = {
    MOUNT: (0.45, 7, [(131, 0.70), (132, 0.25), (133, 0.05)]),
    CAVE: (0.35, 6, [(131, 0.60), (132, 0.30), (133, 0.10)]),
    GLACIER: (0.30, 5, [(138, 1.0)]),
    LAVA: (0.20, 6, [(133, 1.0)]),
    DESERT: (0.15, 5, [(123, 1.0)]),
}


def make_chunk(cx, cy, kind=None, seed=11):
    """Возвращает (raw_bytes) чанка: пол, младшие и старшие байты объекта плоскостями + записи построек."""
    ys, xs = np.mgrid[0:CH, 0:CH]
    wx, wy = xs + cx * CH, ys + cy * CH
    floor = np.zeros((CH, CH), np.uint8)
    obj = np.zeros((CH, CH), np.uint16)
    built = []  # (индекс, владелец, прочность, флаги, природный пол)
    rng = np.random.default_rng([seed, cx, cy])
    if kind is None:
        # биом тайла: ячейка макрокарты со сдвигом границы деталью +-6 тайлов (01, этап 11)
        jx = np.round((vnoise(wx, wy, 32, 5) - 0.5) * 12).astype(int)
        jy = np.round((vnoise(wx, wy, 32, 6) - 0.5) * 12).astype(int)
        mx = ((wx + jx) // 8) % MCELLS
        my = ((wy + jy) // 8) % MCELLS
        bt = BIOME[my, mx]
    else:
        bt = np.full((CH, CH), kind, np.int16)
    n1 = vnoise(wx, wy, 8, 21)
    n2 = vnoise(wx, wy, 6, 22)
    for b in np.unique(bt):
        m = bt == b
        if b == MEADOW:
            floor[m] = GRASS
        elif b == FOREST:
            floor[m] = np.where(n1[m] > 0.55, NEEDLES, GRASS)
        elif b == JUNGLE:
            floor[m] = MOSS
            under = m & (n2 > 0.45)
            obj[under] = 152
        elif b == SWAMP:
            floor[m] = np.where(n1[m] > 0.6, SHALLOW, MUD)
            floor[m & (n2 > 0.82)] = DEEP
        elif b in (MOUNT, CAVE):
            floor[m] = STONE if b == MOUNT else np.where(n1[m] > 0.6, WETSTONE, STONE)
            if b == CAVE:
                floor[m & (n2 > 0.88)] = SHALLOW
        elif b == DESERT:
            floor[m] = SAND
        elif b == CITY:
            floor[m] = np.where(n1[m] > 0.7, GRASS, PLATES)
        elif b == TUNDRA:
            floor[m] = np.where(n1[m] > 0.8, ICE, SNOW)
        elif b == GLACIER:
            floor[m] = np.where(n1[m] > 0.4, ICE, SNOW)
            floor[m & (n2 > 0.9)] = CHASM
        elif b == LAVA:
            floor[m] = ASH
            floor[m & (n2 > 0.7)] = LAVAF
        elif b == LAKE:
            floor[m] = np.where(n1[m] > 0.35, DEEP, SHALLOW)
        elif b == GATE:
            floor[m] = ASPH
        else:
            floor[m] = GRASS
        if b in CLUSTER:
            share, cell, kinds = CLUSTER[b]
            cn = vnoise(wx, wy, cell, 31 + int(b)) * 0.7 + vnoise(wx, wy, 2, 32) * 0.3
            thr = np.quantile(cn, 1 - share) if m.all() else 1 - share * 0.9
            cl = m & (cn > thr) & (floor != LAVAF) & (floor != SHALLOW) & (floor != CHASM)
            r = rng.random((CH, CH))
            acc = 0.0
            for code, fr in kinds:
                sel = cl & (r >= acc) & (r < acc + fr) & (obj == 0)
                obj[sel] = code
                acc += fr
        if b == CITY:  # стены домов мраморным блоком — четверть площади, сеткой кварталов
            walls = m & (((wx % 12) == 0) | ((wy % 12) == 0)) & (rng.random((CH, CH)) > 0.25)
            obj[walls & (obj == 0)] = 136
        for code, dens, w, h in OBJ.get(int(b), []):
            cnt = rng.poisson(dens / 1000.0 * m.sum())
            for _ in range(int(cnt)):
                x, y = int(rng.integers(0, CH - w + 1)), int(rng.integers(0, CH - h + 1))
                if not m[y, x] or (obj[y:y + h, x:x + w] != 0).any():
                    continue
                obj[y, x] = code
                for dy in range(h):
                    for dx in range(w):
                        if dx or dy:
                            obj[y + dy, x + dx] = 0xF000 | (dy << 4) | dx  # часть объекта: смещение до якоря
    if kind == "base":
        pass
    return floor, obj, built


def make_base_chunk(seed=5):
    """Двор 24 x 16 с забором, три комнаты 5 x 5 из камня, пол, двери, факелы, кровати, сундук, верстак."""
    floor = np.full((CH, CH), GRASS, np.uint8)
    obj = np.zeros((CH, CH), np.uint16)
    built = []
    rng = np.random.default_rng(seed)
    x0, y0 = 4, 8
    for x in range(x0, x0 + 24):
        for y in (y0, y0 + 15):
            obj[y, x] = 200  # забор
    for y in range(y0, y0 + 16):
        for x in (x0, x0 + 23):
            obj[y, x] = 200
    obj[y0 + 15, x0 + 11] = 0  # калитка
    obj[y0 + 15, x0 + 11] = 202
    for r in range(3):
        rx, ry = x0 + 2 + r * 7, y0 + 3
        for x in range(rx, rx + 5):
            for y in range(ry, ry + 5):
                border = x in (rx, rx + 4) or y in (ry, ry + 4)
                if border:
                    obj[y, x] = 201  # стена каменная
                else:
                    floor[y, x] = 30  # построенный пол
        obj[ry + 4, rx + 2] = 202  # дверь
        obj[ry + 1, rx + 1] = 210  # кровать 1 x 2
        obj[ry + 2, rx + 1] = 0xF010
        obj[ry + 1, rx + 3] = 211  # факел
        obj[ry + 3, rx + 3] = 212  # сундук
    obj[y0 + 11, x0 + 4] = 213  # верстак 2 x 2
    obj[y0 + 11, x0 + 5] = 0xF001
    obj[y0 + 12, x0 + 4] = 0xF010
    obj[y0 + 12, x0 + 5] = 0xF011
    for i in range(CH * CH):
        y, x = divmod(i, CH)
        if obj[y, x] >= 200 and obj[y, x] < 0xF000 or floor[y, x] == 30:
            hp = 300 if obj[y, x] == 201 else 45 if obj[y, x] == 200 else 90
            if rng.random() < 0.1:
                hp = int(hp * rng.random())
            built.append((i, int(rng.integers(0, 3)), hp, 0, GRASS))
    return floor, obj, built


def make_dungeon_chunk(cx, cy, seed=7):
    """Коридоры и комнаты отдельной карты: стены данжа ~40 %, плиты, факелы, сундуки."""
    ys, xs = np.mgrid[0:CH, 0:CH]
    wx, wy = xs + cx * CH, ys + cy * CH
    floor = np.full((CH, CH), PLATES, np.uint8)
    obj = np.zeros((CH, CH), np.uint16)
    rng = np.random.default_rng([seed, cx, cy])
    rooms = (vnoise(wx, wy, 10, 41) > 0.45) | ((wx % 16 < 3) | (wy % 16 < 3))
    obj[~rooms] = 170
    lights = rooms & (rng.random((CH, CH)) < 0.006)
    obj[lights] = 171
    chests = rooms & (rng.random((CH, CH)) < 0.001)
    obj[chests] = 172
    return floor, obj, []


def make_ocean_chunk(cx, cy, seed=9):
    ys, xs = np.mgrid[0:CH, 0:CH]
    wx, wy = xs + cx * CH, ys + cy * CH
    n = vnoise(wx, wy, 16, 51)
    floor = np.where(n > 0.8, SHALLOW, DEEP).astype(np.uint8)
    obj = np.zeros((CH, CH), np.uint16)
    rng = np.random.default_rng([seed, cx, cy])
    reef = (floor == SHALLOW) & (rng.random((CH, CH)) < 0.025)
    obj[reef] = 180
    return floor, obj, []


def pack(floor, obj, built, level):
    raw = floor.tobytes() + (obj & 0xFF).astype(np.uint8).tobytes() + (obj >> 8).astype(np.uint8).tobytes()
    rec = bytearray(len(built).to_bytes(2, "little"))
    for i, own, hp, fl, nat in built:
        rec += i.to_bytes(2, "little") + bytes([own]) + int(hp).to_bytes(2, "little") + bytes([fl, nat])
    blob = raw + bytes(rec)
    return len(blob), len(zlib.compress(blob, level))


def stats(xs):
    xs = sorted(xs)
    return dict(mean=sum(xs) / len(xs), p50=xs[len(xs) // 2], p95=xs[int(len(xs) * 0.95)], max=xs[-1])


rng = np.random.default_rng(A.seed)
NCH = A.W // CH
sizes1, sizes6, by_biome = [], [], {}
for _ in range(A.samples):
    cx, cy = int(rng.integers(0, NCH)), int(rng.integers(0, NCH))
    f, o, b = make_chunk(cx, cy)
    rawlen, z1 = pack(f, o, b, 1)
    _, z6 = pack(f, o, b, 6)
    sizes1.append(z1)
    sizes6.append(z6)
    dom = int(np.bincount(BIOME[cy * 4:(cy + 1) * 4, cx * 4:(cx + 1) * 4].ravel()).argmax())
    by_biome.setdefault(dom, []).append(z6)
start = []
c0 = NCH // 2
for dy in range(-2, 2):
    for dx in range(-3, 3):
        f, o, b = make_chunk(c0 + dx, c0 + dy)
        start.append(pack(f, o, b, 6)[1])
fb, ob, bb = make_base_chunk()
base_raw, base1 = pack(fb, ob, bb, 1)
_, base6 = pack(fb, ob, bb, 6)
dung = [pack(*make_dungeon_chunk(i % 8, i // 8), 6)[1] for i in range(32)]
ocean = [pack(*make_ocean_chunk(i, 3), 6)[1] for i in range(32)]
S1, S6 = stats(sizes1), stats(sizes6)
CHUNK = S6["mean"]          # кэшированный чанк по сети и в сохранении — уровень 6 (сжимается раз на ревизию)

print("# Бюджеты сервера и сети — модель к 07_multiplayer_tech")
print()
print(f"Мир: {SOURCE}. Параметры: такт {TICK:.0f} Гц, снимки {SNAP:.0f} Гц, ввод {INP:.0f} Гц, бюджет снимка {A.budget} байт, "
      f"потолок врагов на группу {A.cap}, хост медленнее бенча в {A.slow:g} раза, ноутбук — в {A.slow_laptop:g}.")
print()
print("## 1. Чанк 32 × 32 по сети и в сохранении")
print()
print(f"Сырой чанк: 1024 (пол) + 2048 (объект плоскостями) = 3072 байта + 2 байта счётчика построек. Выборка — {A.samples} случайных чанков мира.")
print()
print("| Что | zlib 1, байт: среднее / p95 / max | zlib 6, байт: среднее / p95 / max |")
print("|---|---|---|")
print(f"| Случайный чанк мира | {S1['mean']:.0f} / {S1['p95']} / {S1['max']} | {S6['mean']:.0f} / {S6['p95']} / {S6['max']} |")
st = stats(start)
print(f"| Окно 6 × 4 у Очага | — | {st['mean']:.0f} / {st['p95']} / {st['max']} |")
print(f"| База (двор 24 × 16, три комнаты, {len(bb)} построек с записями) | {base1} | {base6} (сырой {base_raw}) |")
dd, oo = stats(dung), stats(ocean)
print(f"| Отдельная карта данжа | — | {dd['mean']:.0f} / {dd['p95']} / {dd['max']} |")
print(f"| Океан после Разлива | — | {oo['mean']:.0f} / {oo['p95']} / {oo['max']} |")
print()
print("По биому, преобладающему в чанке (zlib 6, среднее): " + "; ".join(
    f"{NAMES[k]} {sum(v) / len(v):.0f} ({len(v)})" for k, v in sorted(by_biome.items(), key=lambda kv: -len(kv[1]))))
print()

# ------------------------------------------------------------------ 2. макрокарта и разведка
def explored_mask(frac, seed=3):
    """Разведка: случайные блуждания от Очага с радиусом 3 ячейки (24 тайла днём, 01 раздел 6), пока не наберётся доля."""
    m = np.zeros((MCELLS, MCELLS), bool)
    r = np.random.default_rng(seed)
    c = MCELLS // 2
    yy, xx = np.mgrid[-3:4, -3:4]
    disk = (xx * xx + yy * yy) <= 9
    target = frac * MCELLS * MCELLS
    walkers = [[c, c, r.random() * 2 * math.pi] for _ in range(6)]
    steps = 0
    while m.sum() < target and steps < 4_000_000:
        for w in walkers:
            w[2] += r.normal(0, 0.25)
            w[0] = (w[0] + math.cos(w[2])) % MCELLS
            w[1] = (w[1] + math.sin(w[2])) % MCELLS
            x, y = int(w[0]), int(w[1])
            ix = (np.arange(x - 3, x + 4) % MCELLS)
            iy = (np.arange(y - 3, y + 4) % MCELLS)
            m[np.ix_(iy, ix)] |= disk
            if r.random() < 0.002:  # вернулся домой — новая вылазка
                w[0], w[1] = c, c
        steps += 1
    return m


def q8(a):
    a = np.asarray(a, float)
    lo, hi = a.min(), a.max()
    return np.round((a - lo) / (hi - lo + 1e-12) * 255).astype(np.uint8)


B8 = BIOME.astype(np.uint8)
FL = ((BIOME == LAKE).astype(np.uint8) | (KOT.astype(np.uint8) << 1))
H8, M8, C8 = q8(H), q8(MM), q8(CC)
server_macro = zlib.compress(B8.tobytes() + H8.tobytes() + M8.tobytes() + C8.tobytes() + FL.tobytes() + bytes(MCELLS * MCELLS), 6)
print("## 2. Макрокарта и маска разведки")
print()
print(f"Сервер хранит {MCELLS}² × 6 байт = {kb(MCELLS * MCELLS * 6):.0f} КБ (01); сжатая целиком — {kb(len(server_macro)):.0f} КБ. "
      "Клиенту — только разведанные ячейки: биом, флаги, высота (3 байта); неразведанные не уходят.")
print()
print("| Разведано | Ячеек | Макрокарта клиенту, КБ сжатая | Маска разведки, КБ сжатая (сырая 32) |")
print("|---|---|---|---|")
JOIN_MACRO, JOIN_FOG = {}, {}
for frac in (0.02, 0.15, 0.5, 1.0):
    em = explored_mask(frac) if frac < 1 else np.ones((MCELLS, MCELLS), bool)
    planes = [np.where(em, B8, 0), np.where(em, FL, 0), np.where(em, H8, 0)]
    mz = len(zlib.compress(b"".join(p.astype(np.uint8).tobytes() for p in planes), 6))
    fz = len(zlib.compress(np.packbits(em).tobytes(), 6))
    JOIN_MACRO[frac], JOIN_FOG[frac] = mz, fz
    print(f"| {frac * 100:.0f} % | {int(em.sum())} | {kb(mz):.1f} | {kb(fz):.1f} |")
print()

# ------------------------------------------------------------------ 3. вход, подгрузка, данж, Разлив
WINDOW = 24
print("## 3. Вход в мир, подгрузка на бегу, отдельный данж, Разлив")
print()
print(f"Окно клиента — 6 × 4 = {WINDOW} чанка (01); средний чанк по сети — {CHUNK:.0f} байт (zlib 6, кэш на ревизию).")
print()
print("| Вход (8 игроков в мире) | Начало акта I (2 %) | Конец акта I (15 %) | Поздняя игра (100 %) |")
print("|---|---|---|---|")
fixed = {"приветствие, настройки, список игроков, спутники, сюжет, порталы": 1024 + 8 * 64 + 14 * 32 + 300 + 200,
         "персонаж (JSON, сжатый)": 4096,
         "метки на карте (8 × 20 × 40 байт, сжатые вдвое)": 8 * 20 * 40 / 2,
         "области (комнаты, дворы) в окне": 3072,
         f"окно {WINDOW} чанков": WINDOW * CHUNK,
         "сущности в обзоре полным снимком (60 × 26)": 60 * 26}
rows = {}
for frac in (0.02, 0.15, 1.0):
    rows[frac] = sum(fixed.values()) + JOIN_MACRO[frac] + JOIN_FOG[frac]
for name, v in fixed.items():
    print(f"| {name} | {kb(v):.1f} КБ | {kb(v):.1f} КБ | {kb(v):.1f} КБ |")
print(f"| макрокарта (разведанное) | {kb(JOIN_MACRO[0.02]):.1f} КБ | {kb(JOIN_MACRO[0.15]):.1f} КБ | {kb(JOIN_MACRO[1.0]):.1f} КБ |")
print(f"| маска разведки | {kb(JOIN_FOG[0.02]):.1f} КБ | {kb(JOIN_FOG[0.15]):.1f} КБ | {kb(JOIN_FOG[1.0]):.1f} КБ |")
print(f"| **Итого** | **{kb(rows[0.02]):.0f} КБ** | **{kb(rows[0.15]):.0f} КБ** | **{kb(rows[1.0]):.0f} КБ** |")
print(f"| Время при потолке {A.bulk:.0f} КБ/с | {kb(rows[0.02]) / A.bulk:.2f} с | {kb(rows[0.15]) / A.bulk:.2f} с | {kb(rows[1.0]) / A.bulk:.2f} с |")
print(f"| Время по каналу 1 Мбит/с | {rows[0.02] * 8 / 1e6:.2f} с | {rows[0.15] * 8 / 1e6:.2f} с | {rows[1.0] * 8 / 1e6:.2f} с |")
print()
JOIN_LATE = rows[1.0]
run = 6.0
straight_h = 4 * run / CH          # новый столбец из 4 чанков каждые 32/6 с
straight_v = 6 * run / CH
diag = (4 + 6) * (run / math.sqrt(2)) / CH
print("| Подгрузка на бегу | Чанков в секунду | КБ/с |")
print("|---|---|---|")
for name, cps in (("бег 6 т/с на восток", straight_h), ("бег 6 т/с на север", straight_v),
                  ("бег по диагонали", diag), ("бег +50 % (снаряжение), по диагонали", diag * 1.5)):
    print(f"| {name} | {cps:.2f} | {kb(cps * CHUNK):.2f} |")
dung_total = sum(dung)
print()
print(f"Отдельный данж: 32 чанка × {dd['mean']:.0f} байт = {kb(dung_total):.0f} КБ сжатых (01 ставил потолок 260 КБ — для несжатых); "
      f"при {A.bulk:.0f} КБ/с — {kb(dung_total) / A.bulk:.2f} с, по каналу 1 Мбит/с — {dung_total * 8 / 1e6:.2f} с.")
flood_tiles_s = 0.12 * A.W * A.W / 600.0
chord = 160.0
print(f"Разлив Стикса: на сервере {flood_tiles_s:.0f} тайлов/с ({flood_tiles_s * 600 / 1024:.0f} чанков за 10 мин). "
      f"Клиенту — только окно: фронт до {chord:.0f} тайлов шириной × 0,5 т/с = {chord * 0.5:.0f} тайлов/с × 5 байт = "
      f"{kb(chord * 0.5 * 5):.2f} КБ/с; правки макрокарты по разведанным ячейкам ≤ {31000 / 600:.0f} ячеек/с × 4 байта = "
      f"{kb(31000 / 600 * 4):.2f} КБ/с.")
print()

# ------------------------------------------------------------------ 4. снимки и трафик
COMP_WORLD = 13       # 05: в походе 5/9/12, в эпилоге 13
ALLIED = 64           # 02 11.1


def enemies(n, d, night=True):
    base = 8 if night else 5
    return min(A.cap, math.ceil(base * (1 + 0.5 * (n - 1)) * d))


def companions_per_player(n):
    return min(2.0, COMP_WORLD / n)


def scenario(n, layout):
    """Что видит один клиент. layout: 'врозь', 'вместе', 'худший', 'босс'."""
    if layout == "врозь":
        pl, comp, summ = 1, companions_per_player(n), 2.0
        en = enemies(1, A.density)
        shooters, mg = 1, False
        builders = 0.25
    else:
        pl = n
        comp = min(2 * n, COMP_WORLD)
        if layout == "худший":
            summ = max(min(6 * n, ALLIED - comp), 2 * n)
            en = enemies(n, A.density_max)
            mg = A.mg or True
        else:
            summ = min(2.0 * n, ALLIED - comp)
            en = enemies(n, A.density)
            mg = False
        shooters = n
        builders = 0.25 * n if layout == "вместе" else 0.0
    adds = 0
    if layout == "босс":
        adds = 2 * math.ceil(3 * (1 + 0.25 * (n - 1)))  # две стаи призванных (04: Цербер)
    moving = 0.8 * (pl + comp + summ + en + adds) + (1 if layout == "босс" else 0)
    static = 6  # дроп, нейтральные, расставленные вне боя
    view = pl + comp + summ + en + adds + static + (1 if layout == "босс" else 0)
    entering = 3.0 if layout == "врозь" else 3.0 + 0.1 * view
    payload = 12 + 16 + moving * A.delta + entering * A.full / SNAP
    sent = min(payload, A.budget)
    ratio = sent / payload
    # события (надёжный канал): снаряды, попадания, правки тайлов
    shot = 10.0 if mg else 1.25
    p_spawn = shooters * shot + 0.3 * en * 0.5
    ev = p_spawn * (16 + 6) + (1.5 * pl + 0.3 * en) * 8 + builders * 2 * 8
    misc = 16 + 0.5 * n * 8 + 20 * 6 + (2 * 16 if layout == "босс" else 0)
    stream = straight_h * CHUNK * (1.0 if layout == "врозь" else 0.3)
    down = SNAP * (A.overhead + sent) + 30 * A.overhead * (1 if ev > 0 else 0) + ev + misc + stream
    up_client = INP * (A.overhead + 4 * 8 + 6) + 300
    live_proj = shooters * shot * (35 / 60 if mg else 30 / 28) + 0.3 * en * 0.5 * 1.6
    return dict(view=view, moving=moving, payload=payload, sent=sent, ratio=ratio, down=down, up=up_client,
                pl=pl, comp=comp, summ=summ, en=en, adds=adds, live_proj=live_proj, ev=ev)


def server_cpu(n, layout, sc):
    if layout == "врозь":
        ents = n * (sc["comp"] + sc["summ"] + sc["en"] + 6)
        clients = n
        view_sum = n * sc["view"]
        live_proj = n * sc["live_proj"]
        builders = 0.25 * n
    else:
        ents = sc["comp"] + sc["summ"] + sc["en"] + sc["adds"] + 6 + (1 if layout == "босс" else 0)
        clients = n
        view_sum = n * sc["view"]
        live_proj = sc["live_proj"]
        builders = 0.25 * n if layout == "вместе" else 0.0
    chasing = sc["en"] * (1 if layout == "врозь" else 1) * (n if layout == "врозь" else 1) + sc["adds"]
    t = {}
    t["персонажи (2 кадра ввода)"] = n * 2 * A.player_us
    t["сущности, шаг"] = ents * A.entity_us
    t["ИИ 10 Гц"] = ents / 3.0 * A.ai_us
    paths = min(40.0, chasing / 15.0)
    t["поиск пути"] = paths * PATH_US
    t["снаряды"] = live_proj * 0.5
    t["снимки"] = clients * 20 + view_sum * 0.2
    t["отмотка"] = n * 1.5 / TICK * 20
    t["появление врагов"] = n / 120.0 * 60
    t["прочее"] = 50
    avg = sum(t.values()) / 1000.0     # комнаты и дворы считает фоновый поток (решение 07), в такт не входят
    return avg, t, ents


def worst_on(avg_bench, slow):
    """Худший такт на машине в slow раз медленнее бенча: 40 отказов поиска, но не больше --path-ms."""
    return avg_bench * slow + min(40 * AST[2] / 1000.0 * slow, A.path_ms)


print("## 4. Снимки и трафик")
print()
print("Сценарии: **врозь** — каждый в своей окрестности ночью (плотность "
      f"{A.density:g}); **вместе** — все в одной окрестности ночью; **худший** — вместе, плотность {A.density_max:g}, "
      "все призыватели в пределах бюджета 64, все стреляют из пулемёта; **босс** — вместе, плюс босс и две стаи призванных.")
print()
print("| N | Сценарий | Сущностей в обзоре (игроки / спутники / подручные / враги) | Снимок, байт (нужно → отправлено) | Доля обновлений | Клиенту, КБ/с | От клиента, КБ/с | Сервер отдаёт, Мбит/с (хост: N − 1) |")
print("|---|---|---|---|---|---|---|---|")
SC = {}
for n in (1, 4, 8, 16, 32):
    for layout in ("врозь", "вместе", "худший", "босс"):
        if n == 1 and layout != "врозь":
            continue
        sc = scenario(n, layout)
        SC[(n, layout)] = sc
        host_up = max(0, n - 1) * sc["down"] * 8 / 1e6
        print(f"| {n} | {layout} | {sc['view']:.0f} ({sc['pl']:.0f} / {sc['comp']:.0f} / {sc['summ']:.0f} / {sc['en']:.0f}"
              f"{' + ' + str(sc['adds']) if sc['adds'] else ''}) | {sc['payload']:.0f} → {sc['sent']:.0f} | "
              f"{min(1.0, sc['ratio']) * 100:.0f} % | {kb(sc['down']):.1f} | {kb(sc['up']):.1f} | "
              f"{n * sc['down'] * 8 / 1e6:.2f} ({host_up:.2f}) |")
print()
print("«Доля обновлений» < 100 % — снимок не влез в бюджет: дальние и менее важные сущности обновляются реже (приоритеты).")
print()

# ------------------------------------------------------------------ 5. процессор
print("## 5. Процессор сервера, мс на такт (бюджет такта 33,3 мс)")
print()
print(f"Поиск пути в среднем {PATH_US:.1f} мкс (смесь {A.astar_mix} из net_bench); худший такт — 40 отказов по {AST[2]:.0f} мкс, "
      f"но не больше {A.path_ms:g} мс на работающей машине (остальные поиски ждут следующего такта). Пересчёт комнат и дворов — в фоновом потоке. "
      "Сущности — {:.1f} мкс со всей логикой, ИИ — {:.0f} мкс на решение *(прикидки)*.".format(A.entity_us, A.ai_us))
print()
print(f"| N | Сценарий | Активных сущностей | Бенч: средний / худший | Хост (×{A.slow:g}) | Ноутбук (×{A.slow_laptop:g}) | Загрузка ядра на хосте |")
print("|---|---|---|---|---|---|---|")
CPU = {}
for (n, layout), sc in SC.items():
    avg, parts, ents = server_cpu(n, layout, sc)
    CPU[(n, layout)] = (avg, parts)
    print(f"| {n} | {layout} | {ents:.0f} | {avg:.2f} / {worst_on(avg, 1):.2f} | {avg * A.slow:.2f} / {worst_on(avg, A.slow):.2f} | "
          f"{avg * A.slow_laptop:.2f} / {worst_on(avg, A.slow_laptop):.2f} | {avg * A.slow / (1000 / TICK) * 100:.0f} % |")
print()
for key in ((8, "худший"), (32, "врозь")):
    print(f"Раскладка «{key[1]}» на {key[0]} (бенч, мкс): " + "; ".join(f"{k} {v:.0f}" for k, v in CPU[key][1].items()))
    print()
lap_ok = max(n for (n, l), (a, _) in CPU.items() if a * A.slow_laptop <= 1000 / TICK * 0.5)
host_ok = max(n for (n, l), (a, _) in CPU.items() if a * A.slow <= 1000 / TICK * 0.5)
print(f"Средний такт не больше половины бюджета (16,7 мс) во всех сценариях: на хосте — до {max(n for n in (1,4,8,16,32) if all(CPU[(k,l)][0]*A.slow<=16.7 for (k,l) in CPU if k==n))} игроков, "
      f"на ноутбуке — до {max(n for n in (1,4,8,16,32) if all(CPU[(k,l)][0]*A.slow_laptop<=16.7 for (k,l) in CPU if k==n))}.")
print()

# ------------------------------------------------------------------ 6. память и сохранения
print("## 6. Память сервера и размер сохранения")
print()
print("| Мир | Игроков | Тайлы | Постройки | Макрокарта и разведка | Сущности и история | Клиенты | Рантайм .NET | **Итого, МБ** | Пик генерации, МБ |")
print("|---|---|---|---|---|---|---|---|---|---|")
for W in (1024, 2048, 4096, 8192):
    for n in (4, 8, 32):
        if W == 1024 and n != 4:
            continue
        tiles = W * W * 3
        built = n * 4000 * 48
        macro = (W // 8) ** 2 * 6 + (W // 8) ** 2 / 8
        sc = SC.get((n, "худший"), SC[(8, "худший")]) if n > 1 else SC[(1, "врозь")]
        ents = n * 40 + 200
        ent_mem = ents * 1536 + ents * 16 * 16 + 10 * 32 * 3072
        cl = n * (32 * sc["view"] * 24 + 300 * 1024 + 64 * 1024)
        runtime = 60 * 1024 * 1024
        total = tiles + built + macro + ent_mem + cl + runtime
        gen_peak = W * W * 8 + 40 * 1024 * 1024
        print(f"| {W}² | {n} | {tiles / 2**20:.0f} | {built / 2**20:.1f} | {macro / 2**20:.1f} | {ent_mem / 2**20:.1f} | "
              f"{cl / 2**20:.1f} | 60 | **{total / 2**20:.0f}** | {gen_peak / 2**20:.0f} |")
print()
nch = (A.W // 32) ** 2
base_bin = nch * CHUNK
early_dirty, late_dirty = 180, int(0.28 * nch) + 1000
ocean_share = 0.12 * nch
late_bytes = (late_dirty - ocean_share) * CHUNK + ocean_share * oo["mean"]
print(f"Сохранение мира {A.W}²: снимок генерации base.bin — {nch} чанков × {CHUNK:.0f} байт = {base_bin / 2**20:.1f} МБ, пишется один раз.")
print()
print("| Когда | Изменённых чанков | Чанки, МБ | Персонажи (8 × 6 КБ) | Состояние мира (макрокарта, разведка, сюжет, данжи) | **Файл, МБ** | Запись при 100 МБ/с |")
print("|---|---|---|---|---|---|---|")
state = len(server_macro) + JOIN_FOG[1.0] + 40 * 1024
for name, dirty, cbytes in (("середина акта I", early_dirty, early_dirty * CHUNK),
                            ("акт III, после преображений", late_dirty, late_bytes)):
    total = cbytes + 8 * 6144 + state
    print(f"| {name} | {dirty} | {cbytes / 2**20:.2f} | 0.05 | {state / 2**20:.2f} | **{total / 2**20:.1f}** | {total / 2**20 / 100 * 1000:.0f} мс |")
print()

# ------------------------------------------------------------------ 7. что вступает первым
print("## 7. Что вступает первым")
print()
print(f"| Лимит | Где вступает |")
print("|---|---|")
for d in (1.0, A.density, 1.5, A.density_max):
    n = next((k for k in range(1, 200) if 8 * (1 + 0.5 * (k - 1)) * d > A.cap), None)
    print(f"| Потолок {A.cap} врагов на группу ночью, плотность {d:g} | с {n} игроков в одной окрестности |")
n_all = next(k for k in range(1, 100) if 6 * k + min(2 * k, COMP_WORLD) > ALLIED)
print(f"| Бюджет 64 союзных (все — призыватели по 6) | с {n_all} игроков-призывателей в одной окрестности |")
for layout in ("вместе", "худший", "босс"):
    n = next((k for k in (2, 4, 6, 8, 10, 12, 16, 20, 24, 32) if scenario(k, layout)["payload"] > A.budget), None)
    print(f"| Бюджет снимка {A.budget} байт, сценарий «{layout}» | {'с ' + str(n) + ' игроков' if n else 'не вступает до 32'} |")
for mbit in (5, 10, 20):
    for layout in ("вместе", "худший"):
        n = next((k for k in range(2, 65) if (k - 1) * scenario(k, layout)["down"] * 8 / 1e6 > mbit), None)
        print(f"| Отдача хоста {mbit} Мбит/с, сценарий «{layout}» | с {n} игроков |")
n40 = next((k for k in range(1, 200) if enemies(k, A.density_max) / 15.0 > 40), None)
print(f"| 40 поисков пути за такт (средний спрос) | не вступает: при потолке {A.cap} врагов спрос — {A.cap / 15:.1f} поисков за такт |")
print()
print(f"Худший такт на хосте (8, «худший»): {worst_on(CPU[(8, 'худший')][0], A.slow):.1f} мс из 33,3; на ноутбуке — "
      f"{worst_on(CPU[(8, 'худший')][0], A.slow_laptop):.1f} мс. 32 игрока врозь на ноутбуке: {worst_on(CPU[(32, 'врозь')][0], A.slow_laptop):.1f} мс.")
