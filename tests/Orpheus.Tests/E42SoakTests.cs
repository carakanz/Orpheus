using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Orpheus.Bots;
using Orpheus.Core.Geometry;
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
    /// E4-2 (шаг 4.2 плана): все новые механики под предсказанием — сквозной прогон двух ботов <c>mech_tour</c>
    /// (добыча, крафт, сундук, лук, постройки сетки 0,5, урон/смерть) 30 минут реального времени по UDP
    /// (127.0.0.1, сервер в процессе теста), расхождений предсказания 0 (сверх принятой нормы касания врага).
    /// Прогон тяжёлый ([Trait Category=Soak]): ни в CI, ни в <c>-All</c>; запуск явный —
    /// <c>dotnet test --filter "FullyQualifiedName~E42"</c> или <c>tools\ci.cmd -Soak -TimeLimitSec 0</c>.
    /// Контроль «хэш сервера = функция входа»: детерминированный стенд Loopback (виртуальные часы) —
    /// прогон с ботами пишёт журнал всего ввода в сервер, прогон-реплей прогоняет журнал по новому серверу,
    /// хэши состояния совпадают каждые 300 тактов. UDP-прогон для контроля непригоден: кадры 60 Гц в
    /// сетевой журнал не пишутся (<c>Simulation.Input.cs:93</c>), время прилёта плывёт — см. «Решения» карточки.
    /// </summary>
    public class E42SoakTests
    {
        private readonly ITestOutputHelper _out;

        public E42SoakTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>Норма расхождений (net.md «Известно»): правок сверх половины тайла — 0; отклонение в unit
        /// не больше касания врага (<see cref="Torus.SubTilesPerTile"/> − 4 = 68, <c>PredictionTests…EnemyTouch</c>).</summary>
        private const int PredMaxDevNormUnits = 68;

        // ---------------------------------------------------------------- 30-минутный прогон

        /// <summary>
        /// Приёмка А: два бота <c>mech_tour</c> 30 минут (сжатая ночная шкала 30 — ночи идут всю вторую половину
        /// прогона), сервер в процессе теста на 127.0.0.1. В логе — счётчики фаз (добыто, изготовлено, сундук,
        /// выстрелов, построек, ударов, смертей); у обоих: правок предсказания сверх нормы 0, отклонение ≤ 68 unit,
        /// <c>PredResynced</c> — в лог. Оба к концу живы (автореспавн), State = InGame.
        /// </summary>
        [Fact]
        [Trait("Category", "Soak")]
        public void E42_Soak_TwoBots_30Min_AllMechanics_NoPredictionDivergence()
        {
            BotReport report = RunSoak(seconds: 1800);
            Assert.Equal(2, report.Bots.Count);
            for (int i = 0; i < report.Bots.Count; i++)
            {
                BotStats b = report.Bots[i];
                _out.WriteLine($"бот {i}: состояние {b.State}, предсказаний {b.PredCompared}, правок >½тайла {b.PredOverHalfTile}, " +
                               $"макс. отклонение {b.PredMaxDevUnits} unit, ресинков {b.PredResynced}, кадров {b.InputsSent}");
                Assert.Equal("InGame", b.State);
                Assert.Equal(0, b.PredOverHalfTile);
                Assert.True(b.PredMaxDevUnits <= PredMaxDevNormUnits, $"бот {i}: отклонение предсказания {b.PredMaxDevUnits} > нормы {PredMaxDevNormUnits}");

                string? s = b.MechTour;
                Assert.NotNull(s);
                _out.WriteLine($"бот {i}: {s}");
                Assert.True(Num(s!, @"деревьев (\d+)") > 0, $"бот {i}: деревьев не добыто: {s}");
                Assert.True(Num(s!, @"камней (\d+)") > 0, $"бот {i}: камня не добыто: {s}");
                Assert.True(Num(s!, @"верстак=(\d+)") == 1, $"бот {i}: верстак не поставлен: {s}");
                Assert.True(Num(s!, @"сундук=(\d+)") == 1, $"бот {i}: сундук не изготовлен: {s}");
                Assert.True(Num(s!, @"лук=(\d+)") == 1, $"бот {i}: лук не изготовлен: {s}");
                Assert.True(Num(s!, @"стрелы=(\d+)") == 1, $"бот {i}: стрелы не изготовлены: {s}");
                Assert.True(Num(s!, @"сундук: пост=1, откр=1, пол=(\d+), взят=(\d+)") > 0, $"бот {i}: в сундук ничего не положено: {s}");
                Assert.True(Num(s!, @"сундук: пост=1, откр=1, пол=\d+, взят=(\d+)") > 0, $"бот {i}: из сундука ничего не взято: {s}");
                Assert.True(Num(s!, @"выстр=(\d+)") > 0, $"бот {i}: ни одного выстрела: {s}");
                Assert.True(Num(s!, @"стройку: пол=(\d+)") > 0, $"бот {i}: пол не поставлен: {s}");
                Assert.True(Num(s!, @"стройку: пол=\d+, стен=(\d+)") > 0, $"бот {i}: стены не поставлены: {s}");
                Assert.True(Num(s!, @"удары=(\d+)") > 0, $"бот {i}: урона не получал: {s}");
                Assert.True(Num(s!, @"смерти=(\d+)") > 0, $"бот {i}: не умирал за 30 минут ночи: {s}");
                Assert.DoesNotContain("отказ:", s!);
            }
        }

        /// <summary>Прогон вне метода-теста (xUnit1031: ожидание сервера — не в тесте; образец <c>NightBotsTests.RunBuildRoomUdp</c>).</summary>
        private static BotReport RunSoak(int seconds)
        {
            int port = FreeUdpPort();
            ServerOptions so = ServerOptions.Parse(new[]
            {
                "--create", "--seed", "11", "--size", "1024", "--port", port.ToString(),
                "--debug-day-scale", "30",
            });
            using var stopServer = new CancellationTokenSource();
            Task<ServerResult> server = Task.Run(() => ServerRun.Run(so, null, stopServer.Token));

            var bo = BotOptions.Parse(new[]
            {
                "--connect", $"127.0.0.1:{port}", "--count", "2", "--script", MechTourBot.Name,
                "--rtt", "100", "--loss", "0", "--seconds", seconds.ToString(), "--seed", "7",
            });
            BotReport report;
            try
            {
                report = BotRun.Run(bo, null);
            }
            finally
            {
                Thread.Sleep(300); // отключение ботов доходит до сервера
                stopServer.Cancel();
                Assert.Equal(0, server.GetAwaiter().GetResult().ExitCode);
            }

            return report;
        }

        private static int Num(string s, string regex)
        {
            Match m = Regex.Match(s, regex);
            Assert.True(m.Success, $"нет «{regex}» в строке: {s}");
            return int.Parse(m.Groups[1].Value);
        }

        internal static int FreeUdpPort()
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)s.LocalEndPoint!).Port;
        }

        // ---------------------------------------------------------------- контроль: журнал ввода → реплей → хэш

        /// <summary>Тактов детерминированного контрольного прогона (267 с игры): фаза добычи/крафта до медной кирки.</summary>
        private const int ControlTicks = 20_000;

        private const long FrameMicros = 1_000_000 / 60;

        /// <summary>
        /// «Хэш сервера совпадает с хэшем контрольного прогона той же последовательности ввода»: стенд Loopback
        /// (виртуальные часы, lockstep 2 кадра/такт), два бота <c>mech_tour</c>; транспорт сервера пишет журнал
        /// всего входящего ввода (такт, пир, канал, байты). Второй стенд — тот же мир и журнал, клиентов-«игроков»
        /// нет: сообщения подаются реплеером на тех же тактах. Хэши состояния совпадают каждые 300 тактов.
        /// </summary>
        [Fact]
        public void E42_Control_Replay_Same_Hash()
        {
            // — прогон 1: боты на виртуальных часах, журнал ввода
            WorldModel world1 = PreparedWorld();
            var sim1 = new Simulation(world1, maxEntities: 32);
            Func<int, WorldPos> spawn = n => EditTests.TileCenter(300 + 2 * n, 200);
            var journal = new List<(long Tick, int Peer, NetChannel Channel, byte[] Data)>();
            long doneTicks = 0;
            var bots = new Dictionary<NetClient, MechTourBot>();
            var hashes1 = new List<ulong>();
            using (var rig = new EditTests.EditRig(sim1, spawn, t => new RecordingTransport(t, journal, () => doneTicks)))
            {
                doneTicks = 0;
                rig.InputOf = cl =>
                {
                    if (!cl.HasSnapshot || !cl.Latest.TryFind(cl.EntityId, out SnapshotEntity se))
                    {
                        return InputFrame.None;
                    }

                    if (!bots.TryGetValue(cl, out MechTourBot? bot))
                    {
                        bot = new MechTourBot(cl.Shape, se.Position);
                        bots[cl] = bot;
                        cl.EditReplied = bot.OnReply;
                        cl.CraftReplied = bot.OnCraftReply;
                        cl.DeathSeen = bot.OnDeath;
                    }

                    return bot.Next(cl, se.Position, rig.NowMicros);
                };
                rig.AfterTick = () =>
                {
                    doneTicks++;
                    if (doneTicks % 300 == 0)
                    {
                        hashes1.Add(StateHash.Compute(rig.Sim).Total);
                    }
                };

                rig.Join();
                rig.Join();
                rig.Run(ControlTicks);

                Assert.Equal(2, bots.Count);
                Assert.All(bots.Values, b => Assert.True(b.TreesTotal + b.RocksTotal > 0, $"бот ничего не добыл: {b.Summary}"));
                Assert.Contains(bots.Values, b => b.BenchPlaced);
                Assert.True(journal.Count > 10_000, $"журнал ввода подозрительно мал: {journal.Count} сообщений");
                _out.WriteLine($"прогон 1: тактов {doneTicks}, сообщений в журнале {journal.Count}, " +
                               $"боты: {string.Join(" | ", bots.Values.Select(b => b.Summary))}");
            }

            // — прогон 2: тот же мир, тот же журнал; игроков нет, только реплей байтов по тактам
            WorldModel world2 = PreparedWorld();
            var sim2 = new Simulation(world2, maxEntities: 32);
            using var st2 = LoopbackTransport.CreateServer(16);
            var srv2 = new NetServer(st2, sim2, spawn, 8, NetModule.Version);
            var noop = new NoopHandler();
            int[] peers = journal.Select(m => m.Peer).Distinct().OrderBy(p => p).ToArray();
            var connFor = new Dictionary<int, LoopbackTransport>();
            var conns = new List<LoopbackTransport>();
            foreach (int p in peers)
            {
                LoopbackTransport c = LoopbackTransport.ConnectTo(st2);
                connFor[p] = c;
                conns.Add(c);
            }

            var byTick = journal.GroupBy(m => m.Tick).ToDictionary(g => g.Key, g => g.ToList());
            var hashes2 = new List<ulong>();
            long frame = 0;
            for (long tick = 0; tick < ControlTicks; tick++)
            {
                frame += 2;
                long now = frame * FrameMicros;
                if (byTick.TryGetValue(tick, out List<(long Tick, int Peer, NetChannel Channel, byte[] Data)>? msgs))
                {
                    foreach ((_, int peer, NetChannel channel, byte[] data) in msgs)
                    {
                        connFor[peer].Send(LoopbackTransport.ServerPeer, channel, data);
                    }
                }

                foreach (LoopbackTransport c in conns)
                {
                    c.Poll(now, noop); // drains server → client
                }

                srv2.Poll(now);
                sim2.Step();
                srv2.SendSnapshots();
                sim2.RefillPools();
                if ((tick + 1) % 300 == 0)
                {
                    hashes2.Add(StateHash.Compute(sim2).Total);
                }
            }

            foreach (LoopbackTransport c in conns)
            {
                c.Dispose();
            }

            Assert.Equal(hashes1.Count, hashes2.Count);
            Assert.True(hashes1.Count >= ControlTicks / 300);
            for (int i = 0; i < hashes1.Count; i++)
            {
                Assert.Equal(hashes1[i], hashes2[i]);
            }

            _out.WriteLine($"прогон 2: хэшей {hashes2.Count}, финальный {hashes2[^1]:x16} — совпадает с прогоном 1");
        }

        /// <summary>Мир контроля: плоский (зерно 1) + рощица, валуны, медь и гранит у старта (образец <c>CraftChainTests</c>).</summary>
        private static WorldModel PreparedWorld()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            void Put(int x, int y, ushort obj) => world.SetBaseTile(new TileCoord(x, y), FloorId.Grass, obj);
            for (int i = 0; i < 6; i++)
            {
                Put(306 + 2 * (i % 3), 196 + 2 * (i / 3), ObjectId.Tree);
            }

            for (int i = 0; i < 8; i++)
            {
                Put(294 - i % 4, 203 + i / 4, ObjectId.Rock);
            }

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    Put(312 + x, 208 + y, ObjectId.Ore);
                }
            }

            for (int i = 0; i < 3; i++)
            {
                Put(320 + i, 214, ObjectId.Granite);
            }

            return world;
        }

        /// <summary>Обёртка транспорта сервера: пишет весь входящий ввод (такт, пир, канал, копия байтов) и пропускает к inner.</summary>
        private sealed class RecordingTransport : ITransport
        {
            private readonly ITransport _inner;
            private readonly List<(long Tick, int Peer, NetChannel Channel, byte[] Data)> _journal;
            private readonly Func<long> _tick;

            public RecordingTransport(ITransport inner, List<(long Tick, int Peer, NetChannel Channel, byte[] Data)> journal, Func<long> tick)
            {
                _inner = inner;
                _journal = journal;
                _tick = tick;
            }

            public void Poll(long nowMicros, ITransportHandler handler) => _inner.Poll(nowMicros, new Proxy(this, handler));

            public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data) => _inner.Send(peer, channel, data);

            public void Flush() => _inner.Flush();

            public void Disconnect(int peer, ReadOnlySpan<byte> payload) => _inner.Disconnect(peer, payload);

            public int RoundTripMs(int peer) => _inner.RoundTripMs(peer);

            public void Dispose() => _inner.Dispose();

            private sealed class Proxy : ITransportHandler
            {
                private readonly RecordingTransport _owner;
                private readonly ITransportHandler _handler;

                public Proxy(RecordingTransport owner, ITransportHandler handler)
                {
                    _owner = owner;
                    _handler = handler;
                }

                public void OnConnected(int peer) => _handler.OnConnected(peer);

                public void OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload) =>
                    _handler.OnDisconnected(peer, reason, payload);

                public void OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
                {
                    _owner._journal.Add((_owner._tick(), peer, channel, data.ToArray()));
                    _handler.OnReceive(peer, channel, data);
                }
            }
        }

        /// <summary>Получатель-пустышка: забирает и выбрасывает всё (реплей-соединения не читают ответы сервера).</summary>
        private sealed class NoopHandler : ITransportHandler
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
    }
}
