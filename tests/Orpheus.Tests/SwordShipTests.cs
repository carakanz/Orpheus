using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Server;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Шип меча (1-004, фаза C): бот бежит на восток и держит ЛКМ — удары серии идут без пропусков,
    /// предсказание шагает фазы тем же шагом, что сервер (70 % скорости, ось кадра нажатия), и по
    /// снимкам с дельтами не получает ни одной правки. Приёмка карточки: «100 ударов на бегу, RTT 100 —
    /// правок 0». Время — виртуальное (петля + имитатор связи), сервер и клиент в одном потоке.
    /// </summary>
    public sealed class SwordShipTests
    {
        private const long TickUs = 1_000_000 / 30;

        /// <summary>Ячейка быстрой панели с мечом (StartingKit: Slot3 = WoodenSword, 1-002).</summary>
        private const byte SwordSlot = 3;

        private readonly ITestOutputHelper _out;

        public SwordShipTests(ITestOutputHelper output) => _out = output;

        public sealed class ShipRun
        {
            public NetServer Server = null!;
            public NetClient Client = null!;
            public LocalPrediction Pred = null!;

            /// <summary>Покой → замах по снимкам — сколько ударов клиент видит у своего персонажа.</summary>
            public int Strikes;

            public double Seconds;

            public override string ToString() =>
                $"ударов {Strikes}; правок {Pred.Resynced}, переполнений {Pred.Overflows}, наибольшее расхождение {Pred.MaxDevUnits} unit, " +
                $"сверок {Pred.Reconciled}; сервер: отключений {Server.Disconnects}, {Seconds:F0} с";
        }

        /// <summary>
        /// Прогон: бот входит (~3 с) и с первого кадра бежит на восток с зажатой кнопкой удара
        /// (удержание = повтор, 1-004). Цикл серии 39/39/49 кадров ⇒ <paramref name="strikes"/> ударов
        /// укладываются в 100 × 49 кадров; имитатор — на стороне клиента, null — без задержки.
        /// </summary>
        public static ShipRun Run(NetSimSettings? sim, int strikes = 100, int botIndex = 0)
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 42);
            var simulation = new Simulation(world, maxEntities: 8);
            WorldPos spawn = ServerRun.SpawnPoint(world.Shape, 0);
            using LoopbackTransport st = LoopbackTransport.CreateServer(4);
            var server = new NetServer(st, simulation, _ => spawn, 4, NetModule.Version);
            ITransport ct = LoopbackTransport.ConnectTo(st);
            if (sim.HasValue)
            {
                ct = new NetSimTransport(ct, sim.Value);
            }

            using ITransport clientTransport = ct;
            var client = new NetClient(ct, LoopbackTransport.ServerPeer, "sword" + botIndex, NetModule.Version);
            LocalPrediction? pred = null;
            int counted = 0;
            byte lastPhase = MeleeAction.PhaseIdle;
            long recTick = -1;

            long tick = 0;
            long endUs = (300L + (long)strikes * 49 + 120) * 1_000_000 / 60; // 3 с на вход + цикл ≤ 49 кадров на удар + запас
            long now;
            for (now = 0; now <= endUs; now += 1000)
            {
                if (now >= tick * TickUs)
                {
                    server.Poll(now);
                    simulation.Step();
                    server.SendSnapshots();
                    tick++;
                }

                client.Poll(now);
                if (client.State != NetClientState.InGame)
                {
                    continue;
                }

                pred ??= new LocalPrediction(client.Shape, simulation.Solid, spawn) { HeldItem = Items.WoodenSword };
                if (client.HasSnapshot && client.Latest.Tick != recTick)
                {
                    recTick = client.Latest.Tick;
                    pred.Reconcile(client.Latest.AckFrame, client.Latest.Self, now);
                    // удары видимы в снимках: фаза своего персонажа (Melee = phase | index << 2, протокол 15)
                    if (client.Latest.TryFind(client.EntityId, out SnapshotEntity e))
                    {
                        byte phase = (byte)(e.Melee & 3);
                        if (phase == MeleeAction.PhaseWindup && lastPhase != MeleeAction.PhaseWindup)
                        {
                            counted++; // вход в замах: в серии фаза не бывает покоем между ударами
                        }

                        lastPhase = phase;
                    }
                }

                int n = client.FramesDue(now);
                var f = new InputFrame(Input8.East, InputButtons.Attack, 0, SwordSlot);
                for (int k = 0; k < n; k++)
                {
                    client.SendFrame(f);
                    pred.Sent(client.Frame, f);
                }
            }

            Assert.NotNull(pred);
            return new ShipRun { Server = server, Client = client, Pred = pred!, Strikes = counted, Seconds = now / 1e6 };
        }

        private static NetSimSettings Sim(int rttMs, int jitterMs, ulong seed) =>
            NetSimSettings.FromRtt(rttMs, jitterMs, 0, seed);

        /// <summary>Без задержки: 100 ударов видны в снимках, предсказание не правится ни разу.</summary>
        [Fact]
        public void D07_S2_13_SwordShip_NoLatency_HundredStrikes_NoResync()
        {
            ShipRun r = Run(null);
            _out.WriteLine(r.ToString());
            Assert.True(r.Strikes >= 100, $"ударов {r.Strikes}, нужно ≥ 100");
            Assert.Equal(0, r.Pred.Resynced);
            Assert.Equal(0, r.Pred.Overflows);
            Assert.Equal(0, r.Server.Disconnects);
        }

        /// <summary>
        /// Приёмка 1-004: 100 ударов на бегу при RTT 100, потерь нет — правок 0 (предсказание ведёт замедление
        /// 70 % и серии так же, как сервер). На четырёх зёрнах.
        /// </summary>
        [Theory]
        [InlineData(29)]
        [InlineData(701)]
        [InlineData(702)]
        [InlineData(703)]
        public void D07_S2_13_SwordShip_Rtt100_HundredStrikes_NoResync(int seed)
        {
            ShipRun r = Run(Sim(100, 0, (ulong)seed), botIndex: seed % 50);
            _out.WriteLine($"зерно {seed}: {r}");
            Assert.True(r.Strikes >= 100, $"зерно {seed}: ударов {r.Strikes}, нужно ≥ 100");
            Assert.Equal(0, r.Pred.Resynced);
            Assert.Equal(0, r.Pred.Overflows);
            Assert.Equal(0, r.Server.Disconnects);
        }

        /// <summary>
        /// RTT 100 ± 10 мс: джиттер рождает кадры «нет ввода» (2.7: сервер шагает их с отпущенными кнопками),
        /// в них сервер едет 100 % вместо 70 % замаха — расхождение с предсказанием и правка по позиции.
        /// Правки от джиттера, не от замедления; расхождение остаётся меньше половины тайла (2.13 п. 1).
        /// </summary>
        [Theory]
        [InlineData(29)]
        [InlineData(701)]
        [InlineData(702)]
        [InlineData(703)]
        public void D07_S2_13_SwordShip_Rtt100Jitter10_SeriesSurvives(int seed)
        {
            ShipRun r = Run(Sim(100, 10, (ulong)seed), botIndex: seed % 50);
            _out.WriteLine($"зерно {seed}: {r}");
            Assert.True(r.Strikes >= 100, $"зерно {seed}: ударов {r.Strikes}, нужно ≥ 100");
            Assert.Equal(0, r.Pred.Overflows);
            Assert.Equal(0, r.Server.Disconnects);
            Assert.InRange(r.Pred.MaxDevUnits, 0, LocalPrediction.HalfTileUnits); // 2.13 п. 1: расхождение < 0,5 тайла
        }
    }
}
