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
        /// <summary>Размер записи на диске и в сети: 9 байт (E2-15: + байт <see cref="Layout"/>; до формата 11 / протокола 27 — 8).</summary>
        public const int SizeBytes = 9;

        /// <summary>Размер записи старых блоков (форматы сохранения 1–10, снимок мира 2): без байта <see cref="Layout"/>.</summary>
        public const int LegacySizeBytes = 8;

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

        /// <summary>
        /// Байт «природный пол + ячейки» (E2-12): младшие 4 бита — природный пол биома, который был под построенным
        /// (`05`, 1.3: снять — вернуть природный; id полов &lt; 16), старшие 4 — маска занятых ячеек 0,5 × 0,5 у стены,
        /// забора, двери (<see cref="BuildCells"/>; 0 — весь тайл). На диске и в сети — как есть.
        /// </summary>
        public readonly byte FloorCells;

        /// <summary>Природный пол биома, который был под построенным (`05`, 1.3: снять — вернуть природный).</summary>
        public readonly byte NaturalFloor => (byte)(FloorCells & 0x0F);

        /// <summary>Маска ячеек постройки-ячеек (E2-12, бит <see cref="BuildCells.Bit"/>); 0 — весь тайл (старые миры, правки без маски).</summary>
        public readonly byte Cells => (byte)(FloorCells >> 4);

        /// <summary>Такт (mod 65 536), до которого тайл — обломки (10 с после сноса, `05`, 1.3). Для двери —
        /// такт (mod 65 536) автозакрытия (1-012, 0 = таймер не заведён); обломки и дверь на тайле не сосуществуют.</summary>
        public readonly ushort DebrisEndTick;

        /// <summary>
        /// Байт «слои» (E2-15, записка <c>work/design/half_tile_all.md</c>): младшие 4 бита — маска ячеек построенного пола
        /// (<see cref="BuildCells.Bit"/>; 0 — весь тайл: старые миры), старшие 4 — сдвиг мебели от СЗ ячейки тайла-якоря:
        /// биты 4–5 — X, 6–7 — Y, знаковые 2 бита (−1, 0, +1 ячейки; 0 — без сдвига).
        /// </summary>
        public readonly byte Layout;

        public BuildRecord(ushort owner, ushort integrity, byte flags, byte naturalFloor, ushort debrisEndTick)
            : this(owner, integrity, flags, naturalFloor, debrisEndTick, 0, true)
        {
        }

        // Все поля как есть (байт пола + ячеек и байт слоёв — сырые).
        private BuildRecord(ushort owner, ushort integrity, byte flags, byte floorCells, ushort debrisEndTick, byte layout, bool raw)
        {
            Owner = owner;
            Integrity = integrity;
            Flags = flags;
            FloorCells = floorCells;
            DebrisEndTick = debrisEndTick;
            Layout = layout;
        }

        /// <summary>Запись с маской ячеек (E2-12): <paramref name="naturalFloor"/> &lt; 16, <paramref name="cells"/> — 4 бита.</summary>
        public BuildRecord(ushort owner, ushort integrity, byte flags, byte naturalFloor, ushort debrisEndTick, byte cells)
            : this(owner, integrity, flags, (byte)((CheckFloor(naturalFloor) & 0x0F) | ((cells & 0x0F) << 4)), debrisEndTick)
        {
        }

        // Пол записи — младшие 4 бита (E2-12): id пола ≥ 16 перепутался бы с маской ячеек — ошибка, а не тихая порча.
        private static byte CheckFloor(byte floor) =>
            floor <= 15 ? floor : throw new ArgumentOutOfRangeException(nameof(floor), floor, "Пол записи постройки — 4 бита (E2-12): id пола ≥ 16 — расширить запись.");

        /// <summary>Маска ячеек построенного пола (E2-15); 0 — весь тайл (старые миры). Смысл есть, только если пол тайла построенный.</summary>
        public readonly byte FloorMask => (byte)(Layout & 0x0F);

        /// <summary>Сдвиг мебели по X от СЗ ячейки тайла-якоря, ячеек (−1, 0, +1; E2-15).</summary>
        public readonly int OffsetX => Signed2(Layout >> 4);

        /// <summary>Сдвиг мебели по Y, ячеек (−1, 0, +1; E2-15).</summary>
        public readonly int OffsetY => Signed2(Layout >> 6);

        /// <summary>У мебели есть сдвиг на полтайла (E2-15).</summary>
        public readonly bool HasOffset => (Layout & 0xF0) != 0;

        private static int Signed2(int v) => ((v & 3) ^ 2) - 2;

        /// <summary>Копия записи с маской ячеек пола <paramref name="mask"/> (E2-15; 4 бита).</summary>
        public readonly BuildRecord WithFloorMask(byte mask) =>
            new BuildRecord(Owner, Integrity, Flags, FloorCells, DebrisEndTick, (byte)((Layout & 0xF0) | (mask & 0x0F)), true);

        /// <summary>Копия записи со сдвигом мебели (<paramref name="dx"/>, <paramref name="dy"/> ∈ −1…+1 ячейки; E2-15).</summary>
        public readonly BuildRecord WithOffset(int dx, int dy)
        {
            if (dx < -1 || dx > 1 || dy < -1 || dy > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(dx), $"Сдвиг мебели — −1…+1 ячейки, дано ({dx}, {dy}).");
            }

            return new BuildRecord(Owner, Integrity, Flags, FloorCells, DebrisEndTick, (byte)((Layout & 0x0F) | ((dx & 3) << 4) | ((dy & 3) << 6)), true);
        }

        /// <summary>
        /// Запись нового верха поверх слоя пола записи <paramref name="under"/> (E2-15): владелец, прочность, ячейки стены — новые,
        /// флаги и обломки сняты; природный пол и маска пола — из прежней записи (стена на полу не теряет пол).
        /// </summary>
        public static BuildRecord Over(in BuildRecord under, ushort owner, ushort integrity, byte cells) =>
            new BuildRecord(owner, integrity, 0, (byte)((under.FloorCells & 0x0F) | ((cells & 0x0F) << 4)), 0, (byte)(under.Layout & 0x0F), true);

        /// <summary>Запись «только пол» после сноса или разрушения верха (E2-15): ячейки, сдвиг, флаги и обломки сняты, пол — прежний.</summary>
        public readonly BuildRecord FloorOnly() =>
            new BuildRecord(Owner, Integrity, 0, (byte)(FloorCells & 0x0F), 0, (byte)(Layout & 0x0F), true);

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

        /// <summary>Постройка получала урон последние 3 с — чинить нельзя (`05` 1.3 «Починка», C-005).</summary>
        public readonly bool IsRepairBlocked => (Flags & (byte)BuildFlags.RepairBlocked) != 0;

        /// <summary>Копия записи с добавленными битами (свои сохраняются).</summary>
        public readonly BuildRecord WithFlags(BuildFlags add) =>
            new BuildRecord(Owner, Integrity, (byte)(Flags | (byte)add), FloorCells, DebrisEndTick, Layout, true);

        /// <summary>Копия записи со снятыми битами (истечение «урона последние 10 с», 1-011).</summary>
        public readonly BuildRecord WithoutFlags(BuildFlags remove) =>
            new BuildRecord(Owner, Integrity, (byte)(Flags & ~(byte)remove), FloorCells, DebrisEndTick, Layout, true);

        /// <summary>Копия записи с новой прочностью (флаги и поля сохраняются).</summary>
        public readonly BuildRecord WithIntegrity(ushort integrity) =>
            new BuildRecord(Owner, integrity, Flags, FloorCells, DebrisEndTick, Layout, true);

        /// <summary>Копия записи с новым тактом истечения (обломки; для двери — дедлайн автозакрытия, 1-012).</summary>
        public readonly BuildRecord WithDebrisEndTick(ushort endTick) =>
            new BuildRecord(Owner, Integrity, Flags, FloorCells, endTick, Layout, true);

        /// <summary>Копия записи с маской ячеек <paramref name="cells"/> (E2-12; пол и прочее сохраняются).</summary>
        public readonly BuildRecord WithCells(byte cells) =>
            new BuildRecord(Owner, Integrity, Flags, (byte)((FloorCells & 0x0F) | ((cells & 0x0F) << 4)), DebrisEndTick, Layout, true);

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
            dest[5] = FloorCells;
            dest[6] = (byte)DebrisEndTick;
            dest[7] = (byte)(DebrisEndTick >> 8);
            dest[8] = Layout;
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
                (ushort)(src[6] | (src[7] << 8)),
                src[8],
                true);
        }

        /// <summary>Прочитать запись старого блока (<see cref="LegacySizeBytes"/> = 8 байт, до E2-15): байт слоёв — 0.</summary>
        public static BuildRecord ReadLegacy(ReadOnlySpan<byte> src)
        {
            if (src.Length < LegacySizeBytes)
            {
                throw new ArgumentException($"Нужно {LegacySizeBytes} байт, есть {src.Length}.", nameof(src));
            }

            return new BuildRecord(
                (ushort)(src[0] | (src[1] << 8)),
                (ushort)(src[2] | (src[3] << 8)),
                src[4],
                src[5],
                (ushort)(src[6] | (src[7] << 8)),
                0,
                true);
        }

        public readonly bool Equals(BuildRecord other) =>
            Owner == other.Owner &&
            Integrity == other.Integrity &&
            Flags == other.Flags &&
            FloorCells == other.FloorCells &&
            DebrisEndTick == other.DebrisEndTick &&
            Layout == other.Layout;

        public override readonly bool Equals(object? obj) => obj is BuildRecord other && Equals(other);

        public override readonly int GetHashCode() =>
            (Owner | (Integrity << 16)) ^ (Flags << 3) ^ (FloorCells << 11) ^ (DebrisEndTick << 7) ^ (Layout << 21);

        public static bool operator ==(BuildRecord a, BuildRecord b) => a.Equals(b);
        public static bool operator !=(BuildRecord a, BuildRecord b) => !a.Equals(b);

        public override readonly string ToString() =>
            $"build(owner {Owner}, integrity {Integrity}, flags {Flags}, floor {NaturalFloor}, cells {Cells}, debris {DebrisEndTick}, layout {Layout})";
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

        /// <summary>
        /// Постройка получала урон последние 3 с: чинить нельзя (`05` 1.3 «Починка», C-005; снимается через 3 с после
        /// последнего урона). До C-005 починку запрещал <see cref="DamagedRecent"/> (10 с).
        /// </summary>
        RepairBlocked = 32,
    }
}
