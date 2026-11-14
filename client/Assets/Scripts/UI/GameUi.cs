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

        /// <summary>Tab / I — открыть / закрыть сумку; Esc — закрыть.</summary>
        public bool Toggle;
        public bool Escape;

        /// <summary>C — открыть / закрыть окно крафта (E2-4).</summary>
        public bool Craft;

        /// <summary>Клавиши окна крафта: вкладка (Home / End: −1 / +1), рецепт (PgUp / PgDn: −1 / +1), Enter — сделать
        /// (с Shift — 5, с Ctrl — всё), Del — отменить последнюю позицию очереди.</summary>
        public int TabStep;
        public int RowStep;
        public bool Make;
        public bool Ctrl;
        public bool CancelLast;

        /// <summary>Q / E — листать ленту построек (E2-5): −1 / +1.</summary>
        public int BuildStep;
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
    }

    /// <summary>Окно сумки (E2-2): 8 × 5 ячеек, верхняя строка — быстрая панель (ячейки 0…7), ниже — ячейки 8…39.</summary>
    public sealed class BagWindow : UiWindow
    {
        const float HintHeight = 46f;
        const float MainY = Pad + HintHeight + 10f;
        static readonly float MainHeight = UiSlotGrid.WidthOf(4);
        static readonly float TopY = MainY + MainHeight + 12f;
        public static readonly float Width = UiSlotGrid.WidthOf(Inventory.Slots) + 2f * Pad;
        public static readonly float Height = TopY + UiStyle.CellSize + 8f + TitleHeight + Pad;

        readonly UiSlotGrid _top;
        readonly UiSlotGrid _main;
        readonly Text _hint;
        bool _dead;

        public BagWindow(UiRoot root)
            : base(root, "Bag", UiStrings.BagTitle, new Vector2(0.5f, 0.5f), new Vector2(-Width / 2f, -Height / 2f + 40f), new Vector2(Width, Height))
        {
            _top = new UiSlotGrid(Rt, Pad, TopY, Inventory.Slots, 1, 0, UiStyle.CellBar, keyLabels: true);
            _main = new UiSlotGrid(Rt, Pad, MainY, Inventory.Slots, (Inventory.Cells - Inventory.Slots) / Inventory.Slots, Inventory.Slots, UiStyle.Cell);
            _hint = UiRoot.Label(Rt, "Hint", Pad, Pad - 2f, Width - 2f * Pad, HintHeight, 16, TextAnchor.MiddleLeft, UiStyle.TextDark);
            _hint.text = UiStrings.BagHint;
            UiRoot.Label(Rt, "Key", Pad, Height - Pad - TitleHeight, Width - 2f * Pad, TitleHeight, 16, TextAnchor.MiddleRight, UiStyle.TextDark)
                .text = UiStrings.BagKeys;
        }

        /// <summary>Ячейка сумки под точкой окна; мимо — −1.</summary>
        public int CellAt(Vector2 local)
        {
            int i = _top.Hit(local);
            if (i >= 0)
            {
                return i;
            }

            i = _main.Hit(local);
            return i >= 0 ? _main.First + i : -1;
        }

        /// <summary>Середина ячейки в координатах окна (скрипт стенда снимков).</summary>
        public Vector2 CellCenter(int cell)
        {
            float half = UiStyle.CellSize / 2f;
            if (cell < Inventory.Slots)
            {
                return new Vector2(Pad + cell * UiSlotGrid.Step + half, TopY + half);
            }

            int i = cell - Inventory.Slots;
            return new Vector2(Pad + i % Inventory.Slots * UiSlotGrid.Step + half, MainY + (_main.Rows - 1 - i / Inventory.Slots) * UiSlotGrid.Step + half);
        }

        /// <summary>Показать сумку: числа — без того, что «в руке»; выбранная ячейка панели и ячейка под курсором.</summary>
        public void Show(in Inventory bag, BagHand hand, int selected, int hover, bool dead)
        {
            for (int i = 0; i < Inventory.Cells; i++)
            {
                UiSlotGrid g = i < Inventory.Slots ? _top : _main;
                g.Set(i - g.First, bag.ItemAt(i), hand.ShownCount(i));
            }

            _top.Select(selected);
            _top.Hover(hover >= 0 && hover < Inventory.Slots ? hover : -1);
            _main.Hover(hover >= Inventory.Slots ? hover - Inventory.Slots : -1);
            if (dead != _dead)
            {
                _dead = dead;
                _hint.text = dead ? UiStrings.BagDead : UiStrings.BagHint;
            }
        }
    }


    /// <summary>
    /// Интерфейс игры (E2-2, E2-4): быстрая панель внизу по центру, окно сумки (Tab), окно крафта (C), полосы здоровья и
    /// выносливости, подсказки, предмет «в руке». Правил игры здесь нет: окна показывают сумку и очередь сервера и шлют
    /// заявки через <see cref="BagHand"/> и <see cref="CraftDesk"/> (те же классы, что у тестов и ботов). Ввод — одним
    /// местом, <see cref="Frame"/>; попадание курсора считается по прямоугольникам холста (без EventSystem).
    /// </summary>
    public sealed partial class GameUi
    {
        readonly UiRoot _root;
        readonly UiGroup _bar;
        readonly UiSlotGrid _barGrid;
        readonly BagWindow _bag;
        readonly CraftWindow _craft;
        readonly UiBar _health;
        readonly UiBar _stamina;
        readonly Text _healthText;
        readonly UiTooltip _tip;
        readonly UiCarry _carry;
        readonly BagHand _hand;
        readonly CraftDesk _desk;
        readonly CraftLine[] _lines = new CraftLine[RecipeTable.Count];
        readonly UiGroup _stationHint;
        readonly Text _stationHintText;
        bool _capture;
        int _healthShown = -1;
        CraftStation _near;
        CraftStation _nearShown = (CraftStation)255;
        int _nearAge;
        string? _message;
        float _messageUntil;

        const float BarPad = 10f;
        const float WindowGap = 16f;
        const float WindowLift = 40f;

        /// <summary>Сколько секунд видна строка отказа сервера.</summary>
        public const float MessageSeconds = 2.5f;

        public GameUi(Camera camera, IBagRequests requests, ICraftRequests craft, IBuildRequests build)
        {
            _root = new UiRoot(camera);
            _hand = new BagHand(requests);
            _desk = new CraftDesk(craft);

            // Полосы — слева вверху.
            var status = new UiGroup(_root, "Status", new Vector2(0f, 1f), new Vector2(24f, -24f - 60f), new Vector2(280f, 60f));
            _health = new UiBar(status.Rt, 0f, 30f, 280f, 28f, UiStyle.Health);
            _healthText = UiRoot.Label(status.Rt, "HealthText", 0f, 30f, 280f, 28f, 18, TextAnchor.MiddleCenter, UiStyle.TextLight, outline: true, bold: true);
            _stamina = new UiBar(status.Rt, 0f, 6f, 220f, 18f, UiStyle.Stamina);

            // Быстрая панель — внизу по центру.
            float w = UiSlotGrid.WidthOf(Inventory.Slots) + 2f * BarPad, h = UiStyle.CellSize + 2f * BarPad;
            _bar = new UiGroup(_root, "Hotbar", new Vector2(0.5f, 0f), new Vector2(-w / 2f, 16f), new Vector2(w, h));
            UiRoot.Picture(_bar.Rt, "Back", 0f, 0f, w, h, UiStyle.Frame(UiStyle.Parchment, UiStyle.Ink, thick: 4f, radius: 10f));
            _barGrid = new UiSlotGrid(_bar.Rt, BarPad, BarPad, Inventory.Slots, 1, 0, UiStyle.CellBar, keyLabels: true);

            // Подсказка «рядом станок» — над правым краем панели (E2-4).
            const float hintW = 190f, hintH = 34f;
            _stationHint = new UiGroup(_root, "StationHint", new Vector2(0.5f, 0f), new Vector2(w / 2f - hintW, 16f + h + 8f), new Vector2(hintW, hintH));
            UiRoot.Picture(_stationHint.Rt, "Back", 0f, 0f, hintW, hintH, UiStyle.Frame(UiStyle.Ink, UiStyle.Ink, radius: 6f));
            _stationHintText = UiRoot.Label(_stationHint.Rt, "Text", 0f, 0f, hintW, hintH, 20, TextAnchor.MiddleCenter, UiStyle.TextLight, bold: true);
            _stationHint.Visible = false;
            _hintLow = 16f + h + 8f;

            _bag = new BagWindow(_root);
            _craft = new CraftWindow(_root);
            _tip = new UiTooltip(_root);
            CreateBuild(_root, build, 16f + h); // E2-5: лента построек, окно сундука, строка у курсора, сообщение
            _carry = new UiCarry(_root);
        }

        readonly float _hintLow;

        /// <summary>Окно сумки открыто.</summary>
        public bool BagOpen => _bag.Visible;

        /// <summary>Окно крафта открыто.</summary>
        public bool CraftOpen => _craft.Visible;

        /// <summary>«Рука» окна (заявки).</summary>
        public BagHand Hand => _hand;

        /// <summary>«Стол» окна крафта (заявки).</summary>
        public CraftDesk Desk => _desk;

        /// <summary>
        /// Мышь занята интерфейсом: курсор над панелью или окном, предмет «в руке», либо ЛКМ ещё не отпущена после
        /// щелчка по интерфейсу. Тогда мир на мышь не отвечает (удар, копание, постройка).
        /// </summary>
        public bool PointerBlocked { get; private set; }

        /// <summary>
        /// Курсор над открытым окном (сумка или крафт; E2-4): колесо мыши не меняет обзор, Shift не уходит в игру
        /// уворотом (Shift+ЛКМ в окне — «перекинуть»).
        /// </summary>
        public bool OverWindow { get; private set; }

        /// <summary>Esc в этом кадре закрыл окно (паузу этим нажатием не ставить).</summary>
        public bool EscapeUsed { get; private set; }

        /// <summary>Ячейка быстрой панели, по которой щёлкнули при закрытом окне (выбрать её); нет — −1.</summary>
        public int SlotClicked { get; private set; }

        /// <summary>Станок, у которого стоит персонаж (подсказка у панели); нет — <see cref="CraftStation.None"/>.</summary>
        public CraftStation NearStation => _near;

        /// <summary>Середина ячейки на холсте: в окне, если оно открыто, иначе на панели (скрипт стенда снимков).</summary>
        public Vector2 CellCenter(int cell)
        {
            if (BagOpen)
            {
                return _bag.Origin(_root.Size) + _bag.CellCenter(cell);
            }

            float half = UiStyle.CellSize / 2f;
            return _bar.Origin(_root.Size) + new Vector2(BarPad + cell * UiSlotGrid.Step + half, BarPad + half);
        }

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
            }

            Keyboard? kb = Keyboard.current;
            if (kb != null)
            {
                input.Shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
                input.Ctrl = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
                input.Toggle = kb.tabKey.wasPressedThisFrame || kb.iKey.wasPressedThisFrame; // `02` 1.1: Tab, запасная — I
                input.Escape = kb.escapeKey.wasPressedThisFrame;
                input.Craft = kb.cKey.wasPressedThisFrame;
                input.TabStep = (kb.endKey.wasPressedThisFrame ? 1 : 0) - (kb.homeKey.wasPressedThisFrame ? 1 : 0);
                input.RowStep = (kb.pageDownKey.wasPressedThisFrame ? 1 : 0) - (kb.pageUpKey.wasPressedThisFrame ? 1 : 0);
                input.Make = kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
                input.CancelLast = kb.deleteKey.wasPressedThisFrame;
                input.BuildStep = (kb.eKey.wasPressedThisFrame ? 1 : 0) - (kb.qKey.wasPressedThisFrame ? 1 : 0); // E2-5
            }

            return input;
        }

        /// <summary>Ответ сервера на заявку изготовления (сообщение 31): отказ — строкой внизу окна на 2,5 с.</summary>
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

            if (input.Toggle)
            {
                if (BagOpen)
                {
                    CloseChestByUser(); // сумку закрыли — сундук рядом с ней тоже
                }

                SetOpen(!BagOpen);
            }
            else if (input.Craft)
            {
                SetCraftOpen(!CraftOpen, craft);
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

            Vector2 size = _root.Size, p = input.Pointer;
            int cell = -1;
            CraftHit hit = default;
            bool overUi = false;
            OverWindow = false;
            if (input.HasPointer)
            {
                if (_bar.Contains(size, p))
                {
                    overUi = true;
                    cell = _barGrid.Hit(_bar.Local(size, p));
                }
                else if (_bag.Contains(size, p))
                {
                    overUi = OverWindow = true;
                    cell = _bag.CellAt(_bag.Local(size, p));
                }
                else if (_chest.Visible && _chest.Contains(size, p))
                {
                    overUi = OverWindow = true;
                    cell = _chest.CellAt(_chest.Local(size, p)); // E2-5: 40…69
                }
                else if (_craft.Contains(size, p))
                {
                    overUi = OverWindow = true;
                    hit = _craft.HitTest(_craft.Local(size, p));
                }
            }

            if (CraftOpen)
            {
                // Строки рецептов — тем же расчётом, что у бота (CraftDesk.LineOf): решает всё равно сервер.
                for (int r = 1; r < _lines.Length; r++)
                {
                    _lines[r] = craft.World != null
                        ? CraftDesk.LineOf(r, bag, craft.Queue, craft.World, craft.Position, !dead)
                        : new CraftLine((byte)r, CraftReply.NoStation, 0);
                }

                CraftKeys(input, craft, dead);
            }

            bool held = _hand.Holding;
            if (input.LeftDown)
            {
                if (cell >= 0 && BagOpen)
                {
                    if (input.Shift)
                    {
                        _hand.Quick(cell);
                    }
                    else
                    {
                        _hand.Left(cell);
                    }
                }
                else if (cell >= 0)
                {
                    SlotClicked = cell; // окно сумки закрыто: щелчок по панели выбирает ячейку
                }
                else if (hit.Part != CraftPart.None && !held)
                {
                    CraftClick(hit, craft, dead);
                }
                else if (!overUi && held)
                {
                    _hand.Outside(); // мимо окна с предметом в руке — выбросить
                }

                if (overUi || held)
                {
                    _capture = true;
                }
            }

            if (input.RightDown && BagOpen)
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

            PointerBlocked = overUi || held || _capture;

            // Показ.
            for (int i = 0; i < Inventory.Slots; i++)
            {
                _barGrid.Set(i, bag.ItemAt(i), _hand.ShownCount(i));
            }

            _barGrid.Select(selected);
            _barGrid.Hover(cell >= 0 && !BagOpen ? cell : -1);
            if (BagOpen)
            {
                _bag.Show(bag, _hand, selected, cell, dead);
            }

            if (_chest.Visible)
            {
                _chest.Show(_hand, cell);
            }

            if (_message != null && Time.unscaledTime > _messageUntil)
            {
                _message = null;
            }

            if (CraftOpen)
            {
                _craft.Show(_lines, bag, craft.Queue, craft.Running, craft.Progress, dead, hit, _message);
            }

            // Подсказка над панелью: что сделает F (E2-5), иначе «C — верстак»; над лентой построек — выше неё.
            string useHint = build.Use != UseKind.None && !_chest.Visible ? UiStrings.UseHint(build.Use) : string.Empty;
            bool hint = !CraftOpen && !_chest.Visible && (useHint.Length > 0 || _near != CraftStation.None);
            _stationHint.Visible = hint;
            if (hint)
            {
                _stationHintText.text = useHint.Length > 0 ? useHint : UiStrings.StationHint(_near);
                _nearShown = _near;
                Vector2 off = _stationHint.Offset;
                _stationHint.Offset = new Vector2(off.x, _buildBar.Visible ? _hintLow + BuildBar.Height + 4f : _hintLow);
            }

            ShowBuild(input, bag, dead, build, overUi, size);

            byte under = cell >= 0 && !_hand.Holding ? _hand.ItemAt(cell) : Items.None;
            string? tip = under != Items.None ? UiStrings.ItemTip(under, _hand.CountIn(cell)) : null;
            if (hit.Part == CraftPart.Queue && hit.Index < craft.Queue.Count)
            {
                tip = UiStrings.QueueTip(craft.Queue.RecipeAt(hit.Index), craft.Queue.LeftAt(hit.Index));
            }

            _tip.Show(tip, size, p);
            _carry.Show(_hand.Item, _hand.Count, p);

            _health.Set(maxHealth > 0 ? health / (float)maxHealth : 0f);
            _stamina.Set(maxStamina > 0 ? stamina / (float)maxStamina : 0f);
            int key = health * 1000 + maxHealth;
            if (key != _healthShown)
            {
                _healthShown = key;
                _healthText.text = $"{health} / {maxHealth}";
            }
        }

        // Щелчок по окну крафта: вкладка, строка, кнопка «сделать», позиция очереди (отмена).
        void CraftClick(CraftHit hit, in UiCraftState craft, bool dead)
        {
            switch (hit.Part)
            {
                case CraftPart.Tab:
                    _craft.SelectTab(hit.Index);
                    break;
                case CraftPart.Row:
                    _craft.SelectRow(hit.Index);
                    break;
                case CraftPart.Button:
                    Make(hit.Index < CraftWindow.ButtonCounts.Length ? CraftWindow.ButtonCounts[hit.Index] : 0, dead);
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
                _craft.SelectTab(_craft.Tab + input.TabStep);
            }

            if (input.RowStep != 0)
            {
                _craft.SelectRow(_craft.SelectedRow + input.RowStep);
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

        // «Сделать × count» выбранный рецепт; count = 0 — всё, на что хватает. Нельзя — причина строкой внизу окна.
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

        // Станок рядом — для подсказки у панели и вкладки при открытии окна; пересчёт раз в 10 кадров.
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

        // Окна по одному — по центру; оба — рядом (сумка слева, крафт справа), верхние края на одной высоте.
        void Layout()
        {
            if (_chest.Visible)
            {
                // E2-5: сумка слева, сундук справа, верхние края вровень.
                float sum = BagWindow.Width + WindowGap + ChestWindow.Width;
                float bagY = -BagWindow.Height / 2f + WindowLift;
                _bag.Offset = new Vector2(-sum / 2f, bagY);
                _chest.Offset = new Vector2(-sum / 2f + BagWindow.Width + WindowGap, bagY + BagWindow.Height - ChestWindow.Height);
                return;
            }

            bool both = BagOpen && CraftOpen;
            float total = BagWindow.Width + WindowGap + CraftWindow.Width;
            float craftY = -CraftWindow.Height / 2f + WindowLift;
            _craft.Offset = new Vector2(both ? -total / 2f + BagWindow.Width + WindowGap : -CraftWindow.Width / 2f, craftY);
            _bag.Offset = new Vector2(both ? -total / 2f : -BagWindow.Width / 2f,
                both ? craftY + CraftWindow.Height - BagWindow.Height : -BagWindow.Height / 2f + WindowLift);
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
                _craft.SelectTab(CraftWindow.TabOf(_near)); // открылось у станка — сразу его вкладка
                _message = null;
            }

            _craft.Visible = open;
        }
    }

    /// <summary>
    /// Показ интерфейса без сервера (E2-2, E2-4; сцены стенда снимков, ключ <c>--ui-demo bag|hotbar|craft|craft+bag|craft-hint</c>):
    /// сумка с набором предметов, заявки исполняются тут же над этой сумкой и очередью, курсор и щелчки — скриптом.
    /// <c>bag</c> — окно открыто, половина стопки камня «в руке»; <c>hotbar</c> — окно закрыто, подсказка над панелью;
    /// <c>craft</c> — окно крафта у верстака, выбран рецепт с нехваткой, в очереди две позиции; <c>craft+bag</c> — оба окна; <c>craft-hint</c> — окна закрыты, у панели подсказка «C — верстак».
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
        public static bool IsCraft(string? mode) => mode == "craft" || mode == "craft+bag" || mode == "craft-hint";

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
            _ui.Frame(input, _bag, selected: 3, dead: false, health: 72, maxHealth: 100, stamina: 45, maxStamina: 100, craft);
        }

        // Скрипт окна крафта (вкладка «Верстак» открывается сама — верстак рядом): слиток «всё» → стрелы × 2 → выбрана
        // медная кирка (слитков не хватает); курсор — над первой строкой списка.
        void CraftScript(ref UiInput input)
        {
            int f = _frame;
            if (_mode == "craft-hint")
            {
                input.Pointer = new Vector2(40f, 40f); // окно закрыто: у панели подсказка «C — верстак»
                return;
            }

            if (_mode == "craft+bag")
            {
                input.Toggle = f == 2;
                f--;
            }

            input.Craft = f == 2;
            if (f < 4)
            {
                input.Pointer = new Vector2(40f, 40f);
                return;
            }

            (CraftPart Part, int Index) at =
                f < 7 ? (CraftPart.Row, 3)        // медный слиток
                : f < 10 ? (CraftPart.Button, 2)  // «сделать всё»
                : f < 13 ? (CraftPart.Row, 2)     // стрелы
                : f < 18 ? (CraftPart.Button, 0)  // «сделать 1» дважды
                : f < 21 ? (CraftPart.Row, 4)     // медная кирка
                : (CraftPart.Row, 0);     // курсор над строкой, которую можно сделать
            input.Pointer = _ui.CraftPoint(at.Part, at.Index) + (f >= 21 ? new Vector2(120f, -6f) : Vector2.zero);
            input.LeftDown = f == 5 || f == 8 || f == 11 || f == 14 || f == 16 || f == 19;
            input.LeftHeld = input.LeftDown;
        }
    }
}
