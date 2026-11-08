using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orpheus.Bots;
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
    /// Пауза (карточка 0-036; BRIEF п. 11b — у любого игрока есть возможность поставить паузу; `07` 2.3, 2.4). Связка в памяти,
    /// как у часов 0-025 (<see cref="ClockTests"/>): <see cref="NetServer"/> + клиенты с предсказанием через имитатор связи;
    /// такт сервера — по расписанию 33,3 мс, в паузе цикл сервера крутится тем же шагом без такта (как <c>ServerRun</c>).
    /// </summary>
    public class PauseTests
    {
        private const double TickUs = 1_000_000.0 / Simulation.TickRate;
        private const long FrameUs = 1_000_000 / 60;
        private static readonly MapShape Map = MapShape.World(1024);
        private static readonly InputFrame East = new InputFrame(new Input8(1, 0), 0);
        private static readonly InputFrame North = new InputFrame(new Input8(0, 1), 0);

        private readonly ITestOutputHelper _out;

        public PauseTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private sealed class Client
        {
            public NetClient C = null!;
            public ITransport T = null!;
            public LocalPrediction? P;
            public long NextFrameAt;
            public uint Reconciled;
            public bool HasReconciled;
            public bool Gone;
        }

        private sealed class Rig : IDisposable
        {
            public readonly Simulation Sim;
            public readonly NetServer Server;
            public readonly List<Client> Clients = new List<Client>();
            public readonly LoopbackTransport St;
            private readonly NetSimSettings? _sim;
            private readonly Func<int, ITransport, ITransport>? _wrap;
            private double _nextTickAt;

            public long Now;

            /// <summary>Ввод клиента i в миг t.</summary>
            public Func<int, long, InputFrame> Script = (_, _) => default;

            /// <summary>После цикла сервера (такт или пауза).</summary>
            public Action? AfterCycle;

            public long PausedCycles;

            public Rig(int clients, NetSimSettings? s, Func<int, ITransport, ITransport>? wrap = null)
            {
                WorldModel world = FlatWorldGenerator.Instance.Generate(Map, 42);
                Sim = new Simulation(world, maxEntities: 8);
                St = LoopbackTransport.CreateServer(8);
                Server = new NetServer(St, Sim, i => ServerRun.SpawnPoint(Map, i), 8, NetModule.Version);
                _sim = s;
                _wrap = wrap;
                for (int i = 0; i < clients; i++)
                {
                    AddClient();
                }
            }

            public Client AddClient()
            {
                int i = Clients.Count;
                ITransport t = LoopbackTransport.ConnectTo(St);
                if (_sim.HasValue)
                {
                    NetSimSettings si = _sim.Value;
                    si.Seed += (ulong)i * 7919;
                    t = new NetSimTransport(t, si);
                }

                if (_wrap != null)
                {
                    t = _wrap(i, t);
                }

                var c = new Client { T = t, C = new NetClient(t, LoopbackTransport.ServerPeer, $"c{i}", NetModule.Version), NextFrameAt = Now + 3000 + i * 5000 };
                Clients.Add(c);
                return c;
            }

            public WorldPos PosOf(int i) => Sim.GetEntity(Clients[i].C.EntityId).Mover.Position;

            public void RunUntil(long untilUs)
            {
                for (; Now < untilUs; Now += 1000)
                {
                    StepServer();
                    for (int i = 0; i < Clients.Count; i++)
                    {
                        StepClient(i);
                    }
                }
            }

            private void StepServer()
            {
                if (Now < _nextTickAt)
                {
                    return;
                }

                _nextTickAt += TickUs;
                Server.Poll(Now);
                if (Server.Paused)
                {
                    Server.SendPausedSnapshots(); // ServerRun: такт стоит, сеть живёт
                    PausedCycles++;
                }
                else
                {
                    Sim.Step();
                    Server.SendSnapshots();
                }

                AfterCycle?.Invoke();
            }

            private void StepClient(int i)
            {
                Client c = Clients[i];
                if (c.Gone || Now < c.NextFrameAt)
                {
                    return;
                }

                c.NextFrameAt += FrameUs;
                c.C.Poll(Now);
                if (c.C.State != NetClientState.InGame)
                {
                    return;
                }

                c.P ??= new LocalPrediction(c.C.Shape, Sim.Solid, new WorldPos(Map.WidthUnits / 2, Map.HeightUnits / 2));
                if (c.C.HasSnapshot && (!c.HasReconciled || c.C.Latest.Tick != c.Reconciled))
                {
                    c.HasReconciled = true;
                    c.Reconciled = c.C.Latest.Tick;
                    c.P.Reconcile(c.C.Latest.AckFrame, c.C.Latest.Self, Now);
                }

                int n = c.C.FramesDue(Now);
                for (int k = 0; k < n; k++)
                {
                    InputFrame f = Script(i, Now);
                    c.C.SendFrame(f);
                    c.P.Sent(c.C.Frame, f);
                }
            }

            public void Leave(int i)
            {
                Clients[i].C.Disconnect();
                Clients[i].Gone = true;
            }

            public void Dispose()
            {
                foreach (Client c in Clients)
                {
                    c.T.Dispose();
                }

                St.Dispose();
            }
        }


        /// <summary>Снимок счётчиков, которые после паузы не должны расти.</summary>
        private readonly struct Counters
        {
            public readonly long Resynced, Renumbers, ClockSteps, ServerResets, Rebases, Skipped, Trimmed, SelfStepped, Stops, Late, Overflow;

            public Counters(Rig rig)
            {
                Resynced = Renumbers = ClockSteps = ServerResets = 0;
                foreach (Client c in rig.Clients)
                {
                    if (c.Gone)
                    {
                        continue;
                    }

                    Resynced += c.P?.Resynced ?? 0;
                    Renumbers += c.C.FrameRenumbers;
                    ClockSteps += c.C.Clock.StepsForward + c.C.Clock.StepsBack;
                    ServerResets += c.C.Clock.ServerResets + c.C.EpochResets;
                }

                NetServer s = rig.Server;
                Rebases = s.InputRebases;
                Skipped = s.FramesSkipped;
                Trimmed = s.FramesTrimmed;
                SelfStepped = s.FramesSelfStepped;
                Stops = s.InputStops;
                Late = s.FramesLate;
                Overflow = s.FramesOverflow;
            }

            public override string ToString() =>
                $"правок {Resynced}, перенумераций {Renumbers}, перестановок часов {ClockSteps}, по серверу {ServerResets}, перестроек очереди {Rebases}, " +
                $"перескок {Skipped}, выброшено {Trimmed}, сам {SelfStepped}, стопов {Stops}, опоздали {Late}, сверх кольца {Overflow}";
        }

        /// <summary>
        /// Приёмка 0-036 (CI, связь в памяти): 3 игрока бегут на восток, RTT 100 (с потерями 1 % — тоже); c0 ставит паузу на 10 с,
        /// снимает c2 (снять может любой). В паузе: такт мира стоит, позиции на сервере и у клиентов (снимок, предсказание) стоят,
        /// кадры ввода не уходят, часы клиентов стоят; отключений 0. После: ни правки предсказания, ни перенумерации кадров, ни
        /// перестановки часов, ни перестройки очереди, ни шага сервера «сам»; ввод «север», нажатый после, применён без
        /// задержки сверх обычной (≤ 10 тактов). Мутации: «часы клиента в паузе идут» и «сервер в паузе шагает кадры»
        /// краснят (см. статус-лог карточки).
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(1.0)]
        public void Pause10s_ThreePlayers_Rtt100_NoDisconnects_WorldStands_NoCorrectionsAfter(double lossPercent)
        {
            using var rig = new Rig(3, NetSimSettings.FromRtt(100, 0, lossPercent, 3));
            long pauseAt = 6_000_000, unpauseAt = 16_000_000, northAt = 17_000_000;
            rig.Script = (_, t) => t >= northAt ? North : East;
            rig.RunUntil(pauseAt);
            Assert.Equal(3, rig.Server.PlayersInGame);
            var before = new Counters(rig);
            rig.Clients[0].C.RequestPause(true);

            // Пауза дошла до всех (RTT 100 + потери): дальше мир стоит.
            rig.RunUntil(pauseAt + 1_000_000);
            Assert.True(rig.Server.Paused);
            long tick0 = rig.Sim.Tick;
            var pos0 = new WorldPos[3];
            var self0 = new WorldPos[3];
            var pred0 = new WorldPos[3];
            var inputs0 = new long[3];
            var est0 = new double[3];
            for (int i = 0; i < 3; i++)
            {
                NetClient c = rig.Clients[i].C;
                Assert.True(c.Paused, $"клиент {i} не узнал о паузе");
                Assert.Equal("c0", c.PausedBy);
                pos0[i] = rig.PosOf(i);
                self0[i] = c.Latest.Self.Position;
                pred0[i] = rig.Clients[i].P!.Position;
                inputs0[i] = c.InputsSent;
                est0[i] = c.Clock.Estimate(rig.Now);
            }

            int moved = 0;
            rig.AfterCycle = () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    if (rig.PosOf(i) != pos0[i] || rig.Clients[i].C.Latest.Self.Position != self0[i] || rig.Clients[i].P!.Position != pred0[i])
                    {
                        moved++;
                    }
                }
            };
            rig.RunUntil(unpauseAt);
            rig.AfterCycle = null;
            Assert.Equal(tick0, rig.Sim.Tick);
            Assert.Equal(0, moved);
            for (int i = 0; i < 3; i++)
            {
                NetClient c = rig.Clients[i].C;
                Assert.Equal(inputs0[i], c.InputsSent);
                Assert.Equal(est0[i], c.Clock.Estimate(rig.Now));
                Assert.Equal(NetClientState.InGame, c.State);
            }

            var atResume = new Counters(rig);
            rig.Clients[2].C.RequestPause(false);

            // Ввод «север» после паузы: применён сервером в обычный срок; всё время после — без правок.
            rig.RunUntil(northAt);
            long tickAtNorth = rig.Sim.Tick, northTick = -1;
            int y0 = rig.PosOf(0).Y;
            rig.AfterCycle = () =>
            {
                if (northTick < 0 && rig.PosOf(0).Y != y0)
                {
                    northTick = rig.Sim.Tick;
                }
            };
            rig.RunUntil(northAt + 5_000_000);
            var after = new Counters(rig);
            NetServer sv = rig.Server;
            _out.WriteLine($"потери {lossPercent} %: до паузы — {before}");
            _out.WriteLine($"  на снятии — {atResume}");
            _out.WriteLine($"  после 6 с — {after}");
            _out.WriteLine($"  сервер: пауз {sv.PausesSet}/{sv.PausesCleared}, циклов в паузе {rig.PausedCycles}, такт стоял на {tick0}, отключений {sv.Disconnects}; " +
                           $"«север» применён через {northTick - tickAtNorth} тактов");
            for (int i = 0; i < 3; i++)
            {
                NetClient c = rig.Clients[i].C;
                _out.WriteLine($"  клиент {i}: пауз {c.Pauses}, стоял {c.PausedTotalUs / 1e6:F2} с, кто снял «{c.PausedBy}», образцов в паузе {c.Clock.SamplesWhileHeld}, " +
                               $"снято по снимку {c.PausesReleasedBySnapshot}, устаревших {c.PausesStale}, L {c.LeadFrames}");
                Assert.Equal(NetClientState.InGame, c.State);
                Assert.False(c.Paused);
                Assert.Equal(1, c.Pauses);
                Assert.Equal("c2", c.PausedBy);
            }

            Assert.Equal(0, sv.Disconnects);
            Assert.Equal(3, sv.PlayersInGame);
            Assert.Equal(1, sv.PausesSet);
            Assert.Equal(1, sv.PausesCleared);
            Assert.InRange(rig.PausedCycles, 9 * Simulation.TickRate, 11 * Simulation.TickRate);
            Assert.Equal(atResume.Resynced, after.Resynced);
            Assert.Equal(atResume.Renumbers, after.Renumbers);
            Assert.Equal(atResume.ClockSteps, after.ClockSteps);
            Assert.Equal(atResume.ServerResets, after.ServerResets);
            Assert.Equal(atResume.Rebases, after.Rebases);
            Assert.Equal(atResume.SelfStepped, after.SelfStepped);
            Assert.Equal(atResume.Stops, after.Stops);
            Assert.Equal(0, after.ServerResets);
            if (lossPercent == 0)
            {
                Assert.Equal(before.Resynced, after.Resynced); // ровная связь — правок нет вовсе, и до паузы тоже
                Assert.Equal(atResume.Skipped, after.Skipped);
                Assert.Equal(atResume.Trimmed, after.Trimmed);
            }

            Assert.InRange(northTick - tickAtNorth, 0, 10);
        }

        /// <summary>
        /// Одиночная (один игрок): пауза и снятие тем же игроком — мир стоит, после — ни правок, ни перенумераций; повторная
        /// «пауза» в паузе ничего не меняет (запрос — желаемое состояние, а не «переключить»).
        /// </summary>
        [Fact]
        public void Pause_SinglePlayer_SetAndClearBySelf_RepeatIgnored()
        {
            using var rig = new Rig(1, null);
            rig.Script = (_, _) => East;
            rig.RunUntil(3_000_000);
            NetClient c = rig.Clients[0].C;
            c.RequestPause(true);
            rig.RunUntil(3_200_000);
            c.RequestPause(true);
            rig.RunUntil(3_400_000);
            long tick = rig.Sim.Tick;
            WorldPos pos = rig.PosOf(0);
            var atPause = new Counters(rig);
            rig.RunUntil(6_000_000);
            Assert.True(c.Paused);
            Assert.Equal(tick, rig.Sim.Tick);
            Assert.Equal(pos, rig.PosOf(0));
            Assert.Equal(1, rig.Server.PauseRequestsIgnored);
            c.RequestPause(false);
            rig.RunUntil(9_000_000);
            var after = new Counters(rig);
            _out.WriteLine($"одиночная: {after}; пауз {rig.Server.PausesSet}, повторных {rig.Server.PauseRequestsIgnored}");
            Assert.False(c.Paused);
            Assert.True(rig.Sim.Tick > tick + 2 * Simulation.TickRate);
            Assert.NotEqual(pos, rig.PosOf(0));
            Assert.Equal(0, after.Resynced);
            Assert.Equal(0, after.Renumbers);
            Assert.Equal(0, after.ClockSteps);
            Assert.Equal(atPause.SelfStepped, after.SelfStepped);
            Assert.Equal(0, after.Rebases);
        }

        /// <summary>
        /// Вошёл в паузу: «вход принят», затем «пауза, поставил c0»; снимок такта паузы приходит ему одному (мир и себя видно),
        /// остальным снимки не дублируются. После снятия — у всех без правок и перенумераций.
        /// </summary>
        [Fact]
        public void Pause_JoinDuringPause_SeesPauseAndWorld_NoCorrectionsAfter()
        {
            using var rig = new Rig(2, NetSimSettings.FromRtt(60, 0, 0, 9));
            rig.Script = (_, _) => East;
            rig.RunUntil(3_000_000);
            rig.Clients[0].C.RequestPause(true);
            rig.RunUntil(4_000_000);
            long snapsBefore = rig.Server.SnapshotsSent;
            Client late = rig.AddClient();
            rig.RunUntil(6_000_000);
            Assert.Equal(NetClientState.InGame, late.C.State);
            Assert.True(late.C.Paused);
            Assert.Equal("c0", late.C.PausedBy);
            Assert.True(late.C.HasSnapshot);
            Assert.True(late.C.Latest.Self.Present);
            Assert.Equal(1, rig.Server.PausedSnapshotsSent);
            Assert.Equal(snapsBefore + 1, rig.Server.SnapshotsSent);
            rig.Clients[1].C.RequestPause(false);
            rig.RunUntil(10_000_000);
            var after = new Counters(rig);
            _out.WriteLine($"вошедший в паузу: {after}; снимков в паузе {rig.Server.PausedSnapshotsSent}, у вошедшего снимков {late.C.SnapshotsApplied}");
            Assert.False(late.C.Paused);
            Assert.True(late.C.SnapshotsApplied > 3 * Simulation.TickRate);
            Assert.Equal(0, after.Resynced);
            Assert.Equal(0, after.Renumbers);
            Assert.Equal(0, after.ClockSteps);
            Assert.Equal(0, after.Rebases);
        }

        /// <summary>
        /// Пустой сервер (`07` 2.3 — выделенный; допущение 0-036): ушёл поставивший — пауза остаётся (другой видит, кто
        /// поставил); ушли все — пауза снимается сама, сервер шагает дальше; вошедший потом паузы не застаёт.
        /// </summary>
        [Fact]
        public void Pause_SetterLeaves_Stays_AllLeave_Cleared_NextJoinRuns()
        {
            using var rig = new Rig(2, null);
            rig.Script = (_, _) => East;
            rig.RunUntil(2_000_000);
            rig.Clients[0].C.RequestPause(true);
            rig.RunUntil(2_500_000);
            rig.Leave(0);
            rig.RunUntil(3_500_000);
            Assert.True(rig.Server.Paused);
            Assert.Equal(1, rig.Server.PlayersInGame);
            Assert.True(rig.Clients[1].C.Paused);
            Assert.Equal("c0", rig.Clients[1].C.PausedBy);
            long tick = rig.Sim.Tick;
            rig.Leave(1);
            rig.RunUntil(4_500_000);
            Assert.Equal(0, rig.Server.PlayersInGame);
            Assert.False(rig.Server.Paused);
            Assert.Equal(1, rig.Server.PausesClearedEmpty);
            Assert.True(rig.Sim.Tick > tick);
            Client next = rig.AddClient();
            rig.RunUntil(7_000_000);
            _out.WriteLine($"пустой: такт {tick} → {rig.Sim.Tick}, снято пустым {rig.Server.PausesClearedEmpty}, вошедший в паузе: {next.C.Paused}");
            Assert.Equal(NetClientState.InGame, next.C.State);
            Assert.False(next.C.Paused);
            Assert.Equal(0, next.C.Pauses);
            Assert.True(next.C.SnapshotsApplied > Simulation.TickRate);
        }

        /// <summary>Транспорт клиента, который задерживает «пауза / продолжить» (канал 2): переотправка после потери.</summary>
        private sealed class DelayPauseTransport : ITransport, ITransportHandler
        {
            private readonly ITransport _inner;
            private readonly long _delayUs;
            private readonly bool _onlyResume;
            private readonly List<(long At, int Peer, byte[] Data)> _held = new List<(long, int, byte[])>();
            private ITransportHandler _handler = null!;
            private long _now;

            public DelayPauseTransport(ITransport inner, long delayUs, bool onlyResume)
            {
                _inner = inner;
                _delayUs = delayUs;
                _onlyResume = onlyResume;
            }

            public int Held { get; private set; }

            public void Poll(long nowMicros, ITransportHandler handler)
            {
                _handler = handler;
                _now = nowMicros;
                _inner.Poll(nowMicros, this);
                for (int k = 0; k < _held.Count; k++)
                {
                    if (_held[k].At <= nowMicros)
                    {
                        handler.OnReceive(_held[k].Peer, NetChannel.Facts, _held[k].Data);
                        _held.RemoveAt(k--);
                    }
                }
            }

            public void OnConnected(int peer) => _handler.OnConnected(peer);

            public void OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload) => _handler.OnDisconnected(peer, reason, payload);

            public void OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
                if (Codec.TypeOf(data) == MessageType.PauseState && (!_onlyResume || data[1] == 0))
                {
                    Held++;
                    _held.Add((_now + _delayUs, peer, data.ToArray()));
                    return;
                }

                _handler.OnReceive(peer, channel, data);
            }

            public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data) => _inner.Send(peer, channel, data);

            public void Flush() => _inner.Flush();

            public void Disconnect(int peer, ReadOnlySpan<byte> payload) => _inner.Disconnect(peer, payload);

            public int RoundTripMs(int peer) => _inner.RoundTripMs(peer);

            public void Dispose() => _inner.Dispose();
        }

        /// <summary>
        /// «Продолжить» задержано переотправкой на 300 мс: клиент снимает паузу по первому снимку новее такта паузы — без правок и
        /// перенумераций; опоздавшее сообщение — ничего. Короткая пауза (0,2 с), у которой опоздали оба сообщения («пауза» пришла
        /// после снимков новее её такта): часы встают по ней и стоят до «продолжить» — столько же, сколько стоял сервер; без
        /// перенумераций и перестановок часов (не встань они — часы ушли бы вперёд на паузу: перестановка назад и перенумерация);
        /// правка — не больше одной (стоп по молчанию, пока часы стоят, а сервер уже шагает).
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Pause_DelayedMessages_ReleaseBySnapshot_StaleIgnored(bool onlyResume)
        {
            using var rig = new Rig(1, NetSimSettings.FromRtt(80, 0, 0, 5), (_, t) => new DelayPauseTransport(t, 300_000, onlyResume));
            rig.Script = (_, _) => East;
            rig.RunUntil(3_000_000);
            NetClient c = rig.Clients[0].C;
            rig.Server.SetPaused(true);
            rig.RunUntil(onlyResume ? 5_000_000 : 3_200_000);
            Assert.Equal(onlyResume, c.Paused);
            rig.Server.SetPaused(false);
            rig.RunUntil(8_000_000);
            var after = new Counters(rig);
            _out.WriteLine($"задержка {(onlyResume ? "«продолжить»" : "обоих")}: {after}; пауз {c.Pauses}, снято по снимку {c.PausesReleasedBySnapshot}, устаревших {c.PausesStale}");
            Assert.False(c.Paused);
            Assert.Equal(NetClientState.InGame, c.State);
            Assert.Equal(0, after.Renumbers);
            Assert.Equal(0, after.ClockSteps);

            // Опоздали оба: сервер уже шагает, а часы клиента стоят 0,2 с — пакетов ввода нет 6 тактов, сервер ставит стоп (2.13 п. 6)
            // и выбрасывает очередь — одна правка. Цена редкого случая (пауза короче переотправки); без остановки часов — перестановка
            // назад, перенумерация и две правки.
            Assert.InRange(after.Resynced, 0, onlyResume ? 0 : 1);
            if (onlyResume)
            {
                Assert.Equal(1, c.Pauses);
                Assert.Equal(1, c.PausesReleasedBySnapshot);
            }
            else
            {
                Assert.Equal(1, c.Pauses); // опоздавшая «пауза»: часы всё равно встали — до «продолжить»
                Assert.Equal(1, c.PausesStale);
                Assert.Equal(0, c.PausesReleasedBySnapshot);
            }
        }

        /// <summary>Часы клиента: остановка и пуск — оценка непрерывна, образцы в паузе не двигают часы, окно сдвинуто на паузу.</summary>
        [Fact]
        public void ClientClock_HoldRelease_Continuous_SamplesWhileHeldIgnored()
        {
            var clock = new ClientClock();
            clock.Start(100, 0);
            long now = 0;
            uint tick = 100;
            for (; now < 1_000_000; now += 33_333)
            {
                clock.OnSample(tick++, now);
            }

            double atHold = clock.Estimate(now);
            clock.Hold(now);
            for (int k = 0; k < 90; k++)
            {
                clock.OnSample(tick, now + k * 33_333); // сервер стоит на такте паузы
            }

            long resume = now + 5_000_000;
            Assert.Equal(atHold, clock.Estimate(resume));
            Assert.Equal(5_000_000, clock.Release(resume));
            Assert.Equal(atHold, clock.Estimate(resume), 6);
            for (now = resume; now < resume + 1_000_000; now += 33_333)
            {
                clock.OnSample(tick++, now);
            }

            _out.WriteLine($"часы: на остановке {atHold:F3}, через 1 с после пуска ошибка окна {clock.WindowErrorTicks:F3} такта, образцов в паузе {clock.SamplesWhileHeld}");
            Assert.Equal(90, clock.SamplesWhileHeld);
            Assert.Equal(0, clock.StepsForward + clock.StepsBack);
            Assert.InRange(clock.WindowErrorTicks, -0.5, 0.5);
        }

        /// <summary>
        /// Время показа чужих: в паузе стоит (кадры не считаются ни «тем же временем», ни «стоит на новейшем»), после — идёт
        /// дальше без перестановки и догона.
        /// </summary>
        [Fact]
        public void RemoteInterpolation_Pause_RenderStands_NoResetNoCatchUpAfter()
        {
            var r = new RemoteInterpolation(Map);
            var snap = new Snapshot { Count = 0 };
            long now = 0;
            uint tick = 1;
            for (; now < 3_000_000; now += 33_333)
            {
                snap.Tick = tick++;
                r.OnSnapshot(snap, now + 50_000);
                r.BeginFrame(now + 50_000);
            }

            long render = r.RenderTimeUs;
            long same = r.SameTimeFrames, hold = r.HoldFrames;
            r.OnPause(true, now);
            for (int k = 0; k < 300; k++)
            {
                Assert.True(r.BeginFrame(now + k * 33_333));
            }

            Assert.Equal(render, r.RenderTimeUs);
            now += 10_000_000;
            r.OnPause(false, now);
            long catchUp = r.CatchUpSteps;
            for (int k = 0; k < 60; k++, now += 33_333)
            {
                snap.Tick = tick++;
                r.OnSnapshot(snap, now + 50_000);
                r.BeginFrame(now + 50_000);
            }

            _out.WriteLine($"показ: кадров в паузе {r.PausedFrames}, перестановок {r.ClockResets}, догона {r.CatchUpSteps - catchUp}, " +
                           $"тех же {r.SameTimeFrames - same}, на новейшем {r.HoldFrames - hold}, запас {r.BufferUs / 1000} мс");
            Assert.Equal(300, r.PausedFrames);
            Assert.Equal(0, r.ClockResets);
            Assert.Equal(same, r.SameTimeFrames);
            Assert.Equal(hold, r.HoldFrames);
            Assert.InRange(r.BufferUs, 50_000, 150_000);
        }
    }

    /// <summary>Пауза по UDP (0-036): сервер в процессе теста (цикл <c>ServerRun</c> с паузой), боты с предсказанием.</summary>
    [Collection(ClockUdpCollection.Name)]
    public class PauseUdpTests
    {
        private readonly ITestOutputHelper _out;

        public PauseUdpTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private BotReport Run(int count, int rtt, double loss, double seconds, double pauseAt, double pauseFor, out ServerResult result)
        {
            int port = FreeUdpPort();
            ServerOptions so = ServerOptions.Parse(new[] { "--create", "--seed", "42", "--size", "1024", "--port", port.ToString() });
            using var stopServer = new CancellationTokenSource();
            Task<ServerResult> server = Task.Run(() => ServerRun.Run(so, null, stopServer.Token));
            var args = new List<string>
            {
                "--connect", $"127.0.0.1:{port}", "--count", count.ToString(), "--script", "wander", "--seconds", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--seed", "7", "--pause-at", pauseAt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--pause-for", pauseFor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (rtt > 0)
            {
                args.AddRange(new[] { "--rtt", rtt.ToString(), "--loss", loss.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }

            BotReport r;
            try
            {
                r = BotRun.Run(BotOptions.Parse(args.ToArray()), null);
            }
            finally
            {
                Thread.Sleep(300);
                stopServer.Cancel();
            }

            result = server.GetAwaiter().GetResult();
            Assert.Equal(0, result.ExitCode);
            foreach (BotStats b in r.Bots)
            {
                _out.WriteLine($"бот {b.Index}: {b.State}, {b.SecondsInGame:F1} с, пауз {b.Pauses}, стоял {b.PausedSeconds:F2} с, «{b.PausedBy}», сдвигов в паузе {b.PauseMovedFrames}, " +
                               $"кадров в паузе {b.PauseInputsSent}, правок всего {b.PredResynced}, после паузы {b.PredResyncedAfterPause}; перенумераций {b.FrameRenumbers}, " +
                               $"перестановок часов {b.ClockStepsForward}/{b.ClockStepsBack}/{b.ClockServerResets}, образцов в паузе {b.ClockSamplesWhileHeld}, " +
                               $"снято по снимку {b.PausesReleasedBySnapshot}, сверок {b.PredCompared}, > 0,5 т {b.PredOverHalfTile}");
            }

            _out.WriteLine($"в игре {r.InGame}, отключений {r.Disconnects}, ≤ 0,5 тайла — {r.PredWithinHalfTile:P3}; сервер: тактов {result.Ticks} за {result.Seconds:F1} с");
            return r;
        }

        /// <summary>
        /// CI: цикл <c>ServerRun</c> в паузе — 1 бот (одиночная), пауза 1,5 с: бот стоял, такт сервера за прогон — меньше
        /// времени на длину паузы, 0 правок после, 0 отключений.
        /// </summary>
        [Fact]
        public void Udp_ServerRun_OneBot_Pause15s_TicksStand_NoCorrectionsAfter()
        {
            BotReport r = Run(1, 0, 0, 5, 1.5, 1.5, out ServerResult res);
            BotStats b = r.Bots[0];
            Assert.Equal(1, r.InGame);
            Assert.Equal(0, r.Disconnects);
            Assert.Equal(1, b.Pauses);
            Assert.InRange(b.PausedSeconds, 1.3, 1.9);
            Assert.Equal(0, b.PauseMovedFrames);
            Assert.Equal(0, b.PauseInputsSent);
            Assert.Equal(0, b.PredResyncedAfterPause);
            Assert.Equal(0, b.FrameRenumbers);
            Assert.True(res.Ticks < (res.Seconds - 1.2) * Simulation.TickRate, $"тактов {res.Ticks} за {res.Seconds:F1} с — пауза не остановила такт");
        }

        /// <summary>
        /// Приёмка 0-036 по UDP (`-All`): 3 бота с предсказанием, RTT 100, потери 1 %, бот 0 ставит паузу на 10 с, бот 2 снимает —
        /// отключений 0, в паузе никто не сдвинулся и не слал кадров, после — 0 правок, 0 перенумераций, 0 перестановок часов.
        /// </summary>
        [Fact]
        [Trait("Category", "Big")]
        public void Udp_Big_ThreeBots_Rtt100_Loss1_Pause10s_NoDisconnects_NoCorrectionsAfter()
        {
            BotReport r = Run(3, 100, 1, 24, 6, 10, out _);
            Assert.Equal(3, r.InGame);
            Assert.Equal(0, r.Disconnects);
            foreach (BotStats b in r.Bots)
            {
                Assert.Equal(1, b.Pauses);
                Assert.Equal("bot2", b.PausedBy);
                Assert.InRange(b.PausedSeconds, 9.5, 10.6);
                Assert.Equal(0, b.PauseMovedFrames);
                Assert.Equal(0, b.PauseInputsSent);
                Assert.Equal(0, b.PredResyncedAfterPause);
                Assert.Equal(0, b.FrameRenumbers);
                Assert.Equal(0, b.ClockStepsForward + b.ClockStepsBack + b.ClockServerResets);
            }

            Assert.True(r.PredWithinHalfTile >= 0.999, $"{r.PredWithinHalfTile:P3}");
        }

        private static int FreeUdpPort()
        {
            using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            s.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            return ((System.Net.IPEndPoint)s.LocalEndPoint!).Port;
        }
    }
}
