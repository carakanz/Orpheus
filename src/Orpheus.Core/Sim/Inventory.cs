using System;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Материалы-счётчики сумки (1-002, <c>06</c> 0): без окна и стаков (стаки по 99 — инвентарь в вехе 2).
    /// Стрелы — счётчик боеприпаса (1-006), факел — расходник стартового набора (<c>06</c> 0).
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

        /// <summary>Число счётчиков.</summary>
        Count = 7,
    }

    /// <summary>
    /// Сумка персонажа (1-002, `06` 0, `02` 1.1 «Быстрая панель»): быстрая панель из
    /// <see cref="Slots"/> ячеек с id предметов (<see cref="Items"/>), выбранная ячейка (предмет в руках)
    /// и счётчики материалов (<see cref="Material"/>). Структура — значение: лежит в <see cref="SimEntity"/>
    /// целым, целиком попадает в хэш состояния (<see cref="StateHash.Entity"/>), сохраняется в записи
    /// персонажа (0-035) и едет хозяину надёжным сообщением (тип 26).
    /// <para>
    /// «Добавить/списать» — атомарны: списание целиком или никак (для 1-010, 1-011). Смена ячейки —
    /// кадром ввода: сервер применяет её в том же кадре, что и предсказание (<see cref="Simulation.Step"/>).
    /// </para>
    /// </summary>
    public struct Inventory : IEquatable<Inventory>
    {
        /// <summary>Ячеек быстрой панели (<see cref="InputFrame.MaxSlots"/> = 8, <c>02</c> 1.1 — прикидка).</summary>
        public const int Slots = InputFrame.MaxSlots;

        /// <summary>Потолок счётчика материала (стаки по 99 придут с окном инвентаря, веха 2).</summary>
        public const int MaxCount = ushort.MaxValue;

        private ushort _wood;
        private ushort _stone;
        private ushort _resin;
        private ushort _fiber;
        private ushort _copper;
        private ushort _arrows;
        private ushort _torches;

        /// <summary>Ячейки быстрой панели: id предметов (<see cref="Items"/>), 0 — пусто.</summary>
        public byte Slot0;
        public byte Slot1;
        public byte Slot2;
        public byte Slot3;
        public byte Slot4;
        public byte Slot5;
        public byte Slot6;
        public byte Slot7;

        /// <summary>Выбранная ячейка 0…<see cref="Slots"/>−1 — предмет в руках.</summary>
        public byte Selected;

        /// <summary>Стартовый набор нового персонажа (<c>06</c> 0 + карточка 1-002): кирка, топор, молот, меч, лук, факел;
        /// стрел 50 (вопрос карточки, до ответа), факелов 3.</summary>
        public static Inventory StartingKit()
        {
            var inv = new Inventory
            {
                Slot0 = Items.StonePickaxe,
                Slot1 = Items.StoneAxe,
                Slot2 = Items.Hammer,
                Slot3 = Items.WoodenSword,
                Slot4 = Items.SimpleBow,
                Slot5 = Items.Torch,
                _arrows = 50,
                _torches = 3,
            };
            return inv;
        }

        /// <summary>Предмет в ячейке <paramref name="slot"/>; вне 0…7 — 0.</summary>
        public readonly byte SlotAt(int slot) => slot switch
        {
            0 => Slot0,
            1 => Slot1,
            2 => Slot2,
            3 => Slot3,
            4 => Slot4,
            5 => Slot5,
            6 => Slot6,
            7 => Slot7,
            _ => Items.None,
        };

        /// <summary>Положить предмет в ячейку (вне 0…<see cref="Slots"/>−1 — false).</summary>
        public bool SetSlotAt(int slot, byte itemId)
        {
            switch (slot)
            {
                case 0: Slot0 = itemId; return true;
                case 1: Slot1 = itemId; return true;
                case 2: Slot2 = itemId; return true;
                case 3: Slot3 = itemId; return true;
                case 4: Slot4 = itemId; return true;
                case 5: Slot5 = itemId; return true;
                case 6: Slot6 = itemId; return true;
                case 7: Slot7 = itemId; return true;
                default: return false;
            }
        }

        /// <summary>Выбрать ячейку (вне 0…<see cref="Slots"/>−1 — false, сумка не меняется).</summary>
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

        /// <summary>Счётчик материала; вне таблицы — 0.</summary>
        public readonly int CountAt(Material m) => m switch
        {
            Material.Wood => _wood,
            Material.Stone => _stone,
            Material.Resin => _resin,
            Material.Fiber => _fiber,
            Material.Copper => _copper,
            Material.Arrows => _arrows,
            Material.Torch => _torches,
            _ => 0,
        };

        /// <summary>Положить материал; переполнение — насыщение до <see cref="MaxCount"/>. Возвращает, сколько легло.</summary>
        public int Add(Material m, int n)
        {
            if (n <= 0)
            {
                return 0;
            }

            int have = CountAt(m);
            int put = Math.Min(n, MaxCount - have);
            SetCount(m, have + put);
            return put;
        }

        /// <summary>Списать материал: целиком или никак (операция атомарна, для 1-010 и 1-011).</summary>
        public bool Spend(Material m, int n)
        {
            int have = CountAt(m);
            if (n < 0 || have < n)
            {
                return false;
            }

            SetCount(m, have - n);
            return true;
        }

        /// <summary>
        /// Списать два материала целиком или никак (1-011, <c>05</c> 1.3: стоимость факела — 1 древесина + 1 смола,
        /// списание атомарно по обоим ресурсам): не хватает любого — false, ничего не списано.
        /// </summary>
        public bool Spend(Material m1, int n1, Material m2, int n2)
        {
            if (n2 <= 0)
            {
                return Spend(m1, n1);
            }

            if (n1 <= 0)
            {
                return Spend(m2, n2);
            }

            if (m1 == m2)
            {
                return Spend(m1, n1 + n2);
            }

            if (CountAt(m1) < n1 || CountAt(m2) < n2)
            {
                return false;
            }

            Spend(m1, n1);
            Spend(m2, n2);
            return true;
        }

        private void SetCount(Material m, int v)
        {
            var u = (ushort)v;
            switch (m)
            {
                case Material.Wood: _wood = u; break;
                case Material.Stone: _stone = u; break;
                case Material.Resin: _resin = u; break;
                case Material.Fiber: _fiber = u; break;
                case Material.Copper: _copper = u; break;
                case Material.Arrows: _arrows = u; break;
                case Material.Torch: _torches = u; break;
            }
        }

        public readonly bool Equals(Inventory o) =>
            _wood == o._wood && _stone == o._stone && _resin == o._resin && _fiber == o._fiber &&
            _copper == o._copper && _arrows == o._arrows && _torches == o._torches &&
            Slot0 == o.Slot0 && Slot1 == o.Slot1 && Slot2 == o.Slot2 && Slot3 == o.Slot3 &&
            Slot4 == o.Slot4 && Slot5 == o.Slot5 && Slot6 == o.Slot6 && Slot7 == o.Slot7 &&
            Selected == o.Selected;

        public override readonly bool Equals(object? obj) => obj is Inventory o && Equals(o);

        public override readonly int GetHashCode()
        {
            var h = new Hasher64(0);
            AddTo(ref h);
            return (int)(h.Value >> 32) ^ (int)h.Value;
        }

        /// <summary>Слов в хэш сумки (для <see cref="StateHash"/>: проверка длины набора полей).</summary>
        public const int HashWords = 1 + Slots + (int)Material.Count;

        /// <summary>Слова сумки в хэш: выбранная ячейка, ячейки, счётчики — по порядку (<see cref="HashWords"/> слов).</summary>
        public readonly void AddTo(ref Hasher64 h)
        {
            h.Add(Selected);
            for (int i = 0; i < Slots; i++)
            {
                h.Add(SlotAt(i));
            }

            for (int m = 0; m < (int)Material.Count; m++)
            {
                h.Add(CountAt((Material)m));
            }
        }

        public static bool operator ==(Inventory a, Inventory b) => a.Equals(b);
        public static bool operator !=(Inventory a, Inventory b) => !a.Equals(b);

        /// <summary>Размер payload сумки: выбранная ячейка 1 + ячейки 8 + счётчики 2 × 7 (1 + 8 + 14 = 23).</summary>
        public const int PayloadBytes = 1 + Slots + 2 * (int)Material.Count;

        /// <summary>
        /// Записать payload (порядок слов, что у <see cref="AddTo"/>): выбранная ячейка, ячейки, счётчики u16 LE.
        /// <paramref name="dest"/> меньше <see cref="PayloadBytes"/> — <see cref="ArgumentException"/>.
        /// </summary>
        public readonly void WritePayload(Span<byte> dest)
        {
            if (dest.Length < PayloadBytes)
            {
                throw new ArgumentException($"payload сумки — {PayloadBytes} байт, дали {dest.Length}.", nameof(dest));
            }

            dest[0] = Selected;
            for (int i = 0; i < Slots; i++)
            {
                dest[1 + i] = SlotAt(i);
            }

            for (int m = 0; m < (int)Material.Count; m++)
            {
                int v = CountAt((Material)m);
                dest[1 + Slots + 2 * m] = (byte)v;
                dest[1 + Slots + 2 * m + 1] = (byte)(v >> 8);
            }
        }

        /// <summary>Прочитать payload, записанный <see cref="WritePayload"/>; короткий — <see cref="ArgumentException"/>.</summary>
        public static Inventory ReadPayload(ReadOnlySpan<byte> src)
        {
            if (src.Length < PayloadBytes)
            {
                throw new ArgumentException($"payload сумки — {PayloadBytes} байт, дали {src.Length}.", nameof(src));
            }

            var inv = new Inventory();
            for (int i = 0; i < Slots; i++)
            {
                inv.SetSlotAt(i, src[1 + i]);
            }

            for (int m = 0; m < (int)Material.Count; m++)
            {
                inv.Add((Material)m, src[1 + Slots + 2 * m] | (src[1 + Slots + 2 * m + 1] << 8));
            }

            inv.Select(src[0]);
            return inv;
        }

        public override readonly string ToString()
        {
            byte held = Held;
            string hands = held == Items.None ? "пусто" : $"#{held}";
            return $"inv(hands {hands}, wood {_wood}, stone {_stone}, resin {_resin}, fiber {_fiber}, copper {_copper}, arrows {_arrows}, torch {_torches})";
        }
    }
}
