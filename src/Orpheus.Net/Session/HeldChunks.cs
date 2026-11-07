using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Сервер: чанки, которые есть у клиента, и их ревизии (docs/07, 3.5 «Вход в окно», 3.7; 0-026). Набор равен окну
    /// клиента (<see cref="ChunkWindow"/>): сервер добавляет чанк, отправив его, и убирает, отправив новый центр окна.
    /// Не больше <see cref="ChunkWindow.MaxChunks"/> = 48; без выделений после создания.
    /// </summary>
    internal sealed class HeldChunks
    {
        public readonly ChunkCoord[] Coords = new ChunkCoord[ChunkWindow.MaxChunks];
        public readonly uint[] Revisions = new uint[ChunkWindow.MaxChunks];
        public int Count;

        /// <summary>Место чанка в наборе; нет — −1.</summary>
        public int IndexOf(ChunkCoord c)
        {
            for (int i = 0; i < Count; i++)
            {
                if (Coords[i] == c)
                {
                    return i;
                }
            }

            return -1;
        }

        public void Add(ChunkCoord c, uint revision)
        {
            if (Count == Coords.Length)
            {
                throw new InvalidOperationException($"У клиента больше {ChunkWindow.MaxChunks} чанков — окно сломано.");
            }

            Coords[Count] = c;
            Revisions[Count] = revision;
            Count++;
        }

        /// <summary>Оставить только чанки из <paramref name="window"/> (отсортирован, <see cref="ChunkSets"/>). Возвращает число убранных.</summary>
        public int RetainIn(ReadOnlySpan<ChunkCoord> window)
        {
            int n = 0;
            for (int i = 0; i < Count; i++)
            {
                if (ChunkSets.Contains(window, Coords[i]))
                {
                    Coords[n] = Coords[i];
                    Revisions[n] = Revisions[i];
                    n++;
                }
            }

            int removed = Count - n;
            Count = n;
            return removed;
        }
    }
}
