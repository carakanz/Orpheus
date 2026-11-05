using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Детерминированный порядок координат чанка: сначала X, затем Y. Нужен для обхода по
    /// отсортированным ключам (правило ORP006: обход словаря запрещён), для бинарного поиска
    /// в окне и для одинакового порядка байтов в снимке.
    /// </summary>
    public sealed class ChunkCoordOrder : IComparer<ChunkCoord>
    {
        /// <summary>Экземпляр для <c>Array.Sort</c> и <c>Array.BinarySearch</c>.</summary>
        public static readonly ChunkCoordOrder Instance = new ChunkCoordOrder();

        public int Compare(ChunkCoord a, ChunkCoord b)
        {
            int byX = a.X.CompareTo(b.X);
            return byX != 0 ? byX : a.Y.CompareTo(b.Y);
        }
    }
}
