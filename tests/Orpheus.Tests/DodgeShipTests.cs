using System;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.Trig;
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
    /// Шип 0-029: уворот по сети без опережения — манекен бьёт ближним (d = 0,4 с) по таймеру, бот жмёт уворот в случайный
    /// кадр окна [конец показанного замаха − 0,2 с; − 17 мс] на своём экране, сервер решает удар в кадре конца замаха
    /// времени персонажа, ожидая ввод не дольше W = 100 мс (docs/07 2.7, 2.13 п. 3, 4.6; `02` 2.5, 2.7 п. 5).
    /// Время — виртуальное (петля + имитатор связи), сервер и бот в одном потоке.
    /// </summary>
    public sealed class DodgeShipTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const long TickUs = 1_000_000 / 30;

        /// <summary>Период замахов манекена: 1,5 с — рывок, откат и путь домой укладываются.</summary>
        public const int SwingPeriodFrames = 90;

        private readonly ITestOutputHelper _out;

        public DodgeShipTests(ITestOutputHelper output) => _out = output;

        public sealed class ShipRun
        {
            public DodgeBot Bot = null!;
            public NetServer Server = null!;
            public NetClient Client = null!;
            public LocalPrediction? Pred;
            public double Seconds;

            public override string ToString() =>
                $"{Bot.Summary()}; сервер: замахов {Server.SwingsStarted}, решено {Server.ThreatsResolved}, попаданий {Server.ThreatHits}, " +
                $"по последнему {Server.ThreatsByLastKnown}, ожидание ≤ {Server.MaxThreatWaitFrames} (ср. {(Server.ThreatsResolved == 0 ? 0 : (double)Server.ThreatWaitFramesTotal / Server.ThreatsResolved):F1}) кадров, " +
                $"опоздали {Server.FramesLate}, сам {Server.FramesSelfStepped}, отключений {Server.Disconnects}; правок {Pred?.Resynced}, {Seconds:F0} с";
        }

        /// <summary>
        /// Прогон шипа: <paramref name="swings"/> замахов по боту (сначала — 3 с на вход и часы), ожидание сервера
        /// <paramref name="waitFrames"/> кадров. Имитатор — на стороне клиента (обе стороны), null — без задержки.
        /// </summary>
        public static ShipRun Run(NetSimSettings? sim, int swings = 100, int waitFrames = 6, int botIndex = 0)
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 42);
            var simulation = new Simulation(world, maxEntities: 8);
            WorldPos spawn = ServerRun.SpawnPoint(world.Shape, 0);
            using LoopbackTransport st = LoopbackTransport.CreateServer(4);
            var server = new NetServer(st, simulation, _ => spawn, 4, NetModule.Version) { InputWaitFrames = waitFrames };
            // Манекен в 1 т к востоку, бьёт на запад — по спавну; первый замах через 3 с.
            server.SetDummy(new MeleeDummy(1000, Torus.Offset(world.Shape, spawn, new Int2(U, 0)), Angle16.FromDirection(new Int2(-1, 0)),
                firstSwingFrame: 180, periodFrames: SwingPeriodFrames));
            ITransport ct = LoopbackTransport.ConnectTo(st);
            if (sim.HasValue)
            {
                ct = new NetSimTransport(ct, sim.Value);
            }

            using ITransport clientTransport = ct;
            var client = new NetClient(ct, LoopbackTransport.ServerPeer, "dodge", NetModule.Version);
            var bot = new NetBot(client, DodgeBot.Name, botIndex)
            {
                PredictionFactory = c => new LocalPrediction(c.Shape, simulation.Solid, new WorldPos(c.Shape.WidthUnits / 2, c.Shape.HeightUnits / 2)),
            };

            long tick = 0;
            long endUs = (180L + (long)SwingPeriodFrames * (swings - 1) + 60) * 1_000_000 / 60;
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

                bot.Frame(now);
            }

            Assert.NotNull(bot.Dodge);
            return new ShipRun { Bot = bot.Dodge!, Server = server, Client = client, Pred = bot.Prediction, Seconds = now / 1e6 };
        }

        private static NetSimSettings Sim(int rttMs, int jitterMs, bool burst, ulong seed)
        {
            NetSimSettings v = NetSimSettings.FromRtt(rttMs, jitterMs, 1.0, seed);
            v.BurstJitter = burst;
            return v;
        }

        /// <summary>Кодек: «замах начат» (13) и «удар решён» (14) — туда и обратно, размер, испорченные флаги.</summary>
        [Fact]
        public void D00_Net_Codec_SwingAndResult_RoundTrip()
        {
            var buf = new byte[64];
            var swing = new SwingStartMessage
            {
                AttackerId = 0x7FFF0001, StartFrame = uint.MaxValue - 3, WindupFrames = 24, Origin = new WorldPos(262_143, 5),
                Axis = 32768, ReachUnits = 384, HalfArcSteps = 8192,
            };
            int n = swing.Write(buf);
            Assert.Equal(SwingStartMessage.Bytes, n);
            Assert.Equal(MessageType.SwingStart, Codec.TypeOf(buf.AsSpan(0, n)));
            SwingStartMessage s2 = SwingStartMessage.Read(buf.AsSpan(0, n));
            Assert.Equal(swing.ToString(), s2.ToString());
            Assert.Equal(swing.ReachUnits, s2.ReachUnits);
            Assert.Equal(swing.HalfArcSteps, s2.HalfArcSteps);
            Assert.Equal(20u, s2.ContactFrame); // через переполнение

            var res = new MeleeResultMessage { AttackerId = 7, StartFrame = 1234, Hit = true, ByLastKnown = true, WaitedFrames = 6, MissingFrames = 3, DecidedAtFrame = 1264 };
            n = res.Write(buf);
            Assert.Equal(MeleeResultMessage.Bytes, n);
            MeleeResultMessage r2 = MeleeResultMessage.Read(buf.AsSpan(0, n));
            Assert.Equal(res.ToString(), r2.ToString());
            Assert.Equal(res.DecidedAtFrame, r2.DecidedAtFrame);
            buf[9] = 4; // флаги вне 0…3
            Assert.Throws<ProtocolException>(() => MeleeResultMessage.Read(buf.AsSpan(0, n)));
        }

        /// <summary>
        /// Правка берёт уворот сервера (протокол 5): клиент нажал уворот, а сервер кадр с нажатием не шагнул (шагнул сам
        /// без кнопок) — после правки рывка в предсказании нет, позиция серверная.
        /// </summary>
        [Fact]
        public void D07_S2_6_Reconcile_TakesServerDodge()
        {
            MapShape map = MapShape.World(1024);
            WorldModel world = FlatWorldGenerator.Instance.Generate(map, 42);
            var simulation = new Simulation(world, maxEntities: 2);
            WorldPos at = ServerRun.SpawnPoint(map, 0);
            var pred = new LocalPrediction(map, simulation.Solid, at);
            pred.Reconcile(100, new SelfState(at, default), 0); // начало: сервер на кадре 100
            pred.Sent(101, new InputFrame(Core.Movement.Input8.North, InputButtons.Dodge));
            Assert.True(pred.Dodge.Dashing);
            pred.Reconcile(101, new SelfState(at, default), 1000); // сервер шагнул 101 сам: стоит, уворота нет
            Assert.Equal(1, pred.Resynced);
            Assert.False(pred.Dodge.Dashing);
            Assert.Equal(at, pred.Position);
        }

        /// <summary>Без задержки: все 100 уворотов засчитаны, сервер не ждёт дольше W, по последнему известному — ни одного.</summary>
        [Fact]
        public void D07_S2_13_P3_DodgeShip_NoLatency_AllCounted()
        {
            ShipRun r = Run(null);
            _out.WriteLine(r.ToString());
            Assert.Equal(100, r.Bot.Scored);
            Assert.Equal(100, r.Bot.Counted);
            Assert.Equal(0, r.Server.ThreatsByLastKnown);
            Assert.InRange(r.Server.MaxThreatWaitFrames, 0, 6);
        }

        /// <summary>
        /// Таблица шипа (0-029, шаг 5): RTT 100 / 200 / 250 / 300 (σ 10, потери 1 %) и 300 пачками σ 20, 100 замахов.
        /// Ожидание сервера ≤ W, отключений нет, все 100 замахов в счёте. Критерий «≥ 95 из 100» держится до RTT 250;
        /// при RTT 300 без опережения L шип не проходит (замер 0-029: 86 и 83 из 100) — строка фиксирует это числом:
        /// ввод кадров C − 2 … C приходит позже W, и нажатия в последние ~35 мс окна не доходят к решению.
        /// </summary>
        [Theory]
        [InlineData(100, 10, false, 95)]
        [InlineData(200, 10, false, 95)]
        [InlineData(250, 10, false, 95)]
        [InlineData(300, 10, false, 75)]
        [InlineData(300, 20, true, 70)]
        public void D07_S2_13_P3_DodgeShip_Rtt(int rttMs, int jitterMs, bool burst, int minCounted)
        {
            ShipRun r = Run(Sim(rttMs, jitterMs, burst, 29));
            _out.WriteLine($"RTT {rttMs}±{jitterMs}{(burst ? " пачками" : "")}, потери 1 %: {r}");
            Assert.Equal(100, r.Bot.Scored);
            Assert.True(r.Bot.Counted >= minCounted, $"засчитано {r.Bot.Counted} из 100, нужно ≥ {minCounted}");
            Assert.InRange(r.Server.MaxThreatWaitFrames, 0, 6);
            Assert.Equal(0, r.Server.Disconnects);
            Assert.Equal(r.Bot.Hits, r.Bot.PressNotArrived); // промахов уворота, кроме недошедших нажатий, нет
        }

        /// <summary>
        /// Разброс по зёрнам имитатора (8 зёрен на строку): числа для «Замер». Проверяет только инварианты — ожидание ≤ W,
        /// все замахи в счёте, попадания — только недошедшие нажатия.
        /// </summary>
        [Theory]
        [InlineData(100, 10, false)]
        [InlineData(200, 10, false)]
        [InlineData(250, 10, false)]
        [InlineData(300, 10, false)]
        [InlineData(300, 20, true)]
        public void D07_S2_13_P3_DodgeShip_Seeds(int rttMs, int jitterMs, bool burst)
        {
            var counted = new int[8];
            int maxWait = 0;
            for (int i = 0; i < counted.Length; i++)
            {
                ShipRun r = Run(Sim(rttMs, jitterMs, burst, 101 + (ulong)i), botIndex: 7 + i);
                counted[i] = r.Bot.Counted;
                maxWait = Math.Max(maxWait, r.Server.MaxThreatWaitFrames);
                Assert.Equal(100, r.Bot.Scored);
                Assert.Equal(r.Bot.Hits, r.Bot.PressNotArrived);
                Assert.Equal(0, r.Server.Disconnects);
            }

            Array.Sort(counted);
            _out.WriteLine($"RTT {rttMs}±{jitterMs}{(burst ? " пачками" : "")}: засчитано по 8 зёрнам {string.Join(" ", counted)} (мин. {counted[0]}, медиана {counted[4]}), ожидание ≤ {maxWait}");
            Assert.InRange(maxWait, 0, 6);
        }
    }
}
