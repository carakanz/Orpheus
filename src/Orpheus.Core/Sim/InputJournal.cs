using System;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>Сущность в начале записи: номер, позиция, скорость (повтор ставит её так же).</summary>
    public readonly struct JournalSpawn
    {
        public const int WireBytes = 16;

        public readonly int EntityId;
        public readonly WorldPos Position;
        public readonly int UnitsPerTickX10;

        public JournalSpawn(int entityId, WorldPos position, int unitsPerTickX10)
        {
            EntityId = entityId;
            Position = position;
            UnitsPerTickX10 = unitsPerTickX10;
        }
    }

    /// <summary>Заголовок журнала: мир (зерно, форма, версия генератора), такт начала и сущности.</summary>
    public sealed class JournalHeader
    {
        public JournalHeader(ulong seed, MapShape shape, int generatorVersion, long startTick, JournalSpawn[] spawns)
        {
            Seed = seed;
            Shape = shape;
            GeneratorVersion = generatorVersion;
            StartTick = startTick;
            Spawns = spawns ?? Array.Empty<JournalSpawn>();
        }

        public ulong Seed { get; }
        public MapShape Shape { get; }
        public int GeneratorVersion { get; }
        public long StartTick { get; }
        public JournalSpawn[] Spawns { get; }

        /// <summary>Заголовок по текущему состоянию симуляции (все сущности — точки старта).</summary>
        public static JournalHeader Of(Simulation sim)
        {
            ReadOnlySpan<SimEntity> entities = sim.Entities;
            var spawns = new JournalSpawn[entities.Length];
            for (int i = 0; i < entities.Length; i++)
            {
                spawns[i] = new JournalSpawn(entities[i].Id, entities[i].Mover.Position, entities[i].Mover.UnitsPerTickX10);
            }

            return new JournalHeader(sim.World.Seed, sim.World.Shape, sim.World.GeneratorVersion, sim.Tick, spawns);
        }
    }

    /// <summary>Вид записи журнала.</summary>
    public enum JournalRecord : byte
    {
        /// <summary>Конец файла без записи «конец» — запись оборвана.</summary>
        None = 0,

        /// <summary>Изменения ввода перед тактом: такт u32, число u16, по записи — номер u32 + кадр 6 байт (формат 1 — 3).</summary>
        Inputs = (byte)'I',

        /// <summary>Хэш после такта: такт u32, общий u64, мира u64, число u16, по сущности — номер u32 + хэш u64.</summary>
        Checkpoint = (byte)'H',

        /// <summary>Конец записи: последний такт u32.</summary>
        End = (byte)'E',
    }

    /// <summary>
    /// Журнал ввода (docs/07, 11.5): двоичный файл «заголовок + записи». Пишутся только изменения ввода
    /// (сущность держит кадр, пока не придёт новый), поэтому 30 мин 8 ботов <c>wander</c> — десятки КБ.
    /// <para>Заголовок: «ORPJ», версия формата 2, зерно u64, ширина u32, высота u32, тор u8,
    /// версия генератора u32, такт начала u64, число сущностей u16, по сущности 16 байт
    /// (номер, x, y, скорость — по int32). Все числа — младший байт первым.</para>
    /// <para>Формат 2 (1-002): кадр ввода 6 байт — направление, кнопки, угол прицела, ячейка быстрой панели.
    /// Формат 1 (кадр 3 байта) читается: угол и ячейка — по умолчанию. Точки сверки формата 1 — со старым
    /// хэшем (угол/ячейка в него не входили): повтор их честно рапортует как расхождение, не падая.</para>
    /// <para>При записи с хэшем раз в 300 тактов пишется точка сверки (<see cref="JournalRecord.Checkpoint"/>):
    /// повтор сравнивает с ней свой хэш и называет такт и сущность, где разошлось (11.2).</para>
    /// </summary>
    public static class InputJournal
    {
        public static ReadOnlySpan<byte> Magic => new byte[] { (byte)'O', (byte)'R', (byte)'P', (byte)'J' };

        public const byte FormatVersion = 2;

        /// <summary>Первая читаемая версия формата (кадр ввода 3 байта, без угла и ячейки).</summary>
        public const byte MinFormatVersion = 1;

        /// <summary>Байт заголовка без списка сущностей.</summary>
        public const int HeaderBytes = 4 + 1 + 8 + 4 + 4 + 1 + 4 + 8 + 2;

        /// <summary>Наибольшее число записей в одной записи журнала (u16).</summary>
        public const int MaxEntries = ushort.MaxValue;
    }

    /// <summary>Запись журнала ввода в поток. Буфер — заранее; запись такта не выделяет памяти.</summary>
    public sealed class InputJournalWriter : IDisposable
    {
        private const int InputEntryBytes = 4 + InputFrame.WireBytes;
        private const int HashEntryBytes = 4 + 8;

        private readonly Stream _out;
        private readonly bool _ownsStream;
        private byte[] _buffer;

        public InputJournalWriter(Stream output, JournalHeader header, bool ownsStream = true)
        {
            _out = output ?? throw new ArgumentNullException(nameof(output));
            _ownsStream = ownsStream;
            if (header == null)
            {
                throw new ArgumentNullException(nameof(header));
            }

            if (header.Spawns.Length > InputJournal.MaxEntries)
            {
                throw new ArgumentException("Слишком много сущностей для журнала.", nameof(header));
            }

            _buffer = new byte[Math.Max(4096, InputJournal.HeaderBytes + header.Spawns.Length * JournalSpawn.WireBytes)];
            Span<byte> b = _buffer;
            InputJournal.Magic.CopyTo(b);
            b[4] = InputJournal.FormatVersion;
            Bytes.WriteUInt64(b.Slice(5), header.Seed);
            Bytes.WriteUInt32(b.Slice(13), (uint)header.Shape.Width);
            Bytes.WriteUInt32(b.Slice(17), (uint)header.Shape.Height);
            b[21] = header.Shape.Wraps ? (byte)1 : (byte)0;
            Bytes.WriteUInt32(b.Slice(22), (uint)header.GeneratorVersion);
            Bytes.WriteUInt64(b.Slice(26), (ulong)header.StartTick);
            Bytes.WriteUInt16(b.Slice(34), (ushort)header.Spawns.Length);
            int at = InputJournal.HeaderBytes;
            for (int i = 0; i < header.Spawns.Length; i++)
            {
                JournalSpawn s = header.Spawns[i];
                Bytes.WriteUInt32(b.Slice(at), (uint)s.EntityId);
                Bytes.WriteUInt32(b.Slice(at + 4), (uint)s.Position.X);
                Bytes.WriteUInt32(b.Slice(at + 8), (uint)s.Position.Y);
                Bytes.WriteUInt32(b.Slice(at + 12), (uint)s.UnitsPerTickX10);
                at += JournalSpawn.WireBytes;
            }

            _out.Write(_buffer, 0, at);
        }

        /// <summary>Изменения ввода перед тактом <paramref name="tick"/>; пусто — ничего не пишется.</summary>
        public void WriteInputs(long tick, ReadOnlySpan<EntityInput> inputs)
        {
            if (inputs.Length == 0)
            {
                return;
            }

            int size = 1 + 4 + 2 + inputs.Length * InputEntryBytes;
            Span<byte> b = Reserve(size);
            b[0] = (byte)JournalRecord.Inputs;
            Bytes.WriteUInt32(b.Slice(1), CheckedTick(tick));
            Bytes.WriteUInt16(b.Slice(5), CheckedCount(inputs.Length));
            int at = 7;
            for (int i = 0; i < inputs.Length; i++)
            {
                Bytes.WriteUInt32(b.Slice(at), (uint)inputs[i].EntityId);
                inputs[i].Frame.Write(b.Slice(at + 4));
                at += InputEntryBytes;
            }

            _out.Write(_buffer, 0, size);
        }

        /// <summary>Точка сверки после такта: хэш состояния и хэши сущностей по номеру.</summary>
        public void WriteCheckpoint(in StateHashResult hash, ReadOnlySpan<SimEntity> entities)
        {
            int size = 1 + 4 + 8 + 8 + 2 + entities.Length * HashEntryBytes;
            Span<byte> b = Reserve(size);
            b[0] = (byte)JournalRecord.Checkpoint;
            Bytes.WriteUInt32(b.Slice(1), CheckedTick(hash.Tick));
            Bytes.WriteUInt64(b.Slice(5), hash.Total);
            Bytes.WriteUInt64(b.Slice(13), hash.World);
            Bytes.WriteUInt16(b.Slice(21), CheckedCount(entities.Length));
            int at = 23;
            for (int i = 0; i < entities.Length; i++)
            {
                Bytes.WriteUInt32(b.Slice(at), (uint)entities[i].Id);
                Bytes.WriteUInt64(b.Slice(at + 4), StateHash.Entity(entities[i]));
                at += HashEntryBytes;
            }

            _out.Write(_buffer, 0, size);
        }

        /// <summary>
        /// Точка сверки из готовых хэшей (копия или обрезка журнала): номера и хэши сущностей по номеру.
        /// </summary>
        public void WriteCheckpoint(in StateHashResult hash, ReadOnlySpan<int> entityIds, ReadOnlySpan<ulong> entityHashes)
        {
            if (entityIds.Length != entityHashes.Length)
            {
                throw new ArgumentException("Номеров и хэшей сущностей должно быть поровну.", nameof(entityHashes));
            }

            int size = 1 + 4 + 8 + 8 + 2 + entityIds.Length * HashEntryBytes;
            Span<byte> b = Reserve(size);
            b[0] = (byte)JournalRecord.Checkpoint;
            Bytes.WriteUInt32(b.Slice(1), CheckedTick(hash.Tick));
            Bytes.WriteUInt64(b.Slice(5), hash.Total);
            Bytes.WriteUInt64(b.Slice(13), hash.World);
            Bytes.WriteUInt16(b.Slice(21), CheckedCount(entityIds.Length));
            int at = 23;
            for (int i = 0; i < entityIds.Length; i++)
            {
                Bytes.WriteUInt32(b.Slice(at), (uint)entityIds[i]);
                Bytes.WriteUInt64(b.Slice(at + 4), entityHashes[i]);
                at += HashEntryBytes;
            }

            _out.Write(_buffer, 0, size);
        }

        /// <summary>Конец записи: последний выполненный такт.</summary>
        public void WriteEnd(long lastTick)
        {
            Span<byte> b = Reserve(5);
            b[0] = (byte)JournalRecord.End;
            Bytes.WriteUInt32(b.Slice(1), CheckedTick(lastTick));
            _out.Write(_buffer, 0, 5);
        }

        public void Flush() => _out.Flush();

        public void Dispose()
        {
            _out.Flush();
            if (_ownsStream)
            {
                _out.Dispose();
            }
        }

        private Span<byte> Reserve(int size)
        {
            if (_buffer.Length < size)
            {
                _buffer = new byte[size];
            }

            return _buffer;
        }

        private static uint CheckedTick(long tick)
        {
            if (tick < 0 || tick > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(tick), tick, "Такт в журнале — u32.");
            }

            return (uint)tick;
        }

        private static ushort CheckedCount(int n)
        {
            if (n > InputJournal.MaxEntries)
            {
                throw new ArgumentOutOfRangeException(nameof(n), n, "Записей больше u16.");
            }

            return (ushort)n;
        }
    }

    /// <summary>Чтение журнала ввода записями по порядку.</summary>
    public sealed class InputJournalReader : IDisposable
    {
        private readonly Stream _in;
        private readonly bool _ownsStream;
        private readonly byte _format;
        private byte[] _buffer = new byte[4096];
        private EntityInput[] _inputs = new EntityInput[16];
        private int _inputCount;
        private ulong[] _entityHashes = new ulong[16];
        private int[] _entityIds = new int[16];
        private int _hashCount;

        public InputJournalReader(Stream input, bool ownsStream = true)
        {
            _in = input ?? throw new ArgumentNullException(nameof(input));
            _ownsStream = ownsStream;
            ReadExactly(InputJournal.HeaderBytes);
            ReadOnlySpan<byte> b = _buffer;
            if (!b.Slice(0, 4).SequenceEqual(InputJournal.Magic))
            {
                throw new InvalidDataException("Журнал ввода: нет метки ORPJ.");
            }

            byte ver = b[4];
            if (ver < InputJournal.MinFormatVersion || ver > InputJournal.FormatVersion)
            {
                throw new InvalidDataException($"Журнал ввода: версия формата {ver}, умею {InputJournal.MinFormatVersion}…{InputJournal.FormatVersion}.");
            }

            _format = ver;

            ulong seed = Bytes.ReadUInt64(b, 5);
            int width = (int)Bytes.ReadUInt32(b, 13);
            int height = (int)Bytes.ReadUInt32(b, 17);
            bool wraps = b[21] != 0;
            int generatorVersion = (int)Bytes.ReadUInt32(b, 22);
            long startTick = (long)Bytes.ReadUInt64(b, 26);
            int count = Bytes.ReadUInt16(b, 34);
            MapShape shape;
            try
            {
                shape = new MapShape(width, height, wraps);
            }
            catch (ArgumentOutOfRangeException e)
            {
                throw new InvalidDataException("Журнал ввода: недопустимая форма карты.", e);
            }

            var spawns = new JournalSpawn[count];
            ReadExactly(count * JournalSpawn.WireBytes);
            for (int i = 0; i < count; i++)
            {
                int at = i * JournalSpawn.WireBytes;
                spawns[i] = new JournalSpawn(
                    (int)Bytes.ReadUInt32(_buffer, at),
                    new WorldPos((int)Bytes.ReadUInt32(_buffer, at + 4), (int)Bytes.ReadUInt32(_buffer, at + 8)),
                    (int)Bytes.ReadUInt32(_buffer, at + 12));
            }

            Header = new JournalHeader(seed, shape, generatorVersion, startTick, spawns);
        }

        public JournalHeader Header { get; }

        /// <summary>Такт последней прочитанной записи.</summary>
        public long Tick { get; private set; }

        /// <summary>Изменения ввода (после записи <see cref="JournalRecord.Inputs"/>).</summary>
        public ReadOnlySpan<EntityInput> Inputs => new ReadOnlySpan<EntityInput>(_inputs, 0, _inputCount);

        /// <summary>Хэш состояния точки сверки (после записи <see cref="JournalRecord.Checkpoint"/>).</summary>
        public StateHashResult CheckpointHash { get; private set; }

        /// <summary>Число сущностей в точке сверки.</summary>
        public int CheckpointEntityCount => _hashCount;

        /// <summary>Номер сущности № <paramref name="i"/> в точке сверки.</summary>
        public int CheckpointEntityId(int i) => _entityIds[i];

        /// <summary>Хэш сущности № <paramref name="i"/> в точке сверки.</summary>
        public ulong CheckpointEntityHash(int i) => _entityHashes[i];

        /// <summary>Следующая запись; <see cref="JournalRecord.None"/> — файл кончился без записи «конец».</summary>
        public JournalRecord Next()
        {
            int kind = _in.ReadByte();
            if (kind < 0)
            {
                return JournalRecord.None;
            }

            switch ((JournalRecord)kind)
            {
                case JournalRecord.Inputs:
                {
                    ReadExactly(6);
                    Tick = Bytes.ReadUInt32(_buffer, 0);
                    int n = Bytes.ReadUInt16(_buffer, 4);
                    if (_inputs.Length < n)
                    {
                        _inputs = new EntityInput[n];
                    }

                    int frameBytes = _format >= 2 ? InputFrame.WireBytes : InputFrame.LegacyWireBytes;
                    int entry = 4 + frameBytes;
                    ReadExactly(n * entry);
                    for (int i = 0; i < n; i++)
                    {
                        int at = i * entry;
                        InputFrame frame;
                        try
                        {
                            frame = _format >= 2
                                ? InputFrame.Read(new ReadOnlySpan<byte>(_buffer, at + 4, frameBytes))
                                : InputFrame.ReadV1(new ReadOnlySpan<byte>(_buffer, at + 4, InputFrame.LegacyWireBytes));
                        }
                        catch (ArgumentOutOfRangeException e)
                        {
                            throw new InvalidDataException($"Журнал ввода, такт {Tick}: испорчен кадр ввода.", e);
                        }

                        _inputs[i] = new EntityInput((int)Bytes.ReadUInt32(_buffer, at), frame);
                    }

                    _inputCount = n;
                    return JournalRecord.Inputs;
                }

                case JournalRecord.Checkpoint:
                {
                    ReadExactly(22);
                    Tick = Bytes.ReadUInt32(_buffer, 0);
                    CheckpointHash = new StateHashResult(Tick, Bytes.ReadUInt64(_buffer, 4), Bytes.ReadUInt64(_buffer, 12));
                    int n = Bytes.ReadUInt16(_buffer, 20);
                    if (_entityIds.Length < n)
                    {
                        _entityIds = new int[n];
                        _entityHashes = new ulong[n];
                    }

                    ReadExactly(n * 12);
                    for (int i = 0; i < n; i++)
                    {
                        _entityIds[i] = (int)Bytes.ReadUInt32(_buffer, i * 12);
                        _entityHashes[i] = Bytes.ReadUInt64(_buffer, i * 12 + 4);
                    }

                    _hashCount = n;
                    return JournalRecord.Checkpoint;
                }

                case JournalRecord.End:
                    ReadExactly(4);
                    Tick = Bytes.ReadUInt32(_buffer, 0);
                    return JournalRecord.End;

                default:
                    throw new InvalidDataException($"Журнал ввода: неизвестная запись 0x{kind:X2} после такта {Tick}.");
            }
        }

        public void Dispose()
        {
            if (_ownsStream)
            {
                _in.Dispose();
            }
        }

        private void ReadExactly(int count)
        {
            if (_buffer.Length < count)
            {
                _buffer = new byte[count];
            }

            int read = 0;
            while (read < count)
            {
                int n = _in.Read(_buffer, read, count - read);
                if (n <= 0)
                {
                    throw new EndOfStreamException($"Журнал ввода оборван: нужно {count} байт, есть {read}.");
                }

                read += n;
            }
        }
    }
}
