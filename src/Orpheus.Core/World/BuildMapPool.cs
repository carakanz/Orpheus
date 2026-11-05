using System;
using System.Collections.Generic;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Запас словарей записей построек для чанков (backlog [такт сервера]: «первая постройка в чанке
    /// выделяет 4 КБ»). Словарь чанка создаётся при первой постройке в нём — то есть в такте, в фазе правок.
    /// Пул выдаёт готовый словарь на <see cref="Capacity"/> записей (без роста до этого числа), а пополняется
    /// вне такта (<see cref="Reserve"/>; сервер — между тактами). Пул пуст — словарь создаётся как раньше:
    /// правка не теряется, только выделение попадает в такт.
    /// </summary>
    public sealed class BuildMapPool
    {
        /// <summary>
        /// Записей в словаре без роста: 128 — комната 10 × 10 с двором и мебелью в одном чанке
        /// *(прикидка; ≈ 3,5 КБ на словарь)*.
        /// </summary>
        public const int DefaultCapacity = 128;

        private readonly Stack<Dictionary<ushort, BuildRecord>> _free = new Stack<Dictionary<ushort, BuildRecord>>();

        public BuildMapPool(int capacity = DefaultCapacity)
        {
            if (capacity <= 0 || capacity > Chunk.TileCount)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, $"Ёмкость словаря — 1…{Chunk.TileCount}.");
            }

            Capacity = capacity;
        }

        /// <summary>Записей в выданном словаре без роста.</summary>
        public int Capacity { get; }

        /// <summary>Готовых словарей в запасе.</summary>
        public int Available => _free.Count;

        /// <summary>Сколько словарей пул выдал сверх запаса (выделение в такте — для метрик).</summary>
        public int Misses { get; private set; }

        /// <summary>Дополнить запас до <paramref name="count"/> словарей. Вне такта: здесь выделяется память.</summary>
        public void Reserve(int count)
        {
            while (_free.Count < count)
            {
                _free.Push(new Dictionary<ushort, BuildRecord>(Capacity));
            }
        }

        internal Dictionary<ushort, BuildRecord> Rent()
        {
            if (_free.Count > 0)
            {
                return _free.Pop();
            }

            Misses++;
            return new Dictionary<ushort, BuildRecord>(Capacity);
        }
    }
}
