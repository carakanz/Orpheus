using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-12b — путь врагов видит ту же проходимость, что движение: решётка точек 0,5 и круг 0,4 (<see cref="PathFinder"/>).
    /// Проём 1 тайл, сдвинутый на полтайла, враг находит; щель 0,5 — нет; дом с открытой дверью любой раскладки — входит,
    /// с закрытой — нет; «цель в замкнутой области»; подход к проёму наискосок без застревания у косяка; случаи приёмки
    /// Автора 15.11 (П-образная постройка и комната, игрок снаружи — враги обходят).
    /// </summary>
    public sealed class HalfPathTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const int R = Collision.RadiusUnits;
        private readonly ITestOutputHelper _out;

        public HalfPathTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static WorldModel Flat(ulong seed) => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), seed);

        private static WorldPos At(double x, double y) => new WorldPos((int)(x * U), (int)(y * U));

        // ------------------------------------------------ постройка ячейками

        /// <summary>Набор ячеек (координаты ячеек: ячейка c — [c/2, c/2 + 0,5) тайла) с видами; ставится по тайлам.</summary>
        private sealed class Plan
        {
            private readonly Dictionary<(int, int), (ushort Obj, byte Mask, bool Open)> _tiles = new Dictionary<(int, int), (ushort, byte, bool)>();

            public void Cell(int cx, int cy, ushort obj = ObjectId.StoneWall, bool open = false)
            {
                var t = (cx >> 1, cy >> 1);
                byte bit = (byte)(1 << (((cy & 1) << 1) | (cx & 1)));
                _tiles.TryGetValue(t, out var v);
                Assert.True(v.Mask == 0 || v.Obj == obj, $"в тайле {t} два вида");
                _tiles[t] = (obj, (byte)(v.Mask | bit), open);
            }

            /// <summary>Горизонтальная линия ячеек: ряд cy, ячейки cx0…cx1.</summary>
            public void H(int cx0, int cx1, int cy)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    Cell(cx, cy);
                }
            }

            /// <summary>Вертикальная линия ячеек: столбец cx, ячейки cy0…cy1.</summary>
            public void V(int cx, int cy0, int cy1)
            {
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    Cell(cx, cy);
                }
            }

            public void Remove(int cx, int cy)
            {
                var t = (cx >> 1, cy >> 1);
                byte bit = (byte)(1 << (((cy & 1) << 1) | (cx & 1)));
                if (_tiles.TryGetValue(t, out var v))
                {
                    v.Mask = (byte)(v.Mask & ~bit);
                    if (v.Mask == 0)
                    {
                        _tiles.Remove(t);
                    }
                    else
                    {
                        _tiles[t] = v;
                    }
                }
            }

            /// <summary>Дверь — пара ячеек тайла (вдоль стены): ячейки (cx, cy) и соседняя по оси стены.</summary>
            public void Door(int cx, int cy, bool horizontal, bool open)
            {
                Remove(cx, cy);
                Remove(horizontal ? cx + 1 : cx, horizontal ? cy : cy + 1);
                Cell(cx, cy, ObjectId.Door, open);
                Cell(horizontal ? cx + 1 : cx, horizontal ? cy : cy + 1, ObjectId.Door, open);
            }

            public void Apply(Simulation sim)
            {
                foreach (var kv in _tiles)
                {
                    BuildRecord rec = BuildRecord.OfPlayer(1, FloorId.Grass).WithCells(kv.Value.Mask);
                    if (kv.Value.Open)
                    {
                        rec = rec.WithFlags(BuildFlags.DoorOpen | BuildFlags.DoorKeepOpen);
                    }

                    Assert.True(sim.RequestEdit(new TileCoord(kv.Key.Item1, kv.Key.Item2), FloorId.Grass, kv.Value.Obj, rec));
                }

                sim.Step();
            }
        }

        /// <summary>Кольцо стены толщиной в ячейку по прямоугольнику ячеек [cx0, cx1] × [cy0, cy1].</summary>
        private static Plan Ring(int cx0, int cy0, int cx1, int cy1)
        {
            var p = new Plan();
            p.H(cx0, cx1, cy0);
            p.H(cx0, cx1, cy1);
            p.V(cx0, cy0 + 1, cy1 - 1);
            p.V(cx1, cy0 + 1, cy1 - 1);
            return p;
        }

        private static bool Inside(WorldPos p, int cx0, int cy0, int cx1, int cy1) =>
            p.X > (cx0 + 1) * (U / 2) && p.X < cx1 * (U / 2) && p.Y > (cy0 + 1) * (U / 2) && p.Y < cy1 * (U / 2);

        /// <summary>Тактов до того, как враг окажется внутри кольца (или −1 за предел).</summary>
        private static int TicksToEnter(Simulation sim, int rogue, int cx0, int cy0, int cx1, int cy1, int limit)
        {
            for (int i = 0; i < limit; i++)
            {
                sim.Step();
                if (Inside(sim.GetEntity(rogue).Mover.Position, cx0, cy0, cx1, cy1))
                {
                    return i;
                }
            }

            return -1;
        }

        // ------------------------------------------------ поиск пути: проём со сдвигом и щель 0,5

        /// <summary>
        /// Стена поперёк окна (ряд ячеек 61, тайлы 0…63 по тору) с проёмом шириной 2 ячейки (1 тайл) в ячейках c, c + 1:
        /// c чётное — проём по тайлу, нечётное — сдвинут на полтайла (две половины соседних тайлов). Путь есть, и все его точки
        /// свободны для круга (тот же <see cref="Collision"/>); щель 1 ячейка (0,5) — пути нет. Горизонтально и вертикально.
        /// </summary>
        [Theory]
        [InlineData(60, 2, false, true)]
        [InlineData(61, 2, false, true)]
        [InlineData(60, 2, true, true)]
        [InlineData(61, 2, true, true)]
        [InlineData(61, 1, false, false)]
        [InlineData(61, 1, true, false)]
        public void E212b_Path_Gap_ShiftedHalfTile_Found_HalfCellGap_NotFound(int c, int width, bool vertical, bool found)
        {
            var sim = new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.World(64), 1221));
            var p = new Plan();
            for (int k = 0; k < 128; k++)
            {
                if (k >= c && k < c + width)
                {
                    continue;
                }

                if (vertical)
                {
                    p.Cell(61, k);
                }
                else
                {
                    p.Cell(k, 61);
                }
            }

            p.Apply(sim);
            var terrain = new SimulationPathTerrain(sim);
            var finder = new PathFinder(enclosureFillLimit: 0);
            var path = new PathBuffer();
            WorldPos start = vertical ? At(25.5, 20.5) : At(20.5, 25.5);
            WorldPos goal = vertical ? At(36.5, 40.5) : At(40.5, 36.5);
            PathStatus status = finder.Find(terrain, start, goal, PathMode.Walker, path);
            _out.WriteLine($"{status}, точек {path.Length}, узлов {path.Expanded}");
            Assert.Equal(found ? PathStatus.Found : PathStatus.Partial, status);
            PathTests.AssertConnected(sim.Shape, start, path);
            for (int i = 0; i < path.Length; i++)
            {
                Assert.False(Collision.TouchesSolid(sim.Shape, path.Points[i], sim.Solid), $"точка {i} {path.Points[i]} задевает стену");
            }
        }

        /// <summary>
        /// Враг идёт за игроком сквозь проём 1 тайл в длинной стене (по тайлу и со сдвигом на полтайла в обе стороны, горизонтальная
        /// и вертикальная стена): доходит и бьёт; в щель 0,5 — не проходит (урона нет, сквозь стену никто).
        /// </summary>
        [Theory]
        [InlineData(60, 2, false)]
        [InlineData(61, 2, false)]
        [InlineData(59, 2, false)]
        [InlineData(60, 2, true)]
        [InlineData(61, 2, true)]
        [InlineData(59, 2, true)]
        [InlineData(61, 1, false)]
        [InlineData(61, 1, true)]
        public void E212b_Enemy_ThroughShiftedGap_NotThroughHalfCell(int c, int width, bool vertical)
        {
            var sim = new Simulation(Flat(1222));
            var p = new Plan();
            for (int k = c - 80; k < c + 80; k++)
            {
                if (k < c || k >= c + width)
                {
                    if (vertical)
                    {
                        p.Cell(61, k);
                    }
                    else
                    {
                        p.Cell(k, 61);
                    }
                }
            }

            p.Apply(sim);
            int ch = sim.AddEntity(vertical ? At(34.5, 30.5) : At(30.5, 34.5));
            int rogue = sim.AddMeleeRogue(vertical ? At(26.5, 33.0) : At(33.0, 26.5));
            bool crossed = false;
            int hp = 100;
            for (int i = 0; i < 600; i++)
            {
                sim.Step();
                hp = Math.Min(hp, sim.GetEntity(ch).Health); // здоровье восстанавливается — берём наименьшее
                WorldPos e = sim.GetEntity(rogue).Mover.Position;
                crossed |= vertical ? e.X - R >= 31 * U : e.Y - R >= 31 * U;
            }

            if (width >= 2)
            {
                Assert.True(crossed, $"враг не прошёл проём (позиция {sim.GetEntity(rogue).Mover.Position})");
                Assert.True(hp < 100, "враг прошёл, но не ударил");
            }
            else
            {
                Assert.False(crossed, "враг прошёл в щель 0,5");
                Assert.Equal(100, hp);
            }
        }

        // ------------------------------------------------ дом: открытая и закрытая дверь, любая раскладка

        /// <summary>
        /// Дом 9 × 9 из тонкой стены: стена на внешних или на внутренних половинах тайлов (смещение кольца на ячейку), дверь на
        /// любой стороне. Открытая — враг снаружи входит; закрытая — не входит (и урона нет). Плюс проём без двери со сдвигом на
        /// полтайла — входит.
        /// </summary>
        [Theory]
        [InlineData(0, 0, true)]
        [InlineData(0, 1, true)]
        [InlineData(0, 2, true)]
        [InlineData(0, 3, true)]
        [InlineData(1, 0, true)]
        [InlineData(1, 1, true)]
        [InlineData(1, 2, true)]
        [InlineData(1, 3, true)]
        [InlineData(0, 0, false)]
        [InlineData(1, 2, false)]
        [InlineData(0, 4, true)]
        [InlineData(1, 4, true)]
        public void E212b_House_AnyLayout_OpenDoor_Enters_ClosedDoor_Not(int shift, int side, bool open)
        {
            // Кольцо по ячейкам: внешние половины — [40, 59] (тайлы 20…29), внутренние — [41, 58].
            int cx0 = 40 + shift, cy0 = 40 + shift, cx1 = 59 - shift, cy1 = 59 - shift;
            Plan plan = Ring(cx0, cy0, cx1, cy1);
            switch (side)
            {
                case 0: plan.Door(48, cy0, true, open); break; // север (ячейки 48, 49 — тайл 24)
                case 1: plan.Door(48, cy1, true, open); break; // юг
                case 2: plan.Door(cx0, 48, false, open); break; // запад
                case 3: plan.Door(cx1, 48, false, open); break; // восток
                default: plan.Remove(49, cy1); plan.Remove(50, cy1); break; // юг: проём без двери, сдвинут (ячейки 49, 50)
            }

            var sim = new Simulation(Flat(1223));
            plan.Apply(sim);
            int ch = sim.AddEntity(At(25, 25));
            WorldPos from = side switch
            {
                0 => At(24.5, 13.5),
                1 => At(24.5, 36.5),
                2 => At(13.5, 24.5),
                3 => At(36.5, 24.5),
                _ => At(27.5, 36.5),
            };
            int rogue = sim.AddMeleeRogue(from);
            int t = TicksToEnter(sim, rogue, cx0, cy0, cx1, cy1, 900);
            _out.WriteLine($"сдвиг {shift}, сторона {side}, открыта {open}: вошёл на такте {t}, здоровье {sim.GetEntity(ch).Health}");
            if (open)
            {
                Assert.True(t >= 0, $"враг не вошёл за 30 с: {sim.GetEntity(rogue).Mover.Position}, {sim.GetEntity(rogue).Brain.State}");
            }
            else
            {
                Assert.Equal(-1, t);
                Assert.Equal(100, sim.GetEntity(ch).Health);
            }
        }

        // ------------------------------------------------ цель в замкнутой области

        /// <summary>
        /// «Цель в замкнутой области» (`02` 9.2) на решётке: дом 16 × 16 тайлов внутри (256 — предел заливки) из тонкой стены
        /// на внутренних половинах; игрок внутри, враг снаружи — итог <see cref="PathStatus.Enclosed"/>, путь — к стене со
        /// стороны игрока; дом 17 × 17 — заливка не дотягивает, частичный путь (как с целыми стенами).
        /// </summary>
        [Theory]
        [InlineData(16, PathStatus.Enclosed)]
        [InlineData(17, PathStatus.Partial)]
        public void E212b_GoalEnclosed_House256(int inner, PathStatus expected)
        {
            var sim = new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.World(128), 1224));
            // Внутренность — тайлы 20…20+inner−1; стена — правая половина тайла 19 и левая половина тайла 20+inner (кольцо ячеек).
            int cx0 = 39, cy0 = 39, cx1 = 2 * (20 + inner), cy1 = 2 * (20 + inner);
            Ring(cx0, cy0, cx1, cy1).Apply(sim);
            var finder = new PathFinder();
            var path = new PathBuffer();
            PathStatus status = finder.Find(new SimulationPathTerrain(sim), At(15.5, 28.5), At(21.5, 28.5), PathMode.Walker, path);
            _out.WriteLine($"{status}: конец {path.EndPoint}, узлов {path.Expanded}, залито {finder.TotalFilled}");
            Assert.Equal(expected, status);
            if (expected == PathStatus.Enclosed)
            {
                // Край — снаружи у западной стены, напротив игрока: x = 19,5 − 0,5 (точка, где круг касается стены не задевая).
                Assert.Equal(At(19.0, 28.5), path.EndPoint);
            }
        }

        /// <summary>
        /// Игрок в доме из тонких стен (без двери), 5 разбойников снаружи — через 20 с все в игноре цели, урона нет, поисков пути
        /// мало (кэш «замкнута»). Стена на внутренних половинах — снаружи у стены есть свободные половины тайлов.
        /// </summary>
        [Fact]
        public void E212b_Enclosed_FiveRogues_GiveUp_FewSearches()
        {
            var sim = new Simulation(Flat(1225));
            Ring(41, 41, 58, 58).Apply(sim);
            int ch = sim.AddEntity(At(21.0, 25.0));
            var rogues = new List<int>();
            for (int k = 0; k < 5; k++)
            {
                rogues.Add(sim.AddMeleeRogue(At(15.5, 21.5 + 2 * k)));
            }

            long searches0 = sim.PathSearches;
            for (int i = 0; i < 800; i++)
            {
                sim.Step();
            }

            Assert.Equal(100, sim.GetEntity(ch).Health);
            foreach (int r in rogues)
            {
                EnemyBrain b = sim.GetEntity(r).Brain;
                Assert.True(b.IgnoreTargetId == ch, $"разбойник {r}: не сдался ({b.State})");
            }

            long searches = sim.PathSearches - searches0;
            _out.WriteLine($"поисков за 800 тактов: {searches}");
            Assert.True(searches <= 5 * 4, $"поисков {searches}");
        }

        // ------------------------------------------------ подход к проёму наискосок (косяк)

        /// <summary>
        /// Дефект E2-12 (backlog [бой]): враг, подходящий к проёму 1 тайл наискосок, застревал у косяка — и с целыми стенами.
        /// Комната 21 × 21 (целые тайлы / тонкие стены), открытая дверь на западе; враг снаружи наискосок с разных сторон —
        /// входит.
        /// </summary>
        [Theory]
        [InlineData(false, -6)]
        [InlineData(false, 6)]
        [InlineData(false, -3)]
        [InlineData(true, -6)]
        [InlineData(true, 6)]
        [InlineData(true, -3)]
        [InlineData(true, 3)]
        public void E212b_DiagonalApproach_ToDoor_NoJambStuck(bool thin, int dy)
        {
            var sim = DoorRoom(thin, 1226, out _);
            int rogue = sim.AddMeleeRogue(At(16.5, 30.5 + dy));
            int entered = -1;
            for (int i = 0; i < 600 && entered < 0; i++)
            {
                sim.Step();
                if (sim.GetEntity(rogue).Mover.Position.X >= 22 * U)
                {
                    entered = i;
                }
            }

            _out.WriteLine($"тонкая {thin}, dy {dy}: вошёл на такте {entered}");
            Assert.True(entered >= 0, $"враг не вошёл за 20 с: {sim.GetEntity(rogue).Mover.Position}, {sim.GetEntity(rogue).Brain.State}");
        }

        // ------------------------------------------------ случаи приёмки Автора 15.11

        /// <summary>
        /// Снимок Автора 1 (15.11): П-образная постройка 10 × 10 из тонких стен, закрытые двери сверху и снизу, правая сторона
        /// открыта (короткий обрубок справа внизу). Игрок снаружи слева сверху у верхней стены; трое врагов справа — у верхнего
        /// правого угла снаружи, в открытой стороне и снаружи у обрубка. Все доходят до игрока (бьют) за 15 с; через закрытые
        /// двери никто не проходит (не оказывается над верхней стеной, не пройдя мимо правого края).
        /// </summary>
        [Fact]
        public void E212b_AuthorCase1_UShape_PlayerOutside_EnemiesWalkAround()
        {
            var sim = new Simulation(Flat(1227));
            var p = new Plan();
            p.H(20, 41, 20); // верх: ячейки x 20…41 (тайлы 10…20), ряд 20 (верхняя половина тайлов ряда 10)
            p.H(20, 41, 41); // низ: ряд 41 (нижняя половина ряда 20)
            p.V(20, 21, 40); // левая: столбец 20
            p.V(41, 38, 40); // обрубок справа внизу
            p.Door(30, 20, true, false);
            p.Door(30, 41, true, false);
            p.Apply(sim);
            int ch = sim.AddEntity(At(12.6, 9.5));
            int[] rogues =
            {
                sim.AddMeleeRogue(At(21.4, 10.4)),
                sim.AddMeleeRogue(At(20.4, 12.0)),
                sim.AddMeleeRogue(At(22.3, 18.3)),
            };
            AssertAllReach(sim, ch, rogues, 450);
        }

        /// <summary>
        /// Снимок Автора 2 (15.11): комната 10 × 10 из тонких стен, сверху закрытая дверь, снизу открытый проём (сдвинутый на
        /// полтайла). Игрок снаружи сверху; двое врагов снаружи справа у правой стены — обходят комнату по полю и бьют за 15 с.
        /// </summary>
        [Fact]
        public void E212b_AuthorCase2_Room_PlayerOutsideTop_EnemiesWalkAround()
        {
            var sim = new Simulation(Flat(1228));
            Plan p = Ring(20, 20, 39, 39); // внешние половины тайлов 10…19
            p.Door(30, 20, true, false);
            p.Remove(29, 39);
            p.Remove(30, 39);
            p.Apply(sim);
            int ch = sim.AddEntity(At(15.0, 8.6));
            int[] rogues =
            {
                sim.AddMeleeRogue(At(20.45, 16.8)),
                sim.AddMeleeRogue(At(21.2, 17.4)),
            };
            AssertAllReach(sim, ch, rogues, 450);
        }

        // ------------------------------------------------ C-009: вторая линия погони (переспрос, сдача, подруливание)

        /// <summary>Комната 21 × 21 (тайлы 20…40, целые тайлы или тонкие стены на внешних половинах), открытый проём на западе
        /// (тайл 20, 30); игрок в центре.</summary>
        private static Simulation DoorRoom(bool thin, ulong seed, out int ch)
        {
            var sim = new Simulation(Flat(seed));
            ch = sim.AddEntity(At(30.5, 30.5));
            var plan = new Plan();
            for (int ty = 20; ty <= 40; ty++)
            {
                for (int tx = 20; tx <= 40; tx++)
                {
                    bool edge = tx == 20 || tx == 40 || ty == 20 || ty == 40;
                    if (!edge || (tx == 20 && ty == 30))
                    {
                        continue;
                    }

                    if (thin)
                    {
                        // Внешние половины: кольцо ячеек [40, 81].
                        if (ty == 20) { plan.Cell(2 * tx, 40); plan.Cell(2 * tx + 1, 40); }
                        if (ty == 40) { plan.Cell(2 * tx, 81); plan.Cell(2 * tx + 1, 81); }
                        if (tx == 20) { plan.Cell(40, 2 * ty); plan.Cell(40, 2 * ty + 1); }
                        if (tx == 40) { plan.Cell(81, 2 * ty); plan.Cell(81, 2 * ty + 1); }
                    }
                    else
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            plan.Cell(2 * tx + (k & 1), 2 * ty + (k >> 1));
                        }
                    }
                }
            }

            plan.Apply(sim);
            return sim;
        }

        /// <summary>
        /// C-009 (замечание 1 проверки E2-12b): «застрял» — переспрос на ближайшем решении, а не кэш до правки мира. Подруливание
        /// выключено (флаг только для тестов) — упор в стену ничем, кроме переспроса, не снимается. Враг идёт по пути в проём
        /// комнаты; толкотня (в тесте — перенос) сбивает его за косяк внутрь: следующая точка старого пути — снаружи прямо на
        /// запад, ход упирается в стену. За ≤ 180 тактов — новый поиск, враг продвинулся к игроку; затем бьёт его.
        /// Мутация «застрял — кэш до правки мира» — стоит до сдачи (600 тактов).
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void C009_StuckWithoutSteer_Repaths_ReachesTarget(bool thin)
        {
            var sim = DoorRoom(thin, 1231, out int ch);
            sim.SteerOffForTests = true;
            int rogue = sim.AddMeleeRogue(At(17.5, 30.5));
            // Погоня по пути: враг на линии проёма, фаза переспроса «путь старый» (150 тактов) — далеко (не спасает сама).
            int guard = 0;
            while (!(sim.GetEntity(rogue).Brain.State == EnemyState.Chase && sim.GetEntity(rogue).Brain.PathStamp != 0 &&
                     sim.GetEntity(rogue).Brain.StateTicks % MeleeRogue.RepathTicks == 12))
            {
                sim.Step();
                Assert.True(++guard < 400, "враг не начал погоню");
            }

            Assert.True(sim.GetEntity(rogue).Mover.Position.X < 20 * U, "враг уже в проёме");
            // Внутрь, к западной стене южнее проёма (y 30,9: круг задевает ряд 31 — стена): точка пути — снаружи на западе.
            sim.PlaceForTests(rogue, At(21.42, 30.9));
            long searches0 = sim.PathSearches;
            int repath = -1, blocked = -1, attack = -1;
            for (int i = 1; i <= 600 && attack < 0; i++)
            {
                sim.Step();
                EnemyBrain b = sim.GetEntity(rogue).Brain;
                if (blocked < 0 && b.State == EnemyState.Blocked)
                {
                    blocked = i;
                }

                if (repath < 0 && blocked >= 0 && sim.PathSearches > searches0 && b.State == EnemyState.Chase)
                {
                    repath = i;
                }

                if (b.State == EnemyState.Attack)
                {
                    attack = i;
                }

                if (i == 180)
                {
                    _out.WriteLine($"такт 180: {sim.GetEntity(rogue).Mover.Position}, {b.State}, поисков {sim.PathSearches - searches0}");
                    Assert.True(sim.GetEntity(rogue).Mover.Position.X >= 23 * U || attack >= 0,
                        $"за 180 тактов не продвинулся: {sim.GetEntity(rogue).Mover.Position}, {b.State}");
                }
            }

            _out.WriteLine($"тонкая {thin}: упёрся на {blocked}, переспросил на {repath}, бьёт на {attack}");
            Assert.True(blocked > 0, "враг не упёрся — сценарий не проверяет переспрос");
            Assert.True(repath > 0 && repath <= 180, $"переспрос на такте {repath}");
            Assert.True(attack > 0 && attack <= 300, $"не дошёл до игрока: {attack}");
        }

        /// <summary>
        /// C-009 (замечание 2): «замкнута» + цель ушла (<c>BlockedGoalMoved</c>). Игрок в доме без входа — враг «упёрся» с кэшем.
        /// Игрок ходит по дому 3 тайла — поисков не больше одного на тайл смещения (+1). Затем без правки мира оказывается снаружи
        /// за домом (перенос) — враг переспрашивает, обходит дом и бьёт до сдачи. Мутация «BlockedGoalMoved = false» — стоит.
        /// </summary>
        [Fact]
        public void C009_GoalLeavesEnclosure_EnemyRepathsAndFollows()
        {
            var sim = new Simulation(Flat(1232));
            Ring(41, 41, 58, 58).Apply(sim); // стена на внутренних половинах, внутри x, y 21…29
            int ch = sim.AddEntity(At(23.0, 25.0));
            int rogue = sim.AddMeleeRogue(At(15.5, 25.5));
            int chSlot = sim.SlotOf(ch);
            int guard = 0;
            while (!(sim.GetEntity(rogue).Brain.State == EnemyState.Blocked && sim.GetEntity(rogue).Brain.EnclosedRev != 0))
            {
                sim.Step();
                Assert.True(++guard < 300, $"враг не «упёрся» у замкнутого дома: {sim.GetEntity(rogue).Brain.State}");
            }

            // По дому на восток 3 тайла.
            long s0 = sim.PathSearches;
            WorldPos p0 = sim.GetEntity(ch).Mover.Position;
            sim.SetInputAt(chSlot, new InputFrame(new Input8(1, 0)));
            while (sim.GetEntity(ch).Mover.Position.X < p0.X + 3 * U)
            {
                sim.Step();
                Assert.True(++guard < 600, "игрок не дошёл");
            }

            sim.SetInputAt(chSlot, InputFrame.None);
            for (int i = 0; i < 6; i++)
            {
                sim.Step();
            }

            long inside = sim.PathSearches - s0;
            _out.WriteLine($"по дому 3 тайла: поисков {inside}, враг {sim.GetEntity(rogue).Brain.State}");
            Assert.True(inside >= 1 && inside <= 4, $"поисков при ходьбе по дому {inside}");
            Assert.Equal(EnemyState.Blocked, sim.GetEntity(rogue).Brain.State);

            // Снаружи за домом (восток), без правки мира.
            sim.PlaceForTests(ch, At(33.5, 25.5));
            int attack = -1;
            for (int i = 1; i <= 450 && attack < 0; i++)
            {
                sim.Step();
                if (sim.GetEntity(rogue).Brain.State == EnemyState.Attack)
                {
                    attack = i;
                }
            }

            EnemyBrain b = sim.GetEntity(rogue).Brain;
            _out.WriteLine($"игрок снаружи: враг бьёт на такте {attack}, {sim.GetEntity(rogue).Mover.Position}, {b.State}, игнор {b.IgnoreTargetId}");
            Assert.True(attack > 0, $"враг не пошёл за вышедшим игроком: {b.State}, игнор {b.IgnoreTargetId}");
        }

        /// <summary>
        /// C-009 (замечание 5): раскачка у косяка. Подруливание выключено — враг, подходящий к проёму наискосок, упирается в косяк
        /// (целые стены); переспрос пути не помогает. Толкотня раскачивает его: каждые 40 тактов отбрасывает на 0,4 тайла назад,
        /// враг возвращается к косяку — смещение ≥ 0,19 тайла, но не ближе к цели. Должен сдаться через 20 с эпизода (+ запас),
        /// а не крутиться вечно (до C-009 любое смещение закрывало эпизод «упёрся»).
        /// </summary>
        [Fact]
        public void C009_RockedAtJamb_GivesUpIn20s()
        {
            var sim = DoorRoom(false, 1226, out int ch);
            sim.SteerOffForTests = true;
            int rogue = sim.AddMeleeRogue(At(16.5, 27.5));
            int guard = 0;
            while (sim.GetEntity(rogue).Brain.BlockedSince == 0)
            {
                sim.Step();
                Assert.True(++guard < 900, $"враг не упёрся: {sim.GetEntity(rogue).Mover.Position}, {sim.GetEntity(rogue).Brain.State}");
            }

            long since = sim.GetEntity(rogue).Brain.BlockedSince;
            long gaveUp = -1;
            for (int i = 1; i <= 1500 && gaveUp < 0; i++)
            {
                if (i % 40 == 0)
                {
                    WorldPos p = sim.GetEntity(rogue).Mover.Position;
                    sim.PlaceForTests(rogue, new WorldPos(p.X - 2 * U / 5, p.Y));
                }

                sim.Step();
                if (sim.GetEntity(rogue).Brain.IgnoreTargetId == ch)
                {
                    gaveUp = sim.Tick;
                }
            }

            _out.WriteLine($"эпизод с {since}, сдался на {gaveUp}, {sim.GetEntity(rogue).Mover.Position}");
            Assert.True(gaveUp > 0, $"раскачиваемый враг не сдался за 50 с: {sim.GetEntity(rogue).Brain.State}");
            Assert.True(gaveUp - since <= MeleeRogue.BlockedGiveUpTicks + 60, $"сдался через {gaveUp - since} тактов");
            Assert.Equal(100, sim.GetEntity(ch).Health);
        }

        /// <summary>
        /// C-009: эпизод «упёрся», начатый застреванием (не «замкнута»), закрывает продвижение по пути. Без подруливания враг стоит у
        /// косяка 18 с (переспросы не помогают); затем подруливание включают — враг проходит проём и идёт по пути к игроку дольше,
        /// чем осталось до 20 с эпизода. Пройденные точки пути закрывают эпизод — враг бьёт, а не сдаётся на ходу.
        /// Мутация «точки пути эпизод не закрывают» — сдаётся (красный).
        /// </summary>
        [Fact]
        public void C009_StuckEpisode_WalksPath_EpisodeClosed()
        {
            var sim = DoorRoom(false, 1226, out int ch);
            sim.SteerOffForTests = true;
            int rogue = sim.AddMeleeRogue(At(16.5, 27.5));
            int guard = 0;
            while (sim.GetEntity(rogue).Brain.BlockedSince == 0)
            {
                sim.Step();
                Assert.True(++guard < 900, "враг не упёрся");
            }

            long since = sim.GetEntity(rogue).Brain.BlockedSince;
            while (sim.Tick < since + 560)
            {
                sim.Step();
            }

            Assert.True(sim.GetEntity(rogue).Mover.Position.X < 20 * U, "враг прошёл проём без подруливания — сценарий не проверяет");
            sim.SteerOffForTests = false;
            long attack = -1, gaveUp = -1;
            for (int i = 0; i < 600 && attack < 0 && gaveUp < 0; i++)
            {
                sim.Step();
                if (sim.GetEntity(rogue).Brain.State == EnemyState.Attack)
                {
                    attack = sim.Tick;
                }

                if (sim.GetEntity(rogue).Brain.IgnoreTargetId == ch)
                {
                    gaveUp = sim.Tick;
                }
            }

            _out.WriteLine($"эпизод с {since}, подруливание с {since + 560}, бьёт {attack}, сдался {gaveUp}");
            Assert.True(attack > since + 600, $"сценарий не проверяет (бьёт {attack} до конца 20 с эпизода)");
            Assert.True(gaveUp < 0, $"сдался на ходу по пути: {gaveUp}");
        }

        /// <summary>
        /// C-009, проверка (П) — длинный обход после «упёрся» (`docs/02` 9.3: «путь появился → Преследование»). Игрок в длинной
        /// узкой комнате (тайлы x 31…33, y 10…50, целые стены), враг снаружи вплотную к западной стене напротив. wait &lt; 0 —
        /// проём (30, 11) открыт с начала (обычный обход); иначе комната замкнута, враг «упёрся», через wait тактов (0/5/10/15/
        /// ~20 с) проём открывают правкой мира — обход ~40 т (сначала на север, прямое расстояние растёт). Враг доходит, а не
        /// сдаётся посреди обхода. Мутация «эпизод закрывает только приближение по прямой» (первая редакция C-009) — красный
        /// при 450. 590, а не 600: на 600 тактах эпизода враг уже сдался (правило 20 с) — до открытия.
        /// </summary>
        [Theory]
        [InlineData(-1, 51)]
        [InlineData(0, 51)]
        [InlineData(150, 51)]
        [InlineData(300, 51)]
        [InlineData(450, 51)]
        [InlineData(590, 51)]
        [InlineData(-1, 41)]
        [InlineData(300, 41)]
        public void C009_LongDetourAfterBlocked_PathAppears_Reaches(int wait, int south)
        {
            var sim = new Simulation(Flat(4242));
            void Wall(int x, int y) =>
                Assert.True(sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, ObjectId.StoneWall, BuildRecord.OfPlayer(1, FloorId.Grass)));
            int y0 = 9, y1 = south;
            for (int y = y0; y <= y1; y++)
            {
                if (!(wait < 0 && y == 11))
                {
                    Wall(30, y);
                }

                Wall(34, y);
            }

            for (int x = 31; x <= 33; x++)
            {
                Wall(x, y0);
                Wall(x, y1);
            }

            sim.Step();
            int ch = sim.AddEntity(At(32.5, 30.5));
            int rogue = sim.AddMeleeRogue(At(29.45, 30.5));
            long since = 0;
            if (wait >= 0)
            {
                int g = 0;
                while (sim.GetEntity(rogue).Brain.BlockedSince == 0)
                {
                    sim.Step();
                    Assert.True(++g < 600, "не упёрся");
                }

                since = sim.GetEntity(rogue).Brain.BlockedSince;
                for (int i = 0; i < wait; i++)
                {
                    sim.Step();
                }

                Assert.True(sim.GetEntity(rogue).Brain.IgnoreTargetId != ch, "сдался до открытия проёма");
                Assert.True(sim.RequestEdit(new TileCoord(30, 11), FloorId.Grass, ObjectId.Empty));
            }

            long open = sim.Tick;
            long attack = -1, gaveUp = -1;
            for (int i = 0; i < 1500 && attack < 0 && gaveUp < 0; i++)
            {
                sim.Step();
                EnemyBrain b = sim.GetEntity(rogue).Brain;
                if (b.State == EnemyState.Attack)
                {
                    attack = sim.Tick;
                }

                if (b.IgnoreTargetId == ch)
                {
                    gaveUp = sim.Tick;
                }
            }

            _out.WriteLine($"wait {wait} south {south}: эпизод с {since}, проём на {open}, бьёт {attack}, сдался {gaveUp}, {sim.GetEntity(rogue).Mover.Position}");
            Assert.True(attack > 0, $"враг сдался/не дошёл при существующем пути: сдался {gaveUp}, эпизод с {since}, проём {open}");
            Assert.True(attack - open <= 300, $"обход {attack - open} тактов");
        }

        /// <summary>
        /// C-021 (хвост П C-009, замечание 1): порог <see cref="MeleeRogue.EpisodeClosePathPoints"/> = 4 обоснован тестом.
        /// Раскачиваемый у косяка враг с переспросом пути (толкотня + штатный цикл Blocked «застрял» → Chase → новый запрос)
        /// проходит за цикл 1–2 точки нового пути — меньше порога, эпизод не закрыт, сдача за 20 с. В П мутация «порог 1
        /// вместо 4» выжила: в старом тесте «RockedAtJamb» враг не проходил ни одной точки (переспроса не было).
        /// Мутация «1» — эпизод закрывается на каждой точке, сдачи нет — красный.
        /// </summary>
        [Fact]
        public void C021_RockedWithRepaths_Passes1_2PathPoints_GivesUpIn20s()
        {
            var sim = DoorRoom(false, 1226, out int ch);
            sim.SteerOffForTests = true;
            int rogue = sim.AddMeleeRogue(At(16.5, 27.5));
            int guard = 0;
            while (sim.GetEntity(rogue).Brain.BlockedSince == 0)
            {
                sim.Step();
                Assert.True(++guard < 900, "враг не упёрся");
            }

            long since = sim.GetEntity(rogue).Brain.BlockedSince;
            int maxPathPos = 0;
            long gaveUp = -1;
            for (int i = 1; i <= 1500 && gaveUp < 0; i++)
            {
                if (i % 40 == 0)
                {
                    WorldPos p = sim.GetEntity(rogue).Mover.Position;
                    sim.PlaceForTests(rogue, new WorldPos(p.X - 4 * U / 5, p.Y));
                }

                sim.Step();
                maxPathPos = Math.Max(maxPathPos, sim.GetEntity(rogue).Brain.PathPos);
                if (sim.GetEntity(rogue).Brain.IgnoreTargetId == ch)
                {
                    gaveUp = sim.Tick;
                }
            }

            _out.WriteLine($"эпизод с {since}, сдался на {gaveUp}, макс. точек пути {maxPathPos}");
            Assert.True(maxPathPos >= 1, $"враг не прошёл ни одной точки пути (макс {maxPathPos}) — порог 1 не проверяется");
            Assert.True(maxPathPos < MeleeRogue.EpisodeClosePathPoints,
                $"пройдено {maxPathPos} точек — на пороге {MeleeRogue.EpisodeClosePathPoints}: сценарий не про порог");
            Assert.True(gaveUp > 0, "раскачиваемый враг (1–2 точки за цикл) не сдался за 50 с — порог 4 не отличается от 1");
            Assert.True(gaveUp - since <= MeleeRogue.BlockedGiveUpTicks + 120, $"сдался через {gaveUp - since} тактов");
        }

        /// <summary>
        /// C-021 (хвост П C-009): враг, циклически проходящий ≥ 4 точки пути, не сдаётся. Цикл: толкотня у косяка при
        /// выключенном подруливании — открывается эпизод «застрял»; отпуск подруливания + отброс на 3 тайла запад проёма —
        /// враг идёт по пути и проходит ≥ 4 точки, точки закрывают эпизод; возврат к косяку — новый эпизод. Шесть циклов
        /// (суммарно больше 20 с): каждый эпизод закрыт точками, сдачи нет. Мутация «эпизод закрывает только путь
        /// появился» (без точек пути) — первый эпизод длится 600 тактов, сдача — красный.
        /// </summary>
        [Fact]
        public void C021_CyclicPathPoints_EpisodesKeepClosing_NoGiveUp()
        {
            var sim = DoorRoom(false, 1226, out int ch);
            int rogue = sim.AddMeleeRogue(At(16.5, 27.5));
            int closed = 0;
            long gaveUp = -1;
            for (int cycle = 0; cycle < 6 && gaveUp < 0; cycle++)
            {
                // Эпизод открывается у косяка: подруливание выключено, враг сам упинается (как в RockedAtJamb).
                sim.SteerOffForTests = true;
                sim.PlaceForTests(rogue, At(16.5, 27.5));
                int guard = 0;
                while (sim.GetEntity(rogue).Brain.BlockedSince == 0)
                {
                    sim.Step();
                    Assert.True(++guard < 700, $"эпизод не открылся в цикле {cycle}: {sim.GetEntity(rogue).Brain.State}");
                }

                // Проход по пути: отброс на 3 тайла запад проёма, подруливание включено — точки закрывают эпизод.
                sim.SteerOffForTests = false;
                sim.PlaceForTests(rogue, At(17.0, 30.5));
                guard = 0;
                while (sim.GetEntity(rogue).Brain.BlockedSince != 0)
                {
                    sim.Step();
                    Assert.True(++guard < 90, $"эпизод не закрыт точками пути в цикле {cycle} — враг идёт по пути");
                }

                closed++;
                for (int i = 0; i < 40; i++)
                {
                    sim.Step();
                    if (sim.GetEntity(rogue).Brain.IgnoreTargetId == ch)
                    {
                        gaveUp = sim.Tick;
                    }
                }
            }

            _out.WriteLine($"эпизодов закрыто {closed}, сдался {gaveUp}, состояние {sim.GetEntity(rogue).Brain.State}");
            Assert.True(gaveUp < 0, $"циклический враг (≥ 4 точки за цикл) сдался на {gaveUp}");
            Assert.True(closed >= 4, $"закрыто эпизодов {closed} — не цикл");
        }

        /// <summary>
        /// C-009 (замечание 4): подруливание у диагонали. Мутация «у диагонали не подруливать» выжила у проверяющего — она
        /// эквивалентна: столкновения раздельные по осям (сначала X, потом Y) и не откатывают за старт, так что диагональный
        /// шаг не приближает, только если оба его осевых сдвига нулевые, — тогда и чисто осевые шаги стоят. Ветка удалена;
        /// тест держит это свойство (случайные постройки ячейками и целыми тайлами, точки дальше прохода 1/4 тайла): если
        /// столкновения изменятся и ось начнёт помогать — красный, ветку надо вернуть. И прямой ход подруливание спасает.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void C009_Steer_DiagonalBlocked_AxesBlockedToo(bool fullTiles)
        {
            var rnd = new Random(fullTiles ? 92 : 91);
            long samples = 0, diagFail = 0, axisHelps = 0, straightFail = 0, straightHelped = 0;
            for (int w = 0; w < (fullTiles ? 60 : 24); w++)
            {
                var sim = new Simulation(Flat((ulong)(2100 + w)));
                var plan = new Plan();
                for (int cy = 80; cy < 100; cy += fullTiles ? 2 : 1)
                {
                    for (int cx = 80; cx < 100; cx += fullTiles ? 2 : 1)
                    {
                        if (rnd.Next(100) < (fullTiles ? 30 : 22))
                        {
                            plan.Cell(cx, cy);
                            if (fullTiles)
                            {
                                plan.Cell(cx + 1, cy);
                                plan.Cell(cx, cy + 1);
                                plan.Cell(cx + 1, cy + 1);
                            }
                        }
                    }
                }

                plan.Apply(sim);
                for (int k = 0; k < 25000; k++)
                {
                    var at = new WorldPos(41 * U + rnd.Next(8 * U), 41 * U + rnd.Next(8 * U));
                    if (sim.TouchesSolidForTests(at))
                    {
                        continue;
                    }

                    var node = new WorldPos(at.X + rnd.Next(-U, U + 1), at.Y + rnd.Next(-U, U + 1));
                    Int2 d = Torus.Delta(sim.Shape, at, node);
                    Input8 q = Input8.FromStick(d.X, d.Y);
                    if (!q.IsMoving || d.LengthSquared <= MeleeRogue.PathPointPass2)
                    {
                        continue; // ближе прохода точки FollowPath к ней не ведёт
                    }

                    samples++;
                    bool C(Input8 dir) => sim.Closer(at, MeleeRogue.ChaseSpeedX10, dir, node, d.LengthSquared);
                    if (C(q))
                    {
                        continue;
                    }

                    if (q.IsDiagonal)
                    {
                        diagFail++;
                        axisHelps += C(new Input8(q.DX, 0)) || C(new Input8(0, q.DY)) ? 1 : 0;
                    }
                    else
                    {
                        straightFail++;
                        Input8 s = sim.SteerTo(at, MeleeRogue.ChaseSpeedX10, node);
                        straightHelped += s != q && C(s) ? 1 : 0;
                    }
                }
            }

            _out.WriteLine($"целые тайлы {fullTiles}: выборка {samples}; диагональ упёрлась {diagFail}, ось помогла бы {axisHelps}; " +
                $"прямой упёрся {straightFail}, подруливание спасло {straightHelped}");
            Assert.True(diagFail >= 50, $"мало упоров диагонали ({diagFail}) — выборка не проверяет свойство");
            Assert.Equal(0, axisHelps);
            Assert.True(straightHelped * 2 >= straightFail, $"подруливание прямого спасло {straightHelped} из {straightFail}");
        }

        private void AssertAllReach(Simulation sim, int ch, int[] rogues, int ticks)
        {
            var reached = new bool[rogues.Length];
            for (int i = 0; i < ticks; i++)
            {
                sim.Step();
                for (int k = 0; k < rogues.Length; k++)
                {
                    long d2 = Torus.Dist2(sim.Shape, sim.GetEntity(rogues[k]).Mover.Position, sim.GetEntity(ch).Mover.Position);
                    reached[k] |= d2 <= MeleeRogue.StrikeRange2 && sim.GetEntity(rogues[k]).Brain.State == EnemyState.Attack;
                }
            }

            for (int k = 0; k < rogues.Length; k++)
            {
                SimEntity e = sim.GetEntity(rogues[k]);
                _out.WriteLine($"враг {k}: дошёл {reached[k]}, {e.Mover.Position}, {e.Brain.State}");
            }

            for (int k = 0; k < rogues.Length; k++)
            {
                Assert.True(reached[k], $"враг {k} не дошёл до игрока за {ticks} тактов: {sim.GetEntity(rogues[k]).Mover.Position}, {sim.GetEntity(rogues[k]).Brain.State}");
            }

            Assert.True(sim.GetEntity(ch).Health < 100, "игрока не ударили");
        }
    }
}
