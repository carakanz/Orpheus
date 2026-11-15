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
    /// Окно сундука (E2-5 → C-008, как в Terraria): под сумкой сетка 6 × 5 (30, `06` 5.3) полупрозрачных ячеек другого
    /// оттенка, справа — кнопки «Взять всё» / «Положить всё» (пачка обычных заявок переноса — <see cref="BagHand.TakeAll"/>,
    /// <see cref="BagHand.PutAll"/>). Ячейки в номерах «руки» — 40…69 (<see cref="BagHand.ChestBase"/>).
    /// </summary>
    public sealed class ChestWindow : UiGroup
    {
        const int Columns = 6;
        const float TitleH = 26f, Gap = 4f, ButtonW = 156f, ButtonH = 38f, ButtonGap = 10f;
        static readonly int Rows = Simulation.ChestCells / Columns;
        static readonly float GridW = UiSlotGrid.WidthOf(Columns);
        static readonly float GridH = UiSlotGrid.WidthOf(Rows);
        public static readonly float Width = GridW + ButtonGap + ButtonW;
        public static readonly float Height = GridH + Gap + TitleH;

        readonly UiSlotGrid _grid;
        readonly UiButton _take, _put;
        readonly Text _title;

        public ChestWindow(UiRoot root)
            : base(root, "Chest", new Vector2(0f, 1f), new Vector2(20f, -Height - 400f), new Vector2(Width, Height))
        {
            _grid = new UiSlotGrid(Rt, 0f, 0f, Columns, Rows, BagHand.ChestBase, UiCellLook.Chest);
            _title = UiRoot.Label(Rt, "Title", 2f, GridH + Gap, GridW + ButtonGap + ButtonW, TitleH, 20, TextAnchor.MiddleLeft, UiStyle.TextLight, outline: true, bold: true);
            _title.text = UiStrings.ChestTitleKeys;
            _take = new UiButton(Rt, GridW + ButtonGap, GridH - ButtonH, ButtonW, ButtonH, UiStrings.ChestTakeAll, 18, glass: true);
            _put = new UiButton(Rt, GridW + ButtonGap, GridH - 2f * ButtonH - 8f, ButtonW, ButtonH, UiStrings.ChestPutAll, 18, glass: true);
            Visible = false;
        }

        /// <summary>Ячейка руки (40…69) под точкой окна; мимо — −1.</summary>
        public int CellAt(Vector2 local)
        {
            int i = _grid.Hit(local);
            return i >= 0 ? BagHand.ChestBase + i : -1;
        }

        /// <summary>Кнопка под точкой окна: 0 — «Взять всё», 1 — «Положить всё», −1 — нет.</summary>
        public int ButtonAt(Vector2 local) => _take.Contains(local) ? 0 : _put.Contains(local) ? 1 : -1;

        /// <summary>Середина кнопки (скрипт стенда снимков).</summary>
        public Vector2 ButtonCenter(int k) => k == 0 ? _take.Center : _put.Center;

        /// <summary>Курсор над сеткой или кнопкой (колесо и Shift в игру не уходят).</summary>
        public bool Covers(Vector2 local) => Visible && (_grid.Covers(local) || ButtonAt(local) >= 0);

        /// <summary>C-008: сетка и кнопки — интерфейс; остальное — мир.</summary>
        public void AddZones(UiPointer p, Vector2 canvas)
        {
            Zone(p, canvas, 0f, 0f, GridW, GridH);
            Zone(p, canvas, GridW + ButtonGap, GridH - 2f * ButtonH - 8f, ButtonW, 2f * ButtonH + 8f);
        }

        /// <summary>Середина ячейки руки в координатах окна (скрипт стенда снимков).</summary>
        public Vector2 CellCenter(int cell) => _grid.CenterOf(cell - BagHand.ChestBase);

        /// <summary>C-008: короткая причина остановки «Взять / Положить всё» вместо заголовка; null — заголовок.</summary>
        public void Note(string? text)
        {
            string t = text ?? UiStrings.ChestTitleKeys;
            if (_title.text != t)
            {
                _title.text = t;
                _title.color = text != null ? new Color32(255, 120, 100, 255) : UiStyle.TextLight;
            }
        }

        /// <summary>Показать сундук через руку (числа без того, что «в руке»), подсветка ячейки и кнопки под курсором.</summary>
        public void Show(BagHand hand, int hover, int button)
        {
            for (int i = 0; i < Simulation.ChestCells; i++)
            {
                int cell = BagHand.ChestBase + i;
                _grid.Set(i, hand.ItemAt(cell), hand.ShownCount(cell));
            }

            _grid.Hover(hover >= BagHand.ChestBase ? hover - BagHand.ChestBase : -1);
            _take.Set(button == 0, false, !hand.Dead);
            _put.Set(button == 1, false, !hand.Dead);
        }
    }
}
