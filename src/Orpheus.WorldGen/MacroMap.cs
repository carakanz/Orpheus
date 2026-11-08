using System;
using System.Security.Cryptography;

namespace Orpheus.WorldGen
{
    /// <summary>Набор биомов (docs/01, 2 «Входы и выход»): полный или «срез» (4 биома, без поясов).</summary>
    public enum BiomeSet : byte
    {
        Slice = 0,
        Full = 1,
    }

    /// <summary>
    /// Макрокарта (docs/01, 2 «Макрокарта»): сетка ячеек 8 × 8 тайлов — 128 × 128 на 1024², 512 × 512 на 4096².
    /// На ячейку: биом, h, m, c, флаги (вода, котловина, участок данжа, защищённая зона) — по байту, как в `01` 2;
    /// и байт «кольцо и зона» — результат этапа 1 (`01` 2, «Этапы»).
    /// <para>
    /// Слои — отдельные массивы [y · Size + x] (а не массив структур): так их проще писать в снимок генерации,
    /// сжимать и (потом) реплицировать по слою. Байтовая форма — <see cref="ToBytes"/> / <see cref="FromBytes"/>;
    /// хэш — SHA-256 этой формы (<see cref="Hash"/>, `01` приёмка 2 п. 1).
    /// </para>
    /// </summary>
    public sealed class MacroMap
    {
        /// <summary>Тайлов в стороне ячейки.</summary>
        public const int CellTiles = 8;

        /// <summary>log₂ <see cref="CellTiles"/>.</summary>
        public const int CellBits = 3;

        /// <summary>Биом ещё не назначен (этап 3 — карточка 1-016).</summary>
        public const byte BiomeNone = 0xFF;

        // ---------- Флаги (`01` 2) ----------
        public const byte FlagWater = 1 << 0;
        public const byte FlagBasin = 1 << 1;
        public const byte FlagDungeon = 1 << 2;
        public const byte FlagProtected = 1 << 3;

        // ---------- Байт зоны: биты 0–1 — кольцо 0…3, дальше — зоны этапа 1 ----------
        public const byte RingMask = 0x03;
        public const byte ZoneHot = 1 << 2;
        public const byte ZoneCold = 1 << 3;
        public const byte ZoneCrossroads = 1 << 4;
        public const byte ZonePregate = 1 << 5;

        private static readonly byte[] Magic = { (byte)'O', (byte)'M', (byte)'A', (byte)'P' };

        /// <summary>Версия байтовой формы макрокарты.</summary>
        public const int FormatVersion = 1;

        /// <summary>Заголовок: магия 4, формат 2, версия генератора 2, W 4, зерно 8, набор 1, резерв 3.</summary>
        public const int HeaderBytes = 24;

        /// <summary>Слоёв по байту на ячейку.</summary>
        public const int Layers = 6;

        public MacroMap(int worldSize, ulong seed, int generatorVersion, BiomeSet set)
        {
            if (!IntMath.IsPow2(worldSize) || worldSize < 256)
            {
                throw new ArgumentOutOfRangeException(nameof(worldSize), worldSize, "Сторона мира — степень двойки не меньше 256.");
            }

            WorldSize = worldSize;
            Size = worldSize >> CellBits;
            Seed = seed;
            GeneratorVersion = generatorVersion;
            Set = set;
            int n = Size * Size;
            Biome = new byte[n];
            Biome.AsSpan().Fill(BiomeNone);
            H = new byte[n];
            M = new byte[n];
            C = new byte[n];
            Flags = new byte[n];
            Zone = new byte[n];
        }

        /// <summary>Сторона мира, тайлов.</summary>
        public int WorldSize { get; }

        /// <summary>Сторона макрокарты, ячеек (W / 8).</summary>
        public int Size { get; }

        public ulong Seed { get; }

        public int GeneratorVersion { get; }

        public BiomeSet Set { get; }

        /// <summary>Биом ячейки (<see cref="BiomeNone"/> до этапа 3).</summary>
        public byte[] Biome { get; }

        /// <summary>Высота: старший байт поля h (Q16 → 0…255).</summary>
        public byte[] H { get; }

