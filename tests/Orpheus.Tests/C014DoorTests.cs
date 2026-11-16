using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-014 (слово Автора 15.11: «Сразу через пол секунды как в их хитбоксе никого нет»): дверь закрывается
    /// через <see cref="Simulation.DoorAutoCloseTicks"/> = 15 тактов после того, как её хитбокс — ячейки двери
    /// на сетке 0,5 (тонкая дверь E2-12), а не тайл с соседями — никто не пересекает. Тонкая дверь — южная пара
    /// ячеек тайла (110, 100): y ∈ [128, 256) внутри тайла.
    /// </summary>
    public class C014DoorTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const int DoorX = 110;
        private const int DoorY = 100;
        private const int CloseTicks = 15; // 0,5 с — слово Автора; число, а не константа ядра: тест ловит её смену
        private static readonly TileCoord Door = new TileCoord(DoorX, DoorY);

        private static WorldModel Flat() => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);

        private static WorldPos In(int tx, int ty, int sx, int sy) => new WorldPos(tx * U + sx, ty * U + sy);

        private static Simulation ThinDoor(bool open)
        {
            var sim = new Simulation(Flat());
            BuildRecord rec = BuildRecord.OfPlayer(1, FloorId.Grass).WithCells(BuildCells.DoorSouth);
            if (open)
            {
                rec = rec.WithFlags(BuildFlags.DoorOpen);
            }

            Assert.True(sim.RequestEdit(Door, FloorId.Grass, ObjectId.Door, rec));
            sim.Step();
            Assert.Equal(ObjectId.Door, sim.World.GetObject(Door));
            Assert.Equal(BuildCells.DoorSouth, BuildCells.MaskAt(sim.World, Door));
            return sim;
        }

        private static bool IsOpen(Simulation sim)
        {
            Assert.True(sim.World.TryGetBuild(Door, out BuildRecord rec), "дверь без записи");
            return rec.IsDoorOpen;
        }

        private static bool InHitbox(Simulation sim, int id) =>
            Collision.CircleOverlapsCells(sim.World.Shape, sim.Entities[sim.SlotOf(id)].Mover.Position, Door, BuildCells.DoorSouth);

        /// <summary>Шагов до закрытия (не больше <paramref name="max"/>; не закрылась — −1).</summary>
        private static int StepsUntilClosed(Simulation sim, int max)
        {
            for (int t = 1; t <= max; t++)
            {
                sim.Step();
                if (!IsOpen(sim))
                {
                    return t;
                }
            }

            return -1;
        }

        /// <summary>Проход сквозь тонкую дверь с севера на юг: открылась шагом, закрылась ровно через 15 тактов
        /// после такта, когда круг персонажа вышел из её ячеек (а не из тайла с соседями).</summary>
        [Fact]
        public void C014_PassThrough_Closes_15_Ticks_After_Leaving_Hitbox()
        {
            Simulation sim = ThinDoor(open: false);
            int me = sim.AddEntity(In(DoorX, DoorY - 3, U / 2, U / 2));
            sim.SetInput(me, new InputFrame(Input8.South));

            bool wasIn = false;
            int freeStep = -1;
            int closedStep = -1;
            for (int t = 1; t <= 200 && closedStep < 0; t++)
            {
                sim.Step();
                bool inside = InHitbox(sim, me);
                if (inside)
                {
                    wasIn = true;
                    freeStep = -1;
                    Assert.True(IsOpen(sim), "закрылась под персонажем");
                }
                else if (wasIn && freeStep < 0)
                {
                    freeStep = t;
                    sim.SetInput(me, InputFrame.None); // вышел — встаёт вплотную к проёму с юга
                }

                if (wasIn && !IsOpen(sim))
                {
                    closedStep = t;
                }
            }

            Assert.True(wasIn, "не прошёл через дверь");
            Assert.True(freeStep > 0, "не вышел из хитбокса");
            Assert.Equal(CloseTicks, closedStep - freeStep);
            Assert.True(Torus.TileOf(sim.World.Shape, sim.Entities[sim.SlotOf(me)].Mover.Position).Y >= DoorY + 1, "застрял в двери");
        }

        /// <summary>Стоит в той же клетке-тайле, но в северной половине (вне ячеек двери), вплотную к проёму —
        /// дверь закрывается через 15 тактов и не открывается снова от стояния (нет «открыть — закрыть» по кругу).</summary>
        [Fact]
        public void C014_Beside_In_Same_Tile_Outside_Hitbox_Closes_And_Stays_Closed()
        {
            Simulation sim = ThinDoor(open: true);
            int me = sim.AddEntity(In(DoorX, DoorY, U / 2, 20)); // круг до y = 122 < 128 — вне ячеек
            Assert.False(InHitbox(sim, me));

            int steps = StepsUntilClosed(sim, 3 * Simulation.DoorAutoCloseTicks);
            Assert.True(steps > 0 && steps <= CloseTicks + 1, $"закрылась за {steps}");

            for (int t = 0; t < 4 * Simulation.DoorAutoCloseTicks; t++)
            {
                sim.Step();
                Assert.False(IsOpen(sim), "открылась от стояния рядом");
            }
        }

        /// <summary>Из северной половины тайла двери шаг на юг в закрытую дверь — открывает, проход сквозь.</summary>
        [Fact]
        public void C014_Step_From_Same_Tile_Opens_Closed_Thin_Door()
        {
            Simulation sim = ThinDoor(open: false);
            int me = sim.AddEntity(In(DoorX, DoorY, U / 2, 20));
            sim.SetInput(me, new InputFrame(Input8.South));
            for (int t = 0; t < 10 && !IsOpen(sim); t++)
            {
                sim.Step();
            }

            Assert.True(IsOpen(sim), "шаг к двери из её тайла не открыл");
            for (int t = 0; t < 20; t++)
            {
                sim.Step();
            }

            Assert.True(Torus.TileOf(sim.World.Shape, sim.Entities[sim.SlotOf(me)].Mover.Position).Y >= DoorY + 1, "не прошёл");
        }

        /// <summary>Стоит в проёме (в ячейках двери) — дверь открыта сколько угодно; то же — враг в проёме.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void C014_Someone_In_Hitbox_Stays_Open(bool enemy)
        {
            Simulation sim = ThinDoor(open: true);
            WorldPos p = In(DoorX, DoorY, U / 2, 3 * U / 4);
            int id = enemy ? sim.AddEnemy(p) : sim.AddEntity(p);
            Assert.True(InHitbox(sim, id));

            Assert.Equal(-1, StepsUntilClosed(sim, 4 * Simulation.DoorAutoCloseTicks));
        }

        /// <summary>Касание краем: круг заходит в ячейки двери на 1 unit — таймер держится; отходит на 1 unit дальше
        /// касания — закрывается.</summary>
        [Fact]
        public void C014_Hitbox_Edge_Is_Exact()
        {
            Simulation sim = ThinDoor(open: true);
            // Над проёмом: центр круга на y = 128 − R (касание без пересечения) — вне; на 1 ближе — внутри.
            int me = sim.AddEnemy(In(DoorX, DoorY, U / 2, U / 2 - Collision.RadiusUnits + 1));
            Assert.True(InHitbox(sim, me));
            Assert.Equal(-1, StepsUntilClosed(sim, 3 * Simulation.DoorAutoCloseTicks));

            Simulation sim2 = ThinDoor(open: true);
            int me2 = sim2.AddEnemy(In(DoorX, DoorY, U / 2, U / 2 - Collision.RadiusUnits));
            Assert.False(InHitbox(sim2, me2));
            Assert.True(StepsUntilClosed(sim2, 3 * Simulation.DoorAutoCloseTicks) > 0, "касание без пересечения держит дверь");
        }

        // ── C-019 (хвост C-014): те же правила для всех 4 ориентаций тонкой двери ──────────────────────

        /// <summary>Дверь с маской ячеек <paramref name="cells"/> (повороты 0/90/180/270 — <see cref="BuildCells.DoorNorth"/>,
        /// <see cref="BuildCells.DoorEast"/>, <see cref="BuildCells.DoorSouth"/>, <see cref="BuildCells.DoorWest"/>).</summary>
        private static Simulation ThinDoor(byte cells, bool open)
        {
            var sim = new Simulation(Flat());
            BuildRecord rec = BuildRecord.OfPlayer(1, FloorId.Grass).WithCells(cells);
            if (open)
            {
                rec = rec.WithFlags(BuildFlags.DoorOpen);
            }

            Assert.True(sim.RequestEdit(Door, FloorId.Grass, ObjectId.Door, rec));
            sim.Step();
            Assert.Equal(ObjectId.Door, sim.World.GetObject(Door));
            Assert.Equal(cells, BuildCells.MaskAt(sim.World, Door));
            return sim;
        }

        private static bool InCells(Simulation sim, int id, byte cells) =>
            Collision.CircleOverlapsCells(sim.World.Shape, sim.Entities[sim.SlotOf(id)].Mover.Position, Door, cells);

        /// <summary>Направление прохода сквозь дверь: вход с той стороны, куда дверь не смотрит, выход — с её стороны.</summary>
        private static Input8 WalkThrough(byte cells) => cells switch
        {
            BuildCells.DoorNorth => Input8.North,
            BuildCells.DoorSouth => Input8.South,
            BuildCells.DoorWest => Input8.West,
            _ => Input8.East, // DoorEast
        };

        /// <summary>Старт за 3 тайла по линии прохода (с той стороны, куда дверь не смотрит).</summary>
        private static WorldPos ApproachStart(byte cells) => cells switch
        {
            BuildCells.DoorNorth => In(DoorX, DoorY + 3, U / 2, U / 2),
            BuildCells.DoorSouth => In(DoorX, DoorY - 3, U / 2, U / 2),
            BuildCells.DoorWest => In(DoorX + 3, DoorY, U / 2, U / 2),
            _ => In(DoorX - 3, DoorY, U / 2, U / 2), // DoorEast
        };

        /// <summary>Ушёл на сторону прохода (вышел за границу тайла двери по линии прохода).</summary>
        private static bool PastDoor(Simulation sim, byte cells, WorldPos p)
        {
            TileCoord t = Torus.TileOf(sim.World.Shape, p);
            return cells switch
            {
                BuildCells.DoorNorth => t.Y <= DoorY - 1,
                BuildCells.DoorSouth => t.Y >= DoorY + 1,
                BuildCells.DoorWest => t.X <= DoorX - 1,
                _ => t.X >= DoorX + 1, // DoorEast
            };
        }

        /// <summary>Та же клетка-тайл, противоположная половине (ровно вне ячеек двери: круг R=102 в 20 unit от края
        /// половины не достраивает до 128).</summary>
        private static WorldPos OppositeHalf(byte cells) => cells switch
        {
            BuildCells.DoorNorth => In(DoorX, DoorY, U / 2, U - 20),
            BuildCells.DoorSouth => In(DoorX, DoorY, U / 2, 20),
            BuildCells.DoorWest => In(DoorX, DoorY, U - 20, U / 2),
            _ => In(DoorX, DoorY, 20, U / 2), // DoorEast
        };

        /// <summary>Центр ячеек двери = центр её хитбокса (<see cref="BuildCells"/>: сторона ячейки — 128 unit).</summary>
        private static WorldPos CellCenter(byte cells) => cells switch
        {
            BuildCells.DoorNorth => In(DoorX, DoorY, U / 2, U / 4),
            BuildCells.DoorSouth => In(DoorX, DoorY, U / 2, 3 * U / 4),
            BuildCells.DoorWest => In(DoorX, DoorY, U / 4, U / 2),
            _ => In(DoorX, DoorY, 3 * U / 4, U / 2), // DoorEast
        };

        /// <summary>
        /// C-019 (хвост C-014), все 4 ориентации: проход сквозь дверь — закрылась ровно через 15 тактов после такта,
        /// когда круг вышел из её ячеек.
        /// </summary>
        [Theory]
        [InlineData(BuildCells.DoorNorth)]
        [InlineData(BuildCells.DoorEast)]
        [InlineData(BuildCells.DoorSouth)]
        [InlineData(BuildCells.DoorWest)]
        public void C019_Pass_Through_All_Orientations_Closes_15_Ticks_After_Hitbox(byte cells)
        {
            Simulation sim = ThinDoor(cells, open: false);
            int me = sim.AddEntity(ApproachStart(cells));
            sim.SetInput(me, new InputFrame(WalkThrough(cells)));

            bool wasIn = false;
            int freeStep = -1;
            int closedStep = -1;
            for (int t = 1; t <= 200 && closedStep < 0; t++)
            {
                sim.Step();
                if (InCells(sim, me, cells))
                {
                    wasIn = true;
                    freeStep = -1;
                    Assert.True(IsOpen(sim), "закрылась под персонажем");
                }
                else if (wasIn && freeStep < 0)
                {
                    freeStep = t;
                    sim.SetInput(me, InputFrame.None); // вышел — стоит вплотную с выходной стороны
                }

                if (wasIn && !IsOpen(sim))
                {
                    closedStep = t;
                }
            }

            Assert.True(wasIn, $"не прошёл через дверь ({cells})");
            Assert.True(freeStep > 0, "не вышел из хитбокса");
            Assert.Equal(CloseTicks, closedStep - freeStep);
            Assert.True(PastDoor(sim, cells, sim.Entities[sim.SlotOf(me)].Mover.Position), "застрял в двери");
        }

        /// <summary>
        /// C-019 (хвост C-014), все 4 ориентации: стоит в противоположной половине тайла двери (вне её ячеек) —
        /// закрылась через 15 тактов и от стояния не открывается.
        /// </summary>
        [Theory]
        [InlineData(BuildCells.DoorNorth)]
        [InlineData(BuildCells.DoorEast)]
        [InlineData(BuildCells.DoorSouth)]
        [InlineData(BuildCells.DoorWest)]
        public void C019_Opposite_Half_All_Orientations_Closes_And_Stays_Closed(byte cells)
        {
            Simulation sim = ThinDoor(cells, open: true);
            int me = sim.AddEntity(OppositeHalf(cells));
            Assert.False(InCells(sim, me, cells), "стоячий в противоположной половине — в хитбоксе двери");

            int steps = StepsUntilClosed(sim, 3 * Simulation.DoorAutoCloseTicks);
            Assert.True(steps > 0 && steps <= CloseTicks + 1, $"закрылась за {steps}");

            for (int t = 0; t < 4 * Simulation.DoorAutoCloseTicks; t++)
            {
                sim.Step();
                Assert.False(IsOpen(sim), "открылась от стояния в противоположной половине");
            }
        }

        /// <summary>C-019 (хвост C-014), все 4 ориентации: персонаж в проёме (в ячейках двери) — дверь открыта
        /// сколько угодно; то же — враг в проёме.</summary>
        [Theory]
        [InlineData(BuildCells.DoorNorth, false)]
        [InlineData(BuildCells.DoorNorth, true)]
        [InlineData(BuildCells.DoorEast, false)]
        [InlineData(BuildCells.DoorEast, true)]
        [InlineData(BuildCells.DoorSouth, false)]
        [InlineData(BuildCells.DoorSouth, true)]
        [InlineData(BuildCells.DoorWest, false)]
        [InlineData(BuildCells.DoorWest, true)]
        public void C019_In_Cells_All_Orientations_Stays_Open(byte cells, bool enemy)
        {
            Simulation sim = ThinDoor(cells, open: true);
            int id = enemy ? sim.AddEnemy(CellCenter(cells)) : sim.AddEntity(CellCenter(cells));
            Assert.True(InCells(sim, id, cells), "проём не покрыл ячейки двери");

            Assert.Equal(-1, StepsUntilClosed(sim, 4 * Simulation.DoorAutoCloseTicks));
        }

        /// <summary>Позиция «шага» для проверки центра хитбокса: боковой соседний тайл, в стороне от ячеек
        /// двери (круг R=102 до них не достраивает). Скаляр смещения такта к центру ТАЙЛА &gt; 0, к центру ЯЧЕЕК
        /// двери ≤ 0 (скаляр в коде считается от позиции после движения — числа подобраны под него: ±32 в первом такте).</summary>
        private static WorldPos StepSpot(byte cells) => cells switch
        {
            BuildCells.DoorNorth => In(DoorX + 1, DoorY, 84, U - 20),
            BuildCells.DoorSouth => In(DoorX + 1, DoorY, 84, 20),
            BuildCells.DoorEast => In(DoorX, DoorY + 1, 20, 84),
            _ => In(DoorX, DoorY + 1, U - 20, 84), // DoorWest
        };

        /// <summary>Диагональ «к центру тайла, прочь от центра ячеек» и диагональ «к центру ячеек».</summary>
        private static (Input8 AwayFromCells, Input8 ToCells) StepDirections(byte cells) => cells switch
        {
            BuildCells.DoorNorth => (Input8.SouthWest, Input8.NorthWest),
            BuildCells.DoorSouth => (Input8.NorthWest, Input8.SouthWest),
            BuildCells.DoorEast => (Input8.NorthWest, Input8.NorthEast),
            _ => (Input8.NorthEast, Input8.NorthWest), // DoorWest
        };

        /// <summary>
        /// C-019 (хвост C-014): «шагнул к двери» меряется к ЦЕНТРУ ХИТБОКСА (центру ячеек двери), а не к центру тайла.
        /// Персонаж в соседнем тайле, у края тайла двери: диагональ, у которой скаляр смещения к центру тайла &gt; 0,
        /// а к центру ячеек двери ≤ 0, закрытую дверь НЕ открывает (открыла бы — значит, целиться идёт в центр тайла).
        /// Рядом — контроль тем же стартом: диагональ к центру ячеек — открывает.
        /// </summary>
        [Theory]
        [InlineData(BuildCells.DoorNorth)]
        [InlineData(BuildCells.DoorEast)]
        [InlineData(BuildCells.DoorSouth)]
        [InlineData(BuildCells.DoorWest)]
        public void C019_Step_Toward_Door_Uses_Hitbox_Center_Not_Tile_Center(byte cells)
        {
            (Input8 away, Input8 to) = StepDirections(cells);

            // Шаг «к центру тайла», прочь от центра ячеек: дверь закрыта все такты (и в первом такте, где разница центров видна).
            Simulation sim = ThinDoor(cells, open: false);
            int me = sim.AddEntity(StepSpot(cells));
            Assert.False(InCells(sim, me, cells), "старт уже в хитбоксе двери");
            sim.SetInput(me, new InputFrame(away));
            for (int t = 0; t < 4; t++)
            {
                sim.Step();
                Assert.False(IsOpen(sim), $"шаг «к центру тайла» открыл дверь ({cells}, такт {t})");
            }

            // Контроль: тот же старт, шаг к центру ячеек — открывает.
            Simulation sim2 = ThinDoor(cells, open: false);
            int me2 = sim2.AddEntity(StepSpot(cells));
            sim2.SetInput(me2, new InputFrame(to));
            bool opened = false;
            for (int t = 0; t < 12 && !opened; t++)
            {
                sim2.Step();
                opened = IsOpen(sim2);
            }

            Assert.True(opened, $"шаг к центру ячеек не открыл дверь ({cells})");
        }
    }
}
