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
    dict(dmg=2.0, cd=7, cast=0.3, eff=0.75, stam=20, surv=0.03, grp=0.02),   # v2: по боссу 1,0 О + следующий удар серии тяжёлый (+1,0 О)
    dict(eff=0.9))                                   # II: 2 заряда
add("W2", "Боевой клич", (1, 0, 0), "a",
    dict(cd=18, cast=0.2, eff=0.9, surv=0.08, grp=0.10), dict(grp=0.14))   # v2: II с запретом на цели (раз в 18 с)
add("W3", "Сшибка", (1, 0, 0), "p", dict(flat=0.01, surv=0.04, grp=0.03), dict(surv=0.05, grp=0.04))
add("W4", "Вихрь", (1, 0, 0), "a",                   # Я2 (порог — тир, см. TIER)
    dict(dmg=2.0, cd=9, cast=1.2, eff=0.75, stam=25, surv=0.03, grp=0.02),
    dict(dmg=3.5, cast=2.1))
add("W5", "Стойка", (1, 0, 0), "p",                  # v2: Я3; v2: «брешь» — идеальный уворот от босса: 1,5 с тяжёлых ударов
    dict(surv=0.10, flat=0.13), dict(surv=0.13, flat=0.13))
add("W6", "Удар оземь", (1, 0, 0), "a",              # Я3
    dict(dmg=1.8, cd=12, cast=0.5, eff=0.75, surv=0.04, grp=0.03), dict(dmg=2.2, grp=0.05))
add("W7", "Кровь за кровь", (1, 0, 0), "p", dict(surv=0.22), dict(surv=0.25))   # v2: Я2; v2: порции, ≤ 50 % порции
add("W8", "Аркан", (10, 0, 0), "a", dict(dmg=0.8, cd=9, cast=0.2, eff=0.8, surv=0.04, grp=0.04))
add("W9", "Последний рубеж", (15, 0, 0), "p", dict(surv=0.30), dict(surv=0.35, flat=0.01))
add("W10", "Аристея", (20, 0, 0), "a",
    dict(buff=(1.8, 12), cd=60, cast=0.4, eff=0.9, surv=0.06), dict(buff=(1.8, 15)))

# ---------- МАГ ----------
add("M1", "Волна", (0, 1, 0), "a",                   # v2: мокрый на боссе 5 с — доля 0,7
    dict(dmg=0.3, cd=6, cast=0.25, eff=0.85, wet=0.7, surv=0.04, grp=0.06, elem="water"), dict(grp=0.08))
add("M2", "Молния", (0, 1, 0), "a",
    dict(dmg=0.5, elec=1.0, cd=3, cast=0.15, eff=0.85, grp=0.03, elem="elec"), dict(grp=0.05))
add("M3", "Резонанс", (0, 1, 0), "p", dict(surv=0.03, grp=0.04), dict(grp=0.07, flat=0.01))
add("M4", "Огненный шар", (0, 1, 0), "a",            # Я2
    dict(dmg=0.6, cd=7, cast=0.2, eff=0.85, burn=0.49, grp=0.02, elem="fire"), dict(dmg=0.8))
add("M5", "Зачарование", (0, 1, 0), "p", dict(special="enchant", dur=6), dict(special="enchant", dur=6, two=True))  # Я2
add("M6", "Ледяная стена", (0, 1, 0), "a",           # Я3
    dict(cd=16, cast=0.3, eff=0.85, surv=0.10, grp=0.08, elem="cold"), dict(dmg=0.8, surv=0.12, grp=0.10))
add("M7", "Ледяная кожа", (0, 1, 0), "p", dict(surv=0.25), dict(surv=0.28, grp=0.02))   # Я3; v2: ×0,5
add("M8", "Мерцание", (0, 10, 0), "a", dict(cd=8, surv=0.18), dict(surv=0.22))
add("M9", "Глаз бури", (0, 15, 0), "p", dict(special="eye", period=10), dict(special="eye", period=10, mark=0.4))
add("M10", "Буря", (0, 20, 0), "a",
    dict(dmg=10.0, elec=1.0, cd=80, cast=0.5, eff=0.9, wet=0.12, grp=0.06, elem="elec"),
    dict(dmg=12.0, wet=0.15))

