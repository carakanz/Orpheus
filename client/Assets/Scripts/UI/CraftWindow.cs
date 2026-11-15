#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Sim;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    /// <summary>Что под курсором в окне изготовления.</summary>
    public enum CraftPart : byte
    {
        None = 0,
        Section,
        Sub,
        Cell,
        Button,
        Queue,
        Filter,
        ScrollBar,
    }

    /// <summary>
    /// Попадание в окно изготовления: часть и её номер (раздел, подраздел, ячейка сетки на экране, кнопка 0…2, позиция
    /// очереди; полоса прокрутки: 0 — выше ползунка, 1 — ниже).
    /// </summary>
    public readonly struct CraftHit
    {
        public readonly CraftPart Part;
        public readonly int Index;

        public CraftHit(CraftPart part, int index)
        {
            Part = part;
            Index = index;
        }

        public bool Is(CraftPart part, int index) => Part == part && Index == index;
    }

    /// <summary>
    /// Окно изготовления (E2-4 → E2-9, <c>BRIEF</c> п. 11e (1): «как в Террарии, но подразделами»). Стоит справа от сумки
    /// (одно окно «сумка + изготовление», Tab или C). Сверху — разделы (Здания, Мебель, Инструменты, Оружие, Броня,
    /// Материалы, Еда), под ними — подразделы раздела; слева — сетка значков рецептов подраздела (доступное ярко,
    /// недоступное приглушено, на значке — сколько получится из того, что есть), с прокруткой; справа — карточка
    /// выбранного (результат, ингредиенты «есть / нужно», время, «Сделать 1 / 5 / всё»), ниже — очередь изготовления.
    /// Галочка «Только доступные» прячет то, что сейчас не сделать.
    /// <para>Только вид: раскладка, фильтр и число на значке — <see cref="CraftShelf"/>, «можно ли» — строки
    /// <see cref="CraftDesk.LineOf"/> (те же функции ядра, что у сервера); заявки шлёт <see cref="GameUi"/> через
    /// <see cref="CraftDesk"/>. Окно ничего не предсказывает.</para>
    /// </summary>
    public sealed class CraftWindow : UiWindow
    {
        public const float Width = 1160f;
        const float HintHeight = 46f;
        const float QueueY = Pad + HintHeight + 10f;
        const float QueueLabelY = QueueY + UiStyle.CellSize + 9f;
        const float BodyY = QueueLabelY + 26f + 8f;
        public const int GridColumns = 8, GridRows = 6;
        static readonly float GridWidth = UiSlotGrid.WidthOf(GridColumns);
        static readonly float GridHeight = UiSlotGrid.WidthOf(GridRows);
        static readonly float BodyTop = BodyY + GridHeight;
        static readonly float ScrollX = Pad + GridWidth + 8f;
        const float ScrollWidth = 12f;
        static readonly float CardX = ScrollX + ScrollWidth + 10f;
        static readonly float CardWidth = Width - CardX - Pad;
        static readonly float SubY = BodyTop + 10f;
        const float SubHeight = 40f, SubWidth = 180f, SubGap = 6f;
        static readonly float TabsY = SubY + SubHeight + 8f;
        const float TabHeight = 46f, TabGap = 6f;
        static readonly float TabWidth = (Width - 2f * Pad - 6f * TabGap) / 7f;
        public static readonly float Height = TabsY + TabHeight + 6f + TitleHeight + Pad;
        const float FilterWidth = 300f, KeyWidth = 170f;
        const float CardPad = 14f;
        const float IngredientHeight = 48f;

        /// <summary>Сколько делает кнопка № 0…1; кнопка 2 — «всё, на что хватает».</summary>
        public static readonly int[] ButtonCounts = { 1, 5 };

        static readonly int MaxSubs = CountMaxSubs();

        sealed class Ingredient
        {
            public RectTransform Rt = null!;
            public UiItemIcon Icon = null!;
            public Text Name = null!;
            public Text Have = null!;
        }

        readonly UiButton[] _tabs = new UiButton[CraftShelf.Sections.Length];
        readonly UiButton[] _subs = new UiButton[MaxSubs];
        readonly UiButton _filter;
        readonly Image _check;
        readonly Text _flat, _empty;
        readonly UiSlotGrid _grid;
        readonly Image _track, _thumb;
        readonly UiItemIcon _result;
        readonly Text _name, _yield, _needs, _state, _queueText, _hint, _message;
        readonly Ingredient[] _ingredients = new Ingredient[RecipeTable.MaxIngredients];
        readonly UiButton[] _buttons = new UiButton[3];
        readonly UiSlotGrid _queue;
        readonly UiBar _progress;

        readonly int[] _subOf = new int[CraftShelf.Sections.Length];      // подраздел, открытый в каждом разделе
        readonly int[][] _selected = new int[CraftShelf.Sections.Length][]; // рецепт, выбранный в каждом подразделе
        readonly List<int> _list = new List<int>();                         // что показано в сетке сейчас
        int _section, _scroll;
        bool _onlyAvailable;
        int _shownRecipe = -1, _shownAll = -1, _shownQueueText = -1;
        string _shownState = string.Empty, _shownMessage = string.Empty, _shownEmpty = "?";

        public CraftWindow(UiRoot root)
            : base(root, "Craft", UiStrings.CraftTitle, new Vector2(0.5f, 0.5f), new Vector2(-Width / 2f, -Height / 2f + 40f), new Vector2(Width, Height))
        {
            float titleY = Height - Pad - TitleHeight;
            UiRoot.Label(Rt, "Key", Width - Pad - KeyWidth, titleY, KeyWidth, TitleHeight, 18, TextAnchor.MiddleRight, UiStyle.TextDark)
                .text = UiStrings.CraftKeyInv;

            // Галочка «Только доступные» — в строке заголовка.
            _filter = new UiButton(Rt, Width - Pad - KeyWidth - 16f - FilterWidth, titleY, FilterWidth, TitleHeight, UiStrings.CraftOnlyAvailable, 20,
                TextAnchor.MiddleLeft, 46f);
            UiRoot.Picture(_filter.Rt, "Box", 12f, 7f, 26f, 26f, UiStyle.Frame(UiStyle.CellHover, UiStyle.Ink));
            _check = UiRoot.Picture(_filter.Rt, "Check", 17f, 12f, 16f, 16f, null, sliced: false);
            _check.color = UiStyle.Ochre;
            _check.enabled = false;

            for (int s = 0; s < _tabs.Length; s++)
            {
                _tabs[s] = new UiButton(Rt, Pad + s * (TabWidth + TabGap), TabsY, TabWidth, TabHeight, UiStrings.SectionName(CraftShelf.Sections[s].Key), 21);
                _selected[s] = new int[CraftShelf.Sections[s].Subs.Length];
            }

            for (int k = 0; k < _subs.Length; k++)
            {
                _subs[k] = new UiButton(Rt, Pad + k * (SubWidth + SubGap), SubY, SubWidth, SubHeight, string.Empty, 20);
            }

            _flat = UiRoot.Label(Rt, "Flat", Pad + 6f, SubY, 500f, SubHeight, 20, TextAnchor.MiddleLeft, UiStyle.TextDim);
            _flat.text = UiStrings.CraftFlat;

            // Сетка рецептов подраздела и полоса прокрутки.
            UiRoot.Picture(Rt, "GridBack", Pad - 6f, BodyY - 6f, GridWidth + 12f, GridHeight + 12f, UiStyle.Frame(UiStyle.Card, UiStyle.InkSoft));
            _grid = new UiSlotGrid(Rt, Pad, BodyY, GridColumns, GridRows, 0, UiStyle.Cell);
            _empty = UiRoot.Label(Rt, "Empty", Pad, BodyY, GridWidth, GridHeight, 22, TextAnchor.MiddleCenter, UiStyle.TextDim, bold: true);
            _track = UiRoot.Picture(Rt, "Track", ScrollX, BodyY, ScrollWidth, GridHeight, UiStyle.Frame(UiStyle.BarBack, UiStyle.Ink, radius: 5f));
            _thumb = UiRoot.Picture(Rt, "Thumb", ScrollX + 2f, BodyY, ScrollWidth - 4f, GridHeight, null, sliced: false);
            _thumb.color = UiStyle.Ochre;

            // Карточка выбранного рецепта.
            UiRoot.Picture(Rt, "Card", CardX, BodyY - 6f, CardWidth, GridHeight + 12f, UiStyle.Frame(UiStyle.Card, UiStyle.InkSoft));
            float x = CardX + CardPad, top = BodyTop - CardPad;
            UiRoot.Picture(Rt, "ResultCell", x, top - UiStyle.CellSize, UiStyle.CellSize, UiStyle.CellSize, UiStyle.Frame(UiStyle.CellBar, UiStyle.Ink));
            _result = new UiItemIcon(Rt, x, top - UiStyle.CellSize, UiStyle.CellSize);
            float textX = x + UiStyle.CellSize + 12f, textW = CardWidth - 2f * CardPad - UiStyle.CellSize - 12f;
            _name = UiRoot.Label(Rt, "Result", textX, top - 34f, textW, 34f, 26, TextAnchor.MiddleLeft, UiStyle.TextDark, bold: true);
            _yield = UiRoot.Label(Rt, "Yield", textX, top - UiStyle.CellSize, textW, 28f, 18, TextAnchor.MiddleLeft, UiStyle.TextDark);
            float needsY = top - UiStyle.CellSize - 10f - 26f;
            _needs = UiRoot.Label(Rt, "Needs", x, needsY, 200f, 26f, 20, TextAnchor.MiddleLeft, UiStyle.TextDark, bold: true);
            _needs.text = UiStrings.CraftNeeds;
            for (int k = 0; k < _ingredients.Length; k++)
            {
                float y = needsY - 4f - (k + 1) * IngredientHeight;
                var ing = new Ingredient { Rt = UiRoot.Node(Rt, "Ingredient", x, y, CardWidth - 2f * CardPad, IngredientHeight) };
                ing.Icon = new UiItemIcon(ing.Rt, 0f, 2f, 44f, 40f);
                ing.Name = UiRoot.Label(ing.Rt, "Name", 54f, 0f, 260f, IngredientHeight, 20, TextAnchor.MiddleLeft, UiStyle.TextDark);
                ing.Have = UiRoot.Label(ing.Rt, "Have", CardWidth - 2f * CardPad - 150f, 0f, 150f, IngredientHeight, 22, TextAnchor.MiddleRight,
                    UiStyle.TextDark, bold: true);
                _ingredients[k] = ing;
            }

            float buttonsY = BodyY + CardPad - 6f, bw = (CardWidth - 2f * CardPad - 2f * 8f) / 3f;
            _state = UiRoot.Label(Rt, "State", x, buttonsY + 46f + 10f, CardWidth - 2f * CardPad, 30f, 20, TextAnchor.MiddleLeft, UiStyle.TextDark, bold: true);
            string[] captions = { UiStrings.CraftMake1, UiStrings.CraftMake5, UiStrings.CraftMakeAll };
            for (int k = 0; k < _buttons.Length; k++)
            {
                _buttons[k] = new UiButton(Rt, x + k * (bw + 8f), buttonsY, bw, 46f, captions[k], 20);
            }

            // Очередь изготовления.
            UiRoot.Label(Rt, "QueueLabel", Pad, QueueLabelY, 600f, 26f, 18, TextAnchor.MiddleLeft, UiStyle.TextDark).text = UiStrings.CraftQueue;
            _queue = new UiSlotGrid(Rt, Pad, QueueY, CraftQueue.MaxEntries, 1, 0, UiStyle.Cell);
            float barX = Pad + UiSlotGrid.WidthOf(CraftQueue.MaxEntries) + 16f, barW = Width - Pad - barX;
            _queueText = UiRoot.Label(Rt, "QueueText", barX, QueueY + 34f, barW, 28f, 20, TextAnchor.MiddleLeft, UiStyle.TextDark, bold: true);
            _progress = new UiBar(Rt, barX, QueueY + 6f, barW, 24f, UiStyle.Ochre);

            _hint = UiRoot.Label(Rt, "Hint", Pad, Pad - 2f, Width - 2f * Pad, HintHeight, 18, TextAnchor.MiddleLeft, UiStyle.TextDark);
            _hint.text = UiStrings.CraftHintInv;
            _message = UiRoot.Label(Rt, "Message", Pad, Pad - 2f, Width - 2f * Pad, HintHeight, 24, TextAnchor.MiddleLeft, UiStyle.TextBad, bold: true);
            _message.enabled = false;
        }

        static int CountMaxSubs()
        {
            int n = 1;
            foreach (ShelfSection s in CraftShelf.Sections)
            {
                n = Mathf.Max(n, s.Subs.Length);
            }

            return n;
        }

        /// <summary>Открытый раздел (номер в <see cref="CraftShelf.Sections"/>).</summary>
        public int Section => _section;

        /// <summary>Открытый подраздел раздела.</summary>
        public int Sub => _subOf[_section];

        /// <summary>Галочка «Только доступные».</summary>
        public bool OnlyAvailable => _onlyAvailable;

        /// <summary>Первая строка сетки на экране (прокрутка).</summary>
        public int ScrollRow => _scroll;

        /// <summary>Рецептов в сетке сейчас (с учётом фильтра).</summary>
        public int Shown => _list.Count;

        /// <summary>Выбранный рецепт (номер <see cref="RecipeTable"/>); в пустой сетке — 0.</summary>
        public int Recipe
        {
            get
            {
                int sel = _selected[_section][Sub];
                return _list.Contains(sel) ? sel : _list.Count > 0 ? _list[0] : 0;
            }
        }

        int RowCount => (_list.Count + GridColumns - 1) / GridColumns;

        int MaxScroll => Mathf.Max(0, RowCount - GridRows);

        /// <summary>
        /// Пересчитать содержимое сетки по строкам рецептов (<paramref name="lines"/> — по номеру рецепта, из
        /// <see cref="CraftDesk.LineOf"/>): тот же <see cref="CraftShelf.Shown"/>, что в тестах. Звать каждый кадр до клавиш и щелчков.
        /// </summary>
        public void Refresh(CraftLine[] lines)
        {
            CraftShelf.Shown(_section, Sub, lines, _onlyAvailable, _list);
            _scroll = Mathf.Clamp(_scroll, 0, MaxScroll);
        }

        public void SelectSection(int section)
        {
            int n = CraftShelf.Sections.Length;
            _section = (section % n + n) % n;
            _scroll = 0;
        }

        public void SelectSub(int sub)
        {
            int n = CraftShelf.Sections[_section].Subs.Length;
            _subOf[_section] = (sub % n + n) % n;
            _scroll = 0;
        }

        /// <summary>Сразу раздел и подраздел (стенд снимков).</summary>
        public void Open(int section, int sub)
        {
            SelectSection(section);
            SelectSub(sub);
        }

        public void ToggleFilter() => _onlyAvailable = !_onlyAvailable;

        /// <summary>Выбрать соседний рецепт сетки (PgUp / PgDn: −1 / +1); выбранный остаётся на экране.</summary>
        public void StepRecipe(int step)
        {
            if (_list.Count == 0)
            {
                return;
            }

            int i = Mathf.Clamp(_list.IndexOf(Recipe) + step, 0, _list.Count - 1);
            Pick(i);
        }

        /// <summary>Щелчок по ячейке сетки на экране (номер ячейки 0…47 с учётом прокрутки).</summary>
        public void SelectCell(int cell)
        {
            int i = _scroll * GridColumns + cell;
            if ((uint)i < (uint)_list.Count)
            {
                Pick(i);
            }
        }

        void Pick(int i)
        {
            _selected[_section][Sub] = _list[i];
            int row = i / GridColumns;
            if (row < _scroll)
            {
                _scroll = row;
            }
            else if (row >= _scroll + GridRows)
            {
                _scroll = row - GridRows + 1;
            }
        }

        /// <summary>Прокрутка сетки на <paramref name="rows"/> строк (колесо: вверх — −1).</summary>
        public void Scroll(int rows) => _scroll = Mathf.Clamp(_scroll + rows, 0, MaxScroll);

        /// <summary>Рецепт в ячейке сетки на экране; пустая ячейка — 0.</summary>
        public int RecipeInCell(int cell)
        {
            int i = _scroll * GridColumns + cell;
            return (uint)i < (uint)_list.Count ? _list[i] : 0;
        }

        /// <summary>Точка в сетке рецептов (колесо прокручивает сетку).</summary>
        public bool OverGrid(Vector2 local) =>
            local.x >= Pad && local.x < ScrollX + ScrollWidth && local.y >= BodyY && local.y < BodyTop;

        /// <summary>Что под точкой окна.</summary>
        public CraftHit HitTest(Vector2 local)
        {
            for (int i = 0; i < _tabs.Length; i++)
            {
                if (_tabs[i].Contains(local))
                {
                    return new CraftHit(CraftPart.Section, i);
                }
            }

            for (int i = 0; i < _subs.Length; i++)
            {
                if (_subs[i].Contains(local))
                {
                    return new CraftHit(CraftPart.Sub, i);
                }
            }

            if (_filter.Contains(local))
            {
                return new CraftHit(CraftPart.Filter, 0);
            }

            int cell = _grid.Hit(local);
            if (cell >= 0)
            {
                return RecipeInCell(cell) > 0 ? new CraftHit(CraftPart.Cell, cell) : default;
            }

            if (MaxScroll > 0 && local.x >= ScrollX && local.x < ScrollX + ScrollWidth && local.y >= BodyY && local.y < BodyTop)
            {
                Rect thumb = ThumbRect();
                return new CraftHit(CraftPart.ScrollBar, local.y >= thumb.yMax ? 0 : local.y < thumb.yMin ? 1 : -1);
            }

            for (int i = 0; i < _buttons.Length; i++)
            {
                if (_buttons[i].Contains(local))
                {
                    return new CraftHit(CraftPart.Button, i);
                }
            }

            int q = _queue.Hit(local);
            return q >= 0 ? new CraftHit(CraftPart.Queue, q) : default;
        }

        /// <summary>Середина части окна в координатах окна (скрипт стенда снимков).</summary>
        public Vector2 CenterOf(CraftPart part, int index) => part switch
        {
            CraftPart.Section => _tabs[index].Center,
            CraftPart.Sub => _subs[index].Center,
            CraftPart.Filter => _filter.Center,
            CraftPart.Cell => new Vector2(Pad + index % GridColumns * UiSlotGrid.Step + UiStyle.CellSize / 2f,
                BodyY + (GridRows - 1 - index / GridColumns) * UiSlotGrid.Step + UiStyle.CellSize / 2f),
            CraftPart.Button => _buttons[index].Center,
            CraftPart.Queue => new Vector2(Pad + index * UiSlotGrid.Step + UiStyle.CellSize / 2f, QueueY + UiStyle.CellSize / 2f),
            _ => Vector2.zero,
        };

        Rect ThumbRect()
        {
            int rows = Mathf.Max(RowCount, GridRows);
            float h = GridHeight * GridRows / rows;
            float y = BodyTop - h - (GridHeight - h) * (MaxScroll > 0 ? _scroll / (float)MaxScroll : 0f);
            return new Rect(ScrollX, y, ScrollWidth, h);
        }

        /// <summary>E2-10: сводка «сундуки рядом» для карточки рецепта (ставит <see cref="GameUi"/> перед <see cref="Show"/>).</summary>
        public ChestStock? Stock { get; set; }

        /// <summary>
        /// Показать: строки рецептов (по номеру рецепта, из <see cref="CraftDesk.LineOf"/>), сумка и очередь сервера, идёт ли
        /// первая позиция и сколько её тактов прошло, часть под курсором, строка отказа. Перед ним — <see cref="Refresh"/>.
        /// </summary>

        public void Show(CraftLine[] lines, in Inventory bag, in CraftQueue queue, bool running, int progress, bool dead, CraftHit hover, string? message)
        {
            ShelfSection section = CraftShelf.Sections[_section];
            for (int s = 0; s < _tabs.Length; s++)
            {
                _tabs[s].Set(hover.Is(CraftPart.Section, s), s == _section, CraftShelf.CountIn(s) > 0);
            }

            for (int k = 0; k < _subs.Length; k++)
            {
                bool on = !section.Flat && k < section.Subs.Length;
                _subs[k].Visible = on;
                if (on)
                {
                    string label = UiStrings.SubName(section.Key, section.Subs[k]);
                    if (_subs[k].Label.text != label)
                    {
                        _subs[k].Label.text = label;
                    }

                    _subs[k].Set(hover.Is(CraftPart.Sub, k), k == Sub, CraftShelf.Recipes(_section, k).Length > 0);
                }
            }

            _flat.enabled = section.Flat;
            _filter.Set(hover.Is(CraftPart.Filter, 0), false, true);
            _check.enabled = _onlyAvailable;

            ShowGrid(lines, hover);
            ShowCard(lines, bag, dead, hover);
            ShowQueue(queue, running, progress, hover);

            string text = message ?? string.Empty;
            if (text != _shownMessage)
            {
                _shownMessage = text;
                _message.text = text;
                _message.enabled = text.Length > 0;
                _hint.enabled = text.Length == 0;
            }
        }

        void ShowGrid(CraftLine[] lines, CraftHit hover)
        {
            int selected = Recipe, selCell = -1;
            for (int i = 0; i < _grid.Count; i++)
            {
                int recipe = RecipeInCell(i);
                _grid.Show(i, recipe > 0);
                if (recipe == 0)
                {
                    continue;
                }

                CraftLine line = lines[recipe];
                _grid.SetRecipe(i, RecipeTable.Get(recipe).Result, CraftShelf.Badge(line), !line.CanMake);
                if (recipe == selected)
                {
                    selCell = i;
                }
            }

            _grid.Select(selCell);
            _grid.Hover(hover.Part == CraftPart.Cell ? hover.Index : -1);

            string empty = _list.Count > 0 ? string.Empty
                : CraftShelf.Recipes(_section, Sub).Length == 0 ? UiStrings.CraftEmptySub : UiStrings.CraftEmptyFiltered;
            if (empty != _shownEmpty)
            {
                _shownEmpty = empty;
                _empty.text = empty;
                _empty.enabled = empty.Length > 0;
            }

            bool scroll = MaxScroll > 0;
            _track.enabled = scroll;
            _thumb.enabled = scroll;
            if (scroll)
            {
                Rect r = ThumbRect();
                _thumb.rectTransform.anchoredPosition = new Vector2(ScrollX + 2f, r.y + 2f);
                _thumb.rectTransform.sizeDelta = new Vector2(ScrollWidth - 4f, r.height - 4f);
            }
        }

        void ShowCard(CraftLine[] lines, in Inventory bag, bool dead, CraftHit hover)
        {
            int recipe = Recipe;
            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            CraftLine line = lines[recipe];
            if (recipe != _shownRecipe)
            {
                _shownRecipe = recipe;
                _result.Set(def.Result, def.ResultCount);
                _name.text = recipe > 0 ? UiStrings.RecipeName(recipe) : string.Empty;
                _yield.text = recipe > 0 ? UiStrings.RecipeYield(def) : string.Empty;
                _needs.enabled = recipe > 0;
                for (int k = 0; k < _ingredients.Length; k++)
                {
                    bool on = recipe > 0 && k < def.IngredientCount;
                    _ingredients[k].Rt.gameObject.SetActive(on);
                    if (on)
                    {
                        _ingredients[k].Icon.Set(def.IngredientItem(k), 1);
                        _ingredients[k].Name.text = UiStrings.ItemName(def.IngredientItem(k));
                    }
                }
            }

            for (int k = 0; recipe > 0 && k < def.IngredientCount; k++)
            {
                // E2-10: «есть» — сумка + сундуки рядом (сводка сервера); взятое из сундуков помечено в названии.
                int need = def.IngredientCountOf(k), have = CraftDesk.Have(recipe, k, bag, Stock);
                bool lacks = CraftDesk.Missing(recipe, k, bag, Stock) > 0;
                Ingredient ing = _ingredients[k];
                string name = CraftDesk.InChests(recipe, k, Stock) > 0
                    ? UiStrings.ItemName(def.IngredientItem(k)) + UiStrings.CraftInChest
                    : UiStrings.ItemName(def.IngredientItem(k));
                if (ing.Name.text != name)
                {
                    ing.Name.text = name;
                }

                ing.Have.text = $"{have} / {need}";
                ing.Have.color = lacks ? UiStyle.TextBad : UiStyle.TextDark;
                ing.Name.color = lacks ? UiStyle.TextBad : UiStyle.TextDark;
            }

            string state = recipe == 0 ? (_list.Count > 0 ? UiStrings.CraftPick : string.Empty)
                : dead ? UiStrings.CraftDead : UiStrings.CraftState(line.State, def.Station, line.Affordable);
            if (state != _shownState)
            {
                _shownState = state;
                _state.text = state;
            }

            _state.color = line.CanMake || recipe == 0 ? UiStyle.TextDark : UiStyle.TextBad;
            int all = line.CanMake ? line.Affordable : 0;
            if (all != _shownAll)
            {
                _shownAll = all;
                _buttons[2].Label.text = all > 0 ? UiStrings.CraftMakeAllOf(all) : UiStrings.CraftMakeAll;
            }

            for (int k = 0; k < _buttons.Length; k++)
            {
                _buttons[k].Set(hover.Is(CraftPart.Button, k), false, line.CanMake);
            }
        }

        void ShowQueue(in CraftQueue queue, bool running, int progress, CraftHit hover)
        {
            for (int i = 0; i < CraftQueue.MaxEntries; i++)
            {
                int recipe = queue.RecipeAt(i);
                ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
                // Число в ячейке — сколько изготовлений осталось; одно — тоже показываем (значок без числа = «одно»).
                _queue.Set(i, recipe > 0 ? def.Result : Items.None, queue.LeftAt(i));
            }

            _queue.Hover(hover.Part == CraftPart.Queue && hover.Index < queue.Count ? hover.Index : -1);
            _queue.Select(queue.IsEmpty ? -1 : 0);
            int first = queue.RecipeAt(0);
            int key = queue.IsEmpty ? 0 : first * 2 + (running ? 1 : 0);
            if (key != _shownQueueText)
            {
                _shownQueueText = key;
                _queueText.text = queue.IsEmpty ? UiStrings.CraftQueueEmpty : running ? UiStrings.RecipeName(first) : UiStrings.CraftQueueStopped;
                _queueText.color = queue.IsEmpty || running ? UiStyle.TextDark : UiStyle.TextBad;
            }

            int ticks = RecipeTable.Get(first).Ticks;
            _progress.Set(queue.IsEmpty || ticks <= 0 ? 0f : progress / (float)ticks);
        }
    }
}
