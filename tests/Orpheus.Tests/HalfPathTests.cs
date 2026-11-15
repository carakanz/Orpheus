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
            var sim = new Simulation(Flat(1226));
            int ch = sim.AddEntity(At(30.5, 30.5));
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
