using System;
using System.Collections.Generic;
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
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Предсказание своего персонажа (docs/07, 2.6–2.7; карточка 0-019 по проверке 0-013): при честном детерминированном
    /// шаге правок нет. Две ступени: модель (только <see cref="LocalPrediction"/> и сервер-модель, шагающая каждый
    /// пришедший кадр) — задержка, разброс, пачки кадров, толчок, шов тора, переполнение кольца; и связка целиком —
    /// <see cref="NetServer"/> + <see cref="NetClient"/> через имитатор <see cref="NetSimTransport"/>.
    /// </summary>
    public class PredictionTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const long FrameUs = 1_000_000 / 60;
        private const long TickUs = 1_000_000 / 30;

        private readonly ITestOutputHelper _out;

        public PredictionTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>Скрипт: стоять 0,5 с, восток 2 с, север 2 с, юго-восток 1 с, стоять 1 с, запад 1 с, дальше стоять.</summary>
        private static Input8 Walk(long us)
        {
            double t = us / 1e6;
            return t < 0.5 ? Input8.None
                : t < 2.5 ? new Input8(1, 0)
                : t < 4.5 ? new Input8(0, -1)
                : t < 5.5 ? new Input8(1, 1)
                : t < 6.5 ? Input8.None
                : t < 7.5 ? new Input8(-1, 0)
                : Input8.None;
        }

        private static Input8 East(long us) => new Input8(1, 0);

        /// <summary>Уворот (0-029): нажатие раз в 0,7 с (окно 16 мс — один-два кадра; лишние в рывке пропадают).</summary>
        private static ushort DodgeEvery700(long us) => us % 700_000 < 16_000 ? InputButtons.Dodge : (ushort)0;

        // ---------- Модель: LocalPrediction против сервера, шагающего каждый пришедший кадр ----------

        /// <summary>
        /// Модель связи и сервера: кадры клиента приходят на сервер через задержку с разбросом (по порядку — порядок
        /// держит кольцо сервера), сервер в каждом такте шагает все пришедшие кадры <see cref="Mover.StepFrame"/> и шлёт
        /// (ack, своё состояние) обратно с задержкой; клиент применяет только новейший снимок. Кадры клиента — по
        /// «кадрам Unity» с рывками: до 4 кадров ввода за кадр Unity (пачки).
        /// </summary>
        private sealed class Model
        {
            private readonly MapShape _map;
            private readonly TileSolid _solid;
            private readonly Func<long, Input8> _script;
            private readonly Queue<(uint Frame, InputFrame Input, long At)> _toServer = new Queue<(uint, InputFrame, long)>();
            private readonly List<(long Tick, uint Ack, SelfState Self, long At)> _toClient = new List<(long, uint, SelfState, long)>();
            private DetRandom _rng;
            private Mover _server;
            private uint _ack;
            private long _lastArrive;
            private long _latestTick = -1;

            public Model(MapShape map, TileSolid solid, WorldPos spawn, Func<long, Input8> script, ulong seed)
            {
                _map = map;
                _solid = solid;
                _script = script;
                _rng = new DetRandom(seed);
                _server = Mover.At(map, spawn);
                Pred = new LocalPrediction(map, solid, new WorldPos(map.WidthUnits / 2, map.HeightUnits / 2)); // спавна не знает
            }

            public LocalPrediction Pred { get; }

            public int OneWayMs { get; set; }

            public int JitterMs { get; set; }

            /// <summary>Кадры Unity неровные (8–50 мс), ввод догоняется пачками до 4 кадров.</summary>
            public bool Hitches { get; set; }

            /// <summary>Кнопки кадра по времени (уворот, 0-029); null — без кнопок.</summary>
            public Func<long, ushort>? Buttons { get; set; }

            /// <summary>Толчок сервера (чужое влияние, 2.6 п. 5): такт и смещение по X, unit.</summary>
            public (long Tick, int Dx)? Push { get; set; }

            public WorldPos ServerPos => _server.Position;

            /// <summary>Картинка своего персонажа: (время, позиция + гашение) после каждой правки и в кадры после неё.</summary>
            public List<(long Us, int ErrUnits)> AfterPush { get; } = new List<(long, int)>();

            public void Run(long durationUs)
            {
                uint frame = 1000;
                long nextUnity = 0;
                long accum = 0;
                long tick = 0;
                for (long t = 0; t <= durationUs; t += 1000)
                {
                    if (t >= tick * TickUs)
                    {
                        ServerTick(tick, t);
                        tick++;
                    }

                    if (t < nextUnity)
                    {
                        continue;
                    }

                    long dt = Hitches ? Pick() : FrameUs;
                    nextUnity = t + dt;
                    DeliverSnapshots(t);
                    accum += dt;
                    int sent = 0;
                    while (accum >= FrameUs && sent < 4)
                    {
                        var input = new InputFrame(_script(t), Buttons?.Invoke(t) ?? 0);
                        frame++;
                        Pred.Sent(frame, input);
                        long at = t + OneWayMs * 1000L + (JitterMs > 0 ? NetSimTransport.Gaussian(ref _rng, JitterMs) : 0);
                        _lastArrive = Math.Max(_lastArrive, Math.Max(at, t));
                        _toServer.Enqueue((frame, input, _lastArrive));
                        accum -= FrameUs;
                        sent++;
                    }

                    if (Push.HasValue && AfterPush.Count > 0 || Pred.ErrorDurationUs > 0)
                    {
                        AfterPush.Add((t, Pred.ErrorUnits(t)));
                    }
                }
            }

            private long Pick()
            {
                int r = _rng.NextInt(10);
                return r < 6 ? 16_000 : r < 7 ? 8_000 : r < 9 ? 33_000 : 50_000;
            }

            private void ServerTick(long tick, long t)
            {
                while (_toServer.Count > 0 && _toServer.Peek().At <= t)
                {
                    (uint f, InputFrame input, _) = _toServer.Dequeue();
                    _server.StepFrame(_map, input.Direction, input.DodgePressed, _solid);
                    _ack = f;
                }

                if (Push.HasValue && Push.Value.Tick == tick)
                {
                    _server.Position = Torus.Offset(_map, _server.Position, new Int2(Push.Value.Dx, 0));
                }

                long at = t + OneWayMs * 1000L + (JitterMs > 0 ? NetSimTransport.Gaussian(ref _rng, JitterMs) : 0);
                _toClient.Add((tick, _ack, new SelfState(_server.Position, _server.Carry, _server.Dodge), Math.Max(at, t)));
            }

            private void DeliverSnapshots(long t)
            {
                _toClient.Sort((a, b) => a.At.CompareTo(b.At));
                int n = 0;
                while (n < _toClient.Count && _toClient[n].At <= t)
                {
                    var s = _toClient[n++];
                    if (s.Tick > _latestTick)
                    {
                        _latestTick = s.Tick;
                        Pred.Reconcile(s.Ack, s.Self, t);
                    }
                }

                _toClient.RemoveRange(0, n);
            }
        }

        private static WorldPos Center(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        [Theory]
        [InlineData(0, 0, false)]
        [InlineData(25, 5, false)]
        [InlineData(50, 10, true)]
        [InlineData(50, 20, true)]
        [InlineData(100, 20, true)]
        [InlineData(150, 30, true)]
        public void D07_S2_6_Model_CleanMovement_DelayJitterBursts_NoCorrections(int oneWayMs, int jitterMs, bool hitches)
        {
            MapShape map = MapShape.World(1024);
            var m = new Model(map, Collision.Empty, Center(300, 400), Walk, 11) { OneWayMs = oneWayMs, JitterMs = jitterMs, Hitches = hitches };
            m.Run(9_000_000);
            LocalPrediction p = m.Pred;
            _out.WriteLine($"задержка {oneWayMs}±{jitterMs} мс, пачки {hitches}: сверок {p.Reconciled}, правок {p.Resynced}, установок {p.Initial}, расх. {p.MaxDevUnits} u");
            Assert.Equal(1, p.Initial);
            Assert.True(p.Reconciled > 200, $"сверок {p.Reconciled}");
            Assert.Equal(0, p.Resynced);
            Assert.Equal(0, p.MaxDevUnits);
            Assert.Equal(m.ServerPos, p.Position); // в конце стоит — догнал сервер ровно
        }

        /// <summary>
        /// Толчок сервера (2.6 п. 3–4): ровно одна правка; кадры новее ack переиграны (иначе следующие сверки тоже правки — M1);
        /// гашение 0,1 с до 0,5 тайла, 0,3 с до 2 тайлов, дальше — сразу (M3).
        /// </summary>
        [Theory]
        [InlineData(77, LocalPrediction.NearUs)]
        [InlineData(256, LocalPrediction.FarUs)]
        [InlineData(768, 0L)]
        public void D07_S2_6_Model_Push_OneCorrection_Replay_Smoothing(int pushUnits, long expectUs)
        {
            MapShape map = MapShape.World(1024);
            var m = new Model(map, Collision.Empty, Center(300, 400), Walk, 3) { OneWayMs = 50, JitterMs = 10, Push = (90, pushUnits) };
            m.Run(9_000_000);
            LocalPrediction p = m.Pred;
            _out.WriteLine($"толчок {pushUnits} u: правок {p.Resynced}, расх. {p.MaxDevUnits} u; гашение: " +
                           string.Join(" ", m.AfterPush.GetRange(0, Math.Min(25, m.AfterPush.Count)).ConvertAll(e => $"{(e.Us - m.AfterPush[0].Us) / 1000}:{e.ErrUnits}")));
            Assert.Equal(1, p.Resynced);
            Assert.InRange(p.MaxDevUnits, pushUnits - 1, pushUnits + 1);
            Assert.Equal(m.ServerPos, p.Position);
            if (expectUs == 0)
            {
                Assert.Empty(m.AfterPush); // переставлен сразу: гасить нечего
                return;
            }

            // Первая точка — сразу после правки: смещение ≈ толчку; к середине — половина; к сроку — 0.
            long t0 = m.AfterPush[0].Us;
            int e0 = m.AfterPush[0].ErrUnits;
            Assert.InRange(e0, pushUnits * 8 / 10, pushUnits + 1);
            int mid = m.AfterPush.Find(e => e.Us - t0 >= expectUs / 2).ErrUnits;
            Assert.InRange(mid, pushUnits * 3 / 10, pushUnits * 6 / 10);
            Assert.True(m.AfterPush.TrueForAll(e => e.Us - t0 < expectUs || e.ErrUnits == 0), "гашение длиннее срока");
            Assert.True(m.AfterPush.Exists(e => e.Us - t0 >= expectUs * 8 / 10 && e.Us - t0 < expectUs && e.ErrUnits > 0), "гашение короче срока");
        }

        /// <summary>Шов тора (3.2): старт у x = 1023,9 т, бег на восток через шов — без правок (M4).</summary>
        [Fact]
        public void D07_S2_6_Model_TorusSeam_East_NoCorrections()
        {
            MapShape map = MapShape.World(1024);
            var start = new WorldPos(1024 * U - 26, 100 * U);
            var m = new Model(map, Collision.Empty, start, East, 5) { OneWayMs = 50, JitterMs = 20, Hitches = true };
            m.Run(3_000_000);
            LocalPrediction p = m.Pred;
            Assert.Equal(0, p.Resynced);
            Assert.InRange(Torus.TileOf(map, p.Position).X, 5, 30); // прошёл шов: ~3 с × 6 т/с − задержка
            Assert.Equal(m.ServerPos.Y, p.Position.Y);
        }

        /// <summary>Коллизии в предсказании (замечание 8 проверки 0-013): бег в стену и вдоль неё — тот же Collision, правок нет.</summary>
        [Fact]
        public void D07_S2_6_Model_WallCollision_NoCorrections()
        {
            MapShape map = MapShape.World(1024);
            TileSolid wall = (MapShape _, TileCoord t) => t.X == 305 && t.Y >= 380;
            var m = new Model(map, wall, Center(300, 400), Walk, 9) { OneWayMs = 50, JitterMs = 20, Hitches = true };
            m.Run(9_000_000);
            Assert.Equal(0, m.Pred.Resynced);
            Assert.True(Torus.TileOf(map, m.ServerPos).X < 305, $"прошёл стену: {m.ServerPos}");
        }

        /// <summary>
        /// Уворот в предсказании (0-029, чек-лист: «предсказание рывка — 0 правок на чистом уворот-движении»): бег по скрипту и
        /// уворот раз в 0,7 с при задержке, разбросе и пачках кадров — правок нет, позиция с сервером совпадает. Сервер-модель
        /// шагает те же кадры той же функцией ядра (<see cref="Mover.StepFrame(in MapShape, Input8, bool, TileSolid)"/>).
        /// </summary>
        [Theory]
        [InlineData(0, 0, false)]
        [InlineData(50, 10, true)]
        [InlineData(150, 30, true)]
        public void D02_S2_5_Model_DodgeMovement_NoCorrections(int oneWayMs, int jitterMs, bool hitches)
        {
            MapShape map = MapShape.World(1024);
            TileSolid wall = (MapShape _, TileCoord t) => t.X == 312 && t.Y >= 380; // рывки на восток упираются в стену
            var m = new Model(map, wall, Center(300, 400), Walk, 13) { OneWayMs = oneWayMs, JitterMs = jitterMs, Hitches = hitches, Buttons = DodgeEvery700 };
            m.Run(9_000_000);
            LocalPrediction p = m.Pred;
            _out.WriteLine($"уворот, задержка {oneWayMs}±{jitterMs}: сверок {p.Reconciled}, правок {p.Resynced}, конец {p.Position} / {m.ServerPos}");
            Assert.Equal(0, p.Resynced);
            Assert.Equal(0, p.MaxDevUnits);
            Assert.Equal(m.ServerPos, p.Position);

            // Рывки и правда были: без уворота путь короче.
            var plain = new Model(map, wall, Center(300, 400), Walk, 13) { OneWayMs = oneWayMs, JitterMs = jitterMs, Hitches = hitches };
            plain.Run(9_000_000);
            Assert.NotEqual(plain.ServerPos, m.ServerPos);
        }

        /// <summary>
        /// Толчок посреди рывка (0-029): одна правка, рывок переигран с состоянием уворота на ack (в снимке его нет — берётся
        /// предсказанное для ack); дальше правок нет, позиция совпадает с сервером.
        /// </summary>
        [Fact]
        public void D02_S2_5_Model_PushDuringDash_OneCorrection()
        {
            MapShape map = MapShape.World(1024);
            var m = new Model(map, Collision.Empty, Center(300, 400), Walk, 3) { OneWayMs = 50, JitterMs = 10, Push = (90, 77), Buttons = us => us >= 2_900_000 && us < 2_916_000 ? InputButtons.Dodge : (ushort)0 };
            m.Run(6_000_000);
            _out.WriteLine($"толчок в рывке: правок {m.Pred.Resynced}, расх. {m.Pred.MaxDevUnits} u");
            Assert.Equal(1, m.Pred.Resynced);
            Assert.Equal(m.ServerPos, m.Pred.Position);
        }

        /// <summary>
        /// Кольцо 256 кадров (≈ 4,3 с, решение 0-019): RTT 3 с — правок нет; RTT 4,6 с — ack старше кольца, каждая сверка —
        /// правка со счётчиком <see cref="LocalPrediction.Overflows"/> и в <see cref="LocalPrediction.Resynced"/>.
        /// </summary>
        [Fact]
        public void D07_S2_6_Model_Ring256_Rtt3sClean_Rtt46sOverflowCounted()
        {
            MapShape map = MapShape.World(1024);
            var ok = new Model(map, Collision.Empty, Center(300, 400), East, 1) { OneWayMs = 1500 };
            ok.Run(8_000_000);
            Assert.Equal(0, ok.Pred.Resynced);
            Assert.Equal(0, ok.Pred.Overflows);

            var far = new Model(map, Collision.Empty, Center(300, 400), East, 1) { OneWayMs = 2300 };
            far.Run(10_000_000);
            _out.WriteLine($"RTT 4,6 с: сверок {far.Pred.Reconciled}, правок {far.Pred.Resynced}, из них переполнений {far.Pred.Overflows}");
            Assert.True(far.Pred.Overflows > 50, $"{far.Pred.Overflows}");
            Assert.Equal(far.Pred.Resynced, far.Pred.Overflows);
        }

        // ---------- Связка целиком: NetServer + NetClient через имитатор связи ----------

        private sealed class NetRun
        {
            public long Reconciled;
            public long Resynced;
            public int MaxDev;
            public long Snapshots;
            public NetServer Server = null!;
            public LocalPrediction Pred = null!;
            public WorldPos ServerPos;

            /// <summary>Перенумераций кадров клиентом (<see cref="NetClient.FrameRenumbers"/>).</summary>
            public long Renumbers;
        }

        private static NetRun RunNet(NetSimSettings? s, Func<long, Input8> script, long durationUs, WorldPos spawn, bool hitches, ulong seed,
            Func<long, ushort>? buttons = null)
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 42);
            var sim = new Simulation(world, maxEntities: 8);
            using LoopbackTransport st = LoopbackTransport.CreateServer(4);
            var server = new NetServer(st, sim, _ => spawn, 4, NetModule.Version);
            ITransport ct = LoopbackTransport.ConnectTo(st);
            if (s.HasValue)
            {
                ct = new NetSimTransport(ct, s.Value);
            }

            using ITransport clientTransport = ct;
            var client = new NetClient(ct, LoopbackTransport.ServerPeer, "p", NetModule.Version);
            LocalPrediction? pred = null;
            long now = 0;
            client.SnapshotApplied = snap => pred?.Reconcile(snap.AckFrame, snap.Self, now);
            var rng = new DetRandom(seed);
            long nextUnity = 0;
            long inGameAt = -1;
            long tick = 0;
            for (now = 0; now <= durationUs; now += 1000)
            {
                if (now >= tick * TickUs)
                {
                    server.Poll(now);
                    sim.Step();
                    server.SendSnapshots();
                    tick++;
                }

                if (now < nextUnity)
                {
                    continue;
                }

                long dt = FrameUs;
                if (hitches)
                {
                    int r = rng.NextInt(10);
                    dt = r < 6 ? 16_000 : r < 7 ? 8_000 : r < 9 ? 33_000 : 50_000;
                }

                nextUnity = now + dt;
                client.Poll(now);
                if (client.State != NetClientState.InGame)
                {
                    continue;
                }

                if (pred == null)
                {
                    pred = new LocalPrediction(client.Shape, sim.Solid, new WorldPos(client.Shape.WidthUnits / 2, client.Shape.HeightUnits / 2));
                    inGameAt = now;
                }

                // Темп кадров — по часам ввода клиента (NetClient.FramesDue, как NetGame): с опережением L (0-033) номер идёт за
                // оценкой + L, и своё «сколько прошло» (dt) разошлось бы с ним — перенумерация и перескок у сервера.
                int due = client.FramesDue(now);
                for (int sent = 0; sent < due; sent++)
                {
                    var input = new InputFrame(script(now - inGameAt), buttons?.Invoke(now - inGameAt) ?? 0);
                    client.SendFrame(input);
                    pred.Sent(client.Frame, input);
                }
            }

            Assert.NotNull(pred);
            return new NetRun
            {
                Reconciled = pred!.Reconciled, Resynced = pred.Resynced, MaxDev = pred.MaxDevUnits, Snapshots = client.SnapshotsApplied,
                Server = server, Pred = pred, ServerPos = sim.Entities[0].Mover.Position,
                Renumbers = client.FrameRenumbers,
            };
        }

        /// <summary>
        /// Критерий 0-019 (п. 4 карточки) в CI: сервер шагает ввод покадрово, своё состояние — без кванта; через имитатор
        /// (RTT 100, σ 20, потери 1 %; RTT 200 пачками; перестановка и дубли) правок на чистом движении нет, расхождение 0.
        /// Сверка — с серверным состоянием из снимка, не клиента с самим собой.
        /// <para>
        /// Правило (П по 0-026, з. 4): правок 0 — только в прогонах, где сервер не перескакивал и не выбрасывал кадры
        /// (<c>FramesSkipped + FramesTrimmed == 0</c>). Перескок (2.7: дыра в номерах не закрылась за <c>HoleWaitTicks</c>)
        /// шагает не тот ввод, что предсказал клиент, — правка там верный исход, но их не больше, чем перескоков. Прогон —
        /// на 8 зёрнах имитатора (в ночном прогоне — на 50, <see cref="D07_S2_6_Net_Big_ThroughSimulator_CleanMovement_50Seeds"/>),
        /// а не на одном удачном.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(0, 0, 0.0, false, false)]
        [InlineData(100, 20, 1.0, false, true)]
        [InlineData(200, 30, 1.0, true, true)]
        [InlineData(80, 10, 2.0, false, true)]
        public void D07_S2_6_Net_ThroughSimulator_CleanMovement_NoCorrections(int rttMs, int jitterMs, double lossPercent, bool burstJitter, bool hitches)
        {
            CleanMovementOnSeeds(rttMs, jitterMs, lossPercent, burstJitter, hitches, 8);
        }

        /// <summary>То же, что <see cref="D07_S2_6_Net_ThroughSimulator_CleanMovement_NoCorrections"/>, на 50 зёрнах имитатора (ночной прогон).</summary>
        [Theory]
        [Trait("Category", "Big")]
        [InlineData(100, 20, 1.0, false, true)]
        [InlineData(200, 30, 1.0, true, true)]
        [InlineData(80, 10, 2.0, false, true)]
        public void D07_S2_6_Net_Big_ThroughSimulator_CleanMovement_50Seeds(int rttMs, int jitterMs, double lossPercent, bool burstJitter, bool hitches)
        {
            CleanMovementOnSeeds(rttMs, jitterMs, lossPercent, burstJitter, hitches, 50);
        }

        private void CleanMovementOnSeeds(int rttMs, int jitterMs, double lossPercent, bool burstJitter, bool hitches, int seeds)
        {
            MapShape shape = MapShape.World(1024);
            WorldPos spawn = ServerRun.SpawnPoint(shape, 0);
            int runs = rttMs > 0 ? seeds : 1; // без имитатора случайности нет — один прогон
            int clean = 0;
            for (int seed = 1; seed <= runs; seed++)
            {
                NetSimSettings? s = null;
                if (rttMs > 0)
                {
                    NetSimSettings v = NetSimSettings.FromRtt(rttMs, jitterMs, lossPercent, (ulong)seed);
                    v.BurstJitter = burstJitter;
                    if (lossPercent >= 2)
                    {
                        v.DuplicatePpm = 20_000;
                        v.ReorderPpm = 50_000;
                    }

                    s = v;
                }

                NetRun r = RunNet(s, Walk, 10_000_000, spawn, hitches, 7);
                NetServer sv = r.Server;
                long skips = sv.FramesSkipped + sv.FramesTrimmed;
                long diverge = skips + sv.FramesSelfStepped; // шаг сам (2.7 «Нет ввода»: пачка σ 30 без пакетов 100 мс) — расхождение по правилу (0-033)
                string tag = $"RTT {rttMs}±{jitterMs}, потери {lossPercent} %, пачки {burstJitter}/{hitches}, зерно {seed}";
                _out.WriteLine($"{tag}: снимков {r.Snapshots}, сверок {r.Reconciled}, правок {r.Resynced}, расх. {r.MaxDev} u; " +
                               $"сервер: кадров {sv.FramesApplied}, сам {sv.FramesSelfStepped}, перескок {sv.FramesSkipped}, " +
                               $"выброшено {sv.FramesTrimmed}, перенумераций {r.Renumbers}, опоздали {sv.FramesLate}, тактов-пачек {sv.BurstTicks}, стопов {sv.InputStops}");
                // Порог относительный (0-025, проверка з. 4; было «> 250»): число принятых снимков зависит от зерна имитатора —
                // переставленные разбросом отбрасываются как старые (RTT 100±20: 240–251 из 300 на 8 зёрнах). Сверка — на каждом
                // принятом снимке, кроме первых до начала предсказания: ≥ 90 % (на 8 зёрнах × 4 строки — не меньше 98 %).
                Assert.True(r.Snapshots > 200, $"{tag}: снимков {r.Snapshots} из 300");
                Assert.True(r.Reconciled >= r.Snapshots * 0.9, $"{tag}: сверок {r.Reconciled} из {r.Snapshots} снимков");
                // Без имитатора связь чистая: перескок там — только дыра в номерах от перенумерации кадров клиентом
                // (FrameRenumbers, подстройка часов 0-025), предсказание перенумеровывает так же — правок быть не может.
                if (skips == 0 || s == null)
                {
                    clean++;
                }

                if (diverge == 0 || s == null)
                {
                    Assert.True(r.Resynced == 0, $"{tag}: правок {r.Resynced} без перескока и выброса кадров сервером (перескок {skips})");
                    Assert.True(r.MaxDev == 0, $"{tag}: расхождение {r.MaxDev} u без перескока и выброса кадров");
                }
                else
                {
                    Assert.True(r.Resynced <= diverge, $"{tag}: правок {r.Resynced} больше перескоков и шагов сам {diverge}");
                }

                Assert.Equal(r.ServerPos, r.Pred.Position);
                Assert.True(Torus.Dist2(shape, spawn, r.ServerPos) > 4L * U * 4 * U, $"{tag}: персонаж не ушёл");
            }

            // Перескок — редкость (50 зёрен RTT 200 пачками: 4 из 50): если «чистых» прогонов меньше половины, правило «правок 0»
            // проверено не было.
            Assert.True(clean * 2 >= runs, $"чистых прогонов (без перескока) {clean} из {runs}");
        }

        /// <summary>Шов тора через сеть: спавн у x = 1023,9 т, бег на восток — правок нет (M4 в связке).</summary>
        [Fact]
        public void D07_S2_6_Net_TorusSeam_NoCorrections()
        {
            var spawn = new WorldPos(1024 * U - 26, 512 * U);
            NetRun r = RunNet(NetSimSettings.FromRtt(100, 20, 1.0, 3), East, 4_000_000, spawn, true, 2);
            Assert.Equal(0, r.Resynced);
            Assert.InRange(Torus.TileOf(MapShape.World(1024), r.Pred.Position).X, 5, 40);
        }

        /// <summary>
        /// Уворот через сеть (0-029): кнопка уворота доходит до сервера в кадре ввода, <see cref="Simulation"/> шагает рывок
        /// той же функцией, что предсказание. RTT 100 σ 20, потери 1 % — правок нет. RTT 200 пачками: бег без правок, а нажатие
        /// в опоздавшем кадре сервер пока теряет (кадр выбрасывается; «липкие» нажатия `07` 2.7 — не в этой карточке) —
        /// правок не больше опоздавших кадров, и после правки позиция снова совпадает с сервером.
        /// </summary>
        [Theory]
        [InlineData(100, 20, false)]
        [InlineData(200, 30, true)]
        public void D02_S2_5_Net_DodgeMovement_NoCorrections(int rttMs, int jitterMs, bool burstJitter)
        {
            NetSimSettings v = NetSimSettings.FromRtt(rttMs, jitterMs, 1.0, 23);
            v.BurstJitter = burstJitter;
            NetRun r = RunNet(v, Walk, 10_000_000, ServerRun.SpawnPoint(MapShape.World(1024), 0), true, 5, DodgeEvery700);
            NetServer sv = r.Server;
            _out.WriteLine($"уворот, RTT {rttMs}±{jitterMs}, пачки {burstJitter}: сверок {r.Reconciled}, правок {r.Resynced}, расх. {r.MaxDev} u; " +
                           $"сервер: кадров {sv.FramesApplied}, сам {sv.FramesSelfStepped}, перескок {sv.FramesSkipped}, опоздали {sv.FramesLate}");
            Assert.True(r.Reconciled > 200, $"сверок {r.Reconciled}");
            Assert.Equal(r.ServerPos, r.Pred.Position);
            if (!burstJitter)
            {
                Assert.Equal(0, r.Resynced);
                Assert.Equal(0, r.MaxDev);
                return;
            }

            NetRun plain = RunNet(v, Walk, 10_000_000, ServerRun.SpawnPoint(MapShape.World(1024), 0), true, 5);
            _out.WriteLine($"без уворота: правок {plain.Resynced}, сам {plain.Server.FramesSelfStepped}, опоздали {plain.Server.FramesLate}");
            Assert.Equal(0, plain.Resynced);
            Assert.InRange(r.Resynced, 0, sv.FramesLate);
        }

        // ---------- Сервер: ввод покадрово ----------

        /// <summary>
        /// Шаг 1 карточки: кадры приходят пачками (0, 4, 1, 3, 0, 0, 6, 2 … за такт) — сервер шагает каждый по порядку
        /// полшага: позиция = эталонный <see cref="Mover.StepFrame"/> по тем же кадрам, <c>AckFrame</c> = последний шагнутый.
        /// Против ускорения: 4 кадра за такт 60 тактов — шагнуто не больше 2 × тактов + запас ведра (20), остальное отброшено со счётчиком.
        /// </summary>
        [Fact]
        public void D07_S2_7_Server_AppliesEveryFrameInOrder_Bursts_SpeedCapped()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            var sim = new Simulation(world, maxEntities: 4);
            var t = new NetTests.ScriptedTransport();
            WorldPos spawn = ServerRun.SpawnPoint(world.Shape, 0);
            var server = new NetServer(t, sim, _ => spawn, 4, NetModule.Version);
            for (int k = 0; k < ScriptedPlayer.LeadInTicks; k++)
            {
                Tick(); // номера кадров с 1 отстают от времени сервера (0-033: кадры «из будущего» ждут своего такта)
            }

            t.Connect(1);
            t.Push(1, NetChannel.Facts, Encode(b => new LoginMessage { Version = NetModule.Version, Name = "p" }.Write(b)));
            void Tick()
            {
                server.Poll(sim.Tick);
                sim.Step();
                server.SendSnapshots();
            }

            Tick();
            int id = server.EntityOf(1);
            Assert.True(id > 0);

            var rng = new DetRandom(4);
            var reference = Mover.At(world.Shape, spawn);
            var frames = new List<InputFrame>();
            uint last = 0;
            var input = new InputMessage();
            void SendFrame(InputFrame f)
            {
                input.Frame0 = input.Frame1;
                input.Frame1 = input.Frame2;
                input.Frame2 = input.Frame3;
                input.Frame3 = WireFrame.From(f);
                input.FirstFrame++;
                last = input.LastFrame;
                byte[] b = new byte[InputMessage.Bytes];
                input.Write(b);
                t.Push(1, NetChannel.Unreliable, b);
            }

            int[] pattern = { 0, 4, 1, 3, 0, 0, 6, 2, 2, 1, 3 };
            long sent = 0;
            for (int k = 0; k < 120; k++)
            {
                for (int j = 0; j < pattern[k % pattern.Length]; j++)
                {
                    var f = new InputFrame(Input8.Directions[rng.NextInt(8)], 0);
                    SendFrame(f);
                    frames.Add(f);
                    sent++;
                }

                Tick();
            }

            for (int k = 0; k < 2; k++)
            {
                Tick(); // догнать очередь (не дольше W = 3 тактов — иначе сервер зашагает сам)
            }

            // Первые три кадра пакета — «до входа» (пустые): эталон шагает и их.
            for (int i = 0; i < 3; i++)
            {
                reference.StepFrame(world.Shape, Input8.None, sim.Solid);
            }

            foreach (InputFrame f in frames)
            {
                reference.StepFrame(world.Shape, f.Direction, sim.Solid);
            }

            SimEntity e = sim.GetEntity(id);
            Assert.Equal(reference.Position, e.Mover.Position);
            Assert.Equal(reference.Carry, e.Mover.Carry);
            Assert.True(server.TryGetLastFrame(id, out uint ack));
            Assert.Equal(last, ack);
            Assert.Equal(sent + 3, server.FramesApplied);
            Assert.True(server.BurstTicks > 0);
            Assert.Equal(0, server.FramesSkipped);
            Assert.Equal(0, server.FramesSelfStepped);

            // Ускорение: 4 кадра на такт 60 тактов подряд.
            long appliedBefore = server.FramesApplied;
            for (int k = 0; k < 60; k++)
            {
                for (int j = 0; j < 4; j++)
                {
                    SendFrame(new InputFrame(new Input8(1, 0), 0));
                }

                Tick();
            }

            long applied = server.FramesApplied - appliedBefore;
            _out.WriteLine($"ускорение: отправлено 240, шагнуто {applied}, отброшено {server.FramesOverflow}");
            Assert.InRange(applied, 120, 120 + NetServer.CreditCap);
            Assert.True(server.FramesOverflow + server.FramesTrimmed > 0);
        }

        /// <summary>
        /// Дыра в номерах кадров (перестановка больше 4 кадров): сервер ждёт <see cref="NetServer.HoleWaitTicks"/> такта —
        /// опоздавший пакет закрывает дыру, кадры шагаются по порядку без перескока; не пришёл — перескок со счётчиком.
        /// </summary>
        [Fact]
        public void D07_S2_7_Server_HoleWaitsForReorderedPacket_ThenSkips()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            var sim = new Simulation(world, maxEntities: 4);
            var t = new NetTests.ScriptedTransport();
            WorldPos spawn = ServerRun.SpawnPoint(world.Shape, 0);
            var server = new NetServer(t, sim, _ => spawn, 4, NetModule.Version);
            for (int k = 0; k < ScriptedPlayer.LeadInTicks; k++)
            {
                Tick(); // номера кадров с 1 отстают от времени сервера (0-033: кадры «из будущего» ждут своего такта)
            }

            t.Connect(1);
            t.Push(1, NetChannel.Facts, Encode(b => new LoginMessage { Version = NetModule.Version, Name = "p" }.Write(b)));
            void Tick()
            {
                server.Poll(sim.Tick);
                sim.Step();
                server.SendSnapshots();
            }

            Tick();
            int id = server.EntityOf(1);
            var east = WireFrame.From(new InputFrame(new Input8(1, 0), 0));
            var north = WireFrame.From(new InputFrame(new Input8(0, -1), 0));
            void Packet(uint first, WireFrame f)
            {
                var m = new InputMessage { FirstFrame = first, Frame0 = f, Frame1 = f, Frame2 = f, Frame3 = f };
                t.Push(1, NetChannel.Unreliable, Encode(b => m.Write(b)));
            }

            Packet(1, east); // кадры 1–4
            Tick();
            Packet(9, north); // кадры 9–12 обогнали 5–8
            Tick();
            Assert.True(server.TryGetLastFrame(id, out uint ack));
            Assert.Equal(4u, ack); // ждёт дыру 5–8
            Packet(5, east); // опоздавший пакет закрыл дыру
            Tick();
            Tick();
            Assert.True(server.TryGetLastFrame(id, out ack));
            Assert.Equal(12u, ack);
            Assert.Equal(0, server.FramesSkipped);

            var reference = Mover.At(world.Shape, spawn);
            for (int i = 0; i < 12; i++)
            {
                reference.StepFrame(world.Shape, i < 8 ? new Input8(1, 0) : new Input8(0, -1), sim.Solid);
            }

            Assert.Equal(reference.Position, sim.GetEntity(id).Mover.Position);

            // Дыра 13–16 не закрылась: через 2 такта — перескок к 17.
            Packet(17, east);
            Tick();
            Assert.True(server.TryGetLastFrame(id, out ack));
            Assert.Equal(12u, ack);
            Tick();
            Tick();
            Assert.True(server.TryGetLastFrame(id, out ack));
            Assert.Equal(20u, ack);
            Assert.Equal(4, server.FramesSkipped);
        }

        /// <summary>Сервер на заглушке транспорта и игрок, шлющий кадры по одному пакету (4 кадра) — для тестов очереди.</summary>
        private sealed class ScriptedPlayer
        {
            /// <summary>Тактов сервера до входа игрока (номера его кадров отстают от времени сервера).</summary>
            public const int LeadInTicks = 60;

            private readonly NetTests.ScriptedTransport _t = new NetTests.ScriptedTransport();
            private InputMessage _input;

            public ScriptedPlayer()
            {
                WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
                Sim = new Simulation(world, maxEntities: 4);
                Server = new NetServer(_t, Sim, _ => ServerRun.SpawnPoint(world.Shape, 0), 4, NetModule.Version);
                // Номера кадров игрока идут с 1, а сервер применяет кадры не дальше своего такта (2.7, 0-033): сначала 2 с сервера —
                // номера отстают от времени сервера на 120 кадров, как у клиента с задержкой, и пачки не упираются в «будущее».
                for (int k = 0; k < LeadInTicks; k++)
                {
                    Tick();
                }

                _t.Connect(1);
                _t.Push(1, NetChannel.Facts, Encode(b => new LoginMessage { Version = NetModule.Version, Name = "p" }.Write(b)));
                Tick();
                Id = Server.EntityOf(1);
                Assert.True(Id > 0);
            }

            public Simulation Sim { get; }

            public NetServer Server { get; }

            public int Id { get; }

            /// <summary>Номер последнего отправленного кадра.</summary>
            public uint Last => _input.LastFrame;

            public WorldPos Pos => Sim.GetEntity(Id).Mover.Position;

            public InputFrame Input => Sim.GetEntity(Id).Input;

            /// <summary>Кадров ждёт на сервере после такта: пришло, но ещё не шагнуто (выброшенные не ждут).</summary>
            public long Backlog => Server.BacklogOf(Id);

            /// <summary>Пакет нового кадра; <paramref name="deliver"/> = false — «застрял в сети», лежит в <see cref="Held"/>.</summary>
            public void Send(InputFrame f, bool deliver = true)
            {
                _input.Frame0 = _input.Frame1;
                _input.Frame1 = _input.Frame2;
                _input.Frame2 = _input.Frame3;
                _input.Frame3 = WireFrame.From(f);
                _input.FirstFrame++;
                InputMessage m = _input;
                byte[] b = Encode(buf => m.Write(buf));
                if (deliver)
                {
                    _t.Push(1, NetChannel.Unreliable, b);
                }
                else
                {
                    Held.Add(b);
                }
            }

            public List<byte[]> Held { get; } = new List<byte[]>();

            public void Release()
            {
                foreach (byte[] b in Held)
                {
                    _t.Push(1, NetChannel.Unreliable, b);
                }

                Held.Clear();
            }

            public void Tick()
            {
                Server.Poll(Sim.Tick);
                Sim.Step();
                Server.SendSnapshots();
            }
        }

        private static readonly InputFrame RunEast = new InputFrame(new Input8(1, 0), 0x0005);

        /// <summary>
        /// Блокирующее замечание проверки 0-019: всплеск задержки (500 / 900 мс без пакетов, потом всё пачкой) — очередь
        /// сервера возвращается к норме (не «навсегда 20 / 44 кадра»), а обрыв после этого — стоп ровно через 6 тактов
        /// после последнего пришедшего кадра (2.13 п. 6, как в 0-015).
        /// </summary>
        [Theory]
        [InlineData(15)]
        [InlineData(27)]
        public void D07_S2_7_Server_DelaySpike_QueueDrains_StopStill6Ticks(int spikeTicks)
        {
            var p = new ScriptedPlayer();
            for (int k = 0; k < 30; k++)
            {
                p.Send(RunEast);
                p.Send(RunEast);
                p.Tick();
            }

            Assert.Equal(0, p.Backlog);
            for (int k = 0; k < spikeTicks; k++)
            {
                p.Send(RunEast, deliver: false);
                p.Send(RunEast, deliver: false);
                p.Tick();
            }

            p.Release(); // задержка спала: всё застрявшее — разом
            var backlog = new List<long>();
            for (int k = 0; k < 30; k++)
            {
                p.Send(RunEast);
                p.Send(RunEast);
                p.Tick();
                backlog.Add(p.Backlog);
            }

            _out.WriteLine($"всплеск {spikeTicks} тактов: очередь после пачки {string.Join(" ", backlog.GetRange(0, 12))}…; " +
                           $"выброшено {p.Server.FramesTrimmed}, самоход {p.Server.FramesSelfStepped}");
            Assert.True(backlog[0] <= NetServer.MaxBacklogFrames, $"сразу после пачки ждёт {backlog[0]}");
            Assert.True(backlog.GetRange(5, 25).TrueForAll(b => b == 0), $"очередь не рассосалась: {string.Join(" ", backlog)}");
            Assert.True(p.Server.FramesTrimmed > 0);

            // Обрыв: последний кадр пришёл на приёме такта lastFrameTick — стоит ровно через 6 тактов.
            long lastFrameTick = p.Sim.Tick - 1; // кадры приняты на приёме перед последним тактом
            var pos = new Dictionary<long, WorldPos>();
            for (int k = 0; k < 12; k++)
            {
                p.Tick();
                pos[p.Sim.Tick] = p.Pos;
            }

            Assert.NotEqual(pos[lastFrameTick + 5], pos[lastFrameTick + 6]);
            Assert.Equal(pos[lastFrameTick + 6], pos[p.Sim.Tick]);
            Assert.Equal(InputFrame.None, p.Input);
        }

        /// <summary>Часы клиента на 0,1 % быстрее (лишний кадр на 1000): 10 минут — очередь ограничена, лишнее выброшено.</summary>
        [Fact]
        public void D07_S2_7_Server_ClientClockFast_QueueBounded()
        {
            var p = new ScriptedPlayer();
            long max = 0;
            int sent = 0;
            for (int k = 0; k < 18_000; k++)
            {
                int n = 2 + ((sent + 2) / 1000 > sent / 1000 ? 1 : 0); // кадр на 1000 сверх 2 за такт
                for (int j = 0; j < n; j++)
                {
                    p.Send(RunEast);
                }

                sent += n;
                p.Tick();
                max = Math.Max(max, p.Backlog);
            }

            _out.WriteLine($"10 мин, +0,1 %: отправлено {sent}, шагнуто {p.Server.FramesApplied}, выброшено {p.Server.FramesTrimmed}, очередь max {max}");
            Assert.True(max <= 6, $"очередь {max}"); // потолок числом (0-025, проверка 0-019 з. 2): мутация «6 → 10» краснит
            Assert.True(p.Server.FramesTrimmed > 0);
        }

        /// <summary>
        /// Запас кадров 20 — числом (0-025, проверка 0-019 повт. з. 1): после долгого затишья запас полон; клиент шлёт по 4 кадра
        /// за такт 40 тактов — шагнуто 2·40 кадров и сверх того не больше 20 (запас), дальше по 2 за такт. Мутация «запас 20 → 60» краснит.
        /// </summary>
        [Fact]
        public void D07_S2_7_Server_CreditCap20_FourPerTick40Ticks_Applies100()
        {
            var p = new ScriptedPlayer();
            for (int k = 0; k < 10; k++)
            {
                p.Send(RunEast);
                p.Send(RunEast);
                p.Tick();
            }

            for (int k = 0; k < 20; k++)
            {
                p.Tick(); // затишье 20 тактов: самоход 6 кадров, стоп, запас копится до потолка
            }

            long before = p.Server.FramesApplied;
            for (int k = 0; k < 40; k++)
            {
                for (int j = 0; j < 4; j++)
                {
                    p.Send(RunEast);
                }

                p.Tick();
            }

            long applied = p.Server.FramesApplied - before;
            _out.WriteLine($"40 тактов по 4 кадра после затишья: шагнуто {applied}, выброшено {p.Server.FramesTrimmed}, очередь {p.Backlog}");
            // Сверх 2 за такт — запас (≤ 20); первые кадры после самохода опоздали (их номера сервер шагнул сам) — минус 4.
            Assert.InRange(applied - 2 * 40, 14, 20);
        }

        /// <summary>Предел кадров за такт (мутация A проверки 0-019): после затишья 12 кадров разом — шагнуто 6, остальные — следующим тактом.</summary>
        [Fact]
        public void D07_S2_7_Server_BurstAfterQuiet_Max6FramesPerTick()
        {
            var p = new ScriptedPlayer();
            for (int k = 0; k < 10; k++)
            {
                p.Send(RunEast);
                p.Send(RunEast);
                p.Tick();
            }

            p.Tick(); // затишье 2 такта — меньше W, самохода нет
            p.Tick();
            long before = p.Server.FramesApplied;
            for (int j = 0; j < 12; j++)
            {
                p.Send(RunEast);
            }

            p.Tick();
            Assert.Equal(NetServer.FramesPerTickLimit, p.Server.FramesApplied - before);
            Assert.Equal(6, NetServer.FramesPerTickLimit);
            p.Tick();
            Assert.Equal(12, p.Server.FramesApplied - before);
            Assert.Equal(0, p.Backlog);
            Assert.Equal(0, p.Server.FramesTrimmed);
            Assert.Equal(0, p.Server.FramesSelfStepped);
        }

        /// <summary>
        /// Самоход (2.7 «Нет ввода», мутация B проверки 0-019): кадры пропали — сервер шагает сам ровно 6 кадров (100 мс)
        /// последним направлением с отпущенными кнопками, лишний пробег — ровно 6 полушагов, потом стоит.
        /// </summary>
        [Fact]
        public void D07_S2_7_Server_SelfStep_Exactly6Frames_ButtonsReleased()
        {
            var p = new ScriptedPlayer();
            for (int k = 0; k < 20; k++)
            {
                p.Send(RunEast);
                p.Send(RunEast);
                p.Tick();
            }

            Mover reference = p.Sim.GetEntity(p.Id).Mover;
            for (int i = 0; i < NetServer.SelfStepFrames; i++)
            {
                reference.StepFrame(p.Sim.Shape, RunEast.Direction, p.Sim.Solid);
            }

            long ticksBefore = p.Server.FramesSelfStepped;
            var buttons = new List<ushort>();
            for (int k = 0; k < 15; k++)
            {
                p.Tick();
                buttons.Add(p.Input.Buttons);
            }

            Assert.Equal(6, NetServer.SelfStepFrames);
            Assert.Equal(6, p.Server.FramesSelfStepped - ticksBefore);
            Assert.Equal(reference.Position, p.Pos);
            Assert.True(buttons.GetRange(NetServer.SelfStepAfterTicks, 12).TrueForAll(b => b == 0), string.Join(" ", buttons));
            Assert.Equal(1, p.Server.InputStops);
        }

        // ---------- Имитатор: Flush (замечание 1 повторной проверки 0-015, мутации F3 и F4) ----------

        /// <summary>
        /// <c>NetSimTransport.Flush</c>: до срока пакет не уходит (F4 — «отдать всё не глядя на срок»), обёрнутый транспорт
        /// сброшен (F3); после срока (Poll) пакет ушёл, следующий Flush снова сбрасывает обёрнутый.
        /// </summary>
        [Fact]
        public void D00_Net_Sim_Flush_HoldsUntilDue_FlushesInner()
        {
            var inner = new CountingTransport();
            using var sim = new NetSimTransport(inner, new NetSimSettings { DelayMs = 50, Seed = 1 });
            sim.Poll(0, new NullHandler());
            sim.Send(1, NetChannel.Unreliable, new byte[] { 1, 2, 3 });
            sim.Flush();
            Assert.Equal(0, inner.Sent);
            Assert.Equal(1, inner.Flushes);

            sim.Poll(49_000, new NullHandler());
            sim.Flush();
            Assert.Equal(0, inner.Sent);
            Assert.Equal(2, inner.Flushes);

            sim.Poll(50_000, new NullHandler());
            Assert.Equal(1, inner.Sent);
            sim.Flush();
            Assert.Equal(3, inner.Flushes);
            Assert.Equal(1, inner.Sent);
        }

        private sealed class CountingTransport : ITransport
        {
            public long Sent;
            public long Flushes;

            public void Poll(long nowMicros, ITransportHandler handler)
            {
            }

            public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data) => Sent++;

            public void Disconnect(int peer, ReadOnlySpan<byte> payload)
            {
            }

            public void Flush() => Flushes++;

            public int RoundTripMs(int peer) => 0;

            public void Dispose()
            {
            }
        }

        private sealed class NullHandler : ITransportHandler
        {
            public void OnConnected(int peer)
            {
            }

            public void OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload)
            {
            }

            public void OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
            }
        }

        private delegate int Writer(Span<byte> buf);

        private static byte[] Encode(Writer write)
        {
            var buf = new byte[NetSettings.MaxUnreliablePayload];
            int n = write(buf);
            return buf.AsSpan(0, n).ToArray();
        }
    }
}
