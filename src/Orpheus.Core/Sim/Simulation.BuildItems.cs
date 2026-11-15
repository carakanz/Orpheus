using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Постройки — предметы (E2-11, <c>BRIEF</c> 11e (3): «взять стену из инвентаря и нажать, куда»): постановка игрока снимает
    /// один предмет-постройку (<see cref="BuildDef.Item"/>) из ячейки заявки, отказ после списания возвращает его в ту же
    /// ячейку, снос возвращает один предмет кучей на тайл. Материалы <see cref="BuildDefs"/> — цена рецепта и починки.
    /// Масса сохраняется: предмет не появляется и не пропадает ни при каком исходе правки.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>Ячейка заявки постановки «любая с этим предметом» (правки без ячейки: тесты, внутренние вызовы).</summary>
        public const byte AnyCell = 0xFF;

        /// <summary>
        /// Постройка правки-постановки: ключ — id объекта; для пола — ключ <b>построенного</b> пола в слое пола
        /// (природные полы 1…7 с постройками не путаются: снос и копание ничего не стоят).
        /// </summary>
        private static bool CostOf(in TileEdit edit, out BuildDef def)
        {
            if (edit.Obj != ObjectId.Empty)
            {
                return BuildDefs.TryGet(edit.Obj, out def);
            }

            def = default;
            return FloorId.IsBuilt(edit.Floor) && BuildDefs.TryGet(BuildDefs.FloorKey(edit.Floor), out def);
        }

        /// <summary>
        /// Pre-pass постановки № <paramref name="i"/>: снять один предмет-постройку из ячейки заявки (<see cref="AnyCell"/> —
        /// из первой ячейки с этим предметом). Нет предмета — «нет ресурсов» (<c>05</c> 1.4: линия идёт дальше, где
        /// кончились — отказ), правка в пачку не идёт. Не постановка (снос, копание, природный пол) — ничего.
        /// </summary>
        private void PayPlacement(int i)
        {
            ref EditRequest e = ref _edits[i];
            if (!CostOf(e.Edit, out BuildDef def))
            {
                return;
            }

            int slot = SlotOf(e.Requester);
            int cell = slot < 0 ? -1 : CellWith(in _entities[slot].Inv, def.Item, e.Cell);
            if (cell < 0 || _entities[slot].Inv.TakeFrom(cell, 1) != 1)
            {
                _preResult[i] = EditResult.NoResources;
                return;
            }

            if (cell != e.Cell)
            {
                e = new EditRequest(e.Chunk, e.Arrival, e.Edit, e.Requester, e.Tag, e.CheckCreatures, e.Repair, (byte)cell);
            }

            _costPaid[i] = 1;
        }

        // Ячейка, из которой брать: названная — если в ней этот предмет; «любая» — первая по номеру с ним; иначе −1.
        private static int CellWith(in Inventory inv, byte item, byte cell)
        {
            if (item == Items.None)
            {
                return -1;
            }

            if (cell != AnyCell)
            {
                return inv.ItemAt(cell) == item && inv.CountIn(cell) > 0 ? cell : -1;
            }

            for (int c = 0; c < Inventory.Cells; c++)
            {
                if (inv.ItemAt(c) == item)
                {
                    return c;
                }
            }

            return -1;
        }

        /// <summary>
        /// Отказ после списания (занято существом, враг рядом, занято в пачке): предмет — обратно персонажу
        /// (<see cref="ReturnBuildItem"/>).
        /// </summary>
        private void RefundPlacement(int i, int requester)
        {
            if (CostOf(_edits[i].Edit, out BuildDef def) && def.Item != Items.None)
            {
                ReturnBuildItem(requester, _edits[i].Cell, def.Item);
            }
        }

        /// <summary>
        /// Вернуть персонажу один предмет-постройку (отказ после списания, E2-11): в ячейку <paramref name="cell"/>, если она
        /// пуста или с тем же предметом и не полна; иначе в сумку с доукладкой; не влез — кучей у ног (не пропадает).
        /// Персонажа нет — false (в такте правки невозможно: заявка идёт от живой сущности).
        /// </summary>
        public bool ReturnBuildItem(int entityId, int cell, byte item)
        {
            int slot = SlotOf(entityId);
            if (slot < 0 || !ItemTable.Exists(item))
            {
                return false;
            }

            ref Inventory inv = ref _entities[slot].Inv;
            if ((uint)cell < Inventory.Cells && (inv.CountIn(cell) == 0 || (inv.ItemAt(cell) == item && inv.CountIn(cell) < ItemTable.MaxStackOf(item))))
            {
                inv.SetCell(cell, item, inv.CountIn(cell) + 1);
                return true;
            }

            if (inv.Add(item, 1) == 0)
            {
                SpawnPile(Tick, item, 1, _entities[slot].Mover.Position, DropPickupDelayTicks);
            }

            return true;
        }

        /// <summary>
        /// Возврат при сносе (E2-11, как в Terraria): один предмет-постройка кучей на тайл постройки (подбирает любой, кто
        /// подойдёт), а не материалы. Обломки (разрушили враги, снос под ударом) сюда не попадают — ничего (<c>05</c> 1.3).
        /// </summary>
        private void GiveBuildRefund(long tick, TileCoord tile, ushort preObj, byte preFloor)
        {
            ushort key = preObj != ObjectId.Empty ? preObj : BuildDefs.FloorKey(preFloor); // постройка — id объекта; снос пола — ключ пола (E2-5)
            if ((preObj == ObjectId.Empty && !FloorId.IsBuilt(preFloor)) || !BuildDefs.TryGet(key, out BuildDef def))
            {
                return;
            }

            DropOnTile(tick, tile, def.Item, 1);
        }
    }
}
