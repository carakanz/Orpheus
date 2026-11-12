using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Orpheus.Bots;
using Orpheus.Core.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Server;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Приёмка 0.2 (docs/08, 3.0; карточка 0-027): бот <c>run_seam</c> на 1024² — экватор и меридиан в обе стороны,
    /// координата после круга совпадает со стартом точно; наблюдатель у шва видит его без скачка — сдвиг показанной позиции
    /// за 1/30 с ≤ 6 т/с × 1/30 с × 1,1 = 0,22 тайла.
    /// </summary>
    public class RunSeamTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const long FrameUs = 1_000_000 / 60;

        /// <summary>Зерно тестового мира (заглушка 1024², полосы расчищаются).</summary>
        private const ulong Seed = 42;

        private readonly ITestOutputHelper _out;

        public RunSeamTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- Правила: сценарий в режиме симуляции ----------

        /// <summary>
        /// Режим симуляции (решение на такт): 4 круга по 1024 тайла на ровном мире — невязка каждого круга (0, 0), круг —
        /// 5120 тактов бега (1024 × 5) плюс подход; конец — точка появления.
        /// </summary>
        [Fact]
        public void D08_S3_0_2_RunSeamBot_Sim_FourLaps_ExactToTheUnit()
        {
            var shape = MapShape.World(1024);
            var sim = new Simulation(FlatWorldGenerator.Instance.Generate(shape, 1), 4);
            var start = new WorldPos(U / 2, U / 2);
            int id = sim.AddEntity(start);
            var bot = (RunSeamBot)SimBot.Create(RunSeamBot.Name, id, 0, 1, shape);
            long tick = 0;
            while (!bot.Done && tick < 4 * 5200)
            {
                int slot = sim.SlotOf(id);
                sim.SetInputAt(slot, bot.Think(++tick, sim.Entities[slot]));
                sim.Step();
            }

            Assert.True(bot.Done, $"кругов {bot.Laps.Count} за {tick} тактов");
            foreach (RunSeamLap l in bot.Laps)
            {
                _out.WriteLine(l.ToString());
                Assert.Equal(new Int2(0, 0), l.Residual);
                Assert.InRange(l.Steps, MovementTick.TicksPerLap(1024), MovementTick.TicksPerLap(1024) + 5);
                Assert.Equal(0, l.MaxCrossUnits);
            }

            Assert.Equal(new[] { "east", "west", "north", "south" }, Array.ConvertAll(ToArray(bot.Laps), l => l.DirectionName));
            Assert.Equal(start, sim.GetEntity(id).Mover.Position);
            Assert.True(bot.AllExact);
        }

        /// <summary>
        /// Невязка ловится: позиции подаются кадрами (25,6 unit, остаток в десятых), но на переходе шва x теряется 1 unit
        /// (1/256 тайла) — круг на восток кончается с невязкой (−1, 0), сценарий не выполнен. Без потери — (0, 0).
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(-1)]
        public void D08_S3_0_2_RunSeamBot_Frames_SeamLosesOneUnit_ResidualCaught(int lossPerCrossing)
        {
            var shape = MapShape.World(1024);
            var bot = new RunSeamBot(1, shape);
            var pos = new WorldPos(U / 2, U / 2);
            var mover = new Mover(pos);
            int frames = 0;
            bool pending = false;
            while (bot.Laps.Count == 0 && frames < 20_000)
            {
                InputFrame f = bot.ThinkFrame(mover.Position, settled: true);
                WorldPos before = mover.Position;
                mover.StepFrame(shape, f.Direction, Collision.Empty);
                pending |= Math.Abs(mover.Position.X - (long)before.X) > shape.WidthUnits / 2;
                if (pending && mover.Position.X >= Math.Abs(lossPerCrossing))
                {
                    // Переход шва: «сломанный заворот» теряет unit (не у самой кромки — чтобы не перейти шов обратно).
                    mover.Position = new WorldPos(mover.Position.X - lossPerCrossing, mover.Position.Y);
                    pending = false;
                }

                frames++;
            }

            Assert.Single(bot.Laps);
            RunSeamLap lap = bot.Laps[0];
            _out.WriteLine($"{lap}; кадров {frames}");
            Assert.Equal(new Int2(-lossPerCrossing, 0), lap.Residual);
        }

        /// <summary>
        /// Полосы тестового мира (<c>--debug-run-lanes</c>): на заглушке ни строка, ни столбец целиком не свободны; после
        /// расчистки строка 0 и столбцы 0, 2, …, 14 проходимы, 8 мест появления на них (тайл (2i, 0)); вторая расчистка того же
        /// зерна даёт тот же хэш генерации (бот находит мир сервера), без расчистки — другой.
        /// </summary>
        [Fact]
        public void D08_S3_0_2_RunSeamLanes_ClearedLanes_SpawnOnLanes_SameHashAfterSameClear()
        {
            var shape = MapShape.World(1024);
            WorldModel plain = WorldGenModule.Current.Generate(shape, Seed);
            WorldModel world = WorldGenModule.Current.Generate(shape, Seed);
            var before = new Simulation(plain, 1);
            Assert.Contains(true, Lane(before, shape, row: true, 0)); // на заглушке строка 0 не свободна

            int changed = RunSeamLanes.Clear(world);
            var sim = new Simulation(world, 1);
            Assert.True(changed > 0);
            Assert.DoesNotContain(true, Lane(sim, shape, row: true, 0));
            for (int x = 0; x < 2 * RunSeamLanes.Places; x += 2)
            {
                Assert.DoesNotContain(true, Lane(sim, shape, row: false, x));
            }

            for (int i = 0; i < RunSeamLanes.Places; i++)
            {
                WorldPos p = ServerRun.SpawnPoint(shape, i, RunSeamLanes.SpawnCenter);
                Assert.Equal(new TileCoord(2 * i, 0), Torus.TileOf(shape, p));
            }

            ulong hash = StateHash.Generation(world);
            Assert.NotEqual(StateHash.Generation(plain), hash);
            RunSeamLanes.Clear(plain);
            Assert.Equal(hash, StateHash.Generation(plain));
            Assert.Equal(0, RunSeamLanes.Clear(plain));
        }

        /// <summary>Ключ сервера: только мир в памяти.</summary>
        [Fact]
        public void D08_S3_0_2_ServerKey_DebugRunLanes_OnlyInMemoryWorld()
        {
            ServerOptions o = ServerOptions.Parse(new[] { "--create", "--seed", "1", "--debug-run-lanes" });
            Assert.True(o.DebugRunLanes);
            Assert.False(o.NoSpawn);
            Assert.True(ServerOptions.Parse(new[] { "--create", "--no-spawn" }).NoSpawn);
            Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--world", "w", "--create", "--debug-run-lanes" }));
            Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--create", "--debug-run-lanes", "--spawn-seam" }));
            Assert.Throws<ArgumentException>(() => SimBot.Create(RunSeamBot.Name, 1, 0, 1));
        }

        // ---------- Сценарий: связь в памяти, имитатор RTT 100 ----------

        /// <summary>
        /// Замер к вопросу [док] 0-027 (без порога на сдвиг 0,22): почему буквальный предел «за 1/30 с ≤ 0,22 тайла» не держится
        /// по сети — бегущий на сервере сам идёт неровно: кадры ввода шагаются по приходу (07 2.7), при разбросе σ 10 мс —
        /// 0…5 кадров за такт. Показ у наблюдателя повторяет траекторию снимков точно: скачок сверх неё — 0 во всех случаях.
        /// </summary>
        [Theory]
        [InlineData(0, 0)]
        [InlineData(10, 0)]
        [InlineData(0, 1)]
        public void D08_S3_0_2_Measure_ServerFramesPerTick_ObserverFollowsSnapshots(int jitter, int loss)
        {
            var shape = MapShape.World(1024);
            WorldModel world = WorldGenModule.Current.Generate(shape, Seed);
            RunSeamLanes.Clear(world);
            var sim = new Simulation(world, maxEntities: 8);
            using var st = LoopbackTransport.CreateServer(8);
            var server = new NetServer(st, sim, n => ServerRun.SpawnPoint(shape, n, RunSeamLanes.SpawnCenter), 4, NetModule.Version);
            var bots = new NetBot[2];
            string[] scripts = { RunSeamBot.Name, SeamWatchBot.Name };
            for (int i = 0; i < 2; i++)
            {
                var t = new NetSimTransport(LoopbackTransport.ConnectTo(st), new NetSimSettings { DelayMs = 50, JitterMs = jitter, LossPpm = NetSimSettings.PercentToPpm(loss), Seed = 27 + (ulong)i });
                bots[i] = new NetBot(new NetClient(t, LoopbackTransport.ServerPeer, $"b{i}", NetModule.Version), scripts[i], i);
            }

            long frame = 0;
            var hist = new int[8];
            WorldPos prev = default;
            bool has = false;
            for (int ticks = 0; ticks < 3000; ticks++)
            {
                for (int f = 0; f < 2; f++, frame++)
                {
                    foreach (NetBot b in bots) b.Frame(frame * FrameUs);
                }

                server.Poll(frame * FrameUs);
                sim.Step();
                server.SendSnapshots();
                if (bots[0].Client.EntityId > 0 && sim.SlotOf(bots[0].Client.EntityId) >= 0)
                {
                    WorldPos p = sim.GetEntity(bots[0].Client.EntityId).Mover.Position;
                    if (has) hist[Math.Min(7, (int)Math.Round(Math.Abs(Torus.Delta(shape, prev, p).X) / 25.6))]++;
                    prev = p; has = true;
                }
            }

            _out.WriteLine($"σ {jitter} мс, потери {loss} %: тактов с 0…7 кадрами бегущего на сервере: {string.Join(" ", hist)}");
            _out.WriteLine("наблюдатель: " + BotRun.WatchText(bots[1].Watch!));
            Assert.True(bots[1].Watch!.Steps30 > 1000);
            Assert.Equal(0, bots[1].Watch!.OverExcess);
        }

        /// <summary>
        /// Приёмка 0.2 ускоренно (связь в памяти, виртуальное время): сервер на мире 1024² с полосами, бегущий
        /// <c>run_seam</c> и наблюдатель <c>seam_watch</c> — NetBot, как в Orpheus.Bots, через имитатор RTT 100, σ 10, потери 1 %.
        /// Бегущий — 4 круга (восток, запад, север, юг), невязка каждого 0 unit, сверка кадров без несовпадений; наблюдатель
        /// у угла швов — сдвиг показанной позиции за 1/30 с сверх траектории снимков ≤ 0,022 тайла во всех кадрах (сдвиг против
        /// 0,22 буквально при σ 10 не держится — в выводе, вопрос [док] 0-027), бегущий виден по обе стороны шва x и шва y,
        /// переходы швов в показе есть.
        /// </summary>
        [Fact]
        public void D08_S3_0_2_RunSeam_Loopback_Rtt100_Sigma10_Loss1_FourLapsExact_ObserverNoSeamJump()
        {
            LoopRun r = RunLoopback(jitter: 10, lossPercent: 1);
            AssertLoopRun(r);
        }

        /// <summary>
        /// Приёмка 0.2 буквально (проверка 0-027, правка 1): связь в памяти, RTT 100 <b>без разброса и потерь</b>, 4 круга (≈ 690 с
        /// виртуальных, в тесте — доли секунды). У наблюдателя сдвиг показанной позиции бегущего за 1/30 с <b>времени показа</b>
        /// ≤ 6 т/с × 1/30 с × 1,1 + квант позиции в снимке 1/32 т ≈ 0,251 тайла (<see cref="SeamWatch.LimitShown30Tiles"/>;
        /// повторная проверка 0-027: буквальные 0,22 по местным часам держались на 0,001 т и краснели от штатной подстройки
        /// часов показа ±1 %, мутация П-5) во <b>всех</b> окнах (<c>Over30 == 0</c>), кроме первой секунды показа (<see cref="SeamWatch.WarmupUs"/>: первые
        /// кадры ввода бегущего приходят пачкой); переходы обоих швов в показе. Ловит рывок шва в симуляции и в снимке, которого
        /// не видит «скачок сверх траектории» (мутации П-1а, П-1б, П-2).
        /// </summary>
        [Fact]
        public void D08_S3_0_2_RunSeam_Loopback_Rtt100_Clean_FourLapsExact_ObserverLiteralStep022()
        {
            LoopRun r = RunLoopback(jitter: 0, lossPercent: 0);
            AssertLoopRun(r);
            Assert.True(r.Watch.Over30 == 0, $"сдвиг за 1/30 с показа сверх {SeamWatch.LimitShown30Tiles:0.000} тайла: {r.Watch.Over30} окон, наибольший {r.Watch.MaxStep30Tiles:0.0000} т (по местным часам {r.Watch.MaxStep30LocalTiles:0.0000} т)");
            Assert.True(r.Watch.MaxStep30Tiles <= SeamWatch.LimitShown30Tiles, $"наибольший сдвиг {r.Watch.MaxStep30Tiles:0.0000} т");
            Assert.True(r.Watch.PassedLiteral);
        }

        private sealed class LoopRun
        {
            public LoopRun(NetBot[] bots, NetSimTransport[] sims, int ticks, double seconds, bool observerQuitEarly)
            {
                Bots = bots;
                Sims = sims;
                Ticks = ticks;
                Seconds = seconds;
                ObserverQuitEarly = observerQuitEarly;
            }

            public NetBot[] Bots { get; }
            public NetSimTransport[] Sims { get; }
            public int Ticks { get; }
            public double Seconds { get; }
            public bool ObserverQuitEarly { get; }
            public RunSeamBot Run => Bots[0].Runner!;
            public SeamWatch Watch => Bots[1].Watch!;
        }

        // Сервер, бегущий и наблюдатель на связи в памяти: такт — 2 кадра ботов, опрос, шаг, снимки; до 4 кругов + 200 тактов.
        private LoopRun RunLoopback(int jitter, int lossPercent)
        {
            var watch = Stopwatch.StartNew();
            var shape = MapShape.World(1024);
            WorldModel world = WorldGenModule.Current.Generate(shape, Seed);
            RunSeamLanes.Clear(world);
            var sim = new Simulation(world, maxEntities: 8);
            using var st = LoopbackTransport.CreateServer(8);
            var server = new NetServer(st, sim, n => ServerRun.SpawnPoint(shape, n, RunSeamLanes.SpawnCenter), 4, NetModule.Version);
            var bots = new NetBot[2];
            var sims = new NetSimTransport[2];
            string[] scripts = { RunSeamBot.Name, SeamWatchBot.Name };
            for (int i = 0; i < 2; i++)
            {
                sims[i] = new NetSimTransport(LoopbackTransport.ConnectTo(st), new NetSimSettings
                {
                    DelayMs = 50, JitterMs = jitter, LossPpm = NetSimSettings.PercentToPpm(lossPercent), Seed = 27 + (ulong)i,
                });
                bots[i] = new NetBot(new NetClient(sims[i], LoopbackTransport.ServerPeer, $"b{i}", NetModule.Version), scripts[i], i);
            }

            long frame = 0;
            int ticks = 0;
            int doneAt = -1;
            bool observerQuitEarly = false;
            const int MaxTicks = 4 * 5120 + 3000;
            while (ticks < MaxTicks && (doneAt < 0 || ticks - doneAt < 200))
            {
                for (int f = 0; f < 2; f++, frame++)
                {
                    foreach (NetBot b in bots)
                    {
                        b.Frame(frame * FrameUs);
                    }
                }

                server.Poll(frame * FrameUs);
                sim.Step();
                server.SendSnapshots();
                ticks++;
                observerQuitEarly |= doneAt < 0 && bots[1].Finished; // бегущий за краем обзора — не конец наблюдения
                if (doneAt < 0 && bots[0].Runner?.Done == true)
                {
                    doneAt = ticks;
                }

                if (doneAt >= 0 && ticks - doneAt == 30)
                {
                    bots[0].Client.Disconnect(); // бегущий вышел у наблюдателя — тот кончает сам через 3 с
                }
            }

            var r = new LoopRun(bots, sims, ticks, watch.Elapsed.TotalSeconds, observerQuitEarly);
            foreach (RunSeamLap l in r.Run.Laps)
            {
                _out.WriteLine(l.ToString());
            }

            _out.WriteLine($"σ {jitter} мс, потери {lossPercent} %: тактов {ticks} ({ticks / 30.0:0.0} с виртуальных), за {r.Seconds:0.0} с; имитатор бегущего: потеряно " +
                           $"{sims[0].SendStats.Lost}/{sims[0].SendStats.Packets} и {sims[0].ReceiveStats.Lost}/{sims[0].ReceiveStats.Packets}");
            _out.WriteLine($"бегущий: сверок кадров {r.Run.FrameChecks}, несовпадений {r.Run.FrameMismatches}, не шагнуто {r.Run.FramesShort}{(r.Run.FirstMismatch != null ? "; " + r.Run.FirstMismatch : "")}");
            _out.WriteLine("наблюдатель: " + BotRun.WatchText(r.Watch));
            return r;
        }

        private static void AssertLoopRun(LoopRun r)
        {
            RunSeamBot run = r.Run;
            SeamWatch w = r.Watch;
            Assert.True(run.Done, $"кругов {run.Laps.Count} за {r.Ticks} тактов");
            Assert.True(run.AllExact, $"невязка до {run.MaxResidualUnits} unit");
            foreach (RunSeamLap l in run.Laps)
            {
                Assert.True(l.Approaches == 1, $"{l} — подходов не 1");
            }

            Assert.Equal(run.Origin, r.Bots[0].Client.Latest.Self.Position);
            Assert.True(run.FrameChecks > 10_000, $"сверок кадров {run.FrameChecks}");
            Assert.True(run.FrameMismatches == 0, $"сверка кадров: {run.FrameMismatches} несовпадений; {run.FirstMismatch}");
            Assert.True(run.Passed);
            Assert.Equal(NetClientState.InGame, r.Bots[1].Client.State);
            Assert.False(r.ObserverQuitEarly, "наблюдатель кончил, пока бегущий был на круге");
            Assert.True(r.Bots[1].Finished, "наблюдатель не заметил, что бегущий вышел");

            Assert.True(w.Steps30 > 1000, $"замеров {w.Steps30}");
            Assert.Equal(0, w.OverExcess); // скачка показа сверх траектории снимков нет (решение 0-027)
            Assert.True(w.MaxExcess30Tiles <= SeamWatch.LimitExcessTiles, $"скачок {w.MaxExcess30Tiles:0.0000} тайла");
            Assert.True(w.ViewEastOfX > 0 && w.ViewWestOfX > 0 && w.ViewSouthOfY > 0 && w.ViewNorthOfY > 0, BotRun.WatchText(w));
            Assert.True(w.CrossingsX >= 2 && w.CrossingsY >= 2, $"переходов шва x {w.CrossingsX}, y {w.CrossingsY}");
            Assert.True(w.Passed);
        }

        // ---------- По UDP: круг на ось (-All) и 4 круга (Soak) ----------

        /// <summary>
        /// Приёмка 0.2 по UDP в `-All` (≈ 6 мин): сервер в процессе теста (<c>--debug-run-lanes</c>, 1024²), бегущий и наблюдатель —
        /// два запуска <see cref="BotRun"/> (свой поток, свой имитатор RTT 100, σ 10, 1 %). Бегущий — по кругу на ось
        /// (<c>--laps axes</c>: восток и север, оба шва). Отключений 0, круги точно, сверка кадров без несовпадений; у наблюдателя
        /// 0 окон 1/30 с со скачком сверх траектории снимков, бегущий виден по обе стороны швов. Полные 4 круга — Soak-тест ниже.
        /// </summary>
        [Fact]
        [Trait("Category", "Big")]
        public Task D08_S3_0_2_RunSeam_Udp_Big_Rtt100_Sigma10_Loss1_LapPerAxisExact_ObserverNoSeamJump() =>
            RunUdp(RunSeamBot.LapsAxes, runnerSeconds: 480);

        /// <summary>
        /// Приёмка 0.2 по UDP целиком (≈ 11,5 мин): то же, что Big-тест, но 4 круга (восток, запад, север, юг). Не в CI и не в
        /// `-All` — <c>tools\ci.cmd -Soak -TimeLimitSec 0</c> или <c>tools\run_seam.ps1</c> перед приёмкой вехи.
        /// </summary>
        [Fact]
        [Trait("Category", "Soak")]
        public Task D08_S3_0_2_RunSeam_Udp_Soak_Rtt100_Sigma10_Loss1_FourLapsExact_ObserverNoSeamJump() =>
            RunUdp(RunSeamBot.LapsAll, runnerSeconds: 840);

        private async Task RunUdp(string laps, int runnerSeconds)
        {
            int port = FreeUdpPort();
            ServerOptions so = ServerOptions.Parse(new[] { "--create", "--seed", Seed.ToString(), "--size", "1024", "--port", port.ToString(), "--debug-run-lanes", "--no-spawn" });
            using var cpu = new CpuLoad();
            using var stopServer = new CancellationTokenSource();
            Task<ServerResult> server = Task.Run(() => ServerRun.Run(so, null, stopServer.Token));
            BotOptions Bots(string script, string seed, int seconds) => BotOptions.Parse(new[]
            {
                "--connect", $"127.0.0.1:{port}", "--count", "1", "--script", script, "--laps", laps, "--rtt", "100", "--jitter", "10", "--loss", "1",
                "--seconds", seconds.ToString(), "--seed", seed, "--name", script,
            });

            BotReport runner;
            BotReport observer;
            try
            {
                Task<BotReport> obs = Task.Run(() => BotRun.Run(Bots(SeamWatchBot.Name, "8", runnerSeconds + 60), null));
                Thread.Sleep(500);
                runner = await Task.Run(() => BotRun.Run(Bots(RunSeamBot.Name, "7", runnerSeconds), null));
                observer = await obs; // бегущий вышел — наблюдатель один и кончает сам через 3 с
            }
            finally
            {
                Thread.Sleep(300);
                stopServer.Cancel();
                Assert.Equal(0, (await server).ExitCode);
            }

            _out.WriteLine($"загрузка: {cpu.Stop()}");
            BotStats rb = runner.Bots[0];
            foreach (RunSeamLap l in rb.Laps!)
            {
                _out.WriteLine(l.ToString());
            }

            SeamWatch w = observer.Bots[0].Watch!;
            _out.WriteLine($"бегущий: {rb.RunSeconds:0.0} с, RTT {rb.RttMs} мс, потеряно {rb.SimSendLost}/{rb.SimSent} и {rb.SimReceiveLost}/{rb.SimReceived}; " +
                           $"сверок кадров {rb.RunFrameChecks}, несовпадений {rb.RunFrameMismatches}, не шагнуто {rb.RunFramesShort}{(rb.RunFirstMismatch != null ? "; " + rb.RunFirstMismatch : "")}");
            _out.WriteLine("наблюдатель: " + BotRun.WatchText(w));
            Assert.Equal(0, runner.Disconnects + observer.Disconnects);
            Assert.Equal(rb.RunPlannedLaps, rb.Laps!.Count);
            Assert.Equal(laps == RunSeamBot.LapsAll ? RunSeamBot.LapCount : 2, rb.RunPlannedLaps);
            Assert.True(rb.RunFrameMismatches == 0, $"сверка кадров: {rb.RunFrameMismatches}; {rb.RunFirstMismatch}");
            Assert.False(rb.ScenarioFailed, "невязка круга ≠ 0 или сверка кадров не сошлась");
            Assert.Equal(0, w.OverExcess);
            Assert.True(w.Passed, BotRun.WatchText(w));
            Assert.Equal(0, runner.ExitCode);
            Assert.Equal(0, observer.ExitCode);
        }

        private static bool[] Lane(Simulation sim, in MapShape shape, bool row, int at)
        {
            var blocked = new bool[row ? shape.Width : shape.Height];
            for (int k = 0; k < blocked.Length; k++)
            {
                blocked[k] = sim.IsBlocked(shape, row ? new TileCoord(k, at) : new TileCoord(at, k));
            }

            return blocked;
        }

        private static RunSeamLap[] ToArray(IReadOnlyList<RunSeamLap> laps)
        {
            var a = new RunSeamLap[laps.Count];
            for (int i = 0; i < a.Length; i++)
            {
                a[i] = laps[i];
            }

            return a;
        }

        private static int FreeUdpPort()
        {
            using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            s.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            return ((System.Net.IPEndPoint)s.LocalEndPoint!).Port;
        }
    }
}