# ---------- ПРИЗЫВАТЕЛЬ ----------
add("S1", "Волк-тень", (0, 0, 1), "a",
    dict(minion=0.12, mup=0.85, dmg=0.6, cd=8, eff=0.85, surv=0.04, grp=0.03, hit=0.12),
    dict(minion=0.24, hit=0.24))
add("S2", "Тени гоплитов", (0, 0, 1), "a",
    dict(minion=0.24, mup=0.64, surv=0.06, grp=0.06, cd=20, hit=0.24 * 0.64),
    dict(minion=0.36, surv=0.08, grp=0.08, hit=0.36 * 0.64))
add("S5", "Жатва теней", (0, 0, 1), "p", dict(flat=0.03, surv=0.03, grp=0.03), dict(flat=0.04, grp=0.04))   # v2: Я1
add("S4", "Обмен", (0, 0, 1), "a", dict(cd=8, surv=0.10, grp=0.02), dict(surv=0.13))   # Я2
add("S3", "Узы", (0, 0, 1), "p", dict(surv=0.20, grp=0.02), dict(surv=0.24, grp=0.03))  # v2: Я2; II — провокация
add("S6", "Вороны", (0, 0, 1), "a",                  # Я3
    dict(minion=0.16, mup=0.6, cd=18, surv=0.05, grp=0.04, hit=0.08 * 0.6),
    dict(surv=0.08, grp=0.06))                       # II: ослеплённый сбрасывает цель
add("S7", "Последний вздох", (0, 0, 1), "p", dict(special="lastbreath", boom=1.2),   # Я3
    dict(special="lastbreath", boom=1.4))
add("S8", "Тень Аякса", (0, 0, 10), "a",
    dict(minion=0.4, mup=0.40, cd=45, cast=0.5, eff=0.9, surv=0.10, grp=0.10, hit=1.0 * 0.4),
    dict(minion=0.4, mup=0.44, surv=0.13, grp=0.12, hit=1.0 * 0.44))   # v2: II — только «на колене» и щит
add("S9", "Тени не умирают", (0, 0, 15), "p", dict(special="undying", k=1.12, surv=0.05),
    dict(special="undying", k=1.12, surv=0.08))
add("S10", "Некия", (0, 0, 20), "a",
    dict(dmg=17.5, cd=90, cast=0.5, eff=0.77, grp=0.08, special="nekyia", dur=15),
    dict(dmg=21.1, special="nekyia", dur=15))        # II: тени ямы взрываются 0,6 О

# ---------- ГИБРИДЫ ----------
add("WM1", "Громовой клинок", (2, 2, 0), "a", dict(dmg=1.8, elec=1.0, cd=12, eff=0.85, grp=0.03, elem="elec"))
add("WM2", "Стихийная сшибка", (5, 5, 0), "p", dict(flat=0.03, surv=0.06, grp=0.06))
add("WM3", "Мерцающий клинок", (8, 8, 0), "a", dict(dmg=3.0, cd=10, cast=0.45, eff=0.85, surv=0.08, grp=0.02))
add("WM4", "Гнев Зевса", (12, 12, 0), "a",
    dict(dmg=6.25, elec=1.0, cd=40, cast=0.5, eff=0.9, wet=0.2, surv=0.05, grp=0.08, elem="elec"))
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


