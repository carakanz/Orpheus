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
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.WorldGen;
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
    /// он в своём «сейчас», без задержки показа. <c>--net-sim RTT,σ,потери%</c> — имитатор связи ядра под клиентом.
    /// Ввод летит на сервер пакетиками 60 Гц, как у ботов (Temp: <c>BotRun</c>).
    /// Кадр ведётся из <see cref="GameBoot.Update"/>: poll → состояние → ввод → предсказание → переезд начала → окно →
    /// персонаж и камера → чужие → HUD.
    /// </summary>
    public sealed class NetGame
    {
        const double InputInterval = 1.0 / 60.0;
        const int MaxSendsPerFrame = 4;
        const int LoginTimeoutSec = 10;

        /// <summary>Пороги net-smoke чужих (0-017): доля кадров с тем же временем показа и кадров «стоит на новейшем».</summary>
        const double MaxSmokeSameTimeShare = 0.01;
        const double MaxSmokeHoldShare = 0.05;
        const int MinSmokeRemoteSteps = 60;

        /// <summary>Порог net-smoke (0-019): правок на чистом движении и расхождение предсказания с сервером, unit.</summary>
        const int MaxSmokeCorrections = 2;
        const int MaxSmokeDevUnits = 8;

        readonly GameBoot _g;
        readonly ClientArgs _args;
        readonly string _host;
        readonly int _port;
        readonly long _startUs = NowUs();

        long _pollUs;
        RemoteInterpolation? _remote;
        long _anchoredInitial; // установка предсказания, под которую поставлено начало
        bool _fatal;           // статус окончательный (мир не совпал, не подключились) — не перетирать

        // net-smoke: свой персонаж на экране — рывок, окно — дыры (как офлайн-smoke 0-010).
        bool _measureSelf;
        WorldPos _prevDrawn;
        float _maxSelfJump;
        int _selfJumpFails;
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
        double _inputAccum;
        long _inputClockUs;
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
            int colon = args.Connect!.LastIndexOf(':');
            _host = args.Connect.Substring(0, colon);
            _port = int.Parse(args.Connect.Substring(colon + 1), CultureInfo.InvariantCulture);
            _heroSprite = _g.Character.GetChild(0).GetComponent<SpriteRenderer>().sprite;
            _entityRoot = new GameObject("Entities");
        }

        /// <summary>Старт: UDP-соединение с сервером. Хост не найден — сразу «отказ» (окно с текстом).</summary>
        public void StartConnect()
        {
            Debug.Log($"[net] подключение к {_host}:{_port} (версия {NetModule.Version}, имя «{_args.Name}»)");
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
                _netSim = new NetSimTransport(_transport, NetSimSettings.FromRtt(_args.NetSimRttMs, _args.NetSimJitterMs, _args.NetSimLossPercent, 0x0019));
                _link = _netSim;
                Debug.Log($"[net] имитатор связи: {_netSim.Settings}");
            }

            _client = new NetClient(_link, _transport.ServerPeer, _args.Name, NetModule.Version);
            _client.SnapshotApplied = OnSnapshot;
            if (_args.NetSmokeSeconds > 0)
            {
                // Как игра: 60 кадров/с, а не ~80 000 в пакетном режиме (замечание 9 проверки 0-012).
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
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

            NetClientState state = _client?.State ?? NetClientState.Disconnected;
            switch (state)
            {
                case NetClientState.Connecting:
                    if ((now - _startUs) / 1_000_000 >= LoginTimeoutSec && !_fatal)
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
                    if ((now - _startUs) / 1_000_000 >= LoginTimeoutSec)
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
                    _status = _view != null
                        ? "Связь потеряна: " + (_client?.DisconnectReason ?? NetDisconnectReason.Other)
                        : $"Не удалось подключиться к {_host}:{_port}: {(_client?.DisconnectReason ?? NetDisconnectReason.Other)}";
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

            _screenshot?.AfterFrame();
            NetSmokeCheck(now, state);
            UpdateHud(now);
            QuitIfEscaped();
        }

        public void Shutdown()
        {
            _client?.Disconnect();
            (_link ?? _transport)?.Dispose();
        }

        // ---------- мир по зерну сервера ----------

        void BuildWorld(in LoginAcceptMessage accept)
        {
            _map = new MapShape(accept.MapWidth, accept.MapHeight, accept.Wraps);
            var sw = Stopwatch.StartNew();
            _world = new StubGenerator().Generate(_map, accept.WorldSeed, out GenStats stats);
            ulong genHash = StateHash.Generation(_world);
            if (genHash != accept.GenerationHash)
            {
                // Мир клиента ≠ миру сервера (другой генератор или зерно): ходить нельзя — коллизия предсказания и картинка
                // разойдутся с сервером (замечание 3 проверки 0-012; docs/07 3.4 — backlog [док]).
                _fatal = true;
                _status = $"Мир не совпал с сервером: хэш генерации 0x{genHash:X16}, у сервера 0x{accept.GenerationHash:X16} " +
                          $"(зерно 0x{accept.WorldSeed:X}, генератор v{_world.GeneratorVersion}) — нужна та же сборка";
                Debug.LogError("[net] " + _status);
                _client!.Disconnect();
                return;
            }

            _localId = accept.EntityId;
            WorldPos spawn = CenterOf(accept.MapWidth / 2, accept.MapHeight / 2);
            _origin = new FloatingOrigin(_map, Torus.ChunkOf(_map, spawn));
            _view = new WorldView(_world, _origin, _g.Grid, _g.TileMeadow, _g.TileStone, _g.TileTree, _g.TileRock);
            _view.UpdateWindow(spawn);
            _localPos = spawn;

            // Проходимость — тот же IsBlocked ядра, что в такте сервера; предсказание стартует из точки поселения.
            _netRules = new Simulation(_world, maxEntities: 1, maxEditsPerTick: 1, reserveBuildMaps: 0);
            _pred = new LocalPrediction(_map, _netRules.Solid, spawn);
            _remote = new RemoteInterpolation(_map);
            Debug.Log($"[net] вход: сущ #{_localId}, мир {_map.Width}×{_map.Height} зерно 0x{accept.WorldSeed:X}, " +
                      $"хэш генерации 0x{genHash:X16} = серверу, " +
                      $"такт {accept.ServerTick}; генерация {sw.Elapsed.TotalMilliseconds:0} мс " +
                      $"(деревьев {stats.Trees}, скал {stats.Rocks})");
            _g.Character.gameObject.SetActive(true);
            _g.Character.position = _origin.ToUnity(spawn);
            _inGameSinceUs = NowUs();
            _hudTimeUs = _inGameSinceUs;
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
            // Часы ввода — монотонные мкс, не Time.unscaledDeltaTime: в -batchmode Unity завышает время очень коротких
            // кадров (net-smoke 0-019: ~79 кадров ввода в секунду вместо 60), и сервер, шагающий 2 кадра за такт, отстаёт.
            if (_inputClockUs == 0)
            {
                _inputClockUs = now;
            }

            _inputAccum += Math.Max(0, now - _inputClockUs) / 1_000_000.0; // шаг часов назад — ноль
            _inputClockUs = now;
            int sent = 0;
            while (_inputAccum >= InputInterval && sent < MaxSendsPerFrame)
            {
                InputFrame frame = _args.NetSmokeSeconds > 0 ? ScriptedSmokeFrame(now) : GameBoot.ToFrame(_g.Move.ReadValue<Vector2>());
                c.SendFrame(frame);
                _pred!.Sent(c.Frame, frame);
                _inputAccum -= InputInterval;
                sent++;
            }

            if (_inputAccum > 1.0)
            {
                _inputAccum = 0; // окно свернули — не «прыгаем» ретрансляцией старых кадров
            }
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
                _view!.Clear();
                _origin!.Rebase(myChunk, seam: false);
                _measureSelf = false;
            }
            else if (_origin!.NeedsRebase(myChunk, out bool seam))
            {
                // Переезд начала, как в офлайне (GameBoot.Update, 0-010): узлы окна, персонаж и чужие пересчитываются от
                // нового якоря в этом же кадре, камера ставится за персонажем — на экране рывка нет.
                _origin.Rebase(myChunk, seam);
            }

            _view!.UpdateWindow(me);
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
                }
            }

            foreach (KeyValuePair<int, GameObject> kv in _ents)
            {
                if (!_seen.Contains(kv.Key))
                {
                    kv.Value.SetActive(false);
                }
            }

            if (_args.NetSmokeSeconds > 0)
            {
                SmokeFrame(me);
            }
        }

        /// <summary>
        /// net-smoke, каждый кадр: окно чанков без дыр и свой персонаж на экране без рывка — позиция относительно
        /// нарисованного узла чанка под ним (как офлайн-smoke 0-010), шаг ≤ 6 т/с × 1/30 с × 1,1.
        /// </summary>
        void SmokeFrame(WorldPos me)
        {
            _maxWindow = Math.Max(_maxWindow, _view!.Count);
            _holes += SmokeRun.WindowHoles(_map, _view, me, msg => Debug.LogError($"[smoke] кадр {_renderFrames}: {msg}"));
            WorldPos drawn = SmokeRun.DrawnAt(_map, _view, _origin!, me, _g.Character.position, out bool ok);
            if (!ok)
            {
                _measureSelf = false;
                return;
            }

            if (_measureSelf)
            {
                Int2 j = Torus.Delta(_map, _prevDrawn, drawn);
                float jump = Mathf.Sqrt((float)j.LengthSquared) / Torus.SubTilesPerTile;
                _maxSelfJump = Mathf.Max(_maxSelfJump, jump);
                if (jump > SmokeRun.MaxJumpTiles && _selfJumpFails++ < 5)
                {
                    Debug.LogError($"[smoke] кадр {_renderFrames}: рывок своего {jump:0.000} т (предел {SmokeRun.MaxJumpTiles:0.000}), якорь {_origin!.Anchor}");
                }
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
            _g.DebugText.text =
                $"в игре  сервер {_host}:{_port}  seed 0x{c.Accept.WorldSeed:X}  {_map.Width}×{_map.Height}  " +
                $"такт {c.ServerTick}  tile {t.X},{t.Y}  сущ {Math.Min(_seen.Count + 1, 99)}  окно {_view!.Count}/{ChunkWindow.MaxChunks}  " +
                $"снимки {snapsPerSec:0.0}/с  дельт {c.DeltasApplied} (не склеено {c.DeltasUnmerged})  " +
                $"rtt {(rtt >= 0 ? rtt + " мс" : "—")}  ввод {c.InputsSent}  " +
                $"предсверок {_pred?.Reconciled ?? 0} правок {_pred?.Resynced ?? 0} расх {_pred?.MaxDevUnits ?? 0}u" +
                $"  показ −{(_remote?.BufferUs ?? 0) / 1000} мс (стоит {_remote?.HoldFrames ?? 0})  якорь {_origin?.Anchor.X},{_origin?.Anchor.Y}" +
                $"{(_pred != null && _pred.Overflows > 0 ? $" (задержка > {LocalPrediction.BufferSize / 60} с: {_pred.Overflows})" : "")}  " +
                $"zoom x{_g.Zoom:0.00}  {fps:0} fps";
        }

        void NetSmokeCheck(long now, NetClientState state)
        {
            if (_args.NetSmokeSeconds <= 0 || _quitPending)
            {
                return;
            }

            if (_fatal || state == NetClientState.Rejected || state == NetClientState.Disconnected || _transport == null)
            {
                Fail($"[smoke] FAILED: {_status}");
                return;
            }

            if (InGame && (now - _inGameSinceUs) / 1_000_000 >= Math.Max(2, _args.NetSmokeSeconds)
                && _client!.SnapshotsApplied >= 10)
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
                string remote = $"чужие: кадров {r.Frames}, с тем же временем показа {r.SameTimeFrames} ({sameShare:P2}), " +
                                $"стоит на новейшем {r.HoldFrames} ({holdShare:P2}), шагов {r.Steps}, шаг max {r.MaxStepUnits:0.0}/256 т " +
                                $"(к пределу {r.MaxStepRatio:0.00}), сверх предела {r.StepViolations}, пар через шов {r.SeamSamples}, " +
                                $"запас показа {r.BufferUs / 1000} мс, перестановок часов {r.ClockResets}, ход {r.ClockRate:+0.0000;-0.0000}";
                Debug.Log("[smoke] " + remote);
                if (sameShare > MaxSmokeSameTimeShare || holdShare > MaxSmokeHoldShare || r.StepViolations > 0 || r.Steps < MinSmokeRemoteSteps
                    || (_args.NetSmokeSeam && r.SeamSamples == 0))
                {
                    Fail($"[smoke] FAILED: интерполяция чужих — {remote} (нужно: тех же ≤ {MaxSmokeSameTimeShare:P0}, стоит ≤ {MaxSmokeHoldShare:P0}, " +
                         $"сверх предела 0, шагов ≥ {MinSmokeRemoteSteps}{(_args.NetSmokeSeam ? ", пар через шов ≥ 1" : "")})");
                    return;
                }

                string seamLine = $"шов: переездов начала {_origin!.Rebases}, на шве {_origin.SeamRebases}, рывок своего max {_maxSelfJump:0.000} т/кадр " +
                                  $"(предел {SmokeRun.MaxJumpTiles:0.000}), окно max {_maxWindow} чанков, дыр {_holes}, якорь {_origin.Anchor}";
                Debug.Log("[smoke] " + seamLine);
                if (_holes > 0 || _selfJumpFails > 0 || _maxWindow > ChunkWindow.MaxChunks || (_args.NetSmokeSeam && _origin.SeamRebases == 0))
                {
                    Fail($"[smoke] FAILED: {seamLine}{(_args.NetSmokeSeam ? " (нужен переезд на шве ≥ 1)" : "")}");
                    return;
                }

                Debug.Log($"[smoke] world: хэш генерации 0x{StateHash.Generation(_world!):X16} = серверу (зерно 0x{_client.Accept.WorldSeed:X})");
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
            else if ((now - _startUs) / 1_000_000 >= _args.NetSmokeSeconds + LoginTimeoutSec)
            {
                Fail($"[smoke] FAILED: тайм-аут, состояние — {_status}");
            }
        }

        void Fail(string logLine)
        {
            Debug.LogError(logLine);
            Quit(1);
        }

        void QuitIfEscaped()
        {
            if (_args.NetSmokeSeconds == 0 && Keyboard.current?.escapeKey.wasPressedThisFrame == true)
            {
                Quit(0);
            }
        }

        void Quit(int code)
        {
            if (_quitPending)
            {
                return;
            }

            _quitPending = true;
            Shutdown();
            Application.Quit(code);
        }
    }
}
