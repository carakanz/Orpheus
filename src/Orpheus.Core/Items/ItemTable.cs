using Orpheus.Core.Combat;
using Orpheus.Core.World;

// Пространство имён — Orpheus.Core.Sim (а не Orpheus.Core.Items): класс Sim.Items уже есть,
// пространство с тем же именем сломало бы `Items.X` во всех файлах вне Sim (E2-1, «Решения»).
namespace Orpheus.Core.Sim
{
    /// <summary>Категория предмета (E2-1; <c>06</c> 1.3 — «ресурс», «снаряжение», «расходник»).</summary>
    public enum ItemCategory : byte
    {
        /// <summary>Нет предмета.</summary>
        None = 0,

        /// <summary>Материал («ресурс» <c>06</c> 1.3): сырьё и переработанное. Стопка 99.</summary>
        Material = 1,

        /// <summary>Инструмент («снаряжение»): кирка, топор, молот. Стопка 1.</summary>
        Tool = 2,

        /// <summary>Оружие («снаряжение»): меч, лук. Стопка 1.</summary>
        Weapon = 3,

        /// <summary>Расходник: боеприпасы (стопка 999), еда, зелья.</summary>
        Consumable = 4,

        /// <summary>Постройка в виде предмета (факел): ставится в мир. Стопка 99.</summary>
        Building = 5,

        /// <summary>Броня (E3-6; «снаряжение» `06` 1.3): носится в ячейке снаряжения. Стопка 1.</summary>
        Armor = 6,
    }

    /// <summary>
    /// Строка таблицы предметов (E2-1): всё, что ядро знает о предмете, — данными.
    /// Новая строка — новый id в конце <see cref="ItemTable"/> (id едут в сохранениях и по сети, менять нельзя).
    /// </summary>
    public readonly struct ItemDef
    {
        /// <summary>Номер предмета (байт; 0 — пусто).</summary>
        public readonly byte Id;

        /// <summary>Ключ названия (латиницей; по нему клиент берёт подпись и значок <c>item_&lt;ключ&gt;</c>).</summary>
        public readonly string Key;

        /// <summary>Размер стопки в одной ячейке (≥ 1).</summary>
        public readonly ushort MaxStack;

        /// <summary>Категория.</summary>
        public readonly ItemCategory Category;

        /// <summary>Вид для правил боя и стройки (<see cref="Items.KindTool"/>…).</summary>
        public readonly byte Kind;

        /// <summary>Вид инструмента: <see cref="Items.ToolPick"/> / <see cref="Items.ToolAxe"/> / <see cref="Items.ToolNone"/>.</summary>
        public readonly byte Tool;

        /// <summary>Ярус инструмента (<see cref="Items.TierStone"/>…); не инструмент — 0.</summary>
        public readonly byte Tier;

        /// <summary>Чем предмет копает (<see cref="DigTimes"/>): кирка, топор, молот; остальное — руки.</summary>
        public readonly ToolKind DigTool;

        /// <summary>Длина для досягаемости, unit (1/256 тайла); 0 — своей длины нет.</summary>
        public readonly int LengthUnits;

        /// <summary>Броня (E3-6; единицы формулы `02` 6.1); у неброни — 0.</summary>
        public readonly byte Armor;

        /// <summary>Ячейка снаряжения (<see cref="Items.SlotHead"/>…); у неброни — <see cref="Items.SlotNone"/> (E3-6).</summary>
        public readonly byte Slot;

        /// <summary>Комплект (<see cref="Items.SetCopper"/>); без комплекта — <see cref="Items.SetNone"/> (E3-6).</summary>
        public readonly byte SetId;

        public ItemDef(byte id, string key, ushort maxStack, ItemCategory category, byte kind = Items.KindNone,
            byte tool = Items.ToolNone, byte tier = Items.TierNone, ToolKind digTool = ToolKind.Hands, int lengthUnits = 0,
            byte armor = 0, byte slot = Items.SlotNone, byte setId = Items.SetNone)
        {
            Id = id;
            Key = key;
            MaxStack = maxStack;
            Category = category;
            Kind = kind;
            Tool = tool;
            Tier = tier;
            DigTool = digTool;
            LengthUnits = lengthUnits;
            Armor = armor;
            Slot = slot;
            SetId = setId;
        }
    }

