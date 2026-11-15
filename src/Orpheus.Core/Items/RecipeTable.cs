using Orpheus.Core.World;

// Пространство имён — Orpheus.Core.Sim (см. ItemTable.cs).
namespace Orpheus.Core.Sim
{
    /// <summary>Станок рецепта (E2-3; <c>06</c> 3.3): где рецепт делается.</summary>
    public enum CraftStation : byte
    {
        /// <summary>Руки: всегда и везде.</summary>
        None = 0,

        /// <summary>Верстак I (<see cref="ObjectId.Workbench"/>) не дальше <see cref="Simulation.CraftRadiusUnits"/>.</summary>
        Workbench = 1,

        /// <summary>Костёр (<see cref="ObjectId.Campfire"/>): еда.</summary>
        Campfire = 2,
    }

    /// <summary>
    /// Строка таблицы рецептов (E2-3): что получается, из чего (до трёх ингредиентов), у какого станка и за сколько
    /// тактов. Номер рецепта — место в <see cref="RecipeTable"/> (едет по сети и в сохранении, менять нельзя).
    /// </summary>
    public readonly struct RecipeDef
    {
        /// <summary>Номер рецепта (1…; 0 — нет рецепта).</summary>
        public readonly byte Id;

        /// <summary>Что получается.</summary>
        public readonly byte Result;

        /// <summary>Сколько получается за одно изготовление.</summary>
        public readonly ushort ResultCount;

        /// <summary>Станок.</summary>
        public readonly CraftStation Station;

        /// <summary>Время одного изготовления, тактов (30 тактов = 1 с).</summary>
        public readonly ushort Ticks;

        /// <summary>Ингредиенты: предмет и количество; количество 0 — ингредиента нет.</summary>
        public readonly byte In1, In2, In3;
        public readonly ushort N1, N2, N3;

        public RecipeDef(byte id, byte result, int resultCount, CraftStation station, int ticks,
            byte in1, int n1, byte in2 = Items.None, int n2 = 0, byte in3 = Items.None, int n3 = 0)
        {
            Id = id;
            Result = result;
            ResultCount = (ushort)resultCount;
            Station = station;
            Ticks = (ushort)ticks;
            In1 = in1;
            N1 = (ushort)n1;
            In2 = in2;
            N2 = (ushort)n2;
            In3 = in3;
            N3 = (ushort)n3;
        }

        /// <summary>Ингредиентов в рецепте (1…<see cref="RecipeTable.MaxIngredients"/>).</summary>
        public int IngredientCount => N3 > 0 ? 3 : N2 > 0 ? 2 : 1;

        /// <summary>Ингредиент № <paramref name="i"/> (0…2): предмет.</summary>
        public byte IngredientItem(int i) => i == 0 ? In1 : i == 1 ? In2 : In3;

        /// <summary>Ингредиент № <paramref name="i"/> (0…2): количество на одно изготовление.</summary>
        public int IngredientCountOf(int i) => i == 0 ? N1 : i == 1 ? N2 : N3;
    }

    /// <summary>
    /// Таблица рецептов вертикального среза (E2-3, шаг 2.3; <c>06</c> раздел 10 «Рецепты среза», 1.5, 2.3, 3.3, 4.4, 6):
    /// одна строка — один рецепт, веток по номеру в коде нет. Время — 0,5 с на рецепт (<c>06</c> 3.3) = 15 тактов.
    /// <para>С E2-11 каждая постройка <see cref="BuildDefs"/> — предмет и рецепт (строки 15…25, факел — 4); цена рецепта = цена в <see cref="BuildDefs"/> (тест).</para>
    /// <para>Добавлять строки — только в конец, номера подряд; строка стоит на месте своего номера (проверка в конструкторе).</para>
    /// </summary>
    public static class RecipeTable
    {
        /// <summary>Время любого рецепта среза: 0,5 с (<c>06</c> 1.5, 3.3).</summary>
        public const int DefaultTicks = 15;

        /// <summary>Наибольшее число ингредиентов в рецепте.</summary>
        public const int MaxIngredients = 3;

        public const byte StonePickaxe = 1;
        public const byte StoneAxe = 2;
        public const byte Hammer = 3;
        public const byte Torch = 4;
        public const byte Rope = 5;
        public const byte WoodenSword = 6;
        public const byte SimpleBow = 7;
        public const byte Arrows = 8;
        public const byte CopperIngot = 9;
        public const byte CopperPickaxe = 10;
        public const byte CopperAxe = 11;
        public const byte CopperSword = 12;
        public const byte CopperBow = 13;
        public const byte RoastMeat = 14;

        // E2-11: постройки — предметы. Цена — прежняя цена постановки (`06` 6.9, `05` 1.3): одна постановка = один предмет.
        public const byte WoodFloor = 15;
        public const byte StoneFloor = 16;
        public const byte Platform = 17;
        public const byte WoodWall = 18;
        public const byte StoneWall = 19;
        public const byte WoodFence = 20;
        public const byte Door = 21;
        public const byte Campfire = 22;
        public const byte Workbench = 23;
        public const byte Chest = 24;
        public const byte Bed = 25;

        private const CraftStation Hands = CraftStation.None;
        private const CraftStation Bench = CraftStation.Workbench;

