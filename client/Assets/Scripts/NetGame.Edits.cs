#nullable enable
using System.Collections.Generic;
using System.Text;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;
using Material = Orpheus.Core.Sim.Material; // HUD сумки: Sim.Material, не UnityEngine.Material

namespace Orpheus.Client
{
    /// <summary>
    /// Правки тайлов в сетевой игре (0-028; docs/07, 3.7; `05` 1.3–1.5) — заглушка управления до палитры и инструментов:
    /// ЛКМ зажата — копать тайл под курсором (повёл курсор — копается следующий); ПКМ — поставить каменную стену под курсором.
    /// Предсказание и откат — в <see cref="ClientWorld"/>; здесь — откат на экране: красная вспышка на тайле 0,6 с и причина в HUD.
    /// Вспышка — квадрат из текстуры 1 × 1, созданной в коде (ассета нет — `_TEMP` не нужен).
    /// </summary>
    public sealed partial class NetGame
    {
        /// <summary>Вспышка отката держится 0,6 с.</summary>
        public const long FlashUs = 600_000;

        struct Flash
        {
            public TileCoord Tile;
            public long UntilUs;
            public SpriteRenderer Renderer;
        }

        readonly List<Flash> _flashes = new List<Flash>();

        // Зеркало своей сумки (1-002): наполняется сервером — «сумка» (тип 26) приходит при входе и при каждом изменении
        // (копание, подбор). Выбранная ячейка для руки берётся из своих кадров, пока подтверждение не пришло.
        Inventory _bag = Inventory.StartingKit();

        /// <summary>Сумка от сервера (этап 4): зеркало — серверная сумка целиком, включая выбранную ячейку.</summary>
        void OnInventory(InventoryMessage m) => _bag = m.Bag;

        /// <summary>Предмет в руках: ячейка последнего отправленного кадра, содержимое — из зеркала сумки (1-002).</summary>
        byte HeldItem => _bag.SlotAt(HeldCell);

        /// <summary>Ячейка панели в руке (E2-11: из неё заявка постановки берёт предмет-постройку).</summary>
        int HeldCell => _pred?.SelectedSlot ?? _bag.Selected;

        /// <summary>
        /// Предмет в руках в кадре с этой ячейкой: сервер применяет ячейку в том же кадре, что кнопки (1-002),
        /// и с ней же решает удар (melee-гейт такта, 1-004) — предсказание берёт предмет по ячейке кадра.
        /// </summary>
        byte HeldItemAt(byte slot) => _bag.SlotAt(slot);

        // ---------- E2-16: поворот (R) и постановка из меню изготовления ----------

        // Поворот последней постановки каждого предмета (0…3): «поворот запоминается для следующей постановки того же предмета».
        readonly Dictionary<byte, int> _turns = new Dictionary<byte, int>();
        int _menuSlot = -1;        // ячейка панели, при которой взяли «из меню» (сменили ячейку — убрать)
        int _menuAvailable;        // «есть» для меню: готовые в сумке + изготовимые (пересчёт каждый кадр)
        int _menuCancelFrame = -1; // кадр, в котором Esc убрал постройку из меню (паузу этот Esc не ставит)

        /// <summary>Постройка взята «Поставить» из окна изготовления (E2-16).</summary>
        bool FromMenu => _ui != null && _ui.MenuPlace != Items.None;

        /// <summary>Предмет «в руке» для стройки: из меню — он, иначе — ячейка панели.</summary>
        byte HandItem => FromMenu ? _ui!.MenuPlace : HeldItem;

        /// <summary>Сумка для проверок и линейки: из меню — копия с постройкой в ячейке руки (<see cref="MenuHand.VirtualBag"/>).</summary>
        Inventory HandBag(NetClient c) => FromMenu ? MenuHand.VirtualBag(c.Inv, HeldCell, _ui!.MenuPlace, _menuAvailable) : c.Inv;

        /// <summary>Рука, через которую идут заявки: из меню — с флагом меню.</summary>
        BuildHand HandBuild => FromMenu ? _ui!.MenuBuild : _ui!.Build;

        /// <summary>Поворот предмета в руке (0 — не поворачивается или ещё не крутили).</summary>
        int TurnOf(byte item) => BuildHand.IsBuildItem(item, out ushort key) && Turnable(key) && _turns.TryGetValue(item, out int r) ? r : 0;

        static bool Turnable(ushort key) => BuildLayers.IsRotatable(key) || key == ObjectId.Door;

        /// <summary>Ключ постройки в руке с её поворотом.</summary>
        ushort HandKey(byte item) => BuildHand.IsBuildItem(item, out ushort key) ? BuildLayers.Turned(key, TurnOf(item)) : (ushort)0;

