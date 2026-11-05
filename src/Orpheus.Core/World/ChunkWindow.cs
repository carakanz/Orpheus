using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Окно чанков у клиента (docs/07, 3.5): чанки, которые есть у клиента.
    /// <list type="bullet">
    /// <item><b>Вход:</b> блок 6 × 4 чанка, центр которого ближе всего к персонажу. Центр блока чётной ширины —
    /// граница чанков, поэтому центр — ближайшая к персонажу граница по каждой оси. От персонажа до края
    /// блока — не меньше 2,5 чанка (80 тайлов) по X и 1,5 чанка (48 тайлов) по Y.</item>
    /// <item><b>Выход:</b> чанк выбрасывается, когда вышел из блока 8 × 6 с тем же центром (на чанк шире
    /// с каждой стороны) — гистерезис: бег вдоль границы чанков не гоняет одни и те же чанки.</item>
    /// </list>
    /// Окно никогда не выходит из блока 8 × 6, поэтому в нём не больше 48 чанков (приёмка 3.13 п. 9).
    /// На торе координаты свёрнуты; окно шире карты выдаёт каждый чанк один раз. На плоской карте
    /// чанки за краем пропускаются. Без выделений памяти.
    /// </summary>
    public static class ChunkWindow
    {
        /// <summary>Ширина блока входа, чанков.</summary>
        public const int Width = 6;

        /// <summary>Высота блока входа, чанков.</summary>
        public const int Height = 4;

        /// <summary>Ширина блока выхода, чанков.</summary>
        public const int KeepWidth = 8;

        /// <summary>Высота блока выхода, чанков.</summary>
        public const int KeepHeight = 6;

        /// <summary>Наибольший размер окна: 8 × 6 = 48 чанков.</summary>
        public const int MaxChunks = KeepWidth * KeepHeight;

        /// <summary>
        /// Центр блока — ближайшая к персонажу граница чанков (номер чанка справа и снизу от неё).
        /// Блок входа — чанки [cx − 3, cx + 2] × [cy − 2, cy + 1].
        /// </summary>
        public static ChunkCoord BlockCenter(in MapShape map, WorldPos character)
        {
            const int half = (Torus.ChunkSize / 2) << Torus.SubTileBits;
            const int shift = Torus.ChunkBits + Torus.SubTileBits;
            WorldPos p = Torus.Wrap(map, character);
            var center = new ChunkCoord((p.X + half) >> shift, (p.Y + half) >> shift);
            return map.Wraps ? Torus.Wrap(map, center) : center;
        }

        /// <summary>
        /// Новое окно по позиции персонажа и текущему окну: (текущее ∩ блок 8 × 6) ∪ блок 6 × 4.
        /// <paramref name="current"/> — отсортированный набор (<see cref="ChunkSets"/>), как его вернул прошлый вызов;
        /// результат в <paramref name="next"/> (≥ <see cref="MaxChunks"/>) тоже отсортирован. Возвращает число чанков.
        /// </summary>
        public static int Update(in MapShape map, WorldPos character, ReadOnlySpan<ChunkCoord> current, Span<ChunkCoord> next)
        {
            if (next.Length < MaxChunks)
            {
                throw new ArgumentException($"Нужно место на {MaxChunks} чанков, есть {next.Length}.", nameof(next));
            }

            ChunkCoord center = BlockCenter(map, character);
            int n = 0;

            // Остаются чанки текущего окна внутри блока 8 × 6: x ∈ [cx − 4, cx + 3], y ∈ [cy − 3, cy + 2].
            for (int i = 0; i < current.Length; i++)
            {
                Int2 d = Torus.Delta(map, center, current[i]);
                if (d.X >= -KeepWidth / 2 && d.X < KeepWidth / 2 && d.Y >= -KeepHeight / 2 && d.Y < KeepHeight / 2)
                {
                    next[n++] = current[i];
                }
            }

            int kept = n;
            AxisRange(center.X, Width, map.ChunksX, map.Wraps, out int startX, out int countX);
            AxisRange(center.Y, Height, map.ChunksY, map.Wraps, out int startY, out int countY);
            for (int j = 0; j < countY; j++)
            {
                for (int i = 0; i < countX; i++)
                {
                    ChunkCoord c = Torus.Wrap(map, new ChunkCoord(startX + i, startY + j));
                    if (!ChunkSets.Contains(next.Slice(0, kept), c))
                    {
                        next[n++] = c;
                    }
                }
            }

            ChunkSets.Sort(next.Slice(0, n));
            return n;
        }

        // Блок длины span с центром на границе center: [center − span/2, center + span/2 − 1].
        // Тор: не длиннее карты (каждый чанк один раз). Плоская карта: обрезка по краям.
        private static void AxisRange(int center, int span, int size, bool wraps, out int start, out int count)
        {
            start = center - span / 2;
            if (wraps)
            {
                count = span < size ? span : size;
                return;
            }

            int end = start + span;
            if (start < 0)
            {
                start = 0;
            }

            if (end > size)
            {
                end = size;
            }

            count = end > start ? end - start : 0;
        }
    }
}
