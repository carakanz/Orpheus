using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Наборы чанков (окно клиента, активная зона) — отсортированные по <see cref="ChunkCoordOrder"/> массивы
    /// без дублей. Сортированный порядок не зависит от истории (ORP006) и даёт дешёвые поиск и разность.
    /// Все методы без выделений памяти.
    /// </summary>
    public static class ChunkSets
    {
        /// <summary>Есть ли чанк в отсортированном наборе (двоичный поиск).</summary>
        public static bool Contains(ReadOnlySpan<ChunkCoord> sorted, ChunkCoord c)
        {
            int lo = 0;
            int hi = sorted.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int cmp = ChunkCoordOrder.Compare(sorted[mid], c);
                if (cmp == 0)
                {
                    return true;
                }

                if (cmp < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return false;
        }

        /// <summary>Отсортировать набор вставками (наборы — до сотен чанков и почти отсортированы).</summary>
        public static void Sort(Span<ChunkCoord> set)
        {
            for (int i = 1; i < set.Length; i++)
            {
                ChunkCoord v = set[i];
                int j = i - 1;
                while (j >= 0 && ChunkCoordOrder.Compare(set[j], v) > 0)
                {
                    set[j + 1] = set[j];
                    j--;
                }

                set[j + 1] = v;
            }
        }

        /// <summary>
        /// Разность двух отсортированных наборов: что вошло (есть в <paramref name="after"/>, нет в <paramref name="before"/>)
        /// и что вышло. Сервер шлёт вошедшие чанки, клиент выбрасывает вышедшие.
        /// </summary>
        public static void Diff(
            ReadOnlySpan<ChunkCoord> before, ReadOnlySpan<ChunkCoord> after,
            Span<ChunkCoord> added, out int addedCount, Span<ChunkCoord> removed, out int removedCount)
        {
            addedCount = 0;
            removedCount = 0;
            int i = 0;
            int j = 0;
            while (i < before.Length || j < after.Length)
            {
                int cmp = i == before.Length ? 1 : j == after.Length ? -1 : ChunkCoordOrder.Compare(before[i], after[j]);
                if (cmp == 0)
                {
                    i++;
                    j++;
                }
                else if (cmp < 0)
                {
                    removed[removedCount++] = before[i++];
                }
                else
                {
                    added[addedCount++] = after[j++];
                }
            }
        }
    }
}
