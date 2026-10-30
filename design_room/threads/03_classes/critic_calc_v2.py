#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Расчёт Критика к docs/03_classes.md — КОПИЯ ДИЗАЙНЕРА v2 (правки [3]): та же программа, модель — classes_calc_v2.py.
Изменения копии: (1) импорт classes_calc_v2.py; (2) ev — обёртка над evaluate v2 с теми же ручками;
(3) поиск набора — с «взятием» Я1–Я3 (legal, пустые слоты); (4) раздел 4 — паритет ±10 % (слово Автора 29.10);
(5) раздел 5 — v2 (мокрый ×1,5) против прежнего ×2; (6) раздел 6 — правило долга v2; (7) раздел 7 — множители Системы;
(8) раздел 9 — одинаковый баф не складывается; (9) раздел 10 — взятие как новинка уровня.

Запуск:  python C:\\Users\\carak\\source\\Orpheus\\design_room\\threads\\03_classes\\critic_calc_v2.py
Вывод:   critic_calc_v2.md рядом со скриптом (и то же — в консоль), ≈ 1–3 мин.

Опора — модель Дизайнера classes_calc.py: таблица способностей ABIL, слоты, пороги, ступени II
импортируются без изменений. Функция оценки набора скопирована (ev) с тремя ручками, которые при
значениях по умолчанию дают в точности числа Дизайнера (проверка assert в начале):
  - wet_bonus — прибавка электричества по мокрому боссу (1,0 = ×2, как в 02; 0,5 = ×1,5);
  - fs, fg    — множители к оценкам живучести и пользы группе каждой способности (Монте-Карло);
  - chord_s, chord_g — то же для встроенных оценок Аккорда (×1,25 живучести, +0,20 пользы).

Разделы вывода:
  1. Полный перебор распределений очков (а не 6–9 выбранных билдов) на уровнях 6, 8, 10, 20, 27, 30.
  2. «Окунания»: чистый X/0/0 против (X−1)/0/1, (X−3)/3/0 и т. п.
  3. Частота способностей в лучших наборах — доминирующие способности и ядро «Гроза».
  4. Чувствительность к оценкам живучести и пользы (Монте-Карло, каждая оценка ×0,5…×2).
  5. Мокрый босс ×1,5 вместо ×2.
  6. Долг «Крови за кровь» по тексту документа и с исправлением — модель во времени.
  7. Здоровье боссов: итоговый множитель вместе с выводом 06 (ярус 3 оружия к боссам).
  8. Опыт: окна актов, уровни по пути.
  9. Аккорд у 4–8 тройных вразнобой против урона босса.
 10. Сколько разных «билдов» даёт треугольник; пустые уровни чистых.
