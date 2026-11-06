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
    /// Сетевой режим клиента (карточка 0-012; предсказание — 0-013): вход на сервер по UDP, мир перегенерируется
    /// по зерну из «входа принят», чужие сущности — по снимкам 30 Гц с задержкой <see cref="InterpDelayUs"/>
    /// и интерполяцией между парой снимков (docs/07, 2.9). Свой персонаж ведёт <see cref="LocalPrediction"/>:
    /// шаг вперёд ядром, сверка по AckFrame, гашение расхождений (2.6) — он в своём «сейчас», без задержки показа.
    /// Ввод летит на сервер пакетиками 60 Гц, как у ботов (Temp: <c>BotRun</c>).
    /// Кадр ведётся из <see cref="GameBoot.Update"/>: poll → состояние → ввод → предсказание → интерполяция → окно → камера → HUD.
    /// </summary>
    public sealed class NetGame
    {
        /// <summary>Задержка рисования: рисует момент на «сейчас − 120 мс» — пара снимков 30 Гц гарантирована.</summary>
        public const long InterpDelayUs = 120_000;

        /// <summary>Число хранимых снимков: 8 × 33 мс ≈ 260 мс истории — с запасом на джиттер.</summary>
        const int RingSize = 8;

        const double InputInterval = 1.0 / 60.0;
        const int MaxSendsPerFrame = 4;
        const int LoginTimeoutSec = 10;

        readonly GameBoot _g;
        readonly ClientArgs _args;
        readonly string _host;
        readonly int _port;
        readonly long _startUs = NowUs();

        readonly Snapshot[] _ring = new Snapshot[RingSize];
        readonly long[] _ringUs = new long[RingSize];
        int _ringCount;
        int _ringHead;
        long _pollUs;
        long _lastSnapshotUs;

        readonly Dictionary<int, GameObject> _ents = new Dictionary<int, GameObject>();
        readonly HashSet<int> _seen = new HashSet<int>();
        GameObject _entityRoot = null!;
        Sprite _heroSprite = null!;

        NetClient? _client;
        LiteNetTransport? _transport;
        WorldModel? _world;
        MapShape _map;
        FloatingOrigin? _origin;
        WorldView? _view;
        WorldPos _localPos;
        WorldPos _spawnPos;
        Simulation? _netRules;
        LocalPrediction? _pred;
        int _localId;
        ScreenshotRun? _screenshot;
        double _inputAccum;
        long _inGameSinceUs;
        long _hudSnaps;
        long _hudTimeUs;
        int _hudFrame;
        bool _quitPending;
        string _status = "Подключение…";

        /// <summary>Монотонные микросекунды (той же шкалы живёт транспорт и кольцо снимков).</summary>
        public static long NowUs() => Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency;

        public NetGame(GameBoot game, ClientArgs args)
        {
            _g = game;
            _args = args;
            int colon = args.Connect!.LastIndexOf(':');
            _host = args.Connect.Substring(0, colon);
            _port = int.Parse(args.Connect.Substring(colon + 1), CultureInfo.InvariantCulture);
            for (int i = 0; i < RingSize; i++)
            {
                _ring[i] = new Snapshot();
            }

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
                _status = "Сервер не найден: " + e.Message;
                Debug.LogError("[net] " + _status);
                return;
            }

            _client = new NetClient(_transport, _transport.ServerPeer, _args.Name, NetModule.Version);
            _client.SnapshotApplied = OnSnapshot;
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
                    if ((now - _startUs) / 1_000_000 >= LoginTimeoutSec)
                    {
                        _status = "Сервер не отвечает";
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
                    if (_view == null)
                    {
                        BuildWorld(_client!.Accept);
                    }

                    break;
                case NetClientState.Rejected:
                    _status = "Отказ входа: " + (_client!.RejectText ?? "неизвестно");
                    break;
                case NetClientState.Disconnected:
                    if (_view != null)
                    {
                        _status = "Связь потеряна: " + (int)(_client!.DisconnectReason ?? NetDisconnectReason.Other);
                    }

                    break;
            }

            if (_view != null && state == NetClientState.InGame)
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
            _transport?.Dispose();
        }

        // ---------- мир по зерну сервера ----------

        void BuildWorld(in LoginAcceptMessage accept)
        {
            _map = new MapShape(accept.MapWidth, accept.MapHeight, accept.Wraps);
            var sw = Stopwatch.StartNew();
            _world = new StubGenerator().Generate(_map, accept.WorldSeed, out GenStats stats);
            _localId = accept.EntityId;
            WorldPos spawn = CenterOf(accept.MapWidth / 2, accept.MapHeight / 2);
            _origin = new FloatingOrigin(_map, Torus.ChunkOf(_map, spawn));
            _view = new WorldView(_world, _origin, _g.Grid, _g.TileMeadow, _g.TileStone, _g.TileTree, _g.TileRock);
            _view.UpdateWindow(spawn);
            _localPos = spawn;
            _spawnPos = spawn;

            // Проходимость — тот же IsBlocked ядра, что в такте сервера; предсказание стартует из точки поселения.
            _netRules = new Simulation(_world, maxEntities: 1, maxEditsPerTick: 1, reserveBuildMaps: 0);
            _pred = new LocalPrediction(_map, _netRules.Solid, spawn);
            Debug.Log($"[net] вход: сущ #{_localId}, мир {_map.Width}×{_map.Height} зерно 0x{accept.WorldSeed:X}, " +
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
            // NetClient переиспользует объект — снимок надо скопировать до возврата из Poll.
            int slot = _ringHead;
            s.CopyTo(_ring[slot]);
            _ringUs[slot] = _pollUs;
            _ringHead = (slot + 1) % RingSize;
            if (_ringCount < RingSize)
            {
                _ringCount++;
            }

            _lastSnapshotUs = _pollUs;

            // Сверка своего персонажа (2.6): своя сущность в снимке — первой,AckFrame — применённый кадр ввода.
            if (_pred != null && s.TryFind(_localId, out SnapshotEntity me))
            {
                _pred.Reconcile(s.AckFrame, me.Position, _pollUs);
            }
        }

        /// <summary>Пара снимков (a, b), между которыми попадает время <paramref name="targetUs"/>; b — индекс возврата.</summary>
        bool SnapshotPair(long targetUs, out int ia, out int ib, out float frac)
        {
            ia = ib = 0;
            frac = 0f;
            int latest = (int)((_ringHead - 1L + RingSize) % RingSize);
            if (_ringCount < 2 || _ringUs[latest] <= targetUs)
            {
                return false;
            }

            int prev = (int)((latest - 1L + RingSize) % RingSize);
            for (int steps = 1; steps < _ringCount; steps++)
            {
                int i = (int)((latest - steps + RingSize) % RingSize);
                if (_ringUs[i] <= targetUs)
                {
                    long span = _ringUs[prev] - _ringUs[i];
                    ia = i;
                    ib = prev;
                    frac = span > 0 ? Mathf.Clamp01((targetUs - _ringUs[i]) / (float)span) : 1f;
                    return true;
                }

                prev = i;
            }

            return false;
        }

        /// <summary>
        /// Позиция сущности на время targetUs в форме «точка + дельта» (как <c>LocalCharacter.Interpolated</c>):
        /// <paramref name="from"/> — позиция в раннем снимке, dx/dy — доля пути до позднего (в 1/256 тайла);
        /// есть сущность только в позднем снимке — дельта нулевая. Нет нигде — false.
        /// </summary>
        bool InterpDelta(int id, long targetUs, out WorldPos from, out float dx, out float dy)
        {
            int latest = (int)((_ringHead - 1L + RingSize) % RingSize);
            if (!SnapshotPair(targetUs, out int ia, out int ib, out float frac))
            {
                bool found = _ring[latest].TryFind(id, out SnapshotEntity e);
                from = found ? e.Position : default;
                dx = dy = 0f;
                return found;
            }

            if (!_ring[ib].TryFind(id, out SnapshotEntity eb))
            {
                from = default;
                dx = dy = 0f;
                return false;
            }

            if (!_ring[ia].TryFind(id, out SnapshotEntity ea))
            {
                from = eb.Position;
                dx = dy = 0f;
                return true;
            }

            long w = (long)_map.Width * Torus.SubTilesPerTile;
            long h = (long)_map.Height * Torus.SubTilesPerTile;
            long ddx = WrapDelta(eb.Position.X - ea.Position.X, _map.Wraps ? w : 0);
            long ddy = WrapDelta(eb.Position.Y - ea.Position.Y, _map.Wraps ? h : 0);
            from = ea.Position;
            dx = ddx * frac;
            dy = ddy * frac;
            return true;
        }

        /// <summary>Кратчайший ход по оси тора; без wrapping — как есть.</summary>
        static long WrapDelta(long d, long size)
        {
            if (size <= 0)
            {
                return d;
            }

            if (d > size / 2)
            {
                return d - size;
            }

            if (d < -size / 2)
            {
                return d + size;
            }

            return d;
        }

        // ---------- кадр: ввод, персонаж, чужие сущности, камера, окно ----------

        void SendInput(long now)
        {
            NetClient c = _client!;
            _inputAccum += (double)Time.unscaledDeltaTime;
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
            long target = _lastSnapshotUs - InterpDelayUs;
            const float sub = FloatingOrigin.UnitsPerTile;
            _seen.Clear();

            // Чужие сущности: набор — поздний снимок, позиция — интерполяция пары.
            int latest = (int)((_ringHead - 1L + RingSize) % RingSize);
            Snapshot snap = _ring[latest];
            for (int i = 0; i < snap.Count; i++)
            {
                SnapshotEntity e = snap.Entities[i];
                if (e.Id == _localId || !InterpDelta(e.Id, target, out WorldPos at, out float edx, out float edy))
                {
                    continue;
                }

                _seen.Add(e.Id);
                GameObject go = GetEntity(e.Id);
                go.SetActive(true);
                go.transform.position = _origin!.ToUnity(at) + new Vector3(edx / sub, -edy / sub, 0f);
            }

            foreach (KeyValuePair<int, GameObject> kv in _ents)
            {
                if (!_seen.Contains(kv.Key))
                {
                    kv.Value.SetActive(false);
                }
            }

            // Свой персонаж: предсказание + гашение (2.6, 2.9), не интерполяция снимков.
            _pred!.RenderDelta(now, out WorldPos me, out float dx, out float dy);
            _localPos = me;
            _g.Character.gameObject.SetActive(true);
            _g.Character.position = _origin!.ToUnity(me) + new Vector3(dx / sub, -dy / sub, 0f);

            _view!.UpdateWindow(_localPos);
            _g.Camera.transform.position = _g.Character.position + GameBoot.CameraOffset;
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
            int rtt = _transport!.RoundTripMs(c.ServerPeer);
            _g.DebugText.text =
                $"в игре  сервер {_host}:{_port}  seed 0x{c.Accept.WorldSeed:X}  {_map.Width}×{_map.Height}  " +
                $"такт {c.ServerTick}  tile {t.X},{t.Y}  сущ {Math.Min(_seen.Count + 1, 99)}  окно {_view!.Count}/{ChunkWindow.MaxChunks}  " +
                $"снимки {snapsPerSec:0.0}/с  дельт {c.DeltasApplied} (не склеено {c.DeltasUnmerged})  " +
                $"rtt {(rtt >= 0 ? rtt + " мс" : "—")}  ввод {c.InputsSent}  " +
                $"предсверок {_pred?.Reconciled ?? 0} правок {_pred?.Resynced ?? 0} расх {_pred?.MaxDevUnits ?? 0}u  " +
                $"zoom x{_g.Zoom:0.00}  {fps:0} fps";
        }

        void NetSmokeCheck(long now, NetClientState state)
        {
            if (_args.NetSmokeSeconds <= 0 || _quitPending)
            {
                return;
            }

            if (state == NetClientState.Rejected || state == NetClientState.Disconnected || _transport == null)
            {
                Fail($"[smoke] FAILED: {_status}");
                return;
            }

            if (InGame && (now - _inGameSinceUs) / 1_000_000 >= Math.Max(2, _args.NetSmokeSeconds)
                && _client!.SnapshotsApplied >= 10)
            {
                // Предсказание (0-013): скрипт шёл на восток/север — персонаж обязан уйти от спавна, сверка — не «прыгать».
                Int2 d = Torus.Delta(_map, _spawnPos, _localPos);
                int moved = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
                LocalPrediction p = _pred!;
                if (moved < 2 * 256 || p.MaxDevUnits > 128)
                {
                    Fail($"[smoke] FAILED: предсказание — сдвиг {moved} unit (нужно ≥ {2 * 256}), " +
                         $"макс.расхождение {p.MaxDevUnits} unit (нужно ≤ 128)");
                    return;
                }

                if (_client.DeltasApplied == 0 || _client.DeltasUnmerged * 10 > _client.SnapshotsApplied)
                {
                    Fail($"[smoke] FAILED: дельты (0-014): применено {_client.DeltasApplied}, " +
                         $"не склеено {_client.DeltasUnmerged} при снимках {_client.SnapshotsApplied}");
                    return;
                }

                Debug.Log($"[smoke] done: вход #{_client.EntityId}, снимков {_client.SnapshotsApplied} " +
                          $"(устаревших {_client.SnapshotsStale}, потерь тактов {_client.SnapshotsMissed}), " +
                          $"ввода отправлено {_client.InputsSent}, плохих сообщений {_client.BadMessages}; " +
                          $"дельт {_client.DeltasApplied} (не склеено {_client.DeltasUnmerged}); " +
                          $"предсказание: сверок {p.Reconciled}, правок {p.Resynced}, " +
                          $"макс.расхождение {p.MaxDevUnits} unit, сдвиг {moved / 256.0:0.0} т");
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
