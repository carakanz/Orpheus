using System;
using System.IO;
using System.Text;

namespace Orpheus.Save
{
    /// <summary>Тип значения записи (docs/07, 6.3: «номер поля, тип, длина, значение»).</summary>
    public enum RecordType : byte
    {
        /// <summary>Беззнаковое целое: 0–8 байт, младший первым, без ведущих нулей.</summary>
        UInt = 1,

        /// <summary>Знаковое целое: зигзаг (0, −1, 1, −2 … → 0, 1, 2, 3 …), дальше как <see cref="UInt"/>.</summary>
        Int = 2,

        /// <summary>Байты как есть.</summary>
        Bytes = 3,

        /// <summary>Текст UTF-8.</summary>
        Text = 4,

        /// <summary>Вложенные записи.</summary>
        Record = 5,
    }

    /// <summary>
    /// Запись структурных разделов сохранения: подряд записи «номер поля (varint), тип (1 байт),
    /// длина значения (varint), значение». Varint — LEB128 (7 бит на байт, старший бит — «дальше ещё»).
    /// Читатель (<see cref="RecordReader"/>) пропускает незнакомые поля по длине, отсутствующее поле
    /// берёт умолчание — поэтому новое поле миграции не требует (6.7).
    /// <para>
    /// Вложенная запись: <see cref="BeginRecord"/> оставляет под длину 5 байт неминимального varint
    /// (0x80 0x80 0x80 0x80 0x00 — всё ещё корректный LEB128), <see cref="EndRecord"/> вписывает длину.
    /// Так нет копирования вложенного блока.
    /// </para>
    /// </summary>
    public sealed class RecordWriter
    {
        /// <summary>Байт под длину вложенной записи.</summary>
        public const int RecordLengthBytes = 5;

        private byte[] _buf;
        private int _len;

        public RecordWriter(int capacity = 256)
        {
            _buf = new byte[Math.Max(16, capacity)];
        }

        /// <summary>Записано байт.</summary>
        public int Length => _len;

        /// <summary>Записанное (без копии; действительно до следующей записи).</summary>
        public ReadOnlySpan<byte> Written => new ReadOnlySpan<byte>(_buf, 0, _len);

        /// <summary>Копия записанного.</summary>
        public byte[] ToArray() => Written.ToArray();

        /// <summary>Начать заново (буфер остаётся).</summary>
        public void Clear() => _len = 0;

        public void WriteUInt(int field, ulong value)
        {
            int n = 0;
            for (ulong v = value; v != 0; v >>= 8)
            {
                n++;
            }

            Head(field, RecordType.UInt, n);
            Ensure(n);
            for (int i = 0; i < n; i++)
            {
                _buf[_len++] = (byte)(value >> (8 * i));
            }
        }

        public void WriteInt(int field, long value)
        {
            ulong zigzag = unchecked((ulong)((value << 1) ^ (value >> 63)));
            int n = 0;
            for (ulong v = zigzag; v != 0; v >>= 8)
            {
                n++;
            }

            Head(field, RecordType.Int, n);
            Ensure(n);
            for (int i = 0; i < n; i++)
            {
                _buf[_len++] = (byte)(zigzag >> (8 * i));
            }
        }

        public void WriteBool(int field, bool value) => WriteUInt(field, value ? 1UL : 0UL);

        public void WriteBytes(int field, ReadOnlySpan<byte> value)
        {
            Head(field, RecordType.Bytes, value.Length);
            Ensure(value.Length);
            value.CopyTo(new Span<byte>(_buf, _len, value.Length));
            _len += value.Length;
        }

        public void WriteText(int field, string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            Head(field, RecordType.Text, utf8.Length);
            Ensure(utf8.Length);
            Buffer.BlockCopy(utf8, 0, _buf, _len, utf8.Length);
            _len += utf8.Length;
        }

        /// <summary>Начать вложенную запись; вернуть метку для <see cref="EndRecord"/>.</summary>
        public int BeginRecord(int field)
        {
            CheckField(field);
            Ensure(5 + 1 + RecordLengthBytes);
            WriteVarint((uint)field);
            _buf[_len++] = (byte)RecordType.Record;
            int mark = _len;
            for (int i = 0; i < RecordLengthBytes - 1; i++)
            {
                _buf[_len++] = 0x80;
            }

            _buf[_len++] = 0x00;
            return mark;
        }

        /// <summary>Закончить вложенную запись: вписать длину на место метки.</summary>
        public void EndRecord(int mark)
        {
            int start = mark + RecordLengthBytes;
            if (mark < 0 || start > _len)
            {
                throw new ArgumentOutOfRangeException(nameof(mark));
            }

            uint length = (uint)(_len - start);
            for (int i = 0; i < RecordLengthBytes; i++)
            {
                byte b = (byte)((length >> (7 * i)) & 0x7F);
                _buf[mark + i] = i < RecordLengthBytes - 1 ? (byte)(b | 0x80) : b;
            }
        }

        private void Head(int field, RecordType type, int length)
        {
            CheckField(field);
            Ensure(5 + 1 + 5);
            WriteVarint((uint)field);
            _buf[_len++] = (byte)type;
            WriteVarint((uint)length);
        }

