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
    /// Шип уворота (0-029, повтор 0-033 со схемой `07` 2.7 целиком): манекен бьёт ближним (d = 0,4 с) по таймеру; клиент
    /// нумерует кадры с опережением L; бот показывает замах честно — с прихода сообщения и не короче min(d; 0,3 с) (2.9) — и
    /// жмёт уворот в случайный кадр окна [конец показа − 0,2 с; − 17 мс] на своём экране; сервер решает удар в кадре
    /// s + max(d, D_p + min(d; 0,3 с)) времени персонажа, ожидая ввод не дольше W = 100 мс; опоздавшие нажатия «липкие»
    /// (docs/07 2.7, 2.9, 2.13 п. 3–4, 4.6; `02` 2.5, 15.1).
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
                $"опоздали {Server.FramesLate}, сам {Server.FramesSelfStepped}, отключений {Server.Disconnects}, липких {Server.StickyPresses}; L {Client.LeadFrames} (≤ {Client.MaxLeadFrames}) кадров; правок {Pred?.Resynced}, {Seconds:F0} с";
        }

        /// <summary>
        /// Прогон шипа: <paramref name="swings"/> замахов по боту (сначала — 3 с на вход и часы), ожидание сервера
        /// <paramref name="waitFrames"/> кадров. Имитатор — на стороне клиента (обе стороны), null — без задержки.
        /// </summary>
        public static ShipRun Run(NetSimSettings? sim, int swings = 100, int waitFrames = 6, int botIndex = 0, int latestLead = DodgeBot.LatestLeadFrames,
            Func<ITransport, ITransport>? wrap = null, int latePress = 0)
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

            if (wrap != null)
            {
                ct = wrap(ct);
            }

            using ITransport clientTransport = ct;
            var client = new NetClient(ct, LoopbackTransport.ServerPeer, "dodge", NetModule.Version);
            var bot = new NetBot(client, DodgeBot.Name, botIndex)
            {
                DodgeLatestLead = latestLead,
                DodgeLatePress = latePress,
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
        /// Правка берёт уворот сервера (протокол 6): клиент нажал уворот, а сервер кадр с нажатием не шагнул (шагнул сам
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

        /// <summary>
        /// Умолчание W = 6 кадров = 100 мс (docs/07 4.6) у сервера и у ключа <c>--dummy-wait</c> (П по 0-029, з. 2):
        /// <see cref="Run"/> передаёт W явно, поэтому умолчание проверяется отдельно.
        /// </summary>
        [Fact]
        public void D07_S2_13_P3_DodgeShip_DefaultWait6Frames()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(256), 42);
            using LoopbackTransport st = LoopbackTransport.CreateServer(1);
            var server = new NetServer(st, new Simulation(world, maxEntities: 2), _ => default, 1, NetModule.Version);
            Assert.Equal(6, server.InputWaitFrames);

            Assert.Equal(6, ServerOptions.Parse(new[] { "--create", "--seed", "42", "--size", "1024", "--dummy" }).DummyWaitFrames);
            Assert.Equal(9, ServerOptions.Parse(new[] { "--create", "--seed", "42", "--size", "1024", "--dummy", "--dummy-wait", "9" }).DummyWaitFrames);
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

        /// <summary>Кадр решения удара (`07` 2.7, 4.6): s + max(d, min(D, 30) + min(d, 18)).</summary>
        [Fact]
        public void D07_S2_7_ResolveFrame_Formula()
        {
            Assert.Equal(24, ThreatTiming.ResolveOffset(24, 0)); // D = 0: конец замаха
            Assert.Equal(24, ThreatTiming.ResolveOffset(24, 6)); // замах 0,4 с виден 0,3 с — решение в конце
            Assert.Equal(31, ThreatTiming.ResolveOffset(24, 13)); // D 217 мс: 13 + 18
            Assert.Equal(48, ThreatTiming.ResolveOffset(24, 30));
            Assert.Equal(48, ThreatTiming.ResolveOffset(24, 45)); // D учитывается не больше 500 мс
            Assert.Equal(17, ThreatTiming.ResolveOffset(12, 5)); // короткий замах: показ не короче d
            Assert.Equal(90, ThreatTiming.ResolveOffset(90, 30)); // тяжёлый 1,5 с: решение в конце замаха
            Assert.Equal(3u, ThreatTiming.ResolveFrame(uint.MaxValue - 20, 24, 0)); // через переполнение
        }

        /// <summary>
        /// Опережение L по отчёту сервера (`07` 2.7; 0-033 шаг 6): при RTT ≤ 100 мс с обычным разбросом L = 0 — чистое время
        /// персонажа; при 150 — не больше 2 кадров (таблица 2.7: «≤ 150 мс — 0–35 мс»); растёт с RTT; не больше 24 кадров
        /// (400 мс) и D_p не больше 30 кадров (500 мс) даже при RTT 1000; значок «высокая задержка» — при D_p > 150 мс.
        /// </summary>
        [Fact]
        public void D07_S2_7_Lead_ZeroAtLowRtt_GrowsWithRtt_Bounded()
        {
            int[] rtts = { 0, 50, 100, 150, 200, 250, 300, 400, 600, 1000 };
            var leads = new int[rtts.Length];
            for (int i = 0; i < rtts.Length; i++)
            {
                ShipRun r = Run(rtts[i] == 0 ? (NetSimSettings?)null : Sim(rtts[i], 10, false, 29), swings: 6);
                leads[i] = r.Client.LeadFrames;
                _out.WriteLine($"RTT {rtts[i]}: L {r.Client.LeadFrames} (≤ {r.Client.MaxLeadFrames}) кадров, запас {r.Client.LeadMargin}, D_p {r.Client.DelayFrames}, значок {r.Client.HighLatency}; {r.Bot.Summary()}");
                Assert.InRange(r.Client.MaxLeadFrames, 0, InputMessage.MaxLeadFrames);
                Assert.InRange(r.Client.DelayFrames, 0, ThreatTiming.MaxDelayFrames);
                Assert.InRange(r.Bot.MaxDelayFrames, 0, ThreatTiming.MaxDelayFrames);
                Assert.Equal(rtts[i] >= 200, r.Client.HighLatency);
                if (rtts[i] <= 100)
                {
                    Assert.Equal(0, r.Client.MaxLeadFrames);
                }

                if (i > 0)
                {
                    Assert.True(leads[i] >= leads[i - 1], $"L при RTT {rtts[i]} ({leads[i]}) меньше, чем при {rtts[i - 1]} ({leads[i - 1]})");
                }
            }

            Assert.InRange(leads[3], 0, 2);
            Assert.True(leads[6] >= 4, $"L при RTT 300: {leads[6]}");
        }

        /// <summary>
        /// Таблица шипа (0-033, шаг 5): RTT 100 / 200 / 250 / 300 (σ 10, потери 1 %) и 300 пачками σ 20, 100 замахов, показ
        /// честный. Критерий 2.13 п. 3: ≥ 95 из 100 на каждой строке. Ожидание сервера ≤ W, отключений нет, все 100 замахов в
        /// счёте; попадания — только недошедшие нажатия.
        /// </summary>
        [Theory]
        [InlineData(100, 10, false)]
        [InlineData(200, 10, false)]
        [InlineData(250, 10, false)]
        [InlineData(300, 10, false)]
        [InlineData(300, 20, true)]
        public void D07_S2_13_P3_DodgeShip_Rtt(int rttMs, int jitterMs, bool burst)
        {
            ShipRun r = Run(Sim(rttMs, jitterMs, burst, 29));
            _out.WriteLine($"RTT {rttMs}±{jitterMs}{(burst ? " пачками" : "")}, потери 1 %: {r}");
            Assert.Equal(100, r.Bot.Scored);
            Assert.True(r.Bot.Counted >= 95, $"засчитано {r.Bot.Counted} из 100, нужно ≥ 95");
            Assert.InRange(r.Server.MaxThreatWaitFrames, 0, 6);
            Assert.Equal(0, r.Server.Disconnects);
            Assert.Equal(r.Bot.Hits, r.Bot.PressNotArrived); // промахов уворота, кроме недошедших нажатий, нет
        }

        /// <summary>
        /// Критерий шипа на 4 зёрнах (29 / 701 / 702 / 703, у бота свой номер — зерно % 50, как в проверке 0-029): ≥ 95 из
        /// 100 при RTT 100…300 и 400 (2.7: опоздавших 0,01 % до 600 мс). Числа — в «Замер».
        /// </summary>
        [Theory]
        [InlineData(100, 10, false)]
        [InlineData(200, 10, false)]
        [InlineData(250, 10, false)]
        [InlineData(300, 10, false)]
        [InlineData(300, 20, true)]
        [InlineData(400, 10, false)]
        public void D07_S2_13_P3_DodgeShip_Seeds(int rttMs, int jitterMs, bool burst)
        {
            ulong[] seeds = { 29, 701, 702, 703 };
            var counted = new int[seeds.Length];
            int maxWait = 0;
            for (int i = 0; i < seeds.Length; i++)
            {
                ShipRun r = Run(Sim(rttMs, jitterMs, burst, seeds[i]), botIndex: (int)(seeds[i] % 50));
                _out.WriteLine($"зерно {seeds[i]}: {r}");
                counted[i] = r.Bot.Counted;
                maxWait = Math.Max(maxWait, r.Server.MaxThreatWaitFrames);
                Assert.Equal(0, r.Server.Disconnects);
                Assert.True(r.Bot.Counted >= 95, $"зерно {seeds[i]}: засчитано {r.Bot.Counted} из {r.Bot.Scored}, нужно ≥ 95");
            }

            _out.WriteLine($"RTT {rttMs}±{jitterMs}{(burst ? " пачками" : "")}: засчитано по зёрнам {string.Join(" / ", counted)}, ожидание ≤ {maxWait}");
            Assert.InRange(maxWait, 0, 6);
        }

        /// <summary>Нечестный клиент: в каждом пакете ввода поле L подменено, номера кадров — как у честного (опыт проверки 0-033).</summary>
        public sealed class LeadRewrite : ITransport
        {
            private readonly ITransport _inner;
            private readonly sbyte _lead;

            public LeadRewrite(ITransport inner, sbyte lead)
            {
                _inner = inner;
                _lead = lead;
            }

            public int Rewritten { get; private set; }

            public void Poll(long nowMicros, ITransportHandler handler) => _inner.Poll(nowMicros, handler);

            public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
                if (data.Length == InputMessage.Bytes && data[0] == (byte)MessageType.Input)
                {
                    byte[] copy = data.ToArray();
                    copy[InputMessage.Bytes - 1] = (byte)_lead; // L — последний байт
                    Rewritten++;
                    _inner.Send(peer, channel, copy);
                    return;
                }

                _inner.Send(peer, channel, data);
            }

            public void Flush() => _inner.Flush();

            public void Disconnect(int peer, ReadOnlySpan<byte> payload) => _inner.Disconnect(peer, payload);

            public int RoundTripMs(int peer) => _inner.RoundTripMs(peer);

            public void Dispose() => _inner.Dispose();
        }

        /// <summary>
        /// З. 2 проверки 0-033: D_p — по замеру сервера (эхо снимков), поле L пакета ввода его не двигает. Нечестный клиент при
        /// RTT 100 шлёт L = 24 при честных номерах: D_p — как у честного (≤ 8, а не 30), и нажатие через 200–380 мс после конца
        /// настоящего замаха (кадры s + 36 … s + 47) не засчитывается — попадание, как у честного. Мутация «D_p из поля L» краснит.
        /// </summary>
        [Fact]
        public void D07_S2_7_DelayFrames_IgnoresLeadField_LatePressNotCounted()
        {
            foreach (ulong seed in new ulong[] { 11, 404 })
            {
                LeadRewrite? lr = null;
                ShipRun honest = Run(Sim(100, 10, false, seed), swings: 40, botIndex: (int)(seed % 50), latePress: 12);
                ShipRun cheat = Run(Sim(100, 10, false, seed), swings: 40, botIndex: (int)(seed % 50), latePress: 12,
                    wrap: t => lr = new LeadRewrite(t, InputMessage.MaxLeadFrames));
                _out.WriteLine($"зерно {seed}, честный: {honest}");
                _out.WriteLine($"зерно {seed}, L = 24: {cheat}; подменено пакетов {lr!.Rewritten}");
                Assert.True(lr.Rewritten > 1000);
                Assert.Equal(0, cheat.Client.LeadFrames); // своё L клиента — 0: запас от поля L не зависит
                Assert.InRange(cheat.Bot.MaxDelayFrames, 1, 8);
                Assert.Equal(honest.Bot.MaxDelayFrames, cheat.Bot.MaxDelayFrames);
                // Итог «удар решён» приходит раньше позднего нажатия — бот его не засчитывает себе; счёт — по серверу.
                Assert.True(cheat.Bot.PressedInTime >= 39, $"нажатий {cheat.Bot.PressedInTime}");
                // 1-001: удар теперь наносит урон и отбрасывание — корабль умирается и возрождается у центра,
                // 1–2 замаха из 40 промахиваются из-за смены геометрии. Смысл з. 2 (D_p по замеру, позднее
                // нажатие не меняет счёт сервера) — в совпадении попаданий честного и нечестного.
                Assert.True(honest.Server.ThreatHits >= 35, $"попаданий {honest.Server.ThreatHits}");
                Assert.Equal(honest.Server.ThreatHits, cheat.Server.ThreatHits); // позднее нажатие — попадание, как у честного
            }
        }

        /// <summary>
        /// RTT 600 (2.13 п. 4): уворот, нажатый за ≥ 0,1 с до конца показанного замаха, — ≥ 90 из 100 на 4 зёрнах. D_p
        /// упирается в 500 мс (owd 300 + L ≈ 250), показ кончается до 6 кадров позже решения — нажатия в последние 0,1 с
        /// опаздывают (строка с окном до 17 мс — только число, «Замер»).
        /// </summary>
        [Fact]
        public void D07_S2_13_P4_DodgeShip_Rtt600()
        {
            ulong[] seeds = { 29, 701, 702, 703 };
            for (int i = 0; i < seeds.Length; i++)
            {
                ShipRun r = Run(Sim(600, 10, false, seeds[i]), botIndex: (int)(seeds[i] % 50), latestLead: 6);
                ShipRun late = Run(Sim(600, 10, false, seeds[i]), botIndex: (int)(seeds[i] % 50));
                _out.WriteLine($"зерно {seeds[i]}, окно до 0,1 с: {r}");
                _out.WriteLine($"зерно {seeds[i]}, окно до 17 мс: {late}");
                Assert.Equal(0, r.Server.Disconnects);
                Assert.True(r.Bot.Counted >= 90, $"зерно {seeds[i]}: засчитано {r.Bot.Counted} из {r.Bot.Scored}, нужно ≥ 90");
                Assert.True(r.Client.LeadFrames <= InputMessage.MaxLeadFrames && r.Bot.MaxDelayFrames <= ThreatTiming.MaxDelayFrames);
            }
        }
    }
}
