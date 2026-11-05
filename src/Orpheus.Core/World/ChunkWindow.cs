using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Окно чанков, которое сервер отдаёт клиенту (= docs/07 3.7).
    /// Вход 6×4 (24 чанка) вокруг чанка игрока, выход = вход + рамка в 1 чанк
    /// с каждой стороны = блок 8×6 (48 чанков). Блок смещается на 1 чанк
    /// (гистерезис): игрок во второй половине своего чанка — блок сдвинут на −1,
    /// в первой половине — без сдвига, поэтому окно не перещёлкивается на каждом шаге.
    /// Всё, что уходит клиенту, лежит внутри блока 8×6, включая швы тора;
    /// дублей нет (свёртка <see cref="Torus.Wrap"/>), размер ≤ 48.
    /// </summary>
    public static class ChunkWindow
    {
        /// <summary>Ширина блока выхода: 6 + рамка по边у = 8 чанков.</summary>
        public const int OutputWidth = 8;

        /// <summary>Высота блока выхода: 4 + рамка по边у = 6 чанков.</summary>
        public const int OutputHeight = 6;

        /// <summary>Край блока выхода (8×6) относительно чанка игрока без сдвига.</summary>
        private const int MinDX = -3;
        private const int MaxDX = 4;
        private const int MinDY = -2;
        private const int MaxDY = 3;

        /// <summary>
        /// Окно для одного игрока: блок 8×6 вокруг его чанка со сдвигом 0/−1
        /// (игрок во второй половине чанка — сдвиг −1). Порядок — по строкам сверху вниз.
        /// </summary>
        public static List<ChunkCoord> Compute(MapShape map, TileCoord playerTile)
        {
            ChunkCoord baseChunk = Torus.ChunkOf(map, playerTile);
            Int2 rel = Torus.Delta(map, Torus.CornerOf(baseChunk), playerTile);
            int shiftX = rel.X >= Torus.ChunkSize / 2 ? -1 : 0;
            int shiftY = rel.Y >= Torus.ChunkSize / 2 ? -1 : 0;

            var list = new List<ChunkCoord>(OutputWidth * OutputHeight);
            for (int dy = MinDY; dy <= MaxDY; dy++)
            {
                for (int dx = MinDX; dx <= MaxDX; dx++)
                {
                    list.Add(Torus.Wrap(map, new ChunkCoord(baseChunk.X + dx + shiftX, baseChunk.Y + dy + shiftY)));
                }
            }

            return list;
        }

        /// <summary>Вход окна: блок 6×4 вокруг чанка игрока (24 чанка).</summary>
        public static List<ChunkCoord> Input6x4(MapShape map, ChunkCoord center)
        {
            var list = new List<ChunkCoord>(24);
            for (int dy = -1; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 3; dx++)
                {
                    list.Add(Torus.Wrap(map, new ChunkCoord(center.X + dx, center.Y + dy)));
                }
            }

            return list;
        }
    }
}
