#!/usr/bin/env python3
"""Расчёты Критика по docs/04_story_acts.md (смена 28.10).

Запуск (из любой папки):
    python C:\\Users\\carak\\source\\Orpheus\\design_room\\threads\\04_story_acts\\story_calc.py
    python story_calc.py --trials 300 --seed 2810 --detour 1.5 --close 90 --prep 5 --boss 5

Печатает Markdown; копия вывода лежит рядом: story_calc.md
(получена командой `python story_calc.py > story_calc.md`).

Откуда числа (всё — прикидки документов, не новые решения):
  00_vision 5.1–5.2 — тор 4096², бег 6 т/с, обход гор и воды ×1,5, сутки 20 мин, потолки 10/20/30,
                      здоровье обычного врага 35/120/400;
  01_world          — кольцо 0 (< 256 от Очага) без порталов, Изнанка — антипод, доли биомов,
                      роща вокруг закрытого портала r = 200, потолок волшебного леса 10 % (4 % — очаги);
  02_combat         — архетипы 8.2 (здоровье по актам), боссы 10.1–10.3 (фазы, ярость, пересчёт N),
                      масштабирование ×(1 + 0,75(N − 1)), спутник = 0,3 урона героя (12.3);
  04_story_acts     — разделы 4 (круг, песнь), 6 (боссы), 8 (подвиги), 9 (подъём), 12 (комиксы).
Модели простые: цель — порядок величин и проверка того, сходятся ли числа документа между собой.
"""
import argparse
import math
import random
import statistics
import sys
from itertools import combinations

sys.stdout.reconfigure(encoding="utf-8")

P = argparse.ArgumentParser()
P.add_argument("--trials", type=int, default=300, help="случайных кругов на вариант")
P.add_argument("--seed", type=int, default=2810)
P.add_argument("--detour", type=float, default=1.5, help="множитель пути за обход гор и воды (00, 5.1)")
P.add_argument("--close", type=float, default=90.0,
               help="с на портал: подход под дыханием, 1 с удержания, 60 с песни (в 04, 4.6 — 1,5 мин)")
P.add_argument("--prep", type=float, default=5.0, help="мин: возврат на базу, Каллиопа, крафт, еда перед хозяином")
P.add_argument("--boss", type=float, default=5.0, help="мин: бой соло с хозяином реки (Кампа 14 000 / 45 у.с. = 5,2)")
P.add_argument("--inv", type=float, default=6.0, help="мин: отбить Магическое нашествие соло (3 волны, ≤ 120 с каждая)")
A = P.parse_args()

W = 4096
RUN = 6.0
RING0 = 256
BASE = (0.0, 0.0)
ANTI = (W / 2, W / 2)          # Изнанка = Разлом
DAY = 20.0                     # минут в сутках
CLOSE = A.close / 60.0         # минут на портал


def td(a, b):
    dx = abs(a[0] - b[0]) % W
    dy = abs(a[1] - b[1]) % W
    if dx > W / 2:
        dx = W - dx
    if dy > W / 2:
        dy = W - dy
    return math.hypot(dx, dy)


def tmin(dist, detour=None):
    return dist * (A.detour if detour is None else detour) / RUN / 60.0


def table(head, rows):
    print("| " + " | ".join(head) + " |")
    print("|" + "---|" * len(head))
    for r in rows:
        print("| " + " | ".join(str(c) for c in r) + " |")
    print()


def f(x, d=0):
    return f"{x:,.{d}f}".replace(",", " ")


def q(xs, p):
    s = sorted(xs)
    return s[min(len(s) - 1, int(p * len(s)))]


def stat(xs, d=0):
    return f"{f(statistics.median(xs), d)} ({f(q(xs, 0.1), d)}–{f(q(xs, 0.9), d)})"


def portal_set(rng, center=None, radius=None):
    pts = []
    while len(pts) < 10:
        if center is None:
            p = (rng.uniform(0, W), rng.uniform(0, W))
        else:
            r = radius * math.sqrt(rng.random())
            a = rng.uniform(0, 2 * math.pi)
            p = ((center[0] + r * math.cos(a)) % W, (center[1] + r * math.sin(a)) % W)
        if td(p, BASE) >= RING0:
            pts.append(p)
    rivers = [0, 0, 0, 0, 1, 1, 1, 2, 2, 2]      # 4/3/3 (04, 4.3)
    rng.shuffle(rivers)
    return pts, rivers