    /// <summary>
    /// Таблица предметов (E2-1, шаг 2.1; <c>06</c> 1.3, 5.3): одна строка — один предмет. Правила читают
    /// свойства отсюда (<see cref="Items"/> — тонкая обёртка с именами id), веток по id в коде нет.
    /// <para>Стопки (<c>06</c> 5.3): ресурс 99, боеприпасы 999, снаряжение 1; факел — 99 (допущение E2-1).</para>
    /// <para>Добавлять строки — только в конец, id подряд; строка обязана стоять на месте своего id (проверка в конструкторе).</para>
    /// </summary>
    public static class ItemTable
    {
        /// <summary>Стопка материала (<c>06</c> 5.3: ресурс 99).</summary>
        public const ushort StackMaterial = 99;

        /// <summary>Стопка боеприпасов (<c>06</c> 5.3: 999).</summary>
        public const ushort StackAmmo = 999;

        /// <summary>Стопка снаряжения (<c>06</c> 5.3: 1).</summary>
        public const ushort StackGear = 1;

        /// <summary>Стопка еды (<c>06</c> 1.3: еда 30).</summary>
        public const ushort StackFood = 30;

        /// <summary>Стопка предмета-мебели: дверь, станок, сундук, кровать (E2-11, допущение: в <c>06</c> 5.3 нет; 10 — как «предметы вызова»).</summary>
        public const ushort StackFurniture = 10;

        private static readonly ItemDef[] Rows =
        {
            new ItemDef(Items.None, "none", 0, ItemCategory.None, lengthUnits: Reach.HandsUnits), // руки копают то, что разрешено таблицей (1-010)
            new ItemDef(Items.StonePickaxe, "stone_pickaxe", StackGear, ItemCategory.Tool, Items.KindTool, Items.ToolPick, Items.TierStone, ToolKind.Pick, Reach.PickaxeUnits),
            new ItemDef(Items.StoneAxe, "stone_axe", StackGear, ItemCategory.Tool, Items.KindTool, Items.ToolAxe, Items.TierStone, ToolKind.Axe, Reach.PickaxeUnits),
            new ItemDef(Items.Hammer, "hammer", StackGear, ItemCategory.Tool, Items.KindHammer, Items.ToolNone, Items.TierStone, ToolKind.Hammer, Reach.PickaxeUnits),
            new ItemDef(Items.WoodenSword, "wooden_sword", StackGear, ItemCategory.Weapon, Items.KindMelee, lengthUnits: Reach.SwordUnits),
            new ItemDef(Items.SimpleBow, "simple_bow", StackGear, ItemCategory.Weapon, Items.KindRanged),
            new ItemDef(Items.Torch, "torch", StackMaterial, ItemCategory.Building, Items.KindConsumable),
            new ItemDef(Items.CopperPickaxe, "copper_pickaxe", StackGear, ItemCategory.Tool, Items.KindTool, Items.ToolPick, Items.TierCopper, ToolKind.Pick, Reach.PickaxeUnits),
            new ItemDef(Items.Wood, "wood", StackMaterial, ItemCategory.Material),
            new ItemDef(Items.Stone, "stone", StackMaterial, ItemCategory.Material),
            new ItemDef(Items.Resin, "resin", StackMaterial, ItemCategory.Material),
            new ItemDef(Items.Fiber, "fiber", StackMaterial, ItemCategory.Material),
            new ItemDef(Items.CopperOre, "copper_ore", StackMaterial, ItemCategory.Material),
            new ItemDef(Items.Arrows, "arrows", StackAmmo, ItemCategory.Consumable),
            // E2-3 (шаг 2.3, `06` раздел 10): переработанное, медный ярус, еда среза.
            new ItemDef(Items.CopperIngot, "copper_ingot", StackMaterial, ItemCategory.Material),
            new ItemDef(Items.Rope, "rope", StackMaterial, ItemCategory.Material),
            new ItemDef(Items.CopperAxe, "copper_axe", StackGear, ItemCategory.Tool, Items.KindTool, Items.ToolAxe, Items.TierCopper, ToolKind.Axe, Reach.PickaxeUnits),
            new ItemDef(Items.CopperSword, "copper_sword", StackGear, ItemCategory.Weapon, Items.KindMelee, lengthUnits: Reach.SwordUnits),
            new ItemDef(Items.CopperBow, "copper_bow", StackGear, ItemCategory.Weapon, Items.KindRanged),
            new ItemDef(Items.Meat, "meat", StackMaterial, ItemCategory.Material),
            new ItemDef(Items.RoastMeat, "roast_meat", StackFood, ItemCategory.Consumable),
            // E2-11: постройки — предметы (ставятся из руки, BuildDef.Item). Стопки: стены, полы, забор — 99 (как ресурс, `06` 5.3);
            // дверь, станки, сундук, кровать — 10 (в `06` 5.3 стопки мебели нет; ближайшая — «предметы вызова» 10, допущение E2-11).
            new ItemDef(Items.WoodFloor, "wood_floor", StackMaterial, ItemCategory.Building),
            new ItemDef(Items.StoneFloor, "stone_floor", StackMaterial, ItemCategory.Building),
            new ItemDef(Items.Platform, "platform", StackMaterial, ItemCategory.Building),
            new ItemDef(Items.WoodWall, "wood_wall", StackMaterial, ItemCategory.Building),
            new ItemDef(Items.StoneWall, "stone_wall", StackMaterial, ItemCategory.Building),
            new ItemDef(Items.WoodFence, "wood_fence", StackMaterial, ItemCategory.Building),
            new ItemDef(Items.Door, "door", StackFurniture, ItemCategory.Building),
            new ItemDef(Items.Campfire, "campfire", StackFurniture, ItemCategory.Building),
            new ItemDef(Items.Workbench, "workbench", StackFurniture, ItemCategory.Building),
            new ItemDef(Items.Chest, "chest", StackFurniture, ItemCategory.Building),
            new ItemDef(Items.Bed, "bed", StackFurniture, ItemCategory.Building),
            // E3-6: медная броня (`06` 4.7) — броня 1 каждая, комплект «Медный» (бонус «Устойчивость» при полном);
            // шкура — ингредиент (`06` 4.7), добыча с зверей (допущение E3-6).
            new ItemDef(Items.CopperHelmet, "copper_helmet", StackGear, ItemCategory.Armor, Items.KindArmor, armor: 1, slot: Items.SlotHead, setId: Items.SetCopper),
            new ItemDef(Items.CopperChest, "copper_chest", StackGear, ItemCategory.Armor, Items.KindArmor, armor: 1, slot: Items.SlotBody, setId: Items.SetCopper),
            new ItemDef(Items.CopperLegs, "copper_legs", StackGear, ItemCategory.Armor, Items.KindArmor, armor: 1, slot: Items.SlotLegs, setId: Items.SetCopper),
            new ItemDef(Items.Hide, "hide", StackMaterial, ItemCategory.Material),
            // Новые строки — сюда, в конец; id = место в таблице.
        };

