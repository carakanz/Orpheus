#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Client
{
    public static partial class PaintDemo
    {
        /// <summary>
        /// C-030, только для снимков без сервера (<c>--bed-demo</c>): спальня из тонких каменных стен с деревянным полом вокруг
        /// персонажа (cx, cy): двуспальная кровать 2 × 2 с поворотом <paramref name="rot"/> к северу от персонажа (якорь
        /// (cx − 1, cy − 3), персонаж — у её южного края), сундук и верстак вдоль северной стены, факел. Кровать — для позы
        /// «лежит» (<see cref="BedDemoAnchor"/>).
        /// </summary>
        public static void Bed(WorldModel world, MapShape map, int cx, int cy, int rot)
        {
            int x0 = cx - 6, x1 = cx + 6, y0 = cy - 5, y1 = cy + 2;
            Rect(world, map, x0 - 3, y0 - 3, x1 - x0 + 7, y1 - y0 + 7, FloorId.Grass);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    byte m = 0;
                    m |= y == y0 ? BuildCells.DoorNorth : (byte)0;
                    m |= y == y1 ? BuildCells.DoorSouth : (byte)0;
                    m |= x == x0 ? BuildCells.DoorWest : (byte)0;
                    m |= x == x1 ? BuildCells.DoorEast : (byte)0;
                    Layered(world, map, x, y, BuildCells.Full, ObjectId.Empty, 0, 0, 0);
                    SetFloor(world, map, x, y, FloorId.WoodFloor);
                    if (m != 0)
                    {
                        Cells(world, map, x, y, ObjectId.StoneWall, m, false);
                    }
                }
            }

            TileCoord bed = BedDemoAnchor(cx, cy);
            Turned(world, map, bed.X, bed.Y, ObjectId.Bed, rot);
            Turned(world, map, cx - 5, cy - 4, ObjectId.Chest, 0);
            Turned(world, map, cx + 2, cy - 4, ObjectId.Workbench, 0);
            Light(world, map, cx + 5, cy + 1, ObjectId.Torch);
            Turned(world, map, cx - 4, cy - 4, ObjectId.Chest, 1);
        }

        /// <summary>Якорь кровати сцены <c>--bed-demo</c>: к северу от персонажа.</summary>
        public static TileCoord BedDemoAnchor(int cx, int cy) => new TileCoord(cx - 1, cy - 3);

        static void SetFloor(WorldModel world, MapShape map, int x, int y, byte floor)
        {
            TileCoord t = Torus.Wrap(map, new TileCoord(x, y));
            Chunk chunk = world.GetOrCreateChunk(Torus.ChunkOf(map, t));
            ushort index = (ushort)Chunk.IndexOf(t.X, t.Y);
            chunk.SetBaseTile(index, floor, chunk.GetObject(index));
        }
    }
}
