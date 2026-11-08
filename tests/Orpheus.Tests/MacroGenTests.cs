using System;
using System.Diagnostics;
using Orpheus.Core.Rng;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Генератор v2, этапы 1–2: макрокарта и поля на торе (карточка 1-015; docs/01, 2 «Поля шума», «Этапы», приёмка 2 п. 1, 6).
    /// Тест на 4096² — с префиксом D15_Macro_Big и [Trait("Category", "Big")]: CI его не гоняет, ночной прогон — гоняет.
    /// </summary>
    public class MacroGenTests
    {
        private readonly ITestOutputHelper _out;

        public MacroGenTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- Хэш: одно зерно — одна макрокарта (Debug и Release, `01` приёмка 2 п. 1) ----------

        /// <summary>
        /// Эталонные хэши макрокарты: тест зелёный и в Debug (CI), и в Release (<c>dotnet test -c Release</c>) —
        /// значит, обе сборки дают один хэш. Меняется результат генератора — меняются хэши и растёт
        /// <see cref="MacroGenerator.GeneratorVersion"/>.
        /// </summary>
        [Theory]
        [InlineData(1024, BiomeSet.Slice, 1UL, "0b78d9207ae20b399c7ae756f1b14064cb81218376b6cbe6797c75fd009d311b")]
        [InlineData(1024, BiomeSet.Full, 1UL, "57edddab4e682834ab74a54fc7277ab679aa0fb43fab15015a253f6e64980075")]
        [InlineData(2048, BiomeSet.Full, 1UL, "b11ec0893e7e096c58260b88d76da5932882eed751059a5926732466d6719174")]
        public void D15_Macro_GoldenHash(int size, BiomeSet set, ulong seed, string expected)
        {
            Assert.Equal(expected, MacroGenerator.Generate(size, seed, set).Map.HashHex());
        }

        [Fact]
        public void D15_Macro_SameSeedSameMap_OtherSeedOtherMap()
        {
            string a = MacroGenerator.Generate(1024, 77, BiomeSet.Slice).Map.HashHex();
            string b = MacroGenerator.Generate(1024, 77, BiomeSet.Slice).Map.HashHex();
            string c = MacroGenerator.Generate(1024, 78, BiomeSet.Slice).Map.HashHex();
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }

        [Fact]
        public void D15_Macro_BytesRoundTrip()
        {
            MacroMap map = MacroGenerator.Generate(1024, 5, BiomeSet.Full).Map;
            byte[] bytes = map.ToBytes();
            Assert.Equal(MacroMap.HeaderBytes + MacroMap.Layers * 128 * 128, bytes.Length);
            MacroMap back = MacroMap.FromBytes(bytes);
            Assert.Equal(map.HashHex(), back.HashHex());
            Assert.Equal(5UL, back.Seed);
            Assert.Equal(BiomeSet.Full, back.Set);
            Assert.Equal(MacroGenerator.GeneratorVersion, back.GeneratorVersion);
            bytes[3] = 0;
            Assert.Throws<FormatException>(() => MacroMap.FromBytes(bytes));
        }

        // ---------- Макрокарта и поля ----------

        [Theory]
        [InlineData(1024, 128)]
        [InlineData(4096, 512)]
        public void D15_Macro_Size_CellIs8Tiles(int world, int cells)
        {
            var map = new MacroMap(world, 1, MacroGenerator.GeneratorVersion, BiomeSet.Slice);
            Assert.Equal(cells, map.Size);
            Assert.Equal(cells * cells, map.H.Length);
            Assert.All(map.Biome, b => Assert.Equal(MacroMap.BiomeNone, b));
        }

        [Fact]
        public void D15_Macro_FieldsBytesAreHighBytes_AndCellCentreSample()
        {
            MacroWorld w = MacroGenerator.Generate(1024, 9, BiomeSet.Slice);
            FieldNoise h = MacroGenerator.CreateH(1024, 9);
            for (int i = 0; i < w.Map.H.Length; i++)
            {
                Assert.Equal(w.Fields.H[i] >> 8, w.Map.H[i]);
                Assert.Equal(w.Fields.M[i] >> 8, w.Map.M[i]);
                Assert.Equal(w.Fields.C[i] >> 8, w.Map.C[i]);
            }

            // Ячейка (x, y) — выборка в тайле (8x + 4, 8y + 4), как coords модели.
            Assert.Equal(h.Sample(8 * 17 + 4, 8 * 33 + 4), w.Fields.H[33 * 128 + 17]);
        }

        [Fact]
        public void D15_Macro_Noise_PeriodicOnTorus()
        {
            foreach (FieldNoise f in new[] { MacroGenerator.CreateH(1024, 3), MacroGenerator.CreateC(1024, 3), MacroGenerator.CreateDetail(1024, 3) })
            {
                for (int y = 0; y < 1024; y += 37)
                {
                    for (int x = 0; x < 1024; x += 11)
                    {
                        int v = f.Sample(x, y);
                        Assert.InRange(v, 0, LatticeNoise.One - 1);
                        Assert.Equal(v, f.Sample(x + 1024, y));
                        Assert.Equal(v, f.Sample(x - 1024, y + 2048));
                    }
                }
            }
        }

        /// <summary>
        /// `01` 2: период октавы — min(ячейка, W/2); на 1024² октава с ячейкой 1024 — решётка 2 × 2, не константа.
        /// </summary>
        [Fact]
        public void D15_Macro_Octave1024On1024_NotConstant()
        {
            FieldNoise h = MacroGenerator.CreateH(1024, 11);
            LatticeOctave2D o0 = h.Octaves[0];
            Assert.Equal(512, o0.Cell);
            Assert.Equal(2, o0.Nodes);
            int min = int.MaxValue;
            int max = int.MinValue;
            for (int seed = 1; seed <= 10; seed++)
            {
                LatticeOctave2D o = MacroGenerator.CreateH(1024, (ulong)seed).Octaves[0];
                int lo = int.MaxValue;
                int hi = int.MinValue;
                for (int y = 4; y < 1024; y += 8)
                {
                    for (int x = 4; x < 1024; x += 8)
                    {
                        int v = o.Sample(x, y);
                        lo = Math.Min(lo, v);
                        hi = Math.Max(hi, v);
                    }
                }

                Assert.True(hi - lo > LatticeNoise.One / 20, $"зерно {seed}: октава 1024 почти константа ({lo}…{hi})");
                min = Math.Min(min, lo);
                max = Math.Max(max, hi);
            }

            _out.WriteLine($"октава 1024 на 1024², 10 зёрен: {min}…{max} из 0…65535");
        }

        /// <summary>
        /// `01` приёмка 2 п. 6 на 30 зёрнах 1024²: 99-й процентиль |разницы| h, m, c соседних ячеек через шов.
        /// Шов лежит на узле каждой октавы (ячейки делят W), где квинтик плоский: там p99 в сотни раз меньше, чем у
        /// всех пар, — и в модели Python тоже. Поэтому проверка двойная: (а) шов не грубее всех пар — p99 шва ≤ 1,1 ×
        /// p99 всех; (б) шов против внутренней линии x = W/2 той же фазы решётки — отличие ≤ 10 % (допущение, `work/backlog.md` [док]).
        /// </summary>
        [Fact]
        public void D15_Macro_Seam_P99_30Seeds()
        {
            const int seeds = 30;
            const int m = 128;
            string[] names = { "h", "m", "c" };
            var all = new int[3][];
            var seam = new int[3][];
            var mid = new int[3][];
            for (int f = 0; f < 3; f++)
            {
                all[f] = new int[seeds * m * m * 2];
                seam[f] = new int[seeds * m * 2];
                mid[f] = new int[seeds * m * 2];
            }

            int na = 0;
            int ns = 0;
            for (int s = 1; s <= seeds; s++)
            {
                FieldData d = MacroGenerator.Generate(1024, (ulong)s, BiomeSet.Slice).Fields;
                ushort[][] fields = { d.H, d.M, d.C };
                for (int y = 0; y < m; y++)
                {
                    for (int x = 0; x < m; x++)
                    {
                        for (int f = 0; f < 3; f++)
                        {
                            ushort[] a = fields[f];
                            int v = a[y * m + x];
                            int dx = Math.Abs(a[y * m + ((x + 1) & (m - 1))] - v);
                            int dy = Math.Abs(a[(((y + 1) & (m - 1)) * m) + x] - v);
                            all[f][na] = dx;
                            all[f][na + 1] = dy;
                            if (x == m - 1)
                            {
                                seam[f][ns + 2 * y] = dx;
                            }

                            if (y == m - 1)
                            {
                                seam[f][ns + 2 * x + 1] = dy;
                            }

                            if (x == m / 2 - 1)
                            {
                                mid[f][ns + 2 * y] = dx;
                            }

                            if (y == m / 2 - 1)
                            {
                                mid[f][ns + 2 * x + 1] = dy;
                            }
                        }

                        na += 2;
                    }
                }

                ns += 2 * m;
            }

            for (int f = 0; f < 3; f++)
            {
                double pAll = P99(all[f]);
                double pSeam = P99(seam[f]);
                double pMid = P99(mid[f]);
                _out.WriteLine($"{names[f]}: p99 все {pAll}, шов {pSeam}, x = W/2 {pMid} (Q16)");
                Assert.True(pSeam <= 1.1 * pAll, $"{names[f]}: шов грубее всех пар ({pSeam} > 1,1 × {pAll})");
                Assert.True(Math.Abs(pSeam - pMid) <= 0.1 * Math.Max(pMid, 1), $"{names[f]}: шов {pSeam} против линии W/2 {pMid} — больше 10 %");
            }
        }

        private static double P99(int[] v)
        {
            var c = (int[])v.Clone();
            Array.Sort(c);
            return c[(int)((c.Length - 1) * 0.99)];
        }

        // ---------- Этап 1 ----------

        [Theory]
        [InlineData(1024)]
        [InlineData(2048)]
        public void D15_Macro_ShiftPeakIs350k(int w)
        {
            int[] s = MacroGenerator.Shift(w, 4, MacroGenerator.FieldShiftHot);
            int peak = 0;
            foreach (int v in s)
            {
                peak = Math.Max(peak, Math.Abs(v));
            }

            Assert.Equal((350 * w + 2048) / 4096, peak);
        }

        [Fact]
        public void D15_Macro_Rings_AndZones()
        {
            MacroWorld slice = MacroGenerator.Generate(1024, 2, BiomeSet.Slice);
            MacroWorld full = MacroGenerator.Generate(2048, 2, BiomeSet.Full);
            var rings = new int[4];
            for (int i = 0; i < slice.Map.Zone.Length; i++)
            {
                byte z = slice.Map.Zone[i];
                Assert.Equal(0, z & ~MacroMap.RingMask);                // в срезе поясов, Перекрёстка и Предвратия нет
                rings[z & MacroMap.RingMask]++;
            }

            Assert.Equal(0, rings[1]);                                   // на 1024² кольца 1 нет (`01` 1 «Края»)
            Assert.True(rings[0] > 0 && rings[2] > 0 && rings[3] > 0);
            int centre = slice.Map.IndexOf(64, 64);                      // ячейка у Очага: тайл (516, 516)
            Assert.Equal(5, slice.Zones.D[centre]);                      // ⌊√(4² + 4²)⌋
            Assert.Equal(0, slice.Map.RingAt(centre));

            int hot = 0, cold = 0, cross = 0, gate = 0;
            for (int i = 0; i < full.Map.Zone.Length; i++)
            {
                byte z = full.Map.Zone[i];
                bool h = (z & MacroMap.ZoneHot) != 0;
                bool c = (z & MacroMap.ZoneCold) != 0;
                hot += h ? 1 : 0;
                cold += c ? 1 : 0;
                if ((z & MacroMap.ZoneCrossroads) != 0)
                {
                    Assert.True(h && c);
                    cross++;
                }

                if ((z & MacroMap.ZonePregate) != 0)
                {
                    Assert.True(full.Zones.DGate[i] < MacroGenerator.PregateRadius);
                    gate++;
                }
            }

            int n = full.Map.Zone.Length;
            // Полоса |dH| < 300 на 2048² — 600 / 2048 = 29,3 % площади; холодная |dC| < 250 — 24,4 %.
            Assert.InRange(100.0 * hot / n, 28.5, 30.0);
            Assert.InRange(100.0 * cold / n, 23.7, 25.1);
            Assert.True(cross > 0 && gate > 0);
            _out.WriteLine($"2048²: жаркая {100.0 * hot / n:F2} %, холодная {100.0 * cold / n:F2} %, Перекрёсток {cross}, Предвратие {gate} ячеек");
        }

        // ---------- Целые помощники ----------

        [Fact]
        public void D15_Macro_IntMath()
        {
            var rng = new SplitMix64(42);
            for (int i = 0; i < 20000; i++)
            {
                long v = (long)(rng.Next() >> (i % 40 + 3));
                long r = IntMath.Isqrt(v);
                Assert.True(r * r <= v && (r + 1) * (r + 1) > v, $"isqrt({v}) = {r}");
            }

            Assert.Equal(0, IntMath.Isqrt(0));
            Assert.Equal(3, IntMath.Isqrt(15));
            Assert.Equal(4, IntMath.Isqrt(16));
            Assert.Equal(3, IntMath.DivRound(5, 2));
            Assert.Equal(2, IntMath.DivRound(7, 4));
            Assert.Equal(-3, IntMath.DivRound(-5, 2));
            Assert.Equal(-1, IntMath.Wrap(1023, 1024));
            Assert.Equal(-512, IntMath.Wrap(512, 1024));
            Assert.Equal(511, IntMath.Wrap(-513, 1024));
        }

        // ---------- Время (норма карточки: поля 1024² ≤ 0,5 с, 4096² ≤ 5 с в Release) ----------

        [Fact]
        public void D15_Macro_Time1024()
        {
            MacroGenerator.Generate(1024, 1, BiomeSet.Slice);
            var sw = Stopwatch.StartNew();
            MacroGenerator.Generate(1024, 2, BiomeSet.Slice);
            double ms = sw.Elapsed.TotalMilliseconds;
            _out.WriteLine($"1024², этапы 1–2: {ms:F1} мс");
            Assert.True(ms < 500, $"{ms:F0} мс");
        }

        [Fact]
        [Trait("Category", "Big")]
        public void D15_Macro_Big4096_TimeAndRepeat()
        {
            MacroGenerator.Generate(1024, 1, BiomeSet.Full);
            var sw = Stopwatch.StartNew();
            MacroWorld a = MacroGenerator.Generate(4096, 1, BiomeSet.Full);
            double ms = sw.Elapsed.TotalMilliseconds;
            string h1 = a.Map.HashHex();
            Assert.Equal(h1, MacroGenerator.Generate(4096, 1, BiomeSet.Full).Map.HashHex());
            Assert.Equal("b9f0af523fc9fb1f9a070584dd9772520004aeeaa373e61a95321de5a025a08d", h1);
            _out.WriteLine($"4096², этапы 1–2: {ms:F0} мс, хэш {h1}");
            Assert.True(ms < 5000, $"{ms:F0} мс");
        }
    }
}
