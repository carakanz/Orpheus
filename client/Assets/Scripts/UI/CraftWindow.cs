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

        /// <summary>C-008: выбранный рецепт крупным значком — щелчок делает (1, Shift — 5, Ctrl — всё).</summary>
        Button,
        Queue,
        Filter,
        ScrollBar,
    }

    /// <summary>
    /// Попадание в окно изготовления: часть и её номер (раздел, подраздел, ячейка столбика на экране, позиция очереди;
    /// полоса прокрутки: 0 — выше ползунка, 1 — ниже).
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
    /// Окно изготовления (E2-4 → E2-9 → C-008, слова Автора 15.11: «Изготовление тоже [уменьшить и сделать прозрачным], и
    /// перенести влево вниз», «как в Террарии»). Компактный столбик в левом нижнем углу без подложки: сверху — разделы
    /// маленькими значками (Здания, Мебель, Инструменты, Оружие, Броня, Материалы, Еда) и галочка «Только доступные»
    /// (включена по умолчанию, как в Terraria), под ними — подразделы маленькими значками, строка очереди с полосой
    /// прогресса, ниже — вертикальный столбик значков рецептов (прокрутка колесом). Выбранный рецепт — крупным значком
    /// справа от своей строки, рядом в строку его ингредиенты «есть / нужно» (нехватка — красным); щелчок по крупному
    /// значку — сделать 1, Shift — 5, Ctrl — всё. Названия — в подсказках при наведении.
    /// <para>Только вид: раскладка, фильтр и число на значке — <see cref="CraftShelf"/>, «можно ли» — строки
    /// <see cref="CraftDesk.LineOf"/>; заявки шлёт <see cref="GameUi"/> через <see cref="CraftDesk"/>. Окно ничего не предсказывает.</para>
    /// </summary>
    public sealed class CraftWindow : UiGroup
    {
        public const int GridColumns = 1, GridRows = 7;
        const float Small = 36f, SmallIcon = 28f;
        static readonly float SmallPitch = Small + UiStyle.CellGap;
        const float MsgH = 24f;
        const float ColY = MsgH + 4f;
        static readonly float ColH = UiSlotGrid.WidthOf(GridRows);
        static readonly float ColTop = ColY + ColH;
        const float TrackX = UiStyle.CellSize + 2f, TrackW = 5f;
        static readonly float QueueY = ColTop + 8f;
        static readonly float SubY = QueueY + Small + 8f;
        static readonly float TabsY = SubY + Small + 8f;
        static readonly float TitleY = TabsY + Small + 4f;
        const float TitleH = 24f;
        public static readonly float Height = TitleY + TitleH;
        const float CardX = UiStyle.CellSize + 10f, Big = 64f, IngStep = 62f, IngBox = 40f;
        const float IngX = Big + 10f;
        static readonly float TabsW = CraftShelf.Sections.Length * SmallPitch - UiStyle.CellGap;
        static readonly float FilterX = TabsW + 8f;
        public static readonly float Width = Mathf.Max(FilterX + Small, CardX + IngX + RecipeTable.MaxIngredients * IngStep);

        /// <summary>Сколько делает щелчок по крупному значку: без клавиш, с Shift; с Ctrl — «всё, на что хватает».</summary>
        public static readonly int[] ButtonCounts = { 1, 5 };

        static readonly int MaxSubs = CountMaxSubs();

        sealed class Ingredient
        {
            public RectTransform Rt = null!;
            public UiItemIcon Icon = null!;
            public Text Have = null!;
        }

        readonly UiSlotGrid _tabs, _subs, _col, _queue;
        readonly Text[] _tabLetters, _subLetters;
        readonly UiButton _filter;
        readonly Image _check;
        readonly Text _empty, _message;
        readonly Image _track, _thumb;
        readonly RectTransform _card;
        readonly Image _bigBack;
        readonly Sprite _bigPlain, _bigHover, _bigOff;
        readonly UiItemIcon _result;
        readonly Text _name, _state;
        readonly Ingredient[] _ingredients = new Ingredient[RecipeTable.MaxIngredients];
        readonly UiBar _progress;
        readonly RectTransform _progressRt;

        readonly int[] _subOf = new int[CraftShelf.Sections.Length];      // подраздел, открытый в каждом разделе
        readonly int[][] _selected = new int[CraftShelf.Sections.Length][]; // рецепт, выбранный в каждом подразделе
        readonly List<int> _list = new List<int>();                         // что показано в столбике сейчас
        int _section, _scroll;
        bool _onlyAvailable = true; // C-008: как в Terraria — по умолчанию только то, что можно сделать
        int _shownRecipe = -1, _queueCount;
        float _cardY = -1f;
        string _shownState = string.Empty, _shownMessage = string.Empty, _shownEmpty = "?";

        public CraftWindow(UiRoot root)
            : base(root, "Craft", new Vector2(0f, 0f), new Vector2(20f, 20f), new Vector2(Width, Height))
        {
            UiRoot.Label(Rt, "Title", 2f, TitleY, Width, TitleH, 20, TextAnchor.MiddleLeft, UiStyle.TextLight, outline: true, bold: true)
                .text = UiStrings.CraftTitleKeys;

            // Разделы — маленькие значки (значок — результат первого рецепта раздела; пустой раздел — буквы).
            _tabs = new UiSlotGrid(Rt, 0f, TabsY, CraftShelf.Sections.Length, 1, 0, UiCellLook.Glass, cell: Small, icon: SmallIcon);
            _tabLetters = new Text[CraftShelf.Sections.Length];
            for (int s = 0; s < _tabLetters.Length; s++)
            {
                _selected[s] = new int[CraftShelf.Sections[s].Subs.Length];
                Vector2 c = _tabs.CenterOf(s);
                _tabLetters[s] = UiRoot.Label(Rt, "Letter", c.x - Small / 2f, c.y - Small / 2f, Small, Small, 18, TextAnchor.MiddleCenter, UiStyle.TextLight, outline: true, bold: true);
                ShelfSection sec = CraftShelf.Sections[s];
                byte icon = FirstResult(s, -1);
                _tabs.SetRecipe(s, icon, 0, CraftShelf.CountIn(s) == 0);
                _tabLetters[s].text = icon == Items.None ? Abbrev(UiStrings.SectionName(sec.Key)) : string.Empty;
            }

            _filter = new UiButton(Rt, FilterX, TabsY, Small, Small, string.Empty, 18, glass: true);
            UiRoot.Picture(_filter.Rt, "Box", 8f, 8f, 20f, 20f, UiStyle.Frame(UiStyle.GlassOff, UiStyle.TextLight, thick: 2f, radius: 4f));
            _check = UiRoot.Picture(_filter.Rt, "Check", 12f, 12f, 12f, 12f, null, sliced: false);
            _check.color = UiStyle.Gold;

            _subs = new UiSlotGrid(Rt, 0f, SubY, MaxSubs, 1, 0, UiCellLook.Glass, cell: Small, icon: SmallIcon);
            _subLetters = new Text[MaxSubs];
            for (int k = 0; k < MaxSubs; k++)
            {
                Vector2 c = _subs.CenterOf(k);
                _subLetters[k] = UiRoot.Label(Rt, "Letter", c.x - Small / 2f, c.y - Small / 2f, Small, Small, 18, TextAnchor.MiddleCenter, UiStyle.TextLight, outline: true, bold: true);
            }

            // Очередь изготовления — строка маленьких значков и полоса прогресса первой позиции.
            _queue = new UiSlotGrid(Rt, 0f, QueueY, CraftQueue.MaxEntries, 1, 0, UiCellLook.Glass, cell: Small, icon: SmallIcon);
            float barX = CraftQueue.MaxEntries * SmallPitch + 4f;
            _progressRt = UiRoot.Node(Rt, "Progress", barX, QueueY + 11f, Width - barX, 14f);
            _progress = new UiBar(_progressRt, 0f, 0f, Width - barX, 14f, UiStyle.Gold);

            // Столбик рецептов и тонкая полоса прокрутки.
            _col = new UiSlotGrid(Rt, 0f, ColY, GridColumns, GridRows, 0, UiCellLook.Glass);
            _track = UiRoot.Picture(Rt, "Track", TrackX, ColY, TrackW, ColH, UiStyle.Frame(UiStyle.GlassOff, UiStyle.GlassOffEdge, thick: 1f, radius: 2f));
            _thumb = UiRoot.Picture(Rt, "Thumb", TrackX, ColY, TrackW, ColH, null, sliced: false);
            _thumb.color = UiStyle.Gold;
            _empty = UiRoot.Label(Rt, "Empty", 2f, ColTop - 26f, 420f, 24f, 18, TextAnchor.MiddleLeft, UiStyle.TextLight, outline: true);

            // Выбранный рецепт: крупный значок, название над ним, ингредиенты в строку, состояние под ним.
            _card = UiRoot.Node(Rt, "Card", CardX, ColY, Width - CardX, Big);
            _bigPlain = UiStyle.Frame(UiStyle.Glass, UiStyle.Gold, thick: 3f, radius: 8f);
            _bigHover = UiStyle.Frame(UiStyle.GlassHover, UiStyle.Gold, thick: 3f, radius: 8f);
            _bigOff = UiStyle.Frame(UiStyle.GlassOff, UiStyle.GlassOffEdge, thick: 3f, radius: 8f);
            _bigBack = UiRoot.Picture(_card, "Big", 0f, 0f, Big, Big, _bigPlain);
            _result = new UiItemIcon(_card, 0f, 0f, Big, 52f);
            _name = UiRoot.Label(_card, "Name", 2f, Big + 2f, 420f, 24f, 20, TextAnchor.MiddleLeft, UiStyle.TextLight, outline: true, bold: true);
            _state = UiRoot.Label(_card, "State", 2f, -24f, 420f, 22f, 18, TextAnchor.MiddleLeft, UiStyle.TextWarn, outline: true, bold: true);
            Sprite ingBack = UiStyle.Frame(UiStyle.Glass, UiStyle.GlassEdge, thick: 2f, radius: 6f);
            for (int k = 0; k < _ingredients.Length; k++)
            {
                var ing = new Ingredient { Rt = UiRoot.Node(_card, "Ingredient", IngX + k * IngStep, 0f, IngStep - 4f, Big) };
                UiRoot.Picture(ing.Rt, "Back", (IngStep - 4f - IngBox) / 2f, Big - IngBox, IngBox, IngBox, ingBack);
                ing.Icon = new UiItemIcon(ing.Rt, (IngStep - 4f - IngBox) / 2f, Big - IngBox, IngBox, 32f);
                ing.Have = UiRoot.Label(ing.Rt, "Have", -4f, -2f, IngStep + 4f, 24f, 18, TextAnchor.MiddleCenter, UiStyle.TextLight, outline: true, bold: true);
                _ingredients[k] = ing;
            }

            _message = UiRoot.Label(Rt, "Message", 2f, 0f, 700f, MsgH, 20, TextAnchor.MiddleLeft, UiStyle.TextWarn, outline: true, bold: true);
            _message.enabled = false;
            Visible = false;
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

        static string Abbrev(string name) => name.Length <= 2 ? name : name.Substring(0, 2);

        // Значок раздела / подраздела — результат первого рецепта (sub = −1 — любой подраздел); нет рецептов — пусто.
        static byte FirstResult(int section, int sub)
        {
            int n = CraftShelf.Sections[section].Subs.Length;
            for (int k = sub < 0 ? 0 : sub; k < (sub < 0 ? n : sub + 1); k++)
            {
                var r = CraftShelf.Recipes(section, k);
                if (r.Length > 0)
                {
                    return RecipeTable.Get(r[0]).Result;
                }
            }

            return Items.None;
        }

        /// <summary>Открытый раздел (номер в <see cref="CraftShelf.Sections"/>).</summary>
        public int Section => _section;

        /// <summary>Открытый подраздел раздела.</summary>
        public int Sub => _subOf[_section];

        /// <summary>Галочка «Только доступные».</summary>
        public bool OnlyAvailable => _onlyAvailable;

        /// <summary>Первая строка столбика на экране (прокрутка).</summary>
        public int ScrollRow => _scroll;

        /// <summary>Рецептов в столбике сейчас (с учётом фильтра).</summary>
        public int Shown => _list.Count;

        /// <summary>Выбранный рецепт (номер <see cref="RecipeTable"/>); в пустом столбике — 0.</summary>
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
        /// Пересчитать содержимое столбика по строкам рецептов (<paramref name="lines"/> — по номеру рецепта, из
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

        /// <summary>Выбрать соседний рецепт (PgUp / PgDn: −1 / +1); выбранный остаётся на экране.</summary>
        public void StepRecipe(int step)
        {
            if (_list.Count == 0)
            {
                return;
            }

            int i = Mathf.Clamp(_list.IndexOf(Recipe) + step, 0, _list.Count - 1);
            Pick(i);
        }

        /// <summary>Щелчок по ячейке столбика на экране (номер ячейки с учётом прокрутки).</summary>
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

        /// <summary>Прокрутка столбика на <paramref name="rows"/> строк (колесо: вверх — −1).</summary>
        public void Scroll(int rows) => _scroll = Mathf.Clamp(_scroll + rows, 0, MaxScroll);

        /// <summary>Рецепт в ячейке столбика на экране; пустая ячейка — 0.</summary>
        public int RecipeInCell(int cell)
        {
            int i = _scroll * GridColumns + cell;
            return (uint)i < (uint)_list.Count ? _list[i] : 0;
        }

        int VisibleRows => Mathf.Clamp(RowCount - _scroll, 0, GridRows);

        /// <summary>Точка над столбиком рецептов (колесо прокручивает столбик).</summary>
        public bool OverGrid(Vector2 local) =>
            Visible && local.x >= 0f && local.x < TrackX + TrackW && local.y >= ColY && local.y < ColTop;

        Rect BigRect => new Rect(CardX, _cardY, Big, Big);

        /// <summary>Что под точкой окна.</summary>
        public CraftHit HitTest(Vector2 local)
        {
            if (!Visible)
            {
                return default;
            }

            int i = _tabs.Hit(local);
            if (i >= 0)
            {
                return new CraftHit(CraftPart.Section, i);
            }

            if (_filter.Contains(local))
            {
                return new CraftHit(CraftPart.Filter, 0);
            }

            ShelfSection section = CraftShelf.Sections[_section];
            i = _subs.Hit(local);
            if (i >= 0)
            {
                return !section.Flat && i < section.Subs.Length ? new CraftHit(CraftPart.Sub, i) : default;
            }

            int cell = _col.Hit(local);
            if (cell >= 0)
            {
                return RecipeInCell(cell) > 0 ? new CraftHit(CraftPart.Cell, cell) : default;
            }

            if (MaxScroll > 0 && local.x >= TrackX - 2f && local.x < CardX && local.y >= ColY && local.y < ColTop)
            {
                Rect thumb = ThumbRect();
                return new CraftHit(CraftPart.ScrollBar, local.y >= thumb.yMax ? 0 : local.y < thumb.yMin ? 1 : -1);
            }

            if (Recipe > 0 && BigRect.Contains(local))
            {
                return new CraftHit(CraftPart.Button, 0);
            }

            int q = _queue.Hit(local);
            return q >= 0 && q < _queueCount ? new CraftHit(CraftPart.Queue, q) : default;
        }

        /// <summary>
        /// C-008: видимые части — интерфейс (значки разделов и подразделов, очередь, ячейки столбика, выбранный рецепт с
        /// ингредиентами); пустое место вокруг — мир.
        /// </summary>
        public void AddZones(UiPointer p, Vector2 canvas)
        {
            if (!Visible)
            {
                return;
            }

            Zone(p, canvas, 0f, TabsY, FilterX + Small, Small);
            ShelfSection section = CraftShelf.Sections[_section];
            if (!section.Flat)
            {
                Zone(p, canvas, 0f, SubY, section.Subs.Length * SmallPitch - UiStyle.CellGap, Small);
            }

            if (_queueCount > 0)
            {
                Zone(p, canvas, 0f, QueueY, Width, Small);
            }

            int rows = VisibleRows;
            if (rows > 0)
            {
                float h = rows * UiSlotGrid.Step - UiStyle.CellGap;
                Zone(p, canvas, 0f, ColTop - h, MaxScroll > 0 ? CardX : UiStyle.CellSize, h);
            }

            int recipe = Recipe;
            if (recipe > 0 && _cardY >= 0f)
            {
                Zone(p, canvas, CardX, _cardY, IngX + RecipeTable.Get(recipe).IngredientCount * IngStep, Big);
            }
        }

        /// <summary>Подсказка значка раздела, подраздела или галочки; иначе null.</summary>
        public string? TipFor(CraftHit hit)
        {
            ShelfSection section = CraftShelf.Sections[_section];
            return hit.Part switch
            {
                CraftPart.Section => UiStrings.SectionName(CraftShelf.Sections[hit.Index].Key),
                CraftPart.Sub => UiStrings.SubName(section.Key, section.Subs[hit.Index]),
                CraftPart.Filter => UiStrings.FilterTip(_onlyAvailable),
                _ => null,
            };
        }

        /// <summary>Середина части окна в координатах окна (скрипт стенда снимков).</summary>
        public Vector2 CenterOf(CraftPart part, int index) => part switch
        {
            CraftPart.Section => _tabs.CenterOf(index),
            CraftPart.Sub => _subs.CenterOf(index),
            CraftPart.Filter => _filter.Center,
            CraftPart.Cell => _col.CenterOf(index),
            CraftPart.Button => new Vector2(CardX + Big / 2f, (_cardY >= 0f ? _cardY : ColY) + Big / 2f),
            CraftPart.Queue => _queue.CenterOf(index),
            _ => Vector2.zero,
        };

        Rect ThumbRect()
        {
            int rows = Mathf.Max(RowCount, GridRows);
            float h = ColH * GridRows / rows;
            float y = ColTop - h - (ColH - h) * (MaxScroll > 0 ? _scroll / (float)MaxScroll : 0f);
            return new Rect(TrackX, y, TrackW, h);
        }

        /// <summary>E2-10: сводка «сундуки рядом» для «есть» ингредиентов (ставит <see cref="GameUi"/> перед <see cref="Show"/>).</summary>
        public ChestStock? Stock { get; set; }

        /// <summary>
        /// Показать: строки рецептов (по номеру рецепта, из <see cref="CraftDesk.LineOf"/>), сумка и очередь сервера, идёт ли
        /// первая позиция и сколько её тактов прошло, часть под курсором, строка отказа. Перед ним — <see cref="Refresh"/>.
        /// </summary>
        public void Show(CraftLine[] lines, in Inventory bag, in CraftQueue queue, bool running, int progress, bool dead, CraftHit hover, string? message)
        {
            ShelfSection section = CraftShelf.Sections[_section];
            _tabs.Select(_section);
            _tabs.Hover(hover.Part == CraftPart.Section ? hover.Index : -1);

            for (int k = 0; k < MaxSubs; k++)
            {
                bool on = !section.Flat && k < section.Subs.Length;
                _subs.Show(k, on);
                if (on)
                {
                    byte icon = FirstResult(_section, k);
                    _subs.SetRecipe(k, icon, 0, CraftShelf.Recipes(_section, k).Length == 0);
                    string letters = icon == Items.None ? Abbrev(UiStrings.SubName(section.Key, section.Subs[k])) : string.Empty;
                    if (_subLetters[k].text != letters)
                    {
                        _subLetters[k].text = letters;
                    }
                }

                _subLetters[k].enabled = on;
            }

            _subs.Select(section.Flat ? -1 : Sub);
            _subs.Hover(hover.Part == CraftPart.Sub ? hover.Index : -1);
            _filter.Set(hover.Is(CraftPart.Filter, 0), false, true);
            _check.enabled = _onlyAvailable;

            ShowColumn(lines, hover);
            ShowCard(lines, bag, dead, hover);
            ShowQueue(queue, running, progress, hover);

            string text = message ?? string.Empty;
            if (text != _shownMessage)
            {
                _shownMessage = text;
                _message.text = text;
                _message.enabled = text.Length > 0;
            }
        }

        void ShowColumn(CraftLine[] lines, CraftHit hover)
        {
            int selected = Recipe, selCell = -1;
            for (int i = 0; i < _col.Count; i++)
            {
                int recipe = RecipeInCell(i);
                _col.Show(i, recipe > 0);
                if (recipe == 0)
                {
                    continue;
                }

                CraftLine line = lines[recipe];
                _col.SetRecipe(i, RecipeTable.Get(recipe).Result, CraftShelf.Badge(line), !line.CanMake);
                if (recipe == selected)
                {
                    selCell = i;
                }
            }

            _col.Select(selCell);
            _col.Hover(hover.Part == CraftPart.Cell ? hover.Index : -1);

            // Крупный значок выбранного — справа от своей строки (в пределах столбика).
            float y = selCell >= 0 ? _col.CenterOf(selCell).y - Big / 2f : ColTop - Big;
            y = Mathf.Clamp(y, ColY, ColTop - Big);
            if (!Mathf.Approximately(y, _cardY))
            {
                _cardY = y;
                _card.anchoredPosition = new Vector2(CardX, y);
            }

            string empty = _list.Count > 0 ? string.Empty
                : CraftShelf.Recipes(_section, Sub).Length == 0 ? UiStrings.CraftEmptyShort : UiStrings.CraftEmptyFilteredShort;
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
                _thumb.rectTransform.anchoredPosition = new Vector2(TrackX, r.y);
                _thumb.rectTransform.sizeDelta = new Vector2(TrackW, r.height);
            }
        }

        void ShowCard(CraftLine[] lines, in Inventory bag, bool dead, CraftHit hover)
        {
            int recipe = Recipe;
            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            CraftLine line = lines[recipe];
            bool on = recipe > 0;
            if (_card.gameObject.activeSelf != on)
            {
                _card.gameObject.SetActive(on);
            }

            if (!on)
            {
                _shownRecipe = 0;
                return;
            }

            if (recipe != _shownRecipe)
            {
                _shownRecipe = recipe;
                _result.Set(def.Result, def.ResultCount);
                _name.text = UiStrings.RecipeName(recipe);
                for (int k = 0; k < _ingredients.Length; k++)
                {
                    bool has = k < def.IngredientCount;
                    _ingredients[k].Rt.gameObject.SetActive(has);
                    if (has)
                    {
                        _ingredients[k].Icon.Set(def.IngredientItem(k), 1);
                    }
                }
            }

            for (int k = 0; k < def.IngredientCount; k++)
            {
                // E2-10: «есть» — сумка + сундуки рядом (сводка сервера).
                int need = def.IngredientCountOf(k), have = CraftDesk.Have(recipe, k, bag, Stock);
                bool lacks = CraftDesk.Missing(recipe, k, bag, Stock) > 0;
                Ingredient ing = _ingredients[k];
                ing.Have.text = $"{have}/{need}";
                ing.Have.color = lacks ? UiStyle.TextWarn : UiStyle.TextLight;
            }

            string state = line.CanMake ? string.Empty
                : dead ? UiStrings.CraftDead : UiStrings.CraftState(line.State, def.Station, line.Affordable);
            if (state != _shownState)
            {
                _shownState = state;
                _state.text = state;
            }

            _bigBack.sprite = !line.CanMake ? _bigOff : hover.Part == CraftPart.Button ? _bigHover : _bigPlain;
            _result.Dim(!line.CanMake);
        }

        void ShowQueue(in CraftQueue queue, bool running, int progress, CraftHit hover)
        {
            _queueCount = queue.Count;
            for (int i = 0; i < CraftQueue.MaxEntries; i++)
            {
                int recipe = queue.RecipeAt(i);
                ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
                _queue.Show(i, i < queue.Count);
                _queue.Set(i, recipe > 0 ? def.Result : Items.None, queue.LeftAt(i));
            }

            _queue.Hover(hover.Part == CraftPart.Queue && hover.Index < queue.Count ? hover.Index : -1);
            _queue.Select(queue.IsEmpty ? -1 : 0);
            bool bar = !queue.IsEmpty;
            if (_progressRt.gameObject.activeSelf != bar)
            {
                _progressRt.gameObject.SetActive(bar);
            }

            int first = queue.RecipeAt(0);
            int ticks = RecipeTable.Get(first).Ticks;
            _progress.Set(queue.IsEmpty || ticks <= 0 || !running ? 0f : progress / (float)ticks);
        }
    }
}
