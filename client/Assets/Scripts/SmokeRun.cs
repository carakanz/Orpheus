#nullable enable
using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.WorldGen;
using UnityEngine;
using UnityEngine.Tilemaps;
using Collision = Orpheus.Core.Movement.Collision;

namespace Orpheus.Client
{
    /// <summary>
    /// Дымовая проверка сборки <c>--smoke</c> (карточка 0-010, шаг 6; запускает <c>tools/build_client.ps1 -Smoke</c>).
    /// <list type="number">
    /// <item>Мир из ядра нарисован верно: каждый тайл каждого чанка окна в Tilemap совпадает с миром ядра; несколько
    /// тайлов разных видов — строками <c>[smoke] sample</c> для сверки с картинкой <c>gen_preview</c> того же зерна
    /// (<c>tools/check_smoke_tiles.py</c>).</item>
    /// <item>Прогон по прямой скриптовым вводом через шов: по X на восток и обратно, по Y на юг и обратно, по 96 тайлов
    /// (480 тактов, шов — через 24 тайла) — на полосе без препятствий; если на шве такой нет — без коллизии (пишется в лог).</item>
    /// <item>Каждый кадр: позиция персонажа и камеры, восстановленная относительно нарисованного чанка, сдвигается
    /// не больше чем на 6 т/с × 1/30 с × 1,1 = 0,22 тайла (рывок), совпадает с интерполяцией ядра; в окне ≤ 48 чанков
    /// и нет дыр в блоке 6 × 4. Шаг персонажа на бегу за кадр ≤ 6 т/с × 1/60 с × 1,1 = 0,11 тайла — доказательство
    /// интерполяции (без неё шаг = 0,203). Место каждого узла окна сверяется с узлом центра блока по смещению из
    /// индексов блока (i·32, −j·32), независимо от <c>ChunkToUnity</c> — доказательство отсутствия дыр у шва.</item>
    /// <item>После каждого отрезка позиция по ядру — ровно старт + 96 тайлов, нарисованная — та же; начало переезжало
    /// и на шве, и по расстоянию.</item>
    /// </list>
    /// Время кадра фиксировано (<see cref="Time.captureDeltaTime"/> = 1/60 с): проверка не зависит от скорости машины.
    /// Код выхода 0 — все проверки прошли и в логе нет ошибок; 1 — иначе.
    /// </summary>
    public sealed class SmokeRun : IDisposable
    {
        public const float FrameSeconds = 1f / 60f;

        /// <summary>Рывок в кадре: 6 т/с × 1/30 с × 1,1 (docs/08, 3.0, приёмка 0.2).</summary>
        public const float MaxJumpTiles = 6f / 30f * 1.1f;

        /// <summary>
        /// Шаг персонажа на бегу за кадр: 6 т/с × 1/60 с × 1,1 = 0,11 тайла. Кадр smoke — 1/60 с, персонаж шагает
        /// по кадрам ввода (07, 2.4), поэтому без интерполяции шаг за кадр = 6 т/с × 1/30 с = 0,203 > 0,11 —
        /// проверка ловит отсутствие интерполяции (замечание П по M3).
        /// </summary>
        public const float MaxStepTiles = 6f / 60f * 1.1f;

        /// <summary>Длина отрезка: 96 тайлов = 3 чанка = 480 тактов базового бега.</summary>
        public const int LegTiles = 96;

        /// <summary>
        /// Старт отрезка — за <see cref="BeforeSeam"/> тайлов до шва: шов на трети пути, дальше ещё 2 чанка —
        /// начало переезжает и на шве, и при уходе от якоря дальше чанка (обе ветки <see cref="FloatingOrigin.NeedsRebase"/>).
        /// </summary>
        public const int BeforeSeam = 24;

        /// <summary>Допуск «нарисовано = ядро» — 2/256 тайла (округление float → 1/256).</summary>
        public const int MatchUnits = 2;

        /// <summary>Предел кадров на всю проверку (страховка от зависания сценария).</summary>
        public const int MaxFrames = 20000;

        sealed class Leg
        {
            public string Name = "";
            public Input8 Dir;
            public WorldPos Start;
            public bool Teleport;
        }

        readonly GameBoot _g;
        readonly MapShape _map;
        Leg[] _legs = Array.Empty<Leg>();
        int _leg = -1;
        long _legStartTick;
        int _phase; // 0 — ждать окно, 1 — бег, 2 — остановка
        int _wait;
        int _errors;
        int _frames;
        bool _measure;
        WorldPos _prevChar, _prevCam;
        float _maxJumpChar, _maxJumpCam;
        int _maxDrift, _maxStep;
        int _holes;
        int _maxWindow;
        int _rebases, _seamRebases;
        int _samples;
        int _tileMismatches, _tilesChecked;
        bool _done;

