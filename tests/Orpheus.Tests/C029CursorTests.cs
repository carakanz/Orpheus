using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-029 (приёмка 17.11, BRIEF 11n): «Предметы почему-то ставятся на клетку ниже чем я прошу», «верстак почему-то не
    /// сносится». Причина — щелчок брал простой тайл под точкой, а призрак — СЗ ячейку мебели от узла 0,5 (и якорь постройки
    /// под курсором). Теперь цель одна — <see cref="BuildCursor.Target"/>.
    /// </summary>
    public sealed class C029CursorTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat() => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 2929);

        private static WorldPos At(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        private static int Me(Simulation sim, int x, int y, params (byte Item, int N)[] items)
        {
            int me = sim.AddEntity(At(x, y));
            var inv = sim.GetEntity(me).Inv;
            foreach (var (item, n) in items)
            {
                Assert.Equal(n, inv.Add(item, n));
            }

            Assert.True(sim.SetInventory(me, inv));
            return me;
        }

        private static int Place(Simulation sim, int me, ushort key, TileCoord tile, int quad)
        {
            var plan = new PlannedEdit[EditRules.MaxLineTiles];
            int n = BuildPlanner.Plan(sim.World, sim.GetEntity(me).Mover.Position, key, tile, tile, quad, (ushort)me, plan);
            int ok = 0;
            for (int i = 0; i < n; i++)
            {
                if (plan[i].Check == EditReply.Accepted
                    && sim.RequestPlayerEdit(plan[i].Tile, plan[i].Floor, plan[i].Obj, true, plan[i].Record, me, i, Simulation.AnyCell, plan[i].FloorOnly))
                {
                    ok++;
                }
            }

            sim.Step();
            return ok;
        }

        // Ближний к точке узел сетки 0,5 (в единицах мира).
        private static WorldPos Node(WorldPos p)
        {
            int c = BuildCells.CellUnits;
            return new WorldPos((p.X + c / 2) / c * c, (p.Y + c / 2) / c * c);
        }

        /// <summary>
        /// Сундук (2 × 2 ячейки) и верстак (4 × 4) из любой четверти тайла под курсором встают центром ровно в ближний узел
        /// сетки 0,5 — там, где был призрак (раньше из северных и западных четвертей — на тайл южнее / восточнее).
        /// </summary>
        [Theory]
        [InlineData(ObjectId.Chest, 0)]
        [InlineData(ObjectId.Chest, 1)]
        [InlineData(ObjectId.Chest, 2)]
        [InlineData(ObjectId.Chest, 3)]
        [InlineData(ObjectId.Workbench, 0)]
        [InlineData(ObjectId.Workbench, 3)]
        public void C029_Furniture_Lands_Under_Cursor(ushort key, int quarter)
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Chest, 1), (Items.Workbench, 1));
            // Точка — середина четверти тайла (102, 100): 1/4 и 3/4 тайла по осям.
            int qx = quarter & 1, qy = quarter >> 1;
            var p = new WorldPos(102 * U + U / 4 + qx * U / 2, 100 * U + U / 4 + qy * U / 2);
            TileCoord tile = BuildCursor.Target(sim.World, sim.Shape, p, key, out int quad);
            Assert.Equal(1, Place(sim, me, key, tile, quad));
            Assert.True(BuildLayers.TryFurnitureAt(sim.World, Torus.TileOf(sim.Shape, p), out TileCoord anchor));
            Assert.Equal(Node(p), BuildLayers.CenterAt(sim.World, anchor));
        }

        /// <summary>
        /// Верстак 2 × 2 тайла со сдвигом на полтайла: молотом по любой ячейке его следа (и по нарисованной высоте — двум
        /// ячейкам севернее) цель — якорь, копание принято. Раньше 3 тайла из 4 отвечали «пусто».
        /// </summary>
        [Fact]
        public void C029_Workbench_Demolish_From_Any_Cell()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Workbench, 1));
            var p = new WorldPos(102 * U + U / 4, 101 * U + U / 4); // узел — СЗ угол тайла (102, 101): сдвиг на полтайла
            TileCoord tile = BuildCursor.Target(sim.World, sim.Shape, p, ObjectId.Workbench, out int quad);
            Assert.Equal(1, Place(sim, me, ObjectId.Workbench, tile, quad));
            Assert.True(BuildLayers.TryFurnitureAt(sim.World, new TileCoord(102, 101), out TileCoord anchor));
            WorldPos self = sim.GetEntity(me).Mover.Position;
            var times = new DigTimes();
            TileCoord nw = BuildCells.CellAt(sim.Shape, new WorldPos(Node(p).X - 2 * BuildCells.CellUnits, Node(p).Y - 2 * BuildCells.CellUnits));
            for (int cy = -2; cy < 4; cy++)
            {
                for (int cx = 0; cx < 4; cx++)
                {
                    var c = new TileCoord(nw.X + cx, nw.Y + cy);
                    var at = new WorldPos(c.X * BuildCells.CellUnits + BuildCells.CellUnits / 2, c.Y * BuildCells.CellUnits + BuildCells.CellUnits / 2);
                    TileCoord t = BuildCursor.Target(sim.World, sim.Shape, at, 0, out _);
                    Assert.Equal(anchor, t);
                    Assert.Equal(EditReply.Accepted, EditRules.CheckDig(sim.World, self, t, times, Items.Hammer));
                }
            }

            // Три ячейки севернее — уже мимо.
            var far = new WorldPos((nw.X * BuildCells.CellUnits) + BuildCells.CellUnits / 2, (nw.Y - 3) * BuildCells.CellUnits + BuildCells.CellUnits / 2);
            Assert.NotEqual(anchor, BuildCursor.Target(sim.World, sim.Shape, far, 0, out _));
        }

        /// <summary>Стена в полтайла: курсор на её ячейке и на ячейку севернее (нарисованный верх) — стена; на две — мимо.</summary>
        [Fact]
        public void C029_Wall_Picked_By_Drawn_Top()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.StoneWall, 2));
            var wall = new TileCoord(102, 102);
            Assert.Equal(1, Place(sim, me, ObjectId.StoneWall, wall, 2)); // западная половина тайла (обе ячейки)
            Assert.Equal(ObjectId.StoneWall, ObjectId.IdOf(sim.World.GetObject(wall)));
            Assert.Equal(BuildCells.Bit(0, 0) | BuildCells.Bit(0, 1), BuildLayers.TopCellsAt(sim.World, wall));
            int cu = BuildCells.CellUnits;
            WorldPos Cell(int cx, int cy) => new WorldPos(cx * cu + cu / 2, cy * cu + cu / 2);
            Assert.Equal(wall, BuildCursor.Target(sim.World, sim.Shape, Cell(204, 205), 0, out _));
            Assert.Equal(wall, BuildCursor.Target(sim.World, sim.Shape, Cell(204, 203), 0, out _)); // верх стены нарисован тут
            Assert.Equal(new TileCoord(102, 101), BuildCursor.Target(sim.World, sim.Shape, Cell(204, 202), 0, out _));
            // С постройкой в руке — без поправки: ячейка точки.
            Assert.Equal(new TileCoord(102, 101), BuildCursor.Target(sim.World, sim.Shape, Cell(204, 203), ObjectId.StoneWall, out int q));
            Assert.Equal(2, q);
        }

        /// <summary>
        /// Пипетка: стена — предмет той же стены (деревянная ≠ каменная), повёрнутый верстак — верстак с поворотом,
        /// построенный пол без верха — его предмет, валун и трава — ничего.
        /// </summary>
        [Fact]
        public void C029_Pipette_Item_Of_Building()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.StoneWall, 2), (Items.WoodWall, 2), (Items.Workbench, 1), (Items.WoodFloor, 4));
            Assert.Equal(1, Place(sim, me, ObjectId.StoneWall, new TileCoord(101, 101), 0));
            Assert.Equal(1, Place(sim, me, ObjectId.WoodWall, new TileCoord(102, 101), 0));
            Assert.Equal(1, Place(sim, me, BuildLayers.Turned(ObjectId.Workbench, 1), new TileCoord(104, 104), 0));
            Assert.Equal(1, Place(sim, me, BuildDefs.FloorKey(FloorId.WoodFloor), new TileCoord(98, 98), 0));
            Assert.Equal(Items.StoneWall, BuildCursor.PipetteItem(sim.World, new TileCoord(101, 101), out _));
            Assert.Equal(Items.WoodWall, BuildCursor.PipetteItem(sim.World, new TileCoord(102, 101), out _));
            Assert.True(BuildLayers.TryFurnitureAt(sim.World, new TileCoord(105, 105), out TileCoord bench));
            Assert.Equal(Items.Workbench, BuildCursor.PipetteItem(sim.World, bench, out int rot));
            Assert.Equal(1, rot);
            Assert.Equal(Items.WoodFloor, BuildCursor.PipetteItem(sim.World, new TileCoord(98, 98), out _));
            Assert.Equal(Items.None, BuildCursor.PipetteItem(sim.World, new TileCoord(90, 90), out _));
            sim.World.SetBaseTile(new TileCoord(91, 90), FloorId.Grass, ObjectId.Rock);
            Assert.Equal(Items.None, BuildCursor.PipetteItem(sim.World, new TileCoord(91, 90), out _));
        }
    }
}
