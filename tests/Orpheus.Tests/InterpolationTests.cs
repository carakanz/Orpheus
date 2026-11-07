using System;
using System.Collections.Generic;
using Orpheus.Core.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Rng;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Server;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Чужие сущности у клиента (docs/07, 2.9; карточка 0-017 по проверке 0-012): <see cref="RemoteInterpolation"/> —
    /// время показа растёт каждый кадр и не идёт назад, задержка 100 мс, шов тора без скачка, потеря снимков без шага
    /// назад; плюс сверка мира по хэшу генерации и появление у шва для net-smoke.
    /// </summary>
    public class InterpolationTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const long TickUs = 1_000_000 / 30;

        private static readonly MapShape Map = MapShape.World(1024);

        private readonly ITestOutputHelper _out;

        public InterpolationTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>
        /// Модель: сущность #7 бежит базовым бегом (51,2 unit за такт) по оси X от <paramref name="startX"/>
        /// (направление <paramref name="dirX"/>); снимок такта n приходит в n × 33,3 мс + 30 мс + разброс; кадры клиента —
        /// через <paramref name="frameUs"/> (с рывками, если задан разброс кадра). Потерянные такты — <paramref name="lost"/>.
        /// </summary>
        private sealed class Run
        {
            public readonly RemoteInterpolation Interp = new RemoteInterpolation(Map);
            public readonly List<double> DrawnX = new List<double>(); // нарисованная X относительно старта, unit (без обёртки)
            public readonly List<long> RenderUs = new List<long>();
            public readonly List<long> BufferUs = new List<long>();
            public long MinBufferUs = long.MaxValue;
            public long MaxBufferUs = long.MinValue;

            public void Go(int ticks, long startX, int dirX, Func<uint, bool>? lost = null, long jitterUs = 0, long frameUs = 16_667,
                long frameJitterUs = 0, ulong seed = 1, int skipFirstFrames = 120)
            {
                var rng = new DetRandom(seed);
                var arrivals = new SortedList<long, uint>();
                long lastArrival = 0;
                for (uint n = 1; n <= ticks; n++)
                {
                    if (lost != null && lost(n))
                    {
                        continue;
                    }

                    long a = RemoteInterpolation.TickTimeUs(n) + 30_000 + (jitterUs > 0 ? rng.NextInt((int)jitterUs) : 0);
                    a = Math.Max(a, lastArrival + 1); // NetClient отдаёт только новее прежнего — порядок прихода по такту
                    lastArrival = a;
                    arrivals.Add(a, n);
                }

                int next = 0;
                long end = RemoteInterpolation.TickTimeUs((uint)ticks);
                int frame = 0;
                double prevAbs = double.NaN;
                double unwrapped = 0;
                for (long now = 0; now < end; now += frameUs + (frameJitterUs > 0 ? rng.NextInt((int)frameJitterUs) : 0))
                {
                    while (next < arrivals.Count && arrivals.Keys[next] <= now)
                    {
                        Interp.OnSnapshot(SnapshotAt(arrivals.Values[next], startX, dirX), arrivals.Keys[next]);
                        next++;
                    }

                    if (!Interp.BeginFrame(now))
                    {
                        continue;
                    }

                    Assert.True(Interp.TryGet(7, out WorldPos from, out float dx, out float dy));
                    Assert.Equal(0f, dy);
                    // Нарисованная X по тору относительно старта: копим разности соседних кадров по тору.
                    double abs = from.X + (double)dx;
                    if (!double.IsNaN(prevAbs))
                    {
                        double d = abs - prevAbs;
                        double w = Map.Width * (double)U;
                        if (d > w / 2) d -= w;
                        if (d < -w / 2) d += w;
                        unwrapped += d;
                    }

                    prevAbs = abs;
                    DrawnX.Add(unwrapped);
                    RenderUs.Add(Interp.RenderTimeUs);
                    if (++frame > skipFirstFrames)
                    {
                        BufferUs.Add(Interp.BufferUs);
                        MinBufferUs = Math.Min(MinBufferUs, Interp.BufferUs);
                        MaxBufferUs = Math.Max(MaxBufferUs, Interp.BufferUs);
                    }
                }
            }
        }

        private static Snapshot SnapshotAt(uint tick, long startX, int dirX)
        {
            long x = startX + dirX * (long)(tick * 51.2);
            var s = new Snapshot { Tick = tick, Count = 2 };
            s.Entities[0] = new SnapshotEntity
            {
                Id = 7, Kind = SimEntity.KindCharacter,
                Position = SnapshotLayout.Quantize(Torus.Wrap(Map, new WorldPos((int)x, 300 * U + U / 2))),
                Direction = 1,
            };
            s.Entities[1] = new SnapshotEntity
            {
                Id = 9, Kind = SimEntity.KindCharacter, Position = SnapshotLayout.Quantize(new WorldPos(500 * U, 500 * U)),
            };
            return s;
        }

        [Fact]
        public void D00_Interp_RenderTime_GrowsEveryFrame_Delay100ms()
        {
            var run = new Run();
            run.Go(ticks: 600, startX: 100 * U, dirX: 1, jitterUs: 20_000, frameJitterUs: 8_000);
            RemoteInterpolation i = run.Interp;
            for (int k = 1; k < run.RenderUs.Count; k++)
            {
                Assert.True(run.RenderUs[k] > run.RenderUs[k - 1], $"кадр {k}: время показа {run.RenderUs[k]} после {run.RenderUs[k - 1]}");
            }

            _out.WriteLine($"кадров {i.Frames}, тех же {i.SameTimeFrames}, стоит {i.HoldFrames}, запас {run.MinBufferUs}…{run.MaxBufferUs} мкс, " +
                           $"шаг max {i.MaxStepUnits:0.0} u (отношение {i.MaxStepRatio:0.00}), сброс часов {i.ClockResets}, ход {i.ClockRate:0.0000}");
            Assert.Equal(0, i.SameTimeFrames);
            Assert.Equal(0, i.HoldFrames);
            Assert.Equal(0, i.StepViolations);
            Assert.Equal(0, i.ClockResets);
            // Запас = время новейшего снимка − время показа: при задержке 100 мс и разбросе 0…20 мс — 40…125 мс,
            // без задержки (M2) он ≈ 0 и чужие стоят на новейшем.
            Assert.InRange(run.MinBufferUs, 40_000, 100_000);
            Assert.InRange(run.MaxBufferUs, 90_000, 130_000);
        }

        [Fact]
        public void D00_Interp_SteadyLink_Buffer_67to100ms_StepIsInterpolated()
        {
            var run = new Run();
            run.Go(ticks: 300, startX: 100 * U, dirX: 1);
            RemoteInterpolation i = run.Interp;
            _out.WriteLine($"запас {run.MinBufferUs}…{run.MaxBufferUs} мкс, шаг max {i.MaxStepUnits:0.0} u");
            // Запас пилой от 100 мс (снимок пришёл) до 66,7 мс (следующий вот-вот); кадры 60 Гц видят часть пилы.
            Assert.InRange(run.MinBufferUs, 66_000, 85_000);
            Assert.InRange(run.MaxBufferUs, 92_000, 101_000);
            // 60 кадров/с, бег 1536 unit/с: шаг ≈ 25,6 unit за кадр; ступенька 30 Гц дала бы 48–56.
            Assert.InRange(i.MaxStepUnits, 20, 33);
            Assert.Equal(0, i.StepViolations);
            Assert.Equal(0, i.HoldFrames);
        }

        [Fact]
        public void D00_Interp_Seam_PairAcrossSeam_NoJump()
        {
            var run = new Run();
            // На восток через шов x = 0 (старт за 10 тайлов до него) и на запад через него же.
            run.Go(ticks: 300, startX: (Map.Width - 10) * U, dirX: 1);
            Assert.True(run.Interp.SeamSamples > 0, "пара через шов не попалась");
            Assert.Equal(0, run.Interp.StepViolations);
            AssertMonotone(run.DrawnX, +1);

            var back = new Run();
            back.Go(ticks: 300, startX: 10 * U, dirX: -1);
            Assert.True(back.Interp.SeamSamples > 0);
            Assert.Equal(0, back.Interp.StepViolations);
            AssertMonotone(back.DrawnX, -1);
            _out.WriteLine($"шов: кадров через пару шва {run.Interp.SeamSamples}/{back.Interp.SeamSamples}, шаг max {run.Interp.MaxStepUnits:0.0} u");
        }

        [Fact]
        public void D00_Interp_LostSnapshots_NoBackwardStep()
        {
            // Две потери подряд — запас 100 мс их переживает: ни остановки, ни шага сверх предела.
            var two = new Run();
            two.Go(ticks: 600, startX: 100 * U, dirX: 1, lost: n => n % 10 == 3 || n % 10 == 4);
            Assert.Equal(0, two.Interp.HoldFrames);
            Assert.Equal(0, two.Interp.StepViolations);
            AssertMonotone(two.DrawnX, +1);

            // Пачка 8 потерь (266 мс) — показ стоит на новейшем, потом догоняет вперёд, но назад не шагает никогда.
            var burst = new Run();
            burst.Go(ticks: 600, startX: 100 * U, dirX: 1, lost: n => n >= 200 && n < 208, jitterUs: 10_000);
            Assert.True(burst.Interp.HoldFrames > 0);
            AssertMonotone(burst.DrawnX, +1);
            for (int k = 1; k < burst.RenderUs.Count; k++)
            {
                Assert.True(burst.RenderUs[k] > burst.RenderUs[k - 1]);
            }

            _out.WriteLine($"пачка: стоит {burst.Interp.HoldFrames} кадров, сброс часов {burst.Interp.ClockResets}");
        }

        [Fact]
        public void D00_Interp_ClockJump_ResetsForward_NeverBack()
        {
            var i = new RemoteInterpolation(Map);
            long now = 0;
            uint tick = 10;
            var seen = new List<long>();
            for (int k = 0; k < 200; k++, now += 16_667)
            {
                if (k % 2 == 0)
                {
                    // На кадре 100 сервер «прыгает» на 300 тактов вперёд (фриз хоста, 2.4), на 150 — на 10 назад (сброс).
                    tick += k == 100 ? 300u : 1u;
                    i.OnSnapshot(SnapshotAt(tick, 100 * U, 1), now);
                }

                if (k == 150)
                {
                    i.OnSnapshot(SnapshotAt(tick + 1, 100 * U, 1), now); // нормальный
                }

                if (i.BeginFrame(now))
                {
                    seen.Add(i.RenderTimeUs);
                }
            }

            Assert.True(i.ClockResets >= 1);
            for (int k = 1; k < seen.Count; k++)
            {
                Assert.True(seen[k] >= seen[k - 1]);
            }
        }

        [Fact]
        public void D00_Interp_NoSnapshots_NoFrame_UnknownEntity_False()
        {
            var i = new RemoteInterpolation(Map);
            Assert.False(i.BeginFrame(1000));
            i.OnSnapshot(SnapshotAt(5, 0, 1), 1000);
            Assert.True(i.BeginFrame(2000));
            Assert.False(i.TryGet(123, out _, out _, out _));
            Assert.True(i.TryGet(9, out WorldPos p, out float dx, out _));
            Assert.Equal(SnapshotLayout.Quantize(new WorldPos(500 * U, 500 * U)), p);
            Assert.Equal(0f, dx);
        }

        private static void AssertMonotone(List<double> xs, int dir)
        {
            for (int k = 1; k < xs.Count; k++)
            {
                Assert.True((xs[k] - xs[k - 1]) * dir >= -0.01, $"кадр {k}: шаг назад {xs[k - 1]} → {xs[k]}");
            }

            Assert.True((xs[xs.Count - 1] - xs[0]) * dir > 40 * U, "сущность не прошла 40 тайлов");
        }

        // ---------- Мир клиента = мир сервера (M3 проверки 0-012) ----------

        [Fact]
        public void D00_GenerationHash_SameSeedEqual_OtherSeedDiffers_EditsIgnoredByServerValue()
        {
            var shape = MapShape.World(1024);
            WorldModel a = new StubGenerator().Generate(shape, 202609);
            WorldModel b = new StubGenerator().Generate(shape, 202609);
            WorldModel c = new StubGenerator().Generate(shape, 202609 ^ 1);
            ulong ha = StateHash.Generation(a);
            Assert.Equal(ha, StateHash.Generation(b));
            Assert.NotEqual(ha, StateHash.Generation(c));
            var t = new TileCoord(5, 7);
            a.SetBaseTile(t, (byte)(a.GetFloor(t) ^ 1), a.GetObject(t));
            Assert.NotEqual(ha, StateHash.Generation(a)); // один тайл — другой хэш
        }

        // ---------- Появление у шва и челнок (net-smoke шва) ----------

        [Fact]
        public void D00_SeamSpawn_ClearLane_ShuttleCrossesSeam()
        {
            var shape = MapShape.World(1024);
            WorldModel world = new StubGenerator().Generate(shape, 202609);
            var sim = new Simulation(world, maxEntities: 8);
            TileCoord center = ServerRun.SeamSpawnCenter(sim, out bool clear);
            Assert.True(clear);
            WorldPos p0 = ServerRun.SpawnPoint(shape, 0, center);
            WorldPos p2 = ServerRun.SpawnPoint(shape, 2, center);
            Assert.Equal(shape.Width - 8, Torus.TileOf(shape, p0).X);
            Assert.Equal(shape.Width - 4, Torus.TileOf(shape, p2).X); // игрок при двух ботах
            Assert.Equal(ServerRun.SpawnPoint(shape, 0), ServerRun.SpawnPoint(shape, 0, ServerRun.DefaultSpawnCenter(shape)));

            int id = sim.AddEntity(p0);
            SimBot bot = SimBot.Create(ShuttleBot.Name, id, 0, world.Seed);
            int crossings = 0;
            int prevX = Torus.TileOf(shape, p0).X;
            for (long tick = 1; tick <= 4 * ShuttleBot.LegTicks; tick++)
            {
                int slot = sim.SlotOf(id);
                sim.SetInputAt(slot, bot.Think(tick, sim.Entities[slot]));
                sim.Step();
                int x = Torus.TileOf(shape, sim.Entities[sim.SlotOf(id)].Mover.Position).X;
                if (Math.Abs(x - prevX) > shape.Width / 2)
                {
                    crossings++;
                }

                prevX = x;
            }

            // Два круга туда-обратно (по 120 тактов на восток и на запад): 4 перехода шва, вернулся к старту.
            Assert.Equal(4, crossings);
            Assert.True(Torus.Dist2(shape, p0, sim.Entities[sim.SlotOf(id)].Mover.Position) <= U * U);
        }
    }
}
