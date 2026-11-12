using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Rng;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Архетип врага (карточка 1-007, docs/02 8.1): набор чисел и поведение. 1-007 задаёт один — «Ближний».
    /// Числа боя — <see cref="MeleeRogue"/>; дальний/кавалерия/челнок — в карточках 1-020/1-023/1-021.
    /// </summary>
    public static class EnemyArchetype
    {
        /// <summary>Нет архетипа: сущность не ИИ (манекен карточки 1-001, персонаж). Мозг выключен.</summary>
        public const byte None = 0;

        /// <summary>«Ближний» (8.1): догоняет и бьёт в упор; урон 10, скорость 4,5 тайла/с.</summary>
        public const byte Melee = 1;

        /// <summary>Число архетипов (поле в снимке — 2 бита).</summary>
        public const int Count = 2;
    }

    /// <summary>
    /// Состояние ИИ (карточка 1-007; docs/02 8.1/8.3). В снимке клиенту — 3 бита (8.13 «Агро»):
    /// Idle/Wander — клиент рисует бродягу, Chase — «заметил», Attack — замах (телеграф шлёт конвейер угроз).
    /// </summary>
    public enum EnemyState : byte
    {
        /// <summary>Нет мозга (архетип <see cref="EnemyArchetype.None"/>).</summary>
        None = 0,

        /// <summary>Стоит на месте (домой не идёт — лежит где встал).</summary>
        Idle = 1,

        /// <summary>Бродит вокруг точки появления: идёт к случайной цели в 6 тайлах (8.1 «бродит»).</summary>
        Wander = 2,

        /// <summary>Преследует цель (заметил по зрению или получил урон — 8.3).</summary>
        Chase = 3,

        /// <summary>Цель в зоне удара: стоит, цикл замаха идёт в такте (8.1 «бьёт в упор»).</summary>
        Attack = 4,

        /// <summary>Цель замечена, путей в окне нет (Enclosed): ждёт правку мира, переспрашивает каждые 2 с (8.4).</summary>
        Blocked = 5,

        /// <summary>Цель потеряна: идёт домой, по пути переспрашивает цель (8.3 «теряет через 2× радиус»).</summary>
        Lost = 6,
    }

    /// <summary>
    /// Мозг вражеской сущности (карточка 1-007): состояние, цель, агро, блуждание. Целочисленный, без ссылок:
    /// живёт в <see cref="SimEntity"/> (struct), попадает в хэш состояния (<see cref="StateHash.Entity"/>)
    /// и в сохранения (формат 6). Кэш пути (<see cref="PathStamp"/>/<see cref="PathPos"/>) — производный:
    /// в хэш и в сейв НЕ входит, после загрузки пути переспрашиваются.
    /// </summary>
    public struct EnemyBrain
    {
        /// <summary>Цель — нет цели.</summary>
        public const int NoTarget = -1;

        /// <summary>Архетип (<see cref="EnemyArchetype"/>). 0 — мозг выключен (манекен, персонаж).</summary>
        public byte Archetype;

        /// <summary>Текущее состояние.</summary>
        public EnemyState State;

        /// <summary>Номер цели-сущности (агро, 8.3), −1 — нет.</summary>
        public int TargetId;

        /// <summary>Тактов в текущем состоянии (пересчёт блуждания, пороги Blocked/Lost).</summary>
        public int StateTicks;

        /// <summary>Тактов с тех пор, как цель вне радиуса потери (сбрасывается при видимости цели).</summary>
        public int LostTicks;

        /// <summary>Такт последнего полученного удара (0 — не было; агро 8.3 «получил урон — сменяет цель»).</summary>
        public long LastHitTick;

        /// <summary>Номер сущности, ударившей последней (−1 — не было).</summary>
        public int LastHitBy;

        /// <summary>Урон последнего удара (агро переключается при уроне ≥ 10 % максимума).</summary>
        public int LastHitAmount;

        /// <summary>Точка появления (тайл X) — «дом» для Idle и Lost (8.1 «бродит у точки появления»).</summary>
        public int HomeX;

        /// <summary>Точка появления (тайл Y).</summary>
        public int HomeY;

        /// <summary>Цель блуждания (тайл X) — случайная в 6 тайлах от дома; в хэше (детерминизм от зерна мира).</summary>
        public int GoalX;

        /// <summary>Цель блуждания (тайл Y).</summary>
        public int GoalY;

        // ---- «Упёрся» (1-013, docs/02 9.3) ----

        /// <summary>Такт входа в текущий эпизод «упёрся» (Blocked); 0 — не упёрся. Сбрасывается при сдачи (игнор),
        /// смене цели и найденном пути; переживает переспросы 2 с (таймер 20 с копится).</summary>
        public long BlockedSince;

        /// <summary>Цель, игнорируемая после сдачи (упёрся 20 с). Действует, пока <c>IgnoreUntil != 0</c>
        /// и <c>tick &lt; IgnoreUntil</c> (дефолт 0 safe: без игнора); −1 — явно «нет».</summary>
        public int IgnoreTargetId;

        /// <summary>Такт, до которого игнорируется <see cref="IgnoreTargetId"/> (30 с после сдачи); 0 — игнор не активен.</summary>
        public long IgnoreUntil;

        // ---- Кэш пути (в хэш и сейв НЕ входит; после загрузки переспрашивается) ----

        /// <summary>Такт запроса пути; 0 — запроса нет. Буфер валиден при совпадении с PathBuffer.RequestedTick.</summary>
        public int PathStamp;

        /// <summary>Индекс следующего шага в буфере пути.</summary>
        public int PathPos;

        /// <summary>Ревизия мира (<see cref="Simulation.EditRevision"/>) на момент запроса пути: сдвинулась —
        /// мир правили, «изменился тайл» → пересчёт сразу (1-013, 9.3).</summary>
        public long PathRev;

        /// <summary>Ревизия мира, на которой цель стала <c>Enclosed</c> (+1, 0 — кэша нет): до следующей правки
        /// переспрос не ставится — «карман» стоит не дороже одного A* на врага (1-013 з.2 backlog).</summary>
        public long EnclosedRev;

        /// <summary>Контрольная точка продвижения в погоне (1-013, 9.3 «упёрся»): позиция и такт последнего
        /// смещения ≥ 96 единиц. За 2 с без продвижения — Blocked (путь есть, но стена под ногами не пускает).
        /// В хэш и сейв НЕ входит (дефолт 0 safe: первая точка сбрасывается сама).</summary>
        public int ProgressX;

        /// <summary>См. <see cref="ProgressX"/>: координата X контрольной точки.</summary>
        public int ProgressY;

        /// <summary>См. <see cref="ProgressX"/>: такт контрольной точки.</summary>
        public int ProgressTick;

        /// <summary>Состояние для снимка клиенту (8.13): поле 3 бита.</summary>
        public int SnapshotState => (int)State & 7;
    }

    /// <summary>
    /// Числа «Ближнего» (карточка 1-007; docs/02 8.1, 6). тайл = 256 единицы (<see cref="Torus.SubTilesPerTile"/>),
    /// такт = 1/30 с (Simulation.TickRate), кадр docs (d = 24 на замах) = 1/60 с = полтакта.
    /// Рельсы: 14 т обзор · 28 т потеря · 4,5 т/с · удар 10 · цикл 33 такта.
    /// </summary>
    public static class MeleeRogue
    {
        /// <summary>Здоровье (1-001: игрок убивает «Ближнего» за 10 ударов мечом: 10 × 10 урона).</summary>
        public const short MaxHealth = 100;

        /// <summary>Скорость преследования 4,5 тайла/с = 460 (X10, тот же масштаб, что 512 = 5,0 т/с у игрока).</summary>
        public const int ChaseSpeedX10 = 460;

        /// <summary>Радиус обнаружения 14 тайлов = 14 336 единицы (8.1: 12–16).</summary>
        public const long Sight2 = 14L * Torus.SubTilesPerTile * 14 * Torus.SubTilesPerTile;

        /// <summary>Радиус потери цели 2 × 14 = 28 тайлов = 28 672 единицы (8.3).</summary>
        public const long Lose2 = 28L * Torus.SubTilesPerTile * 28 * Torus.SubTilesPerTile;

        /// <summary>Тактов вне радиуса потери до сброса цели: 150 = 2,5 с (8.3 «теряет через 2× радиус»).</summary>
        public const int LoseTicks = 150;

        /// <summary>Дистанция удара: сектор врага 358 (радиус 1,4 т) + радиус цели 102 = 460 ед. — ровно перекрываемость
        /// <see cref="MeleeSector"/>; дальше замах гарантированно мимо (8.1 «бьёт в упор», 2 т округлённо).</summary>
        public const long StrikeRange2 = 460L * 460L;

        /// <summary>Замах 0,40 с = 12 тактов = «d = 24 кадра» (docs/02 8.1; = WindupTicks = ThreatTiming.WindupFrames).</summary>
        public const int WindupTicks = 12;

        /// <summary>Отход 0,60 с = 18 тактов (docs/02 8.1; = RecoverTicks).</summary>
        public const int RecoverTicks = 18;

        /// <summary>Замах от замаха: 1,10 с = 33 такта (= DummyPeriodTicks и период MeleeDummy; 10 ударов ≈ 11 с).</summary>
        public const int PeriodTicks = 33;

        /// <summary>Удар 10 урона (8.1 «урон 8–12»; игрок 100 ХП → 10 ударов за 11 ±1 с — приёмка 1-007).</summary>
        public const int StrikeDamage = 10;

        /// <summary>Агро от удара: урон ≥ 10 % максимума цели — цель меняется (8.3 «получил урон — сменяет цель»).</summary>
        public const int AggroPct = 10;

        /// <summary>Блуждание: не дальше 6 тайлов от точки появления (8.1 «бродит»).</summary>
        public const int WanderRadiusTiles = 6;

        /// <summary>Пауза бродяги 2–5 с = 60–150 тактов (8.1 «не спешит»).</summary>
        public const int IdleMinTicks = 60;
        public const int IdleMaxTicks = 150;

        /// <summary>«Домой» (Lost): не дальше 60 тайлов от точки появления — дальше враг не уходит (8.3).</summary>
        public const long Leash2 = 60L * Torus.SubTilesPerTile * 60 * Torus.SubTilesPerTile;

        /// <summary>Путь переспрашивается каждые 150 тактов (2,5 с), цель ушла дальше 1 тайла — сразу (9.3).</summary>
        public const int RepathTicks = 150;

        /// <summary>Смещение цели дальше 1 тайла (1024 единицы) — путь переспрашивается сразу (9.3).</summary>
        public const int RepathGoalDelta = Torus.SubTilesPerTile;

        /// <summary>Blocked (пути нет): переспрос каждые 120 тактов = 2 с (8.4 «раз в секунду переспрашивает»).
        /// 1-013: переспрос — только когда мир правили (ревизия сдвинулась); 2 с остались как ритм сдачи.</summary>
        public const int BlockedRetryTicks = 120;

        /// <summary>«Упёрся» (1-013, 9.3): 20 с в Blocked (600 тактов) — цель игнорируется 30 с (900), враг
        /// уходит в Ожидание/бродягу у стены.</summary>
        public const int BlockedGiveUpTicks = 600;

        /// <summary>Игнор цели после сдачи: 30 с = 900 тактов (9.3 «игнор 30 с», потом Ожидание).</summary>
        public const int IgnoreTargetTicks = 900;

        /// <summary>Узел пути пройден: центр ближе 3/4 тайла (192) к центру шага — идём к следующему (9.3). Меньше
        /// зоны удара (512), иначе погоня «прибывает» к тайлу цели раньше, чем входит в удар, и замирает.</summary>
        public const long NodePass2 = (long)(3 * Torus.SubTilesPerTile / 4) * (3 * Torus.SubTilesPerTile / 4);

        /// <summary>Выталкивание: скорость расталкивания 2 тайла/с = 205 (X10); после фазы движения (8.5 «толкаются»).</summary>
        public const int SeparationSpeedX10 = 205;

        /// <summary>«Упёрся» без продвижения (1-013, 9.3): в погоне 2 с (60 тактов) смещаемся меньше
        /// <see cref="ProgressMove2"/> — путь есть, но стена под ногами (стартовый тайл поиска проходим всегда)
        /// не пускает: Blocked, та же схема, что и при Enclosed (кэш до смены тайла, сдача через 20 с).</summary>
        public const int StuckTicks = 60;

        /// <summary>Продвижение, обнуляющее таймер «упёрся»: смещение ≥ 48 единиц (~0,2 тайла) от контрольной точки.</summary>
        public const long ProgressMove2 = 48L * 48L;
    }

    /// <summary>
    /// Детерминированный шум для ИИ (выбор точки блуждания): SplitMix64 от (номер сущности, такт, зерно мира).
    /// Без выделений, целочисленный; одинаков на любой машине (11.5).
    /// </summary>
    public static class EnemyRng
    {
        /// <summary>Смешанное число из тройки (сущность, такт, зерно).</summary>
        public static ulong Hash(int entityId, long tick, ulong worldSeed)
        {
            return SplitMix64.Hash(entityId, unchecked((int)tick), worldSeed);
        }

        /// <summary>Значение 0…n−1 (n ≥ 1).</summary>
        public static int NextInt(ulong h, int n)
        {
            return (int)(h % (ulong)n);
        }

        /// <summary>Направление 0…7 (0 = восток, по часовой).</summary>
        public static int NextDir8(ulong h)
        {
            return (int)((h >> 32) & 7);
        }
    }
}
