using System;
using Orpheus.Core.World;

namespace Orpheus.Save
{
    /// <summary>
    /// Перенос построек старых миров (форматы 1–9 → 10, E2-12): стена, забор и дверь занимали целый тайл; в формате 10
    /// у них маска ячеек 0,5 × 0,5 в старших 4 битах байта пола записи (<see cref="BuildRecord.Cells"/>). Старая постройка
    /// становится сплошным квадратом из 4 ячеек (маска 1111) — комнаты остаются закрытыми, проходы прежними
    /// (записка <c>work/design/half_tile_walls.md</c>). Правится блок чанка до загрузки в мир.
    /// </summary>
    internal static class LegacyWalls
    {
        /// <summary>Перевести постройки блока; <paramref name="count"/> += число переведённых. true — блок изменён.</summary>
        public static bool Convert(byte[] blob, ref int count)
        {
            int at = Chunk.LayerBytes;
            int n = blob[at] | (blob[at + 1] << 8);
            at += Chunk.BuildCountBytes;
            bool changed = false;
            for (int i = 0; i < n; i++, at += Chunk.BuildEntryBytes)
            {
                int index = blob[at] | (blob[at + 1] << 8);
                ushort obj = (ushort)(blob[Chunk.ObjectLowOffset + index] | (blob[Chunk.ObjectHighOffset + index] << 8));
                int floorCells = at + 2 + 5; // байт «пол + ячейки» записи (BuildRecord.Write: dest[5])
                if (!BuildCells.IsCellBuilding(obj) || (blob[floorCells] >> 4) != 0)
                {
                    continue;
                }

                blob[floorCells] = (byte)((blob[floorCells] & 0x0F) | (BuildCells.Full << 4));
                changed = true;
                count++;
            }

            return changed;
        }

        /// <summary>Строка в журнал загрузки (<c>LoadedWorldState.Notes</c>).</summary>
        public static string[] AddNote(string[] notes, int count, uint format)
        {
            var all = new string[notes.Length + 1];
            Array.Copy(notes, all, notes.Length);
            all[notes.Length] = $"Формат {format} → 10: стен, заборов, дверей переведено в ячейки 0,5 × 0,5 (сплошной тайл): {count}.";
            return all;
        }
    }
}
