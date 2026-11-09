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
            Assert.Throws<ProtocolException>(() => Codec.TypeOf(new byte[] { (byte)MessageType.Respawn + 1 })); // последний тип — Respawn (1-001)
            Assert.Throws<ProtocolException>(() => Codec.TypeOf(new byte[] { (byte)MessageType.MeleeResult + 1 })); // 15 не занят: после MeleeResult (0-029)
            Assert.Throws<ProtocolException>(() => Codec.TypeOf(new byte[] { 15 }));
            Assert.Throws<ProtocolException>(() => Codec.TypeOf(new byte[] { (byte)MessageType.Chunk - 1 })); // … и до Chunk (0-026)
            Assert.Equal(MessageType.Pong, Codec.TypeOf(new byte[] { (byte)MessageType.Pong }));
            Assert.Equal(MessageType.SwingStart, Codec.TypeOf(new byte[] { (byte)MessageType.SwingStart }));
            Assert.Equal(MessageType.MeleeResult, Codec.TypeOf(new byte[] { (byte)MessageType.MeleeResult }));
            Assert.Equal(MessageType.ChunkRequest, Codec.TypeOf(new byte[] { (byte)MessageType.ChunkRequest }));

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

        // ---------- Правки по проверке 0-024 ----------

        /// <summary>
        /// Замечание 1: «Открыть для друзей», а <c>host.lock</c> открыт на чтение другим дескриптором (так его читает
        /// вернувшаяся игра, антивирус, индексатор) — перенос нового замка на место отказывает. Сервер живёт, порт открыт,
        /// замок дописывается вне такта после закрытия дескриптора, мир при остановке сохраняется штатно.
        /// </summary>
        [Fact]
        public void D07_S7_6_ServerRun_OpenForFriends_LockHeldOpen_ServerLives_LockUpdatedLater()
        {
            string dir = Path.Combine(Path.GetTempPath(), "orpheus-lockheld-" + Guid.NewGuid().ToString("N"));
            NetClient? host = null;
            try
            {
                using var srv = new InProcServer(dir);
                int port = srv.WaitReady(20_000);
                host = UdpClient(port, "хост", Token);
                Assert.True(Pump(() => host.State == NetClientState.InGame, 10_000, host), $"хозяин не вошёл: {host.State} {host.RejectText}");

                string lockPath = HostLockFile.PathIn(dir);
                var storage = new PcStorage();
                Assert.Equal(-1, HostLockFile.TryRead(storage, dir)!.OpenPort);
                int openPort;
                using (new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    host.RequestOpenForFriends();
                    Assert.True(Pump(() => host.OpenResult.HasValue && srv.Text.Contains("файл-замок хоста не записан"), 10_000, host),
                        "нет итога «Открыть для друзей» или предупреждения о замке");
                    Assert.True(host.OpenResult!.Value.Opened);
                    openPort = host.OpenResult.Value.Port;
                    Pump(() => false, 500, host); // замок всё ещё занят — повторы идут, сервер работает
                    Assert.False(srv.Exited, "сервер упал из-за замка");
                    Assert.Equal(NetClientState.InGame, host.State);
                    Assert.Equal(-1, HostLockFile.TryRead(storage, dir)!.OpenPort); // старый замок цел
                }

                Assert.True(Pump(() => HostLockFile.TryRead(storage, dir)?.OpenPort == openPort, 5_000, host), "замок не обновился после закрытия дескриптора");
                Assert.False(srv.Exited);

                ServerResult r = srv.Stop(30_000);
                _out.WriteLine($"неудачных записей замка {r.HostLockFailures}, удачных {r.HostLockWrites}, сохранений {r.SavesWritten}, порт друзей {openPort}");
                Assert.Equal(0, r.ExitCode);
                Assert.True(r.HostLockFailures >= 1);
                Assert.True(r.HostLockWrites >= 2);
                Assert.True(r.SavesWritten >= 1, "мир не сохранён при остановке");
                Assert.True(r.LastSavedNumber > 0);
                Assert.Equal(openPort, r.OpenedPort);
                Assert.False(File.Exists(lockPath), "host.lock не удалён при выходе");
            }
            finally
            {
                host?.Transport.Dispose();
                Directory.Delete(dir, true);
            }
        }

        /// <summary>
        /// Замечание 2 (мутация П5): игра хоста ушла, других игроков нет — мир сохраняется сразу (2.3), а не через 60 с
        /// ожидания: у Автора нет ИБП, и до выхода сервера мир иначе держится только в памяти.
        /// </summary>
        [Fact]
        public void D07_S2_3_ServerRun_HostLeftAlone_SavedAtOnce()
        {
            string dir = Path.Combine(Path.GetTempPath(), "orpheus-leftalone-" + Guid.NewGuid().ToString("N"));
            NetClient? host = null;
            try
            {
                using var srv = new InProcServer(dir); // ожидание хозяина — 60 с по умолчанию
                int port = srv.WaitReady(20_000);
                host = UdpClient(port, "хост", Token);
                Assert.True(Pump(() => host.State == NetClientState.InGame, 10_000, host), $"хозяин не вошёл: {host.State}");
                // Хозяин вошёл впервые — создан персонаж: сохранение сразу (6.4, 0-037); автосохранение — раз в 2 мин, его нет.
                Assert.True(Pump(() => SavedLine(srv.Text, "игрок вошёл"), 5_000, host), "вход хозяина (новый персонаж) не сохранён");
                Pump(() => false, 500, host);
                Assert.False(SavedLine(srv.Text, "автосохранение"));
                Assert.False(SavedLine(srv.Text, "хозяин ушёл"));

                host.Disconnect();
                var w = System.Diagnostics.Stopwatch.StartNew();
                bool saved = SpinWait.SpinUntil(() => SavedLine(srv.Text, "хозяин ушёл"), 10_000);
                _out.WriteLine($"сохранено через {w.Elapsed.TotalSeconds:0.00} с после ухода хозяина");
                Assert.True(saved, "хозяин ушёл, других нет — мир не сохранён сразу");
                Assert.False(srv.Exited); // и ждёт его дальше

                ServerResult r = srv.Stop(30_000);
                Assert.Equal(0, r.ExitCode);
                Assert.False(r.HostLeft);
                Assert.True(r.SavesWritten >= 1);
            }
            finally
            {
                host?.Transport.Dispose();
                Directory.Delete(dir, true);
            }
        }

        /// <summary>
        /// Замечание 3 (мутация П1): замок пишется атомарно — <c>.tmp</c>, сброс на носитель, потом перенос на место
        /// (у Автора нет ИБП). Битый замок (нули после пропажи света, обрывок, мусор, занятый файл) поиск сервера
        /// пропускает, а не падает.
        /// </summary>
        [Fact]
        public void D07_S7_6_HostLockFile_Atomic_FlushedBeforeMoved_BrokenIgnored()
        {
            string dir = Path.Combine(Path.GetTempPath(), "orpheus-hostlock-atomic-" + Guid.NewGuid().ToString("N"));
            var stages = new List<(string Path, PcWriteStage Stage)>();
            var storage = new PcStorage { Probe = (path, stage, at, total) => stages.Add((path, stage)) };
            try
            {
                storage.CreateDirectory(dir);
                string lockPath = Path.GetFullPath(HostLockFile.PathIn(dir));
                var l = new HostLock { Port = 50123, Token = Token.ToString(), ProcessId = 4242, Mode = "host", OpenPort = 50124 };
                HostLockFile.Write(storage, dir, l);
                List<PcWriteStage> mine = stages.Where(s => s.Path == lockPath).Select(s => s.Stage).ToList();
                Assert.Contains(PcWriteStage.Flushed, mine);
                Assert.Equal(PcWriteStage.Moved, mine.Last());
                Assert.True(mine.IndexOf(PcWriteStage.Flushed) < mine.IndexOf(PcWriteStage.Moved), "перенос раньше сброса на носитель");
                Assert.False(File.Exists(lockPath + WorldFolder.TmpSuffix));
                byte[] good = File.ReadAllBytes(lockPath);

                var launcher = new PcServerLauncher(Path.Combine(dir, "OrpheusServer.exe"));
                using (new WorldFolder(storage, dir).Lock())
                {
                    Assert.Equal(50123, launcher.FindRunning(dir)!.Value.Port);

                    var broken = new List<byte[]> { Array.Empty<byte>(), new byte[512], new byte[] { 0xff, 0xfe, 0x00, 0xc3 } };
                    var rnd = new Random(24);
                    for (int i = 0; i < 20; i++)
                    {
                        var b = new byte[rnd.Next(1, 300)];
                        rnd.NextBytes(b);
                        broken.Add(b);
                    }

                    for (int cut = 0; cut < good.Length; cut++)
                    {
                        broken.Add(good.AsSpan(0, cut).ToArray());
                    }

                    foreach (byte[] b in broken)
                    {
                        File.WriteAllBytes(lockPath, b);
                        HostLock? back = HostLockFile.TryRead(storage, dir);
                        ServerEndpoint? e = launcher.FindRunning(dir);
                        if (e.HasValue)
                        {
                            // Обрывок после строки токена: порт и токен те же — подключиться можно; чужих не бывает.
                            Assert.Equal(50123, e.Value.Port);
                            Assert.Equal(Token, e.Value.Token);
                        }

                        Assert.True(back == null || back.Port > 0);
                    }

                    // Замок занят без общего доступа (антивирус) — не исключение, а «не нашёл».
                    File.WriteAllBytes(lockPath, good);
                    using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Assert.Null(HostLockFile.TryRead(storage, dir));
                        Assert.Null(launcher.FindRunning(dir));
                    }

                    Assert.NotNull(launcher.FindRunning(dir));
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        /// <summary>
        /// Замечание 5: версия входа сверяется до разбора остального. Клиент протокола 2 (вход без флага токена) у сервера 3
        /// и клиент 3 у сервера 2 (тот вход не разобрал — «вход испорчен» с версией 0.0.2) видят «Версии не совпадают».
        /// </summary>
        [Fact]
        public void D07_S2_10_Login_VersionCheckedBeforeRest_BothWays()
        {
            var v2 = new NetVersion(0, 0, 2);
            Assert.NotEqual(v2, NetModule.Version);

            // Клиент 2 → сервер 3: вход старого вида (тип, версия, длина имени, имя — без флага токена).
            using (var h = new HostHarness())
            {
                var raw = LoopbackTransport.ConnectTo(h.Primary);
                var rec = new PayloadRec();
                raw.Poll(0, rec);
                var buf = new byte[64];
                var w = new BitWriter(buf);
                w.WriteByte((byte)MessageType.Login);
                v2.Write(ref w);
                byte[] name = System.Text.Encoding.UTF8.GetBytes("старый");
                w.WriteByte((byte)name.Length);
                w.WriteBytes(name);
                int len = w.BytesWritten;
                Assert.Throws<ProtocolException>(() => LoginMessage.Read(buf.AsSpan(0, len))); // целиком сервер 3 его не разберёт
                raw.Send(LoopbackTransport.ServerPeer, NetChannel.Facts, buf.AsSpan(0, len));
                h.Run(3);
                raw.Poll(h.Now, rec);
                Assert.NotNull(rec.Payload);
                LoginRejectMessage reject = LoginRejectMessage.Read(rec.Payload);
                Assert.Equal(LoginRejectReason.VersionMismatch, reject.Reason);
                Assert.Equal(NetModule.Version, reject.ServerVersion);
                Assert.StartsWith("Версии не совпадают", reject.Describe(v2));
                raw.Dispose();
            }

            // Клиент 3 → сервер 2: старый сервер не разобрал вход нового вида и ответил «вход испорчен» со своей версией.
            var oldServer = LoopbackTransport.CreateServer(4);
            var client = new NetClient(LoopbackTransport.ConnectTo(oldServer), LoopbackTransport.ServerPeer, "хост", NetModule.Version) { HostToken = Token };
            var old = new OldServer(oldServer, v2);
            for (int i = 0; i < 5 && client.State != NetClientState.Rejected; i++)
            {
                client.Poll(i * TickMicros);
                oldServer.Poll(i * TickMicros, old);
            }

            client.Poll(10 * TickMicros);
            Assert.True(old.LoginsSeen >= 1);
            Assert.Equal(NetClientState.Rejected, client.State);
            Assert.Equal(LoginRejectReason.BadLogin, client.Reject!.Value.Reason);
            Assert.Equal($"Версии не совпадают: у сервера 0.0.2, у тебя {NetModule.Version}", client.RejectText);
            Assert.Equal("Вход не удался", new LoginRejectMessage { Reason = LoginRejectReason.BadLogin, ServerVersion = NetModule.Version }.Describe(NetModule.Version));
            client.Transport.Dispose();
            oldServer.Dispose();
        }

        private static bool SavedLine(string text, string reason)
        {
            foreach (string line in text.Split('\n'))
            {
                if (line.Contains("\"msg\":\"сохранено\"") && line.Contains($"\"reason\":\"{reason}\""))
                {
                    return true;
                }
            }

            return false;
        }

        private static NetClient UdpClient(int port, string name, HostToken token)
        {
            LiteNetTransport t = LiteNetTransport.Connect("127.0.0.1", port);
            return new NetClient(t, t.ServerPeer, name, NetModule.Version) { HostToken = token };
        }

        /// <summary>Крутить клиентов (опрос и пустой ввод), пока не <paramref name="done"/> или не выйдет время.</summary>
        private static bool Pump(Func<bool> done, int timeoutMs, params NetClient[] clients)
        {
            var w = System.Diagnostics.Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < timeoutMs)
            {
                foreach (NetClient c in clients)
                {
                    c.Poll(w.Elapsed.Ticks / 10);
                    if (c.State == NetClientState.InGame)
                    {
                        c.SendFrame(default);
                    }
                }

                if (done())
                {
                    return true;
                }

                Thread.Sleep(15);
            }

            return done();
        }

        private sealed class PayloadRec : ITransportHandler
        {
            public byte[]? Payload { get; private set; }

            public void OnConnected(int peer)
            {
            }

            public void OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload) => Payload = payload.ToArray();

            public void OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
            }
        }

        /// <summary>Сервер протокола 2 глазами клиента 3: вход нового вида не разобран — «вход испорчен» со своей версией.</summary>
        private sealed class OldServer : ITransportHandler
        {
            private readonly LoopbackTransport _t;
            private readonly NetVersion _version;

            public OldServer(LoopbackTransport t, NetVersion version)
            {
                _t = t;
                _version = version;
            }

            public int LoginsSeen { get; private set; }

            public void OnConnected(int peer)
            {
            }

            public void OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload)
            {
            }

            public void OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
                if (data.Length > 0 && data[0] == (byte)MessageType.Login)
                {
                    LoginsSeen++;
                    var buf = new byte[LoginRejectMessage.Bytes];
                    int n = new LoginRejectMessage { Reason = LoginRejectReason.BadLogin, ServerVersion = _version }.Write(buf);
                    _t.Disconnect(peer, buf.AsSpan(0, n));
                }
            }
        }

        /// <summary>
        /// Сервер игры целиком в процессе теста (в своём потоке): UDP только на петле, «Открыть для друзей» — на 127.0.0.2.
        /// </summary>
        private sealed class InProcServer : IDisposable
        {
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly Thread _thread;
            private readonly LockedWriter _console = new LockedWriter();
            private ServerResult? _result;
            private Exception? _error;

            public InProcServer(string dir, params string[] extra)
            {
                var args = new List<string>
                {
                    "--world", dir, "--create", "--seed", "11", "--size", "1024", "--port", "0", "--mode", "host",
                    "--host-token", Token.ToString(), "--open-address", "127.0.0.2", "--open-port", "0",
                };
                args.AddRange(extra);
                ServerOptions o = ServerOptions.Parse(args.ToArray());
                _thread = new Thread(() =>
                {
                    try
                    {
                        _result = ServerRun.Run(o, _console, _stop.Token);
                    }
                    catch (Exception e)
                    {
                        _error = e;
                    }
                })
                { IsBackground = true, Name = "server" };
                _thread.Start();
            }

            public string Text => _console.Text;

            public bool Exited => !_thread.IsAlive;

            public int WaitReady(int timeoutMs)
            {
                string marker = ServerRun.ReadyMarker + " port=";
                int port = -1;
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    string t = Text;
                    int at = t.IndexOf(marker, StringComparison.Ordinal);
                    if (at < 0)
                    {
                        return Exited;
                    }

                    int end = t.IndexOfAny(new[] { '\r', '\n' }, at);
                    port = int.Parse(t.Substring(at + marker.Length, end - at - marker.Length), System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }, timeoutMs), "сервер не готов");
                Assert.True(port > 0, $"сервер вышел до готовности: {_error}\n{Text}");
                return port;
            }

            public ServerResult Stop(int timeoutMs)
            {
                _stop.Cancel();
                Assert.True(_thread.Join(timeoutMs), "сервер не остановился");
                Assert.Null(_error);
                return _result!;
            }

            public void Dispose()
            {
                _stop.Cancel();
                _thread.Join(30_000);
                _stop.Dispose();
            }
        }

        /// <summary>Вывод сервера: пишут поток такта и фоновый поток замка, читает тест.</summary>
        private sealed class LockedWriter : TextWriter
        {
            private readonly System.Text.StringBuilder _sb = new System.Text.StringBuilder();

            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

            public string Text
            {
                get
                {
                    lock (_sb)
                    {
                        return _sb.ToString();
                    }
                }
            }

            public override void Write(char value)
            {
                lock (_sb)
                {
                    _sb.Append(value);
                }
            }

            public override void Write(string? value)
            {
                lock (_sb)
                {
                    _sb.Append(value);
                }
            }

            public override void WriteLine(string? value)
            {
                lock (_sb)
                {
                    _sb.Append(value).Append('\n');
                }
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
