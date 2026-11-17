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
    public sealed partial class CraftChainBot
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
        private int _slotFrame = -100000;
        private const int ArmFrames = 6;
        private byte _sentSlot;
        private int _frame;
        private WorldPos _lastPos;

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

        /// <summary>Кадров ввода прожито.</summary>
        public int Frames => _frame;

        /// <summary>Поисков пути всего и тайлов, обойдённых последним (диагностика: 1 — бот заперт или мир не пришёл).</summary>
        public int Plans { get; private set; }
        public int LastPlanTiles { get; private set; }

        /// <summary>Где стоит верстак (после постановки).</summary>
        public TileCoord? BenchTile => _hasBench ? _bench : (TileCoord?)null;

        /// <summary>Снятый тайл гранита.</summary>
        public TileCoord? GraniteTile { get; private set; }

        /// <summary>Итог строкой — для отчёта бота и теста.</summary>
        public string Summary =>
            $"этап {Now}; деревьев {TreesCut}, валунов {RocksDug}, залежей {OresDug}, гранита {GraniteDug}, прочего {OtherDug}; " +
            $"отказов правок {Refused} (последний: {EditRules.Describe(LastRefusal)}), заявок крафта {CraftRequests}, отказов {CraftRefused} ({LastCraftRefusal}); " +
            $"наугад {Explores}, прорубался {DigThroughs}, поисков {Plans} (последний — {LastPlanTiles} тайлов); кадры этапов: руда {StageFrame[1]}, верстак {StageFrame[2]}, плавка {StageFrame[3]}, " +
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
            if (m.Reply == EditReply.Accepted && (m.Mask & StrikeMessage.ReplySwingBit) != 0)
            {
                return; // C-028: взмах кирки — снятые тайлы приходят «взмахом» (CountBroken)
            }

            if (m.Reply != EditReply.Accepted)
            {
                Refused++;
                LastRefusal = m.Reply;
                _skipUntil[t] = _frame + (m.Reply == EditReply.OutOfReach || m.Reply == EditReply.ToolRequired ? 30 : 600);
                _path.Clear();
                _hasTarget = false;
                return;
            }

            Count(t, raw);
        }

        private readonly List<(TileCoord Tile, ushort Obj)> _broken = new List<(TileCoord Tile, ushort Obj)>();

        private void Count(TileCoord t, ushort raw)
        {
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
            _broken.Clear();
            client.DrainBrokenByMe(_broken); // C-028: снятое своими взмахами — в счёт
            foreach ((TileCoord t, ushort raw) in _broken)
            {
                Count(t, raw);
            }

            InputFrame f = Decide(client, pos);
            if (f.Slot != _sentSlot)
            {
                _sentSlot = f.Slot;
                _slotFrame = _frame; // ячейка в руках сменилась — отсчёт «взвода» заново
            }

            return f;
        }

        private InputFrame Decide(NetClient client, WorldPos pos)
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
                    return Craft(client, world, pos, RecipeTable.CopperIngot, NeedIngots - inv.Count(Items.CopperIngot));
                case Stage.Pickaxe:
                    return Craft(client, world, pos, RecipeTable.CopperPickaxe, 1);
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

            if (_placeReq != 0 || inv.Count(Items.Workbench) > 0 || _frame - _benchOrderFrame < 300)
            {
                return Stage.Bench; // постановка в пути, верстак-предмет в сумке или делается (E2-11): материалы уже сняты
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
    }
}
