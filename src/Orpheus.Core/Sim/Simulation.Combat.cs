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
        /// Начать замах врага на персонажа (1-001, `02` 8.1): сектор фиксируется началом замаха
        /// (позиция врага + направление на цель). Враг жив, замахa нет, цель жива — иначе false.
        /// </summary>
        public bool StartAttack(int attackerId, int targetId)
        {
            int a = SlotOf(attackerId);
            int t = SlotOf(targetId);
            if (a < 0 || t < 0)
            {
                return false;
            }

            ref SimEntity e = ref _entities[a];
            if (e.Kind != SimEntity.KindEnemy || !e.Alive || e.AttackWindup > 0)
            {
                return false;
            }

            ref SimEntity target = ref _entities[t];
            if (!target.Alive)
            {
                return false;
            }

            BeginWindup(ref e, target);
            return true;
        }

        /// <summary>
        /// Удар решён («попал», конвейер 0-029/0-033; 1-001, шаг 3): здоровье цели минусуется по формуле 6.1,
        /// неуязвимость 0,4 с (только персонаж, `02` 6.3), регенерация обнуляется, «в бою» — 8 с обеим сторонам,
        /// отбрасывание от <paramref name="knockOrigin"/> (`02` 6.5; живой цели, смерть — без него),
        /// прерывание замаха врага (≥ 15 % максимума цели, отход 0,5 с, иммунитет 1,5 с, `02` 6.6),
        /// смерть (`02` 8.3 / 13.5: персонаж — 5 с до возрождения, враг — труп 315 тактов).
        /// Нет цели / труп / персонаж в неуязвимости / урон 0 — возврат 0 без изменений.
        /// Зовётся и в такте (атака врага), и вне такта (решение сервером угрозы в <c>Decide</c>, 0-033).
        /// События «урон» и «смерть» — в буфер такта (0 нет: вне такта, сервер читает по своему счёту).
        /// Возвращает урон после брони.
        /// </summary>
        public int ApplyStrike(int targetId, int attackerId, int rawDamage, WorldPos knockOrigin = default, bool knockback = false, byte damageKind = 0)
        {
            int slot = SlotOf(targetId);
            if (slot < 0)
            {
                return 0;
            }

            ref SimEntity target = ref _entities[slot];
            if (!target.Alive)
            {
                return 0;
            }

            if (target.IsCharacter && target.InvulnTicks > 0)
            {
                return 0; // неуязвимость уворота — на уровне Mover.Dodge (проверяет решалка 0-033), здесь — после удара/снаряда
            }

            int damage = DamageMath.Apply(rawDamage, target.Armor);
            if (damage <= 0)
            {
                return 0;
            }

            target.Health = (short)(target.Health - damage);
            if (target.IsCharacter)
            {
                target.InvulnTicks = InvulnStrikeTicks; // 0,4 с (`02` 6.3; у врагов неуязвимости нет)
            }

            target.RegenTicks = 0;
            target.RegenCarryX100 = 0;
            target.CombatTicks = CombatFlagTicks;

            int attackerSlot = SlotOf(attackerId);
            if (attackerSlot >= 0)
            {
                _entities[attackerSlot].CombatTicks = CombatFlagTicks; // «в бою» и за нанесённый урон (6.3)

                // Прерывание (6.6): цель — враг в замахе, урон ≥ 15 % максимума цели, иммунитет не выставлен.
                if (target.Kind == SimEntity.KindEnemy && target.AttackWindup > 0 && target.AttackImmune == 0 &&
                    DamageMath.AtLeastPercent(damage, target.MaxHealth, InterruptPctEnemy))
                {
                    target.AttackWindup = 0;
                    target.AttackRecover = InterruptRecoverTicks;
                    target.AttackImmune = InterruptImmuneTicks;
                    target.AttackTarget = -1;
                }

                // Персонажа прерывает удар ≥ 10 % его максимума (6.6, хук 1-004): замах меча гаснет, серия сбрасывается.
                if (target.IsCharacter && target.Mover.Melee.Phase == MeleeAction.PhaseWindup &&
                    DamageMath.AtLeastPercent(damage, target.MaxHealth, InterruptPctCharacter))
                {
                    target.Mover.Melee.Cancel();
                }

                // Агро (1-007, 8.3 «получил урон — сменяет цель»): мозг запоминает обидчика и урон;
                // UpdateTarget засчитывает запись один раз (сбрасывает LastHitTick) на следующем решении ИИ.
                if (target.Kind == SimEntity.KindEnemy && target.Brain.Archetype != EnemyArchetype.None)
                {
                    target.Brain.LastHitTick = Tick + 1;
                    target.Brain.LastHitBy = attackerId;
                    target.Brain.LastHitAmount = damage;
                }
            }

            AddEvent(new SimEvent(SimEventKind.Damage, Tick, default, -1, EditResult.Applied, 0,
                requester: attackerId, victim: target.Id, damage: damage, damageType: damageKind));

            if (target.Health <= 0)
            {
                target.Health = 0;
                target.DeadTicks = target.IsCharacter ? RespawnDelayTicks : CorpseTicks;
                target.AttackWindup = 0;
                target.AttackRecover = 0;
                target.AttackImmune = 0;
                target.AttackTarget = -1;
                target.Input = InputFrame.None;
                AddEvent(new SimEvent(SimEventKind.Death, Tick, default, -1, EditResult.Applied, 0,
                    requester: attackerId, victim: target.Id, damage: damage, damageType: damageKind));
                return damage;
            }

            if (knockback)
            {
                // 6.5: рывок от источника на база ÷ масса; живая цель (смерть — падение без рывка);
                // стена останавливает без урона (Mover.StepFrame); ввод цели в рывке не действует.
                target.Mover.StartKnock(_shape, knockOrigin, target.Mass, Input8.East);
            }

            return damage;
        }

        private void BeginWindup(ref SimEntity attacker, in SimEntity target)
        {
            attacker.AttackWindup = WindupTicks;
            attacker.AttackOrigin = attacker.Mover.Position;
            attacker.AttackFacingRaw = (short)(int)Angle16.FromDirection(Torus.Delta(_shape, attacker.Mover.Position, target.Mover.Position)).Raw;
            attacker.AttackTarget = target.Id;
        }

        /// <summary>
        /// Цикл манекена (приёмка 1-001: «Ближний» бьёт по таймеру): замах через <paramref name="periodTicks"/>
        /// тактов после конца отхода, цель — ближайший живой персонаж; 0 / −1 — выключить. <paramref name="rawDamage"/> — сырой урон.
        /// </summary>
        public bool EnableAttackCycle(int enemyId, int periodTicks, int rawDamage = StrikeRawMelee)
        {
            int slot = SlotOf(enemyId);
            if (slot < 0 || _entities[slot].Kind != SimEntity.KindEnemy || periodTicks < -1)
            {
                return false;
            }

            _entities[slot].AttackPeriod = periodTicks;
            if (rawDamage > 0)
            {
                _entities[slot].AttackRaw = (byte)Math.Min(255, rawDamage);
            }

            return true;
        }

        /// <summary>
        /// Удар мечом игрока (1-004, `02` 3.1–3.2): в фазе «удар» — сектор от начала удара
        /// (<see cref="MeleeAction.OriginX"/>, фиксирован переходом «замах → удар»), ось — прицел
        /// (<see cref="MeleeAction.AimRaw"/>), радиус <see cref="SwordStrike.ReachUnits"/>, дуга 120°.
        /// Цель — ближайший живой враг, чей круг пересекает сектор и до которого линия удара чиста
        /// (стена/скала/дерево/закрытая дверь — преграда, забор — нет, `02` 3.4); в PvE players не задевает.
        /// Урон 10/10/13 по индексу серии, отбрасывание 1/1/2 тайла ÷ масса; удар решается один раз
        /// (<see cref="MeleeAction.Resolved"/>); постройки урона не получают (цели — только существа).
        /// </summary>
        private void ResolveMeleeStrike(ref SimEntity e)
        {
            ref MeleeAction m = ref e.Mover.Melee;
            if (m.Phase != MeleeAction.PhaseStrike || m.Resolved)
            {
                return;
            }

            m.Resolved = true;
            WorldPos origin = new WorldPos(m.OriginX, m.OriginY);
            var sector = new MeleeSector(origin, Angle16.FromRaw(m.AimRaw), SwordStrike.ReachUnits, SwordStrike.HalfArcSteps);
            TileCoord own = Torus.TileOf(_shape, e.Mover.Position);

            int target = -1;
            long bestD = long.MaxValue;
            for (int i = 0; i < _entityCount; i++)
            {
                ref SimEntity t = ref _entities[i];
                if (t.Kind != SimEntity.KindEnemy || !t.Alive)
                {
                    continue; // PvE: меч бьёт врагов, игроков не задевает (`02` 3.4)
                }

                if (!sector.Overlaps(_shape, t.Mover.Position, Collision.RadiusUnits))
                {
                    continue;
                }

                Int2 d = Torus.Delta(_shape, origin, t.Mover.Position);
                long dist = d.LengthSquared;
                if (dist >= bestD)
                {
                    continue;
                }

                if (!StrikeLineClear(origin, t.Mover.Position, own))
                {
                    continue; // стена/забор-преграда между — удара нет (`02` 3.4)
                }

                bestD = dist;
                target = i;
            }

            if (target < 0)
            {
                return;
            }

            int damage = ApplyStrike(_entities[target].Id, e.Id, SwordStrike.DamageFor(m.Index), origin, knockback: true);
            if (damage > 0)
            {
                // Третий удар отбрасывает на 2 тайла (`02` 3.2): скорость ставит ApplyStrike (база ÷ масса), множитель удара — здесь.
                ref Mover tm = ref _entities[target].Mover;
                if (tm.KnockLeft != 0)
                {
                    tm.KnockSpeedX10 *= SwordStrike.KnockTilesFor(m.Index);
                }
            }
        }

        /// <summary>
        /// Фаза боя такта (после движения): таймеры (неуязвимость, «в бою», регенерация), цикл атаки врага
        /// (замах фиксирован началом: сектор замирает — попадание по позиции цели в конце замаха), смерть и
        /// возрождение. Порядок — по номеру сущности; только целые, без выделений (повтор журнала, 11.5).
        /// </summary>
        private void StepCombat(long tick)
        {
            for (int i = 0; i < _entityCount; i++)
            {
                ref SimEntity e = ref _entities[i];

                if (e.DeadTicks > 0)
                {
                    e.DeadTicks--;
                    if (e.DeadTicks == 0 && e.IsCharacter)
                    {
                        Respawn(ref e, tick);
                    }

                    continue;
                }

                if (e.Health <= 0)
                {
                    continue; // труп врага в такт удаления
                }

                if (e.InvulnTicks > 0)
                {
                    e.InvulnTicks--;
                }

                if (e.CombatTicks > 0)
                {
                    e.CombatTicks--;
                }

                if (e.RegenTicks < RegenDelayTicks)
                {
                    e.RegenTicks++;
                }
                else if (e.Health < e.MaxHealth)
                {
                    e.RegenCarryX100 += e.MaxHealth;
                    if (e.RegenCarryX100 >= RegenCarryPerHealth)
                    {
                        int heal = e.RegenCarryX100 / RegenCarryPerHealth;
                        e.RegenCarryX100 -= heal * RegenCarryPerHealth;
                        e.Health = (short)Math.Min(e.MaxHealth, e.Health + heal);
                    }
                }

                if (e.Kind != SimEntity.KindEnemy)
                {
                    ResolveMeleeStrike(ref e); // удар мечом игрока решается по текущим позициям в фазе «удар» (1-004)
                    continue; // персонаж сам не замахивается на врагов кодом врага (атаки игрока — 1-004…1-006)
                }

                if (e.AttackImmune > 0)
                {
                    e.AttackImmune--;
                }

                if (e.AttackWindup > 0)
                {
                    if (e.Brain.Archetype != EnemyArchetype.None && e.AttackPeriod > 0)
                    {
                        e.AttackPeriod--; // ИИ (1-007): период считается и в замахе — замах-замах ровно PeriodTicks
                    }

                    e.AttackWindup--;
                    if (e.AttackWindup == 0)
                    {
                        var sector = new MeleeSector(e.AttackOrigin, Angle16.FromRaw(e.AttackFacingRaw),
                            EnemyMelee.ReachUnits, EnemyMelee.HalfArcSteps);
                        int slot = SlotOf(e.AttackTarget);
                        // Удар решается по позиции цели в конце замаха (docs/07, 4.6); уворот спасает i-кадрами (0-029, 2.2).
                        if (slot >= 0 && _entities[slot].Alive && !_entities[slot].Mover.Dodge.Invulnerable &&
                            sector.Overlaps(_shape, _entities[slot].Mover.Position, Collision.RadiusUnits))
                        {
                            // Урон ночного врага ×1,25 (8.6, 1-009): 10 → 13; днём ровно ×1.
                            ApplyStrike(_entities[slot].Id, e.Id, ScaledByNight(e.AttackRaw, NightMultX1024), e.Mover.Position, knockback: true);
                        }

                        e.AttackRecover = RecoverTicks;
                        e.AttackTarget = -1;
                    }

                    continue;
                }

                if (e.AttackRecover > 0)
                {
                    e.AttackRecover--;
                }

                if (e.Brain.Archetype != EnemyArchetype.None)
                {
                    // Враг с ИИ (1-007): цикл удара ведёт мозг — в состоянии Attack замах каждые PeriodTicks (1,1 с)
                    // от прошлого замаха; цель жива и в зоне — иначе цикл ждёт (мозг подведёт ближе).
                    if (e.AttackPeriod > 0)
                    {
                        e.AttackPeriod--;
                    }

                    if (e.Brain.State == EnemyState.Attack && e.AttackPeriod == 0 && e.AttackRecover == 0)
                    {
                        int slot = SlotOf(e.Brain.TargetId);
                        if (slot >= 0 && _entities[slot].Alive &&
                            Torus.Dist2(_shape, e.Mover.Position, _entities[slot].Mover.Position) <= MeleeRogue.StrikeRange2 &&
                            // Линия удара (1-013, 9.3): стена/скала/дерево/закрытая дверь между врагом и целью —
                            // не бьёт; забор прозрачен (бьёт через забор).
                            StrikeLineClear(e.Mover.Position, _entities[slot].Mover.Position,
                                Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position))))
                        {
                            BeginWindup(ref e, _entities[slot]);
                            e.AttackPeriod = MeleeRogue.PeriodTicks;
                        }
                    }

                    continue;
                }

                if (e.AttackPeriod > 0 && e.AttackRecover == 0)
                {
                    int target = NearestCharacterSlot(e.Mover.Position, EnemyMelee.ReachUnits + Collision.RadiusUnits);
                    if (target >= 0 && StrikeLineClear(e.Mover.Position, _entities[target].Mover.Position,
                        Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position))))
                    {
                        BeginWindup(ref e, _entities[target]);
                    }
                }
            }
        }

        /// <summary>
        /// Тайл прерывает линию удара (`02` 3.4): как <see cref="IsBlocked"/> для ходьбы, но
        /// деревянный забор — преграда для ходьбы, для удара нет (забор бьётся, приёмка 1-004).
        /// </summary>
        private bool StrikeTileBlocked(TileCoord tile)
        {
            if (IsBlocked(_shape, tile))
            {
                Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_shape, tile));
                if (chunk != null &&
                    ObjectId.IdOf(chunk.GetObject(Chunk.IndexOf(tile.X, tile.Y))) == ObjectId.WoodFence)
                {
                    return false; // забор — линия удара проходит (`02` 3.1:157)
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Линия удара чиста (`02` 3.1, 3.4): отрезок «начало удара — центр цели» с шагом
        /// <see cref="StrikeLosStepUnits"/> unit не пересекает непроходимых тайлов (<see cref="IsBlocked"/>:
        /// стена, скала, дерево, гранит, закрытая дверь/ворота); тайл атакующего пропускается,
        /// только если сам проходим (открытая дверь, забор), — стоит врагу на ЗАКРЫТУЮ дверь (закрылась
        /// под ним, C-001), она перекрывает линию: цель за дверью не под ударом; забор проходим —
        /// линию не прерывает. Шов тора: разность — <see cref="Torus.Delta"/>.
        /// </summary>
        private bool StrikeLineClear(WorldPos from, WorldPos to, TileCoord own)
        {
            Int2 d = Torus.Delta(_shape, from, to);
            int steps = (Math.Abs(d.X) + Math.Abs(d.Y) + StrikeLosStepUnits - 1) / StrikeLosStepUnits;
            for (int i = 1; i < steps; i++)
            {
                WorldPos p = new WorldPos(
                    from.X + (int)((long)d.X * i / steps),
                    from.Y + (int)((long)d.Y * i / steps));
                TileCoord tile = Torus.TileOf(_shape, p);
                if (tile == own && !StrikeTileBlocked(own))
                {
                    continue;
                }

                if (StrikeTileBlocked(tile))
                {
                    return false;
                }
            }

            return true;
        }

    }
}
