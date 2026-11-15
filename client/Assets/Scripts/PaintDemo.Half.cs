#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Client
{
    public static partial class PaintDemo
    {
        /// <summary>
        /// Комната E2-15 (<c>--ui-demo half</c>; записка <c>work/design/half_tile_all.md</c>): контур x ∈ [cx − 4; cx + 4],
        /// y ∈ [cy − 3; cy + 3] из каменных тонких стен — западная, северная, южная по внешним половинам, восточная — по
        /// ЗАПАДНОЙ половине тайла (снаружи остаётся свободная половина). Деревянный пол — под всеми стенами и до самых стен;
        /// в восточном столбце — только под стеной (полтайла пола у стены, восточная половина — трава). Сундук вплотную к
        /// западной стене со сдвигом на полтайла (якорь — соседний тайл), верстак — к северной стене со сдвигом вверх, факел у
        /// южной стены, кровать — без сдвига. Пишет базовые тайлы и записи построек (маски стен и пола, сдвиги).
        /// </summary>
        static void HalfHouse(WorldModel world, MapShape map, int cx, int cy)
        {
            int x0 = cx - 4, x1 = cx + 4, y0 = cy - 3, y1 = cy + 3;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    byte wall = 0;
                    wall |= y == y0 ? BuildCells.DoorNorth : (byte)0;
                    wall |= y == y1 ? BuildCells.DoorSouth : (byte)0;
                    wall |= x == x0 ? BuildCells.DoorWest : (byte)0;
                    wall |= x == x1 ? BuildCells.DoorWest : (byte)0; // восточная стена — западная половина тайла
                    byte floor = x == x1 ? BuildCells.DoorWest : BuildCells.Full; // у восточной стены — пол только под стеной
                    ushort obj = wall == 0 ? ObjectId.Empty : x == cx && y == y1 ? ObjectId.Door : ObjectId.StoneWall;
                    if (obj == ObjectId.Door)
                    {
                        wall = BuildCells.DoorSouth;
                    }

                    Layered(world, map, x, y, floor, obj, wall, 0, 0);
                }
            }

            Layered(world, map, x0 + 1, cy - 1, BuildCells.Full, ObjectId.Chest, 0, -1, 0);     // в восточной половине тайла западной стены
            Layered(world, map, cx, y0 + 1, BuildCells.Full, ObjectId.Workbench, 0, 0, -1);    // в южной половине тайла северной стены
            Layered(world, map, x0 + 2, y1 - 1, BuildCells.Full, ObjectId.Torch, 0, 0, 1);      // к южной стене
            Layered(world, map, x1 - 2, y0 + 1, BuildCells.Full, ObjectId.Bed, 0, 0, 0);
        }

        // Тайл с построенным деревянным полом по маске floorMask (0 — без пола) и верхом obj (ячейки стены или сдвиг мебели).
        static void Layered(WorldModel world, MapShape map, int x, int y, byte floorMask, ushort obj, byte cells, int dx, int dy)
        {
            TileCoord t = Torus.Wrap(map, new TileCoord(x, y));
            Chunk chunk = world.GetOrCreateChunk(Torus.ChunkOf(map, t));
            ushort index = (ushort)Chunk.IndexOf(t.X, t.Y);
            chunk.SetBaseTile(index, floorMask != 0 ? FloorId.WoodFloor : FloorId.Grass, obj);
            BuildRecord rec = BuildRecord.OfPlayer(0, FloorId.Grass).WithCells(cells).WithFloorMask(floorMask);
            chunk.SetBaseBuild(index, dx != 0 || dy != 0 ? rec.WithOffset(dx, dy) : rec);
        }
    }
}
