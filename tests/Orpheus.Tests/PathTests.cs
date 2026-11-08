using System;
using System.Diagnostics;
using Orpheus.Core.Geometry;
using Orpheus.Core.Pathing;
using Orpheus.Core.Rng;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Шип пути 0-023 (docs/02, 9.2; docs/07, 2.4): A* в окне 48 × 48 с заворотом по тору, предел 1500 узлов,
    /// «цель в замкнутой области», бюджет 40 поисков и 3 мс за такт. Бенч 1024² — <c>D02_S9_2_Big_*</c> (Category=Big).
    /// </summary>
    public sealed class PathTests
    {
        private readonly ITestOutputHelper _out;

        public PathTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static TileCoord T(int x, int y) => new TileCoord(x, y);

        // ---------- Заворот ----------

        [Fact]
        public void D02_S9_2_PathThroughSeamX_IsOrdinary()
        {
            var map = new GridTerrain(MapShape.World(64));
            var path = new PathBuffer();
            Assert.Equal(PathStatus.Found, new PathFinder().Find(map, T(62, 10), T(2, 10), PathMode.Walker, path));
            Assert.Equal(new[] { T(63, 10), T(0, 10), T(1, 10), T(2, 10) }, Steps(path));
            Assert.Equal(40, path.Cost);
        }

        [Fact]
        public void D02_S9_2_PathThroughSeamY_IsOrdinary()
        {
            var map = new GridTerrain(MapShape.World(64));
            var path = new PathBuffer();
            Assert.Equal(PathStatus.Found, new PathFinder().Find(map, T(7, 1), T(7, 61), PathMode.Walker, path));
            Assert.Equal(new[] { T(7, 0), T(7, 63), T(7, 62), T(7, 61) }, Steps(path));
            Assert.Equal(40, path.Cost);
        }

        [Fact]
        public void D02_S9_2_PathThroughCorner_Diagonal()
        {
            var map = new GridTerrain(MapShape.World(64));
            var path = new PathBuffer();
            Assert.Equal(PathStatus.Found, new PathFinder().Find(map, T(62, 62), T(1, 1), PathMode.Walker, path));
            Assert.Equal(new[] { T(63, 63), T(0, 0), T(1, 1) }, Steps(path));
            Assert.Equal(3 * PathCost.Diagonal, path.Cost);
        }

        [Fact]
        public void D02_S9_2_SeamBlockedWall_GoesAroundAcrossSeam()
        {
            // Стена поперёк шва по X от y = 5 до y = 15; обход — через y = 4 или y = 16, оба через шов.
            var map = new GridTerrain(MapShape.World(64));
            for (int y = 5; y <= 15; y++)
            {
                map.Wall(0, y);
            }

            var path = new PathBuffer();
            Assert.Equal(PathStatus.Found, new PathFinder().Find(map, T(62, 10), T(2, 10), PathMode.Walker, path));
            AssertConnected(map.Shape, T(62, 10), path);
            foreach (TileCoord t in Steps(path))
            {
                Assert.True(map.Walk(t) != PathCost.Blocked, $"шаг в стену {t}");
            }
        }

        // ---------- Углы и стоимость ----------

        [Fact]
        public void D02_S9_2_NoCornerCutting()
        {
            var map = new GridTerrain(MapShape.World(64));
            map.Wall(11, 10);
            var path = new PathBuffer();
            Assert.Equal(PathStatus.Found, new PathFinder().Find(map, T(10, 10), T(11, 11), PathMode.Walker, path));
            Assert.Equal(new[] { T(10, 11), T(11, 11) }, Steps(path));
            Assert.Equal(20, path.Cost);

            // Оба угла закрыты — диагональ не протиснуться, обход длиннее.
            map.Wall(10, 11);
            Assert.Equal(PathStatus.Found, new PathFinder().Find(map, T(10, 10), T(11, 11), PathMode.Walker, path));
            Assert.True(path.Length > 2);
            AssertConnected(map.Shape, T(10, 10), path);
        }

        [Fact]
        public void D02_S9_2_SlowFloor_CostTimesOneAndHalf()
        {
            // Полоса замедляющего пола поперёк всей карты: пересечь её обязательно, 10 + 15 + 10 + 10.
            var map = new GridTerrain(MapShape.World(64));
            for (int y = 0; y < 64; y++)
            {
                map.Slow(12, y);
            }

            var path = new PathBuffer();
            Assert.Equal(PathStatus.Found, new PathFinder().Find(map, T(10, 10), T(14, 10), PathMode.Walker, path));
            Assert.Equal(45, path.Cost);
            Assert.Equal(4, path.Length);
        }

        [Fact]
        public void D02_S9_2_SlowFloorPatch_DetourIsCheaper()
        {
            // Три медленных тайла по прямой: прямо 15·3 + 10 = 55, обход по y = 9 — 14 + 10 + 10 + 14 = 48.
            var map = new GridTerrain(MapShape.World(64));
            map.Slow(11, 10);
            map.Slow(12, 10);
            map.Slow(13, 10);
            var path = new PathBuffer();
            Assert.Equal(PathStatus.Found, new PathFinder().Find(map, T(10, 10), T(14, 10), PathMode.Walker, path));
            Assert.Equal(48, path.Cost);
            foreach (TileCoord t in Steps(path))
            {
                Assert.Equal(PathCost.Normal, map.Walk(t));
            }
        }

        // ---------- Предел узлов, окно, частичный путь ----------

        [Fact]
        public void D02_S9_2_NodeLimit1500_PartialToNearestByTorus()
        {
            // Цель через шов в коробке; заливка выключена — A* упирается в предел и отдаёт тайл, ближайший к цели.
            var map = new GridTerrain(MapShape.World(64));
            TileCoord goal = T(6, 20);
            Box(map, goal, 2);
            var finder = new PathFinder(enclosureFillLimit: 0);
            var path = new PathBuffer();
            Assert.Equal(PathStatus.Partial, finder.Find(map, T(60, 20), goal, PathMode.Walker, path));
            Assert.Equal(PathFinder.DefaultMaxExpanded, path.Expanded);
            Assert.Equal(T(3, 20), path.End);
            Assert.Equal(7, path.Length);
            AssertConnected(map.Shape, T(60, 20), path);
        }

        [Fact]
        public void D02_S9_2_Window48_GoalOutside_PathToEdge_NoReadsOutside()
        {
            var map = new GridTerrain(MapShape.World(128));
            var path = new PathBuffer();
            TileCoord start = T(120, 5);
            Assert.Equal(PathStatus.WindowEdge, new PathFinder().Find(map, start, T(22, 5), PathMode.Walker, path));
            Assert.Equal(T(15, 5), path.End); // 120 + 23 − 128: край окна за швом
            Assert.Equal(23, path.Length);
            Assert.True(map.Reads > 0);
            Assert.True(map.MaxReadDistance(start) <= PathFinder.WindowHalf, $"чтение за окном: {map.MaxReadDistance(start)}");
        }

        [Fact]
        public void D02_S9_2_Window48_DetourOutsideWindow_IsPartial()
        {
            // Стена поперёк всего окна, проход — за окном: в окне пути нет, цель видна — частичный путь к стене.
            var map = new GridTerrain(MapShape.World(128));
            TileCoord start = T(40, 40);
            for (int y = 40 - 30; y <= 40 + 30; y++)
            {
                if (y != 40 + 28)
                {
                    map.Wall(45, y);
                }
            }

            var path = new PathBuffer();
            Assert.Equal(PathStatus.Partial, new PathFinder().Find(map, start, T(50, 40), PathMode.Walker, path));
            Assert.Equal(T(44, 40), path.End);
            Assert.True(path.Expanded < PathFinder.DefaultMaxExpanded, $"окно исчерпано раньше предела: {path.Expanded}");
            Assert.True(map.MaxReadDistance(start) <= PathFinder.WindowHalf);
        }

        // ---------- Цель в замкнутой области ----------

        /// <summary>
        /// Цель в замкнутой области, враг снаружи (слово Автора 08.11, 0-034): итог «замкнута», путь — к проходимому тайлу вне
        /// области, ближайшему к цели (по тору; при равенстве — ближе к врагу). Через шов: коробка вокруг (6, 20), враг в (60, 20) —
        /// к (3, 20) у западной стены, 7 шагов прямо.
        /// </summary>
        [Fact]
        public void D02_S9_2_GoalEnclosed_PathToEdgeNearestGoal()
        {
            var map = new GridTerrain(MapShape.World(64));
            TileCoord goal = T(6, 20);
            Box(map, goal, 2);
            var finder = new PathFinder();
            var path = new PathBuffer();
            Assert.Equal(PathStatus.Enclosed, finder.Find(map, T(60, 20), goal, PathMode.Walker, path));
            Assert.Equal(1, finder.AStarRuns);
            Assert.Equal(T(3, 20), path.End); // (3, 20) и (9, 20) — оба в 3 от цели; (3, 20) ближе к врагу
            Assert.Equal(new[] { T(61, 20), T(62, 20), T(63, 20), T(0, 20), T(1, 20), T(2, 20), T(3, 20) }, Steps(path));
            AssertConnected(map.Shape, T(60, 20), path);

            // Враг внутри той же коробки — путь есть, обычный A*.
            Assert.Equal(PathStatus.Found, finder.Find(map, T(5, 19), goal, PathMode.Walker, path));
            Assert.Equal(2, finder.AStarRuns);

            // Дверь (проход в стене коробки) — уже не замкнута.
            map.Open(T(4, 20));
            Assert.Equal(PathStatus.Found, finder.Find(map, T(60, 20), goal, PathMode.Walker, path));
            Assert.Equal(3, finder.AStarRuns);
            Assert.Equal(goal, path.End);
        }

        /// <summary>
        /// Ближний к цели край — на дальней от врага стороне (0-034): коробка 7 × 7 внутри стен толщиной 2, цель у восточной стены,
        /// враг на западе — обходит коробку к тайлу за восточной стеной (за 2 тайла стены), а не встаёт у западной.
        /// Враг уже у ближнего края — путь пуст, итог «замкнута».
        /// </summary>
        [Fact]
        public void D02_S9_2_GoalEnclosed_FarSideEdge_WalksAround_ThickWalls()
        {
            var map = new GridTerrain(MapShape.World(128));
            TileCoord center = T(40, 40);
            Box(map, center, 4);
            Box(map, center, 5); // стены толщиной 2: x = 35, 36 и 44, 45
            TileCoord goal = T(43, 40); // у восточной стены
            var finder = new PathFinder();
            var path = new PathBuffer();
            Assert.Equal(PathStatus.Enclosed, finder.Find(map, T(25, 40), goal, PathMode.Walker, path));
            Assert.Equal(T(46, 40), path.End); // 3 от цели; западный край (34, 40) — в 9
            AssertConnected(map.Shape, T(25, 40), path);
            foreach (TileCoord t in Steps(path))
            {
                Assert.True(Math.Abs(t.X - center.X) > 5 || Math.Abs(t.Y - center.Y) > 5); // в обход, не сквозь коробку
            }

            Assert.Equal(PathStatus.Enclosed, finder.Find(map, T(46, 40), goal, PathMode.Walker, path));
            Assert.Equal(0, path.Length);
            Assert.Equal(T(46, 40), path.End);
        }

        [Fact]
        public void D02_S9_2_EnclosureBiggerThanFillLimit_FallsBackToAStar()
        {
            var map = new GridTerrain(MapShape.World(64));
            TileCoord goal = T(30, 30);
            Box(map, goal, 10); // внутри 19 × 19 = 361 > 256
            var finder = new PathFinder();
            var path = new PathBuffer();
            Assert.Equal(PathStatus.Partial, finder.Find(map, T(30, 45), goal, PathMode.Walker, path));
            Assert.Equal(1, finder.AStarRuns);
        }

        // ---------- Летун и правило такта ----------

        [Fact]
        public void D02_S9_2_WalkerUsesSimulationIsBlocked_FlyerOnlyBuildings()
        {
            var shape = MapShape.World(64);
            var world = new WorldModel(shape, 1);
            world.CreateAllChunks();
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    world.SetBaseTile(T(x, y), FloorId.Grass, ObjectId.Empty);
                }
            }

            // Река лавы поперёк карты по x = 20 и скалы по x = 21: пешему не пройти, летуну — да.
            for (int y = 0; y < 64; y++)
            {
                world.SetBaseTile(T(20, y), FloorId.Lava, ObjectId.Empty);
                world.SetBaseTile(T(21, y), FloorId.Grass, ObjectId.Rock);
            }

            var sim = new Simulation(world);
            var terrain = new SimulationPathTerrain(sim);
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    Assert.Equal(sim.IsBlocked(shape, T(x, y)), terrain.CostOf(T(x, y), PathMode.Walker) == PathCost.Blocked);
                }
            }

            var path = new PathBuffer();
            var finder = new PathFinder(enclosureFillLimit: 0);
            Assert.Equal(PathStatus.Partial, finder.Find(terrain, T(15, 10), T(25, 10), PathMode.Walker, path));
            Assert.Equal(T(19, 10), path.End);
            Assert.Equal(PathStatus.Found, finder.Find(terrain, T(15, 10), T(25, 10), PathMode.Flyer, path));
            Assert.Equal(10, path.Length);

            // Стена постройки поперёк карты по x = 23 — летуну тоже не пройти.
            for (int y = 0; y < 64; y++)
            {
                world.SetBaseTile(T(23, y), FloorId.Grass, ObjectId.StoneWall);
            }

            Assert.Equal(PathStatus.Partial, finder.Find(terrain, T(15, 10), T(25, 10), PathMode.Flyer, path));
            Assert.Equal(T(22, 10), path.End);
        }

        // ---------- Детерминизм ----------

        [Fact]
        public void D02_S9_2_Hash1000Paths_SameInTwoRuns()
        {
            ulong a = HashRun(PathBench.Build(256, Seed), 1000);
            ulong b = HashRun(PathBench.Build(256, Seed), 1000);
            _out.WriteLine($"хэш 1000 путей: {a:x16}");
            Assert.Equal(a, b);
            Assert.NotEqual(a, HashRun(PathBench.Build(256, Seed + 1), 1000));
        }

        private static ulong HashRun(PathBench bench, int count)
        {
            var finder = new PathFinder();
            var path = new PathBuffer();
            ulong h = 0;
            for (int i = 0; i < count; i++)
            {
                PathRequest r = bench.Requests[i];
                finder.Find(bench.Terrain, r.Start, r.Goal, r.Mode, path);
                h = PathBench.Hash(h, path);
            }

            return h;
        }

        private const ulong Seed = 0x0_023;

        // ---------- Бюджет такта ----------

        [Fact]
        public void D02_S9_2_Budget40PerTick_RestNextTick()
        {
            PathBench bench = PathBench.Build(128, Seed);
            var results = Buffers(50);
            var scheduler = new PathScheduler(new PathFinder(), bench.Terrain, results, 64);
            for (int i = 0; i < 50; i++)
            {
                PathRequest r = bench.Requests[i];
                Assert.True(scheduler.Enqueue(i, r.Start, r.Goal, r.Mode, 1));
            }

            Assert.Equal(40, scheduler.RunTick(1));
            Assert.Equal(10, scheduler.Pending);
            Assert.Equal(10, scheduler.RunTick(2));
            Assert.Equal(1, scheduler.MaxWaitTicks);
            Assert.Equal(10, scheduler.DeferredTotal);
            Assert.Equal(2, results[49].DoneTick);
        }

        [Fact]
        public void D02_S9_2_Budget3ms_DefersRest_PathsUnchanged()
        {
            // Часы: каждый взгляд — +1 мс. Взгляды: начало такта (0), перед 1-м поиском (1), 2-м (2), 3-м (3 ≥ 3 — стоп).
            PathBench bench = PathBench.Build(128, Seed);
            const int N = 10;
            var results = Buffers(N);
            var clock = new StepClock(1000);
            var scheduler = new PathScheduler(new PathFinder(), bench.Terrain, results, 64, clock: clock);
            for (int i = 0; i < N; i++)
            {
                PathRequest r = bench.Requests[i];
                scheduler.Enqueue(i, r.Start, r.Goal, r.Mode, 1);
            }

            Assert.Equal(2, scheduler.RunTick(1));
            Assert.Equal(N - 2, scheduler.Pending);
            Assert.Equal(1, scheduler.TimeLimitedTicks);
            long tick = 1;
            while (scheduler.Pending > 0)
            {
                Assert.Equal(Math.Min(2, scheduler.Pending), scheduler.RunTick(++tick));
            }

            Assert.Equal(5, tick);
            Assert.Equal(4, scheduler.MaxWaitTicks);

            // Те же пути, что без бюджета: бюджет меняет только такт.
            var finder = new PathFinder();
            var direct = new PathBuffer();
            for (int i = 0; i < N; i++)
            {
                PathRequest r = bench.Requests[i];
                finder.Find(bench.Terrain, r.Start, r.Goal, r.Mode, direct);
                Assert.Equal(PathBench.Hash(0, direct), PathBench.Hash(0, results[i]));
            }
        }

        [Fact]
        public void D02_S9_2_SmallBench_40PerTick_RealClock()
        {
            // Короткий бенч для CI (256², 100 тактов, сборка Debug, машина под нагрузкой): только числа в вывод.
            // Пороги по времени — в D02_S9_2_Big_* (Release).
            PathBench bench = PathBench.Build(256, Seed);
            PathBench.Report report = bench.RunScheduler(100, new StopwatchClock());
            _out.WriteLine(report.ToString());
            Assert.Equal(100 * PathBench.PerTick, report.Completed);
        }

        // ---------- Бенч 1024² (Category=Big) ----------

        [Fact]
        [Trait("Category", "Big")]
        public void D02_S9_2_Big_Bench1024_1000Ticks40Searches()
        {
            var sw = Stopwatch.StartNew();
            PathBench bench = PathBench.Build(1024, Seed, 1000 * PathBench.PerTick);
            _out.WriteLine($"мир 1024², стены {bench.WallShare:P1} (одиночные скалы {PathBench.RockPermille / 10.0:F1} %, отрезки стен, {bench.Boxes} коробок), зерно {Seed:x}; подготовка {sw.ElapsedMilliseconds} мс");

            // Прогрев JIT.
            bench.RunDirect(2000, new PathFinder());

            PathBench.Report direct = bench.RunDirect(bench.Requests.Length, new PathFinder());
            _out.WriteLine("поиски подряд, по одному: " + direct);
            PathBench.Report noFill = bench.RunDirect(bench.Requests.Length, new PathFinder(enclosureFillLimit: 0));
            _out.WriteLine("то же без проверки замкнутой области: " + noFill);
            PathBench.Report sched = bench.RunScheduler(1000, new StopwatchClock());
            _out.WriteLine("такты, 40 запросов за такт, бюджет 40 и 3 мс: " + sched);

            Assert.Equal(direct.Found + direct.Edge + direct.Partial + direct.Enclosed, sched.Completed);
#if DEBUG
            // Сборка Debug втрое медленнее Release (замер 0-023): пороги времени — только в Release,
            // как поедет сервер: dotnet test -c Release --filter FullyQualifiedName~D02_S9_2_Big.
            _out.WriteLine("Debug: пороги 3 мс и 5 тактов не проверяются — запускать в Release.");
#else
            Assert.True(sched.MeanTickUs <= 3000, $"среднее за такт {sched.MeanTickUs:F0} мкс");
            Assert.True(sched.MaxWaitTicks <= 5, $"ожидание {sched.MaxWaitTicks} тактов");
#endif
        }

        // ---------- Помощники ----------

        private static PathBuffer[] Buffers(int n)
        {
            var results = new PathBuffer[n];
            for (int i = 0; i < n; i++)
            {
                results[i] = new PathBuffer();
            }

            return results;
        }

        private static TileCoord[] Steps(PathBuffer path) => path.Steps.AsSpan(0, path.Length).ToArray();

        private static void Box(GridTerrain map, TileCoord center, int radius)
        {
            for (int d = -radius; d <= radius; d++)
            {
                map.Wall(center.X + d, center.Y - radius);
                map.Wall(center.X + d, center.Y + radius);
                map.Wall(center.X - radius, center.Y + d);
                map.Wall(center.X + radius, center.Y + d);
            }
        }

        private static void AssertConnected(MapShape shape, TileCoord start, PathBuffer path)
        {
            TileCoord prev = start;
            for (int i = 0; i < path.Length; i++)
            {
                Int2 d = Torus.Delta(shape, prev, path.Steps[i]);
                Assert.True(Math.Abs(d.X) <= 1 && Math.Abs(d.Y) <= 1 && (d.X != 0 || d.Y != 0), $"разрыв пути на шаге {i}: {prev} → {path.Steps[i]}");
                prev = path.Steps[i];
            }
        }
    }

    /// <summary>Искусственная карта для тестов пути: множители пешего и летуна, счётчик чтений.</summary>
    internal sealed class GridTerrain : IPathTerrain
    {
        private readonly byte[] _walk;
        private readonly byte[] _fly;
        private readonly bool[] _read;

        public GridTerrain(MapShape shape)
        {
            Shape = shape;
            _walk = new byte[shape.Width * shape.Height];
            _fly = new byte[_walk.Length];
            _read = new bool[_walk.Length];
            _walk.AsSpan().Fill(PathCost.Normal);
            _fly.AsSpan().Fill(PathCost.Normal);
        }

        public MapShape Shape { get; }

        public int Reads { get; private set; }

        public void BeginSearch()
        {
        }

        public byte CostOf(TileCoord tile, PathMode mode)
        {
            int i = Index(tile);
            Reads++;
            _read[i] = true;
            return mode == PathMode.Flyer ? _fly[i] : _walk[i];
        }

        public byte Walk(TileCoord t) => _walk[Index(t)];

        public void Wall(int x, int y)
        {
            int i = Index(new TileCoord(x, y));
            _walk[i] = PathCost.Blocked;
            _fly[i] = PathCost.Blocked;
        }

        public void Open(TileCoord t)
        {
            int i = Index(t);
            _walk[i] = PathCost.Normal;
            _fly[i] = PathCost.Normal;
        }

        public void Slow(int x, int y) => _walk[Index(new TileCoord(x, y))] = PathCost.Slow;

        /// <summary>Наибольшее расстояние по Чебышёву (по тору) от <paramref name="center"/> до прочитанного тайла.</summary>
        public int MaxReadDistance(TileCoord center)
        {
            int max = 0;
            for (int i = 0; i < _read.Length; i++)
            {
                if (_read[i])
                {
                    Int2 d = Torus.Delta(Shape, center, new TileCoord(i % Shape.Width, i / Shape.Width));
                    max = Math.Max(max, Math.Max(Math.Abs(d.X), Math.Abs(d.Y)));
                }
            }

            return max;
        }

        private int Index(TileCoord t)
        {
            TileCoord w = Torus.Wrap(Shape, t);
            return w.Y * Shape.Width + w.X;
        }
    }

    /// <summary>Часы, которые сдвигаются на шаг (мкс) при каждом взгляде.</summary>
    internal sealed class StepClock : IMonotonicClock
    {
        private readonly long _step;
        private long _now;

        public StepClock(long stepMicroseconds)
        {
            _step = stepMicroseconds;
        }

        public long Frequency => 1_000_000;

        public long Timestamp
        {
            get
            {
                long t = _now;
                _now += _step;
                return t;
            }
        }
    }

    /// <summary>Монотонные часы хоста.</summary>
    internal sealed class StopwatchClock : IMonotonicClock
    {
        public long Timestamp => Stopwatch.GetTimestamp();

        public long Frequency => Stopwatch.Frequency;
    }

    /// <summary>
    /// Бенч пути (0-023, шаг 7): мир-тор W² из <see cref="Simulation"/> со случайными стенами, запросы на 5–40 тайлов:
    /// 55 % — обычные, 15 % — через шов, 15 % — в замкнутых коробках, 15 % — недостижимые (цель — стена).
    /// </summary>
    internal sealed class PathBench
    {
        public const int PerTick = 40;

        /// <summary>Одиночные скалы, ‰ тайлов.</summary>
        public const int RockPermille = 120;

        private PathBench(Simulation sim, PathRequest[] requests, double wallShare, int boxes)
        {
            Sim = sim;
            Terrain = new SimulationPathTerrain(sim);
            Requests = requests;
            WallShare = wallShare;
            Boxes = boxes;
        }

        public Simulation Sim { get; }

        public SimulationPathTerrain Terrain { get; }

        public PathRequest[] Requests { get; }

        public double WallShare { get; }

        public int Boxes { get; }

        public static PathBench Build(int size, ulong seed, int requestCount = 1000)
        {
            var shape = MapShape.World(size);
            var world = new WorldModel(shape, seed);
            world.CreateAllChunks();
            var rng = new DetRandom(seed);
            var blocked = new bool[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool rock = rng.NextInt(1000) < RockPermille;
                    world.SetBaseTile(new TileCoord(x, y), FloorId.Grass, rock ? ObjectId.Rock : ObjectId.Empty);
                    blocked[y * size + x] = rock;
                }
            }

            // Отрезки стен 3–12 тайлов: по одному на 64 тайла.
            int segments = size * size / 64;
            for (int s = 0; s < segments; s++)
            {
                int x = rng.NextInt(size);
                int y = rng.NextInt(size);
                int len = rng.NextInt(3, 13);
                bool horizontal = rng.NextBool();
                for (int k = 0; k < len; k++)
                {
                    Put(world, blocked, size, horizontal ? x + k : x, horizontal ? y : y + k, ObjectId.StoneWall);
                }
            }

            // Коробки: стена-кольцо радиуса 2–5, внутри чисто. Одна на 32² тайлов.
            int boxCount = size * size / 1024;
            var boxes = new TileCoord[boxCount];
            for (int b = 0; b < boxCount; b++)
            {
                int cx = rng.NextInt(size);
                int cy = rng.NextInt(size);
                int r = rng.NextInt(2, 6);
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        bool ring = Math.Abs(dx) == r || Math.Abs(dy) == r;
                        Put(world, blocked, size, cx + dx, cy + dy, ring ? ObjectId.StoneWall : ObjectId.Empty);
                    }
                }

                boxes[b] = new TileCoord(cx, cy);
            }

            int walls = 0;
            foreach (bool w in blocked)
            {
                walls += w ? 1 : 0;
            }

            var requests = new PathRequest[requestCount];
            for (int i = 0; i < requestCount; i++)
            {
                int kind = rng.NextInt(100);
                TileCoord start;
                TileCoord goal;
                if (kind < 15)
                {
                    // Цель в коробке, враг снаружи.
                    TileCoord c = boxes[rng.NextInt(boxCount)];
                    goal = c;
                    start = FreeNear(ref rng, blocked, size, c, 8, 40);
                }
                else
                {
                    if (kind < 30)
                    {
                        // Через шов: старт в 20 тайлах у шва по X или Y, цель — за швом.
                        int a = size - 1 - rng.NextInt(20);
                        int b = rng.NextInt(size);
                        start = FreeNear(ref rng, blocked, size, rng.NextBool() ? new TileCoord(a, b) : new TileCoord(b, a), 0, 0);
                    }
                    else
                    {
                        start = FreeNear(ref rng, blocked, size, new TileCoord(rng.NextInt(size), rng.NextInt(size)), 0, 0);
                    }

                    bool wantBlocked = kind >= 85;
                    goal = start;
                    for (int tries = 0; tries < 1000; tries++)
                    {
                        TileCoord g = Offset(ref rng, size, start, 5, 40, seam: kind < 30);
                        if (blocked[g.Y * size + g.X] == wantBlocked)
                        {
                            goal = g;
                            break;
                        }
                    }
                }

                requests[i] = new PathRequest(i % PerTick, start, goal, PathMode.Walker, 0);
            }

            return new PathBench(new Simulation(world), requests, (double)walls / blocked.Length, boxCount);
        }

        private static void Put(WorldModel world, bool[] blocked, int size, int x, int y, ushort obj)
        {
            TileCoord t = Torus.Wrap(world.Shape, new TileCoord(x, y));
            world.SetBaseTile(t, FloorId.Grass, obj);
            blocked[t.Y * size + t.X] = obj != ObjectId.Empty;
        }

        /// <summary>Свободный тайл на расстоянии (Чебышёв) от min до max от центра; min = max = 0 — ближайший свободный по спирали.</summary>
        private static TileCoord FreeNear(ref DetRandom rng, bool[] blocked, int size, TileCoord center, int min, int max)
        {
            if (max == 0)
            {
                for (int r = 0; r < size; r++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        for (int dx = -r; dx <= r; dx++)
                        {
                            int x = (center.X + dx) & (size - 1);
                            int y = (center.Y + dy) & (size - 1);
                            if (!blocked[y * size + x])
                            {
                                return new TileCoord(x, y);
                            }
                        }
                    }
                }

                throw new InvalidOperationException("Нет свободного тайла.");
            }

            for (int tries = 0; tries < 10000; tries++)
            {
                TileCoord t = Offset(ref rng, size, center, min, max, seam: false);
                if (!blocked[t.Y * size + t.X])
                {
                    return t;
                }
            }

            throw new InvalidOperationException("Нет свободного тайла.");
        }

        private static TileCoord Offset(ref DetRandom rng, int size, TileCoord from, int min, int max, bool seam)
        {
            while (true)
            {
                int dx = rng.NextInt(-max, max + 1);
                int dy = rng.NextInt(-max, max + 1);
                if (seam)
                {
                    // В сторону шва: старт у правого/нижнего края, цель — с той стороны.
                    dx = from.X >= size - 20 ? Math.Abs(dx) : dx;
                    dy = from.Y >= size - 20 ? Math.Abs(dy) : dy;
                }

                int cheb = Math.Max(Math.Abs(dx), Math.Abs(dy));
                if (cheb >= min && cheb <= max)
                {
                    return new TileCoord((from.X + dx) & (size - 1), (from.Y + dy) & (size - 1));
                }
            }
        }

        public static ulong Hash(ulong h, PathBuffer path)
        {
            h = Orpheus.Core.Rng.SplitMix64.Mix(h ^ ((ulong)path.Status << 56) ^ ((ulong)(uint)path.Length << 24) ^ (uint)path.Cost);
            for (int i = 0; i < path.Length; i++)
            {
                h = Orpheus.Core.Rng.SplitMix64.Mix(h ^ ((ulong)(uint)path.Steps[i].X << 32) ^ (uint)path.Steps[i].Y);
            }

            return h;
        }

        /// <summary>Итоги прогона бенча.</summary>
        internal sealed class Report
        {
            public long Completed;
            public long Found;
            public long Partial;
            public long Enclosed;
            public long Edge;
            public long Deferred;
            public long TimeLimitedTicks;
            public long MaxWaitTicks;
            public double MeanExpanded;
            public double MeanReads;
            public double MeanFilled;
            public int P99Expanded;
            public int MaxExpanded;
            public double MeanSearchUs;
            public double P99SearchUs;
            public double MaxSearchUs;
            public int Ticks;
            public double MeanTickUs;
            public double P99TickUs;
            public double MaxTickUs;
            public ulong Hash;

            public override string ToString()
            {
                string s = $"поисков {Completed}: дошли {Share(Found)}, частичных {Share(Partial)}, край окна {Share(Edge)}, замкнутых {Share(Enclosed)}; " +
                           $"чтений карты на поиск {MeanReads:F0}, залито {MeanFilled:F0}; узлов на поиск ср {MeanExpanded:F0}";
                if (Ticks == 0)
                {
                    s += $" / p99 {P99Expanded} / макс {MaxExpanded}; мкс на поиск ср {MeanSearchUs:F1} / p99 {P99SearchUs:F1} / макс {MaxSearchUs:F1}; 40 поисков ≈ {MeanSearchUs * PerTick / 1000:F2} мс";
                }
                else
                {
                    s += $"; тактов {Ticks}: мс за такт ср {MeanTickUs / 1000:F3} / p99 {P99TickUs / 1000:F3} / макс {MaxTickUs / 1000:F3}; " +
                         $"отложено (запросо-тактов) {Deferred}, тактов с пределом 3 мс {TimeLimitedTicks}, наибольшее ожидание {MaxWaitTicks} т.";
                }

                return Ticks == 0 ? s + $"; хэш {Hash:x16}" : s;
            }

            private string Share(long n) => $"{n} ({(Completed == 0 ? 0 : 100.0 * n / Completed):F1} %)";
        }

        /// <summary>Поиски подряд без очереди: время и узлы каждого.</summary>
        public Report RunDirect(int count, PathFinder finder)
        {
            var path = new PathBuffer();
            var us = new double[count];
            var expanded = new int[count];
            var report = new Report();
            double toUs = 1_000_000.0 / Stopwatch.Frequency;
            for (int i = 0; i < count; i++)
            {
                PathRequest r = Requests[i % Requests.Length];
                long t0 = Stopwatch.GetTimestamp();
                PathStatus status = finder.Find(Terrain, r.Start, r.Goal, r.Mode, path);
                us[i] = (Stopwatch.GetTimestamp() - t0) * toUs;
                expanded[i] = path.Expanded;
                Count(report, status);
                report.Hash = Hash(report.Hash, path);
            }

            report.Completed = count;
            report.MeanReads = (double)finder.TotalTerrainReads / count;
            report.MeanFilled = (double)finder.TotalFilled / count;
            FillExpanded(report, expanded);
            Array.Sort(us);
            double sum = 0;
            foreach (double u in us)
            {
                sum += u;
            }

            report.MeanSearchUs = sum / count;
            report.P99SearchUs = us[(int)(count * 0.99)];
            report.MaxSearchUs = us[count - 1];
            return report;
        }

        /// <summary>Такты: в каждый 40 новых запросов (по порядку), очередь с бюджетом; потом очередь дорабатывается.</summary>
        public Report RunScheduler(int ticks, IMonotonicClock clock)
        {
            var results = new PathBuffer[PerTick];
            for (int i = 0; i < PerTick; i++)
            {
                results[i] = new PathBuffer();
            }

            var finder = new PathFinder();
            var scheduler = new PathScheduler(finder, Terrain, results, ticks * PerTick + PerTick, PerTick, clock);
            var tickUs = new double[ticks];
            var report = new Report();
            double toUs = 1_000_000.0 / clock.Frequency;
            long tick = 0;
            int next = 0;
            while (tick < ticks || scheduler.Pending > 0)
            {
                tick++;
                for (int k = 0; k < PerTick && tick <= ticks; k++)
                {
                    PathRequest r = Requests[next++ % Requests.Length];
                    scheduler.Enqueue(r.Owner, r.Start, r.Goal, r.Mode, tick);
                }

                scheduler.RunTick(tick);
                if (tick <= ticks)
                {
                    tickUs[tick - 1] = scheduler.LastTickElapsed * toUs;
                }
            }

            report.Completed = scheduler.Completed;
            report.Partial = scheduler.PartialCount;
            report.Enclosed = scheduler.EnclosedCount;
            report.Edge = scheduler.WindowEdgeCount;
            report.Found = report.Completed - report.Partial - report.Enclosed - report.Edge;
            report.Deferred = scheduler.DeferredTotal;
            report.TimeLimitedTicks = scheduler.TimeLimitedTicks;
            report.MaxWaitTicks = scheduler.MaxWaitTicks;
            report.MeanExpanded = (double)finder.TotalExpanded / Math.Max(1, report.Completed);
            report.MeanReads = (double)finder.TotalTerrainReads / Math.Max(1, report.Completed);
            report.MeanFilled = (double)finder.TotalFilled / Math.Max(1, report.Completed);
            report.Ticks = ticks;
            Array.Sort(tickUs);
            double sum = 0;
            foreach (double u in tickUs)
            {
                sum += u;
            }

            report.MeanTickUs = sum / ticks;
            report.P99TickUs = tickUs[(int)(ticks * 0.99)];
            report.MaxTickUs = tickUs[ticks - 1];
            return report;
        }

        private static void Count(Report report, PathStatus status)
        {
            switch (status)
            {
                case PathStatus.Found:
                    report.Found++;
                    break;
                case PathStatus.Partial:
                    report.Partial++;
                    break;
                case PathStatus.Enclosed:
                    report.Enclosed++;
                    break;
                case PathStatus.WindowEdge:
                    report.Edge++;
                    break;
            }
        }

        private static void FillExpanded(Report report, int[] expanded)
        {
            long sum = 0;
            foreach (int e in expanded)
            {
                sum += e;
            }

            report.MeanExpanded = (double)sum / expanded.Length;
            Array.Sort(expanded);
            report.P99Expanded = expanded[(int)(expanded.Length * 0.99)];
            report.MaxExpanded = expanded[expanded.Length - 1];
        }
    }
}
