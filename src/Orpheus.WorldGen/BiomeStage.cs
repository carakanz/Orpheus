using System;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Этап 3 «Биомы долями» (карточка 1-016; docs/01, 2 «Таблица биомов», «Набор «срез»», «Выбрано: доли вместо порогов»):
    /// перенос <c>classify2</c> модели <c>worldgen_model_v2.py</c> (режим <c>full=False</c>) на целые числа.
    /// <para>
    /// Строки таблицы идут по порядку (порядок — приоритет). Строка берёт из ещё не размеченных ячеек своего источника
    /// round(доля · N / 100) ячеек с наибольшим (наименьшим) ключом. Отбор — одна сортировка 64-битных ключей
    /// «значение поля в старших битах, индекс ячейки в младших»: порядок полный, поэтому при равных значениях
    /// побеждает меньший индекс на любой машине и при любом алгоритме сортировки (`01` 2: одно зерно — один мир).
    /// </para>
    /// <para>
    /// «Верхняя четверть h» — ровно round(N / 4) ячеек с наибольшей h по всем ячейкам мира (до разметки), равные — по индексу.
    /// У модели — <c>h ≥ np.quantile(h, 0.75)</c>: при непрерывных значениях это те же N / 4 ячеек.
    /// </para>
    /// </summary>
    public static class BiomeStage
    {
        /// <summary>Номер этапа (`01` 2). Этап 3 детерминирован без случайных чисел — поток этапа не нужен.</summary>
        public const int Stage = 3;

        /// <summary>Этап 3 по таблице набора карты: пишет <see cref="MacroMap.Biome"/>. Таблицы нет (полный набор) — ничего не делает.</summary>
        public static void Run(MacroMap map, FieldData fields)
        {
            BiomeRow[]? table = BiomeTables.For(map.Set);
            if (table != null)
            {
                Run(map, fields, table);
            }
        }

        /// <summary>Этап 3 по заданной таблице. Ячейки, которые не забрала ни одна строка, остаются <see cref="MacroMap.BiomeNone"/>.</summary>
        public static void Run(MacroMap map, FieldData fields, BiomeRow[] table)
        {
            int n = map.Size * map.Size;
            byte[] biome = map.Biome;
            biome.AsSpan().Fill(MacroMap.BiomeNone);
            bool[]? upperQuarter = null;
            var keys = new long[n];
            foreach (BiomeRow row in table)
            {
                bool[]? allowed = null;
                if (row.Source == BiomeSource.UpperQuarterH)
                {
                    upperQuarter ??= UpperQuarter(fields.H, keys);
                    allowed = upperQuarter;
                }

                if (row.IsRest)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (biome[i] == MacroMap.BiomeNone && (allowed == null || allowed[i]))
                        {
                            biome[i] = row.Biome;
                        }
                    }

                    continue;
                }

                ushort[] key = KeyField(fields, row.Key);
                int count = 0;
                for (int i = 0; i < n; i++)
                {
                    if (biome[i] == MacroMap.BiomeNone && (allowed == null || allowed[i]))
                    {
                        keys[count++] = SortKey(key[i], i, row.Largest);
                    }
                }

                int take = Math.Min(ShareCount(row.SharePercent, n), count);
                Array.Sort(keys, 0, count);
                for (int j = 0; j < take; j++)
                {
                    biome[(int)(keys[j] & 0xFFFFFFFF)] = row.Biome;
                }
            }
        }

        /// <summary>round(доля · N / 100), половина — вверх (у модели — <c>round</c> Python; на 256²…8192² половин нет).</summary>
        public static int ShareCount(int sharePercent, int cells) => (int)(((long)sharePercent * cells + 50) / 100);

        /// <summary>
        /// Ключ сортировки по возрастанию: старшие 32 бита — значение (для «наибольший первым» — 65 535 − значение),
        /// младшие — индекс ячейки. Равные значения — по возрастанию индекса.
        /// </summary>
        public static long SortKey(ushort value, int index, bool largest)
        {
            long v = largest ? 0xFFFF - value : value;
            return (v << 32) | (uint)index;
        }

        /// <summary>Верхняя четверть h: round(N / 4) ячеек с наибольшей h по всем ячейкам, равные — по индексу.</summary>
        public static bool[] UpperQuarter(ushort[] h, long[] scratch)
        {
            int n = h.Length;
            for (int i = 0; i < n; i++)
            {
                scratch[i] = SortKey(h[i], i, largest: true);
            }

            Array.Sort(scratch, 0, n);
            var mask = new bool[n];
            int quarter = ShareCount(25, n);
            for (int j = 0; j < quarter; j++)
            {
                mask[(int)(scratch[j] & 0xFFFFFFFF)] = true;
            }

            return mask;
        }

        /// <summary>Число ячеек каждого биома на макрокарте: [номер биома]; индекс <see cref="Biome.Count"/> — не размечено.</summary>
        public static int[] CountCells(MacroMap map)
        {
            var cnt = new int[Biome.Count + 1];
            foreach (byte b in map.Biome)
            {
                cnt[b < Biome.Count ? b : Biome.Count]++;
            }

            return cnt;
        }

        private static ushort[] KeyField(FieldData fields, BiomeKey key)
        {
            switch (key)
            {
                case BiomeKey.H: return fields.H;
                case BiomeKey.M: return fields.M;
                case BiomeKey.C: return fields.C;
                default: throw new ArgumentOutOfRangeException(nameof(key), key, "У строки нет ключа.");
            }
        }
    }
}
