using System;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>Числа приёмки мира на тайлах (E1-2): считают тесты на 30 зёрнах и <c>tools/gen_preview</c> — одним кодом.</summary>
    public sealed class TileWorldStats
    {
        /// <summary>Радиус у Очага без воды и обрывов (план 1.2).</summary>
        public const int StartRadius = 150;

        public long Tiles;
        public long Water;
        public long Sand;
        public long Cliff;

        /// <summary>Тайлов обрыва, у которых среди 8 соседей не больше 4 обрывов (линия, а не масса).</summary>
        public long CliffThin;

        /// <summary>Тайлы биома без воды: луг, лес, горы, пещеры — и объекты на них.</summary>
        public long MeadowLand;
        public long MeadowTrees;
        public long MeadowDry;
        public long ForestLand;
        public long ForestTrees;
        public long MountainLand;
        public long MountainBoulders;
        public long CaveLand;
        public long CaveMass;

        /// <summary>Воды и обрывов в <see cref="StartRadius"/> от Очага (должно быть 0).</summary>
        public long WaterNearStart;
        public long CliffNearStart;

        /// <summary>Доля проходимых тайлов, достижимых от Очага без копания.</summary>
        public double Reach;

        public double WaterPercent => 100.0 * Water / Tiles;

        public double CliffPercent => 100.0 * Cliff / Tiles;

        public double CliffThinPercent => Cliff == 0 ? 100.0 : 100.0 * CliffThin / Cliff;

        public double ForestTreePercent => ForestLand == 0 ? 0 : 100.0 * ForestTrees / ForestLand;

        public double MeadowTreePercent => MeadowLand == 0 ? 0 : 100.0 * MeadowTrees / MeadowLand;

        public double MeadowDryPercent => MeadowLand == 0 ? 0 : 100.0 * MeadowDry / MeadowLand;

        public double MountainBoulderPercent => MountainLand == 0 ? 0 : 100.0 * MountainBoulders / MountainLand;

        public double CaveMassPercent => CaveLand == 0 ? 0 : 100.0 * CaveMass / CaveLand;

        public static TileWorldStats Of(TileWorld t)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            int half = w >> 1;
            var s = new TileWorldStats { Tiles = (long)w * w, Reach = ConnectStage.ReachableShare(t) };
            long r2 = (long)StartRadius * StartRadius;
            for (int y = 0; y < w; y++)
            {
                long dy = IntMath.Wrap(y - half, w);
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w) + x;
                    long dx = IntMath.Wrap(x - half, w);
                    bool near = (dx * dx) + (dy * dy) <= r2;
                    byte floor = t.Floor[i];
                    ushort obj = t.Objects[i];
                    if (floor == FloorId.Water)
                    {
                        s.Water++;
                        s.WaterNearStart += near ? 1 : 0;
                        continue;
                    }

                    s.Sand += floor == FloorId.Sand ? 1 : 0;
                    if (obj == ObjectId.Cliff)
                    {
                        s.Cliff++;
                        s.CliffNearStart += near ? 1 : 0;
                        int around = 0;
                        for (int k = 0; k < 9; k++)
                        {
                            if (k != 4 && t.Objects[(((y + (k / 3) - 1) & mask) * w) + ((x + (k % 3) - 1) & mask)] == ObjectId.Cliff)
                            {
                                around++;
                            }
                        }

                        s.CliffThin += around <= 4 ? 1 : 0;
                    }

                    bool boulder = obj == ObjectId.Rock || obj == ObjectId.Granite;
                    switch (t.Biome[i])
                    {
                        case Biome.Meadow:
                            s.MeadowLand++;
                            s.MeadowTrees += obj == ObjectId.Tree ? 1 : 0;
                            s.MeadowDry += floor == FloorId.DryGrass ? 1 : 0;
                            break;
                        case Biome.Forest:
                            s.ForestLand++;
                            s.ForestTrees += obj == ObjectId.Tree ? 1 : 0;
                            break;
                        case Biome.Mountain:
                            s.MountainLand++;
                            s.MountainBoulders += boulder ? 1 : 0;
                            break;
                        case Biome.Cave:
                            s.CaveLand++;
                            s.CaveMass += boulder ? 1 : 0;
                            break;
                    }
                }
            }

            return s;
        }
    }

    /// <summary>
    /// Форма обрывов (карточка E1-6b): гряды — связные куски (8 соседей), их длина, толщина полосы, прямые отрезки края,
    /// близкие параллельные линии. Числа приёмки шага 1.6б; считает <c>ReliefTests</c> и <c>gen_preview --map</c>.
    /// </summary>
    public sealed class CliffShapeStats
    {
        /// <summary>Тайлов обрыва.</summary>
        public long Tiles;

        /// <summary>Гряд (связных кусков между проходами).</summary>
        public int Pieces;

        /// <summary>Длина самой длинной и самой короткой гряды, шагов обхода от конца до конца.</summary>
        public int Longest;

        public int Shortest;

        /// <summary>Наибольшая «глубина» тайла обрыва: шагов (8 соседей) до ближайшего не-обрыва. 2 — полоса в 3–4 тайла.</summary>
        public int MaxDepth;

        /// <summary>Тайлов полосы толщиной в один тайл (с двух противоположных сторон не обрыв).</summary>
        public long Thin;

        /// <summary>Самый длинный прямой отрезок края (подряд идущие тайлы обрыва одной строки или столбца с не-обрывом с одной и той же стороны).</summary>
        public int LongestStraight;

        /// <summary>Тайлов края в прямых отрезках длиннее 8 и всего тайлов края (с повторами по сторонам).</summary>
        public long StraightOver8;

        public long EdgeTiles;

        /// <summary>
        /// Тайлов «коридора»: не обрыв, а по оси с двух противоположных сторон обрыв в 5–8 тайлах (две линии идут рядом,
        /// между ними 10–16 тайлов). Проход в гряде уже (до 8 тайлов по оси) и сюда не попадает.
        /// </summary>
        public long NearParallel;

        public double ThinPercent => Tiles == 0 ? 0 : 100.0 * Thin / Tiles;

        public double StraightOver8Percent => EdgeTiles == 0 ? 0 : 100.0 * StraightOver8 / EdgeTiles;

        public double NearParallelPercent => Tiles == 0 ? 0 : 100.0 * NearParallel / Tiles;

        public static CliffShapeStats Of(TileWorld t)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            int n = w * w;
            var s = new CliffShapeStats { Shortest = int.MaxValue };
            var cliff = new bool[n];
            for (int i = 0; i < n; i++)
            {
                cliff[i] = t.Objects[i] == ObjectId.Cliff;
                s.Tiles += cliff[i] ? 1 : 0;
            }

            // Гряды и их длина: обход от первого тайла, затем от самого дальнего.
            var stamp = new int[n];
            var depth = new int[n];
            var queue = new int[n];
            int mark = 0;
            for (int start = 0; start < n; start++)
            {
                if (!cliff[start] || stamp[start] != 0)
                {
                    continue;
                }

                int qt = Bfs(cliff, w, start, depth, queue, stamp, ++mark);
                qt = Bfs(cliff, w, queue[qt - 1], depth, queue, stamp, ++mark);
                int length = depth[queue[qt - 1]] + 1;
                s.Pieces++;
                s.Longest = Math.Max(s.Longest, length);
                s.Shortest = Math.Min(s.Shortest, length);
            }

            if (s.Pieces == 0)
            {
                s.Shortest = 0;
            }

            // Глубина: слои от края внутрь.
            var layer = new int[n];
            bool any = true;
            for (int d = 1; any; d++)
            {
                any = false;
                for (int i = 0; i < n; i++)
                {
                    if (!cliff[i] || layer[i] != 0)
                    {
                        continue;
                    }

                    int x = i & mask;
                    int y = i / w;
                    bool edge = false;
                    for (int k = 0; k < 9 && !edge; k++)
                    {
                        int v = (((y + (k / 3) - 1) & mask) * w) + ((x + (k % 3) - 1) & mask);
                        edge = !cliff[v] || (layer[v] != 0 && layer[v] < d);
                    }

                    if (edge)
                    {
                        layer[i] = d;
                        s.MaxDepth = d;
                        any = true;
                    }
                }
            }

            int[] ox = { 0, 0, -1, 1 }; // сторона, с которой не обрыв: север, юг, запад, восток
            int[] oy = { -1, 1, 0, 0 };
            for (int y = 0; y < w; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w) + x;
                    if (!cliff[i])
                    {
                        int e = Gap(cliff, w, x, y, 1, 0);
                        int wst = Gap(cliff, w, x, y, -1, 0);
                        int so = Gap(cliff, w, x, y, 0, 1);
                        int no = Gap(cliff, w, x, y, 0, -1);
                        s.NearParallel += (e >= 5 && wst >= 5) || (so >= 5 && no >= 5) ? 1 : 0;
                        continue;
                    }

                    bool north = cliff[(((y - 1) & mask) * w) + x];
                    bool south = cliff[(((y + 1) & mask) * w) + x];
                    bool west = cliff[(y * w) + ((x - 1) & mask)];
                    bool east = cliff[(y * w) + ((x + 1) & mask)];
                    s.Thin += (!north && !south) || (!west && !east) ? 1 : 0;

                    for (int k = 0; k < 4; k++)
                    {
                        if (cliff[(((y + oy[k]) & mask) * w) + ((x + ox[k]) & mask)])
                        {
                            continue; // с этой стороны обрыв — не край
                        }

                        // Прямой отрезок края: идём вдоль (поперёк стороны k), пока тот же край; считаем от его начала.
                        int ax = k < 2 ? 1 : 0;
                        int ay = k < 2 ? 0 : 1;
                        s.EdgeTiles++;
                        if (!IsEdge(cliff, w, x - ax, y - ay, ox[k], oy[k]))
                        {
                            int run = 1;
                            while (run < w && IsEdge(cliff, w, x + (ax * run), y + (ay * run), ox[k], oy[k]))
                            {
                                run++;
                            }

                            s.LongestStraight = Math.Max(s.LongestStraight, run);
                            s.StraightOver8 += run > 8 ? run : 0;
                        }
                    }
                }
            }

            return s;
        }

        // Расстояние до обрыва по лучу, 1…8; 0 — в 8 тайлах обрыва нет.
        private static int Gap(bool[] cliff, int w, int x, int y, int dx, int dy)
        {
            int mask = w - 1;
            for (int d = 1; d <= 8; d++)
            {
                if (cliff[(((y + (dy * d)) & mask) * w) + ((x + (dx * d)) & mask)])
                {
                    return d;
                }
            }

            return 0;
        }

        private static bool IsEdge(bool[] cliff, int w, int x, int y, int ox, int oy)
        {
            int mask = w - 1;
            return cliff[((y & mask) * w) + (x & mask)] && !cliff[(((y + oy) & mask) * w) + ((x + ox) & mask)];
        }

        private static int Bfs(bool[] cliff, int w, int start, int[] depth, int[] queue, int[] stamp, int mark)
        {
            int mask = w - 1;
            int qt = 0;
            queue[qt++] = start;
            stamp[start] = mark;
            depth[start] = 0;
            for (int qh = 0; qh < qt; qh++)
            {
                int u = queue[qh];
                int ux = u & mask;
                int uy = u / w;
                for (int k = 0; k < 9; k++)
                {
                    int v = (((uy + (k / 3) - 1) & mask) * w) + ((ux + (k % 3) - 1) & mask);
                    if (cliff[v] && stamp[v] != mark)
                    {
                        stamp[v] = mark;
                        depth[v] = depth[u] + 1;
                        queue[qt++] = v;
                    }
                }
            }

            return qt;
        }
    }
}