# v2 — числа после выравнивания паритета (разбор [2], слово Автора 29.10); совпадают с docs/03_classes.md
TUNE_V2 = {"M10.1.cd": 60,
    "M10.1.dmg": 18,
    "M10.2.dmg": 24,
    "M2.1.cd": 4,
    "M6.2.grp": 0.12,
    "M7.2.surv": 0.32,
    "M8.1.dmg": 1.0,
    "M8.1.eff": 0.85,
    "M8.1.elec": 1.0,
    "M8.2.surv": 0.25,
    "M9.1.period": 8,
    "M9.2.mark": 0.6,
    "M9.2.period": 8,
    "S10.1.cd": 70,
    "S10.1.dmg": 28.0,
    "S10.2.dmg": 36,
    "S6.2.grp": 0.07,
    "S6.2.surv": 0.09,
    "S7.2.boom": 1.6,
    "S8.2.grp": 0.14,
    "S8.2.surv": 0.16,
    "S9.2.k": 1.25,
    "SW1.1.surv": 0.13,
    "SW2.1.rate": 0.2,
    "SW2.1.surv": 0.1,
    "SW3.1.grp": 0.12,
    "SW4.1.minion": 0.6,
    "SW4.1.mirror": 0.25,
    "T1.1.cdr": 1,
    "T1.1.cg": 0.15,
    "T1.1.cs": 0.15,
    "T2.1.grp": 0.15,
    "W1.1.dmg": 1.0,
    "W1.2.dmg": 2.0,
    "W10.1.buff": [1.8,
    14],
    "W10.1.cd": 45,
    "W10.2.buff": [1.8,
    20],
    "W5.2.surv": 0.16,
    "W8.1.dmg": 1.5,
    "W9.2.surv": 0.45,
    "WM1.1.dmg": 1.4,
    "WM4.1.dmg": 5.0}
for _k, _v in TUNE_V2.items():
    _i, _st, _f = _k.split('.')
    _t = ABIL[_i]['p1'] if _st == '1' else ABIL[_i]['p2']
    _t[_f] = tuple(_v) if isinstance(_v, list) else _v

# Коды и номера документа v2: W5 = В7 Стойка, W7 = В5 Кровь за кровь, S3 = П5 Узы, S5 = П3 Жатва теней; остальные совпадают.
# Ярусы чистых: (класс, ярус). Я1–Я3 — «взятие»: очко в класс открывает одну способность яруса на выбор
# (Я1 — очки 1–3, Я2 — 4–5, Я3 — 6–7); Я4–Я6 — пороги 10 / 15 / 20. Ступень II — порог первого взятия яруса + 10.
TIER = {
    "W1": (0, 1), "W2": (0, 1), "W3": (0, 1), "W4": (0, 2), "W7": (0, 2), "W6": (0, 3), "W5": (0, 3),
    "W8": (0, 4), "W9": (0, 5), "W10": (0, 6),
    "M1": (1, 1), "M2": (1, 1), "M3": (1, 1), "M4": (1, 2), "M5": (1, 2), "M6": (1, 3), "M7": (1, 3),
    "M8": (1, 4), "M9": (1, 5), "M10": (1, 6),
    "S1": (2, 1), "S2": (2, 1), "S5": (2, 1), "S4": (2, 2), "S3": (2, 2), "S6": (2, 3), "S7": (2, 3),
    "S8": (2, 4), "S9": (2, 5), "S10": (2, 6),
}
TIER_OPEN = {1: 1, 2: 4, 3: 6, 4: 10, 5: 15, 6: 20}
for _i, (_c, _t) in TIER.items():
    _r = [0, 0, 0]
    _r[_c] = TIER_OPEN[_t]
    ABIL[_i]["req"] = tuple(_r)


def picks(x, t):
    """Сколько способностей яруса t можно взять при уровне в классе x (Я1–Я3 — по одной за очко)."""
    if t == 1:
        return min(x, 3)
    if t == 2:
        return min(max(x - 3, 0), 2)
    if t == 3:
        return min(max(x - 5, 0), 2)
    return 99


def legal(acts, pas, build):
    cnt = {}
    for i in list(acts) + list(pas):
        if i in TIER:
            cnt[TIER[i]] = cnt.get(TIER[i], 0) + 1
    return all(n <= picks(build[c], t) for (c, t), n in cnt.items())


