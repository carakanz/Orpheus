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
        /// <summary>Сущность по номеру (копия).</summary>
        public SimEntity GetEntity(int entityId)
        {
            int slot = SlotOf(entityId);
            if (slot < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(entityId), entityId, "Нет такой сущности.");
            }

            return _entities[slot];
        }

        /// <summary>Место сущности в списке по номеру; нет — −1.</summary>
        public int SlotOf(int entityId)
        {
            int lo = 0;
            int hi = _entityCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int id = _entities[mid].Id;
                if (id == entityId)
                {
                    return mid;
                }

                if (id < entityId)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return -1;
        }

        /// <summary>Ближайший живой персонаж к <paramref name="from"/> в пределах √dist2Limit; при равенстве — меньший номер; нет — −1.
        /// <paramref name="ignoreId"/> (1-013, 9.3 «игнор 30 с») — цель, пропущенная при выборе.</summary>
        private int NearestCharacterWithin(WorldPos from, long dist2Limit, int ignoreId)
        {
            int best = -1;
            long bestD = long.MaxValue;
            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].IsCharacter || !_entities[i].Alive || _entities[i].Id == ignoreId)
                {
                    continue;
                }

                long d2 = Torus.Dist2(_shape, from, _entities[i].Mover.Position);
                if (d2 <= dist2Limit && d2 < bestD)
                {
                    bestD = d2;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>Возрождение (13.5, 1-001): точка старта, полное здоровье, 3 с неуязвимости (снимается первой атакой), без потерь.</summary>
        private void Respawn(ref SimEntity e, long tick)
        {
            e.Health = e.MaxHealth;
            e.Mover = new Mover(Torus.Wrap(_shape, _respawnCenter), e.Mover.UnitsPerTickX10);
            e.InvulnTicks = InvulnRespawnTicks;
            e.RegenTicks = RegenDelayTicks;
            e.RegenCarryX100 = 0;
            e.CombatTicks = 0;
            e.Input = InputFrame.None;
            AddEvent(new SimEvent(SimEventKind.Respawn, tick, default, -1, EditResult.Applied, 0, victim: e.Id));
        }

        /// <summary>Ближайший живой персонаж к <paramref name="from"/> (перебор по номеру, дистанция на центр ≤
        /// <paramref name="maxDistUnits"/>; при равенстве — меньший номер); нет — −1.</summary>
        private int NearestCharacterSlot(WorldPos from, int maxDistUnits)
        {
            int best = -1;
            long bestD = long.MaxValue;
            long limit = (long)maxDistUnits * maxDistUnits;
            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].IsCharacter || !_entities[i].Alive)
                {
                    continue;
                }

                Int2 d = Torus.Delta(_shape, from, _entities[i].Mover.Position);
                long dist = d.LengthSquared;
                if (dist <= limit && (best < 0 || dist < bestD))
                {
                    bestD = dist;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// Вернуть персонажа в мир с его движением (0-035, docs/07 7.4: вернувшийся игрок — тот же персонаж там же):
        /// позиция (свернётся в карту), остаток скорости, скорость, уворот — как в <paramref name="mover"/>; ввод пустой.
        /// Номер — следующий по счёту (номера не переиспользуются). Вне такта. Возвращает номер.
        /// </summary>
        public int AddEntity(in Mover mover)
        {
            int id = AddEntity(mover.Position, mover.UnitsPerTickX10);
            int slot = SlotOf(id);
            WorldPos at = _entities[slot].Mover.Position;
            _entities[slot].Mover = mover;
            _entities[slot].Mover.Position = at;
            return id;
        }

        /// <summary>
        /// Добавить персонажа в позицию <paramref name="position"/> (свернётся в карту). Номер — следующий
        /// по счёту: список остаётся по возрастанию номера. Возвращает номер.
        /// </summary>
        public int AddEntity(WorldPos position, int unitsPerTickX10 = MovementTick.BaseSpeedX10)
        {
            if (_entityCount == _entities.Length)
            {
                throw new InvalidOperationException($"Сущностей больше {_entities.Length} — ёмкость симуляции.");
            }

            return AddEntityWithId(_nextId, position, unitsPerTickX10);
        }

        /// <summary>
        /// Добавить врага-заглушку (<see cref="SimEntity.KindEnemy"/>; 0-028 — для правила «не ближе 2 тайлов к врагу»): стоит,
        /// пока ему не дали ввод. Здоровье архетипа «Ближний» (35), сам не замахивается (<see cref="StartAttack"/> — удар,
        /// <see cref="EnableAttackCycle"/> — манекен). Номер — следующий. Возвращает номер.
        /// </summary>
        public int AddEnemy(WorldPos position)
        {
            int id = AddEntity(position);
            int slot = SlotOf(id);
            _entities[slot].Kind = SimEntity.KindEnemy;
            _entities[slot].MaxHealth = MaxHealthMeleeEnemy;
            _entities[slot].Health = MaxHealthMeleeEnemy;
            _entities[slot].AttackRaw = (byte)StrikeRawMelee;
            return id;
        }

        /// <summary>
        /// Убрать сущность (игрок ушёл; точка подключения сети, 0-008) — <b>вне такта</b>. Список остаётся по номеру,
        /// номер не переиспользуется. Нет такой — false.
        /// </summary>
        public bool RemoveEntity(int entityId)
        {
            int slot = SlotOf(entityId);
            if (slot < 0)
            {
                return false;
            }

            int tail = _entityCount - slot - 1;
            Array.Copy(_entities, slot + 1, _entities, slot, tail);
            Array.Copy(_pending, slot + 1, _pending, slot, tail);
            Array.Copy(_hasPending, slot + 1, _hasPending, slot, tail);
            Array.Copy(_frameCount, slot + 1, _frameCount, slot, tail);
            Array.Copy(_frames, (slot + 1) * MaxFramesPerTick, _frames, slot * MaxFramesPerTick, tail * MaxFramesPerTick);
            _entityCount--;
            _entities[_entityCount] = default;
            _pending[_entityCount] = default;
            _hasPending[_entityCount] = false;
            _frameCount[_entityCount] = -1;
            _grid.Rebuild(_shape, Entities); // места после удалённой сдвинулись
            ResetPathCaches(); // 1-007: слоты сдвинулись — владельцы запросов путей протухли
            return true;
        }

        /// <summary>
        /// Добавить персонажа с заданным номером (повтор журнала: номера из записи). Номер больше всех
        /// прежних — список по номеру не ломается.
        /// </summary>
        public int AddEntityWithId(int id, WorldPos position, int unitsPerTickX10 = MovementTick.BaseSpeedX10)
        {
            if (_entityCount == _entities.Length)
            {
                throw new InvalidOperationException($"Сущностей больше {_entities.Length} — ёмкость симуляции.");
            }

            if (id < _nextId)
            {
                throw new ArgumentOutOfRangeException(nameof(id), id, $"Номер сущности должен быть ≥ {_nextId}.");
            }

            int slot = _entityCount++;
            _entities[slot] = new SimEntity
            {
                Id = id,
                Kind = SimEntity.KindCharacter,
                Mover = Mover.At(_shape, position, unitsPerTickX10),
                Input = InputFrame.None,
                Health = MaxHealthCharacter,
                MaxHealth = MaxHealthCharacter,
                Mass = 1,
                RegenTicks = RegenDelayTicks,
                AttackTarget = -1,
                AttackPeriod = -1,
            };
            _hasPending[slot] = false;
            _frameCount[slot] = -1;
            _grid.Insert(slot, Torus.ChunkOf(_shape, _entities[slot].Mover.Position));
            _nextId = id + 1;
            return id;
        }

    }
}
