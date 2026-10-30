#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Расчёт к docs/03_classes.md: сильнее ли мультикласс чистого класса.

Запуск:  python C:\\Users\\carak\\source\\Orpheus\\design_room\\threads\\03_classes\\classes_calc.py
Вывод:   classes_calc.md рядом со скриптом (и то же — в консоль).

Модель (все числа — прикидки документа 03, раздел 12):
- Единица урона О — базовый удар оружия в руках (B эпохи оружия x множитель яруса, 06).
- D0 = 0,75 О/с — урон оружия героя по боссу соло: 1,5 О/с x 0,5 (половину боя у цели), как в 02, 10.1.
- Урон по боссу: прирост к D0 от способностей набора: прямой урон (урон x доля применений / откат),
  минус потеря оружейного урона на касты, усиления оружия, подручные (урон x время жизни x 0,85 у цели),
  статусы (горение 0,4U/с = 0,267 О/с; потолок статусов по боссу 0,2 %/с от его здоровья, 02 7.1),
  электричество по мокрому x2 (02 7.2). Огонь и вода в одном наборе мешают друг другу (огонь снимает
  мокрый): доля мокрого и горения x0,6.
- Выживаемость: множитель «эффективного здоровья» = П(1 + вклад способности), минус выносливость,
  которую способности отнимают у уворота (стоимость в секунду / 20 x 0,25).
- Полезность в группе из 4: сумма вкладов в долях «одного товарища» (провокация, снятие контроля,
  лечение, подъём, стены, контроль добавок) — оценка Дизайнера по числам способности.
- Сводный счёт = (1 + прирост урона) x выживаемость x (1 + полезность).
- Набор: активные и пассивные слоты по уровню (03, раздел 3). Для каждого билда и каждой оси набор
  подбирается полным перебором, если вариантов <= 60 000, иначе жадно + обмены по одному
  с 25 случайными стартами (зерно 3010).
- Соло-герой с одним атакующим спутником (для «Уз», «Обмена», «Разом!» цель есть всегда) — одинаково
  для всех билдов.
