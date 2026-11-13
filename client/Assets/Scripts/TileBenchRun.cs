#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using UnityEngine;
using UnityEngine.Tilemaps;
using Collision = Orpheus.Core.Movement.Collision;
using Debug = UnityEngine.Debug;
using Random = System.Random; // UnityEngine.Random — статический, нужен генератор правок с зерном

namespace Orpheus.Client
{
    /// <summary>
    /// Стенд проверки 48 Tilemap (карточка 1-022, ключ <c>--tile-bench &lt;сек&gt;</c>; docs/07 1.7, docs/08 3.1 «Проверка 48 Tilemap»,
    /// приёмка 1.11: самый дальний обзор (96 тайлов, E1-1), бег через шов, поток правок — ≥ 60 fps по 1 % худших).
    /// <list type="bullet">
    /// <item>Бег по кругу X+ → X- → Y+ → Y- (по 96 тайлов, шов на третьей части, как в <see cref="SmokeRun"/>) с коллизией по миру;
    /// кадр реальный (без <c>captureDeltaTime</c>): в окне — настоящие fps, в <c>-batchmode -nographics</c> — стоимость кадра на CPU.</item>
    /// <item>Поток правок у персонажа: 20 тайлов/с (копание объекта / смена пола) и пачки по 32 тайла (линия) каждые 1,5 с.
    /// Правка ложится в мир ядра (<c>Chunk.SetLocalTile</c>) и в тот же кадр в Tilemap (<c>WorldView.ShowChunk</c> — сетевой путь,
    /// <c>ClientWorld.ChunkChanged</c>); клетка Tilemap читается сразу после — «правка в тот же кадр».</item>
    /// <item>Замеры: fps (средний, медиана, 1 % худших, минимум), время <c>ShowChunk</c> (внутри — <c>SetTilesBlock</c> 2×1024),
    /// микрозамер <c>SetTilesBlock</c> и <c>SetTile</c> на узле, видимые тайлы объектов (отрисовки в режиме Individual),
    /// выделения GC в кадре, дыр окна 0 (<see cref="SmokeRun.WindowHoles"/>), тайлы окна против ядра
    /// (<see cref="SmokeRun.CheckViewTiles"/>).</item>
    /// </list>
    /// Код выхода 0: сценарий прошёл, дыр нет, правки видны в тот же кадр, тайлы совпадают с ядром, в логе нет ошибок.
    /// Порог 60 fps в код выхода не входит — числа пишет в лог, решение «Tilemap / свой меш» фиксируется в карточке.
    /// </summary>
    public sealed class TileBenchRun : IDisposable
    {
        /// <summary>Поток одиночных правок: тайлов в секунду (карточка 1-022, шаг 1).</summary>
        public const int EditsPerSecond = 20;

        /// <summary>Пачка — линия из 32 тайлов (блок <c>SetTilesBlock</c>), каждые 1,5 с.</summary>
        public const int BatchTiles = 32;
        public const double BatchEverySec = 1.5;

        /// <summary>Радиус одиночных правок вокруг персонажа, тайлов (свой тайл и сосед — не трогаем).</summary>
        public const int EditRadius = 8;

        /// <summary>Кадров прогрева (генерация, окно, стартовые заливки) до замеров.</summary>
        public const int WarmupFrames = 120;

        /// <summary>Длина отрезка и старт до шва — как в smoke (шов на третьей части пути).</summary>
        public const int LegTiles = SmokeRun.LegTiles;
        public const int BeforeSeam = SmokeRun.BeforeSeam;

        sealed class Leg
        {
            public string Name = "";
            public Input8 Dir;
            public WorldPos Start;
            public bool Teleport;
        }

        readonly GameBoot _g;
        readonly MapShape _map;
        readonly WorldModel _world;
        readonly WorldView _view;
        readonly Random _rng = new Random(0x1022);
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly List<float> _frameMs = new List<float>();

        Leg[] _legs = Array.Empty<Leg>();
        Leg _cur = null!;
        int _leg;
        int _legsDone;
        long _legStartTick;
        int _phase; // 0 — ждать окно, 1 — бег, 2 — остановка
        int _wait;
        bool _done;
        int _errors;
        int _frames;
        long _prevTs;
        int _warmupLeft = WarmupFrames;

