#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Orpheus.Net.Transport;
using Orpheus.Platform.Pc;
using Orpheus.Save;
using Orpheus.Server;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Orpheus.Client
{
    /// <summary>
    /// «Сервер в потоке» (карточка 0-031, docs/07 1.6 «Сервер на консоли», 2.3 «На консоли»): <see cref="ServerHost"/> — та же
    /// библиотека, что в <c>OrpheusServer</c>, — в своём потоке процесса игры (<see cref="ServerThread"/>); клиент входит к нему
    /// через локальную связь (<see cref="LoopbackTransport"/>) тем же <c>NetClient</c>. Мир — в той же папке, что у одиночной
    /// игры дочерним процессом; сохранения и загрузка — только через <see cref="IPlatformStorage"/> (на ПК —
    /// <see cref="PcStorage"/>, атомарная запись 6.5), часы такта — <see cref="PcTickClock"/>. Ключ <c>--server-thread</c>.
    /// <para>Выход игры — остановка: сервер сохраняет мир при остановке и выходит, поток ждётся (до 30 с).</para>
    /// </summary>
    public sealed class ThreadServer : IDisposable
    {
        const int StopTimeoutMs = 30_000;

        readonly ClientArgs _args;
        readonly UnityServerLog _log = new UnityServerLog();
        ServerThread? _thread;
        LoopbackTransport? _serverLink;
        int? _stoppedExitCode;

        public ThreadServer(ClientArgs args)
        {
            _args = args;
            // Application.persistentDataPath — только из главного потока.
            WorldDir = Path.GetFullPath(args.WorldDir ?? Path.Combine(Application.persistentDataPath, "Worlds", "world-1"));
        }

        public string WorldDir { get; }

        /// <summary>Серверный конец локальной связи — к нему подключается клиент (после <see cref="IsReady"/>).</summary>
        public LoopbackTransport? ServerLink => _serverLink;

        /// <summary>Мир открыт, локальная связь слушает.</summary>
        public bool IsReady => _thread?.IsReady == true;

        /// <summary>Сервер не поднялся или упал — текст для окна; иначе null.</summary>
        public string? Error { get; private set; }

        /// <summary>Поток сервера кончился.</summary>
        public bool HasExited => _thread?.HasExited == true;

        /// <summary>Код выхода как у программы (0 — сохранил и вышел штатно); работает — null.</summary>
        public int? ExitCode => _stoppedExitCode ?? _thread?.ExitCode;

        /// <summary>Секунд от старта потока до готовности.</summary>
        public double ReadySeconds { get; private set; }

        float _beginAt;

        /// <summary>Запустить сервер в потоке (мир создаётся или грузится уже в потоке — кадры игры не стоят).</summary>
        public void Begin()
        {
            var storage = new PcStorage();
            bool create = !storage.FileExists(Path.Combine(WorldDir, WorldFolder.BaseFileName));
            var argv = new List<string> { "--world", WorldDir };
            if (create)
            {
                argv.AddRange(new[] { "--create", "--seed", _args.Seed.ToString(CultureInfo.InvariantCulture), "--size", _args.Size.ToString(CultureInfo.InvariantCulture) });
            }

            argv.AddRange(_args.ServerArgs);
            ServerOptions options;
            try
            {
                options = ServerOptions.Parse(argv.ToArray());
            }
            catch (ArgumentException e)
            {
                Error = "Сервер в потоке: ключи — " + e.Message;
                Debug.LogError("[thread] " + Error);
                return;
            }

            _serverLink = LoopbackTransport.CreateServer(options.MaxPlayers);
            var services = new ServerHostServices
            {
                Log = _log,
                Storage = storage,
                CreateClock = rate => new PcTickClock(rate),
                LocalTransport = _serverLink,
            };
            Debug.Log($"[thread] сервер в потоке: мир {WorldDir}" +
                      (create ? $" (создать: зерно 0x{_args.Seed:X}, {_args.Size}²)" : " (загрузка)") +
                      $", ключи: {string.Join(" ", argv)}");
            _beginAt = Time.realtimeSinceStartup;
            _thread = ServerThread.Start(options, services);
        }

        /// <summary>Кадр: готов ли, не упал ли.</summary>
        public void Poll()
        {
            if (_thread == null || Error != null)
            {
                return;
            }

            if (ReadySeconds == 0 && _thread.IsReady)
            {
                ReadySeconds = Time.realtimeSinceStartup - _beginAt;
                Debug.Log($"[thread] сервер готов за {ReadySeconds:0.00} с: локальная связь, поток «{"orpheus-server"}»");
            }

            if (_thread.HasExited && !_thread.IsReady)
            {
                Error = "Сервер в потоке не запустился: " + (_thread.Error?.Message ?? $"код {_thread.ExitCode}");
                Debug.LogError("[thread] " + Error);
            }
        }

        /// <summary>«Мир остановился» (2.3): поток сервера вышел во время игры.</summary>
        public string StoppedText() =>
            $"Мир остановился (сервер в потоке, код {ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "—"}). Запустите игру снова, чтобы загрузить его";

        /// <summary>Выход игры: остановить сервер (сохранить мир, выйти) и вывести итог: такт, сохранения, хэш.</summary>
        public void Dispose()
        {
            ServerThread? t = _thread;
            _thread = null;
            if (t == null)
            {
                return;
            }

            bool stopped = t.Stop(StopTimeoutMs);
            _stoppedExitCode = stopped ? t.ExitCode : null;
            ServerResult? r = t.Result;
            if (r != null)
            {
                MetricsSample? m = r.Metrics;
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[thread] итог: тактов {0} за {1:0.0} с, такт среднее {2:0.000} мс, p50 {3:0.000}, p99 {4:0.000}, p99.9 {5:0.000}, max {6:0.000}; " +
                    "разрывов > 38 мс {7} из {8}, max разрыв {9:0.0} мс; перегрузок (отставание > 3 тактов) {10}; выделено в такте {11:0} Б/такт",
                    r.Ticks, r.Seconds, m?.TickMeanMs ?? 0, m?.TickP50Ms ?? 0, m?.TickP99Ms ?? 0, m?.TickP999Ms ?? 0, m?.TickMaxMs ?? 0,
                    m?.Stalls ?? 0, m?.Gaps ?? 0, m?.MaxGapMs ?? 0, _log.Overloads, r.AllocatedBytesPerTick));
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[thread] сохранения: загружено №{0} (хэш {1:x16}), записано {2}, последнее №{3}; хэш в конце {4:x16}; ошибок сервера {5}",
                    r.LoadedNumber, r.LoadedHash, r.SavesWritten, r.LastSavedNumber, r.FinalHash, _log.Errors));
            }

            if (_log.Errors > 0 && _stoppedExitCode == 0)
            {
                _stoppedExitCode = 3; // ошибка в логе сервера (например, «Мир не сохраняется») — не «штатно»
            }

            Debug.Log($"[thread] сервер остановлен: код {(_stoppedExitCode.HasValue ? _stoppedExitCode.Value.ToString(CultureInfo.InvariantCulture) : "— (поток не вышел)")}");
            if (stopped)
            {
                t.Dispose();
            }

            _serverLink?.Dispose();
            _serverLink = null;
        }

        /// <summary>Лог сервера в лог Unity (Debug.Log потокобезопасен): строки <c>[server] такт уровень категория сообщение ключ=значение</c>.</summary>
        sealed class UnityServerLog : IServerLog
        {
            int _errors;
            int _overloads;

            public int Errors => Volatile.Read(ref _errors);

            public int Overloads => Volatile.Read(ref _overloads);

            public bool Enabled(string cat, Orpheus.Server.LogLevel level) => level >= Orpheus.Server.LogLevel.Info;

            public void Write(Orpheus.Server.LogLevel level, long tick, string cat, string msg, (string Key, object? Value)[] data)
            {
                if (!Enabled(cat, level))
                {
                    return;
                }

                var sb = new StringBuilder(128);
                sb.Append("[server] ").Append(tick).Append(' ').Append(TextServerLog.LevelName(level)).Append(' ').Append(cat).Append(' ').Append(msg);
                if (data != null)
                {
                    foreach ((string key, object? value) in data)
                    {
                        sb.Append(' ').Append(key).Append('=')
                          .Append(value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString());
                    }
                }

                if (level == Orpheus.Server.LogLevel.Error)
                {
                    Interlocked.Increment(ref _errors);
                    Debug.LogError(sb.ToString());
                }
                else if (level == Orpheus.Server.LogLevel.Warn)
                {
                    if (msg.StartsWith("перегрузка", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _overloads);
                    }

                    Debug.LogWarning(sb.ToString());
                }
                else
                {
                    Debug.Log(sb.ToString());
                }
            }
        }
    }
}
