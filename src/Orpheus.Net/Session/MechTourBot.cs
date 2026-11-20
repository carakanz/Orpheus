using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.Trig;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Бот-сценарий <c>mech_tour</c> (E4-2, шаг 4.2 плана): сквозной тур всех новых механик под предсказанием —
    /// пять фаз по кадрам ввода (60/с):
    /// 1) <see cref="CraftChainBot"/> (0–300 с): добыча камня/дерева, верстак, плавка, медная кирка, гранит, топор;
    /// 2) сундук (300–840 с): крафт сундука руками, постановка (мебель, сетка 0,5), открыть (<see cref="NetClient.RequestUse"/>),
    ///    положить (<see cref="NetClient.RequestBagMove"/> в ячейки 40…69), взять, закрыть;
    /// 3) лук (840–1140 с): волокно из кустов, верёвка, лук и стрелы у верстака, стрельба удержанием удара (E3-2);
    /// 4) <see cref="BuildRoomBot"/> (1140–1500 с): добыча, комната 5 × 5 (пол/стены на сетке 0,5, дверь);
    /// 5) ночь в поле (1500–1800 с): охота на ближайшего врага мечом из стартового набора (<see cref="NightFrame"/>) —
    ///    удары, урон, смерть и респавн (счётчики).
    /// Компоненты фаз получают ответы сервера через диспетчер (<see cref="OnReply"/>, <see cref="OnCraftReply"/>).
    /// Счётчики — в <see cref="Summary"/> (числа разбирает тест E4-2).
    /// </summary>
    public sealed class MechTourBot
    {
        public const string Name = "mech_tour";

        /// <summary>Фаза тура.</summary>
        public enum Phase
        {
            Craft = 0,
            Chest = 1,
            Bow = 2,
            Build = 3,
            Night = 4,
            Idle = 5,
        }

        // Границы фаз в кадрах ввода (60/с). 1800 с = 108 000 кадров.
        // Калибровка 20.11 по ЖИВОМУ UDP-прогону (in-proc стенд идёт в 2× ускорении: 1 тик = 2 входных кадра,
        // потому лента фаз завышала прогресс): цепочка верстак→кирка→топор в живом прогоне кончается к ~690 с
        // (на 300 с бот 0 был без кирки/топора, на 900 с бот 1 — с топором). Окна: крафт 0–690 с, сундук
        // 690–1080 с (дерево 8 в густой роще ~150 с + постановка у верстака), лук 1080–1410 с (волокно в густых
        // кустах + верёвка/лук/стрелы у верстака + стрельба), стройка 1410–1620 с, ночь 1620–1800 с (охота с мечом).
        public const int CraftEndFrame = 41_400;     // 690 с (цепочка + затычка: каменный топор)
        public const int ChestEndFrame = 64_800;     // 1080 с (с топором NeedWood ~60–90 с + постановка у верстака)
        public const int BowEndFrame = 84_600;       // 1410 с (волокно/верёвка/лук/стрелы + стрельба)
        public const int BuildEndFrame = 97_200;     // 1620 с (комната 5 × 5: пол, стены, дверь)
        public const int TourEndFrame = 108_000;     // 1800 с

        private const int U = Torus.SubTilesPerTile;
        private const int DigR = 48;          // радиус поиска объекта добычи, тайлов
        private const int DigTimeoutFrames = 1200; // кадров на одно копания, дальше — смена цели

        private enum ChestStep { NeedWood, CraftChest, PlaceChest, OpenChest, Put, Take, Close, Done }

        private enum BowStep { Fiber, Rope, BowAtBench, Arrows, Shoot, Done }

        private readonly MapShape _shape;
        private readonly WorldPos _start;
        private readonly Dictionary<TileCoord, int> _skipUntil = new Dictionary<TileCoord, int>();
        private CraftChainBot? _craft;
        private BuildRoomBot? _build;
        private int _frame;
        private WorldPos _lastPos;
        private int _stuckFrames;
        private int _lastHealth = int.MinValue;

        // копания своих фаз (кусты/дерево/камень)
        private bool _digActive;
        private TileCoord _digTile;
        private ushort _digObj;
        private int _digStartFrame;
        private byte _digSlot;
        private TileCoord _digFindFrom;
        // Сбор дропа из личных куч (E3-3b): срубленное дерево падает в кучу, счёт сумки после сруба
        // не растёт, пока не дойдёшь до кучи (тот же BotPiles.TrySeek, что у BuildRoomBot). В live-зонде
        // 20.11 (`e42_probe17`) фаза сундука вставала на NeedWood именно так: деревья срублены, дерево в кучах.
        private BotPiles? _tourPiles;
        private int _digFindFrame;

        // заявки изготовления
        private int _craftRecipe;
        private int _craftSendFrame = -100_000;

        private ChestStep _chest = ChestStep.NeedWood;
        private int _chestStepFrame;

        /// <summary>Снимок сумки (каждые 600 кадров) — в <see cref="Summary"/>: по нему видно, из-за чего фаза встала.</summary>
        private string _bagDump = "";
        private TileCoord _chestTile;
        private ushort _chestReq;
        private int _chestPlaceTries;
        // Якорь постановки (20.11, live-зонд): кольцо вокруг ТЕКУЩЕЙ позиции бота в лесу давало тайлы в деревьях,
        // попытки кончались на ходьбе. Якорь — верстак (его тайл сервер уже принял), кольцо от него; первый кандидат —
        // южнее верстака (лес севернее, луг открыт). Отказы постановки меряются и пишутся в Summary.
        private bool _chestAnchorSet;
        private TileCoord _chestAnchor;
        public int ChestPlaceRejects { get; private set; }
        public string LastChestReject { get; private set; } = "";

        private BowStep _bow = BowStep.Fiber;
        private int _bowStepFrame;
        private int _arrowsBase;
        private int _arrowSendFrame = -100_000;

        private int _nightFrame;

        private static readonly Input8[] WanderDirs =
        {
            new Input8(1, 0), new Input8(1, 1), new Input8(0, 1), new Input8(-1, 1),
            new Input8(-1, 0), new Input8(-1, -1), new Input8(0, -1), new Input8(1, -1),
        };

        public MechTourBot(MapShape shape, WorldPos spawn)
        {
            _shape = shape;
            _start = spawn;
            _lastPos = spawn;
        }

        /// <summary>Текущая фаза.</summary>
        public Phase Now { get; private set; } = Phase.Craft;

        /// <summary>Кадров ввода прожито.</summary>
        public int Frames => _frame;

        // ------------------------------------------------------------------ счётчики

        /// <summary>Кустов выкопано (волокно), своего пути фаз 2–3.</summary>
        public int BushesDug { get; private set; }
        /// <summary>Деревьев и валунов, выкопанных своими фазами (2–3); цепочка фазы 1 — в <see cref="CraftChainBot"/>.</summary>
        public int TreesDug { get; private set; }
        public int RocksDug { get; private set; }

        /// <summary>Суммарно по всем фазам (свои счётчики + цепочка фазы 1) — по ним судит тест E4-2.</summary>
        public int TreesTotal => TreesDug + (_craft?.TreesCut ?? 0);
        public int RocksTotal => RocksDug + (_craft?.RocksDug ?? 0);
        public int OresTotal => _craft?.OresDug ?? 0;

        /// <summary>Заявок изготовления отправлено (фазы 2–3) и отказов на них.</summary>
        public int CraftRequests { get; private set; }
        public int CraftRefusals { get; private set; }
        public CraftReply LastCraftRefusal { get; private set; }

        /// <summary>Признаки изготовленного (по сумке сервера): сундук, верёвка, лук, стрелы.</summary>
        public bool ChestCrafted { get; private set; }
        public bool RopeCrafted { get; private set; }
        public bool BowCrafted { get; private set; }
        public bool ArrowsCrafted { get; private set; }

        /// <summary>Каменный топор изготовлен в затычке фазы 1 (после цепочки) — им фазы 2–3 рубят деревья.</summary>
        public bool AxeCrafted { get; private set; }

        /// <summary>Сундук: поставлен, открыт, положей, взятий, закрыт.</summary>
        public bool ChestPlaced { get; private set; }
        public bool ChestOpened { get; private set; }
        public int ChestPuts { get; private set; }
        public int ChestTakes { get; private set; }
        public bool ChestClosed { get; private set; }

        /// <summary>Выстрелов (по расходу стрел из сумки сервера).</summary>
        public int Shots { get; private set; }

        /// <summary>Кадров со своим ударом (лук/стройка/ночь) — удержаний кнопки удара.</summary>
        public int AttackFrames { get; private set; }

        /// <summary>Кадров преследования врага в фазе ночи (охота за мечом).</summary>
        private int MeleeChaseFrames { get; set; }

        /// <summary>Своих ударов Health упал (число ударов по снимкам), минимум здоровья в %.</summary>
        public int HitsTaken { get; private set; }
        public int MinHealthPct { get; private set; } = 100;

        /// <summary>Смертей своих («Смерть» 24 по жертве = свой; респавн автоматический).</summary>
        public int Deaths { get; private set; }

        /// <summary>Причина, по которой фаза 2/3 сдалась (постановка сундука, добыча); пустая — тур шёл по плану.</summary>
        public string FailReason { get; private set; } = "";

        /// <summary>Верстак фазы 1 стоит (тайл задан) — фаза 2/3 стреляет в него за ресурсами.</summary>
        public bool BenchPlaced => _craft?.BenchTile != null;
        private bool PickaxeDone => _craft?.Done == true;

        /// <summary>Итог строкой — для отчёта бота и теста E4-2 (числа вида «ключ=число» разбирает тест).</summary>
        public string Summary
        {
            get
            {
                int trees = TreesDug + (_craft?.TreesCut ?? 0);
                int rocks = RocksDug + (_craft?.RocksDug ?? 0);
                int ores = _craft?.OresDug ?? 0;
                int floors = _build?.FloorsPlaced ?? 0;
                int walls = _build?.WallsPlaced ?? 0;
                string door = _build?.DoorPlaced == true ? "1" : "0";
                return
                    $"мех-тур: фаза {Now}; доб: деревьев {trees}, камней {rocks}, руды {ores}, кустов {BushesDug}; " +
                    $"крафт: заявок {CraftRequests}, отказов {CraftRefusals}{(LastCraftRefusal != CraftReply.Accepted ? $" ({LastCraftRefusal})" : "")}, " +
                    $"верстак={(BenchPlaced ? 1 : 0)}, кирка={(PickaxeDone ? 1 : 0)}, топор={(AxeCrafted ? 1 : 0)}, сундук={(ChestCrafted ? 1 : 0)}, верёвка={(RopeCrafted ? 1 : 0)}, лук={(BowCrafted ? 1 : 0)}, стрелы={(ArrowsCrafted ? 1 : 0)}; " +
                    $"сундук: пост={(ChestPlaced ? 1 : 0)}, откр={(ChestOpened ? 1 : 0)}, пол={ChestPuts}, взят={ChestTakes}, закр={(ChestClosed ? 1 : 0)}" +
                    $", попыток={_chestPlaceTries}, отказов={ChestPlaceRejects}{(LastChestReject.Length > 0 ? $" ({LastChestReject})" : "")}; " +
                    $"стрел: выстр={Shots}; стройка: пол={floors}, стен={walls}, дверь={door}; " +
                    $"бой: удары={HitsTaken}, смерти={Deaths}, мин. здоровье {MinHealthPct}%; " +
                    $"кадров {Frames}; {(_bagDump.Length > 0 ? _bagDump : "инв: -")}" +
                    $"{(FailReason.Length > 0 ? $"; отказ: {FailReason}" : "")}";
            }
        }

        // ------------------------------------------------------------------ диспетчер ответов

        /// <summary>Ответ сервера на правку (подключить к <see cref="NetClient.EditReplied"/>).</summary>
        public void OnReply(TileReplyMessage m)
        {
            if (_chestReq != 0 && m.RequestId == _chestReq)
            {
                _chestReq = 0;
                if (m.Reply == EditReply.Accepted)
                {
                    ChestPlaced = true;
                    _chest = ChestStep.OpenChest;
                    _chestStepFrame = _frame;
                }
                else
                {
                    ChestPlaceRejects++;
                    LastChestReject = EditRules.Describe(m.Reply);
                    _chestPlaceTries++;
                    _chest = ChestStep.PlaceChest;
                    if (_chestPlaceTries >= 24)
                    {
                        Fail($"сундук: постановка {EditRules.Describe(m.Reply)}");
                        _chest = ChestStep.Done;
                    }
                }

                return;
            }

            _craft?.OnReply(m);
            _build?.OnReply(m);
        }

        /// <summary>Ответ на заявку изготовления (подключить к <see cref="NetClient.CraftReplied"/>).</summary>
        public void OnCraftReply(CraftReplyMessage m)
        {
            _craft?.OnCraftReply(m);
            if (m.Reply != CraftReply.Accepted)
            {
                CraftRefusals++;
                LastCraftRefusal = m.Reply;
            }
        }

        /// <summary>«Смерть» 24 по жертве = свой (подключить к <see cref="NetClient.DeathSeen"/>).</summary>
        public void OnDeath(DeathMessage m)
        {
            if (m.Victim == _selfId)
            {
                Deaths++;
            }
        }

        private int _selfId = -1;

        // ------------------------------------------------------------------ кадр

        /// <summary>Кадр ввода (60 Гц). <paramref name="pos"/> — своя позиция (снимок или предсказание).</summary>
        public InputFrame Next(NetClient client, WorldPos pos, long nowUs)
        {
            _frame++;
            if (_frame % 600 == 0 && client.World != null)
            {
                _bagDump = BagDump(client) + $" куч={_tourPiles?.Collected ?? 0}";
            }

            if (_selfId < 0)
            {
                _selfId = client.EntityId;
            }

            TrackHealth(client);
            AdvancePhase(client, pos);

            switch (Now)
            {
                case Phase.Craft:
                    if (_craft == null)
                    {
                        _craft = new CraftChainBot(_shape, pos);
                    }

                    if (_craft.Done)
                    {
                        // Затычка фазы 1: каменный топор (камень 3 + дерево 2, руками). Без топора фазы 2–3 рубят
                        // деревья голыми руками и не успевают накопить дерево — на соакере 20.11 фаза сундука
                        // встала на NeedWood (сундук=0, лук=0 за 30 мин).
                        return AxeStepFrame(client, pos);
                    }

                    return _craft.Next(client, pos);

                case Phase.Chest:
                    return ChestStepFrame(client, pos);

                case Phase.Bow:
                    return BowStepFrame(client, pos);

                case Phase.Build:
                    if (_build == null)
                    {
                        _build = new BuildRoomBot(_shape, pos);
                    }

                    return _build.Next(client, pos, nowUs);

                case Phase.Night:
                    return NightFrame(client, pos);

                default:
                    return InputFrame.None;
            }
        }

        private void AdvancePhase(NetClient client, WorldPos pos)
        {
            Phase next =
                _frame < CraftEndFrame ? Phase.Craft :
                _frame < ChestEndFrame ? Phase.Chest :
                _frame < BowEndFrame ? Phase.Bow :
                _frame < BuildEndFrame ? Phase.Build :
                _frame < TourEndFrame ? Phase.Night : Phase.Idle;
            if (next == Now)
            {
                return;
            }

            StopOwnDig(client);
            _craftRecipe = 0;
            Now = next;
            if (next == Phase.Chest)
            {
                _chest = ChestStep.NeedWood;
                _chestStepFrame = _frame;
            }
            else if (next == Phase.Bow)
            {
                _bow = BowStep.Fiber;
                _bowStepFrame = _frame;
            }
            else if (next == Phase.Build && _build == null)
            {
                _build = new BuildRoomBot(_shape, pos);
            }
        }

        private void Fail(string reason)
        {
            if (FailReason.Length == 0)
            {
                FailReason = reason;
            }
        }

        private void TrackHealth(NetClient client)
        {
            if (!client.HasSnapshot)
            {
                return;
            }

            int hp = client.Latest.Self.Health;
            if (_lastHealth != int.MinValue && hp < _lastHealth)
            {
                HitsTaken++;
            }

            _lastHealth = hp;
            int pct = hp * 100 / BuildRoomBot.MaxHealth;
            if (pct < MinHealthPct)
            {
                MinHealthPct = pct;
            }
        }

        private bool Alive(NetClient client) =>
            client.HasSnapshot && client.Latest.Self.Present && client.Latest.Self.Health > 0;

        // ------------------------------------------------------------------ фаза 2: сундук

        private InputFrame ChestStepFrame(NetClient client, WorldPos pos)
        {
            if (!Alive(client) || client.World == null)
            {
                return InputFrame.None;
            }

            // Шаг-сбор (рубит/копает до нужного числа) получает бюджет по времени добычи с ходьбой:
            // дерево 3 за ствол, ближайший ствол 10–12 тайлов (~25 с пешком). Прежние 5400 кадров (90 с)
            // срабатывали на втором дереве — фаза умирала на NeedWood, хотя дерево шло в сумку (20.11).
            // Постановка сундука — тот же класс шага (20.11, live-зонд): до 24 попыток, к каждой идти.
            if (_frame - _chestStepFrame > (_chest == ChestStep.NeedWood || _chest == ChestStep.PlaceChest ? 12_000 : 5_400))
            {
                Fail($"сундук: фаза встала на {_chest}");
                _chest = ChestStep.Done;
            }

            switch (_chest)
            {
                case ChestStep.NeedWood:
                    if (DigFor(client, pos, ObjectId.Tree, Items.Wood, 8, () => TreesDug++))
                    {
                        _chest = ChestStep.CraftChest;
                        _chestStepFrame = _frame;
                    }

                    return _digFrame;

                case ChestStep.CraftChest:
                    if (client.Inv.Count(Items.Chest) > 0)
                    {
                        ChestCrafted = true;
                        _chest = ChestStep.PlaceChest;
                        _chestStepFrame = _frame;
                        return InputFrame.None;
                    }

                    SendCraft(client, RecipeTable.Chest, 1);
                    return InputFrame.None;

                case ChestStep.PlaceChest:
                    return PlaceChestFrame(client, pos);

                case ChestStep.OpenChest:
                    if (InDigReach(pos, _chestTile))
                    {
                        if (client.ChestOpen)
                        {
                            ChestOpened = true;
                            _chest = ChestStep.Put;
                            _chestStepFrame = _frame;
                            return InputFrame.None;
                        }

                        if (_frame % 30 == 0 && client.RequestUse(_chestTile))
                        {
                            return InputFrame.None;
                        }
                    }

                    return WalkToTileNear(pos, _chestTile);

                case ChestStep.Put:
                {
                    if (client.Chest != null && client.Chest.Count(Items.Wood) > 0)
                    {
                        ChestPuts++;
                        _chest = ChestStep.Take;
                        _chestStepFrame = _frame;
                        return InputFrame.None;
                    }

                    int from = BagCellWith(client.Inv, Items.Wood);
                    if (from >= 0 && _frame % 30 == 0)
                    {
                        client.RequestBagMove(from, Simulation.ChestCellBase); // 40 — сундуковая ячейка 0
                    }

                    return InputFrame.None;
                }

                case ChestStep.Take:
                {
                    if (client.Chest != null && client.Chest.Count(Items.Wood) == 0)
                    {
                        ChestTakes++;
                        _chest = ChestStep.Close;
                        _chestStepFrame = _frame;
                        return InputFrame.None;
                    }

                    int to = BagCellWith(client.Inv, Items.Wood);
                    if (to < 0)
                    {
                        to = Inventory.Slots + 3; // сумка: ячейка 11
                    }

                    if (_frame % 30 == 0)
                    {
                        client.RequestBagMove(Simulation.ChestCellBase, to); // сундуковая ячейка 0 → сумка
                    }

                    return InputFrame.None;
                }

                case ChestStep.Close:
                    if (!client.ChestOpen)
                    {
                        ChestClosed = ChestPuts > 0 && ChestTakes > 0;
                        _chest = ChestStep.Done;
                        return InputFrame.None;
                    }

                    if (_frame % 30 == 0)
                    {
                        client.RequestCloseChest();
                    }

                    return InputFrame.None;

                default:
                    return InputFrame.None;
            }
        }

        private InputFrame _digFrame;

        private InputFrame PlaceChestFrame(NetClient client, WorldPos pos)
        {
            Inventory inv = client.Inv;
            int cell = BuildHand.SlotWith(inv, Items.Chest);
            if (cell < 0)
            {
                if (BuildHand.StockMove(inv, Items.Chest, out int from, out int to) && _frame % 30 == 0 && client.RequestBagMove(from, to))
                {
                    return InputFrame.None;
                }

                return InputFrame.None; // сумка сервера ещё без сундука
            }

            TileCoord me = Torus.TileOf(_shape, pos);
            if (!_chestAnchorSet)
            {
                // Якорь — верстак (сервер его принял, тайл его открыт); верстака нет — текущая позиция.
                _chestAnchor = _craft?.BenchTile ?? me;
                _chestAnchorSet = true;
            }

            int quad = _chestPlaceTries & 3;
            // Кандидаты от якоря: 0 — южнее верстака на 3 (лес севернее, луг открыт), дальше кольцо 8 сторон × радиусы 3–5.
            // Занятые миром тайлы (дерево/камень/куст/постройка) пропускаем без ходьбы — попытка на них гибла в 20.11.
            var tile = _chestPlaceTries == 0
                ? Torus.Wrap(_shape, new TileCoord(_chestAnchor.X, _chestAnchor.Y + 3))
                : AnchorRingTile(_chestAnchor, _chestPlaceTries - 1);
            if (client.World!.World.GetObject(tile) != ObjectId.Empty || client.World.World.TryGetBuild(tile, out _))
            {
                _chestPlaceTries++;
                if (_chestPlaceTries >= 24)
                {
                    Fail("сундук: постановка — все тайлы вокруг верстака заняты");
                    _chest = ChestStep.Done;
                }

                return InputFrame.None;
            }

            if (_chestReq != 0)
            {
                return InputFrame.None; // постановка в пути
            }

            if (!InDigReach(pos, tile))
            {
                return WalkToTileNear(pos, tile);
            }

            var hand = new BuildHand(client);
            BuildCheck c = hand.Click(client.World!.World, pos, inv, cell, true, tile, quad);
            if (c.Ok)
            {
                _chestReq = hand.LastRequest;
                _chestTile = tile;
                _chestPlaceTries++;
            }
            else
            {
                _chestPlaceTries++;
                LastChestReject = c.Reason;
                if (_chestPlaceTries >= 24)
                {
                    Fail($"сундук: постановка {c.Reason}");
                    _chest = ChestStep.Done;
                }
            }

            return InputFrame.None;
        }

        /// <summary>Тайл кольца от якоря: попытка n (0…23) — 8 сторон × радиусы 3–5.</summary>
        private TileCoord AnchorRingTile(TileCoord anchor, int n)
        {
            int[] dirX = { 1, 1, 0, -1, -1, -1, 0, 1 };
            int[] dirY = { 0, 1, 1, 1, 0, -1, -1, -1 };
            int ring = 3 + n / 8;
            return Torus.Wrap(_shape, new TileCoord(anchor.X + dirX[n % 8] * ring, anchor.Y + dirY[n % 8] * ring));
        }

        // ------------------------------------------------------------------ фаза 3: лук

        private InputFrame BowStepFrame(NetClient client, WorldPos pos)
        {
            if (!Alive(client) || client.World == null)
            {
                return InputFrame.None;
            }

            // Бюджет шага: сбор (кусты/деревья/валуны) — добыча с ходьбой до 4 стволов; остальное — 60 с.
            int bowStall = _bow is BowStep.Fiber or BowStep.BowAtBench or BowStep.Arrows ? 12_000 : 3_600;
            if (_bow != BowStep.Shoot && _frame - _bowStepFrame > bowStall)
            {
                Fail($"лук: фаза встала на {_bow}");
                _bow = BowStep.Done;
            }

            switch (_bow)
            {
                case BowStep.Fiber:
                    if (DigFor(client, pos, ObjectId.Bush, Items.Fiber, 3, () => BushesDug++))
                    {
                        _bow = BowStep.Rope;
                        _bowStepFrame = _frame;
                    }

                    return _digFrame;

                case BowStep.Rope:
                    if (client.Inv.Count(Items.Rope) > 0)
                    {
                        RopeCrafted = true;
                        _bow = BowStep.BowAtBench;
                        _bowStepFrame = _frame;
                        return InputFrame.None;
                    }

                    SendCraft(client, RecipeTable.Rope, 1);
                    return InputFrame.None;

                case BowStep.BowAtBench:
                    if (client.Inv.Count(Items.SimpleBow) > 0)
                    {
                        BowCrafted = true;
                        _bow = BowStep.Arrows;
                        _bowStepFrame = _frame;
                        return InputFrame.None;
                    }

                    if (client.Inv.Count(Items.Wood) < 10 && DigFor(client, pos, ObjectId.Tree, Items.Wood, 10, () => TreesDug++))
                    {
                        _bowStepFrame = _frame; // дерево добирали — шаг завершён, повторяем проверку
                    }

                    if (_digActive)
                    {
                        return _digFrame;
                    }

                    TileCoord? bench = _craft?.BenchTile;
                    if (bench == null)
                    {
                        Fail("лук: верстака нет (фаза 1 его не поставила)");
                        _bow = BowStep.Done;
                        return InputFrame.None;
                    }

                    if (!InDigReach(pos, bench.Value))
                    {
                        return WalkToTileNear(pos, bench.Value);
                    }

                    SendCraft(client, RecipeTable.SimpleBow, 1);
                    return InputFrame.None;

                case BowStep.Arrows:
                    if (client.Inv.Count(Items.Arrows) >= 10)
                    {
                        ArrowsCrafted = true;
                        _bow = BowStep.Shoot;
                        _arrowsBase = client.Inv.Count(Items.Arrows);
                        return InputFrame.None;
                    }

                    if (client.Inv.Count(Items.Stone) < 1 && DigFor(client, pos, ObjectId.Rock, Items.Stone, 1, () => RocksDug++))
                    {
                        _bowStepFrame = _frame;
                    }

                    if (_digActive)
                    {
                        return _digFrame;
                    }

                    TileCoord? benchForArrows = _craft?.BenchTile;

                    if (benchForArrows == null)
                    {
                        Fail("лук: верстака нет (стрелы)");
                        _bow = BowStep.Done;
                        return InputFrame.None;
                    }

                    if (!InDigReach(pos, benchForArrows.Value))
                    {
                        return WalkToTileNear(pos, benchForArrows.Value);
                    }

                    SendCraft(client, RecipeTable.Arrows, 2);
                    return InputFrame.None;

                case BowStep.Shoot:
                    return ShootFrame(client, pos);

                default:
                    return InputFrame.None;
            }
        }

        private InputFrame ShootFrame(NetClient client, WorldPos pos)
        {
            int arrows = client.Inv.Count(Items.Arrows);
            if (arrows < _arrowsBase)
            {
                Shots += _arrowsBase - arrows;
                _arrowsBase = arrows;
            }

            if (arrows == 0)
            {
                if (_frame - _arrowSendFrame > 300)
                {
                    _arrowSendFrame = _frame;
                    SendCraft(client, RecipeTable.Arrows, 1, force: true);
                }

                return InputFrame.None; // ждём стрелы у верстака (рядом быть не обязаны — очередь)
            }

            int slot = BuildHand.SlotWith(client.Inv, Items.SimpleBow);
            if (slot < 0)
            {
                if (BuildHand.StockMove(client.Inv, Items.SimpleBow, out int from, out int to) && _frame % 30 == 0 && client.RequestBagMove(from, to))
                {
                    return InputFrame.None;
                }

                return InputFrame.None;
            }

            AttackFrames++;
            return new InputFrame(Input8.None, InputButtons.Attack, 0, (byte)slot); // удержание — непрерывная стрельба (C-031)
        }

        // ------------------------------------------------------------------ фаза 5: ночь в поле

        /// <summary>
        /// Ночь в поле: охота на ближайшего живого врага — меч из стартового набора (<c>06</c> 0), удар в сектор
        /// прицела в досягаемости, шаг к цели иначе; врагов нет — бродит. Прежний простой бродёж (прогон 20.11)
        /// давал 4–14 ударов за 6 минут ночи и ни одной смерти (мин. здоровье 50 %), а приём тура требует смерти.
        /// </summary>
        private InputFrame NightFrame(NetClient client, WorldPos pos)
        {
            _nightFrame++;
            if (!Alive(client) || client.World == null)
            {
                return InputFrame.None; // мёртв (автореспавн) или мир не пришёл — ждём
            }

            Snapshot snap = client.Latest;
            const int reach = Reach.SwordStrikeUnits + Collision.RadiusUnits; // сектор + круг цели (`02` 3.2)
            long bestD2 = long.MaxValue;
            WorldPos target = default;
            for (int i = 0; i < snap.Count; i++)
            {
                SnapshotEntity e = snap.Entities[i];
                if (e.Id == client.EntityId || e.Enemy >> SnapshotLayout.EnemyArchetypeShift == 0 || e.HealthPct == 0)
                {
                    continue; // не враг (архетип 0 — мозг выключен) или мёртв
                }

                Int2 d = Torus.Delta(_shape, pos, e.Position);
                long d2 = (long)d.X * d.X + (long)d.Y * d.Y;
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    target = e.Position;
                }
            }

            if (bestD2 == long.MaxValue)
            {
                return new InputFrame(WanderDirs[_nightFrame / 150 % WanderDirs.Length]);
            }

            int sword = BuildHand.SlotWith(client.Inv, Items.WoodenSword);
            if (bestD2 <= (long)reach * reach && sword >= 0)
            {
                AttackFrames++;
                Int2 d = Torus.Delta(_shape, pos, target);
                return new InputFrame(Input8.None, InputButtons.Attack, Angle16.FromDirection(d).Raw, (byte)sword);
            }

            MeleeChaseFrames++;
            return WalkToTileNear(pos, Torus.TileOf(_shape, target));
        }

        // ------------------------------------------------------------------ добыча своих фаз

        /// <summary>
        /// Копает ближайший объект <paramref name="obj"/> до <paramref name="need"/> предмета в сумке (кадры хода/копания —
        /// в <see cref="_digFrame"/>). true — нужное уже в сумке (копать не надо).
        /// </summary>
        private bool DigFor(NetClient client, WorldPos pos, ushort obj, byte item, int need, Action onDug)
        {
            _digFrame = InputFrame.None;
            if (client.Inv.Count(item) >= need)
            {
                StopOwnDig(client);
                return true;
            }

            if (_digActive)
            {
                if (_frame - _digStartFrame > DigTimeoutFrames)
                {
                    StopOwnDig(client);
                    _skipUntil[_digTile] = _frame + 900;
                }
                else if (client.World!.World.GetObject(_digTile) != obj)
                {
                    _digActive = false;
                    onDug();
                }
                else
                {
                    _digFrame = new InputFrame(Input8.None, InputButtons.Dig, 0, _digSlot);
                    return false;
                }
            }

            // Дроп срубленного/скопанного лежит в личных кучах (E3-3b) — пока не соберёшь цельный предмет
            // этой фазы, следующего объекта не ищем: в live-зонде 20.11 NeedWood срубал 3–4 дерева и стоял на
            // Count(дерево)=2 (всё дерево в кучах), фаза умирала по сторожу.
            _tourPiles ??= new BotPiles(_shape);
            if (_tourPiles.TrySeek(client, pos, it => it == item, out Input8 toPile))
            {
                _digFrame = new InputFrame(toPile);
                return false;
            }

            ClientWorld w = client.World!;
            if (_frame - _digFindFrame > 120 || !TargetAlive(w, obj))
            {
                _digFindFrame = _frame;
                _digFindFrom = Torus.TileOf(_shape, pos);
                if (!FindTile(w, _digFindFrom, obj, out TileCoord t))
                {
                    _digFrame = new InputFrame(WanderDirs[_frame / 120 % WanderDirs.Length]); // цели нет в окне — бродим
                    return false;
                }

                _digTile = t;
            }

            if (InDigReach(pos, _digTile))
            {
                byte tool = ToolSlotFor(client, obj);
                ushort id = client.StartDig(_digTile);
                if (id != 0)
                {
                    _digActive = true;
                    _digObj = obj;
                    _digSlot = tool;
                    _digStartFrame = _frame;
                    _digFrame = new InputFrame(Input8.None, InputButtons.Dig, 0, tool);
                }
                else
                {
                    _skipUntil[_digTile] = _frame + 300;
                    _digFrame = InputFrame.None;
                }

                return false;
            }

            _digFrame = WalkToTileNear(pos, _digTile);
            return false;
        }

        private bool TargetAlive(ClientWorld w, ushort obj) =>
            w.World.GetObject(_digTile) == obj && !_skipUntil.ContainsKey(_digTile);

        private void StopOwnDig(NetClient client)
        {
            if (_digActive)
            {
                _digActive = false;
                client.StopDig();
            }
        }

        /// <summary>
        /// Ближайший тайл с объектом в окне <see cref="DigR"/> (рядом с ботом; прежнему обходу ряд за рядом
        /// попадался дальний угол окна — 24 тайла пешком на каждое дерево, и фаза сундука не успевала
        /// накопить дерево за 90 с, 20.11). Пропускает тайлы из <see cref="_skipUntil"/> и предсказанные.
        /// </summary>
        private bool FindTile(ClientWorld w, TileCoord from, ushort obj, out TileCoord best)
        {
            long bestD2 = long.MaxValue;
            TileCoord found = default;
            bool any = false;
            for (int dy = -DigR; dy <= DigR; dy++)
            {
                for (int dx = -DigR; dx <= DigR; dx++)
                {
                    var t = Torus.Wrap(_shape, new TileCoord(from.X + dx, from.Y + dy));
                    if (w.IsPredicted(t) || w.World.GetObject(t) != obj)
                    {
                        continue;
                    }

                    if (_skipUntil.TryGetValue(t, out int until) && _frame < until)
                    {
                        continue;
                    }

                    long d2 = (long)dx * dx + (long)dy * dy;
                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        found = t;
                        any = true;
                    }
                }
            }

            best = found;
            return any;
        }

        private bool InDigReach(WorldPos pos, TileCoord t) =>
            EditRules.InReach(_shape, pos, t, EditRules.DigReachUnits - U / 4);

        // ------------------------------------------------------------------ ходьба

        /// <summary>Идёт к точке у тайла со стороны бота (чтобы встать в радиусе добычи/постановки).</summary>
        private InputFrame WalkToTileNear(WorldPos pos, TileCoord t)
        {
            WorldPos c = TileCenter(t);
            int dx = Torus.DeltaX(_shape, c.X, pos.X);
            int dy = Torus.DeltaY(_shape, c.Y, pos.Y);
            int sx = Math.Abs(dx) >= Math.Abs(dy) ? Math.Sign(dx) : 0;
            int sy = sx == 0 ? Math.Sign(dy) : 0;
            if (sx == 0 && sy == 0)
            {
                sx = 1;
            }

            var near = new WorldPos(c.X + sx * U, c.Y + sy * U);
            return WalkTo(pos, near, t);
        }

        /// <summary>Ходьба к точке с детектором «упёрся» (помеха — в пропуск на время).</summary>
        private InputFrame WalkTo(WorldPos pos, WorldPos target, TileCoord goalTile)
        {
            int dx = Torus.DeltaX(_shape, pos.X, target.X);
            int dy = Torus.DeltaY(_shape, pos.Y, target.Y);
            int sx = Math.Abs(dx) > 2 ? Math.Sign(dx) : 0;
            int sy = Math.Abs(dy) > 2 ? Math.Sign(dy) : 0;
            var dir = new Input8(sx, sy);
            if (dir.IsMoving && pos == _lastPos)
            {
                if (++_stuckFrames > 50)
                {
                    _stuckFrames = 0;
                    _skipUntil[goalTile] = _frame + 400;
                    _digFindFrame = 0; // сменить цель
                }
            }
            else
            {
                _stuckFrames = 0;
            }

            _lastPos = pos;
            return new InputFrame(dir);
        }

        private WorldPos TileCenter(TileCoord t) =>
            new WorldPos((t.X << Torus.SubTileBits) + U / 2, (t.Y << Torus.SubTileBits) + U / 2);

        // ------------------------------------------------------------------ затычка фазы 1: каменный топор

        /// <summary>
        /// После цепочки фазы 1 добираем каменный топор (камень 3 + дерево 2, руками) — им фазы 2–3 рубят деревья.
        /// Без топора деревья копаются голыми руками (~в 6 раз медленнее), и фаза сундука не успевает накопить
        /// дерево: на соакере 20.11 она встала на NeedWood (сундук=0, лук=0 за 30 мин).
        /// </summary>
        private InputFrame AxeStepFrame(NetClient client, WorldPos pos)
        {
            if (!Alive(client) || client.World == null)
            {
                return InputFrame.None;
            }

            if (client.Inv.Count(Items.StoneAxe) > 0)
            {
                AxeCrafted = true;
                return InputFrame.None;
            }

            if (client.Inv.Count(Items.Stone) < 3)
            {
                if (DigFor(client, pos, ObjectId.Rock, Items.Stone, 3, () => RocksDug++))
                {
                    return InputFrame.None; // камень готов, в следующем кадре — за деревом
                }

                return _digFrame;
            }

            if (client.Inv.Count(Items.Wood) < 2)
            {
                if (DigFor(client, pos, ObjectId.Tree, Items.Wood, 2, () => TreesDug++))
                {
                    return InputFrame.None; // дерево готово, в следующем кадре — крафт
                }

                return _digFrame;
            }

            SendCraft(client, RecipeTable.StoneAxe, 1);
            return InputFrame.None;
        }

        // ------------------------------------------------------------------ крафт и сумка

        /// <summary>Заявку крафта — не чаще раза в 60 кадров (лимит 8 заявок на такт — общий с сумкой).</summary>
        private void SendCraft(NetClient client, int recipe, int count, bool force = false)
        {
            if (!force && _craftRecipe == recipe && _frame - _craftSendFrame < 60)
            {
                return;
            }

            _craftRecipe = recipe;
            _craftSendFrame = _frame;
            if (client.RequestCraft(recipe, count))
            {
                CraftRequests++;
            }
        }

        /// <summary>Ячейка сумки (не панели) с предметом; нет — −1.</summary>
        private static int BagCellWith(in Inventory bag, byte item)
        {
            for (int c = Inventory.Slots; c < Inventory.Cells; c++)
            {
                if (bag.ItemAt(c) == item && bag.CountIn(c) > 0)
                {
                    return c;
                }
            }

            return -1;
        }

        /// <summary>
        /// Снимок сумки в <see cref="Summary"/> (по 600 кадрам): содержимое и свободное место под дерево.
        /// Нужен, чтобы видеть причину остановки фазы: на соакере 20.11 фаза сундука стояла на <c>NeedWood</c>
        /// при восьми срубленных деревьях — loot уходит на землю, когда сумка набита.
        /// </summary>
        private static string BagDump(NetClient client)
        {
            Inventory inv = client.Inv;
            int used = 0;
            for (int c = 0; c < Inventory.Cells; c++)
            {
                if (inv.ItemAt(c) != Items.None)
                {
                    used++;
                }
            }

            return $"инв: дерево={inv.CountAt(Material.Wood)} камень={inv.CountAt(Material.Stone)} " +
                $"волокно={inv.CountAt(Material.Fiber)} медь={inv.CountAt(Material.Copper)} " +
                $"мест {used}/{Inventory.Cells} место-под-дерево={inv.RoomFor(Items.Wood)}";
        }

        /// <summary>Ячейка панели с инструментом, который копаёт объект (время &gt; 0 по таблице клиента); нет — 0 (1-010).</summary>
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

            return 0;
        }
    }
}
