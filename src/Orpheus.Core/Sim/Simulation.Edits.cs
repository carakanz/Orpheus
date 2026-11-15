using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;
using Orpheus.Core.Trig;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// P-002: partial Simulation - per-file code motion, no behavior change.
    /// </summary>
    public sealed partial class Simulation
    {
        private void AddExpire(long endTick, ChunkCoord chunk, ushort index, bool debris, BuildFlags clear = BuildFlags.DamagedRecent)
        {
            if (_expireCount == _expire.Length)
            {
                // Переполнение (патология): снимаем запись, которой истечь раньше всех, — её эффект слабее всего
                // (флаг урона переживёт 10 с; обломки без записи останутся навсегда — их бережём).
                int oldest = 0;
                for (int i = 1; i < _expire.Length; i++)
                {
                    if (_expire[i].EndTick < _expire[oldest].EndTick && !_expire[i].Debris)
                    {
                        oldest = i;
                    }
                }

                if (_expire[oldest].Debris)
                {
                    return;
                }

                _expire[oldest] = _expire[--_expireCount];
            }

            _expire[_expireCount++] = new ExpireEntry { EndTick = endTick, Chunk = chunk, Index = index, Debris = debris, Clear = debris ? BuildFlags.None : clear };
        }

        /// <summary>Истечения такта (1-011): правки снятия — в очередь этого же такта; очередь полна — проба в следующем.</summary>
        private void StepExpiry(long tick)
        {
            for (int i = 0; i < _expireCount; i++)
            {
                ExpireEntry e = _expire[i];
                if (e.EndTick > tick)
                {
                    continue;
                }

                TileCoord tile = TileOf(e.Chunk, e.Index);
                Chunk? chunkData = _world.FindChunk(e.Chunk);
                bool applied = true;
                if (chunkData is not null && chunkData.TryGetBuild(e.Index, out BuildRecord rec))
                {
                    if (e.Debris && rec.IsDebris)
                    {
                        // Обломки кончились: тайл остаётся как есть (пол природный), запись снимается правкой без записи.
                        applied = RequestEdit(tile, _world.GetFloor(tile), ObjectId.Empty);
                    }
                    else if (!e.Debris)
                    {
                        // C-005: два флага урона (10 с и 3 с) — одна правка на тайл в такт: снимаем всё, что истекло у тайла,
                        // одной записью, а другие истёкшие записи тайла убираем — иначе две правки из одной старой записи
                        // вернули бы друг другу снятый флаг.
                        BuildFlags clear = ExpiredFlags(i, tick) & (BuildFlags)rec.Flags;
                        applied = clear == BuildFlags.None
                            || RequestEdit(tile, _world.GetFloor(tile), _world.GetObject(tile), rec.WithoutFlags(clear));
                        if (applied)
                        {
                            DropExpiredFlagsAfter(i, tick);
                        }
                    }
                }

                if (applied)
                {
                    _expire[i--] = _expire[--_expireCount]; // снято; хвост на место
                }
            }
        }

        /// <summary>
        /// Флаги урона тайла записи истечения № <paramref name="i"/>, которые пора снять: «урон последние 10 с» — если у тайла
        /// есть наступившее истечение этого флага (как до C-005); «чинить нельзя» — если есть наступившее и нет ненаступившего
        /// (C-005: 3 с отсчитываются от последнего урона, а не от первого).
        /// </summary>
        private BuildFlags ExpiredFlags(int i, long tick)
        {
            bool damage = false, repairDue = false, repairLater = false;
            for (int j = 0; j < _expireCount; j++)
            {
                ExpireEntry o = _expire[j];
                if (o.Debris || o.Index != _expire[i].Index || o.Chunk != _expire[i].Chunk)
                {
                    continue;
                }

                bool due = o.EndTick <= tick;
                damage |= due && o.Clear == BuildFlags.DamagedRecent;
                repairDue |= due && o.Clear == BuildFlags.RepairBlocked;
                repairLater |= !due && o.Clear == BuildFlags.RepairBlocked;
            }

            return (damage ? BuildFlags.DamagedRecent : BuildFlags.None) | (repairDue && !repairLater ? BuildFlags.RepairBlocked : BuildFlags.None);
        }

        /// <summary>Убрать наступившие истечения флагов урона того же тайла после № <paramref name="i"/> — их сняла правка № i.</summary>
        private void DropExpiredFlagsAfter(int i, long tick)
        {
            for (int j = _expireCount - 1; j > i; j--)
            {
                ExpireEntry o = _expire[j];
                if (!o.Debris && o.EndTick <= tick && o.Index == _expire[i].Index && o.Chunk == _expire[i].Chunk)
                {
                    _expire[j] = _expire[--_expireCount];
                }
            }
        }

        /// <summary>
        /// Урон постройки (1-011, <c>05</c> 1.3 — осадные враги, взрывы; оружие игроков — нет): прочность −<paramref name="raw"/>;
        /// флаг «урон последние 10 с» на 10 с и «чинить нельзя» на 3 с (C-005); 0 прочности → обломки на 10 с без дропа. Полы «не бьют» (<c>05</c> 1.3).
        /// Правка — через очередь такта (одна пачка на чанк, 3.7). Применится в ближайшем такте — true.
        /// </summary>
        public bool DamageBuilding(TileCoord tile, int raw)
        {
            TileCoord t = Torus.Wrap(_shape, tile);
            ChunkCoord chunk = Torus.ChunkOf(_shape, t);
            Chunk? chunkData = _world.FindChunk(chunk);
            ushort index = (ushort)Chunk.IndexOf(t.X, t.Y);
            if (raw <= 0 || chunkData is null || !chunkData.TryGetBuild(index, out BuildRecord rec) || rec.IsDebris)
            {
                return false;
            }

            ushort obj = _world.GetObject(t);
            if (obj == ObjectId.Empty)
            {
                return false; // построенный пол не бьют (`05` 1.3)
            }

            int left = rec.Integrity - raw;
            if (left <= 0)
            {
                var debris = new BuildRecord(rec.Owner, 0, (byte)BuildFlags.Debris, rec.NaturalFloor, (ushort)(Tick + BuildDefs.DebrisTicks));
                if (!Enqueue(t, rec.NaturalFloor, ObjectId.Empty, true, debris, 0, 0, false))
                {
                    return false;
                }

                AddExpire(Tick + BuildDefs.DebrisTicks, chunk, index, debris: true);
                return true;
            }

            BuildRecord damaged = rec.WithIntegrity((ushort)left).WithFlags(BuildFlags.DamagedRecent | BuildFlags.RepairBlocked);
            if (!Enqueue(t, _world.GetFloor(t), obj, true, damaged, 0, 0, false))
            {
                return false;
            }

            AddExpire(Tick + BuildDefs.DamageFlagTicks, chunk, index, debris: false);
            AddExpire(Tick + BuildDefs.RepairBlockTicks, chunk, index, debris: false, BuildFlags.RepairBlocked); // C-005: чинить — через 3 с
            return true;
        }

        /// <summary>
        /// Существа у тайла правки (`05` 1.3; 0-028): непроходимое на тайл, который задевает сущность, — «занято существом»;
        /// непроходимое или ловушка ближе 2 тайлов к врагу — «враг ближе 2 тайлов». Сущностей ≤ ёмкости — перебор всех.
        /// </summary>
        private EditResult CheckCreatures(ChunkCoord chunk, in TileEdit edit)
        {
            TileCoord tile = TileOf(chunk, edit.Index);
            bool blocks = EditRules.BlocksCreature(edit.Obj);
            byte cells = BuildCells.MaskOf(edit.Obj, edit.HasRecord, edit.Record); // E2-12: стена — только её ячейки
            EditResult result = EditResult.Applied;
            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].Alive)
                {
                    continue; // труп без коллизии (8.3; 1-001): правке не мешает
                }

                WorldPos p = _entities[i].Mover.Position;
                if (blocks && Collision.CircleOverlapsCells(_shape, p, tile, cells))
                {
                    return EditResult.Occupied;
                }

                if (_entities[i].Kind == SimEntity.KindEnemy && EditRules.EnemyTooClose(_shape, p, tile))
                {
                    result = EditResult.EnemyNear;
                }
            }

            return result;
        }

        /// <summary>
        /// Добыча снятого природного объекта (1-010, <c>01</c> 5): число — детерминированный бросок по зерну, тайлу и id
        /// (<see cref="DigLoot.DropFor"/>). С E2-1 добыча падает кучей на тайл объекта (<see cref="DropOnTile"/>), а не в
        /// сумку: подбирает тот, кто подойдёт. Постройка (не природный) — ничего: возврат по доле прочности (1-011).
        /// <paramref name="old"/> — что стояло на тайле ДО пачки (<see cref="_preObj"/>): после <c>ApplyEdits</c> тайл уже пуст.
        /// </summary>
        private void GiveDigLoot(long tick, ChunkCoord chunk, ushort index, ushort old)
        {
            if (!ObjectId.IsNatural(old))
            {
                return;
            }

            TileCoord tile = TileOf(chunk, index);
            int n = DigLoot.DropFor(old, tile.X, tile.Y, _world.Seed, out Material material);
            DropOnTile(tick, tile, ItemTable.OfMaterial(material), n);
        }

        /// <summary>Тайл двери и его соседи (chebyshev ≤ 1) свободны от живых существ — закрытие безопасно.</summary>
        private bool TileAndNeighborsFree(TileCoord tile)
        {
            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].Alive)
                {
                    continue;
                }

                Int2 td = Torus.Delta(_shape, Torus.TileOf(_shape, _entities[i].Mover.Position), tile);
                if (Math.Max(Math.Abs(td.X), Math.Abs(td.Y)) <= 1)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Тайл по чанку и индексу тайла в чанке.</summary>
        private static TileCoord TileOf(ChunkCoord chunk, ushort index) => new TileCoord(
            (chunk.X << Torus.ChunkBits) + (index & (Torus.ChunkSize - 1)),
            (chunk.Y << Torus.ChunkBits) + (index >> Torus.ChunkBits));

        /// <summary>
        /// Правка игрока (docs/07, 3.7; 0-028): как <see cref="RequestEdit(TileCoord, byte, ushort)"/>, плюс метки для ответа
        /// (<paramref name="requester"/>, <paramref name="tag"/> — в событии такта) и проверка существ <b>в фазе 3, после
        /// движения</b> (`05` 1.3): непроходимое — не на тайл, который задевает сущность (в том числе своя), непроходимое и
        /// ловушка — не ближе 2 тайлов к врагу (<see cref="SimEntity.KindEnemy"/>). Иначе персонаж, шагнувший на тайл в том же
        /// такте, оказался бы в стене и вышел бы сквозь стену в 1 тайл (backlog [стройка], П по 0-004).
        /// Запросивший мёртв — false (C-004).
        /// </summary>
        public bool RequestPlayerEdit(TileCoord tile, byte floor, ushort obj, bool hasRecord, BuildRecord record, int requester, int tag, byte cell = AnyCell)
        {
            // Мёртвый не строит и не копает (C-004): запрос от сущности-трупа (5 с до возрождения) — отказ.
            // Раньше убитый строитель достраивал стены и ставил дверь с места гибели (D08_3_1, такты 1292–1336).
            // Процесс мира (requester 0 — автозакрытие двери) сущностью не является и проходит.
            int slot = SlotOf(requester);
            if (slot >= 0 && !_entities[slot].Alive)
            {
                return false;
            }

            // Дверь с записью (1-012) — правка состояния (F/автозакрытие/автооткрытие): существо на тайле или
            // враг рядом не повод отказывать, выталкиванием занимается BeginDoorClose; иначе F у врага ломался бы.
            return Enqueue(tile, floor, obj, hasRecord, record, requester, tag,
                EditRules.NeedsClearance(obj) && !(obj == ObjectId.Door && hasRecord), cell: cell);
        }

        private bool Enqueue(TileCoord tile, byte floor, ushort obj, bool hasRecord, BuildRecord record, int requester, int tag, bool checkCreatures, bool repair = false, byte cell = AnyCell)
        {
            if (_editCount == _edits.Length)
            {
                return false;
            }

            if (!_shape.Wraps && ((uint)tile.X >= (uint)_shape.Width || (uint)tile.Y >= (uint)_shape.Height))
            {
                throw new ArgumentOutOfRangeException(nameof(tile), tile, $"Тайл вне карты {_shape}.");
            }

            TileCoord t = Torus.Wrap(_shape, tile);
            ushort index = (ushort)Chunk.IndexOf(t.X, t.Y);
            _edits[_editCount] = new EditRequest(
                Torus.ChunkOf(_shape, t),
                _editCount,
                hasRecord ? new TileEdit(index, floor, obj, record) : new TileEdit(index, floor, obj),
                requester, tag, checkCreatures, repair, cell);
            _editCount++;
            return true;
        }

        /// <summary>
        /// Запрос правки тайла на ближайший такт: тайл становится ровно таким (пол, объект, без записи постройки).
        /// Применится в фазе 3 пачкой своего чанка. Очередь полна — false.
        /// </summary>
        public bool RequestEdit(TileCoord tile, byte floor, ushort obj) =>
            Enqueue(tile, floor, obj, false, default, 0, 0, false);

        /// <summary>Запрос правки тайла с записью постройки (см. <see cref="RequestEdit(TileCoord, byte, ushort)"/>).</summary>
        public bool RequestEdit(TileCoord tile, byte floor, ushort obj, BuildRecord record) =>
            Enqueue(tile, floor, obj, true, record, 0, 0, false);

        private void ApplyEditBatches(long tick)
        {
            int n = _editCount;
            if (n == 0)
            {
                return;
            }

            // Pre-pass по порядку прихода (до сортировки по чанкам, 1-011): состояние тайла до пачки, списание
            // стоимости постановки (сумка: атомарно на тайл; не хватает — «нет ресурсов», правка в пачку не идёт),
            // снос постройки, получавшей урон последние 10 с, — правка подменяется обломочной (`05` 1.3).
            for (int i = 0; i < n; i++)
            {
                PreEdit(tick, i);
            }

            SortByChunkThenArrival(n);
            int start = 0;
            while (start < n)
            {
                ChunkCoord chunk = _edits[start].Chunk;
                int end = start + 1;
                while (end < n && _edits[end].Chunk == chunk)
                {
                    end++;
                }

                // Проверка существ (0-028) — до пачки, по позициям после движения: отказ в пачку не идёт.
                // Больше правок на чанк, чем тайлов: хвост — «занято» без вызова мира
                // (на тайл в пачке проходит только одна правка, 3.7).
                int take = 0;
                for (int i = start; i < end; i++)
                {
                    EditResult pre = _preResult[i] != EditResult.Applied
                        ? _preResult[i]
                        : _edits[i].CheckCreatures ? CheckCreatures(chunk, _edits[i].Edit) : EditResult.Applied;
                    if (pre == EditResult.Applied && take < _batch.Length)
                    {
                        _batchOf[i] = take;
                        _batch[take++] = _edits[i].Edit;
                    }
                    else
                    {
                        _batchOf[i] = -1;
                        _preResult[i] = pre == EditResult.Applied ? EditResult.Busy : pre;
                    }
                }

                uint before = _world.FindChunk(chunk)?.Revision ?? 0;
                uint revision = take > 0 ? _world.ApplyEdits(chunk, new ReadOnlySpan<TileEdit>(_batch, 0, take), _results) : before;
                for (int i = start; i < end; i++)
                {
                    EditResult result = _batchOf[i] >= 0 ? _results[_batchOf[i]] : _preResult[i];
                    AddEvent(new SimEvent(SimEventKind.TileEdit, tick, chunk, _edits[i].Edit.Index, result, revision,
                        _edits[i].Requester, _edits[i].Tag));

                    // Дверь (1-012): применённая правка с объектом «дверь» — в индекс дверей (dedup); автологика
                    // видит её с этого такта. Снятие — ленивое в StepDoors (объект/запись пропали).
                    if (result == EditResult.Applied && _edits[i].Edit.Obj == ObjectId.Door)
                    {
                        AddDoor(TileOf(chunk, _edits[i].Edit.Index));
                    }

                    // Сундук (E2-5): снесён или разрушен — содержимое кучами на его тайл (и от правок мира, без заказчика).
                    if (result == EditResult.Applied && ObjectId.IdOf(_preObj[i]) == ObjectId.Chest && _edits[i].Edit.Obj != _preObj[i])
                    {
                        SpillChest(tick, TileOf(chunk, _edits[i].Edit.Index));
                    }

                    int req = _edits[i].Requester;
                    if (req <= 0)
                    {
                        continue;
                    }

                    // Возврат оплаченного (1-011): «занято существом»/«враг»/«занято» после пачки — сумка целая
                    // (`05` 1.4: «его ресурсы не списаны»).
                    if (_costPaid[i] == CostPaidRepair && result != EditResult.Applied)
                    {
                        RefundRepair(i, req); // E2-5: починка оплачена, но не применилась
                    }
                    else if (_costPaid[i] != 0 && result != EditResult.Applied)
                    {
                        RefundPlacement(i, req); // E2-11: предмет-постройка — обратно в ячейку (не влез — кучей у ног)
                    }

                    // Добыча (1-010, E2-1): правка «снять природный объект» применена — материал падает кучей на тайл в том же такте.
                    // Двое копающих один тайл: вторая правка — Busy, добыча ровно одному.
                    if (result == EditResult.Applied && _edits[i].Edit.Obj == ObjectId.Empty)
                    {
                        GiveDigLoot(tick, chunk, _edits[i].Edit.Index, _preObj[i]);

                        // Снос постройки (E2-11): один предмет-постройка кучей на тайл (как в Terraria), а не материалы;
                        // обломки ранней расчистки молотом — ничего.
                        if (_preHasBuild[i] && !_preBuild[i].IsDebris)
                        {
                            GiveBuildRefund(tick, TileOf(chunk, _edits[i].Edit.Index), _preObj[i], _preFloor[i], BuildCells.MaskOf(_preObj[i], true, _preBuild[i]));
                        }
                    }
                }

                if (revision != before)
                {
                    _editRev++;
                    AddEvent(new SimEvent(SimEventKind.ChunkRevised, tick, chunk, -1, EditResult.Applied, revision));
                }

                start = end;
            }

            _editCount = 0;
        }

        /// <summary>
        /// Pre-pass одной правки (1-011): снимает состояние тайла до пачки (<see cref="_preObj"/> и компанию),
        /// списывает стоимость постановки (<see cref="EditResult.NoResources"/> — в пачку не идёт) и подменяет
        /// снос «получавшей урон последние 10 с» постройки на обломочную правку (<c>05</c> 1.3).
        /// </summary>
        private void PreEdit(long tick, int i)
        {
            ref EditRequest e = ref _edits[i];
            Chunk? chunkData = _world.FindChunk(e.Chunk);
            ushort index = e.Edit.Index;
            _preObj[i] = chunkData?.GetObject(index) ?? 0;
            _preFloor[i] = chunkData?.GetFloor(index) ?? 0;
            _preHasBuild[i] = chunkData is not null && chunkData.TryGetBuild(index, out _preBuild[i]);
            _preResult[i] = EditResult.Applied;
            _costPaid[i] = 0;

            if (e.Requester <= 0)
            {
                return; // правки сервера (урон, истечения) — без стоимости и возврата
            }

            // Снос постройки, получавшей урон последние 10 с → обломки на 10 с: правка подменяется здесь,
            // второй ApplyEdits в такт невозможен (ревизия +2 сломала бы патч-цепочку клиента).
            if (_preHasBuild[i] && _preBuild[i].IsDamagedRecent && e.Edit.Obj == ObjectId.Empty)
            {
                BuildRecord rec = _preBuild[i];
                var debris = new BuildRecord(rec.Owner, 0, (byte)BuildFlags.Debris, rec.NaturalFloor, (ushort)(tick + BuildDefs.DebrisTicks));
                e = new EditRequest(e.Chunk, e.Arrival, new TileEdit(index, _preFloor[i], ObjectId.Empty, debris),
                    e.Requester, e.Tag, e.CheckCreatures);
                _preHasBuild[i] = false; // снос в обломки — без возврата (`05` 1.3); запись «получав урон» больше не считается сносимой
                AddExpire(tick + BuildDefs.DebrisTicks, e.Chunk, index, debris: true);
                return;
            }

            if (IsRepairEdit(i))
            {
                PreRepair(i); // E2-5: починка — цена по недостающей доле прочности, не цена постановки
                return;
            }

            // Смена состояния двери (1-012): запись на уже существующей двери (F-закрытие) — это не постановка,
            // стоимость не списывается и не возвращается (постановка двери молотом идёт с пустого тайла).
            if (e.Edit.Obj == ObjectId.Door && e.Edit.HasRecord && _preObj[i] == ObjectId.Door)
            {
                return;
            }

            PayPlacement(i); // E2-11: постановка снимает один предмет-постройку из ячейки заявки (Simulation.BuildItems.cs)
        }

    }
}
