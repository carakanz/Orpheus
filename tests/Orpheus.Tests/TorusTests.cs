using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Приёмка тора: docs/07 3.13 п. 1 (= docs/01 1, приёмка 1), границы, карта «не заворачивать».
    /// </summary>
    public class TorusTests
    {
        private const int U = Torus.SubTilesPerTile;

        public static IEnumerable<object[]> WorldSizes() => new[]
        {
            new object[] { 1024 },
            new object[] { 2048 },
            new object[] { 4096 },
            new object[] { 8192 },
        };

        public static IEnumerable<object[]> RunCases()
        {
            int[] sizes = { 1024, 4096 };
            (int dx, int dy, string name)[] dirs =
            {
                (1, 0, "восток"),
                (0, -1, "север"),
                (-1, 0, "запад"),
                (1, -1, "северо-восток"),
                (1, 1, "юго-восток"),
            };
            foreach (int size in sizes)
            {
                foreach (var d in dirs)
                {
                    yield return new object[] { size, d.dx, d.dy, d.name };
                }
            }
        }

        // ---------- 3.13 п. 1: бег на W тайлов ----------

        [Theory]
        [MemberData(nameof(RunCases))]
        public void D00_Torus_RunWTiles_ReturnsToSameTile(int size, int dirX, int dirY, string direction)
        {
            var map = MapShape.World(size);
            for (ulong seed = 1; seed <= 10; seed++)
            {
                var rng = new SplitMix64(seed);
                var start = new WorldPos(rng.NextInt(map.WidthUnits), rng.NextInt(map.HeightUnits));
                int speed = 1 + rng.NextInt(2 * U); // до 2 тайлов за такт
                TileCoord startTile = Torus.TileOf(map, start);

                WorldPos p = start;
                long remaining = (long)size * U;
                int seamCrossingsX = 0;
                int seamCrossingsY = 0;
                while (remaining > 0)
                {
                    int s = (int)Math.Min(speed, remaining);
                    var step = new Int2(dirX * s, dirY * s);
                    WorldPos prev = p;
                    p = Torus.Offset(map, p, step);

                    Assert.Equal(step, Torus.Delta(map, prev, p)); // через шов — без скачка
                    Assert.InRange(p.X, 0, map.WidthUnits - 1);
                    Assert.InRange(p.Y, 0, map.HeightUnits - 1);
                    if (Math.Abs(p.X - prev.X) > s) seamCrossingsX++;
                    if (Math.Abs(p.Y - prev.Y) > s) seamCrossingsY++;
                    remaining -= s;
                }

                Assert.True(start == p, $"{direction}, зерно {seed}: {start} → {p}");
                Assert.Equal(startTile, Torus.TileOf(map, p));
                Assert.Equal(dirX != 0 ? 1 : 0, seamCrossingsX);
                Assert.Equal(dirY != 0 ? 1 : 0, seamCrossingsY);
            }
        }

        // ---------- 3.13 п. 1: Delta на 10⁶ случайных пар против формулы ----------

        // Формула docs/01, раздел 1: Δ = ((b − a + R) mod W) − R, mod неотрицательный; в 1/256 тайла.
        private static long ReferenceDelta(long a, long b, long size)
        {
            long r = size / 2;
            long m = (b - a + r) % size;
            if (m < 0) m += size;
            return m - r;
        }

        [Theory]
        [MemberData(nameof(WorldSizes))]
        public void D00_Torus_Delta_MillionRandomPairs_MatchReference(int size)
        {
            var map = MapShape.World(size);
            var rng = new SplitMix64(0x0EF1_0001UL + (ulong)size);
            long units = map.WidthUnits;
            for (int i = 0; i < 1_000_000; i++)
            {
                WorldPos a, b;
                if ((i & 3) == 0)
                {
                    // четверть пар — несвёрнутые значения во всём диапазоне int32
                    a = new WorldPos(rng.NextAnyInt(), rng.NextAnyInt());
                    b = new WorldPos(rng.NextAnyInt(), rng.NextAnyInt());
                }
                else
                {
                    a = new WorldPos(rng.NextInt(map.WidthUnits), rng.NextInt(map.HeightUnits));
                    b = new WorldPos(rng.NextInt(map.WidthUnits), rng.NextInt(map.HeightUnits));
                }

                Int2 d = Torus.Delta(map, a, b);
                long rx = ReferenceDelta(a.X, b.X, units);
                long ry = ReferenceDelta(a.Y, b.Y, units);
                if (d.X != rx || d.Y != ry)
                {
                    Assert.Fail($"W={size}, пара {i}: {a} → {b}: Delta {d}, эталон <{rx}, {ry}>");
                }

                if (Torus.Dist2(map, a, b) != rx * rx + ry * ry)
                {
                    Assert.Fail($"W={size}, пара {i}: Dist2 не равен dx² + dy²");
                }

                // Расстояние по оси из docs/01: min(|x₁ − x₂|, W − |x₁ − x₂|) для свёрнутых координат.
                if ((i & 3) != 0)
                {
                    long ax = Math.Abs((long)a.X - b.X);
                    if (Math.Abs((long)d.X) != Math.Min(ax, units - ax))
                    {
                        Assert.Fail($"W={size}, пара {i}: |Δx| не равен кратчайшему расстоянию");
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(WorldSizes))]
        public void D00_Torus_DeltaTiles_RandomPairs_MatchReference(int size)
        {
            var map = MapShape.World(size);
            var rng = new SplitMix64(0x0EF1_0002UL + (ulong)size);
            for (int i = 0; i < 100_000; i++)
            {
                var a = new TileCoord(rng.NextAnyInt(), rng.NextAnyInt());
                var b = new TileCoord(rng.NextAnyInt(), rng.NextAnyInt());
                Int2 d = Torus.Delta(map, a, b);
                Assert.Equal(ReferenceDelta(a.X, b.X, size), d.X);
                Assert.Equal(ReferenceDelta(a.Y, b.Y, size), d.Y);
            }
        }

        // ---------- Границы ----------

        [Theory]
        [MemberData(nameof(WorldSizes))]
        public void D00_Torus_Wrap_Boundaries(int size)
        {
            var map = MapShape.World(size);
            int wu = size * U;
            Assert.Equal(0, Torus.WrapX(map, 0));
            Assert.Equal(wu - 1, Torus.WrapX(map, wu - 1));
            Assert.Equal(0, Torus.WrapX(map, wu));
            Assert.Equal(wu - 1, Torus.WrapX(map, -1));
            Assert.Equal(0, Torus.WrapX(map, -wu));
            Assert.Equal(wu / 2, Torus.WrapY(map, -wu / 2));
            Assert.Equal(new WorldPos(wu - 1, 0), Torus.Wrap(map, new WorldPos(-1, wu)));

            Assert.Equal(new TileCoord(size - 1, 0), Torus.Wrap(map, new TileCoord(-1, size)));
            Assert.Equal(new ChunkCoord(size / 32 - 1, 0), Torus.Wrap(map, new ChunkCoord(-1, size / 32)));
        }

        [Theory]
        [MemberData(nameof(WorldSizes))]
        public void D00_Torus_TileOf_ChunkOf_Boundaries(int size)
        {
            var map = MapShape.World(size);
            int wu = size * U;
            Assert.Equal(new TileCoord(0, 0), Torus.TileOf(map, new WorldPos(0, 255)));
            Assert.Equal(new TileCoord(1, 0), Torus.TileOf(map, new WorldPos(256, 0)));
            Assert.Equal(new TileCoord(size - 1, size - 1), Torus.TileOf(map, new WorldPos(wu - 1, -1)));
            Assert.Equal(new TileCoord(0, size / 2), Torus.TileOf(map, new WorldPos(wu, wu / 2)));
            Assert.Equal(new TileCoord(size - 1, size - 1), Torus.TileOf(map, new WorldPos(-256, -255)));

            int lastChunk = size / Torus.ChunkSize - 1;
            Assert.Equal(new ChunkCoord(0, 0), Torus.ChunkOf(map, new TileCoord(31, 0)));
            Assert.Equal(new ChunkCoord(1, 0), Torus.ChunkOf(map, new TileCoord(32, 31)));
            Assert.Equal(new ChunkCoord(lastChunk, lastChunk), Torus.ChunkOf(map, new TileCoord(size - 1, -1)));
            Assert.Equal(new ChunkCoord(0, 0), Torus.ChunkOf(map, new TileCoord(size, size)));
            Assert.Equal(new ChunkCoord(lastChunk, 0), Torus.ChunkOf(map, new WorldPos(-1, 0)));

            Assert.Equal(new WorldPos(5 * U, 7 * U), Torus.CornerOf(new TileCoord(5, 7)));
            Assert.Equal(new TileCoord(64, 96), Torus.CornerOf(new ChunkCoord(2, 3)));
        }

        [Theory]
        [MemberData(nameof(WorldSizes))]
        public void D00_Torus_Delta_Boundaries(int size)
        {
            var map = MapShape.World(size);
            int wu = size * U;
            int half = wu / 2;

            // соседи через шов
            Assert.Equal(-1, Torus.DeltaX(map, 0, wu - 1));
            Assert.Equal(1, Torus.DeltaX(map, wu - 1, 0));
            Assert.Equal(0, Torus.DeltaX(map, 0, wu)); // одна и та же точка
            Assert.Equal(1, Torus.DeltaY(map, -1, 0));

            // ровно W/2: Δ = −R в обе стороны (X ∈ [−R, R), docs/01)
            Assert.Equal(-half, Torus.DeltaX(map, 0, half));
            Assert.Equal(-half, Torus.DeltaX(map, half, 0));
            Assert.Equal(half - 1, Torus.DeltaX(map, 0, half - 1));
            Assert.Equal(-(half - 1), Torus.DeltaX(map, 0, half + 1));

            // то же в тайлах
            Assert.Equal(new Int2(-1, 1), Torus.Delta(map, new TileCoord(0, size - 1), new TileCoord(size - 1, 0)));
            Assert.Equal(new Int2(-size / 2, -size / 2), Torus.Delta(map, new TileCoord(0, 0), new TileCoord(size / 2, size / 2)));

            // наибольшее расстояние — до Изнанки по диагонали: R·√2
            long r = size / 2;
            Assert.Equal(2 * r * r, Torus.Dist2(map, new TileCoord(size / 2, size / 2), new TileCoord(0, 0)));

            // в чанках
            int chunks = size / Torus.ChunkSize;
            Assert.Equal(new Int2(1, -1), Torus.Delta(map, new ChunkCoord(chunks - 1, 0), new ChunkCoord(0, chunks - 1)));
        }

        // Края docs/01, раздел 1 (мир 4096²).
        [Fact]
        public void D00_Torus_World01_EdgeCases()
        {
            var map = MapShape.World(4096);

            // Игрок на шве: шаг на запад с x = 0 — x = W − 1.
            WorldPos onSeam = Torus.CornerOf(new TileCoord(0, 100));
            Assert.Equal(new TileCoord(4095, 100), Torus.TileOf(map, Torus.Step(map, onSeam, -1f, 0f)));

            // Объект 2 × 2 с якорем (W − 1, y) занимает тайлы x = W − 1 и x = 0.
            Assert.Equal(new TileCoord(0, 11), Torus.Wrap(map, new TileCoord(4095 + 1, 10 + 1)));

            // Два игрока по разные стороны шва в двух тайлах: расстояние 2, а не 4094.
            var left = new TileCoord(4095, 50);
            var right = new TileCoord(1, 50);
            Assert.Equal(new Int2(2, 0), Torus.Delta(map, left, right));
            Assert.Equal(4, Torus.Dist2(map, left, right));

            // Приёмка 5 docs/01: игрок X = +2000, база X = −2000 → на восток, расстояние 96.
            // Показ X = ((x − W/2 + R) mod W) − R ⇒ x = X + W/2 (mod W).
            var player = new TileCoord(2000 + 2048, 2048);
            var home = new TileCoord(-2000 + 2048, 2048);
            Assert.Equal(new Int2(96, 0), Torus.Delta(map, player, home));
        }

        [Fact]
        public void D00_Torus_Step_RoundsToSubTile()
        {
            var map = MapShape.World(1024);
            var p = new WorldPos(1000, 1000);
            Assert.Equal(new WorldPos(1051, 1000), Torus.Step(map, p, 0.2f, 0f)); // 6 тайлов/с при 30 Гц: 51,2 → 51
            Assert.Equal(new WorldPos(1000, 1000), Torus.Step(map, p, 0.001f, -0.001f)); // 0,256 → 0
            Assert.Equal(new WorldPos(1000 + 128, 1000 - 384), Torus.Step(map, p, 0.5f, -1.5f));
            Assert.Equal(new WorldPos(1024 * U - 256, 0), Torus.Step(map, new WorldPos(0, 0), -1f, 0f));
            Assert.Equal(2, Torus.ToUnits(0.005859375f)); // ровно 1,5/256 → к чётному
            Assert.Equal(-51, Torus.ToUnits(-0.2f));
        }

        [Fact]
        public void D00_Torus_NeighborChunks_WrapAcrossSeam()
        {
            var map = MapShape.World(1024); // 32 × 32 чанка
            Span<ChunkCoord> buf = stackalloc ChunkCoord[Torus.MaxNeighborChunks(1, 1)];
            int n = Torus.NeighborChunks(map, new ChunkCoord(0, 0), 1, buf);
            Assert.Equal(9, n);
            var expected = new[]
            {
                new ChunkCoord(31, 31), new ChunkCoord(0, 31), new ChunkCoord(1, 31),
                new ChunkCoord(31, 0), new ChunkCoord(0, 0), new ChunkCoord(1, 0),
                new ChunkCoord(31, 1), new ChunkCoord(0, 1), new ChunkCoord(1, 1),
            };
            Assert.Equal(expected, buf.Slice(0, n).ToArray());

            // Прямоугольный радиус 3 × 2 (7 × 5 чанков) у угла карты: заворот по обеим осям.
            Span<ChunkCoord> wide = stackalloc ChunkCoord[Torus.MaxNeighborChunks(3, 2)];
            n = Torus.NeighborChunks(map, new ChunkCoord(31, 30), 3, 2, wide);
            Assert.Equal(35, n);
            var set = new HashSet<ChunkCoord>();
            for (int i = 0; i < n; i++)
            {
                Assert.InRange(wide[i].X, 0, 31);
                Assert.InRange(wide[i].Y, 0, 31);
                Assert.True(set.Add(wide[i]));
                Assert.InRange(Math.Abs(Torus.Delta(map, new ChunkCoord(31, 30), wide[i]).X), 0, 3);
                Assert.InRange(Math.Abs(Torus.Delta(map, new ChunkCoord(31, 30), wide[i]).Y), 0, 2);
            }

            // Окно шире карты: каждый чанк один раз.
            var all = new ChunkCoord[Torus.MaxNeighborChunks(20, 20)];
            n = Torus.NeighborChunks(map, new ChunkCoord(5, 5), 20, all);
            Assert.Equal(32 * 32, n);
            Assert.Equal(32 * 32, new HashSet<ChunkCoord>(all[..n]).Count);

            // Буфер мал — ошибка, а не порча памяти.
            var small = new ChunkCoord[8];
            Assert.Throws<ArgumentException>(() => Torus.NeighborChunks(map, new ChunkCoord(0, 0), 1, small));
            Assert.Throws<ArgumentOutOfRangeException>(() => Torus.NeighborChunks(map, new ChunkCoord(0, 0), -1, small));
        }

        [Fact]
        public void D00_Torus_MapShape_Validation()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MapShape.World(1000));
            Assert.Throws<ArgumentOutOfRangeException>(() => MapShape.World(16));
            Assert.Throws<ArgumentOutOfRangeException>(() => MapShape.World(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => MapShape.World(MapShape.MaxSize * 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => MapShape.Flat(0, 10));

            var w = MapShape.World(4096);
            Assert.Equal(128, w.ChunksX);
            Assert.Equal(4096 * U, w.WidthUnits);
            var big = MapShape.World(MapShape.MaxSize);
            Assert.Equal(-big.WidthUnits / 2, Torus.DeltaX(big, 0, big.WidthUnits / 2));

            var f = MapShape.Flat(100, 37);
            Assert.Equal(4, f.ChunksX);
            Assert.Equal(2, f.ChunksY);
        }

        // ---------- Карта «не заворачивать» ----------

        [Fact]
        public void D00_Torus_FlatMap_NoWrap()
        {
            var map = MapShape.Flat(100, 60);
            int wu = 100 * U;

            // Wrap прижимает к краю.
            Assert.Equal(0, Torus.WrapX(map, -5));
            Assert.Equal(wu - 1, Torus.WrapX(map, wu));
            Assert.Equal(1234, Torus.WrapX(map, 1234));
            Assert.Equal(new TileCoord(0, 59), Torus.Wrap(map, new TileCoord(-1, 60)));

            // Delta — обычная разность, без кратчайшего пути через край.
            Assert.Equal(99 * U, Torus.DeltaX(map, 0, 99 * U));
            Assert.Equal(-99 * U, Torus.DeltaX(map, 99 * U, 0));
            Assert.Equal(new Int2(99, -59), Torus.Delta(map, new TileCoord(0, 59), new TileCoord(99, 0)));
            Assert.Equal(99L * 99, Torus.Dist2(map, new TileCoord(0, 0), new TileCoord(99, 0)));

            // TileOf и ChunkOf не выходят за край.
            Assert.Equal(new TileCoord(0, 0), Torus.TileOf(map, new WorldPos(-1, -1000)));
            Assert.Equal(new TileCoord(99, 59), Torus.TileOf(map, new WorldPos(wu + 5000, 60 * U)));
            Assert.Equal(new ChunkCoord(3, 1), Torus.ChunkOf(map, new TileCoord(99, 59)));

            // Бег на восток на W тайлов упирается в край, а не заворачивает.
            WorldPos p = Torus.CornerOf(new TileCoord(10, 10));
            for (int i = 0; i < 100 * 5; i++)
            {
                p = Torus.Step(map, p, 0.2f, 0f);
            }
            Assert.Equal(new TileCoord(99, 10), Torus.TileOf(map, p));

            // Соседи у края: чанки за краем пропущены.
            Span<ChunkCoord> buf = stackalloc ChunkCoord[Torus.MaxNeighborChunks(1, 1)];
            int n = Torus.NeighborChunks(map, new ChunkCoord(0, 0), 1, buf);
            Assert.Equal(new[] { new ChunkCoord(0, 0), new ChunkCoord(1, 0), new ChunkCoord(0, 1), new ChunkCoord(1, 1) },
                buf.Slice(0, n).ToArray());
            n = Torus.NeighborChunks(map, new ChunkCoord(3, 1), 1, buf);
            Assert.Equal(new[] { new ChunkCoord(2, 0), new ChunkCoord(3, 0), new ChunkCoord(2, 1), new ChunkCoord(3, 1) },
                buf.Slice(0, n).ToArray());
        }
    }
}
