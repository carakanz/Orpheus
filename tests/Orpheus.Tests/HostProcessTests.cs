using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using Orpheus.Bots;
using Orpheus.Net;
using Orpheus.Net.Hosting;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Platform.Pc;
using Orpheus.Save;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Сервер одиночной игры и хоста дочерним процессом (карточка 0-024; docs/07 2.3, 2.13 п. 9, 1.9 п. 4 и 7, 7.6):
    /// собранный <c>OrpheusServer</c> через <see cref="PcServerLauncher"/>; «игра хоста» — процесс-бот <c>Orpheus.Bots</c>
    /// с токеном хозяина, друг — бот в процессе теста. «Открыть для друзей» — на втором адресе петли 127.0.0.2: настоящий
    /// бинд на все адреса вызывает окно брандмауэра Windows (ручная проверка Автора). Свои процессы снимаются в
    /// <c>finally</c> по номеру — только свои.
    /// </summary>
    public class HostProcessTests : IDisposable
    {
        private const string OpenAddress = "127.0.0.2";

        private static readonly string ServerExe = PcServerLauncher.DefaultPath(AppContext.BaseDirectory);
        private static readonly string BotsExe = Path.Combine(AppContext.BaseDirectory, Path.DirectorySeparatorChar == '\\' ? "Orpheus.Bots.exe" : "Orpheus.Bots");

        private readonly ITestOutputHelper _out;
        private readonly string _root;
        private readonly string _world;
        private readonly string _logs;
        private readonly List<int> _serverPids = new List<int>();

        public HostProcessTests(ITestOutputHelper output)
        {
            _out = output;
            _root = Path.Combine(Path.GetTempPath(), "orpheus-host-" + Guid.NewGuid().ToString("N"));
            _world = Path.Combine(_root, "world");
            _logs = Path.Combine(_root, "logs");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            foreach (int pid in _serverPids)
            {
                KillServer(pid);
            }

            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // ---------- 2.13 п. 9: одиночная → «Открыть для друзей» → второй клиент; мир не перезапускался ----------

        [Fact]
        public void D07_S2_13_9_Launcher_SingleThenOpen_FriendJoins_NoRestart()
        {
            var launcher = new PcServerLauncher(ServerExe);
            var launch = NewLaunch(ServerMode.Single, grace: 60);
            IServerInstance server = launcher.Start(launch);
            _serverPids.Add(server.Endpoint.ProcessId);
            NetClient? host = null;
            NetClient? friend = null;
            try
            {
                _out.WriteLine($"сервер готов за {server.ReadySeconds:0.000} с (1024², создание мира), порт {server.Endpoint.Port}");
                Assert.True(server.ReadySeconds <= 5, $"старт {server.ReadySeconds:0.00} с > 5 с (1.9 п. 4)");
                AssertListening(server.Endpoint.Port, a => IPAddress.IsLoopback(a), "до «Открыть для друзей» — только петля");
                using (Process sp = Process.GetProcessById(server.Endpoint.ProcessId))
                {
                    double mb = sp.WorkingSet64 / 1048576.0;
                    _out.WriteLine($"память сервера после старта: рабочий набор {mb:0} МБ, частная {sp.PrivateMemorySize64 / 1048576.0:0} МБ");
                    Assert.True(mb <= 128, $"сервер занимает {mb:0} МБ > 128 (1.9 п. 4)");
                }

                host = Client("127.0.0.1", server.Endpoint.Port, "хост", server.Endpoint.Token);
                var hostBot = new NetBot(host, "wander", 0);
                NetBot? friendBot = null;
                var watch = Stopwatch.StartNew();
                bool openSent = false;
                uint hostTickAtOpen = 0;
                double friendInAt = -1;
                long hostTickWentBack = 0;
                uint lastHostTick = 0;
                while (watch.Elapsed.TotalSeconds < 20)
                {
                    long now = watch.Elapsed.Ticks / 10;
                    hostBot.Frame(now);
                    friendBot?.Frame(now);
                    if (host.HasSnapshot)
                    {
                        if (host.Latest.Tick < lastHostTick)
                        {
                            hostTickWentBack++;
                        }

                        lastHostTick = host.Latest.Tick;
                    }

                    if (!openSent && host.State == NetClientState.InGame && host.SnapshotsApplied > 30)
                    {
                        hostTickAtOpen = host.Latest.Tick;
                        host.RequestOpenForFriends();
                        openSent = true;
                    }

                    if (friend == null && host.OpenResult.HasValue)
                    {
                        Assert.True(host.OpenResult.Value.Opened, "сервер не открылся");
                        friend = Client(OpenAddress, host.OpenResult.Value.Port, "друг", default);
                        friendBot = new NetBot(friend, "wander", 1);
                    }

                    if (friend != null && friend.State == NetClientState.InGame && friendInAt < 0)
                    {
                        friendInAt = watch.Elapsed.TotalSeconds;
                    }

                    if (friendInAt >= 0 && watch.Elapsed.TotalSeconds > friendInAt + 2)
                    {
                        break;
                    }

                    Assert.True(host.State != NetClientState.Disconnected && host.State != NetClientState.Rejected, $"хост отключён: {host.RejectText}");
                    Thread.Sleep(15);
                }

                Assert.NotNull(friend);
                Assert.Equal(NetClientState.InGame, friend!.State);
                Assert.Equal(NetClientState.InGame, host.State);
                int openPort = host.OpenResult!.Value.Port;
                AssertListening(openPort, a => a.Equals(IPAddress.Parse(OpenAddress)), "«Открыть для друзей» — на заданном адресе");
                Assert.True(friend.Accept.ServerTick > hostTickAtOpen, $"такт входа друга {friend.Accept.ServerTick} ≤ {hostTickAtOpen}");
                Assert.Equal(host.Accept.GenerationHash, friend.Accept.GenerationHash);
                Assert.Equal(0, hostTickWentBack);
                Assert.True(friend.Latest.TryFind(host.EntityId, out _), "друг не видит хоста");
                Assert.False(server.HasExited);
                _out.WriteLine($"открыт порт {openPort} на {OpenAddress}; друг вошёл на такте {friend.Accept.ServerTick} (хост открыл на {hostTickAtOpen})");

                // Штатная остановка игрой: мир сохранён, всем — «Сервер остановлен», файл-замок убран.
                server.Stop(15_000);
                Assert.Equal(0, server.ExitCode);
                PumpUntil(() => friend.State == NetClientState.Disconnected && host.State == NetClientState.Disconnected, 3000, host, friend);
                Assert.Equal(ClosingReason.Stopped, friend.Closing);
                Assert.Equal(ClosingReason.Stopped, host.Closing);
                Assert.False(File.Exists(HostLockFile.PathIn(_world)));

                List<JsonElement> log = ReadLog();
                Assert.Single(log, e => Msg(e).StartsWith("старт:", StringComparison.Ordinal));
                Assert.Single(log, e => Msg(e) == "открыто для друзей: слушаю UDP");
            }
            finally
            {
                host?.Transport.Dispose();
                friend?.Transport.Dispose();
                server.Dispose();
            }
        }

        // ---------- 1.9 п. 7: игра хоста убита — друг играет; хост вернулся по файлу-замку в тот же мир ----------

        [Fact]
        [Trait("Category", "Big")]
        public void D07_S1_9_7_HostKilled_FriendPlays_HostReturnsByLock_Big()
        {
            using Child hostGame = StartHostGame(grace: 60);
            int serverPid = ServerPidOf(hostGame);
            int openPort = OpenPortOf(hostGame);

            FriendRun friend = Friend(openPort, seconds: 14);
            Thread.Sleep(2500);

            var killed = Stopwatch.StartNew();
            hostGame.Kill(); // только процесс игры — сервер, его дочерний процесс, живёт (2.3)
            Assert.True(Process.GetProcessById(serverPid) != null);
            Thread.Sleep(2000);
            Assert.False(friend.Done, "друг вышел, пока хоста не было");

            // Игра хоста перезапущена: находит сервер по host.lock и входит хозяином — соединение упавшей ещё висит
            // до тайм-аута, новый вход его вытесняет.
            using Child back = Child.Start(BotsExe, new[] { "--find", _world, "--count", "1", "--seconds", "4", "--name", "host" });
            string found = back.WaitLine("found ", 15_000);
            double returnSeconds = killed.Elapsed.TotalSeconds;
            Assert.Contains($"pid={serverPid} ", found + " ");
            Assert.True(back.WaitExit(30_000), "вернувшийся хост не вышел");
            Assert.Equal(0, back.ExitCode);
            _out.WriteLine($"хост вернулся через {returnSeconds:0.0} с после падения; {found}");
            Assert.True(returnSeconds <= 60);

            BotReport r = friend.Result(60_000);
            Assert.Equal(1, r.InGame);
            Assert.Equal(0, r.Disconnects);
            Assert.True(r.Bots[0].SecondsInGame > 12, $"{r.Bots[0].SecondsInGame}");

            List<JsonElement> log = ReadLog();
            Assert.Single(log, e => Msg(e).StartsWith("старт:", StringComparison.Ordinal));
            Assert.Contains(log, e => Msg(e) == "хозяин вернулся");
            Assert.False(Process.GetProcessById(serverPid).HasExited);
        }

        // ---------- 2.3 / 7.6: хост не вернулся — сохранение и выход через отведённое время ----------

        /// <summary>
        /// Короткий вариант (12 с): обрыв убитой игры транспорт замечает через тайм-аут 10 с (2.10), поэтому меньше 12 с ожидание
        /// не проверить; оба варианта — в ночном прогоне, в CI — <c>HostTests.D07_S2_3_ServerRun_HostNeverJoins_*</c> (1 с).
        /// </summary>
        [Fact]
        [Trait("Category", "Big")]
        public void D07_S2_3_HostNotBack_SaveAndExitAfter12s_Big()
        {
            HostNotBack(grace: 12, tolerance: 1.0);
        }

        [Fact]
        [Trait("Category", "Big")]
        public void D07_S2_3_HostNotBack_SaveAndExitIn60s_Big()
        {
            HostNotBack(grace: 60, tolerance: 1.0);
        }

        private void HostNotBack(int grace, double tolerance)
        {
            using Child hostGame = StartHostGame(grace);
            int serverPid = ServerPidOf(hostGame);
            int openPort = OpenPortOf(hostGame);
            using Process server = Process.GetProcessById(serverPid);

            FriendRun friend = Friend(openPort, seconds: grace + 20);
            Thread.Sleep(2000);
            var killed = Stopwatch.StartNew();
            DateTime killedAt = DateTime.UtcNow;
            hostGame.Kill();
            double killMs = killed.Elapsed.TotalMilliseconds;

            Assert.True(server.WaitForExit((grace + 15) * 1000), $"сервер не вышел за {grace + 15} с");
            double exitSeconds = killed.Elapsed.TotalSeconds;
            _out.WriteLine($"игра хоста убита → сервер вышел через {exitSeconds:0.00} с (ожидание {grace} с)");

            BotReport r = friend.Result(60_000);
            Assert.Equal("Хост вышел. Мир сохранён у него", r.DisconnectText);
            Assert.True(r.Bots[0].SecondsInGame >= grace, $"друг играл {r.Bots[0].SecondsInGame:0.0} с");

            // Поколение сохранения целое: проверка при старте не находит повреждений, новейшее загружается.
            var storage = new PcStorage();
            var folder = new WorldFolder(storage, _world);
            WorldRecovery rec = folder.Recover();
            Assert.Empty(rec.Damaged);
            Assert.False(rec.PromotedTmp);
            Assert.NotNull(rec.Latest);
            Assert.False(File.Exists(HostLockFile.PathIn(_world)), "host.lock остался после штатного выхода");

            List<JsonElement> log = ReadLog();
            JsonElement left = log.First(e => Msg(e) == "хозяин ушёл — ждём его");
            DateTime leftAt = left.GetProperty("time").GetDateTime();
            double silentMs = left.GetProperty("data").GetProperty("silentMs").GetDouble();
            _out.WriteLine($"Kill занял {killMs:0} мс; последнее сообщение хозяина — через {(leftAt.AddMilliseconds(-silentMs) - killedAt).TotalMilliseconds:0} мс после Kill; обрыв замечен через {(leftAt - killedAt).TotalSeconds:0.00} с; " +
                $"последняя строка лога — через {(log[log.Count - 1].GetProperty("time").GetDateTime() - killedAt).TotalSeconds:0.00} с");
            Assert.InRange(exitSeconds, grace - tolerance, grace + tolerance);
            Assert.Single(log, e => Msg(e).StartsWith("старт:", StringComparison.Ordinal));
            Assert.Contains(log, e => Msg(e) == "хост не вернулся — закрываю мир");
            Assert.Contains(log, e => Msg(e) == "хост вышел: мир сохранён, игроки отключены"); // код выхода чужого процесса не узнать — штатный путь по логу
            Assert.DoesNotContain(log, e => e.GetProperty("level").GetString() == "error");
            // Такт и хэш шли подряд: точки хэша — 300, 600, … без пропусков и повторов.
            long[] hashTicks = log.Where(e => Msg(e) == "хэш").Select(e => e.GetProperty("tick").GetInt64()).ToArray();
            for (int i = 0; i < hashTicks.Length; i++)
            {
                Assert.Equal(300L * (i + 1), hashTicks[i]);
            }

            _out.WriteLine($"сохранение {rec.LatestName}; точек хэша подряд: {hashTicks.Length}");
        }

        // ---------- Помощники ----------

        private ServerLaunch NewLaunch(ServerMode mode, int grace)
        {
            var l = new ServerLaunch
            {
                WorldDir = _world,
                Create = true,
                Seed = 2024,
                Size = 1024,
                Mode = mode,
                OpenAddress = OpenAddress,
                OpenPort = 0,
                HostGraceSeconds = grace,
            };
            l.ExtraArgs.AddRange(new[] { "--hash", "--logs", _logs });
            return l;
        }

        /// <summary>«Игра хоста» — процесс-бот: запускает сервер лаунчером, входит хозяином, жмёт «Открыть для друзей».</summary>
        private Child StartHostGame(int grace)
        {
            var args = new List<string>
            {
                "--launch", ServerExe, "--world", _world, "--create-seed", "2024", "--mode", "host",
                "--open-address", OpenAddress, "--open-port", "0", "--host-grace", grace.ToString(CultureInfo.InvariantCulture),
                "--server-arg", "--hash", "--server-arg", "--logs", "--server-arg", _logs,
                "--open-for-friends", "--count", "1", "--seconds", "600", "--name", "host",
            };
            return Child.Start(BotsExe, args);
        }

        private int ServerPidOf(Child hostGame)
        {
            string line = hostGame.WaitLine("server pid=", 30_000);
            int pid = int.Parse(Field(line, "pid"), CultureInfo.InvariantCulture);
            _serverPids.Add(pid);
            _out.WriteLine(line);
            return pid;
        }

        private int OpenPortOf(Child hostGame)
        {
            string line = hostGame.WaitLine("open-result ", 15_000);
            Assert.Equal("1", Field(line, "opened"));
            return int.Parse(Field(line, "port"), CultureInfo.InvariantCulture);
        }

        private static FriendRun Friend(int port, double seconds)
        {
            var o = BotOptions.Parse(new[] { "--connect", $"{OpenAddress}:{port}", "--count", "1", "--name", "friend", "--seconds", seconds.ToString(CultureInfo.InvariantCulture) });
            return new FriendRun(o);
        }

        /// <summary>Друг — бот в потоке теста (по UDP на 127.0.0.2).</summary>
        private sealed class FriendRun
        {
            private readonly Thread _thread;
            private BotReport? _report;
            private Exception? _error;

            public FriendRun(BotOptions o)
            {
                _thread = new Thread(() =>
                {
                    try
                    {
                        _report = BotRun.Run(o, null);
                    }
                    catch (Exception e)
                    {
                        _error = e;
                    }
                })
                {
                    IsBackground = true,
                    Name = "друг",
                };
                _thread.Start();
            }

            public bool Done => !_thread.IsAlive;

            public BotReport Result(int timeoutMs)
            {
                Assert.True(_thread.Join(timeoutMs), $"друг не закончил за {timeoutMs} мс");
                if (_error != null)
                {
                    throw new InvalidOperationException("друг упал: " + _error.Message, _error);
                }

                return _report!;
            }
        }

        private static NetClient Client(string host, int port, string name, HostToken token)
        {
            LiteNetTransport t = LiteNetTransport.Connect(host, port);
            return new NetClient(t, t.ServerPeer, name, NetModule.Version) { HostToken = token };
        }

        private static void PumpUntil(Func<bool> done, int timeoutMs, params NetClient[] clients)
        {
            var w = Stopwatch.StartNew();
            while (!done() && w.ElapsedMilliseconds < timeoutMs)
            {
                foreach (NetClient c in clients)
                {
                    c.Poll(w.Elapsed.Ticks / 10);
                }

                Thread.Sleep(10);
            }
        }

        private static void AssertListening(int port, Func<IPAddress, bool> ok, string what)
        {
            IPEndPoint[] mine = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
                .Where(e => e.Port == port).ToArray();
            Assert.NotEmpty(mine);
            Assert.All(mine, e => Assert.True(ok(e.Address), $"{what}: порт {port} слушает {e.Address}"));
        }

        private static string Field(string line, string key)
        {
            foreach (string part in line.Split(' '))
            {
                if (part.StartsWith(key + "=", StringComparison.Ordinal))
                {
                    return part.Substring(key.Length + 1);
                }
            }

            throw new InvalidOperationException($"нет {key} в «{line}»");
        }

        private List<JsonElement> ReadLog()
        {
            var list = new List<JsonElement>();
            foreach (string file in Directory.GetFiles(_logs, "*.jsonl"))
            {
                using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(f, Encoding.UTF8);
                string? line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.Length > 0)
                    {
                        list.Add(JsonDocument.Parse(line).RootElement.Clone());
                    }
                }
            }

            return list;
        }

        private static string Msg(JsonElement e) => e.GetProperty("msg").GetString() ?? "";

        /// <summary>Снять сервер по номеру — только если это действительно наш OrpheusServer (номер мог перейти к другому).</summary>
        private static void KillServer(int pid)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                if (!p.HasExited && p.ProcessName.StartsWith("OrpheusServer", StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch (ArgumentException)
            {
                // уже вышел
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>Дочерний процесс теста: строки stdout в очередь; снимается только он сам (не дерево).</summary>
        private sealed class Child : IDisposable
        {
            private readonly Process _p;
            private readonly BlockingCollection<string> _lines = new BlockingCollection<string>();
            private readonly List<string> _seen = new List<string>();

            private Child(Process p)
            {
                _p = p;
            }

            public int ExitCode => _p.ExitCode;

            public static Child Start(string exe, IEnumerable<string> args)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                };
                foreach (string a in args)
                {
                    psi.ArgumentList.Add(a);
                }

                var p = new Process { StartInfo = psi };
                var c = new Child(p);
                p.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        c._lines.Add(e.Data);
                    }
                };
                p.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        c._lines.Add("stderr: " + e.Data);
                    }
                };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                return c;
            }

            public string WaitLine(string prefix, int timeoutMs)
            {
                var w = Stopwatch.StartNew();
                while (true)
                {
                    int left = (int)Math.Max(0, timeoutMs - w.ElapsedMilliseconds);
                    Assert.True(_lines.TryTake(out string? line, left), $"нет строки «{prefix}» за {timeoutMs} мс: {string.Join(" | ", _seen)}");
                    _seen.Add(line!);
                    if (line!.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return line;
                    }
                }
            }

            /// <summary>Ждать выхода (без ожидания конца вывода: его трубу держит и унаследовавший её сервер).</summary>
            public bool WaitExit(int timeoutMs) => _p.WaitForExit(timeoutMs);

            public void Kill()
            {
                if (!_p.HasExited)
                {
                    _p.Kill(entireProcessTree: false);
                    _p.WaitForExit(5000);
                }
            }

            public void Dispose()
            {
                try
                {
                    Kill();
                }
                catch (InvalidOperationException)
                {
                }

                _p.Dispose();
            }
        }
    }
}
