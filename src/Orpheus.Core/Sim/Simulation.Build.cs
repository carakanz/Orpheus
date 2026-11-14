using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// E2-5 (шаг 2.5): починка построек, сундуки (содержимое принадлежит миру) и кровать — точка возрождения.
    /// <para>
    /// Порядок детерминирован: сундуки лежат по возрастанию (Y, X) тайла; заявки на сундук идут общей очередью заявок на
    /// сумку (<see cref="RequestBagOp"/>) по приходу. Запись сундука есть, только пока в нём что-то лежит: пустой сундук —
    /// просто объект на тайле (в сохранение и в хэш не попадает).
    /// </para>
    /// </summary>
    public sealed partial class Simulation
    {
        // ---------- Починка (`05` 1.3) ----------

        /// <summary>
        /// Заявка починки постройки на тайле (E2-5): правка «та же постройка с полной прочностью» в очередь такта.
        /// Цена ⌈количество × недостающая доля⌉ списывается в такте (<see cref="PreRepair"/>); не хватило — «нет ресурсов»,
        /// постройку били недавно — «занято». Мёртвый, нет постройки или очередь полна — false.
        /// </summary>
        public bool RequestRepair(TileCoord tile, int requester, int tag)
        {
            int slot = SlotOf(requester);
            TileCoord t = Torus.Wrap(_shape, tile);
            ushort obj = _world.GetObject(t);
            if (slot < 0 || !_entities[slot].Alive || obj == ObjectId.Empty
                || !_world.TryGetBuild(t, out BuildRecord rec) || rec.IsDebris || !BuildDefs.TryGet(obj, out BuildDef def))
            {
                return false;
            }

            return Enqueue(t, _world.GetFloor(t), obj, true, rec.WithIntegrity(def.MaxIntegrity), requester, tag, false);
        }

        /// <summary>Правка № <paramref name="i"/> — починка: тот же объект, запись с прочностью выше прежней.</summary>
        private bool IsRepairEdit(int i)
        {
            ref EditRequest e = ref _edits[i];
            return e.Edit.HasRecord && e.Edit.Obj != ObjectId.Empty && e.Edit.Obj == _preObj[i] && _preHasBuild[i]
                && e.Edit.Record.Integrity > _preBuild[i].Integrity;
        }

        /// <summary>
        /// Pre-pass починки: постройка не в обломках и не под ударом, цена списана из сумки — правка подменяется на
        /// «запись тайла на этот такт с полной прочностью» (флаги двери и прочее — как есть сейчас).
        /// </summary>
        private void PreRepair(int i)
        {
            ref EditRequest e = ref _edits[i];
            BuildRecord rec = _preBuild[i];
            if (rec.IsDebris || rec.IsDamagedRecent || !BuildDefs.TryGet(_preObj[i], out BuildDef def))
            {
                _preResult[i] = EditResult.Busy;
                return;
            }

            int slot = SlotOf(e.Requester);
            int c1 = BuildDefs.RepairCostOf(def.Count1, rec.Integrity, def.MaxIntegrity);
            int c2 = def.Count2 > 0 ? BuildDefs.RepairCostOf(def.Count2, rec.Integrity, def.MaxIntegrity) : 0;
            if (slot < 0 || !_entities[slot].Inv.Spend(def.Material1, c1, def.Material2, c2))
            {
                _preResult[i] = EditResult.NoResources;
                return;
            }

            _costPaid[i] = 2;
            e = new EditRequest(e.Chunk, e.Arrival, new TileEdit(e.Edit.Index, _preFloor[i], _preObj[i], rec.WithIntegrity(def.MaxIntegrity)),
                e.Requester, e.Tag, false);
        }

        /// <summary>Починка оплачена, но правка не применилась — цена возвращается в сумку.</summary>
        private void RefundRepair(int i, int requester)
        {
            int slot = SlotOf(requester);
            BuildRecord rec = _preBuild[i];
            if (slot < 0 || !BuildDefs.TryGet(_preObj[i], out BuildDef def))
            {
                return;
            }

            ref Inventory inv = ref _entities[slot].Inv;
            inv.Add(def.Material1, BuildDefs.RepairCostOf(def.Count1, rec.Integrity, def.MaxIntegrity));
            if (def.Count2 > 0)
            {
                inv.Add(def.Material2, BuildDefs.RepairCostOf(def.Count2, rec.Integrity, def.MaxIntegrity));
            }
        }

        // ---------- Кровать: точка возрождения (`05` 7.3) ----------

        /// <summary>
        /// F на кровати (E2-5): точка возрождения персонажа — этот тайл. Проверки: жив, кровать в радиусе действия
        /// (<see cref="EditRules.InUseRadius"/>). Правило «годная комната» не проверяется — комнат пока нет (допущение E2-5).
        /// </summary>
        public EditReply RequestSetBed(int entityId, TileCoord tile)
        {
            int slot = SlotOf(entityId);
            if (slot < 0 || !_entities[slot].IsCharacter)
            {
                return EditReply.NotAllowed;
            }

            if (!_entities[slot].Alive)
            {
                return EditReply.Dead;
            }

            TileCoord t = Torus.Wrap(_shape, tile);
            if (!EditRules.InUseRadius(_shape, _entities[slot].Mover.Position, t))
            {
                return EditReply.OutOfReach;
            }

            if (ObjectId.IdOf(_world.GetObject(t)) != ObjectId.Bed)
            {
                return EditReply.Empty;
            }

            _entities[slot].HasBed = true;
            _entities[slot].Bed = t;
            return EditReply.Accepted;
        }

        /// <summary>Точка возрождения персонажа из записи (вход вернувшегося игрока); вне такта.</summary>
        public bool SetBed(int entityId, bool hasBed, TileCoord bed)
        {
            int slot = SlotOf(entityId);
            if (slot < 0)
            {
                return false;
            }

            _entities[slot].HasBed = hasBed;
            _entities[slot].Bed = hasBed ? Torus.Wrap(_shape, bed) : default;
            return true;
        }

        /// <summary>
        /// Где возродится персонаж: у своей кровати, если она цела (свободный тайл рядом — сама кровать твёрдая), иначе у
        /// старта; снесённая кровать точкой быть перестаёт. Чанк кровати не загружен — кровать считается целой.
        /// </summary>
        private WorldPos RespawnPointOf(ref SimEntity e)
        {
            if (!e.HasBed)
            {
                return _respawnCenter;
            }

            Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_shape, e.Bed));
            if (chunk != null && ObjectId.IdOf(chunk.GetObject(Chunk.IndexOf(e.Bed.X, e.Bed.Y))) != ObjectId.Bed)
            {
                e.HasBed = false;
                e.Bed = default;
                return _respawnCenter;
            }

            return FreeGroundNear(EditRules.CenterOf(e.Bed));
        }

        // ---------- Сундуки (`05` 1.3, `06` 5.3: 30 ячеек) ----------

        /// <summary>Ячеек в сундуке: 30 (`05` 1.3, `06` 5.3).</summary>
        public const int ChestCells = 30;

        /// <summary>
        /// Номера ячеек сундука в заявках на сумку: <see cref="Inventory.Cells"/> + номер ячейки сундука (0…29). Ячейки
        /// 0…39 — сумка персонажа.
        /// </summary>
        public const int ChestCellBase = Inventory.Cells;

        private TileCoord[] _chestTiles = new TileCoord[8];
        private Inventory[] _chests = new Inventory[8];
        private int _chestCount;

        /// <summary>Сундуков с содержимым (пустые не хранятся).</summary>
        public int ChestCount => _chestCount;

        /// <summary>Тайлы сундуков с содержимым по возрастанию (Y, X).</summary>
        public ReadOnlySpan<TileCoord> ChestTiles => new ReadOnlySpan<TileCoord>(_chestTiles, 0, _chestCount);

        /// <summary>Содержимое сундуков в порядке <see cref="ChestTiles"/> (ячейки 0…<see cref="ChestCells"/>−1).</summary>
        public ReadOnlySpan<Inventory> ChestContents => new ReadOnlySpan<Inventory>(_chests, 0, _chestCount);

        /// <summary>Содержимое сундука на тайле; на тайле не сундук — false. Пустой сундук — true и пустые ячейки.</summary>
        public bool TryGetChest(TileCoord tile, out Inventory content)
        {
            TileCoord t = Torus.Wrap(_shape, tile);
            content = default;
            if (ObjectId.IdOf(_world.GetObject(t)) != ObjectId.Chest)
            {
                return false;
            }

            int i = IndexOfChest(t);
            if (i >= 0)
            {
                content = _chests[i];
            }

            return true;
        }

        /// <summary>Положить содержимое в сундук на тайле целиком (тесты, отладочные сцены); на тайле не сундук — false.</summary>
        public bool SetChest(TileCoord tile, in Inventory content)
        {
            TileCoord t = Torus.Wrap(_shape, tile);
            if (ObjectId.IdOf(_world.GetObject(t)) != ObjectId.Chest)
            {
                return false;
            }

            StoreChest(t, content);
            return true;
        }

        /// <summary>Загрузка сундуков из сохранения: только в симуляцию без сундуков; строго по возрастанию (Y, X).</summary>
        public void RestoreChests(ReadOnlySpan<TileCoord> tiles, ReadOnlySpan<Inventory> contents)
        {
            if (_chestCount != 0 || tiles.Length != contents.Length)
            {
                throw new InvalidOperationException("Загрузка сундуков — только в симуляцию без сундуков, тайлов и содержимого поровну.");
            }

            for (int i = 0; i < tiles.Length; i++)
            {
                TileCoord t = Torus.Wrap(_shape, tiles[i]);
                if (i > 0 && ChestKey(t) <= ChestKey(_chestTiles[i - 1]))
                {
                    throw new ArgumentException($"Сундук № {i} на {t}: порядок (Y, X) не растёт.", nameof(tiles));
                }

                if (_chestCount == _chestTiles.Length)
                {
                    Array.Resize(ref _chestTiles, _chestCount * 2);
                    Array.Resize(ref _chests, _chestCount * 2);
                }

                _chestTiles[_chestCount] = t;
                _chests[_chestCount++] = contents[i];
            }
        }

        /// <summary>
        /// Заявка игрока на операцию с открытым сундуком (E2-5): ячейки 0…39 — сумка, 40…69 — сундук на тайле
        /// <paramref name="chest"/>. Идёт той же очередью, что заявки на сумку; сундук и расстояние до него
        /// (<see cref="EditRules.InUseRadius"/>) проверяются в такте. Отказ — как у <see cref="RequestBagOp"/>.
        /// </summary>
        public bool RequestChestOp(int entityId, TileCoord chest, BagOp op, int from, int to, int count)
        {
            if (op != BagOp.Move && op != BagOp.Split && op != BagOp.Drop)
            {
                return false;
            }

            if (!RequestBagOp(entityId, op, from, to, count))
            {
                return false;
            }

            _bagOps[_bagOpCount - 1].HasChest = true;
            _bagOps[_bagOpCount - 1].Chest = Torus.Wrap(_shape, chest);
            return true;
        }

        private void ApplyChestOp(long tick, int slot, in BagRequest r)
        {
            TileCoord t = r.Chest;
            if (ObjectId.IdOf(_world.GetObject(t)) != ObjectId.Chest || !EditRules.InUseRadius(_shape, _entities[slot].Mover.Position, t))
            {
                return; // сундук снесли или персонаж отошёл — заявка не исполняется
            }

            bool fromChest = r.From >= ChestCellBase, toChest = r.To >= ChestCellBase;
            int from = fromChest ? r.From - ChestCellBase : r.From, to = toChest ? r.To - ChestCellBase : r.To;
            if ((fromChest && from >= ChestCells) || (toChest && to >= ChestCells))
            {
                return;
            }

            int ci = IndexOfChest(t);
            Inventory chest = ci >= 0 ? _chests[ci] : default;
            ref Inventory bag = ref _entities[slot].Inv; // сумка в массиве сущностей, не копия
            switch (r.Op)
            {
                case BagOp.Move:
                    if (fromChest && toChest)
                    {
                        chest.Move(from, to);
                    }
                    else if (fromChest)
                    {
                        Inventory.Move(ref chest, from, ref bag, to);
                    }
                    else if (toChest)
                    {
                        Inventory.Move(ref bag, from, ref chest, to);
                    }
                    else
                    {
                        bag.Move(from, to);
                    }

                    break;
                case BagOp.Split:
                    if (fromChest && toChest)
                    {
                        chest.Split(from, to, r.Count);
                    }
                    else if (fromChest)
                    {
                        Inventory.Split(ref chest, from, ref bag, to, r.Count);
                    }
                    else if (toChest)
                    {
                        Inventory.Split(ref bag, from, ref chest, to, r.Count);
                    }
                    else
                    {
                        bag.Split(from, to, r.Count);
                    }

                    break;
                case BagOp.Drop:
                    {
                        byte item = fromChest ? chest.ItemAt(from) : bag.ItemAt(from);
                        int want = r.Count == 0 ? int.MaxValue : r.Count;
                        int n = fromChest ? chest.TakeFrom(from, want) : bag.TakeFrom(from, want);
                        if (n > 0)
                        {
                            SpawnPile(tick, item, n, _entities[slot].Mover.Position, DropPickupDelayTicks);
                        }

                        break;
                    }
            }

            StoreChest(t, chest);
        }

        /// <summary>
        /// Сундук на тайле снесён или разрушен (`05` 1.3): содержимое — кучами на его тайл, запись сундука снимается.
        /// Кучи разбитого сундука не исчезают (`05` 8: «россыпи разбитых сундуков не исчезают»).
        /// </summary>
        private void SpillChest(long tick, TileCoord tile)
        {
            int ci = IndexOfChest(tile);
            if (ci < 0)
            {
                return;
            }

            Inventory chest = _chests[ci];
            RemoveChestAt(ci);
            WorldPos center = EditRules.CenterOf(tile);
            for (int c = 0; c < ChestCells; c++)
            {
                if (chest.CountIn(c) > 0)
                {
                    SpawnPile(tick, chest.ItemAt(c), chest.CountIn(c), center, LootPickupDelayTicks, true);
                }
            }
        }

        private static long ChestKey(TileCoord t) => ((long)t.Y << 32) | (uint)t.X;

        /// <summary>Место сундука в списке; нет — дополнение места вставки (как у BinarySearch).</summary>
        private int IndexOfChest(TileCoord tile)
        {
            long key = ChestKey(tile);
            int lo = 0, hi = _chestCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                long v = ChestKey(_chestTiles[mid]);
                if (v == key)
                {
                    return mid;
                }

                if (v < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return ~lo;
        }

        private void StoreChest(TileCoord tile, in Inventory content)
        {
            int ci = IndexOfChest(tile);
            bool empty = content.UsedCells == 0;
            if (ci >= 0)
            {
                if (empty)
                {
                    RemoveChestAt(ci);
                }
                else
                {
                    _chests[ci] = content;
                }

                return;
            }

            if (empty)
            {
                return;
            }

            if (_chestCount == _chestTiles.Length)
            {
                Array.Resize(ref _chestTiles, _chestCount * 2);
                Array.Resize(ref _chests, _chestCount * 2);
            }

            int at = ~ci;
            Array.Copy(_chestTiles, at, _chestTiles, at + 1, _chestCount - at);
            Array.Copy(_chests, at, _chests, at + 1, _chestCount - at);
            _chestTiles[at] = tile;
            _chests[at] = content;
            _chests[at].Selected = 0;
            _chestCount++;
        }

        private void RemoveChestAt(int ci)
        {
            _chestCount--;
            Array.Copy(_chestTiles, ci + 1, _chestTiles, ci, _chestCount - ci);
            Array.Copy(_chests, ci + 1, _chests, ci, _chestCount - ci);
        }
    }
}
