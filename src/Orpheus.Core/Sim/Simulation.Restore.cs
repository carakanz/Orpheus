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
        /// <summary>
        /// Загрузка сохранения (docs/07, 6.4: состояние на границе такта): номер такта и сущности со всеми
        /// полями. Только в пустую симуляцию до первого такта. Сущности — строго по возрастанию номера;
        /// следующий номер — больше всех загруженных (номера не переиспользуются). Ввод сущности — тот,
        /// по которому она шла в такте сохранения; ждущих кадров нет.
        /// </summary>
        public void Restore(long tick, ReadOnlySpan<SimEntity> entities, int nextId)
        {
            Restore(tick, entities, nextId, 0);
        }

        /// <summary>
        /// Загрузка с временем мира (1-014): то же + <see cref="WorldTime"/> (формат 6, поле МИР 10;
        /// у сохранений до 1-014 — 0).
        /// </summary>
        public void Restore(long tick, ReadOnlySpan<SimEntity> entities, int nextId, int worldTime)
        {
            if (Tick != 0 || _entityCount != 0 || _editCount != 0)
            {
                throw new InvalidOperationException("Загрузка — только в пустую симуляцию до первого такта.");
            }

            if (tick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tick), tick, "Такт не отрицательный.");
            }

            if (entities.Length > _entities.Length)
            {
                throw new ArgumentException($"Сущностей {entities.Length}, ёмкость симуляции {_entities.Length}.", nameof(entities));
            }

            int maxId = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                if (entities[i].Id <= maxId)
                {
                    throw new ArgumentException($"Сущность № {i}: номер {entities[i].Id} не больше предыдущего {maxId}.", nameof(entities));
                }

                maxId = entities[i].Id;
            }

            for (int i = 0; i < entities.Length; i++)
            {
                _entities[i] = RestoreDefaults(entities[i]);
                _hasPending[i] = false;
                _frameCount[i] = -1;
            }

            _entityCount = entities.Length;
            _grid.Rebuild(_shape, Entities);
            ResetPathCaches(); // 1-007: кэш путей — производный, в сейве его нет; после загрузки всё переспрашивается
            _nextId = Math.Max(nextId, maxId + 1);
            Tick = tick;
            SetWorldTime(worldTime);
        }

        /// <summary>Следующий номер сущности (пишется в сохранение: номера не переиспользуются).</summary>
        public int NextEntityId => _nextId;

        /// <summary>
        /// Здоровье и боевые таймеры едут в сейве с формата 6 (1-007). Форматы 1–5 их не пишут (0 при чтении) —
        /// сущность получает дефолты спавна: вернувшийся персонаж входит с полным здоровьем («Не трогать» 0-037,
        /// `08` 3.1). Признак старого формата — MaxHealth = 0: в формате 6 он всегда положителен.
        /// </summary>
        private static SimEntity RestoreDefaults(SimEntity e)
        {
            if (e.MaxHealth <= 0)
            {
                e.MaxHealth = (short)(e.Kind == SimEntity.KindEnemy ? MaxHealthMeleeEnemy : MaxHealthCharacter);
                e.Health = e.MaxHealth;
                e.Mass = 1;
                e.RegenTicks = RegenDelayTicks;
                e.AttackTarget = -1;
                e.AttackPeriod = -1;
            }

            if (e.Brain.Archetype != EnemyArchetype.None)
            {
                // ИИ (1-007): −1 в полях «нет цели» — строчка по умолчанию (format 5 без этих полей) это 0.
                if (e.Brain.TargetId == 0)
                {
                    e.Brain.TargetId = EnemyBrain.NoTarget;
                }

                if (e.Brain.LastHitBy == 0)
                {
                    e.Brain.LastHitBy = EnemyBrain.NoTarget;
                }

                if (e.Brain.State == EnemyState.None)
                {
                    e.Brain.State = EnemyState.Idle;
                }

                if (e.Brain.HomeX == 0 && e.Brain.HomeY == 0)
                {
                    e.Brain.HomeX = e.Brain.GoalX;
                    e.Brain.HomeY = e.Brain.GoalY;
                }
            }

            return e;
        }
    }
}
