using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Обход ячеек линии удара (C-023; вынесено из <c>Simulation.Combat.cs</c> в C-028 — предел 500 строк) и линия взмаха кирки.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>
        /// C-028 (проверка П): линия взмаха кирки «точка персонажа → центр тайла <paramref name="target"/>» чиста — тем же обходом
        /// ячеек 0,5, что у меча (<see cref="FirstBlockedCellStep"/>: стена, закрытая дверь, обрыв, дерево — преграда, угол к углу —
        /// обе ячейки). Тайлы, для которых <paramref name="pass"/> — да (камни, которые эта кирка бьёт, и сама цель), прозрачны:
        /// удар по коридору задевает 1-й и 2-й камень. Тайл персонажа не проверяется. Только чтение.
        /// </summary>
        public bool PickLineClear(WorldPos from, TileCoord target, Func<TileCoord, bool> pass)
        {
            WorldPos to = EditRules.CenterOf(Torus.Wrap(_shape, target));
            Int2 d = Torus.Delta(_shape, from, to);
            int span = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
            int n = Math.Max(1, (span + StrikeLosStepUnits - 1) / StrikeLosStepUnits);
            return FirstBlockedCellStep(from, d, n, n, withStart: false, pass) > n;
        }

        /// <summary>
        /// Первая точка <paramref name="k"/> (1…<paramref name="last"/>) на отрезке старт + d·k/n в твёрдой
        /// ячейке 0,5 × 0,5 (128 unit): <see cref="CellSolidForStrike"/>. Точки через ≤ 1 ячейку по каждой оси
        /// — ячейка не перепрыгивается; ячейка смотрится при смене. Переход угол к углу (стык двух стен
        /// «лесенкой», диагональный) проверяет обе промежуточные ячейки — любая твёрдая — преграда
        /// (фикс E3-2 для стрелы; C-023 — та же дыра у линии удара). <paramref name="withStart"/> — проверять
        /// и начальную точку. Возвращает номер точки ≤ last; last + 1 — преграды нет.
        /// </summary>
        private int FirstBlockedCellStep(WorldPos start, Int2 d, int n, int last, bool withStart, Func<TileCoord, bool>? pass = null)
        {
            const int CellShift = Torus.SubTileBits - 1; // ячейка 0,5 тайла = 128 unit
            int cx = start.X >> CellShift, cy = start.Y >> CellShift;
            if (withStart && CellSolidForStrike(cx, cy, pass))
            {
                return 0;
            }

            for (int k = 1; k <= last; k++)
            {
                int nx = (start.X + (int)((long)d.X * k / n)) >> CellShift; // арифметический сдвиг — пол и для отрицательных
                int ny = (start.Y + (int)((long)d.Y * k / n)) >> CellShift;
                if (nx == cx && ny == cy)
                {
                    continue;
                }

                if (nx != cx && ny != cy && (CellSolidForStrike(nx, cy, pass) || CellSolidForStrike(cx, ny, pass)))
                {
                    return k; // угол к углу: любая из промежуточных твёрдая — стык закрыт
                }

                if (CellSolidForStrike(nx, ny, pass))
                {
                    return k;
                }

                cx = nx;
                cy = ny;
            }

            return last + 1;
        }

        /// <summary>Ячейка 0,5 (номер по сетке 128 unit, без обёртки) твёрдая для удара и стрелы.</summary>
        private bool CellSolidForStrike(int cellX, int cellY, Func<TileCoord, bool>? pass = null)
        {
            TileCoord tile = Torus.Wrap(_shape, new TileCoord(cellX >> 1, cellY >> 1));
            if (!StrikeTileBlocked(tile) || (pass != null && pass(tile)))
            {
                return false;
            }

            const int half = Torus.SubTilesPerTile / 2;
            return StrikeCellSolid(tile, new WorldPos(tile.X * Torus.SubTilesPerTile + (cellX & 1) * half + half / 2,
                tile.Y * Torus.SubTilesPerTile + (cellY & 1) * half + half / 2));
        }

    }
}
