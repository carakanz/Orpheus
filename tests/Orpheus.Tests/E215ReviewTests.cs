using System;
using System.Collections.Generic;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Save;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-15, замечания проверки (П): занятость ячеек в одном такте, свес мебели через границу чанка и шов мира, настоящий файл
    /// формата 10, откат предсказания пола и мебели, причина «мешает мебель».
    /// </summary>
    public sealed class E215ReviewTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat(int size = 1024) => FlatWorldGenerator.Instance.Generate(MapShape.World(size), 1515);

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

        // Заявка в очередь такта по плану ядра (без шага): правки плана от сущности me.
        private static int Queue(Simulation sim, int me, ushort key, TileCoord tile, int quad)
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

            return ok;
        }

        private static int Place(Simulation sim, int me, ushort key, TileCoord tile, int quad)
        {
            int n = Queue(sim, me, key, tile, quad);
            sim.Step();
            return n;
        }

        private static int Count(Simulation sim, int me, byte item)
        {
            int n = sim.GetEntity(me).Inv.Count(item);
            foreach (GroundPile p in sim.Piles)
            {
                n += p.Item == item ? p.Count : 0;
            }

            return n;
        }

        // ------------------------------------------------ 1. занятость ячеек в одном такте

        /// <summary>
        /// Сундук со сдвигом (свес в западную половину тайла 103) и стена в ту же половину 103 — в одном такте: первая постановка
        /// встаёт, вторая — «занято» до списания, предмет на месте; ячейка занята один раз. Тот же и разные заявители.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void E215_SameTick_Chest_Overhang_And_Wall_Same_Cells_Second_Refused(bool twoPlayers)
        {
            var sim = new Simulation(Flat());
            int a = Me(sim, 100, 100, (Items.Chest, 1), (Items.StoneWall, 4));
            int b = twoPlayers ? Me(sim, 100, 102, (Items.StoneWall, 4)) : a;
            var refused = new List<SimEvent>();
            Assert.Equal(1, Queue(sim, a, ObjectId.Chest, new TileCoord(102, 100), 1)); // ячейки x 205…206: тайлы 102 (В) и 103 (З)
            Assert.Equal(1, Queue(sim, b, ObjectId.StoneWall, new TileCoord(103, 100), 0 | (2 << 2))); // западная половина 103
            sim.Step();
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.TileEdit && e.Result != EditResult.Applied)
                {
                    refused.Add(e);
                }
            }

            Assert.Single(refused);
            Assert.Equal(EditResult.Busy, refused[0].Result);
            Assert.Equal(b, refused[0].Requester);
            Assert.Equal(ObjectId.Chest, sim.World.GetObject(new TileCoord(102, 100)));
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(new TileCoord(103, 100)));
            Assert.Equal(4, Count(sim, b, Items.StoneWall)); // стена не списана
            Assert.Equal(0, Count(sim, a, Items.Chest) - (twoPlayers ? 0 : 0));
        }

        /// <summary>Два сундука с разными якорями (со сдвигом и без) на одну ячейку в одном такте — второй «занято», предмет цел.</summary>
        [Fact]
        public void E215_SameTick_Two_Furniture_Overlap_Second_Refused()
        {
            var sim = new Simulation(Flat());
            int a = Me(sim, 100, 100, (Items.Chest, 1));
            int b = Me(sim, 100, 103, (Items.Workbench, 1));
            Assert.Equal(1, Queue(sim, a, ObjectId.Chest, new TileCoord(102, 100), 1)); // якорь 102, свес в 103 (З)
            Assert.Equal(1, Queue(sim, b, ObjectId.Workbench, new TileCoord(103, 100), 0)); // весь тайл 103
            sim.Step();
            Assert.Equal(ObjectId.Chest, sim.World.GetObject(new TileCoord(102, 100)));
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(new TileCoord(103, 100)));
            Assert.Equal(1, Count(sim, b, Items.Workbench));
            Assert.Equal(0, Count(sim, a, Items.Chest));
        }

        // ------------------------------------------------ 3. «мешает мебель»

        /// <summary>Стена в свободную половину тайла-якоря сдвинутой мебели — перенос якоря (C-011; до него — «мешает мебель»), ячейка самой мебели — «занято».</summary>
        [Fact]
        public void E215_Wall_In_Free_Half_Of_Furniture_Anchor_Reason()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Chest, 1), (Items.StoneWall, 4));
            Assert.Equal(1, Place(sim, me, ObjectId.Chest, new TileCoord(102, 100), 3)); // СЗ ячейка — ЮВ 102: якорь 102, сдвиг (+1, +1)
            var plan = new PlannedEdit[EditRules.MaxLineTiles];
            WorldPos self = sim.GetEntity(me).Mover.Position;
            BuildPlanner.Plan(sim.World, self, ObjectId.StoneWall, new TileCoord(102, 100), new TileCoord(102, 100), 0, (ushort)me, plan);
            // C-011 [док]: свободная ячейка тайла-якоря — сундук переносит якорь (раньше «мешает мебель»); причина осталась для
            // случаев, где перенос невозможен (C011TailTests.C011_Wall_Into_Anchor_Refused_When_Move_Impossible).
            Assert.Equal(EditReply.Accepted, plan[0].Check);
            Assert.True(plan[0].Moves);
            Assert.Equal("мешает мебель — ставьте её после стен", EditRules.Describe(EditReply.FurnitureInTile));
            BuildPlanner.Plan(sim.World, self, ObjectId.StoneWall, new TileCoord(102, 100), new TileCoord(102, 100), 3 | (3 << 2), (ushort)me, plan);
            Assert.Equal(EditReply.Busy, plan[0].Check); // ячейка самого сундука — просто «занято»
        }

        // ------------------------------------------------ 2. граница чанка и шов мира

        /// <summary>
        /// Сундук со сдвигом через границу чанка (тайл 31 → 32 по X и по Y) и через шов мира (1023 → 0): свес твёрдый в чужом чанке,
        /// стена туда — «занято», круг упирается, путь не идёт сквозь; изменённые чанки после записи и чтения — тот же свес.
        /// </summary>
        [Theory]
        [InlineData(31, 40, 1)]   // по X через границу чанка
        [InlineData(40, 31, 2)]   // по Y через границу чанка
        [InlineData(1023, 40, 1)] // через шов мира по X
        public void E215_Overhang_Across_Chunk_And_Seam(int ax, int ay, int quad)
        {
            WorldModel world = Flat();
            var sim = new Simulation(world);
            int me = Me(sim, ax - 2, ay - 2, (Items.Chest, 1), (Items.StoneWall, 4));
            var anchor = new TileCoord(ax, ay);
            Assert.Equal(1, Place(sim, me, ObjectId.Chest, anchor, quad));
            Assert.True(sim.World.TryGetBuild(anchor, out BuildRecord rec));
            Assert.True(rec.HasOffset);
            int dx = quad & 1, dy = quad >> 1;
            var over = Torus.Wrap(sim.Shape, new TileCoord(ax + dx, ay + dy));
            Assert.NotEqual(Torus.ChunkOf(sim.Shape, anchor), Torus.ChunkOf(sim.Shape, over));
            byte want = dx == 1 ? BuildCells.DoorWest : BuildCells.DoorNorth;
            Assert.Equal(want, sim.SolidCells(sim.Shape, over));
            Assert.True(sim.IsBlocked(sim.Shape, over));

            var plan = new PlannedEdit[EditRules.MaxLineTiles];
            BuildPlanner.Plan(sim.World, sim.GetEntity(me).Mover.Position, ObjectId.StoneWall, over, over, 0, (ushort)me, plan);
            Assert.Equal(EditReply.Busy, plan[0].Check);

            // Круг идёт к свесу из соседнего тайла за ним — упирается в грань свеса (полтайла от границы тайлов).
            WorldPos p = dx == 1 ? At(over.X + 3, over.Y) : At(over.X, over.Y + 3);
            for (int i = 0; i < 200; i++)
            {
                p = Collision.Move(sim.Shape, p, dx == 1 ? -20 : 0, dx == 1 ? 0 : -20, sim.Solid);
            }

            int face = (dx == 1 ? over.X : over.Y) * U + U / 2 + Collision.RadiusUnits;
            Assert.Equal(face, Torus.Wrap(sim.Shape, p).X * dx + Torus.Wrap(sim.Shape, p).Y * dy);

            // Путь к точке за сундуком не проходит сквозь свес.
            var finder = new PathFinder(enclosureFillLimit: 0);
            var path = new PathBuffer();
            finder.Find(new SimulationPathTerrain(sim), dx == 1 ? At(over.X + 3, over.Y) : At(over.X, over.Y + 3), At(anchor.X - 3 * dx, anchor.Y - 3 * dy), PathMode.Walker, path);
            for (int i = 0; i < path.Length; i++)
            {
                Assert.False(Collision.TouchesSolid(sim.Shape, path.Points[i], sim.Solid), $"точка {i} {path.Points[i]} в свесе");
            }

            // Снимок изменённых чанков (как сохранение и чанк по сети): свес виден в загруженном мире, счёт сдвигов пересчитан.
            byte[] baseBytes = WorldSnapshot.WriteBase(FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1515));
            WorldModel back = WorldSnapshot.Load(baseBytes, WorldSnapshot.WriteChanges(sim.World));
            Assert.Equal(want, BuildLayers.TopCellsAt(back, over));
            Assert.True(back.FindChunk(Torus.ChunkOf(back.Shape, anchor))!.OffsetCount > 0);
            Assert.Equal(0, back.FindChunk(Torus.ChunkOf(back.Shape, over))!.OffsetCount);
            Assert.True(BuildLayers.TryTopOfCell(back, BuildCells.CellOf(over, 0, 0), out TileCoord top));
            Assert.Equal(anchor, top);
        }

        /// <summary>По сети: сундук со сдвигом у границы чанка — мир клиента (чанки и патчи) видит свес в соседнем чанке, как сервер.</summary>
        [Fact]
        public void E215_Net_Client_Sees_Overhang_In_Neighbour_Chunk()
        {
            using var rig = new EditTests.EditRig(Flat(), _ => At(29, 40));
            NetClient c = rig.Join();
            rig.Run(5);
            var inv = rig.Sim.GetEntity(c.EntityId).Inv;
            inv.SetCell(7, Items.Chest, 1);
            Assert.True(rig.Sim.SetInventory(c.EntityId, inv));
            rig.Run(3);
            c.Place(new TileCoord(31, 40), new TileCoord(31, 40), ObjectId.Chest, rig.Sim.GetEntity(c.EntityId).Mover.Position, 7, 1);
            rig.Run(6);
            var over = new TileCoord(32, 40);
            Assert.Equal(BuildCells.DoorWest, BuildLayers.TopCellsAt(rig.Sim.World, over));
            Assert.Equal(BuildCells.DoorWest, BuildLayers.TopCellsAt(c.World!.World, over));
            Assert.Equal(rig.Sim.GetEntity(c.EntityId).Inv, c.Inv);
        }

        // ------------------------------------------------ откат предсказания

        /// <summary>
        /// Сервер отказал (сундук: в тайле свеса стоит существо; пол: в том же такте на тайл встал валун) — клиент откатывает
        /// предсказание: мир и сумка клиента совпадают с сервером.
        /// </summary>
        [Fact]
        public void E215_Net_Prediction_Rollback_Floor_And_Furniture()
        {
            using var rig = new EditTests.EditRig(Flat(), _ => At(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(5);
            var inv = rig.Sim.GetEntity(c.EntityId).Inv;
            inv.SetCell(6, Items.WoodFloor, 8);
            inv.SetCell(7, Items.Chest, 1);
            Assert.True(rig.Sim.SetInventory(c.EntityId, inv));
            rig.Run(3);
            rig.Sim.AddEntity(At(304, 200)); // чужое существо в тайле свеса сундука (клиент о нём не проверяет)
            rig.Run(2);
            WorldPos self = rig.Sim.GetEntity(c.EntityId).Mover.Position;

            c.Place(new TileCoord(303, 200), new TileCoord(303, 200), ObjectId.Chest, self, 7, 1); // якорь 303, свес в 304
            var t = new TileCoord(302, 202);
            Assert.True(rig.Sim.RequestEdit(t, FloorId.Grass, ObjectId.Rock)); // валун пришёл раньше заявки пола
            c.Place(t, t, BuildDefs.WoodFloorKey, self, 6);
            Assert.Equal(ObjectId.Chest, c.World!.World.GetObject(new TileCoord(303, 200))); // предсказано
            Assert.Equal(FloorId.WoodFloor, c.World.World.GetFloor(t));
            rig.Run(8);

            Assert.Equal(2, replies.Count);
            Assert.All(replies, r => Assert.NotEqual(EditReply.Accepted, r.Reply));
            for (int x = 300; x <= 306; x++)
            {
                for (int y = 198; y <= 204; y++)
                {
                    var q = new TileCoord(x, y);
                    Assert.Equal(rig.Sim.World.GetObject(q), c.World.World.GetObject(q));
                    Assert.Equal(rig.Sim.World.GetFloor(q), c.World.World.GetFloor(q));
                    Assert.Equal(rig.Sim.World.TryGetBuild(q, out _), c.World.World.TryGetBuild(q, out _));
                }
            }

            Assert.Equal(rig.Sim.GetEntity(c.EntityId).Inv, c.Inv);
            Assert.Equal(8, c.Inv.Count(Items.WoodFloor));
            Assert.Equal(1, c.Inv.Count(Items.Chest));
        }

        // ------------------------------------------------ настоящий файл формата 10

        /// <summary>
        /// Настоящий файл формата 10 (записан кодом main 2aca59a, генератор <c>Golden/format10_build.make.cs.txt</c>): полы
        /// деревом и камнем, стена в западных половинах, сундук, верстак, факел, повреждённая стена. Читается, хэш генератора
        /// тот же; пол — весь тайл, мебель без сдвига (весь тайл); снос пола — 4 предмета, мебели — 1; пересохранение в формат
        /// 11 — тот же хэш.
        /// </summary>
        [Fact]
        public void E215_Save_Format10_RealFile_Reads_Floors_Furniture_Full()
        {
            byte[] v10 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format10_build.save"));
            byte[] baseBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format10_build.base"));
            SaveFile f = SaveContainer.Read(v10);
            Assert.True(f.IsOk, f.Error);
            Assert.Equal(10u, f.Header.FormatVersion);
            Simulation sim = SaveFixtures.Load(baseBytes, v10);
            string hash = StateHash.Compute(sim).Total.ToString("x16");
            Assert.Equal("0329ad4c20838f97", hash); // хэш генератора (код main); E3-6: формат 15

            for (int x = 130; x <= 132; x++)
            {
                Assert.Equal(FloorId.WoodFloor, sim.World.GetFloor(new TileCoord(x, 126)));
                Assert.Equal(BuildCells.Full, BuildLayers.FloorCellsAt(sim.World, new TileCoord(x, 126)));
                Assert.Equal(FloorId.StoneFloor, sim.World.GetFloor(new TileCoord(x, 130)));
            }

            for (int y = 127; y <= 129; y++)
            {
                Assert.Equal(BuildCells.DoorWest, sim.SolidCells(sim.Shape, new TileCoord(124, y)));
            }

            foreach (int x in new[] { 126, 129 })
            {
                var t = new TileCoord(x, 132);
                Assert.True(sim.World.TryGetBuild(t, out BuildRecord rec));
                Assert.False(rec.HasOffset);
                Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, t)); // сундук, верстак — весь тайл
            }

            Assert.Equal(ObjectId.Torch, ObjectId.IdOf(sim.World.GetObject(new TileCoord(132, 132))));
            Assert.True(sim.World.TryGetBuild(new TileCoord(124, 129), out BuildRecord damaged));
            Assert.Equal(150, damaged.Integrity);

            // Пересохранение в формат 11 — тот же хэш.
            byte[] again = WorldSave.Build(WorldSave.Capture(sim, WorldSnapshot.StoredHash(baseBytes), "формат10→11"), 6, 0);
            Assert.Equal(15u, SaveContainer.Read(again).Header.FormatVersion); // E2-14: 13, E2-16: 12
            Assert.Equal(hash, StateHash.Compute(SaveFixtures.Load(baseBytes, again)).Total.ToString("x16"));

            // Снос старого пола — 4 предмета (тайл = 4 ячейки), сундука — 1.
            int id = sim.Entities[0].Id;
            int floors = Count(sim, id, Items.WoodFloor), chests = Count(sim, id, Items.Chest);
            foreach (TileCoord t in new[] { new TileCoord(131, 126), new TileCoord(126, 132) })
            {
                BuildLayers.AfterDig(sim.World, t, out byte fl, out bool keep, out BuildRecord rest);
                Assert.True(sim.RequestPlayerEdit(t, fl, ObjectId.Empty, keep, rest, id, 0));
                sim.Step();
            }

            Assert.Equal(floors + 4, Count(sim, id, Items.WoodFloor));
            Assert.Equal(chests + 1, Count(sim, id, Items.Chest));
        }
    }
}