        /// <summary>Влажность — старший байт m.</summary>
        public byte[] M { get; }

        /// <summary>Пещерность — старший байт c.</summary>
        public byte[] C { get; }

        /// <summary>Флаги <c>Flag*</c>.</summary>
        public byte[] Flags { get; }

        /// <summary>Кольцо (биты 0–1) и зоны <c>Zone*</c> этапа 1.</summary>
        public byte[] Zone { get; }

        /// <summary>Индекс ячейки (x, y); координаты сворачиваются тором.</summary>
        public int IndexOf(int x, int y) => ((y & (Size - 1)) * Size) + (x & (Size - 1));

        /// <summary>Кольцо ячейки 0…3.</summary>
        public int RingAt(int index) => Zone[index] & RingMask;

        /// <summary>Байтовая форма: заголовок и слои Biome, H, M, C, Flags, Zone подряд.</summary>
        public byte[] ToBytes()
        {
            int n = Size * Size;
            var b = new byte[HeaderBytes + Layers * n];
            Magic.CopyTo(b, 0);
            WriteU16(b, 4, FormatVersion);
            WriteU16(b, 6, GeneratorVersion);
            WriteU32(b, 8, (uint)WorldSize);
            for (int i = 0; i < 8; i++)
            {
                b[12 + i] = (byte)(Seed >> (8 * i));
            }

            b[20] = (byte)Set;
            int at = HeaderBytes;
            foreach (byte[] layer in new[] { Biome, H, M, C, Flags, Zone })
            {
                Buffer.BlockCopy(layer, 0, b, at, n);
                at += n;
            }

            return b;
        }

        /// <summary>Макрокарта из байтовой формы <see cref="ToBytes"/>.</summary>
        public static MacroMap FromBytes(byte[] b)
        {
            if (b.Length < HeaderBytes || b[0] != Magic[0] || b[1] != Magic[1] || b[2] != Magic[2] || b[3] != Magic[3])
            {
                throw new FormatException("Не макрокарта: нет магии OMAP.");
            }

            int format = b[4] | (b[5] << 8);
            if (format != FormatVersion)
            {
                throw new FormatException($"Формат макрокарты {format}, ожидается {FormatVersion}.");
            }

            int version = b[6] | (b[7] << 8);
            int w = (int)(b[8] | ((uint)b[9] << 8) | ((uint)b[10] << 16) | ((uint)b[11] << 24));
            ulong seed = 0;
            for (int i = 0; i < 8; i++)
            {
                seed |= (ulong)b[12 + i] << (8 * i);
            }

            var map = new MacroMap(w, seed, version, (BiomeSet)b[20]);
            int n = map.Size * map.Size;
            if (b.Length != HeaderBytes + Layers * n)
            {
                throw new FormatException($"Длина макрокарты {b.Length}, ожидается {HeaderBytes + Layers * n}.");
            }

            int at = HeaderBytes;
            foreach (byte[] layer in new[] { map.Biome, map.H, map.M, map.C, map.Flags, map.Zone })
            {
                Buffer.BlockCopy(b, at, layer, 0, n);
                at += n;
            }

            return map;
        }

        /// <summary>SHA-256 байтовой формы (`01` приёмка 2 п. 1: одинаковый хэш макрокарты).</summary>
        public byte[] Hash()
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(ToBytes());
        }

        /// <summary>Хэш строкой (hex, нижний регистр).</summary>
        public string HashHex()
        {
            byte[] h = Hash();
            var chars = new char[h.Length * 2];
            const string digits = "0123456789abcdef";
            for (int i = 0; i < h.Length; i++)
            {
                chars[2 * i] = digits[h[i] >> 4];
                chars[2 * i + 1] = digits[h[i] & 15];
            }

            return new string(chars);
        }

        private static void WriteU16(byte[] b, int at, int v)
        {
            b[at] = (byte)v;
            b[at + 1] = (byte)(v >> 8);
        }

        private static void WriteU32(byte[] b, int at, uint v)
        {
            b[at] = (byte)v;
            b[at + 1] = (byte)(v >> 8);
            b[at + 2] = (byte)(v >> 16);
            b[at + 3] = (byte)(v >> 24);
        }
    }
}
