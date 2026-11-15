#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Client
{
    public static partial class PaintDemo
    {
        /// <summary>
        /// Сцена поворота E2-16 (<c>--ui-demo turn</c>): деревянный пол x ∈ [cx − 7; cx + 7], y ∈ [cy − 5; cy + 4]; кровати 1 × 2
        /// в четырёх поворотах (слева: 0° и 180° стоя, 90° и 270° лёжа поперёк), верстаки 2 × 2 — 0° и 90° (сверху справа),
        /// сундуки 0°…270° в ряд, вертикальная тонкая стена по x = cx + 6 (западные половины) с дверью в проёме — дверь
        /// вертикальная. Призрак кровати «из меню» ставит <see cref="UI.UiDemo"/>. Записи — с флагом Wide и поворотом в объекте.
        /// </summary>
        static void TurnRoom(WorldModel world, MapShape map, int cx, int cy)
        {
            for (int y = cy - 5; y <= cy + 4; y++)
            {
                for (int x = cx - 7; x <= cx + 7; x++)
                {
                    Layered(world, map, x, y, BuildCells.Full, ObjectId.Empty, 0, 0, 0);
                }
            }

            Turned(world, map, cx - 6, cy - 4, ObjectId.Bed, 0);
            Turned(world, map, cx - 4, cy - 4, ObjectId.Bed, 2);
            Turned(world, map, cx - 6, cy - 1, ObjectId.Bed, 1);
            Turned(world, map, cx - 6, cy + 1, ObjectId.Bed, 3);
            Turned(world, map, cx + 1, cy - 4, ObjectId.Workbench, 0);
            Turned(world, map, cx + 4, cy - 4, ObjectId.Workbench, 1);
            for (int r = 0; r < 4; r++)
            {
                Turned(world, map, cx + 1 + r, cy, ObjectId.Chest, r);
            }

            for (int y = cy - 3; y <= cy + 3; y++)
            {
                bool door = y == cy + 2;
                Layered(world, map, cx + 6, y, BuildCells.Full, door ? ObjectId.Door : ObjectId.StoneWall, BuildCells.DoorWest, 0, 0);
            }
        }

        // Мебель с поворотом: объект — id + поворот (биты 10–11), запись — флаг Wide у многоклеточной.
        static void Turned(WorldModel world, MapShape map, int x, int y, ushort id, int rot)
        {
            ushort obj = BuildLayers.Turned(id, rot);
            Layered(world, map, x, y, BuildCells.Full, obj, 0, 0, 0);
            if (BuildLayers.IsWide(obj))
            {
                TileCoord t = Torus.Wrap(map, new TileCoord(x, y));
                Chunk chunk = world.GetOrCreateChunk(Torus.ChunkOf(map, t));
                ushort index = (ushort)Chunk.IndexOf(t.X, t.Y);
                chunk.TryGetBuild(index, out BuildRecord rec);
                chunk.SetBaseBuild(index, rec.WithFlags(BuildFlags.Wide));
            }
        }
    }
}
