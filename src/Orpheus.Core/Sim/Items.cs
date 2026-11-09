namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Таблица предметов данными (1-002, <c>06</c> 0 и 2.3): id, вид, длина для досягаемости (<see cref="Combat.Reach"/>),
    /// у инструмента — вид (кирка/топор) и ярус (каменный = 1). Параметры боя у оружия — ссылки, заполнят 1-004 и 1-006.
    /// Id — байт: в снимке «предмет в руках» едет одним байтом (протокол 12+), в сумке — в ячейке.
    /// </summary>
    public static class Items
    {
        /// <summary>Ничего (руки, пустая ячейка).</summary>
        public const byte None = 0;

        /// <summary>Каменная кирка (ярус 1): копать твёрдость 1 (<c>06</c> 2.3).</summary>
        public const byte StonePickaxe = 1;

        /// <summary>Каменный топор (ярус 1): рубить деревья (<c>06</c> 2.3).</summary>
        public const byte StoneAxe = 2;

        /// <summary>Молот: стройка и снос (<c>05</c>, <c>06</c> 2.3).</summary>
        public const byte Hammer = 3;

        /// <summary>Деревянный меч: ближнее оружие (параметры боя — 1-004).</summary>
        public const byte WoodenSword = 4;

        /// <summary>Простой лук: дальнее оружие (параметры боя — 1-006).</summary>
        public const byte SimpleBow = 5;

        /// <summary>Факел: расходник (свет; 3 — в стартовом наборе, <c>06</c> 0).</summary>
        public const byte Torch = 6;

        /// <summary>Размер таблицы (id 0…<see cref="Count"/>−1).</summary>
        public const int Count = 7;

        /// <summary>Вид предмета.</summary>
        public const byte KindNone = 0;

        /// <summary>Инструмент (кирка/топор) — вид в <see cref="ToolKindOf"/>.</summary>
        public const byte KindTool = 1;

        /// <summary>Молот (стройку ведёт 1-011).</summary>
        public const byte KindHammer = 2;

        /// <summary>Оружие ближнего боя.</summary>
        public const byte KindMelee = 3;

        /// <summary>Оружие дальнего боя (стрелы).</summary>
        public const byte KindRanged = 4;

        /// <summary>Расходник (факел).</summary>
        public const byte KindConsumable = 5;

        /// <summary>Вид инструмента: не инструмент.</summary>
        public const byte ToolNone = 0;

        /// <summary>Вид инструмента: кирка — копать (<c>06</c> 2.3).</summary>
        public const byte ToolPick = 1;

        /// <summary>Вид инструмента: топор — рубить (<c>06</c> 2.3).</summary>
        public const byte ToolAxe = 2;

        /// <summary>Ярус 0 — не инструмент; каменный = 1 (<c>06</c> 2.3).</summary>
        public const byte TierNone = 0;

        /// <summary>Ярус каменных инструментов.</summary>
        public const byte TierStone = 1;

        /// <summary>Вид предмета; id вне таблицы — <see cref="KindNone"/>.</summary>
        public static byte KindOf(byte id) => id switch
        {
            StonePickaxe or StoneAxe => KindTool,
            Hammer => KindHammer,
            WoodenSword => KindMelee,
            SimpleBow => KindRanged,
            Torch => KindConsumable,
            _ => KindNone,
        };

        /// <summary>Вид инструмента (кирка/топор); остального — <see cref="ToolNone"/>.</summary>
        public static byte ToolKindOf(byte id) => id switch
        {
            StonePickaxe => ToolPick,
            StoneAxe => ToolAxe,
            _ => ToolNone,
        };

        /// <summary>Ярус инструмента; остального — <see cref="TierNone"/>.</summary>
        public static byte TierOf(byte id) => id switch
        {
            StonePickaxe or StoneAxe => TierStone,
            _ => TierNone,
        };

        /// <summary>Есть такой id в таблице.</summary>
        public static bool Exists(byte id) => id < Count && KindOf(id) != KindNone;

        /// <summary>Копать умеет только кирка (1-002 шаг 4; <c>06</c> 2.3: руки копать не могут).</summary>
        public static bool IsDiggingTool(byte id) => ToolKindOf(id) == ToolPick;

        /// <summary>Рубить умеет топор (<c>06</c> 2.3; рубка — 1-010).</summary>
        public static bool IsChoppingTool(byte id) => ToolKindOf(id) == ToolAxe;

        /// <summary>
        /// Длина предмета для досягаемости «персонаж + предмет» (<see cref="Combat.Reach.Of"/>) в unit (1/256 т):
        /// кирка 0,75 т = 192; топор и молот — как кирка (вопрос карточки 1-002, ответ Автора не пришёл);
        /// меч 1,0 т = 256 (<c>02</c>); у лука и расходника своей длины пока нет (0).
        /// </summary>
        public static int LengthUnits(byte id) => id switch
        {
            StonePickaxe or StoneAxe or Hammer => Combat.Reach.PickaxeUnits,
            WoodenSword => Combat.Reach.SwordUnits,
            _ => 0,
        };
    }
}
