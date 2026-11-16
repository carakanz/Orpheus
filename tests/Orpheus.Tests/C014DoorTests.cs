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

        private static Simulation ThinDoor(bool open) => ThinDoor(BuildCells.DoorSouth, open);

        private static Simulation ThinDoor(byte mask, bool open)
        {
            var sim = new Simulation(Flat());
            BuildRecord rec = BuildRecord.OfPlayer(1, FloorId.Grass).WithCells(mask);
            if (open)
            {
                rec = rec.WithFlags(BuildFlags.DoorOpen);
            }

            Assert.True(sim.RequestEdit(Door, FloorId.Grass, ObjectId.Door, rec));
            sim.Step();
            Assert.Equal(ObjectId.Door, sim.World.GetObject(Door));
            Assert.Equal(mask, BuildCells.MaskAt(sim.World, Door));
            return sim;
        }

        private static bool IsOpen(Simulation sim)
        {
            Assert.True(sim.World.TryGetBuild(Door, out BuildRecord rec), "дверь без записи");
            return rec.IsDoorOpen;
        }

        private static bool InHitbox(Simulation sim, int id) => InHitbox(sim, id, BuildCells.DoorSouth);

        private static bool InHitbox(Simulation sim, int id, byte mask) =>
            Collision.CircleOverlapsCells(sim.World.Shape, sim.Entities[sim.SlotOf(id)].Mover.Position, Door, mask);

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

        /// <summary>Ориентация (C-020, хвост П C-014): для каждой из четырёх масок тонкой двери существо в её
        /// ячейках держит дверь открытой; стоящий в противоположной половине тайла (вне ячеек) — не держит:
        /// закрылась за ≤ 16 тактов и остаётся закрытой. Ловит мутацию «ориентация игнорируется (всегда южная
        /// пара)»: под ней на не-южных масках стоящий вне ячеек «занимает» хитбокс и дверь не закрывается.</summary>
        [Theory]
        [InlineData(BuildCells.DoorNorth, 128, 64, 128, 236)]   // круг в северной паре; юг тайла — вне (y − 102 = 134 ≥ 128)
        [InlineData(BuildCells.DoorSouth, 128, 192, 128, 20)]   // южная пара; север тайла — вне (y + 102 = 122 < 128)
        [InlineData(BuildCells.DoorWest, 64, 128, 236, 128)]    // западная колонка; восток тайла — вне (x − 102 = 134 ≥ 128)
        [InlineData(BuildCells.DoorEast, 192, 128, 20, 128)]    // восточная колонка; запад тайла — вне (x + 102 = 122 < 128)
        public void C020_Orientation_In_Cells_Holds_Open_Opposite_Half_Closes(byte mask, int inX, int inY, int outX, int outY)
        {
            Simulation sim = ThinDoor(mask, open: true);
            int me = sim.AddEntity(In(DoorX, DoorY, inX, inY));
            Assert.True(InHitbox(sim, me, mask), $"в ячейках маски {mask}?");
            Assert.Equal(-1, StepsUntilClosed(sim, 4 * Simulation.DoorAutoCloseTicks));

            Simulation sim2 = ThinDoor(mask, open: true);
            int me2 = sim2.AddEntity(In(DoorX, DoorY, outX, outY));
            Assert.False(InHitbox(sim2, me2, mask), $"вне ячеек маски {mask}?");
            int steps = StepsUntilClosed(sim2, 3 * Simulation.DoorAutoCloseTicks);
            Assert.True(steps > 0 && steps <= CloseTicks + 1, $"закрылась за {steps} (маска {mask})");
            for (int t = 0; t < 4 * Simulation.DoorAutoCloseTicks; t++)
            {
                sim2.Step();
                Assert.False(IsOpen(sim2), $"открылась от стояния в противоположной половине (маска {mask})");
            }
        }

        /// <summary>Открытие от соседнего тайла считает направление к <b>центру ячеек</b> двери, а не к центру
        /// тайла (C-020, хвост П C-014; покрывает `DoorHitboxCenter`): южная дверь (центр ячеек y = 192),
        /// западный сосед (круг до проёма не дотягивается: dx = 128 &gt; 102) шагает на юг. Стартовав с y = 136
        /// и шагнув ≈ 51 unit (y = 187), существо смещается к центру ячеек (+5) и от центра тайла (−59):
        /// под мутацией «центр хитбокса = центр тайла» дверь остаётся закрытой.</summary>
        [Fact]
        public void C020_Open_From_Neighbor_Uses_Hitbox_Center()
        {
            Simulation sim = ThinDoor(BuildCells.DoorSouth, open: false);
            int me = sim.AddEntity(In(DoorX - 1, DoorY, U / 2, 136));
            Assert.False(InHitbox(sim, me), "круг уже в проёме");
            sim.SetInput(me, new InputFrame(Input8.South));

            int openedAt = -1;
            for (int t = 1; t <= 10 && openedAt < 0; t++)
            {
                sim.Step();
                if (IsOpen(sim))
                {
                    openedAt = t;
                }
            }

            Assert.True(openedAt > 0, "шаг на юг из западного тайла не открыл дверь — скаляр считается к центру тайла, а не к центру ячеек");
        }
    }
}
