using System;
using System.Collections.Generic;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Номера биомов в байте <see cref="MacroMap.Biome"/> — как в модели <c>worldgen_model.py</c> (<c>MEADOW … GATE = range(13)</c>),
    /// чтобы дамп C# и модель сравнивались без перекодировки. <see cref="MacroMap.BiomeNone"/> — не размечено.
    /// </summary>
    public static class Biome
    {
        public const byte Meadow = 0;
        public const byte Forest = 1;
        public const byte Jungle = 2;
        public const byte Swamp = 3;
        public const byte Mountain = 4;
        public const byte Cave = 5;
        public const byte Desert = 6;
        public const byte City = 7;
        public const byte Tundra = 8;
        public const byte Glacier = 9;
        public const byte Lava = 10;
        public const byte Lake = 11;
        public const byte Pregate = 12;

        /// <summary>Всего номеров (для гистограмм).</summary>
        public const int Count = 13;
    }

    /// <summary>Откуда строка таблицы этапа 3 берёт ячейки (столбец «Откуда берёт», `01` 2).</summary>
    public enum BiomeSource : byte
    {
        /// <summary>Все ещё не размеченные ячейки.</summary>
        All = 0,

        /// <summary>Не размеченные ячейки, у которых h в верхней четверти мира (четверть — по всем ячейкам, до разметки).</summary>
        UpperQuarterH = 1,
    }

    /// <summary>Ключ строки — поле этапа 2 в Q16 (столбец «Ключ», `01` 2).</summary>
    public enum BiomeKey : byte
    {
        /// <summary>Без ключа: строка забирает всё, что осталось (луга — остаток).</summary>
        None = 0,
        H = 1,
        M = 2,
        C = 3,
    }

    /// <summary>
    /// Строка таблицы биомов этапа 3 (`01` 2, «Таблица биомов»): биом, доля мира в процентах, откуда берёт, ключ и направление.
    /// Строка с <see cref="IsRest"/> забирает все оставшиеся ячейки источника.
    /// </summary>
    public readonly struct BiomeRow
    {
        public BiomeRow(byte biome, int sharePercent, BiomeSource source, BiomeKey key, bool largest)
        {
            Biome = biome;
            SharePercent = sharePercent;
            Source = source;
            Key = key;
            Largest = largest;
        }

        public byte Biome { get; }

        /// <summary>Доля площади мира, %; у строки-остатка — 0.</summary>
        public int SharePercent { get; }

        public BiomeSource Source { get; }

        public BiomeKey Key { get; }

        /// <summary>Первыми идут ячейки с наибольшим ключом (иначе — с наименьшим).</summary>
        public bool Largest { get; }

        /// <summary>Строка-остаток (ключа нет): забирает всё, что осталось.</summary>
        public bool IsRest => Key == BiomeKey.None;

        /// <summary>Остаток: все оставшиеся ячейки.</summary>
        public static BiomeRow Rest(byte biome) => new BiomeRow(biome, 0, BiomeSource.All, BiomeKey.None, false);
    }

    /// <summary>Таблицы этапа 3 по наборам (`01` 2). Порядок строк — приоритет.</summary>
    public static class BiomeTables
    {
        /// <summary>
        /// Набор «срез» (`01` 2, «Набор «срез»»): пещеры 8 % по наибольшей c среди ячеек с h в верхней четверти,
        /// горы 14 % по наибольшей h, лес 33 % по наибольшей m, луга — остаток.
        /// </summary>
        /// <remarks>Только чтение (замечание П по 1-016): строки не подменить снаружи — ни элементом, ни приведением к массиву.</remarks>
        public static readonly IReadOnlyList<BiomeRow> Slice = Array.AsReadOnly(new[]
        {
            new BiomeRow(Biome.Cave, 8, BiomeSource.UpperQuarterH, BiomeKey.C, largest: true),
            new BiomeRow(Biome.Mountain, 14, BiomeSource.All, BiomeKey.H, largest: true),
            new BiomeRow(Biome.Forest, 33, BiomeSource.All, BiomeKey.M, largest: true),
            BiomeRow.Rest(Biome.Meadow),
        });

        /// <summary>Таблица набора; полный набор (пояса, Перекрёсток, вода) — следующая карточка, пока нет.</summary>
        public static IReadOnlyList<BiomeRow>? For(BiomeSet set)
        {
            if (set == BiomeSet.Slice)
            {
                return Slice;
            }

            return null;
        }
    }
}