def held_karp(pts):
    """Точный кратчайший путь база → все порталы; dp[full][j] — путь, кончающийся на j."""
    n = len(pts)
    d0 = [td(BASE, p) for p in pts]
    d = [[td(p, r) for r in pts] for p in pts]
    INF = float("inf")
    size = 1 << n
    dp = [[INF] * n for _ in range(size)]
    par = [[-1] * n for _ in range(size)]
    for j in range(n):
        dp[1 << j][j] = d0[j]
    for mask in range(1, size):
        row = dp[mask]
        for j in range(n):
            cur = row[j]
            if cur == INF:
                continue
            dj = d[j]
            for k in range(n):
                if mask >> k & 1:
                    continue
                nm = mask | (1 << k)
                v = cur + dj[k]
                if v < dp[nm][k]:
                    dp[nm][k] = v
                    par[nm][k] = j
    return dp[size - 1], par, d0, d


def order_from(par, n, end):
    mask, j, out = (1 << n) - 1, end, []
    while j != -1:
        out.append(j)
        pj = par[mask][j]
        mask ^= 1 << j
        j = pj
    return out[::-1]


def split_makespan(order, d0, d, k):
    """Нижняя граница для k команд: делим кратчайший замкнутый обход на k кусков подряд."""
    n = len(order)
    best = float("inf")
    for cuts in combinations(range(1, n), k - 1):
        bounds = (0,) + cuts + (n,)
        worst = 0.0
        for a, b in zip(bounds, bounds[1:]):
            seg = order[a:b]
            path = sum(d[seg[i]][seg[i + 1]] for i in range(len(seg) - 1))
            t = tmin(min(d0[seg[0]], d0[seg[-1]]) + path) + len(seg) * CLOSE
            worst = max(worst, t)
        best = min(best, worst)
    return best


def greedy(pts, k, reserve=None):
    """Как играют люди: освободившаяся команда бежит к ближайшему незакрытому порталу.
    reserve — портал реки нужного хозяина, его закрывают последним. Возвращает (мин до 10-го, индекс 10-го)."""
    pos, t = [BASE] * k, [0.0] * k
    todo = set(range(len(pts)))
    if reserve is not None:
        todo.discard(reserve)
    last, last_j = 0.0, None
    while todo:
        i = min(range(k), key=lambda z: t[z])
        j = min(todo, key=lambda z: td(pos[i], pts[z]))
        todo.discard(j)
        t[i] += tmin(td(pos[i], pts[j])) + CLOSE
        pos[i] = pts[j]
        if t[i] >= last:
            last, last_j = t[i], j
    if reserve is None:
        return last, last_j
    arr = min(t[z] + tmin(td(pos[z], pts[reserve])) + CLOSE for z in range(k))
    return max(arr, last + 0.05), reserve


def best_reserve(pts, rivers, k, river):
    best = None
    for j in range(10):
        if rivers[j] != river:
            continue
        t, _ = greedy(pts, k, j)
        if best is None or t < best[0]:
            best = (t, j)
    return best


def solo_with_invasions(pts, reserve, rng, roll_at_open, defend):
    """Соло «ближайший следующий»; в каждые сумерки бросок Магического нашествия:
    5 % × открытые порталы, не больше 50 %, не чаще раза в 3 суток (04, 4.3 и 7.3)."""
    pos, t = BASE, 0.0
    todo = set(range(10))
    todo.discard(reserve)
    closed, n_inv = 0, 0
    dusk = 0.0 if roll_at_open else DAY
    cd = -1.0
    seq = []
    while todo or reserve not in seq:
        if todo:
            j = min(todo, key=lambda z: td(pos, pts[z]))
            todo.discard(j)
        else:
            j = reserve
        seq.append(j)
        t_end = t + tmin(td(pos, pts[j])) + CLOSE
        home = False
        while dusk <= t_end:
            if dusk >= cd:
                p = min(0.5, 0.05 * (10 - closed))
                if rng.random() < p:
                    n_inv += 1
                    cd = dusk + 3 * DAY
                    home = defend
            dusk += DAY
        closed += 1
        t, pos = t_end, pts[j]
        if home:
            t += A.inv
            pos = BASE
    return t, n_inv


def boss_ratio(n):
    """Длительность боя группы к соло по 02, 10.2: здоровье ×(1 + 0,75(N − 1)), урон ×N."""
    return (1 + 0.75 * (n - 1)) / n


rng = random.Random(A.seed)