        // Кадр стройки: R — повернуть (как в Factorio; Shift+R — обратно), Tab — «линейка / поштучно» (до E2-16 — R),
        // Esc / ПКМ — убрать постройку из меню, смена ячейки панели — тоже.
        void TurnAndMenuKeys(byte item)
        {
            Keyboard? kb = Keyboard.current;
            if (kb == null || _ui == null)
            {
                return;
            }

            if (kb.rKey.wasPressedThisFrame && BuildHand.IsBuildItem(item, out ushort key) && Turnable(key))
            {
                _turns[item] = (TurnOf(item) + (kb.leftShiftKey.isPressed ? 3 : 1)) & 3;
                Debug.Log($"[build] поворот {item}: {_turns[item] * 90}°");
            }

            if (kb.tabKey.wasPressedThisFrame && !BuildHand.IsHammer(item))
            {
                PlaceMode mode = _ui.ToggleRuler(); // E2-13 → E2-16: Tab
                Debug.Log($"[build] режим постановки: {mode}");
            }

            if (!FromMenu)
            {
                _menuSlot = -1;
                return;
            }

            if (_menuSlot < 0)
            {
                _menuSlot = HeldCell;
            }

            bool rmb = Mouse.current?.rightButton.wasPressedThisFrame == true && !_ui.Ruler.Dragging;
            bool esc = kb.escapeKey.wasPressedThisFrame && !_ui.Ruler.Dragging;
            if (esc || rmb || HeldCell != _menuSlot)
            {
                if (esc)
                {
                    _menuCancelFrame = Time.frameCount;
                }

                _ui.MenuPlace = Items.None;
                _ui.Ruler.Cancel();
                _menuSlot = -1;
            }
        }

        /// <summary>
        /// Кадр интерфейса (E2-2): быстрая панель, окно сумки (Tab), полосы. Показывает сумку сервера и состояние
        /// своего персонажа; заявки на сумку уходят через <see cref="BagHand"/> в <see cref="NetClient"/> (сообщение 29).
        /// В net-smoke ввод интерфейсу не даётся (кадр скриптованный).
        /// </summary>
        void UiFrame()
        {
            NetClient c = _client!;
            if (_ui == null)
            {
                _ui = new UI.GameUi(_g.Camera, c, c, c);
                c.CraftReplied = _ui.CraftReplied; // E2-4: отказ сервера (сообщение 31) — строкой в окне крафта
                UI.GameUi ui = _ui;
                c.UseReplied = m =>
                {
                    // E2-5: ответ на F — отказ строкой над панелью; кровать — «здесь вы возродитесь».
                    if (m.Reply != EditReply.Accepted)
                    {
                        ui.Notify(UI.UiStrings.UseRefusal(EditRules.Describe(m.Reply)));
                    }
                    else if (ObjectUse.KindOf(m.Obj) == UseKind.Bed)
                    {
                        ui.Notify(UI.UiStrings.BedSet);
                    }
                };
                _ghost = new BuildGhost(_entityRoot.transform);
            }

            UI.UiInput input = _args.NetSmokeSeconds > 0 ? default : _ui.ReadInput();
            LocalPrediction? pr = _pred;
            // E2-4: окно крафта показывает очередь сервера; прогресс первой позиции — по такту сервера, без предсказания.
            var craft = new UI.UiCraftState
            {
                Queue = c.Craft, Running = c.CraftRunning, Progress = c.CraftProgressAt(c.ServerTick), World = _world, Position = _localPos,
                Stock = c.NearbyStock, // E2-10: сундуки рядом — «есть» в окне
            };
            _craftState = craft;
            UI.UiBuildState build = _args.NetSmokeSeconds > 0 ? default : BuildState(c, pr);
            _ui.MapSnapshot = c.HasSnapshot ? c.Latest : null; // C-008: точки игроков и врагов на мини-карте
            _ui.MapSelf = _localId;
            _ui.Frame(input, _bag, _slot, pr?.Dead ?? false, pr?.Health ?? 0, Simulation.MaxHealthCharacter, pr?.Stamina ?? 0,
                Orpheus.Core.Movement.Stamina.Max, craft, build);
            if (_ui.SlotClicked >= 0)
            {
                _slot = _ui.SlotClicked;
            }

            if (_ui.ChestCloseRequested)
            {
                c.RequestCloseChest(); // E2-5: окно сундука закрыли (Esc, Tab)
            }

            // E2-5 → E2-11: призрак и сетка — пока в руке постройка (или молот над повреждённой постройкой) и мышь не над интерфейсом.
            if (build.Dragging && build.Plan != null)
            {
                _ghost!.ShowPlan(_origin!, _map, _localPos, build.Plan.Spots); // E2-13: линейка — вся линия / прямоугольник
            }
            else if ((build.Hammer || build.Placing) && build.HasGhost && !_ui.PointerBlocked && _cursorTile.HasValue)
            {
                _ghost!.HidePlan();
                _ghost!.SetWorld(_world);
                _ghost!.Show(_origin!, _map, _localPos, 0, true, _cursorTile.Value, build.Check);
            }
            else
            {
                _ghost?.Hide();
            }
        }

        BuildGhost? _ghost;
        UI.UiCraftState _craftState;
        TileCoord? _cursorTile;
        int _cursorQuad;          // E2-12: ячейка 0,5 × 0,5 под курсором в тайле (бит 0 — восточная, бит 1 — южная половина)
        TileCoord? _buildLast;
        int _buildLastQuad;
        int _rulerCancelFrame = -1; // E2-13: кадр, в котором Esc отменил протягивание (паузу этот Esc не ставит)

        /// <summary>Esc этого кадра занят линейкой: идёт протягивание или его только что отменили.</summary>
        bool RulerTookEscape => (_ui?.Ruler.Dragging ?? false) || _rulerCancelFrame == Time.frameCount || _menuCancelFrame == Time.frameCount || FromMenu;

