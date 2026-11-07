using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Net;
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
    /// Режим «сервер в потоке» (0-031; docs/07 1.6 «Сервер на консоли», 1.9 п. 5, 2.3 «На консоли»): <see cref="ServerHost"/>
    /// из библиотеки netstandard2.1 в своём потоке, клиент — тот же <see cref="NetClient"/> через локальную связь; сохранения и
    /// загрузка — только через <see cref="IPlatformStorage"/> (на ПК — <see cref="PcStorage"/>; консоль — хранилище платформы,
    /// здесь — в памяти). CI без Unity (приёмка 0.1); клиент Unity в потоке — smoke сборки (<c>build_client.ps1 -ThreadSmoke</c>).
    /// </summary>
    public sealed class ServerThreadTests : IDisposable
    {
        /// <summary>Ускорение часов теста: такт 16,7 мс вместо 33,3 — 300 тактов (10 с мира) за 5 с.</summary>
        private const int Speed = 2;

        private const int Ticks = 300;

        private readonly ITestOutputHelper _out;
        private readonly string _dir;

        public ServerThreadTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "orpheus-thread-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        /// <summary>
        /// Приёмка 0.12 без Unity (`07` 1.9 п. 5 — часть CI): сервер в потоке + клиент по локальной связи, 300 тактов по часам
        /// (ускорено ×2) — такт держится (ни одного «перегрузка»: отставание ≤ 3 тактов), клиент в игре и получает снимки;
        /// автосохранения и сохранение при остановке — через хранилище платформы; загрузка заново — тот же хэш мира.
        /// </summary>
        [Theory]
        [InlineData("pc")]
        [InlineData("memory")]
        public void D07_S1_9_5_ServerThread_LoopbackClient_TickHolds_SaveLoadThroughStorage(string storageKind)
        {
            MemoryStorage? memory = storageKind == "memory" ? new MemoryStorage() : null;
            IPlatformStorage storage = memory ?? (IPlatformStorage)new PcStorage();
            string world = Path.Combine(_dir, "world-1"); // у хранилища в памяти этой папки на диске нет вовсе
            var log = new CaptureLog();
            LoopbackTransport serverLink = LoopbackTransport.CreateServer(4);
            var services = new ServerHostServices
            {
                Log = log,
                Storage = storage,
                CreateClock = rate => new PcTickClock(rate * Speed),
                LocalTransport = serverLink,
            };
            ServerOptions o = ServerOptions.Parse(new[]
            {
                "--world", world, "--create", "--seed", "31", "--size", "1024", "--autosave", "3", "--bots", "2:wander", "--metrics", "0",
            });

            var wall = Stopwatch.StartNew();
            ServerResult first;
            long snapshots;
            using (ServerThread host = ServerThread.Start(o, services))
            {
                while (!host.IsReady && !host.HasExited && wall.Elapsed.TotalSeconds < 30)
                {
                    Thread.Sleep(5);
                }

                Assert.True(host.IsReady, $"сервер в потоке не готов: {host.Error?.Message} {string.Join(" | ", log.Errors)}");
                Assert.Equal(0, host.Port);
                double readySec = wall.Elapsed.TotalSeconds;

                using var clientLink = LoopbackTransport.ConnectTo(serverLink);
                var client = new NetClient(clientLink, LoopbackTransport.ServerPeer, "одиночка", NetModule.Version);
                var clock = Stopwatch.StartNew();
                long inGameAt = -1;
                while (clock.Elapsed.TotalSeconds < Ticks / (double)(Simulation.TickRate * Speed) + 1 && !host.HasExited)
                {
                    long now = clock.Elapsed.Ticks / 10;
                    client.Poll(now);
                    if (client.State == NetClientState.InGame)
                    {
                        inGameAt = inGameAt < 0 ? now : inGameAt;
                        int n = client.FramesDue(now);
                        for (int k = 0; k < n; k++)
                        {
                            client.SendFrame(new InputFrame(Input8.FromStick((k & 1) == 0 ? 1024 : 0, 1024)));
                        }
                    }

                    Thread.Sleep(2);
                }

                Assert.Equal(NetClientState.InGame, client.State);
                snapshots = client.SnapshotsApplied;
                client.Disconnect();
                Assert.True(host.Stop(30_000), "сервер в потоке не остановился");
                first = host.Result ?? throw new Xunit.Sdk.XunitException("нет итога: " + host.Error);
                _out.WriteLine($"[{storageKind}] готов за {readySec:0.00} с, в игре с {inGameAt / 1e6:0.00} с; тактов {first.Ticks} за {first.Seconds:0.00} с " +
                               $"(×{first.SpeedX:0.00}), снимков у клиента {snapshots}; такт среднее {first.Metrics?.TickMeanMs:0.000} мс, " +
                               $"p99 {first.Metrics?.TickP99Ms:0.000}, max {first.Metrics?.TickMaxMs:0.000}, max разрыв {first.Metrics?.MaxGapMs:0.0} мс; " +
                               $"сохранений {first.SavesWritten}, последнее №{first.LastSavedNumber}, хэш {first.FinalHash:x16}");
            }

            Assert.Equal(0, first.ExitCode);
            Assert.Empty(log.Errors);
            Assert.DoesNotContain(log.Warnings, w => w.Contains("перегрузка", StringComparison.Ordinal)); // отставание ≤ 3 тактов
            Assert.InRange(first.Ticks, Ticks * 8 / 10, Ticks * 2);
            Assert.True(first.LoginsAccepted == 1, $"входов {first.LoginsAccepted}");
            Assert.True(snapshots > first.Ticks / 2, $"снимков {snapshots} за {first.Ticks} тактов");
            Assert.True(first.SavesWritten >= 2, $"сохранений {first.SavesWritten}"); // автосохранение раз в 3 с мира + при остановке
            if (memory != null)
            {
                Assert.False(Directory.Exists(world), "сохранение мимо хранилища: папка мира появилась на диске");
                Assert.Contains(memory.ListFiles(world), f => f.StartsWith(WorldFolder.GenerationPrefix, StringComparison.Ordinal));
            }

            // Загрузка заново — новый хост, то же хранилище: поколение последнее, хэш состояния тот же.
            var log2 = new CaptureLog();
            ServerOptions o2 = ServerOptions.Parse(new[] { "--world", world, "--ticks", "0" });
            ServerResult second = ServerHost.Run(o2, new ServerHostServices { Log = log2, Storage = storage });
            _out.WriteLine($"[{storageKind}] загрузка: поколение №{second.LoadedNumber}, такт {second.StartTick}, хэш {second.LoadedHash:x16}");
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(first.LastSavedNumber, second.LoadedNumber);
            Assert.Equal(first.FinalHash, second.LoadedHash);
            Assert.Equal(first.FinalHash, second.FinalHash);
            Assert.Equal(0, second.SavesWritten); // загруженное и не изменившееся состояние не пишется заново
        }

        /// <summary>
        /// Правила сборки ServerHost (0-031, `07` 1.3 и 1.6): netstandard2.1; без Unity, System.Text.Json, генерации кода; в
        /// исходниках — без рефлексии, P/Invoke, Console, Process и прямых файлов ОС (File/Directory/FileStream): файлы мира —
        /// только через <see cref="IPlatformStorage"/>. То же правило «файлы только через хранилище» — для Orpheus.Save.
        /// </summary>
        [Fact]
        public void D07_S1_6_ServerHost_CoreRules_NoPlatformCalls()
        {
            Assembly asm = typeof(ServerHost).Assembly;
            Assert.Equal("Orpheus.ServerHost", asm.GetName().Name);
            Assert.Equal(".NETStandard,Version=v2.1", asm.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
            foreach (AssemblyName reference in asm.GetReferencedAssemblies())
            {
                string name = reference.Name ?? string.Empty;
                foreach (string prefix in new[] { "UnityEngine", "UnityEditor", "Unity.", "System.Text.Json", "System.Reflection.Emit", "Newtonsoft.Json", "Orpheus.Platform" })
                {
                    Assert.False(name.StartsWith(prefix, StringComparison.Ordinal), $"ServerHost ссылается на {name}");
                }
            }

            var forbidden = new Regex(
                @"UnityEngine|System\.Reflection|System\.Text\.Json|DllImport|extern\s|\bConsole\.|System\.Diagnostics\.Process|\bProcess\.|" +
                @"\bFile\.|\bDirectory\.|\bFileStream\b|\bFileInfo\b|\bDirectoryInfo\b|\bStreamWriter\b",
                RegexOptions.CultureInvariant);
            int files = 0;
            foreach (string project in new[] { "Orpheus.ServerHost", "Orpheus.Save" })
            {
                foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", project), "*.cs", SearchOption.AllDirectories))
                {
                    if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    files++;
                    string[] lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string code = StripComment(lines[i]);
                        Match m = forbidden.Match(code);
                        Assert.False(m.Success, $"{file}:{i + 1}: «{m.Value}» — платформенный вызов вне платформенного слоя: {lines[i].Trim()}");
                    }
                }
            }

            Assert.True(files >= 15, $"файлов {files}");
        }

        private static string StripComment(string line)
        {
            int c = line.IndexOf("//", StringComparison.Ordinal);
            return c >= 0 ? line.Substring(0, c) : line;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Orpheus.sln")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!.FullName;
        }

        /// <summary>Лог теста: строки ошибок и предупреждений.</summary>
        private sealed class CaptureLog : IServerLog
        {
            private readonly object _sync = new object();

            public List<string> Errors { get; } = new List<string>();

            public List<string> Warnings { get; } = new List<string>();

            public bool Enabled(string cat, LogLevel level) => level >= LogLevel.Info;

            public void Write(LogLevel level, long tick, string cat, string msg, (string Key, object? Value)[] data)
            {
                lock (_sync)
                {
                    if (level == LogLevel.Error)
                    {
                        Errors.Add($"{tick} {cat} {msg}");
                    }
                    else if (level == LogLevel.Warn)
                    {
                        Warnings.Add($"{tick} {cat} {msg}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Хранилище платформы в памяти (0-031): так выглядит сохранение на консоли — API платформы, а не файлы ОС. Атомарность
    /// записи — по построению (замена целого массива под блокировкой). Пути — строки; папки — множество.
    /// </summary>
    internal sealed class MemoryStorage : IPlatformStorage
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly HashSet<string> _dirs = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _locks = new HashSet<string>(StringComparer.Ordinal);

        public int Writes { get; private set; }

        public bool FileExists(string path)
        {
            lock (_sync)
            {
                return _files.ContainsKey(Norm(path));
            }
        }

        public bool DirectoryExists(string path)
        {
            lock (_sync)
            {
                return _dirs.Contains(Norm(path));
            }
        }

        public void CreateDirectory(string path)
        {
            lock (_sync)
            {
                for (string? d = Norm(path); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                {
                    _dirs.Add(d);
                }
            }
        }

        public string[] ListFiles(string directory)
        {
            string dir = Norm(directory);
            var names = new List<string>();
            lock (_sync)
            {
                foreach (string path in _files.Keys)
                {
                    if (string.Equals(Path.GetDirectoryName(path), dir, StringComparison.Ordinal))
                    {
                        names.Add(Path.GetFileName(path));
                    }
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
        }

        public byte[] ReadAllBytes(string path)
        {
            lock (_sync)
            {
                return _files.TryGetValue(Norm(path), out byte[]? data)
                    ? (byte[])data.Clone()
                    : throw new FileNotFoundException("Нет файла в хранилище: " + path, path);
            }
        }

        public void WriteAtomic(string path, ReadOnlySpan<byte> data)
        {
            byte[] copy = data.ToArray();
            lock (_sync)
            {
                RequireDir(path);
                _files[Norm(path)] = copy;
                Writes++;
            }
        }

        public void MoveDurable(string from, string to, bool replaceExisting)
        {
            lock (_sync)
            {
                string f = Norm(from), t = Norm(to);
                if (!_files.TryGetValue(f, out byte[]? data))
                {
                    throw new FileNotFoundException("Нет файла в хранилище: " + from, from);
                }

                if (!replaceExisting && _files.ContainsKey(t))
                {
                    throw new IOException("Уже есть: " + to);
                }

                RequireDir(to);
                _files.Remove(f);
                _files[t] = data;
            }
        }

        public void Delete(string path)
        {
            lock (_sync)
            {
                _files.Remove(Norm(path));
            }
        }

        public IDisposable? TryLock(string path)
        {
            string p = Norm(path);
            lock (_sync)
            {
                RequireDir(path);
                if (!_locks.Add(p))
                {
                    return null;
                }
            }

            return new Release(this, p);
        }

        private void RequireDir(string path)
        {
            string? dir = Path.GetDirectoryName(Norm(path));
            if (!string.IsNullOrEmpty(dir) && !_dirs.Contains(dir))
            {
                throw new DirectoryNotFoundException("Нет папки в хранилище: " + dir);
            }
        }

        private static string Norm(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

        private sealed class Release : IDisposable
        {
            private readonly MemoryStorage _owner;
            private readonly string _path;

            public Release(MemoryStorage owner, string path)
            {
                _owner = owner;
                _path = path;
            }

            public void Dispose()
            {
                lock (_owner._sync)
                {
                    _owner._locks.Remove(_path);
                }
            }
        }
    }
}