print("# Расчёты по 04_story_acts (Критик, 28.10)\n")
print(f"Параметры: тор {W}², база у Очага, порталы равномерно вне кольца 0 (< {RING0}); бег {RUN:.0f} т/с; "
      f"обход ×{A.detour}; портал {A.close:.0f} с; подготовка {A.prep:.0f} мин; бой соло с хозяином "
      f"{A.boss:.0f} мин (группа — по 02: ×{boss_ratio(4):.2f} при 4, ×{boss_ratio(8):.2f} при 8); "
      f"{A.trials} случайных кругов, зерно {A.seed}.\n")

# ---------------------------------------------------------------------------------------------
# 1. Круг: путь и время до закрытия десятого
# ---------------------------------------------------------------------------------------------
R = {key: [] for key in (
    "opt_free_d", "opt_r3_d", "nn_free_d", "nn_r3_d", "opt_free_t", "opt_r3_t", "nn_free_t", "nn_r3_t",
    "g2", "g4", "g8", "s2", "s4", "s8", "gates_solo", "gates_g4", "gates_g8", "dist1",
    "c1000", "c500", "inv_n_open", "inv_n_late", "inv_t_def", "inv_t_ign")}

for _ in range(A.trials):
    pts, rivers = portal_set(rng)
    end_cost, par, d0, d = held_karp(pts)
    r3 = 1 if rivers.count(1) == 3 else 2      # река с 3 порталами
    free_j = min(range(10), key=lambda j: end_cost[j])
    r3_j = min((j for j in range(10) if rivers[j] == r3), key=lambda j: end_cost[j])
    R["opt_free_d"].append(end_cost[free_j])
    R["opt_r3_d"].append(end_cost[r3_j])
    R["opt_free_t"].append(tmin(end_cost[free_j]) + 10 * CLOSE)
    R["opt_r3_t"].append(tmin(end_cost[r3_j]) + 10 * CLOSE)
    R["dist1"].extend(d0)

    t_free, j_free = greedy(pts, 1)
    t_r3, j_r3 = best_reserve(pts, rivers, 1, r3)
    R["nn_free_t"].append(t_free)
    R["nn_r3_t"].append(t_r3)
    R["nn_free_d"].append((t_free - 10 * CLOSE) * 60 * RUN / A.detour)
    R["nn_r3_d"].append((t_r3 - 10 * CLOSE) * 60 * RUN / A.detour)
    R["gates_solo"].append(td(BASE, pts[j_r3]))

    for k in (2, 4, 8):
        tk, jk = best_reserve(pts, rivers, k, r3)
        R[f"g{k}"].append(tk)
        if k == 4:
            R["gates_g4"].append(td(BASE, pts[jk]))
        if k == 8:
            R["gates_g8"].append(td(BASE, pts[jk]))
    closed_best = min(range(10), key=lambda j: end_cost[j] + d0[j])
    order = order_from(par, 10, closed_best)
    for k in (2, 4, 8):
        R[f"s{k}"].append(split_makespan(order, d0, d, k))

    # после поражения: новые 10 — около старых Врат (r = 1000) или вокруг одной случайной точки (r = 500)
    cpts, _ = portal_set(rng, center=pts[j_r3], radius=1000)
    R["c1000"].append(greedy(cpts, 1)[0])
    while True:
        c = (rng.uniform(0, W), rng.uniform(0, W))
        if td(c, BASE) >= RING0 + 500:
            break
    cpts, _ = portal_set(rng, center=c, radius=500)
    R["c500"].append(greedy(cpts, 1)[0])

    t1, n1 = solo_with_invasions(pts, r3_j, rng, roll_at_open=True, defend=True)
    t2, n2 = solo_with_invasions(pts, r3_j, rng, roll_at_open=False, defend=False)
    R["inv_n_open"].append(n1)
    R["inv_n_late"].append(n2)
    R["inv_t_def"].append(t1)
    R["inv_t_ign"].append(t_r3)

print("## 1. Круг порталов: путь и время до закрытия десятого\n")
print(f"Расстояние от базы до случайного портала: медиана {f(statistics.median(R['dist1']))} т, "
      f"среднее {f(statistics.mean(R['dist1']))} т (теория для тора: 0,383 · 4096 = {f(0.3826 * W)}); "
      f"до Изнанки — {f(td(BASE, ANTI))} т = {tmin(td(BASE, ANTI)):.1f} мин с обходом.\n")
