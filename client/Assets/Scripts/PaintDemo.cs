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
            painted += Forest(world, map, cx + ForestDx, cy + ForestDy);
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

        static int Set(WorldModel world, MapShape map, int x, int y, byte floor, ushort obj)
        {
            TileCoord t = Torus.Wrap(map, new TileCoord(x, y));
            Chunk? chunk = world.FindChunk(Torus.ChunkOf(map, t));
            if (chunk == null)
            {
                return 0;
            }

            chunk.SetLocalTile(new TileEdit((ushort)Chunk.IndexOf(t.X, t.Y), floor, obj));
            return 1;
        }

        /// <summary>Центр поляны демо-леса от старта, тайлов (E1-7).</summary>
        public const int ForestDx = 8, ForestDy = 52;

        /// <summary>Доля тайлов под деревьями в глубине демо-леса и леса стенда (план 1.3: 35–55 %).</summary>
        public const float ForestDensity = 0.45f;

        // E1-7: плотный лес (пятно ~27 тайлов) с поляной, костром и факелом, тропой в 2 тайла на север к старту;
        // опушка редеет; на лугу вокруг — редкие деревья, кусты, валуны, медь, пни.
        static int Forest(WorldModel world, MapShape map, int x0, int y0)
        {
            const float radius = 27f;
            float p1 = GroundAtlas.Hash(1, 0, 21) * 6.28f, p2 = GroundAtlas.Hash(2, 0, 21) * 6.28f;
            int n = 0;
            for (int dy = -44; dy <= 44; dy++)
            {
                for (int dx = -50; dx <= 50; dx++)
                {
                    int x = x0 + dx, y = y0 + dy;
                    float a = Mathf.Atan2(dy, dx);
                    float edge = radius * (1f + 0.18f * Mathf.Sin(3f * a + p1) + 0.1f * Mathf.Sin(7f * a + p2));
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float inside = edge - r; // > 0 — в лесу, тайлов до опушки
                    float path = Mathf.Abs(dx - 2.5f * Mathf.Sin(dy * 0.22f)); // тропа от поляны на север
                    bool onPath = dy < 0 && path < 1.2f;
                    bool glade = r < 5.2f + 1.2f * Mathf.Sin(4f * a);
                    float h = GroundAtlas.Hash(x, y, 77);
                    float h2 = GroundAtlas.Hash(x, y, 78);
                    byte floor;
                    ushort obj = ObjectId.Empty;
                    if (inside > 0f)
                    {
                        floor = onPath ? FloorId.Dirt : glade ? FloorId.Grass : FloorId.ForestFloor;
                        if (!onPath && !glade)
                        {
                            float density = ForestDensity * Mathf.Clamp01(0.35f + inside / 5f); // опушка редеет
                            obj = h < density ? ObjectId.Tree : h2 < 0.05f ? ObjectId.Bush : h2 > 0.985f ? ObjectId.Rock : ObjectId.Empty;
                        }
                        else if (glade && !onPath && r > 3.4f)
                        {
                            obj = h2 < 0.10f ? ObjectId.Stump : h2 > 0.94f ? ObjectId.Bush : ObjectId.Empty;
                        }
                    }
                    else if (inside > -14f)
                    {
                        // Луг у опушки: редкие деревья (оливы, кипарисы), кусты, валуны, медь.
                        if (onPath && inside > -6f)
                        {
                            n += Set(world, map, x, y, FloorId.Dirt, ObjectId.Empty);
                            continue;
                        }

                        floor = FloorId.Grass;
                        float fall = Mathf.Clamp01(1f + inside / 14f);
                        obj = h < 0.07f * fall ? ObjectId.Tree
                            : h2 < 0.03f ? ObjectId.Bush
                            : h2 > 0.985f ? ObjectId.Rock
                            : h2 > 0.975f ? ObjectId.Granite
                            : h2 > 0.968f ? ObjectId.Ore
                            : ObjectId.Empty;
                    }
                    else
                    {
                        continue;
                    }

                    n += Set(world, map, x, y, floor, obj);
                }
            }

            n += Light(world, map, x0, y0, ObjectId.Campfire);
            n += Light(world, map, x0 + 3, y0 - 2, ObjectId.Torch);
            n += Light(world, map, x0 - 9, y0 - 22, ObjectId.Torch); // у опушки
            Debug.Log($"[paint-demo] лес у {x0},{y0}: костёр на поляне, факелы у {x0 + 3},{y0 - 2} и {x0 - 9},{y0 - 22}");
            return n;
        }

        /// <summary>
        /// Ключ <c>--paint-forest</c> (E1-7), только для замера <c>--tile-bench</c>: плотный лес (<see cref="ForestDensity"/>)
        /// полосами ±70 тайлов вдоль средней строки и среднего столбца мира (там бегает стенд), сами строка и столбец
        /// (по 2 тайла) — свободная тропа.
        /// </summary>
        public static void ApplyBenchForest(WorldModel world, MapShape map)
        {
            int cx = map.Width / 2, cy = map.Height / 2, n = 0, trees = 0;
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    int ax = Mathf.Abs(x - cx), ay = Mathf.Abs(y - cy);
                    if (ax > 70 && ay > 70)
                    {
                        continue;
                    }

                    bool lane = x == cx || x == cx - 1 || y == cy || y == cy - 1;
                    bool tree = !lane && GroundAtlas.Hash(x, y, 77) < ForestDensity;
                    n += Set(world, map, x, y, lane ? FloorId.Dirt : FloorId.ForestFloor, tree ? ObjectId.Tree : ObjectId.Empty);
                    trees += tree ? 1 : 0;
                }
            }

            Debug.Log($"[paint-forest] лес для замера: тайлов {n}, деревьев {trees} ({100f * trees / Mathf.Max(n, 1):0} %)");
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
