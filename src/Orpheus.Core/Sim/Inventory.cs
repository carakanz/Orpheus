using System;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Материалы прежней сумки-счётчиков (1-002). С E2-1 это предметы в ячейках (<see cref="ItemTable.OfMaterial"/>);
    /// перечисление осталось именами для таблиц стоимости (<see cref="World.BuildDef"/>) и добычи (<see cref="DigLoot"/>).
    /// </summary>
    public enum Material : byte
    {
        /// <summary>Древесина.</summary>
        Wood = 0,

        /// <summary>Камень.</summary>
        Stone = 1,

        /// <summary>Смола.</summary>
        Resin = 2,

        /// <summary>Волокно.</summary>
        Fiber = 3,

        /// <summary>Медная руда.</summary>
        Copper = 4,

        /// <summary>Стрелы (боеприпас лука).</summary>
        Arrows = 5,

        /// <summary>Факелы.</summary>
        Torch = 6,

        /// <summary>Число материалов.</summary>
        Count = 7,
    }

    /// <summary>
    /// Сумка персонажа (E2-1, <c>06</c> 5.3): <see cref="Cells"/> ячеек, в каждой — стопка одного предмета
    /// (id из <see cref="ItemTable"/> и количество до размера стопки). Первые <see cref="Slots"/> ячеек — быстрая
    /// панель (клавиши 1–8); <see cref="Selected"/> — выбранная ячейка панели, её предмет — «в руках».
    /// <para>
    /// Структура — значение (без ссылок): лежит в <see cref="SimEntity"/> целиком, копируется с сущностью,
    /// целиком попадает в хэш состояния, сохраняется в записи персонажа и едет хозяину надёжным сообщением (тип 26).
    /// </para>
    /// <para>
    /// Порядок детерминирован: «положить» сначала доукладывает неполные стопки по возрастанию номера ячейки,
    /// потом занимает пустые — сперва ячейки сумки (<see cref="Slots"/>…), потом панель; «снять» берёт с конца.
    /// Списание — целиком или никак. Имена <see cref="Material"/> остались как короткая запись предмета.
    /// </para>
    /// </summary>
    public struct Inventory : IEquatable<Inventory>
    {
        /// <summary>Ячеек быстрой панели (<see cref="InputFrame.MaxSlots"/> = 8, <c>06</c> 5.3).</summary>
        public const int Slots = InputFrame.MaxSlots;

        /// <summary>Ячеек в сумке всего, вместе с панелью (<c>06</c> 5.3: 40 = 5 рядов × 8).</summary>
        public const int Cells = 40;

        // Ряд из 8 ячеек: ячейка — id в младшем байте, количество — в битах 8…23. Массивом нельзя: сумка — значение.
        private struct Row8
        {
            public uint C0, C1, C2, C3, C4, C5, C6, C7;

            public readonly uint Get(int i) => i switch
            {
                0 => C0,
                1 => C1,
                2 => C2,
                3 => C3,
                4 => C4,
                5 => C5,
                6 => C6,
                _ => C7,
            };

            public void Set(int i, uint v)
            {
                switch (i)
                {
                    case 0: C0 = v; break;
                    case 1: C1 = v; break;
                    case 2: C2 = v; break;
                    case 3: C3 = v; break;
                    case 4: C4 = v; break;
                    case 5: C5 = v; break;
                    case 6: C6 = v; break;
                    default: C7 = v; break;
                }
            }
        }

        private Row8 _r0, _r1, _r2, _r3, _r4;

        /// <summary>Выбранная ячейка быстрой панели 0…<see cref="Slots"/>−1 — предмет в руках.</summary>
        public byte Selected;

        private readonly uint Raw(int cell) => (cell >> 3) switch
        {
            0 => _r0.Get(cell & 7),
            1 => _r1.Get(cell & 7),
            2 => _r2.Get(cell & 7),
            3 => _r3.Get(cell & 7),
            4 => _r4.Get(cell & 7),
            _ => 0u,
        };

        private void SetRaw(int cell, uint v)
        {
            switch (cell >> 3)
            {
                case 0: _r0.Set(cell & 7, v); break;
                case 1: _r1.Set(cell & 7, v); break;
                case 2: _r2.Set(cell & 7, v); break;
                case 3: _r3.Set(cell & 7, v); break;
                case 4: _r4.Set(cell & 7, v); break;
            }
        }

        private static uint Pack(byte item, int count) => count <= 0 ? 0u : item | ((uint)count << 8);

        /// <summary>
        /// Стартовый набор нового персонажа (<c>06</c> 0 + карточка 1-002), раскладка по ячейкам (E2-1): панель —
        /// кирка, топор, молот, меч, лук, факел × 3; первая ячейка сумки — стрелы × 50.
        /// </summary>
        public static Inventory StartingKit()
        {
            var inv = new Inventory();
            inv.SetCell(0, Items.StonePickaxe, 1);
            inv.SetCell(1, Items.StoneAxe, 1);
            inv.SetCell(2, Items.Hammer, 1);
            inv.SetCell(3, Items.WoodenSword, 1);
            inv.SetCell(4, Items.SimpleBow, 1);
            inv.SetCell(5, Items.Torch, 3);
            inv.SetCell(Slots, Items.Arrows, 50);
            return inv;
        }

        // ---------- Ячейки ----------

        /// <summary>Предмет в ячейке (0 — пусто); вне 0…<see cref="Cells"/>−1 — 0.</summary>
        public readonly byte ItemAt(int cell) => (uint)cell < Cells ? (byte)Raw(cell) : Items.None;

        /// <summary>Количество в ячейке; пустая или вне сумки — 0.</summary>
        public readonly int CountIn(int cell) => (uint)cell < Cells ? (int)(Raw(cell) >> 8) : 0;

        /// <summary>
        /// Положить в ячейку ровно это (прежнее содержимое пропадает): количество ≤ 0 или предмет «пусто» — ячейка пустая;
        /// больше стопки — обрезается до стопки. Ячейка вне сумки или предмета нет в таблице — false.
        /// </summary>
        public bool SetCell(int cell, byte item, int count)
        {
            if ((uint)cell >= Cells)
            {
                return false;
            }

            if (item == Items.None || count <= 0)
            {
                SetRaw(cell, 0);
                return true;
            }

            int max = ItemTable.MaxStackOf(item);
            if (max == 0)
            {
                return false;
            }

            SetRaw(cell, Pack(item, Math.Min(count, max)));
            return true;
        }

        /// <summary>Предмет в ячейке панели <paramref name="slot"/>; вне 0…7 — 0.</summary>
        public readonly byte SlotAt(int slot) => (uint)slot < Slots ? ItemAt(slot) : Items.None;

        /// <summary>Положить один предмет в ячейку панели (0 — очистить); вне 0…<see cref="Slots"/>−1 — false.</summary>
        public bool SetSlotAt(int slot, byte itemId) => (uint)slot < Slots && SetCell(slot, itemId, 1);

        /// <summary>Выбрать ячейку панели (вне 0…<see cref="Slots"/>−1 — false, сумка не меняется).</summary>
        public bool Select(int slot)
        {
            if ((uint)slot >= (uint)Slots)
            {
                return false;
            }

            Selected = (byte)slot;
            return true;
        }

        /// <summary>Предмет в руках (ячейка <see cref="Selected"/>); пустая ячейка — <see cref="Items.None"/>.</summary>
        public readonly byte Held => SlotAt(Selected);

        // ---------- Количество по всей сумке ----------

        /// <summary>Сколько предмета во всех ячейках.</summary>
        public readonly int Count(byte item)
        {
            if (item == Items.None)
            {
                return 0;
            }

            int n = 0;
            for (int i = 0; i < Cells; i++)
            {
                uint raw = Raw(i);
                if ((byte)raw == item)
                {
                    n += (int)(raw >> 8);
                }
            }

            return n;
        }

        /// <summary>Сколько ещё такого предмета влезет: место в неполных стопках и пустые ячейки.</summary>
        public readonly int RoomFor(byte item)
        {
            int max = ItemTable.MaxStackOf(item);
            int room = 0;
            for (int i = 0; i < Cells && max > 0; i++)
            {
                uint raw = Raw(i);
                if (raw == 0)
                {
                    room += max;
                }
                else if ((byte)raw == item)
                {
                    room += Math.Max(0, max - (int)(raw >> 8));
                }
            }

            return room;
        }

        /// <summary>Занятых ячеек.</summary>
        public readonly int UsedCells
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Cells; i++)
                {
                    n += Raw(i) != 0 ? 1 : 0;
                }

                return n;
            }
        }

        /// <summary>
        /// Положить предмет: сперва доукладка в неполные стопки (по номеру ячейки), потом пустые ячейки — сумка
        /// (<see cref="Slots"/>…<see cref="Cells"/>−1), затем панель. Возвращает, сколько легло (остальное не влезло).
        /// </summary>
        public int Add(byte item, int n)
        {
            int max = ItemTable.MaxStackOf(item);
            if (n <= 0 || max == 0)
            {
                return 0;
            }

            int left = n;
            for (int i = 0; i < Cells && left > 0 && max > 1; i++)
            {
                uint raw = Raw(i);
                int have = (int)(raw >> 8);
                if ((byte)raw == item && have < max)
                {
                    int put = Math.Min(left, max - have);
                    SetRaw(i, Pack(item, have + put));
                    left -= put;
                }
            }

            for (int k = 0; k < Cells && left > 0; k++)
            {
                int i = k < Cells - Slots ? k + Slots : k - (Cells - Slots); // сумка, потом панель
                if (Raw(i) == 0)
                {
                    int put = Math.Min(left, max);
                    SetRaw(i, Pack(item, put));
                    left -= put;
                }
            }

            return n - left;
        }

        /// <summary>Снять предмет: целиком или никак; берётся с последних ячеек (панель — в последнюю очередь).</summary>
        public bool Remove(byte item, int n)
        {
            if (n < 0 || Count(item) < n)
            {
                return false;
            }

            int left = n;
            for (int i = Cells - 1; i >= 0 && left > 0; i--)
            {
                uint raw = Raw(i);
                if ((byte)raw == item)
                {
                    int have = (int)(raw >> 8);
                    int take = Math.Min(left, have);
                    SetRaw(i, Pack(item, have - take));
                    left -= take;
                }
            }

            return true;
        }

        /// <summary>Снять два предмета целиком или никак (стоимость постройки, 1-011): не хватает любого — false, ничего не снято.</summary>
        public bool Remove(byte item1, int n1, byte item2, int n2)
        {
            if (n2 <= 0)
            {
                return Remove(item1, n1);
            }

            if (n1 <= 0)
            {
                return Remove(item2, n2);
            }

            if (item1 == item2)
            {
                return Remove(item1, n1 + n2);
            }

            if (Count(item1) < n1 || Count(item2) < n2)
            {
                return false;
            }

            Remove(item1, n1);
            Remove(item2, n2);
            return true;
        }

        // ---------- Операции игрока с ячейками (заявки клиента, решает сервер) ----------

        /// <summary>
        /// Переместить ячейку <paramref name="from"/> в <paramref name="to"/>: пустая цель — перенос; тот же предмет —
        /// доукладка до стопки (остаток остаётся в исходной); другой предмет или обе стопки полные — обмен.
        /// Нечего переносить или ячейки вне сумки — false.
        /// </summary>
        public bool Move(int from, int to)
        {
            if ((uint)from >= Cells || (uint)to >= Cells || from == to || Raw(from) == 0)
            {
                return false;
            }

            uint a = Raw(from), b = Raw(to);
            if (b != 0 && (byte)a == (byte)b)
            {
                int put = Math.Min((int)(a >> 8), ItemTable.MaxStackOf((byte)a) - (int)(b >> 8));
                if (put > 0)
                {
                    SetRaw(to, Pack((byte)b, (int)(b >> 8) + put));
                    SetRaw(from, Pack((byte)a, (int)(a >> 8) - put));
                    return true;
                }
            }

            SetRaw(from, b);
            SetRaw(to, a);
            return true;
        }

        /// <summary>
        /// Разделить стопку: <paramref name="count"/> штук из <paramref name="from"/> в <paramref name="to"/> — в пустую
        /// ячейку или в стопку того же предмета, если влезает целиком. Иначе — false, сумка не меняется.
        /// </summary>
        public bool Split(int from, int to, int count)
        {
            if ((uint)from >= Cells || (uint)to >= Cells || from == to || count <= 0)
            {
                return false;
            }

            uint a = Raw(from), b = Raw(to);
            int have = (int)(a >> 8);
            if (a == 0 || count > have)
            {
                return false;
            }

            int there = 0;
            if (b != 0)
            {
                there = (int)(b >> 8);
                if ((byte)b != (byte)a || there + count > ItemTable.MaxStackOf((byte)a))
                {
                    return false;
                }
            }

            SetRaw(to, Pack((byte)a, there + count));
            SetRaw(from, Pack((byte)a, have - count));
            return true;
        }

        /// <summary>Вынуть из ячейки до <paramref name="count"/> штук (выброс на землю); возвращает, сколько вынуто.</summary>
        public int TakeFrom(int cell, int count)
        {
            int have = CountIn(cell);
            int take = Math.Min(have, count);
            if (take <= 0)
            {
                return 0;
            }

            SetRaw(cell, Pack(ItemAt(cell), have - take));
            return take;
        }

        // ---------- Материалы (короткая запись предмета, прежний интерфейс счётчиков) ----------

        /// <summary>Сколько материала во всей сумке; вне таблицы — 0.</summary>
        public readonly int CountAt(Material m) => Count(ItemTable.OfMaterial(m));

        /// <summary>Положить материал в ячейки. Возвращает, сколько легло.</summary>
        public int Add(Material m, int n) => Add(ItemTable.OfMaterial(m), n);

        /// <summary>Списать материал: целиком или никак.</summary>
        public bool Spend(Material m, int n) => Remove(ItemTable.OfMaterial(m), n);

        /// <summary>Списать два материала целиком или никак (стоимость постройки из нескольких стопок).</summary>
        public bool Spend(Material m1, int n1, Material m2, int n2) =>
            Remove(ItemTable.OfMaterial(m1), n1, ItemTable.OfMaterial(m2), n2);

        // ---------- Сравнение и хэш ----------

        public readonly bool Equals(Inventory o)
        {
            if (Selected != o.Selected)
            {
                return false;
            }

            for (int i = 0; i < Cells; i++)
            {
                if (Raw(i) != o.Raw(i))
                {
                    return false;
                }
            }

            return true;
        }

        public override readonly bool Equals(object? obj) => obj is Inventory o && Equals(o);

        public override readonly int GetHashCode()
        {
            var h = new Hasher64(0);
            AddTo(ref h);
            return (int)(h.Value >> 32) ^ (int)h.Value;
        }

        /// <summary>Слов в хэш сумки (для <see cref="StateHash"/>: проверка длины набора полей).</summary>
        public const int HashWords = 1 + Cells;

        /// <summary>Слова сумки в хэш: выбранная ячейка, затем ячейки по номеру (id и количество одним словом).</summary>
        public readonly void AddTo(ref Hasher64 h)
        {
            h.Add(Selected);
            for (int i = 0; i < Cells; i++)
            {
                h.Add((int)Raw(i));
            }
        }

        public static bool operator ==(Inventory a, Inventory b) => a.Equals(b);
        public static bool operator !=(Inventory a, Inventory b) => !a.Equals(b);

        // ---------- Запись ----------

        /// <summary>Размер payload сумки: выбранная ячейка 1 + ячейки (id 1 + количество 2) × 40 = 121.</summary>
        public const int PayloadBytes = 1 + 3 * Cells;

        /// <summary>Размер payload сумки-счётчиков до E2-1 (сохранения формата 4…6): 1 + 8 + 2 × 7 = 23.</summary>
        public const int LegacyPayloadBytes = 1 + Slots + 2 * (int)Material.Count;

        /// <summary>
        /// Записать payload: выбранная ячейка, затем на ячейку — id и количество u16 LE.
        /// <paramref name="dest"/> меньше <see cref="PayloadBytes"/> — <see cref="ArgumentException"/>.
        /// </summary>
        public readonly void WritePayload(Span<byte> dest)
        {
            if (dest.Length < PayloadBytes)
            {
                throw new ArgumentException($"payload сумки — {PayloadBytes} байт, дали {dest.Length}.", nameof(dest));
            }

            dest[0] = Selected;
            for (int i = 0; i < Cells; i++)
            {
                uint raw = Raw(i);
                dest[1 + 3 * i] = (byte)raw;
                dest[2 + 3 * i] = (byte)(raw >> 8);
                dest[3 + 3 * i] = (byte)(raw >> 16);
            }
        }

        /// <summary>
        /// Прочитать payload: <see cref="PayloadBytes"/> байт — ячейки (E2-1); ровно <see cref="LegacyPayloadBytes"/> —
        /// сумка-счётчики старого сохранения (<see cref="ReadLegacyPayload"/>). Другая длина, неизвестный предмет,
        /// количество больше стопки — <see cref="ArgumentException"/>.
        /// </summary>
        public static Inventory ReadPayload(ReadOnlySpan<byte> src)
        {
            if (src.Length == LegacyPayloadBytes)
            {
                return ReadLegacyPayload(src);
            }

            if (src.Length < PayloadBytes)
            {
                throw new ArgumentException($"payload сумки — {PayloadBytes} байт, дали {src.Length}.", nameof(src));
            }

            var inv = new Inventory();
            for (int i = 0; i < Cells; i++)
            {
                byte item = src[1 + 3 * i];
                int count = src[2 + 3 * i] | (src[3 + 3 * i] << 8);
                if ((item == Items.None) != (count == 0) || count > ItemTable.MaxStackOf(item))
                {
                    throw new ArgumentException($"Сумка: ячейка {i} — предмет {item} × {count}.", nameof(src));
                }

                inv.SetCell(i, item, count);
            }

            if (!inv.Select(src[0]))
            {
                throw new ArgumentException($"Сумка: выбранная ячейка {src[0]} >= {Slots}.", nameof(src));
            }

            return inv;
        }

        /// <summary>
        /// Сумка-счётчики (до E2-1) → ячейки: предметы панели остаются в своих ячейках (факел — стопкой из счётчика
        /// факелов; счётчик 0 — ячейка пустая), материалы и стрелы раскладываются стопками по сумке; факелы без
        /// ячейки на панели — тоже в сумку. Не влезло (больше 40 ячеек) — пропадает.
        /// </summary>
        public static Inventory ReadLegacyPayload(ReadOnlySpan<byte> src)
        {
            if (src.Length < LegacyPayloadBytes)
            {
                throw new ArgumentException($"payload старой сумки — {LegacyPayloadBytes} байт, дали {src.Length}.", nameof(src));
            }

            var inv = new Inventory();
            int torches = LegacyCount(src, Material.Torch);
            for (int i = 0; i < Slots; i++)
            {
                byte item = src[1 + i];
                if (item == Items.Torch)
                {
                    int put = Math.Min(torches, ItemTable.MaxStackOf(item));
                    inv.SetCell(i, item, put);
                    torches -= put;
                }
                else if (ItemTable.Exists(item))
                {
                    inv.SetCell(i, item, 1);
                }
            }

            inv.Add(Items.Torch, torches);
            for (int m = 0; m < (int)Material.Count; m++)
            {
                if ((Material)m != Material.Torch)
                {
                    inv.Add((Material)m, LegacyCount(src, (Material)m));
                }
            }

            inv.Select(src[0]);
            return inv;
        }

        private static int LegacyCount(ReadOnlySpan<byte> s, Material m) =>
            s[1 + Slots + 2 * (int)m] | (s[1 + Slots + 2 * (int)m + 1] << 8);

        public override readonly string ToString()
        {
            var sb = new System.Text.StringBuilder("inv(hands ");
            byte held = Held;
            sb.Append(held == Items.None ? "пусто" : "#" + held);
            for (int i = 0; i < Cells; i++)
            {
                if (Raw(i) != 0)
                {
                    sb.Append(", ").Append(i).Append(':').Append(ItemTable.Get(ItemAt(i)).Key).Append('×').Append(CountIn(i));
                }
            }

            return sb.Append(')').ToString();
        }
    }
}
