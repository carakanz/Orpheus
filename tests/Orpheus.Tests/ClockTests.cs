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
    /// Часы клиента и «часы после фриза» (docs/07, 2.4; приёмка 2.13 п. 1, 5; карточка 0-025). Связка целиком в памяти:
    /// <see cref="NetServer"/> + <see cref="NetClient"/> с предсказанием через имитатор связи; такт сервера — по своему
    /// расписанию (33,3 мс), заморозка — сервер не разбирает пакеты и не шагает, после неё такты не догоняются
    /// (как <see cref="TickClock"/>) и рассылаются «часы». Правда для ошибки часов — кадр сервера в тот же миг:
    /// последний шагнутый такт + доля такта с его начала.
    /// </summary>
    public class ClockTests
    {
        private const double TickUs = 1_000_000.0 / Simulation.TickRate;
        private const long FrameUs = 1_000_000 / 60;
        private static readonly MapShape Map = MapShape.World(1024);

        private readonly ITestOutputHelper _out;

        public ClockTests(ITestOutputHelper output)
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
        }

        /// <summary>Сервер и клиенты в памяти; время — 1 мс шагом.</summary>
        private sealed class Rig : IDisposable
        {
            public readonly Simulation Sim;
            public readonly NetServer Server;
            public readonly List<Client> Clients = new List<Client>();
            public readonly LoopbackTransport St;
            private double _nextTickAt;
            private long _lastTickAt;
            private long _frozenUntil = -1;

            public long Now;

            /// <summary>Ввод клиента i в миг t.</summary>
            public Func<int, long, InputFrame> Script = (_, _) => default;

            /// <summary>Снимок применён клиентом i (после сверки предсказания — та идёт в кадре клиента).</summary>
            public Action<int, Snapshot>? OnSnapshot;

            /// <summary>После такта сервера.</summary>
            public Action? AfterTick;

            public Rig(int clients, NetSimSettings? s, Func<int, ITransport, ITransport>? wrap = null)
            {
                WorldModel world = FlatWorldGenerator.Instance.Generate(Map, 42);
                Sim = new Simulation(world, maxEntities: 8);
                St = LoopbackTransport.CreateServer(8);
                Server = new NetServer(St, Sim, i => ServerRun.SpawnPoint(Map, i), 8, NetModule.Version);
                for (int i = 0; i < clients; i++)
                {
                    ITransport t = LoopbackTransport.ConnectTo(St);
                    if (s.HasValue)
                    {
                        NetSimSettings si = s.Value;
                        si.Seed += (ulong)i * 7919;
                        t = new NetSimTransport(t, si);
                    }

                    if (wrap != null)
                    {
                        t = wrap(i, t);
                    }

                    var c = new Client { T = t, C = new NetClient(t, LoopbackTransport.ServerPeer, $"c{i}", NetModule.Version), NextFrameAt = 3000 + i * 5000 };
                    int index = i;
                    c.C.SnapshotApplied = snap => OnSnapshot?.Invoke(index, snap);
                    Clients.Add(c);
                }
            }

            public bool Frozen => _frozenUntil >= 0;

            /// <summary>Кадр сервера «сейчас» (такты, дробно): последний шагнутый + доля такта; в заморозке — стоит.</summary>
            public double TruthTicks => Sim.Tick + (Frozen ? 0 : (Now - _lastTickAt) / TickUs);

            /// <summary>Заморозить сервер на <paramref name="ms"/> мс с этого мига (2.13 п. 5).</summary>
            public void Freeze(int ms) => _frozenUntil = Now + ms * 1000L;

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
                if (Frozen)
                {
                    if (Now < _frozenUntil)
                    {
                        return;
                    }

                    // Разморозка: отставание > 3 тактов не догоняется (TickClock), «часы» — до разбора пакетов (ServerRun).
                    _frozenUntil = -1;
                    _nextTickAt = Now;
                    Server.BroadcastClock();
                }

                if (Now < _nextTickAt)
                {
                    return;
                }

                Server.Poll(Now);
                Sim.Step();
                Server.SendSnapshots();
                _lastTickAt = Now;
                _nextTickAt += TickUs;
                AfterTick?.Invoke();
            }

            private void StepClient(int i)
            {
                Client c = Clients[i];
                if (Now < c.NextFrameAt)
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

            public void Dispose()
            {
                foreach (Client c in Clients)
                {
                    c.T.Dispose();
                }

                St.Dispose();
            }
        }

        private static readonly InputFrame East = new InputFrame(new Input8(1, 0), 0);
        private static readonly InputFrame North = new InputFrame(new Input8(0, 1), 0);

        /// <summary>
        /// 2.4 «Часы клиента»: ровная связь RTT 100 — ошибка оценки кадра сервера ≤ 1 кадра ввода (16,7 мс) в ≥ 99 % снимков,
        /// выбросов в очереди сервера нет (0-019 с новыми часами). Скачок часов сервера на +300 мс (9 тактов разом) —
        /// перестановка сразу: уже на первом снимке после скачка ошибка ≤ 1 кадра. Мутации «перестановка при ошибке
        /// &gt; 1 с» и «часы только по пингу» краснят вторую часть.
        /// </summary>
        [Fact]
        public void D07_S2_4_ClientClock_SteadyRtt100_ErrorWithin1Frame_Jump300msResetsAtOnce()
        {
            using var rig = new Rig(1, NetSimSettings.FromRtt(100, 0, 0, 11));
            rig.Script = (_, _) => East;
            int measured = 0, within = 0, first = -1;
            double worst = 0, firstErr = 0;
            bool jumped = false;
            long jumpTick = long.MaxValue;
            rig.OnSnapshot = (_, s) =>
            {
                double err = (rig.Clients[0].C.Clock.Estimate(rig.Now) - rig.TruthTicks) * 2; // в кадрах ввода
                if (jumped && first < 0 && s.Tick > jumpTick) // снимки, бывшие в пути до скачка, — не в счёт
                {
                    first = 1;
                    firstErr = err;
                }

                if (rig.Now < 1_000_000 || jumped)
                {
                    return; // первая секунда — установка owd по первому пингу
                }

                measured++;
                worst = Math.Max(worst, Math.Abs(err));
                if (Math.Abs(err) <= 1.0)
                {
                    within++;
                }
            };

            rig.RunUntil(20_000_000);
            NetServer sv = rig.Server;
            NetClient c = rig.Clients[0].C;
            _out.WriteLine($"ровно 19 с: снимков {measured}, ошибка ≤ 1 кадра — {within}, наибольшая {worst:F2} кадра; owd {c.Clock.OwdUs} мкс, " +
                           $"пингов {c.PongsReceived}; очередь: перескок {sv.FramesSkipped}, выброшено {sv.FramesTrimmed}, сам {sv.FramesSelfStepped}, " +
                           $"опоздали {sv.FramesLate}; перестановок {c.Clock.StepsForward}/{c.Clock.StepsBack}");
            Assert.True(measured > 500, $"{measured}");
            Assert.True(within >= measured * 0.99, $"≤ 1 кадра у {within} из {measured}");
            Assert.Equal(0, sv.FramesSkipped);
            Assert.Equal(0, sv.FramesTrimmed);
            Assert.Equal(0, sv.FramesSelfStepped);
            Assert.Equal(0, c.Clock.StepsForward + c.Clock.StepsBack);

            // Скачок: сервер прошёл 9 тактов разом (+300 мс) — его кадр впереди оценки клиента.
            for (int k = 0; k < 9; k++)
            {
                rig.Sim.Step();
            }

            jumped = true;
            jumpTick = rig.Sim.Tick;
            rig.RunUntil(22_000_000);
            _out.WriteLine($"скачок +300 мс: ошибка на первом снимке после — {firstErr:F2} кадра, перестановок вперёд {c.Clock.StepsForward}, " +
                           $"перенумераций {c.FrameRenumbers}, правок предсказания {rig.Clients[0].P!.Resynced}");
            Assert.True(Math.Abs(firstErr) <= 1.0, $"первый снимок после скачка: ошибка {firstErr:F2} кадра");
            Assert.Equal(1, c.Clock.StepsForward);
        }

        /// <summary>
        /// 2.13 п. 5 (CI, связь в памяти, RTT 100): сервер заморожен на 2 с посреди движения — отключений 0; первый кадр,
        /// нажатый после разморозки, применён сервером не позже 30 тактов (1 с) после неё; клиент получил «часы» и уже на
        /// первом снимке после разморозки ошибка его оценки ≤ 1 кадра. Мутация «без часов после фриза» краснит последнее.
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(1.0)]
        public void D07_S2_13_5_ServerFrozen2s_NoDisconnects_InputWithin1s_ClockResetFirstSnapshot(double lossPercent)
        {
            using var rig = new Rig(2, NetSimSettings.FromRtt(100, 0, lossPercent, 5));
            long unfreezeAt = 12_000_000;
            rig.Script = (_, t) => t >= unfreezeAt ? North : East;
            var firstErr = new double[2];
            var seenAfter = new bool[2];
            rig.OnSnapshot = (i, s) =>
            {
                if (!rig.Frozen && rig.Now >= unfreezeAt && !seenAfter[i])
                {
                    seenAfter[i] = true;
                    firstErr[i] = (rig.Clients[i].C.Clock.Estimate(rig.Now) - rig.TruthTicks) * 2;
                }
            };

            rig.RunUntil(10_000_000);
            long skippedBefore = rig.Server.FramesSkipped, trimmedBefore = rig.Server.FramesTrimmed;
            var y0 = new int[2];
            var movedNorthAt = new long[] { -1, -1 };
            long tickAtUnfreeze = -1;

            // Перестройка очереди (з. 1 проверки): 1 с после первого кадра «север» — Y меняется каждый такт, сервер не шагает
            // сам и не останавливает ввод. Без перестройки пакеты с номерами «назад» отбрасываются: сервер шагает сам и встаёт.
            var prevY = new int[2];
            var stillTicks = new int[2];
            var watched = new int[2];
            long selfAt = -1, stopsAt = -1, selfAfter = -1, stopsAfter = -1;
            rig.AfterTick = () =>
            {
                if (rig.Now < unfreezeAt)
                {
                    return;
                }

                if (tickAtUnfreeze < 0)
                {
                    tickAtUnfreeze = rig.Sim.Tick;
                    for (int i = 0; i < 2; i++)
                    {
                        y0[i] = rig.Sim.GetEntity(rig.Clients[i].C.EntityId).Mover.Position.Y;
                    }
                }

                for (int i = 0; i < 2; i++)
                {
                    int y = rig.Sim.GetEntity(rig.Clients[i].C.EntityId).Mover.Position.Y;
                    if (movedNorthAt[i] >= 0 && watched[i] < Simulation.TickRate)
                    {
                        watched[i]++;
                        if (y == prevY[i])
                        {
                            stillTicks[i]++;
                        }
                    }

                    if (movedNorthAt[i] < 0 && y != y0[i])
                    {
                        movedNorthAt[i] = rig.Sim.Tick - tickAtUnfreeze;
                    }

                    prevY[i] = y;
                }

                if (selfAt < 0 && movedNorthAt[0] >= 0 && movedNorthAt[1] >= 0)
                {
                    selfAt = rig.Server.FramesSelfStepped;
                    stopsAt = rig.Server.InputStops;
                }
                else if (selfAfter < 0 && watched[0] == Simulation.TickRate && watched[1] == Simulation.TickRate)
                {
                    selfAfter = rig.Server.FramesSelfStepped;
                    stopsAfter = rig.Server.InputStops;
                }
            };

            rig.Freeze(2000);
            rig.RunUntil(unfreezeAt + 3_000_000);
            NetServer sv = rig.Server;
            for (int i = 0; i < 2; i++)
            {
                NetClient c = rig.Clients[i].C;
                LocalPrediction p = rig.Clients[i].P!;
                _out.WriteLine($"клиент {i}: состояние {c.State}, «часы» {c.ClocksReceived}, эпох {c.EpochResets}, перестановок по серверу {c.Clock.ServerResets}, " +
                               $"ошибка на первом снимке после разморозки {firstErr[i]:F2} кадра, ввод «север» применён через {movedNorthAt[i]} тактов, " +
                               $"тактов без движения за 1 с после — {stillTicks[i]} из {watched[i]}, перенумераций {c.FrameRenumbers}; предсказание: правок {p.Resynced}, пропущено ack {p.IgnoredAcks}, наиб. {p.MaxDevUnits} u");
                Assert.Equal(NetClientState.InGame, c.State);
                Assert.True(c.ClocksReceived >= 1);
                Assert.True(seenAfter[i]);
                Assert.True(Math.Abs(firstErr[i]) <= 1.0, $"клиент {i}: ошибка часов на первом снимке после разморозки {firstErr[i]:F2} кадра");
                Assert.InRange(movedNorthAt[i], 0, 30);
                Assert.Equal(Simulation.TickRate, watched[i]);
                Assert.Equal(0, stillTicks[i]);
            }

            _out.WriteLine($"сервер: отключений {sv.Disconnects}, перестроек очереди {sv.InputRebases}, до заморозки перескок {skippedBefore}, " +
                           $"выброшено {trimmedBefore}; эпоха {sv.ClockEpoch}; за 1 с после «севера» сам {selfAfter - selfAt}, стопов {stopsAfter - stopsAt}; " +
                           $"пакетов отброшено {sv.InputsDropped}");
            Assert.Equal(0, sv.Disconnects);
            Assert.Equal(2, sv.PlayersInGame);
            Assert.InRange(sv.InputRebases, 2, 6); // по одной на клиента (+ дубли при потерях); без перестройки — сотни
            Assert.True(selfAt >= 0 && selfAfter >= 0);
            Assert.Equal(0, selfAfter - selfAt);
            Assert.Equal(0, stopsAfter - stopsAt);
            Assert.Equal(1, sv.ClockEpoch);
            if (lossPercent == 0)
            {
                Assert.Equal(0, skippedBefore); // ровная связь — выбросов очереди нет
                Assert.Equal(0, trimmedBefore);
            }
        }

        /// <summary>
        /// Транспорт клиента, который задерживает «часы» (канал 2): переотправка после потери — снимок с новой эпохой
        /// приходит раньше них.
        /// </summary>
        private sealed class HoldClockTransport : ITransport, ITransportHandler
        {
            private readonly ITransport _inner;
            private readonly long _delayUs;
            private readonly List<(long At, int Peer, byte[] Data)> _held = new List<(long, int, byte[])>();
            private ITransportHandler _handler = null!;
            private long _now;

            public HoldClockTransport(ITransport inner, long delayUs)
            {
                _inner = inner;
                _delayUs = delayUs;
            }

            public int Held { get; private set; }

            public int Released { get; private set; }

            public void Poll(long nowMicros, ITransportHandler handler)
            {
                _handler = handler;
                _now = nowMicros;
                _inner.Poll(nowMicros, this);
                for (int k = 0; k < _held.Count; k++)
                {
                    if (_held[k].At <= nowMicros)
                    {
                        Released++;
                        handler.OnReceive(_held[k].Peer, NetChannel.Facts, _held[k].Data);
                        _held.RemoveAt(k--);
                    }
                }
            }

            public void OnConnected(int peer) => _handler.OnConnected(peer);

            public void OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload) => _handler.OnDisconnected(peer, reason, payload);

            public void OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
                if (Codec.TypeOf(data) == MessageType.Clock)
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
        /// Опоздавшие «часы» (проверка 0-025 з. 3): «часы» после разморозки потерялись и пришли переотправкой на 200 мс
        /// позже снимка с новой эпохой — клиент их не применяет: часы не уходят назад, перенумерация одна (по эпохе), без
        /// перестановки вперёд следом. Мутация «часы — без условий» краснит.
        /// </summary>
        [Fact]
        public void D07_S2_4_ClientClock_LateClockAfterNewEpochSnapshot_Ignored()
        {
            HoldClockTransport? hold = null;
            using var rig = new Rig(1, NetSimSettings.FromRtt(100, 0, 0, 5), (_, t) => hold = new HoldClockTransport(t, 200_000));
            rig.Script = (_, _) => East;
            long releasedAt = -1;
            double worstAfter = 0;
            int afterRelease = 0;
            rig.OnSnapshot = (_, _) =>
            {
                if (releasedAt >= 0)
                {
                    afterRelease++;
                    worstAfter = Math.Max(worstAfter, Math.Abs(rig.Clients[0].C.Clock.Estimate(rig.Now) - rig.TruthTicks) * 2);
                }
            };

            rig.RunUntil(5_000_000);
            rig.Freeze(2000);
            rig.RunUntil(7_000_000);
            while (hold!.Released == 0 && rig.Now < 8_000_000)
            {
                rig.RunUntil(rig.Now + 1000);
            }

            NetClient c = rig.Clients[0].C;
            long epochResets = c.EpochResets;
            double errAtRelease = (c.Clock.Estimate(rig.Now) - rig.TruthTicks) * 2;
            releasedAt = rig.Now;
            rig.RunUntil(rig.Now + 2_000_000);
            _out.WriteLine($"«часы» задержаны {hold.Held}, отданы {hold.Released}; эпох {epochResets} → {c.EpochResets}, «часов» принято {c.ClocksReceived}, " +
                           $"пропущено {c.ClocksStale}; ошибка в миг опоздавших «часов» {errAtRelease:F2} кадра, после — наиб. {worstAfter:F2} за {afterRelease} снимков; " +
                           $"перестановок по серверу {c.Clock.ServerResets}, вперёд {c.Clock.StepsForward}, назад {c.Clock.StepsBack}, перенумераций {c.FrameRenumbers}");
            Assert.Equal(1, hold.Released);
            Assert.Equal(1, epochResets); // часы переставлены по снимку с новой эпохой — до «часов»
            Assert.True(Math.Abs(errAtRelease) <= 1.0, $"опоздавшие «часы» сдвинули оценку: ошибка {errAtRelease:F2} кадра");
            Assert.Equal(1, c.ClocksStale);
            Assert.Equal(1, c.FrameRenumbers);
            Assert.Equal(0, c.Clock.StepsForward);
            Assert.Equal(0, c.Clock.StepsBack);
            Assert.True(afterRelease > 30, $"{afterRelease}");
            Assert.True(worstAfter <= 1.0, $"ошибка после опоздавших «часов» {worstAfter:F2} кадра");
        }

        /// <summary>
        /// Один опоздавший снимок (проверка 0-025 з. 5): ровная связь RTT 100, снимок на 150 / 400 / 1000 мс позже — часы назад
        /// не переставляются (назад — только когда отстало всё окно из 15), ошибка следующих снимков ≤ 1 кадра. Мутация
        /// «назад — по одному образцу» краснит.
        /// </summary>
        [Theory]
        [InlineData(150)]
        [InlineData(400)]
        [InlineData(1000)]
        public void D07_S2_4_ClientClock_OneLateSnapshot_NoStepBack(int lateMs)
        {
            // Снимки 30 Гц и ответы на пинг раз в 1 с, owd 50 мс; правда — кадр сервера t / такт. Снимок 200 опоздал, а
            // шедшие за ним до его прихода потерялись — он новейший и попадает в часы (старее принятого клиент отбросил бы).
            const long owdUs = 50_000;
            var events = new List<(long At, bool Pong, uint Tick)>();
            int lostUntil = 200 + (int)Math.Ceiling(lateMs * 1000.0 / ClientClock.TickUs);
            for (uint tick = 1; tick <= 400; tick++)
            {
                long sentUs = (long)(tick * ClientClock.TickUs);
                if (tick % 30 == 0)
                {
                    events.Add((sentUs + owdUs, true, tick));
                }

                if (tick > 200 && tick <= lostUntil)
                {
                    continue;
                }

                events.Add((sentUs + owdUs + (tick == 200 ? lateMs * 1000L : 0), false, tick));
            }

            events.Sort((a, b) => a.At.CompareTo(b.At));
            var clock = new ClientClock();
            clock.SeedRtt(2 * owdUs);
            clock.Start(0, owdUs);
            uint last = 0;
            double worst = 0;
            bool lateSeen = false;
            foreach ((long at, bool pong, uint tick) in events)
            {
                if (pong)
                {
                    clock.OnPong(tick, 2 * owdUs, at);
                }
                else if (tick > last)
                {
                    last = tick;
                    clock.OnSample(tick, at);
                    if (lateSeen)
                    {
                        worst = Math.Max(worst, Math.Abs(clock.Estimate(at) - at / ClientClock.TickUs) * 2);
                    }

                    lateSeen |= tick == 200;
                }
            }

            _out.WriteLine($"снимок опоздал на {lateMs} мс (потеряно за ним {lostUntil - 200}): перестановок вперёд {clock.StepsForward}, назад {clock.StepsBack}, " +
                           $"наиб. ошибка снимков после — {worst:F2} кадра");
            Assert.True(lateSeen);
            Assert.Equal(0, clock.StepsBack);
            Assert.Equal(0, clock.StepsForward);
            Assert.True(worst <= 1.0, $"{worst:F2}");
        }

        /// <summary>
        /// Тайм-аут входа (0-025; проверка 0-008 з. 8): соединение без входа закрывается через 300 тактов (10 с) по такту
        /// сервера — заморозка 2 с (тактов нет) его не приближает, вошедший игрок не трогается.
        /// </summary>
        [Fact]
        public void D07_S2_13_5_LoginTimeout_300Ticks_FreezeDoesNotCount()
        {
            using var rig = new Rig(1, null);
            rig.RunUntil(1_000_000);
            Assert.Equal(1, rig.Server.PlayersInGame);
            using LoopbackTransport silent = (LoopbackTransport)LoopbackTransport.ConnectTo(rig.St);
            long t0 = rig.Sim.Tick;
            rig.Freeze(2000);
            rig.RunUntil(rig.Now + 2_000_000);
            var h = new NullHandler();
            while (rig.Sim.Tick - t0 < NetServer.LoginTimeoutTicks - 2)
            {
                rig.RunUntil(rig.Now + 1000);
                silent.Poll(rig.Now, h);
            }

            Assert.Equal(0, rig.Server.LoginTimeouts);
            rig.RunUntil(rig.Now + 200_000);
            silent.Poll(rig.Now, h);
            Assert.Equal(1, rig.Server.LoginTimeouts);
            Assert.Equal(300, NetServer.LoginTimeoutTicks);
            Assert.Equal(1, rig.Server.PlayersInGame);
            Assert.Equal(NetClientState.InGame, rig.Clients[0].C.State);
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

        /// <summary>
        /// Часы показа чужих после фриза (проверка 0-021 з. 2, backlog «в 0-025»): «часы» сбрасывают окно медианы — уже
        /// после первого снимка «сервер сейчас» на новой шкале (без сброса медиана 30 образцов переходит через ~15 снимков);
        /// перестановка показа — один эпизод, а не по кадру.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void D07_S2_4_RemoteClock_ResetOnServerClock_OneEpisode(bool reset)
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

            now += 2_000_000; // фриз: такты не догоняются — номер идёт дальше с того же
            if (reset)
            {
                r.OnClockReset();
            }

            double offAfter3 = 0;
            for (int k = 0; k < 30; k++, now += 33_333)
            {
                snap.Tick = tick++;
                r.OnSnapshot(snap, now + 50_000);
                r.BeginFrame(now + 50_000);
                if (k == 2)
                {
                    offAfter3 = r.ServerNowUs(now + 50_000) - RemoteInterpolation.TickTimeUs(snap.Tick);
                }
            }

            _out.WriteLine($"сброс {reset}: «сервер сейчас» − такт снимка через 3 снимка {offAfter3 / 1000:F0} мс, перестановок {r.ClockResets}");
            if (reset)
            {
                Assert.InRange(offAfter3, -1000, 1000);
            }
            else
            {
                Assert.True(offAfter3 > 1_000_000, "без сброса медиана ещё на старой шкале");
            }

            Assert.True(r.ClockResets <= 1, $"{r.ClockResets}");
        }
    }

    /// <summary>Отдельная коллекция без параллели: UDP-прогон 60 с под чужой нагрузкой (другие Big UDP-тесты) краснеет не по своей вине.</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ClockUdpCollection
    {
        public const string Name = "ClockUdp";
    }

    /// <summary>Часы и заморозка по UDP (0-025): сервер в процессе теста, боты с предсказанием, `-All`.</summary>
    [Collection(ClockUdpCollection.Name)]
    public class ClockUdpTests
    {
        private readonly ITestOutputHelper _out;

        public ClockUdpTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>
        /// 2.13 п. 1 и 5 по UDP (`-All`): 2 бота с предсказанием × 60 с, RTT 100, потери 1 %, поток такта сервера заморожен на 2 с
        /// на 30-й секунде (<c>--debug-freeze</c>) — отключений 0, расхождение предсказания с сервером в тот же кадр ≤ 0,5 тайла
        /// в ≥ 99,9 % сверок.
        /// </summary>
        [Fact]
        [Trait("Category", "Big")]
        public async Task D07_S2_13_1_Udp_Big_TwoBots60s_Rtt100_Loss1_Freeze2s_NoDisconnects_Within05Tile()
        {
            int port = FreeUdpPort();
            ServerOptions so = ServerOptions.Parse(new[] { "--create", "--seed", "42", "--size", "1024", "--port", port.ToString(), "--debug-freeze", "900:2000" });
            using var stopServer = new CancellationTokenSource();
            Task<ServerResult> server = Task.Run(() => ServerRun.Run(so, null, stopServer.Token));
            BotOptions bo = BotOptions.Parse(new[]
            {
                "--connect", $"127.0.0.1:{port}", "--count", "2", "--script", "wander", "--rtt", "100", "--loss", "1", "--seconds", "60", "--seed", "7",
            });
            BotReport r;
            try
            {
                r = BotRun.Run(bo, null);
            }
            finally
            {
                Thread.Sleep(300);
                stopServer.Cancel();
                Assert.Equal(0, (await server).ExitCode);
            }

            foreach (BotStats b in r.Bots)
            {
                _out.WriteLine($"бот {b.Index}: {b.State}, {b.SecondsInGame:F1} с, сверок {b.PredCompared}, > 0,5 тайла {b.PredOverHalfTile}, наиб. {b.PredMaxDevUnits} u, " +
                               $"правок {b.PredResynced}; часы: «часы» {b.ClocksReceived}, по серверу {b.ClockServerResets}, вперёд {b.ClockStepsForward}, назад {b.ClockStepsBack}, " +
                               $"перенумераций {b.FrameRenumbers}, owd {b.OwdMs:F1} мс");
            }

            _out.WriteLine($"в игре {r.InGame}, отключений {r.Disconnects}, ≤ 0,5 тайла — {r.PredWithinHalfTile:P3}");
            Assert.Equal(2, r.InGame);
            Assert.Equal(0, r.Disconnects);
            foreach (BotStats b in r.Bots)
            {
                Assert.True(b.PredCompared > 50 * 25, $"сверок {b.PredCompared}");
                Assert.True(b.ClocksReceived >= 1, "«часы» после заморозки не пришли");
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
