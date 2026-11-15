using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

// Пространство имён — Orpheus.Core.Sim (см. ItemTable.cs).
namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Правила крафта без состояния (E2-3): одни и те же проверки у сервера (<see cref="Simulation"/>) и у клиента
    /// (окно крафта, бот) — «на сколько хватает» и «рядом ли станок».
    /// </summary>
    public static class CraftRules
    {
        /// <summary>Станок работает не дальше 3 тайлов от центра персонажа до центра тайла станка (<c>06</c> 3.3) = 768 unit.</summary>
        public const int RadiusUnits = 3 * Torus.SubTilesPerTile;

        /// <summary>Дальше радиуса станок ищется ещё до 8 тайлов — чтобы отличить «слишком далеко» от «станка нет».</summary>
        public const int SearchTiles = 8;

        /// <summary>
        /// E2-10: ингредиенты берутся и из сундуков не дальше 6 тайлов (<c>06</c> 3.5: «из сундуков в 6 тайлах от станка»)
        /// от центра тайла станка до центра тайла сундука; у рецептов «руками» — от центра персонажа. Тор — через шов.
        /// </summary>
        public const int ChestRadiusTiles = 6;

        /// <summary><see cref="ChestRadiusTiles"/> в unit.</summary>
        public const int ChestRadiusUnits = ChestRadiusTiles * Torus.SubTilesPerTile;

        /// <summary>Рецепт, который делает предмет <paramref name="item"/> (первый по номеру); нет — 0 (E2-16: постройка из меню).</summary>
        public static int RecipeFor(byte item)
        {
            for (int id = 1; id < RecipeTable.Count; id++)
            {
                if (item != Items.None && RecipeTable.Get(id).Result == item)
                {
                    return id;
                }
            }

            return 0;
        }

        /// <summary>
        /// Сколько предметов <paramref name="item"/> можно изготовить на месте (E2-16, постановка из меню): рецепт есть, станок
        /// рецепта рядом (<see cref="StationCheck"/>), ингредиенты — сумка и сундуки рядом (<paramref name="stock"/>, null — сумка);
        /// число изготовлений × выход рецепта. Готовые предметы в сумке сюда не входят.
        /// </summary>
        public static int Makeable(WorldModel world, WorldPos pos, in Inventory inv, ChestStock? stock, byte item)
        {
            int id = RecipeFor(item);
            if (id == 0)
            {
                return 0;
            }

            ref readonly RecipeDef def = ref RecipeTable.Get(id);
            return StationCheck(world, pos, def.Station) == CraftReply.Accepted ? Affordable(inv, def, stock) * def.ResultCount : 0;
        }

        /// <summary>
        /// C-013: сумка после снятия ингредиентов <paramref name="runs"/> изготовлений — только сумочная часть, тем же порядком,
        /// что <c>Simulation.TakeIngredients</c> (из сумки сколько есть, остальное — из сундуков рядом, их тут нет). Копия:
        /// по ней сервер и клиент одной функцией решают, куда ляжет результат.
        /// </summary>
        public static Inventory BagAfterIngredients(in Inventory bag, in RecipeDef def, int runs)
        {
            Inventory v = bag;
            for (int i = 0; i < def.IngredientCount; i++)
            {
                byte item = def.IngredientItem(i);
                v.Remove(item, Math.Min(def.IngredientCountOf(i) * runs, v.Count(item)));
            }

            return v;
        }

        /// <summary>
        /// C-013: сколько предметов <paramref name="item"/> влезет в ячейку <paramref name="cell"/>: пустая — стопка, с тем же
        /// предметом — до полной стопки, с другим — 0.
        /// </summary>
        public static int RoomInCell(in Inventory bag, int cell, byte item)
        {
            if ((uint)cell >= Inventory.Cells || item == Items.None)
            {
                return 0;
            }

            int max = ItemTable.MaxStackOf(item);
            int have = bag.CountIn(cell);
            return have == 0 ? max : bag.ItemAt(cell) == item ? Math.Max(0, max - have) : 0;
        }

        /// <summary>
        /// C-013: сколько изготовлений рецепта <paramref name="def"/> (не больше <paramref name="want"/>) поместится результатом
        /// в ячейку <paramref name="cell"/> (<c>0xFF</c> — в сумку куда угодно, с доукладкой), если ингредиенты снимаются из
        /// этой же сумки (освобождённое ими место считается). Ингредиентов хватает — проверяет вызывающий.
        /// </summary>
        public static int RunsThatFit(in Inventory bag, in RecipeDef def, int cell, int want)
        {
            for (int runs = want; runs > 0; runs--)
            {
                Inventory v = BagAfterIngredients(bag, def, runs);
                int room = cell == 0xFF ? v.RoomFor(def.Result) : RoomInCell(v, cell, def.Result);
                if (room >= runs * def.ResultCount)
                {
                    return runs;
                }
            }

            return 0;
        }

        /// <summary>
        /// C-013 (слова Автора 15.11: «если крафтится 1 предмет … можно делать» при полной сумке): постановка «из меню»
        /// <paramref name="units"/> предметов-построек <paramref name="item"/> — остаток от выхода рецепта (стена — 2, пол — 4)
        /// влезет в сумку? Готовые берутся из сумки, недостающее — ⌈недостаёт ÷ выход⌉ изготовлений; остатка нет (выход 1 или
        /// хватило готовых) — true при любой сумке. Тот же расчёт у сервера (<c>Simulation.PayOrCraft</c>) и у призрака клиента.
        /// </summary>
        public static bool MenuLeftoverFits(in Inventory bag, byte item, int units)
        {
            int have = Math.Min(units, bag.Count(item));
            int missing = units - have;
            int recipe = missing > 0 ? RecipeFor(item) : 0;
            if (recipe == 0)
            {
                return true;
            }

            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            int runs = (missing + def.ResultCount - 1) / def.ResultCount;
            int extra = runs * def.ResultCount - missing;
            if (extra <= 0)
            {
                return true;
            }

            Inventory v = BagAfterIngredients(bag, def, runs);
            v.Remove(item, have);
            return v.RoomFor(item) >= extra;
        }

        /// <summary>На сколько изготовлений хватает ингредиентов в сумке (по всем ячейкам).</summary>
        public static int Affordable(in Inventory inv, in RecipeDef def)
        {
            int can = int.MaxValue;
            for (int i = 0; i < def.IngredientCount; i++)
            {
                can = Math.Min(can, inv.Count(def.IngredientItem(i)) / def.IngredientCountOf(i));
            }

            return can == int.MaxValue ? 0 : can;
        }

        /// <summary>
        /// E2-10: на сколько изготовлений хватает ингредиентов в сумке и в сундуках рядом (сводка сервера
        /// <paramref name="stock"/> для станка рецепта; null — только сумка).
        /// </summary>
        public static int Affordable(in Inventory inv, in RecipeDef def, ChestStock? stock)
        {
            int can = int.MaxValue;
            for (int i = 0; i < def.IngredientCount; i++)
            {
                byte item = def.IngredientItem(i);
                int have = inv.Count(item) + (stock?.Count(def.Station, item) ?? 0);
                can = Math.Min(can, have / def.IngredientCountOf(i));
            }

            return can == int.MaxValue ? 0 : can;
        }

        /// <summary>
        /// E2-10: откуда считаются сундуки рядом для рецептов станка <paramref name="station"/>: «руки» — позиция персонажа;
        /// станок — центр тайла ближайшего станка этого вида в радиусе крафта (равные — первый в обходе строками сверху вниз,
        /// слева направо). Станка в радиусе нет — false.
        /// </summary>
        public static bool SourceCenter(WorldModel world, WorldPos pos, CraftStation station, out WorldPos center)
        {
            center = pos;
            ushort want = RecipeTable.ObjectOf(station);
            if (want == ObjectId.Empty)
            {
                return true;
            }

            MapShape shape = world.Shape;
            TileCoord c0 = Torus.TileOf(shape, pos);
            long best = (long)RadiusUnits * RadiusUnits + 1;
            const int R = (RadiusUnits / Torus.SubTilesPerTile) + 1;
            for (int dy = -R; dy <= R; dy++)
            {
                for (int dx = -R; dx <= R; dx++)
                {
                    var t = new TileCoord(c0.X + dx, c0.Y + dy);
                    if (!shape.Wraps && ((uint)t.X >= (uint)shape.Width || (uint)t.Y >= (uint)shape.Height))
                    {
                        continue;
                    }

                    t = Torus.Wrap(shape, t);
                    ushort raw = world.GetObject(t);
                    if (raw == ObjectId.Empty || ObjectId.IsPart(raw) || ObjectId.IdOf(raw) != want)
                    {
                        continue;
                    }

                    WorldPos mid = BuildLayers.CenterAt(world, t); // E2-15: центр предмета (мебель со сдвигом на полтайла)
                    long d2 = Torus.Dist2(shape, pos, mid);
                    if (d2 < best)
                    {
                        best = d2;
                        center = mid;
                    }
                }
            }

            return best <= (long)RadiusUnits * RadiusUnits;
        }

        /// <summary>
        /// Станок рядом: <see cref="CraftReply.Accepted"/> / <see cref="CraftReply.StationFar"/> /
        /// <see cref="CraftReply.NoStation"/>; «руки» — всегда принято. Обход тайлов — строки сверху вниз, слева направо.
        /// </summary>
        public static CraftReply StationCheck(WorldModel world, WorldPos pos, CraftStation station)
        {
            ushort want = RecipeTable.ObjectOf(station);
            if (want == ObjectId.Empty)
            {
                return CraftReply.Accepted;
            }

            MapShape shape = world.Shape;
            TileCoord center = Torus.TileOf(shape, pos);
            long r2 = (long)RadiusUnits * RadiusUnits;
            bool seen = false;
            for (int dy = -SearchTiles; dy <= SearchTiles; dy++)
            {
                for (int dx = -SearchTiles; dx <= SearchTiles; dx++)
                {
                    var t = new TileCoord(center.X + dx, center.Y + dy);
                    if (!shape.Wraps && ((uint)t.X >= (uint)shape.Width || (uint)t.Y >= (uint)shape.Height))
                    {
                        continue;
                    }

                    t = Torus.Wrap(shape, t);
                    ushort raw = world.GetObject(t);
                    if (raw == ObjectId.Empty || ObjectId.IsPart(raw) || ObjectId.IdOf(raw) != want)
                    {
                        continue;
                    }

                    seen = true;
                    WorldPos mid = BuildLayers.CenterAt(world, t); // E2-15: центр предмета
                    if (Torus.Dist2(shape, pos, mid) <= r2)
                    {
                        return CraftReply.Accepted;
                    }
                }
            }

            return seen ? CraftReply.StationFar : CraftReply.NoStation;
        }
    }
}
