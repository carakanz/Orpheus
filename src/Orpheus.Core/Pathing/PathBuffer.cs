using Orpheus.Core.Geometry;

namespace Orpheus.Core.Pathing
{
    /// <summary>
    /// Результат поиска пути — владеет массивом шагов на всё окно (<see cref="PathFinder.CellCount"/>),
    /// переиспользуется без выделений. Шаги — свёрнутые тайлы мира от первого после старта до конечного.
    /// </summary>
    public sealed class PathBuffer
    {
        /// <summary>Шаги пути; значимы первые <see cref="Length"/>.</summary>
        public readonly TileCoord[] Steps = new TileCoord[PathFinder.CellCount];

        /// <summary>Итог поиска.</summary>
        public PathStatus Status;

        /// <summary>Число шагов.</summary>
        public int Length;

        /// <summary>Стоимость пути (прямой шаг — 10).</summary>
        public int Cost;

        /// <summary>Раскрыто узлов A* (0 — A* не запускался).</summary>
        public int Expanded;

        /// <summary>Конечный тайл пути (старт, если шагов нет).</summary>
        public TileCoord End;

        /// <summary>Такт, в который запрос поставлен в очередь (<see cref="PathScheduler"/>).</summary>
        public long RequestedTick;

        /// <summary>Такт, в который поиск выполнен (<see cref="PathScheduler"/>).</summary>
        public long DoneTick;

        /// <summary>Сбросить итог (шаги не чистятся).</summary>
        public void Clear()
        {
            Status = PathStatus.None;
            Length = 0;
            Cost = 0;
            Expanded = 0;
            End = default;
        }
    }
}
