using Orpheus.Core.Geometry;

// Пространство имён — Orpheus.Core.Sim (см. ItemTable.cs).
namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Куча на земле (E2-1, <c>06</c> 5.3 «Предметы на земле»): стопка одного предмета, лежащая в мире.
    /// Не блокирует движение, врагам безразлична. Живёт в списке <see cref="Simulation.Piles"/> по возрастанию
    /// номера; номера растут и не переиспользуются.
    /// </summary>
    public struct GroundPile
    {
        /// <summary>Номер кучи (свой счётчик, не номер сущности).</summary>
        public int Id;

        /// <summary>Предмет (<see cref="ItemTable"/>).</summary>
        public byte Item;

        /// <summary>Количество (1…<see cref="Simulation.PileMaxCount"/>).</summary>
        public ushort Count;

        /// <summary>Позиция, unit (1/256 тайла), как у сущностей.</summary>
        public WorldPos Position;

        /// <summary>Такт, на котором куча исчезает (время жизни <see cref="Simulation.PileLifetimeTicks"/>).</summary>
        public long ExpireTick;

        /// <summary>Такт, с которого кучу можно подобрать (задержка после появления и после выброса).</summary>
        public long PickupTick;

        /// <summary>
        /// Вид (C-031): <see cref="KindPlain"/> — обычная куча; <see cref="KindArrowWall"/> / <see cref="KindArrowGround"/> —
        /// воткнувшаяся стрела (сама — предмет, 1 штука, не сливается; рисуется стрелой, а не значком).
        /// </summary>
        public byte Kind;

        /// <summary>Ось полёта воткнувшейся стрелы (u16, как прицел) — для картинки; у обычной кучи 0.</summary>
        public ushort Aim;

        /// <summary>
        /// Владелец личной капли (E3-3b, `06` 9): номер сущности-игрока, увидевшего добычу; 0 — общая куча.
        /// Видна и подбирается только владельцем; не сливается с чужими и общими.
        /// </summary>
        public int Owner;

        /// <summary>Обычная куча.</summary>
        public const byte KindPlain = 0;

        /// <summary>Стрела, воткнувшаяся в преграду (стена, дерево, скала, закрытая дверь).</summary>
        public const byte KindArrowWall = 1;

        /// <summary>Стрела, долетевшая до конца дальности и воткнувшаяся в землю.</summary>
        public const byte KindArrowGround = 2;

        /// <summary>Наибольший вид.</summary>
        public const byte MaxKind = KindArrowGround;

        /// <summary>Воткнувшаяся стрела (C-031).</summary>
        public readonly bool IsStuckArrow => Kind != KindPlain;

        public override readonly string ToString() => $"pile#{Id} {ItemTable.Get(Item).Key}×{Count} {Position}{(Kind != 0 ? $" стрела{Kind} ∠{Aim}" : "")}";
    }

    /// <summary>Операция игрока с ячейками сумки (заявка клиента, решает сервер — <see cref="Simulation.RequestBagOp"/>).</summary>
    public enum BagOp : byte
    {
        /// <summary>Переместить ячейку в другую: перенос, доукладка или обмен (<see cref="Inventory.Move"/>).</summary>
        Move = 1,

        /// <summary>Отделить часть стопки в другую ячейку (<see cref="Inventory.Split"/>).</summary>
        Split = 2,

        /// <summary>Выбросить из ячейки на землю (количество; 0 — всю стопку).</summary>
        Drop = 3,

        /// <summary>Изготовить рецепт (E2-3; только внутри ядра — <see cref="Simulation.RequestCraft"/>, по сети — своё сообщение).</summary>
        Craft = 4,

        /// <summary>Отменить позицию очереди изготовления (E2-3; <see cref="Simulation.RequestCraftCancel"/>).</summary>
        CraftCancel = 5,

        /// <summary>
        /// Изготовить сразу в ячейку сумки (C-013; <see cref="Simulation.RequestCraftInto"/>): рецепт, ячейка (0xFF — куда
        /// влезет), сколько раз. Без очереди и без времени — как изготовление при постановке (E2-16).
        /// </summary>
        CraftInto = 6,
    }
}