        public SmokeRun(GameBoot g)
        {
            _g = g;
            _map = g.Map;
            Application.logMessageReceived += OnLog;
            Application.targetFrameRate = -1; // время кадра фиксировано, ждать реальные 1/60 с незачем
            QualitySettings.vSyncCount = 0;
            Time.captureDeltaTime = FrameSeconds;
            Debug.Log($"[smoke] start: Unity {Application.unityVersion}, backend {Backend()}, мир {_map.Width}², " +
                      $"генерация {g.GenerationMs:0} мс, предел рывка {MaxJumpTiles:0.000} т/кадр");
            PlanLegs();
            CheckTiles("spawn");
        }

        public void Dispose() => Application.logMessageReceived -= OnLog;

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

        void Fail(string msg)
        {
            Debug.LogError("[smoke] FAIL " + msg);
        }

        // ---------- План: полосы без препятствий через шов ----------

        void PlanLegs()
        {
            int w = _map.Width;
            int before = w - BeforeSeam;
            int after = LegTiles - BeforeSeam;
            bool laneX = FindLane(horizontal: true, out int row);
            bool laneY = FindLane(horizontal: false, out int col);
            if (!laneX) row = w / 2;
            if (!laneY) col = w / 2;
            Debug.Log($"[smoke] полоса X: {(laneX ? "строка " + row : "нет чистой — без коллизии")}; " +
                      $"полоса Y: {(laneY ? "столбец " + col : "нет чистой — без коллизии")}");
            _laneX = laneX;
            _laneY = laneY;
            _legs = new[]
            {
                new Leg { Name = "X+ через шов", Dir = Input8.East, Start = _g.TileCenter(new TileCoord(before, row)), Teleport = true },
                new Leg { Name = "X- через шов", Dir = Input8.West, Start = _g.TileCenter(new TileCoord(after, row)) },
                new Leg { Name = "Y+ через шов", Dir = Input8.South, Start = _g.TileCenter(new TileCoord(col, before)), Teleport = true },
                new Leg { Name = "Y- через шов", Dir = Input8.North, Start = _g.TileCenter(new TileCoord(col, after)) },
            };
        }

        bool _laneX, _laneY;

        // Полоса: строка (или столбец), где тайлы от W − BeforeSeam − 1 до LegTiles − BeforeSeam + 1 через шов проходимы — круг 0,4 по центру тайла
        // соседних строк не касается. Поиск от середины мира наружу — повторяемо для зерна.
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

            if (_leg < 0 || (_phase == 2 && _wait <= 0))
            {
                NextLeg();
                if (_done)
                {
                    return;
                }
            }

            Leg leg = _legs[_leg];
            if (_phase == 0)
            {
                _g.ScriptedInput = InputFrame.None;
                if (--_wait <= 0)
                {
                    _phase = 1;
                    _legStartTick = _g.Player.Ticks;
                    _rebaseAtStart = _g.Origin.Rebases;
                    _seamRebaseAtStart = _g.Origin.SeamRebases;
                }
            }

