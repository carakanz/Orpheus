#nullable enable
using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
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
    /// <item>Прогон по прямой скриптовым вводом через шов: по X на восток и обратно, по Y на юг и обратно, по 64 тайла
    /// (320 тактов) — на полосе без препятствий; если на шве такой нет — без коллизии (пишется в лог).</item>
    /// <item>Каждый кадр: позиция персонажа и камеры, восстановленная относительно нарисованного чанка, сдвигается
    /// не больше чем на 6 т/с × 1/30 с × 1,1 = 0,22 тайла (рывок), совпадает с интерполяцией ядра; в окне ≤ 48 чанков
    /// и нет дыр в блоке 6 × 4.</item>
    /// <item>После каждого отрезка позиция по ядру — ровно старт + 64 тайла, нарисованная — та же.</item>
    /// </list>
    /// Время кадра фиксировано (<see cref="Time.captureDeltaTime"/> = 1/60 с): проверка не зависит от скорости машины.
    /// Код выхода 0 — все проверки прошли и в логе нет ошибок; 1 — иначе.
    /// </summary>
    public sealed class SmokeRun : IDisposable
    {
        public const float FrameSeconds = 1f / 60f;

        /// <summary>Рывок в кадре: 6 т/с × 1/30 с × 1,1 (docs/08, 3.0, приёмка 0.2).</summary>
        public const float MaxJumpTiles = 6f / 30f * 1.1f;

        /// <summary>Длина отрезка: 64 тайла = 2 чанка = 320 тактов базового бега.</summary>
        public const int LegTiles = 64;

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
        int _maxDrift;
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
            int half = LegTiles / 2;
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
                new Leg { Name = "X+ через шов", Dir = Input8.East, Start = _g.TileCenter(new TileCoord(w - half, row)), Teleport = true },
                new Leg { Name = "X- через шов", Dir = Input8.West, Start = _g.TileCenter(new TileCoord(half, row)) },
                new Leg { Name = "Y+ через шов", Dir = Input8.South, Start = _g.TileCenter(new TileCoord(col, w - half)), Teleport = true },
                new Leg { Name = "Y- через шов", Dir = Input8.North, Start = _g.TileCenter(new TileCoord(col, half)) },
            };
        }

        bool _laneX, _laneY;

        // Полоса: строка (или столбец), где тайлы от W − 33 до 33 через шов проходимы — круг 0,4 по центру тайла
        // соседних строк не касается. Поиск от середины мира наружу — повторяемо для зерна.
        bool FindLane(bool horizontal, out int lane)
        {
            int w = _map.Width;
            int reach = LegTiles / 2 + 1;
            for (int k = 0; k < w; k++)
            {
                int l = (w / 2 + ((k & 1) == 0 ? k / 2 : -(k / 2 + 1))) & (w - 1);
                bool clear = true;
                for (int s = -reach; s <= reach && clear; s++)
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

            if (seamRebases == 0)
            {
                Fail($"{leg.Name}: начало не переехало на шве");
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

        WorldPos DrawnAt(Vector3 unity, out bool ok)
        {
            ChunkCoord c = Torus.ChunkOf(_map, _g.Player.Position);
            if (!_g.View.TryGetNode(c, out WorldView.ChunkNode node) || !node.Root.activeSelf)
            {
                ok = false;
                return default;
            }

            ok = true;
            WorldPos corner = Torus.CornerOf(Torus.CornerOf(c));
            return _g.Origin.FromUnity(corner, node.Root.transform.position, unity);
        }

        void CheckWindow()
        {
            int n = _g.View.Count;
            if (n > _maxWindow)
            {
                _maxWindow = n;
            }

            if (n > ChunkWindow.MaxChunks)
            {
                Fail($"кадр {_frames}: чанков в окне {n} > {ChunkWindow.MaxChunks}");
            }

            // Дыры: блок входа 6 × 4 вокруг персонажа целиком в окне, узлы активны и стоят в Δ от якоря.
            ChunkCoord center = ChunkWindow.BlockCenter(_map, _g.Player.Position);
            for (int j = -ChunkWindow.Height / 2; j < ChunkWindow.Height / 2; j++)
            {
                for (int i = -ChunkWindow.Width / 2; i < ChunkWindow.Width / 2; i++)
                {
                    ChunkCoord c = Torus.Wrap(_map, new ChunkCoord(center.X + i, center.Y + j));
                    if (!_g.View.TryGetNode(c, out WorldView.ChunkNode node) || !node.Root.activeSelf)
                    {
                        _holes++;
                        Fail($"кадр {_frames}: дыра — нет чанка {c} в окне");
                    }
                    else if ((node.Root.transform.position - _g.Origin.ChunkToUnity(c)).sqrMagnitude > 1e-6f)
                    {
                        _holes++;
                        Fail($"кадр {_frames}: чанк {c} стоит в {node.Root.transform.position}, а не в Δ от якоря");
                    }
                }
            }
        }

        // ---------- Тайлы: Tilemap против мира ядра ----------

        void CheckTiles(string where)
        {
            int mismatches = 0;
            int checkedTiles = 0;
            bool tree = false, rock = false, stone = false, grass = false;
            WorldModel world = _g.World;
            ReadOnlySpan<ChunkCoord> chunks = _g.View.Chunks;
            for (int k = 0; k < chunks.Length; k++)
            {
                ChunkCoord c = chunks[k];
                _g.View.TryGetNode(c, out WorldView.ChunkNode node);
                Chunk? chunk = world.FindChunk(c);
                for (int ly = 0; ly < Torus.ChunkSize; ly++)
                {
                    for (int lx = 0; lx < Torus.ChunkSize; lx++)
                    {
                        int index = Chunk.IndexOf(lx, ly);
                        byte floor = chunk!.GetFloor(index);
                        ushort obj = chunk.GetObject(index);
                        Vector3Int cell = WorldView.CellOf(lx, ly);
                        TileBase? f = node.Floor.GetTile(cell);
                        TileBase? o = node.Objects.GetTile(cell);
                        checkedTiles++;
                        if (f != _g.View.FloorTile(floor) || o != _g.View.ObjectTile(obj))
                        {
                            if (mismatches++ < 5)
                            {
                                Fail($"{where}: тайл чанка {c} ({lx},{ly}) нарисован {Name(f)}/{Name(o)}, в ядре пол {floor} объект {obj}");
                            }
                        }

                        // Образцы для сверки с gen_preview: по одному тайлу каждого вида на проверку.
                        var t = new TileCoord((c.X << Torus.ChunkBits) + lx, (c.Y << Torus.ChunkBits) + ly);
                        if (obj == ObjectId.Tree && !tree) tree = Sample(t, "tree", floor, obj, f, o);
                        else if (obj == ObjectId.Rock && !rock) rock = Sample(t, "rock", floor, obj, f, o);
                        else if (obj == ObjectId.Empty && floor == FloorId.Stone && !stone) stone = Sample(t, "stone", floor, obj, f, o);
                        else if (obj == ObjectId.Empty && floor == FloorId.Grass && !grass && ly > 0 && lx > 0)
                            grass = Sample(t, "grass", floor, obj, f, o);
                    }
                }
            }

            _tileMismatches += mismatches;
            _tilesChecked += checkedTiles;
            Debug.Log($"[smoke] tiles {where}: чанков {chunks.Length}, тайлов {checkedTiles}, расхождений {mismatches}");
        }

        bool Sample(TileCoord t, string kind, byte floor, ushort obj, TileBase? f, TileBase? o)
        {
            _samples++;
            Debug.Log($"[smoke] sample x={t.X} y={t.Y} kind={kind} floor={floor} obj={obj} drawn={Name(f)}/{Name(o)}");
            return true;
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
                      $"(предел {MaxJumpTiles:0.000}); нарисовано − ядро max {_maxDrift}/256 т; переездов начала {_rebases}, на шве {_seamRebases}; " +
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
