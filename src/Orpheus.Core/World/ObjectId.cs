using System;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Идентификатор объекта — 2 байта на тайл (docs/07, 3.3; docs/05, 1.3). 0 — пусто, объекта нет.
    /// <para>Раскладка ushort («сырое значение», как лежит в слое чанка):</para>
    /// <list type="bullet">
    /// <item>биты 0…9 — id объекта (0…1023, 0 = пусто);</item>
    /// <item>биты 10…11 — поворот якоря (0…3): «якорь хранит id и поворот» (3.3);</item>
    /// <item>значение 0xF000 + смещение — «часть» многотайлового объекта: по 4 бита на ось
    /// (младшие 4 бита — dx, следующие 4 — dy, 0…15, не оба нуля), якорь — левый верхний тайл (`05`, 1.3),
    /// то есть якорь лежит в (x − dx, y − dy) по тору.</item>
    /// </list>
    /// <para>
    /// Части лежат в диапазоне 0xF000…0xF0FF и не пересекаются с id (≤ 0xFFF), поэтому «часть»
    /// отличима от якоря одной маской. Смещение до якоря, а не координата якоря — из-за шва тора
    /// (docs/07, 3.11: объект 2 × 2 на шве).
    /// </para>
    /// <para>
    /// Свойства id — статическая таблица, без рефлексии. Проходимость двери зависит от флага записи
    /// (<see cref="BuildFlags.DoorOpen"/>) — для неё есть <see cref="IsPassable(ushort, byte)"/>.
    /// Вид объекта не хранится: вариант спрайта клиент берёт из SplitMix64(x, y) (3.3).
    /// </para>
    /// </summary>
    public static class ObjectId
    {
        /// <summary>0 — объекта нет.</summary>
        public const ushort Empty = 0;

        /// <summary>Число бит id в сыром значении: id занимает биты 0…9.</summary>
        public const int IdBits = 10;

        /// <summary>Маска id в сыром значении: 0x3FF.</summary>
        public const ushort IdMask = (1 << IdBits) - 1;

        /// <summary>Наибольший id (без поворота): 1023.</summary>
        public const ushort MaxId = IdMask;

        /// <summary>Сдвиг поворота в сыром значении: биты 10…11.</summary>
        public const int RotationShift = 10;

        /// <summary>Повороты 0…3 (4 стороны, `05`, 1.3: кровать, стул, верстаки).</summary>
        public const int RotationMask = 3;

        /// <summary>Маркер «часть многотайлового объекта»: 0xF000 (docs/07, 3.3).</summary>
        public const ushort PartMarker = 0xF000;

        /// <summary>Маска проверки «часть»: 0xFF00 (младшие 8 бит — смещение).</summary>
        public const ushort PartMask = 0xFF00;

        /// <summary>Смещение до якоря — по 4 бита на ось (dx — биты 0…3, dy — биты 4…7).</summary>
        public const int PartAxisBits = 4;

        /// <summary>Маска оси смещения: 0xF.</summary>
        public const int PartAxisMask = (1 << PartAxisBits) - 1;

        /// <summary>Наибольшее смещение части до якоря по оси: 15 (объект до 16 × 16 тайлов).</summary>
        public const int MaxPartOffset = PartAxisMask;

        /// <summary>Наибольшее сырое значение «части»: 0xF0FF.</summary>
        public const ushort MaxPart = PartMarker | 0xFF;

        // ---------- Эталонные id (для ядра и тестов; точные рецепты — `06`) ----------

        /// <summary>Проходимые объекты: не мешают ходить и не отнимают место у условия комнаты (`05`, 1.3).</summary>
        public const ushort Torch = 1;
        public const ushort Carpet = 2;
        public const ushort Spikes = 3;

        /// <summary>Твёрдые постройки игрока.</summary>
        public const ushort WoodWall = 4;
        public const ushort StoneWall = 5;
        public const ushort WoodFence = 6;

        /// <summary>Дверь и ворота: твёрдые, проходимы, когда открыты (флаг записи).</summary>
        public const ushort Door = 7;
        public const ushort Gate = 8;

        /// <summary>Мебель и хранение.</summary>
        public const ushort Chest = 9;
        public const ushort Bed = 10;
        public const ushort Workbench = 11;

        /// <summary>Многотайловые объекты (якорь — левый верхний тайл, размер — в таблице размеров).</summary>
        public const ushort Table = 12;
        public const ushort Forge = 13;

        /// <summary>Природные объекты (копаются, `01`, 5).</summary>
        public const ushort Tree = 20;
        public const ushort Rock = 21;
        public const ushort Ore = 22;
        public const ushort Stump = 23;

        /// <summary>Наибольший описанный id.</summary>
        public const ushort MaxKnownId = 23;

        /// <summary>Размер таблицы свойств по сырому значению якоря: 0…0xFFF (id + поворот).</summary>
        public const int TableSize = 0x1000;

        /// <summary>Свойства объекта.</summary>
        [Flags]
        public enum Properties : byte
        {
            None = 0,

            /// <summary>Объект мешает ходить (стена, скала, дерево).</summary>
            Solid = 1,

            /// <summary>Через тайл проходят (факел, шипы, ковёр — `05`, 1.3).</summary>
            Passable = 2,

            /// <summary>Природный объект: под постройку его не ставят, сначала копают (`05`, 1.3).</summary>
            Natural = 4,

            /// <summary>Якорь многотайлового объекта: у якоря лежит запись постройки, части записей не имеют.</summary>
            MultiTile = 8,

            /// <summary>Часть многотайлового объекта (0xF000 + смещение): свойства объекта — у якоря.</summary>
            Part = 16,
        }

        private static readonly Properties[] PropertiesTable = BuildTable();

        private static Properties[] BuildTable()
        {
            var table = new Properties[TableSize];

            // id 0 — пусто: проходимый, без свойств.
            table[Empty] = Properties.Passable;

            Set(Torch, Properties.Passable);
            Set(Carpet, Properties.Passable);
            Set(Spikes, Properties.Passable);

            Set(WoodWall, Properties.Solid);
            Set(StoneWall, Properties.Solid);
            Set(WoodFence, Properties.Solid);

            Set(Door, Properties.Solid);
            Set(Gate, Properties.Solid);

            Set(Chest, Properties.Solid);
            Set(Bed, Properties.Solid);
            Set(Workbench, Properties.Solid);

            Set(Table, Properties.Solid | Properties.MultiTile);
            Set(Forge, Properties.Solid | Properties.MultiTile);

            Set(Tree, Properties.Solid | Properties.Natural);
            Set(Rock, Properties.Solid | Properties.Natural);
            Set(Ore, Properties.Solid | Properties.Natural);
            Set(Stump, Properties.Solid | Properties.Natural);

            return table;

            void Set(ushort id, Properties properties)
            {
                // Заполняем все четыре поворота одного id.
                for (int rotation = 0; rotation <= RotationMask; rotation++)
                {
                    table[(ushort)(id | (rotation << RotationShift))] = properties;
                }
            }
        }

        // ---------- Якорь: id + поворот ----------

        /// <summary>Сырое значение якоря (или одиночного объекта) из id и поворота 0…3.</summary>
        public static ushort MakeAnchor(ushort id, byte rotation)
        {
            if (id > MaxId)
            {
                throw new ArgumentOutOfRangeException(nameof(id), id, $"Id объекта — от 0 до {MaxId} (10 бит).");
            }

            if (rotation > RotationMask)
            {
                throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "Поворот — 0…3.");
            }

            return (ushort)(id | (rotation << RotationShift));
        }

        /// <summary>Id объекта из сырого значения (для якоря и одиночного объекта).</summary>
        public static ushort IdOf(ushort raw) => (ushort)(raw & IdMask);

        /// <summary>Поворот якоря 0…3.</summary>
        public static byte RotationOf(ushort raw) => (byte)((raw >> RotationShift) & RotationMask);

        // ---------- Часть многотайлового объекта ----------

        /// <summary>Сырое значение «части»: 0xF000 + смещение до якоря (dx, dy — 0…15, не оба нуля).</summary>
        public static ushort MakePart(int dx, int dy)
        {
            if (dx < 0 || dx > MaxPartOffset)
            {
                throw new ArgumentOutOfRangeException(nameof(dx), dx, $"Смещение по X до якоря — 0…{MaxPartOffset}.");
            }

            if (dy < 0 || dy > MaxPartOffset)
            {
                throw new ArgumentOutOfRangeException(nameof(dy), dy, $"Смещение по Y до якоря — 0…{MaxPartOffset}.");
            }

            if (dx == 0 && dy == 0)
            {
                throw new ArgumentException("Часть со смещением (0, 0) — это сам якорь.", nameof(dx));
            }

            return (ushort)(PartMarker | (dy << PartAxisBits) | dx);
        }

        /// <summary>true — тайл «часть» многотайлового объекта (0xF001…0xF0FF).</summary>
        public static bool IsPart(ushort raw) => (raw & PartMask) == PartMarker && raw != PartMarker;

        /// <summary>Смещение по X от части до якоря (0…15).</summary>
        public static int PartOffsetX(ushort raw) => raw & PartAxisMask;

        /// <summary>Смещение по Y от части до якоря (0…15).</summary>
        public static int PartOffsetY(ushort raw) => (raw >> PartAxisBits) & PartAxisMask;

        /// <summary>
        /// Сырое значение допустимо в слое: якорь или одиночный объект (0…0xFFF) либо часть (0xF001…0xF0FF).
        /// </summary>
        public static bool IsValidRaw(ushort raw) => raw < TableSize || IsPart(raw);

        // ---------- Свойства ----------

        /// <summary>
        /// Свойства сырого значения: для якоря и одиночного объекта — по id; для «части» —
        /// <see cref="Properties.Part"/> (настоящие свойства — у якоря, его тайл даёт смещение).
        /// Недопустимое сырое значение — <see cref="Properties.None"/>.
        /// </summary>
        public static Properties Of(ushort raw)
        {
            if (raw < TableSize)
            {
                return PropertiesTable[raw];
            }

            return IsPart(raw) ? Properties.Part : Properties.None;
        }

        /// <summary>Объект мешает ходить.</summary>
        public static bool IsSolid(ushort raw) => (Of(raw) & Properties.Solid) != 0;

        /// <summary>Через тайл проходят (без учёта состояния двери).</summary>
        public static bool IsPassable(ushort raw) => (Of(raw) & Properties.Passable) != 0;

        /// <summary>
        /// Проходимость с учётом состояния постройки (docs/05, 1.3): дверь и ворота проходимы,
        /// когда открыты (<see cref="BuildFlags.DoorOpen"/>) или держатся открытыми.
        /// </summary>
        public static bool IsPassable(ushort raw, byte recordFlags)
        {
            ushort id = IdOf(raw);
            if (id == Door || id == Gate)
            {
                return (recordFlags & (byte)(BuildFlags.DoorOpen | BuildFlags.DoorKeepOpen)) != 0;
            }

            return IsPassable(raw);
        }

        /// <summary>Природный объект (скала, залежь, дерево): пол под него не кладут, сначала копают.</summary>
        public static bool IsNatural(ushort raw) => (Of(raw) & Properties.Natural) != 0;

        /// <summary>Якорь многотайлового объекта (у него запись постройки).</summary>
        public static bool IsMultiTileAnchor(ushort raw) => !IsPart(raw) && (Of(raw) & Properties.MultiTile) != 0;

        /// <summary>Id описан в таблице (0…<see cref="MaxKnownId"/>).</summary>
        public static bool KnownId(ushort id) => id <= MaxKnownId;
    }
}
