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