rows = [
    ["Соло, кратчайший путь (нижняя граница), 10-й — любой", stat(R["opt_free_d"]), stat(R["opt_free_t"], 1)],
    ["Соло, кратчайший путь, 10-й — река с 3 порталами", stat(R["opt_r3_d"]), stat(R["opt_r3_t"], 1)],
    ["Соло «как человек» (ближайший следующий), 10-й — любой", stat(R["nn_free_d"]), stat(R["nn_free_t"], 1)],
    ["Соло «как человек», 10-й — река с 3 порталами", stat(R["nn_r3_d"]), stat(R["nn_r3_t"], 1)],
    ["4 игрока двумя парами («как люди»)", "—", stat(R["g2"], 1)],
    ["4 игрока поодиночке («как люди»)", "—", stat(R["g4"], 1)],
    ["8 игроков четырьмя парами («как люди»)", "—", stat(R["g4"], 1) + " *(то же, что 4 команды)*"],
    ["8 игроков поодиночке («как люди»)", "—", stat(R["g8"], 1)],
    ["Нижняя граница: 2 / 4 / 8 команд (делёж кратчайшего обхода)", "—",
     f"{stat(R['s2'], 1)} / {stat(R['s4'], 1)} / {stat(R['s8'], 1)}"],
]
table(["Вариант", "Путь бега по прямой, тайлов: медиана (10–90 %)",
       f"До закрытия 10-го, мин (с обходом ×{A.detour} и {A.close:.0f} с на портал)"], rows)
dr = statistics.median(R["nn_r3_t"]) / statistics.median(R["nn_free_t"]) - 1
do = statistics.median(R["opt_r3_t"]) / statistics.median(R["opt_free_t"]) - 1
print(f"Цена выбора хозяина (10-й — из реки с 3 порталами против любого): +{do * 100:.0f} % при кратчайшем пути, "
      f"+{dr * 100:.0f} % «как человек». Оценка документа (4.6): ≈ 9200 т ≈ 26 мин бега — это путь **без** обхода ×1,5 "
      f"из `00`; с обходом бег соло — {f(statistics.median(R['nn_r3_d']) * A.detour / RUN / 60, 0)} мин.\n")

# ---------------------------------------------------------------------------------------------
# 2. Круг целиком, цена поражения, порталы за акт II
# ---------------------------------------------------------------------------------------------
print("## 2. Круг целиком и цена поражения\n")
print("Круг = до 10-го + подготовка на базе + бег база → Врата круга + бой. Поражение = такой же новый круг "
      "(новые 10 открываются сразу, персонаж — на базе).\n")


def totals(tour, gates, n):
    return [t + A.prep + tmin(g) + A.boss * boss_ratio(n) for t, g in zip(tour, gates)]


solo_tot = totals(R["nn_r3_t"], R["gates_solo"], 1)
g4p_tot = totals(R["g2"], R["gates_g4"], 4)
g4s_tot = totals(R["g4"], R["gates_g4"], 4)
g8p_tot = totals(R["g4"], R["gates_g8"], 8)
g8s_tot = totals(R["g8"], R["gates_g8"], 8)
THANATOS = 6.0   # 18 000 / 50 у.с. (02) ≈ 6 мин соло


def raz(tour, n):
    return [t + A.prep + tmin(td(BASE, ANTI)) + THANATOS * boss_ratio(n) for t in tour]


rows = [
    ["Соло", "50–60", stat(solo_tot), stat(raz(R["nn_r3_t"], 1))],
    ["4 игрока двумя парами", "25–30", stat(g4p_tot), stat(raz(R["g2"], 4))],
    ["4 игрока поодиночке", "—", stat(g4s_tot), stat(raz(R["g4"], 4))],
    ["8 игроков четырьмя парами", "15–20", stat(g8p_tot), stat(raz(R["g4"], 8))],
    ["8 игроков поодиночке", "—", stat(g8s_tot), stat(raz(R["g8"], 8))],
]
table(["Состав", "Документ (4.6), мин", "Круг реки, мин: медиана (10–90 %)",
       "Круг Разлома (Врата — на Изнанке, Танатос 6 мин соло), мин"], rows)

ms = statistics.median(solo_tot)
m8 = statistics.median(g8s_tot)
print(f"Цена одного поражения: соло ≈ {ms:.0f} мин, ввосьмером ≈ {m8:.0f} мин (поодиночке) — разница ×{ms / m8:.1f}. "
      f"Бой с хозяином соло — {A.boss:.0f} мин: поражение стоит ×{ms / A.boss:.0f} от самого боя.\n")

