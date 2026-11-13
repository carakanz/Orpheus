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
        /// <summary>Идти к центру тайла (ввод как у персонажа); пришли (ближе 3/4 тайла) — ввод пустой, true.</summary>
        private bool MoveToward(ref SimEntity e, TileCoord tile)
        {
            Int2 d = Torus.Delta(_shape, e.Mover.Position, EditRules.CenterOf(Torus.Wrap(_shape, tile)));
            if (d.LengthSquared <= MeleeRogue.NodePass2)
            {
                e.Input = InputFrame.None;
                return true;
            }

            e.Input = new InputFrame(Input8.FromStick(d.X, d.Y));
            return false;
        }

        /// <summary>Новый планировщик путей (очередь по слотам; без часов — поиск ровно PathMaxPerTick за такт).</summary>
        private PathScheduler NewPathScheduler() =>
            new PathScheduler(_pathFinder, _pathTerrain, _pathBuf, PathQueueCapacity, PathMaxPerTick);

        /// <summary>
        /// Сброс кэша путей (1-007): слоты сдвинулись (<see cref="RemoveEntity"/>) или состояние заменили
        /// (<see cref="Restore"/>) — владельцы запросов протухли; буферы очищаются, пути переспрашиваются.
        /// </summary>
        private void ResetPathCaches()
        {
            for (int i = 0; i < _entities.Length; i++)
            {
                _pathBuf[i].Clear();
                _entities[i].Brain.PathStamp = 0;
                _entities[i].Brain.PathPos = 0;
            }

            _paths = NewPathScheduler();
        }

        /// <summary>
        /// Преследование (1-007, 9.3): цель в зоне удара handled выше; тут — путь по плану A* (окно 48 × 48,
        /// планировщик такта) или прямо к цели, пока путь в работе/не готов. Переспрос: цель ушла дальше тайла,
        /// каждые 150 тактов или мир правили с запроса («изменился тайл» — сразу, 1-013). Нет пути (Enclosed) —
        /// Blocked («упёрся», 9.3): стоит; пересчёт — при правке мира (кэш Enclosed до смены тайла, з.2);
        /// 20 с — цель в игнор на 30 с, враг в Ожидание; дальше 60 тайлов от дома — то же + домой (з.3).
        /// </summary>
        private void Chase(ref SimEntity e, ref SimEntity target, int slot, long tick)
        {
            ref EnemyBrain b = ref e.Brain;
            e.Mover.UnitsPerTickX10 = MeleeRogue.ChaseSpeedX10;

            if (b.State == EnemyState.Blocked)
            {
                // «Упёрся» (1-013, 9.3): стоит у преграды; пересчёт — сразу при правке мира (ревизия сдвинулась);
                // через 20 с цель уходит в игнор на 30 с, враг — в Ожидание (не застревает).
                e.Input = InputFrame.None;
                long blockedFor = tick - b.BlockedSince;
                if (b.BlockedSince != 0 && blockedFor >= MeleeRogue.BlockedGiveUpTicks)
                {
                    b.IgnoreTargetId = b.TargetId;
                    b.IgnoreUntil = tick + MeleeRogue.IgnoreTargetTicks;
                    b.TargetId = EnemyBrain.NoTarget;
                    b.State = EnemyState.Idle;
                    b.StateTicks = 0;
                    b.PathStamp = 0;
                    b.BlockedSince = 0;
                    b.EnclosedRev = 0;
                    return;
                }

                if (b.EnclosedRev != 0 && _editRev + 1 != b.EnclosedRev)
                {
                    // Тайл в окне поменялся (стена снесена, дверь открыта) — пересчёт сразу, без ожидания (9.3).
                    TileCoord start = Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position));
                    TileCoord bt = Torus.Wrap(_shape, Torus.TileOf(_shape, target.Mover.Position));
                    if (_paths.Enqueue(slot, start, bt, PathMode.Walker, tick))
                    {
                        b.PathStamp = (int)tick;
                        b.PathPos = 0;
                        b.PathRev = _editRev;
                        b.EnclosedRev = 0;
                        b.ProgressTick = (int)tick;
                        b.ProgressX = e.Mover.Position.X;
                        b.ProgressY = e.Mover.Position.Y;
                        b.GoalX = bt.X;
                        b.GoalY = bt.Y;
                        b.State = EnemyState.Chase;
                        b.StateTicks = 0;
                    }
                }
                else if (b.EnclosedRev == 0 && blockedFor % MeleeRogue.BlockedRetryTicks == 0 &&
                    (tick + e.Id) % AiDecisionTicks == 0)
                {
                    // Blocked без кэша (загрузка сейва): переспрос каждые 2 с, как в 8.4.
                    b.State = EnemyState.Chase;
                    b.StateTicks = 0;
                    b.PathStamp = 0;
                    b.ProgressTick = (int)tick;
                    b.ProgressX = e.Mover.Position.X;
                    b.ProgressY = e.Mover.Position.Y;
                }

                return;
            }

            if (b.State != EnemyState.Chase)
            {
                b.State = EnemyState.Chase;
                b.StateTicks = 0;
                b.PathStamp = 0;
                b.ProgressTick = (int)tick;
                b.ProgressX = e.Mover.Position.X;
                b.ProgressY = e.Mover.Position.Y;
            }
            else if (Torus.Dist2(_shape, e.Mover.Position, new WorldPos(b.ProgressX, b.ProgressY)) >= MeleeRogue.ProgressMove2)
            {
                // Продвинулись — контрольная точка за текущей позицией, таймер «упёрся» сброшен.
                b.ProgressTick = (int)tick;
                b.ProgressX = e.Mover.Position.X;
                b.ProgressY = e.Mover.Position.Y;
            }
            else if (b.ProgressTick != 0 && tick - b.ProgressTick >= MeleeRogue.StuckTicks)
            {
                // «Упёрся» без продвижения (1-013, 9.3): 2 с упираемся в преграду (путь Found идёт через
                // стену под ногами) — Blocked; пересчёт при правке мира (кэш), сдача через 20 с.
                b.State = EnemyState.Blocked;
                b.StateTicks = 0;
                b.PathStamp = 0;
                b.EnclosedRev = _editRev + 1;
                if (b.BlockedSince == 0)
                {
                    b.BlockedSince = tick;
                }

                e.Input = InputFrame.None;
                return;
            }

            TileCoord goal = Torus.Wrap(_shape, Torus.TileOf(_shape, target.Mover.Position));
            PathBuffer buf = _pathBuf[slot];

            // Не дальше 60 тайлов от точки появления (1-013 з.3, 0-028): увязся далеко — цель в игнор, домой (Lost).
            if (Torus.Dist2(_shape, e.Mover.Position,
                EditRules.CenterOf(Torus.Wrap(_shape, new TileCoord(b.HomeX, b.HomeY)))) > MeleeRogue.Leash2)
            {
                b.IgnoreTargetId = b.TargetId;
                b.IgnoreUntil = tick + MeleeRogue.IgnoreTargetTicks;
                b.TargetId = EnemyBrain.NoTarget;
                b.State = EnemyState.Lost;
                b.StateTicks = 0;
                b.PathStamp = 0;
                b.BlockedSince = 0;
                b.EnclosedRev = 0;
                return;
            }

            // Запрос в работе/не ставился (PathStamp = 0) — идём прямо к цели (на открытой земле путь и не нужен).
            if (b.PathStamp == 0)
            {
                if ((tick + e.Id) % AiDecisionTicks == 0)
                {
                    TileCoord start = Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position));
                    if (_paths.Enqueue(slot, start, goal, PathMode.Walker, tick))
                    {
                        b.PathStamp = (int)tick;
                        b.PathPos = 0;
                        b.PathRev = _editRev;
                        b.GoalX = goal.X;
                        b.GoalY = goal.Y;
                    }
                }

                MoveToward(ref e, goal);
                return;
            }

            // Запрос в очереди (путь ещё не готов): тоже идём прямо.
            if (buf.RequestedTick != b.PathStamp)
            {
                MoveToward(ref e, goal);
                return;
            }

            // Цель ушла дальше тайла от запрошенной, путь старый (150 тактов) или мир правили с запроса
            // («изменился тайл» — 1-013, 9.3: пересчёт сразу) — новый запрос.
            bool stale = b.StateTicks % MeleeRogue.RepathTicks == 0 ||
                Torus.Dist2(_shape, EditRules.CenterOf(goal), EditRules.CenterOf(new TileCoord(b.GoalX, b.GoalY))) >
                    (long)MeleeRogue.RepathGoalDelta * MeleeRogue.RepathGoalDelta;
            bool revised = _editRev != b.PathRev;
            if (stale || revised)
            {
                if (revised || (tick + e.Id) % AiDecisionTicks == 0)
                {
                    TileCoord start = Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position));
                    if (_paths.Enqueue(slot, start, goal, PathMode.Walker, tick))
                    {
                        b.PathStamp = (int)tick;
                        b.PathPos = 0;
                        b.PathRev = _editRev;
                        b.EnclosedRev = 0;
                        b.GoalX = goal.X;
                        b.GoalY = goal.Y;
                    }
                }

                MoveToward(ref e, goal);
                return;
            }

            switch (buf.Status)
            {
                case PathStatus.Enclosed:
                    // Пути нет вообще (8.4, 1-013): Blocked — стоит у стены; пересчёт — только при правке мира;
                    // 20 с — сдаётся (игнор цели 30 с). Кэш «Enclosed до смены тайла» (з.2): переспрос не дороже 1 A*.
                    b.State = EnemyState.Blocked;
                    b.StateTicks = 0;
                    b.PathStamp = 0;
                    b.EnclosedRev = _editRev + 1;
                    if (b.BlockedSince == 0)
                    {
                        b.BlockedSince = tick;
                    }

                    e.Input = InputFrame.None;
                    return;

                case PathStatus.None:
                    MoveToward(ref e, goal); // буфер сброшен (RemoveEntity/Restore) — прямо, переспросит
                    b.PathStamp = 0;
                    return;

                default:
                    b.BlockedSince = 0; // путь есть — «упёрся» не копится (1-013)
                    break; // Found / WindowEdge / Partial — идём по шагам
            }

            // Путь готов (Found — до цели; WindowEdge — до края окна; Partial — предельная длина): по шагам.
            int steps = buf.Length;
            while (b.PathPos < steps &&
                Torus.Dist2(_shape, e.Mover.Position,
                    EditRules.CenterOf(Torus.Wrap(_shape, buf.Steps[b.PathPos]))) <= MeleeRogue.NodePass2)
            {
                b.PathPos++;
            }

            if (b.PathPos < steps)
            {
                TileCoord node = Torus.Wrap(_shape, buf.Steps[b.PathPos]);
                e.Mover.UnitsPerTickX10 = MeleeRogue.ChaseSpeedX10;
                Int2 d = Torus.Delta(_shape, e.Mover.Position, EditRules.CenterOf(node));
                e.Input = new InputFrame(Input8.FromStick(d.X, d.Y));
                return;
            }

            // Шаги кончились (дошли до края окна/предела/цели) — прямо к цели; переспрос по правилам выше.
            MoveToward(ref e, goal);
        }

        /// <summary>
        /// Добавить «Ближнего» с мозгом (разбойник, архетип <see cref="EnemyArchetype.Melee"/>, карточка 1-007):
        /// дом — точка появления, старт в Idle; числа — <see cref="MeleeRogue"/>. Номер — следующий. Возвращает номер.
        /// </summary>
        public int AddMeleeRogue(WorldPos position, int healthMultX1024 = 1024)
        {
            int id = AddEnemy(position);
            int slot = SlotOf(id);
            ref SimEntity e = ref _entities[slot];
            short hp = (short)ScaledByNight(MeleeRogue.MaxHealth, healthMultX1024); // ночь ×1,25 → 44 (8.6, 1-009)
            e.MaxHealth = hp;
            e.Health = hp;
            e.AttackRaw = (byte)MeleeRogue.StrikeDamage; // удар ночью масштабируется в ApplyStrike (8.6)
            e.AttackPeriod = MeleeRogue.PeriodTicks;
            e.Mover.UnitsPerTickX10 = RogueWanderSpeedX10;
            TileCoord home = Torus.TileOf(_shape, e.Mover.Position);
            e.Brain = new EnemyBrain
            {
                Archetype = EnemyArchetype.Melee,
                State = EnemyState.Idle,
                TargetId = EnemyBrain.NoTarget,
                HomeX = home.X,
                HomeY = home.Y,
                GoalX = home.X,
                GoalY = home.Y,
                LastHitBy = EnemyBrain.NoTarget,
                IgnoreTargetId = EnemyBrain.NoTarget,
                CauseId = EnemyBrain.NoTarget,
            };
            return id;
        }

        /// <summary>
        /// Фаза ИИ такта (после ввода, до движения): цели (зрение/агро), состояние, направление. Движение —
        /// общая фаза по <see cref="SimEntity.Input"/> (враг идёт как персонаж по своему «вводу»).
        /// Порядок — по номеру сущности; решения каждые <see cref="AiDecisionTicks"/> тактов с фазой по номеру (9.3).
        /// </summary>
        private void StepAI(long tick)
        {
            ulong seed = _world.Seed;
            for (int i = 0; i < _entityCount; i++)
            {
                ref SimEntity e = ref _entities[i];
                if (e.Kind != SimEntity.KindEnemy || e.Brain.Archetype == EnemyArchetype.None || !e.Alive)
                {
                    continue; // манекен (1-001) и заглушка (0-028) — Brain.Archetype = 0, стоят
                }

                ref EnemyBrain b = ref e.Brain;
                b.StateTicks++;
                UpdateTarget(ref e, tick);

                int ts = b.TargetId >= 0 ? SlotOf(b.TargetId) : -1;
                if (ts >= 0)
                {
                    ref SimEntity target = ref _entities[ts];
                    long dist2 = Torus.Dist2(_shape, e.Mover.Position, target.Mover.Position);
                    if (dist2 <= MeleeRogue.StrikeRange2)
                    {
                        // В зоне удара (8.1 «бьёт в упор»): стоит; замах ведёт фаза боя по состоянию Attack.
                        e.Mover.UnitsPerTickX10 = MeleeRogue.ChaseSpeedX10;
                        if (b.State != EnemyState.Attack)
                        {
                            b.State = EnemyState.Attack;
                            b.StateTicks = 0;
                            b.PathStamp = 0;
                            b.BlockedSince = 0; // дошёл — эпизод «упёрся» закрыт (1-013)
                        }

                        e.Input = InputFrame.None;
                        continue;
                    }

                    Chase(ref e, ref target, i, tick);
                    continue;
                }

                // «Интерес» (1-009, 8.5): появился из спавнера — 20 с идёт шагом в сторону игрока-причины
                // (знает направление, не позицию: прямо, без пути; скорость как «домой»). Игрок в зрении —
                // цель берёт UpdateTarget выше, дальше Преследование штатно. Истёк/игрок потерян — Ожидание.
                if (b.State == EnemyState.Interest)
                {
                    e.Mover.UnitsPerTickX10 = RogueHomeSpeedX10;
                    int cs = SlotOf(b.CauseId);
                    if (b.InterestUntil == 0 || tick >= b.InterestUntil || cs < 0 || !_entities[cs].Alive)
                    {
                        b.State = EnemyState.Idle;
                        b.StateTicks = 0;
                        b.CauseId = EnemyBrain.NoTarget;
                        b.InterestUntil = 0;
                        continue;
                    }

                    MoveToward(ref e, Torus.TileOf(_shape, _entities[cs].Mover.Position)); // прямо, без A*
                    continue;
                }

                // Цели нет: Idle/Wander у дома; после потери — Lost (домой), зрение переспрашивает целью выше (8.1, 8.3).
                TileCoord home = new TileCoord(b.HomeX, b.HomeY);
                if (b.State == EnemyState.Wander)
                {
                    e.Mover.UnitsPerTickX10 = RogueWanderSpeedX10;
                    if (MoveToward(ref e, new TileCoord(b.GoalX, b.GoalY)))
                    {
                        b.State = EnemyState.Idle;
                        b.StateTicks = 0;
                    }

                    continue;
                }

                if (b.State == EnemyState.Idle)
                {
                    e.Input = InputFrame.None;
                    e.Mover.UnitsPerTickX10 = RogueWanderSpeedX10;
                    long stateStart = tick - b.StateTicks;
                    int span = MeleeRogue.IdleMinTicks + EnemyRng.NextInt(
                        EnemyRng.Hash(e.Id, stateStart, seed),
                        MeleeRogue.IdleMaxTicks - MeleeRogue.IdleMinTicks + 1);
                    if (b.StateTicks < span)
                    {
                        continue;
                    }

                    // Новая точка бродяги (8.1: в 6 тайлах от дома) — шум от (номер, такт, зерно): детерминизм (11.5).
                    ulong h = EnemyRng.Hash(e.Id, tick, seed);
                    Input8 dir = Input8.FromIndex(EnemyRng.NextDir8(h));
                    int dist = 1 + EnemyRng.NextInt(h >> 16, MeleeRogue.WanderRadiusTiles);
                    TileCoord goal = Torus.Wrap(_shape, new TileCoord(home.X + dir.DX * dist, home.Y + dir.DY * dist));
                    if (IsBlocked(_shape, goal))
                    {
                        goal = home; // в стенку не идём — пауза ещё раз
                    }

                    b.GoalX = goal.X;
                    b.GoalY = goal.Y;
                    b.State = EnemyState.Wander;
                    b.StateTicks = 0;
                    e.Mover.UnitsPerTickX10 = RogueWanderSpeedX10;
                    continue;
                }

                // Lost: домой; дом рядом — Idle (8.3).
                e.Mover.UnitsPerTickX10 = RogueHomeSpeedX10;
                if (MoveToward(ref e, home))
                {
                    b.State = EnemyState.Idle;
                    b.StateTicks = 0;
                }
            }
        }

        /// <summary>
        /// Цель такта (8.3 «Агро»): 1) урон ≥ 10 % максимума — обидчик (один раз на удар); 2) зрение 14 тайлов —
        /// ближайший живой персонаж (при равенстве — меньший номер); 3) цель в 28 тайлах («чует») — держится,
        /// вне 28 тайлов 150 тактов — теряется.
        /// </summary>
        private void UpdateTarget(ref SimEntity e, long tick)
        {
            ref EnemyBrain b = ref e.Brain;
            WorldPos at = e.Mover.Position;

            // Агро от урона: обидчик жив, в радиусе потери — цель; засчитывается один раз на удар.
            if (b.LastHitBy >= 0 && b.LastHitTick != 0)
            {
                long by = b.LastHitTick;
                b.LastHitTick = 0;
                if (DamageMath.AtLeastPercent(b.LastHitAmount, e.MaxHealth, MeleeRogue.AggroPct))
                {
                    int s = SlotOf(b.LastHitBy);
                    if (s >= 0 && _entities[s].Alive &&
                        Torus.Dist2(_shape, at, _entities[s].Mover.Position) <= MeleeRogue.Lose2)
                    {
                        if (b.IgnoreTargetId == b.LastHitBy && b.IgnoreUntil != 0)
                        {
                            // Удар по «упёршемуся» (1-013, 9.3): агро сильнее игнора — игнор снимается.
                            b.IgnoreTargetId = EnemyBrain.NoTarget;
                            b.IgnoreUntil = 0;
                        }

                        if (b.TargetId != b.LastHitBy)
                        {
                            b.TargetId = b.LastHitBy;
                            b.PathStamp = 0; // цель сменилась — путь не годится (9.3)
                            b.BlockedSince = 0; // новый эпизод: «упёрся» не переносится (1-013)
                            b.ProgressTick = (int)tick;
                            b.ProgressX = at.X;
                            b.ProgressY = at.Y;
                        }

                        b.LostTicks = 0;
                        return;
                    }
                }
            }

            // Зрение (8.1 «замечает игрока в 12–16 тайлах»): 14 тайлов; ночью ×1,5 → 21 (8.6, 1-009).
            // Игнор цели после сдачи (1-013, 9.3) — цель не берётся, пока срок не истёк.
            if (b.IgnoreUntil != 0 && tick >= b.IgnoreUntil)
            {
                b.IgnoreTargetId = EnemyBrain.NoTarget;
                b.IgnoreUntil = 0;
            }

            int slot = NearestCharacterWithin(at, ScaledSight2(MeleeRogue.Sight2, NightSightMultX1024),
                b.IgnoreUntil != 0 ? b.IgnoreTargetId : EnemyBrain.NoTarget);
            if (slot >= 0)
            {
                b.LostTicks = 0;
                if (b.TargetId != _entities[slot].Id)
                {
                    b.TargetId = _entities[slot].Id;
                    b.PathStamp = 0;
                    b.BlockedSince = 0;
                    b.ProgressTick = (int)tick;
                    b.ProgressX = at.X;
                    b.ProgressY = at.Y;
                }

                return;
            }

            // Цель вне зрения: держится, пока в 28 тайлах (потеря — 150 тактов вне, 8.3).
            if (b.TargetId >= 0)
            {
                int s = SlotOf(b.TargetId);
                if (s < 0 || !_entities[s].Alive)
                {
                    b.TargetId = EnemyBrain.NoTarget;
                    b.PathStamp = 0;
                    b.BlockedSince = 0;
                    b.State = b.State == EnemyState.Chase || b.State == EnemyState.Attack || b.State == EnemyState.Blocked
                        ? EnemyState.Lost : b.State;
                    return;
                }

                if (Torus.Dist2(_shape, at, _entities[s].Mover.Position) <= MeleeRogue.Lose2)
                {
                    b.LostTicks = 0;
                    return;
                }

                b.LostTicks++;
                if (b.LostTicks >= MeleeRogue.LoseTicks)
                {
                    b.TargetId = EnemyBrain.NoTarget;
                    b.LostTicks = 0;
                    b.PathStamp = 0;
                    if (b.State == EnemyState.Chase || b.State == EnemyState.Attack || b.State == EnemyState.Blocked)
                    {
                        b.State = EnemyState.Lost;
                        b.StateTicks = 0;
                    }
                }
            }
        }

    }
}
