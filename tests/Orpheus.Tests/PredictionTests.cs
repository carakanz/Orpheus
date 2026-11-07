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
                        var input = new InputFrame(_script(t), 0);
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
                    _server.StepFrame(_map, input.Direction, _solid);
                    _ack = f;
                }

                if (Push.HasValue && Push.Value.Tick == tick)
                {
                    _server.Position = Torus.Offset(_map, _server.Position, new Int2(Push.Value.Dx, 0));
                }

                long at = t + OneWayMs * 1000L + (JitterMs > 0 ? NetSimTransport.Gaussian(ref _rng, JitterMs) : 0);
                _toClient.Add((tick, _ack, new SelfState(_server.Position, _server.Carry), Math.Max(at, t)));
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
        }

        private static NetRun RunNet(NetSimSettings? s, Func<long, Input8> script, long durationUs, WorldPos spawn, bool hitches, ulong seed)
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
            long accum = 0;
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

                accum += dt;
                for (int sent = 0; accum >= FrameUs && sent < 4; sent++, accum -= FrameUs)
                {
                    var input = new InputFrame(script(now - inGameAt), 0);
                    client.SendFrame(input);
                    pred.Sent(client.Frame, input);
                }
            }

            Assert.NotNull(pred);
            return new NetRun
            {
                Reconciled = pred!.Reconciled, Resynced = pred.Resynced, MaxDev = pred.MaxDevUnits, Snapshots = client.SnapshotsApplied,
                Server = server, Pred = pred, ServerPos = sim.Entities[0].Mover.Position,
            };
        }

        /// <summary>
        /// Критерий 0-019 (п. 4 карточки) в CI: сервер шагает ввод покадрово, своё состояние — без кванта; через имитатор
        /// (RTT 100, σ 20, потери 1 %; RTT 200 пачками; перестановка и дубли) правок на чистом движении нет, расхождение 0.
        /// Сверка — с серверным состоянием из снимка, не клиента с самим собой.
        /// </summary>
        [Theory]
        [InlineData(0, 0, 0.0, false, false)]
        [InlineData(100, 20, 1.0, false, true)]
        [InlineData(200, 30, 1.0, true, true)]
        [InlineData(80, 10, 2.0, false, true)]
        public void D07_S2_6_Net_ThroughSimulator_CleanMovement_NoCorrections(int rttMs, int jitterMs, double lossPercent, bool burstJitter, bool hitches)
        {
            NetSimSettings? s = null;
            if (rttMs > 0)
            {
                NetSimSettings v = NetSimSettings.FromRtt(rttMs, jitterMs, lossPercent, 19);
                v.BurstJitter = burstJitter;
                if (lossPercent >= 2)
                {
                    v.DuplicatePpm = 20_000;
                    v.ReorderPpm = 50_000;
                }

                s = v;
            }

            NetRun r = RunNet(s, Walk, 10_000_000, ServerRun.SpawnPoint(MapShape.World(1024), 0), hitches, 7);
            NetServer sv = r.Server;
            _out.WriteLine($"RTT {rttMs}±{jitterMs}, потери {lossPercent} %, пачки {burstJitter}/{hitches}: снимков {r.Snapshots}, сверок {r.Reconciled}, " +
                           $"правок {r.Resynced}, расх. {r.MaxDev} u; сервер: кадров {sv.FramesApplied}, сам {sv.FramesSelfStepped}, " +
                           $"перескок {sv.FramesSkipped}, опоздали {sv.FramesLate}, тактов-пачек {sv.BurstTicks}, стопов {sv.InputStops}");
            Assert.True(r.Reconciled > 250, $"сверок {r.Reconciled}");
            Assert.Equal(0, r.Resynced);
            Assert.Equal(0, r.MaxDev);
            Assert.Equal(r.ServerPos, r.Pred.Position);
            Assert.True(Torus.Dist2(MapShape.World(1024), ServerRun.SpawnPoint(MapShape.World(1024), 0), r.ServerPos) > 4L * U * 4 * U, "персонаж не ушёл");
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

        // ---------- Сервер: ввод покадрово ----------

        /// <summary>
        /// Шаг 1 карточки: кадры приходят пачками (0, 4, 1, 3, 0, 0, 6, 2 … за такт) — сервер шагает каждый по порядку
        /// полшага: позиция = эталонный <see cref="Mover.StepFrame"/> по тем же кадрам, <c>AckFrame</c> = последний шагнутый.
        /// Против ускорения: 4 кадра за такт 60 тактов — шагнуто не больше 2 × тактов + 4, остальное отброшено со счётчиком.
        /// </summary>
        [Fact]
        public void D07_S2_7_Server_AppliesEveryFrameInOrder_Bursts_SpeedCapped()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            var sim = new Simulation(world, maxEntities: 4);
            var t = new NetTests.ScriptedTransport();
            WorldPos spawn = ServerRun.SpawnPoint(world.Shape, 0);
            var server = new NetServer(t, sim, _ => spawn, 4, NetModule.Version);
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
            Assert.InRange(applied, 120, 120 + NetServer.FramesPerTickLimit);
            Assert.True(server.FramesOverflow > 0);
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
