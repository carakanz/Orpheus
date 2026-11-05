using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Активная зона (= docs/07 3.8): вход - чанки входят зоной в 7×7 вокруг чанка
    /// каждого игрока; выход - чанк выпадает, если он дальше 9×9 от всех игроков
    /// (дальше 4 чанков по X или Y). Окно каждого игрока = блок 8×6 (окно чанков),
    /// пропущенный через фильтр 9×9; на нескольких игроках - объединение.
    /// Сервер считает активную зону сам (ядро общее) и применяет её к окну
    /// при выдаче клиенту. Окно на игрока - не больше 48 чанков.
    /// </summary>
    public static class ActiveZone
    {
        /// <summary>Радиус входа: соседство 7×7 (= docs/07 3.13).</summary>
        public const int EnterRadius = 3;

        /// <summary>Радиус выхода: чанк выпадет, если дальше 4 чанков по любой оси (блок 9×9).</summary>
        public const int ExitRadius = 4;

        /// <summary>
        /// Итоговый набор чанков для выдачи: для каждого игрока его блок 8×6,
        /// пропущенный через фильтр 9×9 всех игроков; объединение без дублей.
        /// </summary>
        public static List<ChunkCoord> Compute(MapShape map, IReadOnlyList<TileCoord> players)
        {
            var result = new List<ChunkCoord>();
            var seen = new HashSet<ChunkCoord>();
            for (int i = 0; i < players.Count; i++)
            {
                List<ChunkCoord> block = ChunkWindow.Compute(map, players[i]);
                for (int j = 0; j < block.Count; j++)
                {
                    ChunkCoord c = block[j];
                    if (!seen.Add(c))
                    {
                        continue;
                    }
                    if (IsInZoneOfAnyPlayer(c, players, map))
                    {
                        result.Add(c);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Чанк в активной зоне, если отстоит не дальше 4 чанков по X и по Y
        /// хотя бы от одного игрока (блок 9×9 вокруг его чанка).
        /// </summary>
        public static bool IsInZoneOfAnyPlayer(ChunkCoord chunk, IReadOnlyList<TileCoord> players, MapShape map)
        {
            for (int i = 0; i < players.Count; i++)
            {
                ChunkCoord center = Torus.TileOf(players[i], map);
                Torus.Delta(center, chunk, map, out int dx, out int dy);
                if (Math.Abs(dx) <= ExitRadius && Math.Abs(dy) <= ExitRadius)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
