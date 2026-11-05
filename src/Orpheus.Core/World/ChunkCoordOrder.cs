using System.Collections.Generic;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Порядок координат чанка: по Y, затем по X (строками, как лежат тайлы в чанке). Нужен для обхода
    /// по отсортированным ключам (ORP006), поиска в наборах чанков и одинакового порядка байтов в снимке.
    /// </summary>
    public sealed class ChunkCoordOrder : IComparer<ChunkCoord>
    {
        /// <summary>Экземпляр для <c>Array.Sort</c>.</summary>
        public static readonly ChunkCoordOrder Instance = new ChunkCoordOrder();

        /// <summary>Сравнение: Y, затем X.</summary>
        public static int Compare(ChunkCoord a, ChunkCoord b)
        {
            if (a.Y != b.Y)
            {
                return a.Y < b.Y ? -1 : 1;
            }

            return a.X == b.X ? 0 : a.X < b.X ? -1 : 1;
        }

        int IComparer<ChunkCoord>.Compare(ChunkCoord a, ChunkCoord b) => Compare(a, b);
    }
}
