using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// P-002: partial NetServer - per-file code motion, no behavior change.
    /// </summary>
    public sealed partial class NetServer
    {
        private static SnapshotEntity Quantize(in SimEntity e) => new SnapshotEntity
        {
            Id = e.Id,
            Kind = e.Kind,
            Position = SnapshotLayout.Quantize(e.Mover.Position),
            Direction = e.Input.DirectionCode,
            HealthPct = (byte)Math.Max(0, Math.Min(100, e.Health * 100 / Math.Max(1, (int)e.MaxHealth))), // 1-001: доля HP
            HeldItem = e.Inv.Held, // 1-002: предмет в руках — картинка у всех
            Melee = (byte)(e.Mover.Melee.Phase | (e.Mover.Melee.Index << 2)), // 1-004: фаза удара — анимация у чужих
            Enemy = SnapshotLayout.EncodeEnemy(e.Brain.Archetype, e.Brain.SnapshotState), // 1-007: ИИ врага — архетип + состояние
        };

        /// <summary>
        /// Обзор игрока (docs/07, 4.4; 0-022) в <paramref name="cur"/>: свой — первым; позиции квантованы; число сущностей.
        /// Не влезает в снимок (больше <see cref="Snapshot.MaxEntities"/> или 1200 байт) — остальные ближние первыми
        /// (<see cref="Torus.Dist2"/> до своего), дальние отбрасывает обрезка (4.3: приоритет с копилкой — «потом»).
        /// <list type="bullet">
        /// <item>Окно — <see cref="ChunkWindow.Update"/>, как окно чанков клиента (3.5): пересчёт только когда сменился центр
        /// блока окна (персонаж перешёл к другой ближайшей границе чанков), иначе окно то же.</item>
        /// <item>Сущности — из сетки по чанкам (<see cref="Simulation.Grid"/>) блока выхода 8 × 6: входит сущность, чей чанк
        /// в окне; уже бывшая в обзоре остаётся, пока её чанк в блоке 8 × 6 (гистерезис «как у чанков»).</item>
        /// </list>
        /// </summary>
        private int BuildView(Player pl, ReadOnlySpan<SimEntity> entities, int selfSlot, SnapshotEntity[] cur)
        {
            MapShape shape = _sim.Shape;
            WorldPos self = entities[selfSlot].Mover.Position;
            ViewState v = pl.View ??= new ViewState();
            ChunkCoord center = ChunkWindow.BlockCenter(shape, self);
            if (!v.HasWindow || center != v.Center)
            {
                int wn = ChunkWindow.Update(shape, self, new ReadOnlySpan<ChunkCoord>(v.Window, 0, v.WindowCount), v.WindowNext);
                (v.Window, v.WindowNext) = (v.WindowNext, v.Window);
                v.WindowCount = wn;
                v.KeepCount = ChunkWindow.KeepBlock(shape, center, v.Keep);
                var window = new ReadOnlySpan<ChunkCoord>(v.Window, 0, v.WindowCount);
                for (int k = 0; k < v.KeepCount; k++)
                {
                    v.KeepInWindow[k] = ChunkSets.Contains(window, v.Keep[k]);
                }

                v.Center = center;
                v.HasWindow = true;
                ViewWindowRecalcs++;
                SyncChunks(pl, v);
            }

            EntityChunkGrid grid = _sim.Grid;
            int nc = 0;
            for (int k = 0; k < v.KeepCount; k++)
            {
                ChunkCoord c = v.Keep[k];
                int slot = grid.First(c);
                if (slot < 0)
                {
                    continue;
                }

                bool inWindow = v.KeepInWindow[k];
                for (; slot >= 0; slot = grid.Next(slot))
                {
                    if (slot == selfSlot)
                    {
                        continue;
                    }

                    if (!inWindow)
                    {
                        if (!v.IdsSorted)
                        {
                            Array.Sort(v.Ids, 0, v.IdCount);
                            v.IdsSorted = true;
                        }

                        if (Array.BinarySearch(v.Ids, 0, v.IdCount, entities[slot].Id) < 0)
                        {
                            continue; // вне окна и не была в обзоре — не входит
                        }

                        ViewKeptByHysteresis++;
                    }

                    _candKeys[nc++] = slot;
                }
            }

            // Всё влезает в полный снимок (а дельта шлётся, только если она меньше полного) — обрезки не будет, порядок
            // не важен: остаётся порядок обхода (чанки блока по рядам, в чанке — по номеру). Иначе — ближние первыми.
            if (nc + 1 > Snapshot.MaxEntities || _layout.SizeFor(nc + 1) > NetSettings.SnapshotBudget)
            {
                for (int i = 0; i < nc; i++)
                {
                    long d2 = Torus.Dist2(shape, self, entities[(int)_candKeys[i]].Mover.Position);
                    _candKeys[i] = (d2 << CandSlotBits) | _candKeys[i];
                }

                Array.Sort(_candKeys, 0, nc);
                ViewsSortedByDistance++;
            }

            int n = 0;
            cur[n++] = Quantize(entities[selfSlot]);
            for (int i = 0; i < nc && n < Snapshot.MaxEntities; i++)
            {
                cur[n++] = Quantize(entities[(int)(_candKeys[i] & ((1L << CandSlotBits) - 1))]);
            }

            v.IdCount = n - 1;
            for (int i = 1; i < n; i++)
            {
                v.Ids[i - 1] = cur[i].Id;
            }

            v.IdsSorted = false;
            return n;
        }

        private static bool Add(ref SnapshotWriter w, in SimEntity e) =>
            w.TryAdd(e.Id, e.Kind, e.Mover.Position, e.Input.DirectionCode, 100, e.Inv.Held,
                     (byte)(e.Mover.Melee.Phase | (e.Mover.Melee.Index << 2)));

        private static int IndexIn(SnapshotEntity[] ents, int count, int id)
        {
            for (int i = 0; i < count; i++)
            {
                if (ents[i].Id == id)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Полный снимок из <see cref="_cur"/>; возвращает длину, <paramref name="written"/> — сколько влезло.</summary>
        private int WriteFull(uint tick, Player pl, int curCount, out int written)
        {
            var w = new SnapshotWriter(_buf, _layout, tick, pl.LastApplied, _self);
            w.SetClockEpoch(ClockEpoch);
            w.SetWorldTime(WorldTimeTicks);
            for (int i = 0; i < curCount; i++)
            {
                if (!w.TryAdd(_cur[i].Id, _cur[i].Kind, _cur[i].Position, _cur[i].Direction, _cur[i].HealthPct, _cur[i].HeldItem, _cur[i].Melee, _cur[i].Enemy))
                {
                    break;
                }
            }

            written = w.Count;
            return w.Finish();
        }

        /// <summary>
        /// «Сумка» игроку (1-002, тип 26, канал 2): сумка его сущности — при входе и при каждом изменении
        /// (сверка в <see cref="SendSnapshots"/>). Версия растёт при каждой отправке; порядок хранит канал 2.
        /// </summary>
        private void SendInventoryTo(Player pl, in Inventory bag)
        {
            pl.InvVersion = unchecked((ushort)(pl.InvVersion + 1));
            pl.SentInv = bag;
            pl.HasSentInv = true;
            int len = new InventoryMessage { EntityId = pl.EntityId, Version = pl.InvVersion, Bag = bag }.Write(_buf);
            _transport.Send(pl.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            InventoriesSent++;
        }

        /// <summary>
        /// После такта: снимок каждому игроку в игре (0-014, 4.3). База — снимок, который клиент подтвердил
        /// (<c>LastSnapshot</c> ввода) и который ещё лежит в кольце «что клиент имеет» (32 такта). Есть база —
        /// дельта к ней; нет (вход, потеря больше секунды) — полный. Плюс контрольный полный раз в
        /// <see cref="KeyframePeriod"/> тактов и полный вместо дельты, если дельта не меньше полного (все вокруг бегут).
        /// </summary>
        public void SendSnapshots()
        {
            BroadcastPatches(); // правки такта — до окон: вошедшие в окно чанки уйдут уже с ревизией после такта (3.7)
            BroadcastCombatEvents(); // 1-001: смерть/возрождение такта — канал 2 всем в игре
            SendEditReplies(); // 0-028: «принято» / «отказ» по исходам такта
            ReadOnlySpan<SimEntity> entities = _sim.Entities;
            uint tick = (uint)_sim.Tick;
            for (int p = 0; p < _players.Count; p++)
            {
                Player pl = _players[p];
                if (pl.State == PlayerState.InGame)
                {
                    SendSnapshotTo(pl, entities, tick);
                    int slot = _sim.SlotOf(pl.EntityId);
                    if (slot >= 0 && (!pl.HasSentInv || entities[slot].Inv != pl.SentInv))
                    {
                        SendInventoryTo(pl, entities[slot].Inv); // 1-002: сумка изменилась (копание, подбор, смена ячейки)
                        int ci = Characters.FindEntity(pl.EntityId); // 1-004: запись реестра — за сущностью, сейв в мире берёт живую сумку
                        if (ci >= 0)
                        {
                            Characters.SetInventory(ci, entities[slot].Inv);
                        }
                    }
                }
            }

            StepEnemyThreats(); // 1-007: замахи врагов с ИИ — телеграф (удар уже решён симуляцией)
            SendSwings();
            _transport.Flush(); // снимки такта — сразу, а не по таймеру транспорта (0-015)
        }

        /// <summary>
        /// Дельта к базе в <see cref="_buf"/>; состояние клиента после неё — в <see cref="_merged"/>. Дельта не меньше
        /// полного — −1 (вызывающий шлёт полный). Сервер читает свою же дельту и склеивает её той же
        /// <see cref="SnapshotDeltaMessage.ApplyTo"/>, что и клиент, — номера сущностей в базе совпадают по построению.
        /// <para>
        /// Бюджет 1200 (4.3; 0-022 — ближние первыми): исчезнувшие (≤ 255 байт) влезают всегда; изменившиеся и новые
        /// берутся по порядку обзора — свой, затем ближние — пока влезают; первая не влезшая и все дальше неё не
        /// пишутся (не влезшее изменение клиент оставит из базы, не влезшую новую дошлёт следующая дельта).
        /// </para>
        /// </summary>
        private int TryWriteDelta(uint tick, Player pl, SnapshotEntity[] baseEnts, int baseCount, int curCount,
                                  out bool truncated, out int mergedCount)
        {
            truncated = false;
            mergedCount = 0;
            for (int ci = 0; ci < curCount; ci++)
            {
                _baseOf[ci] = -1;
            }

            int removedN = 0;
            int bits = SnapshotDeltaMessage.HeaderBytes * 8 + _layout.SelfBits;
            for (int j = 0; j < baseCount; j++)
            {
                SnapshotEntity b = baseEnts[j];
                int ci = IndexIn(_cur, curCount, b.Id);
                if (ci < 0 || _cur[ci].Kind != b.Kind)
                {
                    _removedIdx[removedN++] = j; // ушла из обзора или сменила вид (тогда придёт новой)
                    bits += SnapshotDeltaMessage.IndexBits;
                    continue;
                }

                _baseOf[ci] = j;
            }

            // Записи по порядку обзора: размер всей дельты (сравнить с полным) и сколько влезает в бюджет.
            int limitBits = NetSettings.SnapshotBudget * 8;
            int totalBits = bits;
            int updatedN = 0;
            int addedN = 0;
            for (int ci = 0; ci < curCount; ci++)
            {
                int j = _baseOf[ci];
                int rec;
                DeltaFields f = DeltaFields.None;
                if (j >= 0)
                {
                    SnapshotEntity b = baseEnts[j];
                    if (_cur[ci].Position != b.Position)
                    {
                        f |= DeltaFields.Position;
                    }

                    if (_cur[ci].Direction != b.Direction)
                    {
                        f |= DeltaFields.Direction;
                    }

                    if (_cur[ci].HealthPct != b.HealthPct)
                    {
                        f |= DeltaFields.Health; // 1-001
                    }

                    if (_cur[ci].HeldItem != b.HeldItem)
                    {
                        f |= DeltaFields.Held; // 1-002
                    }

                    if (_cur[ci].Melee != b.Melee)
                    {
                        f |= DeltaFields.Melee; // 1-004
                    }

                    if (_cur[ci].Enemy != b.Enemy)
                    {
                        f |= DeltaFields.Enemy; // 1-007
                    }

                    if (f == DeltaFields.None)
                    {
                        continue;
                    }

                    rec = SnapshotDeltaMessage.UpdatedBits(f, _layout);
                }
                else
                {
                    rec = _layout.EntityBits;
                }

                totalBits += rec;
                if (truncated || bits + rec > limitBits)
                {
                    truncated = true; // эта и все дальше — в следующую дельту
                    continue;
                }

                bits += rec;
                if (j >= 0)
                {
                    _updBase[updatedN] = j;
                    _updCur[updatedN] = ci;
                    _updFields[updatedN] = f;
                    updatedN++;
                }
                else
                {
                    _addCur[addedN++] = ci;
                }
            }

            if ((totalBits + 7) / 8 >= _layout.SizeFor(curCount))
            {
                truncated = false;
                return -1;
            }

            var w = new SnapshotDeltaWriter(_buf, _layout, tick, pl.LastApplied, pl.LastSnapshotAck, _self);
            w.SetClockEpoch(ClockEpoch);
            w.SetWorldTime(WorldTimeTicks);
            for (int i = 0; i < removedN; i++)
            {
                w.TryAddRemoved(_removedIdx[i]);
            }

            for (int i = 0; i < updatedN; i++)
            {
                SnapshotEntity e = _cur[_updCur[i]];
                truncated |= !w.TryAddUpdated(_updBase[i], _updFields[i], e.Position, e.Direction, e.HealthPct, e.HeldItem, e.Melee, e.Enemy);
            }

            for (int i = 0; i < addedN; i++)
            {
                SnapshotEntity e = _cur[_addCur[i]];
                truncated |= !w.TryAddAdded(e.Id, e.Kind, e.Position, e.Direction, e.HealthPct, e.HeldItem, e.Melee, e.Enemy);
            }

            int len = w.Finish();
            _delta.Read(new ReadOnlySpan<byte>(_buf, 0, len), _layout);
            mergedCount = _delta.ApplyTo(baseEnts, baseCount, _merged);
            return len;
        }

        /// <summary>Снимок такта <paramref name="tick"/> одному игроку (дельта или полный); персонажа нет — false.</summary>
        private bool SendSnapshotTo(Player pl, ReadOnlySpan<SimEntity> entities, uint tick)
        {
            int selfSlot = _sim.SlotOf(pl.EntityId);
            if (selfSlot < 0)
            {
                return false;
            }

            Mover me = entities[selfSlot].Mover;
            _self = new SelfState(me.Position, me.Carry, me.Dodge) // подтверждённое состояние без кванта (2.6 п. 2; 0-019), с уворотом (0-029)
            {
                LeadMargin = (sbyte)Math.Max(-SelfState.MarginBias, Math.Min(SelfState.MarginBias - 1, pl.Margin)), // 0-033
                DelayFrames = (byte)pl.DelayFrames,
                Health = (byte)Math.Max(0, Math.Min(255, (int)entities[selfSlot].Health)), // 1-001: бой — клиент берёт целиком
                Mass = (byte)Math.Max(1, Math.Min(4, (int)entities[selfSlot].Mass)),
                KnockLeft = (byte)Math.Min(255, (int)me.KnockLeft),
                KnockDir = (byte)Math.Min(255, (int)me.KnockDir),
                Stamina = me.Stamina.Value, // 1-003: выносливость — клиент берёт целиком, как уворот
            };
            int curCount = BuildView(pl, entities, selfSlot, _cur);
            SentRing ring = pl.Ring ??= new SentRing();
            int bi = ring.Find(pl.LastSnapshotAck);
            bool keyframe = KeyframePeriod <= 1 || tick % KeyframePeriod == 0;
            uint age = tick - pl.LastSnapshotAck;

            int len = -1;
            bool truncated = false;
            if (bi >= 0 && !keyframe && age >= 1 && age <= SnapshotDeltaMessage.MaxBaselineAge)
            {
                len = TryWriteDelta(tick, pl, ring.Ents[bi], ring.Count[bi], curCount, out truncated, out int mergedCount);
                if (len > 0)
                {
                    ring.Push(tick, _merged, mergedCount);
                    DeltasSent++;
                    DeltaBytes += len;
                    if (len > MaxDeltaBytes)
                    {
                        MaxDeltaBytes = len;
                    }
                }
            }

            if (len < 0)
            {
                len = WriteFull(tick, pl, curCount, out int written);
                truncated = written < curCount;
                ring.Push(tick, _cur, written); // у клиента будет ровно то, что влезло
                FullSent++;
                if (len > MaxSnapshotBytes)
                {
                    MaxSnapshotBytes = len;
                }
            }

            if (truncated)
            {
                SnapshotsTruncated++;
                if (SnapshotsTruncated == 1 || SnapshotsTruncated % 300 == 0)
                {
                    Log?.Invoke(NetLogLevel.Error, $"снимок для #{pl.EntityId} больше {NetSettings.SnapshotBudget} байт: обрезан (всего обрезано {SnapshotsTruncated})");
                }
            }

            _transport.Send(pl.Peer, NetChannel.Unreliable, new ReadOnlySpan<byte>(_buf, 0, len));
            SnapshotsSent++;
            SnapshotBytes += len;
            pl.SentTick = tick;
            pl.HasSentTick = true;
            return true;
        }

    }
}
