using System;
using Orpheus.Core.Rng;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Решётчатый value-шум генератора v2 в фиксированной точке Q16 (docs/01, 2 «Поля шума»; перенос
    /// <c>octave2d</c>/<c>octave1d</c> из <c>design_room/threads/01_world/worldgen_model.py</c>).
    /// <para>
    /// Значение узла — 16 бит хэша SplitMix64(ix, iy, зерно октавы), вес — квинтик 6t⁵ − 15t⁴ + 10t³ в Q16
    /// по таблице, как <c>fade</c> модели. Индекс узла берётся по модулю числа узлов: последний отрезок решётки
    /// интерполирует к узлу 0, шва на торе нет по построению. Выборка — в целых координатах тайла:
    /// центр ячейки макрокарты i — тайл 8i + 4, ровно как <c>coords</c> модели.
    /// </para>
    /// <para>Только целые: <c>long</c> на умножениях Q16 × Q16, сдвиг вправо — округление к −∞ (одинаково везде).</para>
    /// </summary>
    public static class LatticeNoise
    {
        /// <summary>Единица Q16: 1.0 = 65 536. Узлы — 0…65 535, веса — 0…65 536.</summary>
        public const int One = 1 << 16;

        /// <summary>
        /// Период октавы — min(ячейка, W/2) (docs/01, 2: иначе на 1024² октава с ячейкой 1024 — один узел, константа).
        /// </summary>
        public static int OctaveCell(int cell, int worldSize) => Math.Min(cell, worldSize >> 1);

        /// <summary>Таблица квинтика для ячейки: w[f] = fade(f / cell) в Q16, f = 0…cell − 1.</summary>
        public static int[] FadeTable(int cell)
        {
            var w = new int[cell];
            int shift = 16 - IntMath.Log2(cell);
            for (int f = 0; f < cell; f++)
            {
                // t в Q16 — точно (ячейка — степень двойки ≤ 65 536).
                long t = shift >= 0 ? (long)f << shift : (long)f >> -shift;
                long t2 = (t * t) >> 16;
                long t3 = (t2 * t) >> 16;
                long inner = ((t * (6 * t - 15L * One)) >> 16) + 10L * One;
                w[f] = (int)((t3 * inner) >> 16);
            }

            return w;
        }

        /// <summary>a + (b − a)·w в Q16.</summary>
        public static int Lerp(int a, int b, int w) => a + (int)(((long)(b - a) * w) >> 16);

        /// <summary>Значение узла: старшие 16 бит хэша.</summary>
        public static int Node(int ix, int iy, ulong seed) => (int)(SplitMix64.Hash(ix, iy, seed) >> 48);
    }

    /// <summary>Одна октава 2D: решётка (W / ячейка)² узлов на торе W × W.</summary>
    public sealed class LatticeOctave2D
    {
        private readonly int _worldMask;
        private readonly int _nodes;
        private readonly int _shift;
        private readonly int _cellMask;
        private readonly int[] _values; // [iy * n + ix]
        private readonly int[] _fade;

        /// <param name="seed">Зерно октавы.</param>
        /// <param name="worldSize">Сторона тора, степень двойки.</param>
        /// <param name="cell">Ячейка решётки (уже с min(·, W/2)), степень двойки, делит сторону.</param>
        public LatticeOctave2D(ulong seed, int worldSize, int cell)
        {
            if (!IntMath.IsPow2(worldSize) || !IntMath.IsPow2(cell) || cell > worldSize)
            {
                throw new ArgumentOutOfRangeException(nameof(cell), cell, $"Ячейка — степень двойки, не больше стороны {worldSize}.");
            }

            _worldMask = worldSize - 1;
            Cell = cell;
            _nodes = worldSize / cell;
            _shift = IntMath.Log2(cell);
            _cellMask = cell - 1;
            _values = new int[_nodes * _nodes];
            for (int iy = 0; iy < _nodes; iy++)
            {
                for (int ix = 0; ix < _nodes; ix++)
                {
                    _values[iy * _nodes + ix] = LatticeNoise.Node(ix, iy, seed);
                }
            }

            _fade = LatticeNoise.FadeTable(cell);
        }

        /// <summary>Ячейка решётки, тайлов.</summary>
        public int Cell { get; }

        /// <summary>Узлов на ось.</summary>
        public int Nodes => _nodes;

        /// <summary>Значение в тайле (x, y), Q16 0…65 535; координаты сворачиваются тором.</summary>
        public int Sample(int x, int y)
        {
            x &= _worldMask;
            y &= _worldMask;
            int ix0 = x >> _shift;
            int iy0 = y >> _shift;
            int ix1 = ix0 + 1 == _nodes ? 0 : ix0 + 1;
            int iy1 = iy0 + 1 == _nodes ? 0 : iy0 + 1;
            int fx = _fade[x & _cellMask];
            int fy = _fade[y & _cellMask];
            int r0 = iy0 * _nodes;
            int r1 = iy1 * _nodes;
            // Как модель: сначала по x (top, bot), потом по y.
            int top = LatticeNoise.Lerp(_values[r0 + ix0], _values[r0 + ix1], fx);
            int bot = LatticeNoise.Lerp(_values[r1 + ix0], _values[r1 + ix1], fx);
            return LatticeNoise.Lerp(top, bot, fy);
        }
    }

    /// <summary>Одна октава 1D на кольце длины W (смещение осей поясов).</summary>
    public sealed class LatticeOctave1D
    {
        private readonly int _worldMask;
        private readonly int _nodes;
        private readonly int _shift;
        private readonly int _cellMask;
        private readonly int[] _values;
        private readonly int[] _fade;

        public LatticeOctave1D(ulong seed, int worldSize, int cell)
        {
            if (!IntMath.IsPow2(worldSize) || !IntMath.IsPow2(cell) || cell > worldSize)
            {
                throw new ArgumentOutOfRangeException(nameof(cell), cell, $"Ячейка — степень двойки, не больше стороны {worldSize}.");
            }

            _worldMask = worldSize - 1;
            _nodes = worldSize / cell;
            _shift = IntMath.Log2(cell);
            _cellMask = cell - 1;
            _values = new int[_nodes];
            for (int i = 0; i < _nodes; i++)
            {
                _values[i] = LatticeNoise.Node(i, 0, seed);
            }

            _fade = LatticeNoise.FadeTable(cell);
        }

        /// <summary>Значение в точке x, Q16 0…65 535.</summary>
        public int Sample(int x)
        {
            x &= _worldMask;
            int i0 = x >> _shift;
            int i1 = i0 + 1 == _nodes ? 0 : i0 + 1;
            return LatticeNoise.Lerp(_values[i0], _values[i1], _fade[x & _cellMask]);
        }
    }

    /// <summary>
    /// Поле из нескольких октав (docs/01, 2, таблица полей): октава o — ячейка cells[o] с min(·, W/2) и вес 2⁻ᵒ;
    /// сумма делится на сумму весов, как в <c>fields2</c> модели. Плавное поле — Σ wₒ·v; гребневое (h) —
    /// Σ wₒ·(1 − |2v − 1|)². Результат — Q16 0…65 535.
    /// </summary>
    public sealed class FieldNoise
    {
        private readonly LatticeOctave2D[] _octaves;
        private readonly bool _ridged;
        private readonly int _weightSum; // 2ⁿ − 1 (веса 2^(n−1−o))

        /// <param name="fieldSeed">Зерно поля: hash(зерно этапа, номер поля); октава o — hash(поле, o).</param>
        /// <param name="worldSize">Сторона тора.</param>
        /// <param name="cells">Ячейки октав по порядку (базовая — первая).</param>
        /// <param name="ridged">Гребневое поле (h).</param>
        public FieldNoise(ulong fieldSeed, int worldSize, int[] cells, bool ridged)
        {
            if (cells.Length < 1 || cells.Length > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(cells), cells.Length, "Октав — от 1 до 8.");
            }

            _octaves = new LatticeOctave2D[cells.Length];
            for (int o = 0; o < cells.Length; o++)
            {
                _octaves[o] = new LatticeOctave2D(OctaveSeed(fieldSeed, o), worldSize, LatticeNoise.OctaveCell(cells[o], worldSize));
            }

            _ridged = ridged;
            _weightSum = (1 << cells.Length) - 1;
        }

        /// <summary>Зерно октавы o поля.</summary>
        public static ulong OctaveSeed(ulong fieldSeed, int octave) => SplitMix64.Hash(octave, 0x0C7A, fieldSeed);

        /// <summary>Октавы (для тестов периода).</summary>
        public ReadOnlySpan<LatticeOctave2D> Octaves => _octaves;

        /// <summary>Значение в тайле (x, y), Q16 0…65 535.</summary>
        public int Sample(int x, int y)
        {
            int n = _octaves.Length;
            long acc = 0;
            for (int o = 0; o < n; o++)
            {
                long v = _octaves[o].Sample(x, y);
                if (_ridged)
                {
                    long t = LatticeNoise.One - Math.Abs(2 * v - LatticeNoise.One); // 0…65 536
                    v = (t * t) >> 16;
                }

                acc += v << (n - 1 - o);
            }

            long r = acc / _weightSum;
            return r > LatticeNoise.One - 1 ? LatticeNoise.One - 1 : (int)r;
        }
    }
}
