using System;
using System.Diagnostics;
using Orpheus.Core.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.Trig;
using Orpheus.Core.World;
using Orpheus.Save;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Карточка 1-007 «Ближний» (docs/02 8.1, 8.3–8.5, 9.3, 11.5): автомат состояний разбойника,
    /// зрение 14 тайлов, агро от урона, преследование (в том числе через шов), цикл удара 33 такта
    /// (10 ударов ≈ 11 с), уворот i-кадрами спасает, выталкивание враг↔враг, детерминизм и хэш мозга,
    /// сохранение формата 6, бюджет такта на 40 врагах.
    /// </summary>
    public class EnemyAiTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat(int tiles, ulong seed) => FlatWorldGenerator.Instance.Generate(MapShape.World(tiles), seed);

        private static WorldPos At(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        private static void Steps(Simulation sim, int n)
        {
            for (int i = 0; i < n; i++)
            {
                sim.Step();
            }
        }

        // -------------------------------------------------------- зрение (8.1)

        /// <summary>Обзор ровно 14 тайлов (8.1 «12–16»): в 14 — замечает (цель + погоня), в 15 — нет (бродит).</summary>
        [Theory]
        [InlineData(12, true)]
        [InlineData(13, true)]
        [InlineData(14, true)]
        [InlineData(15, false)]
        [InlineData(16, false)]
        public void D02_S8_1_Rogue_SightRadius14Tiles(int tiles, bool detects)
        {
            var sim = new Simulation(Flat(1024, 31));
            int rogue = sim.AddMeleeRogue(At(512, 512));
            int ch = sim.AddEntity(At(512 + tiles, 512));
            Steps(sim, 4); // решение ИИ каждые 3 такта с фазой по номеру — 4 хватает на первое

            EnemyBrain b = sim.GetEntity(rogue).Brain;
            if (detects)
            {
                Assert.Equal(ch, b.TargetId);
                Assert.Equal(EnemyState.Chase, b.State);
            }
            else
            {
                Assert.Equal(EnemyBrain.NoTarget, b.TargetId);
                Assert.True(b.State is EnemyState.Idle or EnemyState.Wander, $"состояние {b.State} — вне «бродит»");
            }
        }

        // -------------------------------------------------------- цикл удара (8.1, 6)

        /// <summary>
        /// Приёмка 1-007: «Ближний» убивает стоящего игрока за 10 ударов: урон 10 × 10, период замах-замах
        /// 33 такта — смерть после такта 309 (первый замах на 33, касание на 45, десятый на 342).
        /// </summary>
        [Fact]
        public void D02_S8_2_Rogue_KillsIdleCharacterInTenStrikes()
        {
            var sim = new Simulation(Flat(1024, 41));
            int ch = sim.AddEntity(At(512, 512));
            int rogue = sim.AddMeleeRogue(new WorldPos(512 * U + U / 2 + 300, 512 * U + U / 2)); // в зоне удара (2 т)

            int hits = 0, deathTick = -1, prevHit = 0, gapMin = int.MaxValue, gapMax = 0;
            for (int t = 1; t <= 400 && deathTick < 0; t++)
            {
                sim.Step();
                foreach (SimEvent ev in sim.Events)
                {
                    if (ev.Victim != ch)
                    {
                        continue;
                    }

                    if (ev.Kind == SimEventKind.Damage)
                    {
                        hits++;
                        if (prevHit > 0)
                        {
                            int gap = t - prevHit;
                            gapMin = Math.Min(gapMin, gap);
                            gapMax = Math.Max(gapMax, gap);
                        }

                        prevHit = t;
                    }
                    else if (ev.Kind == SimEventKind.Death)
                    {
                        deathTick = t;
                    }
                }
            }

            Assert.Equal(10, hits);
            Assert.True(deathTick > 0, "игрок не умер");
            Assert.InRange(deathTick, 309, 400); // ≈ 11 с (приёмка «10 ударов за 11 ±1 с»)
            Assert.True(gapMin >= 33, $"удары чаще периода: {gapMin} тактов");
            Assert.True(gapMax <= 60, $"разрыв {gapMax} тактов — враг слишком долго догоняет после отбрасывания");
            Assert.Equal(0, sim.GetEntity(ch).Health);
        }

        /// <summary>Замах ИИ-врага начинается через 33 такта после прошлого (AttackPeriod), замах 12 — телеграф.</summary>
        [Fact]
        public void D02_S8_2_Rogue_Period33_Windup12()
        {
            var sim = new Simulation(Flat(1024, 43));
            int ch = sim.AddEntity(At(512, 512));
            int rogue = sim.AddMeleeRogue(new WorldPos(512 * U + U / 2 + 300, 512 * U + U / 2));

            int firstWindup = -1, secondWindup = -1, prev = 0;
            for (int t = 1; t <= 120 && secondWindup < 0; t++)
            {
                sim.Step();
                int w = sim.GetEntity(rogue).AttackWindup;
                if (w == MeleeRogue.WindupTicks && prev == 0)
                {
                    if (firstWindup < 0)
                    {
                        firstWindup = t;
                    }
                    else
                    {
                        secondWindup = t;
                    }
                }

                prev = w;
            }

            Assert.Equal(MeleeRogue.PeriodTicks, firstWindup);        // период стартует с такта 1
            Assert.Equal(firstWindup + MeleeRogue.PeriodTicks, secondWindup);
            Assert.Equal(EnemyState.Attack, sim.GetEntity(rogue).Brain.State);
        }

        // -------------------------------------------------------- преследование через шов (9.3)

        /// <summary>Погоня через шов тора: разбойник у восточного края (5 т через меридиан) доходит и бьёт.</summary>
        [Fact]
        public void D02_S9_3_Rogue_ChasesAcrossSeam()
        {
            var sim = new Simulation(Flat(1024, 51));
            int ch = sim.AddEntity(At(2, 512));
            int rogue = sim.AddMeleeRogue(At(1021, 512)); // 5 тайлов через шов — в обзоре, вне зоны удара

            bool crossedEdge = false;
            for (int t = 1; t <= 90; t++)
            {
                sim.Step();
                if (sim.GetEntity(rogue).Mover.Position.X > 1023 * U)
                {
                    crossedEdge = true; // шёл на восток и пересёл меридиан
                }
            }

            SimEntity e = sim.GetEntity(rogue);
            Assert.True(crossedEdge, "разбойник не шёл через восточный край");
            Assert.True(e.Mover.Position.X < 3 * U, $"после шва x = {e.Mover.Position.X} — не у западного края");
            long d2 = Torus.Dist2(sim.Shape, e.Mover.Position, sim.GetEntity(ch).Mover.Position);
            Assert.True(d2 <= MeleeRogue.StrikeRange2, $"дистанция {d2} — не догнал (зона {MeleeRogue.StrikeRange2})");
            Assert.Equal(EnemyState.Attack, e.Brain.State);
        }

        // -------------------------------------------------------- уворот (8.13, 0-029 2.2)

        /// <summary>
        /// Игрок читает телеграф (замах 12) и жмёт уворот за 4 такта до касания: i-кадры (12 кадров = 6 тактов,
        /// с кадра нажатия) накрывают удар — из ≥ 100 замахов «Ближнего» спасение ≥ 95.
        /// </summary>
        [Fact]
        public void D02_S8_13_RogueStrikes_Dodged95of100()
        {
            var sim = new Simulation(Flat(1024, 61));
            int ch = sim.AddEntity(At(512, 512));
            int rogue = sim.AddMeleeRogue(new WorldPos(512 * U + U / 2 + 300, 512 * U + U / 2));
            int slot = sim.SlotOf(ch);

            int attacks = 0, hits = 0, prev = 0;
            for (int t = 1; t <= 6_000; t++) // ≥ 100 замахов: цикл 33 + доводка после рывка (~768 ед. на 46/такт)
            {
                if (sim.GetEntity(rogue).AttackWindup == MeleeRogue.WindupTicks - 8) // 4 такта до касания: i-кадры (6 тактов) накрывают
                {
                    // Рывок на север (враг с востока): уворот идёт в направлении ввода кадра нажатия (0-029 2.2);
                    // на следующих тактах ввод гасим — иначе игрок побежит на север быстрее врага и не догонится.
                    sim.SetInputAt(slot, new InputFrame(new Input8(0, -1), InputButtons.Dodge));
                }
                else
                {
                    sim.SetInputAt(slot, InputFrame.None);
                }

                sim.Step();
                foreach (SimEvent ev in sim.Events)
                {
                    if (ev.Victim == ch && ev.Kind == SimEventKind.Damage && ev.Damage > 0)
                    {
                        hits++;
                    }
                }

                int w = sim.GetEntity(rogue).AttackWindup;
                if (w == MeleeRogue.WindupTicks && prev == 0)
                {
                    attacks++;
                }

                prev = w;
            }

            Assert.True(attacks >= 100, $"замахов всего {attacks} за 6 000 тактов");
            Assert.True(attacks - hits >= 95, $"спасено {attacks - hits} из {attacks} — меньше 95 %");
            Assert.True(sim.GetEntity(ch).Health > 0, $"игрок умер: попаданий {hits}");
        }

        // -------------------------------------------------------- агро (8.3)

        /// <summary>Агро 8.3: удар ≥ 10 % максимума меняет цель на обидчика (и обратно — за новым ударом).</summary>
        [Fact]
        public void D02_S8_3_RogueAggro_StrikeSwitchesTarget()
        {
            var sim = new Simulation(Flat(1024, 81));
            int rogue = sim.AddMeleeRogue(At(512, 512));
            int a = sim.AddEntity(At(516, 512)); // оба ровно в 4 тайлах — равные дистанции
            int b = sim.AddEntity(At(512, 516));

            Steps(sim, 4);
            Assert.Equal(a, sim.GetEntity(rogue).Brain.TargetId); // при равенстве — меньший номер (8.3)

            sim.ApplyStrike(rogue, b, 10); // 10 = 10 % от 100
            sim.Step();
            Assert.Equal(b, sim.GetEntity(rogue).Brain.TargetId);

            sim.ApplyStrike(rogue, a, 10);
            sim.Step();
            Assert.Equal(a, sim.GetEntity(rogue).Brain.TargetId);

            sim.ApplyStrike(rogue, b, 9); // 9 < 10 % — агро не переключает
            sim.Step();
            Assert.Equal(a, sim.GetEntity(rogue).Brain.TargetId);
        }

        /// <summary>Потеря (8.3): цель вне 28 тайлов 150 тактов → Lost → домой; у дома — Idle.</summary>
        [Fact]
        public void D02_S8_3_Rogue_LosesTargetAfter150TicksHome()
        {
            var sim = new Simulation(Flat(1024, 83));
            int rogue = sim.AddMeleeRogue(At(512, 512));
            int ch = sim.AddEntity(At(520, 512));
            Steps(sim, 6);
            Assert.Equal(ch, sim.GetEntity(rogue).Brain.TargetId);

            // увозим цель за радиус потери: смена позиции — через Restore (прямого телепорта нет)
            SimEntity[] ents = new SimEntity[sim.EntityCount];
            sim.Entities.CopyTo(ents);
            for (int i = 0; i < ents.Length; i++)
            {
                if (ents[i].Id == ch)
                {
                    ents[i].Mover.Position = At(600, 512); // 88 тайлов > 28
                }
            }

            var sim2 = new Simulation(sim.World);
            sim2.Restore(sim.Tick, ents, sim.NextEntityId);

            EnemyBrain b = sim2.GetEntity(rogue).Brain;
            Assert.Equal(ch, b.TargetId);
            Assert.Equal(0, b.LostTicks);

            Steps(sim2, MeleeRogue.LoseTicks + 3);
            b = sim2.GetEntity(rogue).Brain;
            Assert.Equal(EnemyBrain.NoTarget, b.TargetId);
            Assert.Equal(EnemyState.Lost, b.State);

            Steps(sim2, 300); // домой ~27 тайлов со скоростью 307 (30,7 ед/такт) ≈ 225 тактов
            SimEntity r = sim2.GetEntity(rogue);
            long home2 = Torus.Dist2(sim2.Shape, r.Mover.Position, At(512, 512));
            Assert.True(r.Brain.State is EnemyState.Idle or EnemyState.Wander, $"дома состояние {r.Brain.State} — бродяга не возобновился");
            Assert.True(home2 <= 7L * U * 7 * U, $"у дома нет: {Math.Sqrt(home2) / U:F1} тайлов");
        }

        // -------------------------------------------------------- выталкивание (8.5)

        /// <summary>Враг↔враг (8.5 «толкаются»):spawn внахлёст — разъезжаются до суммы радиусов (204) и не дальше.</summary>
        [Fact]
        public void D02_S8_5_TwoRogues_PushApart()
        {
            var sim = new Simulation(Flat(1024, 91));
            WorldPos at = At(512, 512);
            int r1 = sim.AddMeleeRogue(at);
            int r2 = sim.AddMeleeRogue(new WorldPos(at.X + 20, at.Y));

            Steps(sim, 30);
            long d2 = Torus.Dist2(sim.Shape, sim.GetEntity(r1).Mover.Position, sim.GetEntity(r2).Mover.Position);
            long min = 2 * Collision.RadiusUnits;
            Assert.True(d2 >= min * min, $"центры {Math.Sqrt(d2):F0} ед. — не разъехались ({min})");
            Assert.True(d2 <= (min + 140L) * (min + 140L), $"растолкали дальше {min + 140} ед. — толкаются и после расхождения");
        }

        // -------------------------------------------------------- детерминизм и хэш (11.5)

        [Fact]
        public void D02_S11_5_RoguesAndBots_TwoRunsSameHash()
        {
            ulong a = RogueRun(77);
            ulong b = RogueRun(77);
            Assert.Equal(a, b);
            Assert.NotEqual(a, RogueRun(78)); // другое зерно — другой мир
        }

        /// <summary>Прогон: 10 разбойников кольцом 10–19 тайлов + блуждающий персонаж, 2 000 тактов; хэш каждые 300.</summary>
        private static ulong RogueRun(ulong seed)
        {
            var sim = new Simulation(Flat(1024, seed));
            WorldPos center = At(512, 512);
            int ch = sim.AddEntity(center);
            SimBot bot = SimBot.Create(WanderBot.Name, ch, 0, seed);
            for (int i = 0; i < 10; i++)
            {
                int raw = i * 65536 / 10; // равные углы
                int radius = (10 + i) * U;
                var at = new WorldPos(center.X + Angle16.FromRaw(raw).CosBy(radius), center.Y + Angle16.FromRaw(raw).SinBy(radius));
                sim.AddMeleeRogue(at);
            }

            ulong acc = 0;
            for (int t = 1; t <= 2_000; t++)
            {
                int slot = sim.SlotOf(ch);
                sim.SetInputAt(slot, bot.Think(t, sim.Entities[slot]));
                sim.Step();
                if (t % Simulation.HashInterval == 0)
                {
                    acc ^= StateHash.Compute(sim).Total;
                }
            }

            return acc;
        }

        /// <summary>Бюджет такта (11.5): 40 разбойников (погоня + расталкивание + A* 2/такт) — в среднем ≤ 10 мс/такт.</summary>
        [Fact]
        public void D02_S11_5_TickBudget_40Rogues_Under10ms()
        {
            var sim = new Simulation(Flat(1024, 71), maxEntities: 64);
            WorldPos center = At(512, 512);
            sim.AddEntity(center);
            for (int i = 0; i < 40; i++)
            {
                int raw = i * 65536 / 40;
                int radius = (10 + (i * 7) % 11) * U;
                sim.AddMeleeRogue(new WorldPos(center.X + Angle16.FromRaw(raw).CosBy(radius), center.Y + Angle16.FromRaw(raw).SinBy(radius)));
            }

            Steps(sim, 60); // прогрев: пути, аллокаторы
            var sw = Stopwatch.StartNew();
            for (int t = 1; t <= 900; t++)
            {
                sim.Step();
            }

            sw.Stop();
            double perTick = sw.Elapsed.TotalMilliseconds / 900;
            Assert.True(perTick <= 10.0, $"{perTick:F2} мс/такт на 40 разбойниках — бюджет 10 мс превышен");
        }

        // -------------------------------------------------------- сохранение (формат 6)

        /// <summary>Сейв формата 6: мозги разбойников пишутся и читаются — хэш мира после загрузки совпадает.</summary>
        [Fact]
        public void D07_S6_2_Save6_Rogues_RoundTrip()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, 131, bots: 2);
            WorldPos center = At(512, 512);
            int r1 = live.Sim.AddMeleeRogue(new WorldPos(center.X + 10 * U, center.Y));
            int r2 = live.Sim.AddMeleeRogue(new WorldPos(center.X - 10 * U, center.Y + 3 * U));
            live.Run(300, 1);

            byte[] save = WorldSave.Build(WorldSave.Capture(live.Sim, live.BaseSha, "ИИ"), 6, 0);
            Assert.Equal(6u, SaveContainer.Read(save).Header.FormatVersion);

            Simulation loaded = SaveFixtures.Load(live.BaseBytes, save);
            Assert.Equal(live.Sim.Tick, loaded.Tick);
            Assert.Equal(StateHash.Compute(live.Sim).Total, StateHash.Compute(loaded).Total);

            EnemyBrain a = live.Sim.GetEntity(r1).Brain, a2 = loaded.GetEntity(r1).Brain;
            Assert.Equal(a.Archetype, a2.Archetype);
            Assert.Equal(a.State, a2.State);
            Assert.Equal(a.TargetId, a2.TargetId);
            Assert.Equal(a.GoalX, a2.GoalX);
            Assert.Equal(a.GoalY, a2.GoalY);
            Assert.Equal(live.Sim.GetEntity(r2).Brain.HomeX, loaded.GetEntity(r2).Brain.HomeX);
        }

        // -------------------------------------------------------- мутации мозга в хэше

        /// <summary>Поля мозга в хэше сущности: мутация любого — хэш расходится (минимум три).</summary>
        [Fact]
        public void D02_S11_5_Mutation_BrainFieldsChangeHash()
        {
            var sim = new Simulation(Flat(1024, 141));
            int rogue = sim.AddMeleeRogue(At(512, 512));
            sim.AddEntity(new WorldPos(512 * U + U / 2 + 5 * U, 512 * U + U / 2)); // в обзоре — погоня к такту 200
            Steps(sim, 200);
            Assert.Equal(EnemyState.Attack, sim.GetEntity(rogue).Brain.State); // мозги «живые»

            ulong Run(Action<SimEntity[]> mutate)
            {
                SimEntity[] copy = new SimEntity[sim.EntityCount];
                sim.Entities.CopyTo(copy);
                mutate(copy);
                var s2 = new Simulation(sim.World);
                s2.Restore(sim.Tick, copy, sim.NextEntityId);
                return StateHash.Compute(s2).Total;
            }

            ulong baseHash = Run(_ => { });
            Assert.Equal(baseHash, Run(_ => { })); // копия детерминирована
            Assert.NotEqual(baseHash, Run(a => a[0].Brain.State = EnemyState.Lost));
            Assert.NotEqual(baseHash, Run(a => a[0].Brain.TargetId = 999));
            Assert.NotEqual(baseHash, Run(a => a[0].Brain.GoalX += 1));
            Assert.NotEqual(baseHash, Run(a => a[0].Brain.LastHitAmount = 7));
        }
    }
}
