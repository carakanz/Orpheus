using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Сетка сущностей по чанкам (docs/07, 4.4: «принадлежность сущностей — по сетке чанков: поиск в словаре, не
    /// перебор»). Словарь «чанк → первое место в списке сущностей» и цепочки мест внутри чанка по возрастанию (место =
    /// порядок номеров, поэтому обход не зависит от истории переходов, ORP006). Ведёт <see cref="Simulation"/>: сущность
    /// переносится, когда в такте перешла границу чанка. Без выделений памяти: словарь заранее на ёмкость симуляции —
    /// непустых чанков не больше, чем сущностей, а пустой чанк из словаря убирается.
    /// </summary>
    public sealed class EntityChunkGrid
    {
        private readonly Dictionary<ChunkCoord, int> _head;
        private readonly int[] _next;
        private readonly ChunkCoord[] _chunk;

        internal EntityChunkGrid(int capacity)
        {
            _head = new Dictionary<ChunkCoord, int>(capacity);
            _next = new int[capacity];
            _chunk = new ChunkCoord[capacity];
        }

        /// <summary>Непустых чанков.</summary>
        public int ChunkCount => _head.Count;

        /// <summary>Сколько раз сущность перешла в другой чанк (переносы в сетке в тактах).</summary>
        public long Moves { get; private set; }

        /// <summary>Первое (наименьшее) место сущности в чанке <paramref name="chunk"/>; пусто — −1.</summary>
        public int First(ChunkCoord chunk) => _head.TryGetValue(chunk, out int slot) ? slot : -1;

        /// <summary>Следующее место в том же чанке после <paramref name="slot"/>; дальше нет — −1.</summary>
        public int Next(int slot) => _next[slot];

        /// <summary>Чанк, в котором сетка держит сущность на месте <paramref name="slot"/>.</summary>
        public ChunkCoord ChunkOf(int slot) => _chunk[slot];

        /// <summary>Заново по всем сущностям (вне такта: загрузка, удаление сдвигает места).</summary>
        internal void Rebuild(in MapShape map, ReadOnlySpan<SimEntity> entities)
        {
            _head.Clear();
            for (int i = 0; i < entities.Length; i++)
            {
                Insert(i, Torus.ChunkOf(map, entities[i].Mover.Position));
            }
        }

        /// <summary>Сущность на месте <paramref name="slot"/> сейчас в чанке <paramref name="chunk"/>: сменила — перенос.</summary>
        internal void Update(int slot, ChunkCoord chunk)
        {
            if (_chunk[slot] == chunk)
            {
                return;
            }

            Unlink(slot);
            Insert(slot, chunk);
            Moves++;
        }

        /// <summary>Вставить место в цепочку чанка по возрастанию.</summary>
        internal void Insert(int slot, ChunkCoord chunk)
        {
            _chunk[slot] = chunk;
            if (!_head.TryGetValue(chunk, out int head) || slot < head)
            {
                _next[slot] = _head.TryGetValue(chunk, out head) ? head : -1;
                _head[chunk] = slot;
                return;
            }

            int prev = head;
            while (_next[prev] >= 0 && _next[prev] < slot)
            {
                prev = _next[prev];
            }

            _next[slot] = _next[prev];
            _next[prev] = slot;
        }

        private void Unlink(int slot)
        {
            ChunkCoord chunk = _chunk[slot];
            int head = _head[chunk];
            if (head == slot)
            {
                if (_next[slot] < 0)
                {
                    _head.Remove(chunk);
                }
                else
                {
                    _head[chunk] = _next[slot];
                }

                return;
            }

            int prev = head;
            while (_next[prev] != slot)
            {
                prev = _next[prev];
            }

            _next[prev] = _next[slot];
        }
    }
}
