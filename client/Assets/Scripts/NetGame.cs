#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Hosting;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace Orpheus.Client
{
    /// <summary>
    /// Сетевой режим клиента (карточка 0-012; предсказание — 0-013; интерполяция и шов — 0-017): вход на сервер по UDP,
    /// мир перегенерируется по зерну из «входа принят» и сверяется с сервером по хэшу генерации; чужие сущности ведёт
    /// <see cref="RemoteInterpolation"/> ядра — время показа «сервер сейчас − 100 мс» растёт каждый кадр (docs/07, 2.9);
    /// «плавающее начало» переезжает за своим персонажем, как в офлайне (0-010). Свой персонаж ведёт <see cref="LocalPrediction"/> ядра
    /// (Orpheus.Net, 0-019): шаг кадра ядром, сверка по AckFrame со своим состоянием без кванта, гашение расхождений (2.6) —
    /// он в своём «сейчас», без задержки показа. <c>--net-sim RTT,σ,потери%[,зерно]</c> — имитатор связи ядра под клиентом.
    /// Ввод летит на сервер пакетиками 60 Гц, как у ботов (Temp: <c>BotRun</c>).
    /// Кадр ведётся из <see cref="GameBoot.Update"/>: poll → состояние → ввод → предсказание → переезд начала → окно →
    /// персонаж и камера → чужие → HUD.
    /// </summary>
    public sealed partial class NetGame
    {
        const int LoginTimeoutSec = 10;

        /// <summary>
        /// Пороги net-smoke чужих (0-017, 0-021): доля кадров с тем же временем показа и кадров «стоит на новейшем»; доля
        /// шагов догона (быстрее предела по местному времени — показ упирается в новейший снимок: без задержки показа,
        /// M2 проверки 0-017, их ~25 %; модель 200,30,1 на 500 прогонах — не больше 1 %); перестановок часов — ни одной.
        /// </summary>
        const double MaxSmokeSameTimeShare = 0.01;
        const double MaxSmokeHoldShare = 0.05;
        const double MaxSmokeCatchUpShare = 0.03;
        const int MinSmokeRemoteSteps = 60;

        /// <summary>Порог net-smoke (0-019): правок на чистом движении и расхождение предсказания с сервером, unit.</summary>
        const int MaxSmokeCorrections = 2;
        const int MaxSmokeDevUnits = 8;

        readonly GameBoot _g;
        readonly ClientArgs _args;
        string _host;
        int _port;
        readonly long _startUs = NowUs();
        long _connectStartUs;

        // Одиночная и хост (0-024): свой сервер дочерним процессом; null — --connect.
        LocalServer? _local;
        int? _localExitCode;
        bool _localWasOurs;
        bool _openSent;
        bool _openLogged;

        /// <summary>Запуск мира может идти до 30 с (ожидание готовности лаунчера) — до него тайм-аут входа не считается.</summary>
        const int LaunchTimeoutSec = 30;

        long _pollUs;
        RemoteInterpolation? _remote;
        long _anchoredInitial; // установка предсказания, под которую поставлено начало
        bool _fatal;           // статус окончательный (мир не совпал, не подключились) — не перетирать

        // net-smoke: свой персонаж на экране — нарисовано против ядра (как офлайн-smoke 0-010), окно — дыры.
        bool _measureSelf;
        WorldPos _prevDrawn;
        float _maxSelfJump;
        int _maxSelfDrift;
        int _selfDriftFails;
        int _holes;
        int _maxWindow;
        long _renderFrames;

        readonly Dictionary<int, GameObject> _ents = new Dictionary<int, GameObject>();
        readonly HashSet<int> _seen = new HashSet<int>();
        GameObject _entityRoot = null!;
        Sprite _heroSprite = null!;

        NetClient? _client;
        LiteNetTransport? _transport;
        ITransport? _link; // транспорт клиента: UDP или имитатор над ним (0-019)
        NetSimTransport? _netSim;
        bool _hasServerStart;
        int _corrLogged;
        WorldPos _serverStart;
        WorldPos _serverLast;
        WorldModel? _world;
        MapShape _map;
        FloatingOrigin? _origin;
        WorldView? _view;
        WorldPos _localPos;
        Simulation? _netRules;
        LocalPrediction? _pred;
        int _localId;
        ScreenshotRun? _screenshot;
        Anim.NetAnimation? _anim; // K-001: процедурная анимация — только отображение
        long _inGameSinceUs;
        long _hudSnaps;
        long _hudTimeUs;
        int _hudFrame;
        bool _quitPending;
        string _status = "Подключение…";

        /// <summary>
        /// Монотонные микросекунды (той же шкалы живёт транспорт и кольцо снимков). Без переполнения: <c>ticks × 10⁶</c>
        /// выходит за long через ≈ 10,7 суток работы QPC 10 МГц (проверка 0-019, з. 4) — делим сначала.
        /// </summary>
        public static long NowUs()
        {
            long ts = Stopwatch.GetTimestamp();
            long f = Stopwatch.Frequency;
            return ts / f * 1_000_000 + ts % f * 1_000_000 / f;
        }

        public NetGame(GameBoot game, ClientArgs args)
        {
            _g = game;
            _args = args;
            if (args.Connect != null)
            {
                int colon = args.Connect.LastIndexOf(':');
                _host = args.Connect.Substring(0, colon);
                _port = int.Parse(args.Connect.Substring(colon + 1), CultureInfo.InvariantCulture);
            }
            else
            {
                _host = "127.0.0.1"; // клиент хоста ходит к своему серверу через петлю (07, 2.3); порт сообщит сервер
                _port = 0;
            }

            _heroSprite = _g.Character.GetChild(0).GetComponent<SpriteRenderer>().sprite;
            _entityRoot = new GameObject("Entities");
        }

        /// <summary>
        /// Старт: <c>--connect</c> — сразу UDP-соединение; одиночная/хост (0-024) — найти сервер мира по замку или запустить
        /// его лаунчером (фоном), соединение — когда сервер готов (<see cref="Frame"/>).
        /// </summary>
        public void StartConnect()
        {
            if (_args.NetSmokeSeconds > 0)
            {
                // Как игра: 60 кадров/с, а не ~80 000 в пакетном режиме (замечание 9 проверки 0-012).
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
            }

            if (StartThreadServer())
            {
                return; // 0-031: сервер в потоке, вход по локальной связи (NetGame.Thread.cs)
            }

            if (_args.Mode.HasValue)
            {
                _status = "Запуск мира…";
                _local = new LocalServer(_args);
                _local.Begin();
                return;
            }

            Connect(default);
        }

        /// <summary>UDP-соединение с сервером; токен хозяина — у игры, запустившей сервер. Хост не найден — сразу «отказ».</summary>
        void Connect(HostToken token)
        {
            _connectStartUs = NowUs();
            Debug.Log($"[net] подключение к {_host}:{_port} (версия {NetModule.Version}, имя «{_args.Name}»" +
                      $"{(token.IsEmpty ? "" : ", хозяином")})");
            try
            {
                _transport = LiteNetTransport.Connect(_host, _port);
            }
            catch (SocketException e)
            {
                _fatal = true;
                _status = $"Не удалось подключиться к {_host}:{_port}: {e.Message}";
                Debug.LogError("[net] " + _status);
                return;
            }

            _link = _transport;
            if (_args.NetSimRttMs > 0 || _args.NetSimJitterMs > 0 || _args.NetSimLossPercent > 0)
            {
                _netSim = new NetSimTransport(_transport, NetSimSettings.FromRtt(_args.NetSimRttMs, _args.NetSimJitterMs, _args.NetSimLossPercent, _args.NetSimSeed));
                _link = _netSim;
                Debug.Log($"[net] имитатор связи: {_netSim.Settings}");
            }

            _client = new NetClient(_link, _transport.ServerPeer, _args.Name, NetModule.Version);
            _client.HostToken = token; // до первого Poll: вход уходит, когда соединение установлено
            _client.SnapshotApplied = OnSnapshot;
        }

        /// <summary>Свой сервер (0-024): запуск закончился → соединение; не поднялся или вышел во время игры → текст в окне.</summary>
        void PollLocalServer(long now)
        {
            LocalServer? local = _local;
            if (local == null || _fatal)
            {
                return;
            }

            local.Poll();
            if (local.Error != null)
            {
                _fatal = true;
                _status = local.Error;
                return;
            }

            if (_client == null)
            {
                if (local.Endpoint is ServerEndpoint ep)
                {
                    _port = ep.Port;
                    Connect(ep.Token);
                }

                return;
            }

            if (local.HasExited(now))
            {
                // 2.3: упал сам сервер — понятный текст сразу, а не «Связь с хостом потеряна» через 10 с тайм-аута.
                _fatal = true;
                _status = local.StoppedText();
                Debug.LogError("[net] " + _status);
                _client.Disconnect();
            }
        }

        /// <summary>«Открыть для друзей» (2.13 п. 9): ключ --open-for-friends сразу после входа или клавиша F2 (меню — потом).</summary>
        void OpenForFriendsFrame()
        {
            NetClient? c = _client;
            if (_local == null || c == null || !InGame)
            {
                return;
            }

            bool key = Keyboard.current?.f2Key.wasPressedThisFrame == true;
            if ((_args.OpenForFriends && !_openSent) || key)
            {
                _openSent = true;
                _openLogged = false;
                c.RequestOpenForFriends();
                Debug.Log("[host] «Открыть для друзей» отправлено");
            }

            if (!_openLogged && c.OpenResult is OpenResultMessage r)
            {
                _openLogged = true;
                Debug.Log(r.Opened
                    ? $"[host] открыто для друзей: UDP-порт {r.Port} ({_args.OpenAddress ?? "все адреса"})"
                    : "[host] открыть для друзей не удалось");
            }
        }

        public bool InGame => _view != null && _client?.State == NetClientState.InGame;

        /// <summary>Один кадр: разбор связи, состояние, ввод, интерполяция, окно, камера, HUD, smoke/снимок.</summary>
        /// <summary>Секунд с входа в игру (0 до входа).</summary>
        public double InGameSeconds => InGame ? (NowUs() - _inGameSinceUs) / 1_000_000.0 : 0.0;

        public void Frame()
        {
            long now = NowUs();
            _pollUs = now;
            PollLocalServer(now);
            PollThreadServer(now);
            if (_client != null)
            {
                try
                {
                    _client.Poll(now);
                }
                catch (Exception e)
                {
                    _status = "Ошибка связи: " + e.Message;
                    Debug.LogError("[net] " + _status);
                    _client.Disconnect();
                }
            }

            UpdateZoom();

            NetClientState state = _client?.State ?? (_fatal ? NetClientState.Disconnected : NetClientState.Connecting);
            switch (state)
            {
                case NetClientState.Connecting:
                    if (_client == null)
                    {
                        break; // свой сервер ещё запускается (лаунчер ждёт готовности сам, до 30 с)
                    }

                    if ((now - _connectStartUs) / 1_000_000 >= LoginTimeoutSec && !_fatal)
                    {
                        // Нет сервера — через 10 с понятный текст вместо вечного «Подключение…» (замечание 7 проверки 0-012).
                        _fatal = true;
                        _status = $"Не удалось подключиться к {_host}:{_port}: сервер не отвечает";
                        Debug.LogError("[net] " + _status);
                        _client!.Disconnect();
                    }

                    break;
                case NetClientState.LoggingIn:
                    _status = "Вход…";
                    if ((now - _connectStartUs) / 1_000_000 >= LoginTimeoutSec)
                    {
                        _status = "Сервер не ответил на вход";
                    }

                    break;
                case NetClientState.InGame:
                    if (_view == null && !_fatal)
                    {
                        BuildWorld(_client!.Accept);
                    }

                    break;
                case NetClientState.Rejected:
                    _status = "Отказ входа: " + (_client!.RejectText ?? "неизвестно");
                    break;
                case NetClientState.Disconnected:
                    if (_fatal)
                    {
                        break;
                    }

                    _fatal = true;
                    // 0-024: «Хост вышел. Мир сохранён у него», «Связь с хостом потеряна» и т. п. — по причине закрытия (7.6).
                    _status = _view != null
                        ? (_client?.DisconnectText ?? "Связь с хостом потеряна") + $" ({_client?.DisconnectReason ?? NetDisconnectReason.Other})"
                        : $"Не удалось подключиться к {_host}:{_port}: " +
                          (_client?.DisconnectReason == NetDisconnectReason.ConnectFailed ? "сервер не отвечает" : $"{_client?.DisconnectReason}");
                    Debug.LogError("[net] " + _status);
                    break;
            }

            if (_view != null && state == NetClientState.InGame && !_fatal)
            {
                SendInput(now);
                Render(now);
                if (_screenshot == null && _args.ScreenshotPath != null)
                {
                    _screenshot = new ScreenshotRun(_g, _args.ScreenshotPath, netMode: true);
                }
            }

            OpenForFriendsFrame();
            _screenshot?.AfterFrame();
            NetSmokeCheck(now, state);
            UpdateHud(now);
            QuitIfEscaped();
        }

        /// <summary>Выход: отключиться; свой сервер — штатно остановить (сохранить мир и выйти, 0-024). Повторный вызов — ничего.</summary>
        public void Shutdown()
        {
            _client?.Disconnect();
            (_link ?? _transport)?.Dispose();
            _link = null;
            _transport = null;
            ShutdownThreadServer();
            LocalServer? local = _local;
            _local = null;
            if (local != null)
            {
                _localWasOurs = !local.FoundByLock && local.Endpoint.HasValue;
                local.Dispose();
                _localExitCode = local.ExitCode;
            }
        }

        // ---------- мир — чанки от сервера (0-026) ----------

        /// <summary>
        /// Мир клиента — только чанки окна от сервера (docs/07, 3.4: генератора и зерна у клиента нет; 0-026): модель —
        /// <see cref="ClientWorld"/> из <see cref="NetClient"/>; пришёл чанк или пачка — этот чанк перерисовывается в тот же кадр,
        /// чанк вышел из окна (центр окна от сервера) — узел в пул.
        /// </summary>
        void BuildWorld(in LoginAcceptMessage accept)
        {
            _map = new MapShape(accept.MapWidth, accept.MapHeight, accept.Wraps);
            ClientWorld cw = _client!.World!;
            _world = cw.World;
            _localId = accept.EntityId;
            WorldPos spawn = CenterOf(accept.MapWidth / 2, accept.MapHeight / 2);
            _origin = new FloatingOrigin(_map, Torus.ChunkOf(_map, spawn));
            _view = new WorldView(_world, _origin, _g.Grid, _g.TileMeadow, _g.TileStone, _g.TileTree, _g.TileRock);
            WorldView view = _view;
            foreach (ChunkCoord c in _world.GetChunkCoords().ToArray())
            {
                view.ShowChunk(c); // чанки, пришедшие в том же Poll, что и «вход принят»
            }

            cw.ChunkChanged = c => view.ShowChunk(c);
            cw.ChunkRemoved = c => view.HideChunk(c);
            cw.RolledBack = OnRolledBack; // 0-028: откат правки — красная вспышка с причиной
            _localPos = spawn;

            // Проходимость — тот же IsBlocked ядра, что в такте сервера; предсказание стартует из точки поселения.
            _netRules = new Simulation(_world, maxEntities: 1, maxEditsPerTick: 1, reserveBuildMaps: 0);
            _pred = new LocalPrediction(_map, _netRules.Solid, spawn);
            _remote = new RemoteInterpolation(_map);
            _client!.ClockReset = _remote.OnClockReset; // «часы после фриза» (2.4): окно медианы показа — заново (0-025)
            _anim = new Anim.NetAnimation(_g, _client, _origin, _world, _entityRoot.transform);
            Debug.Log($"[net] вход: сущ #{_localId}, мир {_map.Width}×{_map.Height}, такт {accept.ServerTick}; " +
                      $"мир — чанки от сервера (генератора у клиента нет), пришло уже {cw.ChunksReceived}");
            _g.Character.gameObject.SetActive(true);
            _g.Character.position = _origin.ToUnity(spawn);
            _inGameSinceUs = NowUs();
            _hudTimeUs = _inGameSinceUs;
            if (_local != null)
            {
                Debug.Log($"[host] вход хозяином: {(_inGameSinceUs - _startUs) / 1e6:0.00} с от старта сетевого режима " +
                          $"({Time.realtimeSinceStartupAsDouble:0.00} с от запуска игры), сервер " +
                          (_local.FoundByLock ? "найден по замку" : $"готов за {_local.ReadySeconds:0.00} с"));
            }

            _hudSnaps = _client!.SnapshotsApplied;
        }

        static WorldPos CenterOf(int tileX, int tileY) =>
            new WorldPos(tileX * Torus.SubTilesPerTile + Torus.SubTilesPerTile / 2,
                         tileY * Torus.SubTilesPerTile + Torus.SubTilesPerTile / 2);

        // ---------- снимки: кольцо и интерполяция ----------

        void OnSnapshot(Snapshot s)
        {
            // NetClient переиспользует объект — RemoteInterpolation копирует снимок в своё кольцо до возврата из Poll.
            _remote?.OnSnapshot(s, _pollUs);

            // Сверка своего персонажа (2.6): AckFrame — последний шагнутый сервером кадр, Self — состояние после него без кванта.
            if (_pred != null && s.Self.Present)
            {
                long before = _pred.Resynced;
                _pred.Reconcile(s.AckFrame, s.Self, _pollUs);
                if (_pred.Resynced != before && _corrLogged++ < 20)
                {
                    Debug.Log($"[net] правка предсказания: снимок {s.Tick}, ack {s.AckFrame}, отправлен кадр {_client!.Frame}, " +
                              $"сервер {s.Self}, расхождение {_pred.LastDevUnits} u, переполнений {_pred.Overflows}, сверок {_pred.Reconciled}");
                }
                if (!_hasServerStart)
                {
                    _hasServerStart = true;
                    _serverStart = s.Self.Position;
                }

                _serverLast = s.Self.Position;
            }
        }

        // ---------- кадр: ввод, персонаж, чужие сущности, камера, окно ----------

        void SendInput(long now)
        {
            NetClient c = _client!;
            // Часы ввода (docs/07 2.4, 2.7; 0-025): номер кадра идёт за оценкой кадра сервера (часы клиента по снимкам и
            // пингу) — NetClient.FramesDue, 0…4 кадра за кадр рисования. Время — монотонные мкс, не Time.unscaledDeltaTime:
            // в -batchmode Unity завышает время очень коротких кадров (net-smoke 0-019). После фриза сервера часы
            // переставляются, следующий кадр перенумеровывается (NetClient.SendFrame, LocalPrediction.Sent).
            int n = c.FramesDue(now);
            ushort editButtons = UpdateEdits(now); // 0-028: копание под курсором, стена по ПКМ
            if (_args.NetSmokeSeconds > 0 ? ScriptedSmokeDodge(now) : _g.Dodge.WasPressedThisFrame())
            {
                _dodgeWait = DodgeWaitFrames; // 0-034: нажатие ждёт направления до 0,1 с
                DodgePresses++;
            }

            for (int k = 0; k < n; k++)
            {
                InputFrame frame = _args.NetSmokeSeconds > 0 ? ScriptedSmokeFrame(now) : GameBoot.ToFrame(_g.Move.ReadValue<Vector2>());
                ushort buttons = editButtons;
                if (_dodgeWait > 0)
                {
                    if (frame.Direction.IsMoving)
                    {
                        buttons |= InputButtons.Dodge; // бит — в одном кадре: нажатие, не удержание (`02` 2.2)
                        _dodgeWait = 0;
                        DodgesSent++;
                    }
                    else
                    {
                        _dodgeWait--; // без направления — ничего (слово Автора 08.11)
                    }
                }

                if (buttons != 0)
                {
                    frame = new InputFrame(frame.Direction, (ushort)(frame.Buttons | buttons));
                }

                c.SendFrame(frame);
                _pred!.Sent(c.Frame, frame);
            }
        }

        /// <summary>
        /// Нажатие уворота ждёт направления движения столько кадров ввода (6 = 0,1 с): «Shift + стрелка» нажимают не строго
        /// одновременно. Не дождалось — пропадает (уворота без направления нет, 0-034).
        /// </summary>
        public const int DodgeWaitFrames = 6;

        int _dodgeWait;
        bool _smokeDodgeDone;

        /// <summary>
        /// Net-smoke жмёт уворот, только без имитации сети и не у шва: с потерями «липкое» нажатие сервер вправе применить в другом
        /// кадре (2.7), а у шва замер чужих и переезды проверяют своё.
        /// </summary>
        bool SmokeDodges => _args.NetSimRttMs == 0 && _args.NetSimJitterMs == 0 && _args.NetSimLossPercent == 0 && !_args.NetSmokeSeam;

        /// <summary>Нажатий уворота (Shift, X, B/○) и отправленных кадров с битом уворота (HUD, net-smoke).</summary>
        public int DodgePresses { get; private set; }

        /// <inheritdoc cref="DodgePresses"/>
        public int DodgesSent { get; private set; }

        /// <summary>
        /// Net-smoke без помех сети и шва (<see cref="SmokeDodges"/>): одно нажатие уворота на 1,5 с — в беге на восток (вместе с
        /// направлением). Сервер должен повторить рывок: иначе предсказание разойдётся на 3 тайла и smoke упадёт по правкам.
        /// </summary>
        bool ScriptedSmokeDodge(long now)
        {
            if (!SmokeDodges || _smokeDodgeDone || now - _inGameSinceUs < 1_500_000)
            {
                return false;
            }

            _smokeDodgeDone = true;
            return true;
        }

        /// <summary>Скрипт net-smoke: стоять 0,5 с, 2 с на восток, 2 с на север, дальше стоять.</summary>
        InputFrame ScriptedSmokeFrame(long now)
        {
            double t = (now - _inGameSinceUs) / 1_000_000.0;
            Vector2 v = t < 0.5 ? Vector2.zero : t < 2.5 ? Vector2.right : t < 4.5 ? Vector2.up : Vector2.zero;
            return GameBoot.ToFrame(v);
        }

        void Render(long now)
        {
            const float sub = FloatingOrigin.UnitsPerTile;
            _renderFrames++;

            // Свой персонаж: предсказание + гашение (2.6, 2.9), не интерполяция снимков.
            _pred!.RenderDelta(now, out WorldPos me, out float dx, out float dy);
            _localPos = me;
            ChunkCoord myChunk = Torus.ChunkOf(_map, me);
            if (_pred.Initial != _anchoredInitial)
            {
                // Установка (первая сверка): сервер поставил персонажа не там, где ждали, — как телепорт офлайна:
                // окно заново, начало — к персонажу. Рывок этого кадра не мерим.
                _anchoredInitial = _pred.Initial;
                _origin!.Rebase(myChunk, seam: false); // набор чанков задаёт сервер (0-026) — окно не сбрасывается
                _measureSelf = false;
            }
            else if (_origin!.NeedsRebase(myChunk, out bool seam))
            {
                // Переезд начала, как в офлайне (GameBoot.Update, 0-010): узлы окна, персонаж и чужие пересчитываются от
                // нового якоря в этом же кадре, камера ставится за персонажем — на экране рывка нет.
                _origin.Rebase(myChunk, seam);
            }

            _view!.PlaceNodes(); // набор чанков — от сервера (ShowChunk/HideChunk); здесь только места от якоря
            RenderFlashes(now);
            _g.Character.gameObject.SetActive(true);
            _g.Character.position = _origin!.ToUnity(me) + new Vector3(dx / sub, -dy / sub, 0f);
            _g.Camera.transform.position = _g.Character.position + GameBoot.CameraOffset;

            // Чужие: набор — новейший снимок, позиция — RemoteInterpolation на время показа этого кадра.
            _seen.Clear();
            RemoteInterpolation r = _remote!;
            if (r.BeginFrame(now))
            {
                Snapshot snap = r.Latest;
                for (int i = 0; i < snap.Count; i++)
                {
                    int id = snap.Entities[i].Id;
                    if (id == _localId || !r.TryGet(id, out WorldPos at, out float edx, out float edy))
                    {
                        continue;
                    }

                    _seen.Add(id);
                    GameObject go = GetEntity(id);
                    go.SetActive(true);
                    go.transform.position = _origin.ToUnity(at) + new Vector3(edx / sub, -edy / sub, 0f);
                    _anim?.Remote(id, go.transform);
                }
            }

            foreach (KeyValuePair<int, GameObject> kv in _ents)
            {
                if (!_seen.Contains(kv.Key))
                {
                    kv.Value.SetActive(false);
                }
            }

            _anim?.Frame(now, _pred!);
            if (_args.NetSmokeSeconds > 0)
            {
                SmokeFrame(me, dx, dy);
            }
        }

        /// <summary>
        /// net-smoke, каждый кадр: окно чанков без дыр и свой персонаж на экране там, где его ставит ядро (0-021,
        /// замечание 4 проверки 0-017): позиция относительно нарисованного узла чанка под ним (как офлайн-smoke 0-010)
        /// минус позиция предсказания с гашением (<see cref="LocalPrediction.RenderDelta"/>) — не больше 2/256 т. Не зависит
        /// от длины кадра и кванта ввода (шаг предсказания 0,1 т × до 4 кадров ввода за кадр рисования давал ложный красный
        /// по пределу рывка), а скачок узлов или персонажа на шве и при переезде начала (чанк, 32 т) ловит так же. Рывок за
        /// кадр — в лог, для сведения.
        /// </summary>
        void SmokeFrame(WorldPos me, float dx, float dy)
        {
            _maxWindow = Math.Max(_maxWindow, _view!.Count);
            // Окно задаёт сервер по своей позиции персонажа (0-026): дыры — от подтверждённой позиции последнего снимка,
            // а не от предсказания, которое впереди на RTT (у новой границы чанк ещё в пути — это не дыра).
            WorldPos confirmed = _client!.HasSnapshot && _client.Latest.Self.Present ? _client.Latest.Self.Position : me;
            _holes += SmokeRun.WindowHoles(_map, _view, confirmed, msg => Debug.LogError($"[smoke] кадр {_renderFrames}: {msg}"));
            WorldPos drawn = SmokeRun.DrawnAt(_map, _view, _origin!, me, _g.Character.position, out bool ok);
            if (!ok)
            {
                _measureSelf = false;
                return;
            }

            WorldPos core = Torus.Offset(_map, me, new Int2(Mathf.RoundToInt(dx), Mathf.RoundToInt(dy)));
            Int2 drift = Torus.Delta(_map, core, drawn);
            int driftAbs = Math.Max(Math.Abs(drift.X), Math.Abs(drift.Y));
            _maxSelfDrift = Math.Max(_maxSelfDrift, driftAbs);
            if (driftAbs > SmokeRun.MatchUnits && _selfDriftFails++ < 5)
            {
                Debug.LogError($"[smoke] кадр {_renderFrames}: свой нарисован в {drawn}, ядро — {core}: разница {drift.X},{drift.Y}/256 т " +
                               $"(допуск {SmokeRun.MatchUnits}), якорь {_origin!.Anchor}");
            }

            if (_measureSelf)
            {
                Int2 j = Torus.Delta(_map, _prevDrawn, drawn);
                _maxSelfJump = Mathf.Max(_maxSelfJump, Mathf.Sqrt((float)j.LengthSquared) / Torus.SubTilesPerTile);
            }

            _prevDrawn = drawn;
            _measureSelf = true;
        }

        GameObject GetEntity(int id)
        {
            if (_ents.TryGetValue(id, out GameObject? existing))
            {
                return existing;
            }

            var root = new GameObject($"Ent {id}");
            root.transform.SetParent(_entityRoot.transform, false);
            var spriteGo = new GameObject("Sprite");
            spriteGo.transform.SetParent(root.transform, false);
            spriteGo.transform.localPosition = new Vector3(0f, -0.4f, 0f);
            var sr = spriteGo.AddComponent<SpriteRenderer>();
            sr.sprite = _heroSprite;
            sr.color = new Color(0.55f, 0.8f, 1f); // чужие — голубоватые, свой — родной цвет
            sr.sortingOrder = WorldView.ObjectOrder;
            sr.spriteSortPoint = SpriteSortPoint.Pivot;
            _ents[id] = root;
            return root;
        }

        void UpdateZoom()
        {
            float scroll = _g.ZoomAxis.ReadValue<float>();
            if (scroll > 0.01f)
            {
                _g.SetZoom(_g.Zoom * GameBoot.ZoomStep);
            }
            else if (scroll < -0.01f)
            {
                _g.SetZoom(_g.Zoom / GameBoot.ZoomStep);
            }

            if (_g.ZoomIn.WasPressedThisFrame())
            {
                _g.SetZoom(_g.Zoom * GameBoot.ZoomStep);
            }

            if (_g.ZoomOut.WasPressedThisFrame())
            {
                _g.SetZoom(_g.Zoom / GameBoot.ZoomStep);
            }
        }

        // ---------- HUD, smoke, выход ----------

        void UpdateHud(long now)
        {
            if (_g.DebugText == null || ++_hudFrame % 6 != 0)
            {
                return;
            }

            NetClient? c = _client;
            float fps = 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            if (c == null || c.State != NetClientState.InGame)
            {
                _g.DebugText.text = $"{_status}  ({_host}:{_port})  {fps:0} fps";
                return;
            }

            long dUs = now - _hudTimeUs;
            float snapsPerSec = dUs > 0 ? (c.SnapshotsApplied - _hudSnaps) * 1_000_000f / dUs : 0f;
            _hudSnaps = c.SnapshotsApplied;
            _hudTimeUs = now;
            TileCoord t = Torus.TileOf(_map, _localPos);
            int rtt = _link!.RoundTripMs(c.ServerPeer);
            // Значок «высокая задержка» (docs/07 2.7; 0-033): D_p > 150 мс — пока строкой в HUD, картинка — потом.
            _g.DebugText.text = (c.HighLatency ? $"[высокая задержка: показ −{c.DelayFrames * 1000 / 60} мс]  " : "") +
                $"в игре  сервер {_host}:{_port}  seed 0x{c.Accept.WorldSeed:X}  {_map.Width}×{_map.Height}  " +
                $"такт {c.ServerTick}  tile {t.X},{t.Y}  сущ {Math.Min(_seen.Count + 1, 99)}  окно {_view!.Count}/{ChunkWindow.MaxChunks}  " +
                $"снимки {snapsPerSec:0.0}/с  дельт {c.DeltasApplied} (не склеено {c.DeltasUnmerged})  " +
                $"rtt {(rtt >= 0 ? rtt + " мс" : "—")}  L {c.LeadFrames} D {c.DelayFrames} запас {c.LeadMargin}  ввод {c.InputsSent}  " +
                $"предсверок {_pred?.Reconciled ?? 0} правок {_pred?.Resynced ?? 0} расх {_pred?.MaxDevUnits ?? 0}u" +
                $"  показ −{(_remote?.BufferUs ?? 0) / 1000} мс (стоит {_remote?.HoldFrames ?? 0})  якорь {_origin?.Anchor.X},{_origin?.Anchor.Y}" +
                $"{(_pred != null && _pred.Overflows > 0 ? $" (задержка > {LocalPrediction.BufferSize / 60} с: {_pred.Overflows})" : "")}  " +
                $"zoom x{_g.Zoom:0.00}  {fps:0} fps{EditHud(now)}";
        }

        void NetSmokeCheck(long now, NetClientState state)
        {
            if (_args.NetSmokeSeconds <= 0 || _quitPending)
            {
                return;
            }

            if (_fatal || state == NetClientState.Rejected || state == NetClientState.Disconnected || (_transport == null && _local == null && _threadServer == null))
            {
                Fail($"[smoke] FAILED: {_status}");
                return;
            }

            if (InGame && (now - _inGameSinceUs) / 1_000_000 >= Math.Max(2, _args.NetSmokeSeconds)
                && _client!.SnapshotsApplied >= 10 && SmokePauseDone) // 0-036: пауза по скрипту прошла
            {
                // Предсказание (0-013, 0-019): сдвиг — по подтверждённому серверному состоянию (не клиент сам с собой);
                // на чистом движении правок ≤ 2, расхождение предсказания с сервером ≤ 8 unit на всех сверках.
                Int2 d = Torus.Delta(_map, _serverStart, _serverLast);
                int moved = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
                LocalPrediction p = _pred!;
                if (!_hasServerStart || moved < 2 * 256 || p.Resynced > MaxSmokeCorrections || p.MaxDevUnits > MaxSmokeDevUnits || p.Initial != 1)
                {
                    Fail($"[smoke] FAILED: предсказание — сдвиг на сервере {moved} unit (нужно ≥ {2 * 256}), правок {p.Resynced} " +
                         $"(нужно ≤ {MaxSmokeCorrections}), макс.расхождение {p.MaxDevUnits} unit (нужно ≤ {MaxSmokeDevUnits}), установок {p.Initial}, сверок {p.Reconciled}");
                    return;
                }

                // Уворот (0-034): скриптовое нажатие ушло одним кадром с направлением; что сервер его повторил — проверка выше.
                Debug.Log($"[smoke] уворот: нажатий {DodgePresses}, кадров с битом {DodgesSent}");
                if (SmokeDodges && (DodgePresses != 1 || DodgesSent != 1))
                {
                    Fail($"[smoke] FAILED: уворот — нажатий {DodgePresses}, кадров с битом {DodgesSent} (нужно 1 и 1)");
                    return;
                }

                if (_client.DeltasApplied == 0 || _client.DeltasUnmerged * 10 > _client.SnapshotsApplied)
                {
                    Fail($"[smoke] FAILED: дельты (0-014): применено {_client.DeltasApplied}, " +
                         $"не склеено {_client.DeltasUnmerged} при снимках {_client.SnapshotsApplied}");
                    return;
                }

                // Чужие (0-017): время показа растёт каждый кадр, чужие идут плавно (шаг ≤ предела скорости × приращение
                // времени показа), почти не стоят на новейшем снимке; у шва — пара через шов и переезд начала на шве.
                RemoteInterpolation r = _remote!;
                double sameShare = r.Frames > 0 ? r.SameTimeFrames / (double)r.Frames : 1.0;
                double holdShare = r.Frames > 0 ? r.HoldFrames / (double)r.Frames : 1.0;
                double catchShare = r.Steps > 0 ? r.CatchUpSteps / (double)r.Steps : 1.0;
                string remote = $"чужие: кадров {r.Frames}, с тем же временем показа {r.SameTimeFrames} ({sameShare:P2}), " +
                                $"стоит на новейшем {r.HoldFrames} ({holdShare:P2}), шагов {r.Steps}, шаг max {r.MaxStepUnits:0.0}/256 т " +
                                $"(к пределу {r.MaxStepRatio:0.00}), сверх предела {r.StepViolations}, шагов догона {r.CatchUpSteps} ({catchShare:P2}), " +
                                $"пар через шов {r.SeamSamples}, " +
                                $"запас показа {r.BufferUs / 1000} мс, перестановок часов {r.ClockResets}, ход показа {r.PlaybackRate:0.000}, " +
                                $"догон к пределу {r.MaxCatchUpRatio:0.00} (k = {RemoteInterpolation.CatchUpRate:0.#})";
                Debug.Log("[smoke] " + remote);
                if (sameShare > MaxSmokeSameTimeShare || holdShare > MaxSmokeHoldShare || catchShare > MaxSmokeCatchUpShare
                    || r.StepViolations > 0 || r.ClockResets > 0 || r.Steps < MinSmokeRemoteSteps
                    || (_args.NetSmokeSeam && r.SeamSamples == 0))
                {
                    Fail($"[smoke] FAILED: интерполяция чужих — {remote} (нужно: тех же ≤ {MaxSmokeSameTimeShare:P0}, стоит ≤ {MaxSmokeHoldShare:P0}, " +
                         $"догона ≤ {MaxSmokeCatchUpShare:P0} шагов, сверх предела 0, перестановок часов 0, шагов ≥ {MinSmokeRemoteSteps}" +
                         $"{(_args.NetSmokeSeam ? ", пар через шов ≥ 1" : "")})");
                    return;
                }

                string seamLine = $"шов: переездов начала {_origin!.Rebases}, на шве {_origin.SeamRebases}, нарисовано − ядро max {_maxSelfDrift}/256 т " +
                                  $"(допуск {SmokeRun.MatchUnits}), рывок своего max {_maxSelfJump:0.000} т/кадр, окно max {_maxWindow} чанков, дыр {_holes}, якорь {_origin.Anchor}";
                Debug.Log("[smoke] " + seamLine);
                if (_holes > 0 || _selfDriftFails > 0 || _maxWindow > ChunkWindow.MaxChunks || (_args.NetSmokeSeam && _origin.SeamRebases == 0))
                {
                    Fail($"[smoke] FAILED: {seamLine}{(_args.NetSmokeSeam ? " (нужен переезд на шве ≥ 1)" : "")}");
                    return;
                }

                if (_args.OpenForFriends && _client.OpenResult?.Opened != true)
                {
                    Fail("[smoke] FAILED: «Открыть для друзей» — " +
                         (_client.OpenResult.HasValue ? "сервер ответил «не открыто»" : "ответа нет (вход не хозяином?)"));
                    return;
                }

                ClientWorld cw = _client.World!;
                int tileErrors = SmokeRun.CheckViewTiles(_view!, _world!, _map, "net", out int tilesChecked, out int samples,
                    out bool windowHasStone, line => Debug.Log("[smoke] " + line), msg => Debug.LogError("[smoke] " + msg));
                Debug.Log($"[smoke] world: чанков от сервера {cw.ChunksReceived}, пачек {cw.PatchesApplied}, разрывов {cw.PatchGaps}, " +
                          $"выброшено {cw.ChunksDropped}, сейчас {cw.ChunkCount} (max {cw.MaxChunkCount}); тайлов проверено {tilesChecked}, " +
                          $"расхождений Tilemap/ядро {tileErrors}, образцов {samples} (пол гор в окне: {(windowHasStone ? "есть" : "нет")}); " +
                          $"зерно сервера 0x{_client.Accept.WorldSeed:X} " +
                          $"(только для сверки образцов с gen_preview: tools/check_smoke_tiles.py)");
                // 1-017: у Очага окно — луга стартовой зоны, пола гор там нет по построению генератора,
                // поэтому образцов три (трава, дерево, скала); гранит в окне — четвертый обязателен.
                int needSamples = windowHasStone ? 4 : 3;
                if (tileErrors > 0 || samples < needSamples || cw.ChunkCount < ChunkWindow.Width * ChunkWindow.Height)
                {
                    Fail($"[smoke] FAILED: тайлы — расхождений {tileErrors}, образцов {samples} (нужно ≥ {needSamples}), чанков {cw.ChunkCount}");
                    return;
                }
                Debug.Log($"[smoke] input: кадров ввода {_client.InputsSent} за {(now - _inGameSinceUs) / 1e6:0.00} с " +
                          $"({_client.InputsSent / Math.Max((now - _inGameSinceUs) / 1e6, 1e-3):0.0} в секунду)");
                Debug.Log($"[smoke] done: вход #{_client.EntityId}, снимков {_client.SnapshotsApplied} " +
                          $"(устаревших {_client.SnapshotsStale}, потерь тактов {_client.SnapshotsMissed}), " +
                          $"ввода отправлено {_client.InputsSent}, плохих сообщений {_client.BadMessages}; " +
                          $"дельт {_client.DeltasApplied} (не склеено {_client.DeltasUnmerged}); " +
                          $"предсказание: сверок {p.Reconciled}, правок {p.Resynced}, " +
                          $"макс.расхождение {p.MaxDevUnits} unit, сдвиг {moved / 256.0:0.0} т" +
                          (_netSim != null
                              ? $"; имитатор: {_netSim.Settings}, ушло {_netSim.SendStats.Delivered} (потерь {_netSim.SendStats.Lost}), " +
                                $"пришло {_netSim.ReceiveStats.Delivered} (потерь {_netSim.ReceiveStats.Lost})"
                              : "; без имитатора"));
                Quit(0);
            }
            else if ((now - (_connectStartUs != 0 ? _connectStartUs : _startUs + LaunchTimeoutSec * 1_000_000L)) / 1_000_000
                     >= _args.NetSmokeSeconds + LoginTimeoutSec)
            {
                Fail($"[smoke] FAILED: тайм-аут, состояние — {_status}");
            }
        }

        void Fail(string logLine)
        {
            Debug.LogError(logLine);
            Quit(1);
        }

        void QuitIfEscaped() => PauseFrame(); // 0-036: в игре Esc / Start — пауза, выход — Q в паузе; вне игры Esc и Q — выход (NetGame.Pause.cs)

        void Quit(int code)
        {
            if (_quitPending)
            {
                return;
            }

            _quitPending = true;
            Shutdown();
            if (_args.NetSmokeSeconds > 0 && code == 0 && _localWasOurs && _localExitCode != 0)
            {
                // net-smoke хоста: сервер обязан сохранить мир и выйти сам по «stop» (код 0), а не быть снятым.
                Debug.LogError("[smoke] FAILED: свой сервер вышел с кодом " +
                               (_localExitCode.HasValue ? _localExitCode.Value.ToString(CultureInfo.InvariantCulture) : "— (не вышел)"));
                code = 1;
            }

            Application.Quit(code);
        }
    }
}
