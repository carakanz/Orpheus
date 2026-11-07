using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Orpheus.Core.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Rng;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Server;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Чужие сущности у клиента (docs/07, 2.9; карточки 0-017 и 0-021): <see cref="RemoteInterpolation"/> — время показа
    /// растёт каждый кадр и не идёт назад, задержка 100 мс, шов тора без скачка, потеря снимков без шага назад, часы не
    /// переставляются по одному опоздавшему снимку (модель связи — <see cref="NetSimTransport"/> с зёрнами); плюс сверка
    /// мира по хэшу генерации и появление у шва для net-smoke.
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
                long frameJitterUs = 0, ulong seed = 1, int skipFirstFrames = 120, long firstLateUs = 0, double drift = 0,
                Func<uint, long>? extraDelayUs = null)
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

                    long a = RemoteInterpolation.TickTimeUs(n) + 30_000 + (jitterUs > 0 ? rng.NextInt((int)jitterUs) : 0) + (extraDelayUs?.Invoke(n) ?? 0);
                    a = (long)(a * (1.0 + drift)) + (arrivals.Count == 0 ? firstLateUs : 0); // местные часы идут иначе; первый снимок опоздал
                    if (a <= lastArrival)
                    {
                        continue; // NetClient отдаёт только новее прежнего: обогнанный снимок выброшен
                    }

                    lastArrival = a;
                    arrivals.Add(a, n);
                }

                int next = 0;
                long end = (long)(RemoteInterpolation.TickTimeUs((uint)ticks) * (1.0 + drift));
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

        // ---------- Модель связи с зёрнами: тот же NetSimTransport, что под клиентом (0-021) ----------

        /// <summary>
        /// Сервер шлёт снимок каждый такт (33,3 мс) в «провод» — <see cref="WireTransport"/>; клиент — кадры 60 Гц с разбросом
        /// 0–4 мс, каждый кадр <see cref="NetSimTransport.Poll"/> (задержка RTT/2 + нормальный разброс σ, потери — тем же
        /// кодом, что в игре, по зерну), принимает только новее прежнего (как <see cref="NetClient"/>), время прихода —
        /// начало кадра (как <c>NetGame</c>). Местные часы клиента сдвинуты на 7 с от серверных.
        /// </summary>
        private sealed class NetModel : ITransportHandler
        {
            public readonly RemoteInterpolation Interp = new RemoteInterpolation(Map);
            public long BackwardRender;
            public long BackwardDrawn;
            public long Accepted;
            public long Stale;
            private uint _latest;
            private long _now;

            private sealed class WireTransport : ITransport
            {
                public readonly Queue<(long Due, uint Tick)> Wire = new Queue<(long, uint)>();

                public void Poll(long nowMicros, ITransportHandler handler)
                {
                    Span<byte> b = stackalloc byte[4];
                    while (Wire.Count > 0 && Wire.Peek().Due <= nowMicros)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(b, Wire.Dequeue().Tick);
                        handler.OnReceive(1, NetChannel.Unreliable, b);
                    }
                }

                public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data)
                {
                }

                public void Flush()
                {
                }

                public void Disconnect(int peer, ReadOnlySpan<byte> payload)
                {
                }

                public int RoundTripMs(int peer) => 0;

                public void Dispose()
                {
                }
            }

            public NetModel Go(double seconds, int rttMs, int sigmaMs, double lossPercent, ulong seed, bool burst = false)
            {
                const long ClockShift = 7_000_000;
                var wire = new WireTransport();
                NetSimSettings set = NetSimSettings.FromRtt(rttMs, sigmaMs, lossPercent, seed);
                set.BurstJitter = burst;
                var sim = new NetSimTransport(wire, set);
                var frames = new DetRandom(seed ^ 0xF4A3E5);
                long end = (long)(seconds * 1_000_000);
                uint sent = 0;
                long prevRender = long.MinValue;
                double prevX = double.NaN;
                for (long server = 0; server < end; server += 16_667 + frames.NextInt(4_000))
                {
                    while (RemoteInterpolation.TickTimeUs(sent + 1) <= server)
                    {
                        sent++;
                        wire.Wire.Enqueue((RemoteInterpolation.TickTimeUs(sent), sent));
                    }

                    _now = server + ClockShift;
                    sim.Poll(server, this);
                    if (!Interp.BeginFrame(_now))
                    {
                        continue;
                    }

                    Assert.True(Interp.TryGet(7, out WorldPos from, out float dx, out _));
                    double x = from.X + (double)dx;
                    if (Interp.RenderTimeUs < prevRender)
                    {
                        BackwardRender++;
                    }

                    if (!double.IsNaN(prevX) && x < prevX - 0.01)
                    {
                        BackwardDrawn++;
                    }

                    prevRender = Interp.RenderTimeUs;
                    prevX = x;
                }

                return this;
            }

            public void OnConnected(int peer)
            {
            }

            public void OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload)
            {
            }

            public void OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
                uint tick = BinaryPrimitives.ReadUInt32LittleEndian(data);
                if (Accepted > 0 && (int)(tick - _latest) <= 0)
                {
                    Stale++;
                    return;
                }

                _latest = tick;
                Accepted++;
                Interp.OnSnapshot(SnapshotAt(tick, 100 * U, 1), _now);
            }

            public double SameShare => Interp.SameTimeFrames / (double)Interp.Frames;

            public double HoldShare => Interp.HoldFrames / (double)Interp.Frames;

            public double CatchUpShare => Interp.CatchUpSteps / (double)Math.Max(1, Interp.Steps);

            /// <summary>Красный ли net-smoke с такими числами (пороги — <c>NetGame</c>).</summary>
            public bool SmokeRed => SameShare > SmokeSameShare || HoldShare > SmokeHoldShare || CatchUpShare > SmokeCatchUpShare
                                    || Interp.StepViolations > 0 || Interp.ClockResets > 0;

            public override string ToString() =>
                $"кадров {Interp.Frames}, тех же {Interp.SameTimeFrames} ({SameShare:P2}), стоит {Interp.HoldFrames} ({HoldShare:P2}), " +
                $"догон {Interp.CatchUpSteps} ({CatchUpShare:P2}, к пределу {Interp.MaxCatchUpRatio:0.00}), сверх предела {Interp.StepViolations}, " +
                $"перестановок {Interp.ClockResets}, назад {BackwardRender}/{BackwardDrawn}, снимков {Accepted} (выброшено старых {Stale})";
        }

        /// <summary>Пороги net-smoke чужих (<c>NetGame</c>): тех же ≤ 1 %, стоит ≤ 5 %, догон ≤ 3 % шагов.</summary>
        private const double SmokeSameShare = 0.01;
        private const double SmokeHoldShare = 0.05;
        private const double SmokeCatchUpShare = 0.03;

        [Fact]
        public void D00_Interp_Rtt200Sigma30_16Seeds_NoClockResets_HoldUnder1pct()
        {
            // Замечание 1 проверки 0-017: при σ 30 мс поздний снимок (+3σ) переставлял часы назад, 4 из 16 зёрен net-smoke
            // красные. Теперь часы — медиана за 1 с: 16 зёрен по 60 с (и те же 16 с разбросом пачками) — ни одной перестановки,
            // стоит ≤ 1 %, догон ≤ 3 % шагов и не быстрее k = 2, назад — никогда.
            for (ulong seed = 1; seed <= 32; seed++)
            {
                bool burst = seed > 16; // зёрна 17–32 — разброс пачками (одна добавка на 250 мс)
                NetModel m = new NetModel().Go(60, 200, 30, 1, burst ? seed - 16 : seed, burst);
                _out.WriteLine($"зерно {(burst ? seed - 16 : seed)}{(burst ? " пачками" : "")}: {m}");
                Assert.True(m.Interp.ClockResets == 0, $"зерно {seed}: {m}");
                Assert.True(m.HoldShare <= 0.01, $"зерно {seed}: {m}");
                Assert.True(m.SameShare <= 0.01, $"зерно {seed}: {m}");
                Assert.True(m.Interp.StepViolations == 0 && m.BackwardRender == 0 && m.BackwardDrawn == 0, $"зерно {seed}: {m}");
                Assert.True(m.CatchUpShare <= SmokeCatchUpShare, $"зерно {seed}: {m}"); // M2 (задержка 0) — догон постоянно
                Assert.True(m.Interp.MaxCatchUpRatio <= RemoteInterpolation.CatchUpRate, $"зерно {seed}: {m}");
            }
        }

        [Fact]
        public void D00_Interp_NetSmokeModel_500Runs_NoRed()
        {
            // Как net-smoke: 6 с, пороги NetGame. Проверка 0-017 насчитала 11 % красных прогонов при 200,30,1 (своя модель),
            // 0 % при 100,20,1. Теперь 0 из 500 и там, и там. Разброс пачками (одна добавка на 250 мс): перестановок 0, но
            // пачка, опоздавшая на 3σ (90+ мс), — 250 мс без данных, показ честно стоит до 6 % шестисекундного прогона:
            // это нехватка данных, её закроет экстраполяция 2.9 (backlog), а не часы; допускаем ≤ 1 % таких прогонов.
            foreach ((int rtt, int sigma, bool burst) in new[] { (200, 30, false), (100, 20, false), (200, 30, true) })
            {
                int red = 0;
                long resets = 0;
                double holdSum = 0, holdMax = 0, catchMax = 0;
                for (ulong seed = 1; seed <= 500; seed++)
                {
                    NetModel m = new NetModel().Go(6, rtt, sigma, 1, seed * 7919, burst);
                    if (m.SmokeRed)
                    {
                        red++;
                        _out.WriteLine($"  красный: {rtt},{sigma},1{(burst ? " пачками" : "")} зерно {seed * 7919}: {m}");
                    }

                    resets += m.Interp.ClockResets;
                    holdSum += m.HoldShare;
                    holdMax = Math.Max(holdMax, m.HoldShare);
                    catchMax = Math.Max(catchMax, m.CatchUpShare);
                    Assert.True(m.BackwardRender == 0 && m.BackwardDrawn == 0, m.ToString());
                }

                _out.WriteLine($"{rtt},{sigma},1{(burst ? " пачками" : "")}: красных {red} из 500, перестановок {resets}, " +
                               $"стоит в среднем {holdSum / 500:P2}, max {holdMax:P2}; догон max {catchMax:P2}");
                Assert.True(burst ? red <= 5 : red == 0, $"красных {red} из 500");
                Assert.Equal(0, resets);
            }
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
                           $"шаг max {i.MaxStepUnits:0.0} u (отношение {i.MaxStepRatio:0.00}), сброс часов {i.ClockResets}, ход {i.PlaybackRate:0.0000}");
            Assert.Equal(0, i.SameTimeFrames);
            Assert.Equal(0, i.HoldFrames);
            Assert.Equal(0, i.StepViolations);
            Assert.Equal(0, i.CatchUpSteps);
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
        public void D00_Interp_ClockSlews_FirstLateSample_AndDrift()
        {
            // Первый снимок опоздал на 80 мс (часы поставлены с ошибкой), местные часы спешат на 0,3 %: медиана окна
            // забывает первый образец, ход показа догоняет — запас возвращается к 67–100 мс без перестановок.
            var run = new Run();
            run.Go(ticks: 900, startX: 100 * U, dirX: 1, firstLateUs: 80_000, drift: 0.003, skipFirstFrames: 600);
            RemoteInterpolation i = run.Interp;
            _out.WriteLine($"запас после 10 с: {run.MinBufferUs}…{run.MaxBufferUs} мкс, ход {i.PlaybackRate:0.0000}, перестановок {i.ClockResets}, " +
                           $"догон {i.CatchUpSteps} (к пределу {i.MaxCatchUpRatio:0.00})");
            Assert.Equal(0, i.ClockResets);
            Assert.InRange(run.MinBufferUs, 55_000, 90_000);
            Assert.InRange(run.MaxBufferUs, 85_000, 110_000);
            Assert.Equal(0, i.SameTimeFrames);
            Assert.Equal(0, i.StepViolations);
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
        public void D00_Interp_LostSnapshots_NoBackwardStep_CatchUpBounded()
        {
            // Две потери подряд — запас 100 мс их переживает: ни остановки, ни шага сверх предела.
            var two = new Run();
            two.Go(ticks: 600, startX: 100 * U, dirX: 1, lost: n => n % 10 == 3 || n % 10 == 4);
            Assert.Equal(0, two.Interp.HoldFrames);
            Assert.Equal(0, two.Interp.StepViolations);
            AssertMonotone(two.DrawnX, +1);

            // Пачка 8 потерь (266 мс) — показ стоит на новейшем (время показа не растёт), потом догоняет вперёд со скоростью
            // ≤ 2 (замечание 3 проверки 0-017: раньше догон прыгал на всё простоявшее время, до 28× предела). Назад — никогда.
            var burst = new Run();
            burst.Go(ticks: 600, startX: 100 * U, dirX: 1, lost: n => n >= 200 && n < 208, jitterUs: 10_000);
            RemoteInterpolation b = burst.Interp;
            _out.WriteLine($"пачка: стоит {b.HoldFrames} кадров, тех же {b.SameTimeFrames}, догон {b.CatchUpSteps} шагов " +
                           $"(к пределу по местному времени {b.MaxCatchUpRatio:0.00}), перестановок {b.ClockResets}");
            Assert.InRange(b.HoldFrames, 6, 16);       // ~166 мс без данных за вычетом запаса
            Assert.InRange(b.SameTimeFrames, 5, 16);
            Assert.InRange(b.CatchUpSteps, 3, 30);     // догон после остановки — отдельный счёт
            Assert.True(b.MaxCatchUpRatio <= RemoteInterpolation.CatchUpRate, $"догон {b.MaxCatchUpRatio:0.00} × предела");
            Assert.Equal(0, b.StepViolations);
            Assert.Equal(0, b.ClockResets);
            AssertMonotone(burst.DrawnX, +1);
            for (int k = 1; k < burst.RenderUs.Count; k++)
            {
                Assert.True(burst.RenderUs[k] >= burst.RenderUs[k - 1]);
            }
        }

        [Fact]
        public void D00_Interp_ClockJumps_ForwardResets_BackNeverBack()
        {
            // (а) Фриз хоста (2.4): сервер прыгнул на 300 тактов вперёд — медиана перескакивает за полуокно, показ
            // переставляется вперёд (перестановка), назад не идёт.
            var i = new RemoteInterpolation(Map);
            long now = 0;
            uint tick = 10;
            long prev = long.MinValue;
            for (int k = 0; k < 300; k++, now += 16_667)
            {
                if (k % 2 == 0)
                {
                    tick += k == 100 ? 300u : 1u;
                    i.OnSnapshot(SnapshotAt(tick, 100 * U, 1), now);
                }

                Assert.True(i.BeginFrame(now));
                Assert.True(i.RenderTimeUs >= prev, $"кадр {k}: {i.RenderTimeUs} после {prev}");
                prev = i.RenderTimeUs;
            }

            _out.WriteLine($"фриз: перестановок {i.ClockResets}, запас {i.BufferUs}, до старейшего {i.BeforeOldestFrames}");
            Assert.Equal(1, i.ClockResets);
            Assert.InRange(i.BufferUs, 60_000, 110_000); // после перестановки — снова 100 мс позади новейшего

            // (б) Часы назад по-настоящему: задержка выросла на 300 мс с такта 300 и не вернулась. Оценка «сервер
            // сейчас» уходит назад на 300 мс (замечание 2 проверки 0-017: старый тест такого скачка не делал).
            // Время показа стоит, пока данных нет, потом идёт медленнее часов — но ни кадра назад, без перестановок.
            var late = new Run();
            late.Go(ticks: 900, startX: 100 * U, dirX: 1, extraDelayUs: n => n >= 300 ? 300_000 : 0);
            RemoteInterpolation l = late.Interp;
            _out.WriteLine($"задержка +300 мс: тех же {l.SameTimeFrames}, стоит {l.HoldFrames}, перестановок {l.ClockResets}, " +
                           $"запас в конце {l.BufferUs / 1000} мс, ход {l.PlaybackRate:0.000}");
            for (int k = 1; k < late.RenderUs.Count; k++)
            {
                Assert.True(late.RenderUs[k] >= late.RenderUs[k - 1], $"кадр {k}: время показа {late.RenderUs[k]} после {late.RenderUs[k - 1]}");
            }

            AssertMonotone(late.DrawnX, +1);
            Assert.InRange(l.SameTimeFrames, 10, 40); // ~300 мс без данных и стояния на новейшем, пока медиана не перешла
            Assert.Equal(0, l.ClockResets);
            Assert.Equal(0, l.StepViolations);
            Assert.InRange(l.BufferUs, 60_000, 110_000); // к концу — снова задержка 100 мс

            // (в) Местные часы назад на 50 мс (кадр с меньшим временем): время показа стоит, не идёт назад.
            var c = new RemoteInterpolation(Map);
            c.OnSnapshot(SnapshotAt(100, 100 * U, 1), 0);
            c.OnSnapshot(SnapshotAt(101, 100 * U, 1), 33_333);
            Assert.True(c.BeginFrame(140_000));
            long r0 = c.RenderTimeUs;
            Assert.True(c.BeginFrame(90_000));
            Assert.Equal(r0, c.RenderTimeUs);
            Assert.Equal(1, c.SameTimeFrames);
        }

        [Fact]
        public void D00_Interp_RateFor_BandAndLimits()
        {
            Assert.Equal(1.0, RemoteInterpolation.RateFor(0));
            Assert.Equal(1.005, RemoteInterpolation.RateFor(5_000), 6);
            Assert.Equal(1.01, RemoteInterpolation.RateFor(RemoteInterpolation.FastUs), 6);   // край полосы — 1 %
            Assert.Equal(0.99, RemoteInterpolation.RateFor(-RemoteInterpolation.FastUs), 6);
            Assert.Equal(RemoteInterpolation.CatchUpRate, RemoteInterpolation.RateFor(200_000)); // догон ограничен k = 2
            Assert.Equal(RemoteInterpolation.MinRate, RemoteInterpolation.RateFor(-200_000));
            for (double e = -150_000; e < 150_000; e += 1_000)
            {
                Assert.True(RemoteInterpolation.RateFor(e + 1_000) >= RemoteInterpolation.RateFor(e)); // монотонна
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

        // ---------- Мир клиента = мир сервера (M3 проверки 0-012, M6a/M6b проверки 0-017) ----------

        [Fact]
        public void D00_GenerationHash_SameSeedEqual_OtherSeedDiffers_TileEditChangesHash()
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
            Assert.NotEqual(ha, StateHash.Generation(a)); // один тайл — другой хэш: правки в хэш генерации попадать не должны
        }

        [Fact]
        public async Task D00_GenerationHash_LoadedWorldWithEdits_LoginSendsGenerationHash()
        {
            // Замечание 6 проверки 0-017 (M6a, M6b): мир загружен из папки (не --create), в сохранении есть правка тайла.
            // «Вход принят» по UDP несёт хэш генерации — тот, что клиент получит, сгенерировав мир по зерну, а не хэш мира
            // с правками. M6a (ServerRun не ставит хэш до наложения сохранения) и M6b (сервер шлёт 0) здесь красные.
            string dir = Path.Combine(Path.GetTempPath(), "orpheus-hash-" + Guid.NewGuid().ToString("N"));
            try
            {
                string world = Path.Combine(dir, "w");
                var edit = new TileCoord(37, 41);
                ServerOptions first = ServerOptions.Parse(new[]
                {
                    "--world", world, "--create", "--seed", "202609", "--size", "1024", "--sim", "--ticks", "5", "--logs", Path.Combine(dir, "logs"),
                });
                first.AfterTick = sim =>
                {
                    if (sim.Tick == 1)
                    {
                        sim.RequestEdit(edit, FloorId.Sand, ObjectId.Empty);
                    }
                };
                Assert.Equal(0, ServerRun.Run(first, null).ExitCode);

                int port = FreeUdpPort();
                ServerOptions second = ServerOptions.Parse(new[]
                {
                    "--world", world, "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--ticks", "600",
                    "--logs", Path.Combine(dir, "logs"),
                });
                ulong loadedHash = 0;
                second.AfterTick = sim =>
                {
                    if (Volatile.Read(ref loadedHash) == 0)
                    {
                        Volatile.Write(ref loadedHash, StateHash.Generation(sim.World)); // мир сервера — с правкой
                    }
                };

                using var stop = new CancellationTokenSource();
                Task<ServerResult> server = Task.Run(() => ServerRun.Run(second, null, stop.Token));
                LoginAcceptMessage accept;
                using (LiteNetTransport t = LiteNetTransport.Connect("127.0.0.1", port))
                {
                    var client = new NetClient(t, t.ServerPeer, "хэш", NetModule.Version);
                    var sw = Stopwatch.StartNew();
                    while (client.State != NetClientState.InGame && sw.Elapsed < TimeSpan.FromSeconds(15))
                    {
                        client.Poll(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
                        await Task.Delay(2);
                    }

                    Assert.Equal(NetClientState.InGame, client.State);
                    accept = client.Accept;
                    client.Disconnect();
                }

                stop.Cancel();
                Assert.Equal(0, (await server).ExitCode);

                ulong generated = StateHash.Generation(new StubGenerator().Generate(MapShape.World(1024), accept.WorldSeed));
                _out.WriteLine($"вход: 0x{accept.GenerationHash:X16}, генерация клиента 0x{generated:X16}, мир сервера с правкой 0x{loadedHash:X16}");
                Assert.NotEqual(generated, Volatile.Read(ref loadedHash)); // правка загрузилась — тест о чём-то
                Assert.Equal(generated, accept.GenerationHash);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
        }

        private static int FreeUdpPort()
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)s.LocalEndPoint!).Port;
        }

        [Fact]
        public void D00_LoginName_Utf8Bytes_OrpheusFits()
        {
            Assert.Null(LoginMessage.NameError("Орфей"));
            Assert.Null(LoginMessage.NameError(new string('я', 16))); // 32 байта
            Assert.NotNull(LoginMessage.NameError(new string('я', 17))); // 34 байта — протокол бы отверг
            Assert.Null(LoginMessage.NameError(new string('a', 32)));
            Assert.NotNull(LoginMessage.NameError(new string('a', 33)));
            Assert.NotNull(LoginMessage.NameError(" "));
            Assert.NotNull(LoginMessage.NameError(null));
            byte[] buf = new byte[LoginMessage.MaxBytes];
            Assert.True(new LoginMessage { Version = Orpheus.Net.NetModule.Version, Name = new string('я', 16), Token = new HostToken(1, 1) }.Write(buf) == LoginMessage.MaxBytes);
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
