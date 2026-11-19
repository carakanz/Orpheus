using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-040 (хвост C-029): дверь поверх стены ставится правкой такта без заказчика — стена сносится,
    /// а возврата стены в сумку нет (возврат делается только ставящему, <c>Simulation.Edits.cs</c>), масса мира не сходится.
    /// Правило (как <c>ApplyChestOp</c>): «заявитель жив и в радиусе» проверяется в такте — отказ с кодом,
    /// стена цела, масса не меняется.
    /// </summary>
    public sealed class C040DoorOwnerlessTests
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

        /// <summary>Сколько предмета у всех: сумки персонажей, сундуки, кучи на земле (как в BuildTests.cs).</summary>
        private static int Total(Simulation sim, byte item)
        {
            int n = 0;
            foreach (SimEntity e in sim.Entities)
            {
                n += e.Inv.Count(item);
            }

            foreach (Inventory ch in sim.ChestContents)
            {
                n += ch.Count(item);
            }

            foreach (GroundPile p in sim.Piles)
            {
                n += p.Item == item ? p.Count : 0;
            }

            return n;
        }

        /// <summary>Исход правки тайла <paramref name="t"/> в событиях такта; −1 — событий по тайлу не было.</summary>
        private static int TileResults(Simulation sim, TileCoord t, out EditResult last)
        {
            ChunkCoord chunk = Torus.ChunkOf(sim.World.Shape, Torus.Wrap(sim.World.Shape, t));
            int index = Chunk.IndexOf(t.X, t.Y);
            int seen = 0;
            last = EditResult.Applied;
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.TileEdit && e.Chunk == chunk && e.TileIndex == index)
                {
                    seen++;
                    last = e.Result;
                }
            }

            return seen;
        }

        /// <summary>
        /// Стена есть; правка такта (процесс мира, заказчика нет) ставит дверь поверх стены.
        /// Отказ с кодом: объект — по-прежнему стена, ячейки стены целы, ни предмета стены, ни предмета
        /// двери не убыло (масса мира не меняется).
        /// </summary>
        [Fact]
        public void C040_Door_Over_Wall_No_Requester_Refused()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 101, 102, (Items.StoneWall, 2), (Items.Door, 1));
            var mid = new TileCoord(101, 100);
            Assert.Equal(1, Place(sim, me, ObjectId.StoneWall, mid, mid, 0 | (1 << 2))); // северная пара ячеек
            Assert.Equal(ObjectId.StoneWall, ObjectId.IdOf(sim.World.GetObject(mid)));
            byte cellsBefore = BuildLayers.TopCellsAt(sim.World, mid);
            int wallTotal = Total(sim, Items.StoneWall);
            int doorTotal = Total(sim, Items.Door);
            Assert.Equal(0, wallTotal); // обе стены в мире (списаны в постановку)

            byte f = sim.World.GetFloor(mid);
            Assert.True(sim.RequestEdit(mid, f, ObjectId.Door, BuildRecord.OfPlayer(BuildRecord.WorldOwner, f)));
            sim.Step();

            Assert.Equal(ObjectId.StoneWall, ObjectId.IdOf(sim.World.GetObject(mid))); // стена цела, дверь не встала
            Assert.Equal(cellsBefore, BuildLayers.TopCellsAt(sim.World, mid));
            Assert.Equal(wallTotal, Total(sim, Items.StoneWall)); // масса не меняется
            Assert.Equal(doorTotal, Total(sim, Items.Door));
            Assert.Equal(1, TileResults(sim, mid, out EditResult last));
            Assert.NotEqual(EditResult.Applied, last); // отказ с кодом
        }

        /// <summary>
        /// Прямая заявка игрока (мимо планировщика) на дверь поверх дальней стены: заявитель не в радиусе
        /// постановки — в такте отказ, стена цела, масса не меняется.
        /// </summary>
        [Fact]
        public void C040_Door_Over_Wall_Requester_Out_Of_Reach_Refused()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 101, 102, (Items.Door, 1));
            var far = new TileCoord(130, 100); // 29 тайлов от игрока — дальше радиуса постановки (6)
            byte f = sim.World.GetFloor(far);
            Assert.True(sim.RequestEdit(far, f, ObjectId.StoneWall, BuildRecord.OfPlayer(BuildRecord.WorldOwner, f)));
            sim.Step();
            Assert.Equal(ObjectId.StoneWall, ObjectId.IdOf(sim.World.GetObject(far)));
            byte cellsBefore = BuildLayers.TopCellsAt(sim.World, far);
            int wallTotal = Total(sim, Items.StoneWall);
            int doorTotal = Total(sim, Items.Door);

            Assert.True(sim.RequestPlayerEdit(far, f, ObjectId.Door, true, BuildRecord.OfPlayer((ushort)me, f), me, 5));
            sim.Step();

            Assert.Equal(ObjectId.StoneWall, ObjectId.IdOf(sim.World.GetObject(far))); // стена цела
            Assert.Equal(cellsBefore, BuildLayers.TopCellsAt(sim.World, far));
            Assert.Equal(wallTotal, Total(sim, Items.StoneWall));
            Assert.Equal(doorTotal, Total(sim, Items.Door)); // дверь из сумки не списана
        }

        /// <summary>
        /// Заявка живого игрока на дверь поверх стены стоит в очереди; игрок гибнет в том же такте до фазы
        /// правок — отказ, стена цела, дверь не списана (масса цела; смерть в такте, как C-004 для запроса).
        /// </summary>
        [Fact]
        public void C040_Door_Over_Wall_Requester_Died_In_Tick_Refused()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 101, 102, (Items.StoneWall, 2), (Items.Door, 1));
            var mid = new TileCoord(101, 100);
            Assert.Equal(1, Place(sim, me, ObjectId.StoneWall, mid, mid, 0 | (1 << 2)));
            byte cellsBefore = BuildLayers.TopCellsAt(sim.World, mid);
            int wallTotal = Total(sim, Items.StoneWall);
            int doorTotal = Total(sim, Items.Door);

            byte f = sim.World.GetFloor(mid);
            Assert.True(sim.RequestPlayerEdit(mid, f, ObjectId.Door, true, BuildRecord.OfPlayer((ushort)me, f), me, 7));
            Assert.True(sim.ApplyStrike(me, 0, 500) > 0); // гибель в том же такте, до фазы правок
            Assert.False(sim.GetEntity(me).Alive);
            sim.Step();

            Assert.Equal(ObjectId.StoneWall, ObjectId.IdOf(sim.World.GetObject(mid))); // стена цела
            Assert.Equal(cellsBefore, BuildLayers.TopCellsAt(sim.World, mid));
            Assert.Equal(wallTotal, Total(sim, Items.StoneWall));
            Assert.Equal(doorTotal, Total(sim, Items.Door));
        }
    }
}
