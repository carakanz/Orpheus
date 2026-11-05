using System;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Правка одного тайла в пачке правок чанка (docs/07, 3.7): индекс тайла в чанке (2 байта),
    /// пол (1), объект (2) и запись постройки (8, если есть) — 5…13 байт на тайл по сети.
    /// <para>
    /// Признак «есть запись» — бит 15 в слове индекса: индекс тайла в чанке ≤ 1023, старшие 6 бит свободны,
    /// поэтому пачка читается без внешнего счётчика и без потерь в размере 5…13 байт.
    /// </para>
    /// </summary>
    public readonly struct TileEdit : IEquatable<TileEdit>
    {
        /// <summary>Байт в пачке на тайл без записи постройки: 2 + 1 + 2.</summary>
        public const int WireBytesNoRecord = 5;

        /// <summary>Байт в пачке на тайл с записью постройки: 5 + 8.</summary>
        public const int WireBytesWithRecord = WireBytesNoRecord + BuildRecord.SizeBytes;

        /// <summary>Бит «есть запись постройки» в слове индекса.</summary>
        public const int HasRecordBit = 15;

        /// <summary>Маска индекса тайла в слове (индекс ≤ 1023).</summary>
        public const int IndexMask = 0x3FF;

        /// <summary>Индекс тайла в чанке, 0…1023 (<see cref="Chunk.MaxIndex"/>).</summary>
        public readonly ushort Index;

        /// <summary>Новый пол (0 — пола нет).</summary>
        public readonly byte Floor;

        /// <summary>Новый объект (сырое значение <see cref="ObjectId"/>; 0 — объекта нет).</summary>
        public readonly ushort Obj;

        /// <summary>Есть запись постройки. Правка задаёт тайл целиком: записи нет — прежняя снимается.</summary>
        public readonly bool HasRecord;

        /// <summary>Запись постройки (значима, только если <see cref="HasRecord"/>).</summary>
        public readonly BuildRecord Record;

        public TileEdit(ushort index, byte floor, ushort obj)
        {
            Index = index;
            Floor = floor;
            Obj = obj;
            HasRecord = false;
            Record = default;
        }

        public TileEdit(ushort index, byte floor, ushort obj, BuildRecord record)
        {
            Index = index;
            Floor = floor;
            Obj = obj;
            HasRecord = true;
            Record = record;
        }

        /// <summary>Байт в пачке на эту правку: 5 или 13 (docs/07, 3.7).</summary>
        public readonly int WireBytes => HasRecord ? WireBytesWithRecord : WireBytesNoRecord;

        /// <summary>Записать правку в <paramref name="dest"/> и вернуть число байт.</summary>
        public readonly int Write(Span<byte> dest)
        {
            int bytes = WireBytes;
            if (dest.Length < bytes)
            {
                throw new ArgumentException($"Нужно {bytes} байт, есть {dest.Length}.", nameof(dest));
            }

            ushort word = (ushort)(Index & IndexMask);
            if (HasRecord)
            {
                word |= (ushort)(1 << HasRecordBit);
            }

            dest[0] = (byte)word;
            dest[1] = (byte)(word >> 8);
            dest[2] = Floor;
            dest[3] = (byte)Obj;
            dest[4] = (byte)(Obj >> 8);
            if (HasRecord)
            {
                Record.Write(dest.Slice(WireBytesNoRecord));
            }

            return bytes;
        }

        /// <summary>Прочитать правку из <paramref name="src"/> и вернуть число прочитанных байт.</summary>
        public static int Read(ReadOnlySpan<byte> src, out TileEdit edit)
        {
            if (src.Length < WireBytesNoRecord)
            {
                throw new ArgumentException($"Нужно не меньше {WireBytesNoRecord} байт, есть {src.Length}.", nameof(src));
            }

            ushort word = (ushort)(src[0] | (src[1] << 8));
            bool hasRecord = (word & (1 << HasRecordBit)) != 0;
            int bytes = hasRecord ? WireBytesWithRecord : WireBytesNoRecord;
            if (src.Length < bytes)
            {
                throw new ArgumentException($"Нужно {bytes} байт, есть {src.Length}.", nameof(src));
            }

            edit = hasRecord
                ? new TileEdit(
                    (ushort)(word & IndexMask),
                    src[2],
                    (ushort)(src[3] | (src[4] << 8)),
                    BuildRecord.Read(src.Slice(WireBytesNoRecord)))
                : new TileEdit(
                    (ushort)(word & IndexMask),
                    src[2],
                    (ushort)(src[3] | (src[4] << 8)));
            return bytes;
        }

        public readonly bool Equals(TileEdit other) =>
            Index == other.Index && Floor == other.Floor && Obj == other.Obj &&
            HasRecord == other.HasRecord && Record == other.Record;

        public override readonly bool Equals(object? obj) => obj is TileEdit other && Equals(other);

        public override readonly int GetHashCode() =>
            unchecked((Index * 73856093) ^ (Floor * 19349663) ^ (Obj * 83545207) ^ (HasRecord ? 1 : 0));

        public static bool operator ==(TileEdit a, TileEdit b) => a.Equals(b);
        public static bool operator !=(TileEdit a, TileEdit b) => !a.Equals(b);

        public override readonly string ToString() =>
            HasRecord
                ? $"edit #{Index} floor {Floor} obj {Obj} +record"
                : $"edit #{Index} floor {Floor} obj {Obj}";
    }

    /// <summary>Исход одной правки в пачке (docs/07, 3.7: «принято» или «отказ: причина»).</summary>
    public enum EditResult : byte
    {
        /// <summary>Принято: тайл изменён в этом такте.</summary>
        Applied = 0,

        /// <summary>Отказ «занято»: на этот тайл в этой пачке правка уже прошла раньше (3.7, конфликт).</summary>
        Busy = 1,

        /// <summary>Отказ: индекс тайла вне чанка (0…1023).</summary>
        InvalidIndex = 2,

        /// <summary>Отказ: сырое значение объекта недопустимо (<see cref="ObjectId.IsValidRaw"/>).</summary>
        InvalidObject = 3,
    }
}