        private static readonly RecipeDef[] Rows =
        {
            default, // 0 — нет рецепта
            // Руками (`06` 2.3, 3.3, 6.9, 1.5).
            new RecipeDef(StonePickaxe, Items.StonePickaxe, 1, Hands, DefaultTicks, Items.Stone, 3, Items.Wood, 2),
            new RecipeDef(StoneAxe, Items.StoneAxe, 1, Hands, DefaultTicks, Items.Stone, 3, Items.Wood, 2),
            new RecipeDef(Hammer, Items.Hammer, 1, Hands, DefaultTicks, Items.Wood, 4, Items.Stone, 2),
            new RecipeDef(Torch, Items.Torch, 1, Hands, DefaultTicks, Items.Wood, 1, Items.Resin, 1),
            new RecipeDef(Rope, Items.Rope, 1, Hands, DefaultTicks, Items.Fiber, 3),
            // Верстак I (`06` 4.4 шаблоны, 6.7, 1.5, 2.3).
            new RecipeDef(WoodenSword, Items.WoodenSword, 1, Bench, DefaultTicks, Items.Wood, 8),
            new RecipeDef(SimpleBow, Items.SimpleBow, 1, Bench, DefaultTicks, Items.Wood, 10, Items.Rope, 3),
            new RecipeDef(Arrows, Items.Arrows, 10, Bench, DefaultTicks, Items.Wood, 1, Items.Stone, 1),
            new RecipeDef(CopperIngot, Items.CopperIngot, 1, Bench, DefaultTicks, Items.CopperOre, 2),
            new RecipeDef(CopperPickaxe, Items.CopperPickaxe, 1, Bench, DefaultTicks, Items.CopperIngot, 8, Items.Wood, 3),
            new RecipeDef(CopperAxe, Items.CopperAxe, 1, Bench, DefaultTicks, Items.CopperIngot, 7, Items.Wood, 3),
            new RecipeDef(CopperSword, Items.CopperSword, 1, Bench, DefaultTicks, Items.CopperIngot, 8, Items.Wood, 2),
            new RecipeDef(CopperBow, Items.CopperBow, 1, Bench, DefaultTicks, Items.Wood, 10, Items.CopperIngot, 4, Items.Rope, 3),
            // Костёр (`06` 6.3).
            new RecipeDef(RoastMeat, Items.RoastMeat, 1, CraftStation.Campfire, DefaultTicks, Items.Meat, 1),
            // Постройки (E2-11; `06` 3.3: «руки — … верстак I и постройки»; костёр — у верстака I, «В-I» `05`). Выход — 1 штука:
            // в `06` 6.9 стена — 2 древесины на тайл, «4 из 1 бревна» в документах нет.
            new RecipeDef(WoodFloor, Items.WoodFloor, 1, Hands, DefaultTicks, Items.Wood, 1),
            new RecipeDef(StoneFloor, Items.StoneFloor, 1, Hands, DefaultTicks, Items.Stone, 1),
            new RecipeDef(Platform, Items.Platform, 1, Hands, DefaultTicks, Items.Wood, 1),
            // E2-12: стена и забор — ячейки 0,5 × 0,5, погонный тайл стены — 2 ячейки: выход 2, цена тайла прежняя.
            new RecipeDef(WoodWall, Items.WoodWall, 2, Hands, DefaultTicks, Items.Wood, 2),
            new RecipeDef(StoneWall, Items.StoneWall, 2, Hands, DefaultTicks, Items.Stone, 2),
            new RecipeDef(WoodFence, Items.WoodFence, 2, Hands, DefaultTicks, Items.Wood, 1),
            new RecipeDef(Door, Items.Door, 1, Hands, DefaultTicks, Items.Wood, 6),
            new RecipeDef(Campfire, Items.Campfire, 1, Bench, DefaultTicks, Items.Stone, 8, Items.Wood, 4),
            new RecipeDef(Workbench, Items.Workbench, 1, Hands, DefaultTicks, Items.Wood, 10, Items.Stone, 6),
            new RecipeDef(Chest, Items.Chest, 1, Hands, DefaultTicks, Items.Wood, 8),
            new RecipeDef(Bed, Items.Bed, 1, Hands, DefaultTicks, Items.Wood, 10, Items.Fiber, 5),
            // Новые строки — сюда, в конец; номер = место в таблице.
        };

        static RecipeTable()
        {
            for (int i = 1; i < Rows.Length; i++)
            {
                ref readonly RecipeDef r = ref Rows[i];
                bool ok = r.Id == i && ItemTable.Exists(r.Result) && r.ResultCount > 0 && r.Ticks > 0 && r.N1 > 0 && ItemTable.Exists(r.In1)
                    && (r.N2 == 0 || ItemTable.Exists(r.In2)) && (r.N3 == 0 || (r.N2 > 0 && ItemTable.Exists(r.In3)));
                if (!ok)
                {
                    throw new System.InvalidOperationException($"Таблица рецептов: строка {i} неверна (id {r.Id}).");
                }
            }
        }

        /// <summary>Строк в таблице вместе с нулевой (номера рецептов 1…<see cref="Count"/>−1).</summary>
        public static int Count => Rows.Length;

        /// <summary>Есть такой рецепт (1…<see cref="Count"/>−1).</summary>
        public static bool Exists(int id) => id > 0 && id < Rows.Length;

        /// <summary>Строка рецепта; номер вне таблицы — пустая строка (id 0).</summary>
        public static ref readonly RecipeDef Get(int id) => ref Rows[Exists(id) ? id : 0];

        /// <summary>Объект мира, который служит станком; для «рук» — <see cref="ObjectId.Empty"/>.</summary>
        public static ushort ObjectOf(CraftStation station) => station switch
        {
            CraftStation.Workbench => ObjectId.Workbench,
            CraftStation.Campfire => ObjectId.Campfire,
            _ => ObjectId.Empty,
        };
    }
}
