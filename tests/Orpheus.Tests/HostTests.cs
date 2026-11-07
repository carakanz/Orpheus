using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Threading;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Hosting;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Platform.Pc;
using Orpheus.Save;
using Orpheus.Server;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Сервер одиночной игры и хоста без процессов (карточка 0-024; docs/07 2.3, 2.13 п. 9, 7.6, 1.6): протокол хозяина,
    /// два слушающих транспорта под одним сервером, токен и вытеснение, ожидание хозяина, файл-замок, «Process только в ПК-слое».
    /// </summary>
    public class HostTests
    {
        private const long TickMicros = 1_000_000 / Simulation.TickRate;
        private static readonly HostToken Token = new HostToken(0x0123456789abcdefUL, 0xfedcba9876543210UL);

        private readonly ITestOutputHelper _out;

        public HostTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- Протокол ----------

        [Fact]
        public void D07_S2_3_Codec_HostMessages_RoundTrip()
        {
            var login = new LoginMessage { Version = NetModule.Version, Name = "хост", Token = Token };
            byte[] a = Encode(b => login.Write(b));
            Assert.Equal(1 + 6 + 1 + 8 + 1 + 16, a.Length);
            LoginMessage back = LoginMessage.Read(a);
            Assert.Equal(Token, back.Token);
            Assert.Equal("хост", back.Name);
            Assert.Equal(a, Encode(b => back.Write(b)));

            byte[] plain = Encode(b => new LoginMessage { Version = NetModule.Version, Name = "друг" }.Write(b));
            Assert.True(LoginMessage.Read(plain).Token.IsEmpty);

            // Флаг токена 2 и токен из нулей — испорченный вход.
            byte[] badFlag = (byte[])plain.Clone();
            badFlag[badFlag.Length - 1] = 2;
            Assert.Throws<ProtocolException>(() => LoginMessage.Read(badFlag));
            byte[] zeros = plain.Concat(new byte[16]).ToArray();
            zeros[plain.Length - 1] = 1;
            Assert.Throws<ProtocolException>(() => LoginMessage.Read(zeros));

            Assert.Equal(new byte[] { 8 }, Encode(b => new OpenForFriendsMessage().Write(b)));
            OpenForFriendsMessage.Read(new byte[] { 8 });
            var open = new OpenResultMessage { Opened = true, Port = 7777 };
            OpenResultMessage openBack = OpenResultMessage.Read(Encode(b => open.Write(b)));
            Assert.True(openBack.Opened);
            Assert.Equal(7777, openBack.Port);
            Assert.Throws<ProtocolException>(() => OpenResultMessage.Read(new byte[] { 9, 2, 0, 0 }));

            foreach (ClosingReason r in new[] { ClosingReason.HostLeft, ClosingReason.Stopped, ClosingReason.Replaced })
            {
                Assert.Equal(r, ClosingMessage.Read(Encode(b => new ClosingMessage { Reason = r }.Write(b))).Reason);
            }

            Assert.Throws<ProtocolException>(() => ClosingMessage.Read(new byte[] { 10, 0 }));
            Assert.Throws<ProtocolException>(() => ClosingMessage.Read(new byte[] { 10, 4 }));
            Assert.Equal("Хост вышел. Мир сохранён у него", new ClosingMessage { Reason = ClosingReason.HostLeft }.Describe());
            Assert.Throws<ProtocolException>(() => Codec.TypeOf(new byte[] { 11 }));

            Assert.True(HostToken.TryParse(Token.ToString(), out HostToken parsed));
            Assert.Equal(Token, parsed);
            Assert.False(HostToken.TryParse(new string('0', 32), out _));
            Assert.False(HostToken.TryParse("0123", out _));
            Assert.False(HostToken.TryParse(new string('g', 32), out _));
            Assert.NotEqual(PcServerLauncher.NewToken(), PcServerLauncher.NewToken());
        }

        // ---------- Два транспорта под одним сервером ----------

        [Fact]
        public void D07_S2_13_9_Mux_TwoListeners_DistinctPeers_Routed()
        {
            LoopbackTransport a = LoopbackTransport.CreateServer(4);
            LoopbackTransport b = LoopbackTransport.CreateServer(4);
            using var mux = new MuxTransport(a);
            LoopbackTransport ca = LoopbackTransport.ConnectTo(a);
            mux.Add(b);
            LoopbackTransport cb = LoopbackTransport.ConnectTo(b);
            var rec = new Rec();
            mux.Poll(0, rec);
            Assert.Equal(2, rec.Connected.Count);
            Assert.NotEqual(rec.Connected[0], rec.Connected[1]); // у обоих внутренних — номер 1, снаружи — разные

            mux.Send(rec.Connected[1], NetChannel.Facts, new byte[] { 42 });
            var got = new Rec();
            cb.Poll(0, got);
            ca.Poll(0, new Rec());
            Assert.Single(got.Received);
            Assert.Equal(42, got.Received[0]);

            ca.Send(LoopbackTransport.ServerPeer, NetChannel.Facts, new byte[] { 7 });
            mux.Poll(0, rec);
            Assert.Equal((rec.Connected[0], (byte)7), rec.From.Single());

            ca.Disconnect(LoopbackTransport.ServerPeer, ReadOnlySpan<byte>.Empty);
            mux.Poll(0, rec);
            Assert.Equal(rec.Connected[0], rec.Disconnected.Single());
            Assert.Equal(-1, mux.RoundTripMs(rec.Connected[0]));
            Assert.Equal(0, mux.RoundTripMs(rec.Connected[1]));
        }

        [Fact]
        public void D07_S2_13_9_Loopback_OpenForFriends_SecondJoins_WorldNotRestarted()
        {
            using var h = new HostHarness();
            NetClient host = h.Connect(h.Primary, "хост", Token);
            h.Run(5);
            Assert.Equal(NetClientState.InGame, host.State);
            Assert.True(h.Server.HostInGame);
            Assert.Equal(1, h.Joined);

            // До «Открыть для друзей» второго входа нет: слушает только петля (второго транспорта нет).
            Assert.Null(h.Public);
            NetClient stranger = h.Connect(h.Primary, "чужой", default);
            h.Run(3);
            Assert.Equal(NetClientState.InGame, stranger.State); // без токена — обычный игрок, не хозяин
            stranger.RequestOpenForFriends();
            h.Run(3);
            Assert.Null(h.Public); // команда не от хозяина — мимо
            Assert.Null(stranger.OpenResult);

            long tickBefore = h.Sim.Tick;
            ulong genHash = StateHash.Generation(h.Sim.World);
            host.RequestOpenForFriends();
            h.Run(3);
            Assert.NotNull(h.Public);
            Assert.True(host.OpenResult!.Value.Opened);
            Assert.Equal(HostHarness.OpenPort, host.OpenResult.Value.Port);

            NetClient friend = h.Connect(h.Public!, "друг", default);
            h.Run(5);
            Assert.Equal(NetClientState.InGame, friend.State);
            Assert.Equal(NetClientState.InGame, host.State); // хозяин не переподключался
            Assert.Equal(host.Accept.EntityId, h.Server.EntityOf(1)); // и та же сущность на прежнем соединении
            Assert.True(friend.Accept.ServerTick > tickBefore); // такт шёл подряд
            Assert.Equal(genHash, friend.Accept.GenerationHash);
            Assert.Equal(1, h.Opens);

            // Повтор команды — тот же порт, второй сокет не открывается.
            host.RequestOpenForFriends();
            h.Run(3);
            Assert.Equal(1, h.Opens);
            Assert.Equal(3, h.Server.PlayersInGame);
            Assert.Equal(0, h.Server.Disconnects);
        }

        [Fact]
        public void D07_S7_6_Loopback_Token_BadRejected_SecondLoginReplacesFirst()
        {
            using var h = new HostHarness();
            NetClient bad = h.Connect(h.Primary, "вор", new HostToken(1, 2));
            h.Run(3);
            Assert.Equal(NetClientState.Rejected, bad.State);
            Assert.Equal(LoginRejectReason.BadToken, bad.Reject!.Value.Reason);

            NetClient first = h.Connect(h.Primary, "хост", Token);
            NetClient friend = h.Connect(h.Primary, "друг", default);
            h.Run(5);
            Assert.Equal(NetClientState.InGame, first.State);

            // Игра хоста упала, соединение ещё не отвалилось по тайм-ауту — вернувшаяся игра входит сразу, прежнее вытеснено.
            NetClient again = h.Connect(h.Primary, "хост", Token);
            h.Run(5);
            Assert.Equal(NetClientState.InGame, again.State);
            Assert.Equal(NetClientState.Disconnected, first.State);
            Assert.Equal(ClosingReason.Replaced, first.Closing);
            Assert.Equal("Вы вошли с другого компьютера", first.DisconnectText);
            Assert.Equal(NetClientState.InGame, friend.State);
            Assert.Equal(2, h.Server.PlayersInGame);
            Assert.Equal(2, h.Sim.EntityCount);
            Assert.Equal(2, h.Joined);

            // Выделенный сервер (без токена хозяина) вход с токеном не пускает.
            using var dedicated = new HostHarness(hostToken: new HostToken());
            NetClient c = dedicated.Connect(dedicated.Primary, "хост", Token);
            dedicated.Run(3);
            Assert.Equal(LoginRejectReason.BadToken, c.Reject!.Value.Reason);

            // Закрытие мира: всем — «Хост вышел…» в данных отключения.
            h.Server.CloseAll(ClosingReason.HostLeft);
            h.Run(2);
            Assert.Equal("Хост вышел. Мир сохранён у него", friend.DisconnectText);
            Assert.Equal(ClosingReason.HostLeft, again.Closing);
        }

        [Fact]
        public void D07_S7_6_Loopback_HostLeft_ReportsLastHeard()
        {
            using var h = new HostHarness();
            NetClient host = h.Connect(h.Primary, "хост", Token);
            h.Run(10);
            long lastHeard = h.Now;
            host.Disconnect();
            h.Run(3);
            Assert.Single(h.Left);
            Assert.InRange(h.Left[0].LastHeard, lastHeard - 2 * TickMicros, lastHeard);
            Assert.True(h.Left[0].Now >= h.Left[0].LastHeard);
            Assert.False(h.Server.HostInGame);
        }

        // ---------- Ожидание хозяина ----------

        [Fact]
        public void D07_S2_3_HostSession_Grace60s_FromLastHeard()
        {
            const long S = 1_000_000;
            var s = new HostSession(60 * S, 0);
            Assert.False(s.Expired(59 * S));
            Assert.True(s.Expired(60 * S)); // хозяин так и не вошёл
            s.Joined();
            Assert.False(s.Expired(1000 * S));

            // Упала игра: транспорт заметил через 10 с тишины — отсчёт от последнего сообщения.
            s.Left(100 * S, 110 * S);
            Assert.False(s.Expired(159 * S));
            Assert.True(s.Expired(160 * S));
            s.Joined();
            Assert.Equal(1, s.Returns);
            Assert.False(s.Expired(10_000 * S));

            // Время «последнего сообщения» из будущего или не было — от сейчас.
            s.Left(0, 200 * S);
            Assert.Equal(260 * S, s.DeadlineMicros);
        }

        // ---------- Файл-замок ----------

        [Fact]
        public void D07_S7_6_HostLockFile_WriteRead_StaleWithoutWorldLock()
        {
            string dir = Path.Combine(Path.GetTempPath(), "orpheus-hostlock-" + Guid.NewGuid().ToString("N"));
            var storage = new PcStorage();
            try
            {
                storage.CreateDirectory(dir);
                Assert.Null(HostLockFile.TryRead(storage, dir));
                var l = new HostLock { Port = 50123, Token = Token.ToString(), ProcessId = 4242, Mode = "host" };
                HostLockFile.Write(storage, dir, l);
                HostLock back = HostLockFile.TryRead(storage, dir)!;
                Assert.Equal(50123, back.Port);
                Assert.Equal(Token.ToString(), back.Token);
                Assert.Equal(4242, back.ProcessId);
                Assert.Equal("host", back.Mode);
                Assert.Equal(-1, back.OpenPort);
                Assert.False(File.Exists(HostLockFile.PathIn(dir) + ".tmp"));

                var launcher = new PcServerLauncher(Path.Combine(dir, "OrpheusServer.exe"));
                Assert.False(HostLockFile.IsWorldHeld(storage, dir));
                Assert.Null(launcher.FindRunning(dir)); // файл есть, а мир никто не держит — сервер умер (свет)

                using (new WorldFolder(storage, dir).Lock())
                {
                    Assert.True(HostLockFile.IsWorldHeld(storage, dir));
                    ServerEndpoint e = launcher.FindRunning(dir)!.Value;
                    Assert.Equal(50123, e.Port);
                    Assert.Equal(Token, e.Token);
                    Assert.Equal("127.0.0.1", e.Host);
                }

                File.WriteAllText(HostLockFile.PathIn(dir), "мусор\nport=1\n");
                Assert.Null(HostLockFile.TryRead(storage, dir));
                HostLockFile.Delete(storage, dir);
                HostLockFile.Delete(storage, dir);
                Assert.False(File.Exists(HostLockFile.PathIn(dir)));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // ---------- Ключи ----------

        [Fact]
        public void D07_S2_3_ServerOptions_ModeKeys()
        {
            string t = Token.ToString();
            ServerOptions o = ServerOptions.Parse(new[] { "--world", "w", "--port", "0", "--mode", "host", "--host-token", t });
            Assert.Equal(ServerMode.Host, o.Mode);
            Assert.Equal(Token, o.HostToken);
            Assert.True(o.LoopbackOnly); // без «Открыть для друзей» — только петля
            Assert.Null(o.OpenAddress);
            Assert.Equal(7777, o.OpenPort);
            Assert.Equal(60, o.HostGraceSeconds);

            o = ServerOptions.Parse(new[] { "--world", "w", "--port", "0", "--mode", "single", "--host-token", t, "--open-address", "127.0.0.2", "--open-port", "0", "--host-grace", "3" });
            Assert.Equal(ServerMode.Single, o.Mode);
            Assert.Equal("127.0.0.2", o.OpenAddress!.ToString());
            Assert.Equal(0, o.OpenPort);
            Assert.Equal(3, o.HostGraceSeconds);

            foreach (string[] bad in new[]
            {
                new[] { "--world", "w", "--port", "0", "--mode", "host" }, // нет токена
                new[] { "--world", "w", "--mode", "host", "--host-token", t }, // нет порта
                new[] { "--create", "--port", "0", "--mode", "host", "--host-token", t }, // нет папки мира
                new[] { "--world", "w", "--port", "0", "--mode", "host", "--host-token", t, "--public" },
                new[] { "--world", "w", "--port", "0", "--host-token", t }, // токен без режима
                new[] { "--world", "w", "--port", "0", "--mode", "duo", "--host-token", t },
                new[] { "--world", "w", "--port", "0", "--mode", "host", "--host-token", "00" },
                new[] { "--world", "w", "--port", "0", "--mode", "host", "--host-token", t, "--open-address", "::1" },
                new[] { "--world", "w", "--port", "0", "--mode", "host", "--host-token", t, "--host-grace", "0" },
            })
            {
                Assert.Throws<ArgumentException>(() => ServerOptions.Parse(bad));
            }

            var launch = new ServerLaunch { WorldDir = @"C:\мир с пробелом", Create = true, Seed = 5, OpenAddress = "127.0.0.2", OpenPort = 0, HostGraceSeconds = 3 };
            List<string> args = PcServerLauncher.Arguments(launch, Token);
            ServerOptions fromLauncher = ServerOptions.Parse(args.ToArray());
            Assert.Equal(ServerMode.Single, fromLauncher.Mode);
            Assert.Equal(launch.WorldDir, fromLauncher.WorldDir);
            Assert.Equal(0, fromLauncher.Port);
            Assert.Equal(3, fromLauncher.HostGraceSeconds);
            Assert.Equal(@"--world ""C:\мир с пробелом"" --x ""a\""b"" ""d e\\"" c\", PcServerLauncher.JoinArguments(new[] { "--world", launch.WorldDir, "--x", "a\"b", "d e\\", "c\\" }));
        }

        // ---------- Сервер игры целиком в процессе теста: хозяин не вошёл ----------

        [Fact]
        public void D07_S2_3_ServerRun_HostNeverJoins_SavesAndExitsAfterGrace()
        {
            string dir = Path.Combine(Path.GetTempPath(), "orpheus-grace-" + Guid.NewGuid().ToString("N"));
            try
            {
                ServerOptions o = ServerOptions.Parse(new[]
                {
                    "--world", dir, "--create", "--seed", "11", "--size", "1024", "--port", "0", "--mode", "single",
                    "--host-token", Token.ToString(), "--host-grace", "1", "--open-address", "127.0.0.2", "--open-port", "0",
                });
                bool lockSeen = false;
                o.AfterTick = _ => lockSeen |= File.Exists(HostLockFile.PathIn(dir));
                var console = new StringWriter();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                ServerResult r = ServerRun.Run(o, console);
                double seconds = watch.Elapsed.TotalSeconds;
                _out.WriteLine($"выход через {seconds:0.00} с, тактов {r.Ticks}, сохранений {r.SavesWritten}");
                Assert.Equal(0, r.ExitCode);
                Assert.True(r.HostLeft);
                Assert.InRange(r.Ticks, 25, 40); // 1 с ± по часам такта
                Assert.True(lockSeen, "host.lock не появился");
                Assert.False(File.Exists(HostLockFile.PathIn(dir)), "host.lock не удалён при выходе");
                Assert.Contains($"{ServerRun.ReadyMarker} port={r.Port}", console.ToString());
                Assert.True(r.SavesWritten >= 1);
                Assert.Equal(-1, r.OpenedPort);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // ---------- 1.6: Process только в ПК-слое ----------

        [Fact]
        public void D07_S1_6_Process_OnlyInPlatformPc()
        {
            // Сборки ядра (их же берёт клиент Unity) не ссылаются на System.Diagnostics.Process — по метаданным.
            foreach (string name in new[] { "Orpheus.Core", "Orpheus.WorldGen", "Orpheus.Net", "Orpheus.Save" })
            {
                Assert.DoesNotContain("System.Diagnostics.Process", TypeRefs(name));
            }

            Assert.Contains("System.Diagnostics.Process", TypeRefs("Orpheus.Platform.Pc")); // шов проверки живой

            // Исходники ядра и клиента Unity — без Process (клиент собирается Unity, его сборок здесь нет).
            string root = RepoRoot();
            var dirs = new List<string> { Path.Combine(root, "client", "Assets", "Scripts") };
            dirs.AddRange(new[] { "Orpheus.Core", "Orpheus.WorldGen", "Orpheus.Net", "Orpheus.Save" }.Select(n => Path.Combine(root, "src", n)));
            int files = 0;
            foreach (string dir in dirs.Where(Directory.Exists))
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    files++;
                    string text = File.ReadAllText(file);
                    Assert.False(text.Contains("Diagnostics.Process", StringComparison.Ordinal) || text.Contains("Process.Start", StringComparison.Ordinal),
                        $"{file}: Process вне Orpheus.Platform.Pc (docs/07, 1.6)");
                }
            }

            Assert.True(files > 50, $"файлов {files}");
        }

        private static HashSet<string> TypeRefs(string assembly)
        {
            string path = Path.Combine(AppContext.BaseDirectory, assembly + ".dll");
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            MetadataReader md = pe.GetMetadataReader();
            var set = new HashSet<string>();
            foreach (TypeReferenceHandle h in md.TypeReferences)
            {
                TypeReference t = md.GetTypeReference(h);
                set.Add(md.GetString(t.Namespace) + "." + md.GetString(t.Name));
            }

            return set;
        }

        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Orpheus.sln")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static byte[] Encode(Func<byte[], int> write)
        {
            var buf = new byte[256];
            int n = write(buf);
            return buf.AsSpan(0, n).ToArray();
        }

        private sealed class Rec : ITransportHandler
        {
            public List<int> Connected { get; } = new List<int>();
            public List<int> Disconnected { get; } = new List<int>();
            public List<byte> Received { get; } = new List<byte>();
            public List<(int Peer, byte B)> From { get; } = new List<(int, byte)>();

            public void OnConnected(int peer) => Connected.Add(peer);

            public void OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload) => Disconnected.Add(peer);

            public void OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
                Received.Add(data[0]);
                From.Add((peer, data[0]));
            }
        }

        /// <summary>Сервер игры на связи в памяти: петля + «Открыть для друзей» вторым транспортом под <see cref="MuxTransport"/>.</summary>
        private sealed class HostHarness : IDisposable
        {
            public const int OpenPort = 7777;

            private readonly MuxTransport _mux;
            private readonly List<NetClient> _clients = new List<NetClient>();

            public HostHarness(HostToken? hostToken = null)
            {
                WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 7);
                Sim = new Simulation(world, maxEntities: 64);
                Primary = LoopbackTransport.CreateServer(16);
                _mux = new MuxTransport(Primary);
                Server = new NetServer(_mux, Sim, n => ServerRun.SpawnPoint(world.Shape, n), 8, NetModule.Version)
                {
                    HostToken = hostToken ?? Token,
                };
                Server.OpenForFriends = () =>
                {
                    Public = LoopbackTransport.CreateServer(16);
                    _mux.Add(Public);
                    Opens++;
                    return OpenPort;
                };
                Server.HostJoined = () => Joined++;
                Server.HostLeft = (heard, now) => Left.Add((heard, now));
            }

            public Simulation Sim { get; }
            public NetServer Server { get; }
            public LoopbackTransport Primary { get; }
            public LoopbackTransport? Public { get; private set; }
            public int Opens { get; private set; }
            public int Joined { get; private set; }
            public List<(long LastHeard, long Now)> Left { get; } = new List<(long, long)>();
            public long Now { get; private set; }

            public NetClient Connect(LoopbackTransport server, string name, HostToken token)
            {
                var c = new NetClient(LoopbackTransport.ConnectTo(server), LoopbackTransport.ServerPeer, name, NetModule.Version) { HostToken = token };
                _clients.Add(c);
                return c;
            }

            public void Run(int ticks)
            {
                for (int t = 0; t < ticks; t++)
                {
                    Now += TickMicros;
                    foreach (NetClient c in _clients)
                    {
                        c.Poll(Now);
                        c.SendFrame(default);
                    }

                    Server.Poll(Now);
                    Sim.Step();
                    Server.SendSnapshots();
                }

                foreach (NetClient c in _clients)
                {
                    c.Poll(Now);
                }
            }

            public void Dispose()
            {
                foreach (NetClient c in _clients)
                {
                    c.Transport.Dispose();
                }

                _mux.Dispose();
            }
        }
    }
}
