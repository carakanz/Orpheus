using System;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Запись постройки — 8 байт на тайл с постройкой игрока (docs/07, 3.3): словарь чанка
    /// «индекс тайла → запись» существует только у тайлов с постройками.
    /// <para>
    /// Поля и размеры: владелец — 2 байта (индекс игрока в мире, 0xFFFF — мир), прочность — 2 байта,
    /// флаги — 1 байт, природный пол под построенным — 1 байт, конец обломков — 2 байта
    /// (номер такта по модулю 65 536 — 36 мин при такте 30 Гц, docs/07, 3.3 и 3.11).
    /// </para>
    /// </summary>
    public readonly struct BuildRecord : IEquatable<BuildRecord>
    {
        /// <summary>Размер записи на диске и в сети: 8 байт.</summary>
        public const int SizeBytes = 8;

        /// <summary>Владелец — мир: 0xFFFF (docs/07, 3.3).</summary>
        public const ushort WorldOwner = ushort.MaxValue;

        /// <summary>Максимальная прочность (полная).</summary>
        public const ushort MaxIntegrity = ushort.MaxValue;

        /// <summary>Индекс игрока-владельца; <see cref="WorldOwner"/> — мир.</summary>
        public readonly ushort Owner;

        /// <summary>Прочность постройки (доля возврата при сносе, `05`, 1.3).</summary>
        public readonly ushort Integrity;

        /// <summary>Флаги — <see cref="BuildFlags"/>.</summary>
        public readonly byte Flags;

        /// <summary>Природный пол биома, который был под построенным (`05`, 1.3: снять — вернуть природный).</summary>
        public readonly byte NaturalFloor;

        /// <summary>Такт (mod 65 536), до которого тайл — обломки (10 с после сноса, `05`, 1.3).</summary>
        public readonly ushort DebrisEndTick;

        public BuildRecord(ushort owner, ushort integrity, byte flags, byte naturalFloor, ushort debrisEndTick)
        {
            Owner = owner;
            Integrity = integrity;
            Flags = flags;
            NaturalFloor = naturalFloor;
            DebrisEndTick = debrisEndTick;
        }

        /// <summary>Запись мира: владелец 0xFFFF, полная прочность, без обломков.</summary>
        public static BuildRecord World(ushort integrity, byte naturalFloor) =>
            new BuildRecord(WorldOwner, integrity, 0, naturalFloor, 0);

        /// <summary>Запись игрока № <paramref name="playerIndex"/> с полной прочностью.</summary>
        public static BuildRecord OfPlayer(ushort playerIndex, byte naturalFloor) =>
            new BuildRecord(playerIndex, MaxIntegrity, 0, naturalFloor, 0);

        /// <summary>Постройку поставил мир, не игрок.</summary>
        public readonly bool IsWorldOwned => Owner == WorldOwner;

        /// <summary>Флаги как типизированные биты.</summary>
        public readonly BuildFlags BuildFlags => (BuildFlags)Flags;

        /// <summary>Дверь открыта (или держится открытой) — тайл проходим (docs/05, 1.3).</summary>
        public readonly bool IsDoorOpen =>
            (Flags & (byte)(BuildFlags.DoorOpen | BuildFlags.DoorKeepOpen)) != 0;

        /// <summary>Тайл — обломки (`05`, 1.3): флаг ставит снос/разрушение, снимает истечение <see cref="DebrisEndTick"/>.</summary>
        public readonly bool IsDebris => (Flags & (byte)BuildFlags.Debris) != 0;

        /// <summary>Постройка получала урон последние 10 с (снос таких оставляет обломки, `05`, 1.3).</summary>
        public readonly bool IsDamagedRecent => (Flags & (byte)BuildFlags.DamagedRecent) != 0;

        /// <summary>Копия записи с добавленными битами (свои сохраняются).</summary>
        public readonly BuildRecord WithFlags(BuildFlags add) =>
            new BuildRecord(Owner, Integrity, (byte)(Flags | (byte)add), NaturalFloor, DebrisEndTick);

        /// <summary>Копия записи со снятыми битами (истечение «урона последние 10 с», 1-011).</summary>
        public readonly BuildRecord WithoutFlags(BuildFlags remove) =>
            new BuildRecord(Owner, Integrity, (byte)(Flags & ~(byte)remove), NaturalFloor, DebrisEndTick);

        /// <summary>Копия записи с новой прочностью (флаги и поля сохраняются).</summary>
        public readonly BuildRecord WithIntegrity(ushort integrity) =>
            new BuildRecord(Owner, integrity, Flags, NaturalFloor, DebrisEndTick);

        // ---------- Байты (младший байт первым) ----------

        /// <summary>Записать 8 байт в <paramref name="dest"/>.</summary>
        public readonly void Write(Span<byte> dest)
        {
            if (dest.Length < SizeBytes)
            {
                throw new ArgumentException($"Нужно {SizeBytes} байт, есть {dest.Length}.", nameof(dest));
            }

            dest[0] = (byte)Owner;
            dest[1] = (byte)(Owner >> 8);
            dest[2] = (byte)Integrity;
            dest[3] = (byte)(Integrity >> 8);
            dest[4] = Flags;
            dest[5] = NaturalFloor;
            dest[6] = (byte)DebrisEndTick;
            dest[7] = (byte)(DebrisEndTick >> 8);
        }

        /// <summary>Прочитать 8 байт.</summary>
        public static BuildRecord Read(ReadOnlySpan<byte> src)
        {
            if (src.Length < SizeBytes)
            {
                throw new ArgumentException($"Нужно {SizeBytes} байт, есть {src.Length}.", nameof(src));
            }

            return new BuildRecord(
                (ushort)(src[0] | (src[1] << 8)),
                (ushort)(src[2] | (src[3] << 8)),
                src[4],
                src[5],
                (ushort)(src[6] | (src[7] << 8)));
        }

        public readonly bool Equals(BuildRecord other) =>
            Owner == other.Owner &&
            Integrity == other.Integrity &&
            Flags == other.Flags &&
            NaturalFloor == other.NaturalFloor &&
            DebrisEndTick == other.DebrisEndTick;

        public override readonly bool Equals(object? obj) => obj is BuildRecord other && Equals(other);

        public override readonly int GetHashCode() =>
            (Owner | (Integrity << 16)) ^ (Flags << 3) ^ (NaturalFloor << 11) ^ (DebrisEndTick << 7);

        public static bool operator ==(BuildRecord a, BuildRecord b) => a.Equals(b);
        public static bool operator !=(BuildRecord a, BuildRecord b) => !a.Equals(b);

        public override readonly string ToString() =>
            $"build(owner {Owner}, integrity {Integrity}, flags {Flags}, floor {NaturalFloor}, debris {DebrisEndTick})";
    }

    /// <summary>
    /// Флаги записи постройки (1 байт, docs/07, 3.3): дверь закрыта / открыта / держать открытой, сундук общий.
    /// Дверь «закрыта» — открытого бита нет.
    /// </summary>
    [Flags]
    public enum BuildFlags : byte
    {
        None = 0,

        /// <summary>Дверь открыта сейчас (тайл проходим).</summary>
        DoorOpen = 1,

        /// <summary>Дверь держать открытой (кнопка «держать открытой», `05`, 1.3).</summary>
        DoorKeepOpen = 2,

        /// <summary>Сундук общий: руда обоих лежит в одном месте (`05`, 1.3).</summary>
        ChestShared = 4,

        /// <summary>Тайл — обломки сноса/разрушения (`05`, 1.3): проходим, строить нельзя до <see cref="BuildRecord.DebrisEndTick"/>.</summary>
        Debris = 8,

        /// <summary>Постройка получала урон последние 10 с: снос такого тайла оставляет обломки (`05`, 1.3; снимается по истечении).</summary>
        DamagedRecent = 16,
    }
}
