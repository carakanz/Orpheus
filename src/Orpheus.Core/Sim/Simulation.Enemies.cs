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

            // Сдача (1-013, 9.3): 20 с эпизода «упёрся» без продвижения по пути — цель в игнор на 30 с, враг в Ожидание. C-009: и из
            // погони — враг, которого толкотня раскачивает у косяка, двигается (таймер «застрял» не доходит до Blocked), но не ближе.
            if (b.BlockedSince != 0 && tick - b.BlockedSince >= MeleeRogue.BlockedGiveUpTicks)
            {
                e.Input = InputFrame.None;
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

            if (b.State == EnemyState.Blocked)
            {
                // «Упёрся» (1-013, 9.3): стоит у преграды; пересчёт — сразу при правке мира (ревизия сдвинулась).
                e.Input = InputFrame.None;
                bool decide = (tick + e.Id) % AiDecisionTicks == 0;
                if ((b.EnclosedRev != 0 && _editRev + 1 != b.EnclosedRev) || (decide && BlockedGoalMoved(ref b, target.Mover.Position)))
                {
                    // Тайл в окне поменялся (стена снесена, дверь открыта) — пересчёт сразу, без ожидания (9.3). E2-12b: и цель ушла
                    // дальше тайла от той, к которой искали (игрок вышел из дома, обошёл постройку) — иначе враг стоял до 20 с.
                    if (EnqueueChasePath(ref e, slot, ChaseGoal(target.Mover.Position), tick))
                    {
                        b.EnclosedRev = EnemyBrain.AfterEnclosed; // C-009: найдёт путь — «путь появился», эпизод закрыт
                        b.ProgressTick = (int)tick;
                        b.ProgressX = e.Mover.Position.X;
                        b.ProgressY = e.Mover.Position.Y;
                        b.State = EnemyState.Chase;
                        b.StateTicks = 0;
                    }
                }
                else if (b.EnclosedRev == 0 && decide && b.StateTicks >= AiDecisionTicks)
                {
                    // Blocked без кэша — «застрял» (нет продвижения 2 с при пути) или загрузка сейва: переспрос на ближайшем решении
                    // (E2-12b; было «каждые 2 с» по условию, которое при сдвиге фаз не выполнялось никогда — враг стоял 20 с).
                    // Сдача через 20 с — по BlockedSince, он сбрасывается только продвижением.
                    b.State = EnemyState.Chase;
                    b.StateTicks = 0;
                    b.PathStamp = 0;
                    b.ProgressTick = (int)tick;
                    b.ProgressX = e.Mover.Position.X;
                    b.ProgressY = e.Mover.Position.Y;
                }

                return;
            }

            if (b.State != EnemyState.Chase && b.State != EnemyState.Pounce)
            {
                // Pounce (E3-3): рывок волка идёт по той же трассе, что погоня, но состояние и скорость волка.
                b.State = EnemyState.Chase;
                b.StateTicks = 0;
                b.PathStamp = 0;
                b.ProgressTick = (int)tick;
                b.ProgressX = e.Mover.Position.X;
                b.ProgressY = e.Mover.Position.Y;
            }
            else if (Torus.Dist2(_shape, e.Mover.Position, new WorldPos(b.ProgressX, b.ProgressY)) >= MeleeRogue.ProgressMove2)
            {
                // Продвинулись — контрольная точка за текущей позицией, таймер «застрял» сброшен. Эпизод «упёрся» (BlockedSince)
                // смещение не закрывает (C-009: толкотня раскачивает у косяка — смещается, но путь не укорачивается; враг не
                // сдавался никогда) — закрывает продвижение по пути (точки пути, ниже) и «путь появился».
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
                b.EnclosedRev = 0; // E2-12b: без кэша — переспрос на ближайшем решении (застрял — не значит «пути нет»)
                if (b.BlockedSince == 0)
                {
                    b.BlockedSince = tick;
                }

                e.Input = InputFrame.None;
                return;
            }

            WorldPos goalPoint = ChaseGoal(target.Mover.Position);
            TileCoord goal = Torus.TileOf(_shape, goalPoint);
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
                    EnqueueChasePath(ref e, slot, goalPoint, tick);
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
                    EnqueueChasePath(ref e, slot, goalPoint, tick);
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
                    // Found / WindowEdge / Partial — идём по точкам. Эпизод «упёрся» закрыт (C-009, 9.3 «путь появился →
                    // Преследование»): поиск после «замкнута» нашёл путь до цели — или враг прошёл по пути
                    // EpisodeClosePathPoints точек (остаток пути укоротился; раскачиваемый у косяка точек не проходит).
                    if (b.BlockedSince != 0 &&
                        ((b.EnclosedRev == EnemyBrain.AfterEnclosed && buf.Status == PathStatus.Found) ||
                         b.PathPos >= MeleeRogue.EpisodeClosePathPoints))
                    {
                        b.BlockedSince = 0;
                    }

                    b.EnclosedRev = 0;
                    break;
            }

            // Путь готов (Found — до цели; WindowEdge — до края окна; Partial — предельная длина): по точкам решётки (E2-12b).
            if (FollowPath(ref e, buf))
            {
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
        /// Добавить «Дальнего» с мозгом (лучник, архетип <see cref="EnemyArchetype.Ranged"/>, карточка E3-3):
        /// дом — точка появления, старт в Idle; числа — <see cref="RangedArcher"/>. Возвращает номер.
        /// </summary>
        public int AddRangedArcher(WorldPos position, int healthMultX1024 = 1024)
        {
            int id = AddEnemy(position);
            int slot = SlotOf(id);
            ref SimEntity e = ref _entities[slot];
            short hp = (short)ScaledByNight(RangedArcher.MaxHealth, healthMultX1024); // ночь ×1,25 → 31 (8.6)
            e.MaxHealth = hp;
            e.Health = hp;
            e.AttackRaw = (byte)RangedArcher.ShotDamage; // урон стрелы; ночь масштабируется при выстреле
            e.AttackPeriod = RangedArcher.PeriodTicks;
            e.Mover.UnitsPerTickX10 = RogueWanderSpeedX10;
            TileCoord home = Torus.TileOf(_shape, e.Mover.Position);
            e.Brain = new EnemyBrain
            {
                Archetype = EnemyArchetype.Ranged,
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
        /// Добавить «Зверя» с мозгом (волк, архетип <see cref="EnemyArchetype.Beast"/>, карточка E3-3):
        /// дом — точка появления, старт в Idle; числа — <see cref="BeastWolf"/>. Возвращает номер.
        /// </summary>
        public int AddBeastWolf(WorldPos position, int healthMultX1024 = 1024)
        {
            int id = AddEnemy(position);
            int slot = SlotOf(id);
            ref SimEntity e = ref _entities[slot];
            short hp = (short)ScaledByNight(BeastWolf.MaxHealth, healthMultX1024); // ночь ×1,25 → 26 (8.6)
            e.MaxHealth = hp;
            e.Health = hp;
            e.AttackRaw = (byte)BeastWolf.StrikeDamage; // укус ночью масштабируется в ApplyStrike (8.6)
            e.AttackPeriod = BeastWolf.PeriodTicks;
            e.Mover.UnitsPerTickX10 = RogueWanderSpeedX10;
            TileCoord home = Torus.TileOf(_shape, e.Mover.Position);
            e.Brain = new EnemyBrain
            {
                Archetype = EnemyArchetype.Beast,
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
        /// Шаг ИИ лучника (E3-3): ближе зоны удержания — отход от цели; дальше 12 тайлов — сближение (Chase
        /// крейсерской скоростью); в зоне 8–12 стоит и стреляет раз в <see cref="RangedArcher.PeriodTicks"/>
        /// при чистой линии. Раненый (≤ 50 % HP) держит 10 тайлов вместо 8.
        /// </summary>
        private void StepArcher(ref SimEntity e, ref SimEntity target, int slot, long tick, long dist2)
        {
            ref EnemyBrain b = ref e.Brain;
            bool wounded = e.Health * 100 <= e.MaxHealth * RangedArcher.WoundedPct;
            long keepOut = wounded ? RangedArcher.WoundedKeepOut2 : RangedArcher.KeepOut2;

            if (dist2 <= keepOut)
            {
                e.Mover.UnitsPerTickX10 = RangedArcher.RetreatSpeedX10;
                if (b.State != EnemyState.Retreat)
                {
                    b.State = EnemyState.Retreat;
                    b.StateTicks = 0;
                    b.PathStamp = 0;
                    b.BlockedSince = 0;
                }

                MoveAway(ref e, target.Mover.Position, 6);
                return;
            }

            if (dist2 > RangedArcher.KeepIn2)
            {
                e.Mover.UnitsPerTickX10 = RangedArcher.CruiseSpeedX10;
                Chase(ref e, ref target, slot, tick);
                return;
            }

            // Зона удержания: стоит; выстрел — по таймеру состояния (фаза боя не трогается: цель дальше зоны удара).
            e.Mover.UnitsPerTickX10 = RangedArcher.CruiseSpeedX10;
            if (b.State != EnemyState.Attack)
            {
                b.State = EnemyState.Attack;
                b.StateTicks = 0;
                b.PathStamp = 0;
                b.BlockedSince = 0;
            }

            e.Input = InputFrame.None;
            if (b.StateTicks > 0 && b.StateTicks % RangedArcher.PeriodTicks == 0 &&
                dist2 <= RangedArcher.ShootIn2 &&
                StrikeLineClear(e.Mover.Position, target.Mover.Position,
                    Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position))))
            {
                ushort aim = (ushort)Angle16.FromDirection(Torus.Delta(_shape, e.Mover.Position, target.Mover.Position)).Raw;
                SpawnProjectile(e.Id, hostile: true, Combat.Projectiles.KindArrow, e.Mover.Position, aim,
                    RangedArcher.ArrowSpeedX256, RangedArcher.ArrowRangeUnits,
                    ScaledByNight(RangedArcher.ShotDamage, NightMultX1024), Items.Arrows);
            }
        }

        /// <summary>
        /// Шаг ИИ волка (E3-3): ≤ 4 HP — отход на 4 тайла (упёрся за 60 тактов — принимает бой); цель дальше
        /// 6 тайлов — рывок на <see cref="BeastWolf.PounceTicks"/>; укус в упор (зона/линия как у ближнего).
        /// Цикл замаха ведёт фаза боя по состоянию Attack (общий, как у ближнего).
        /// </summary>
        private void StepWolf(ref SimEntity e, ref SimEntity target, int slot, long tick, long dist2)
        {
            ref EnemyBrain b = ref e.Brain;
            TileCoord own = Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position));

            if (e.Health <= BeastWolf.FleeHp && b.State != EnemyState.Attack)
            {
                if (b.State == EnemyState.Retreat)
                {
                    if (b.StateTicks >= BeastWolf.BlockedFightTicks)
                    {
                        // Долго убегает и не оторвался (упёрся) — принимает смерть укусом (8.2).
                        b.State = EnemyState.Chase;
                        b.StateTicks = 0;
                        b.PathStamp = 0;
                        b.BlockedSince = 0;
                    }
                    else if (dist2 > BeastWolf.FleeGoal2)
                    {
                        // Оторвался на 4 тайла — готов кусаться: погоня подведёт ближе.
                        b.State = EnemyState.Chase;
                        b.StateTicks = 0;
                        b.PathStamp = 0;
                    }
                    else
                    {
                        e.Mover.UnitsPerTickX10 = BeastWolf.FleeSpeedX10;
                        MoveAway(ref e, target.Mover.Position, 8);
                        return;
                    }
                }
                else
                {
                    // Малый HP — начинаем отход.
                    e.Mover.UnitsPerTickX10 = BeastWolf.FleeSpeedX10;
                    b.State = EnemyState.Retreat;
                    b.StateTicks = 0;
                    b.PathStamp = 0;
                    b.BlockedSince = 0;
                    MoveAway(ref e, target.Mover.Position, 8);
                    return;
                }
            }

            if (dist2 <= BeastWolf.StrikeRange2 &&
                (InsideSolid(e.Mover.Position) || StrikeLineClear(e.Mover.Position, target.Mover.Position, own)))
            {
                e.Mover.UnitsPerTickX10 = BeastWolf.ChaseSpeedX10;
                if (b.State != EnemyState.Attack)
                {
                    b.State = EnemyState.Attack;
                    b.StateTicks = 0;
                    b.PathStamp = 0;
                    b.BlockedSince = 0;
                }

                e.Input = InputFrame.None;
                return;
            }

            if (b.State == EnemyState.Pounce)
            {
                if (b.StateTicks < BeastWolf.PounceTicks)
                {
                    Chase(ref e, ref target, slot, tick); // трасса как в погоне (Chase свои поля не сбит: Pounce защищён)
                    e.Mover.UnitsPerTickX10 = BeastWolf.PounceSpeedX10; // скорость рывка — поверх Chase
                    return;
                }

                b.State = EnemyState.Chase;
                b.StateTicks = 1; // не «свежая» погоня: следующий рывок только после кулдауна
                b.PathStamp = 0;
            }

            if (dist2 > BeastWolf.PounceFrom2 &&
                (b.State != EnemyState.Chase || b.StateTicks == 0 || b.StateTicks >= BeastWolf.PounceCooldownTicks))
            {
                // Рывок с 6 тайлов (8.2): бросок на PounceTicks; дальше перерыв — далеко убегающего не догоняет.
                // Встреча издалека (первый такт погони) — рывок сразу; повторный — только с перерывом.
                b.State = EnemyState.Pounce;
                b.StateTicks = 0;
                b.PathStamp = 0;
                Chase(ref e, ref target, slot, tick);
                e.Mover.UnitsPerTickX10 = BeastWolf.PounceSpeedX10;
                return;
            }

            e.Mover.UnitsPerTickX10 = BeastWolf.ChaseSpeedX10;
            Chase(ref e, ref target, slot, tick);
        }

        /// <summary>
        /// Отход (E3-3): шаг от цели — прямо к тайлу в <paramref name="tiles"/> тайлах за спиной вдоль линии
        /// «цель → враг» (целочисленно, детерминизм 11.5). Путь к цели — обычный <see cref="MoveToward"/>.
        /// </summary>
        private void MoveAway(ref SimEntity e, WorldPos from, int tiles)
        {
            TileCoord st = Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position));
            Int2 d = Torus.Delta(_shape, from, e.Mover.Position);
            int m = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
            if (m == 0)
            {
                m = 1; // враг в центре цели — отход на восток
                d = new Int2(1, 0);
            }

            var goal = new TileCoord(st.X + (int)((long)tiles * d.X / m), st.Y + (int)((long)tiles * d.Y / m));
            MoveToward(ref e, Torus.Wrap(_shape, goal));
        }

        /// <summary>
        /// Лут с врага при смерти (E3-3b, `06` 1.6) — личная капля: дроп только когда убил игрок
        /// (<see cref="EnemyBrain.LastHitBy"/> — персонаж), владелец кучи = убийца. Зверь (волк): мясо 1–2 (100 %),
        /// шкура 1 (70 %). Дальний (лучник): стрелы 2–5 (50 %). Ближний (разбойник): дропа нет (у людей по `06` 1.6
        /// только оболы — валюта, не предмет среза). Шум от (номер, такт, зерно) — детерминизм 11.5.
        /// </summary>
        private void SpawnEnemyDrop(ref SimEntity e)
        {
            int owner = e.Brain.LastHitBy;
            int slot = SlotOf(owner);
            if (slot < 0 || !_entities[slot].IsCharacter)
            {
                return; // E3-3b: убило не игроком (деспавн, среда) — не дропаем
            }

            ulong h = EnemyRng.Hash(e.Id, Tick, _world.Seed);
            TileCoord tile = Torus.TileOf(_shape, e.Mover.Position);
            if (e.Brain.Archetype == EnemyArchetype.Beast)
            {
                DropOnTile(Tick, tile, Items.Meat, 1 + (int)(h % 2), owner); // мясо 1–2 (100 %)
                if ((int)((h >> 8) % 100) < 70)
                {
                    DropOnTile(Tick, tile, Items.Hide, 1, owner); // шкура (70 %)
                }
            }
            else if (e.Brain.Archetype == EnemyArchetype.Ranged && (int)((h >> 8) % 100) < 50)
            {
                DropOnTile(Tick, tile, Items.Arrows, 2 + (int)(h % 4), owner); // стрелы 2–5 (50 %)
            }
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

                    // Архетипы E3-3: лучник держит дистанцию и стреляет; волк — рывок, укус, отход при 4 HP.
                    if (b.Archetype == EnemyArchetype.Ranged)
                    {
                        StepArcher(ref e, ref target, i, tick, dist2);
                        continue;
                    }

                    if (b.Archetype == EnemyArchetype.Beast)
                    {
                        StepWolf(ref e, ref target, i, tick, dist2);
                        continue;
                    }

                    // В атаку — только при чистой линии удара (E2-12, `02` 9.3): сквозь стену 0,5 игрок у внутренней грани в 1,3 т —
                    // в зоне удара, но за стеной; стоять «в атаке» у стены нельзя — погоня, «упёрся» и уход через 20 с. Забор прозрачен.
                    // Враг в закрытой двери (закрыли под ним правкой без выталкивания, C-001) или центром в твёрдой ячейке стоит, как
                    // раньше: погоня вывела бы его из двери на сторону цели. Замах гасит линия удара. Свободная половина тайла стены — нет.
                    TileCoord own = Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position));
                    if (dist2 <= MeleeRogue.StrikeRange2 &&
                        (InsideSolid(e.Mover.Position) || StrikeLineClear(e.Mover.Position, target.Mover.Position, own)))
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
                    if (IsBlocked(_shape, goal) || TerrainSlow.FloorQ8(_world.GetFloor(goal)) != TerrainSlow.One)
                    {
                        goal = home; // в стенку и в воду (C-018) не идём — пауза ещё раз
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
