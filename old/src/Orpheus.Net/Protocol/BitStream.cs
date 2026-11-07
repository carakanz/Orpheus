using System;

namespace Orpheus.Net.Protocol
{
    /// <summary>Испорченное или чужое сообщение: короче схемы, неизвестный тип, поле вне диапазона.</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Битовый писатель протокола (docs/07, 2.10): поля по битам, младший бит первым, без выделений.
    /// Места не хватило — <see cref="ProtocolException"/> (размер сообщений считается заранее — переполнение
    /// значит ошибку в коде, а не в данных).
    /// </summary>
    public ref struct BitWriter
    {
        private readonly Span<byte> _buf;
        private int _bit;

        public BitWriter(Span<byte> buffer)
        {
            _buf = buffer;
            _bit = 0;
            buffer.Clear();
        }

        /// <summary>Записано бит.</summary>
        public int BitPosition => _bit;

        /// <summary>Записано байт (неполный последний — целиком).</summary>
        public int BytesWritten => (_bit + 7) >> 3;

        /// <summary>Младшие <paramref name="count"/> бит (0…32) значения.</summary>
        public void WriteBits(uint value, int count)
        {
            if ((uint)count > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (_bit + count > _buf.Length * 8)
            {
                throw new ProtocolException($"Буфер {_buf.Length} байт мал: нужно ещё {count} бит после {_bit}.");
            }

            for (int i = 0; i < count;)
            {
                int byteIndex = _bit >> 3;
                int offset = _bit & 7;
                int take = Math.Min(8 - offset, count - i);
                uint part = (value >> i) & ((1u << take) - 1);
                _buf[byteIndex] |= (byte)(part << offset);
                _bit += take;
                i += take;
            }
        }

        public void WriteBool(bool v) => WriteBits(v ? 1u : 0u, 1);

        public void WriteByte(byte v) => WriteBits(v, 8);

        public void WriteSByte(sbyte v) => WriteBits((byte)v, 8);

        public void WriteUInt16(ushort v) => WriteBits(v, 16);

        public void WriteUInt32(uint v) => WriteBits(v, 32);

        public void WriteUInt64(ulong v)
        {
            WriteBits((uint)v, 32);
            WriteBits((uint)(v >> 32), 32);
        }

        /// <summary>Байты как есть.</summary>
        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                WriteBits(bytes[i], 8);
            }
        }
    }

    /// <summary>Битовый читатель — пара <see cref="BitWriter"/>. Данных меньше схемы — <see cref="ProtocolException"/>.</summary>
    public ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _buf;
        private int _bit;

        public BitReader(ReadOnlySpan<byte> buffer)
        {
            _buf = buffer;
            _bit = 0;
        }

        /// <summary>Прочитано бит.</summary>
        public int BitPosition => _bit;

        /// <summary>Прочитано байт (неполный последний — целиком).</summary>
        public int BytesRead => (_bit + 7) >> 3;

        /// <summary>Осталось бит.</summary>
        public int BitsLeft => _buf.Length * 8 - _bit;

        public uint ReadBits(int count)
        {
            if ((uint)count > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (_bit + count > _buf.Length * 8)
            {
                throw new ProtocolException($"Сообщение короче схемы: {_buf.Length} байт, нужно ещё {count} бит после {_bit}.");
            }

            uint value = 0;
            for (int i = 0; i < count;)
            {
                int byteIndex = _bit >> 3;
                int offset = _bit & 7;
                int take = Math.Min(8 - offset, count - i);
                uint part = ((uint)_buf[byteIndex] >> offset) & ((1u << take) - 1);
                value |= part << i;
                _bit += take;
                i += take;
            }

            return value;
        }

        public bool ReadBool() => ReadBits(1) != 0;

        public byte ReadByte() => (byte)ReadBits(8);

        public sbyte ReadSByte() => (sbyte)(byte)ReadBits(8);

        public ushort ReadUInt16() => (ushort)ReadBits(16);

        public uint ReadUInt32() => ReadBits(32);

        public ulong ReadUInt64()
        {
            ulong lo = ReadBits(32);
            ulong hi = ReadBits(32);
            return lo | (hi << 32);
        }

        public void ReadBytes(Span<byte> dest)
        {
            for (int i = 0; i < dest.Length; i++)
            {
                dest[i] = (byte)ReadBits(8);
            }
        }

        /// <summary>Сообщение кончилось ровно на схеме: лишних байт нет (хвост неполного байта — нули).</summary>
        public void ExpectEnd()
        {
            if (BytesRead != _buf.Length)
            {
                throw new ProtocolException($"Лишние данные: прочитано {BytesRead} байт из {_buf.Length}.");
            }
        }
    }
}
