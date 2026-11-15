using System;
using Orpheus.Core.World;

namespace Orpheus.Save
{
    /// <summary>
    /// Перенос построек старых миров (форматы 1–9 → 10, E2-12): стена, забор и дверь занимали целый тайл; в формате 10
    /// у них маска ячеек 0,5 × 0,5 в старших 4 битах байта пола записи (<see cref="BuildRecord.Cells"/>). Старая постройка
    /// становится сплошным квадратом из 4 ячеек — комнаты остаются закрытыми, проходы прежними (записка
    /// <c>work/design/half_tile_walls.md</c>). Перевод — правилом, а не байтами: маска 0 у постройки-ячеек и есть «весь тайл»
    /// (<see cref="BuildCells.MaskOf"/>), блок не меняется — хэш состояния старого мира тот же. Здесь — только счёт для журнала.
    /// </summary>
    internal static class LegacyWalls
    {
        /// <summary>Посчитать старые постройки-ячейки блока (маска 0 — весь тайл); <paramref name="count"/> += их число. Блок не меняется — false.</summary>
        public static bool Convert(byte[] blob, ref int count)
        {
            int at = Chunk.LayerBytes;
            int n = blob[at] | (blob[at + 1] << 8);
            at += Chunk.BuildCountBytes;
            for (int i = 0; i < n; i++, at += Chunk.BuildEntryBytes)
            {
                int index = blob[at] | (blob[at + 1] << 8);
                ushort obj = (ushort)(blob[Chunk.ObjectLowOffset + index] | (blob[Chunk.ObjectHighOffset + index] << 8));
                int floorCells = at + 2 + 5; // байт «пол + ячейки» записи (BuildRecord.Write: dest[5])
                if (!BuildCells.IsCellBuilding(obj) || (blob[floorCells] >> 4) != 0)
                {
                    continue;
                }

                count++;
            }

            return false;
        }

        /// <summary>Строка в журнал загрузки (<c>LoadedWorldState.Notes</c>).</summary>
        public static string[] AddNote(string[] notes, int count, uint format)
        {
            var all = new string[notes.Length + 1];
            Array.Copy(notes, all, notes.Length);
            all[notes.Length] = $"Формат {format} → 10: стен, заборов, дверей читаются как сплошной тайл из 4 ячеек 0,5 × 0,5: {count}.";
            return all;
        }
    }
}
