using System;
using System.Collections.Generic;
using System.Text;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Save;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Карточка E3-3 «Лучник и волк» (docs/02 8.2, 8.5, 8.6): лучник держит 8–12 тайлов, стреляет
    /// раз в 2 с (урон 8, враждебная стрела), раненый отходит дальше; волк догоняет рывком (быстрее
    /// погони), кусает (7), при 4 HP отходит и, убегая 60 тактов, принимает бой; лут с смерти
    /// (стрелы/еда); архетип в хэше и детерминизм; спавн по полу (лес — стая волков, луг — лучник/разбойник).
    /// Мутации: ветка архетипа / отход лучника / рывок волка — тесты краснеют.
    /// </summary>
    public class E33ArcherWolfTests
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

        private static long Dist2(Simulation sim, int a, int b) =>
            Torus.Dist2(sim.Shape, sim.GetEntity(a).Mover.Position, sim.GetEntity(b).Mover.Position);

        /// <summary>Пересборка симы из её же сущностей (прямых сеттеров позиции/HP нет — только Restore).</summary>
        private static Simulation Rebake(Simulation sim, Action<SimEntity[]>? mutate = null)
        {
            SimEntity[] ents = new SimEntity[sim.EntityCount];
            sim.Entities.CopyTo(ents);
            mutate?.Invoke(ents);
            var s2 = new Simulation(sim.World)
            {
                DayScale = sim.DayScale,
                SpawnEnabled = sim.SpawnEnabled,
                SpawnCursor = sim.SpawnCursor,
                SpawnNextTry = sim.SpawnNextTry,
            };
            s2.Restore(sim.Tick, ents, sim.NextEntityId, sim.WorldTime);
            return s2;
        }

        /// <summary>Поле сущности id в массиве для пересборки: SimEntity — структура, правим копией и пишем обратно.</summary>
        private static void Patch(SimEntity[] a, int id, Func<SimEntity, SimEntity> set)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].Id == id)
                {
                    a[i] = set(a[i]);
                }
            }
        }

        /// <summary>Здоровье персонажа p — 30 000 (танк: удары врагов не убивают тестовую цель).</summary>
        private static Simulation Tank(Simulation sim, int p) => Rebake(sim, a => Patch(a, p, e =>
        {
            e.MaxHealth = 30_000;
            e.Health = 30_000;
            return e;
        }));

        /// <summary>Игроку p на N тактов возвращается позиция pos (откноут не уносит цель из зоны правил).</summary>
        private static Simulation StepPinned(Simulation sim, int ticks, int p, WorldPos pos)
        {
            for (int t = 0; t < ticks; t++)
            {
                sim.Step();
                sim = Rebake(sim, a => Patch(a, p, e =>
                {
                    e.Mover.Position = pos;
                    return e;
                }));
            }

            return sim;
        }

        private static void Wall(Simulation sim, int x, int y) =>
            sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, ObjectId.WoodWall, BuildRecord.OfPlayer(1, FloorId.Grass));

        /// <summary>Глухая комната 3 × 3 вокруг тайла.</summary>
        private static void Room(Simulation sim, int x, int y)
        {
            Wall(sim, x - 1, y - 1); Wall(sim, x, y - 1); Wall(sim, x + 1, y - 1);
            Wall(sim, x - 1, y); Wall(sim, x + 1, y);
            Wall(sim, x - 1, y + 1); Wall(sim, x, y + 1); Wall(sim, x + 1, y + 1);
        }

        // ==================================================== лучник (8.2)

        /// <summary>Цель в 5 тайлах: лучник отходит (Retreat) и держит ≥ 8 тайлов; мутация «убрать отход» красит тест.</summary>
        [Fact]
        public void E33_Archer_Keeps_Distance_Retreats()
        {
            var sim = new Simulation(Flat(1024, 3301));
            int ch = sim.AddEntity(At(512, 512));
            int archer = sim.AddRangedArcher(At(512 + 5, 512));
            sim = Tank(sim, ch);

            sim = StepPinned(sim, 240, ch, At(512, 512));

            long d2 = Dist2(sim, archer, ch);
            Assert.True(d2 >= RangedArcher.KeepOut2,
                $"лучник в {Math.Sqrt(d2) / U} тайлах от цели — не отступил за 8");
            EnemyState st = sim.GetEntity(archer).Brain.State;
            Assert.True(st == EnemyState.Retreat || st == EnemyState.Attack,
                $"лучник в состоянии {st} — ни отхода, ни стрельбы из зоны");
        }

        /// <summary>Цель в 10 тайлах: стоит в зоне удержания, стреляет раз в ~60 тактов; стрела враждебная, урон 8, каждая доходит.</summary>
        [Fact]
        public void E33_Archer_Holds_Zone_And_Shoots()
        {
            var sim = new Simulation(Flat(1024, 3302));
            int ch = sim.AddEntity(At(512, 512));
            int archer = sim.AddRangedArcher(At(512 + 10, 512));
            sim = Tank(sim, ch);

            int shots = 0, hits = 0, shotDamage = 0;
            int startHp = sim.GetEntity(ch).Health;
            for (int t = 0; t < 600; t++)
            {
                sim.Step();
                foreach (Projectile pr in sim.ShotsThisTick)
                {
                    if (pr.Owner == archer)
                    {
                        Assert.True(pr.Hostile, "стрела лучника не враждебная");
                        Assert.Equal(Projectiles.KindArrow, pr.Kind);
                        shots++;
                        shotDamage = pr.Damage;
                    }
                }

                foreach (SimEvent ev in sim.Events)
                {
                    if (ev.Kind == SimEventKind.Damage && ev.Victim == ch)
                    {
                        hits++;
                    }
                }
            }

            Assert.InRange(shots, 8, 12); // ~10 за 10 с; мутация «нет выстрелов» → 0
            Assert.Equal(RangedArcher.ShotDamage, shotDamage);
            // Большинство стрел доходит (цель стоит); отскок от попаданий сносит героя — пара стрел мимо, это физика.
            Assert.True(hits * 4 >= shots * 3, $"попаданий {hits} из {shots} — стрелы почти не попадают");
            Assert.Equal(startHp - hits * RangedArcher.ShotDamage, sim.GetEntity(ch).Health);

            long d2 = Dist2(sim, archer, ch);
            // Зона 8–12 тайлов; герой сдвигается отскоками стрел — допуск ±1 тайл на снос цели.
            Assert.True(d2 >= RangedArcher.KeepOut2 && d2 <= 13L * U * 13 * U,
                $"лучник в {Math.Sqrt(d2) / U} тайлах — вышел из зоны удержания 8–12");
        }

        /// <summary>Цель в 13 тайлах: лучник сближается (крейсерская не быстрее героя).</summary>
        [Fact]
        public void E33_Archer_Closes_From_Far()
        {
            var sim = new Simulation(Flat(1024, 3303));
            int ch = sim.AddEntity(At(512, 512));
            int archer = sim.AddRangedArcher(At(512 + 13, 512));

            Steps(sim, 120);

            long d2 = Dist2(sim, archer, ch);
            Assert.True(d2 < RangedArcher.KeepIn2, "лучник не сблизился с 13 тайлов за 4 с");
            Assert.True(sim.GetEntity(archer).Mover.UnitsPerTickX10 <= 512, "крейсерская скорость лучника быстрее героя");
        }

        /// <summary>Раненый (≤ 50 % HP) держит 10 тайлов: у цели в 9 тайлах отходит за 10.</summary>
        [Fact]
        public void E33_Archer_Wounded_Keeps_Further()
        {
            var sim = new Simulation(Flat(1024, 3304));
            int ch = sim.AddEntity(At(512, 512));
            int archer = sim.AddRangedArcher(At(512 + 9, 512));
            sim = Rebake(sim, a =>
            {
                Patch(a, ch, e =>
                {
                    e.MaxHealth = 30_000;
                    e.Health = 30_000;
                    return e;
                });
                Patch(a, archer, e =>
                {
                    e.Health = 12; // ≤ 50 % от 25
                    return e;
                });
            });

            sim = StepPinned(sim, 240, ch, At(512, 512));

            long d2 = Dist2(sim, archer, ch);
            Assert.True(d2 > RangedArcher.WoundedKeepOut2,
                $"раненый лучник в {Math.Sqrt(d2) / U} тайлах — не отошёл за 10");
        }

        // ==================================================== волк (8.2)

        /// <summary>Волк в 12 тайлах от цели: рывок (Pounce) со скоростью выше погони; дистанция падает.</summary>
        [Fact]
        public void E33_Wolf_Pounce_And_Closes()
        {
            var sim = new Simulation(Flat(1024, 3305));
            int ch = sim.AddEntity(At(512, 512));
            int wolf = sim.AddBeastWolf(At(512 + 12, 512));

            bool sawPounce = false;
            int pounceSpeed = 0;
            for (int t = 0; t < 300 && !sawPounce; t++)
            {
                sim.Step();
                if (sim.GetEntity(wolf).Brain.State == EnemyState.Pounce)
                {
                    sawPounce = true;
                    pounceSpeed = sim.GetEntity(wolf).Mover.UnitsPerTickX10;
                }
            }

            Assert.True(sawPounce, "волк не перешёл в рывок (Pounce) за 5 с — мутация «убрать рывок»");
            // Скорость рывка заметно выше погони разбойника (460) — мутация «рывок = погоня» красит тест.
            Assert.True(pounceSpeed > MeleeRogue.ChaseSpeedX10 + 100,
                $"скорость рывка {pounceSpeed} — не быстрее погони ({MeleeRogue.ChaseSpeedX10})");

            Steps(sim, 200);
            long d2 = Dist2(sim, wolf, ch);
            Assert.True(d2 < 8L * 8 * U * U, $"дистанция после 8 с: {Math.Sqrt(d2) / U} тайлов — волк не догоняет");
        }

        /// <summary>Волк в упоре кусает: урон укуса 7, состояние Attack.</summary>
        [Fact]
        public void E33_Wolf_Bites_In_Melee()
        {
            var sim = new Simulation(Flat(1024, 3306));
            int ch = sim.AddEntity(At(512, 512));
            int wolf = sim.AddBeastWolf(new WorldPos(512 * U + U / 2 + 300, 512 * U + U / 2)); // в зоне укуса
            sim = Tank(sim, ch);

            int hits = 0, lastDamage = 0;
            int startHp = sim.GetEntity(ch).Health;
            for (int t = 0; t < 400; t++)
            {
                sim.Step();
                foreach (SimEvent ev in sim.Events)
                {
                    if (ev.Kind == SimEventKind.Damage && ev.Victim == ch)
                    {
                        hits++;
                        lastDamage = ev.Damage;
                    }
                }
            }

            Assert.True(hits >= 5, $"укусов за 13 с: {hits}");
            Assert.Equal(BeastWolf.StrikeDamage, lastDamage);
            Assert.Equal(startHp - hits * BeastWolf.StrikeDamage, sim.GetEntity(ch).Health);
            Assert.Equal(EnemyState.Attack, sim.GetEntity(wolf).Brain.State);
        }

        /// <summary>Волк при 4 HP отходит (Retreat, быстрее героя); убегая 60 тактов — принимает бой (укус).</summary>
        [Fact]
        public void E33_Wolf_Flees_At_Low_Hp_Then_Fights()
        {
            var sim = new Simulation(Flat(1024, 3307));
            int ch = sim.AddEntity(At(512, 512));
            int wolf = sim.AddBeastWolf(At(512 + 2, 512));
            sim = Rebake(sim, a =>
            {
                Patch(a, ch, e =>
                {
                    e.MaxHealth = 30_000;
                    e.Health = 30_000;
                    return e;
                });
                Patch(a, wolf, e =>
                {
                    e.Health = BeastWolf.FleeHp;
                    return e;
                });
            });

            // Первые такты — волк в отходе; к 4 с оторвался минимум на тайл (от 2 тайлов к цели).
            sim = StepPinned(sim, 5, ch, At(512, 512));
            Assert.Equal(EnemyState.Retreat, sim.GetEntity(wolf).Brain.State);

            sim = StepPinned(sim, 115, ch, At(512, 512));
            long d2 = Dist2(sim, wolf, ch);
            Assert.True(d2 >= 3L * 3 * U * U, $"волк отшёл только на {Math.Sqrt(d2) / U} тайлов за 4 с");

            // 60 тактов бегства — волк принимает бой; к такту ~400 (укус через догон) урон по цели.
            int hits = 0;
            for (int t = 0; t < 400; t++)
            {
                sim.Step();
                foreach (SimEvent ev in sim.Events)
                {
                    if (ev.Kind == SimEventKind.Damage && ev.Victim == ch)
                    {
                        hits++;
                    }
                }

                sim = Rebake(sim, a => Patch(a, ch, e =>
                {
                    e.Mover.Position = At(512, 512);
                    e.MaxHealth = 30_000;
                    e.Health = 30_000;
                    return e;
                }));
            }

            Assert.True(hits > 0, "отступающий волк не принял бой за 60+ тактов (укусов нет)");
        }

        // ==================================================== лут с смерти (8.2, 8.6)

        /// <summary>Смерть ближнего (разбойник) рукой игрока: дропа нет (у людей по `06` 1.6 только оболы —
        /// валюта, не предмет среза); смерть лучника рукой игрока: стрелы 2–5 в 50 % случаев, личная капля
        /// (Owner = убийца). Убит не игроком (самоудар) — дропа нет (E3-3b).</summary>
        [Fact]
        public void E33_Death_Drops()
        {
            // 30 разных id в одной симе — шум (id, такт, зерно) разный: оценка доли со съедобным лутом.
            // Тайлы через 4 — кучи не сливаются (PileMergeUnits ≈ тайл).
            var sim = new Simulation(Flat(1024, 3308));
            int ch = sim.AddEntity(At(400, 400)); // E3-3b: дропаем только когда убил персонаж
            int[] rogues = new int[30];
            for (int k = 0; k < 30; k++)
            {
                rogues[k] = sim.AddMeleeRogue(At(500 + 4 * k, 500));
            }

            foreach (int rogue in rogues)
            {
                Assert.True(sim.ApplyStrike(rogue, ch, 999, sim.GetEntity(rogue).Mover.Position) > 0);
            }

            Assert.Empty(sim.Piles.ToArray()); // E3-3b: с людей дропа нет

            // Лучник: стрелы 2–5 в 50 % случаев (10 разных id).
            var sim2 = new Simulation(Flat(1024, 3309));
            int ch2 = sim2.AddEntity(At(400, 404));
            int[] archers = new int[10];
            for (int k = 0; k < 10; k++)
            {
                archers[k] = sim2.AddRangedArcher(At(500 + 4 * k, 504));
            }

            foreach (int archer in archers)
            {
                sim2.ApplyStrike(archer, ch2, 999, sim2.GetEntity(archer).Mover.Position);
            }

            int arrows = 0;
            foreach (GroundPile p in sim2.Piles.ToArray())
            {
                Assert.Equal(Items.Arrows, p.Item); // только стрелы
                Assert.Equal(ch2, p.Owner); // личная капля — убийце
                Assert.InRange(p.Count, 2, 5);
                arrows++;
            }

            Assert.InRange(arrows, 2, 8); // ~5 из 10 при 50 %; мутация «лута нет» → 0

            // Убит не игроком (самоудар врага) — дропа нет (E3-3b).
            var sim3 = new Simulation(Flat(1024, 3311));
            int wolf3 = sim3.AddBeastWolf(At(500, 500));
            sim3.ApplyStrike(wolf3, wolf3, 999, sim3.GetEntity(wolf3).Mover.Position);
            Assert.Empty(sim3.Piles.ToArray());
        }

        // ==================================================== хэш и детерминизм

        /// <summary>Архетип в хэше сущности: мутация поля — хэш расходится.</summary>
        [Fact]
        public void E33_Archetype_In_StateHash()
        {
            var sim = new Simulation(Flat(1024, 3310));
            int archer = sim.AddRangedArcher(At(512, 512));
            sim.AddBeastWolf(At(514, 512));
            Steps(sim, 60);

            ulong Run(Action<SimEntity[]> mutate)
            {
                var s2 = Rebake(sim, mutate);
                return StateHash.Compute(s2).Total;
            }

            ulong baseHash = Run(_ => { });
            Assert.Equal(baseHash, Run(_ => { }));
            Assert.NotEqual(baseHash, Run(a => Patch(a, archer, e =>
            {
                e.Brain.Archetype = EnemyArchetype.Melee;
                return e;
            })));
        }

        /// <summary>Детерминизм 11.5: герой + лучник + волк, 2 прогона по 600 тактов — хэш совпадает.</summary>
        [Fact]
        public void E33_Determinism_600_Ticks()
        {
            ulong Run()
            {
                var sim = new Simulation(Flat(1024, 3311));
                sim.AddEntity(At(512, 512));
                sim.AddRangedArcher(At(512 + 10, 512));
                sim.AddBeastWolf(At(512 - 9, 512));
                Steps(sim, 600);
                return StateHash.Compute(sim).Total;
            }

            Assert.Equal(Run(), Run());
        }

        // ==================================================== спавн по полу (8.5)

        private static string SpawnSignature(Simulation sim)
        {
            var sb = new StringBuilder();
            foreach (SimEntity e in sim.Entities)
            {
                if (e.Kind == SimEntity.KindEnemy && e.Brain.Archetype != EnemyArchetype.None && e.Health > 0)
                {
                    sb.Append(e.Id).Append(':').Append(e.Brain.Archetype).Append(',');
                }
            }

            return sb.ToString();
        }

        /// <summary>Одна ночь спавна; seed — мир, forest — кольцо 30–50 т лесом. Подпись: id:архетип через запятую.</summary>
        private static string NightSpawn(ulong seed, bool forest)
        {
            var world = Flat(1024, seed);
            if (forest)
            {
                for (int x = 512 - 50; x <= 512 + 50; x++)
                {
                    for (int y = 512 - 50; y <= 512 + 50; y++)
                    {
                        var t = new TileCoord(x, y);
                        if (Torus.Dist2(world.Shape, Torus.CornerOf(t), At(512, 512)) >= 30L * 30 * U)
                        {
                            world.SetBaseTile(t, FloorId.ForestFloor, ObjectId.Empty);
                        }
                    }
                }
            }

            var sim = new Simulation(world) { SpawnEnabled = true };
            sim.SetWorldTime(DayClock.NightStartTick);
            Room(sim, 512, 512);
            int p = sim.AddEntity(At(512, 512));
            sim = Tank(sim, p);
            return SpawnSignature(StepPinned(sim, 3600, p, At(512, 512)));
        }

        /// <summary>Ночь на лугу: спавнятся только лучник и разбойник (35 %/65 %), волков нет; состав детерминирован.</summary>
        [Fact]
        public void E33_Spawn_Grass_No_Wolves_Deterministic()
        {
            string s1 = NightSpawn(1301, forest: false);
            Assert.Equal(s1, NightSpawn(1301, forest: false)); // детерминизм выбора архетипа

            Assert.DoesNotContain(":" + EnemyArchetype.Beast + ",", s1);
            Assert.Contains(":" + EnemyArchetype.Ranged + ",", s1); // 35 % за ~30 попыток — обязан быть
            Assert.Contains(":" + EnemyArchetype.Melee + ",", s1);
        }

        /// <summary>Ночь в лесу: спавнятся волки стаей (≥ 2).</summary>
        [Fact]
        public void E33_Spawn_Forest_Wolf_Pack()
        {
            string s = NightSpawn(1301, forest: true);
            int beasts = 0;
            foreach (string part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.EndsWith(":" + EnemyArchetype.Beast))
                {
                    beasts++;
                }
            }

            Assert.True(beasts >= 2, $"волков в лесу за ночь: {beasts} — стая не спавнится");
        }

        // ==================================================== сохранение (формат 6)

        /// <summary>
        /// Круг сейва с новыми состояниями (E3-3): лучник в Retreat (8) и волк в Pounce (9) — запись читается,
        /// хэш совпадает, состояния круг проходят целыми, и загруженный мир идёт дальше так же.
        /// Граница чтения BrainState «Интерес = 7» (до E3-3) на этом тесте красная: чтение 8/9 бросает InvalidDataException.
        /// </summary>
        [Fact]
        public void E33_Save_Retreat_Pounce_RoundTrip()
        {
            WorldModel world = Flat(1024, 3313);
            var sim = new Simulation(world);
            int ch = sim.AddEntity(At(512, 512));
            int archer = sim.AddRangedArcher(At(512 + 5, 512));    // цель в 5 тайлах → отход
            int wolf = sim.AddBeastWolf(At(512 + 10, 512 + 3));    // цель в ~10 тайлах → рывок
            sim = Tank(sim, ch);

            // Лучник уходит сразу; волк входит в рывок, как только замечает цель издалека.
            int pounceAt = -1;
            for (int t = 0; t < 60 && pounceAt < 0; t++)
            {
                sim.Step();
                if (sim.GetEntity(wolf).Brain.State == EnemyState.Pounce)
                {
                    pounceAt = t;
                }
            }

            Assert.Equal(EnemyState.Retreat, sim.GetEntity(archer).Brain.State);
            Assert.True(pounceAt >= 0, "волк не сделал рывок за 60 тактов с 10 тайлов");

            byte[] baseBytes = WorldSnapshot.WriteBase(world);
            byte[] save = WorldSave.Build(WorldSave.Capture(sim, WorldSnapshot.StoredHash(baseBytes), "E3-3 круг"), 3, 0);
            SaveFile f = SaveContainer.Read(save);
            Assert.True(f.IsOk, f.Error);
            Simulation loaded = SaveFixtures.Load(baseBytes, save);

            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total);
            Assert.Equal(EnemyState.Retreat, loaded.GetEntity(archer).Brain.State);
            Assert.Equal(EnemyState.Pounce, loaded.GetEntity(wolf).Brain.State);

            for (int t = 0; t < 120; t++)
            {
                sim.Step();
                loaded.Step();
            }

            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total);
        }

        /// <summary>
        /// Круг сейва с летящей стрелой (E3-3, формат 15): лучник выстрелил, стрела в полёте — запись, чтение,
        /// хэш совпадает (снаряды в <c>StateHash</c>), состав полёта цел, и загруженный мир летит и стреляет дальше
        /// так же. До формата 15 летящие не сохранялись: хэш загруженного мира расходился с исходным (падение D00).
        /// </summary>
        [Fact]
        public void E33_Save_Projectile_InFlight_RoundTrip()
        {
            WorldModel world = Flat(1024, 3314);
            var sim = new Simulation(world);
            int ch = sim.AddEntity(At(512, 512));
            int archer = sim.AddRangedArcher(At(512 + 10, 512));   // цель в 10 тайлах — зона удержания, выстрел
            sim = Tank(sim, ch);                                     // танк один раз: дальше без Rebake (он гасит полёт)

            int shotAt = -1;
            for (int t = 0; t < 240 && shotAt < 0; t++)
            {
                sim.Step();
                if (sim.Projectiles.Length > 0)
                {
                    shotAt = t;
                }
            }

            Assert.True(shotAt >= 0, "лучник не выстрелил за 240 тактов с 10 тайлов");
            Assert.Single(sim.Projectiles.ToArray());
            Assert.True(sim.NextProjectileId > 1);

            WorldSaveData cap = WorldSave.Capture(sim, WorldSnapshot.StoredHash(WorldSnapshot.WriteBase(world)), "E3-3 полёт");
            Assert.Single(cap.Projectiles);
            Assert.Equal(sim.NextProjectileId, cap.NextProjectileId);
            byte[] save = WorldSave.Build(cap, 3, 0);
            SaveFile f = SaveContainer.Read(save);
            Assert.True(f.IsOk, f.Error);
            Simulation loaded = SaveFixtures.Load(WorldSnapshot.WriteBase(world), save);

            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total);
            Assert.Equal(sim.NextProjectileId, loaded.NextProjectileId);
            Assert.Equal(sim.Projectiles.Length, loaded.Projectiles.Length);
            for (int i = 0; i < sim.Projectiles.Length; i++)
            {
                Projectile a = sim.Projectiles[i], b = loaded.Projectiles[i];
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Owner, b.Owner);
                Assert.Equal(a.Hostile, b.Hostile);
                Assert.Equal(a.Kind, b.Kind);
                Assert.Equal(a.Damage, b.Damage);
                Assert.Equal(a.DropItem, b.DropItem);
                Assert.Equal(a.Strong, b.Strong);
                Assert.Equal(a.SpawnTick, b.SpawnTick);
                Assert.Equal(a.Origin.X, b.Origin.X);
                Assert.Equal(a.Origin.Y, b.Origin.Y);
                Assert.Equal(a.AimRaw, b.AimRaw);
                Assert.Equal(a.SpeedX256, b.SpeedX256);
                Assert.Equal(a.RangeUnits, b.RangeUnits);
                Assert.Equal(a.Age, b.Age);
            }

            for (int t = 0; t < 120; t++)
            {
                sim.Step();
                loaded.Step();
            }

            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total);
        }
    }
}