            if (_phase == 1)
            {
                // Ровно LegTiles × 5 тактов с направлением: такт берёт кадр ввода, выставленный перед ним.
                long done = _g.Player.Ticks - _legStartTick;
                _g.ScriptedInput = done < LegTiles * MovementTick.TicksPerTile ? new InputFrame(leg.Dir) : InputFrame.None;
                if (done >= LegTiles * MovementTick.TicksPerTile)
                {
                    _phase = 2;
                    _wait = 4; // такт-другой на остановку интерполяции
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
        }

        int _rebaseAtStart, _seamRebaseAtStart;

        void NextLeg()
        {
            _leg++;
            if (_leg >= _legs.Length)
            {
                Finish();
                return;
            }

            Leg leg = _legs[_leg];
            _g.Player.Solid = (leg.Dir.DX != 0 ? _laneX : _laneY) ? _g.Rules.Solid : Collision.Empty;
            if (leg.Teleport || _g.Player.Position != leg.Start)
            {
                _g.Teleport(leg.Start);
            }

            _measure = false;
            _phase = 0;
            _wait = 3;
        }

        void FinishLeg(Leg leg)
        {
            int units = LegTiles * Torus.SubTilesPerTile;
            WorldPos expected = Torus.Offset(_map, leg.Start, new Int2(leg.Dir.DX * units, leg.Dir.DY * units));
            WorldPos core = _g.Player.Position;
            WorldPos drawn = DrawnCharacter(out bool ok);
            Int2 dd = Torus.Delta(_map, core, drawn);
            int rebases = _g.Origin.Rebases - _rebaseAtStart;
            int seamRebases = _g.Origin.SeamRebases - _seamRebaseAtStart;
            _rebases += rebases;
            _seamRebases += seamRebases;
            bool posOk = core == expected && ok && Math.Abs(dd.X) <= MatchUnits && Math.Abs(dd.Y) <= MatchUnits;
            Debug.Log($"[smoke] leg {leg.Name}: старт {leg.Start} → ядро {core}, ожидалось {expected}, нарисовано {drawn} " +
                      $"(Δ {dd.X},{dd.Y}/256); тактов {_g.Player.Ticks - _legStartTick}; переездов начала {rebases}, на шве {seamRebases}; " +
                      $"{(posOk ? "ok" : "НЕ СОВПАЛО")}");
            if (!posOk)
            {
                Fail($"{leg.Name}: позиция ядра {core} / нарисованная {drawn}, ожидалась {expected}");
            }

            if (seamRebases == 0 || rebases - seamRebases == 0)
            {
                Fail($"{leg.Name}: начало переезжало на шве {seamRebases} раз, по расстоянию {rebases - seamRebases} — нужно оба");
            }

            CheckTiles(leg.Name);
        }

        public void AfterFrame()
        {
            if (_done)
            {
                return;
            }

            _frames++;
            if (_frames > MaxFrames)
            {
                Fail($"сценарий не кончился за {MaxFrames} кадров");
                Finish();
                return;
            }

            CheckWindow();
            WorldPos ch = DrawnCharacter(out bool okChar);
            WorldPos cam = DrawnCamera(out bool okCam);
            if (!okChar || !okCam)
            {
                return; // дыра уже посчитана
            }

            // Нарисованный персонаж = интерполяция ядра.
            _g.Player.Interpolated(out WorldPos from, out float dx, out float dy);
            WorldPos interp = Torus.Offset(_map, from, new Int2(Mathf.RoundToInt(dx), Mathf.RoundToInt(dy)));
            Int2 drift = Torus.Delta(_map, interp, ch);
            int driftAbs = Math.Max(Math.Abs(drift.X), Math.Abs(drift.Y));
            if (driftAbs > _maxDrift)
            {
                _maxDrift = driftAbs;
            }

            if (driftAbs > MatchUnits)
            {
                Fail($"кадр {_frames}: нарисованный персонаж {ch} ушёл от ядра {interp} на {drift}");
            }

            if (_measure && _phase >= 1)
            {
                float jc = Length(Torus.Delta(_map, _prevChar, ch));
                float jm = Length(Torus.Delta(_map, _prevCam, cam));
                _maxJumpChar = Mathf.Max(_maxJumpChar, jc);
                _maxJumpCam = Mathf.Max(_maxJumpCam, jm);
                if (jc > MaxJumpTiles || jm > MaxJumpTiles)
                {
                    Fail($"кадр {_frames}: рывок персонажа {jc:0.000} т, камеры {jm:0.000} т (предел {MaxJumpTiles:0.000}), " +
                         $"якорь {_g.Origin.Anchor}");
                }

                // Шаг на бегу за кадр (только бег, без остановки в конце отрезка): доказательство интерполяции.
                if (_phase == 1)
                {
                    Int2 st = Torus.Delta(_map, _prevChar, ch);
                    int axis = Math.Max(Math.Abs(st.X), Math.Abs(st.Y));
                    if (axis > _maxStep)
                    {
                        _maxStep = axis;
                    }

                    float step = axis / (float)Torus.SubTilesPerTile;
                    if (step > MaxStepTiles)
                    {
                        Fail($"кадр {_frames}: шаг персонажа {step:0.000} т за кадр (предел {MaxStepTiles:0.000} — " +
                             $"признак отсутствия интерполяции), Δ {st.X},{st.Y}/256");
                    }
                }
            }

            _prevChar = ch;
            _prevCam = cam;
            _measure = true;
        }

        static float Length(Int2 d) => Mathf.Sqrt((float)d.LengthSquared) / Torus.SubTilesPerTile;

        // Где персонаж стоит относительно нарисованных тайлов: от узла чанка под ним (не от якоря), чтобы рывок
        // узлов при переезде начала был виден так же, как рывок персонажа.
        WorldPos DrawnCharacter(out bool ok) => DrawnAt(_g.Character.position, out ok);

        WorldPos DrawnCamera(out bool ok) => DrawnAt(_g.Camera.transform.position - GameBoot.CameraOffset, out ok);

        WorldPos DrawnAt(Vector3 unity, out bool ok) => DrawnAt(_map, _g.View, _g.Origin, _g.Player.Position, unity, out ok);

        /// <summary>
        /// Где точка Unity стоит относительно нарисованных тайлов: от узла чанка под позицией ядра <paramref name="core"/>
        /// (не от якоря) — рывок узлов при переезде начала виден так же, как рывок персонажа. Общая с net-smoke (0-017).
        /// </summary>
        public static WorldPos DrawnAt(MapShape map, WorldView view, FloatingOrigin origin, WorldPos core, Vector3 unity, out bool ok)
        {
            ChunkCoord c = Torus.ChunkOf(map, core);
            if (!view.TryGetNode(c, out WorldView.ChunkNode node) || !node.Root.activeSelf)
            {
                ok = false;
                return default;
            }

            ok = true;
            WorldPos corner = Torus.CornerOf(Torus.CornerOf(c));
            return origin.FromUnity(corner, node.Root.transform.position, unity);
        }

        void CheckWindow()
        {
            int n = _g.View.Count;
            if (n > _maxWindow)
            {
                _maxWindow = n;
            }

            _holes += WindowHoles(_map, _g.View, _g.Player.Position, msg => Fail($"кадр {_frames}: {msg}"));
        }

        /// <summary>
        /// Окно чанков: не больше <see cref="ChunkWindow.MaxChunks"/>, блок входа 6 × 4 вокруг <paramref name="core"/> целиком
        /// в окне, узлы активны и стоят ровно в сетке. Возвращает число дыр (ошибки — в <paramref name="fail"/>). Общая с net-smoke.
        /// </summary>
        public static int WindowHoles(MapShape map, WorldView view, WorldPos core, Action<string> fail)
        {
            int holes = 0;
            int n = view.Count;
            if (n > ChunkWindow.MaxChunks)
            {
                fail($"чанков в окне {n} > {ChunkWindow.MaxChunks}");
            }

            // Дыры: блок входа 6 × 4 вокруг персонажа целиком в окне, узлы активны и стоят ровно в сетке
            // относительно узла центра блока. Сверка — по смещению из индексов блока (i·32, −j·32), независимо от
            // ChunkToUnity: узел, вставленный по другую сторону шва, даёт расхождение в целый мир (замечание П по M2).
            ChunkCoord center = ChunkWindow.BlockCenter(map, core);
            if (!view.TryGetNode(center, out WorldView.ChunkNode centerNode) || !centerNode.Root.activeSelf)
            {
                holes++;
                fail($"нет узла центра блока {center} в окне");
                return holes;
            }

            Vector3 centerPos = centerNode.Root.transform.position;
            for (int j = -ChunkWindow.Height / 2; j < ChunkWindow.Height / 2; j++)
            {
                for (int i = -ChunkWindow.Width / 2; i < ChunkWindow.Width / 2; i++)
                {
                    ChunkCoord c = Torus.Wrap(map, new ChunkCoord(center.X + i, center.Y + j));
                    if (!view.TryGetNode(c, out WorldView.ChunkNode node) || !node.Root.activeSelf)
                    {
                        holes++;
                        fail($"дыра — нет чанка {c} в окне");
                    }
                    else
                    {
                        Vector3 expected = centerPos + new Vector3(i * Torus.ChunkSize, -j * Torus.ChunkSize, 0f);
                        if ((node.Root.transform.position - expected).sqrMagnitude > 1e-6f)
                        {
                            holes++;
                            fail($"чанк {c} стоит в {node.Root.transform.position}, " +
                                 $"а узел центра {center} в {centerPos}: ждали {(i * Torus.ChunkSize, -j * Torus.ChunkSize)} относительно него");
                        }
                    }
                }
            }

            return holes;
        }

        // ---------- Тайлы: Tilemap против мира ядра ----------

        void CheckTiles(string where)
        {
            int mismatches = CheckViewTiles(_g.View, _g.World, _map, where, out int checkedTiles, out int samples,
                line => Debug.Log("[smoke] " + line), msg => Fail(msg));
            _samples += samples;
            _tileMismatches += mismatches;
            _tilesChecked += checkedTiles;
        }

        /// <summary>
        /// Тайлы окна: Tilemap каждого узла против чанка ядра (офлайн — мир генератора, сеть — чанки от сервера, 0-026);
        /// по одному образцу каждого вида — строкой <c>sample</c> для сверки с картинкой gen_preview того же зерна
        /// (<c>tools/check_smoke_tiles.py</c>). Возвращает число расхождений (первые 5 — в <paramref name="fail"/>).
        /// </summary>
        public static int CheckViewTiles(WorldView view, WorldModel world, MapShape map, string where, out int checkedTiles,
            out int samples, Action<string> log, Action<string> fail)
        {
            int mismatches = 0;
            checkedTiles = 0;
            samples = 0;
            bool tree = false, rock = false, stone = false, grass = false;
            ReadOnlySpan<ChunkCoord> chunks = view.Chunks;
            for (int k = 0; k < chunks.Length; k++)
            {
                ChunkCoord c = chunks[k];
                view.TryGetNode(c, out WorldView.ChunkNode node);
                Chunk? chunk = world.FindChunk(c);
                if (chunk == null)
                {
                    mismatches++;
                    fail($"{where}: чанк {c} нарисован, а в мире ядра его нет");
                    continue;
                }

                for (int ly = 0; ly < Torus.ChunkSize; ly++)
                {
                    for (int lx = 0; lx < Torus.ChunkSize; lx++)
                    {
                        int index = Chunk.IndexOf(lx, ly);
                        byte floor = chunk.GetFloor(index);
                        ushort obj = chunk.GetObject(index);
                        Vector3Int cell = WorldView.CellOf(lx, ly);
                        TileBase? f = node.Floor.GetTile(cell);
                        TileBase? o = node.Objects.GetTile(cell);
                        checkedTiles++;
                        if (f != view.FloorTile(floor) || o != view.ObjectTile(obj))
                        {
                            if (mismatches++ < 5)
                            {
                                fail($"{where}: тайл чанка {c} ({lx},{ly}) нарисован {Name(f)}/{Name(o)}, в ядре пол {floor} объект {obj}");
                            }
                        }

                        // Образцы для сверки с gen_preview: по одному тайлу каждого вида на проверку.
                        var t = new TileCoord((c.X << Torus.ChunkBits) + lx, (c.Y << Torus.ChunkBits) + ly);
                        string? kind = null;
                        if (obj == ObjectId.Tree && !tree) { tree = true; kind = "tree"; }
                        else if (obj == ObjectId.Rock && !rock) { rock = true; kind = "rock"; }
                        else if (obj == ObjectId.Empty && floor == FloorId.Stone && !stone) { stone = true; kind = "stone"; }
                        else if (obj == ObjectId.Empty && floor == FloorId.Grass && !grass && !StubGenerator.InStartZone(map, t.X, t.Y))
                        {
                            grass = true;
                            kind = "grass";
                        }

                        if (kind != null)
                        {
                            samples++;
                            log($"sample x={t.X} y={t.Y} kind={kind} floor={floor} obj={obj} drawn={Name(f)}/{Name(o)}");
                        }
                    }
                }
            }

            log($"tiles {where}: чанков {chunks.Length}, тайлов {checkedTiles}, расхождений {mismatches}");
            return mismatches;
        }

        static string Name(TileBase? t) => t == null ? "-" : t.name;

        // ---------- Итог ----------

        void Finish()
        {
            _done = true;
            _g.ScriptedInput = null;
            Time.captureDeltaTime = 0f;
            bool lanes = _laneX && _laneY;
            Debug.Log($"[smoke] window: max {_maxWindow} чанков (предел {ChunkWindow.MaxChunks}), дыр {_holes}, " +
                      $"узлов создано {_g.View.NodesCreated}, заливок {_g.View.Fills}");
            Debug.Log($"[smoke] seam: рывок персонажа max {_maxJumpChar:0.000} т/кадр, камеры {_maxJumpCam:0.000} " +
                      $"(предел {MaxJumpTiles:0.000}); шаг на бегу max {_maxStep}/256 т/кадр (предел " +
                      $"{(int)(MaxStepTiles * Torus.SubTilesPerTile)}/256); нарисовано − ядро max {_maxDrift}/256 т; " +
                      $"переездов начала {_rebases}, на шве {_seamRebases}; " +
                      $"коллизия {(lanes ? "по миру" : "частично выключена")}");
            Debug.Log($"[smoke] tiles: проверено {_tilesChecked}, расхождений {_tileMismatches}, образцов {_samples}");
            if (_samples < 4)
            {
                Fail($"образцов тайлов {_samples} < 4");
            }

            int code = _errors == 0 ? 0 : 1;
            Debug.Log($"[smoke] done: frames={_frames} errors={_errors} exit={code}");
            Application.Quit(code);
        }
    }
}
