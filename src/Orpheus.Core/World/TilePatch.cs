using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Пачка правок одного чанка за такт — одно сообщение в канал 1 (docs/07, 3.7):
    /// заголовок (карта 1, чанк x и y по 2, новая ревизия 4 = 9 байт), число правок (2) и сами правки
    /// по 5…13 байт. Клиент применяет пачку, только если его ревизия чанка — ровно предыдущая.
    /// </summary>
    public readonly struct TilePatch
    {
        /// <summary>Заголовок по сети: карта (1) + чанк x (2) + чанк y (2) + ревизия (4) = 9 байт (docs/07, 3.6).</summary>
        public const int HeaderBytes = 9;

        /// <summary>Поле «число правок» в пачке: 2 байта.</summary>
        public const int CountBytes = 2;

        /// <summary>Карта: 0 — поверхность, 1..K — отдельная карта данжа (docs/07, 3.10).</summary>
        public readonly byte MapId;

        /// <summary>Чанк, к которому относится пачка.</summary>
        public readonly ChunkCoord Chunk;

        /// <summary>Новая ревизия чанка после пачки.</summary>
        public readonly uint Revision;

        /// <summary>Правки в порядке их прохождения (3.7: «по порядку», конфликт — первая выигрывает).</summary>
        public readonly TileEdit[] Edits;

        public TilePatch(byte mapId, ChunkCoord chunk, uint revision, TileEdit[] edits)
        {
            MapId = mapId;
            Chunk = chunk;
            Revision = revision;
            Edits = edits ?? Array.Empty<TileEdit>();
        }

        /// <summary>Число правок в пачке.</summary>
        public readonly int EditCount => Edits.Length;

        /// <summary>Размер пачки в байтах: 9 + 2 + сумма правок.</summary>
        public readonly int WireBytes => SizeOf(Edits, Edits.Length);

        /// <summary>Размер пачки из <paramref name="count"/> первых правок массива <paramref name="edits"/>.</summary>
        public static int SizeOf(TileEdit[] edits, int count)
        {
            if (edits == null)
            {
                throw new ArgumentNullException(nameof(edits));
            }

            if (count < 0 || count > edits.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Число правок — от 0 до длины массива.");
            }

            int bytes = HeaderBytes + CountBytes;
            for (int i = 0; i < count; i++)
            {
                bytes += edits[i].WireBytes;
            }

            return bytes;
        }

        /// <summary>Записать пачку в <paramref name="dest"/> и вернуть число байт.</summary>
        public readonly int Write(Span<byte> dest)
        {
            int total = WireBytes;
            if (dest.Length < total)
            {
                throw new ArgumentException($"Нужно {total} байт, есть {dest.Length}.", nameof(dest));
            }

            dest[0] = MapId;
            WriteUInt16(dest.Slice(1), (ushort)Chunk.X);
            WriteUInt16(dest.Slice(3), (ushort)Chunk.Y);
            WriteUInt32(dest.Slice(5), Revision);
            WriteUInt16(dest.Slice(9), (ushort)Edits.Length);

            int at = HeaderBytes + CountBytes;
            for (int i = 0; i < Edits.Length; i++)
            {
                at += Edits[i].Write(dest.Slice(at));
            }

            return total;
        }

        /// <summary>Прочитать пачку из <paramref name="src"/> и вернуть число прочитанных байт.</summary>
        public static int Read(ReadOnlySpan<byte> src, out TilePatch patch)
        {
            if (src.Length < HeaderBytes + CountBytes)
            {
                throw new ArgumentException(
                    $"Нужно не меньше {HeaderBytes + CountBytes} байт заголовка, есть {src.Length}.", nameof(src));
            }

            byte mapId = src[0];
            var chunk = new ChunkCoord(ReadUInt16(src, 1), ReadUInt16(src, 3));
            uint revision = ReadUInt32(src, 5);
            int count = ReadUInt16(src, 9);

            var edits = new TileEdit[count];
            int at = HeaderBytes + CountBytes;
            for (int i = 0; i < count; i++)
            {
                at += TileEdit.Read(src.Slice(at), out edits[i]);
            }

            patch = new TilePatch(mapId, chunk, revision, edits);
            return at;
        }

        internal static void WriteUInt16(Span<byte> dest, ushort value)
        {
            dest[0] = (byte)value;
            dest[1] = (byte)(value >> 8);
        }

        internal static void WriteUInt32(Span<byte> dest, uint value)
        {
            dest[0] = (byte)value;
            dest[1] = (byte)(value >> 8);
            dest[2] = (byte)(value >> 16);
            dest[3] = (byte)(value >> 24);
        }

        internal static int ReadUInt16(ReadOnlySpan<byte> src, int at) => src[at] | (src[at + 1] << 8);

        internal static uint ReadUInt32(ReadOnlySpan<byte> src, int at) =>
            (uint)(src[at] | (src[at + 1] << 8) | (src[at + 2] << 16) | (src[at + 3] << 24));

        public override readonly string ToString() =>
            $"patch chunk({Chunk.X}, {Chunk.Y}) rev {Revision}, {Edits.Length} правок, {WireBytes} байт";
}
}
