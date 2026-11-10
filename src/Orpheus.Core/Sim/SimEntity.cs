using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Сущность в такте (docs/07, 4.2): номер, вид, движение, текущий кадр ввода.
    /// Сущности живут в списке по возрастанию номера (11.5: «списки по номеру сущности»);
    /// хэш состояния берёт все поля по порядку (<see cref="StateHash"/>).
    /// </summary>
    public struct SimEntity
    {
        /// <summary>Персонаж (игрок или бот — для такта одно и то же: сущность с вводом).</summary>
        public const byte KindCharacter = 1;

        /// <summary>Враг (0-028: заглушка для правила постановки «не ближе 2 тайлов к врагу», `05` 1.3).</summary>
        public const byte KindEnemy = 2;

        /// <summary>Номер сущности: растёт, не переиспользуется.</summary>
        public int Id;

        /// <summary>Вид сущности.</summary>
        public byte Kind;

        /// <summary>Позиция, остаток скорости, скорость.</summary>
        public Mover Mover;

        /// <summary>Кадр ввода, по которому сущность шла в последнем такте (держится, пока не придёт новый).</summary>
        public InputFrame Input;

        /// <summary>Сумка персонажа (1-002): панель, выбранная ячейка, материалы. У врагов — пустая.</summary>
        public Inventory Inv;

        /// <summary>Здоровье сейчас (1-001, `02` 6.3). ≤ 0 — смерть; в вехе 1 не сохраняется (карточка, «Не трогать»).</summary>
        public short Health;

        /// <summary>Максимум здоровья: персонаж 100 (`02` 6.3, уровень 0), враг по архетипу (манекен «Ближний» — 35).</summary>
        public short MaxHealth;

        /// <summary>Броня (в вехе 1 у всех 0, формула учитывает — `02` 6.1).</summary>
        public byte Armor;

        /// <summary>Масса для отбрасывания (6.5): игрок и обычный враг — 1, танк — 3.</summary>
        public byte Mass;

        /// <summary>Тактов неуязвимости после удара или снаряда осталось (0,4 с = 12 тактов, 6.3; у врагов тоже).</summary>
        public int InvulnTicks;

        /// <summary>Тактов с последнего полученного урона (регенерация с 180-го = 6 с, 6.3).</summary>
        public int RegenTicks;

        /// <summary>Накопитель регенерации в сотых долях здоровья × такт: +MaxHealth за такт, 3000 → +1 HP (1 %/с).</summary>
        public int RegenCarryX100;

        /// <summary>Тактов флага «в бою» осталось (240 = 8 с после нанесённого или полученного урона, 6.3).</summary>
        public int CombatTicks;

        /// <summary>Тактов до возрождения (персонаж, 150 = 5 с) или труп лежит (враг, 300 = 10 с, 8.3). 0 — жив.</summary>
        public int DeadTicks;

        /// <summary>Тактов замаха врага осталось (12 = 0,4 с, `02` 8.1). 0 — не замахивается.</summary>
        public int AttackWindup;

        /// <summary>Тактов отхода после удара/прерывания осталось (18 = 0,6 с; после прерывания — 15 = 0,5 с, 6.6).</summary>
        public int AttackRecover;

        /// <summary>Тактов иммунитета к повторному прерыванию осталось (45 = 1,5 с, 6.6).</summary>
        public int AttackImmune;

        /// <summary>Жертва замаха (номер сущности), −1 — нет.</summary>
        public int AttackTarget;

        /// <summary>Сырой урон удара (архетип «Ближний» — 10, `02` 8.2; манекен 1-001).</summary>
        public byte AttackRaw;

        /// <summary>
        /// Цикл замаха (манекен 1-001): тактов от конца отхода до следующего замаха; 0 — удар одноразовый,
        /// −1 — сам не замахивается (заглушка 0-028, враги без ИИ). Манекен — 33 такта (1,1 с, период MeleeDummy).
        /// </summary>
        public int AttackPeriod;

        /// <summary>Сектор замаха зафиксирован началом замаха: центр.</summary>
        public WorldPos AttackOrigin;

        /// <summary>Сектор замаха: ось (сырое значение <c>Angle16.Raw</c>).</summary>
        public short AttackFacingRaw;

        /// <summary>
        /// Мозг ИИ врага (карточка 1-007): архетип, состояние, цель, агро, блуждание, кэш пути.
        /// У персонажей и манекенов (1-001) — <see cref="EnemyBrain.Archetype"/> = 0, мозг выключен.
        /// </summary>
        public EnemyBrain Brain;

        /// <summary>Персонаж (есть регенерация, смерть и возрождение у точки старта).</summary>
        public readonly bool IsCharacter => Kind == KindCharacter;

        /// <summary>Жив (не труп и не ждёт возрождения).</summary>
        public readonly bool Alive => DeadTicks == 0 && Health > 0;

        /// <summary>В бою (флаг 6.3).</summary>
        public readonly bool InCombat => CombatTicks > 0;
    }
}
