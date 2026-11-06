using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Protocol
{
    /// <summary>Сущность в снимке: номер, вид, позиция (квантована до 1/32 тайла), направление движения.</summary>
    public struct SnapshotEntity
    {
        public int Id;
        public byte Kind;

        /// <summary>Позиция после квантования: центр клетки 1/32 тайла (в 1/256 тайла).</summary>
        public WorldPos Position;

        /// <summary>Код направления ввода 0…8 (0 — стоит); для интерполяции и анимации.</summary>
        public byte Direction;

        public override string ToString() => $"#{Id} k{Kind} {Position} d{Direction}";
    }

    /// <summary>
    /// Разметка снимка для формы карты (docs/07, 4.3): позиция — 1/32 тайла (младшие 3 бита из 8 отброшены),
    /// бит на ось — сколько нужно для ширины карты: 1024² — 15, 4096² — 17. Обе стороны знают форму из входа.
    /// </summary>
    public readonly struct SnapshotLayout
    {
        /// <summary>Отброшено бит позиции: 1/256 → 1/32 тайла.</summary>
        public const int QuantShift = 3;

        /// <summary>Бит на номер, вид, направление.</summary>
        public const int IdBits = 32;
        public const int KindBits = 8;
        public const int DirectionBits = 4;

        public readonly MapShape Shape;
        public readonly int BitsX;
        public readonly int BitsY;

        public SnapshotLayout(MapShape shape)
        {
            Shape = shape;
            BitsX = BitLength(shape.WidthUnits - 1) - QuantShift;
            BitsY = BitLength(shape.HeightUnits - 1) - QuantShift;
        }

        /// <summary>Бит на сущность.</summary>
        public int EntityBits => IdBits + KindBits + BitsX + BitsY + DirectionBits;

        /// <summary>Сколько сущностей влезает в бюджет <see cref="NetSettings.SnapshotBudget"/>.</summary>
        public int MaxEntitiesInBudget => Math.Min(Snapshot.MaxEntities, (NetSettings.SnapshotBudget - Snapshot.HeaderBytes) * 8 / EntityBits);

        /// <summary>Байт снимка с <paramref name="count"/> сущностями.</summary>
        public int SizeFor(int count) => Snapshot.HeaderBytes + (count * EntityBits + 7) / 8;

        /// <summary>Позиция → клетка 1/32 → обратно (то, что увидит клиент).</summary>
        public static WorldPos Quantize(WorldPos p) =>
            new WorldPos(((p.X >> QuantShift) << QuantShift) | (1 << (QuantShift - 1)), ((p.Y >> QuantShift) << QuantShift) | (1 << (QuantShift - 1)));

        private static int BitLength(int v)
        {
            int n = 0;
            while (v > 0)
            {
                n++;
                v >>= 1;
            }

            return Math.Max(n, QuantShift + 1);
        }
    }

    /// <summary>
    /// Полный снимок (docs/07, 4.3; дельты — следующей задачей): такт сервера, последний применённый кадр ввода
    /// этого клиента, сущности рядом. Своя сущность — первой. Объект переиспользуется: чтение не выделяет память.
    /// <para>Схема: тип 1 байт, такт u32, подтверждённый кадр u32, число сущностей u8; затем сущности по битам
    /// (<see cref="SnapshotLayout"/>).</para>
    /// </summary>
    public sealed class Snapshot
    {
        /// <summary>Заголовок: тип 1 + такт 4 + кадр 4 + число 1.</summary>
        public const int HeaderBytes = 10;

        /// <summary>Наибольшее число сущностей в снимке (поле — u8).</summary>
        public const int MaxEntities = 255;

        private const int CountOffset = 9;

        public uint Tick;
        public uint AckFrame;
        public int Count;
        public readonly SnapshotEntity[] Entities = new SnapshotEntity[MaxEntities];

        /// <summary>Сущность по номеру; нет — false.</summary>
        public bool TryFind(int id, out SnapshotEntity e)
        {
            for (int i = 0; i < Count; i++)
            {
                if (Entities[i].Id == id)
                {
                    e = Entities[i];
                    return true;
                }
            }

            e = default;
            return false;
        }

        /// <summary>Скопировать в <paramref name="into"/>.</summary>
        public void CopyTo(Snapshot into)
        {
            into.Tick = Tick;
            into.AckFrame = AckFrame;
            into.Count = Count;
            Array.Copy(Entities, into.Entities, Count);
        }

        /// <summary>Записать снимок из этого объекта (все <see cref="Count"/> сущностей; бюджет не проверяется).</summary>
        public int Write(Span<byte> dest, in SnapshotLayout layout)
        {
            var w = new SnapshotWriter(dest, layout, Tick, AckFrame, dest.Length);
            for (int i = 0; i < Count; i++)
            {
                if (!w.TryAdd(Entities[i].Id, Entities[i].Kind, Entities[i].Position, Entities[i].Direction))
                {
                    throw new ProtocolException($"Снимок: {Count} сущностей не влезли в {dest.Length} байт.");
                }
            }

            return w.Finish();
        }

        /// <summary>Прочитать снимок в этот объект.</summary>
        public void Read(ReadOnlySpan<byte> src, in SnapshotLayout layout)
        {
            BitReader r = Codec.Begin(src, MessageType.Snapshot);
            uint tick = r.ReadUInt32();
            uint ack = r.ReadUInt32();
            int count = r.ReadByte();
            for (int i = 0; i < count; i++)
            {
                ref SnapshotEntity e = ref Entities[i];
                e.Id = (int)r.ReadBits(SnapshotLayout.IdBits);
                e.Kind = (byte)r.ReadBits(SnapshotLayout.KindBits);
                int qx = (int)r.ReadBits(layout.BitsX);
                int qy = (int)r.ReadBits(layout.BitsY);
                e.Position = new WorldPos(
                    (qx << SnapshotLayout.QuantShift) | (1 << (SnapshotLayout.QuantShift - 1)),
                    (qy << SnapshotLayout.QuantShift) | (1 << (SnapshotLayout.QuantShift - 1)));
                e.Direction = (byte)r.ReadBits(SnapshotLayout.DirectionBits);
                if (e.Direction > InputFrame.MaxDirectionCode || e.Position.X >= layout.Shape.WidthUnits || e.Position.Y >= layout.Shape.HeightUnits)
                {
                    throw new ProtocolException($"Снимок: сущность {e} вне карты или с кодом направления > 8.");
                }
            }

            r.ExpectEnd();
            Tick = tick;
            AckFrame = ack;
            Count = count;
        }

        /// <summary>Номер сущности в заголовке не читая всего снимка — такт (для отбрасывания старых).</summary>
        public static uint PeekTick(ReadOnlySpan<byte> src)
        {
            BitReader r = Codec.Begin(src, MessageType.Snapshot);
            return r.ReadUInt32();
        }

        internal static void PatchCount(Span<byte> dest, int count) => dest[CountOffset] = (byte)count;
    }

    /// <summary>
    /// Запись снимка по одной сущности без выделений: <see cref="TryAdd"/> отказывает, когда следующая сущность
    /// не влезает в лимит байт (бюджет 1200) или в 255 — сервер пишет ошибку в лог (4.3) и шлёт то, что влезло.
    /// </summary>
    public ref struct SnapshotWriter
    {
        private readonly Span<byte> _dest;
        private readonly SnapshotLayout _layout;
        private readonly int _limitBits;
        private BitWriter _w;
        private int _count;

        public SnapshotWriter(Span<byte> dest, in SnapshotLayout layout, uint tick, uint ackFrame, int limitBytes = NetSettings.SnapshotBudget)
        {
            _dest = dest;
            _layout = layout;
            _limitBits = Math.Min(limitBytes, dest.Length) * 8;
            _w = new BitWriter(dest);
            _count = 0;
            _w.WriteByte((byte)MessageType.Snapshot);
            _w.WriteUInt32(tick);
            _w.WriteUInt32(ackFrame);
            _w.WriteByte(0); // число сущностей — в Finish
        }

        /// <summary>Сущностей записано.</summary>
        public int Count => _count;

        /// <summary>Добавить сущность; не влезает — false (ничего не записано).</summary>
        public bool TryAdd(int id, byte kind, WorldPos position, byte direction)
        {
            if (_count == Snapshot.MaxEntities || _w.BitPosition + _layout.EntityBits > _limitBits)
            {
                return false;
            }

            _w.WriteBits((uint)id, SnapshotLayout.IdBits);
            _w.WriteBits(kind, SnapshotLayout.KindBits);
            _w.WriteBits((uint)(position.X >> SnapshotLayout.QuantShift), _layout.BitsX);
            _w.WriteBits((uint)(position.Y >> SnapshotLayout.QuantShift), _layout.BitsY);
            _w.WriteBits(direction, SnapshotLayout.DirectionBits);
            _count++;
            return true;
        }

        /// <summary>Закрыть снимок: записать число сущностей; вернуть длину в байтах.</summary>
        public int Finish()
        {
            Snapshot.PatchCount(_dest, _count);
            return _w.BytesWritten;
        }
    }
}