        // Замеры
        double _editBudget;
        double _nextBatchAt;
        int _tilesEdited, _batches;
        int _showChunks;
        double _showUsSum, _showUsMax;
        long _showCount;
        int _sameFrameBad;
        int _holes, _maxWindow;
        int _visibleObjects; // видимые тайлы объектов в окне (отрисовки Individual) — раз в секунду
        long _gcMark, _gcEnd;
        double _microBlockUs, _microTileUs;
        int _microBlockOps, _microTileOps;
        double _lastReportAt;
        int _framesAtReport;
        int _tileMismatches, _tilesChecked;

        public TileBenchRun(GameBoot g)
        {
            _g = g;
            _map = g.Map;
            _world = g.World;
            _view = g.View;
            Application.logMessageReceived += OnLog;
            Application.targetFrameRate = -1; // реальные кадры: в окне — fps, в -nographics — стоимость кадра на CPU
            QualitySettings.vSyncCount = 0;
            _g.SetZoom(GameBoot.MinZoom); // приёмка 1.11: самый дальний обзор — 96 тайлов в ширину (E1-1; было 67)
            Debug.Log($"[tilebench] старт: Unity {Application.unityVersion}, backend {Backend()}, мир {_map.Width}², " +
                      $"зум x{GameBoot.MinZoom:0.00}, правок {EditsPerSecond}/с + пачка {BatchTiles} каждые {BatchEverySec:0.0} с, " +
                      $"прогрев {WarmupFrames} кадров, порог 16,67 мс/кадр (60 fps по 1 % худших)");
            PlanLegs();
        }

        public void Dispose() => Application.logMessageReceived -= OnLog;

        // E1-4, --bench-render: кадр камеры в текстуру 1920 × 1080 и ожидание видеокарты (чтение пикселя) — время кадра
        // включает отрисовку и без окна (-batchmode). Без ключа стенд меряет только CPU, как раньше.
        RenderTexture? _benchRt;
        Texture2D? _benchPixel;

        void RenderFrame()
        {
            if (!_g.Args.BenchRender)
            {
                return;
            }

            if (_benchRt == null)
            {
                _benchRt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                _benchPixel = new Texture2D(1, 1, TextureFormat.RGB24, false);
                Debug.Log($"[tilebench] отрисовка: каждый кадр камера → текстура 1920x1080 + чтение пикселя, устройство {SystemInfo.graphicsDeviceType}");
            }

            Camera cam = _g.Camera;
            RenderTexture? prevTarget = cam.targetTexture;
            RenderTexture? prevActive = RenderTexture.active;
            cam.targetTexture = _benchRt;
            cam.Render();
            RenderTexture.active = _benchRt;
            _benchPixel!.ReadPixels(new Rect(960, 540, 1, 1), 0, 0);
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
        }

        static string Backend()
        {
#if ENABLE_IL2CPP
            return "IL2CPP";
#else
            return "Mono";
#endif
        }

