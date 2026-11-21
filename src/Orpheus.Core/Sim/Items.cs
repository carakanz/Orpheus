using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Имена id и видов предметов; сами свойства — строки <see cref="ItemTable"/> (E2-1). Раньше — таблица предметов данными (1-002, <c>06</c> 0 и 2.3): id, вид, длина для досягаемости (<see cref="Combat.Reach"/>),
    /// у инструмента — вид (кирка/топор/молот) и ярус (каменный = 1, медный = 2 — <c>06</c> 2.3).
    /// Параметры боя у оружия — ссылки, заполнят 1-004 и 1-006.
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

        /// <summary>Медная кирка (ярус 2): копать твёрдость 2 — гранит (<c>06</c> 2.3; 1-010).</summary>
        public const byte CopperPickaxe = 7;

        /// <summary>Древесина (материал; до E2-1 — счётчик <see cref="Material.Wood"/>).</summary>
        public const byte Wood = 8;

        /// <summary>Камень (материал).</summary>
        public const byte Stone = 9;

        /// <summary>Смола (материал).</summary>
        public const byte Resin = 10;

        /// <summary>Волокно (материал).</summary>
        public const byte Fiber = 11;

        /// <summary>Медная руда (материал).</summary>
        public const byte CopperOre = 12;

        /// <summary>Стрелы (боеприпас лука, стопка 999).</summary>
        public const byte Arrows = 13;

        /// <summary>Медный слиток (E2-3; <c>06</c> 1.5: 2 медной руды → 1, верстак I).</summary>
        public const byte CopperIngot = 14;

        /// <summary>Верёвка (<c>06</c> 1.5: 3 волокна → 1, руками) — для луков.</summary>
        public const byte Rope = 15;

        /// <summary>Медный топор (ярус 2; <c>06</c> 2.3).</summary>
        public const byte CopperAxe = 16;

        /// <summary>Медный меч (I-2; <c>06</c> 4.3: урон 15 — параметры боя за модулем combat).</summary>
        public const byte CopperSword = 17;

        /// <summary>Медный лук (I-2; <c>06</c> 4.4).</summary>
        public const byte CopperBow = 18;

        /// <summary>Мясо (ресурс; дроп зверей — этап 3).</summary>
        public const byte Meat = 19;

        /// <summary>Жареное мясо (еда среза, <c>06</c> 6.3; стопка 30). Съесть — этап 3.</summary>
        public const byte RoastMeat = 20;

        // E2-11: постройки — предметы (`BRIEF` 11e (3), как в Terraria): делаются рецептом, ставятся из руки.
        // Ключ постройки ↔ предмет — BuildDef.Item (BuildDefs). Факел — прежний Torch (6).

        /// <summary>Деревянный пол (предмет-постройка, E2-11).</summary>
        public const byte WoodFloor = 21;

        /// <summary>Каменный пол.</summary>
        public const byte StoneFloor = 22;

        /// <summary>Настил (над водой).</summary>
        public const byte Platform = 23;

        /// <summary>Деревянная стена.</summary>
        public const byte WoodWall = 24;

        /// <summary>Каменная стена.</summary>
        public const byte StoneWall = 25;

        /// <summary>Деревянный забор.</summary>
        public const byte WoodFence = 26;

        /// <summary>Дверь.</summary>
        public const byte Door = 27;

        /// <summary>Костёр.</summary>
        public const byte Campfire = 28;

        /// <summary>Верстак I.</summary>
        public const byte Workbench = 29;

        /// <summary>Сундук.</summary>
        public const byte Chest = 30;

        /// <summary>Кровать.</summary>
        public const byte Bed = 31;

        // E3-6: медная броня (`06` 4.7, `02` 6.1) — первое снаряжение с ячейками экипировки.

        /// <summary>Медный шлем (`06` 4.7: броня 1, ячейка «голова»; комплект «Медный»).</summary>
        public const byte CopperHelmet = 32;

        /// <summary>Медный доспех (`06` 4.7: броня 1, ячейка «тело»).</summary>
        public const byte CopperChest = 33;

        /// <summary>Медные поножи (`06` 4.7: броня 1, ячейка «ноги»).</summary>
        public const byte CopperLegs = 34;

        /// <summary>Шкура (`06` 4.7: ингредиент брони; добыча с зверей — допущение E3-6).</summary>
        public const byte Hide = 35;

        /// <summary>Размер таблицы (id 0…<see cref="Count"/>−1) — строк в <see cref="ItemTable"/>.</summary>
        public static int Count => ItemTable.Count;

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

        /// <summary>Броня (E3-6): носится в ячейке снаряжения (<see cref="SlotHead"/>…).</summary>
        public const byte KindArmor = 6;

        /// <summary>Ячейка снаряжения: не броня (у предмета нет ячейки).</summary>
        public const byte SlotNone = 0;

        /// <summary>Ячейка снаряжения: голова.</summary>
        public const byte SlotHead = 1;

        /// <summary>Ячейка снаряжения: тело.</summary>
        public const byte SlotBody = 2;

        /// <summary>Ячейка снаряжения: ноги.</summary>
        public const byte SlotLegs = 3;

        /// <summary>Комплект: без комплекта (бонуса набора нет).</summary>
        public const byte SetNone = 0;

        /// <summary>Комплект «Медный» (`06` 4.7: бонус «Устойчивость» — масса ×2, отброс вдвое короче, `02` 6.5).</summary>
        public const byte SetCopper = 1;

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

        /// <summary>Ярус медных инструментов (<c>06</c> 2.3: открывает твёрдость 2).</summary>
        public const byte TierCopper = 2;

        /// <summary>Вид предмета; id вне таблицы — <see cref="KindNone"/>.</summary>
        public static byte KindOf(byte id) => ItemTable.Get(id).Kind;

        /// <summary>Броня предмета (единицы формулы `02` 6.1); у неброни — 0 (E3-6).</summary>
        public static int ArmorOf(byte id) => ItemTable.Get(id).Armor;

        /// <summary>Ячейка снаряжения предмета (<see cref="SlotHead"/>…); у неброни — <see cref="SlotNone"/> (E3-6).</summary>
        public static byte SlotOf(byte id) => ItemTable.Get(id).Slot;

        /// <summary>Комплект предмета (<see cref="SetCopper"/>); без комплекта — <see cref="SetNone"/> (E3-6).</summary>
        public static byte SetOf(byte id) => ItemTable.Get(id).SetId;

        /// <summary>Вид инструмента (кирка/топор); остального — <see cref="ToolNone"/>.</summary>
        public static byte ToolKindOf(byte id) => ItemTable.Get(id).Tool;

        /// <summary>Ярус инструмента; остального — <see cref="TierNone"/>. Молот — каменный ярус (1-011: строки
        /// построек в <see cref="World.DigTimes"/> требуют ярус 1).</summary>
        public static byte TierOf(byte id) => ItemTable.Get(id).Tier;

        /// <summary>
        /// Вид инструмента копания (<c>01</c> 5, 1-010): кирка — Pick, топор — Axe, молот — Hammer (снос построек, <c>05</c> 1.3);
        /// остального — <see cref="ToolKind.Hands"/> (руки).
        /// </summary>
        public static ToolKind DigToolOf(byte id) => ItemTable.Get(id).DigTool;

        /// <summary>Есть такой id в таблице (не «пусто»).</summary>
        public static bool Exists(byte id) => ItemTable.Exists(id);

        /// <summary>Размер стопки предмета в ячейке; нет такого — 0.</summary>
        public static int MaxStackOf(byte id) => ItemTable.MaxStackOf(id);

        /// <summary>Копать умеет только кирка (1-002 шаг 4; <c>06</c> 2.3: руки копать не могут).</summary>
        public static bool IsDiggingTool(byte id) => ToolKindOf(id) == ToolPick;

        /// <summary>Рубить умеет топор (<c>06</c> 2.3; рубка — 1-010).</summary>
        public static bool IsChoppingTool(byte id) => ToolKindOf(id) == ToolAxe;

        /// <summary>
        /// Длина предмета для досягаемости «персонаж + предмет» (<see cref="Combat.Reach.Of"/>) в unit (1/256 т):
        /// кирка 0,75 т = 192; топор и молот — как кирка; руки — <see cref="Combat.Reach.HandsUnits"/> (1-010);
        /// меч 1,0 т = 256 (<c>02</c>); у лука, материалов и расходников своей длины нет (0). Числа — в <see cref="ItemTable"/>.
        /// </summary>
        public static int LengthUnits(byte id) => id < ItemTable.Count ? ItemTable.Get(id).LengthUnits : 0;
    }
}
