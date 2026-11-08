using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Server;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Генератор v1 (карточка 1-017; docs/01, 2 этапы 7, 11, 12, приёмка 2 п. 1–2; 3 «Луга», «Лес», «Горы», «Пещеры»):
    /// мир 1024² на тайлах вместо заглушки, ключ сервера <c>--gen</c>, старые миры заглушки. Тесты на 4096² — с префиксом
    /// D17_Gen_Big и [Trait("Category", "Big")].
    /// </summary>
    public class GenV1Tests : IDisposable
    {
        private const int W = 1024;
        private readonly ITestOutputHelper _out;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "orpheus-gen-" + Guid.NewGuid().ToString("N"));

        public GenV1Tests(ITestOutputHelper output)
        {
            _out = output;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, true);
                }
            }
            catch (IOException)
            {
            }
        }

        private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        // ---------- `01` приёмка 2 п. 1: повторяемость, эталон ----------

        /// <summary>
        /// Одно зерно дважды — одинаковые хэши макрокарты (после этапа 7) и всех чанков (SHA-256 снимка генерации и
        /// <see cref="StateHash.Generation"/>); эталоны — одинаковые в Debug (CI) и Release (<c>dotnet test -c Release</c>).
        /// Меняется результат — меняются эталоны и растёт <see cref="GeneratorV1.GeneratorVersion"/>.
        /// </summary>
        [Fact]
        public void D17_Gen_SameSeedTwice_SameHashes_Golden()
        {
            TileWorld a = GeneratorV1.BuildTiles(MapShape.World(W), 1);
            TileWorld b = GeneratorV1.BuildTiles(MapShape.World(W), 1);
            byte[] baseA = WorldSnapshot.WriteBase(GeneratorV1.ToModel(a));
            byte[] baseB = WorldSnapshot.WriteBase(new GeneratorV1().Generate(MapShape.World(W), 1));
            Assert.Equal(a.Macro.Map.HashHex(), b.Macro.Map.HashHex());
            Assert.Equal(baseA, baseB);
            WorldModel loaded = WorldSnapshot.LoadBase(baseA);
            Assert.Equal(GeneratorV1.GeneratorVersion, loaded.GeneratorVersion);
            Assert.Equal(1UL, loaded.Seed);
            ulong gen = StateHash.Generation(loaded);
            _out.WriteLine($"зерно 1: макрокарта {a.Macro.Map.HashHex()}, снимок {Sha(baseA)}, генерация {gen:x16}");
            Assert.Equal("912d8e607f4a3b7c2f9b8400c288dd72c4fbc873c3ee61d9303122a0fcb3f202", a.Macro.Map.HashHex());
            Assert.Equal("44b043a093e0bb9a9b3586c153c1bdd778ba9d346f4d8c7c19c965c909eaebae", Sha(baseA));
            Assert.Equal(0xdfb545018fe15c73UL, gen);

            TileWorld other = GeneratorV1.BuildTiles(MapShape.World(W), 2);
            Assert.NotEqual(StateHash.Generation(GeneratorV1.ToModel(other)), gen);
        }

        // ---------- `01` приёмка 2 п. 2: время ----------

        /// <summary>1024² целиком (этапы 1–12 и запись в чанки) — не дольше 3 с (норма `01`; цель — сильно меньше).</summary>
        [Fact]
        public void D17_Gen_Time1024_Under3s()
        {
            new GeneratorV1().Generate(MapShape.World(256), 1); // JIT
            var sw = Stopwatch.StartNew();
            WorldModel world = new GeneratorV1().Generate(MapShape.World(W), 3);
            double ms = sw.Elapsed.TotalMilliseconds;
            _out.WriteLine($"1024² целиком: {ms:F0} мс, чанков {world.ChunkCount}");
            Assert.Equal(world.TotalChunks, world.ChunkCount);
            Assert.True(ms < 3000, $"{ms:F0} мс");
        }

        // ---------- Числа приёмки 1-017 на 30 зёрнах ----------

        private sealed class SeedStats
        {
            public int Seed;
            public double MeadowTrees;
            public double ForestTrees;
            public int Copper;
            public double Reach;
            public double MountMass;
            public double CaveGranite;
            public double MaxShareDiff;
            public bool CopperStamped;
        }

        private static SeedStats Measure(int seed)
        {
            TileWorld t = GeneratorV1.BuildTiles(MapShape.World(W), (ulong)seed);
            var tiles = new long[Biome.Count];
            var trees = new long[Biome.Count];
            var mass = new long[Biome.Count];
            for (int i = 0; i < t.Biome.Length; i++)
            {
                byte b = t.Biome[i];
                tiles[b]++;
                ushort o = t.Objects[i];
                if (o == ObjectId.Tree)
                {
                    trees[b]++;
                }
                else if (o == ObjectId.Granite || (o == ObjectId.Rock && b == Biome.Mountain))
                {
                    mass[b]++;
                }
            }

            // Доли биомов тайлов против макрокарты: деталь двигает границу, но не долю.
            int[] cells = BiomeStage.CountCells(t.Macro.Map);
            double n = t.Biome.Length;
            double maxDiff = 0;
            foreach (byte b in new[] { Biome.Meadow, Biome.Forest, Biome.Mountain, Biome.Cave })
            {
                maxDiff = Math.Max(maxDiff, Math.Abs(100.0 * tiles[b] / n - 100.0 * cells[b] / (n / 64)));
            }

            return new SeedStats
            {
                Seed = seed,
                MeadowTrees = 1000.0 * trees[Biome.Meadow] / tiles[Biome.Meadow],
                ForestTrees = 1000.0 * trees[Biome.Forest] / tiles[Biome.Forest],
                Copper = HearthStage.CountCopper(t),
                Reach = ConnectStage.ReachableOpenShare(t, out _, out _),
                MountMass = 100.0 * mass[Biome.Mountain] / tiles[Biome.Mountain],
                CaveGranite = 100.0 * mass[Biome.Cave] / Math.Max(1, tiles[Biome.Cave]),
                MaxShareDiff = maxDiff,
                CopperStamped = t.CopperStamp.HasValue,
            };
        }

        /// <summary>
        /// 30 зёрен 1024²: деревья на лугах 6–10 на 1000 тайлов (заглушка — ~56); залежей меди в 600 от Очага ≥ 8;
        /// от Очага без копания достижимы ≥ 99 % проходимых тайлов лугов и леса; лес — ~120 на 1000; скальная масса гор ~45 %;
        /// доли биомов тайлов — в 1 п. п. от макрокарты.
        /// </summary>
        [Fact]
        public void D17_Gen_30Seeds_TreesCopperConnectivity()
        {
            SeedStats[] all = Enumerable.Range(1, 30).AsParallel().Select(Measure).OrderBy(s => s.Seed).ToArray();
            foreach (SeedStats s in all)
            {
                _out.WriteLine($"зерно {s.Seed}: деревья лугов {s.MeadowTrees:F2}, леса {s.ForestTrees:F1} на 1000; медь в 600 {s.Copper}" +
                               $"{(s.CopperStamped ? " (штамп)" : "")}; достижимо {100 * s.Reach:F3} %; масса гор {s.MountMass:F1} %, " +
                               $"гранит пещер {s.CaveGranite:F1} %; доли тайлов − макро ≤ {s.MaxShareDiff:F2} п. п.");
            }

            Assert.All(all, s => Assert.InRange(s.MeadowTrees, 6.0, 10.0));
            Assert.All(all, s => Assert.InRange(s.ForestTrees, 100.0, 140.0));
            Assert.All(all, s => Assert.True(s.Copper >= HearthStage.CopperNeeded, $"зерно {s.Seed}: меди {s.Copper}"));
            Assert.All(all, s => Assert.True(s.Reach >= 0.99, $"зерно {s.Seed}: достижимо {100 * s.Reach:F3} %"));
            Assert.All(all, s => Assert.InRange(s.MountMass, 40.0, 46.0));
            Assert.All(all, s => Assert.True(s.MaxShareDiff <= 1.0, $"зерно {s.Seed}: {s.MaxShareDiff:F2} п. п."));
        }

        // ---------- Этап 7: штампы ----------

        /// <summary>
        /// Мир без гор, пещер и леса (все ячейки — луга): своей меди нет — штамп даёт ≥ 8 залежей в 600 от Очага, и все они
        /// достижимы для кирки (соседний тайл каждой — в компоненте Очага); леса в 200 нет — штамп леса.
        /// </summary>
        [Fact]
        public void D17_Gen_Hearth_StampsCopperAndForest_WhenMissing()
        {
            MacroWorld macro = MacroGenerator.Generate(W, 5, BiomeSet.Slice);
            Array.Fill(macro.Map.Biome, Biome.Meadow);
            TileWorld t = GeneratorV1.BuildTiles(macro);
            Assert.True(t.CopperStamp.HasValue);
            Assert.True(t.ForestStamped);
            int copper = HearthStage.CountCopper(t);
            _out.WriteLine($"штамп меди в {t.CopperStamp}, залежей в 600: {copper}");
            Assert.True(copper >= HearthStage.CopperNeeded, $"меди {copper}");

            int[] label = ConnectStage.Label(t, out _);
            int main = label[(W / 2 * W) + (W / 2)];
            for (int i = 0; i < t.Objects.Length; i++)
            {
                if (t.Objects[i] != ObjectId.Ore)
                {
                    continue;
                }

                int x = i % W;
                int y = i / W;
                bool reachable = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }
                    .Any(d => label[(((y + d.Item2) & (W - 1)) * W) + ((x + d.Item1) & (W - 1))] == main);
                Assert.True(reachable, $"залежь ({x}, {y}) не подойти");
            }

            // Лес — в 200 от Очага по макрокарте, луга — в 150.
            int[] dist = macro.Zones.D;
            Assert.Contains(Enumerable.Range(0, dist.Length), i => dist[i] <= HearthStage.ForestWithin && macro.Map.Biome[i] == Biome.Forest);
            Assert.All(Enumerable.Range(0, dist.Length).Where(i => dist[i] <= HearthStage.MeadowRadius), i => Assert.Equal(Biome.Meadow, macro.Map.Biome[i]));
        }

        /// <summary>Своя медь есть (обычный мир) — штампа нет; стартовая зона 32 × 32 — трава без объектов; луга Ø 300.</summary>
        [Fact]
        public void D17_Gen_StartZone_ClearGrass_NoStampWhenOwnCopper()
        {
            TileWorld t = GeneratorV1.BuildTiles(MapShape.World(W), 1);
            Assert.False(t.CopperStamp.HasValue);
            int half = GeneratorV1.StartZoneSize / 2;
            for (int y = W / 2 - half; y < W / 2 + half; y++)
            {
                for (int x = W / 2 - half; x < W / 2 + half; x++)
                {
                    Assert.Equal(FloorId.Grass, t.Floor[y * W + x]);
                    Assert.Equal(ObjectId.Empty, t.Objects[y * W + x]);
                }
            }

            Assert.Contains(t.Objects, o => o != ObjectId.Empty);
        }

        // ---------- Этап 11: граница с деталью и шов ----------

        /// <summary>
        /// Биом тайла у швов (столбцы и строки 0…15 и W − 16…W − 1) = ячейка тайла, сдвинутого деталью, со сворачиванием по
        /// модулю W — независимый расчёт; плюс доля тайлов, чей биом отличается от соседа через шов, — как у соседей внутри мира
        /// (нет прямой линии на шве).
        /// </summary>
        [Fact]
        public void D17_Gen_TileBiome_WrapsAtSeams()
        {
            TileWorld t = GeneratorV1.BuildTiles(MapShape.World(W), 4);
            MacroMap map = t.Macro.Map;
            FieldNoise dx = TileStage.CreateDetailX(W, 4);
            FieldNoise dy = TileStage.CreateDetailY(W, 4);
            int Mod(int v) => ((v % W) + W) % W;
            int checkedTiles = 0;
            for (int a = 0; a < W; a++)
            {
                for (int k = -16; k < 16; k++)
                {
                    foreach ((int x, int y) in new[] { (Mod(k), a), (a, Mod(k)) })
                    {
                        int sx = Mod(x + TileStage.Shift(dx.Sample(x, y)));
                        int sy = Mod(y + TileStage.Shift(dy.Sample(x, y)));
                        Assert.Equal(map.Biome[(sy / 8 * map.Size) + (sx / 8)], t.Biome[y * W + x]);
                        checkedTiles++;
                    }
                }
            }

            // Граница через шов x = 0 не чаще, чем через средний столбец мира (прямой линии на шве нет).
            int seamDiff = 0;
            long allDiff = 0;
            for (int y = 0; y < W; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int d = t.Biome[y * W + x] != t.Biome[y * W + ((x + 1) & (W - 1))] ? 1 : 0;
                    allDiff += d;
                    seamDiff += x == W - 1 ? d : 0;
                }
            }

            double mean = (double)allDiff / W;
            _out.WriteLine($"проверено тайлов у швов {checkedTiles}; смен биома через шов x: {seamDiff}, в среднем через столбец: {mean:F1}");
            Assert.True(mean > 5, $"границ почти нет: {mean:F1}");
            Assert.True(seamDiff <= 3 * mean + 8, $"шов {seamDiff}, в среднем {mean:F1}");
        }

        /// <summary>Сдвиг деталью — −6…+6 с обрезкой, середина поля — 0.</summary>
        [Fact]
        public void D17_Gen_DetailShift_Range()
        {
            Assert.Equal(-TileStage.DetailShift, TileStage.Shift(0));
            Assert.Equal(TileStage.DetailShift, TileStage.Shift(LatticeNoise.One - 1));
            Assert.Equal(0, TileStage.Shift(LatticeNoise.One / 2));
            for (int v = 0; v < LatticeNoise.One; v += 97)
            {
                Assert.InRange(TileStage.Shift(v), -TileStage.DetailShift, TileStage.DetailShift);
            }
        }

        // ---------- Этап 12: связность ----------

        /// <summary>
        /// Квадрат лугов 24 × 24, обнесённый стеной гранита толщиной 6 на каменном полу, — отрезан; этап 12 прорубает к нему
        /// перевал шириной ≥ 4 через гранит; залежь меди в стене не прокапывается.
        /// </summary>
        [Fact]
        public void D17_Gen_Connect_CarvesPassThroughWall_KeepsOre()
        {
            MacroWorld macro = MacroGenerator.Generate(W, 6, BiomeSet.Slice);
            Array.Fill(macro.Map.Biome, Biome.Meadow);
            var t = new TileWorld(macro);
            HearthStage.RunMacro(macro);
            TileStage.Run(t);
            Array.Fill(t.Objects, ObjectId.Empty);
            int x0 = 100;
            int y0 = 100;
            int ore = -1;
            for (int y = y0 - 6; y < y0 + 30; y++)
            {
                for (int x = x0 - 6; x < x0 + 30; x++)
                {
                    bool inner = x >= x0 && x < x0 + 24 && y >= y0 && y < y0 + 24;
                    if (!inner)
                    {
                        t.Floor[y * W + x] = FloorId.Stone;
                        t.Objects[y * W + x] = ObjectId.Granite;
                    }
                }
            }

            for (int y = y0 - 6; y < y0 + 30; y++)
            {
                t.Objects[y * W + x0 + 26] = ObjectId.Ore; // жила поперёк восточной стены
                ore = y * W + x0 + 26;
            }

            Assert.True(ConnectStage.ReachableOpenShare(t, out _, out _) < 1.0);
            ConnectStage.Run(t);
            Assert.Equal(1.0, ConnectStage.ReachableOpenShare(t, out int open, out int reached));
            Assert.Equal(1, t.ComponentsJoined);
            Assert.True(t.TilesCarved >= 4 * 6, $"прорублено {t.TilesCarved}");
            Assert.Equal(ObjectId.Ore, t.Objects[ore]);
            Assert.Equal(36, Enumerable.Range(y0 - 6, 36).Count(y => t.Objects[y * W + x0 + 26] == ObjectId.Ore));

            // Перевал шириной ≥ 4: в стене есть 4 соседних чистых столбца (или строки) насквозь.
            bool wide = false;
            for (int s = y0 - 6; s + 3 < y0 + 30 && !wide; s++)
            {
                wide = Enumerable.Range(s, 4).All(y => Enumerable.Range(x0 - 6, 6).All(x => t.Objects[y * W + x] == ObjectId.Empty))
                       || Enumerable.Range(s, 4).All(x => Enumerable.Range(y0 - 6, 6).All(y => t.Objects[y * W + x] == ObjectId.Empty))
                       || Enumerable.Range(s, 4).All(x => Enumerable.Range(y0 + 24, 6).All(y => t.Objects[y * W + x] == ObjectId.Empty));
            }

            _out.WriteLine($"прорублено {t.TilesCarved} тайлов; лугов достижимо {reached} из {open}");
            Assert.True(wide, "перевал уже 4");
        }

        // ---------- Этап 3 на 4096² (замечание П по 1-016) ----------

        /// <summary>
        /// Срез на 4096² (макрокарта 512² = 2¹⁸ ячеек — больше 2¹⁶): доли точно по таблице; верхняя четверть h — ровно N/4 ячеек
        /// и все пещеры в ней. Мутация «16 бит под индекс в ключе» ломает разметку здесь.
        /// </summary>
        [Fact]
        public void D17_Biome_Slice4096_ExactShares()
        {
            MacroWorld world = MacroGenerator.Generate(4096, 1, BiomeSet.Slice);
            int n = world.Map.Size * world.Map.Size;
            int[] cnt = BiomeStage.CountCells(world.Map);
            Assert.Equal(BiomeStage.ShareCount(8, n), cnt[Biome.Cave]);
            Assert.Equal(BiomeStage.ShareCount(14, n), cnt[Biome.Mountain]);
            Assert.Equal(BiomeStage.ShareCount(33, n), cnt[Biome.Forest]);
            Assert.Equal(n - cnt[Biome.Cave] - cnt[Biome.Mountain] - cnt[Biome.Forest], cnt[Biome.Meadow]);
            bool[] upper = BiomeStage.UpperQuarter(world.Fields.H, new long[n]);
            Assert.Equal(BiomeStage.ShareCount(25, n), upper.Count(u => u));
            for (int i = 0; i < n; i++)
            {
                if (world.Map.Biome[i] == Biome.Cave)
                {
                    Assert.True(upper[i]);
                }
            }

            // Горы — наибольшая h среди не пещер: минимальная h гор ≥ максимальной h лугов и леса.
            int minMount = Enumerable.Range(0, n).Where(i => world.Map.Biome[i] == Biome.Mountain).Min(i => world.Fields.H[i]);
            int maxRest = Enumerable.Range(0, n).Where(i => world.Map.Biome[i] == Biome.Meadow || world.Map.Biome[i] == Biome.Forest).Max(i => world.Fields.H[i]);
            Assert.True(minMount >= maxRest, $"горы от {minMount}, луга и лес до {maxRest}");
        }

        /// <summary>Ключ сортировки держит индекс до 2²⁰ − 1 (8192², макрокарта 1024²): индекс читается обратно, порядок — по значению.</summary>
        [Fact]
        public void D17_Biome_SortKey_Index20Bits()
        {
            const int big = (1 << 20) - 1;
            foreach (bool largest in new[] { true, false })
            {
                foreach (ushort v in new ushort[] { 0, 1, 65534, 65535 })
                {
                    Assert.Equal(big, (int)(BiomeStage.SortKey(v, big, largest) & 0xFFFFFFFF));
                    Assert.Equal(70000, (int)(BiomeStage.SortKey(v, 70000, largest) & 0xFFFFFFFF));
                }
            }

            Assert.True(BiomeStage.SortKey(65535, big, largest: true) < BiomeStage.SortKey(65534, 0, largest: true));
            Assert.True(BiomeStage.SortKey(0, big, largest: false) < BiomeStage.SortKey(1, 0, largest: false));
            Assert.True(BiomeStage.SortKey(7, 65535, largest: false) < BiomeStage.SortKey(7, 65536, largest: false));
        }

        /// <summary>Таблица среза — только чтение (замечание П по 1-016): не массив, запись — исключение.</summary>
        [Fact]
        public void D17_Biome_SliceTable_ReadOnly()
        {
            Assert.False(BiomeTables.Slice is BiomeRow[]);
            var list = Assert.IsAssignableFrom<IList<BiomeRow>>(BiomeTables.Slice);
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list[0] = BiomeRow.Rest(Biome.Meadow));
            Assert.Equal(4, BiomeTables.Slice.Count);
            Assert.Equal(Biome.Cave, BiomeTables.Slice[0].Biome);
        }

        // ---------- Сервер: --gen, сохранение, старые миры ----------

        private ServerResult Run(params string[] args) => ServerRun.Run(ServerOptions.Parse(args), null);

        [Fact]
        public void D17_Server_GenKey_ParseAndDefault()
        {
            Assert.Equal("v1", ServerOptions.Parse(new[] { "--create" }).Gen);
            Assert.Equal("stub", ServerOptions.Parse(new[] { "--create", "--gen", "stub" }).Gen);
            Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--gen", "v9" }));
            Assert.Contains("--gen", Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--sim", "--replay", "x.bin", "--gen", "v1" })).Message);
            Assert.Same(WorldGenModule.Current, WorldGenModule.ForVersion(GeneratorV1.GeneratorVersion));
            Assert.Same(WorldGenModule.Stub, WorldGenModule.ForVersion(StubGenerator.GeneratorVersion));
            Assert.Null(WorldGenModule.ForVersion(2));
        }

        /// <summary>
        /// Сервер с <c>--gen v1</c>: мир v1 в снимке генерации; сохранение и загрузка — хэш совпадает; повтор журнала без папки
        /// мира — тот же мир по версии из журнала.
        /// </summary>
        [Fact]
        public void D17_Server_GenV1_SaveLoadSameHash_ReplayFromJournal()
        {
            string world = Path.Combine(_dir, "v1");
            string journal = Path.Combine(_dir, "v1.bin");
            ServerResult first = Run("--world", world, "--create", "--gen", "v1", "--seed", "11", "--size", "1024", "--sim",
                "--bots", "4:wander", "--ticks", "900", "--hash", "--record", journal);
            Assert.Equal(0, first.ExitCode);
            WorldModel created = WorldSnapshot.LoadBase(File.ReadAllBytes(Path.Combine(world, ServerRun.BaseFileName)));
            Assert.Equal(GeneratorV1.GeneratorVersion, created.GeneratorVersion);
            Assert.Equal(StateHash.Generation(new GeneratorV1().Generate(MapShape.World(1024), 11)), StateHash.Generation(created));

            ServerResult second = Run("--world", world, "--sim", "--ticks", "0");
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(900, second.StartTick);
            Assert.Equal(first.FinalHash, second.LoadedHash);
            Assert.Equal(first.FinalHash, second.FinalHash);

            ServerResult rep = Run("--sim", "--replay", journal, "--hash");
            Assert.Equal(0, rep.ExitCode);
            Assert.Equal(-1, rep.DivergedTick);
            Assert.True(rep.CheckpointsVerified > 0);
            _out.WriteLine($"v1: хэш после 900 тактов {first.FinalHash:x16}, загружен {second.LoadedHash:x16}, сверок повтора {rep.CheckpointsVerified}");
        }

        /// <summary>
        /// Старый мир (заглушка, версия 1) грузится как раньше сервером, у которого по умолчанию v1: снимок читается с диска со
        /// своей версией, генератор не зовётся; журнал заглушки повторяется заглушкой.
        /// </summary>
        [Fact]
        public void D17_Server_OldStubWorld_LoadsAsBefore()
        {
            string world = Path.Combine(_dir, "stub");
            string journal = Path.Combine(_dir, "stub.bin");
            ServerResult first = Run("--world", world, "--create", "--gen", "stub", "--seed", "12", "--size", "1024", "--sim",
                "--bots", "4:wander", "--ticks", "600", "--hash", "--record", journal);
            Assert.Equal(0, first.ExitCode);
            byte[] baseBytes = File.ReadAllBytes(Path.Combine(world, ServerRun.BaseFileName));
            Assert.Equal(StubGenerator.GeneratorVersion, WorldSnapshot.LoadBase(baseBytes).GeneratorVersion);

            ServerResult second = Run("--world", world, "--sim", "--bots", "4:wander", "--ticks", "300");
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(first.FinalHash, second.LoadedHash);
            Assert.Equal(baseBytes, File.ReadAllBytes(Path.Combine(world, ServerRun.BaseFileName)));

            ServerResult rep = Run("--sim", "--replay", journal, "--hash");
            Assert.Equal(0, rep.ExitCode);
            Assert.Equal(-1, rep.DivergedTick);
            Assert.True(rep.CheckpointsVerified > 0);
        }

        // ---------- 4096² (ночной прогон) ----------

        /// <summary>4096² набора «срез» целиком: замер в карточку (норма полного набора ≤ 30 с — не цель вехи 1), повтор — тот же хэш.</summary>
        [Fact]
        [Trait("Category", "Big")]
        public void D17_Gen_Big4096_TimeAndRepeat()
        {
            new GeneratorV1().Generate(MapShape.World(256), 1);
            var sw = Stopwatch.StartNew();
            WorldModel a = new GeneratorV1().Generate(MapShape.World(4096), 1);
            double ms = sw.Elapsed.TotalMilliseconds;
            ulong h = StateHash.Generation(a);
            Assert.Equal(h, StateHash.Generation(new GeneratorV1().Generate(MapShape.World(4096), 1)));
            _out.WriteLine($"4096² целиком: {ms:F0} мс, генерация {h:x16}");
            Assert.True(ms < 30000, $"{ms:F0} мс");
        }
    }
}