print("### 2а. Порталы за акт II (12–15 ч по `00`)\n")
print("Минимум 4 круга: три реки и Разлом. Вероятность поражения p на каждую попытку → в среднем 1/(1 − p) кругов на хозяина.\n")
rows = []
for p in (0.0, 0.2, 0.33, 0.5):
    k = 1 / (1 - p)
    s = (3 * statistics.median(solo_tot) + statistics.median(raz(R["nn_r3_t"], 1))) * k / 60
    a4 = (3 * statistics.median(g4p_tot) + statistics.median(raz(R["g2"], 4))) * k / 60
    a8 = (3 * statistics.median(g8s_tot) + statistics.median(raz(R["g8"], 8))) * k / 60
    rows.append([f"{p:.2f}", f"{4 * k:.1f}", f"{s:.1f} ч ({s / 15 * 100:.0f}–{s / 12 * 100:.0f} % акта)",
                 f"{a4:.1f} ч", f"{a8:.1f} ч"])
table(["p поражения", "Кругов", "Соло", "4 игрока (пары)", "8 игроков (поодиночке)"], rows)
print("Документ (4.6): соло ≈ 4 ч, вчетвером ≈ 2 ч, ввосьмером ≈ 1,5 ч. Передышки между кругами (20–40 мин) "
      "сюда не входят — это время на остальное содержимое акта.\n")

print("### 2б. Магическое нашествие во время соло-круга\n")
pin = sum(1 for n in R["inv_n_open"] if n > 0) / A.trials
pil = sum(1 for n in R["inv_n_late"] if n > 0) / A.trials
print(f"Бросок 5 % × открытые (≤ 50 %) в каждые сумерки, не чаще раза в 3 суток. Если бросок идёт в те же сумерки, "
      f"когда открылся круг (порядок в документе не задан), — нашествие хотя бы раз за круг в {pin * 100:.0f} % кругов, "
      f"в среднем {statistics.mean(R['inv_n_open']):.2f} за круг; если со следующих сумерек — в {pil * 100:.0f} %. "
      f"Соло, который идёт домой отбиваться ({A.inv:.0f} мин) и потом снова выходит с базы: до 10-го — "
      f"{stat(R['inv_t_def'])} мин против {stat(R['inv_t_ign'])} без нашествий.\n")

print("### 2в. Если новые 10 после поражения — не по всей планете\n")
rows = [
    ["По всей планете (как в документе)", stat(R["nn_free_t"], 1), stat(totals(R["nn_free_t"], R["gates_solo"], 1))],
    ["Не дальше 1000 от старых Врат (альтернатива в 15.3)", stat(R["c1000"], 1),
     stat([t + A.prep + tmin(1567) + A.boss for t in R["c1000"]])],
    ["Вокруг одной случайной точки, r = 500 («в случайном месте» — буква Автора)", stat(R["c500"], 1),
     stat([t + A.prep + tmin(1567) + A.boss for t in R["c500"]])],
]
table(["Где открываются новые 10", "До 10-го соло, мин", "Цена поражения соло, мин (круг целиком)"], rows)

# ---------------------------------------------------------------------------------------------
# 3. Песнь закрытия: нагрузка волн
# ---------------------------------------------------------------------------------------------
print("## 3. Песнь закрытия: хватает ли урона\n")
HP2 = {"Б": 120, "С": 84, "З": 72, "К": 360, "Л": 60, "М": 96, "Р": 6 * 18, "Н": 180}   # 02, 8.2, акт II
RIV = {"Флегетон": "Б З К Н", "Коцит": "Б Л М К", "Лета": "Б М С Р"}
print("Здоровье врагов акта II по архетипам `02` (8.2): Б 120, С 84, З 72, К 360, Л 60, М 96, Н 180, рой — стая 6 × 18 = 108. "
      "В волне враги реки выбираются равновероятно. Документ (4.4) считает всех по 120.\n")
DPS = [("снаряжение акта I (02: 22 × 1,3 × 0,5)", 14.3), ("ярус 1 эпохи II, оценка 04 «около 25»", 25.0),
       ("ярус 1 эпохи II по формуле 02 (45 × 1,5 × 0,5)", 33.8), ("ярус 2 эпохи II (02: 50)", 50.0)]
rows = []
for name, letters in RIV.items():
    mean_hp = statistics.mean(HP2[x] for x in letters.split())
    for n in (1, 4, 8):
        wave = 4 + 2 * (n - 1)
        song = 3 * wave * mean_hp
        loads = []
        for _, dps in DPS:
            per_wave = wave * mean_hp / (n * dps)
            loads.append(f"{per_wave:.0f} с")
        rows.append([name if n == 1 else "", n, wave, f(mean_hp), f(song), f"{wave / n:.2f}"] + loads)
