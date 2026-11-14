#nullable enable
using Orpheus.Core.Sim;
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
    }

    /// <summary>Окно сумки (E2-2): 8 × 5 ячеек, верхняя строка — быстрая панель (ячейки 0…7), ниже — ячейки 8…39.</summary>
    public sealed class BagWindow : UiWindow
    {
        const float HintHeight = 40f;
        const float MainY = Pad + HintHeight + 10f;
        static readonly float MainHeight = UiSlotGrid.WidthOf(4);
        static readonly float TopY = MainY + MainHeight + 12f;
        static readonly float Width = UiSlotGrid.WidthOf(Inventory.Slots) + 2f * Pad;
        static readonly float Height = TopY + UiStyle.CellSize + 8f + TitleHeight + Pad;

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
    /// Интерфейс игры (E2-2, шаг 2.2): быстрая панель внизу по центру, окно сумки (Tab), полосы здоровья и
    /// выносливости, подсказка, предмет «в руке». Правил игры здесь нет: окно показывает сумку сервера и шлёт заявки
    /// через <see cref="BagHand"/> (тот же класс, что у тестов и ботов). Ввод — одним местом, <see cref="Frame"/>;
    /// попадание курсора считается по прямоугольникам холста (без EventSystem).
    /// </summary>
    public sealed class GameUi
    {
        readonly UiRoot _root;
        readonly UiGroup _bar;
        readonly UiSlotGrid _barGrid;
        readonly BagWindow _bag;
        readonly UiBar _health;
        readonly UiBar _stamina;
        readonly Text _healthText;
        readonly UiTooltip _tip;
        readonly UiCarry _carry;
        readonly BagHand _hand;
        bool _capture;
        int _healthShown = -1;

        const float BarPad = 10f;

        public GameUi(Camera camera, IBagRequests requests)
        {
            _root = new UiRoot(camera);
            _hand = new BagHand(requests);

            // Полосы — слева вверху.
            var status = new UiGroup(_root, "Status", new Vector2(0f, 1f), new Vector2(24f, -24f - 60f), new Vector2(280f, 60f));
            _health = new UiBar(status.Rt, 0f, 30f, 280f, 28f, UiStyle.Health);
            _healthText = UiRoot.Label(status.Rt, "HealthText", 0f, 30f, 280f, 28f, 16, TextAnchor.MiddleCenter, UiStyle.TextLight, outline: true, bold: true);
            _stamina = new UiBar(status.Rt, 0f, 6f, 220f, 18f, UiStyle.Stamina);

            // Быстрая панель — внизу по центру.
            float w = UiSlotGrid.WidthOf(Inventory.Slots) + 2f * BarPad, h = UiStyle.CellSize + 2f * BarPad;
            _bar = new UiGroup(_root, "Hotbar", new Vector2(0.5f, 0f), new Vector2(-w / 2f, 16f), new Vector2(w, h));
            UiRoot.Picture(_bar.Rt, "Back", 0f, 0f, w, h, UiStyle.Frame(UiStyle.Parchment, UiStyle.Ink, thick: 4f, radius: 10f));
            _barGrid = new UiSlotGrid(_bar.Rt, BarPad, BarPad, Inventory.Slots, 1, 0, UiStyle.CellBar, keyLabels: true);

            _bag = new BagWindow(_root);
            _tip = new UiTooltip(_root);
            _carry = new UiCarry(_root);
        }

        /// <summary>Окно сумки открыто.</summary>
        public bool BagOpen => _bag.Visible;

        /// <summary>«Рука» окна (заявки).</summary>
        public BagHand Hand => _hand;

        /// <summary>
        /// Мышь занята интерфейсом: курсор над панелью или окном, предмет «в руке», либо ЛКМ ещё не отпущена после
        /// щелчка по интерфейсу. Тогда мир на мышь не отвечает (удар, копание, постройка).
        /// </summary>
        public bool PointerBlocked { get; private set; }

        /// <summary>Esc в этом кадре закрыл окно (паузу этим нажатием не ставить).</summary>
        public bool EscapeUsed { get; private set; }

        /// <summary>Ячейка быстрой панели, по которой щёлкнули при закрытом окне (выбрать её); нет — −1.</summary>
        public int SlotClicked { get; private set; }

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
                input.Toggle = kb.tabKey.wasPressedThisFrame || kb.iKey.wasPressedThisFrame; // `02` 1.1: Tab, запасная — I
                input.Escape = kb.escapeKey.wasPressedThisFrame;
            }

            return input;
        }

        /// <summary>
        /// Кадр интерфейса: сумка сервера, выбранная ячейка панели, жив ли, здоровье и выносливость (0…max).
        /// Звать до чтения мыши миром (<see cref="PointerBlocked"/>).
        /// </summary>
        public void Frame(in UiInput input, in Inventory bag, int selected, bool dead, int health, int maxHealth, int stamina, int maxStamina)
        {
            _root.FitToScreen();
            _hand.Sync(bag, dead);
            EscapeUsed = false;
            SlotClicked = -1;
            if (!input.LeftHeld)
            {
                _capture = false;
            }

            if (input.Toggle)
            {
                SetOpen(!BagOpen);
            }
            else if (input.Escape && BagOpen)
            {
                SetOpen(false);
                EscapeUsed = true;
            }

            Vector2 size = _root.Size, p = input.Pointer;
            int cell = -1;
            bool overUi = false;
            if (input.HasPointer)
            {
                if (_bar.Contains(size, p))
                {
                    overUi = true;
                    cell = _barGrid.Hit(_bar.Local(size, p));
                }
                else if (_bag.Contains(size, p))
                {
                    overUi = true;
                    cell = _bag.CellAt(_bag.Local(size, p));
                }
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
                    SlotClicked = cell; // окно закрыто: щелчок по панели выбирает ячейку
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

            byte under = cell >= 0 && !_hand.Holding ? bag.ItemAt(cell) : Items.None;
            _tip.Show(under != Items.None ? UiStrings.ItemTip(under, bag.CountIn(cell)) : null, size, p);
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

        void SetOpen(bool open)
        {
            _bag.Visible = open;
            if (!open)
            {
                _hand.Cancel(); // закрыл окно с предметом в руке — предмет остаётся в своей ячейке
            }
        }
    }

    /// <summary>
    /// Показ интерфейса без сервера (E2-2, сцены стенда снимков, ключ <c>--ui-demo bag|hotbar</c>): сумка с набором
    /// предметов, заявки исполняются тут же над этой сумкой, курсор и щелчки — скриптом.
    /// <c>bag</c> — окно открыто, половина стопки камня «в руке»; <c>hotbar</c> — окно закрыто, подсказка над панелью.
    /// </summary>
    public sealed class UiDemo : IBagRequests
    {
        readonly GameUi _ui;
        readonly bool _bagMode;
        Inventory _bag = Inventory.StartingKit();
        int _frame;

        public UiDemo(Camera camera, string mode)
        {
            _ui = new GameUi(camera, this);
            _bagMode = mode == "bag";
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
        }

        public bool RequestBagMove(int from, int to) => _bag.Move(from, to);

        public bool RequestBagSplit(int from, int to, int count) => _bag.Split(from, to, count);

        public bool RequestBagDrop(int cell, int count = 0) => _bag.TakeFrom(cell, count == 0 ? _bag.CountIn(cell) : count) > 0;

        public void Frame()
        {
            _frame++;
            var input = new UiInput { HasPointer = true };
            if (_bagMode)
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
            else
            {
                input.Pointer = _ui.CellCenter(5) + new Vector2(6f, 10f);
            }

            _ui.Frame(input, _bag, selected: 3, dead: false, health: 72, maxHealth: 100, stamina: 45, maxStamina: 100);
        }
    }
}
