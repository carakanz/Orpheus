using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Orpheus.Net.Hosting;
using Orpheus.Net.Protocol;
using Orpheus.Save;

namespace Orpheus.Platform.Pc
{
    /// <summary>
    /// Запуск <c>OrpheusServer</c> дочерним процессом (docs/07, 1.6 и 2.3) — реализация «ПК» <see cref="IServerLauncher"/>.
    /// <c>System.Diagnostics.Process</c> — только здесь. Готовность — по строке <c>orpheus-ready port=P</c> в выводе
    /// сервера (порт 0 → выбранный ОС порт), без пауз. Вывод читается до конца жизни процесса (иначе полная труба
    /// остановила бы такт); упала игра — труба закрывается, сервер живёт дальше (2.3), лог — в файле папки мира.
    /// </summary>
    public sealed class PcServerLauncher : IServerLauncher
    {
        /// <summary>Строка готовности сервера (<c>ServerRun.ReadyMarker</c>).</summary>
        public const string ReadyMarker = "orpheus-ready";

        private readonly PcStorage _storage = new PcStorage();

        /// <param name="serverPath">
        /// <c>OrpheusServer.exe</c> (рядом со сборкой клиента — <c>tools/build_client.ps1</c>) или <c>OrpheusServer.dll</c>
        /// (тогда через <c>dotnet</c>).
        /// </param>
        /// <param name="hiddenDesktop">
        /// C-022: сервер — на невидимом рабочем столе (<see cref="HiddenProcess"/>; тесты и инструменты — всегда так, чтобы не
        /// мигать экраном Автора). Игра (<c>LocalServer</c>) — <c>false</c>: сервер переживает игру (2.3), а стол живёт,
        /// пока его держит игра или кто-то на нём; сервер без окна (<c>CreateNoWindow</c>) на столе игрока.
        /// </param>
        public PcServerLauncher(string serverPath, bool hiddenDesktop = false)
        {
            ServerPath = Path.GetFullPath(serverPath ?? throw new ArgumentNullException(nameof(serverPath)));
            HiddenDesktop = hiddenDesktop && HiddenProcess.IsSupported;
        }

        public string ServerPath { get; }

        /// <summary>Запуск на невидимом столе (только Windows).</summary>
        public bool HiddenDesktop { get; }

        /// <summary><c>OrpheusServer.exe</c> (или <c>.dll</c>) в папке <paramref name="dir"/>; нет ни того, ни другого — путь к .exe.</summary>
        public static string DefaultPath(string dir)
        {
            string exe = Path.Combine(dir, OperatingSystemExe("OrpheusServer"));
            string dll = Path.Combine(dir, "OrpheusServer.dll");
            return File.Exists(exe) || !File.Exists(dll) ? exe : dll;
        }

        /// <summary>Новый одноразовый токен хозяина: 128 бит криптографического генератора.</summary>
        public static HostToken NewToken()
        {
            var bytes = new byte[HostToken.Bytes];
            using (var rng = RandomNumberGenerator.Create())
            {
                do
                {
                    rng.GetBytes(bytes);
                }
                while (HostToken.FromBytes(bytes).IsEmpty);
            }

            return HostToken.FromBytes(bytes);
        }

        /// <summary>Ключи сервера для запуска (видны тестам).</summary>
        public static List<string> Arguments(ServerLaunch l, HostToken token)
        {
            var a = new List<string> { "--world", l.WorldDir };
            if (l.Create)
            {
                a.Add("--create");
                a.Add("--seed");
                a.Add(l.Seed.ToString(CultureInfo.InvariantCulture));
                a.Add("--size");
                a.Add(l.Size.ToString(CultureInfo.InvariantCulture));
            }

            a.Add("--port");
            a.Add(l.Port.ToString(CultureInfo.InvariantCulture));
            a.Add("--mode");
            a.Add(l.Mode == ServerMode.Host ? "host" : "single");
            a.Add("--host-token");
            a.Add(token.ToString());
            a.Add("--open-address");
            a.Add(l.OpenAddress ?? "all");
            a.Add("--open-port");
            a.Add(l.OpenPort.ToString(CultureInfo.InvariantCulture));
            a.Add("--host-grace");
            a.Add(l.HostGraceSeconds.ToString(CultureInfo.InvariantCulture));
            a.AddRange(l.ExtraArgs);
            return a;
        }

