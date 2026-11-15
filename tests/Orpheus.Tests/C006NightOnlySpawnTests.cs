using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Карточка C-006 (слово Автора 15.11: «мобы агрессивные только ночью появлялись»): попытки появления —
    /// только в фазе «ночь» (день, сумерки, рассвет — ни одного нового врага); с рассветом враги уходят:
    /// без цели — в пределах 1 с, в бою — по потере цели или через 30 с светлого времени, не посреди замаха.
    /// </summary>
    public class C006NightOnlySpawnTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat(ulong seed) => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), seed);

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

        private static void Room(Simulation sim, int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx != 0 || dy != 0)
                    {
                        Wall(sim, x + dx, y + dy);
                    }
                }
            }
        }

        /// <summary>Игрокам 30 000 HP (как <c>SpawnTests.Tank</c>): враги не убивают тестового игрока за прогон.</summary>
        private static Simulation Tank(Simulation sim, int player)
        {
            SimEntity[] ents = new SimEntity[sim.EntityCount];
            sim.Entities.CopyTo(ents);
            for (int i = 0; i < ents.Length; i++)
            {
                if (ents[i].Id == player)
                {
                    ents[i].Health = 30_000;
                    ents[i].MaxHealth = 30_000;
                }
            }

            var sim2 = new Simulation(sim.World) { SpawnEnabled = sim.SpawnEnabled, DayScale = sim.DayScale, SpawnCursor = sim.SpawnCursor, SpawnNextTry = sim.SpawnNextTry };
            sim2.Restore(sim.Tick, ents, sim.NextEntityId, sim.WorldTime);
            return sim2;
        }

        private static Simulation Move(Simulation sim, int id, WorldPos to)
        {
            SimEntity[] ents = new SimEntity[sim.EntityCount];
            sim.Entities.CopyTo(ents);
            for (int i = 0; i < ents.Length; i++)
            {
                if (ents[i].Id == id)
                {
                    ents[i].Mover.Position = to;
                }
            }

            var sim2 = new Simulation(sim.World) { SpawnEnabled = sim.SpawnEnabled, DayScale = sim.DayScale, SpawnCursor = sim.SpawnCursor, SpawnNextTry = sim.SpawnNextTry };
            sim2.Restore(sim.Tick, ents, sim.NextEntityId, sim.WorldTime);
            return sim2;
        }

        private static List<int> EnemyIds(Simulation sim)
        {
            var s = new List<int>();
            foreach (SimEntity e in sim.Entities)
            {
                if (e.Kind == SimEntity.KindEnemy && e.Brain.Archetype != EnemyArchetype.None && e.Health > 0)
                {
                    s.Add(e.Id);
                }
            }

            return s;
        }

        // ------------------------------------------------ появление только ночью

        /// <summary>День: 10 игровых минут на открытом поле — ни одной новой сущности (раньше лимит 5 за 2 мин).</summary>
        [Fact]
        public void C006_Day_TenMinutes_ZeroSpawns()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(0);
            Room(sim, 512, 512);
            sim.AddEntity(At(512, 512));
            Steps(sim, 2);
            int next = sim.NextEntityId;
            for (int t = 0; t < 10 * 60 * DayClock.TicksPerSecond; t++)
            {
                sim.Step();
                Assert.Equal(DayPhase.Day, DayClock.PhaseOf(sim.WorldTime));
                Assert.Equal(next, sim.NextEntityId); // ни одного добавления — даже мгновенно исчезнувшего
            }

            Assert.Empty(EnemyIds(sim));
        }

        /// <summary>Сумерки и рассвет целиком (по 60 с) — ни одного появления.</summary>
        [Theory]
        [InlineData(DayClock.DuskStartTick, DayPhase.Dusk)]
        [InlineData(DayClock.DawnStartTick, DayPhase.Dawn)]
        public void C006_Dusk_And_Dawn_ZeroSpawns(int startTime, DayPhase phase)
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(startTime);
            Room(sim, 512, 512);
            sim.AddEntity(At(512, 512));
            while (DayClock.PhaseOf(sim.WorldTime) == phase)
            {
                sim.Step();
                if (DayClock.PhaseOf(sim.WorldTime) == phase)
                {
                    Assert.Empty(EnemyIds(sim)); // уход на рассвете — до появления в том же такте: новый был бы виден
                }
            }
        }

        /// <summary>
        /// Конец сумерек → ночь: в сумерки никого, с первого такта ночи спавнер работает как раньше
        /// (первая попытка сразу, далее раз в 4 с; лимит ночной — 8, проверяет <c>SpawnTests.S09_S8_5_Night_Limit8</c>).
        /// </summary>
        [Fact]
        public void C006_Night_Starts_Spawning_At_Once()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(DayClock.NightStartTick - 60);
            Room(sim, 512, 512);
            int p = sim.AddEntity(At(512, 512));
            sim = Tank(sim, p);
            while (DayClock.PhaseOf(sim.WorldTime) != DayPhase.Night)
            {
                Assert.Empty(EnemyIds(sim)); // такт, в котором часы перешли в ночь, — уже ночной
                sim.Step();
            }

            Assert.Single(EnemyIds(sim)); // первая попытка — в первый же такт ночи
            Steps(sim, 4 * Simulation.SpawnAttemptNightTicks);
            Assert.InRange(EnemyIds(sim).Count, 2, 5); // 4–5 попыток за 16 с, тайл кольца на поле почти всегда годен
        }

        // ------------------------------------------------ с рассветом враги уходят

        /// <summary>Враг без цели на рассвете исчезает в пределах 1 с (разброс по номеру); до рассвета — жив.</summary>
        [Fact]
        public void C006_Dawn_NoTarget_Despawns_Within_1s()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(DayClock.DawnStartTick - 40);
            Room(sim, 512, 512);
            int p = sim.AddEntity(At(512, 512));
            int rogue = sim.AddMeleeRogue(At(560, 512)); // 48 т: вне зрения (ночью 21 т), цели нет
            sim = Tank(sim, p);
            while (DayClock.PhaseOf(sim.WorldTime) == DayPhase.Night)
            {
                sim.Step();
                Assert.True(sim.SlotOf(rogue) >= 0, "исчез ночью");
            }

            Assert.True(sim.GetEntity(rogue).Brain.TargetId < 0);
            Steps(sim, Simulation.DaylightDespawnStaggerTicks);
            Assert.True(sim.SlotOf(rogue) < 0, "враг без цели не ушёл за 1 с рассвета");
        }

        /// <summary>Днём (не только на рассвете) поставленный враг без цели уходит в пределах 1 с.</summary>
        [Fact]
        public void C006_Day_Placed_NoTarget_Despawns_Within_1s()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(DayClock.TicksPerDay / 4);
            Room(sim, 512, 512);
            sim.AddEntity(At(512, 512));
            int rogue = sim.AddMeleeRogue(At(560, 512));
            Steps(sim, Simulation.DaylightDespawnStaggerTicks);
            Assert.True(sim.SlotOf(rogue) < 0);
        }

        /// <summary>
        /// Враг в бою на рассвете доигрывает: с целью жив до 30 с светлого времени, затем исчезает — не посреди
        /// замаха (на такте ухода замах не идёт: удар либо не начат, либо приземлился в этом такте).
        /// </summary>
        [Fact]
        public void C006_Dawn_InFight_Finishes_Then_Despawns_After_30s()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(DayClock.DawnStartTick - 60);
            int p = sim.AddEntity(At(512, 512)); // в поле: враг бьёт
            int rogue = sim.AddMeleeRogue(At(515, 512));
            sim = Tank(sim, p);
            Steps(sim, 10);
            Assert.Equal(p, sim.GetEntity(rogue).Brain.TargetId);

            int prevWindup = 0;
            bool struck = false;
            while (sim.SlotOf(rogue) >= 0)
            {
                SimEntity e = sim.GetEntity(rogue);
                prevWindup = e.AttackWindup;
                struck |= e.AttackWindup > 0;
                int since = Simulation.TicksSinceDawn(sim.WorldTime);
                bool day = Simulation.IsDaylight(DayClock.PhaseOf(sim.WorldTime));
                Assert.True(!day || since < Simulation.DaylightFightTicks + MeleeRogue.WindupTicks,
                    $"враг в бою не ушёл: {since} тактов часов с рассвета");
                Assert.True(e.Brain.TargetId == p, "цель потеряна — сценарий проверяет уход по 30 с");
                sim.Step();
            }

            int at = Simulation.TicksSinceDawn(sim.WorldTime);
            Assert.True(at >= Simulation.DaylightFightTicks, $"ушёл раньше 30 с ({at})");
            Assert.True(prevWindup <= 1, $"ушёл посреди замаха (замах {prevWindup})");
            Assert.True(struck, "сценарий без ударов — бой не шёл");
        }

        /// <summary>Враг в бою на рассвете потерял цель (игрок ушёл на 40 т) — уходит сразу после, задолго до 30 с.</summary>
        [Fact]
        public void C006_Dawn_InFight_LosesTarget_Then_Despawns()
        {
            var sim = new Simulation(Flat(1301)) { SpawnEnabled = true };
            sim.SetWorldTime(DayClock.DawnStartTick);
            int p = sim.AddEntity(At(512, 512));
            int rogue = sim.AddMeleeRogue(At(515, 512));
            sim = Tank(sim, p);
            Steps(sim, 10);
            Assert.Equal(p, sim.GetEntity(rogue).Brain.TargetId);
            Assert.True(sim.SlotOf(rogue) >= 0);

            sim = Move(sim, p, At(582, 512)); // 70 т — дальше радиуса потери (28 т, ×1,5 ночью)
            int t = 0;
            while (sim.SlotOf(rogue) >= 0 && t < 600)
            {
                sim.Step();
                t++;
            }

            Assert.True(sim.SlotOf(rogue) < 0, "потерявший цель на рассвете не ушёл за 20 с");
            Assert.True(Simulation.TicksSinceDawn(sim.WorldTime) < Simulation.DaylightFightTicks,
                "ушёл только по 30 с, а не по потере цели");
        }

        /// <summary>
        /// Детерминизм: последние 40 с ночи с появлениями → рассвет; два прогона с одним зерном дают одинаковый
        /// хэш состояния каждые 30 тактов и одинаковые номера; к 50-й секунде рассвета врагов нет.
        /// </summary>
        [Fact]
        public void C006_Night_To_Dawn_Deterministic_And_Empty_By_Day()
        {
            static Simulation Run(List<ulong> hashes)
            {
                var sim = new Simulation(Flat(77)) { SpawnEnabled = true };
                sim.SetWorldTime(DayClock.DawnStartTick - 1200);
                Room(sim, 512, 512);
                int p = sim.AddEntity(At(512, 512));
                sim = Tank(sim, p);
                for (int t = 0; t < 1200 + 1500; t++)
                {
                    sim.Step();
                    if (t % 30 == 0)
                    {
                        hashes.Add(StateHash.Compute(sim).Total);
                    }
                }

                return sim;
            }

            var ha = new List<ulong>();
            var hb = new List<ulong>();
            Simulation a = Run(ha);
            Simulation b = Run(hb);
            Assert.Equal(ha, hb);
            Assert.Equal(a.NextEntityId, b.NextEntityId);
            Assert.True(a.NextEntityId > 3, "ночью никто не появился — сценарий пуст");
            Assert.Empty(EnemyIds(a));
        }
    }
}