"""
import itertools
import os
import random
import sys

D0 = 0.75
BURN = 0.267
STATUS_CAP = {1: 0.467, 2: 0.667, 3: 0.993}   # О/с: 0,2 % здоровья босса акта / О при ярусе 2
COMPANION_HIT = 0.6                            # удар лучника-спутника 0,9 B = 0,6 О (02, 12.3)

ABIL = {}


def add(i, name, req, kind, p1, p2=None):
    tags = set()
    for k, c in zip(req, "WMS"):
        if k > 0:
            tags.add(c)
    ABIL[i] = dict(id=i, name=name, req=req, kind=kind, tags=tags, p1=p1, p2=p2)


# ---------- ВОИН ----------
add("W1", "Таран", (1, 0, 0), "a",
    dict(dmg=1.0, cd=7, cast=0.3, eff=0.75, stam=20, surv=0.03, grp=0.02),
    dict(eff=0.9))                                   # II: 2 заряда — меньше потерянных откатов
add("W2", "Боевой клич", (1, 0, 0), "a",
    dict(cd=18, cast=0.2, eff=0.9, surv=0.08, grp=0.10),   # урон x0,7 на 5 с из 18; провокация
    dict(grp=0.18))                                  # II: снятие контроля с союзников
add("W3", "Сшибка", (1, 0, 0), "p", dict(flat=0.01, surv=0.04, grp=0.03), dict(surv=0.05, grp=0.04))
add("W4", "Вихрь", (3, 0, 0), "a",
    dict(dmg=2.0, cd=9, cast=1.2, eff=0.75, stam=25, surv=0.03, grp=0.02),
    dict(dmg=3.5, cast=2.1))                         # II: 2,1 с, 7 ударов по 0,5 О
add("W5", "Стойка", (3, 0, 0), "p", dict(surv=0.10), dict(surv=0.15))
add("W6", "Удар оземь", (6, 0, 0), "a",
    dict(dmg=1.8, cd=12, cast=0.5, eff=0.75, surv=0.04, grp=0.03), dict(dmg=2.2, grp=0.05))
add("W7", "Кровь за кровь", (6, 0, 0), "p", dict(surv=0.28), dict(surv=0.38))  # 15 % (II 20 %) долга
add("W8", "Аркан", (10, 0, 0), "a", dict(dmg=0.8, cd=9, cast=0.2, eff=0.8, surv=0.04, grp=0.04))
add("W9", "Последний рубеж", (15, 0, 0), "p", dict(surv=0.30), dict(surv=0.35, flat=0.01))
add("W10", "Аристея", (20, 0, 0), "a",
    dict(buff=(1.8, 12), cd=60, cast=0.4, eff=0.9, surv=0.06),  # x2 урона и у цели 0,7 вместо 0,5
    dict(buff=(1.8, 15)))

# ---------- МАГ ----------
add("M1", "Волна", (0, 1, 0), "a",
    dict(dmg=0.3, cd=6, cast=0.25, eff=0.85, wet=0.95, surv=0.04, grp=0.06, elem="water"),
    dict(grp=0.08))
add("M2", "Молния", (0, 1, 0), "a",
    dict(dmg=0.5, elec=1.0, cd=3, cast=0.15, eff=0.85, grp=0.03, elem="elec"), dict(grp=0.05))
add("M3", "Резонанс", (0, 1, 0), "p", dict(surv=0.03, grp=0.04), dict(grp=0.07, flat=0.01))  # II + цепь стихий
add("M4", "Огненный шар", (0, 3, 0), "a",
    dict(dmg=0.6, cd=7, cast=0.2, eff=0.85, burn=0.49, grp=0.02, elem="fire"),
    dict(dmg=0.8))                                   # II: горящий пол 0,15 О/с на 4 с
add("M5", "Зачарование", (0, 3, 0), "p", dict(special="enchant", dur=6), dict(special="enchant", dur=10))
add("M6", "Ледяная стена", (0, 6, 0), "a",
    dict(cd=16, cast=0.3, eff=0.85, surv=0.10, grp=0.08, elem="cold"), dict(dmg=0.8, surv=0.12, grp=0.10))
add("M7", "Ледяная кожа", (0, 6, 0), "p", dict(surv=0.19), dict(surv=0.31))   # 4 (II 6) с урон x0,6 раз в 10 с
add("M8", "Мерцание", (0, 10, 0), "a", dict(cd=8, surv=0.15), dict(surv=0.20))
add("M9", "Глаз бури", (0, 15, 0), "p", dict(special="eye", period=10), dict(special="eye", period=6))
add("M10", "Буря", (0, 20, 0), "a",
    dict(dmg=10.0, elec=1.0, cd=80, cast=0.5, eff=0.9, wet=0.12, grp=0.06, elem="elec"),
    dict(dmg=12.0, wet=0.15))

# ---------- ПРИЗЫВАТЕЛЬ ----------
add("S1", "Волк-тень", (0, 0, 1), "a",
    dict(minion=0.12, mup=0.85, dmg=0.6, cd=8, eff=0.85, surv=0.04, grp=0.03, hit=0.12),
    dict(minion=0.24, hit=0.24))                     # II: два волка, «Взять!» — один вожак
add("S2", "Тени гоплитов", (0, 0, 1), "a",
    dict(minion=0.24, mup=0.64, surv=0.06, grp=0.06, cd=20, hit=0.24 * 0.64),
    dict(minion=0.36, surv=0.08, grp=0.08, hit=0.36 * 0.64))
add("S3", "Узы", (0, 0, 1), "p", dict(surv=0.20, grp=0.02), dict(surv=0.27))   # 20 % (II 30 %) урона на подручного
add("S4", "Обмен", (0, 0, 3), "a", dict(cd=8, surv=0.10, grp=0.02), dict(surv=0.13))
add("S5", "Жатва теней", (0, 0, 3), "p", dict(flat=0.03, surv=0.03, grp=0.03), dict(flat=0.04, grp=0.04))
add("S6", "Вороны", (0, 0, 6), "a",
    dict(minion=0.16, mup=0.6, cd=18, surv=0.05, grp=0.04, hit=0.08 * 0.6),
    dict(mup=0.9, surv=0.07, hit=0.08 * 0.9))          # II: стая живёт 18 с
add("S7", "Последний вздох", (0, 0, 6), "p", dict(special="lastbreath", boom=1.2),
    dict(special="lastbreath", boom=1.4))
add("S8", "Тень Аякса", (0, 0, 10), "a",
    dict(minion=0.4, mup=0.40, cd=45, cast=0.5, eff=0.9, surv=0.10, grp=0.10, hit=1.0 * 0.4),
    dict(minion=0.4, mup=0.60, surv=0.15, grp=0.13, hit=1.0 * 0.6))
add("S9", "Тени не умирают", (0, 0, 15), "p", dict(special="undying", k=1.12, surv=0.05),
    dict(special="undying", k=1.2, surv=0.05))
add("S10", "Некия", (0, 0, 20), "a",
    dict(dmg=17.5, cd=90, cast=0.5, eff=0.77, grp=0.08, special="nekyia", dur=15),   # 58,5 тене-секунды x 0,35 О/с
    dict(dmg=26.5, special="nekyia", dur=20))

# ---------- ГИБРИДЫ ----------
add("WM1", "Громовой клинок", (2, 2, 0), "a", dict(dmg=1.8, elec=1.0, cd=12, eff=0.85, grp=0.03, elem="elec"))
add("WM2", "Стихийная сшибка", (5, 5, 0), "p", dict(flat=0.03, surv=0.06, grp=0.06))
add("WM3", "Мерцающий клинок", (8, 8, 0), "a", dict(dmg=3.0, cd=10, cast=0.45, eff=0.85, surv=0.06, grp=0.02))
add("WM4", "Гнев Зевса", (12, 12, 0), "a",
    dict(dmg=6.25, elec=1.0, cd=40, cast=0.5, eff=0.9, wet=0.25, surv=0.05, grp=0.08, elem="elec"))
add("MS1", "Стихийные тени", (0, 2, 2), "p", dict(special="elemshades"))
add("MS2", "Проводник", (0, 5, 5), "a", dict(cd=20, special="conduit", grp=0.03))
add("MS3", "Жертва стихиям", (0, 8, 8), "a", dict(dmg=2.0, elec=0.5, cd=10, eff=0.85, grp=0.04, special="sacrifice"))
add("MS4", "Воды Стикса", (0, 12, 12), "a",
    dict(dmg=3.6, cd=45, cast=0.4, eff=0.9, wet=0.27, surv=0.04, grp=0.10, special="styx", elem="water"))
add("SW1", "Фаланга", (2, 0, 2), "a", dict(cd=18, cast=0.2, eff=0.9, surv=0.15, grp=0.10, special="phalanx"))
add("SW2", "Плечом к плечу", (5, 0, 5), "p", dict(special="together", surv=0.12))
add("SW3", "Вынести с поля", (8, 0, 8), "a", dict(cd=30, surv=0.10, grp=0.15))
add("SW4", "Мирмидоняне", (12, 0, 12), "a",
    dict(minion=1.0, mup=0.30, cd=60, cast=0.5, eff=0.9, surv=0.08, grp=0.08, special="myrmidons", hit=1.0 * 0.3))
add("T1", "Аккорд", (4, 4, 4), "p", dict(special="chord"))
add("T2", "Лира Орфея", (7, 7, 7), "a", dict(cd=60, cast=0.3, eff=0.9, special="lyre", surv=0.08, grp=0.25))
add("T3", "Катабасис", (10, 10, 10), "a", dict(cd=90, cast=0.4, eff=0.9, special="katabasis", surv=0.08, grp=0.05))

SUMMONS = {"S1", "S2", "S6", "S8", "S10", "SW4"}


def slots(level):
    if level >= 22:
        return 4, 3
    if level >= 14:
        return 4, 2
    if level >= 10:
        return 3, 2
    if level >= 6:
        return 3, 1
    return 2, 1


def act_of(level):
    return 1 if level <= 10 else (2 if level <= 20 else 3)


def known(build):
    return [i for i, a in ABIL.items() if all(b >= r for b, r in zip(build, a["req"]))]


def pure_class(i):
    cls = [k for k in range(3) if ABIL[i]["req"][k] > 0]
    return cls[0] if len(cls) == 1 else None


def is_stage2(i, build):
    a = ABIL[i]
    k = pure_class(i)
    return bool(a["p2"]) and k is not None and build[k] >= a["req"][k] + 10


def params(i, build):
    p = dict(ABIL[i]["p1"])
    if is_stage2(i, build):
        p.update(ABIL[i]["p2"])
    return p


def evaluate(acts, pas, build, act):
    P = {i: params(i, build) for i in list(acts) + list(pas)}
    direct = elec = minion = flat = loss = 0.0
    burn, wet = [], []
    stam = 1.0
    surv = 1.0
    grp = 0.0
    hits = COMPANION_HIT * 0.85        # «Разом!»: внеочередной выстрел спутника
    act_dps = {}
    elems = set()
    min_cd_elem = 99.0
    tags = set()
    for i in acts:
        p = P[i]
        tags |= ABIL[i]["tags"]
        cd = p.get("cd", 10)
        eff = p.get("eff", 0.85)
        d = e = 0.0
        if p.get("dmg") and p.get("special") != "sacrifice":
            r = p["dmg"] * eff / cd
            e = r * p.get("elec", 0.0)
            d = r - e
        if p.get("buff"):
            ex, dur = p["buff"]
            d += D0 * ex * dur * eff / cd
        if p.get("cast"):
            loss += D0 * p["cast"] * eff / cd
        if p.get("minion"):
            minion += p["minion"] * p.get("mup", 0.85) * 0.85
            hits += p.get("hit", 0.0)
        if p.get("burn"):
            burn.append(p["burn"])
        if p.get("wet"):
            wet.append(p["wet"])
        if p.get("elem"):
            elems.add(p["elem"])
            min_cd_elem = min(min_cd_elem, cd)
        if p.get("stam"):
            stam *= 1 - (p["stam"] / cd) / 20 * 0.25
        direct += d
        elec += e
        act_dps[i] = (d, e)
        surv *= 1 + p.get("surv", 0.0)
        grp += p.get("grp", 0.0)
    for i in pas:
        p = P[i]
        surv *= 1 + p.get("surv", 0.0)
        grp += p.get("grp", 0.0)
        flat += p.get("flat", 0.0)
    sp = {P[i]["special"]: P[i] for i in list(acts) + list(pas) if P[i].get("special")}
    if "enchant" in sp and elems:                      # Зачарование
        u = min(1.0, sp["enchant"]["dur"] / min_cd_elem)
        if "elec" in elems:
            elec += D0 * 0.15 * u
        if "fire" in elems:
            burn.append(u)
        if "water" in elems:
            wet.append(u)
    if "elemshades" in sp and elems and minion > 0:    # Стихийные тени
        u = min(1.0, 8.0 / min_cd_elem)
        if "water" in elems:
            wet.append(0.9 * u)
        if "fire" in elems:
            burn.append(0.9 * u)
        if "elec" in elems:
            elec += 0.5 * minion * u
    if "undying" in sp and minion > 0:
        minion *= sp["undying"]["k"]
    if "styx" in sp and minion > 0:
        minion *= 1 + 0.5 * 12 / 45 * 0.9
    if "phalanx" in sp and minion > 0:
        minion *= 1.05
    if "nekyia" in sp:
        direct += minion * 0.5 * sp["nekyia"]["dur"] * 0.9 / 90   # подручные в 1,5 раза чаще
    w = 1.0
    for x in wet:
        w *= (1 - min(1.0, x))
    wet_up = 1 - w
    b = 1.0
    for x in burn:
        b *= (1 - min(1.0, x))
    burn_up = 1 - b
    if burn and wet:
        wet_up *= 0.6
        burn_up *= 0.6
    if "sacrifice" in sp and minion > 0:
        p = sp["sacrifice"]
        r = p["dmg"] * p["eff"] / p["cd"]
        e_part = r * (0.5 if "elec" in elems else 0.0)
        direct += r - e_part
        elec += e_part
        act_dps["MS3"] = (r - e_part, e_part)
    if "lastbreath" in sp and minion > 0:
        boom = sp["lastbreath"]["boom"]
        flat += boom / 30.0
        if "sacrifice" in sp:
            flat += boom * 0.85 / 10.0
    elec_total = elec * (1 + wet_up)

    def dps_of(i):
        d, e = act_dps.get(i, (0.0, 0.0))
        return d + e * (1 + wet_up)
    actives_dps = sum(dps_of(i) for i in acts)
    if "eye" in sp:                                    # заряд раз в period; способность с откатом длиннее — не чаще отката
        best = 0.0
        for i in acts:
            if "M" in ABIL[i]["tags"] and i not in ("M8", "M10") and P[i].get("dmg"):
                per_use = P[i]["dmg"] * (1 + P[i].get("elec", 0) * wet_up)
                best = max(best, per_use / max(sp["eye"]["period"], P[i].get("cd", 10)))
        flat += best * 0.9
    if "conduit" in sp:
        m_dps = sum(dps_of(i) for i in acts if "M" in ABIL[i]["tags"] and i not in ("M8", "M10", "MS2"))
        flat += m_dps * 0.5 * 0.85
    if "myrmidons" in sp:
        w_dps = sum(dps_of(i) for i in acts if "W" in ABIL[i]["tags"] and i != "SW4")
        flat += w_dps * 4 * 0.5 * (20 / 60) * 0.9
    if "together" in sp:
        flat += 0.25 * hits * 0.85
    if "chord" in sp and {"W", "M", "S"} <= tags:
        flat += 1.5 * 0.85 / 12 + actives_dps * 2 / 12
        surv *= 1.25
        grp += 0.20
    if "lyre" in sp:
        flat += -D0 * 8 / 60 * 0.9 + (actives_dps + minion) * 8 / 60 * 0.9
    if "katabasis" in sp:
        nons = sum(dps_of(i) for i in acts if i not in SUMMONS and i != "T3")
        flat += nons * 3.0 * 15 / 90 * 0.9               # 15 с: откаты x2 и эхо 100 %: x4 - 1
    status = min(STATUS_CAP[act], BURN * burn_up)
    boss = direct + elec_total + minion + flat + status - loss
    return boss / D0, surv * stam, grp


AXES = ("boss", "surv", "grp", "all")


def score(t, axis):
    g, s, r = t
    if axis == "boss":
        return g
    if axis == "surv":
        return s
    if axis == "grp":
        return r
    return ((1 + g) * s * (1 + r)) ** (1 / 3)


def ncomb(n, k):
    from math import comb
    return comb(n, k) if 0 <= k <= n else 0


def best_loadout(build, level, axis, rng):
    act = act_of(level)
    na, np_ = slots(level)
    k = known(build)
    A = [i for i in k if ABIL[i]["kind"] == "a"]
    Pp = [i for i in k if ABIL[i]["kind"] == "p"]
    na = min(na, len(A))
    np_ = min(np_, len(Pp))

    def val(a, p):
        return score(evaluate(a, p, build, act), axis)
    if ncomb(len(A), na) * ncomb(len(Pp), np_) <= 60000:
        best = None
        for a in itertools.combinations(A, na):
            for p in itertools.combinations(Pp, np_):
                v = val(a, p)
                if best is None or v > best[0] + 1e-12:
                    best = (v, a, p)
        return best
    best = None
    for start in range(26):
        if start == 0:
            a, p = [], []
            while len(a) < na:
                c = max((x for x in A if x not in a), key=lambda x: val(a + [x], p))
                a.append(c)
            while len(p) < np_:
                c = max((x for x in Pp if x not in p), key=lambda x: val(a, p + [x]))
                p.append(c)
        else:
            a = rng.sample(A, na)
            p = rng.sample(Pp, np_)
        cur = val(a, p)
        improved = True
        while improved:
            improved = False
            for idx in range(na):
                for x in A:
                    if x in a:
                        continue
                    t = a[:]
                    t[idx] = x
                    v = val(t, p)
                    if v > cur + 1e-12:
                        a, cur, improved = t, v, True
            for idx in range(np_):
                for x in Pp:
                    if x in p:
                        continue
                    t = p[:]
                    t[idx] = x
                    v = val(a, t)
                    if v > cur + 1e-12:
                        p, cur, improved = t, v, True
        if best is None or cur > best[0] + 1e-12:
            best = (cur, tuple(a), tuple(p))
    return best


BUILDS = {
    6: [("Чистый воин", (6, 0, 0)), ("Чистый маг", (0, 6, 0)), ("Чистый призыватель", (0, 0, 6)),
        ("Воин-маг", (3, 3, 0)), ("Маг-призыватель", (0, 3, 3)), ("Призыватель-воин", (3, 0, 3)),
        ("Воин + маг (4/2)", (4, 2, 0)), ("Тройной", (2, 2, 2))],
    10: [("Чистый воин", (10, 0, 0)), ("Чистый маг", (0, 10, 0)), ("Чистый призыватель", (0, 0, 10)),
         ("Воин-маг", (5, 5, 0)), ("Маг-призыватель", (0, 5, 5)), ("Призыватель-воин", (5, 0, 5)),
         ("Воин + маг (7/3)", (7, 3, 0)), ("Тройной", (4, 3, 3)), ("Тройной с упором", (6, 2, 2))],
    20: [("Чистый воин", (20, 0, 0)), ("Чистый маг", (0, 20, 0)), ("Чистый призыватель", (0, 0, 20)),
         ("Воин-маг", (10, 10, 0)), ("Маг-призыватель", (0, 10, 10)), ("Призыватель-воин", (10, 0, 10)),
         ("Воин + маг (14/6)", (14, 6, 0)), ("Тройной", (7, 7, 6)), ("Тройной с упором", (10, 5, 5))],
    30: [("Чистый воин", (30, 0, 0)), ("Чистый маг", (0, 30, 0)), ("Чистый призыватель", (0, 0, 30)),
         ("Воин-маг", (15, 15, 0)), ("Маг-призыватель", (0, 15, 15)), ("Призыватель-воин", (15, 0, 15)),
         ("Воин + маг (20/10)", (20, 10, 0)), ("Тройной", (10, 10, 10)), ("Тройной с упором", (12, 12, 6))],
}


def name_of(i, build):
    return ABIL[i]["name"] + (" II" if is_stage2(i, build) else "")


def fmt_load(build, a, p):
    return ", ".join(name_of(i, build) for i in a) + " — " + ", ".join(name_of(i, build) for i in p)


def main():
    rng = random.Random(3010)
    out = []
    w = out.append
    w("# Расчёт к 03_classes: мультикласс против чистого класса\n")
    w("Команда: `python C:\\Users\\carak\\source\\Orpheus\\design_room\\threads\\03_classes\\classes_calc.py` "
      "(зерно 3010). Модель и допущения — в шапке скрипта; числа способностей — `docs/03_classes.md`, разделы 5–10.\n")
    w("Оси: **урон** — прирост урона по боссу соло к урону оружия (D0 = 0,75 О/с, как в 02, 10.1); "
      "**живучесть** — множитель эффективного здоровья; **группа** — вклад в группу из 4 в долях одного товарища; "
      "**сводный** = среднее геометрическое трёх множителей: ((1 + урон) × живучесть × (1 + группа))^(1/3). По каждой оси набор подобран отдельно (лучший для этой оси); "
      "в столбце «набор» — набор сводного счёта.\n")
    for level in (6, 10, 20, 30):
        na, np_ = slots(level)
        w(f"\n## Уровень {level} (акт {act_of(level)}; слоты: {na} активных + {np_} пассивных)\n")
        w("| Билд | В/М/П | Урон, % | Живучесть, × | Группа, + | Сводный | Урон в сводном наборе, % | Набор сводного (активные — пассивные) |")
        w("|---|---|---|---|---|---|---|---|")
        rows = []
        for name, build in BUILDS[level]:
            res = {axis: best_loadout(build, level, axis, rng) for axis in AXES}
            g, s, r, c = res["boss"][0], res["surv"][0], res["grp"][0], res["all"][0]
            load = fmt_load(build, res["all"][1], res["all"][2])
            gb = evaluate(res["all"][1], res["all"][2], build, act_of(level))[0]
            rows.append((name, build, g, s, r, c, load, gb))
            w(f"| {name} | {build[0]}/{build[1]}/{build[2]} | {g * 100:+.0f} | {s:.2f} | {r:.2f} | {c:.2f} | {gb * 100:+.0f} | {load} |")
        pures = [x for x in rows if x[0].startswith("Чистый")]
        multis = [x for x in rows if not x[0].startswith("Чистый")]
        w("")
        for col, label in ((2, "урон"), (3, "живучесть"), (4, "группа"), (5, "сводный")):
            bp = max(pures, key=lambda x: x[col])
            bm = max(multis, key=lambda x: x[col])
            ok = "мультикласс не слабее" if bm[col] >= bp[col] - 1e-9 else "ЧИСТЫЙ СИЛЬНЕЕ"
            w(f"- {label}: лучший чистый — {bp[0]} ({bp[col]:.2f}), лучший мультикласс — {bm[0]} ({bm[col]:.2f}): {ok}.")
        best_all = max(rows, key=lambda x: x[5])[5]
        for x in pures:
            dom = [m[0] for m in multis if m[2] >= x[2] - 1e-9 and m[3] >= x[3] - 1e-9 and m[4] >= x[4] - 1e-9]
            w(f"- {x[0]}: не слабее его по всем трём осям сразу — " + (", ".join(dom) if dom else "НИКТО") + ".")
        gbs = sorted(x[7] for x in rows)
        med = gbs[len(gbs) // 2]
        w(f"- медиана прироста урона по боссу в сводных наборах: {med * 100:+.0f} % (02 считал боссов на +30 / +50 / +70 %).")
        for x in pures:
            verdict = "жизнеспособен" if x[5] >= 0.8 * best_all else "НИЖЕ 80 %"
            w(f"- {x[0]}: сводный {x[5]:.2f} = {x[5] / best_all * 100:.0f} % лучшего ({verdict}).")
    w("\n## Приложение: каждая способность отдельно (одна в наборе; требования ровно по порогу; II — порог + 10)\n")
    w("| Способность | Требование В/М/П | Вид | Урон, % (I / II) | Живучесть (I / II) | Группа (I / II) |")
    w("|---|---|---|---|---|---|")
    for i, a in ABIL.items():
        req = a["req"]
        k = pure_class(i)
        b2 = tuple(r + 10 if kk == k else r for kk, r in enumerate(req)) if k is not None else req
        acts = (i,) if a["kind"] == "a" else ()
        pas = (i,) if a["kind"] == "p" else ()
        t1 = evaluate(acts, pas, req, act_of(max(1, sum(req))))
        t2 = evaluate(acts, pas, b2, act_of(sum(b2))) if a["p2"] else None

        def f(t, j, pct=False):
            if t is None:
                return "—"
            return f"{t[j] * 100:+.0f}" if pct else f"{t[j]:.2f}"
        rq = "/".join(str(x) for x in req)
        kind = "акт." if a["kind"] == "a" else "пасс."
        w(f"| {a['name']} | {rq} | {kind} | {f(t1, 0, True)} / {f(t2, 0, True)} | {f(t1, 1)} / {f(t2, 1)} | {f(t1, 2)} / {f(t2, 2)} |")
    text = "\n".join(out) + "\n"
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, "classes_calc.md"), "w", encoding="utf-8") as fh:
        fh.write(text)
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass
    print(text)


if __name__ == "__main__":
    main()
