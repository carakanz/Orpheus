using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>Бот <c>craft_chain</c> (E2-3): добыча — ходьба рывками по пути, копание, подбор, «взвод» инструмента.</summary>
    public sealed partial class CraftChainBot
    {
        // ---------- Добыча: путь к ближайшей цели, копание, подбор ----------

        private static bool Wanted(byte item) => item == Items.Wood || item == Items.Stone || item == Items.CopperOre;

        // Ходьба «рывками»: своя позиция приходит из снимков с опозданием (задержка сети), поэтому бот не рулит по ней на
        // ходу, а отмеряет рывок в кадрах (25,6 unit за кадр), шагает его вслепую и ждёт, пока позиция в снимках устоится.
        private const int SettleFrames = 6;
        private const int MaxRunTiles = 12;
        private int _burstLeft;
        private Input8 _burstDir;
        private bool _settling;
        private int _settleSame;
        private WorldPos _settlePos;
        private WorldPos _burstFrom;
        private TileCoord _burstTile;
        private bool _burstCheck;
        private int _stuckBursts;
        private int _lookFrame;

        private InputFrame Harvest(NetClient client, ClientWorld world, WorldPos pos, Func<ushort, bool> isTarget)
        {
            if (_burstLeft > 0)
            {
                _burstLeft--;
                return new InputFrame(_burstDir, 0, 0, _slot);
            }

            if (_settling)
            {
                if (pos == _settlePos)
                {
                    _settleSame++;
                }
                else
                {
                    _settleSame = 0;
                    _settlePos = pos;
                }

                if (_settleSame < SettleFrames)
                {
                    return Idle(pos);
                }

                _settling = false;
                if (_burstCheck)
                {
                    _burstCheck = false;
                    _stuckBursts = pos == _burstFrom ? _stuckBursts + 1 : 0;
                    if (_stuckBursts >= 3)
                    {
                        _stuckBursts = 0;
                        _skipUntil[_burstTile] = _frame + 600; // трижды упёрлись — этот тайл обходим
                        _path.Clear();
                        _hasTarget = false;
                    }
                }
            }

            TileCoord me = Torus.TileOf(_shape, pos);

            // Добыча лежит рядом — дождаться подбора (или подойти), потом дальше.
            if (Now != Stage.Granite && _path.Count == 0 && _piles.TrySeek(client, pos, Wanted, out Input8 toPile))
            {
                if (toPile != Input8.None)
                {
                    // Два кадра хода и ждём устойчивой позиции — шаг за шагом, без раскачки при задержке сети.
                    _burstDir = toPile;
                    _burstLeft = 1;
                    BeginSettle(pos);
                }

                return new InputFrame(toPile, 0, 0, _slot);
            }

            if (_hasTarget && !isTarget(ObjectId.IdOf(world.World.GetObject(_target))))
            {
                _hasTarget = false; // цель снята (или уже не нужна) — искать заново
                _path.Clear();
            }

            if (!_hasTarget && _path.Count == 0)
            {
                if (_frame < _replanAt)
                {
                    return Idle(pos);
                }

                if (!Plan(client, world, me, isTarget, allowDig: false) && !Plan(client, world, me, isTarget, allowDig: true)
                    && !Plan(client, world, me, isTarget, allowDig: false, explore: true) && !Plan(client, world, me, isTarget, allowDig: true, explore: true))
                {
                    _replanAt = _frame + 30; // ни цели, ни куда идти — подождать чанки
                    return Idle(pos);
                }

                _lookFrame = _frame;
            }

            // Идём наугад — раз в секунду смотрим, не показалась ли цель в окне (тогда путь к ней заменит разведку).
            if (!_hasTarget && _path.Count > 0 && _frame - _lookFrame >= 60)
            {
                _lookFrame = _frame;
                Plan(client, world, me, isTarget, allowDig: false);
            }

            // Идём по пути: от центра тайла к центру, прямые участки — одним рывком.
            while (_path.Count > 0)
            {
                TileCoord next = _path[0];
                ushort obj = world.World.GetObject(next);
                if (!(next.X == me.X && next.Y == me.Y) && (ObjectId.IsSolid(obj) || ObjectId.IsPart(obj)))
                {
                    if (_pathDigs && Diggable(client, obj) && !Skipped(next) && InReach(pos, next))
                    {
                        return Dig(client, world, next, obj) ? new InputFrame(Input8.None, InputButtons.Dig, 0, _slot) : Idle(pos);
                    }

                    _path.Clear(); // путь перекрыт (мир изменился) — заново
                    _hasTarget = false;
                    return Idle(pos);
                }

                Int2 d = Torus.Delta(_shape, pos, EditRules.CenterOf(next));
                bool offX = Math.Abs(d.X) > U / 6, offY = Math.Abs(d.Y) > U / 6;
                if (!offX && !offY)
                {
                    _path.RemoveAt(0);
                    continue;
                }

                // Прямой участок: пока следующие тайлы пути лежат на той же линии и свободны — рывок до последнего из них.
                if (offX != offY)
                {
                    int sx = offX ? Math.Sign(d.X) : 0, sy = offY ? Math.Sign(d.Y) : 0;
                    int run = 0;
                    while (run + 1 < _path.Count && run < MaxRunTiles)
                    {
                        Int2 step = Torus.Delta(_shape, _path[run], _path[run + 1]);
                        if (step.X != sx || step.Y != sy || !Walkable(world, _path[run + 1]))
                        {
                            break;
                        }

                        run++;
                    }

                    if (run > 0)
                    {
                        _path.RemoveRange(0, run);
                        next = _path[0];
                        d = Torus.Delta(_shape, pos, EditRules.CenterOf(next));
                    }
                }

                int dist = offX ? Math.Abs(d.X) : Math.Abs(d.Y); // обе оси сбиты — сначала X, потом Y
                _burstDir = offX ? new Input8(Math.Sign(d.X), 0) : new Input8(0, Math.Sign(d.Y));
                _burstLeft = Math.Max(1, (dist * 20 + 256) / 512) - 1; // 25,6 unit за кадр
                _burstFrom = pos;
                _burstTile = next;
                _burstCheck = true;
                BeginSettle(pos);
                return new InputFrame(_burstDir, 0, 0, _slot);
            }

            // Пришли: цель — сосед по стороне.
            if (_hasTarget)
            {
                ushort obj = world.World.GetObject(_target);
                if (!world.IsPredicted(_target) && InReach(pos, _target) && Dig(client, world, _target, obj))
                {
                    return new InputFrame(Input8.None, InputButtons.Dig, 0, _slot);
                }

                if (!InReach(pos, _target))
                {
                    _hasTarget = false; // снесло с места — путь заново
                }
            }

            return Idle(pos);
        }

        private void BeginSettle(WorldPos pos)
        {
            _settling = true;
            _settleSame = 0;
            _settlePos = pos;
        }

        private InputFrame Idle(WorldPos pos)
        {
            _lastPos = pos;
            return new InputFrame(Input8.None, 0, 0, _slot);
        }

        // Досягаемость с запасом 1/4 тайла: своя позиция — из снимка, сервер её уже обогнал.
        private bool InReach(WorldPos pos, TileCoord t) => EditRules.InReach(_shape, pos, t, EditRules.DigReachUnits - U / 4);

        private ushort _strikeReq;
        private TileCoord _strikeTile;

        /// <summary>C-028 (П): свой взмах не задел цель (линия перекрыта) — тайл пропускается 4 с, а не бьётся вечно.</summary>
        private void SkipMissed(NetClient client)
        {
            if (client.OwnStrikeResolved(_strikeReq, _strikeTile, out bool hit))
            {
                if (!hit)
                {
                    _skipUntil[_strikeTile] = _frame + 240;
                    _path.Clear();
                    _hasTarget = false;
                }

                _strikeReq = 0;
            }
        }

        private void NoteStrike(NetClient client, ushort id, TileCoord t)
        {
            if (client.DigStrike)
            {
                _strikeReq = id;
                _strikeTile = t;
            }
        }

        private bool Skipped(TileCoord t) => _skipUntil.TryGetValue(t, out int until) && _frame < until;

        private bool Dig(NetClient client, ClientWorld world, TileCoord t, ushort obj)
        {
            byte slot = ToolSlotFor(client, obj);
            if (slot == NoTool || world.IsPredicted(t))
            {
                return false;
            }

            // «Взвод» инструмента: сервер смотрит предмет в руках по кадру ввода, а запрос копания приходит раньше кадра —
            // сначала несколько кадров держим нужную ячейку (иначе «нужен инструмент»), потом просим.
            _slot = slot;
            if (slot != _sentSlot || _frame - _slotFrame < ArmFrames)
            {
                return false; // вызывающий вернёт кадр с этой ячейкой — «взвод» пойдёт
            }

            ushort id = client.StartDig(t);
            if (id == 0)
            {
                return false;
            }

            NoteStrike(client, id, t); // C-028 (П)

            _slot = slot;
            _digTile[id] = t;
            _digRaw[id] = obj;
            return true;
        }

        /// <summary>Ячейка панели с инструментом, который копает объект (время &gt; 0 по таблице клиента); нет — <see cref="NoTool"/>.</summary>
        private static byte ToolSlotFor(NetClient client, ushort obj)
        {
            for (byte s = 0; s < Inventory.Slots; s++)
            {
                byte item = client.Inv.SlotAt(s);
                if (item != Items.None && client.DigTimes.FramesFor(obj, Items.DigToolOf(item), Items.TierOf(item)) > 0)
                {
                    return s;
                }
            }

            return NoTool;
        }

        // Прорубаться можно через природное, что берётся инструментом с панели (дерево, пень, куст, валун, залежь).
        private static bool Diggable(NetClient client, ushort obj) =>
            obj != ObjectId.Empty && !ObjectId.IsPart(obj) && ObjectId.IsNatural(obj) && ToolSlotFor(client, obj) != NoTool;

        private bool Walkable(ClientWorld world, TileCoord t)
        {
            if (world.World.FindChunk(Torus.ChunkOf(_shape, t)) == null || !FloorId.IsPassable(world.World.GetFloor(t)))
            {
                return false;
            }

            ushort obj = world.World.GetObject(t);
            return !ObjectId.IsSolid(obj) && !ObjectId.IsPart(obj);
        }
    }
}
