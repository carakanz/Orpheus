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

        // _costPaid: 1…4 — снято предметов-построек за постановку (E2-12: по предмету на ячейку стены); это — починка (E2-5).
        private const byte CostPaidRepair = 0x80;

        /// <summary>
        /// Сколько предметов стоит правка-постановка (E2-12): стена и забор — по одному на добавленную ячейку
        /// (ячеек в новой маске минус ячеек того же вида до пачки), дверь и прочее — один.
        /// </summary>
        private int PlacementUnits(int i)
        {
            TileEdit edit = _edits[i].Edit;
            if (!BuildCells.IsCellBuilding(edit.Obj) || ObjectId.IdOf(edit.Obj) == ObjectId.Door)
            {
                return 1;
            }

            byte after = BuildCells.MaskOf(edit.Obj, edit.HasRecord, edit.Record);
            byte before = _preObj[i] == edit.Obj ? BuildCells.MaskOf(_preObj[i], _preHasBuild[i], _preBuild[i]) : (byte)0;
            int n = BuildCells.Count((byte)(after & ~before));
            return n < 1 ? 1 : n;
        }

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

            int units = PlacementUnits(i);
            int slot = SlotOf(e.Requester);
            int cell = slot < 0 ? -1 : CellWith(in _entities[slot].Inv, def.Item, e.Cell);
            if (cell < 0 || _entities[slot].Inv.CountIn(cell) < units || _entities[slot].Inv.TakeFrom(cell, units) != units)
            {
                _preResult[i] = EditResult.NoResources;
                return;
            }

            if (cell != e.Cell)
            {
                e = new EditRequest(e.Chunk, e.Arrival, e.Edit, e.Requester, e.Tag, e.CheckCreatures, e.Repair, (byte)cell);
            }

            _costPaid[i] = (byte)units;
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
        /// Отказ после списания (занято существом, враг рядом, занято в пачке): предмет — обратно в ячейку заявки, если она
        /// пуста или с тем же предметом и не полна; иначе в сумку с доукладкой; не влез — кучей у ног (не пропадает).
        /// </summary>
        private void RefundPlacement(int i, int requester)
        {
            int slot = SlotOf(requester);
            if (!CostOf(_edits[i].Edit, out BuildDef def) || def.Item == Items.None)
            {
                return;
            }

            if (slot < 0)
            {
                return; // персонажа нет (ушёл в такте) — сумки нет; в такте правки это невозможно: заявка от сущности
            }

            ref Inventory inv = ref _entities[slot].Inv;
            int cell = _edits[i].Cell;
            int units = _costPaid[i];
            if (cell < Inventory.Cells && (inv.CountIn(cell) == 0 || (inv.ItemAt(cell) == def.Item && inv.CountIn(cell) + units <= ItemTable.MaxStackOf(def.Item))))
            {
                inv.SetCell(cell, def.Item, inv.CountIn(cell) + units);
                return;
            }

            int added = inv.Add(def.Item, units);
            if (added < units)
            {
                SpawnPile(Tick, def.Item, units - added, _entities[slot].Mover.Position, DropPickupDelayTicks);
            }
        }

        /// <summary>
        /// Возврат при сносе (E2-11, как в Terraria): один предмет-постройка кучей на тайл постройки (подбирает любой, кто
        /// подойдёт), а не материалы. Обломки (разрушили враги, снос под ударом) сюда не попадают — ничего (<c>05</c> 1.3).
        /// </summary>
        private void GiveBuildRefund(long tick, TileCoord tile, ushort preObj, byte preFloor, byte preCells)
        {
            // E2-12: стена и забор — по предмету на ячейку (старая стена в 1 тайл — 4 ячейки), дверь — один.
            int units = BuildCells.IsCellBuilding(preObj) && ObjectId.IdOf(preObj) != ObjectId.Door ? BuildCells.Count(preCells) : 1;
            ushort key = preObj != ObjectId.Empty ? preObj : BuildDefs.FloorKey(preFloor); // постройка — id объекта; снос пола — ключ пола (E2-5)
            if ((preObj == ObjectId.Empty && !FloorId.IsBuilt(preFloor)) || !BuildDefs.TryGet(key, out BuildDef def))
            {
                return;
            }

            DropOnTile(tick, tile, def.Item, units);
        }
    }
}
