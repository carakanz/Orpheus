using System;
using System.Collections.Generic;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Hosting
{
    /// <summary>Режим сервера, запущенного игрой (docs/07, 2.3).</summary>
    public enum ServerMode : byte
    {
        /// <summary>Одиночная игра: только 127.0.0.1, входит только сам игрок (пока «Открыть для друзей» не нажато).</summary>
        Single = 1,

        /// <summary>Хост из игры: то же, плюс «Открыть для друзей» с первого шага меню.</summary>
        Host = 2,
    }

    /// <summary>
    /// Что запустить (2.3: «игра передаёт путь к миру, режим, свободный порт и одноразовый токен хозяина»).
    /// Токен создаёт лаунчер; порт 0 — свободный выберет ОС, сервер сообщит его сам.
    /// </summary>
    public sealed class ServerLaunch
    {
        /// <summary>Папка мира.</summary>
        public string WorldDir { get; set; } = string.Empty;

        /// <summary>Создать мир (<c>--create</c>); иначе — загрузить.</summary>
        public bool Create { get; set; }

        public ulong Seed { get; set; }

        /// <summary>Размер мира при создании: 1024 или 4096.</summary>
        public int Size { get; set; } = 1024;

        public ServerMode Mode { get; set; } = ServerMode.Single;

        /// <summary>UDP-порт на петле; 0 — свободный (по умолчанию).</summary>
        public int Port { get; set; }

        /// <summary>
        /// Адрес «Открыть для друзей»: null — все адреса (игра); тесты — второй адрес петли 127.0.0.2, чтобы брандмауэр
        /// Windows не спрашивал разрешения у каждой сборки.
        /// </summary>
        public string? OpenAddress { get; set; }

        /// <summary>Порт «Открыть для друзей»: по умолчанию UDP 7777 (docs/07, 0 и 10); 0 — выдаст ОС.</summary>
        public int OpenPort { get; set; } = 7777;

        /// <summary>Сколько ждать упавшую игру хоста (2.3, 7.6): 60 с.</summary>
        public int HostGraceSeconds { get; set; } = 60;

        /// <summary>Сколько ждать готовности сервера, мс.</summary>
        public int ReadyTimeoutMs { get; set; } = 30_000;

        /// <summary>Прочие ключи сервера (автосохранение, логи, хэш — для тестов и отладки).</summary>
        public List<string> ExtraArgs { get; } = new List<string>();
    }

    /// <summary>Куда подключаться: порт на петле и токен хозяина; номер процесса — для снятия и диагностики.</summary>
    public readonly struct ServerEndpoint
    {
        public ServerEndpoint(int port, HostToken token, int processId)
        {
            Port = port;
            Token = token;
            ProcessId = processId;
        }

        /// <summary>Адрес — всегда петля: клиент хоста ходит к своему серверу через 127.0.0.1 (2.3).</summary>
        public string Host => "127.0.0.1";

        public int Port { get; }

        public HostToken Token { get; }

        public int ProcessId { get; }
    }

    /// <summary>Работающий сервер, запущенный этой игрой.</summary>
    public interface IServerInstance : IDisposable
    {
        ServerEndpoint Endpoint { get; }

        /// <summary>Секунд от запуска процесса до готовности (порт слушается).</summary>
        double ReadySeconds { get; }

        /// <summary>Процесс завершился (упал или вышел сам): игра показывает «Мир остановился…» (2.3).</summary>
        bool HasExited { get; }

        /// <summary>Код выхода; ещё работает — null.</summary>
        int? ExitCode { get; }

        /// <summary>Последние строки вывода сервера (для сообщения об ошибке).</summary>
        IReadOnlyList<string> RecentOutput { get; }

        /// <summary>Штатно остановить (сервер сохраняет мир и выходит); не вышел за <paramref name="timeoutMs"/> — снять.</summary>
        void Stop(int timeoutMs);

        /// <summary>Снять процесс немедленно (только этот процесс, по номеру).</summary>
        void Kill();

        bool WaitForExit(int timeoutMs);
    }

    /// <summary>
    /// Запуск <c>OrpheusServer</c> (docs/07, 1.6 и 2.3): на ПК — дочерний процесс (<c>Orpheus.Platform.Pc.PcServerLauncher</c>;
    /// <c>System.Diagnostics.Process</c> только там), на консоли — потом «сервер в потоке» за этим же интерфейсом (0-031).
    /// </summary>
    public interface IServerLauncher
    {
        /// <summary>
        /// Запустить и дождаться готовности: сервер сам сообщает порт (строка готовности в выводе) — без пауз «на всякий
        /// случай». Не поднялся — <see cref="ServerLaunchException"/> с последними строками вывода.
        /// </summary>
        IServerInstance Start(ServerLaunch launch);

        /// <summary>
        /// Работающий сервер мира по файлу-замку (7.6: игра хоста после своего падения находит сервер и подключается);
        /// нет файла или сервер уже не держит мир — null.
        /// </summary>
        ServerEndpoint? FindRunning(string worldDir);
    }

    /// <summary>Сервер не поднялся: вышел до готовности или не успел.</summary>
    public sealed class ServerLaunchException : Exception
    {
        public ServerLaunchException(string message)
            : base(message)
        {
        }
    }
}
