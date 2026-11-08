using System;
using System.Threading;

namespace Orpheus.Server
{
    /// <summary>
    /// Режим «сервер в потоке» (0-031, docs/07 1.6 и 2.3): <see cref="ServerHost"/> в своём потоке процесса игры — так сервер
    /// работает на консоли, где дочерних процессов нет; на ПК по умолчанию остаётся отдельный процесс (0-024). Клиент ходит к
    /// нему через локальную связь (<c>LoopbackTransport</c>, <see cref="ServerHostServices.LocalTransport"/>) тем же
    /// <c>NetClient</c>. Поток такта — с приоритетом выше обычного: кадр игры не должен отнимать у такта время (у сборки мусора
    /// игры отнимет — это цена режима, 2.3).
    /// <para>Остановка — <see cref="Stop"/>: отмена, сервер сохраняет мир при остановке и выходит, поток ждётся.</para>
    /// </summary>
    public sealed class ServerThread : IDisposable
    {
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Thread _thread;
        private readonly ServerOptions _options;
        private readonly ServerHostServices _services;
        private readonly Action<int>? _ready;
        private volatile bool _isReady;
        private volatile bool _finished;
        private volatile ServerResult? _result;
        private volatile Exception? _error;

        private ServerThread(ServerOptions options, ServerHostServices services, string name)
        {
            _options = options;
            _services = services;
            _ready = services.Ready;
            services.Ready = port =>
            {
                Port = port;
                _isReady = true;
                _ready?.Invoke(port);
            };
            _thread = new Thread(Body)
            {
                IsBackground = true,
                Name = name,
                Priority = ThreadPriority.AboveNormal,
            };
        }

        /// <summary>Запустить сервер в новом потоке; вернуть сразу (мир создаётся или грузится уже в потоке).</summary>
        public static ServerThread Start(ServerOptions options, ServerHostServices services, string name = "orpheus-server")
        {
            var t = new ServerThread(options, services, name);
            t._thread.Start();
            return t;
        }

        /// <summary>Мир открыт, сеть слушает — можно подключаться.</summary>
        public bool IsReady => _isReady;

        /// <summary>Порт из готовности: 0 — локальная связь, −1 — сети нет.</summary>
        public int Port { get; private set; } = -1;

        /// <summary>Поток кончился (остановлен, «хост вышел», ошибка).</summary>
        public bool HasExited => _finished;

        /// <summary>Итог; до конца потока — null.</summary>
        public ServerResult? Result => _result;

        /// <summary>Исключение вне перечня, которое <see cref="ServerHost.Run"/> не ловит (тогда <see cref="Result"/> — null).</summary>
        public Exception? Error => _error;

        /// <summary>Код выхода как у программы: итог, исключение — 2; работает — null.</summary>
        public int? ExitCode => !_finished ? (int?)null : _result?.ExitCode ?? 2;

        /// <summary>Остановить (сохранить мир, выйти) и дождаться потока. Не вышел за <paramref name="timeoutMs"/> — false.</summary>
        public bool Stop(int timeoutMs)
        {
            _stop.Cancel();
            return _thread.Join(timeoutMs);
        }

        public void Dispose()
        {
            if (!Stop(30_000))
            {
                return; // поток не вышел — токен не отпускаем, он ещё нужен потоку
            }

            _stop.Dispose();
        }

        private void Body()
        {
            try
            {
                _result = ServerHost.Run(_options, _services, _stop.Token);
            }
            catch (Exception e)
            {
                _error = e;
                _services.Log?.Error(0, "admin", "сервер в потоке упал: " + e.Message, ("exception", e.GetType().Name), ("stack", e.StackTrace));
            }
            finally
            {
                _finished = true;
            }
        }
    }
}