        /// <summary>Тайл под курсором мыши (нет мыши или мира — null).</summary>
        TileCoord? CursorTile()
        {
            Mouse? mouse = Mouse.current;
            if (mouse == null || _origin == null)
            {
                return null;
            }

            Vector3 screen = mouse.position.ReadValue();
            Vector3 v = _g.Camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -GameBoot.CameraOffset.z));
            WorldPos p = Torus.Wrap(_map, _origin.FromUnity(v));
            TileCoord cell = BuildCells.CellAt(_map, p);
            byte held = HandItem;
            if (BuildHand.IsBuildItem(held, out ushort key) && BuildLayers.IsFurniture(key))
            {
                // E2-15: мебель с шагом 0,5 — центром к ближнему узлу сетки 0,5 под курсором; тайл и четверть — её СЗ ячейка.
                // E2-16: размер — по повороту (кровать 1 × 2, верстак 2 × 2).
                cell = BuildLayers.FurnitureCellAt(_map, p, HandKey(held));
                _cursorQuad = BuildCells.QuadOf(cell);
                return BuildCells.TileOfCell(cell);
            }

            _cursorQuad = BuildCells.QuadOf(cell);
            if (!BuildHand.IsBuildItem(held) && _world != null && BuildLayers.TryTopOfCell(_world, cell, out TileCoord top))
            {
                return top; // E2-15: молот, F — по постройке под курсором; мебель соседа со сдвигом — её тайл-якорь
            }

