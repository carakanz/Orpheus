using System;
using System.Linq;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Генератор v2, этап 3 «Биомы долями», набор «срез» (карточка 1-016; docs/01, 2 «Таблица биомов», «Набор «срез»»,
    /// приёмка 2 п. 4–5; docs/08 приёмка 1.8). Сверка долей с моделью Python — <c>tools/gen_preview/compare_biomes.py</c>
    /// (таблица в карточке); здесь — правила таблицы, равные ключи и эталонные хэши.
    /// </summary>
    public class BiomeStageTests
    {
        private readonly ITestOutputHelper _out;

        public BiomeStageTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- Независимая эталонная реализация таблицы «срез» прямо по тексту `01` 2 ----------

        /// <summary>
        /// Срез по документу, без кода генератора: верхняя четверть h — N/4 ячеек с наибольшей h (равные — по индексу);
        /// пещеры — 8 % N из неё по наибольшей c, горы — 14 % по наибольшей h из оставшихся, лес — 33 % по наибольшей m
        /// из оставшихся, луга — остаток. Доля — round(% · N / 100), половина вверх.
        /// </summary>
        private static byte[] ReferenceSlice(FieldData f, int n)
        {
            var b = Enumerable.Repeat(MacroMap.BiomeNone, n).ToArray();
            int Count(int pct) => (pct * n + 50) / 100;
            var upper = Enumerable.Range(0, n).OrderByDescending(i => f.H[i]).ThenBy(i => i).Take((n + 2) / 4).ToHashSet();
            void Take(byte biome, int pct, Func<int, bool> allowed, ushort[] key)
            {
                foreach (int i in Enumerable.Range(0, n).Where(i => b[i] == MacroMap.BiomeNone && allowed(i))
                             .OrderByDescending(i => key[i]).ThenBy(i => i).Take(Count(pct)).ToList())
                {
                    b[i] = biome;
                }
            }

            Take(Biome.Cave, 8, upper.Contains, f.C);
            Take(Biome.Mountain, 14, _ => true, f.H);
            Take(Biome.Forest, 33, _ => true, f.M);
            for (int i = 0; i < n; i++)
            {
                if (b[i] == MacroMap.BiomeNone)
                {
                    b[i] = Biome.Meadow;
                }
            }

            return b;
        }

        [Theory]
        [InlineData(1024, 1, 12)]
        [InlineData(256, 1, 30)]
        [InlineData(2048, 5, 1)]
        public void D16_Biome_Slice_MatchesReference(int size, int seedFrom, int seeds)
        {
            for (int s = seedFrom; s < seedFrom + seeds; s++)
            {
                MacroWorld w = MacroGenerator.Generate(size, (ulong)s, BiomeSet.Slice);
                byte[] want = ReferenceSlice(w.Fields, w.Map.Biome.Length);
                Assert.True(want.SequenceEqual(w.Map.Biome), $"{size}², зерно {s}: этап 3 не совпал с таблицей `01`");
            }
        }

        // ---------- Смысл строк (не зависит от разрешения равных ключей) ----------

        /// <summary>
        /// На 30 зёрнах 1024²: пещеры — только в верхней четверти h, и c у любой пещеры не меньше, чем у любой
        /// не-пещеры той же четверти; h у гор не меньше, чем у леса и лугов; m у леса не меньше, чем у лугов.
        /// </summary>
        [Fact]
        public void D16_Biome_Slice_RowSemantics_30Seeds()
        {
            for (ulong s = 1; s <= 30; s++)
            {
                MacroWorld w = MacroGenerator.Generate(1024, s, BiomeSet.Slice);
                byte[] b = w.Map.Biome;
                FieldData f = w.Fields;
                int n = b.Length;
                int[] byH = Enumerable.Range(0, n).OrderByDescending(i => f.H[i]).ToArray();
                int hQuarterMin = f.H[byH[n / 4 - 1]];
                int caveMinC = int.MaxValue, upperOtherMaxC = -1;
                int mountMinH = int.MaxValue, lowerMaxH = -1;
                int forestMinM = int.MaxValue, meadowMaxM = -1;
                for (int i = 0; i < n; i++)
                {
                    switch (b[i])
                    {
                        case Biome.Cave:
                            Assert.True(f.H[i] >= hQuarterMin, $"зерно {s}: пещера вне верхней четверти h");
                            caveMinC = Math.Min(caveMinC, f.C[i]);
                            break;
                        case Biome.Mountain:
                            mountMinH = Math.Min(mountMinH, f.H[i]);
                            break;
                        case Biome.Forest:
                            forestMinM = Math.Min(forestMinM, f.M[i]);
                            lowerMaxH = Math.Max(lowerMaxH, f.H[i]);
                            break;
                        case Biome.Meadow:
                            meadowMaxM = Math.Max(meadowMaxM, f.M[i]);
                            lowerMaxH = Math.Max(lowerMaxH, f.H[i]);
                            break;
                        default:
                            throw new Xunit.Sdk.XunitException($"зерно {s}: биом {b[i]} не из набора «срез»");
                    }

                    if (b[i] != Biome.Cave && f.H[i] > hQuarterMin)
                    {
                        upperOtherMaxC = Math.Max(upperOtherMaxC, f.C[i]);
                    }
                }

                Assert.True(caveMinC >= upperOtherMaxC, $"зерно {s}: c пещер {caveMinC} < {upperOtherMaxC} у другой ячейки верхней четверти");
                Assert.True(mountMinH >= lowerMaxH, $"зерно {s}: h гор {mountMinH} < {lowerMaxH}");
                Assert.True(forestMinM >= meadowMaxM, $"зерно {s}: m леса {forestMinM} < {meadowMaxM}");
            }
        }

        // ---------- Доли: `01` 2 п. 4 (часть), п. 5; `08` 1.8 ----------

        /// <summary>
        /// На 100 зёрнах 1024²: все ячейки размечены, все 4 биома есть, доля каждого — ровно round(% · N / 100)
        /// (тем более в допуске `01` 3: ±25 % доли, но не уже ±1 п. п.).
        /// </summary>
        [Fact]
        public void D16_Biome_Slice_Shares_100Seeds()
        {
            const int n = 128 * 128;
            for (ulong s = 1; s <= 100; s++)
            {
                int[] cnt = BiomeStage.CountCells(MacroGenerator.Generate(1024, s, BiomeSet.Slice).Map);
                Assert.Equal(0, cnt[Biome.Count]);
                Assert.Equal(1311, cnt[Biome.Cave]);      // 8 % от 16 384 = 1310,72
                Assert.Equal(2294, cnt[Biome.Mountain]);  // 14 % = 2293,76
                Assert.Equal(5407, cnt[Biome.Forest]);    // 33 % = 5406,72
                Assert.Equal(n - 1311 - 2294 - 5407, cnt[Biome.Meadow]);
                foreach ((byte biome, int pct) in new[] { (Biome.Cave, 8), (Biome.Mountain, 14), (Biome.Forest, 33), (Biome.Meadow, 45) })
                {
                    double share = 100.0 * cnt[biome] / n;
                    Assert.True(Math.Abs(share - pct) <= Math.Max(1.0, 0.25 * pct), $"зерно {s}: биом {biome} {share:F2} %");
                }
            }
        }

        [Fact]
        public void D16_Biome_FullSet_NotYet_LeavesNone()
        {
            Assert.All(MacroGenerator.Generate(1024, 1, BiomeSet.Full).Map.Biome, b => Assert.Equal(MacroMap.BiomeNone, b));
        }

        // ---------- Равные ключи: побеждает меньший индекс ----------

        /// <summary>
        /// Все поля равны: верхняя четверть h — индексы 0…255 (N = 1024), пещеры — первые 82, горы — следующие 143,
        /// лес — следующие 338, луга — остальное. Хэш результата — эталонный: одинаков на любой машине.
        /// </summary>
        [Fact]
        public void D16_Biome_EqualKeys_ResolvedByIndex()
        {
            var map = new MacroMap(256, 1, MacroGenerator.GeneratorVersion, BiomeSet.Slice);
            int n = map.Size * map.Size;
            var f = new FieldData(map.Size);
            Array.Fill(f.H, (ushort)30000);
            Array.Fill(f.M, (ushort)30000);
            Array.Fill(f.C, (ushort)30000);
            BiomeStage.Run(map, f);
            for (int i = 0; i < n; i++)
            {
                byte want = i < 82 ? Biome.Cave : i < 82 + 143 ? Biome.Mountain : i < 82 + 143 + 338 ? Biome.Forest : Biome.Meadow;
                Assert.Equal(want, map.Biome[i]);
            }

            // Частично равные: h — две ступени (чётные выше), c и m — одна величина. Верхняя четверть — первые 256 чётных
            // индексов; пещеры — первые 82 из них, горы — следующие чётные по индексу.
            Array.Fill(f.H, (ushort)1000);
            for (int i = 0; i < n; i += 2)
            {
                f.H[i] = 2000;
            }

            BiomeStage.Run(map, f);
            for (int j = 0; j < 82; j++)
            {
                Assert.Equal(Biome.Cave, map.Biome[2 * j]);
            }

            for (int j = 82; j < 82 + 143; j++)
            {
                Assert.Equal(Biome.Mountain, map.Biome[2 * j]);
            }

            Assert.Equal(Biome.Forest, map.Biome[2 * 225]);
            Assert.Equal(Biome.Forest, map.Biome[1]);
            _out.WriteLine($"равные ключи: хэш {map.HashHex()}");
            Assert.Equal("1594a78983be27debd3cdf9c6db7c7a42532bb6d9fb3b17cea6e69fc8d6f0e82", map.HashHex());
        }

        [Fact]
        public void D16_Biome_SortKey_TotalOrder()
        {
            Assert.True(BiomeStage.SortKey(500, 7, largest: true) < BiomeStage.SortKey(400, 0, largest: true));
            Assert.True(BiomeStage.SortKey(400, 0, largest: false) < BiomeStage.SortKey(500, 7, largest: false));
            Assert.True(BiomeStage.SortKey(500, 3, largest: true) < BiomeStage.SortKey(500, 4, largest: true));
            Assert.True(BiomeStage.SortKey(500, 3, largest: false) < BiomeStage.SortKey(500, 4, largest: false));
            Assert.Equal(82, BiomeStage.ShareCount(8, 1024));
            Assert.Equal(1311, BiomeStage.ShareCount(8, 16384));
            Assert.Equal(1, BiomeStage.ShareCount(50, 1));  // половина — вверх
        }

        // ---------- Эталонный хэш полей в полной точности (замечание П по 1-015: край без теста) ----------

        /// <summary>
        /// SHA-256 полей H, M, C и края в Q16 — ловит любое изменение шума, включая поле края, которого нет в хэше макрокарты.
        /// Зелёный в Debug и Release — значит, обе сборки дают одни поля.
        /// </summary>
        [Theory]
        [InlineData(1024, BiomeSet.Slice, 1UL, "d47682c8ea9c29432ecaae54d0b3ad8dcf23210ab686a6369e349142244a49de")]
        [InlineData(2048, BiomeSet.Full, 1UL, "7b386117a0c40c6188b7c6d895700fe63863601a499c6d9da36ebcf0ea1f451b")]
        public void D16_Fields_GoldenHash(int size, BiomeSet set, ulong seed, string expected)
        {
            string got = MacroGenerator.Generate(size, seed, set).Fields.HashHex();
            _out.WriteLine($"{size}² {set} {seed}: {got}");
            Assert.Equal(expected, got);
        }
    }
}
