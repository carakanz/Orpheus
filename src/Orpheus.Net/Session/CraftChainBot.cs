using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Бот-сценарий <c>craft_chain</c> (E2-3, шаг 2.3 плана): из стартового набора доходит до медной кирки и берёт гранит.
    /// <para>
    /// Этапы: рубит деревья и копает валуны (древесина 13, камень 6) → идёт к меди и копает руду (16) → ставит молотом
    /// верстак рядом → делает 8 слитков → медную кирку → кладёт её на быструю панель → идёт к граниту и копает тайл.
    /// </para>
    /// <para>
    /// Ходит по своему миру клиента (чанки окна): поиск в ширину по тайлам до ближайшей цели, встаёт на соседний по
    /// стороне тайл (добыча падает в радиус подбора). Нет пути в обход — прорубается через деревья и валуны. Цели нет в
    /// окне — идёт прочь от старта (окно чанков едет за ним). Крафт не предсказывается: бот ждёт сумку сервера.
    /// </para>
    /// </summary>
    public sealed class CraftChainBot
    {
        public const string Name = "craft_chain";

        /// <summary>Этап сценария.</summary>
        public enum Stage
        {
            Gather = 0,
            Ore = 1,
            Bench = 2,
            Smelt = 3,
            Pickaxe = 4,
            Equip = 5,
            Granite = 6,
            Done = 7,
        }

        /// <summary>Древесины нужно: верстак 10 + кирка 3 (<c>06</c> 3.3, 2.3).</summary>
        public const int NeedWood = 13;

        /// <summary>Камня нужно: верстак 6.</summary>
        public const int NeedStone = 6;

        /// <summary>Слитков на медную кирку (<c>06</c> 2.3).</summary>
        public const int NeedIngots = 8;

        /// <summary>Руды нужно: 8 слитков × 2 (<c>06</c> 1.5).</summary>
        public const int NeedOre = 2 * NeedIngots;

        private const int U = Torus.SubTilesPerTile;
        private const int R = 90; // радиус поиска пути, тайлов (окно чанков клиента — 96 × 64 в каждую сторону)
        private const int Side = 2 * R + 1;
        private const byte NoTool = byte.MaxValue;
        private const int StuckFrames = 90;

        private static readonly int[] Dx4 = { 1, 0, -1, 0 };
        private static readonly int[] Dy4 = { 0, 1, 0, -1 };

        private readonly MapShape _shape;
        private readonly TileCoord _start;
        private readonly BotPiles _piles;
        private readonly int[] _prev = new int[Side * Side];
        private readonly int[] _seen = new int[Side * Side];
        private readonly int[] _queue = new int[Side * Side];
        private int _gen;

        private readonly List<TileCoord> _path = new List<TileCoord>();
        private bool _hasTarget;
        private TileCoord _target;
        private bool _pathDigs;
        private int _replanAt;

        private readonly Dictionary<ushort, TileCoord> _digTile = new Dictionary<ushort, TileCoord>();
        private readonly Dictionary<ushort, ushort> _digRaw = new Dictionary<ushort, ushort>();
        private readonly Dictionary<TileCoord, int> _skipUntil = new Dictionary<TileCoord, int>();
        private byte _slot;
        private int _frame;
        private WorldPos _lastPos;
        private int _still;

        private bool _hasBench;
        private TileCoord _bench;
        private ushort _placeReq;
        private TileCoord _placeTile;
        private int _placeFrame;
        private bool _armed;
        private TileCoord _armTile;
        private int _armFrame;
        private readonly HashSet<TileCoord> _benchRefused = new HashSet<TileCoord>();
        private int _craftFrame = -100000;
        private int _moveFrame = -100000;

        public CraftChainBot(MapShape shape, WorldPos start)
        {
            _shape = shape;
            _start = Torus.TileOf(shape, start);
            _piles = new BotPiles(shape);
            _lastPos = start;
        }

        /// <summary>Текущий этап.</summary>
        public Stage Now { get; private set; }

        /// <summary>Сценарий пройден: тайл гранита снят медной киркой.</summary>
        public bool Done => Now == Stage.Done;

        /// <summary>Кадр (60 Гц), на котором бот впервые вошёл в этап; не входил — 0.</summary>
        public int[] StageFrame { get; } = new int[(int)Stage.Done + 1];

        public int TreesCut { get; private set; }
        public int RocksDug { get; private set; }
        public int OresDug { get; private set; }
        public int GraniteDug { get; private set; }
        public int OtherDug { get; private set; }

        /// <summary>Отказов на копание и постановку (любая причина); последний — <see cref="LastRefusal"/>.</summary>
        public int Refused { get; private set; }
        public EditReply LastRefusal { get; private set; }

        /// <summary>Заявок изготовления отправлено / ответов-отказов получено; последний отказ — <see cref="LastCraftRefusal"/>.</summary>
        public int CraftRequests { get; private set; }
        public int CraftRefused { get; private set; }
        public CraftReply LastCraftRefusal { get; private set; }

        /// <summary>Сколько раз шёл наугад (цели не было в окне) и сколько раз прорубался (обхода не было).</summary>
        public int Explores { get; private set; }
        public int DigThroughs { get; private set; }

        /// <summary>Где стоит верстак (после постановки).</summary>
        public TileCoord? BenchTile => _hasBench ? _bench : (TileCoord?)null;

        /// <summary>Снятый тайл гранита.</summary>
        public TileCoord? GraniteTile { get; private set; }

        /// <summary>Итог строкой — для отчёта бота и теста.</summary>
        public string Summary =>
            $"этап {Now}; деревьев {TreesCut}, валунов {RocksDug}, залежей {OresDug}, гранита {GraniteDug}, прочего {OtherDug}; " +
            $"отказов правок {Refused} (последний: {EditRules.Describe(LastRefusal)}), заявок крафта {CraftRequests}, отказов {CraftRefused} ({LastCraftRefusal}); " +
            $"наугад {Explores}, прорубался {DigThroughs}; кадры этапов: руда {StageFrame[1]}, верстак {StageFrame[2]}, плавка {StageFrame[3]}, " +
            $"кирка {StageFrame[4]}, гранит {StageFrame[6]}, конец {StageFrame[7]}";

        /// <summary>Ответ сервера на правку (подключить к <see cref="NetClient.EditReplied"/>).</summary>
        public void OnReply(TileReplyMessage m)
        {
            if (m.RequestId == _placeReq && _placeReq != 0)
            {
                _placeReq = 0;
                if (m.Reply == EditReply.Accepted)
                {
                    _hasBench = true;
                    _bench = _placeTile;
                }
                else
                {
                    Refused++;
                    LastRefusal = m.Reply;
                    _benchRefused.Add(_placeTile);
                }

                return;
            }

            if (!_digTile.TryGetValue(m.RequestId, out TileCoord t))
            {
                return;
            }

            ushort raw = _digRaw[m.RequestId];
            _digTile.Remove(m.RequestId);
            _digRaw.Remove(m.RequestId);
            if (m.Reply != EditReply.Accepted)
            {
                Refused++;
                LastRefusal = m.Reply;
                _skipUntil[t] = _frame + (m.Reply == EditReply.OutOfReach ? 30 : 600);
                _path.Clear();
                _hasTarget = false;
                return;
            }

            switch (ObjectId.IdOf(raw))
            {
                case ObjectId.Tree:
                    TreesCut++;
                    break;
                case ObjectId.Rock:
                    RocksDug++;
                    break;
                case ObjectId.Ore:
                    OresDug++;
                    break;
                case ObjectId.Granite:
                    GraniteDug++;
                    GraniteTile = t;
                    break;
                default:
                    OtherDug++;
                    break;
            }
        }

        /// <summary>Ответ на заявку изготовления (подключить к <see cref="NetClient.CraftReplied"/>).</summary>
        public void OnCraftReply(CraftReplyMessage m)
        {
            if (m.Reply != CraftReply.Accepted)
            {
                CraftRefused++;
                LastCraftRefusal = m.Reply;
            }
        }

        /// <summary>Кадр ввода (60 Гц). <paramref name="pos"/> — своя позиция (снимок или предсказание).</summary>
        public InputFrame Next(NetClient client, WorldPos pos)
        {
            _frame++;
            ClientWorld? world = client.World;
            if (world == null || Done)
            {
                return InputFrame.None;
            }

            if (client.Digging)
            {
                return new InputFrame(Input8.None, InputButtons.Dig, 0, _slot);
            }

            Inventory inv = client.Inv;
            Enter(StageOf(inv, world));
            switch (Now)
            {
                case Stage.Gather:
                    return Harvest(client, world, pos, id =>
                        (id == ObjectId.Tree && inv.Count(Items.Wood) < NeedWood) || (id == ObjectId.Rock && inv.Count(Items.Stone) < NeedStone));
                case Stage.Ore:
                    return Harvest(client, world, pos, id => id == ObjectId.Ore);
                case Stage.Bench:
                    return PlaceBench(client, world, pos);
                case Stage.Smelt:
                    return Craft(client, RecipeTable.CopperIngot, NeedIngots - inv.Count(Items.CopperIngot));
                case Stage.Pickaxe:
                    return Craft(client, RecipeTable.CopperPickaxe, 1);
                case Stage.Equip:
                    return Equip(client, inv);
                case Stage.Granite:
                    return Harvest(client, world, pos, id => id == ObjectId.Granite);
                default:
                    return InputFrame.None;
            }
        }

        private Stage StageOf(in Inventory inv, ClientWorld world)
        {
            if (GraniteDug > 0)
            {
                return Stage.Done;
            }

            if (inv.Count(Items.CopperPickaxe) > 0)
            {
                for (int s = 0; s < Inventory.Slots; s++)
                {
                    if (inv.SlotAt(s) == Items.CopperPickaxe)
                    {
                        return Stage.Granite;
                    }
                }

                return Stage.Equip;
            }

            if (_hasBench)
            {
                // Сервер верстак принял: дальше только крафт у него (стоимость снята — «мало древесины» уже не повод уходить).
                return inv.Count(Items.CopperIngot) >= NeedIngots ? Stage.Pickaxe : Stage.Smelt;
            }

            if (_placeReq != 0)
            {
                return Stage.Bench; // постановка в пути: стоимость уже предсказана и снята с локальной сумки
            }

            if (inv.Count(Items.Wood) < NeedWood || inv.Count(Items.Stone) < NeedStone)
            {
                return Stage.Gather;
            }

            return inv.Count(Items.CopperOre) < NeedOre ? Stage.Ore : Stage.Bench;
        }

        private void Enter(Stage s)
        {
            if (s == Now && StageFrame[(int)s] != 0)
            {
                return;
            }

            if (s != Now)
            {
                _path.Clear();
                _hasTarget = false;
                _replanAt = 0;
            }

            Now = s;
            if (StageFrame[(int)s] == 0)
            {
                StageFrame[(int)s] = _frame;
            }
        }

        // ---------- Добыча: путь к ближайшей цели, копание, подбор ----------

        private static bool Wanted(byte item) => item == Items.Wood || item == Items.Stone || item == Items.CopperOre;

        private InputFrame Harvest(NetClient client, ClientWorld world, WorldPos pos, Func<ushort, bool> isTarget)
        {
            TileCoord me = Torus.TileOf(_shape, pos);

            // Добыча лежит рядом — дождаться подбора (или подойти), потом дальше.
            if (Now != Stage.Granite && _path.Count == 0 && _piles.TrySeek(client, pos, Wanted, out Input8 toPile))
            {
                _still = 0;
                _lastPos = pos;
                return new InputFrame(toPile);
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
                    return InputFrame.None;
                }

                if (!Plan(client, world, me, isTarget, allowDig: false) && !Plan(client, world, me, isTarget, allowDig: true))
                {
                    _replanAt = _frame + 30; // ни цели, ни куда идти — подождать чанки
                    return InputFrame.None;
                }
            }

            // Идём по пути: тайл за тайлом по центрам.
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
                    return InputFrame.None;
                }

                Int2 d = Torus.Delta(_shape, pos, EditRules.CenterOf(next));
                if (Math.Abs(d.X) <= U / 6 && Math.Abs(d.Y) <= U / 6)
                {
                    _path.RemoveAt(0);
                    continue;
                }

                var move = new Input8(Math.Abs(d.X) > U / 8 ? Math.Sign(d.X) : 0, Math.Abs(d.Y) > U / 8 ? Math.Sign(d.Y) : 0);
                if (pos == _lastPos)
                {
                    if (++_still > StuckFrames)
                    {
                        _still = 0;
                        _skipUntil[next] = _frame + 600; // упёрлись — этот тайл обходим
                        _path.Clear();
                        _hasTarget = false;
                    }
                }
                else
                {
                    _still = 0;
                    _lastPos = pos;
                }

                return new InputFrame(move);
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

        private InputFrame Idle(WorldPos pos)
        {
            _lastPos = pos;
            return InputFrame.None;
        }

        // Досягаемость с запасом 1/4 тайла: своя позиция — из снимка, сервер её уже обогнал.
        private bool InReach(WorldPos pos, TileCoord t) => EditRules.InReach(_shape, pos, t, EditRules.DigReachUnits - U / 4);

        private bool Skipped(TileCoord t) => _skipUntil.TryGetValue(t, out int until) && _frame < until;

        private bool Dig(NetClient client, ClientWorld world, TileCoord t, ushort obj)
        {
            byte slot = ToolSlotFor(client, obj);
            if (slot == NoTool || world.IsPredicted(t))
            {
                return false;
            }

            ushort id = client.StartDig(t);
            if (id == 0)
            {
                return false;
            }

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

        /// <summary>
        /// Поиск в ширину от своего тайла: ближайший по пути тайл, у которого сосед по стороне — цель. Нашёл — путь и цель.
        /// Не нашёл (только при <paramref name="allowDig"/>) — путь к достижимому тайлу, самому дальнему от старта.
        /// </summary>
        private bool Plan(NetClient client, ClientWorld world, TileCoord me, Func<ushort, bool> isTarget, bool allowDig)
        {
            _gen++;
            int head = 0, tail = 0;
            int startIdx = R * Side + R;
            _seen[startIdx] = _gen;
            _prev[startIdx] = -1;
            _queue[tail++] = startIdx;
            int far = -1, farScore = -1;
            while (head < tail)
            {
                int idx = _queue[head++];
                int cx = idx % Side - R, cy = idx / Side - R;
                var here = Torus.Wrap(_shape, new TileCoord(me.X + cx, me.Y + cy));
                for (int k = 0; k < 4; k++)
                {
                    var n = Torus.Wrap(_shape, new TileCoord(here.X + Dx4[k], here.Y + Dy4[k]));
                    ushort obj = world.World.GetObject(n);
                    if (obj != ObjectId.Empty && !ObjectId.IsPart(obj) && isTarget(ObjectId.IdOf(obj)) && !Skipped(n)
                        && !world.IsPredicted(n) && ToolSlotFor(client, obj) != NoTool)
                    {
                        BuildPath(me, idx);
                        _target = n;
                        _hasTarget = true;
                        _pathDigs = allowDig;
                        DigThroughs += allowDig ? 1 : 0;
                        return true;
                    }
                }

                if (allowDig)
                {
                    Int2 fromStart = Torus.Delta(_shape, _start, here);
                    int score = Math.Max(Math.Abs(fromStart.X), Math.Abs(fromStart.Y));
                    if (score > farScore)
                    {
                        farScore = score;
                        far = idx;
                    }
                }

                for (int k = 0; k < 4; k++)
                {
                    int nx = cx + Dx4[k], ny = cy + Dy4[k];
                    if (Math.Abs(nx) > R || Math.Abs(ny) > R)
                    {
                        continue;
                    }

                    int nidx = (ny + R) * Side + nx + R;
                    if (_seen[nidx] == _gen)
                    {
                        continue;
                    }

                    var n = Torus.Wrap(_shape, new TileCoord(me.X + nx, me.Y + ny));
                    if (Skipped(n))
                    {
                        continue;
                    }

                    bool ok = Walkable(world, n);
                    if (!ok && allowDig && world.World.FindChunk(Torus.ChunkOf(_shape, n)) != null
                        && FloorId.IsPassable(world.World.GetFloor(n)))
                    {
                        ok = Diggable(client, world.World.GetObject(n));
                    }

                    if (ok)
                    {
                        _seen[nidx] = _gen;
                        _prev[nidx] = idx;
                        _queue[tail++] = nidx;
                    }
                }
            }

            if (!allowDig || far < 0 || far == startIdx)
            {
                return false;
            }

            BuildPath(me, far); // цели в окне нет — идём прочь от старта, окно чанков поедет следом
            _hasTarget = false;
            _pathDigs = true;
            Explores++;
            return true;
        }

        private void BuildPath(TileCoord me, int idx)
        {
            _path.Clear();
            for (int i = idx; i >= 0 && _prev[i] >= 0; i = _prev[i])
            {
                _path.Add(Torus.Wrap(_shape, new TileCoord(me.X + i % Side - R, me.Y + i / Side - R)));
            }

            _path.Reverse();
            if (_path.Count == 0)
            {
                _path.Add(me); // цель рядом — встать по центру своего тайла
            }
        }

        // ---------- Верстак, крафт, панель ----------

        private static readonly sbyte[] BenchDx = { 2, -2, 0, 0, 2, 2, -2, -2, 1, -1, 1, -1, 2, 2, -2, -2, 1, -1, 0, 0, 1, -1, 1, -1 };
        private static readonly sbyte[] BenchDy = { 0, 0, 2, -2, 1, -1, 1, -1, 2, 2, -2, -2, 2, -2, 2, -2, 0, 0, 1, -1, 1, 1, -1, -1 };

        private InputFrame PlaceBench(NetClient client, ClientWorld world, WorldPos pos)
        {
            InputFrame hammer = HammerFrame(client);
            if (_placeReq != 0)
            {
                if (_frame - _placeFrame > 180)
                {
                    _placeReq = 0; // ответ не пришёл — пробуем снова
                }

                return hammer;
            }

            TileCoord me = Torus.TileOf(_shape, pos);
            for (int k = 0; k < BenchDx.Length; k++)
            {
                var t = Torus.Wrap(_shape, new TileCoord(me.X + BenchDx[k], me.Y + BenchDy[k]));
                if (_benchRefused.Contains(t) || world.World.GetObject(t) != ObjectId.Empty || !Walkable(world, t))
                {
                    continue;
                }

                // «Взвод»: сервер проверяет молот по кадру ввода, пришедшему после запроса, — сначала несколько кадров с молотом.
                if (!_armed || _armTile.X != t.X || _armTile.Y != t.Y)
                {
                    _armed = true;
                    _armTile = t;
                    _armFrame = _frame;
                    return hammer;
                }

                if (_frame - _armFrame < 6)
                {
                    return hammer;
                }

                ushort id = client.Place(t, t, ObjectId.Workbench, pos);
                if (id != 0)
                {
                    _placeReq = id;
                    _placeTile = t;
                    _placeFrame = _frame;
                }

                return hammer;
            }

            _benchRefused.Clear(); // все места вокруг перебрали — заново (мир мог измениться)
            return hammer;
        }

        private static InputFrame HammerFrame(NetClient client)
        {
            byte slot = 0;
            for (byte s = 0; s < Inventory.Slots; s++)
            {
                if (Items.DigToolOf(client.Inv.SlotAt(s)) == ToolKind.Hammer)
                {
                    slot = s;
                    break;
                }
            }

            return new InputFrame(Input8.None, 0, 0, slot);
        }

        private InputFrame Craft(NetClient client, byte recipe, int count)
        {
            // Очередь пуста и ответа давно нет — заявка (первая или повтор); пока очередь идёт — стоим у верстака.
            if (count > 0 && client.Craft.IsEmpty && _frame - _craftFrame > 90 && client.RequestCraft(recipe, count))
            {
                _craftFrame = _frame;
                CraftRequests++;
            }

            return InputFrame.None;
        }

        private InputFrame Equip(NetClient client, in Inventory inv)
        {
            if (_frame - _moveFrame <= 60)
            {
                return InputFrame.None;
            }

            int cell = -1, slot = Inventory.Slots - 1;
            for (int c = Inventory.Cells - 1; c >= 0; c--)
            {
                cell = inv.ItemAt(c) == Items.CopperPickaxe ? c : cell;
            }

            for (int s = Inventory.Slots - 1; s >= 0; s--)
            {
                slot = inv.SlotAt(s) == Items.None ? s : slot;
            }

            if (cell >= 0 && client.RequestBagMove(cell, slot))
            {
                _moveFrame = _frame;
            }

            return InputFrame.None;
        }
    }
}
