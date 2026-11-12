using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Save;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Карточка 1-009 «появление и исчезновение врагов, ночные коэффициенты» (docs/02 8.5, 8.6, 8.8;
    /// docs/01 6 «Свет и появление врагов»): лимиты и ритм спавнера (день 5 / ночь 8, группа ×1,5 на
    /// соседа), кольцо 36–46 т и запреты (пол игрока, свет ≥ 0,5, замкнутые области, 16 т от старта),
    /// «Интерес» 20 с, ночные множители (здоровье/удар ×1,25, зрение ×1,5), исчезновение (30 с дальше 80 т,
    /// чанк вне активной зоны, цель прерывает серию).
    /// </summary>
    public class SpawnTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const int Tiles = 1024; // карта теста

        private static WorldModel Flat(ulong seed) => FlatWorldGenerator.Instance.Generate(MapShape.World(Tiles), seed);

        private static WorldPos At(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        private static void Steps(Simulation sim, int n)
        {
            for (int i = 0; i < n; i++)
            {
                sim.Step();
            }
        }

        private static void Wall(Simulation sim, int x, int y) =>
            sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, ObjectId.WoodWall, BuildRecord.OfPlayer(1, FloorId.Grass));

        private static void Floor(Simulation sim, int x, int y) =>
            sim.RequestEdit(new TileCoord(x, y), FloorId.WoodFloor, ObjectId.Empty, BuildRecord.OfPlayer(1, FloorId.Grass));

        private static void Torch(Simulation sim, int x, int y) =>
            sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, ObjectId.Torch);

        /// <summary>Глухая комната 3 × 3 (стены вокруг тайла): тестовый игрок в безопасности — удары врагов
        /// с откноутом не «уносят» его от точки, относительно которой считаются кольца 8.5 и 8.8.</summary>
        private static void Room(Simulation sim, int x, int y)
        {
            Wall(sim, x - 1, y - 1); Wall(sim, x, y - 1); Wall(sim, x + 1, y - 1);
            Wall(sim, x - 1, y); Wall(sim, x + 1, y);
            Wall(sim, x - 1, y + 1); Wall(sim, x, y + 1); Wall(sim, x + 1, y + 1);
        }

        /// <summary>
        /// «Танк»: игрокам 30 000 HP через Restore — спавненные враги не убивают тестового игрока за прогон
        /// (смерть → возрождение в центр карты ломает зоны, окрестности и дистанции правил 8.5/8.8).
        /// </summary>
        private static Simulation Tank(Simulation sim, params int[] players)
        {
            SimEntity[] ents = new SimEntity[sim.EntityCount];
            sim.Entities.CopyTo(ents);
            for (int i = 0; i < ents.Length; i++)
            {
                foreach (int id in players)
                {
                    if (ents[i].Id == id)
                    {
                        ents[i].Health = 30_000;
                        ents[i].MaxHealth = 30_000;
                    }
                }
            }

            var sim2 = new Simulation(sim.World) { SpawnEnabled = sim.SpawnEnabled, DayScale = sim.DayScale, SpawnCursor = sim.SpawnCursor, SpawnNextTry = sim.SpawnNextTry };
            sim2.Restore(sim.Tick, ents, sim.NextEntityId, sim.WorldTime);
            return sim2;
        }

        /// <summary>
        /// Шаги с пином: после каждого такта игрокам <paramref name="ids"/> возвращается позиция <paramref name="pos"/>.
        /// Откноут ударов «уносит» игрока (телепорт позиции), а кольца 8.5 и счёт 8.8 считаются от неё — без пина
        /// лимит окрестности перелетается, когда стая уводит игрока на десятки тайлов.
        /// </summary>
        private static Simulation StepsPinned(Simulation sim, int ticks, int[] ids, WorldPos[] pos)
        {
            for (int t = 0; t < ticks; t++)
            {
                sim.Step();
                SimEntity[] ents = new SimEntity[sim.EntityCount];
                sim.Entities.CopyTo(ents);
                for (int i = 0; i < ents.Length; i++)
                {
                    for (int k = 0; k < ids.Length; k++)
                    {
                        if (ents[i].Id == ids[k])
                        {
                            ents[i].Mover.Position = pos[k];
                        }
                    }
                }

                var sim2 = new Simulation(sim.World) { SpawnEnabled = sim.SpawnEnabled, DayScale = sim.DayScale, SpawnCursor = sim.SpawnCursor, SpawnNextTry = sim.SpawnNextTry };
                sim2.Restore(sim.Tick, ents, sim.NextEntityId, sim.WorldTime);
                sim = sim2;
            }

            return sim;
        }

        /// <summary>Номера живых врагов с мозгом (спавнер считает именно их).</summary>
        private static HashSet<int> EnemyIds(Simulation sim)
        {
            var s = new HashSet<int>();
            var ents = sim.Entities;
            for (int i = 0; i < ents.Length; i++)
            {
                if (ents[i].Kind == SimEntity.KindEnemy && ents[i].Brain.Archetype != EnemyArchetype.None &&
                    ents[i].Health > 0)
                {
                    s.Add(ents[i].Id);
                }
            }

            return s;
        }

        /// <summary>Квадрат расстояния в тайлах от врага до тайла (тор).</summary>
        private static long Dist2Tiles(Simulation sim, int id, TileCoord t)
        {
            var e = sim.Entities[sim.SlotOf(id)];
            return Torus.Dist2(sim.World.Shape, Torus.TileOf(sim.World.Shape, e.Mover.Position), t);
        }

        private static int CountNear(Simulation sim, WorldPos at, int tiles)
        {
            long r2 = (long)tiles * tiles;
            int n = 0;
            foreach (int id in EnemyIds(sim))
            {
                var e = sim.Entities[sim.SlotOf(id)];
                if (Torus.Dist2(sim.World.Shape, Torus.TileOf(sim.World.Shape, e.Mover.Position),
                        Torus.TileOf(sim.World.Shape, at)) <= r2)
                {
                    n++;
                }
            }

            return n;
        }

        // ==================================================== 8.5 «Появление»: лимиты и ритм

        /// <summary>День, одинокий игрок на открытом поле: за 2 минуты спавнер поднимает окрестность до лимита 5.</summary>
        [Fact]
        public void S09_S8_5_Day_Limit5_In2Minutes()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            Room(sim, 512, 512);
            int p = sim.AddEntity(At(512, 512));
            sim = Tank(sim, p);
            sim = StepsPinned(sim, 3600, new[] { p }, new[] { At(512, 512) }); // 2 минуты

            int n = EnemyIds(sim).Count;
            Assert.InRange(n, 5, 9); // лимит 5 с дрейфом блуждания ±6 т; без лимита было бы 15 попыток
        }

        /// <summary>Ночь: лимит 8 на игрока; за ночь спавнер доходит до 8 (без группы).</summary>
        [Fact]
        public void S09_S8_5_Night_Limit8()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(DayClock.NightStartTick);
            Room(sim, 512, 512);
            int p = sim.AddEntity(At(512, 512));
            sim = Tank(sim, p);
            sim = StepsPinned(sim, 3600, new[] { p }, new[] { At(512, 512) }); // ночь

            int n = EnemyIds(sim).Count;
            Assert.InRange(n, 8, 14); // лимит 8 с дрейфом; без лимита — 30 попыток
        }

        /// <summary>Двое в 40 т (окрестности пересекаются) ночью: лимит группы ceil(8 × 1,5) = 12.</summary>
        [Fact]
        public void S09_S8_5_Duo_Night_Group12()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(DayClock.NightStartTick);
            Room(sim, 512, 512);
            Room(sim, 552, 512);
            int a = sim.AddEntity(At(512, 512));
            int b = sim.AddEntity(At(552, 512));
            sim = Tank(sim, a, b);
            sim = StepsPinned(sim, 3600, new[] { a, b }, new[] { At(512, 512), At(552, 512) });

            Assert.True(EnemyIds(sim).Count >= 12,
                $"двое ночью: {EnemyIds(sim).Count} врагов, лимит группы 12");
        }

        /// <summary>Двое в 500 т (окрестности не пересекаются) ночью: по 8 на каждого, суммарно 16.</summary>
        [Fact]
        public void S09_S8_5_Duo_Far_Each8()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(DayClock.NightStartTick);
            Room(sim, 100, 100);
            Room(sim, 600, 100);
            int pa = sim.AddEntity(At(100, 100));
            int pb = sim.AddEntity(At(600, 100));
            sim = Tank(sim, pa, pb);
            sim = StepsPinned(sim, 3600, new[] { pa, pb }, new[] { At(100, 100), At(600, 100) });

            int a = CountNear(sim, At(100, 100), 56);
            int b = CountNear(sim, At(600, 100), 56);
            Assert.InRange(a, 6, 10);
            Assert.InRange(b, 6, 10);
            Assert.Equal(a + b, EnemyIds(sim).Count); // окрестности не смешиваются
        }

        /// <summary>Каждое появление — не ближе 36 т к каждому из игроков (кольцо по тору, не к «своему»).</summary>
        [Fact]
        public void S09_S8_5_Spawn_AtLeast36_FromEveryPlayer()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(DayClock.NightStartTick);
            var p1 = new TileCoord(512, 512);
            var p2 = new TileCoord(572, 512); // 60 т: кольцо P1 залезает в 14 т от P2 — проверка на обоих
            Room(sim, 512, 512);
            Room(sim, 572, 512);
            int pa = sim.AddEntity(At(512, 512));
            int pb = sim.AddEntity(At(572, 512));
            sim = Tank(sim, pa, pb);

            var before = EnemyIds(sim);
            int spawns = 0;
            for (int t = 0; t < 3600; t++)
            {
                sim = StepsPinned(sim, 1, new[] { pa, pb }, new[] { At(512, 512), At(572, 512) });
                var now = EnemyIds(sim);
                foreach (int id in now)
                {
                    if (before.Contains(id))
                    {
                        continue;
                    }

                    spawns++;
                    Assert.True(Dist2Tiles(sim, id, p1) >= 36L * 36,
                        $"появление в {Dist2Tiles(sim, id, p1)} т² от P1 (&lt; 36 т)");
                    Assert.True(Dist2Tiles(sim, id, p2) >= 36L * 36,
                        $"появление в {Dist2Tiles(sim, id, p2)} т² от P2 (&lt; 36 т)");
                }

                before = now;
            }

            Assert.True(spawns >= 8, $"за ночь спавнов {spawns} — спавнер молчит");
        }

        /// <summary>«Не на построенном полу»: кольцо 36–46 т застроено полом игрока — за день ни одного спавна.</summary>
        [Fact]
        public void S09_S8_5_NoSpawn_OnBuiltFloor()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.AddEntity(At(512, 512));
            for (int x = 460; x <= 564; x++)
            {
                for (int y = 460; y <= 564; y++)
                {
                    long d2 = (x - 512L) * (x - 512) + (y - 512L) * (y - 512);
                    if (d2 >= 35L * 35 && d2 <= 47L * 47)
                    {
                        Floor(sim, x, y);
                    }
                }
            }

            Steps(sim, 3600); // правки применены в первых тактах; далее — весь день попытки
            Assert.Empty(EnemyIds(sim));
        }

        /// <summary>«Свет ≥ 0,5 запрещает появление»: кольцо игроков в факельной сетке — за три ночи ни одного.</summary>
        [Fact]
        public void S09_S8_5_NoSpawn_InTorchBand_3Nights()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(DayClock.NightStartTick);
            sim.AddEntity(At(512, 512));
            sim.AddEntity(At(544, 512));
            for (int x = 466; x <= 590; x++)
            {
                for (int y = 466; y <= 558; y++)
                {
                    if (x % 4 == 0 && y % 4 == 0)
                    {
                        Torch(sim, x, y); // сетка шаг 4: факел r=6 покрывает каждый тайл колец ≥ 0,5 (1083/1024)
                    }
                }
            }

            Steps(sim, 3 * 3600);
            Assert.Empty(EnemyIds(sim));
        }

        /// <summary>Открытое поле без света ночью: спавнер работает — за ночь не меньше десяти на двоих.</summary>
        [Fact]
        public void S09_S8_5_OpenField_Duo_Night_AtLeast10()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(DayClock.NightStartTick);
            Room(sim, 512, 512);
            Room(sim, 552, 512);
            int pa = sim.AddEntity(At(512, 512));
            int pb = sim.AddEntity(At(552, 512));
            sim = Tank(sim, pa, pb);
            sim = StepsPinned(sim, 3600, new[] { pa, pb }, new[] { At(512, 512), At(552, 512) });
            Assert.True(EnemyIds(sim).Count >= 10, $"за ночь на открытом поле {EnemyIds(sim).Count} спавнов");
        }

        /// <summary>
        /// Предикат тайла: замкнутая стенами комната (заливка 16 &lt; 256) — нельзя; открытое поле — можно;
        /// комната 20 × 20 (заливка упирается в лимит 256) — можно (правило про крупные постройки).
        /// </summary>
        [Fact]
        public void S09_S8_5_EnclosedRoom_Predicate()
        {
            var sim = new Simulation(Flat(1301));
            for (int x = 300; x <= 305; x++)
            {
                for (int y = 300; y <= 305; y++)
                {
                    if (x is 300 or 305 || y is 300 or 305)
                    {
                        Wall(sim, x, y);
                    }
                }
            }

            for (int x = 700; x <= 719; x++)
            {
                for (int y = 700; y <= 719; y++)
                {
                    if (x is 700 or 719 || y is 700 or 719)
                    {
                        Wall(sim, x, y);
                    }
                }
            }

            sim.Step(); // правки применены
            Assert.False(sim.IsSpawnTileOk(new TileCoord(302, 302))); // комната 6 × 6: заливка 16 — замкнуто
            Assert.True(sim.IsSpawnTileOk(new TileCoord(600, 600))); // открытое поле
            Assert.True(sim.IsSpawnTileOk(new TileCoord(701, 701))); // комната 20 × 20: заливка 324 &gt; 256 — открыто
        }

        // ==================================================== 8.6 «Сутки»: ночные коэффициенты

        /// <summary>Множители: ночь hp/удар ×1,25 (35→44, 10→13), зрение ×1,5; день — ровно ×1.</summary>
        [Fact]
        public void S09_S8_6_Night_Multipliers()
        {
            var sim = new Simulation(Flat(1301)) { DayScale = 0 };
            sim.SetWorldTime(0);
            Assert.Equal(1024, sim.NightMultX1024);
            Assert.Equal(1024, sim.NightSightMultX1024);

            sim.SetWorldTime(DayClock.NightStartTick);
            Assert.Equal(1280, sim.NightMultX1024);
            Assert.Equal(1536, sim.NightSightMultX1024);
            Assert.Equal(44, Simulation.ScaledByNight(35, sim.NightMultX1024));
            Assert.Equal(13, Simulation.ScaledByNight(10, sim.NightMultX1024));

            int day = sim.AddMeleeRogue(At(100, 100));
            Assert.Equal(35, sim.GetEntity(day).MaxHealth); // день — базовое
            int night = sim.AddMeleeRogue(At(100, 110), sim.NightMultX1024);
            Assert.Equal(44, sim.GetEntity(night).MaxHealth);
            Assert.Equal(44, sim.GetEntity(night).Health);
        }

        /// <summary>Ночной удар: 10 → 13 урона (8.6 «сила удара ×1,25»).</summary>
        [Fact]
        public void S09_S8_6_Night_Strike_Is13()
        {
            var sim = new Simulation(Flat(1301)) { DayScale = 0 };
            sim.SetWorldTime(DayClock.NightStartTick);
            int ch = sim.AddEntity(At(512, 512));
            sim.AddMeleeRogue(At(514, 512), sim.NightMultX1024);
            for (int t = 0; t < 300 && sim.GetEntity(ch).Health == 100; t++)
            {
                sim.Step();
            }

            Assert.Equal(87, sim.GetEntity(ch).Health); // первый удар 13, не 10
        }

        /// <summary>Ночное зрение ×1,5: в 20 т замечает, в 24 т — нет; днём в 20 т — нет (база 14 т).</summary>
        [Fact]
        public void S09_S8_6_Night_Sight_21Tiles()
        {
            var sim = new Simulation(Flat(1301)) { DayScale = 0 };
            sim.SetWorldTime(DayClock.NightStartTick);
            int ch = sim.AddEntity(At(532, 512)); // 20 т
            int rogue = sim.AddMeleeRogue(At(512, 512));
            Steps(sim, 6);
            Assert.Equal(ch, sim.GetEntity(rogue).Brain.TargetId); // ночью 20 т видно (радиус 21)

            var day = new Simulation(Flat(1301)) { DayScale = 0 };
            int ch2 = day.AddEntity(At(532, 512));
            int rogue2 = day.AddMeleeRogue(At(512, 512));
            Steps(day, 60);
            Assert.Equal(EnemyBrain.NoTarget, day.GetEntity(rogue2).Brain.TargetId); // днём 20 т &gt; 14 т

            var sim3 = new Simulation(Flat(1301)) { DayScale = 0 };
            sim3.SetWorldTime(DayClock.NightStartTick);
            sim3.AddEntity(At(536, 512)); // 24 т
            int rogue3 = sim3.AddMeleeRogue(At(512, 512));
            Steps(sim3, 60);
            Assert.Equal(EnemyBrain.NoTarget, sim3.GetEntity(rogue3).Brain.TargetId); // 24 т &gt; 21 т
        }

        /// <summary>«Интерес» (8.5): спавн — состояние Interest с причиной; через 20 с — Ожидание.</summary>
        [Fact]
        public void S09_S8_5_Spawn_Interest_20s_ThenIdle()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(DayClock.NightStartTick);
            Room(sim, 512, 512);
            int p = sim.AddEntity(At(512, 512));
            sim = Tank(sim, p);

            int spawned = -1;
            var before = EnemyIds(sim);
            for (int t = 0; t < 300 && spawned < 0; t++)
            {
                sim.Step();
                var now = EnemyIds(sim);
                foreach (int id in now)
                {
                    if (!before.Contains(id))
                    {
                        spawned = id;
                        break;
                    }
                }

                before = now;
            }

            Assert.True(spawned >= 0, "за 10 тактов ночи спавна нет");
            EnemyBrain b = sim.GetEntity(spawned).Brain;
            Assert.Equal(EnemyState.Interest, b.State);
            Assert.Equal(p, b.CauseId);
            Assert.Equal(sim.Tick + Simulation.SpawnInterestTicks, b.InterestUntil);

            // уводим игрока далеко: Interest идёт прямо, цели не берёт, по истечении — Ожидание
            SimEntity[] ents = new SimEntity[sim.EntityCount];
            sim.Entities.CopyTo(ents);
            for (int i = 0; i < ents.Length; i++)
            {
                if (ents[i].Id == p)
                {
                    ents[i].Mover.Position = At(900, 512); // далеко: Interest доходит ~120 т, в зрение (21 т) не попадает
                }
            }

            var sim2 = new Simulation(sim.World);
            sim2.Restore(sim.Tick, ents, sim.NextEntityId, sim.WorldTime);
            long until = sim.Tick + Simulation.SpawnInterestTicks;
            while (sim2.Tick + 1 < until) // Interest активен, пока такт < InterestUntil
            {
                sim2.Step();
                Assert.Equal(EnemyState.Interest, sim2.GetEntity(spawned).Brain.State);
            }

            sim2.Step();
            Assert.Equal(EnemyState.Idle, sim2.GetEntity(spawned).Brain.State);
        }

        // ==================================================== 8.8 «Исчезновение»

        /// <summary>30 с дальше 80 т: враг в 90 т от единственного игрока исчезает на такте 900 (на 898 жив).</summary>
        [Fact]
        public void S09_S8_8_Far90_Despawn_AtTick900()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            Room(sim, 30, 30);
            int p = sim.AddEntity(At(30, 30));
            sim = Tank(sim, p);
            int rogue = sim.AddMeleeRogue(At(120, 30)); // 90 т
            Steps(sim, 898);
            Assert.True(sim.SlotOf(rogue) >= 0, "исчез раньше 900 тактов");
            Steps(sim, 2);
            Assert.True(sim.SlotOf(rogue) < 0, "не исчез на такте 900 (30 с подряд дальше 80 т)");
        }

        /// <summary>70 т — вне порога: за 1200 тактов не исчезает.</summary>
        [Fact]
        public void S09_S8_8_Far70_NoDespawn()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            Room(sim, 30, 30);
            int p = sim.AddEntity(At(30, 30));
            sim = Tank(sim, p);
            int rogue = sim.AddMeleeRogue(At(100, 30)); // 70 т
            Steps(sim, 1200);
            Assert.True(sim.SlotOf(rogue) >= 0, "исчез в 70 т от игрока");
        }

        /// <summary>
        /// «Идёт бой прерывает серию»: цель не исчезает по дальности; после потери цели отсчёт начинается
        /// заново — исчезновение позже 900 тактов от разрыва.
        /// </summary>
        [Fact]
        public void S09_S8_8_WithTarget_Exempt_ThenDespawn()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            Room(sim, 512, 512);
            Room(sim, 608, 512); // цель после переноса тоже в комнате: спавненные враги её не бьют и не сносят
            int p = sim.AddEntity(At(512, 512));
            int rogue = sim.AddMeleeRogue(At(520, 512));
            sim = Tank(sim, p);
            Steps(sim, 4);
            Assert.Equal(p, sim.GetEntity(rogue).Brain.TargetId);

            SimEntity[] ents = new SimEntity[sim.EntityCount];
            sim.Entities.CopyTo(ents);
            for (int i = 0; i < ents.Length; i++)
            {
                if (ents[i].Id == p)
                {
                    ents[i].Mover.Position = At(608, 512); // уносим цель на 88 т
                }
            }

            var sim2 = new Simulation(sim.World) { SpawnEnabled = true };
            sim2.Restore(sim.Tick, ents, sim.NextEntityId, sim.WorldTime);
            while (sim2.Tick < 900)
            {
                sim2.Step();
            }

            Assert.True(sim2.SlotOf(rogue) >= 0, "исчез с целью на 900-м такте — бой не прервал серию");
            // Сдача «упёршегося» у стены (1-013) ~такта 675: серия стартует от неё → исчезновение ~1576.
            while (sim2.Tick < 1700 && sim2.SlotOf(rogue) >= 0)
            {
                sim2.Step();
            }

            Assert.True(sim2.SlotOf(rogue) < 0, "после потери цели серия так и не привела к исчезновению");
        }

        /// <summary>Чанк вне активной зоны (9 × 9 у всех игроков): враг в 300 т удаляется за такт.</summary>
        [Fact]
        public void S09_S8_8_ChunkOutsideZone_OneTick()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.AddEntity(At(30, 30));
            int rogue = sim.AddMeleeRogue(At(330, 30));
            sim.Step();
            Assert.True(sim.SlotOf(rogue) < 0, "чанк вне активной зоны — враг не удалён за такт");
        }

        // ==================================================== сохранение (формат 6)

        /// <summary>Поля спавнера (CauseId/InterestUntil/FarSince) пишутся в сейв формата 6 и читаются.</summary>
        [Fact]
        public void S09_S6_Save_RoundTrip_BrainFields()
        {
            WorldModel world = Flat(1301);
            byte[] baseBytes = WorldSnapshot.WriteBase(world);
            byte[] baseSha = WorldSnapshot.StoredHash(baseBytes);

            var sim = new Simulation(world) { DayScale = 0 };
            int p = sim.AddEntity(At(512, 512));
            int r = sim.AddMeleeRogue(At(512, 520));
            Steps(sim, 10);

            SimEntity[] ents = new SimEntity[sim.EntityCount];
            sim.Entities.CopyTo(ents);
            for (int i = 0; i < ents.Length; i++)
            {
                if (ents[i].Id == r)
                {
                    ents[i].Brain.CauseId = p;
                    ents[i].Brain.InterestUntil = 999;
                    ents[i].Brain.FarSince = 77;
                }
            }

            var sim2 = new Simulation(world) { DayScale = 0 };
            sim2.Restore(sim.Tick, ents, sim.NextEntityId, sim.WorldTime);
            byte[] save = WorldSave.Build(WorldSave.Capture(sim2, baseSha, "спавн"), 6, 0);

            var sim3 = SaveFixtures.Load(baseBytes, save);
            SimEntity e3 = sim3.GetEntity(r);
            Assert.Equal(p, e3.Brain.CauseId);
            Assert.Equal(999L, e3.Brain.InterestUntil);
            Assert.Equal(77, e3.Brain.FarSince);

            byte[] again = WorldSave.Build(WorldSave.Capture(sim3, baseSha, "спавн"), 6, 0);
            Assert.Equal(save, again); // хэш полей сходится — round-trip полный
        }
    }
}
