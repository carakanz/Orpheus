using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-029 (слово Автора 17.11: «Нужно иметь возможность построить дверь поверх стены»): дверь на стену — замена без сноса,
    /// стена возвращается в сумку.
    /// </summary>
    public sealed class C029DoorTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat() => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 2930);

        private static int Me(Simulation sim, int x, int y, params (byte Item, int N)[] items)
        {
            int me = sim.AddEntity(new WorldPos(x * U + U / 2, y * U + U / 2));
            var inv = sim.GetEntity(me).Inv;
            foreach (var (item, n) in items)
            {
                Assert.Equal(n, inv.Add(item, n));
            }

            Assert.True(sim.SetInventory(me, inv));
            return me;
        }

        private static int Place(Simulation sim, int me, ushort key, TileCoord from, TileCoord to, int quad)
        {
            var plan = new PlannedEdit[EditRules.MaxLineTiles];
            int n = BuildPlanner.Plan(sim.World, sim.GetEntity(me).Mover.Position, key, from, to, quad, (ushort)me, plan);
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

        /// <summary>
        /// Горизонтальная стена (северные половины) через 3 тайла; дверь на средний тайл — встаёт северной парой, стена
        /// этого тайла (2 ячейки) — в сумку, дверь из сумки ушла; соседние тайлы стены целы. Забор дверью не заменяется.
        /// </summary>
        [Fact]
        public void C029_Door_Replaces_Wall_And_Returns_It()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 101, 102, (Items.StoneWall, 6), (Items.Door, 1), (Items.WoodFence, 2));
            // Линия ячеек от СЗ ячейки тайла 100/100 до СВ ячейки тайла 102/100 — северные половины трёх тайлов.
            Assert.Equal(3, Place(sim, me, ObjectId.StoneWall, new TileCoord(100, 100), new TileCoord(102, 100), 0 | (1 << 2)));
            Assert.Equal(0, sim.GetEntity(me).Inv.Count(Items.StoneWall));
            var mid = new TileCoord(101, 100);
            Assert.Equal(1, Place(sim, me, ObjectId.Door, mid, mid, 0));
            Assert.Equal(ObjectId.Door, ObjectId.IdOf(sim.World.GetObject(mid)));
            Assert.Equal(BuildCells.DoorNorth, BuildLayers.TopCellsAt(sim.World, mid));
            Assert.Equal(2, sim.GetEntity(me).Inv.Count(Items.StoneWall));
            Assert.Equal(0, sim.GetEntity(me).Inv.Count(Items.Door));
            Assert.Equal(ObjectId.StoneWall, ObjectId.IdOf(sim.World.GetObject(new TileCoord(100, 100))));
            Assert.Equal(ObjectId.StoneWall, ObjectId.IdOf(sim.World.GetObject(new TileCoord(102, 100))));

            // Забор — не стена: дверь поверх него — «занято».
            var fence = new TileCoord(101, 104);
            Assert.Equal(1, Place(sim, me, ObjectId.WoodFence, fence, fence, 0 | (1 << 2)));
            var plan = new PlannedEdit[EditRules.MaxLineTiles];
            Assert.Equal(1, BuildPlanner.Plan(sim.World, sim.GetEntity(me).Mover.Position, ObjectId.Door, fence, fence, 0, (ushort)me, plan));
            Assert.Equal(EditReply.Busy, plan[0].Check);
        }
    }
}