table(["Река", "N", "Врагов в волне", "Здоровье врага, ср.", "Здоровье трёх волн", "Врагов на игрока"] +
      [f"Добить волну, {nm}" for nm, _ in DPS], rows)
print("Волны выходят через 20 с (0 / 33 / 66 % от 60 с). Если «добить волну» > 20 с, враги копятся до конца песни. "
      "Проверка документа: 3 × 4 × 120 = 1440 / 25 = 58 с из 60 — запаса нет, а не «2 волны».\n")

# ---------------------------------------------------------------------------------------------
# 4. Боссы против принципов 02
# ---------------------------------------------------------------------------------------------
print("## 4. Боссы: время боя, лимит, масштабирование\n")
BOSSES = [  # имя, акт, здоровье соло, фазы в 04, лимит 04 (мин), фазы по 02, ярость 02 (мин)
    ("Цербер", 1, 3600, "3 (по спящим головам)", 15, "2: 50 %", 10),
    ("Кампа", 2, 14000, "2: 50 %", 12, "3: 66/33 %", 12),
    ("Эмпуса", 2, 11000, "2: 50 %", 12, "3: 66/33 %", 12),
    ("Керы", 2, 12000, "2: 50 %", 12, "3: 66/33 %", 12),
    ("Танатос", 2, 18000, "3: 60/25 %", 15, "3: 66/33 %", 12),
    ("Халкотавры", 3, 32000, "2: 50 % у любого", None, "3: 66/33 %", 16),
    ("Пандора", 3, 50000, "3: 60/25 %", 15, "3: 66/33 %", 16),
]
D04 = {1: 16, 2: 45, 3: 120}       # 04, 6.1
D02 = {1: 14, 2: 50, 3: 170}       # 02, 10.1 (расчёт опоры)
OPOR = {1: 3500, 2: 15000, 3: 65000}
rows = []
for name, act, hp, ph4, lim, ph2, rage in BOSSES:
    t04 = hp / D04[act] / 60
    t02 = hp / D02[act] / 60
    lim_s = f"{lim}" if lim else "закат"
    rows.append([name, act, f(hp), f(OPOR[act]), ph4, ph2, f"{t04:.1f} / {t02:.1f}", lim_s, rage])
table(["Босс", "Акт", "Здоровье соло (04)", "Опора 02", "Фазы 04", "Фазы по 02", "Бой соло, мин: у.с. 04 / 02",
       "Уход по времени, мин (04)", "Ярость, мин (02)"], rows)
print("Хозяин реки на снаряжении акта I (02: 14 у.с.): Кампа "
      f"{14000 / 14.3 / 60:.1f} мин, Эмпуса {11000 / 14.3 / 60:.1f}, Керы {12000 / 14.3 / 60:.1f} — "
      "все дольше лимита 12 мин: поражение гарантировано, а оно стоит круг.\n")
rows = []
for n in (1, 4, 8):
    hp = 14000 * (1 + 0.75 * (n - 1))
    t_ok = hp / (45 * n) / 60
    t_exp = 14000 / (45 * n) / 60
    rows.append([n, f(hp), f"{t_ok:.1f}", f"{t_exp * 60:.0f} с"])
table(["Участников", "Здоровье Кампы по 02", "Бой, мин (у.с. 45 на каждого)",
       "Бой, если здоровье «задано при начале» одним (04, 4.7)"], rows)

# ---------------------------------------------------------------------------------------------
# 5. Подъём
# ---------------------------------------------------------------------------------------------
print("## 5. Подъём: проходим ли отрезок\n")