EMPTY_A = ["_A1", "_A2", "_A3", "_A4"]
EMPTY_P = ["_P1", "_P2", "_P3"]
for _e in EMPTY_A:
    ABIL[_e] = dict(id=_e, name="(пусто)", req=(0, 0, 0), kind="a", tags=set(), p1={}, p2=None)
for _e in EMPTY_P:
    ABIL[_e] = dict(id=_e, name="(пусто)", req=(0, 0, 0), kind="p", tags=set(), p1={}, p2=None)


def constrained(build):
    """Есть ли ярус, где взять можно меньше, чем в нём способностей (тогда в переборе нужны пустые слоты)."""
    size = {}
    for i, ct in TIER.items():
        size[ct] = size.get(ct, 0) + 1
    return any(build[c] >= TIER_OPEN[t] and picks(build[c], t) < n for (c, t), n in size.items() if t <= 3)


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
    return [i for i, a in ABIL.items() if not i.startswith("_") and all(b >= r for b, r in zip(build, a["req"]))]


def candidates(build):
    k = known(build)
    if constrained(build):
        k = k + EMPTY_A + EMPTY_P
    return k


def pure_class(i):
    if i.startswith("_"):
        return None
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


class K:
    """Ручки для проверки чувствительности: доля прибавки по мокрому, множители оценок живучести и пользы."""
    def __init__(self, wet_bonus=0.5, fs=None, fg=None, chord_s=1.0, chord_g=1.0, tag="base"):
        self.wb, self.fs, self.fg = wet_bonus, fs or {}, fg or {}
        self.cs, self.cg, self.tag = chord_s, chord_g, tag


BASE = K()
DR_FLOOR = 0.4          # 4.2 v2: все снижения урона от способностей вместе — не ниже ×0,4


# 4.3 v2: повтор (Глаз бури, Проводник, эхо Катабасиса) — только урон в точке
NO_REPEAT = {"M8", "M10", "MS2", "MS3", "MS4"}
REP = {"WM1": 0.5, "WM3": 1.0, "WM4": 2.0}       # урон повтора, О


def rep_share(i, P):
    d = P[i].get("dmg")
    return min(1.0, REP[i] / d) if i in REP and d else 1.0


def evaluate(acts, pas, build, act, k=None):
    k = k or BASE
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
        surv *= 1 + p.get("surv", 0.0) * k.fs.get(i, 1.0)
        grp += p.get("grp", 0.0) * k.fg.get(i, 1.0)
    for i in pas:
        p = P[i]
        surv *= 1 + p.get("surv", 0.0) * k.fs.get(i, 1.0)
        grp += p.get("grp", 0.0) * k.fg.get(i, 1.0)
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
            elec += 0.3 * minion * u
    if "undying" in sp and minion > 0:
        minion *= sp["undying"]["k"]
    if "styx" in sp and minion > 0:
        minion *= 1 + 0.5 * 12 / 45 * 0.9
    if "phalanx" in sp and minion > 0:
        minion *= 1.05
    if "nekyia" in sp:
        direct += minion * 0.5 * sp["nekyia"]["dur"] * 0.9 / 90   # подручные в 1,5 раза чаще
    if "eye" in sp and sp["eye"].get("mark"):
        wet.append(sp["eye"]["mark"])
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
    wet_up = k.wb * wet_up / 1.0     # v2: мокрый босс ×1,5 (доля прибавки 0,5)
    elec_total = elec * (1 + wet_up)

    def dps_of(i):
        d, e = act_dps.get(i, (0.0, 0.0))
        return d + e * (1 + wet_up)
    actives_dps = sum(dps_of(i) for i in acts)
    if "eye" in sp:                                    # заряд раз в period; способность с откатом длиннее — не чаще отката
        best = 0.0
        for i in acts:
            if "M" in ABIL[i]["tags"] and i not in NO_REPEAT and P[i].get("dmg"):
                per_use = REP.get(i, P[i]["dmg"]) * (1 + P[i].get("elec", 0) * wet_up)
                best = max(best, per_use / max(sp["eye"]["period"], P[i].get("cd", 10)))
        flat += best * 0.9
    if "conduit" in sp:
        m_dps = sum(dps_of(i) * rep_share(i, P) for i in acts if "M" in ABIL[i]["tags"] and i not in NO_REPEAT)
        flat += m_dps * 0.5 * 0.85
    if "myrmidons" in sp:
        w_dps = sum(dps_of(i) for i in acts if "W" in ABIL[i]["tags"] and i != "SW4")
        flat += w_dps * 4 * sp["myrmidons"].get("mirror", 0.5) * (20 / sp["myrmidons"].get("cd", 60)) * 0.9
    if "together" in sp:
        flat += sp["together"].get("rate", 0.25) * hits * 0.85
    if "chord" in sp and {"W", "M", "S"} <= tags:
        flat += 1.5 * 0.85 / 12 + actives_dps * sp["chord"].get("cdr", 2) / 12
        surv *= 1 + sp["chord"].get("cs", 0.25) * k.cs   # v2: лечение — баф «Аккорд» 1 %/с 10 с, одинаковые не складываются
        grp += sp["chord"].get("cg", 0.20) * k.cg
    if "lyre" in sp:
        flat += -D0 * 8 / 60 * 0.9 + (actives_dps + minion) * 8 / 60 * 0.9
    if "katabasis" in sp:
        nons = sum(dps_of(i) * rep_share(i, P) for i in acts if i not in SUMMONS and i not in NO_REPEAT and i != "T3")
        flat += nons * 3.0 * 15 / 90 * 0.9               # 15 с: откаты x2 и эхо 100 %: x4 - 1
    status = min(STATUS_CAP[act], BURN * burn_up)
    boss = direct + elec_total + minion + flat + status - loss
    surv = min(surv, 1 / DR_FLOOR * 1.6)   # потолок снижения урона ×0,4 при лечении/возвратах сверху
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


