#nullable enable
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.UI;
using Material = Orpheus.Core.Sim.Material;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// Лента построек режима стройки (E2-5, `05` 1.3 «палитра»): видна, пока в руках молот. Над быстрой панелью —
    /// <see cref="Visible"/> = 7 карточек вокруг выбранной (лента по кругу, Q / E): значок постройки, название, цена
    /// «есть / нужно» со значками материалов (не хватает — красным). Выбранная — охряная рамка, крупнее. Данные — из
    /// <see cref="BuildDefs.Palette"/> и сумки сервера через <see cref="BuildHand.CostLine"/>; своих правил нет.
    /// </summary>
    public sealed class BuildBar : UiGroup
    {
        /// <summary>Карточек на экране.</summary>
        public const int Shown = 7;

        public const float CardW = 168f, CardH = 132f, Gap = 8f;
        public static readonly float Width = Shown * CardW + (Shown - 1) * Gap + 2f * 64f;
        public static readonly float Height = CardH + 40f;

        sealed class Card
        {
            public RectTransform Rt = null!;
            public Image Back = null!;
            public Image Icon = null!;
            public Text Name = null!;
            public readonly Image[] Mat = new Image[2];
            public readonly Text[] Cost = new Text[2];
        }

        readonly Card[] _cards = new Card[Shown];
        readonly Text _keys;
        readonly Sprite _back, _backSel, _backOff;

        public BuildBar(UiRoot root, float lift)
            : base(root, "BuildBar", new Vector2(0.5f, 0f), new Vector2(-Width / 2f, lift), new Vector2(Width, Height))
        {
            _back = UiStyle.Frame(UiStyle.Parchment, UiStyle.Ink, thick: 3f, radius: 8f);
            _backSel = UiStyle.Frame(UiStyle.CellHover, UiStyle.Ochre, thick: 5f, radius: 8f);
            _backOff = UiStyle.Frame(UiStyle.CellOff, UiStyle.InkSoft, thick: 3f, radius: 8f);
            UiRoot.Label(Rt, "Q", 0f, 40f, 60f, CardH, 30, TextAnchor.MiddleCenter, UiStyle.TextLight, outline: true, bold: true).text = "Q";
            UiRoot.Label(Rt, "E", Width - 60f, 40f, 60f, CardH, 30, TextAnchor.MiddleCenter, UiStyle.TextLight, outline: true, bold: true).text = "E";
            for (int i = 0; i < Shown; i++)
            {
                var c = new Card();
                float x = 64f + i * (CardW + Gap);
                c.Rt = UiRoot.Node(Rt, "Card" + i, x, 40f, CardW, CardH);
                c.Back = UiRoot.Picture(c.Rt, "Back", 0f, 0f, CardW, CardH, _back);
                c.Icon = UiRoot.Picture(c.Rt, "Icon", (CardW - 56f) / 2f, CardH - 62f, 56f, 56f, null, sliced: false);
                c.Icon.preserveAspect = true;
                c.Name = UiRoot.Label(c.Rt, "Name", 4f, 38f, CardW - 8f, 26f, 18, TextAnchor.MiddleCenter, UiStyle.TextDark, bold: true);
                for (int m = 0; m < 2; m++)
                {
                    float mx = 6f + m * (CardW / 2f - 2f);
                    c.Mat[m] = UiRoot.Picture(c.Rt, "Mat" + m, mx, 6f, 28f, 28f, null, sliced: false);
                    c.Cost[m] = UiRoot.Label(c.Rt, "Cost" + m, mx + 28f, 4f, CardW / 2f - 34f, 30f, 18, TextAnchor.MiddleLeft, UiStyle.TextDark);
                }

                _cards[i] = c;
            }

            _keys = UiRoot.Label(Rt, "Keys", 0f, 4f, Width, 32f, 20, TextAnchor.MiddleCenter, UiStyle.TextLight, outline: true);
            _keys.text = UiStrings.BuildKeys;
            Visible = false;
        }

        /// <summary>Показать ленту: выбранная постройка в середине, цены по сумке <paramref name="bag"/>.</summary>
        public void Show(int selected, in Inventory bag, bool dead)
        {
            System.ReadOnlySpan<ushort> palette = BuildDefs.Palette;
            int n = palette.Length;
            for (int i = 0; i < Shown; i++)
            {
                Card c = _cards[i];
                int at = ((selected + i - Shown / 2) % n + n) % n;
                ushort key = palette[at];
                bool sel = i == Shown / 2;
                BuildDefs.TryGet(key, out BuildDef def);
                bool can = BuildDefs.Affordable(def, bag) && !dead;
                c.Back.sprite = sel ? _backSel : can ? _back : _backOff;
                c.Icon.sprite = BuildLook.Icon(key);
                c.Icon.enabled = c.Icon.sprite != null;
                c.Icon.color = can ? Color.white : new Color(1f, 1f, 1f, 0.5f);
                c.Name.text = UiStrings.BuildName(key);
                c.Name.color = can ? UiStyle.TextDark : UiStyle.TextDim;
                for (int m = 0; m < 2; m++)
                {
                    (Material mat, int have, int need) = BuildHand.CostLine(key, m, bag);
                    bool on = need > 0;
                    c.Mat[m].enabled = on;
                    c.Cost[m].enabled = on;
                    if (!on)
                    {
                        continue;
                    }

                    c.Mat[m].sprite = PileView.SpriteFor(ItemTable.OfMaterial(mat));
                    c.Cost[m].text = $"{have}/{need}";
                    c.Cost[m].color = have >= need ? UiStyle.TextDark : UiStyle.TextBad;
                }
            }
        }
    }

    /// <summary>
    /// Окно сундука (E2-5): 6 × 5 ячеек (30, `06` 5.3), рядом с сумкой. Ячейки в номерах «руки» — 40…69
    /// (<see cref="BagHand.ChestBase"/>): перенос, половина, Shift — общим путём <see cref="BagHand"/>.
    /// </summary>
    public sealed class ChestWindow : UiWindow
    {
        const int Columns = 6;
        const float HintHeight = 46f;
        const float GridY = Pad + HintHeight + 10f;
        static readonly int Rows = Simulation.ChestCells / Columns;
        public static readonly float Width = UiSlotGrid.WidthOf(Columns) + 2f * Pad;
        public static readonly float Height = GridY + UiSlotGrid.WidthOf(Rows) + 12f + TitleHeight + Pad;

        readonly UiSlotGrid _grid;

        public ChestWindow(UiRoot root)
            : base(root, "Chest", UiStrings.ChestTitle, new Vector2(0.5f, 0.5f), new Vector2(-Width / 2f, -Height / 2f), new Vector2(Width, Height))
        {
            _grid = new UiSlotGrid(Rt, Pad, GridY, Columns, Rows, BagHand.ChestBase, UiStyle.Cell);
            UiRoot.Label(Rt, "Hint", Pad, Pad - 2f, Width - 2f * Pad, HintHeight, 16, TextAnchor.MiddleLeft, UiStyle.TextDark).text = UiStrings.ChestHint;
            UiRoot.Label(Rt, "Key", Pad, Height - Pad - TitleHeight, Width - 2f * Pad, TitleHeight, 16, TextAnchor.MiddleRight, UiStyle.TextDark)
                .text = UiStrings.ChestKeys;
        }

        /// <summary>Ячейка руки (40…69) под точкой окна; мимо — −1.</summary>
        public int CellAt(Vector2 local)
        {
            int i = _grid.Hit(local);
            return i >= 0 ? BagHand.ChestBase + i : -1;
        }

        /// <summary>Середина ячейки руки в координатах окна (скрипт стенда снимков).</summary>
        public Vector2 CellCenter(int cell)
        {
            int i = cell - BagHand.ChestBase;
            float half = UiStyle.CellSize / 2f;
            return new Vector2(Pad + i % Columns * UiSlotGrid.Step + half, GridY + (Rows - 1 - i / Columns) * UiSlotGrid.Step + half);
        }

        /// <summary>Показать сундук через руку (числа без того, что «в руке»), подсветка ячейки под курсором.</summary>
        public void Show(BagHand hand, int hover)
        {
            for (int i = 0; i < Simulation.ChestCells; i++)
            {
                int cell = BagHand.ChestBase + i;
                _grid.Set(i, hand.ItemAt(cell), hand.ShownCount(cell));
            }

            _grid.Hover(hover >= BagHand.ChestBase ? hover - BagHand.ChestBase : -1);
        }
    }
}
