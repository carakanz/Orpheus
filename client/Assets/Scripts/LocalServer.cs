#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Orpheus.Net.Hosting;
using Orpheus.Platform.Pc;
using Orpheus.Save;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Orpheus.Client
{
    /// <summary>
    /// Сервер одиночной игры и хоста (карточка 0-024, docs/07 2.3): <c>OrpheusServer</c> дочерним процессом через
    /// <see cref="IServerLauncher"/> (реализация «ПК» — <see cref="PcServerLauncher"/>; процессы ОС клиент сам не трогает, 1.6).
    /// <list type="bullet">
    /// <item>Сначала — работающий сервер этого мира по <c>host.lock</c> (7.6: игра хоста упала и перезапущена) — вход к нему
    /// с токеном из замка, ничего не запускается.</item>
    /// <item>Иначе — запуск в фоне (готовность — строка сервера, кадры игры не стоят); нет <c>world.base</c> — мир создаётся.</item>
    /// <item>Жив ли сервер: свой процесс — <see cref="IServerInstance.HasExited"/>, найденный по замку — тот же замок раз в 1 с.</item>
    /// <item>Выход игры — штатная остановка (строка <c>stop</c>: сервер сохраняет мир и выходит). Найденный по замку сервер
    /// игрой не останавливается: у неё нет его stdin, он закроет мир сам через 60 с без хозяина (7.6).</item>
    /// </list>
    /// </summary>
    public sealed class LocalServer : IDisposable
    {
        const long AliveCheckUs = 1_000_000;
        const int StopTimeoutMs = 10_000;

        readonly ClientArgs _args;
        readonly PcServerLauncher _launcher;
        Task<IServerInstance>? _start;
        IServerInstance? _instance;
        long _nextAliveUs;
        bool _exited;
        int? _stoppedExitCode; // после Dispose процесс отпущен — код выхода запомнен здесь

        public LocalServer(ClientArgs args)
        {
            _args = args;
            // Application.dataPath — только из главного потока: папка игры = родитель Orpheus_Data (в редакторе — client/).
            string gameDir = Path.GetDirectoryName(Application.dataPath) ?? ".";
            _launcher = new PcServerLauncher(args.ServerPath ?? PcServerLauncher.DefaultPath(gameDir));
            WorldDir = Path.GetFullPath(args.WorldDir ?? Path.Combine(Application.persistentDataPath, "Worlds", "world-1"));
        }

        public string WorldDir { get; }

        /// <summary>Куда входить; до готовности — null.</summary>
        public ServerEndpoint? Endpoint { get; private set; }

        /// <summary>Сервер найден по замку (игра вернулась после падения), а не запущен этой игрой.</summary>
        public bool FoundByLock { get; private set; }

        /// <summary>Сервер не поднялся — текст для окна; иначе null.</summary>
        public string? Error { get; private set; }

        /// <summary>Секунд от запуска процесса сервера до готовности (у найденного по замку — 0).</summary>
        public double ReadySeconds => _instance?.ReadySeconds ?? 0;

        /// <summary>Код выхода своего процесса сервера; работает или не наш — null.</summary>
        public int? ExitCode => _stoppedExitCode ?? _instance?.ExitCode;

        /// <summary>Найти работающий сервер мира или начать запуск (фоном).</summary>
        public void Begin()
        {
            try
            {
                ServerEndpoint? found = _launcher.FindRunning(WorldDir);
                if (found.HasValue)
                {
                    FoundByLock = true;
                    Endpoint = found;
                    Debug.Log($"[host] найден работающий сервер мира по замку: порт {found.Value.Port}, процесс {found.Value.ProcessId} ({WorldDir})");
                    return;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning("[host] замок мира не прочитан: " + e.Message + " — запускаю сервер");
            }

            bool create = !File.Exists(Path.Combine(WorldDir, WorldFolder.BaseFileName));
            var launch = new ServerLaunch
            {
                WorldDir = WorldDir,
                Create = create,
                Seed = _args.Seed,
                Size = _args.Size,
                Mode = _args.Mode ?? ServerMode.Single,
                Port = 0,
                OpenAddress = _args.OpenAddress,
                OpenPort = _args.OpenPort,
            };
            launch.ExtraArgs.AddRange(_args.ServerArgs);
            Debug.Log($"[host] запуск {_launcher.ServerPath}: {launch.Mode}, мир {WorldDir}" +
                      (create ? $" (создать: зерно 0x{_args.Seed:X}, {_args.Size}²)" : " (загрузка)") +
                      $", «для друзей» — {launch.OpenAddress ?? "все адреса"}:{launch.OpenPort}");
            _start = Task.Run(() => _launcher.Start(launch));
        }

        /// <summary>Кадр: запуск закончился? Готов — <see cref="Endpoint"/>; не поднялся — <see cref="Error"/>.</summary>
        public void Poll()
        {
            if (_start == null || !_start.IsCompleted)
            {
                return;
            }

            Task<IServerInstance> t = _start;
            _start = null;
            if (t.Status == TaskStatus.RanToCompletion)
            {
                _instance = t.Result;
                Endpoint = _instance.Endpoint;
                Debug.Log($"[host] сервер готов за {_instance.ReadySeconds:0.00} с: 127.0.0.1:{Endpoint.Value.Port}, процесс {Endpoint.Value.ProcessId}");
                return;
            }

            Exception e = t.Exception?.GetBaseException() ?? new Exception("запуск отменён");
            Error = "Сервер не запустился: " + e.Message;
            Debug.LogError("[host] " + Error);
        }

        /// <summary>Сервер завершился (упал, снят, вышел сам) — проверка дешёвая, у найденного по замку — раз в 1 с.</summary>
        public bool HasExited(long nowUs)
        {
            if (_exited)
            {
                return true;
            }

            if (_instance != null)
            {
                _exited = _instance.HasExited;
            }
            else if (FoundByLock && nowUs >= _nextAliveUs)
            {
                _nextAliveUs = nowUs + AliveCheckUs;
                try
                {
                    _exited = _launcher.FindRunning(WorldDir) == null;
                }
                catch (IOException)
                {
                    // замок занят на миг — считаем живым до следующей проверки
                }
            }

            return _exited;
        }

        /// <summary>
        /// «Мир остановился. Последнее сохранение — N мин назад» (2.3). Загрузить заново — перезапуском игры, пока меню
        /// нет (заглушка). Последние строки вывода сервера — в лог.
        /// </summary>
        public string StoppedText()
        {
            if (_instance != null)
            {
                Debug.LogError($"[host] сервер вышел с кодом {_instance.ExitCode}; последние строки вывода:{Environment.NewLine}" +
                               string.Join(Environment.NewLine, _instance.RecentOutput));
            }

            return "Мир остановился. Последнее сохранение — " + LastSaveAgo() + ". Запустите игру снова, чтобы загрузить его";
        }

        string LastSaveAgo()
        {
            DateTime newest = DateTime.MinValue;
            try
            {
                foreach (string f in Directory.GetFiles(WorldDir, WorldFolder.GenerationPrefix + "*" + WorldFolder.GenerationSuffix))
                {
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t > newest)
                    {
                        newest = t;
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // папки нет — сохранений нет
            }

            if (newest == DateTime.MinValue)
            {
                return "нет";
            }

            int minutes = (int)Math.Max(0, (DateTime.UtcNow - newest).TotalMinutes);
            return minutes == 0 ? "меньше минуты назад" : minutes.ToString(CultureInfo.InvariantCulture) + " мин назад";
        }

        /// <summary>Строки вывода своего сервера (диагностика).</summary>
        public IReadOnlyList<string> RecentOutput => _instance?.RecentOutput ?? (IReadOnlyList<string>)Array.Empty<string>();

        /// <summary>Выход игры: свой сервер — штатно (сохранить и выйти, не вышел за 10 с — снять). Запуск ещё идёт — дождаться и остановить.</summary>
        public void Dispose()
        {
            if (_start != null)
            {
                try
                {
                    _start.Wait(StopTimeoutMs);
                }
                catch (AggregateException)
                {
                    // не поднялся — останавливать нечего
                }

                Poll();
            }

            if (_instance == null)
            {
                return;
            }

            bool wasRunning = !_instance.HasExited;
            _instance.Stop(StopTimeoutMs);
            int? code = _instance.ExitCode;
            _stoppedExitCode = code;
            if (wasRunning)
            {
                Debug.Log($"[host] сервер остановлен: код {(code.HasValue ? code.Value.ToString(CultureInfo.InvariantCulture) : "—")}");
            }

            _instance.Dispose();
        }
    }
}
