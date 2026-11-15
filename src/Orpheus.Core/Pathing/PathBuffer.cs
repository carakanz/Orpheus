using Orpheus.Core.Geometry;

namespace Orpheus.Core.Pathing
{
    /// <summary>
    /// Результат поиска пути — владеет массивом точек на наибольший путь (<see cref="PathFinder.MaxExpandedLimit"/>),
    /// переиспользуется без выделений. Точки — точки решётки через 0,5 тайла (E2-12b), свёрнутые позиции мира в 1/256
    /// тайла, от первой после старта до конечной; соседние точки отстоят на полшага по прямой или диагонали.
    /// </summary>
    public sealed class PathBuffer
    {
        /// <summary>Точки пути; значимы первые <see cref="Length"/>.</summary>
        public readonly WorldPos[] Points = new WorldPos[PathFinder.MaxExpandedLimit];

        /// <summary>Итог поиска.</summary>
        public PathStatus Status;

        /// <summary>Число точек.</summary>
        public int Length;

        /// <summary>Стоимость пути (прямой шаг на тайл — 10).</summary>
        public int Cost;

        /// <summary>Раскрыто узлов A* (0 — A* не запускался).</summary>
        public int Expanded;

        /// <summary>Тайл конечной точки пути (тайл старта, если точек нет).</summary>
        public TileCoord End;

        /// <summary>Конечная точка пути (точка старта, если точек нет).</summary>
        public WorldPos EndPoint;

        /// <summary>Такт, в который запрос поставлен в очередь (<see cref="PathScheduler"/>).</summary>
        public long RequestedTick;

        /// <summary>Такт, в который поиск выполнен (<see cref="PathScheduler"/>).</summary>
        public long DoneTick;

        /// <summary>Сбросить итог (точки не чистятся).</summary>
        public void Clear()
        {
            Status = PathStatus.None;
            Length = 0;
            Cost = 0;
            Expanded = 0;
            End = default;
            EndPoint = default;
        }
    }
}
