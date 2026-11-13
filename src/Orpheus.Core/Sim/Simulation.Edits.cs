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
        private void AddExpire(long endTick, ChunkCoord chunk, ushort index, bool debris)
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

            _expire[_expireCount++] = new ExpireEntry { EndTick = endTick, Chunk = chunk, Index = index, Debris = debris };
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
                    else if (!e.Debris && rec.IsDamagedRecent)
                    {
                        applied = RequestEdit(tile, _world.GetFloor(tile), _world.GetObject(tile), rec.WithoutFlags(BuildFlags.DamagedRecent));
                    }
                }

                if (applied)
                {
                    _expire[i--] = _expire[--_expireCount]; // снято; хвост на место
                }
            }
        }

        /// <summary>
        /// Возврат при сносе (1-011, `05` 1.3): неповреждённой постройки — рецепт целиком, повреждённой — по каждому
        /// материалу ⌊количество × прочность ÷ максимум⌋; сносителю. Не влезло в сумку — пропадает (россыпь — веха 2).
        /// </summary>
        private void GiveBuildRefund(int requester, ushort preObj, byte preFloor, in BuildRecord rec)
        {
            int slot = SlotOf(requester);
            ushort key = preObj != ObjectId.Empty ? preObj : preFloor; // постройка — id объекта; снос пола — id построенного пола
            if (slot < 0 || (preObj == ObjectId.Empty && !FloorId.IsBuilt(preFloor)) || !BuildDefs.TryGet(key, out BuildDef def))
            {
                return;
            }

            ref Inventory inv = ref _entities[slot].Inv; // сумка в массиве сущностей, не копия (SimEntity — структура)
            int k1 = BuildDefs.RefundOf(def.Count1, rec.Integrity, def.MaxIntegrity);
            if (k1 > 0)
            {
                inv.Add(def.Material1, k1);
            }

            if (def.Count2 > 0)
            {
                int k2 = BuildDefs.RefundOf(def.Count2, rec.Integrity, def.MaxIntegrity);
                if (k2 > 0)
                {
                    inv.Add(def.Material2, k2);
                }
            }
        }

        /// <summary>
        /// Урон постройки (1-011, <c>05</c> 1.3 — осадные враги, взрывы; оружие игроков — нет): прочность −<paramref name="raw"/>;
        /// флаг «урон последние 10 с» на 10 с; 0 прочности → обломки на 10 с без дропа. Полы «не бьют» (<c>05</c> 1.3).
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

            BuildRecord damaged = rec.WithIntegrity((ushort)left).WithFlags(BuildFlags.DamagedRecent);
            if (!Enqueue(t, _world.GetFloor(t), obj, true, damaged, 0, 0, false))
            {
                return false;
            }

            AddExpire(Tick + BuildDefs.DamageFlagTicks, chunk, index, debris: false);
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
            EditResult result = EditResult.Applied;
            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].Alive)
                {
                    continue; // труп без коллизии (8.3; 1-001): правке не мешает
                }

                WorldPos p = _entities[i].Mover.Position;
                if (blocks && EditRules.Overlaps(_shape, p, tile))
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
        /// (<see cref="DigLoot.DropFor"/>) — сервер и клиент (предсказание) считают одно и то же; материал — в сумку
        /// <paramref name="requester"/>. Постройка (не природный) — ничего: возврат по доле прочности за строителем (1-011).
        /// <paramref name="old"/> — что стояло на тайле ДО пачки (<see cref="_preObj"/>): после <c>ApplyEdits</c> тайл уже пуст.
        /// </summary>
        private void GiveDigLoot(ChunkCoord chunk, ushort index, ushort old, int requester)
        {
            int slot = SlotOf(requester);
            if (slot < 0 || !ObjectId.IsNatural(old))
            {
                return;
            }

            int x = (chunk.X << Torus.ChunkBits) + (index & (Torus.ChunkSize - 1));
            int y = (chunk.Y << Torus.ChunkBits) + (index >> Torus.ChunkBits);
            int n = DigLoot.DropFor(old, x, y, _world.Seed, out Material material);
            if (n > 0)
            {
                _entities[slot].Inv.Add(material, n);
            }
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
        /// </summary>
        public bool RequestPlayerEdit(TileCoord tile, byte floor, ushort obj, bool hasRecord, BuildRecord record, int requester, int tag) =>
            // Дверь с записью (1-012) — правка состояния (F/автозакрытие/автооткрытие): существо на тайле или
            // враг рядом не повод отказывать, выталкиванием занимается BeginDoorClose; иначе F у врага ломался бы.
            Enqueue(tile, floor, obj, hasRecord, record, requester, tag,
                EditRules.NeedsClearance(obj) && !(obj == ObjectId.Door && hasRecord));

        private bool Enqueue(TileCoord tile, byte floor, ushort obj, bool hasRecord, BuildRecord record, int requester, int tag, bool checkCreatures)
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
                requester, tag, checkCreatures);
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

        /// <summary>
        /// Стоимость правки-постановки (1-011): ключ — id объекта; для пола — id <b>построенного</b> пола в слое пола
        /// (природные полы 1…7 с id построек не путаются: снос и копание стоимость не списывают).
        /// </summary>
        private static bool CostOf(in TileEdit edit, out BuildDef def)
        {
            if (edit.Obj != ObjectId.Empty)
            {
                return BuildDefs.TryGet(edit.Obj, out def);
            }

            def = default;
            return FloorId.IsBuilt(edit.Floor) && BuildDefs.TryGet(edit.Floor, out def);
        }

        /// <summary>Вернуть стоимость постановки при отказе после списания (1-011, `05` 1.4).</summary>
        private void RefundCost(in TileEdit edit, int requester)
        {
            int slot = SlotOf(requester);
            if (slot < 0 || !CostOf(edit, out BuildDef def))
            {
                return;
            }

            ref Inventory inv = ref _entities[slot].Inv; // SimEntity — структура: правим сумку в массиве, не копию
            inv.Add(def.Material1, def.Count1);
            if (def.Count2 > 0)
            {
                inv.Add(def.Material2, def.Count2);
            }
        }

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

                    int req = _edits[i].Requester;
                    if (req <= 0)
                    {
                        continue;
                    }

                    // Возврат оплаченного (1-011): «занято существом»/«враг»/«занято» после пачки — сумка целая
                    // (`05` 1.4: «его ресурсы не списаны»).
                    if (_costPaid[i] != 0 && result != EditResult.Applied)
                    {
                        RefundCost(_edits[i].Edit, req);
                    }

                    // Добыча (1-010): правка «снять природный объект» применена — материал в сумку просящего в том же такте.
                    // Двое копающих один тайл: вторая правка — Busy, добыча ровно одному.
                    if (result == EditResult.Applied && _edits[i].Edit.Obj == ObjectId.Empty)
                    {
                        GiveDigLoot(chunk, _edits[i].Edit.Index, _preObj[i], req);

                        // Снос постройки (1-011, `05` 1.3): 100 % рецепта; повреждённой — ⌊кол × прочность ÷ максимум⌋;
                        // обломки ранней расчистки молотом — ничего.
                        if (_preHasBuild[i] && !_preBuild[i].IsDebris)
                        {
                            GiveBuildRefund(req, _preObj[i], _preFloor[i], _preBuild[i]);
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

            // Смена состояния двери (1-012): запись на уже существующей двери (F-закрытие) — это не постановка,
            // стоимость не списывается и не возвращается (постановка двери молотом идёт с пустого тайла).
            if (e.Edit.Obj == ObjectId.Door && e.Edit.HasRecord && _preObj[i] == ObjectId.Door)
            {
                return;
            }

            if (!CostOf(e.Edit, out BuildDef def))
            {
                return; // снос, копание, природный пол — стоимость не списывается
            }

            int slot = SlotOf(e.Requester);
            if (slot < 0 || !_entities[slot].Inv.Spend(def.Material1, def.Count1, def.Material2, def.Count2))
            {
                _preResult[i] = EditResult.NoResources; // «нет ресурсов» (`05` 1.4): линия продолжается следующими тайлами
                return;
            }

            _costPaid[i] = 1;
        }

    }
}
