#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    /// <summary>Ввод интерфейса за кадр: настоящий (<see cref="GameUi.ReadInput"/>) или скриптовый (стенд снимков).</summary>
    public struct UiInput
    {
        /// <summary>Есть курсор; <see cref="Pointer"/> — в координатах холста.</summary>
        public bool HasPointer;
        public Vector2 Pointer;

        /// <summary>ЛКМ нажата в этом кадре / удерживается; ПКМ нажата в этом кадре.</summary>
        public bool LeftDown;
        public bool LeftHeld;
        public bool RightDown;
        public bool Shift;

        /// <summary>Q / I — открыть / закрыть сумку (слово Автора 15.11: было Tab); Esc — закрыть.</summary>
        public bool Toggle;
        public bool Escape;

        /// <summary>C — открыть / закрыть окно «сумка + изготовление» (E2-4, E2-9: то же окно, что Tab).</summary>
        public bool Craft;

        /// <summary>Клавиши окна изготовления: раздел (Home / End: −1 / +1; с Shift — подраздел), рецепт (PgUp / PgDn:
        /// −1 / +1), Enter — сделать (с Shift — 5, с Ctrl — всё), Del — отменить последнюю позицию очереди, Ins — галочка
        /// «Только доступные» (E2-9).</summary>
        public int TabStep;
        public bool Filter;

        /// <summary>Колесо мыши за кадр (вверх — больше нуля): над сеткой рецептов — прокрутка (E2-9).</summary>
        public float Wheel;
        public int RowStep;
        public bool Make;
        public bool Ctrl;
        public bool CancelLast;

        /// <summary>C-008: M — свернуть / развернуть мини-карту.</summary>
        public bool Map;

    }

    /// <summary>
    /// Что окну крафта нужно знать за кадр (E2-4): очередь сервера, идёт ли первая позиция и сколько её тактов прошло
    /// (<c>NetClient.Craft</c>, <c>CraftRunning</c>, <c>CraftProgressAt</c>), свой мир клиента и место персонажа (станок рядом).
    /// </summary>
    public struct UiCraftState
    {
        public CraftQueue Queue;
        public bool Running;
        public int Progress;
        public WorldModel? World;
        public WorldPos Position;

        /// <summary>E2-10: сводка сервера «сундуки рядом» (<c>NetClient.NearbyStock</c>) — «есть» с учётом сундуков; null — только сумка.</summary>
        public ChestStock? Stock;
    }

    /// <summary>
    /// Окно сумки (E2-2 → C-008, как в Terraria): в левом верхнем углу сетка 8 × 4 полупрозрачных ячеек (8…39) без
    /// подложки-окна, сверху мелкая подпись, снизу одна строка клавиш. Быстрая панель (ячейки 0…7) — внизу экрана между
    /// сферами, при открытой сумке она и есть верхний ряд сумки (ЛКМ / ПКМ / Shift / Ctrl по ней — те же заявки), поэтому
    /// здесь не дублируется (решение C-008 по слову Автора о нижней панели).
    /// </summary>
    public sealed class BagWindow : UiGroup
    {
        public const float TitleH = 26f, HintH = 24f, Gap = 4f;
        public static readonly float GridW = UiSlotGrid.WidthOf(Inventory.Slots);
        static readonly int Rows = (Inventory.Cells - Inventory.Slots) / Inventory.Slots;
        public static readonly float GridH = UiSlotGrid.WidthOf(Rows);
        public static readonly float Width = GridW;
        public static readonly float Height = HintH + Gap + GridH + Gap + TitleH;
        static readonly float GridY = HintH + Gap;

        readonly UiSlotGrid _main;
        readonly Text _hint;
        bool _dead;

        public BagWindow(UiRoot root)
            : base(root, "Bag", new Vector2(0f, 1f), new Vector2(20f, -20f - Height), new Vector2(Width, Height))
        {
            _main = new UiSlotGrid(Rt, 0f, GridY, Inventory.Slots, Rows, Inventory.Slots, UiCellLook.Glass);
            UiRoot.Label(Rt, "Title", 2f, Height - TitleH, Width, TitleH, 20, TextAnchor.MiddleLeft, UiStyle.TextLight, outline: true, bold: true)
                .text = UiStrings.BagTitleKeys;
            _hint = UiRoot.Label(Rt, "Hint", 2f, 0f, Width + 120f, HintH, 18, TextAnchor.MiddleLeft, UiStyle.TextLight, outline: true);
            _hint.text = UiStrings.BagHintShort;
            Visible = false;
        }

        /// <summary>Ячейка сумки (8…39) под точкой окна; мимо — −1.</summary>
        public int CellAt(Vector2 local)
        {
            int i = _main.Hit(local);
            return i >= 0 ? _main.First + i : -1;
        }

        /// <summary>C-008: прямоугольник сетки — интерфейс; подписи над и под ней — мир.</summary>
        public void AddZones(UiPointer p, Vector2 canvas) => Zone(p, canvas, 0f, GridY, GridW, GridH);

        /// <summary>Курсор над сеткой (в зазоре тоже) — колесо и Shift в игру не уходят.</summary>
        public bool Covers(Vector2 local) => Visible && _main.Covers(local);

        /// <summary>Середина ячейки 8…39 в координатах окна (скрипт стенда снимков).</summary>
        public Vector2 CellCenter(int cell) => _main.CenterOf(cell - Inventory.Slots);

        /// <summary>Показать сумку: числа — без того, что «в руке»; ячейка под курсором.</summary>
        public void Show(in Inventory bag, BagHand hand, int hover, bool dead)
        {
            for (int i = Inventory.Slots; i < Inventory.Cells; i++)
            {
                _main.Set(i - Inventory.Slots, bag.ItemAt(i), hand.ShownCount(i));
            }

            _main.Hover(hover >= Inventory.Slots && hover < Inventory.Cells ? hover - Inventory.Slots : -1);
            if (dead != _dead)
            {
                _dead = dead;
                _hint.text = dead ? UiStrings.BagDead : UiStrings.BagHintShort;
            }
        }
    }

    /// <summary>
    /// Интерфейс игры (E2-2, E2-4 → C-008): нижняя панель со сферами здоровья и выносливости и быстрой панелью, сумка в
    /// левом верхнем углу, сундук под ней, изготовление — столбиком в левом нижнем углу; подсказки, предмет «в руке».
    /// Окна не закрывают мир: интерфейс — только нарисованные части (<see cref="UiPointer"/>), щелчок мимо них идёт в
    /// мир тем же путём, что при закрытых окнах. Правил игры здесь нет: окна показывают сумку и очередь сервера и шлют
    /// заявки через <see cref="BagHand"/> и <see cref="CraftDesk"/>. Ввод — одним местом, <see cref="Frame"/>.
    /// </summary>
    public sealed partial class GameUi
    {
        readonly UiRoot _root;
        readonly BottomBar _bar;
        readonly BagWindow _bag;
        readonly MiniMapView _map;
        readonly CraftWindow _craft;
        readonly UiTooltip _tip;
        readonly UiCarry _carry;
        readonly BagHand _hand;
        readonly CraftDesk _desk;
        readonly UiPointer _pointer = new UiPointer();
        readonly CraftLine[] _lines = new CraftLine[RecipeTable.Count];
        readonly UiGroup _stationHint;
        readonly Text _stationHintText;
        bool _capture;
        CraftStation _near;
        int _nearAge;
        string? _message;
        float _messageUntil;

        /// <summary>Сколько секунд видна строка отказа сервера.</summary>
        public const float MessageSeconds = 2.5f;

        /// <summary>Верх нижней панели (над ним — подсказки F / станка, сообщения, подпись линейки).</summary>
        public static readonly float BarTop = BottomBar.Bottom + BottomBar.Height;

        public GameUi(Camera camera, IBagRequests requests, ICraftRequests craft, IBuildRequests build)
        {
            _root = new UiRoot(camera);
            _hand = new BagHand(requests);
            _desk = new CraftDesk(craft);

            _bar = new BottomBar(_root); // C-008: сферы и быстрая панель внизу по центру

            // Подсказка «рядом станок» / «F — сундук» — над правым краем панели (E2-4).
            const float hintW = 190f, hintH = 34f;
            _stationHint = new UiGroup(_root, "StationHint", new Vector2(0.5f, 0f), new Vector2(BottomBar.PanelW / 2f - hintW, BarTop + 6f), new Vector2(hintW, hintH));
            UiRoot.Picture(_stationHint.Rt, "Back", 0f, 0f, hintW, hintH, UiStyle.Frame(UiStyle.GlassPanel, UiStyle.Gold, thick: 2f, radius: 6f));
            _stationHintText = UiRoot.Label(_stationHint.Rt, "Text", 0f, 0f, hintW, hintH, 20, TextAnchor.MiddleCenter, UiStyle.TextLight, bold: true);
            _stationHint.Visible = false;

            _bag = new BagWindow(_root);
            _map = new MiniMapView(_root); // C-008 п. 8: мини-карта справа вверху
            _craft = new CraftWindow(_root);
            CreateBuild(_root, build, BarTop); // E2-5: окно сундука, строка у курсора, сообщение, подпись линейки
            _tip = new UiTooltip(_root);
            _carry = new UiCarry(_root);
            Layout();
        }

        /// <summary>Окно сумки открыто.</summary>
        public bool BagOpen => _bag.Visible;

        /// <summary>C-008: свежий снимок сервера для точек игроков и врагов на мини-карте (ставит NetGame перед <see cref="Frame"/>).</summary>
        public Orpheus.Net.Protocol.Snapshot? MapSnapshot { get; set; }

        /// <summary>Свой номер сущности (на мини-карте — не точкой, а золотым центром).</summary>
        public int MapSelf { get; set; } = -1;

        /// <summary>Мини-карта (C-008).</summary>
        public MiniMapView Map => _map;

        /// <summary>Окно крафта открыто.</summary>
        public bool CraftOpen => _craft.Visible;

        /// <summary>«Рука» окна (заявки).</summary>
        public BagHand Hand => _hand;

        /// <summary>«Стол» окна крафта (заявки).</summary>
        public CraftDesk Desk => _desk;

        /// <summary>
        /// Мышь занята интерфейсом (<see cref="UiPointer.Blocks"/>): курсор над нарисованной частью интерфейса, предмет «в
        /// руке курсора», либо ЛКМ ещё не отпущена после щелчка по интерфейсу. Тогда мир на мышь не отвечает. C-008: с
        /// открытыми окнами курсор вне их ячеек и кнопок — мир (копать, бить, ставить, линейка).
        /// </summary>
        public bool PointerBlocked { get; private set; }

        /// <summary>Куда пошёл щелчок в этом кадре (C-008).</summary>
        public PointerRoute Route { get; private set; }

        /// <summary>
        /// Курсор над частью открытого окна (сумка, сундук, изготовление; E2-4): колесо мыши не меняет обзор, Shift не
        /// уходит в игру уворотом (Shift+ЛКМ в окне — «половину», Ctrl+ЛКМ — «перекинуть»).
        /// </summary>
        public bool OverWindow { get; private set; }

        /// <summary>Esc в этом кадре закрыл окно (паузу этим нажатием не ставить).</summary>
        public bool EscapeUsed { get; private set; }

        /// <summary>Ячейка быстрой панели, по которой щёлкнули при закрытом окне (выбрать её); нет — −1.</summary>
        public int SlotClicked { get; private set; }

        /// <summary>Станок, у которого стоит персонаж (подсказка у панели); нет — <see cref="CraftStation.None"/>.</summary>
        public CraftStation NearStation => _near;

        /// <summary>Середина ячейки на холсте: 0…7 — на нижней панели, 8…39 — в сумке (скрипт стенда снимков).</summary>
        public Vector2 CellCenter(int cell) => cell < Inventory.Slots
            ? _bar.Origin(_root.Size) + _bar.CellCenter(cell)
            : _bag.Origin(_root.Size) + _bag.CellCenter(cell);

        /// <summary>Середина части окна крафта на холсте (скрипт стенда снимков).</summary>
        public Vector2 CraftPoint(CraftPart part, int index) => _craft.Origin(_root.Size) + _craft.CenterOf(part, index);

        /// <summary>Ввод игрока: мышь и клавиатура.</summary>
        public UiInput ReadInput()
        {
            var input = new UiInput();
            Mouse? mouse = Mouse.current;
            if (mouse != null)
            {
                input.HasPointer = true;
                input.Pointer = _root.FromScreen(mouse.position.ReadValue());
                input.LeftDown = mouse.leftButton.wasPressedThisFrame;
                input.LeftHeld = mouse.leftButton.isPressed;
                input.RightDown = mouse.rightButton.wasPressedThisFrame;
                input.Wheel = mouse.scroll.ReadValue().y;
            }

            Keyboard? kb = Keyboard.current;
            if (kb != null)
            {
                input.Shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
                input.Ctrl = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
                input.Toggle = kb.qKey.wasPressedThisFrame || kb.iKey.wasPressedThisFrame; // слово Автора 15.11: инвентарь — Q (было Tab), запасная — I
                input.Escape = kb.escapeKey.wasPressedThisFrame;
                input.Craft = kb.cKey.wasPressedThisFrame;
                input.TabStep = (kb.endKey.wasPressedThisFrame ? 1 : 0) - (kb.homeKey.wasPressedThisFrame ? 1 : 0);
                input.RowStep = (kb.pageDownKey.wasPressedThisFrame ? 1 : 0) - (kb.pageUpKey.wasPressedThisFrame ? 1 : 0);
                input.Make = kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
                input.CancelLast = kb.deleteKey.wasPressedThisFrame;
                input.Filter = kb.insertKey.wasPressedThisFrame;
                input.Map = kb.mKey.wasPressedThisFrame;
            }

            return input;
        }

        /// <summary>Ответ сервера на заявку изготовления (сообщение 31): отказ — строкой под столбиком на 2,5 с.</summary>
        public void CraftReplied(CraftReplyMessage m)
        {
            if (m.Reply != CraftReply.Accepted)
            {
                CraftStation station = m.Op == CraftOp.Make ? RecipeTable.Get(m.Arg).Station : CraftStation.None;
                Say(UiStrings.CraftRefusal(m.Reply, station));
            }
        }

        void Say(string text)
        {
            _message = text;
            _messageUntil = Time.unscaledTime + MessageSeconds;
        }

        /// <summary>
        /// Кадр интерфейса: сумка сервера, выбранная ячейка панели, жив ли, здоровье и выносливость (0…max), состояние
        /// крафта (очередь сервера, мир и место — для «станок рядом»). Звать до чтения мыши миром (<see cref="PointerBlocked"/>).
        /// </summary>
        public void Frame(in UiInput input, in Inventory bag, int selected, bool dead, int health, int maxHealth, int stamina, int maxStamina,
            in UiCraftState craft, in UiBuildState build = default)
        {
            _root.FitToScreen();
            _hand.Sync(bag, dead);
            _hand.SyncChest(build.ChestOpen, build.Chest); // E2-5: ячейки 40…69 — открытый сундук
            SyncChestWindow(build, craft);
            EscapeUsed = false;
            SlotClicked = -1;
            if (!input.LeftHeld)
            {
                _capture = false;
            }

            // E2-9: Q и C открывают сумку с изготовлением; закрывают их же. При открытом сундуке (сумка + сундук) Q
            // закрывает всё, C — сундук закрывает и открывает сумку с изготовлением.
            if (input.Toggle || input.Craft)
            {
                bool open = input.Craft ? !CraftOpen : !(BagOpen || CraftOpen);
                CloseChestByUser();
                SetOpen(open);
                SetCraftOpen(open, craft);
            }
            else if (input.Escape && (BagOpen || CraftOpen || _chest.Visible))
            {
                CloseChestByUser();
                SetOpen(false);
                SetCraftOpen(false, craft);
                EscapeUsed = true;
            }

            Layout();
            FindStation(craft, force: false);

            if (CraftOpen)
            {
                // Строки рецептов — тем же расчётом, что у бота (CraftDesk.LineOf): решает всё равно сервер.
                for (int r = 1; r < _lines.Length; r++)
                {
                    _lines[r] = craft.World != null
                        ? CraftDesk.LineOf(r, bag, craft.Queue, craft.World, craft.Position, !dead, craft.Stock)
                        : new CraftLine((byte)r, CraftReply.NoStation, 0);
                }

                _craft.Refresh(_lines); // столбик подраздела (CraftShelf.Shown) — до клавиш, щелчков и прямоугольников
            }

            // C-008: «интерфейс, не мир» — только нарисованные части (ячейки, кнопки, значки), набранные за этот кадр.
            Vector2 size = _root.Size, p = input.Pointer;
            _pointer.Clear();
            _bar.AddZones(_pointer, size);
            _bag.AddZones(_pointer, size);
            _chest.AddZones(_pointer, size);
            _craft.AddZones(_pointer, size);
            _map.AddZones(_pointer, size);
            if (input.Map)
            {
                _map.Toggle();
            }
            bool overUi = input.HasPointer && _pointer.Over(p.x, p.y);

            int cell = -1, chestButton = -1;
            CraftHit hit = default;
            BottomBar.Part barPart = BottomBar.Part.None;
            bool overGrid = false;
            OverWindow = false;
            if (input.HasPointer)
            {
                Vector2 barLocal = _bar.Local(size, p);
                cell = _bar.CellAt(barLocal);
                if (cell < 0)
                {
                    barPart = overUi ? _bar.PartAt(barLocal) : BottomBar.Part.None;
                }

                if (cell < 0 && _bag.Covers(_bag.Local(size, p)))
                {
                    OverWindow = true;
                    cell = _bag.CellAt(_bag.Local(size, p));
                }
                else if (cell < 0 && _chest.Covers(_chest.Local(size, p)))
                {
                    OverWindow = true;
                    Vector2 local = _chest.Local(size, p);
                    cell = _chest.CellAt(local); // E2-5: 40…69
                    chestButton = _chest.ButtonAt(local);
                }
                else if (cell < 0 && CraftOpen)
                {
                    Vector2 local = _craft.Local(size, p);
                    hit = _craft.HitTest(local);
                    overGrid = _craft.OverGrid(local);
                    OverWindow = overUi && (hit.Part != CraftPart.None || overGrid);
                }
            }

            if (input.HasPointer && _map.Covers(size, p))
            {
                OverWindow = true; // колесо над картой — масштаб карты, не обзор
                if (Mathf.Abs(input.Wheel) > 0.01f)
                {
                    _map.Zoom(input.Wheel);
                }
            }

            if (CraftOpen)
            {
                CraftKeys(input, craft, dead);
                if (overGrid && Mathf.Abs(input.Wheel) > 0.01f)
                {
                    _craft.Scroll(input.Wheel > 0f ? -1 : 1);
                }
            }

            bool held = _hand.Holding;
            Route = UiPointer.Route(overUi, held, _capture);
            if (input.LeftDown)
            {
                if (cell >= 0 && (BagOpen || cell >= BagHand.ChestBase))
                {
                    if (input.Ctrl && !held)
                    {
                        _hand.Quick(cell); // Ctrl+ЛКМ — перекинуть (слово Автора 15.11)
                    }
                    else if (input.Shift && !held)
                    {
                        _hand.Half(cell); // Shift+ЛКМ — взять половину (слово Автора 15.11)
                    }
                    else
                    {
                        _hand.Left(cell);
                    }
                }
                else if (cell >= 0)
                {
                    SlotClicked = cell; // сумка закрыта: щелчок по панели выбирает ячейку
                }
                else if (chestButton >= 0 && !held)
                {
                    if (chestButton == 0)
                    {
                        _hand.TakeAll(); // C-008: «Взять всё» — пачка обычных заявок переноса
                    }
                    else
                    {
                        _hand.PutAll();
                    }
                }
                else if (hit.Part != CraftPart.None && !held)
                {
                    CraftClick(hit, input, craft, dead);
                }
                else if (Route == PointerRoute.Drop)
                {
                    _hand.Outside(); // мимо интерфейса с предметом «в руке курсора» — выбросить (допущение C-008, как в Terraria)
                }

                if (overUi || held)
                {
                    _capture = true;
                }

                Route = UiPointer.Route(overUi, held, _capture);
            }

            if (input.RightDown && (BagOpen || _chest.Visible))
            {
                if (cell >= 0)
                {
                    _hand.Right(cell);
                }
                else if (held)
                {
                    _hand.Cancel();
                }
            }

            PointerBlocked = UiPointer.Blocks(Route);

            // Показ.
            for (int i = 0; i < Inventory.Slots; i++)
            {
                _bar.Grid.Set(i, bag.ItemAt(i), _hand.ShownCount(i));
            }

            _bar.Grid.Select(selected);
            _bar.Grid.Hover(cell >= 0 && cell < Inventory.Slots ? cell : -1);
            if (BagOpen)
            {
                _bag.Show(bag, _hand, cell, dead);
            }

            if (_chest.Visible)
            {
                _chest.Show(_hand, cell, chestButton);
            }

            if (_message != null && Time.unscaledTime > _messageUntil)
            {
                _message = null;
            }

            if (CraftOpen)
            {
                _craft.Stock = craft.Stock; // E2-10: «есть» — сумка + сундуки рядом
                _craft.Show(_lines, bag, craft.Queue, craft.Running, craft.Progress, dead, hit, _message);
            }

            // Подсказка над панелью: что сделает F (E2-5), иначе «C — верстак».
            string useHint = build.Use != UseKind.None && !_chest.Visible ? UiStrings.UseHint(build.Use) : string.Empty;
            bool hint = !CraftOpen && !_chest.Visible && (useHint.Length > 0 || _near != CraftStation.None);
            _stationHint.Visible = hint;
            if (hint)
            {
                _stationHintText.text = useHint.Length > 0 ? useHint : UiStrings.StationHint(_near);
            }

            ShowBuild(input, bag, selected, build, overUi, size);

            byte under = cell >= 0 && !_hand.Holding ? _hand.ItemAt(cell) : Items.None;
            string? tip = under != Items.None ? UiStrings.ItemTip(under, _hand.CountIn(cell)) : null;
            if (hit.Part == CraftPart.Queue && hit.Index < craft.Queue.Count)
            {
                tip = UiStrings.QueueTip(craft.Queue.RecipeAt(hit.Index), craft.Queue.LeftAt(hit.Index));
            }
            else if (hit.Part == CraftPart.Cell && CraftOpen)
            {
                tip = RecipeTip(_craft.RecipeInCell(hit.Index), bag, craft.Stock);
            }
            else if (hit.Part == CraftPart.Button && CraftOpen)
            {
                tip = RecipeTip(_craft.Recipe, bag, craft.Stock) + "\n" + UiStrings.CraftMakeTip;
            }
            else if (hit.Part != CraftPart.None)
            {
                tip = _craft.TipFor(hit);
            }
            else if (chestButton >= 0)
            {
                tip = chestButton == 0 ? UiStrings.ChestTakeAllTip : UiStrings.ChestPutAllTip;
            }
            else if (barPart != BottomBar.Part.None)
            {
                tip = barPart switch
                {
                    BottomBar.Part.Health => UiStrings.HealthTip(health, maxHealth),
                    BottomBar.Part.Stamina => UiStrings.StaminaTip(stamina, maxStamina),
                    BottomBar.Part.Ability => UiStrings.AbilityLocked,
                    _ => UiStrings.PassiveLocked,
                };
            }

            _tip.Show(tip, size, p);
            _carry.Show(_hand.Item, _hand.Count, p);
            _bar.Set(health, maxHealth, stamina, maxStamina);
            _map.Frame(craft.World, craft.Position, MapSnapshot, MapSelf);
        }

        // Подсказка значка рецепта: название, «есть / нужно», станок, что мешает.
        string? RecipeTip(int recipe, in Inventory bag, ChestStock? stock)
        {
            return recipe == 0 ? null : UiStrings.RecipeTip(recipe, _lines[recipe], CraftDesk.Have(recipe, 0, bag, stock),
                CraftDesk.Have(recipe, 1, bag, stock), CraftDesk.Have(recipe, 2, bag, stock));
        }

        // Щелчок по окну изготовления: раздел, подраздел, галочка, значок рецепта, полоса прокрутки, крупный значок
        // выбранного («сделать»: 1, Shift — 5, Ctrl — всё), позиция очереди (отмена).
        void CraftClick(CraftHit hit, in UiInput input, in UiCraftState craft, bool dead)
        {
            switch (hit.Part)
            {
                case CraftPart.Section:
                    _craft.SelectSection(hit.Index);
                    _craft.Refresh(_lines);
                    break;
                case CraftPart.Sub:
                    _craft.SelectSub(hit.Index);
                    _craft.Refresh(_lines);
                    break;
                case CraftPart.Filter:
                    _craft.ToggleFilter();
                    _craft.Refresh(_lines);
                    break;
                case CraftPart.Cell:
                    _craft.SelectCell(hit.Index);
                    break;
                case CraftPart.ScrollBar:
                    if (hit.Index >= 0)
                    {
                        _craft.Scroll(hit.Index == 0 ? -CraftWindow.GridRows : CraftWindow.GridRows);
                    }

                    break;
                case CraftPart.Button:
                    Make(input.Ctrl ? 0 : input.Shift ? CraftWindow.ButtonCounts[1] : CraftWindow.ButtonCounts[0], dead);
                    break;
                case CraftPart.Queue:
                    if (!dead)
                    {
                        _desk.Cancel(craft.Queue, hit.Index);
                    }

                    break;
            }
        }

        // Клавиши окна крафта — всё, что можно нажать мышью.
        void CraftKeys(in UiInput input, in UiCraftState craft, bool dead)
        {
            if (input.TabStep != 0)
            {
                if (input.Shift)
                {
                    _craft.SelectSub(_craft.Sub + input.TabStep);
                }
                else
                {
                    _craft.SelectSection(_craft.Section + input.TabStep);
                }

                _craft.Refresh(_lines);
            }

            if (input.Filter)
            {
                _craft.ToggleFilter();
                _craft.Refresh(_lines);
            }

            if (input.RowStep != 0)
            {
                _craft.StepRecipe(input.RowStep);
            }

            if (input.Make)
            {
                Make(input.Ctrl ? 0 : input.Shift ? CraftWindow.ButtonCounts[1] : CraftWindow.ButtonCounts[0], dead);
            }

            if (input.CancelLast && !dead && craft.Queue.Count > 0)
            {
                _desk.Cancel(craft.Queue, craft.Queue.Count - 1);
            }
        }

        // «Сделать × count» выбранный рецепт; count = 0 — всё, на что хватает. Нельзя — причина строкой под столбиком.
        void Make(int count, bool dead)
        {
            CraftLine line = _lines[_craft.Recipe];
            if (line.Recipe == 0)
            {
                return;
            }

            if (!_desk.Make(line, count > 0 ? count : line.Affordable))
            {
                Say(dead ? UiStrings.CraftDead : UiStrings.CraftState(line.State, RecipeTable.Get(line.Recipe).Station, line.Affordable));
            }
        }

        // Станок рядом — для подсказки у панели; пересчёт раз в 10 кадров.
        void FindStation(in UiCraftState craft, bool force)
        {
            if (!force && _nearAge++ % 10 != 0)
            {
                return;
            }

            _near = CraftStation.None;
            if (craft.World == null)
            {
                return;
            }

            if (CraftRules.StationCheck(craft.World, craft.Position, CraftStation.Workbench) == CraftReply.Accepted)
            {
                _near = CraftStation.Workbench;
            }
            else if (CraftRules.StationCheck(craft.World, craft.Position, CraftStation.Campfire) == CraftReply.Accepted)
            {
                _near = CraftStation.Campfire;
            }
        }

        // C-008: сумка — левый верхний угол, сундук — под ней, изготовление — левый нижний угол; места постоянные.
        void Layout()
        {
            _chest.Offset = new Vector2(20f, -20f - BagWindow.Height - 14f - ChestWindow.Height);
        }

        void SetOpen(bool open)
        {
            _bag.Visible = open;
            if (!open)
            {
                _hand.Cancel(); // закрыл окно с предметом в руке — предмет остаётся в своей ячейке
            }
        }

        void SetCraftOpen(bool open, in UiCraftState craft)
        {
            if (open && !CraftOpen)
            {
                FindStation(craft, force: true);
                _message = null; // раздел и подраздел — те, что были открыты в прошлый раз (E2-9)
            }

            _craft.Visible = open;
        }
    }

    /// <summary>
    /// Показ интерфейса без сервера (E2-2, E2-4; сцены стенда снимков, ключ <c>--ui-demo bag|hotbar|craft|craft+bag|craft-hint|inv|inv-weapons|inv-buildings</c>):
    /// сумка с набором предметов, заявки исполняются тут же над этой сумкой и очередью, курсор и щелчки — скриптом.
    /// <c>bag</c> — окно открыто, половина стопки камня «в руке»; <c>hotbar</c> — окно закрыто, подсказка над панелью;
    /// <c>craft-hint</c> — окна закрыты, у панели подсказка «C — верстак». E2-9: <c>inv</c> (и прежние <c>craft</c>,
    /// <c>craft+bag</c>) — окно «сумка + изготовление» у верстака: Инструменты → Кирки, в очереди кирка и слитки, выбрана
    /// медная кирка (слитков не хватает), подсказка над её значком; <c>inv-weapons</c> — Оружие → Мечи с галочкой «Только
    /// доступные»; <c>inv-buildings</c> — Здания → Дерево с рецептами построек (E2-11), выбрана деревянная стена.
    /// </summary>
    public sealed partial class UiDemo : IBagRequests, ICraftRequests, IBuildRequests
    {
        readonly GameUi _ui;
        readonly string _mode;
        readonly WorldModel? _world;
        Inventory _bag = Inventory.StartingKit();
        CraftQueue _queue;
        int _frame;

        public UiDemo(Camera camera, string mode, WorldModel? world = null)
        {
            _ui = new GameUi(camera, this, this, this);
            _mode = mode;
            _world = world;
            _bag.SetCell(5, Items.Torch, 12);
            _bag.SetCell(6, Items.Wood, 40);
            _bag.SetCell(8, Items.Arrows, 250);
            _bag.SetCell(9, Items.Wood, 99);
            _bag.SetCell(10, Items.Stone, 64);
            _bag.SetCell(11, Items.Resin, 5);
            _bag.SetCell(12, Items.Fiber, 18);
            _bag.SetCell(13, Items.CopperOre, 9);
            _bag.SetCell(14, Items.CopperPickaxe, 1);
            _bag.SetCell(21, Items.Stone, 7);
            _bag.SetCell(39, Items.Fiber, 2);
            if (IsCraft(mode))
            {
                _bag.SetCell(15, Items.CopperIngot, 5);
                _bag.SetCell(16, Items.Rope, 2);
                _bag.SetCell(17, Items.Meat, 3);
                _bag.SetCell(18, Items.CopperAxe, 1);
            }
        }

        /// <summary>Сцена с окном крафта (нужен верстак рядом с персонажем).</summary>
        public static bool IsCraft(string? mode) => mode == "craft" || mode == "craft+bag" || mode == "craft-hint" || mode == "craft-chest" || IsInv(mode);

        /// <summary>Сцены окна «сумка + изготовление» (E2-9).</summary>
        public static bool IsInv(string? mode) => mode == "inv" || mode == "inv-weapons" || mode == "inv-buildings" || mode == "inv-stone";

        public static bool IsMode(string? mode) => mode == "bag" || mode == "hotbar" || IsCraft(mode) || IsBuild(mode);

        public bool RequestBagMove(int from, int to) =>
            from >= BagHand.ChestBase || to >= BagHand.ChestBase ? ChestMove(from, to) : _bag.Move(from, to);

        public bool RequestBagSplit(int from, int to, int count) =>
            from >= BagHand.ChestBase || to >= BagHand.ChestBase ? ChestSplit(from, to, count) : _bag.Split(from, to, count);

        public bool RequestBagDrop(int cell, int count = 0) => _bag.TakeFrom(cell, count == 0 ? _bag.CountIn(cell) : count) > 0;

        // Показ без сервера: «сервер» стенда — эта же сумка и очередь (в игре всё это решает Simulation.Craft).
        public bool RequestCraft(int recipe, int count = 1)
        {
            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            int n = System.Math.Min(count, System.Math.Min(CraftRules.Affordable(_bag, def), _queue.RoomFor(def.Id)));
            if (def.Id == 0 || n <= 0)
            {
                return false;
            }

            for (int i = 0; i < def.IngredientCount; i++)
            {
                _bag.Remove(def.IngredientItem(i), def.IngredientCountOf(i) * n);
            }

            _queue.Push(def.Id, n);
            return true;
        }

        public bool RequestCraftCancel(int index)
        {
            if ((uint)index >= (uint)_queue.Count)
            {
                return false;
            }

            ref readonly RecipeDef def = ref RecipeTable.Get(_queue.RecipeAt(index));
            for (int i = 0; i < def.IngredientCount; i++)
            {
                _bag.Add(def.IngredientItem(i), def.IngredientCountOf(i) * _queue.LeftAt(index));
            }

            _queue.RemoveAt(index);
            return true;
        }

        public void Frame(WorldPos position)
        {
            _frame++;
            if (IsBuild(_mode))
            {
                BuildFrame(position); // E2-5: UiDemo.Build.cs
                return;
            }

            var input = new UiInput { HasPointer = true };
            if (_mode == "bag")
            {
                input.Toggle = _frame == 2;
                if (_frame >= 3)
                {
                    // Перенос скриптом: камень 7 → к большой стопке (доукладка); затем ПКМ по стопке камня — половина в руке.
                    input.Pointer = _ui.CellCenter(_frame < 6 ? 21 : _frame < 8 ? 10 : _frame < 12 ? 10 : 19) + (_frame >= 12 ? new Vector2(14f, -6f) : Vector2.zero);
                    input.LeftDown = _frame == 4 || _frame == 7;
                    input.RightDown = _frame == 10;
                }
            }
            else if (IsCraft(_mode))
            {
                CraftScript(ref input);
            }
            else
            {
                input.Pointer = _ui.CellCenter(5) + new Vector2(6f, 10f);
            }

            var craft = new UiCraftState { Queue = _queue, Running = !_queue.IsEmpty, Progress = 9, World = _world, Position = position };
            if (_mode == "craft-chest")
            {
                craft.Stock = DemoStock(); // E2-10: «в сундуке рядом» — 12 слитков и 20 древесины у верстака
            }

            _ui.Frame(input, _bag, selected: 3, dead: false, health: 72, maxHealth: 100, stamina: 45, maxStamina: 100, craft);
        }

        // Скрипт окна «сумка + изготовление» (E2-9; верстак рядом). Шаги — (кадр, часть окна, номер, щелчок).
        // inv: C → Инструменты → Кирки → каменная кирка «Сделать 1» → Материалы → слиток «Сделать всё» → Инструменты
        // (подраздел запомнен) → медная кирка (слитков не хватает) → курсор над её значком (подсказка).
        // C-008: галочка «Только доступные» включена по умолчанию — inv сначала снимает её (видна нехватка красным);
        // крупный значок выбранного (Button): индекс 0 — щелчок, 1 — Shift (5), 2 — Ctrl (всё).
        static readonly (int Frame, CraftPart Part, int Index)[] InvSteps =
        {
            (5, CraftPart.Filter, 0), (8, CraftPart.Section, 2), (11, CraftPart.Sub, 0), (14, CraftPart.Cell, 0), (17, CraftPart.Button, 0),
            (20, CraftPart.Section, 5), (23, CraftPart.Cell, 0), (26, CraftPart.Button, 2),
            (29, CraftPart.Section, 2), (32, CraftPart.Cell, 1), (99, CraftPart.Cell, 1),
        };

        // inv-weapons: Q → Оружие → Мечи (галочка «Только доступные» уже стоит) → деревянный меч, курсор над ним.
        static readonly (int Frame, CraftPart Part, int Index)[] WeaponSteps =
        {
            (5, CraftPart.Section, 3), (8, CraftPart.Sub, 0), (11, CraftPart.Cell, 0), (99, CraftPart.Cell, 0),
        };

        // inv-buildings: Q → снять галочку → Здания → Дерево → деревянная стена (E2-11: постройки — предметы с рецептами).
        static readonly (int Frame, CraftPart Part, int Index)[] BuildingSteps =
        {
            (5, CraftPart.Filter, 0), (8, CraftPart.Section, 0), (11, CraftPart.Sub, 0), (14, CraftPart.Cell, 2), (99, CraftPart.Cell, 2),
        };

        // inv-stone (C-008): Здания → Камень — стены и полы из камня рядом (различимы ли по силуэту).
        static readonly (int Frame, CraftPart Part, int Index)[] StoneSteps =
        {
            (5, CraftPart.Filter, 0), (8, CraftPart.Section, 0), (11, CraftPart.Sub, 1), (14, CraftPart.Cell, 0), (99, CraftPart.Cell, 0),
        };

        // craft-chest (E2-10): C → Инструменты → Кирки → медная кирка: слитков в сумке 5, ещё 12 в сундуке рядом.
        static readonly (int Frame, CraftPart Part, int Index)[] ChestSteps =
        {
            (5, CraftPart.Section, 2), (8, CraftPart.Sub, 0), (11, CraftPart.Cell, 1), (99, CraftPart.Button, 0),
        };

        static ChestStock DemoStock()
        {
            var stock = new ChestStock();
            stock.Set(CraftStation.Workbench, Items.CopperIngot, 12);
            stock.Set(CraftStation.Workbench, Items.Wood, 20);
            return stock;
        }

        void CraftScript(ref UiInput input)
        {
            int f = _frame;
            if (_mode == "craft-hint")
            {
                input.Pointer = new Vector2(40f, 40f); // окно закрыто: у панели подсказка «C — верстак»
                return;
            }

            // Открыть: inv и прежние craft — клавишей C, остальные — Tab (то же окно).
            bool byC = _mode == "inv" || _mode == "craft" || _mode == "craft+bag" || _mode == "craft-chest";
            input.Craft = byC && f == 2;
            input.Toggle = !byC && f == 2;
            if (f < 4)
            {
                input.Pointer = new Vector2(40f, 40f);
                return;
            }

            var steps = _mode == "inv-weapons" ? WeaponSteps : _mode == "inv-buildings" ? BuildingSteps : _mode == "inv-stone" ? StoneSteps
                : _mode == "craft-chest" ? ChestSteps : InvSteps;
            int k = 0;
            while (k < steps.Length - 1 && f > steps[k].Frame)
            {
                k++;
            }

            var step = steps[k];
            bool last = k == steps.Length - 1;
            input.Pointer = _ui.CraftPoint(step.Part, step.Index) + (last ? new Vector2(10f, -8f) : Vector2.zero);
            input.LeftDown = !last && f == step.Frame;
            input.LeftHeld = input.LeftDown;
            input.Shift = input.LeftDown && step.Part == CraftPart.Button && step.Index == 1;
            input.Ctrl = input.LeftDown && step.Part == CraftPart.Button && step.Index == 2;
        }
    }
}
