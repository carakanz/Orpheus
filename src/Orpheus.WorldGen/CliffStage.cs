using System;
using Orpheus.Core.Rng;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Тропы (карточка E1-2, план 1.3): линии проходов шириной 2–3 тайла через весь мир — линии уровня ½ двух независимых
    /// гладких полей (ячейки 128, 64). Ширина постоянная: тайл на тропе, если расстояние до линии |v − ½| / |∇v| ≤ 1,3.
    /// Тропы вычитаются из леса и обрывов (там — проход), пол на них — земля.
    /// </summary>
    public static class TrailStage
    {
        public const int Stage = 16;

        /// <summary>Сколько полей троп (линии разных полей пересекаются — получается сеть).</summary>
        public const int Fields = 2;

        public static readonly int[] Cells = { 256, 128 };

        /// <summary>Полуширина тропы, сотые тайла.</summary>
        public const int HalfWidth100 = 130;

        /// <summary>Пометить тропы в <see cref="TileWorld.Marks"/> (<see cref="TileWorld.MarkTrail"/>).</summary>
        public static void Run(TileWorld t)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            ulong seed = t.Macro.Map.Seed;
            for (int f = 0; f < Fields; f++)
            {
                ushort[] v = GridField.Upsample(new FieldNoise(MacroGenerator.FieldSeed(seed, Stage, f + 1), w, Cells, ridged: false), w);
                for (int y = 0; y < w; y++)
                {
                    int row = y * w;
                    int up = ((y - 1) & mask) * w;
                    int down = ((y + 1) & mask) * w;
                    for (int x = 0; x < w; x++)
                    {
                        long d = v[row + x] - (LatticeNoise.One >> 1);
                        long gx = v[row + ((x + 1) & mask)] - v[row + ((x - 1) & mask)]; // 2 × градиент
                        long gy = v[down + x] - v[up + x];
                        // |d| ≤ hw · |∇v|, |∇v| = √(gx² + gy²) / 2  ⇔  4·d²·100² ≤ hw100² · (gx² + gy²)
                        if (4 * d * d * 10000 <= (long)HalfWidth100 * HalfWidth100 * ((gx * gx) + (gy * gy)))
                        {
                            t.Marks[row + x] |= TileWorld.MarkTrail;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Обрывы-границы (карточка E1-2, план 1.2; образец — Factorio): непроходимые и неломаемые линии там, где рельеф
    /// (<see cref="ReliefMap"/>) переходит с уровня на уровень.
    /// <para>
    /// Сетка ячеек 4 × 4 тайла; углы ячейки — узлы решётки рельефа. Ребро ячейки «пересечено», если уровни двух его углов
    /// разные; ячейка с пересечёнными рёбрами получает кусок обрыва: узел в тайле (1, 1) ячейки и «рукава» толщиной в один
    /// тайл к каждому пересечённому ребру (влево — (0, 1), вправо — (2, 1), (3, 1), вверх — (1, 0), вниз — (1, 2), (1, 3)).
    /// Соседние ячейки стыкуются на общем ребре, поэтому линия связна по 4 сторонам и не пропускает даже по диагонали.
    /// </para>
    /// <para>
    /// Кусок не ставится: в <see cref="WaterStage.NoWaterRadius"/> от Очага; в ячейку с водой или берегом; на тропу; где шум
    /// «обрывистости» (ячейки 128, 64) в нижних <see cref="SkipPerMille"/> ‰ — это длинные разрывы. Затем по каждой связной
    /// линии идёт обход в ширину от её первой ячейки, и каждая <see cref="GapEvery"/>-я ячейка убирается — проход шириной 4
    /// тайла не реже чем через 44 тайла линии. Обрывки короче <see cref="MinRunCells"/> ячеек убираются.
    /// Ячейки-проходы помечаются <see cref="TileWorld.MarkClear"/>: этап 11 не ставит туда объекты и кладёт землю.
    /// </para>
    /// </summary>
    public static class CliffStage
    {
        public const int Stage = 17;

        public const int FieldCliffiness = 1;

        public static readonly int[] CellsCliffiness = { 128, 64 };

        /// <summary>Доля ячеек линии, убираемая шумом обрывистости, ‰.</summary>
        public const int SkipPerMille = 100;

        /// <summary>Период проходов вдоль линии, ячеек (12 ячеек = 44 тайла обрыва + проход 4).</summary>
        public const int GapEvery = 12;

        /// <summary>Линия короче — не обрыв, а мусор: убирается (до нарезки проходов).</summary>
        public const int MinLineCells = 8;

        /// <summary>Обрывок после нарезки короче — убирается.</summary>
        public const int MinRunCells = 3;

        private const byte EdgeLeft = 1;
        private const byte EdgeRight = 2;
        private const byte EdgeUp = 4;
        private const byte EdgeDown = 8;

        /// <summary>Поставить обрывы (после воды и троп, до объектов этапа 11).</summary>
        public static void Run(TileWorld t)
        {
            int w = t.WorldSize;
            int n = w >> GridField.StepBits;
            int cmask = n - 1;
            int half = w >> 1;
            ulong seed = t.Macro.Map.Seed;
            ReliefMap relief = t.Relief;

            // Пересечённые рёбра ячеек.
            var edges = new byte[n * n];
            var keep = new bool[n * n];
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int a = relief.NodeLevel(i, j);
                    int b = relief.NodeLevel(i + 1, j);
                    int c = relief.NodeLevel(i, j + 1);
                    int d = relief.NodeLevel(i + 1, j + 1);
                    byte e = 0;
                    if (a != c) e |= EdgeLeft;
                    if (b != d) e |= EdgeRight;
                    if (a != b) e |= EdgeUp;
                    if (c != d) e |= EdgeDown;
                    edges[(j * n) + i] = e;
                    keep[(j * n) + i] = e != 0;
                }
            }

            // Обрывистость: нижние SkipPerMille ‰ ячеек линии — разрыв.
            var cliffiness = new FieldNoise(MacroGenerator.FieldSeed(seed, Stage, FieldCliffiness), w, CellsCliffiness, ridged: false);
            var cv = new ushort[n * n];
            var hist = new int[LatticeNoise.One];
            long lineCells = 0;
            for (int c = 0; c < keep.Length; c++)
            {
                if (keep[c])
                {
                    int v = cliffiness.Sample(((c & cmask) << GridField.StepBits) + 2, ((c / n) << GridField.StepBits) + 2);
                    cv[c] = (ushort)v;
                    hist[v]++;
                    lineCells++;
                }
            }

            int skipBelow = GridField.QuantileBelow(hist, lineCells * SkipPerMille / 1000);
            long r = WaterStage.NoWaterRadius + GridField.Step;
            for (int c = 0; c < keep.Length; c++)
            {
                if (!keep[c])
                {
                    continue;
                }

                int ci = c & cmask;
                int cj = c / n;
                long dx = IntMath.Wrap((ci << GridField.StepBits) + 2 - half, w);
                long dy = IntMath.Wrap((cj << GridField.StepBits) + 2 - half, w);
                if (cv[c] < skipBelow || (dx * dx) + (dy * dy) <= r * r || CellBusy(t, ci, cj))
                {
                    keep[c] = false;
                }
            }

            // Короткие линии — прочь; длинные — нарезать проходами; обрывки — прочь.
            var level = new int[n * n];
            var queue = new int[n * n];
            DropShort(keep, edges, n, MinLineCells, level, queue, cutSeed: MacroGenerator.StageSeed(seed, Stage), t);
            DropShort(keep, edges, n, MinRunCells, level, queue, cutSeed: 0, null);

            // Тайлы.
            int mask = w - 1;
            for (int c = 0; c < keep.Length; c++)
            {
                if (!keep[c])
                {
                    continue;
                }

                int x0 = (c & cmask) << GridField.StepBits;
                int y0 = (c / n) << GridField.StepBits;
                byte e = edges[c];
                Put(t, x0 + 1, y0 + 1);
                if ((e & EdgeLeft) != 0)
                {
                    Put(t, x0, y0 + 1);
                }

                if ((e & EdgeRight) != 0)
                {
                    Put(t, x0 + 2, y0 + 1);
                    Put(t, x0 + 3, y0 + 1);
                }

                if ((e & EdgeUp) != 0)
                {
                    Put(t, x0 + 1, y0);
                }

                if ((e & EdgeDown) != 0)
                {
                    Put(t, x0 + 1, y0 + 2);
                    Put(t, x0 + 1, y0 + 3);
                }
            }
        }

        private static void Put(TileWorld t, int x, int y) => t.Objects[(y * t.WorldSize) + x] = ObjectId.Cliff;

        // В ячейке вода, берег или тропа — обрыва нет.
        private static bool CellBusy(TileWorld t, int ci, int cj)
        {
            int w = t.WorldSize;
            for (int dy = 0; dy < GridField.Step; dy++)
            {
                int row = (((cj << GridField.StepBits) + dy) * w) + (ci << GridField.StepBits);
                for (int dx = 0; dx < GridField.Step; dx++)
                {
                    if (t.Floor[row + dx] != FloorId.Empty || (t.Marks[row + dx] & TileWorld.MarkTrail) != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Связные линии ячеек (соседи — через пересечённое общее ребро): линия короче <paramref name="minCells"/> убирается.
        /// С <paramref name="world"/> — ещё и нарезка: обход в ширину от первой ячейки линии, каждая <see cref="GapEvery"/>-я
        /// глубина (со сдвигом от хэша первой ячейки) — проход; ячейки прохода помечаются в мире.
        /// </summary>
        private static void DropShort(bool[] keep, byte[] edges, int n, int minCells, int[] level, int[] queue, ulong cutSeed, TileWorld? world)
        {
            int cmask = n - 1;
            var seen = new bool[keep.Length];
            for (int s = 0; s < keep.Length; s++)
            {
                if (!keep[s] || seen[s])
                {
                    continue;
                }

                int qt = 0;
                queue[qt++] = s;
                seen[s] = true;
                level[s] = 0;
                for (int qh = 0; qh < qt; qh++)
                {
                    int u = queue[qh];
                    int ui = u & cmask;
                    int uj = u / n;
                    byte e = edges[u];
                    for (int k = 0; k < 4; k++)
                    {
                        if ((e & (1 << k)) == 0)
                        {
                            continue;
                        }

                        int vi = k == 0 ? (ui - 1) & cmask : k == 1 ? (ui + 1) & cmask : ui;
                        int vj = k == 2 ? (uj - 1) & cmask : k == 3 ? (uj + 1) & cmask : uj;
                        int v = (vj * n) + vi;
                        if (keep[v] && !seen[v])
                        {
                            seen[v] = true;
                            level[v] = level[u] + 1;
                            queue[qt++] = v;
                        }
                    }
                }

                if (qt < minCells)
                {
                    for (int k = 0; k < qt; k++)
                    {
                        keep[queue[k]] = false;
                    }

                    continue;
                }

                if (world == null)
                {
                    continue;
                }

                int offset = (int)((SplitMix64.Hash(s & cmask, s / n, cutSeed) >> 33) % GapEvery);
                for (int k = 0; k < qt; k++)
                {
                    int c = queue[k];
                    if ((level[c] + offset) % GapEvery == GapEvery - 1)
                    {
                        keep[c] = false;
                        MarkClear(world, c & cmask, c / n);
                    }
                }
            }
        }

        private static void MarkClear(TileWorld t, int ci, int cj)
        {
            int w = t.WorldSize;
            for (int dy = 0; dy < GridField.Step; dy++)
            {
                int row = (((cj << GridField.StepBits) + dy) * w) + (ci << GridField.StepBits);
                for (int dx = 0; dx < GridField.Step; dx++)
                {
                    t.Marks[row + dx] |= TileWorld.MarkClear;
                }
            }
        }
    }
}
