#nullable enable
using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Ключ <c>--paint-demo</c> (E1-4), только для снимков: офлайн после генерации перекрашивает полы у точки старта в
    /// тестовый узор, пока генератор не даёт воду и виды земли (шаги 1.2–1.3). Правила игры не трогает: меняются только
    /// номера полов в памяти клиента.
    /// <list type="bullet">
    /// <item>У старта (сцена «озеро»): озеро неправильной формы с песчаным берегом к западу, лужа рядом, пятна голой
    /// земли, сухой травы, лесной подстилки и каменистой земли, тропинка в один тайл, две площадки построенного пола,
    /// факелы вдоль тропинки и костёр на берегу (E1-8: источники света для тона суток).</item>
    /// <item>В <see cref="JunctionDx"/> тайлах к востоку (сцена «стык»): шесть видов земли клиньями вокруг пруда.</item>
    /// </list>
    /// </summary>
    public static class PaintDemo
    {
        /// <summary>Центр озера от старта, тайлов.</summary>
        public const int LakeDx = -13, LakeDy = -2;

        /// <summary>Центр стыка всех видов земли от старта, тайлов.</summary>
        public const int JunctionDx = 70, JunctionDy = 0;

        public static void Apply(WorldModel world, MapShape map)
        {
            int cx = map.Width / 2, cy = map.Height / 2;
            int painted = 0;

            // Пятна земли: центр, радиус, пол. Позже в списке — поверх.
            painted += Blob(world, map, cx + 25, cy - 5, 6.5f, FloorId.Stone, 1, keepObjects: true);
            painted += Blob(world, map, cx + 9, cy - 9, 5f, FloorId.Dirt, 2, keepObjects: true);
            painted += Blob(world, map, cx + 17, cy + 8, 7f, GroundTable.DryGrassFloor, 3, keepObjects: true);
            painted += Blob(world, map, cx - 3, cy + 12, 6f, FloorId.ForestFloor, 4, keepObjects: true);

            // Тропинка шириной в тайл: змейка на восток.
            for (int x = cx - 1; x <= cx + 30; x++)
            {
                int y = cy + 3 + Mathf.RoundToInt(1.6f * Mathf.Sin(x * 0.45f));
                painted += Set(world, map, x, y, FloorId.Dirt, keepObjects: false);
            }

            // Озеро: песок шире воды на 1,5–3 тайла, потом вода.
            painted += Blob(world, map, cx + LakeDx, cy + LakeDy, 11f, FloorId.Sand, 5, keepObjects: false);
            painted += Blob(world, map, cx + LakeDx, cy + LakeDy, 8.6f, FloorId.Water, 5, keepObjects: false);
            // Лужа без песка — вся отмель.
            painted += Blob(world, map, cx + 5, cy - 3, 2.6f, FloorId.Water, 6, keepObjects: false);

            // Построенные полы (плиткой поверх земли).
            painted += Rect(world, map, cx + 11, cy + 13, 4, 3, FloorId.StoneFloor);
            painted += Rect(world, map, cx - 8, cy + 10, 3, 2, FloorId.WoodFloor);

            // Источники света (E1-8, тон суток): факелы вдоль тропинки, костёр на восточном берегу озера
            // (песок между водой и сушей). Пол сохраняется, объект — как содержимое генерации (ревизия 0).
            for (int x = cx + 3; x <= cx + 27; x += 8)
            {
                int y = cy + 3 + Mathf.RoundToInt(1.6f * Mathf.Sin(x * 0.45f));
                painted += Light(world, map, x, y, ObjectId.Torch);
            }

            painted += Light(world, map, cx + LakeDx + 10, cy + LakeDy, ObjectId.Campfire);

            // Стык: клинья шести видов земли и пруд в середине.
            byte[] kinds = { FloorId.Grass, GroundTable.DryGrassFloor, FloorId.Dirt, FloorId.ForestFloor, FloorId.Sand, FloorId.Stone };
            int jx = cx + JunctionDx, jy = cy + JunctionDy;
            for (int dy = -16; dy <= 16; dy++)
            {
                for (int dx = -16; dx <= 16; dx++)
                {
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    if (r > 15.5f)
                    {
                        continue;
                    }

                    float turn = Mathf.Atan2(dy, dx) / (2f * Mathf.PI) + 0.5f + r * 0.012f; // лёгкая закрутка клиньев
                    int k = (int)(turn * kinds.Length) % kinds.Length;
                    painted += Set(world, map, jx + dx, jy + dy, kinds[k], keepObjects: false);
                }
            }

            painted += Blob(world, map, jx, jy, 3.2f, FloorId.Water, 7, keepObjects: false);
            Debug.Log($"[paint-demo] перекрашено тайлов: {painted}; озеро у {cx + LakeDx},{cy + LakeDy}, стык у {jx},{jy}");
        }

        // Пятно неправильной формы: радиус гуляет по углу тремя волнами (фазы — из зерна).
        static int Blob(WorldModel world, MapShape map, int x0, int y0, float radius, byte floor, uint seed, bool keepObjects)
        {
            float p1 = GroundAtlas.Hash(1, 0, seed) * 6.28f, p2 = GroundAtlas.Hash(2, 0, seed) * 6.28f, p3 = GroundAtlas.Hash(3, 0, seed) * 6.28f;
            int reach = Mathf.CeilToInt(radius * 1.5f);
            int n = 0;
            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    float a = Mathf.Atan2(dy, dx);
                    float r = radius * (1f + 0.22f * Mathf.Sin(3f * a + p1) + 0.12f * Mathf.Sin(5f * a + p2) + 0.07f * Mathf.Sin(9f * a + p3));
                    if (dx * dx + dy * dy <= r * r)
                    {
                        n += Set(world, map, x0 + dx, y0 + dy, floor, keepObjects);
                    }
                }
            }

            return n;
        }

        static int Rect(WorldModel world, MapShape map, int x0, int y0, int w, int h, byte floor)
        {
            int n = 0;
            for (int y = y0; y < y0 + h; y++)
            {
                for (int x = x0; x < x0 + w; x++)
                {
                    n += Set(world, map, x, y, floor, keepObjects: false);
                }
            }

            return n;
        }

        static int Set(WorldModel world, MapShape map, int x, int y, byte floor, bool keepObjects)
        {
            TileCoord t = Torus.Wrap(map, new TileCoord(x, y));
            Chunk? chunk = world.FindChunk(Torus.ChunkOf(map, t));
            if (chunk == null)
            {
                return 0;
            }

            ushort index = (ushort)Chunk.IndexOf(t.X, t.Y);
            ushort obj = keepObjects ? chunk.GetObject(index) : ObjectId.Empty;
            chunk.SetLocalTile(new TileEdit(index, floor, obj));
            return 1;
        }

        /// <summary>Поставить источник света (факел/костёр) на существующий пол (E1-8).</summary>
        static int Light(WorldModel world, MapShape map, int x, int y, ushort obj)
        {
            TileCoord t = Torus.Wrap(map, new TileCoord(x, y));
            Chunk chunk = world.GetOrCreateChunk(Torus.ChunkOf(map, t));
            ushort index = (ushort)Chunk.IndexOf(t.X, t.Y);
            chunk.SetBaseTile(index, chunk.GetFloor(index), obj);
            return 1;
        }
    }
}