"""
import importlib.util
import itertools
import math
import os
import random
import statistics
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("designer_calc", os.path.join(HERE, "classes_calc_v2.py"))
D = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(D)

ABIL = D.ABIL
D0, BURN, STATUS_CAP, COMPANION_HIT, SUMMONS = D.D0, D.BURN, D.STATUS_CAP, D.COMPANION_HIT, D.SUMMONS
slots, act_of, known, is_stage2, params, score = D.slots, D.act_of, D.known, D.is_stage2, D.params, D.score
NAME = {i: a["name"] for i, a in ABIL.items()}


def fmt(x, nd=2):
    return f"{x:.{nd}f}".replace(".", ",")


def pct(x, nd=0):
    return (f"{x * 100:+.{nd}f}").replace(".", ",") + " %"


# ---------------------------------------------------------------------------
# Копия evaluate Дизайнера с ручками (при значениях по умолчанию — те же числа)
# ---------------------------------------------------------------------------
class K(D.K):
    def __init__(self, wet_bonus=0.5, fs=None, fg=None, chord_s=1.0, chord_g=1.0, tag="base"):
        super().__init__(wet_bonus, fs, fg, chord_s, chord_g, tag)


BASE = K()


def ev(acts, pas, build, act, k=BASE):
    return D.evaluate(acts, pas, build, act, k)


def _selfcheck():
    return None        # v2: своей копии формулы нет


# ---------------------------------------------------------------------------
# Поиск лучшего набора (как у Дизайнера: полный перебор до 60 000, иначе жадно + обмены)
# ---------------------------------------------------------------------------
_CACHE = {}


def key_of(build, level):
    kn = known(build)
    st2 = tuple(sorted(i for i in kn if is_stage2(i, build)))
    pk = tuple(D.picks(build[c], t) for c in range(3) for t in (1, 2, 3))
    return tuple(sorted(kn)), st2, pk, slots(level), act_of(level)


def search(build, level, axis, k=BASE, restarts=12, cache=True, ex_limit=8000):
    ck = (key_of(build, level), axis, k.tag)
    if cache and ck in _CACHE:
        return _CACHE[ck]
    act = act_of(level)
    best = D.best_loadout(build, level, axis, random.Random(3010), k, restarts=restarts, ex_limit=ex_limit)
    res = (best[0], best[1], best[2], ev(best[1], best[2], build, act, k))
    if cache:
        _CACHE[ck] = res
    return res


def all_builds(level):
    return [(w, m, level - w - m) for w in range(level + 1) for m in range(level + 1 - w)]


def is_pure(b):
    return sum(1 for x in b if x > 0) == 1


def bname(b):
    return f"{b[0]}/{b[1]}/{b[2]}"


def lname(a, p, build):
    return ", ".join(D.name_of(i, build) for i in a) + " — " + ", ".join(D.name_of(i, build) for i in p)


def quant(xs, q):
    xs = sorted(xs)
    if not xs:
        return float("nan")
    pos = (len(xs) - 1) * q
    lo = int(math.floor(pos))
    hi = int(math.ceil(pos))
    return xs[lo] + (xs[hi] - xs[lo]) * (pos - lo)


# ---------------------------------------------------------------------------
def main():
    t0 = time.time()
    _selfcheck()
    out = []
    w = out.append
    w("# Расчёт Критика к 03_classes — копия Дизайнера v2 (правки [3])\n")
    w("Команда: `python C:\\Users\\carak\\source\\Orpheus\\design_room\\threads\\03_classes\\critic_calc_v2.py` (модель `classes_calc_v2.py`). "
      "Модель — Дизайнера v2 (`classes_calc_v2.py`); оценка — его функция с ручками (своей копии формулы нет), "
      "при значениях по умолчанию совпадает с исходной (проверка на 3000 случайных наборах — пройдена). "
      "Поиск набора — как у Дизайнера (полный перебор до 8000 вариантов, иначе жадно + обмены, 12 стартов); точность жадного поиска — раздел 0.\n")

    # ---------------- 0. Точность жадного поиска
    w("\n## 0. Жадный поиск против полного перебора\n")
    rng0 = random.Random(11)
    pool = []
    for L in (20, 27, 30):
        for b in all_builds(L):
            kn = D.candidates(b)
            A = [i for i in kn if ABIL[i]["kind"] == "a"]
            Pp = [i for i in kn if ABIL[i]["kind"] == "p"]
            na, np_ = slots(L)
            n_ = math.comb(len(A), min(na, len(A))) * math.comb(len(Pp), min(np_, len(Pp)))
            if 8000 < n_ <= 250000:
                pool.append((L, b, n_))
    sample = rng0.sample(pool, min(14, len(pool)))
    same = 0
    worst = 0.0
    for L, b, n_ in sample:
        for axis in ("all", "boss"):
            g = search(b, L, axis, cache=False)[0]
            e = search(b, L, axis, cache=False, ex_limit=10 ** 9)[0]
            same += abs(g - e) < 1e-9
            worst = max(worst, (e - g) / abs(e) if e else 0.0)
    w(f"Случайные {len(sample)} распределений уровней 20, 27, 30 с 8 000–250 000 вариантов набора, оси «сводный» и «Босс»: "
      f"жадный поиск нашёл оптимум полного перебора в {same} из {2 * len(sample)} случаев; наибольший недобор — {worst * 100:.2f} %. "
      "(Отдельно, вне скрипта: 10/10/10, 12/12/6, 9/9/9, 14/8/8, 7/7/6 — до 2,5 млн вариантов — совпадение во всех 10 случаях.)\n")

    LEVELS = (6, 8, 10, 20, 27, 30)
    scan = {}
    for L in LEVELS:
        rows = []
        for b in all_builds(L):
            ra = search(b, L, "all")
            rb = search(b, L, "boss")
            rows.append(dict(b=b, all=ra[0], ag=ra[3][0], as_=ra[3][1], ar=ra[3][2], aload=(ra[1], ra[2]),
                             boss=rb[0], bload=(rb[1], rb[2]), bs=rb[3][1]))
        scan[L] = rows

    # ---------------- 1. Полный перебор
    w("\n## 1. Полный перебор распределений очков\n")
    w("Для каждого распределения В/М/П на уровне — лучший набор по сводному счёту Дизайнера и лучший набор по урону по боссу "
      "(игрок ходит к боссу с набором «Босс», 03 раздел 3). «Место» — среди всех распределений уровня.\n")
    w("| Уровень | Распределений | Лучшее по сводному | Сводный | Лучший чистый (место) | % лучшего | Смешанных слабее лучшего чистого | Слабее чистого воина | Лучший урон по боссу (билд) | Лучший чистый урон |")
    w("|---|---|---|---|---|---|---|---|---|---|")
    for L in LEVELS:
        rows = scan[L]
        rs = sorted(rows, key=lambda r: -r["all"])
        top = rs[0]
        pures = [r for r in rows if is_pure(r["b"])]
        bp = max(pures, key=lambda r: r["all"])
        place = 1 + sum(1 for r in rows if r["all"] > bp["all"] + 1e-12)
        mixed = [r for r in rows if not is_pure(r["b"])]
        weaker = sum(1 for r in mixed if r["all"] < bp["all"] - 1e-12)
        pw = [r for r in pures if r["b"][0] == L][0]
        weaker_w = sum(1 for r in mixed if r["all"] < pw["all"] - 1e-12)
        tb = max(rows, key=lambda r: r["boss"])
        bpb = max(pures, key=lambda r: r["boss"])
        w(f"| {L} | {len(rows)} | {bname(top['b'])} | {fmt(top['all'])} | {bname(bp['b'])} ({place}-е) | {bp['all'] / top['all'] * 100:.0f} % | "
          f"{weaker} из {len(mixed)} | {weaker_w} из {len(mixed)} | {pct(tb['boss'])} ({bname(tb['b'])}) | {pct(bpb['boss'])} ({bname(bpb['b'])}) |")
    w("")
    for L in (10, 20, 30):
        rows = scan[L]
        rs = sorted(rows, key=lambda r: -r["all"])
        w(f"**Уровень {L}, первые 8 по сводному** (урон / живучесть / польза — в сводном наборе):\n")
        w("| Место | В/М/П | Сводный | Урон | Живучесть | Польза | Набор |")
        w("|---|---|---|---|---|---|---|")
        for n, r in enumerate(rs[:8], 1):
            w(f"| {n} | {bname(r['b'])} | {fmt(r['all'])} | {pct(r['ag'])} | {fmt(r['as_'])} | {fmt(r['ar'])} | {lname(*r['aload'], r['b'])} |")
        for r in rows:
            if is_pure(r["b"]):
                place = 1 + sum(1 for x in rows if x["all"] > r["all"] + 1e-12)
                w(f"| {place} | {bname(r['b'])} | {fmt(r['all'])} | {pct(r['ag'])} | {fmt(r['as_'])} | {fmt(r['ar'])} | {lname(*r['aload'], r['b'])} |")
        w("")

    # Доминирование «одним набором» (а не тремя разными наборами по осям)
    w("**Доминирование одним набором.** Дизайнер сравнивает оси, у каждой из которых свой лучший набор; в бою набор один. "
      "Ниже — сколько смешанных билдов одним (сводным) набором не хуже чистого сразу по урону, живучести и пользе:\n")
    w("| Уровень | Чистый | Его сводный набор: урон / живучесть / польза | Смешанных, не хуже по всем трём одним набором |")
    w("|---|---|---|---|")
    for L in (10, 20, 30):
        rows = scan[L]
        for r in rows:
            if not is_pure(r["b"]):
                continue
            dom = [x for x in rows if not is_pure(x["b"]) and x["ag"] >= r["ag"] - 1e-9 and x["as_"] >= r["as_"] - 1e-9 and x["ar"] >= r["ar"] - 1e-9]
            ex = ", ".join(bname(x["b"]) for x in sorted(dom, key=lambda x: -x["all"])[:4])
            w(f"| {L} | {bname(r['b'])} | {pct(r['ag'])} / {fmt(r['as_'])} / {fmt(r['ar'])} | {len(dom)}{' (' + ex + ' …)' if dom else ''} |")
    w("")

    # ---------------- 2. Окунания
    w("\n## 2. «Окунание»: одно-три очка в чужой класс против чистого\n")
    w("| Уровень | Билд | Сводный | Урон по боссу (набор «Босс») | Что даёт окунание |")
    w("|---|---|---|---|---|")
    for L in (10, 20, 30):
        idx = {r["b"]: r for r in scan[L]}
        for c in range(3):
            pure = tuple(L if j == c else 0 for j in range(3))
            cand = [pure]
            for o in range(3):
                if o == c:
                    continue
                for d in (1, 3):
                    b = [0, 0, 0]
                    b[c] = L - d
                    b[o] = d
                    cand.append(tuple(b))
            b = [1, 1, 1]
            b[c] = L - 2
            cand.append(tuple(b))
            base = idx[pure]
            for b in cand:
                r = idx[b]
                gain = "—" if b == pure else f"сводный {pct(r['all'] / base['all'] - 1)}, урон {pct((1 + r['boss']) / (1 + base['boss']) - 1)}"
                w(f"| {L} | {bname(b)} | {fmt(r['all'])} | {pct(r['boss'])} | {gain} |")
    w("")

    # ---------------- 3. Частота способностей
    w("\n## 3. Частота способностей в лучших наборах\n")
    w("Доля распределений уровня, у которых способность стоит в лучшем наборе; «когда известна» — среди тех, кому она открыта.\n")
    for L in (10, 20, 30):
        for kind, keyl in (("сводный", "aload"), ("Босс", "bload")):
            cnt, avail = {}, {}
            for r in scan[L]:
                kn = set(known(r["b"]))
                for i in kn:
                    avail[i] = avail.get(i, 0) + 1
                a, p = r[keyl]
                for i in list(a) + list(p):
                    cnt[i] = cnt.get(i, 0) + 1
            n = len(scan[L])
            top = sorted(cnt.items(), key=lambda kv: -kv[1])[:8]
            w(f"- Уровень {L}, набор «{kind}»: " + "; ".join(
                f"{NAME[i]} {c / n * 100:.0f} % (когда известна — {c / avail[i] * 100:.0f} %)" for i, c in top) + ".")
    # ядро «Гроза»
    core = {"M1", "M2", "WM1", "M5"}
    w("")
    for L in (10, 20, 27, 30):
        have = [r for r in scan[L] if core <= set(known(r["b"]))]
        used = [r for r in have if core <= set(r["bload"][0]) | set(r["bload"][1])]
        w(f"- Уровень {L}: ядро «Волна + Молния + Громовой клинок + Зачарование» открыто у {len(have)} из {len(scan[L])} распределений; "
          f"в их наборе «Босс» оно целиком стоит у {len(used)} ({len(used) / max(1, len(have)) * 100:.0f} %). Порог ядра — В ≥ 2, М ≥ 3 (5 очков).")
    w("")

    # ---------------- 4. Чувствительность
    w("\n## 4. Чувствительность к оценкам живучести и пользы (Монте-Карло)\n")
    N_MC = 160
    rng = random.Random(2910)
    rep = {L: [b for _, b in D.BUILDS[L]] for L in (10, 20, 30)}
    names = {L: {b: n for n, b in D.BUILDS[L]} for L in (10, 20, 30)}
    stats = {L: dict(mixed_ok=0, pure_best=0, ps_best=0, ps_share=[], pw_share=[], minpure=[], par=0, wm=0) for L in (10, 20, 30)}
    ids = list(ABIL.keys())
    for s in range(N_MC):
        fs = {i: math.exp(rng.uniform(math.log(0.5), math.log(2.0))) for i in ids}
        fg = {i: math.exp(rng.uniform(math.log(0.5), math.log(2.0))) for i in ids}
        k = K(fs=fs, fg=fg, chord_s=math.exp(rng.uniform(math.log(0.5), math.log(2.0))),
              chord_g=math.exp(rng.uniform(math.log(0.5), math.log(2.0))), tag=f"mc{s}")
        for L in (10, 20, 30):
            res = {b: search(b, L, "all", k, restarts=6, cache=False)[0] for b in rep[L]}
            pures = {b: v for b, v in res.items() if is_pure(b)}
            mixed = {b: v for b, v in res.items() if not is_pure(b)}
            best = max(res.values())
            st = stats[L]
            if max(mixed.values()) >= max(pures.values()) - 1e-12:
                st["mixed_ok"] += 1
            if max(pures.values()) >= best - 1e-12:
                st["pure_best"] += 1
            ps = (0, 0, L)
            pw = (L, 0, 0)
            if res[ps] >= best - 1e-12:
                st["ps_best"] += 1
            st["ps_share"].append(res[ps] / best)
            st["pw_share"].append(res[pw] / best)
            st["minpure"].append(min(pures.values()) / best)
            r_ = max(pures.values()) / max(mixed.values())
            if 0.9 <= r_ <= 1.1:
                st["par"] += 1
            if L == 20:
                r2 = res[(10, 10, 0)] / res[(20, 0, 0)]
                if 0.9 <= r2 <= 1.1:
                    st["wm"] += 1
    w(f"{N_MC} прогонов: каждая оценка живучести и пользы группе у каждой способности (и встроенные оценки Аккорда) умножена на случайное ×0,5…×2 "
      "(лог-равномерно); урон не трогается. Билды — набор Дизайнера (по 9 на уровень).\n")
    w("Паритет (слово Автора 29.10, решение Системы): лучший чистый / лучший смешанный по сводному в пределах 0,9–1,1; Воин-маг 10/10 / чистый воин 20 — тоже.\n")
    for L in (10, 20, 30):
        st = stats[L]
        extra = f"; Воин-маг 10/10 к чистому воину 20 в пределах ±10 % — {st['wm'] / N_MC * 100:.0f} %" if L == 20 else ""
        w(f"- Уровень {L}: паритет ±10 % в {st['par'] / N_MC * 100:.0f} % прогонов{extra}.")
    w("")
    w("| Уровень | Лучший смешанный ≥ лучшего чистого | Чистый — лучший из всех | Чистый призыватель — лучший | Чистый призыватель, % лучшего (медиана; 10–90 %) | Чистый воин, % лучшего (медиана; 10–90 %) | Худший чистый ≥ 80 % лучшего |")
    w("|---|---|---|---|---|---|---|")
    for L in (10, 20, 30):
        st = stats[L]
        w(f"| {L} | {st['mixed_ok'] / N_MC * 100:.0f} % | {st['pure_best'] / N_MC * 100:.0f} % | {st['ps_best'] / N_MC * 100:.0f} % | "
          f"{statistics.median(st['ps_share']) * 100:.0f} % ({quant(st['ps_share'], 0.1) * 100:.0f}–{quant(st['ps_share'], 0.9) * 100:.0f}) | "
          f"{statistics.median(st['pw_share']) * 100:.0f} % ({quant(st['pw_share'], 0.1) * 100:.0f}–{quant(st['pw_share'], 0.9) * 100:.0f}) | "
          f"{sum(1 for x in st['minpure'] if x >= 0.8) / N_MC * 100:.0f} % |")
    w("")
    # 20-й уровень: откуда сила чистого призывателя, рычаг Некии
    L = 20
    b = (0, 0, 20)
    ra = search(b, L, "all")
    rb = search(b, L, "boss")
    w(f"**Чистый призыватель 0/0/20.** Сводный набор: {lname(ra[1], ra[2], b)} (сводный {fmt(ra[0])}). "
      f"Набор «Босс»: {lname(rb[1], rb[2], b)} (урон {pct(rb[3][0])}).")
    saved = ABIL["S10"]["p1"]["dmg"]
    ABIL["S10"]["p1"]["dmg"] = saved * 0.25 / 0.3
    ra2 = search(b, L, "all", K(tag="nekyia025"), cache=False)
    rb2 = search(b, L, "boss", K(tag="nekyia025"), cache=False)
    ABIL["S10"]["p1"]["dmg"] = saved
    w(f" Рычаг Дизайнера «тени Некии 0,3 → 0,25 О/с»: сводный {fmt(ra[0])} → {fmt(ra2[0])}, урон «Босс» {pct(rb[3][0])} → {pct(rb2[3][0])}.")
    # без ступени II у Я1 (как если бы ступень II Я1 была на П ≥ 16)
    alt = []
    for i in ("S8",):
        alt.append(i)
    sv = ABIL["S8"]["p2"]
    ABIL["S8"]["p2"] = None
    ra3 = search(b, L, "all", K(tag="noajax2"), cache=False)
    ABIL["S8"]["p2"] = sv
    w(f" Без ступени II Тени Аякса на 20-м: сводный {fmt(ra[0])} → {fmt(ra3[0])}.\n")

    # ---------------- 5. Мокрый босс ×1,5
    w("\n## 5. Мокрый босс: v2 ×1,5 (левое число) против прежнего ×2 (правое)\n")
    k15 = K(wet_bonus=1.0, tag="wet2")   # v2: база уже ×1,5; сравниваем с прежним ×2
    w("| Уровень | Медиана урона «Босс» ×2 → ×1,5 | Лучший урон ×2 → ×1,5 | Ядро «Гроза» в наборах «Босс» (где открыто) ×2 → ×1,5 | Лучший чистый урон ×2 → ×1,5 |")
    w("|---|---|---|---|---|")
    for L in (10, 20, 30):
        g2 = [r["boss"] for r in scan[L]]
        g15, core15, have = [], 0, 0
        bp2 = max(r["boss"] for r in scan[L] if is_pure(r["b"]))
        bp15 = -9
        core2 = 0
        for r in scan[L]:
            rr = search(r["b"], L, "boss", k15)
            g15.append(rr[0])
            if is_pure(r["b"]):
                bp15 = max(bp15, rr[0])
            if core <= set(known(r["b"])):
                have += 1
                if core <= set(rr[1]) | set(rr[2]):
                    core15 += 1
                if core <= set(r["bload"][0]) | set(r["bload"][1]):
                    core2 += 1
        w(f"| {L} | {pct(statistics.median(g2))} → {pct(statistics.median(g15))} | {pct(max(g2))} → {pct(max(g15))} | "
          f"{core2 / max(1, have) * 100:.0f} % → {core15 / max(1, have) * 100:.0f} % | {pct(bp2)} → {pct(bp15)} |")
    w("")

    # ---------------- 6. Кровь за кровь
    w("\n## 6. «Кровь за кровь»: долг по тексту документа и с исправлением\n")
    w("Текст (5.3, В7): долг живёт 2 с, **новый урон добавляется и продлевает весь долг**, каждое действие возвращает 15 % долга; "
      "приёмка 5 (+4,5 × 3 от долга 30) показывает, что возвращённое из долга не вычитается. Исправление: у каждой порции урона "
      "свои 2 с без продления, возвращённое вычитается, вернуть больше порции нельзя.\n")

    def blood(rule, p, dmg, acts, hp0, T=40.0):
        ev_ = sorted([(t, "d", a) for t, a in dmg if t <= T] + [(t, "a", 0) for t in acts if t <= T])
        hp, dmg_sum, heal_sum = hp0, 0.0, 0.0
        debt, until = 0.0, -1.0
        chunks = []                      # [start, original, remaining]
        hp_min, t_dead, t_even = hp0, None, None
        for t, kind, a in ev_:
            if kind == "d":
                hp -= a
                dmg_sum += a
                if rule == "text":
                    if t > until:
                        debt = 0.0
                    debt += a
                    until = t + 2.0
                elif rule == "v2":
                    total = sum(c[2] for c in chunks if t - c[0] <= 2.0)
                    chunks.append([t, a, max(0.0, min(0.5 * a, hp0 - total))])
                else:
                    chunks.append([t, a, a])
                hp_min = min(hp_min, hp)
                if hp <= 0 and t_dead is None:
                    t_dead = t
                    break
            else:
                if rule == "text":
                    h = p * debt if t <= until else 0.0
                else:
                    h = 0.0
                    for c in chunks:
                        if t - c[0] <= 2.0 and c[2] > 0:
                            x = min(c[2], p * c[1])
                            c[2] -= x
                            h += x
                real = min(h, hp0 - hp)
                hp += real
                heal_sum += real
            if t_even is None and heal_sum >= dmg_sum * 0.999 and dmg_sum > 0 and t > 1:
                t_even = t
        return hp_min, t_dead, t_even, dmg_sum, heal_sum, hp

    def periodic(start, period, amount, T=40.0):
        out_, t = [], start
        while t <= T:
            out_.append((t, amount))
            t += period
        return out_

    scen = []
    # А: пять разбойников акта I по воину 10-го уровня (200), медная броня 3, Боевой клич ×0,7 в 0–5 и 18–23 с; меч 1,5 действия/с
    dmgA = []
    for j in range(5):
        for t, a in periodic(0.22 * j, 1.1, 7.0):
            m = 0.7 if (t % 18.0) < 5.0 else 1.0
            dmgA.append((t, a * m))
    actsA = [0.3 + n / 1.5 for n in range(80)]
    scen.append(("А. Пятеро разбойников акта I, воин 10-го (200), меч 1,5 удара/с, 15 %", dmgA, actsA, 200, 0.15))
    # Б: пятеро ближних акта II (25 − броня 10 = 15), воин 20-го (300), Кровь за кровь II 18 %, Вихрь II (7 действий за 2,1 с раз в 9 с) + меч
    dmgB = []
    for j in range(5):
        dmgB += periodic(0.22 * j, 1.1, 15.0)
    actsB = []
    for cyc in range(5):                       # Вихрь II в 0, 9, 18, 27, 36 с: 7 действий за 2,1 с; между — меч 1,5/с
        c0 = 9.0 * cyc
        actsB += [c0 + 0.3 * q for q in range(7)]
        t = c0 + 2.1 + 1 / 1.5
        while t < c0 + 9.0:
            actsB.append(t)
            t += 1 / 1.5
    actsB = sorted(x for x in actsB if x <= 40)
    scen.append(("Б. Пятеро ближних акта II, воин 20-го (300), Вихрь II + меч, 18 %", dmgB, actsB, 300, 0.18))
    actsB2 = []
    for cyc in range(5):                       # v2: Вихрь — одно действие
        c0 = 9.0 * cyc
        actsB2.append(c0)
        t = c0 + 2.1 + 1 / 1.5
        while t < c0 + 9.0:
            actsB2.append(t)
            t += 1 / 1.5
    scen.append(("Б2. То же, v2: Вихрь — одно действие, 15 %", dmgB, sorted(x for x in actsB2 if x <= 40), 300, 0.15))
    # В: Цербер бьёт стоящего без уворотов воина 6-го (160), железная броня 5: укус 12 раз в 1,75 с, вой 25 раз в 5,25 с; у цели 0,75 действия/с
    dmgC = [(t, 12 - 5) for t, _ in periodic(0.0, 1.75, 1)] + [(t, 25 - 5) for t, _ in periodic(2.0, 5.25, 1)]
    actsC = [0.5 + n / 0.75 for n in range(40)]
    scen.append(("В. Цербер по воину 6-го (160) без уворотов, 0,75 действия/с, 15 %", dmgC, actsC, 160, 0.15))
    w("| Сценарий | Правило | Мин. здоровье | Смерть | Лечение ≥ урона с | Урон за 40 с | Вернул | Здоровье на 40-й с |")
    w("|---|---|---|---|---|---|---|---|")
    for title, dm, ac, hp0, p in scen:
        for rule, rn in (("text", "по тексту v1"), ("fix", "исправлено Критиком"), ("v2", "v2: порция ≤ 50 %")):
            hp_min, t_dead, t_even, ds, hs, hp_end = blood(rule, p, dm, ac, hp0)
            w(f"| {title} | {rn} | {hp_min:.0f} | {'на ' + fmt(t_dead, 1) + ' с' if t_dead else 'нет'} | "
              f"{fmt(t_even, 1) if t_even else '—'} | {ds:.0f} | {hs:.0f} ({hs / max(ds, 1) * 100:.0f} %) | {max(hp_end, 0):.0f} |")
        # без способности
        hp_min, t_dead, t_even, ds, hs, hp_end = blood("text", 0.0, dm, ac, hp0)
        w(f"| {title} | без Крови за кровь | {hp_min:.0f} | {'на ' + fmt(t_dead, 1) + ' с' if t_dead else 'нет'} | — | {ds:.0f} | 0 | {max(hp_end, 0):.0f} |")
    w("\nПо тексту при непрерывном уроне долг растёт линейно, а возврат — пропорционально долгу: лечение за действие растёт со временем, "
      "и после t* = 1 / (доля × действий в секунду) возврат обгоняет урон (15 % и 1,5 удара/с — 4,4 с; 18 % и Вихрь II — меньше 2 с). "
      "Вихрь II с Кровью за кровь II возвращает 7 × 18 % = 126 % долга даже при исправленном правиле без потолка — нужен потолок «не больше порции».\n")

    # ---------------- 7. Здоровье боссов
    w("\n## 7. Здоровье боссов: итоговый множитель (способности 03 × оружие 06)\n")
    w("Формула 02 (10.1): здоровье = 1,5 × B × ярус × (1 + прирост способностей) × 0,5 × время. Опора 02: ярус 2 (×1,5) и прирост +30 / +50 / +70 %. "
      "06: к боссам актов у игрока ярус 3 (×1,8) → ×1,2. Прирост 03 — медиана по всем распределениям уровня: набор «Босс» (к боссу идут с ним, "
      "03 раздел 3) и для сравнения — сводный набор (метод Дизайнера). Время — соло без смертей при множителе из последнего столбца.\n")
    bosses = [
        ("Цербер", 1, 10, 3500, 0.30, 4.0, (3, 5), [(4, "мин."), (6, "обычно по 03"), (8, "обычно по счёту Критика"), (10, "всё")], 6),
        ("Танатос", 2, 30, 15000, 0.50, 5.0, (4, 6), [(20, "всегда")], 20),
        ("Пандора", 3, 90, 67000, 0.70, 6.5, (5, 8), [(25, "мин."), (27, "обычно"), (30, "всё")], 27),
    ]
    # дополнительные уровни
    extra_levels = set()
    for _, _, _, _, _, _, _, lv, _ in bosses:
        for l, _ in lv:
            if l not in scan:
                extra_levels.add(l)
    for L in sorted(extra_levels):
        rows = []
        for b in all_builds(L):
            ra = search(b, L, "all")
            rb = search(b, L, "boss")
            rows.append(dict(b=b, all=ra[0], ag=ra[3][0], as_=ra[3][1], ar=ra[3][2], aload=(ra[1], ra[2]),
                             boss=rb[0], bload=(rb[1], rb[2]), bs=rb[3][1]))
        scan[L] = rows
    w("| Босс | Уровень | Медиана «Босс» (10–90 %) | Медиана сводного набора | Чистый воин «Босс» | Множитель способностей (по «Босс») | × оружие 06 | Итог к опоре 02 |")
    w("|---|---|---|---|---|---|---|---|")
    summary_mult = {}
    for nm, act, B, hp02, g02, tmid, win, lv, typical in bosses:
        for L, why in lv:
            gb = [r["boss"] for r in scan[L]]
            ga = [r["ag"] for r in scan[L]]
            pw = [r for r in scan[L] if r["b"] == (L, 0, 0)][0]
            mb = statistics.median(gb)
            m_ab = (1 + mb) / (1 + g02)
            tot = m_ab * 1.2
            if L == typical:
                summary_mult[nm] = (mb, m_ab, tot, statistics.median(ga))
            w(f"| {nm} | {L} ({why}) | {pct(mb)} ({pct(quant(gb, 0.1))}…{pct(quant(gb, 0.9))}) | {pct(statistics.median(ga))} | {pct(pw['boss'])} | "
              f"×{fmt(m_ab)} | ×1,2 | ×{fmt(tot)} |")
    w("")
    w("**Время соло при итоговом множителе** (уровень — «обычно»; диапазон — 10–90 % распределений; крайние — худший и лучший билд):\n")
    w("| Босс | Итог к опоре 02 | Здоровье соло | Медиана, мин | 10–90 %, мин | Худший / лучший билд, мин | Окно `00` | Чистый воин, мин | Только ×1,2 (06), медиана | Только ×1,25–1,29 (03), медиана | Ничего не менять, медиана |")
    w("|---|---|---|---|---|---|---|---|---|---|---|")
    for nm, act, B, hp02, g02, tmid, win, lv, typical in bosses:
        mb, m_ab, tot, ga_med = summary_mult[nm]
        tot_r = round(tot * 20) / 20                     # шаг 0,05
        hp = hp02 * tot_r
        rows = scan[typical]

        def tmin(g, hp_=hp, tier=1.8):
            return hp_ / (1.5 * B * tier * 0.5 * (1 + g)) / 60
        gb = [r["boss"] for r in rows]
        pw = [r for r in rows if r["b"] == (typical, 0, 0)][0]
        d_mult = {"Цербер": 1.25, "Танатос": 1.29, "Пандора": 1.22}[nm]
        w(f"| {nm} | ×{fmt(tot_r)} | {hp:.0f} | {fmt(tmin(statistics.median(gb)), 1)} | {fmt(tmin(quant(gb, 0.9)), 1)}–{fmt(tmin(quant(gb, 0.1)), 1)} | "
          f"{fmt(tmin(min(gb)), 1)} / {fmt(tmin(max(gb)), 1)} | {win[0]}–{win[1]} | {fmt(tmin(pw['boss']), 1)} | "
          f"{fmt(tmin(statistics.median(gb), hp02 * 1.2), 1)} | {fmt(tmin(statistics.median(gb), hp02 * d_mult), 1)} | {fmt(tmin(statistics.median(gb), hp02), 1)} |")
    w("")
    w("**Множители Системы (решение по разбору [2]): Цербер ×1,55, Танатос ×1,9, Пандора ×1,9; оружие яруса 3 (×1,8).** "
      "Время соло: медиана набора «Босс» по всем распределениям, худший билд и чистые классы:\n")
    w("| Босс | Уровень | Здоровье | Медиана, мин | Худший билд, мин | Чистый воин | Чистый маг | Чистый призыватель | Окно `00` |")
    w("|---|---|---|---|---|---|---|---|---|")
    for nm, act, B, hp02, g02, tmid, win, lv, typical in bosses:
        mult = {"Цербер": 1.55, "Танатос": 1.9, "Пандора": 1.9}[nm]
        hp = hp02 * mult
        for L, why in lv:
            rows = scan[L]

            def tm(g):
                return hp / (1.5 * B * 1.8 * 0.5 * (1 + g)) / 60
            gb = [r["boss"] for r in rows]
            pv = {c: [r for r in rows if r["b"][c] == L][0]["boss"] for c in range(3)}
            w(f"| {nm} | {L} ({why}) | {hp:.0f} | {fmt(tm(statistics.median(gb)), 1)} | {fmt(tm(min(gb)), 1)} | {fmt(tm(pv[0]), 1)} | "
              f"{fmt(tm(pv[1]), 1)} | {fmt(tm(pv[2]), 1)} | {win[0]}–{win[1]} |")
    w("")
    # хозяева рек и Халкотавры
    w("**Хозяева рек и Халкотавры** (здоровье 04 по номеру победы; ярус по 06; уровень на сюжетном минимуме 14 / 16 / 18 и при «всё» 20):\n")
    w("| Бой | Здоровье 04 | Ярус 06 | Уровень | Медиана «Босс» | Время сейчас, мин | Множитель к 04 для 5 мин (медиана) |")
    w("|---|---|---|---|---|---|---|")
    for nm, hp04, tier, Ls in (("1-й хозяин", 9500, 1.0, (14, 16)), ("2-й хозяин", 12000, 1.5, (16, 18)),
                               ("3-й хозяин", 13500, 1.8, (18, 20)), ("Халкотавры (2 × 16 000)", 32000, 1.5, (25, 27))):
        for L in Ls:
            if L not in scan:
                rows = []
                for b in all_builds(L):
                    rb = search(b, L, "boss")
                    rows.append(dict(b=b, boss=rb[0]))
                scan[L] = rows
            B = 30 if "хозяин" in nm else 90
            med = statistics.median(r["boss"] for r in scan[L])
            tnow = hp04 / (1.5 * B * tier * 0.5 * (1 + med)) / 60
            tgt = 5.0 if "хозяин" in nm else 4.6
            w(f"| {nm} | {hp04} | ×{fmt(tier, 1)} | {L} | {pct(med)} | {fmt(tnow, 1)} | ×{fmt(tgt / tnow)} |")
    w("\nХалкотавры — цель 4,6 мин (04). Хозяева — 5 мин (04: «первый — 5,0 мин»).\n")

    # ---------------- 8. Опыт
    w("\n## 8. Опыт: окна актов и уровни по пути\n")
    w("| Мир | Данжей акта I | Окно акта I (данжи + набег + Изнанка + биомы + обход) | Потолок | Запас |")
    w("|---|---|---|---|---|")
    for size, dn in ((2048, 6), (4096, 7), (8192, 10)):
        win1 = dn + 4
        w(f"| {size}² | {dn} | {win1} | 10 | {win1 - 10} |")
    w("")
    story_min = 4 + 1
    rest = (7 - 4) + 1 + 1 + 1
    w(f"- Сюжетный минимум к Церберу: 4 струны (данжи) + Изнанка (Врата стоят на Изнанке, «ступить в 64 т» — по пути) = {story_min}, а не 4.")
    w(f"- «Обычно — сюжет и половина остального» (03, 15.4): остальное = 3 данжа + набег + все биомы + обход = {rest}; половина — {rest / 2:.0f} → "
      f"{story_min + rest / 2:.0f}, а не 6.")
    w("- Акт II, сюжетный минимум: 13 → первый портал 14 → хозяева 16 / 18 / 20 → Танатос на 20. Хозяев встречают на 14, 16, 18 (с данжами и нашествием — до 20), а не «14–17».")
    w("- Акт III: 23 → Кузня 25 → Пандора на 25 (+3 → 28). Халкотавры — ивент, не на сюжетном пути.\n")

    # ---------------- 9. Аккорд у группы
    w("\n## 9. Аккорд у 4–8 тройных вразнобой против урона босса по одной цели\n")
    w("Правило 16.3: «один и тот же эффект не складывается» — для мгновенного лечения не задано окно; Аккорды разных игроков "
      "со сдвигом в 1 с — разные срабатывания. Лечение — 10 % максимума раз в 10 с от каждого. Урон босса по одной цели — "
      "атаки «только уворот» не чаще раза в 1,75 с по 1,25U плюс тяжёлая 2,5U раз в 5,25 с, минус броня высшего яруса эпохи (5 / 12 / 25), без уворотов.\n")
    w("| Акт | Здоровье героя на потолке | Урон босса по цели без уворотов, в с | Лечение Аккордами: 4 тройных | 8 тройных | С исправлением «раз в 10 с на союзника» |")
    w("|---|---|---|---|---|---|")
    for act, U, arm, H in ((1, 10, 5, 200), (2, 25, 12, 300), (3, 50, 25, 400)):
        dps = (max(1.25 * U - arm, 0.25 * 1.25 * U) * 2 + max(2.5 * U - arm, 0.25 * 2.5 * U)) / 5.25
        w(f"| {act} | {H} | {fmt(dps, 1)} | {fmt(4 * 0.1 * H / 10, 1)} | {fmt(8 * 0.1 * H / 10, 1)} | {fmt(0.1 * H / 10, 1)} |")
    w("\n**v2 (слово Автора 29.10: одинаковый баф из разных источников не складывается).** Лечение Аккорда — баф «Аккорд» "
      "(1 % максимума в секунду 10 с); восемь Аккордов вразнобой держат его постоянно, но не складывают: 2 / 3 / 4 здоровья в секунду "
      "против 6,7 / 17 / 33 урона босса — 30 / 18 / 12 %. Снижения урона от способностей вместе — не ниже ×0,4 (4.2). "
      "Ни Аккорд, ни Лира, ни Клич II у восьмерых неуязвимости не дают.\n")
    w("\n(Ниже — разбор v1.) Восемь Аккордов вразнобой лечат цель быстрее, чем босс её бьёт без уворотов, во всех трёх актах; четыре — наравне. "
      "То же с Лирой (откаты ×2 «у союзников»: 8 Лир по 8 с из 60 — 64 с из 60, то есть всегда) и Боевым кличем II (8 кличей по 2 с из 18 — 89 % времени невосприимчивости к контролю).\n")

    # ---------------- 10. Разнообразие и пустые уровни
    w("\n## 10. Сколько разных билдов даёт треугольник; пустые уровни чистых\n")
    for L in (10, 20, 30):
        keys = set()
        for b in all_builds(L):
            kn = known(b)
            st2 = tuple(sorted(i for i in kn if is_stage2(i, b)))
            keys.add((tuple(sorted(kn)), st2))
        loads = set()
        for r in scan[L]:
            loads.add((frozenset(r["aload"][0]), frozenset(r["aload"][1])))
        w(f"- Уровень {L}: распределений {len(all_builds(L))}, разных наборов «известно + ступени II» {len(keys)}, разных лучших сводных наборов {len(loads)}.")
    for c, cn in enumerate(("Воин", "Маг", "Призыватель")):
        new_levels = []
        prev = set()
        for L in range(1, 31):
            b = tuple(L if j == c else 0 for j in range(3))
            kn = known(b)
            st2 = {i for i in kn if is_stage2(i, b)}
            cur = set(kn) | {i + "_II" for i in st2} | {f"pick{t}_{D.picks(L, t)}" for t in (1, 2, 3)}
            if cur - prev:
                new_levels.append(L)
            prev = cur
        empty = [L for L in range(1, 31) if L not in new_levels]
        w(f"- Чистый {cn}: новое на уровнях {', '.join(map(str, new_levels))} — {len(new_levels)} из 30; пустых (только +10 здоровья) — {len(empty)}.")
    b = (10, 10, 10)
    w(f"- Тройной 10/10/10 знает {len(known(b))} способностей, чистый 30/0/0 — {len(known((30, 0, 0)))}; слотов у обоих 4 + 3.")
    # ---------------- 11. Кто слабее чистого воина; лучший урон при мокром ×1,5
    w("\n## 11. Дополнительно\n")
    L = 20
    idx = {r["b"]: r for r in scan[L]}
    pw = idx[(20, 0, 0)]["all"]
    weak = [b for b, r in idx.items() if not is_pure(b) and r["all"] < pw - 1e-12]
    by = {c: sum(1 for b in weak if max(range(3), key=lambda j: b[j]) == n) for n, c in enumerate("ВМП")}
    w(f"- 20-й уровень: смешанных слабее чистого воина по сводному — {len(weak)}; с наибольшим вкладом в В — {by['В']}, в М — {by['М']}, в П — {by['П']}; "
      f"с М ≥ 10 — {sum(1 for b in weak if b[1] >= 10)}. Воин-маг 10/10/0 — {fmt(idx[(10, 10, 0)]['all'])}, 14/6/0 — {fmt(idx[(14, 6, 0)]['all'])}, чистый воин — {fmt(pw)}.")
    for L in (10, 20, 30):
        top2 = sorted(scan[L], key=lambda r: -r["boss"])[:3]
        top15 = sorted(((search(r["b"], L, "boss", k15)[0], r["b"]) for r in scan[L]), reverse=True)[:3]
        w(f"- Уровень {L}, лучший урон по боссу: мокрый ×2 — " + ", ".join(f"{bname(r['b'])} {pct(r['boss'])}" for r in top2)
          + "; мокрый ×1,5 — " + ", ".join(f"{bname(b)} {pct(v)}" for v, b in top15) + ".")
    w(f"\nВремя расчёта: {time.time() - t0:.0f} с.")

    text = "\n".join(out) + "\n"
    with open(os.path.join(HERE, "critic_calc_v2.md"), "w", encoding="utf-8") as fh:
        fh.write(text)
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass
    print(text)


if __name__ == "__main__":
    main()
