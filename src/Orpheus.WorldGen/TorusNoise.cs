using System;
using Orpheus.Core.Rng;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Решётчатый value noise в фиксированной точке, замкнутый на торе (docs/01, 2 «Поля шума»).
    /// <para>
    /// Выбор: value noise, а не simplex — так велит `01` 2 (значение в узле — хэш зерна и индексов узла,
    /// период решётки — степень двойки), и он считается целыми сложениями и сдвигами без таблицы градиентов.
    /// </para>
    /// <para>
    /// Октава k: ячейка <c>base &gt;&gt; k</c>, но не больше половины стороны мира (`01` 2: иначе октава на 1024²
    /// вырождается в константу). Узлы октавы — сетка (W / ячейка) × (H / ячейка); индекс узла берётся по модулю
    /// числа узлов, поэтому последний отрезок решётки интерполирует к узлу 0 — шва нет по построению.
    /// Значение узла — 15 бит из SplitMix64(ix, iy, зерно октавы); вес — smoothstep 3t² − 2t³ в Q15 по таблице;
    /// октава k складывается со сдвигом вправо на k (амплитуда 1/2ᵏ). Только целые числа, без float.
    /// </para>
    /// <para>Диапазон: 0 … <see cref="MaxValue"/> (≤ 32 767 · (2 − 2^(1−n)) &lt; 65 536).</para>
    /// </summary>
    public sealed class TorusNoise
    {
        /// <summary>Узел: 15 бит, 0…32 767.</summary>
        public const int NodeBits = 15;

        /// <summary>Единица веса: 1.0 = 2¹⁵.</summary>
        public const int WeightOne = 1 << 15;

        private readonly int _width;
        private readonly int _height;
        private readonly Octave[] _octaves;

        /// <param name="seed">Зерно поля (уже производное: зерно мира + этап + поле).</param>
        /// <param name="width">Ширина тора, степень двойки.</param>
        /// <param name="height">Высота тора, степень двойки.</param>
        /// <param name="baseCell">Ячейка базовой октавы, степень двойки ≥ 2.</param>
        /// <param name="octaves">Число октав, 1…8.</param>
        public TorusNoise(ulong seed, int width, int height, int baseCell, int octaves)
        {
            CheckPow2(width, nameof(width), 2);
            CheckPow2(height, nameof(height), 2);
            CheckPow2(baseCell, nameof(baseCell), 2);
            if (octaves < 1 || octaves > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(octaves), octaves, "Октав — от 1 до 8.");
            }

            _width = width;
            _height = height;
            _octaves = new Octave[octaves];
            int max = 0;
            for (int k = 0; k < octaves; k++)
            {
                int cell = Math.Max(2, baseCell >> k);
                _octaves[k] = new Octave(SplitMix64.Hash(k, 0x4E6F6973, seed), width, height, cell, k);
                max += ((1 << NodeBits) - 1) >> k;
            }

            MaxValue = max;
        }

        /// <summary>Ширина тора.</summary>
        public int Width => _width;

        /// <summary>Высота тора.</summary>
        public int Height => _height;

        /// <summary>Наибольшее возможное значение (сумма амплитуд октав).</summary>
        public int MaxValue { get; }

        /// <summary>Значение в тайле (x, y); координаты сворачиваются тором — любые int.</summary>
        public int Sample(int x, int y)
        {
            x &= _width - 1;
            y &= _height - 1;
            int sum = 0;
            for (int k = 0; k < _octaves.Length; k++)
            {
                sum += _octaves[k].Sample(x, y);
            }

            return sum;
        }

        /// <summary>
        /// Строка y целиком: dest[x] = Sample(x, y) для x = 0…W−1. Быстрый путь генератора:
        /// узлы по Y интерполируются один раз на строку, по X — проход со сдвигами.
        /// <paramref name="scratch"/> — буфер не короче <see cref="ScratchLength"/>.
        /// </summary>
        public void FillRow(int y, Span<int> dest, Span<int> scratch)
        {
            if (dest.Length < _width)
            {
                throw new ArgumentException($"Строка короче ширины {_width}.", nameof(dest));
            }

            dest.Slice(0, _width).Clear();
            y &= _height - 1;
            for (int k = 0; k < _octaves.Length; k++)
            {
                _octaves[k].AddRow(y, dest, scratch);
            }
        }

        /// <summary>Длина буфера для <see cref="FillRow"/>: узлов строки самой мелкой октавы.</summary>
        public int ScratchLength
        {
            get
            {
                int n = 0;
                for (int k = 0; k < _octaves.Length; k++)
                {
                    n = Math.Max(n, _octaves[k].NodesX);
                }

                return n;
            }
        }

        private static void CheckPow2(int v, string name, int min)
        {
            if (v < min || (v & (v - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(name, v, $"Нужна степень двойки не меньше {min}.");
            }
        }

        private static int Lerp(int a, int b, int w) => a + (((b - a) * w) >> NodeBits);

        private sealed class Octave
        {
            public readonly int NodesX;
            private readonly int _nodesY;
            private readonly int _shiftX;
            private readonly int _shiftY;
            private readonly int _maskX;
            private readonly int _maskY;
            private readonly int _amplitudeShift;
            private readonly int[] _nodes;   // [iy * NodesX + ix], 0…32 767
            private readonly int[] _weightX; // smoothstep(fx / cellX) в Q15
            private readonly int[] _weightY;

            public Octave(ulong seed, int width, int height, int cell, int amplitudeShift)
            {
                // Период октавы — min(ячейка, сторона / 2) по каждой оси (`01` 2).
                int cellX = Math.Min(cell, Math.Max(1, width >> 1));
                int cellY = Math.Min(cell, Math.Max(1, height >> 1));
                NodesX = width / cellX;
                _nodesY = height / cellY;
                _shiftX = Log2(cellX);
                _shiftY = Log2(cellY);
                _maskX = cellX - 1;
                _maskY = cellY - 1;
                _amplitudeShift = amplitudeShift;
                _nodes = new int[NodesX * _nodesY];
                for (int iy = 0; iy < _nodesY; iy++)
                {
                    for (int ix = 0; ix < NodesX; ix++)
                    {
                        _nodes[iy * NodesX + ix] = (int)(SplitMix64.Hash(ix, iy, seed) >> (64 - NodeBits));
                    }
                }

                _weightX = Smoothstep(cellX);
                _weightY = Smoothstep(cellY);
            }

            public int Sample(int x, int y)
            {
                int ix0 = x >> _shiftX;
                int iy0 = y >> _shiftY;
                int ix1 = ix0 + 1 == NodesX ? 0 : ix0 + 1;
                int iy1 = iy0 + 1 == _nodesY ? 0 : iy0 + 1;
                int wx = _weightX[x & _maskX];
                int wy = _weightY[y & _maskY];
                // Порядок как в AddRow: сначала по Y (столбцы узлов), потом по X — округление то же.
                int left = Lerp(_nodes[iy0 * NodesX + ix0], _nodes[iy1 * NodesX + ix0], wy);
                int right = Lerp(_nodes[iy0 * NodesX + ix1], _nodes[iy1 * NodesX + ix1], wy);
                return Lerp(left, right, wx) >> _amplitudeShift;
            }

            public void AddRow(int y, Span<int> dest, Span<int> column)
            {
                int iy0 = y >> _shiftY;
                int iy1 = iy0 + 1 == _nodesY ? 0 : iy0 + 1;
                int wy = _weightY[y & _maskY];
                int row0 = iy0 * NodesX;
                int row1 = iy1 * NodesX;
                for (int ix = 0; ix < NodesX; ix++)
                {
                    column[ix] = Lerp(_nodes[row0 + ix], _nodes[row1 + ix], wy);
                }

                int cell = _maskX + 1;
                int x = 0;
                for (int ix = 0; ix < NodesX; ix++)
                {
                    int a = column[ix];
                    int b = column[ix + 1 == NodesX ? 0 : ix + 1];
                    for (int fx = 0; fx < cell; fx++, x++)
                    {
                        dest[x] += Lerp(a, b, _weightX[fx]) >> _amplitudeShift;
                    }
                }
            }

            private static int[] Smoothstep(int cell)
            {
                // t = f / cell в Q15; s = 3t² − 2t³ = t²(3 − 2t); в long, чтобы не переполниться.
                var w = new int[cell];
                for (int f = 0; f < cell; f++)
                {
                    long t = ((long)f << NodeBits) / cell;
                    long t2 = (t * t) >> NodeBits;
                    w[f] = (int)((t2 * ((3L << NodeBits) - 2 * t)) >> NodeBits);
                }

                return w;
            }

            private static int Log2(int v)
            {
                int n = 0;
                while ((1 << n) < v)
                {
                    n++;
                }

                return n;
            }
        }
    }
}
