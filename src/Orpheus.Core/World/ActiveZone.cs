using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Активная зона сервера (docs/07, 3.5): чанки, которые сервер симулирует.
    /// <list type="bullet">
    /// <item>чанк входит, когда попал в квадрат 7 × 7 чанков вокруг чанка любого игрока;</item>
    /// <item>выходит, когда вышел из квадрата 9 × 9 у всех игроков (гистерезис, как у окна).</item>
    /// </list>
    /// Арена босса, база во время ивента и отдельные карты (тоже из 3.5) — карточки боя и ивентов.
    /// На торе координаты свёрнуты; квадрат шире карты выдаёт каждый чанк один раз. Без выделений памяти
    /// (до <see cref="StackPlayers"/> игроков).
    /// </summary>
    public static class ActiveZone
    {
        /// <summary>Радиус входа: квадрат 7 × 7.</summary>
        public const int EnterRadius = 3;

        /// <summary>Радиус выхода: квадрат 9 × 9.</summary>
        public const int ExitRadius = 4;

        /// <summary>Чанков во входном квадрате одного игрока: 49.</summary>
        public const int EnterChunks = (2 * EnterRadius + 1) * (2 * EnterRadius + 1);

        /// <summary>Чанков в выходном квадрате одного игрока: 81.</summary>
        public const int ExitChunks = (2 * ExitRadius + 1) * (2 * ExitRadius + 1);

        /// <summary>До скольких игроков чанки игроков лежат на стеке.</summary>
        public const int StackPlayers = 64;

        /// <summary>Наибольший размер зоны: 81 на игрока, но не больше всей карты.</summary>
        public static int MaxChunks(in MapShape map, int players)
        {
            long max = (long)ExitChunks * players;
            long total = (long)map.ChunksX * map.ChunksY;
            return (int)(max < total ? max : total);
        }

        /// <summary>
        /// Новая зона: (текущая ∩ ⋃ 9 × 9) ∪ ⋃ 7 × 7 по всем игрокам. <paramref name="current"/> — отсортированный набор
        /// из прошлого вызова (<see cref="ChunkSets"/>); результат в <paramref name="next"/> тоже отсортирован.
        /// Возвращает число чанков. Нет игроков — зона пуста.
        /// </summary>
        public static int Update(in MapShape map, ReadOnlySpan<WorldPos> players, ReadOnlySpan<ChunkCoord> current, Span<ChunkCoord> next)
        {
            int need = MaxChunks(map, players.Length);
            if (next.Length < need)
            {
                throw new ArgumentException($"Нужно место на {need} чанков, есть {next.Length}.", nameof(next));
            }

            Span<ChunkCoord> centers = players.Length <= StackPlayers
                ? stackalloc ChunkCoord[players.Length]
                : new ChunkCoord[players.Length];
            for (int i = 0; i < players.Length; i++)
            {
                centers[i] = Torus.ChunkOf(map, players[i]);
            }

            // Остаются чанки текущей зоны внутри 9 × 9 хоть одного игрока.
            int n = 0;
            for (int i = 0; i < current.Length; i++)
            {
                if (Within(map, centers, centers.Length, current[i], ExitRadius))
                {
                    next[n++] = current[i];
                }
            }

            // Входят чанки 7 × 7 каждого игрока, которых не было; чанк из квадрата более раннего игрока уже учтён.
            int kept = n;
            Span<ChunkCoord> square = stackalloc ChunkCoord[EnterChunks];
            for (int p = 0; p < centers.Length; p++)
            {
                int count = Torus.NeighborChunks(map, centers[p], EnterRadius, square);
                for (int k = 0; k < count; k++)
                {
                    ChunkCoord c = square[k];
                    if (!ChunkSets.Contains(next.Slice(0, kept), c) && !Within(map, centers, p, c, EnterRadius))
                    {
                        next[n++] = c;
                    }
                }
            }

            ChunkSets.Sort(next.Slice(0, n));
            return n;
        }

        /// <summary>Чанк внутри квадрата (2r + 1)² вокруг хоть одного из первых <paramref name="count"/> центров.</summary>
        private static bool Within(in MapShape map, ReadOnlySpan<ChunkCoord> centers, int count, ChunkCoord c, int radius)
        {
            for (int i = 0; i < count; i++)
            {
                Int2 d = Torus.Delta(map, centers[i], c);
                if (d.X >= -radius && d.X <= radius && d.Y >= -radius && d.Y <= radius)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