def ascent(n_players, n_comp, dps, seg=150.0, barriers=(50.0, 100.0), door=148.0, enemy=400.0,
           dark_speed=4.0, dark_start=30.0, dt=0.05, scale_barrier=True, dark_waits=False, dark_idle=None):
    """Ведущий бежит 6 т/с; Эвридика в 3 т позади; Тьма стартует в dark_start позади Эвридики и идёт
    dark_speed т/с всегда (dark_waits=True — стоит, пока стоит Эвридика). Заслон — (6 + 2(N−1)) врагов,
    группа стоит, пока он жив; дверь — 4 с. Ловцы: (2 + союзники) по 300 здоровья раз в 20 с отнимают урон;
    соло без союзников теряет 1 Нить/с от ловцов. Нить −20/с во Тьме, +2/с если Тьма дальше 10."""
    allies = (n_players - 1) + n_comp
    team = n_players * dps + n_comp * 0.3 * dps
    catch_drain = (2 + min(allies, 8)) * 300.0 / 20.0
    eff = max(team - catch_drain, 1.0)
    x, t, nit, min_nit = 0.0, 0.0, 100.0, 100.0
    dark = -3.0 - dark_start
    b_hp = []
    for _ in barriers:
        size = 6 + 2 * (n_players - 1) if scale_barrier else 6
        b_hp.append(size * enemy)
    door_left = 4.0
    bi = 0
    while x < seg:
        moving = True
        if bi < len(barriers) and x >= barriers[bi]:
            b_hp[bi] -= eff * dt
            moving = False
            if b_hp[bi] <= 0:
                bi += 1
        elif x >= door and door_left > 0:
            door_left -= dt
            moving = False
        if moving:
            x += 6.0 * dt
        if dark_idle is not None and not moving:
            dark += dark_idle * dt
        elif not (dark_waits and not moving):
            dark += dark_speed * dt
        eur = x - 3.0
        if dark >= eur:
            nit -= 20 * dt
        elif dark < eur - 10:
            nit = min(100.0, nit + 2 * dt)
        if allies == 0:
            nit -= 1.0 * dt
        min_nit = min(min_nit, nit)
        t += dt
        if nit <= 0:
            return t, 0.0, False
        if t > 600:
            break
    return t, min_nit, True


rows = []
for label, n, c in (("Соло без спутников", 1, 0), ("Соло + 2 спутника", 1, 2), ("4 игрока", 4, 0), ("8 игроков", 8, 0)):
    cells = []
    for dps in (120, 170, 240):
        tt, mn, ok = ascent(n, c, dps)
        cells.append(f"{'проходит' if ok else 'Нить 0'} за {tt:.0f} с" + (f", Нить ≥ {mn:.0f}" if ok else ""))
    rows.append([label] + cells)
table(["Кто идёт", "у.с. 120 (04)", "у.с. 170 (02)", "у.с. 240 (по обычным врагам)"], rows)
print(f"Предел по документу: Тьма проходит 150 + 33 т за {183 / 4:.0f} с, значит отрезок должен быть пройден за ≈ 45–50 с "
      f"(плюс 5 с запаса Нити); документ ждёт 2–3 мин — за 120 с Тьма уходит на {-33 + 4 * 120:.0f} т вперёд старта.\n")
rows = []
for label, kw in (("Тьма стоит, пока стоит Эвридика", dict(dark_waits=True)),
                  ("Тьма 4 т/с, пока Эвридика идёт, и 1 т/с, пока стоит", dict(dark_idle=1.0)),
                  ("Тьма 2 т/с", dict(dark_speed=2.0)),
                  ("Заслон не растёт с N (6 врагов)", dict(scale_barrier=False)),
                  ("Старт Тьмы в 90 т", dict(dark_start=90.0))):
    cells = []
    for n, c in ((1, 0), (1, 2), (4, 0), (8, 0)):
        tt, mn, ok = ascent(n, c, 120, **kw)
        cells.append(f"{'да' if ok else 'нет'}, {tt:.0f} с")
    rows.append([label] + cells)
table(["Правка (у.с. 120)", "Соло", "Соло + 2", "4", "8"], rows)

# ---------------------------------------------------------------------------------------------
# 6. Уровни
# ---------------------------------------------------------------------------------------------
print("## 6. Уровни по актам\n")
M, L, O = "обяз", "вероятно", "по желанию"
SEQ = {
    1: [("данжи ×4 (струны)", 4, M), ("Изнанка", 1, L), ("Набег отбит", 1, L), ("данжи ×3", 3, O),
        ("Обход", 1, O), ("Все биомы акта I", 1, O), ("@Цербер", 0, M), ("Цербер", 3, M)],
    2: [("Первый портал", 1, M), ("@1-й хозяин", 0, M), ("Хозяева ×3", 6, M), ("Нашествие", 1, L),
        ("данжи II ×3", 3, O), ("@Танатос", 0, M), ("Танатос", 3, M)],
    3: [("Кузня", 2, M), ("Армия машин", 2, L), ("Арсенал, Маяк, Кузни, Кругосветник, Все биомы", 5, O),
        ("@Пандора", 0, M), ("Пандора", 3, M), ("@финал", 0, M)],
}
CAP = {1: 10, 2: 20, 3: 30}
for act in (1, 2, 3):
    tot = sum(v for _, v, _ in SEQ[act])
    mand = sum(v for _, v, c in SEQ[act] if c == M)
    lik = sum(v for _, v, c in SEQ[act] if c in (M, L))
    print(f"- Акт {act}: обязательных по сюжету {mand}, с вероятными {lik}, всего {tot}; прирост потолка 10.")
