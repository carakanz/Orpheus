using System;

namespace Orpheus.Save
{
    /// <summary>
    /// CRC32 (IEEE 802.3, многочлен 0xEDB88320, как в zip и PNG) для таблицы разделов контейнера (docs/07, 6.3).
    /// Своя реализация: в .NET Standard 2.1 нет System.IO.Hashing. Таблица на 256 значений, без выделений.
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        /// <summary>CRC32 всего блока.</summary>
        public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

        /// <summary>Продолжить CRC32 (<paramref name="crc"/> — итог прошлого куска, 0 — начало).</summary>
        public static uint Append(uint crc, ReadOnlySpan<byte> data)
        {
            uint c = ~crc;
            for (int i = 0; i < data.Length; i++)
            {
                c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            }

            return ~c;
        }

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                t[n] = c;
            }

            return t;
        }
    }
}
