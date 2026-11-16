using System;

namespace Orpheus.WorldGen
{
    /// <summary>Гладкое поле шума на всех тайлах: значения в узлах решётки шага 4, между узлами — билинейно (целые, тор).</summary>
    public static class GridField
    {
        /// <summary>Шаг решётки, тайлов (он же — ячейка обрывов).</summary>
        public const int Step = 4;

        public const int StepBits = 2;

        /// <summary>Значения поля в узлах (4i, 4j): массив (W/4)², Q16.</summary>
        public static ushort[] Lattice(FieldNoise f, int w)
        {
            int n = w >> StepBits;
            var g = new ushort[n * n];
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    g[(j * n) + i] = (ushort)f.Sample(i << StepBits, j << StepBits);
                }
            }

            return g;
        }

        /// <summary>Значение в тайле (x, y) по узлам решётки, Q16; координаты уже свёрнуты (0…W−1).</summary>
        public static int At(ushort[] lattice, int n, int x, int y)
        {
            int i0 = x >> StepBits;
            int j0 = y >> StepBits;
            int i1 = i0 + 1 == n ? 0 : i0 + 1;
            int j1 = j0 + 1 == n ? 0 : j0 + 1;
            int fx = x & (Step - 1);
            int fy = y & (Step - 1);
            int top = (lattice[(j0 * n) + i0] * (Step - fx)) + (lattice[(j0 * n) + i1] * fx);
            int bot = (lattice[(j1 * n) + i0] * (Step - fx)) + (lattice[(j1 * n) + i1] * fx);
            return ((top * (Step - fy)) + (bot * fy)) >> (2 * StepBits);
        }

        /// <summary>Поле на всех тайлах W × W (решётка шага 4 + билинейно).</summary>
        public static ushort[] Upsample(FieldNoise f, int w)
        {
            int n = w >> StepBits;
            ushort[] g = Lattice(f, w);
            var r = new ushort[w * w];
            for (int y = 0; y < w; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    r[row + x] = (ushort)At(g, n, x, y);
                }
            }

            return r;
        }

        /// <summary>Порог по гистограмме Q16: наименьшее v, для которого значений &lt; v не меньше <paramref name="below"/>.</summary>
        public static int QuantileBelow(int[] hist, long below)
        {
            long acc = 0;
            for (int v = 0; v < hist.Length; v++)
            {
                if (acc >= below)
                {
                    return v;
                }

                acc += hist[v];
            }

            return hist.Length;
        }
    }

    /// <summary>
    /// Рельеф мира (карточка E1-2, план 1.2): поле высот и уровни 0…3. Высоты в симуляции нет — рельеф нужен генератору
    /// (озёра — низины, обрывы — линии, где высота пересекает порог уровня) и клиенту (куда «смотрит» обрыв: в сторону
    /// меньшего уровня). Считается только от размера мира и зерна: клиент строит тот же рельеф вызовом <see cref="Create"/>.
    /// <para>
    /// Высота — полусумма гребневого поля h макрокарты (горы — высоко) и своего поля рельефа (ячейки 256…32): крупная форма
    /// согласована с биомами, линии уровня изгибаются на 60–120 тайлов. Хранится в узлах решётки шага 4; пороги уровней —
    /// квантили высоты по узлам (<see cref="LevelQuantiles"/>), поэтому доли уровней одинаковы на всех зёрнах.
    /// </para>
    /// </summary>
    public sealed class ReliefMap
    {
        /// <summary>Номер этапа (свой поток случайных чисел).</summary>
        public const int Stage = 14;

        public const int FieldRelief = 1;

        /// <summary>Октавы своего поля рельефа.</summary>
        public static readonly int[] CellsRelief = { 128, 64, 32 };

        /// <summary>Пороги уровней 1, 2, 3 — доли узлов ниже порога, ‰.</summary>
        public static readonly int[] LevelQuantiles = { 330, 500, 670 };

        /// <summary>Число уровней выше нулевого.</summary>
        public const int Levels = 3;

        private readonly ushort[] _height;
        private readonly byte[] _level;

        private ReliefMap(int w, ushort[] height, int[] thresholds)
        {
            WorldSize = w;
            Nodes = w >> GridField.StepBits;
            _height = height;
            Thresholds = thresholds;
            _level = new byte[height.Length];
            for (int i = 0; i < height.Length; i++)
            {
                _level[i] = (byte)LevelOf(height[i]);
            }
        }

        public int WorldSize { get; }

        /// <summary>Узлов решётки на ось (W / 4).</summary>
        public int Nodes { get; }

        /// <summary>Пороги высоты уровней 1…3, Q16 (по возрастанию).</summary>
        public int[] Thresholds { get; }

        /// <summary>Рельеф мира W × W с зерном <paramref name="seed"/>.</summary>
        public static ReliefMap Create(int w, ulong seed)
        {
            FieldNoise h = MacroGenerator.CreateH(w, seed);
            var r = new FieldNoise(MacroGenerator.FieldSeed(seed, Stage, FieldRelief), w, CellsRelief, ridged: false);
            int n = w >> GridField.StepBits;
            var height = new ushort[n * n];
            var hist = new int[LatticeNoise.One];
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int x = i << GridField.StepBits;
                    int y = j << GridField.StepBits;
                    int v = (h.Sample(x, y) + r.Sample(x, y)) >> 1;
                    height[(j * n) + i] = (ushort)v;
                    hist[v]++;
                }
            }

            var thresholds = new int[Levels];
            for (int k = 0; k < Levels; k++)
            {
                thresholds[k] = GridField.QuantileBelow(hist, (long)height.Length * LevelQuantiles[k] / 1000);
            }

            return new ReliefMap(w, height, thresholds);
        }

        /// <summary>Высота в тайле, Q16 (билинейно по узлам); координаты сворачиваются тором.</summary>
        public int HeightAt(int x, int y) => GridField.At(_height, Nodes, x & (WorldSize - 1), y & (WorldSize - 1));

        /// <summary>Уровень высоты в тайле, 0…3: сколько порогов не выше высоты тайла. Дёшево: 4 чтения и сравнения.</summary>
        public int LevelAt(int x, int y) => LevelOf(HeightAt(x, y));

        /// <summary>Уровень узла решётки (i, j) — угла ячейки обрывов; индексы сворачиваются.</summary>
        public int NodeLevel(int i, int j) => _level[((j & (Nodes - 1)) * Nodes) + (i & (Nodes - 1))];

        private int LevelOf(int height)
        {
            int level = 0;
            while (level < Levels && height >= Thresholds[level])
            {
                level++;
            }

            return level;
        }
    }
}