print()


def run(levels_kind, bank_on, floor_on, newcomer_act=None):
    take = {"минимум": (M,), "сюжет + вероятное": (M, L), "всё": (M, L, O)}[levels_kind]
    lvl, bank, marks = 0, 0, {}
    for act in (1, 2, 3):
        cap = CAP[act]
        if newcomer_act is not None and act < newcomer_act:
            continue
        if newcomer_act is not None and act == newcomer_act:
            lvl = CAP[act - 1]
        if act > 1:
            if floor_on:
                lvl = max(lvl, CAP[act - 1])
            if bank_on:
                add = min(bank, cap - lvl)
                lvl += add
                bank = 0
            marks[f"старт {act}"] = lvl
        for name, v, cat in SEQ[act]:
            if name.startswith("@"):
                marks[name[1:]] = lvl
                continue
            if cat not in take:
                continue
            lvl += v
            if lvl > cap:
                if bank_on:
                    bank = min(3, bank + lvl - cap)
                lvl = cap
    return marks


cols = ["Цербер", "старт 2", "1-й хозяин", "Танатос", "старт 3", "Пандора", "финал"]
rows = []
for kind in ("минимум", "сюжет + вероятное", "всё"):
    for bank_on, floor_on, tag in ((True, False, "копилка ≤ 3 (предложение 04)"), (True, True, "копилка + пол акта")):
        m = run(kind, bank_on, floor_on)
        rows.append([kind, tag] + [m.get(c, "—") for c in cols])
for act in (2, 3):
    m = run("сюжет + вероятное", True, False, newcomer_act=act)
    rows.append([f"новичок в акте {act}", "старт с 10 / 20 (04, 10.3)"] + [m.get(c, "—") for c in cols])
table(["Игрок", "Правило"] + [f"ур. у «{c}»" for c in cols], rows)
print("«ур. у босса» — уровень до его уровней. «Пол акта» — на переходе в акт K любой персонаж ниже потолка акта K − 1 "
      "поднимается до него (как новичок в 10.3).\n")

# ---------------------------------------------------------------------------------------------
# 7. Комиксы
# ---------------------------------------------------------------------------------------------
print("## 7. Комиксы\n")
SC = [("Свадьба и змея", 5, "личная"), ("Песнь и разлом", 4, "местная + мировая"), ("Смерть в цепях", 2, "мировая"),
      ("Гефест", 1, "местная"), ("Новые Врата", 1, "мировая"), ("Концовка A или B", 2, "мировая"), ("Эпилог", 1, "мировая")]
rows = [[n, p, a, f"{p * 15}–{p * 25}", "да" if p * 25 > 90 else "нет"] for n, p, a in SC]
table(["Сцена", "Страниц", "Охват", "Чтение, с (15–25 с/стр, 12.2)", "Дольше защиты 90 с?"], rows)
pages = sum(p for _, p, _ in SC)
print(f"За прохождение {pages} страниц ≈ {pages * 15 / 60:.0f}–{pages * 25 / 60:.0f} мин чтения на 34–42 ч игры "
      f"(≈ {pages * 20 / 60 / (38 * 60) * 100:.2f} % времени); нарисовать 18 страниц и 2 панели-варианта.\n")

# ---------------------------------------------------------------------------------------------
# 8. Волшебный лес вокруг закрытых порталов
# ---------------------------------------------------------------------------------------------
print("## 8. Роща вокруг закрытого портала: 200 или 120\n")
conv = 0.20 + 0.17 + 0.06   # луга, лес, джунгли (01, раздел 3)
rows = []
for r in (200, 120):
    share = math.pi * r * r / (W * W)
    per = share * conv
    n_cap = 0.06 / per
    rows.append([r, f"{share * 100:.2f} %", f"{per * 100:.2f} %", f"{0.04 / per:.0f}", f"{n_cap:.0f}",
                 f"{n_cap / 10:.1f}"])
table(["Радиус", "Круг от планеты", "Преображается (луга+лес+джунгли = 43 %)", "Порталов до +4 % (оценка 01)",
       "Порталов до потолка 10 % (6 % сверх очагов)", "На каком круге потолок"], rows)
print("В акте II закрывается не 10–12 порталов (на это рассчитан 01), а 40 при четырёх кругах и 50–60 с поражениями. "
      "Пересечения рощ не учтены — реальный рост медленнее на 10–20 %.\n")