        void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                _errors++;
            }
        }

        void Fail(string msg) => Debug.LogError("[tilebench] FAIL " + msg);

        // ---------- План: полосы как в smoke, круг X+ → X- → Y+ → Y- (бесконечно до конца прогона) ----------

        void PlanLegs()
        {
            int w = _map.Width;
            int before = w - BeforeSeam;
            int after = LegTiles - BeforeSeam;
            bool laneX = FindLane(horizontal: true, out int row);
            bool laneY = FindLane(horizontal: false, out int col);
            if (!laneX) row = w / 2;
            if (!laneY) col = w / 2;
            Debug.Log($"[tilebench] полоса X: {(laneX ? "строка " + row : "нет чистой — без коллизии")}; " +
                      $"полоса Y: {(laneY ? "столбец " + col : "нет чистой — без коллизии")}");
            _laneX = laneX;
            _laneY = laneY;
            _laneRow = row;
            _laneCol = col;
            _legs = new[]
            {
                new Leg { Name = "X+ через шов", Dir = Input8.East, Start = _g.TileCenter(new TileCoord(before, row)), Teleport = true },
                new Leg { Name = "X- через шов", Dir = Input8.West, Start = _g.TileCenter(new TileCoord(after, row)) },
                new Leg { Name = "Y+ через шов", Dir = Input8.South, Start = _g.TileCenter(new TileCoord(col, before)), Teleport = true },
                new Leg { Name = "Y- через шов", Dir = Input8.North, Start = _g.TileCenter(new TileCoord(col, after)) },
            };
        }

        bool _laneX, _laneY;
        int _laneRow, _laneCol; // полосы бега: строка X-полосы и столбец Y-полосы — стены там не ставим

        // Полоса без препятствий через шов — поиск от середины мира наружу (как в smoke, повторяемо).
        bool FindLane(bool horizontal, out int lane)
        {
            int w = _map.Width;
            for (int k = 0; k < w; k++)
            {
                int l = (w / 2 + ((k & 1) == 0 ? k / 2 : -(k / 2 + 1))) & (w - 1);
                bool clear = true;
                for (int s = -BeforeSeam - 1; s <= LegTiles - BeforeSeam + 1 && clear; s++)
                {
                    int along = s & (w - 1);
                    var t = horizontal ? new TileCoord(along, l) : new TileCoord(l, along);
                    clear = !_g.Rules.IsBlocked(_map, t);
                }

                if (clear)
                {
                    lane = l;
                    return true;
                }
            }

            lane = 0;
            return false;
        }

        // ---------- Кадр ----------

        public void BeforeFrame()
        {
            if (_done)
            {
                return;
            }

            double sec = _clock.Elapsed.TotalSeconds;
            if (_leg == 0 || (_phase == 2 && _wait <= 0))
            {
                if (sec >= _g.Args.TileBenchSeconds)
                {
                    Finish();
                    return;
                }

                NextLeg();
            }

            Leg leg = _cur;
            if (_phase == 0)
            {
                _g.ScriptedInput = InputFrame.None;
                if (--_wait <= 0)
                {
                    _phase = 1;
                    _legStartTick = _g.Player.Ticks;
                }
            }

            if (_phase == 1)
            {
                long done = _g.Player.Ticks - _legStartTick;
                _g.ScriptedInput = done < LegTiles * MovementTick.TicksPerTile ? new InputFrame(leg.Dir) : InputFrame.None;
                if (done >= LegTiles * MovementTick.TicksPerTile)
                {
                    _phase = 2;
                    _wait = 4;
                }
            }
            else if (_phase == 2)
            {
                _g.ScriptedInput = InputFrame.None;
                _wait--;
                if (_wait == 0)
                {
                    FinishLeg(leg);
                }
            }

            if (_warmupLeft > 0)
            {
                _warmupLeft--;
                if (_warmupLeft == 0)
                {
                    _gcMark = GC.GetTotalMemory(true);
                    MicroBench();
                }

                return; // правки и замеры — после прогрева
            }

            if (sec >= _g.Args.TileBenchSeconds)
            {
                return; // сценарий остановился, AfterFrame закончит прогон
            }

            if (_nextBatchAt == 0)
            {
                _nextBatchAt = sec + BatchEverySec;
            }

            _editBudget += EditsPerSecond * Time.deltaTime;
            while (_editBudget >= 1)
            {
                _editBudget -= 1;
                ApplySingleEdit();
            }

            if (sec >= _nextBatchAt)
            {
                _nextBatchAt = sec + BatchEverySec;
                ApplyBatch();
            }

            if (sec - _lastReportAt >= 5)
            {
                if (_lastReportAt > 0)
                {
                    double fps = (_frames - _framesAtReport) / (sec - _lastReportAt);
                    Debug.Log($"[tilebench] t={sec:0.0} с: окно {_view.Count} чанков (макс {_maxWindow}), правок {_tilesEdited}, " +
                              $"пачек {_batches}, заливок чанков {_showChunks}, fps за 5 с {fps:0.0}");
                }

                _lastReportAt = sec;
                _framesAtReport = _frames;
            }

            if ((int)sec != _visibleObjectsTick)
            {
                _visibleObjectsTick = (int)sec;
                _visibleObjects = VisibleObjectTiles();
            }
        }

        int _visibleObjectsTick;

        void NextLeg()
        {
            _leg++;
            Leg leg = _legs[(_leg - 1) & 3];
            _cur = leg;
            _g.Player.Solid = (leg.Dir.DX != 0 ? _laneX : _laneY) ? _g.Rules.Solid : Collision.Empty;
            if (leg.Teleport || _g.Player.Position != leg.Start)
            {
                _g.Teleport(leg.Start);
            }

            _phase = 0;
            _wait = 3;
        }

        void FinishLeg(Leg leg)
        {
            int units = LegTiles * Torus.SubTilesPerTile;
            WorldPos expected = Torus.Offset(_map, leg.Start, new Int2(leg.Dir.DX * units, leg.Dir.DY * units));
            WorldPos core = _g.Player.Position;
            bool posOk = core == expected;
            Debug.Log($"[tilebench] leg {leg.Name}: старт {leg.Start} → ядро {core}, ожидалось {expected} {(posOk ? "ok" : "НЕ СОВПАЛО")}");
            if (!posOk)
            {
                Fail($"{leg.Name}: позиция ядра {core}, ожидалась {expected}");
            }

            _legsDone++;
        }

        public void AfterFrame()
        {
            if (_done)
            {
                return;
            }

            RenderFrame();
            long ts = Stopwatch.GetTimestamp();
            if (_prevTs != 0)
            {
                double ms = (ts - _prevTs) * 1000.0 / Stopwatch.Frequency;
                if (_warmupLeft == 0)
                {
                    _frameMs.Add((float)ms);
                }

                int n = _view.Count;
                if (n > _maxWindow)
                {
                    _maxWindow = n;
                }

                _holes += SmokeRun.WindowHoles(_map, _view, _g.Player.Position, msg => Fail($"кадр {_frames}: {msg}"));
            }

            _prevTs = ts;
            _frames++;

            if (_warmupLeft == 0 && _clock.Elapsed.TotalSeconds >= _g.Args.TileBenchSeconds)
            {
                Finish();
            }
        }

        // ---------- Правки ----------

        /// <summary>Одиночная правка у персонажа: копание объекта, а на пустом тайле — смена пола (трава ↔ камень).
        /// В полосе бега не строим: стена в 1–2 тайлах от персонажа остановила бы ногу (перпендикуляр — ≥ 3 тайлов).</summary>
        void ApplySingleEdit()
        {
            TileCoord c0 = Torus.TileOf(_map, _g.Player.Position);
            int dx = _rng.Next(-EditRadius, EditRadius + 1);
            int dy = _rng.Next(-EditRadius, EditRadius + 1);
            if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1)
            {
                return; // под ногами не трогаем
            }

            bool horiz = _cur.Dir.DX != 0;
            if ((horiz ? Math.Abs(dy) : Math.Abs(dx)) < 3)
            {
                return; // по оси, вдоль которой идёт нога, рядом не ставим
            }

            TileCoord t = Torus.Wrap(_map, new TileCoord(c0.X + dx, c0.Y + dy));
            ApplyTile(t, dig: true, toggleFloor: true);
        }

        /// <summary>Пачка: линия 32 тайла параллельно бегу, в 4 тайлах в сторону (чтобы не перекрывать полосу).</summary>
        void ApplyBatch()
        {
            TileCoord c0 = Torus.TileOf(_map, _g.Player.Position);
            bool horiz = _cur.Dir.DX != 0;
            for (int i = 0; i < BatchTiles; i++)
            {
                TileCoord t = horiz
                    ? Torus.Wrap(_map, new TileCoord(c0.X - BatchTiles / 2 + i, c0.Y - 4))
                    : Torus.Wrap(_map, new TileCoord(c0.X + 4, c0.Y - BatchTiles / 2 + i));
                ApplyTile(t, dig: true, toggleFloor: false);
            }

            _batches++;
        }

        /// <summary>
        /// Правка тайла в мире ядра + <c>ShowChunk</c> в тот же кадр (сетевой путь) + сверка клетки Tilemap в этом же кадре.
        /// Возвращает координату чанка (для группировки пачки).
        /// </summary>
        ChunkCoord ApplyTile(TileCoord t, bool dig, bool toggleFloor)
        {
            ChunkCoord c = Torus.ChunkOf(_map, t);
            Chunk? chunk = _world.FindChunk(c);
            if (chunk == null)
            {
                return c;
            }

            int lx = t.X & (Torus.ChunkSize - 1);
            int ly = t.Y & (Torus.ChunkSize - 1);
            ushort index = (ushort)Chunk.IndexOf(lx, ly);
            ushort obj = chunk.GetObject(index);
            byte floor = chunk.GetFloor(index);
            if (dig && obj != ObjectId.Empty && !ObjectId.IsPart(obj))
            {
                obj = ObjectId.Empty; // копание
            }
            else if (obj == ObjectId.Empty && Math.Abs(t.Y - _laneRow) >= 2 && Math.Abs(t.X - _laneCol) >= 2)
            {
                obj = ObjectId.StoneWall; // постановка (стена рисуется камнем — WorldView.ObjectTile); в полосах бега не строим — стена на полосе останавливает ногу (первый прогон 1-022)
            }

            if (toggleFloor)
            {
                floor = (byte)(floor == FloorId.Stone ? FloorId.Grass : FloorId.Stone);
            }
            else
            {
                floor = FloorId.Stone;
            }

            chunk.SetLocalTile(new TileEdit(index, floor, obj));
            TimeShowChunk(c);
            _tilesEdited++;

            // Правка в тот же кадр: клетка Tilemap читается сразу после ShowChunk.
            if (_view.TryGetNode(c, out WorldView.ChunkNode node))
            {
                Vector3Int cell = WorldView.CellOf(lx, ly);
                if (node.Floor.GetTile(cell) != _view.FloorTile(floor) || node.Objects.GetTile(cell) != _view.ObjectTile(obj)
                    || !WorldView.GroundShows(node, lx, ly, floor))
                {
                    _sameFrameBad++;
                    Fail($"правка {t} не видна в тот же кадр: тайл Tilemap ≠ ядро");
                }
            }

            return c;
        }

        void TimeShowChunk(ChunkCoord c)
        {
            long t0 = Stopwatch.GetTimestamp();
            _view.ShowChunk(c);
            double us = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
            _showChunks++;
            _showCount++;
            _showUsSum += us;
            if (us > _showUsMax)
            {
                _showUsMax = us;
            }
        }

        /// <summary>Микрозамер API Tilemap на узле под персонажем: <c>SetTilesBlock</c> 32 × 32 и одиночный <c>SetTile</c>.</summary>
        void MicroBench()
        {
            ChunkCoord c = Torus.ChunkOf(_map, _g.Player.Position);
            if (!_view.TryGetNode(c, out WorldView.ChunkNode node))
            {
                Fail("микрозамер: нет узла под персонажем");
                return;
            }

            var block = new BoundsInt(0, -Torus.ChunkSize, 0, Torus.ChunkSize, Torus.ChunkSize, 1);
            var buf = new TileBase?[Torus.ChunkSize * Torus.ChunkSize];
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] = _view.FloorTile(FloorId.StoneFloor); // E1-4: плитки пола — только у построенных полов
            }

            _microBlockOps = 200;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < _microBlockOps; i++)
            {
                node.Floor.SetTilesBlock(block, buf);
            }

            _microBlockUs = sw.Elapsed.TotalMilliseconds * 1000.0 / _microBlockOps;

            _microTileOps = 2000;
            var cell = new Vector3Int(1, -2, 0);
            sw.Restart();
            for (int i = 0; i < _microTileOps; i++)
            {
                node.Floor.SetTile(cell, i % 2 == 0 ? _view.FloorTile(FloorId.StoneFloor) : null);
            }

            _microTileUs = sw.Elapsed.TotalMilliseconds * 1000.0 / _microTileOps;
            node.HasBuiltFloor = true; // микрозамер писал в Tilemap пола мимо WorldView — заливка ниже обязана его очистить
            _view.ShowChunk(c); // узел под персонажем залит заново из ядра: микрозамер не оставляет следов в Tilemap
            Debug.Log($"[tilebench] микро: SetTilesBlock 32×32 — {_microBlockUs:0.0} мкс/вызов ({_microBlockOps} шт), " +
                      $"SetTile — {_microTileUs:0.00} мкс/вызов ({_microTileOps} шт, чередование)");
        }

        /// <summary>Видимые тайлы объектов в окне: отрисовки в режиме Individual (по тайлу на отрисовку).</summary>
        int VisibleObjectTiles()
        {
            int n = 0;
            ReadOnlySpan<ChunkCoord> chunks = _view.Chunks;
            for (int k = 0; k < chunks.Length; k++)
            {
                if (_view.TryGetNode(chunks[k], out WorldView.ChunkNode node))
                {
                    n += node.Objects.GetUsedTilesCount();
                }
            }

            return n;
        }

        // ---------- Итог ----------

        void Finish()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            _g.ScriptedInput = null;
            _gcEnd = GC.GetTotalMemory(true);

            int checkedTiles, samples;
            _tileMismatches += SmokeRun.CheckViewTiles(_view, _world, _map, "tilebench", out checkedTiles, out samples,
                out bool sawStoneFloor, line => Debug.Log("[tilebench] " + line), msg => Fail(msg));
            _tilesChecked = checkedTiles;

            var f = new List<float>(_frameMs);
            f.Sort();
            int n = f.Count;
            double avg = 0;
            foreach (float ms in f)
            {
                avg += ms;
            }

            avg = n > 0 ? avg / n : 0;
            double median = n > 0 ? f[n / 2] : 0;
            double p99 = n > 0 ? f[Math.Min(n - 1, (int)(n * 0.99))] : 0;
            double worst = n > 0 ? f[n - 1] : 0;
            double gcKbPerFrame = n > 0 ? (_gcEnd - _gcMark) / (double)n / 1024.0 : 0;

            Debug.Log($"[tilebench] кадры: {n} (прогрев {WarmupFrames} не считан), средний {avg:0.000} мс ({1000.0 / Math.Max(avg, 1e-6):0.0} fps), " +
                      $"медиана {median:0.000} мс, 1 % худших {p99:0.000} мс ({1000.0 / Math.Max(p99, 1e-6):0.0} fps), худший {worst:0.000} мс; " +
                      $"порог 16,67 мс: {(p99 <= 16.67 ? "держит" : "НЕ держит")}");
            Debug.Log($"[tilebench] правки: тайлов {_tilesEdited} (одиночных + пачки), пачек по {BatchTiles} — {_batches}, " +
                      $"заливок чанков (ShowChunk→SetTilesBlock×2) {_showChunks}, в среднем {_showUsSum / Math.Max(_showCount, 1):0.0} мкс, " +
                      $"худший {_showUsMax:0.0} мкс; не видно в тот же кадр: {_sameFrameBad}");
            double paintUs = _showUsSum / Math.Max(_showCount, 1);
            double paintsPerFrame = (EditsPerSecond + BatchTiles / BatchEverySec) / 60.0; // при 60 fps
            Debug.Log($"[tilebench] бюджет правок: заливка чанка {paintUs:0.0} мкс → поток (20/с + пачка 32 за 1,5 с) ≈ " +
                      $"{paintsPerFrame * paintUs / 1000:0.000} мс/кадр при 60 fps ({paintsPerFrame * paintUs / 1000 / 16.67:0.0 % кадра}); " +
                      $"полная перелицовка окна ({_maxWindow} чанков) ≈ {_maxWindow * paintUs / 1000:0.00} мс");
            Debug.Log($"[tilebench] окно: ног пройдено {_legsDone}, макс {_maxWindow} чанков (предел {ChunkWindow.MaxChunks}), дыр {_holes}, " +
                      $"узлов создано {_view.NodesCreated} (Tilemap-рендереров {2 * _view.NodesCreated}), мешей земли собрано {_view.GroundBuilds}, " +
                      $"видимых тайлов объектов (отрисовок Individual) ~{_visibleObjects}; GC {gcKbPerFrame:0.0} КБ/кадр; " +
                      $"тайлов сверено {_tilesChecked}, расхождений с ядром {_tileMismatches}");

            int code = _errors == 0 && _holes == 0 && _sameFrameBad == 0 && _tileMismatches == 0 && n > 0 ? 0 : 1;
            Debug.Log($"[tilebench] done: frames={n} errors={_errors} holes={_holes} sameFrameBad={_sameFrameBad} " +
                      $"tileMismatches={_tileMismatches} exit={code}");
            Application.Quit(code);
        }
    }
}
