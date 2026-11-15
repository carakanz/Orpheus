#nullable enable
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    // E2-11: лента построек молота (BuildBar, E2-5) убрана — постройки стали предметами и берутся в руку из панели.

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
