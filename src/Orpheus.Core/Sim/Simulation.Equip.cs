using System;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// E3-6 (шаг 3.6): ячейки снаряжения (`06` 4.7) — голова / тело / ноги. Экипировка — те же заявки сумки
    /// (<see cref="BagOp.Move"/>, <see cref="BagOp.Drop"/>), ячейки <see cref="EquipBase"/>…+2 — продолжение
    /// номеров (сумка 0…39, сундук 40…69), как сундук в E2-5: правила решает только сервер.
    /// <para>
    /// Armor и Mass персонажа — производные от надетого: <see cref="RecalcGear"/> (сумма брони `02` 6.1;
    /// полный комплект одного SetId — масса 2, «Устойчивость» `06` 4.7: отброс вдвое короче, `02` 6.5).
    /// Снятие в полную сумку — кучей у ног (потери нет, как у <see cref="BagOp.Drop"/>).
    /// </para>
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>Номер первой ячейки снаряжения в заявках сумки: после сумки (0…39) и сундука (40…69).</summary>
        public const int EquipBase = Inventory.Cells + ChestCells; // 70

        /// <summary>Ячеек снаряжения: голова, тело, ноги.</summary>
        public const int EquipCells = 3;

        /// <summary>Ячейка заявки — снаряжение?</summary>
        public static bool IsEquipCell(int cell) => cell >= EquipBase && cell < EquipBase + EquipCells;

        /// <summary>Ячейка снаряжения по номеру заявки (<see cref="Items.SlotHead"/>…); не снаряжение — <see cref="Items.SlotNone"/>.</summary>
        public static byte EquipSlotOfCell(int cell) => IsEquipCell(cell) ? (byte)(cell - EquipBase + 1) : Items.SlotNone;

        /// <summary>Надетый предмет в ячейке снаряжения персонажа; у врага и вне ячеек — <see cref="Items.None"/>.</summary>
        public byte EquipItemAt(int entityId, int cell)
        {
            int slot = SlotOf(entityId);
            if (slot < 0 || !_entities[slot].IsCharacter || !IsEquipCell(cell))
            {
                return Items.None;
            }

            ref readonly SimEntity e = ref _entities[slot];
            return EquipSlotOfCell(cell) switch
            {
                Items.SlotHead => e.EquipHead,
                Items.SlotBody => e.EquipBody,
                _ => e.EquipLegs,
            };
        }

        /// <summary>
        /// Пересчёт производных от надетого (E3-6): Armor — сумма брони трёх предметов (`02` 6.1);
        /// Mass — 2, когда надеты все три части одного комплекта (SetId ≠ 0, «Устойчивость» `06` 4.7), иначе 1.
        /// Только для персонажей: масса врагов задаётся архетипом и от снаряжения не зависит.
        /// </summary>
        private static void RecalcGear(ref SimEntity e)
        {
            int armor = ItemTable.Get(e.EquipHead).Armor + ItemTable.Get(e.EquipBody).Armor + ItemTable.Get(e.EquipLegs).Armor;
            byte set = e.EquipHead != Items.None && e.EquipBody != Items.None && e.EquipLegs != Items.None
                ? ItemTable.Get(e.EquipHead).SetId : Items.SetNone;
            if (set != Items.SetNone && (ItemTable.Get(e.EquipBody).SetId != set || ItemTable.Get(e.EquipLegs).SetId != set))
            {
                set = Items.SetNone; // части разных комплектов — бонуса нет (`06` 4.7: комплект собирается целиком)
            }

            e.Armor = (byte)Math.Min(byte.MaxValue, armor);
            e.Mass = set != Items.SetNone ? (byte)2 : (byte)1;
        }

        /// <summary>Надетый предмет ячейки; вне ячеек снаряжения и у не-персонажа — <see cref="Items.None"/>.</summary>
        private byte GetEquip(in SimEntity e, byte slot) => slot switch
        {
            Items.SlotHead => e.EquipHead,
            Items.SlotBody => e.EquipBody,
            Items.SlotLegs => e.EquipLegs,
            _ => Items.None,
        };

        private static void SetEquip(ref SimEntity e, byte slot, byte item)
        {
            switch (slot)
            {
                case Items.SlotHead: e.EquipHead = item; break;
                case Items.SlotBody: e.EquipBody = item; break;
                case Items.SlotLegs: e.EquipLegs = item; break;
            }
        }

        /// <summary>
        /// Перенос с ячейкой снаряжения (E3-6): в ячейку — надеть (предмет с этой ячейкой связан, обмен с надетым),
        /// из ячейки — снять в сумку (обмен; ячейка сумки занята чужим предметом — обмен, как <see cref="Inventory.Move"/>).
        /// Снаряжение — стопка 1, частичных переносов нет. Возвращает true, если сумка/снаряжение изменились.
        /// </summary>
        private bool ApplyEquipMove(long tick, int slot, in BagRequest r)
        {
            ref SimEntity e = ref _entities[slot];
            ref Inventory inv = ref e.Inv;
            bool fromEquip = IsEquipCell(r.From), toEquip = IsEquipCell(r.To);
            if (fromEquip == toEquip)
            {
                return false; // снаряжение ↔ снаряжение или сумка ↔ сумка — сюда не приходят
            }

            if (toEquip)
            {
                // Надеть: одну штуку из стопки в ячейке сумки; надетый — обратно в сумку (в эту же ячейку или свободную).
                byte toSlot = EquipSlotOfCell(r.To);
                byte item = inv.ItemAt(r.From);
                if (item == Items.None || ItemTable.Get(item).Slot != toSlot)
                {
                    return false; // в ячейку «голова» — только шлем и т. п.
                }

                byte old = GetEquip(e, toSlot);
                if (inv.TakeFrom(r.From, 1) == 0)
                {
                    return false;
                }

                if (old != Items.None && inv.Add(old, 1) == 0)
                {
                    inv.Add(item, 1); // сумка полна для надетого — надевать нельзя, стопка на месте
                    return false;
                }

                SetEquip(ref e, toSlot, item);
                RecalcGear(ref e);
                return true;
            }

            // Снять: из ячейки снаряжения в ячейку сумки; надетый из сумки (та же ячейка предмета) — обменом.
            byte fromSlot = EquipSlotOfCell(r.From);
            byte worn = GetEquip(e, fromSlot);
            if (worn == Items.None)
            {
                return false;
            }

            byte inCell = inv.ItemAt(r.To);
            if (inCell != Items.None)
            {
                if (inv.CountIn(r.To) != 1 || ItemTable.Get(inCell).Slot != fromSlot)
                {
                    return false; // обменивать можно только предметом своей ячейки
                }

                SetEquip(ref e, fromSlot, inCell);
                inv.SetCell(r.To, worn, 1);
                RecalcGear(ref e);
                return true;
            }

            if (!inv.SetCell(r.To, worn, 1))
            {
                return false; // ячейки сумки 0…39; вне — отказ (перенос в сундук идёт через его путь)
            }

            SetEquip(ref e, fromSlot, Items.None);
            RecalcGear(ref e);
            return true;
        }
    }
}
