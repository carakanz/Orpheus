using System;
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
        /// Ячейка заявки «из меню изготовления» (E2-16, протокол 28): предмет-постройка — из любых ячеек сумки; не хватает —
        /// недостающее изготавливается на месте по рецепту (ингредиенты — сумка, затем сундуки рядом; станок рецепта — рядом).
        /// Проверка до списания: не хватает материалов — «нет ресурсов», нет станка — «нужен станок», ничего не снято.
        /// </summary>
        public const byte CraftCell = 0xFE;

        // _costPaid: 1…4 — снято предметов-построек за постановку (E2-12: по предмету на ячейку стены); это — починка (E2-5).
        private const byte CostPaidRepair = 0x80;

        /// <summary>
        /// Что ставит правка № <paramref name="i"/> и сколько предметов это стоит (по состоянию тайла до пачки, E2-15 — по
        /// слоям): новый верх — его постройка (стена и забор — по предмету на ячейку, прочее — один); та же стена/забор с новыми
        /// ячейками — по предмету на добавленную; построенный пол с новыми ячейками — по предмету на добавленную ячейку пола
        /// (верх тайла при этом не меняется). Не постановка (снос, копание, смена состояния двери, урон) — false.
        /// </summary>
        private bool PlacementOf(int i, out BuildDef def, out int units)
        {
            TileEdit edit = _edits[i].Edit;
            units = 0;
            if (edit.Obj != ObjectId.Empty)
            {
                bool cellWall = BuildCells.IsCellBuilding(edit.Obj) && ObjectId.IdOf(edit.Obj) != ObjectId.Door;
                if (_preObj[i] != edit.Obj)
                {
                    units = cellWall ? Math.Max(1, BuildCells.Count(BuildCells.MaskOf(edit.Obj, edit.HasRecord, edit.Record))) : 1;
                    return BuildDefs.TryGet(edit.Obj, out def);
                }

                if (cellWall)
                {
                    byte after = BuildCells.MaskOf(edit.Obj, edit.HasRecord, edit.Record);
                    byte before = BuildCells.MaskOf(_preObj[i], _preHasBuild[i], _preBuild[i]);
                    int added = BuildCells.Count((byte)(after & ~before));
                    if (added > 0)
                    {
                        units = added;
                        return BuildDefs.TryGet(edit.Obj, out def);
                    }
                }
            }

            // Пол (нижний слой): ячеек построенного пола стало больше.
            byte floorAfter = BuildLayers.FloorCellsOf(edit.Floor, edit.HasRecord, edit.Record);
            byte floorBefore = _preFloor[i] == edit.Floor ? BuildLayers.FloorCellsOf(_preFloor[i], _preHasBuild[i], _preBuild[i]) : (byte)0;
            int floorAdded = BuildCells.Count((byte)(floorAfter & ~floorBefore));
            if (floorAdded > 0 && BuildDefs.TryGet(BuildDefs.FloorKey(edit.Floor), out def))
            {
                units = floorAdded;
                return true;
            }

            def = default;
            return false;
        }

        /// <summary>
        /// Pre-pass постановки № <paramref name="i"/>: снять один предмет-постройку из ячейки заявки (<see cref="AnyCell"/> —
        /// из первой ячейки с этим предметом). Нет предмета — «нет ресурсов» (<c>05</c> 1.4: линия идёт дальше, где
        /// кончились — отказ), правка в пачку не идёт. Не постановка (снос, копание, природный пол) — ничего.
        /// </summary>
        private void PayPlacement(int i)
        {
            ref EditRequest e = ref _edits[i];
            if (!PlacementOf(i, out BuildDef def, out int units))
            {
                return;
            }

            int slot = SlotOf(e.Requester);
            if (e.Cell == CraftCell)
            {
                PayOrCraft(i, slot, def.Item, units);
                return;
            }

            int cell = slot < 0 ? -1 : CellWith(in _entities[slot].Inv, def.Item, e.Cell);
            // E2-15: в руке стопка меньше цены тайла (стена, пол — по предмету на ячейку) — недостающее из сумки.
            if (cell < 0 || _entities[slot].Inv.TakeFromThenAny(cell, def.Item, units) != units)
            {
                _preResult[i] = EditResult.NoResources;
                return;
            }

            if (cell != e.Cell)
            {
                e = e.With(e.Edit, (byte)cell); // C-011: перенос якоря мебели сохраняется
            }

            _costPaid[i] = (byte)units;
        }

        /// <summary>
        /// Постановка из меню (E2-16): <paramref name="units"/> предметов <paramref name="item"/> — сначала готовые из сумки,
        /// недостающее изготавливается здесь же: ⌈недостаёт ÷ выход⌉ изготовлений, ингредиенты — <see cref="TakeIngredients"/>
        /// (сумка → сундуки рядом), лишнее от выхода рецепта (стена — по 2, пол — по 4) — в сумку (не влезло — кучей у ног).
        /// Масса — как у изготовления: ингредиенты превращаются в предметы рецепта, предмет-постройка уходит в постройку.
        /// </summary>
        private void PayOrCraft(int i, int slot, byte item, int units)
        {
            if (slot < 0 || item == Items.None)
            {
                _preResult[i] = EditResult.NoResources;
                return;
            }

            ref Inventory inv = ref _entities[slot].Inv;
            int have = Math.Min(units, inv.Count(item));
            int missing = units - have;
            int runs = 0;
            int recipe = 0;
            if (missing > 0)
            {
                recipe = CraftRules.RecipeFor(item);
                if (recipe == 0)
                {
                    _preResult[i] = EditResult.NoResources;
                    return;
                }

                ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
                if (CraftRules.StationCheck(_world, _entities[slot].Mover.Position, def.Station) != CraftReply.Accepted)
                {
                    _preResult[i] = EditResult.NoStation;
                    return;
                }

                runs = (missing + def.ResultCount - 1) / def.ResultCount;
                if (AffordableNear(slot, def) < runs)
                {
                    _preResult[i] = EditResult.NoResources;
                    return;
                }

                TakeIngredients(slot, def, runs);
            }

            inv.Remove(item, have);
            int extra = recipe == 0 ? 0 : runs * RecipeTable.Get(recipe).ResultCount - missing;
            if (extra > 0)
            {
                ReturnBuildItem(_edits[i].Requester, AnyCell, item, extra);
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
        /// Отказ после списания (занято существом, враг рядом, занято в пачке): предмет — обратно персонажу
        /// (<see cref="ReturnBuildItem"/>).
        /// </summary>
        private void RefundPlacement(int i, int requester)
        {
            if (PlacementOf(i, out BuildDef def, out _) && def.Item != Items.None)
            {
                ReturnBuildItem(requester, _edits[i].Cell, def.Item, _costPaid[i]); // E2-12: столько, сколько снято (по ячейке)
            }
        }

        /// <summary>
        /// Вернуть персонажу <paramref name="count"/> предметов-построек (отказ после списания, E2-11; E2-12 — по ячейке стены): в ячейку <paramref name="cell"/>, если она
        /// пуста или с тем же предметом и не полна; иначе в сумку с доукладкой; не влез — кучей у ног (не пропадает).
        /// Персонажа нет — false (в такте правки невозможно: заявка идёт от живой сущности).
        /// </summary>
        public bool ReturnBuildItem(int entityId, int cell, byte item, int count = 1)
        {
            int slot = SlotOf(entityId);
            if (slot < 0 || !ItemTable.Exists(item))
            {
                return false;
            }

            ref Inventory inv = ref _entities[slot].Inv;
            if ((uint)cell < Inventory.Cells && (inv.CountIn(cell) == 0 || (inv.ItemAt(cell) == item && inv.CountIn(cell) + count <= ItemTable.MaxStackOf(item))))
            {
                inv.SetCell(cell, item, inv.CountIn(cell) + count);
                return true;
            }

            int added = inv.Add(item, count);
            if (added < count)
            {
                SpawnPile(Tick, item, count - added, _entities[slot].Mover.Position, DropPickupDelayTicks);
            }

            return true;
        }

        /// <summary>
        /// Возврат при сносе (E2-11, как в Terraria): предмет-постройка кучей на тайл постройки (подбирает любой, кто подойдёт),
        /// а не материалы. Обломки (разрушили враги, снос под ударом) сюда не попадают — ничего (<c>05</c> 1.3). E2-15 — по
        /// слоям: снят верх — его предмет (стена и забор — по предмету на ячейку, дверь и мебель — один), пол остаётся; верха
        /// не было, снят построенный пол — по предмету на ячейку пола.
        /// </summary>
        private void GiveBuildRefund(long tick, TileCoord tile, int i)
        {
            ushort preObj = _preObj[i];
            BuildRecord pre = _preBuild[i];
            int units;
            ushort key;
            if (preObj != ObjectId.Empty)
            {
                units = BuildCells.IsCellBuilding(preObj) && ObjectId.IdOf(preObj) != ObjectId.Door ? BuildCells.Count(BuildCells.MaskOf(preObj, true, pre)) : 1;
                key = preObj;
            }
            else if (FloorId.IsBuilt(_preFloor[i]) && !FloorId.IsBuilt(_edits[i].Edit.Floor))
            {
                units = BuildCells.Count(BuildLayers.FloorCellsOf(_preFloor[i], true, pre));
                key = BuildDefs.FloorKey(_preFloor[i]);
            }
            else
            {
                return; // не снос (постановка пола в тайл с записью)
            }

            if (BuildDefs.TryGet(key, out BuildDef def))
            {
                DropOnTile(tick, tile, def.Item, units);
            }
        }
    }
}
