using System;
using System.Diagnostics;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Приёмка генератора-заглушки (карточка 0-007; docs/07 3.4, 11.5; docs/01 2 — этап полей).
    /// Тест на 4096² — с префиксом D00_Gen_Big и [Trait("Category", "Big")]: CI его не гоняет, ночной прогон — гоняет.
    /// </summary>
    public class WorldGenTests
    {
        private const ulong Seed = 0x0E5_0007UL;
        private const int Small = 1024;

        // Один мир 1024² на класс: тесты долей, стартовой зоны и объектов читают его, не меняя.
        private static readonly Lazy<WorldModel> SmallWorld =
            new Lazy<WorldModel>(() => new StubGenerator().Generate(MapShape.World(Small), Seed));

        private readonly ITestOutputHelper _out;

        public WorldGenTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- Шум ----------

        [Theory]
        [InlineData(1024, 1024, 128, 4)]
        [InlineData(256, 64, 128, 3)]
        [InlineData(4096, 4096, 64, 3)]
        public void D00_Gen_Noise_PeriodicOnTorus_RowEqualsSample(int w, int h, int cell, int octaves)
        {
            var noise = new TorusNoise(Seed, w, h, cell, octaves);
            var row = new int[w];
            var scratch = new int[noise.ScratchLength];
            int[] ys = { 0, 1, h / 2 - 1, h - 1 };
            foreach (int y in ys)
            {
                noise.FillRow(y, row, scratch);
                for (int x = 0; x < w; x++)
                {
                    int v = noise.Sample(x, y);
                    Assert.Equal(v, row[x]);
                    Assert.InRange(v, 0, noise.MaxValue);
                    Assert.Equal(v, noise.Sample(x + w, y));
                    Assert.Equal(v, noise.Sample(x - w, y + h));
                    Assert.Equal(v, noise.Sample(x, y - 3 * h));
                }
            }
        }

        [Fact]
        public void D00_Gen_Noise_NotDegenerate_OctaveCellCappedAtHalfWorld()
        {
            // `01` 2: период октавы — min(ячейка, W/2); иначе на малом мире октава — константа.
            var noise = new TorusNoise(Seed, 64, 64, 1024, 1);
            int min = int.MaxValue;
            int max = int.MinValue;
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    int v = noise.Sample(x, y);
                    min = Math.Min(min, v);
                    max = Math.Max(max, v);
                }
            }

            Assert.True(max - min > 1000, $"Поле почти константа: {min}…{max}.");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void D00_Gen_Seam_DiffsAcrossSeam_LikeInside(bool rockField)
        {
            // Шов X = W−1 → 0 и шов Y = H−1 → 0 лежат на узле всех октав. Сравниваем с такими же внутренними
            // границами ячеек базовой октавы (тот же «фазовый» случай: smoothstep на узле): среднее и максимум
            // |разности соседей| через шов — в том же распределении, что внутри.
            MapShape shape = MapShape.World(Small);
            TorusNoise noise = rockField
                ? StubGenerator.CreateRockNoise(shape, Seed)
                : StubGenerator.CreateDensityNoise(shape, Seed);
            int cell = rockField ? StubGenerator.RockCell : StubGenerator.DensityCell;
            int w = shape.Width;

            Diffs(noise, w, w - 1, out double seamMeanX, out int seamMaxX, alongX: true);
            Diffs(noise, w, w - 1, out double seamMeanY, out int seamMaxY, alongX: false);

            double insideSum = 0;
            int insideMax = 0;
            int boundaries = 0;
            for (int b = cell; b < w; b += cell)
            {
                Diffs(noise, w, b - 1, out double mx, out int xx, alongX: true);
                Diffs(noise, w, b - 1, out double my, out int xy, alongX: false);
                insideSum += mx + my;
                insideMax = Math.Max(insideMax, Math.Max(xx, xy));
                boundaries += 2;
            }

            double insideMean = insideSum / boundaries;
            _out.WriteLine($"{(rockField ? "скалы" : "плотность")}: через шов X среднее {seamMeanX:F1} макс {seamMaxX}, " +
                $"Y {seamMeanY:F1} / {seamMaxY}; внутри на границах ячеек среднее {insideMean:F1} макс {insideMax}");

            Assert.True(insideMean > 0);
            Assert.InRange(seamMeanX / insideMean, 0.6, 1.6);
            Assert.InRange(seamMeanY / insideMean, 0.6, 1.6);
            Assert.True(seamMaxX <= insideMax * 1.5, $"Скачок через шов X: {seamMaxX} при максимуме внутри {insideMax}.");
            Assert.True(seamMaxY <= insideMax * 1.5, $"Скачок через шов Y: {seamMaxY} при максимуме внутри {insideMax}.");
        }

        // Разности |n(a) − n(a+1)| по линии a → a+1 (через шов, если a = W−1) вдоль всей другой оси.
        private static void Diffs(TorusNoise noise, int size, int a, out double mean, out int max, bool alongX)
        {
            long sum = 0;
            max = 0;
            for (int t = 0; t < size; t++)
            {
                int d = alongX
                    ? Math.Abs(noise.Sample(a, t) - noise.Sample(a + 1, t))
                    : Math.Abs(noise.Sample(t, a) - noise.Sample(t, a + 1));
                sum += d;
                max = Math.Max(max, d);
            }

            mean = sum / (double)size;
        }

        // ---------- Повторяемость ----------

        [Fact]
        public void D00_Gen_SameSeed_ByteIdenticalSnapshot()
        {
            byte[] a = WorldSnapshot.WriteBase(new StubGenerator().Generate(MapShape.World(Small), Seed));
            byte[] b = WorldSnapshot.WriteBase(new StubGenerator().Generate(MapShape.World(Small), Seed));
            Assert.Equal(a, b);

            WorldModel loaded = WorldSnapshot.LoadBase(a);
            Assert.Equal(StubGenerator.GeneratorVersion, loaded.GeneratorVersion);
            Assert.Equal(WorldGenModule.GeneratorVersion, loaded.GeneratorVersion);
            Assert.Equal(Seed, loaded.Seed);
            Assert.Equal(Small * Small / Chunk.TileCount, loaded.ChunkCount);
            Assert.Equal(WorldSnapshot.StoredHash(a), WorldSnapshot.ContentHash(loaded));
            Assert.Empty(loaded.GetChangedSinceBase());
            _out.WriteLine($"снимок генерации 1024²: {a.Length} байт ({a.Length / 1048576.0:F2} МБ)");
        }

        [Fact]
        public void D00_Gen_DifferentSeeds_DifferentWorlds()
        {
            MapShape shape = MapShape.World(256);
            var gen = new StubGenerator();
            WorldModel a = gen.Generate(shape, 1);
            WorldModel b = gen.Generate(shape, 2);
            WorldModel c = gen.Generate(shape, Seed);
            Assert.NotEqual(WorldSnapshot.ContentHash(a), WorldSnapshot.ContentHash(b));
            Assert.NotEqual(WorldSnapshot.ContentHash(a), WorldSnapshot.ContentHash(c));
            Assert.NotEqual(WorldSnapshot.ContentHash(b), WorldSnapshot.ContentHash(c));

            int differentFloor = 0;
            for (int y = 0; y < shape.Height; y++)
            {
                for (int x = 0; x < shape.Width; x++)
                {
                    if (a.GetFloor(new TileCoord(x, y)) != b.GetFloor(new TileCoord(x, y)))
                    {
                        differentFloor++;
                    }
                }
            }

            // Два независимых поля с долей 25 %: несовпадение пола ≈ 2 · 0,25 · 0,75 = 37,5 %.
            Assert.True(differentFloor > shape.Width * shape.Height / 10, $"Миры почти одинаковы: различий пола {differentFloor}.");
        }

        // ---------- Поля, стартовая зона, объекты ----------

        [Theory]
        [InlineData(0x0E5_0007UL)]
        [InlineData(1UL)]
        [InlineData(0xDEADBEEFUL)]
        public void D00_Gen_RockShare_Within3Percent(ulong seed)
        {
            WorldModel world = seed == Seed ? SmallWorld.Value : new StubGenerator().Generate(MapShape.World(Small), seed);
            Count(world, out int stone, out int grass, out int trees, out int rocks);
            double share = 100.0 * stone / (Small * Small);
            _out.WriteLine($"зерно {seed:X}: скалы {share:F2} %, луга {100.0 * grass / (Small * Small):F2} %, деревьев {trees}, скал-объектов {rocks}");
            Assert.InRange(share, StubGenerator.RockSharePercent - 3.0, StubGenerator.RockSharePercent + 3.0);
            Assert.Equal(Small * Small, stone + grass);
            Assert.True(trees > 0 && rocks > 0);
        }

        [Fact]
        public void D00_Gen_StartZone_32x32_AtCenter_IsClean()
        {
            WorldModel world = SmallWorld.Value;
            int half = StubGenerator.StartZoneSize / 2;
            int inZone = 0;
            for (int y = Small / 2 - half; y < Small / 2 + half; y++)
            {
                for (int x = Small / 2 - half; x < Small / 2 + half; x++)
                {
                    var t = new TileCoord(x, y);
                    Assert.Equal(FloorId.Grass, world.GetFloor(t));
                    Assert.Equal(ObjectId.Empty, world.GetObject(t));
                    Assert.True(StubGenerator.InStartZone(world.Shape, x, y));
                    inZone++;
                }
            }

            Assert.Equal(32 * 32, inZone);
            Assert.False(StubGenerator.InStartZone(world.Shape, Small / 2 - half - 1, Small / 2));
            Assert.False(StubGenerator.InStartZone(world.Shape, Small / 2 + half, Small / 2));
        }

        [Fact]
        public void D00_Gen_Objects_MinDistance1_AcrossSeam_TreesOnMeadows()
        {
            WorldModel world = SmallWorld.Value;
            int objects = 0;
            for (int y = 0; y < Small; y++)
            {
                for (int x = 0; x < Small; x++)
                {
                    ushort obj = world.GetObject(new TileCoord(x, y));
                    if (obj == ObjectId.Empty)
                    {
                        continue;
                    }

                    objects++;
                    Assert.True(obj == ObjectId.Tree || obj == ObjectId.Rock, $"Чужой объект {obj} в ({x}, {y}).");
                    if (obj == ObjectId.Tree)
                    {
                        Assert.Equal(FloorId.Grass, world.GetFloor(new TileCoord(x, y)));
                    }

                    // Соседи по тору: x − 1 у x = 0 — это тайл W − 1 (WorldModel сворачивает координаты).
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if ((dx != 0 || dy != 0) && world.GetObject(new TileCoord(x + dx, y + dy)) != ObjectId.Empty)
                            {
                                Assert.Fail($"Объекты рядом: ({x}, {y}) и ({x + dx}, {y + dy}).");
                            }
                        }
                    }
                }
            }

            Assert.True(objects > Small * Small / 50, $"Объектов мало: {objects}.");
        }

        // ---------- Время ----------

        [Fact]
        public void D00_Gen_Time_1024_WithinNorm3s()
        {
            var gen = new StubGenerator();
            gen.Generate(MapShape.World(256), 1); // прогрев JIT
            var sw = Stopwatch.StartNew();
            WorldModel world = gen.Generate(MapShape.World(Small), Seed, out GenStats stats);
            double genMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            byte[] snapshot = WorldSnapshot.WriteBase(world);
            double snapMs = sw.Elapsed.TotalMilliseconds;
            _out.WriteLine($"1024²: генерация {genMs:F0} мс, снимок генерации {snapMs:F0} мс ({snapshot.Length / 1048576.0:F2} МБ); " +
                $"скал-тайлов {stats.RockTiles}, деревьев {stats.Trees}, скал-объектов {stats.Rocks}");
            Assert.True(genMs + snapMs < 3000, $"1024² дольше нормы 3 с: {genMs + snapMs:F0} мс.");
        }

        [Fact]
        [Trait("Category", "Big")]
        public void D00_Gen_Big4096_TimeShareStartZoneRepeat()
        {
            MapShape shape = MapShape.World(4096);
            var gen = new StubGenerator();
            gen.Generate(MapShape.World(256), 1); // прогрев JIT
            var sw = Stopwatch.StartNew();
            WorldModel world = gen.Generate(shape, Seed, out GenStats stats);
            double genMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            byte[] snapshot = WorldSnapshot.WriteBase(world);
            double snapMs = sw.Elapsed.TotalMilliseconds;
            double share = 100.0 * stats.RockTiles / ((long)shape.Width * shape.Height);
            _out.WriteLine($"4096²: генерация {genMs:F0} мс, снимок генерации {snapMs:F0} мс ({snapshot.Length / 1048576.0:F2} МБ); " +
                $"скалы {share:F2} %, деревьев {stats.Trees}, скал-объектов {stats.Rocks}");

            Assert.True(genMs + snapMs < 30000, $"4096² дольше нормы 30 с: {genMs + snapMs:F0} мс.");
            Assert.InRange(share, StubGenerator.RockSharePercent - 3.0, StubGenerator.RockSharePercent + 3.0);
            for (int y = 2048 - 16; y < 2048 + 16; y++)
            {
                for (int x = 2048 - 16; x < 2048 + 16; x++)
                {
                    Assert.Equal(FloorId.Grass, world.GetFloor(new TileCoord(x, y)));
                    Assert.Equal(ObjectId.Empty, world.GetObject(new TileCoord(x, y)));
                }
            }

            byte[] again = WorldSnapshot.WriteBase(gen.Generate(shape, Seed));
            Assert.Equal(snapshot, again);
        }

        private static void Count(WorldModel world, out int stone, out int grass, out int trees, out int rocks)
        {
            stone = grass = trees = rocks = 0;
            for (int y = 0; y < world.Shape.Height; y++)
            {
                for (int x = 0; x < world.Shape.Width; x++)
                {
                    var t = new TileCoord(x, y);
                    byte f = world.GetFloor(t);
                    if (f == FloorId.Stone)
                    {
                        stone++;
                    }
                    else if (f == FloorId.Grass)
                    {
                        grass++;
                    }

                    ushort o = world.GetObject(t);
                    if (o == ObjectId.Tree)
                    {
                        trees++;
                    }
                    else if (o == ObjectId.Rock)
                    {
                        rocks++;
                    }
                }
            }
        }
    }
}