def best_loadout(build, level, axis, rng, k=None, restarts=26, ex_limit=60000):
    """Лучший набор для оси. v2: ярусы Я1–Я3 — взятие по очку; пустые слоты допустимы, если взять нечего."""
    act = act_of(level)
    na, np_ = slots(level)
    kn = candidates(build)
    A = [i for i in kn if ABIL[i]["kind"] == "a"]
    Pp = [i for i in kn if ABIL[i]["kind"] == "p"]
    na = min(na, len(A))
    np_ = min(np_, len(Pp))

    def val(a, p):
        if not legal(a, p, build):
            return -1e9
        return score(evaluate(a, p, build, act, k), axis)
    best = None
    if ncomb(len(A), na) * ncomb(len(Pp), np_) <= ex_limit:
        for a in itertools.combinations(A, na):
            if sum(1 for x in a if x.startswith("_")) and not constrained(build):
                continue
            for p in itertools.combinations(Pp, np_):
                v = val(a, p)
                if best is None or v > best[0] + 1e-12:
                    best = (v, a, p)
        return best
    for start in range(restarts):
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
    a = [i for i in a if not i.startswith("_")]
    p = [i for i in p if not i.startswith("_")]
    return ", ".join(name_of(i, build) for i in a) + " — " + ", ".join(name_of(i, build) for i in p)


def main():
    rng = random.Random(3010)
    out = []
    w = out.append
    w("# Расчёт к 03_classes: мультикласс против чистого класса\n")
    w("Команда: `python C:\\Users\\carak\\source\\Orpheus\\design_room\\threads\\03_classes\\classes_calc_v2.py` "
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
            if col == 2:
                r_ = (1 + bp[col]) / (1 + bm[col])
            else:
                r_ = bp[col] / bm[col] if bm[col] else 1.0
            ok = f"чистый / смешанный = {r_:.2f} — " + ("паритет (±10 %)" if 0.9 <= r_ <= 1.1 else "НЕ ПАРИТЕТ")
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
        if i.startswith("_"):
            continue
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
    with open(os.path.join(here, "classes_calc_v2.md"), "w", encoding="utf-8") as fh:
        fh.write(text)
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass
    print(text)


if __name__ == "__main__":
    main()