        private void WriteVarint(uint v)
        {
            while (v >= 0x80)
            {
                _buf[_len++] = (byte)(v | 0x80);
                v >>= 7;
            }

            _buf[_len++] = (byte)v;
        }

        private void Ensure(int more)
        {
            if (_len + more <= _buf.Length)
            {
                return;
            }

            int size = _buf.Length;
            while (size < _len + more)
            {
                size = size <= int.MaxValue / 2 ? size * 2 : int.MaxValue;
            }

            Array.Resize(ref _buf, size);
        }

        private static void CheckField(int field)
        {
            if (field <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(field), field, "Номер поля — с 1.");
            }
        }
    }

    /// <summary>
    /// Чтение записей <see cref="RecordWriter"/>. Шаблон:
    /// <code>
    /// var r = new RecordReader(data);
    /// while (r.Next())
    ///     switch (r.Field) { case 1: x = r.UInt(); break; case 2: var sub = r.Record(); … break; }
    /// </code>
    /// Незнакомое поле — просто не читается: <see cref="Next"/> перешагивает значение по длине.
    /// Неверный тип знакомого поля, обрыв, длина за край — <see cref="InvalidDataException"/>.
    /// </summary>
    public ref struct RecordReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _at;
        private int _valueAt;
        private int _valueLength;

        public RecordReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _at = 0;
            _valueAt = 0;
            _valueLength = 0;
            Field = 0;
            Type = 0;
        }

        /// <summary>Номер поля текущей записи.</summary>
        public int Field { get; private set; }

        /// <summary>Тип текущей записи.</summary>
        public RecordType Type { get; private set; }

        /// <summary>Длина значения текущей записи.</summary>
        public int Length => _valueLength;

        /// <summary>Смещение значения текущей записи в блоке.</summary>
        public int ValueOffset => _valueAt;

        /// <summary>Значение текущей записи как есть.</summary>
        public ReadOnlySpan<byte> Raw => _data.Slice(_valueAt, _valueLength);

        /// <summary>Перейти к следующей записи; false — блок кончился.</summary>
        public bool Next()
        {
            if (_at == _data.Length)
            {
                Field = 0;
                return false;
            }

            uint field = ReadVarint();
            if (field == 0 || field > int.MaxValue)
            {
                throw new InvalidDataException($"Запись на смещении {_at}: номер поля {field}.");
            }

            if (_at >= _data.Length)
            {
                throw new InvalidDataException($"Запись поля {field} оборвана до типа.");
            }

            byte type = _data[_at++];
            uint length = ReadVarint();
            if (length > (uint)(_data.Length - _at))
            {
                throw new InvalidDataException($"Запись поля {field}: длина {length} за краем блока ({_data.Length - _at} байт осталось).");
            }

            Field = (int)field;
            Type = (RecordType)type;
            _valueAt = _at;
            _valueLength = (int)length;
            _at += (int)length;
            return true;
        }

        public ulong UInt()
        {
            Expect(RecordType.UInt);
            return Little();
        }

        public long Int()
        {
            Expect(RecordType.Int);
            ulong z = Little();
            return unchecked((long)(z >> 1) ^ -(long)(z & 1));
        }

        /// <summary>Беззнаковое в пределах int (номер, координата, длина).</summary>
        public int UIntAsInt(int max = int.MaxValue)
        {
            ulong v = UInt();
            if (v > (ulong)max)
            {
                throw new InvalidDataException($"Поле {Field}: {v} больше {max}.");
            }

            return (int)v;
        }

        /// <summary>Знаковое в пределах int.</summary>
        public int IntAsInt()
        {
            long v = Int();
            if (v < int.MinValue || v > int.MaxValue)
            {
                throw new InvalidDataException($"Поле {Field}: {v} вне int.");
            }

            return (int)v;
        }

        public bool Bool() => UInt() != 0;

        public ReadOnlySpan<byte> Bytes()
        {
            Expect(RecordType.Bytes);
            return Raw;
        }

        public string Text()
        {
            Expect(RecordType.Text);
            return Encoding.UTF8.GetString(Raw);
        }

        /// <summary>Читатель вложенной записи.</summary>
        public RecordReader Record()
        {
            Expect(RecordType.Record);
            return new RecordReader(Raw);
        }

        private ulong Little()
        {
            if (_valueLength > 8)
            {
                throw new InvalidDataException($"Поле {Field}: целое длиной {_valueLength} байт (не больше 8).");
            }

            ulong v = 0;
            for (int i = 0; i < _valueLength; i++)
            {
                v |= (ulong)_data[_valueAt + i] << (8 * i);
            }

            return v;
        }

        private void Expect(RecordType type)
        {
            if (Type != type)
            {
                throw new InvalidDataException($"Поле {Field}: тип {Type}, ожидается {type}.");
            }
        }

        private uint ReadVarint()
        {
            uint v = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (_at >= _data.Length)
                {
                    throw new InvalidDataException("Запись оборвана посреди varint.");
                }

                byte b = _data[_at++];
                if (shift == 28 && (b & 0x70) != 0)
                {
                    throw new InvalidDataException("Varint длиннее 32 бит.");
                }

                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return v;
                }
            }

            throw new InvalidDataException("Varint длиннее 5 байт.");
        }
    }
}
