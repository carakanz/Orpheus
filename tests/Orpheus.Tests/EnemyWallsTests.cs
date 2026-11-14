using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Карточка 1-013 «правило стен» (docs/02, 9.3): враг не проходит сквозь стену/забор/закрытую дверь;
    /// «упёрся» (Blocked) — пересчёт пути сразу при правке мира (кэш «кармана» — один A* на враг),
    /// 20 с у преграды — цель в игнор на 30 с и Ожидание; открытая дверь — проход сразу; линия удара
    /// (стена/закрытая дверь между врагом и целью — не бьёт, забор прозрачен); бюджет A* на 100 врагах;
    /// хижина в осаде; leash 60 тайлов от точки появления.
    /// </summary>
    public class EnemyWallsTests
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

        private static void Wall(Simulation sim, int x, int y)
        {
            sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, ObjectId.WoodWall, BuildRecord.OfPlayer(1, FloorId.Grass));
        }

        private static void Door(Simulation sim, int x, int y, bool open = false)
        {
            BuildRecord rec = BuildRecord.OfPlayer(1, FloorId.Grass);
            if (open)
            {
                rec = rec.WithFlags(BuildFlags.DoorOpen);
            }

            sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, ObjectId.Door, rec);
        }

        private static void Fence(Simulation sim, int x, int y)
        {
            sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, ObjectId.WoodFence, BuildRecord.OfPlayer(1, FloorId.Grass));
        }

        private static TileCoord TileOf(Simulation sim, int id) =>
            Torus.TileOf(sim.World.Shape, sim.Entities[sim.SlotOf(id)].Mover.Position);

        private static int Hp(Simulation sim, int id) => sim.GetEntity(id).Health;

        // ------------------------------------------------ 9.3: не проходит сквозь преграду

        /// <summary>
        /// «Стена — не проход»: дом 21 × 21 из стены / стены с закрытой дверью / забора, враг снаружи,
        /// 1000 тактов: враг ни разу не внутри (тайл X ≤ 19 с западной стороны), игрок цел.
        /// </summary>
        [Theory]
        [InlineData(0)] // сплошная стена
        [InlineData(1)] // стена с закрытой дверью в проёме
        [InlineData(2)] // забор (непроходим, transparent для удара — проходa нет)
        public void W13_Barrier_NoEntry_1000Ticks(int kind)
        {
            var sim = new Simulation(Flat(1301));
            int ch = sim.AddEntity(At(30, 30));
            for (int t = 20; t <= 40; t++)
            {
                for (int x = 20; x <= 40; x++)
                {
                    bool edge = x == 20 || x == 40 || t == 20 || t == 40;
                    if (!edge)
                    {
                        continue; // периметр
                    }

                    if (kind == 1 && x == 20 && t == 30)
                    {
                        Door(sim, x, t); // закрытая дверь в проёме
                    }
                    else if (kind == 2)
                    {
                        Fence(sim, x, t);
                    }
                    else
                    {
                        Wall(sim, x, t);
                    }
                }
            }

            sim.Step();
            int rogue = sim.AddMeleeRogue(At(17, 30)); // 13 тайлов до игрока — в обзоре, стена между
            for (int i = 0; i < 1000; i++)
            {
                sim.Step();
                Assert.True(TileOf(sim, rogue).X <= 19, $"такт {i}: враг прошёл преграду (тайл {TileOf(sim, rogue)})");
            }

            Assert.Equal(100, Hp(sim, ch));
            Assert.True(sim.GetEntity(rogue).Alive, "враг жив");
        }

        // ------------------------------------------------ 9.3: «упёрся» → сдача (игнор 30 с) → Ожидание

        /// <summary>
        /// «Упёрся» (9.3): карман из стены, враг в 4 тайлах; ~30-й такт — Blocked; к 650-му — 20 с истекли:
        /// Ожидание, цель сброшена, цель в игноре; до конца игнора (30 с) цели нет; после — снова цель и погоня.
        /// </summary>
        [Fact]
        public void W13_Blocked_GivesUp_After20s_Ignore30s_Then_Chase()
        {
            var sim = new Simulation(Flat(1302));
            int ch = sim.AddEntity(At(22, 22));
            for (int x = 21; x <= 23; x++)
            {
                for (int y = 21; y <= 23; y++)
                {
                    if (x != 22 || y != 22)
                    {
                        Wall(sim, x, y);
                    }
                }
            }

            sim.Step();
            int rogue = sim.AddMeleeRogue(At(18, 22));

            Steps(sim, 650);
            EnemyBrain b = sim.GetEntity(rogue).Brain;
            Assert.Equal(EnemyState.Idle, b.State);
            Assert.Equal(EnemyBrain.NoTarget, b.TargetId);
            Assert.Equal(ch, b.IgnoreTargetId);
            Assert.True(b.IgnoreUntil > 650, "игнор цели ещё активен");

            Steps(sim, 1500 - 650);
            b = sim.GetEntity(rogue).Brain;
            Assert.Equal(EnemyBrain.NoTarget, b.TargetId); // игнор 30 с (~1530) ещё держит

            bool chased = false;
            for (int i = 1500; i < 1560 && !chased; i++)
            {
                sim.Step();
                b = sim.GetEntity(rogue).Brain;
                if (b.TargetId == ch)
                {
                    chased = true;
                    Assert.Equal(EnemyState.Chase, b.State);
                }
            }

            Assert.True(chased, "после игнора враг снова заметил цель");
        }

        // ------------------------------------------------ 9.3: мир правили — пересчёт сразу (дверь открыли)

        /// <summary>
        /// «Изменился тайл» (9.3): игрок в кармане с дверью, враг снаружи — упёрся; дверь убрана правкой —
        /// пересчёт без ожидания (тот же такт), враг проходит проём за ≤ 15 тактов.
        /// </summary>
        [Fact]
        public void W13_DoorRemoved_Repath_Enters_Within15Ticks()
        {
            var sim = new Simulation(Flat(1303));
            int ch = sim.AddEntity(At(20, 22)); // в глубине кармана — от двери не достать (972 > 460)
            for (int x = 19; x <= 23; x++)
            {
                for (int y = 21; y <= 23; y++)
                {
                    if (x == 23 && y == 22)
                    {
                        Door(sim, x, y); // дверь в проёме
                    }
                    else if (y != 22)
                    {
                        Wall(sim, x, y); // верхний и нижний ряды
                    }
                    else if (x == 19)
                    {
                        Wall(sim, x, y); // западная стена за игроком — карман замкнут
                    }
                }
            }

            sim.Step();
            int rogue = sim.AddMeleeRogue(At(25, 22)); // дверь между

            Steps(sim, 100);
            Assert.Equal(EnemyState.Blocked, sim.GetEntity(rogue).Brain.State);
            Assert.Equal(24, TileOf(sim, rogue).X); // дошёл до проёма

            // Игрок снёс дверь (правка мира) — пересчёт пути в том же такте, без ожидания 2 с.
            sim.RequestEdit(new TileCoord(23, 22), FloorId.Grass, ObjectId.Empty);
            sim.Step();
            int removedTick = (int)sim.Tick;

            int entered = -1;
            for (int i = 0; i < 15; i++)
            {
                sim.Step();
                if (TileOf(sim, rogue).X <= 23)
                {
                    entered = (int)sim.Tick;
                    break;
                }
            }

            Assert.True(entered >= 0 && entered - removedTick <= 15,
                $"враг вошёл в проём на такте {entered} (снятие двери на {removedTick})");
        }

        // ------------------------------------------------ з.2: «карман» стоит не дороже одного A*

        /// <summary>
        /// Кэш «кармана» (з.2): цель в замкнутой области, мир не правят — за 1000 тактов ровно один поиск
        /// (первый запрос), переспросов нет; тайл не сменился — пересчёт не нужен (9.3).
        /// </summary>
        [Fact]
        public void W13_EnclosedPocket_CostsOneSearchPer1000Ticks()
        {
            var sim = new Simulation(Flat(1304));
            int ch = sim.AddEntity(At(22, 22));
            for (int x = 21; x <= 23; x++)
            {
                for (int y = 21; y <= 23; y++)
                {
                    if (x != 22 || y != 22)
                    {
                        Wall(sim, x, y);
                    }
                }
            }

            sim.Step();
            int rogue = sim.AddMeleeRogue(At(18, 22));

            Steps(sim, 200);
            // Кэш «кармана» (з.2): Blocked несёт ревизию правки, на которой цель стала Enclosed, —
            // до следующей смены тайла переспрос не ставится.
            Assert.Equal(EnemyState.Blocked, sim.GetEntity(rogue).Brain.State);
            Assert.Equal(sim.EditRevision + 1, sim.GetEntity(rogue).Brain.EnclosedRev);
            Assert.Equal(1, sim.PathSearches);

            Steps(sim, 800);
            Assert.Equal(1, sim.PathSearches);
        }

        // ------------------------------------------------ з.1: бюджет A* — ≤ 2 поиска за такт на 100 врагах

        /// <summary>
        /// Бюджет такта (docs/02, 2.4; 1-013 з.1): 99 разбойников бегут к игроку — планировщик завершает
        /// не больше PathMaxPerTick (2) поисков за такт; за 300 тактов — не больше 2 × 300 поисков.
        /// </summary>
        [Fact]
        public void W13_Budget_100Rogues_MaxTwoSearchesPerTick()
        {
            var sim = new Simulation(Flat(1305), maxEntities: 128);
            int ch = sim.AddEntity(At(22, 22));
            int rogues = 0;
            for (int i = 0; i < 10; i++)
            {
                for (int j = 0; j < 10; j++)
                {
                    int x = 10 + i * 3, y = 10 + j * 3;
                    if (x == 22 && y == 22)
                    {
                        continue; // тайл игрока
                    }

                    sim.AddMeleeRogue(At(x, y));
                    rogues++;
                }
            }

            Assert.Equal(99, rogues);
            for (int i = 0; i < 300; i++)
            {
                sim.Step();
                Assert.True(sim.PathLastTickCompleted <= Simulation.PathMaxPerTick,
                    $"такт {i}: поисков {sim.PathLastTickCompleted} > {Simulation.PathMaxPerTick}");
            }

            Assert.True(sim.PathSearches <= 2L * 300, $"поисков за 300 тактов: {sim.PathSearches}");
        }

        // ------------------------------------------------ приёмка 1.6: хижина в осаде

        /// <summary>
        /// Хижина 5 × 5 с дверью, игрок в центре, 10 разбойников по кругу: 9000 тактов (5 мин) — ни один
        /// не проходит (игрок цел), эпизод «упёрся» у каждого прерывается сдачей (непрерывный Blocked ≤ 610
        /// тактов — «никто не застревает», 9.3).
        /// </summary>
        [Fact]
        public void W13_Hut_Siege_NoOnePasses_NoOneStuck_9000Ticks()
        {
            var sim = new Simulation(Flat(1306));
            int ch = sim.AddEntity(At(22, 22));
            for (int x = 20; x <= 24; x++)
            {
                for (int y = 20; y <= 24; y++)
                {
                    if (x == 22 && y == 20)
                    {
                        Door(sim, x, y); // дверь с севера
                    }
                    else
                    {
                        Wall(sim, x, y);
                    }
                }
            }

            sim.Step();
            int[] ring = new int[10];
            int[][] at = new int[][]
            {
                new[] { 16, 19 }, new[] { 16, 22 }, new[] { 16, 25 },
                new[] { 28, 19 }, new[] { 28, 22 }, new[] { 28, 25 },
                new[] { 19, 16 }, new[] { 25, 16 }, new[] { 19, 28 }, new[] { 25, 28 },
            };
            for (int i = 0; i < ring.Length; i++)
            {
                ring[i] = sim.AddMeleeRogue(At(at[i][0], at[i][1]));
            }

            int[] blockedRun = new int[ring.Length];
            for (int t = 0; t < 9000; t++)
            {
                sim.Step();
                Assert.Equal(100, Hp(sim, ch)); // «0 проходов»: сквозь стену/дверь никто не прошёл и не ударил
                for (int i = 0; i < ring.Length; i++)
                {
                    SimEntity e = sim.GetEntity(ring[i]);
                    blockedRun[i] = e.Brain.State == EnemyState.Blocked ? blockedRun[i] + 1 : 0;
                    Assert.True(blockedRun[i] <= 610,
                        $"враг {ring[i]} застревает: {blockedRun[i]} тактов подряд в Blocked (предел 610)");
                }
            }

            for (int i = 0; i < ring.Length; i++)
            {
                Assert.True(sim.GetEntity(ring[i]).Alive, "враг жив");
            }
        }

        // ------------------------------------------------ 9.3: линия удара (забор прозрачен, стена — нет)

        /// <summary>
        /// Линия удара через забор (9.3 «забор прозрачен»): игрок, ряд забора, враг — дистанция в зоне удара;
        /// за 300 тактов враг бьёт через забор (урон есть).
        /// </summary>
        [Fact]
        public void W13_StrikeLine_Fence_Transparent()
        {
            var sim = new Simulation(Flat(1307));
            int ch = sim.AddEntity(new WorldPos(10 * U + U / 2, 10 * U + 200)); // верхний край ряда 10
            Fence(sim, 10, 11);
            sim.Step();
            sim.AddMeleeRogue(new WorldPos(10 * U + U / 2, 12 * U + 40)); // ряд 12; дистанция 352 ≤ 460

            Steps(sim, 300);
            Assert.True(Hp(sim, ch) < 100, "враг бьёт через забор (забор прозрачен для линии удара)");
        }

        /// <summary>
        /// Линия удара через стену (9.3 «стена между — не бьёт»): та же постановка, вместо забора стена;
        /// за 300 тактов — ни одного удара (замах не начинается).
        /// </summary>
        [Fact]
        public void W13_StrikeLine_Wall_Blocks()
        {
            var sim = new Simulation(Flat(1308));
            int ch = sim.AddEntity(new WorldPos(10 * U + U / 2, 10 * U + 200));
            Wall(sim, 10, 11);
            sim.Step();
            sim.AddMeleeRogue(new WorldPos(10 * U + U / 2, 12 * U + 40));

            Steps(sim, 300);
            Assert.Equal(100, Hp(sim, ch));
        }

        /// <summary>
        /// Удар сквозь закрытую дверь с порога (C-001, `02` 3.4): дверь закрыта, враг стоит НА тайле
        /// двери (закрылась под ним — сдвиг из непроходимого тайла не форсируется), игрок за дверью
        /// в зоне удара. За 300 тактов — ни одного удара: закрытая дверь под атакующим — преграда
        /// линии, безусловный пропуск «своего» тайла её не отменяет.
        /// </summary>
        [Fact]
        public void W13_StrikeLine_ClosedDoor_RogueOnThreshold_NoStrike()
        {
            var sim = new Simulation(Flat(1317));
            int ch = sim.AddEntity(new WorldPos(10 * U + U / 2, 10 * U + 200)); // игрок за дверью, ряд 10
            Door(sim, 10, 11); // закрытая (open=false)
            sim.Step();
            sim.AddMeleeRogue(At(10, 11)); // враг на пороге: дистанция 184 ≤ 460

            Steps(sim, 300);
            Assert.Equal(100, Hp(sim, ch)); // «урон за дверью 4 кадра» (D08_3_1) не повторяется
        }

        /// <summary>
        /// Открытая дверь с порога бьёт (C-001, фикс не ломает проход): тот же порог, дверь открыта —
        /// за 300 тактов удар есть (открытая дверь под атакующим — проходима, линию не перекрывает).
        /// </summary>
        [Fact]
        public void W13_StrikeLine_OpenDoor_RogueOnThreshold_Strikes()
        {
            var sim = new Simulation(Flat(1318));
            int ch = sim.AddEntity(new WorldPos(10 * U + U / 2, 10 * U + 200));
            Door(sim, 10, 11, open: true);
            sim.Step();
            sim.AddMeleeRogue(At(10, 11));

            Steps(sim, 300);
            Assert.True(Hp(sim, ch) < 100, "враг на пороге открытой двери бьёт");
        }

        // ------------------------------------------------ з.3: leash 60 тайлов от точки появления

        /// <summary>
        /// Leash (з.3, 0-028): игрок убегает на восток (51,2 ед/такт против 46 у врага); увязшийся погонщик
        /// сдаётся на 60 тайлах от дома: за 2040 тактов враг не уходит дальше 62 тайлов от точки появления
        /// и возвращается (домой, Ожидание/бродяга).
        /// </summary>
        [Fact]
        public void W13_Leash_NeverFartherThan62TilesFromHome()
        {
            var sim = new Simulation(Flat(1309));
            int ch = sim.AddEntity(At(10, 10));
            int rogue = sim.AddMeleeRogue(At(6, 10)); // 4 тайла — замечает сразу
            WorldPos home = At(6, 10);
            long leash2 = 62L * U * 62 * U;

            for (int i = 0; i < 2040; i++)
            {
                sim.SetInput(ch, new InputFrame(Input8.East));
                sim.Step();
                long d2 = Torus.Dist2(sim.World.Shape, sim.Entities[sim.SlotOf(rogue)].Mover.Position, home);
                Assert.True(d2 <= leash2, $"такт {i}: враг ушёл от дома дальше 62 тайлов");
            }

            long endD2 = Torus.Dist2(sim.World.Shape, sim.Entities[sim.SlotOf(rogue)].Mover.Position, home);
            Assert.True(endD2 <= 8L * U * 8 * U, "враг вернулся домой");
            EnemyState st = sim.GetEntity(rogue).Brain.State;
            Assert.True(st is EnemyState.Idle or EnemyState.Wander, $"состояние {st} — бродяга у дома");
        }
    }
}
