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
                    WorldPos c = Torus.CornerOf(t);
                    var mid = new WorldPos(c.X + Torus.SubTilesPerTile / 2, c.Y + Torus.SubTilesPerTile / 2);
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