        public IServerInstance Start(ServerLaunch launch)
        {
            if (launch == null)
            {
                throw new ArgumentNullException(nameof(launch));
            }

            if (!File.Exists(ServerPath))
            {
                throw new ServerLaunchException($"Нет сервера: {ServerPath}");
            }

            HostToken token = NewToken();
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ServerPath) ?? string.Empty,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            var args = new List<string>();
            if (ServerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "dotnet";
                args.Add(ServerPath);
            }
            else
            {
                psi.FileName = ServerPath;
            }

            args.AddRange(Arguments(launch, token));
            psi.Arguments = JoinArguments(args);

            var instance = new Instance(token);
            if (HiddenDesktop)
            {
                instance.BeginHidden(psi.FileName, args, psi.WorkingDirectory);
            }
            else
            {
                instance.Begin(psi);
            }

            try
            {
                instance.WaitReady(launch.ReadyTimeoutMs);
            }
            catch
            {
                instance.Kill();
                instance.Dispose();
                throw;
            }

            return instance;
        }

        public ServerEndpoint? FindRunning(string worldDir)
        {
            string dir = Path.GetFullPath(worldDir);
            HostLock? l = HostLockFile.TryRead(_storage, dir);
            if (l == null || !HostToken.TryParse(l.Token, out HostToken token))
            {
                return null; // нет замка или он битый — сервера по нему не найти, это не ошибка игры
            }

            // Файл остаётся после пропажи света; жив ли сервер — по замку мира, который он держит.
            try
            {
                return HostLockFile.IsWorldHeld(_storage, dir) ? new ServerEndpoint(l.Port, token, l.ProcessId) : (ServerEndpoint?)null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string OperatingSystemExe(string name) =>
            Path.DirectorySeparatorChar == '\\' ? name + ".exe" : name;

        /// <summary>Строка аргументов с кавычками по правилам Windows (CommandLineToArgvW) — netstandard2.1 без ArgumentList.</summary>
        public static string JoinArguments(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                {
                    sb.Append(a);
                    continue;
                }

                sb.Append('"');
                int slashes = 0;
                foreach (char c in a)
                {
                    if (c == '\\')
                    {
                        slashes++;
                        continue;
                    }

                    if (c == '"')
                    {
                        sb.Append('\\', slashes * 2 + 1);
                    }
                    else
                    {
                        sb.Append('\\', slashes);
                    }

                    slashes = 0;
                    sb.Append(c);
                }

                sb.Append('\\', slashes * 2);
                sb.Append('"');
            }

            return sb.ToString();
        }

        /// <summary>Работающий дочерний сервер.</summary>
        private sealed class Instance : IServerInstance
        {
            private const int KeepLines = 40;

            private readonly HostToken _token;
            private readonly object _gate = new object();
            private readonly Queue<string> _recent = new Queue<string>();
            private readonly ManualResetEventSlim _readyOrExit = new ManualResetEventSlim(false);
            private Process? _process;
            private HiddenProcess? _hidden;
            private Stopwatch _since = new Stopwatch();
            private int _port = -1;
            private int _pid;

            public Instance(HostToken token)
            {
                _token = token;
            }

            public ServerEndpoint Endpoint => new ServerEndpoint(_port, _token, _pid);

            public double ReadySeconds { get; private set; }

            public bool HasExited
            {
                get
                {
                    try
                    {
                        return _hidden != null ? _hidden.HasExited : _process == null || _process.HasExited;
                    }
                    catch (InvalidOperationException)
                    {
                        return true;
                    }
                }
            }

            public int? ExitCode => !HasExited ? (int?)null : _hidden != null ? _hidden.ExitCode : _process?.ExitCode;

            public IReadOnlyList<string> RecentOutput
            {
                get
                {
                    lock (_gate)
                    {
                        return _recent.ToArray();
                    }
                }
            }

            public void Begin(ProcessStartInfo psi)
            {
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) => OnLine(e.Data, stdout: true);
                p.ErrorDataReceived += (_, e) => OnLine(e.Data, stdout: false);
                p.Exited += (_, __) => _readyOrExit.Set();
                _since = Stopwatch.StartNew();
                if (!p.Start())
                {
                    throw new ServerLaunchException($"Сервер не запустился: {psi.FileName}");
                }

                _process = p;
                _pid = p.Id;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }

            /// <summary>C-022: то же на невидимом столе.</summary>
            public void BeginHidden(string fileName, IEnumerable<string> args, string workingDirectory)
            {
                _since = Stopwatch.StartNew();
                HiddenProcess p;
                try
                {
                    p = HiddenProcess.Start(fileName, args, workingDirectory);
                }
                catch (System.ComponentModel.Win32Exception e)
                {
                    throw new ServerLaunchException($"Сервер не запустился: {fileName}: {e.Message}");
                }

                _hidden = p;
                _pid = p.Id;
                p.Exited += () =>
                {
                    try
                    {
                        _readyOrExit.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                        // ручки уже отпущены (Dispose) — ждать некому
                    }
                };
                if (p.HasExited)
                {
                    _readyOrExit.Set();
                }

                p.BeginReadLines(l => OnLine(l, stdout: true), l => OnLine(l, stdout: false));
            }

            public void WaitReady(int timeoutMs)
            {
                if (!_readyOrExit.Wait(timeoutMs))
                {
                    throw new ServerLaunchException($"Сервер не готов за {timeoutMs} мс:{Environment.NewLine}{string.Join(Environment.NewLine, RecentOutput)}");
                }

                if (_port < 0)
                {
                    WaitForExit(Timeout.Infinite); // дочитать вывод
                    throw new ServerLaunchException(
                        $"Сервер вышел до готовности (код {ExitCode}):{Environment.NewLine}{string.Join(Environment.NewLine, RecentOutput)}");
                }
            }

            private void OnLine(string? line, bool stdout)
            {
                if (line == null)
                {
                    return;
                }

                lock (_gate)
                {
                    _recent.Enqueue(line);
                    while (_recent.Count > KeepLines)
                    {
                        _recent.Dequeue();
                    }
                }

                if (stdout && _port < 0 && line.StartsWith(ReadyMarker + " port=", StringComparison.Ordinal)
                    && int.TryParse(line.Substring(ReadyMarker.Length + 6).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port))
                {
                    _port = port;
                    ReadySeconds = _since.Elapsed.TotalSeconds;
                    _readyOrExit.Set();
                }
            }

            public void Stop(int timeoutMs)
            {
                if (HasExited)
                {
                    return;
                }

                try
                {
                    StreamWriter input = _hidden != null ? _hidden.StandardInput : _process!.StandardInput;
                    input.WriteLine("stop");
                    input.Flush();
                }
                catch (IOException)
                {
                    // труба закрыта — снимем
                }

                if (!WaitForExit(timeoutMs))
                {
                    Kill();
                }
            }

            public void Kill()
            {
                if (_hidden != null)
                {
                    _hidden.Kill();
                    _hidden.WaitForExit(5000);
                    return;
                }

                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        _process.Kill();
                        _process.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // уже вышел
                }
            }

            public bool WaitForExit(int timeoutMs)
            {
                if (_hidden != null)
                {
                    if (!_hidden.WaitForExit(timeoutMs))
                    {
                        return false;
                    }

                    _hidden.WaitForExit(); // дочитать вывод
                    return true;
                }

                if (_process == null)
                {
                    return true;
                }

                if (!_process.WaitForExit(timeoutMs))
                {
                    return false;
                }

                _process.WaitForExit(); // дочитать вывод (асинхронное чтение)
                return true;
            }

            /// <summary>Отпустить ручки. Работающий сервер останавливается штатно (игра закрывается — мир сохраняется).</summary>
            public void Dispose()
            {
                if (_hidden != null)
                {
                    Stop(10_000);
                    _hidden.Dispose();
                    _hidden = null;
                    _readyOrExit.Dispose();
                    return;
                }

                if (_process == null)
                {
                    return;
                }

                Stop(10_000);
                _process.Dispose();
                _process = null;
                _readyOrExit.Dispose();
            }
        }
    }
}
