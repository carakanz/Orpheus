#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Sim;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    /// <summary>Что под курсором в окне крафта.</summary>
    public enum CraftPart : byte
    {
        None = 0,
        Tab,
        Row,
        Button,
        Queue,
    }

    /// <summary>Попадание в окно крафта: часть и её номер (вкладка 0…2, строка списка, кнопка 0…2, позиция очереди).</summary>
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
    /// Окно крафта (E2-4, шаг 2.4): вкладки «Руками / Верстак / Костёр», слева список рецептов вкладки, справа карточка
    /// выбранного (результат, ингредиенты «есть / нужно», время, кнопки «Сделать 1 / 5 / всё»), ниже очередь изготовления
    /// с полосой прогресса и строка отказа сервера. Только вид: что можно сделать, считает <see cref="CraftDesk"/> (теми же
    /// правилами ядра, что сервер), заявки шлёт <see cref="GameUi"/> через него же; окно ничего не предсказывает.
    /// </summary>
    public sealed class CraftWindow : UiWindow
    {
        public const float Width = 830f;
        const float HintHeight = 46f;
        const float QueueY = Pad + HintHeight + 10f;
        const float QueueLabelY = QueueY + UiStyle.CellSize + 9f;
        const float BodyY = QueueLabelY + 26f + 8f;
        const int MaxRows = 8;
        const float RowHeight = 50f, RowGap = 4f;
        const float BodyHeight = MaxRows * (RowHeight + RowGap) - RowGap;
        const float BodyTop = BodyY + BodyHeight;
        const float TabsY = BodyTop + 8f;
        const float TabHeight = 40f, TabWidth = 170f, TabGap = 6f;
        public const float Height = TabsY + TabHeight + 6f + TitleHeight + Pad;
        const float ListWidth = 360f;
        const float CardX = Pad + ListWidth + 14f;
        const float CardWidth = Width - CardX - Pad;
        const float CardPad = 14f;
        const float IngredientHeight = 48f;

        /// <summary>Станки по порядку вкладок.</summary>
        public static readonly CraftStation[] Stations = { CraftStation.None, CraftStation.Workbench, CraftStation.Campfire };

        /// <summary>Сколько делает кнопка № 0…1; кнопка 2 — «всё, на что хватает».</summary>
        public static readonly int[] ButtonCounts = { 1, 5 };

        static readonly int[][] ByTab = BuildTabs();

        sealed class Row
        {
            public UiButton Back = null!;
            public UiItemIcon Icon = null!;
            public Text Name = null!;
            public Text Note = null!;
        }

        sealed class Ingredient
        {
            public RectTransform Rt = null!;
            public UiItemIcon Icon = null!;
            public Text Name = null!;
            public Text Have = null!;
        }

        readonly UiButton[] _tabs = new UiButton[3];
        readonly Row[] _rows = new Row[MaxRows];
        readonly UiItemIcon _result;
        readonly Text _name, _yield, _state, _queueText, _hint, _message;
        readonly Ingredient[] _ingredients = new Ingredient[RecipeTable.MaxIngredients];
        readonly UiButton[] _buttons = new UiButton[3];
        readonly UiSlotGrid _queue;
        readonly UiBar _progress;
        readonly int[] _selected = new int[3]; // строка, выбранная на каждой вкладке
        int _tab;
        int _shownRecipe = -1, _shownAll = -1, _shownQueueText = -1;
        string _shownState = string.Empty, _shownMessage = string.Empty;

        public CraftWindow(UiRoot root)
            : base(root, "Craft", UiStrings.CraftTitle, new Vector2(0.5f, 0.5f), new Vector2(-Width / 2f, -Height / 2f + 40f), new Vector2(Width, Height))
        {
            UiRoot.Label(Rt, "Key", Pad, Height - Pad - TitleHeight, Width - 2f * Pad, TitleHeight, 18, TextAnchor.MiddleRight, UiStyle.TextDark)
                .text = UiStrings.CraftKey;

            for (int i = 0; i < _tabs.Length; i++)
            {
                _tabs[i] = new UiButton(Rt, Pad + i * (TabWidth + TabGap), TabsY, TabWidth, TabHeight, UiStrings.StationName(Stations[i]), 22);
            }

            for (int i = 0; i < MaxRows; i++)
            {
                float y = BodyTop - (i + 1) * RowHeight - i * RowGap;
                var row = new Row { Back = new UiButton(Rt, Pad, y, ListWidth, RowHeight, string.Empty) };
                row.Icon = new UiItemIcon(Rt, Pad + 5f, y + 3f, 44f, 40f);
                row.Name = UiRoot.Label(Rt, "Name", Pad + 56f, y, 190f, RowHeight, 20, TextAnchor.MiddleLeft, UiStyle.TextDark, bold: true);
                row.Note = UiRoot.Label(Rt, "Note", Pad + ListWidth - 170f, y, 160f, RowHeight, 18, TextAnchor.MiddleRight, UiStyle.TextDim);
                _rows[i] = row;
            }

            // Карточка выбранного рецепта.
            UiRoot.Picture(Rt, "Card", CardX, BodyY, CardWidth, BodyHeight, UiStyle.Frame(UiStyle.Card, UiStyle.InkSoft));
            float x = CardX + CardPad, top = BodyTop - CardPad;
            UiRoot.Picture(Rt, "ResultCell", x, top - UiStyle.CellSize, UiStyle.CellSize, UiStyle.CellSize, UiStyle.Frame(UiStyle.CellBar, UiStyle.Ink));
            _result = new UiItemIcon(Rt, x, top - UiStyle.CellSize, UiStyle.CellSize);
            float textX = x + UiStyle.CellSize + 12f, textW = CardWidth - 2f * CardPad - UiStyle.CellSize - 12f;
            _name = UiRoot.Label(Rt, "Result", textX, top - 34f, textW, 34f, 26, TextAnchor.MiddleLeft, UiStyle.TextDark, bold: true);
            _yield = UiRoot.Label(Rt, "Yield", textX, top - UiStyle.CellSize, textW, 28f, 18, TextAnchor.MiddleLeft, UiStyle.TextDark);
            float needsY = top - UiStyle.CellSize - 10f - 26f;
            UiRoot.Label(Rt, "Needs", x, needsY, 200f, 26f, 20, TextAnchor.MiddleLeft, UiStyle.TextDark, bold: true).text = UiStrings.CraftNeeds;
            for (int k = 0; k < _ingredients.Length; k++)
            {
                float y = needsY - 4f - (k + 1) * IngredientHeight;
                var ing = new Ingredient { Rt = UiRoot.Node(Rt, "Ingredient", x, y, CardWidth - 2f * CardPad, IngredientHeight) };
                ing.Icon = new UiItemIcon(ing.Rt, 0f, 2f, 44f, 40f);
                ing.Name = UiRoot.Label(ing.Rt, "Name", 54f, 0f, 220f, IngredientHeight, 20, TextAnchor.MiddleLeft, UiStyle.TextDark);
                ing.Have = UiRoot.Label(ing.Rt, "Have", CardWidth - 2f * CardPad - 150f, 0f, 150f, IngredientHeight, 22, TextAnchor.MiddleRight,
                    UiStyle.TextDark, bold: true);
                _ingredients[k] = ing;
            }

            float buttonsY = BodyY + CardPad, bw = (CardWidth - 2f * CardPad - 2f * 8f) / 3f;
            _state = UiRoot.Label(Rt, "State", x, buttonsY + 46f + 10f, CardWidth - 2f * CardPad, 30f, 20, TextAnchor.MiddleLeft, UiStyle.TextDark, bold: true);
            string[] captions = { UiStrings.CraftMake1, UiStrings.CraftMake5, UiStrings.CraftMakeAll };
            for (int k = 0; k < _buttons.Length; k++)
            {
                _buttons[k] = new UiButton(Rt, x + k * (bw + 8f), buttonsY, bw, 46f, captions[k], 18);
            }

            // Очередь изготовления.
            UiRoot.Label(Rt, "QueueLabel", Pad, QueueLabelY, 500f, 26f, 18, TextAnchor.MiddleLeft, UiStyle.TextDark).text = UiStrings.CraftQueue;
            _queue = new UiSlotGrid(Rt, Pad, QueueY, CraftQueue.MaxEntries, 1, 0, UiStyle.Cell);
            float barX = Pad + UiSlotGrid.WidthOf(CraftQueue.MaxEntries) + 16f, barW = Width - Pad - barX;
            _queueText = UiRoot.Label(Rt, "QueueText", barX, QueueY + 34f, barW, 28f, 20, TextAnchor.MiddleLeft, UiStyle.TextDark, bold: true);
            _progress = new UiBar(Rt, barX, QueueY + 6f, barW, 24f, UiStyle.Ochre);

            _hint = UiRoot.Label(Rt, "Hint", Pad, Pad - 2f, Width - 2f * Pad, HintHeight, 18, TextAnchor.MiddleLeft, UiStyle.TextDark);
            _hint.text = UiStrings.CraftHint;
            _message = UiRoot.Label(Rt, "Message", Pad, Pad - 2f, Width - 2f * Pad, HintHeight, 24, TextAnchor.MiddleLeft, UiStyle.TextBad, bold: true);
            _message.enabled = false;
        }

        static int[][] BuildTabs()
        {
            var tabs = new int[Stations.Length][];
            for (int t = 0; t < tabs.Length; t++)
            {
                var list = new List<int>();
                for (int r = 1; r < RecipeTable.Count && list.Count < MaxRows; r++)
                {
                    if (RecipeTable.Get(r).Station == Stations[t])
                    {
                        list.Add(r);
                    }
                }

                tabs[t] = list.ToArray();
            }

            return tabs;
        }

        /// <summary>Вкладка станка.</summary>
        public static int TabOf(CraftStation station) => station == CraftStation.Workbench ? 1 : station == CraftStation.Campfire ? 2 : 0;

        /// <summary>Открытая вкладка (0…2).</summary>
        public int Tab => _tab;

        /// <summary>Рецептов на открытой вкладке.</summary>
        public int Rows => ByTab[_tab].Length;

        /// <summary>Выбранная строка открытой вкладки.</summary>
        public int SelectedRow => _selected[_tab];

        /// <summary>Выбранный рецепт (номер <see cref="RecipeTable"/>); на пустой вкладке — 0.</summary>
        public int Recipe => Rows > 0 ? ByTab[_tab][Mathf.Clamp(_selected[_tab], 0, Rows - 1)] : 0;

        public void SelectTab(int tab) => _tab = (tab % _tabs.Length + _tabs.Length) % _tabs.Length;

        public void SelectRow(int row)
        {
            if (Rows > 0)
            {
                _selected[_tab] = Mathf.Clamp(row, 0, Rows - 1);
            }
        }

        /// <summary>Что под точкой окна.</summary>
        public CraftHit HitTest(Vector2 local)
        {
            for (int i = 0; i < _tabs.Length; i++)
            {
                if (_tabs[i].Contains(local))
                {
                    return new CraftHit(CraftPart.Tab, i);
                }
            }

            for (int i = 0; i < Rows; i++)
            {
                if (_rows[i].Back.Contains(local))
                {
                    return new CraftHit(CraftPart.Row, i);
                }
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
            CraftPart.Tab => _tabs[index].Center,
            CraftPart.Row => _rows[index].Back.Center,
            CraftPart.Button => _buttons[index].Center,
            CraftPart.Queue => new Vector2(Pad + index * UiSlotGrid.Step + UiStyle.CellSize / 2f, QueueY + UiStyle.CellSize / 2f),
            _ => Vector2.zero,
        };

        /// <summary>
        /// Показать: строки рецептов (<paramref name="lines"/> — по номеру рецепта, из <see cref="CraftDesk.LineOf"/>), сумка
        /// и очередь сервера, идёт ли первая позиция и сколько её тактов прошло, часть под курсором, строка отказа.
        /// </summary>
        public void Show(CraftLine[] lines, in Inventory bag, in CraftQueue queue, bool running, int progress, bool dead, CraftHit hover, string? message)
        {
            for (int t = 0; t < _tabs.Length; t++)
            {
                _tabs[t].Set(hover.Is(CraftPart.Tab, t), t == _tab, StationNear(lines, t));
            }

            int[] list = ByTab[_tab];
            for (int i = 0; i < MaxRows; i++)
            {
                Row row = _rows[i];
                bool on = i < list.Length;
                row.Back.Visible = on;
                row.Icon.Rt.gameObject.SetActive(on);
                row.Name.enabled = on;
                row.Note.enabled = on;
                if (!on)
                {
                    continue;
                }

                ref readonly RecipeDef def = ref RecipeTable.Get(list[i]);
                CraftLine line = lines[list[i]];
                bool noStation = line.State == CraftReply.NoStation || line.State == CraftReply.StationFar;
                row.Back.Set(hover.Is(CraftPart.Row, i), i == _selected[_tab], line.CanMake);
                row.Icon.Set(def.Result, 1);
                row.Icon.Dim(!line.CanMake);
                row.Name.text = UiStrings.ItemName(def.Result);
                row.Name.color = line.CanMake ? UiStyle.TextDark : UiStyle.TextDim;
                row.Note.color = line.CanMake ? UiStyle.TextDark : UiStyle.TextDim;
                row.Note.text = noStation ? UiStrings.StationNeeded(def.Station) : def.ResultCount > 1 ? "× " + def.ResultCount : string.Empty;
            }

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

        static bool StationNear(CraftLine[] lines, int tab)
        {
            int[] list = ByTab[tab];
            if (list.Length == 0)
            {
                return false;
            }

            CraftReply s = lines[list[0]].State;
            return s != CraftReply.NoStation && s != CraftReply.StationFar;
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
                int need = def.IngredientCountOf(k), have = bag.Count(def.IngredientItem(k));
                bool lacks = CraftDesk.Missing(recipe, k, bag) > 0;
                Ingredient ing = _ingredients[k];
                ing.Have.text = $"{have} / {need}";
                ing.Have.color = lacks ? UiStyle.TextBad : UiStyle.TextDark;
                ing.Name.color = lacks ? UiStyle.TextBad : UiStyle.TextDark;
            }

            string state = recipe == 0 ? string.Empty : dead ? UiStrings.CraftDead : UiStrings.CraftState(line.State, def.Station, line.Affordable);
            if (state != _shownState)
            {
                _shownState = state;
                _state.text = state;
            }

            _state.color = line.CanMake ? UiStyle.TextDark : UiStyle.TextBad;
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
