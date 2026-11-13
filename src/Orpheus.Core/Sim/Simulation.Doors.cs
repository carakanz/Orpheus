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
        /// <summary>Открыть дверь (1-012): правка записи с битом DoorOpen в очередь такта (автооткрытие).</summary>
        private void BeginDoorOpen(int di, in BuildRecord rec)
        {
            TileCoord tile = new TileCoord(_doorX[di], _doorY[di]);
            Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_shape, tile));
            int index = Chunk.IndexOf(tile.X, tile.Y);
            if (chunk is null || chunk.GetObject(index) != ObjectId.Door)
            {
                RemoveDoor(di);
                return;
            }

            RequestEdit(tile, chunk.GetFloor(index), ObjectId.Door, rec.WithFlags(BuildFlags.DoorOpen).WithDebrisEndTick(0));
        }

        /// <summary>
        /// Закрытие двери (1-012): правка записи без битов открытости и без таймера в очередь такта.
        /// Персонаж на тайле — отказ; враг — выталкивание (E,S,W,N), цели нет — отказ (закрытие отложено,
        /// автозакрытия его догонят). <paramref name="fromF"/> — закрытие кнопкой F: замок
        /// <param name="fromF">закрытие кнопкой F: замок <see cref="DoorLockTicks"/> тактов (автооткрытие молчит);
        /// F по закрытой двери — только продление замка, без правки (1-028), принимается и при активном замке;
        /// автозакрытие замок не ставит — замок только рантайм-состояние, в сейве его нет,
        /// и на детерминизм загрузки он не влияет.</param>
        /// <param name="requester">запросивший игрок (F по сети, 0-028) — для ответа событием такта; 0 — процесс мира.</param>
        /// <param name="tag">метка запроса (номер запроса клиента) — в событие такта.</param>
        /// </summary>
        private bool BeginDoorClose(int di, long tick, bool fromF, int requester = 0, int tag = 0)
        {
            TileCoord tile = new TileCoord(_doorX[di], _doorY[di]);
            Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_shape, tile));
            int index = Chunk.IndexOf(tile.X, tile.Y);
            if (chunk is null || chunk.GetObject(index) != ObjectId.Door)
            {
                RemoveDoor(di);
                return false;
            }

            if (!chunk.TryGetBuild(index, out BuildRecord rec))
            {
                rec = default;
            }

            if (fromF && !rec.IsDoorOpen)
            {
                // F по закрытой двери (1-028): только продление замка, без правки — замок держится
                // всю ночь (клиент жмёт F каждый кадр), автооткрытие молчит, закрывать нечего.
                // Ветка ДО проверки замка: F-спам при активном замке тоже принимается и продлевает.
                _doorLockUntil[di] = tick + 1 + DoorLockTicks;
                return true;
            }

            if (fromF && tick < _doorLockUntil[di])
            {
                return false;
            }

            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].Alive || !EditRules.Overlaps(_shape, _entities[i].Mover.Position, tile))
                {
                    continue;
                }

                if (_entities[i].Kind != SimEntity.KindEnemy)
                {
                    return false; // персонаж в проёме — не выталкиваем, закрытие откладывается
                }

                if (!PushEnemyOffTile(i, tile))
                {
                    return false; // выталкивать некуда — закрытие откладывается
                }

                // Телепорт после фазы сетки: сетку обновляем за тем же существом сразу.
                _grid.Update(i, Torus.ChunkOf(_shape, _entities[i].Mover.Position));
            }

            if (!RequestPlayerEdit(tile, chunk.GetFloor(index), ObjectId.Door, true,
                    rec.WithoutFlags(BuildFlags.DoorOpen | BuildFlags.DoorKeepOpen).WithDebrisEndTick(0), requester, tag))
            {
                return false; // очередь правок полна — проба в следующем такте
            }

            if (fromF)
            {
                _doorLockUntil[di] = tick + 1 + DoorLockTicks;
            }

            return true;
        }

        /// <summary>Индекс двери в списке (канонический порядок (Y, X)); нет — −1.</summary>
        private int IndexOfDoor(TileCoord tile)
        {
            for (int i = 0; i < _doorCount; i++)
            {
                if (_doorX[i] == tile.X && _doorY[i] == tile.Y)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Двери такта (фаза 4.5, после движения и сетки): открытая без «держать» закрывается через
        /// <see cref="DoorAutoCloseTicks"/> тактов, когда тайл и соседи свободны; закрытая открывается,
        /// когда персонаж на тайле (страховка) или шагнул к ней (скаляр смещения такта к центру двери &gt; 0),
        /// в пределах <see cref="DoorLockTicks"/> после F — молчит. Враг дверь не открывает (`05` 1.3).
        /// </summary>
        private void StepDoors(long tick)
        {
            for (int di = 0; di < _doorCount; di++)
            {
                TileCoord tile = new TileCoord(_doorX[di], _doorY[di]);
                Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_shape, tile));
                int index = Chunk.IndexOf(tile.X, tile.Y);
                if (chunk is null || chunk.GetObject(index) != ObjectId.Door)
                {
                    RemoveDoor(di--); // снесена/разрушена — ленивое снятие с индекса
                    continue;
                }

                if (!chunk.TryGetBuild(index, out BuildRecord rec))
                {
                    rec = default;
                }

                if (rec.IsDoorOpen)
                {
                    if ((rec.Flags & (byte)BuildFlags.DoorKeepOpen) != 0)
                    {
                        continue; // «держать открытой» — автозакрытие не трогает
                    }

                    // Автозакрытие (1-012): дедлайн в байте записи (DebrisEndTick, такт mod 65 536, 0 — не заведён):
                    // таймер живёт в мире, сейв v6 не растёт, после загрузки будущее не расходится.
                    ushort tickMod = unchecked((ushort)tick);
                    if (TileAndNeighborsFree(tile))
                    {
                        if (rec.DebrisEndTick == 0)
                        {
                            TouchDoorDeadline(di, unchecked((ushort)(tickMod + DoorAutoCloseTicks))); // завести таймер
                        }
                        else if (unchecked((short)(tickMod - rec.DebrisEndTick)) >= 0)
                        {
                            BeginDoorClose(di, tick, fromF: false); // дедлайн наступил — закрываем
                        }
                    }
                    else if (rec.DebrisEndTick != 0)
                    {
                        TouchDoorDeadline(di, 0); // тайл занят — таймер снимается
                    }

                    continue;
                }

                if (tick < _doorLockUntil[di])
                {
                    continue; // замок F
                }

                for (int i = 0; i < _entityCount; i++)
                {
                    if (!_entities[i].Alive || _entities[i].Kind == SimEntity.KindEnemy)
                    {
                        continue; // враг дверь не открывает
                    }

                    WorldPos p = _entities[i].Mover.Position;
                    if (EditRules.Overlaps(_shape, p, tile))
                    {
                        BeginDoorOpen(di, rec); // страховка: персонаж на тайле двери
                        break;
                    }

                    Int2 td = Torus.Delta(_shape, Torus.TileOf(_shape, p), tile);
                    if (Math.Max(Math.Abs(td.X), Math.Abs(td.Y)) != 1)
                    {
                        continue; // не соседний тайл
                    }

                    Int2 toDoor = Torus.Delta(_shape, p, new WorldPos(
                        (tile.X << Torus.SubTileBits) + Torus.SubTilesPerTile / 2,
                        (tile.Y << Torus.SubTileBits) + Torus.SubTilesPerTile / 2));

                    // «Шагнул к двери» — скаляр смещения такта; «упёрся и жмёт к двери» — скаляр направления
                    // ввода (у стены закрытой двери смещение нуля, без этого стоящий у двери не открывал бы).
                    Int2 moved = Torus.Delta(_shape, _posBefore[i], p);
                    Int2 want = DesiredDirection(i);
                    if (moved.X * toDoor.X + moved.Y * toDoor.Y > 0
                        || want.X * toDoor.X + want.Y * toDoor.Y > 0)
                    {
                        BeginDoorOpen(di, rec);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Вытолкнуть существо с тайла двери (1-012): соседние тайлы в порядке E,S,W,N; цель — непроходимый
        /// и без других существ. Телепорт в центр тайла, остаток скорости и уворот гасятся (шаг через дверь
        /// не «дотягивается» до закрытого проёма).
        /// </summary>
        private bool PushEnemyOffTile(int i, TileCoord door)
        {
            for (int k = 0; k < PushDx.Length; k++)
            {
                TileCoord t = Torus.Wrap(_shape, new TileCoord(door.X + PushDx[k], door.Y + PushDy[k]));
                if (IsBlocked(_shape, t))
                {
                    continue;
                }

                bool occupied = false;
                for (int j = 0; j < _entityCount; j++)
                {
                    if (j != i && _entities[j].Alive && EditRules.Overlaps(_shape, _entities[j].Mover.Position, t))
                    {
                        occupied = true;
                        break;
                    }
                }

                if (occupied)
                {
                    continue;
                }

                ref Mover m = ref _entities[i].Mover;
                m.Position = Torus.Wrap(_shape, new WorldPos(
                    (t.X << Torus.SubTileBits) + Torus.SubTilesPerTile / 2,
                    (t.Y << Torus.SubTileBits) + Torus.SubTilesPerTile / 2));
                m.Carry = default;
                m.Dodge = default;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Перезавести дедлайн автозакрытия двери (1-012) в байте записи: 0 — таймер снят, иначе такт
        /// (mod 65 536) закрытия. Правка состояния без смены проходимости.
        /// </summary>
        private void TouchDoorDeadline(int di, ushort endTick)
        {
            TileCoord tile = new TileCoord(_doorX[di], _doorY[di]);
            Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_shape, tile));
            int index = Chunk.IndexOf(tile.X, tile.Y);
            if (chunk is null || chunk.GetObject(index) != ObjectId.Door)
            {
                RemoveDoor(di);
                return;
            }

            if (!chunk.TryGetBuild(index, out BuildRecord rec))
            {
                rec = default;
            }

            RequestEdit(tile, chunk.GetFloor(index), ObjectId.Door, rec.WithDebrisEndTick(endTick));
        }

        private void AddDoor(TileCoord tile)
        {
            for (int i = 0; i < _doorCount; i++)
            {
                if (_doorX[i] == tile.X && _doorY[i] == tile.Y)
                {
                    return; // дедуп: правка двери в каждом такте не размножает список
                }
            }

            if (_doorCount == _doorX.Length)
            {
                Array.Resize(ref _doorX, _doorX.Length + DoorCapacityStep);
                Array.Resize(ref _doorY, _doorY.Length + DoorCapacityStep);
                Array.Resize(ref _doorLockUntil, _doorLockUntil.Length + DoorCapacityStep);
            }

            // Порядок индекса канонический — по (Y, X): регистрация при применении правок и сканирование
            // мира при загрузке дают один и тот же индекс, детерминизм будущим состояний не расходится.
            int at = _doorCount;
            while (at > 0 && (_doorY[at - 1] > tile.Y || (_doorY[at - 1] == tile.Y && _doorX[at - 1] > tile.X)))
            {
                at--;
            }

            for (int i = _doorCount; i > at; i--)
            {
                _doorX[i] = _doorX[i - 1];
                _doorY[i] = _doorY[i - 1];
                _doorLockUntil[i] = _doorLockUntil[i - 1];
            }

            _doorX[at] = tile.X;
            _doorY[at] = tile.Y;
            _doorLockUntil[at] = 0;
            _doorCount++;
        }

        /// <summary>Индекс дверей по миру: каждый чанк, его записи построек, объект «дверь» (конструктор).</summary>
        private void IndexDoors()
        {
            ushort[] idx = new ushort[Torus.ChunkSize * Torus.ChunkSize];
            foreach (ChunkCoord c in _world.GetChunkCoords())
            {
                Chunk? chunk = _world.FindChunk(c);
                if (chunk is null)
                {
                    continue;
                }

                int n = chunk.GetBuildIndices(idx);
                for (int k = 0; k < n; k++)
                {
                    if (chunk.GetObject(idx[k]) == ObjectId.Door)
                    {
                        AddDoor(TileOf(c, idx[k]));
                    }
                }
            }
        }

        /// <summary>Замок двери (рантайм-состояние, 1-028): такт, до которого автооткрытие молчит (0 — нет замка).</summary>
        public long DoorLockUntil(int index) => index >= 0 && index < _doorCount ? _doorLockUntil[index] : 0;

        /// <summary>
        /// F по запросу сервера (1-012, протокол 19): закрыть дверь на <paramref name="tile"/>. Радиус —
        /// chebyshev ≤ 1 от тайла нажавшего, тайл под нажавшим — отказ; замок и выталкивание врага — внутри
        /// <see cref="BeginDoorClose"/>. Принято — true; исход доходит до запросчика событием такта
        /// (<paramref name="requester"/> = <paramref name="entityId"/>, <paramref name="tag"/>).
        /// </summary>
        public bool RequestCloseDoor(int entityId, TileCoord tile, int tag)
        {
            int slot = SlotOf(entityId);
            if (slot < 0 || !_entities[slot].Alive)
            {
                return false;
            }

            Int2 d = Torus.Delta(_shape, Torus.TileOf(_shape, _entities[slot].Mover.Position), tile);
            int cheb = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
            if (cheb == 0 || cheb > 1)
            {
                return false; // не в радиусе или тайл под нажавшим («в проёме F — отказ»)
            }

            int di = IndexOfDoor(tile);
            return di >= 0 && BeginDoorClose(di, Tick, fromF: true, requester: entityId, tag: tag);
        }

        /// <summary>
        /// F (1-012): закрыть ближайшую дверь в радиусе 1 тайл (chebyshev, тор); тайл под нажавшим — отказ
        /// («в проёме F — отказ»). Ближайшая — первая в порядке индекса при равной дистанции (детерминизм).
        /// </summary>
        private void CloseDoorNearest(int slot, long tick)
        {
            TileCoord own = Torus.TileOf(_shape, _entities[slot].Mover.Position);
            int best = -1;
            int bestCheb = int.MaxValue;
            for (int d = 0; d < _doorCount; d++)
            {
                Int2 td = Torus.Delta(_shape, own, new TileCoord(_doorX[d], _doorY[d]));
                int cheb = Math.Max(Math.Abs(td.X), Math.Abs(td.Y));
                if (cheb == 0 || cheb > 1)
                {
                    continue; // свой тайл — отказ; дальше радиуса — не сюда
                }

                if (cheb < bestCheb)
                {
                    bestCheb = cheb;
                    best = d;
                    if (cheb == 1)
                    {
                        break; // дистанций 0 уже нет, 1 — минимум
                    }
                }
            }

            if (best >= 0)
            {
                BeginDoorClose(best, tick, fromF: true);
            }
        }

        private void RemoveDoor(int i)
        {
            // Сдвиг, не swap: канонический порядок (Y, X) сохраняется (см. AddDoor).
            for (; i + 1 < _doorCount; i++)
            {
                _doorX[i] = _doorX[i + 1];
                _doorY[i] = _doorY[i + 1];
                _doorLockUntil[i] = _doorLockUntil[i + 1];
            }

            _doorCount--;
        }

    }
}