        static ItemTable()
        {
            for (int i = 0; i < Rows.Length; i++)
            {
                if (Rows[i].Id != i)
                {
                    throw new System.InvalidOperationException($"Таблица предметов: строка {i} несёт id {Rows[i].Id}.");
                }
            }
        }

        /// <summary>Строк в таблице (id 0…<see cref="Count"/>−1).</summary>
        public static int Count => Rows.Length;

        /// <summary>Строка предмета; id вне таблицы — строка «пусто» (id 0).</summary>
        public static ref readonly ItemDef Get(byte id) => ref Rows[id < Rows.Length ? id : 0];

        /// <summary>Есть такой предмет (id 1…<see cref="Count"/>−1).</summary>
        public static bool Exists(byte id) => id != Items.None && id < Rows.Length;

        /// <summary>Размер стопки; нет такого предмета — 0.</summary>
        public static int MaxStackOf(byte id) => Exists(id) ? Rows[id].MaxStack : 0;

        /// <summary>Предмет счётчика-материала прежней сумки (<see cref="Material"/>, до E2-1); вне таблицы — <see cref="Items.None"/>.</summary>
        public static byte OfMaterial(Material m) => m switch
        {
            Material.Wood => Items.Wood,
            Material.Stone => Items.Stone,
            Material.Resin => Items.Resin,
            Material.Fiber => Items.Fiber,
            Material.Copper => Items.CopperOre,
            Material.Arrows => Items.Arrows,
            Material.Torch => Items.Torch,
            _ => Items.None,
        };
    }
}
