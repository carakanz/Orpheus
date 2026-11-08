using System;
using Orpheus.Core.Rng;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Генератор v2, этапы 1–2 (карточка 1-015; docs/01, 2 «Этапы»): кольца и зоны, поля h, m, c, шум края
    /// на макрокарте. Перенос <c>fields2</c> и <c>geometry</c> модели
    /// <c>design_room/threads/01_world/worldgen_model_v2.py</c> (+ <c>worldgen_model.py</c>) на целые числа.
    /// <para>
    /// У этапа свой поток случайных чисел: seed_этапа = hash(зерно, номер этапа) (`01` 2), у поля —
    /// hash(seed_этапа, номер поля), у октавы — hash(поле, номер октавы). Номера полей — как в модели
    /// (h 1, m 2, c 3, смещение жаркой оси 4, холодной 5) плюс шум края 6 и деталь границ 7.
    /// Зёрна модели (<c>numpy</c>) и C# разные, поэтому сверка с моделью — по распределениям, не бит в бит.
    /// </para>
    /// </summary>
    public static class MacroGenerator
    {
        /// <summary>Версия генератора v2 (пишется в заголовок макрокарты; растёт при любом изменении результата).</summary>
        public const int GeneratorVersion = 2;

        /// <summary>Этап 1 «Кольца и зоны» (в его потоке — смещение осей поясов: этап 1 его читает).</summary>
        public const int StageZones = 1;

        /// <summary>Этап 2 «Поля».</summary>
        public const int StageFields = 2;

        public const int FieldH = 1;
        public const int FieldM = 2;
        public const int FieldC = 3;
        public const int FieldShiftHot = 4;
        public const int FieldShiftCold = 5;
        public const int FieldEdge = 6;
        public const int FieldDetail = 7;

        /// <summary>Октавы полей (`01` 2, таблица «Поля шума»; ячейки до min(·, W/2)).</summary>
        public static readonly int[] CellsH = { 1024, 512, 256, 128 };
        public static readonly int[] CellsM = { 512, 256, 128 };
        public static readonly int[] CellsC = { 256, 128, 64 };
        public static readonly int[] CellsEdge = { 256, 128 };
        public static readonly int[] CellsDetail = { 32, 16 };

        /// <summary>Пик смещения оси пояса на 4096², тайлов (масштаб как R).</summary>
        public const int ShiftPeak4096 = 350;

        /// <summary>Пределы зон на 4096² (`01` 2, этап 1): жаркая dH &lt; 600, холодная dC &lt; 500.</summary>
        public const int HotHalf4096 = 600;
        public const int ColdHalf4096 = 500;

        /// <summary>Перекрёсток на 4096²: dH &lt; 300 и dC &lt; 250.</summary>
        public const int CrossHot4096 = 300;
        public const int CrossCold4096 = 250;

        /// <summary>Предвратие — круг r = 64 у Изнанки (не масштабируется).</summary>
        public const int PregateRadius = 64;

        /// <summary>Кольцо 0 — d &lt; 256 (не масштабируется); границы 1/2 и 2/3 — R/2 и R.</summary>
        public const int Ring0Radius = 256;

        /// <summary>seed_этапа = hash(зерно, номер этапа).</summary>
        public static ulong StageSeed(ulong seed, int stage) => SplitMix64.Hash(stage, 0, seed);

        /// <summary>Зерно поля в потоке этапа.</summary>
        public static ulong FieldSeed(ulong seed, int stage, int field) => SplitMix64.Hash(field, 0, StageSeed(seed, stage));

        /// <summary>Поле h мира (гребневое).</summary>
        public static FieldNoise CreateH(int w, ulong seed) => new FieldNoise(FieldSeed(seed, StageFields, FieldH), w, CellsH, ridged: true);

        public static FieldNoise CreateM(int w, ulong seed) => new FieldNoise(FieldSeed(seed, StageFields, FieldM), w, CellsM, ridged: false);

        public static FieldNoise CreateC(int w, ulong seed) => new FieldNoise(FieldSeed(seed, StageFields, FieldC), w, CellsC, ridged: false);

        public static FieldNoise CreateEdge(int w, ulong seed) => new FieldNoise(FieldSeed(seed, StageFields, FieldEdge), w, CellsEdge, ridged: false);

        /// <summary>Деталь границ (ячейка 32, 2 октавы) — на тайлах, для этапа 11; на макрокарте не хранится.</summary>
        public static FieldNoise CreateDetail(int w, ulong seed) => new FieldNoise(FieldSeed(seed, StageFields, FieldDetail), w, CellsDetail, ridged: false);

        /// <summary>Этапы 1–2 целиком: макрокарта (биом пока не назначен) и данные этапов для следующих.</summary>
        public static MacroWorld Generate(int worldSize, ulong seed, BiomeSet set)
        {
            var map = new MacroMap(worldSize, seed, GeneratorVersion, set);
            ZoneData zones = Stage1Zones(map);
            FieldData fields = Stage2Fields(map);
            return new MacroWorld(map, zones, fields);
        }

        // ---------------------------------------------------------------- этап 1

        /// <summary>
        /// Этап 1 «Кольца и зоны»: d до Очага (W/2, W/2), dH и dC до осей поясов со смещением, dA до Изнанки (0, 0);
        /// кольцо в байт зоны; зоны поясов, Перекрёсток и Предвратие — только у полного набора (в срезе поясов нет, `01` 1 «Края»).
        /// Расстояния — целые тайлы: d и dA — ⌊√·⌋ (поэтому «d &lt; r» и «d ≥ r» с целым r точны), смещение оси — округлено до тайла.
        /// </summary>
        public static ZoneData Stage1Zones(MacroMap map)
        {
            int w = map.WorldSize;
            int size = map.Size;
            int half = w >> 1;
            int[] shiftHot = Shift(w, map.Seed, FieldShiftHot);
            int[] shiftCold = Shift(w, map.Seed, FieldShiftCold);
            int n = size * size;
            var data = new ZoneData(size, shiftHot, shiftCold);
            bool full = map.Set == BiomeSet.Full;
            for (int y = 0; y < size; y++)
            {
                int cy = (y << MacroMap.CellBits) + (MacroMap.CellTiles >> 1);
                long py = cy - half;
                int wy = IntMath.Wrap(cy, w);
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    int cx = (x << MacroMap.CellBits) + (MacroMap.CellTiles >> 1);
                    long px = cx - half;
                    int wx = IntMath.Wrap(cx, w);
                    int d = (int)IntMath.Isqrt(px * px + py * py);
                    int dh = IntMath.Abs(IntMath.Wrap(cx - shiftHot[y], w));   // ось жаркого пояса: x = смещение(y)
                    int dc = IntMath.Abs(IntMath.Wrap(cy - shiftCold[x], w));  // ось холодного: y = смещение(x)
                    int da = (int)IntMath.Isqrt((long)wx * wx + (long)wy * wy);
                    data.D[i] = d;
                    data.DHot[i] = dh;
                    data.DCold[i] = dc;
                    data.DGate[i] = da;

                    byte zone = (byte)RingOf(d, w);
                    if (full)
                    {
                        // Пределы на 4096² × W/4096 — сравнение без деления: dH·4096 < 600·W.
                        long dh4 = (long)dh * 4096;
                        long dc4 = (long)dc * 4096;
                        if (dh4 < (long)HotHalf4096 * w)
                        {
                            zone |= MacroMap.ZoneHot;
                        }

                        if (dc4 < (long)ColdHalf4096 * w)
                        {
                            zone |= MacroMap.ZoneCold;
                        }

                        if (dh4 < (long)CrossHot4096 * w && dc4 < (long)CrossCold4096 * w)
                        {
                            zone |= MacroMap.ZoneCrossroads;
                        }

                        if (da < PregateRadius)
                        {
                            zone |= MacroMap.ZonePregate;
                        }
                    }

                    map.Zone[i] = zone;
                }
            }

            return data;
        }

        /// <summary>Кольцо по d (`01` 1, таблица колец): 0 — &lt; 256, 1 — до R/2, 2 — до R, 3 — ≥ R.</summary>
        public static int RingOf(int d, int worldSize)
        {
            int r = worldSize >> 1;
            if (d < Ring0Radius)
            {
                return 0;
            }

            if (d < (r >> 1))
            {
                return 1;
            }

            return d < r ? 2 : 3;
        }

        /// <summary>
        /// Смещение оси пояса (перенос <c>shift</c> модели): 1D-шум, ячейки 2048·k и 1024·k (k = W/4096, то есть W/2 и W/4),
        /// веса 1 и ½; s = 2·Σ/Σw − 1, нормировка на max|s| по оси, пик ±350·k. Значения — тайлы, округлённые.
        /// </summary>
        public static int[] Shift(int worldSize, ulong seed, int field)
        {
            ulong fs = FieldSeed(seed, StageZones, field);
            var o0 = new LatticeOctave1D(FieldNoise.OctaveSeed(fs, 0), worldSize, worldSize >> 1);
            var o1 = new LatticeOctave1D(FieldNoise.OctaveSeed(fs, 1), worldSize, worldSize >> 2);
            int size = worldSize >> MacroMap.CellBits;
            var num = new long[size];
            long max = 0;
            for (int i = 0; i < size; i++)
            {
                int c = (i << MacroMap.CellBits) + (MacroMap.CellTiles >> 1);
                long acc = 2L * o0.Sample(c) + o1.Sample(c);      // Σ w·v × 2, Σw × 2 = 3
                long s = 2 * acc - 3L * LatticeNoise.One;        // (2·Σ/Σw − 1) × 3·One
                num[i] = s;
                long a = s < 0 ? -s : s;
                if (a > max)
                {
                    max = a;
                }
            }

            var shift = new int[size];
            if (max == 0)
            {
                return shift;
            }

            // s / max × 350 · W / 4096.
            long den = max * 4096;
            for (int i = 0; i < size; i++)
            {
                shift[i] = (int)IntMath.DivRound(num[i] * ShiftPeak4096 * worldSize, den);
            }

            return shift;
        }

        // ---------------------------------------------------------------- этап 2

        /// <summary>
        /// Этап 2 «Поля»: h, m, c и шум края в центре каждой ячейки (тайл 8i + 4), Q16 0…65 535;
        /// в макрокарту — старшие байты h, m, c.
        /// </summary>
        public static FieldData Stage2Fields(MacroMap map)
        {
            int w = map.WorldSize;
            int size = map.Size;
            FieldNoise h = CreateH(w, map.Seed);
            FieldNoise m = CreateM(w, map.Seed);
            FieldNoise c = CreateC(w, map.Seed);
            FieldNoise e = CreateEdge(w, map.Seed);
            var data = new FieldData(size);
            for (int y = 0; y < size; y++)
            {
                int ty = (y << MacroMap.CellBits) + (MacroMap.CellTiles >> 1);
                for (int x = 0; x < size; x++)
                {
                    int tx = (x << MacroMap.CellBits) + (MacroMap.CellTiles >> 1);
                    int i = y * size + x;
                    int hv = h.Sample(tx, ty);
                    int mv = m.Sample(tx, ty);
                    int cv = c.Sample(tx, ty);
                    data.H[i] = (ushort)hv;
                    data.M[i] = (ushort)mv;
                    data.C[i] = (ushort)cv;
                    data.Edge[i] = (ushort)e.Sample(tx, ty);
                    map.H[i] = (byte)(hv >> 8);
                    map.M[i] = (byte)(mv >> 8);
                    map.C[i] = (byte)(cv >> 8);
                }
            }

            return data;
        }
    }

    /// <summary>Результат этапов 1–2: макрокарта и данные для этапов 3+.</summary>
    public sealed class MacroWorld
    {
        public MacroWorld(MacroMap map, ZoneData zones, FieldData fields)
        {
            Map = map;
            Zones = zones;
            Fields = fields;
        }

        public MacroMap Map { get; }

        public ZoneData Zones { get; }

        public FieldData Fields { get; }
    }

    /// <summary>Этап 1 на ячейку, тайлов: d до Очага, dH, dC до осей поясов, dA до Изнанки; смещения осей.</summary>
    public sealed class ZoneData
    {
        public ZoneData(int size, int[] shiftHot, int[] shiftCold)
        {
            int n = size * size;
            D = new int[n];
            DHot = new int[n];
            DCold = new int[n];
            DGate = new int[n];
            ShiftHot = shiftHot;
            ShiftCold = shiftCold;
        }

        public int[] D { get; }

        public int[] DHot { get; }

        public int[] DCold { get; }

        public int[] DGate { get; }

        /// <summary>Смещение жаркой оси по строке y макрокарты: ось — x = смещение(y), тайлов.</summary>
        public int[] ShiftHot { get; }

        /// <summary>Смещение холодной оси по столбцу x: ось — y = смещение(x), тайлов.</summary>
        public int[] ShiftCold { get; }
    }

    /// <summary>Этап 2 на ячейку: поля в Q16 (полная точность для квантилей этапа 3; в макрокарте — байты).</summary>
    public sealed class FieldData
    {
        public FieldData(int size)
        {
            int n = size * size;
            H = new ushort[n];
            M = new ushort[n];
            C = new ushort[n];
            Edge = new ushort[n];
        }

        public ushort[] H { get; }

        public ushort[] M { get; }

        public ushort[] C { get; }

        /// <summary>Шум края пояса (ячейка 256, 2 октавы); ±80 тайлов на 4096² — пересчёт на этапе 3.</summary>
        public ushort[] Edge { get; }
    }
}
