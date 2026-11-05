using System;

namespace Orpheus.Core.World
{
    /// <summary>Целые в байтах, младший байт первым (сеть и сохранения мира).</summary>
    internal static class Bytes
    {
        public static void WriteUInt16(Span<byte> dest, ushort value)
        {
            dest[0] = (byte)value;
            dest[1] = (byte)(value >> 8);
        }

        public static void WriteUInt32(Span<byte> dest, uint value)
        {
            dest[0] = (byte)value;
            dest[1] = (byte)(value >> 8);
            dest[2] = (byte)(value >> 16);
            dest[3] = (byte)(value >> 24);
        }

        public static void WriteUInt64(Span<byte> dest, ulong value)
        {
            WriteUInt32(dest, (uint)value);
            WriteUInt32(dest.Slice(4), (uint)(value >> 32));
        }

        public static int ReadUInt16(ReadOnlySpan<byte> src, int at) => src[at] | (src[at + 1] << 8);

        public static uint ReadUInt32(ReadOnlySpan<byte> src, int at) =>
            (uint)(src[at] | (src[at + 1] << 8) | (src[at + 2] << 16) | (src[at + 3] << 24));

        public static ulong ReadUInt64(ReadOnlySpan<byte> src, int at) =>
            ReadUInt32(src, at) | ((ulong)ReadUInt32(src, at + 4) << 32);
    }
}
