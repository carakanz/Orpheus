#nullable enable
using Orpheus.Net;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Orpheus.Client
{
    /// <summary>
    /// «Сервер в потоке» в сетевом режиме клиента (0-031): <see cref="ThreadServer"/> вместо дочернего процесса; когда сервер
    /// готов — вход через локальную связь (<see cref="LoopbackTransport"/>) тем же <see cref="NetClient"/>. Всё остальное —
    /// как у одиночной игры: снимки, предсказание, net-smoke, выход с сохранением мира.
    /// </summary>
    public sealed partial class NetGame
    {
        ThreadServer? _threadServer;

        /// <summary>Ключ <c>--server-thread</c>: запустить сервер в потоке (вход — в <see cref="PollThreadServer"/>). Иначе false.</summary>
        bool StartThreadServer()
        {
            if (!_args.ServerThread)
            {
                return false;
            }

            _status = "Запуск мира (сервер в потоке)…";
            _threadServer = new ThreadServer(_args);
            _threadServer.Begin();
            return true;
        }

        /// <summary>Кадр: сервер готов → вход по локальной связи; не поднялся или вышел во время игры → текст в окне.</summary>
        void PollThreadServer(long now)
        {
            ThreadServer? ts = _threadServer;
            if (ts == null || _fatal)
            {
                return;
            }

            ts.Poll();
            if (ts.Error != null)
            {
                _fatal = true;
                _status = ts.Error;
                return;
            }

            if (_client == null)
            {
                if (ts.IsReady && ts.ServerLink != null)
                {
                    _connectStartUs = NowUs();
                    Debug.Log($"[net] подключение по локальной связи к серверу в потоке (версия {NetModule.Version}, имя «{_args.Name}»)");
                    _link = LoopbackTransport.ConnectTo(ts.ServerLink);
                    _client = new NetClient(_link, LoopbackTransport.ServerPeer, _args.Name, NetModule.Version);
                    _client.SnapshotApplied = OnSnapshot;
                }

                return;
            }

            if (ts.HasExited)
            {
                _fatal = true;
                _status = ts.StoppedText();
                Debug.LogError("[net] " + _status);
                _client.Disconnect();
            }
        }

        /// <summary>Выход: остановить сервер в потоке (сохранить мир, выйти); код — для проверки net-smoke.</summary>
        void ShutdownThreadServer()
        {
            ThreadServer? ts = _threadServer;
            _threadServer = null;
            if (ts == null)
            {
                return;
            }

            _localWasOurs = true;
            ts.Dispose();
            _localExitCode = ts.ExitCode;
        }
    }
}
