using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Изменение набора чанков за один шаг: что вошло и что вышло (окно клиента и активная зона).
    /// Оба списка отсортированы (<see cref="ChunkCoordOrder"/>), чтобы порядок не зависел от вставки.
    /// </summary>
    public readonly struct ChunkSetDelta
    {
        /// <summary>Чанки, вошедшие в набор (их раньше не было).</summary>
        public readonly ChunkCoord[] Added;

        /// <summary>Чанки, вышедшие из набора (правило выхода, гистерезис).</summary>
        public readonly ChunkCoord[] Removed;

        public ChunkSetDelta(ChunkCoord[] added, ChunkCoord[] removed)
        {
            Added = added ?? Array.Empty<ChunkCoord>();
            Removed = removed ?? Array.Empty<ChunkCoord>();
        }

        /// <summary>Сумели набор за этот шаг (ни входа, ни выхода).</summary>
        public readonly bool IsEmpty => Added.Length == 0 && Removed.Length == 0;

        public readonly int AddedCount => Added.Length;
        public readonly int RemovedCount => Removed.Length;

        public override readonly string ToString() =>
            $"+{Added.Length}, -{Removed.Length}";
    }

    /// <summary>
    /// Общая работа с наборами чанков: сбор прямоугольника вокруг чанка (на торе — через
    /// <see cref="Torus.Wrap"/>, каждый чанк один раз) и объединение с памятью набора
    /// в отсортированном порядке (правило ORP006: без обхода словарей и множеств).
    /// </summary>
    internal static class ChunkSetOps
    {
        /// <summary>
        /// Собрать чанки прямоугольника <paramref name="x0"/>…<paramref name="x1"/> × <paramref name="y0"/>…<paramref name="y1"/>
        /// вокруг центра (смещения в чанках) в порядке строк сверху вниз. На торе координаты свёрнуты,
        /// дубли пропускаются: окно шире карты выдаёт каждый чанк один раз. На плоской карте
        /// чанки за краем пропускаются.
        /// </summary>
        public static void Collect(MapShape map, ChunkCoord center, int x0, int x1, int y0, int y1, List<ChunkCoord> into, HashSet<ChunkCoord> seen)
        {
            for (int oy = y0; oy <= y1; oy++)
            {
                for (int ox = x0; ox <= x1; ox++)
                {
                    ChunkCoord c = Torus.Wrap(map, new ChunkCoord(center.X + ox, center.Y + oy));
                    if (map.Wraps)
                    {
                        if (seen.Add(c))
                        {
                            into.Add(c);
                        }
                    }
                    else if (c.X == center.X + ox && c.Y == center.Y + oy)
                    {
                        if (seen.Add(c))
                        {
                            into.Add(c);
                        }
                    }
                }
            }
        }

        /// <summary>Отсортированный набор без дублей из двух списков (порядок внутри списков не важен).</summary>
        public static ChunkCoord[] SortedUnique(List<ChunkCoord> a, List<ChunkCoord> b)
        {
            var all = new ChunkCoord[a.Count + b.Count];
            for (int i = 0; i < a.Count; i++)
            {
                all[i] = a[i];
            }

            for (int i = 0; i < b.Count; i++)
            {
                all[a.Count + i] = b[i];
            }

            Array.Sort(all, ChunkCoordOrder.Instance);

            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (n == 0 || ChunkCoordOrder.Instance.Compare(all[n - 1], all[i]) != 0)
                {
                    all[n++] = all[i];
                }
            }

            Array.Resize(ref all, n);
            return all;
        }

        /// <summary>Элемент отсортированного набора.</summary>
        public static bool Contains(ChunkCoord[] sorted, ChunkCoord c) =>
            Array.BinarySearch(sorted, 0, sorted.Length, c, ChunkCoordOrder.Instance) >= 0;

        /// <summary>Элементы набора <paramref name="memory"/>, которых нет в <paramref name="keep"/> (в исходном, отсортированном порядке).</summary>
        public static ChunkCoord[] NotIn(ChunkCoord[] memory, HashSet<ChunkCoord> keep)
        {
            var outList = new ChunkCoord[memory.Length];
            int n = 0;
            for (int i = 0; i < memory.Length; i++)
            {
                if (!keep.Contains(memory[i]))
                {
                    outList[n++] = memory[i];
                }
            }

            Array.Resize(ref outList, n);
            return outList;
        }

        /// <summary>Элементы набора <paramref name="memory"/>, которые есть в <paramref name="keep"/>.</summary>
        public static List<ChunkCoord> In(ChunkCoord[] memory, HashSet<ChunkCoord> keep)
        {
            var kept = new List<ChunkCoord>(memory.Length);
            for (int i = 0; i < memory.Length; i++)
            {
                if (keep.Contains(memory[i]))
                {
                    kept.Add(memory[i]);
                }
            }

            return kept;
        }
    }
}
