using System;
using Orpheus.Core.Rng;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Вода (карточка E1-2, план 1.2): озёра в низинах рельефа, 1–2 реки через весь мир, песчаный берег.
    /// <list type="bullet">
    /// <item><b>Озёра</b> — тайлы с наименьшей «высотой воды»: высота рельефа + деталь берега (ячейки 32, 16; сдвигает берег на
    /// ±8 тайлов) + подъём у Очага (до 170 тайлов — на ½, к 300 сходит на нет: берег огибает старт, а не обрезается кругом).
    /// Доля — ровно <see cref="LakePerMille"/> ‰ тайлов по гистограмме; лужи меньше <see cref="MinLakeTiles"/> убираются.</item>
    /// <item><b>Реки</b> (мир ≥ 1024) — одна всегда, вторая поперёк неё у половины зёрен: линия вдоль оси со смещением из
    /// 1D-шума (ячейки W/2, 128, 32), ширина 3–7, броды из песка шириной <see cref="FordWidth"/> каждые
    /// <see cref="FordEvery"/> тайлов. Ось реки не ближе 280 к Очагу, изгиб ≤ 112: до старта ≥ 165.</item>
    /// <item><b>Берег</b> — песок на 1–3 тайла от воды (ширина — от детали берега). В радиусе
    /// <see cref="NoWaterRadius"/> от Очага воды нет.</item>
    /// </list>
    /// Мелководье в данных не различается: клиент рисует отмель по расстоянию до берега.
    /// </summary>
    public static class WaterStage
    {
        /// <summary>Номер этапа (свой поток случайных чисел).</summary>
        public const int Stage = 15;

        public const int FieldShoreDetail = 1;
        public const int FieldRiver = 2;

        /// <summary>Доля озёр, ‰ тайлов мира (до вычета луж и круга у Очага).</summary>
        public const int LakePerMille = 75;

        /// <summary>Водоём меньше стольких тайлов — не озеро, убирается.</summary>
        public const int MinLakeTiles = 40;

        /// <summary>В этом радиусе от Очага воды и обрывов нет (с запасом к 150 из плана).</summary>
        public const int NoWaterRadius = 158;

        public const int BumpFull = 170;
        public const int BumpEnd = 300;

        /// <summary>Реки — только в мире не меньше этого.</summary>
        public const int RiverMinWorld = 1024;

        public const int RiverAxisMinDistance = 280;
        public const int FordEvery = 160;
        public const int FordWidth = 5;

        /// <summary>Наибольшая ширина берега, тайлов.</summary>
        public const int ShoreMax = 3;

        /// <summary>Значение <see cref="TileWorld.Shore"/> «далеко от воды».</summary>
        public const byte ShoreFar = 255;

        public static readonly int[] CellsShoreDetail = { 32, 16 };

        /// <summary>Вода и берег: пишет <see cref="FloorId.Water"/> и <see cref="FloorId.Sand"/> в пол (остальной пол — 0, его кладёт этап 11) и расстояние до воды.</summary>
        public static void Run(TileWorld t)
        {
            int w = t.WorldSize;
            int n = w * w;
            int mask = w - 1;
            int half = w >> 1;
            ulong seed = t.Macro.Map.Seed;
            ReliefMap relief = t.Relief;
            ushort[] detail = GridField.Upsample(
                new FieldNoise(MacroGenerator.FieldSeed(seed, Stage, FieldShoreDetail), w, CellsShoreDetail, ridged: false), w);

            // Высота воды и гистограмма.
            var wh = new ushort[n];
            var hist = new int[LatticeNoise.One];
            for (int y = 0; y < w; y++)
            {
                long dy = IntMath.Wrap(y - half, w);
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    long dx = IntMath.Wrap(x - half, w);
                    int d = (int)IntMath.Isqrt((dx * dx) + (dy * dy));
                    int bump = d <= BumpFull ? LatticeNoise.One >> 1
                        : d >= BumpEnd ? 0
                        : (LatticeNoise.One >> 1) * (BumpEnd - d) / (BumpEnd - BumpFull);
                    int v = relief.HeightAt(x, y) + ((detail[row + x] - (LatticeNoise.One >> 1)) >> 3) + bump;
                    v = v < 0 ? 0 : v > LatticeNoise.One - 1 ? LatticeNoise.One - 1 : v;
                    wh[row + x] = (ushort)v;
                    hist[v]++;
                }
            }

            int line = GridField.QuantileBelow(hist, (long)n * LakePerMille / 1000);
            var water = new bool[n];
            for (int i = 0; i < n; i++)
            {
                water[i] = wh[i] < line;
            }

            DropPuddles(water, w);
            if (w >= RiverMinWorld)
            {
                Rivers(water, w, seed);
            }

            // У Очага воды нет.
            long r2 = (long)NoWaterRadius * NoWaterRadius;
            for (int y = 0; y < w; y++)
            {
                long dy = IntMath.Wrap(y - half, w);
                if (dy * dy > r2)
                {
                    continue;
                }

                for (int x = 0; x < w; x++)
                {
                    long dx = IntMath.Wrap(x - half, w);
                    if ((dx * dx) + (dy * dy) <= r2)
                    {
                        water[(y * w) + x] = false;
                    }
                }
            }

            // Расстояние до воды (4 стороны) до ShoreMax и пол.
            byte[] shore = t.Shore;
            var queue = new int[n];
            int qt = 0;
            for (int i = 0; i < n; i++)
            {
                shore[i] = water[i] ? (byte)0 : ShoreFar;
                if (water[i])
                {
                    queue[qt++] = i;
                    t.Floor[i] = FloorId.Water;
                }
            }

            int shift = IntMath.Log2(w);
            for (int qh = 0; qh < qt; qh++)
            {
                int u = queue[qh];
                int du = shore[u];
                if (du >= ShoreMax)
                {
                    continue;
                }

                int ux = u & mask;
                int uy = u >> shift;
                for (int k = 0; k < 4; k++)
                {
                    int vx = k == 0 ? (ux + 1) & mask : k == 1 ? (ux - 1) & mask : ux;
                    int vy = k == 2 ? (uy + 1) & mask : k == 3 ? (uy - 1) & mask : uy;
                    int v = (vy << shift) | vx;
                    if (shore[v] == ShoreFar)
                    {
                        shore[v] = (byte)(du + 1);
                        queue[qt++] = v;
                        int width = detail[v] < 26000 ? 1 : detail[v] < 38000 ? 2 : ShoreMax;
                        if (du + 1 <= width)
                        {
                            t.Floor[v] = FloorId.Sand;
                        }
                    }
                }
            }
        }

        // Водоёмы меньше MinLakeTiles (4 стороны) — убрать.
        private static void DropPuddles(bool[] water, int w)
        {
            int n = w * w;
            int mask = w - 1;
            int shift = IntMath.Log2(w);
            var seen = new bool[n];
            var queue = new int[n];
            for (int s = 0; s < n; s++)
            {
                if (!water[s] || seen[s])
                {
                    continue;
                }

                int qt = 0;
                queue[qt++] = s;
                seen[s] = true;
                for (int qh = 0; qh < qt; qh++)
                {
                    int u = queue[qh];
                    int ux = u & mask;
                    int uy = u >> shift;
                    for (int k = 0; k < 4; k++)
                    {
                        int vx = k == 0 ? (ux + 1) & mask : k == 1 ? (ux - 1) & mask : ux;
                        int vy = k == 2 ? (uy + 1) & mask : k == 3 ? (uy - 1) & mask : uy;
                        int v = (vy << shift) | vx;
                        if (water[v] && !seen[v])
                        {
                            seen[v] = true;
                            queue[qt++] = v;
                        }
                    }
                }

                if (qt < MinLakeTiles)
                {
                    for (int k = 0; k < qt; k++)
                    {
                        water[queue[k]] = false;
                    }
                }
            }
        }

        private static void Rivers(bool[] water, int w, ulong seed)
        {
            ulong rs = MacroGenerator.FieldSeed(seed, Stage, FieldRiver);
            ulong bits = SplitMix64.Hash(0, 0, rs);
            int axis0 = (int)(bits & 1);
            int count = 1 + (int)((bits >> 1) & 1);
            int mask = w - 1;
            int half = w >> 1;
            for (int r = 0; r < count; r++)
            {
                int axis = axis0 ^ r;
                int span = w - (2 * RiverAxisMinDistance);
                int baseLine = half + RiverAxisMinDistance + (int)((SplitMix64.Hash(r, 1, rs) >> 33) % (ulong)span);
                var o0 = new LatticeOctave1D(SplitMix64.Hash(r, 2, rs), w, w >> 1);
                var o1 = new LatticeOctave1D(SplitMix64.Hash(r, 3, rs), w, 128);
                var o2 = new LatticeOctave1D(SplitMix64.Hash(r, 4, rs), w, 32);
                var ow = new LatticeOctave1D(SplitMix64.Hash(r, 5, rs), w, 64);
                int phase = (int)((SplitMix64.Hash(r, 6, rs) >> 33) % FordEvery);
                int h1 = LatticeNoise.One >> 1;
                int Center(int s) =>
                    baseLine + (((o0.Sample(s) - h1) * 70) >> 15) + (((o1.Sample(s) - h1) * 36) >> 15) + (((o2.Sample(s) - h1) * 6) >> 15);

                for (int s = 0; s < w; s++)
                {
                    if ((s + phase) % FordEvery < FordWidth)
                    {
                        continue; // брод
                    }

                    int c0 = Center(s);
                    int c1 = Center((s + 1) & mask);
                    int hw = 1 + ((ow.Sample(s) * 3) >> 16);
                    int lo = Math.Min(c0, c1) - hw;
                    int hi = Math.Max(c0, c1) + hw;
                    for (int p = lo; p <= hi; p++)
                    {
                        int i = axis == 0 ? (s * w) + (p & mask) : ((p & mask) * w) + s;
                        water[i] = true;
                    }
                }
            }
        }
    }
}