            return Torus.TileOf(_map, p);
        }

        /// <summary>
        /// Состояние стройки, сундука и F за кадр (E2-5 → E2-11): проверка тайла под курсором — <see cref="BuildHand.Check"/>
        /// (функции ядра, как у сервера) по своему миру, месту, сумке клиента (с предсказанными тратами) и ячейке в руке:
        /// постройка в руке — призрак постановки; молот — починка повреждённой постройки и прочность.
        /// </summary>
        UI.UiBuildState BuildState(NetClient c, LocalPrediction? pr)
        {
            var s = new UI.UiBuildState { ChestOpen = c.ChestOpen, Chest = c.Chest };
            _cursorTile = CursorTile();
            if (_world == null || _ui == null)
            {
                return s;
            }

            s.Use = ObjectUse.Find(_world, _localPos, _cursorTile, out _, out UseKind kind) ? kind : UseKind.None;
            byte held = HandItem;
            if (FromMenu)
            {
                _menuAvailable = MenuHand.Available(_world, _localPos, c.Inv, c.NearbyStock, held); // E2-16: готовые + изготовимые
            }

            Inventory hb = HandBag(c);
            int turn = TurnOf(held);
            bool hammer = BuildHand.IsHammer(held), placing = BuildHand.IsBuildItem(held);
            BuildRuler ruler = _ui.Ruler;
            if (ruler.Dragging && (held != ruler.AnchorItem || BuildRuler.KindOfItem(held) == RulerKind.Single || ruler.Mode != PlaceMode.Ruler))
            {
                ruler.Cancel(); // сменили предмет в руке (другая постройка, дверь, факел, не постройка) — протягивание отменено
            }

            s.Placing = placing;
            s.RulerMode = ruler.Mode == PlaceMode.Ruler;
            if (!(hammer || placing) || !_cursorTile.HasValue)
            {
                return s;
            }

            if (placing && ruler.Dragging)
            {
                Keyboard? kb = Keyboard.current;
                bool diagonal = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
                ruler.Update(_world, _localPos, hb, HeldCell, !(pr?.Dead ?? false), _cursorTile.Value, _cursorQuad, diagonal);
                s.Dragging = true;
                s.Plan = ruler;
            }

            TileCoord t = _cursorTile.Value;
            s.Hammer = hammer;
            s.Placing = placing;
            s.Check = FromMenu
                ? MenuHand.Check(_world, _localPos, c.Inv, HeldCell, held, _menuAvailable, !(pr?.Dead ?? false), t, _cursorQuad, turn)
                : BuildHand.Check(_world, _localPos, hb, HeldCell, !(pr?.Dead ?? false), t, _cursorQuad, turn);
            s.Rotatable = placing && BuildHand.IsBuildItem(held, out ushort hk) && Turnable(hk);
            s.FromMenu = FromMenu;
            s.MenuAvailable = _menuAvailable;
            s.HasGhost = placing || (s.Check.Reply != EditReply.Empty && s.Check.Reply != EditReply.Intact && s.Check.Reply != EditReply.OutOfReach);
            ushort obj = _world.GetObject(t);
            if (obj != ObjectId.Empty && _world.TryGetBuild(t, out BuildRecord rec) && !rec.IsDebris && BuildDefs.TryGet(obj, out BuildDef def))
            {
                s.HasIntegrity = true;
                s.Integrity = rec.Integrity;
                s.MaxIntegrity = def.MaxIntegrity;
            }

            return s;
        }

        /// <summary>
        /// F (E2-5, общее правило — <see cref="ObjectUse.Find"/>): объект под курсором или ближайший с действием. Дверь —
        /// закрыть (1-012), верстак и костёр — окно крафта на их вкладке, сундук — открыть, кровать — точка возрождения;
        /// открытый сундук — закрыть. Ничего рядом — прежнее правило двери (ближайшая открытая в 1 тайле).
        /// </summary>
        void UseObject(NetClient c)
        {
            if (_ui != null && _ui.ChestShown)
            {
                c.RequestCloseChest();
                return;
            }

            if (_world == null || !ObjectUse.Find(_world, _localPos, CursorTile(), out TileCoord t, out UseKind kind))
            {
                CloseNearestDoor(c);
                return;
            }

            switch (kind)
            {
                case UseKind.Door:
                    c.CloseDoor(t);
                    break;
                case UseKind.Workbench:
                    _ui?.OpenCraft(CraftStation.Workbench, _craftState);
                    break;
                case UseKind.Campfire:
                    _ui?.OpenCraft(CraftStation.Campfire, _craftState);
                    break;
                case UseKind.Chest:
                case UseKind.Bed:
                    c.RequestUse(t);
                    break;
            }

            Debug.Log($"[use] F: {kind} на {t.X},{t.Y}");
        }

        /// <summary>
        /// Кучи на земле (E2-1): сервер шлёт «кучи» (27) и «кучи нет» (28) — рисует <see cref="PileView"/>. Подобранная
        /// куча «прыгает» к поднявшему: своему персонажу или чужой сущности, если она на экране.
        /// </summary>
        void HookPiles(NetClient client)
        {
            client.PileSeen = p => _pileView?.Set(p.Id, p.Item, p.Count, p.Position);
            client.PileGone = (m, was) =>
            {
                Transform? taker = null;
                if (m.Reason == PileGoneReason.Picked)
                {
                    taker = m.Taker == _localId ? _g.Character
                        : _ents.TryGetValue(m.Taker, out GameObject? go) && go.activeSelf ? go.transform : null;
                    if (m.Taker == _localId)
                    {
                        PilesPicked++;
                    }

                    if (taker != null && _origin != null)
                    {
                        _anim?.OnPickup(m.Taker == _localId, m.Taker, _origin.ToUnity(was.Position)); // E2-7: наклон подбора
                    }
                }

                _pileView?.Remove(m.Id, taker);
            };
        }

        /// <summary>Куч подобрано своим персонажем (HUD).</summary>
        public int PilesPicked { get; private set; }

        /// <summary>
        /// Кусок HUD про сумку (1-002; E2-1 — по ячейкам): что в руках, итог по материалам (стройка клавишами считает
        /// их по всей сумке) и занятые ячейки коротко: «9:древесина×3» — номер ячейки с 1, первые 8 — быстрая панель.
        /// </summary>
        string BagHud()
        {
            var sb = new StringBuilder("  в руках: ");
            sb.Append(ItemName(HeldItem));
            sb.Append($" [{_slot + 1}/{InputFrame.MaxSlots}]");
            for (int m = 0; m < (int)Material.Count; m++)
            {
                int n = _bag.CountAt((Material)m);
                if (n > 0)
                {
                    sb.Append($"  {MaterialName(m)} {n}");
                }
            }

            sb.Append($"\nсумка {_bag.UsedCells}/{Inventory.Cells}:");
            for (int i = 0; i < Inventory.Cells; i++)
            {
                byte item = _bag.ItemAt(i);
                if (item == Items.None)
                {
                    continue;
                }

                int n = _bag.CountIn(i);
                sb.Append($" {i + 1}:{ItemName(item)}");
                if (n > 1)
                {
                    sb.Append('×').Append(n);
                }
            }

            sb.Append($"  на земле куч {_pileView?.Count ?? 0}");
            return sb.ToString();
        }

        static string ItemName(byte id) => id switch
        {
            Items.None => "пусто",
            Items.StonePickaxe => "кирка",
            Items.StoneAxe => "топор",
            Items.Hammer => "молот",
            Items.WoodenSword => "меч",
            Items.SimpleBow => "лук",
            Items.Torch => "факел",
            Items.CopperPickaxe => "медная кирка",
            Items.Wood => "древесина",
            Items.Stone => "камень",
            Items.Resin => "смола",
            Items.Fiber => "волокно",
            Items.CopperOre => "медь",
            Items.Arrows => "стрелы",
            _ => ItemTable.Get(id).Key, // новые строки таблицы (E2-6) — ключом, пока нет подписи
        };

        static string MaterialName(int m) => m switch
        {
            (int)Material.Wood => "древесина",
            (int)Material.Stone => "камень",
            (int)Material.Resin => "смола",
            (int)Material.Fiber => "волокно",
            (int)Material.Copper => "медь",
            (int)Material.Arrows => "стрелы",
            _ => "факелы",
        };

        Sprite? _flashSprite;
        string _lastRefusal = string.Empty;
        long _lastRefusalUntilUs;
        bool _smokeEditDone;

        // 1-022, шаг 4: стена на свободный тайл в net-smoke — предсказание видно в Tilemap в тот же кадр, сервер принимает.
        TileCoord _smokeWallTile;
        int _smokeWallTry;
        int _smokeWallFrameOk;
        int _smokeWallRollbacks;
        long _smokeWallConfirmed0;
        bool _smokeWallConfirmed;
        bool _smokeWallDone;
        long _smokeWallNextUs;

        /// <summary>Откатов показано (HUD, smoke).</summary>
        public int Rollbacks { get; private set; }

        /// <summary>
        /// До отправки кадров: курсор и кнопки мыши → запросы правок; возвращает биты кнопок для кадров ввода
        /// (<see cref="InputButtons.Dig"/>, пока копание идёт).
        /// </summary>
        ushort UpdateEdits(long now)
        {
            NetClient c = _client!;
            if (_args.NetSmokeSeconds > 0)
            {
                SmokeEdit(now, c);
                // 1-004: в net-smoke удар — скриптом (окно удержания ЛКМ в беге), бит как на настоящей мыши.
                return (ushort)((c.Digging ? InputButtons.Dig : (ushort)0) | (ScriptedSmokeAttack(now) ? InputButtons.Attack : (ushort)0));
            }

            Mouse? mouse = Mouse.current;
            if (mouse == null)
            {
                return 0;
            }

            Vector3 screen = mouse.position.ReadValue();
            Vector3 v = _g.Camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -GameBoot.CameraOffset.z));
            TileCoord tile = Torus.TileOf(_map, _origin!.FromUnity(v));

            // 1-012: F — закрыть ближайшую открытую дверь (передний фронт здесь, запрос TileOp.CloseDoor — протокол 19;
            // радиус, проём и замок проверяет сервер, предсказание и откат — общие с правками).
            if (Keyboard.current?.fKey.wasPressedThisFrame == true)
            {
                UseObject(c); // E2-5: общее правило F (дверь, станки, сундук, кровать)
            }

            if (_ui != null && _ui.PointerBlocked)
            {
                // E2-2: мышь занята интерфейсом (курсор над окном или панелью, предмет «в руке») — мир на неё не отвечает.
                c.StopDig();
                if (_ui.Ruler.Dragging && (!mouse.leftButton.isPressed || Keyboard.current?.escapeKey.wasPressedThisFrame == true))
                {
                    _ui.Ruler.Cancel(); // E2-13: отпустил или Esc над интерфейсом — линейка не ставит
                    _rulerCancelFrame = Time.frameCount;
                }

                return c.Digging ? InputButtons.Dig : (ushort)0;
            }

            TurnAndMenuKeys(HandItem); // E2-16: R — поворот, Tab — линейка, Esc / ПКМ — убрать постройку из меню
            byte held = HandItem;      // E2-16: постройка из меню — как в руке
            if ((BuildHand.IsHammer(held) || BuildHand.IsBuildItem(held)) && _ui != null && _world != null)
            {
                return HammerEdits(c, mouse, tile, held, now);
            }

            // 1-004 (`02` 1.2 «удержание ЛКМ — повтор»): с ближним оружием в руках ЛКМ — удар,
            // бит стоит на всех кадрах удержания (ядро берёт нажатие битом кадра).
            ushort attack = Items.KindOf(held) == Items.KindMelee && mouse.leftButton.isPressed ? InputButtons.Attack : (ushort)0;
            if (!mouse.leftButton.isPressed)
            {
                c.StopDig(); // отпустил до конца — «копать: конец»; докопанное своим счётом ждёт ответа
                return (ushort)((c.Digging ? InputButtons.Dig : (ushort)0) | attack);
            }

            // 1-011: «что копать и чем» решает ядро — CheckDig (кирка/топор/молот, ярус, ямы, построенные полы и
            // обломки молотом). Не подходит — ЛКМ не начинает копание и запрос не уходит (у сервера тот же отказ молча).
            if (!c.Digging && EditRules.CheckDig(_world!, _localPos, tile, c.DigTimes, held) == EditReply.Accepted)
            {
                c.StartDig(tile);
            }

            return (ushort)((c.Digging ? InputButtons.Dig : (ushort)0) | attack);
        }

        /// <summary>
        /// Постройка или молот в руке (E2-11, «взять стену из инвентаря и нажать, куда»). Постройка: ЛКМ — поставить её, снимая
        /// один предмет из ячейки (<see cref="BuildHand.Click"/>; зажатая ЛКМ ставит подряд на каждом новом тайле под курсором).
        /// Молот: ЛКМ по повреждённой постройке — починить, по целой — снести; ПКМ — снести (копание молотом, досягаемость
        /// копания; возврат — сам предмет-постройка кучей). Нельзя — причина строкой HUD (у курсора её пишет интерфейс).
        /// </summary>
        ushort HammerEdits(NetClient c, Mouse mouse, TileCoord tile, byte held, long now)
        {
            bool alive = !(_pred?.Dead ?? false);
            bool hammer = BuildHand.IsHammer(held);
            if (!hammer && _ui!.Ruler.Mode == PlaceMode.Ruler && BuildRuler.KindOfItem(held) != RulerKind.Single)
            {
                RulerEdits(c, mouse, tile, alive, now);
                c.StopDig();
                return c.Digging ? InputButtons.Dig : (ushort)0;
            }

            bool demolishLeft = false;
            if (hammer && mouse.leftButton.isPressed)
            {
                BuildCheck rc = BuildHand.Check(_world!, _localPos, c.Inv, HeldCell, alive, tile);
                demolishLeft = rc.Reply == EditReply.Intact; // целая постройка: ЛКМ молотом — снос
            }

            if (FromMenu && _menuAvailable <= 0)
            {
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    _lastRefusal = EditRules.Describe(EditReply.NoResources); // E2-16: ни готовых, ни материалов — заявку не шлём
                    _lastRefusalUntilUs = now + FlashUs;
                }

                c.StopDig();
                return c.Digging ? InputButtons.Dig : (ushort)0;
            }

            // E2-12: зажатая ЛКМ ставит на каждой новой ячейке 0,5 × 0,5 под курсором (стена — цепочкой ячеек).
            if (!demolishLeft && (mouse.leftButton.wasPressedThisFrame || (mouse.leftButton.isPressed && (_buildLast != tile || _buildLastQuad != _cursorQuad))))
            {
                BuildCheck chk = HandBuild.Click(_world!, _localPos, HandBag(c), HeldCell, alive, tile, _cursorQuad, TurnOf(held));
                _buildLast = tile;
                _buildLastQuad = _cursorQuad;
                if (!chk.Ok && mouse.leftButton.wasPressedThisFrame)
                {
                    _lastRefusal = chk.Reason;
                    _lastRefusalUntilUs = now + FlashUs;
                }
            }

            if (!mouse.leftButton.isPressed)
            {
                _buildLast = null;
            }

            if (hammer && (mouse.rightButton.isPressed || demolishLeft))
            {
                if (!c.Digging && EditRules.CheckDig(_world!, _localPos, tile, c.DigTimes, held) == EditReply.Accepted)
                {
                    c.StartDig(tile);
                }
            }
            else
            {
                c.StopDig();
            }

            return c.Digging ? InputButtons.Dig : (ushort)0;
        }

        /// <summary>
        /// Линейка (E2-13): ЛКМ нажата — начало протягивания в ячейке под курсором; пока зажата — план считает
        /// <see cref="BuildState"/> (призрак всей линии / прямоугольника); отпущена — план уходит пачкой заявок линии
        /// (<see cref="BuildRuler.Release"/>, предсказание как у обычной постановки). ПКМ или Esc во время протягивания — отмена.
        /// </summary>
        void RulerEdits(NetClient c, Mouse mouse, TileCoord tile, bool alive, long now)
        {
            BuildRuler ruler = _ui!.Ruler;
            if (ruler.Dragging && (mouse.rightButton.wasPressedThisFrame || Keyboard.current?.escapeKey.wasPressedThisFrame == true))
            {
                ruler.Cancel();
                _rulerCancelFrame = Time.frameCount;
                return;
            }

            if (!ruler.Dragging && mouse.leftButton.wasPressedThisFrame)
            {
                ruler.Begin(tile, _cursorQuad, HandItem);
            }

            if (ruler.Dragging && !mouse.leftButton.isPressed)
            {
                Keyboard? kb = Keyboard.current;
                bool diagonal = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
                ruler.Update(_world!, _localPos, HandBag(c), HeldCell, alive, tile, _cursorQuad, diagonal);
                string? why = null;
                if (ruler.Need == 0)
                {
                    foreach (RulerSpot sp in ruler.Spots)
                    {
                        why ??= sp.Ok ? null : EditRules.Describe(sp.Reply);
                    }
                }
                else if (ruler.Have == 0)
                {
                    why = EditRules.Describe(EditReply.NoResources);
                }

                int sent = ruler.Release(HandBuild, _localPos, HandBag(c), HeldCell);
                Debug.Log($"[build] линейка: заявок {sent}");
                if (sent == 0 && why != null)
                {
                    _lastRefusal = why;
                    _lastRefusalUntilUs = now + FlashUs;
                }
            }
        }

        /// <summary>
        /// F (1-012): закрыть ближайшую открытую дверь в радиусе 1 тайл от своего персонажа (порядок обхода
        /// восток→юг→запад→север… — как порядок <c>NeighborDeltas</c> в ядре). Свой тайл — отказ: сервер
        /// проверяет сам. Шлёт <see cref="NetClient.CloseDoor"/>: предсказание + запрос (протокол 19).
        /// </summary>
        void CloseNearestDoor(NetClient c)
        {
            WorldModel w = _world!;
            TileCoord own = Torus.TileOf(_map, _localPos);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue; // в проёме F — отказ
                    }

                    TileCoord t = Torus.Wrap(_map, new TileCoord(own.X + dx, own.Y + dy));
                    if (w.GetObject(t) == ObjectId.Door && w.TryGetBuild(t, out BuildRecord rec) && rec.IsDoorOpen)
                    {
                        ushort id = c.CloseDoor(t);
                        Debug.Log($"[door] F: закрываю дверь {t.X},{t.Y}, запрос {id}");
                        return;
                    }
                }
            }
        }

        // ---------- 1-012: host-smoke проход через дверь (--smoke-door → сервер --debug-door) ----------

        TileCoord _smokeDoorTile;
        bool _smokeDoorFound;
        bool _smokeDoorOpenSeen;
        bool _smokeDoorClosedSeen;
        bool _smokeDoorPassed;

        /// <summary>
        /// Net-smoke каждый кадр (1-012): дверь — тайл (cx − 3, cy − 1), 4 тайла к востоку от появления № 0
        /// (сервер ставит её <c>--debug-door</c> закрытой; центр карты — та же середина, что у сервера).
        /// Скриптовый бег на восток: автооткрытие шагом → проход → автозакрытие через 30 тактов.
        /// Фиксируем все три события; проверка — в <c>NetSmokeCheck</c>.
        /// </summary>
        void SmokeDoorFrame(WorldPos confirmed)
        {
            if (!_smokeDoorFound)
            {
                // Формула сервера: появление № 0 — (cx − 7, cy − 1), дверь — (cx − 3, cy − 1). Подтверждённая
                // позиция не годится: первый снимок может прийти, когда бег уже начался.
                _smokeDoorTile = Torus.Wrap(_map, new TileCoord(_map.Width / 2 - 3, _map.Height / 2 - 1));
                _smokeDoorFound = true;
                Debug.Log($"[door] smoke: ожидаю дверь на {_smokeDoorTile.X},{_smokeDoorTile.Y} (4 тайла к востоку от появления)");
            }

            WorldModel w = _world!;
            if (w.GetObject(_smokeDoorTile) != ObjectId.Door || !w.TryGetBuild(_smokeDoorTile, out BuildRecord rec))
            {
                return; // чанк с дверью ещё не пришёл
            }

            if (rec.IsDoorOpen)
            {
                if (!_smokeDoorOpenSeen)
                {
                    _smokeDoorOpenSeen = true;
                    Debug.Log("[door] smoke: дверь открыта — автооткрытие шагом сработало");
                }
            }
            else if (_smokeDoorOpenSeen && !_smokeDoorClosedSeen)
            {
                _smokeDoorClosedSeen = true;
                Debug.Log("[door] smoke: дверь закрылась — автозакрытие через 30 тактов сработало");
            }

            TileCoord me = Torus.TileOf(_map, confirmed);
            Int2 d = Torus.Delta(_map, _smokeDoorTile, me);
            if (d.X >= 1 && d.Y == 0)
            {
                _smokeDoorPassed = true; // персонаж прошёл тайл двери на восток
            }
        }

        /// <summary>
        /// Net-smoke: через 0,3 с — ковёр на свой тайл. Тайл занят существом (`05` 1.3), клиент предсказывает призрак,
        /// сервер отказывает — клиент откатывает со вспышкой; путь отката проверяется строкой «[edit] откат» в логе.
        /// Через 1,0 с — ковёр на свободный тайл (1-022, шаг 4): бесплатный (нет записи в <see cref="BuildDefs"/>),
        /// поэтому сумка стартового набора не нужна; молот в кадр ввода ставит <see cref="SmokeMeleeSlot"/>.
        /// Предсказание видно в Tilemap в тот же кадр, ответ сервера «принято» подтверждает правку;
        /// отказ — повтор на другом тайле, до 3 проб.
        /// </summary>
        void SmokeEdit(long now, NetClient c)
        {
            long sinceUs = now - _inGameSinceUs;
            if (!_smokeEditDone && sinceUs >= 300_000)
            {
                _smokeEditDone = true;
                TileCoord own = Torus.TileOf(_map, _localPos);
                ushort id = c.Place(own, own, ObjectId.Carpet, _localPos, 0); // ковёр — вне таблицы построек, предмета не берёт
                Debug.Log($"[edit] smoke: ковёр на свой тайл {own.X},{own.Y}, запрос {id}");
            }

            if (!_smokeWallDone && _smokeWallTry < 3 && sinceUs >= 1_000_000 && now >= _smokeWallNextUs)
            {
                PlaceSmokeWall(c, now);
            }

            ClientWorld cw = c.World!;
            if (_smokeWallTry > 0 && !_smokeWallConfirmed && cw.PredictionsConfirmed > _smokeWallConfirmed0)
            {
                _smokeWallConfirmed = true;
                _smokeWallDone = true;
                bool inChunk = SmokeCarpetInChunk();
                Debug.Log($"[edit] smoke (1-022): правка принята сервером (проба {_smokeWallTry}), тайл {_smokeWallTile.X},{_smokeWallTile.Y} " +
                          $"в чанке = ковёр: {(inChunk ? "да" : "НЕТ")}");
                if (!inChunk)
                {
                    Debug.LogError($"[edit] FAIL net-smoke (1-022): после «принято» тайл {_smokeWallTile.X},{_smokeWallTile.Y} в чанке не ковёр");
                }
            }
        }

        /// <summary>
        /// 1-022, шаг 4: ковёр на свободный тайл. Предсказание ложится в чанк и в Tilemap синхронно
        /// (<c>ClientWorld.Predict → ChunkChanged → WorldView.ShowChunk</c>) — клетку Tilemap читаем сразу после вызова.
        /// </summary>
        void PlaceSmokeWall(NetClient c, long now)
        {
            _smokeWallTry++;
            if (!TryFindFreePlaceTile(out TileCoord t))
            {
                Debug.LogError($"[edit] FAIL net-smoke (1-022): нет свободного тайла в радиусе {EditRules.PlaceRadiusTiles} (проба {_smokeWallTry})");
                _smokeWallNextUs = now + 700_000;
                return;
            }

            _smokeWallTile = t;
            _smokeWallConfirmed0 = c.World!.PredictionsConfirmed;
            ushort id = c.Place(t, t, ObjectId.Carpet, _localPos, 0);
            bool visible = TilemapShowsCarpet(t);
            Debug.Log($"[edit] smoke (1-022): ковёр на свободный тайл {t.X},{t.Y}, запрос {id}, проба {_smokeWallTry}, " +
                      $"Tilemap в тот же кадр: {(visible ? "да" : "НЕТ")}");
            if (visible)
            {
                _smokeWallFrameOk++;
            }
            else
            {
                Debug.LogError($"[edit] FAIL net-smoke (1-022): предсказание правки {t.X},{t.Y} не видно в Tilemap в тот же кадр");
            }

            _smokeWallNextUs = now + 1_200_000; // ждём ответ сервера; отказ сдвинет срок раньше (OnRolledBack)
        }

        /// <summary>
        /// Свободный тайл для постановки: 6 тайлов по оси максимума от персонажа (<see cref="EditRules.PlaceRadiusTiles"/>),
        /// тайл пуст, пол не вода/лава, проходим, чанк в окне, чужих существ ближе 3 тайлов нет (<c>`05` 1.3</c>).
        /// Обход детерминированный: верхний ряд кольца, далее построчно.
        /// </summary>
        bool TryFindFreePlaceTile(out TileCoord tile)
        {
            TileCoord c0 = Torus.TileOf(_map, _localPos);
            const int r = EditRules.PlaceRadiusTiles;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != r)
                    {
                        continue;
                    }

                    TileCoord t = Torus.Wrap(_map, new TileCoord(c0.X + dx, c0.Y + dy));
                    Chunk? chunk = _world!.FindChunk(Torus.ChunkOf(_map, t));
                    if (chunk == null)
                    {
                        continue;
                    }

                    int index = Chunk.IndexOf(t.X, t.Y);
                    if (chunk.GetObject(index) != ObjectId.Empty)
                    {
                        continue;
                    }

                    byte floor = chunk.GetFloor(index);
                    if (floor == FloorId.Water || floor == FloorId.Lava || _netRules!.IsBlocked(_map, t) || RemoteNear(t, 3))
                    {
                        continue;
                    }

                    tile = t;
                    return true;
                }
            }

            tile = default;
            return false;
        }

        /// <summary>Чужой персонаж в пределах <paramref name="tiles"/> тайлов по оси максимума (по узлам сущностей на экране).</summary>
        bool RemoteNear(TileCoord t, int tiles)
        {
            foreach (KeyValuePair<int, GameObject> kv in _ents)
            {
                if (!kv.Value.activeSelf)
                {
                    continue;
                }

                TileCoord at = Torus.TileOf(_map, _origin!.FromUnity(kv.Value.transform.position));
                Int2 d = Torus.Delta(_map, at, t);
                if (System.Math.Max(System.Math.Abs(d.X), System.Math.Abs(d.Y)) <= tiles)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Клетка Tilemap под тайлом — та же стена, что положило предсказание (чтение в том же кадре).</summary>
        bool TilemapShowsCarpet(TileCoord t)
        {
            ChunkCoord c = Torus.ChunkOf(_map, t);
            if (_view == null || !_view.TryGetNode(c, out WorldView.ChunkNode node))
            {
                return false;
            }

            return node.Objects.GetTile(WorldView.CellOf(t.X & (Torus.ChunkSize - 1), t.Y & (Torus.ChunkSize - 1)))
                == _view.ObjectTile(ObjectId.Carpet);
        }

        /// <summary>Тайл в чанке ядра клиента — ковёр (проверка после ответа «принято»).</summary>
        bool SmokeCarpetInChunk()
        {
            Chunk? chunk = _world!.FindChunk(Torus.ChunkOf(_map, _smokeWallTile));
            return chunk != null &&
                   ObjectId.IdOf(chunk.GetObject(Chunk.IndexOf(_smokeWallTile.X, _smokeWallTile.Y))) == ObjectId.Carpet;
        }

        void OnRolledBack(TileCoord tile, EditReply reason)
        {
            Rollbacks++;
            long now = NowUs();
            _lastRefusal = EditRules.Describe(reason);
            _lastRefusalUntilUs = now + FlashUs;
            Debug.Log($"[edit] откат: тайл {tile.X},{tile.Y} — «{_lastRefusal}»");
            if (_smokeWallTry > 0 && !_smokeWallConfirmed && !_smokeWallDone && tile == _smokeWallTile)
            {
                // 1-022, шаг 4: проба со свободным тайлом не удалась (занято существом и т. п.) — повтор на другом тайле.
                _smokeWallRollbacks++;
                _smokeWallNextUs = now + 700_000;
                Debug.Log($"[edit] smoke (1-022): проба {_smokeWallTry} отклонена — повтор на другом тайле");
            }

            if (_flashSprite == null)
            {
                var tex = new Texture2D(1, 1) { filterMode = FilterMode.Point };
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                _flashSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            }

            var go = new GameObject($"Flash {tile.X},{tile.Y}");
            go.transform.SetParent(_entityRoot.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _flashSprite;
            sr.color = new Color(1f, 0.1f, 0.1f, 0.6f);
            sr.sortingOrder = WorldView.ObjectOrder + 1;
            _flashes.Add(new Flash { Tile = tile, UntilUs = now + FlashUs, Renderer = sr });
        }

        /// <summary>Вспышки: место от якоря (после переезда начала), гаснут к концу 0,6 с, потом убираются.</summary>
        void RenderFlashes(long now)
        {
            for (int i = _flashes.Count - 1; i >= 0; i--)
            {
                Flash f = _flashes[i];
                long left = f.UntilUs - now;
                if (left <= 0)
                {
                    Object.Destroy(f.Renderer.gameObject);
                    _flashes.RemoveAt(i);
                    continue;
                }

                f.Renderer.transform.position = _origin!.ToUnity(CenterOf(f.Tile.X, f.Tile.Y));
                Color col = f.Renderer.color;
                col.a = 0.6f * left / FlashUs;
                f.Renderer.color = col;
            }
        }

        /// <summary>Строка HUD: последняя причина отказа, пока горит вспышка.</summary>
        string EditHud(long now) => now < _lastRefusalUntilUs ? $"  отказ: {_lastRefusal}" : string.Empty;
    }
}
