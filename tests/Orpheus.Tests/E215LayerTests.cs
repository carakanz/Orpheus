using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-15 (слова Автора 15.11: «Сетку для всех предметов уменьшить в 2 раза… не могу ничего поставить у стен или постелить
    /// там пол», «Стену и предметы нужно разрешить поверх пола ставить»; записка <c>work/design/half_tile_all.md</c>): два слоя
    /// тайла (пол и верх), пол и мебель по ячейкам 0,5, снос по слоям, масса, путь и столкновения у мебели, старый мир.
    /// </summary>
    public sealed class E215LayerTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat(ulong seed = 1515) => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), seed);

        private static WorldPos At(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        // Постановка игрока по плану ядра (тот же, что у сервера): правки плана — в очередь такта от сущности me, такт.
        private static int Place(Simulation sim, int me, ushort key, TileCoord from, TileCoord to, int quad)
        {
            var plan = new PlannedEdit[EditRules.MaxLineTiles];
            WorldPos self = sim.GetEntity(me).Mover.Position;
            int n = BuildPlanner.Plan(sim.World, self, key, from, to, quad, (ushort)me, plan);
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

        private static EditReply Check(Simulation sim, int me, ushort key, TileCoord tile, int quad)
        {
            var plan = new PlannedEdit[EditRules.MaxLineTiles];
            int n = BuildPlanner.Plan(sim.World, sim.GetEntity(me).Mover.Position, key, tile, tile, quad | (quad << 2), (ushort)me, plan);
            return n > 0 ? plan[0].Check : EditReply.NotAllowed;
        }

        // Снос молотом — та же правка, что у сервера (BuildLayers.AfterDig).
        private static void Dig(Simulation sim, int me, TileCoord t)
        {
            BuildLayers.AfterDig(sim.World, t, out byte floor, out bool keep, out BuildRecord rest);
            Assert.True(sim.RequestPlayerEdit(t, floor, ObjectId.Empty, keep, rest, me, 0));
            sim.Step();
        }

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

        private static int Total(Simulation sim, int me, byte item)
        {
            int n = sim.GetEntity(me).Inv.Count(item);
            foreach (GroundPile p in sim.Piles)
            {
                n += p.Item == item ? p.Count : 0;
            }

            return n;
        }

        // Тонкая стена в западной половине тайла (ячейки СЗ + ЮЗ).
        private static void WestWall(Simulation sim, int me, TileCoord t) =>
            Assert.Equal(1, Place(sim, me, ObjectId.StoneWall, t, t, 0 | (2 << 2)));

        // ------------------------------------------------ пол и стена — слои

        /// <summary>Пол целым тайлом под уже стоящую тонкую стену: стена на месте, пол — весь тайл, природный пол в записи — луг.</summary>
        [Fact]
        public void E215_Floor_Under_Existing_Wall()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.StoneWall, 10), (Items.WoodFloor, 10));
            var t = new TileCoord(102, 100);
            WestWall(sim, me, t);
            Assert.Equal(EditReply.Accepted, Check(sim, me, BuildDefs.WoodFloorKey, t, 0));
            byte q = BuildCells.WholeTilesQuad(sim.Shape, t, t);
            Assert.Equal(1, Place(sim, me, BuildDefs.WoodFloorKey, t, t, q));
            Assert.Equal(ObjectId.StoneWall, sim.World.GetObject(t));
            Assert.Equal(BuildCells.DoorWest, BuildCells.MaskAt(sim.World, t));
            Assert.Equal(FloorId.WoodFloor, sim.World.GetFloor(t));
            Assert.Equal(BuildCells.Full, BuildLayers.FloorCellsAt(sim.World, t));
            Assert.True(sim.World.TryGetBuild(t, out BuildRecord rec));
            Assert.Equal(FloorId.Grass, rec.NaturalFloor);
            Assert.Equal(BuildCells.DoorWest, sim.SolidCells(sim.Shape, t)); // пол не твёрдый — твёрдая только стена
            Assert.Equal(10 - 4, sim.GetEntity(me).Inv.Count(Items.WoodFloor)); // предмет на ячейку пола
            Assert.Equal(10 - 2, sim.GetEntity(me).Inv.Count(Items.StoneWall));
        }

        /// <summary>Стена на уже постеленном полу: ставится, пол и природный пол под ним в записи сохраняются (раньше — терялся).</summary>
        [Fact]
        public void E215_Wall_On_Existing_Floor()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.StoneWall, 10), (Items.WoodFloor, 10));
            var t = new TileCoord(102, 100);
            Assert.Equal(1, Place(sim, me, BuildDefs.WoodFloorKey, t, t, BuildCells.WholeTilesQuad(sim.Shape, t, t)));
            Assert.Equal(EditReply.Accepted, Check(sim, me, ObjectId.StoneWall, t, 0));
            WestWall(sim, me, t);
            Assert.Equal(ObjectId.StoneWall, sim.World.GetObject(t));
            Assert.Equal(FloorId.WoodFloor, sim.World.GetFloor(t));
            Assert.True(sim.World.TryGetBuild(t, out BuildRecord rec));
            Assert.Equal(FloorId.Grass, rec.NaturalFloor);
            Assert.Equal(BuildCells.Full, BuildLayers.FloorCellsAt(sim.World, t));
            Assert.Equal(BuildCells.DoorWest, rec.Cells);

            // Мебель на полу — тоже.
            var c = new TileCoord(104, 100);
            Assert.Equal(1, Place(sim, me, BuildDefs.WoodFloorKey, c, c, BuildCells.WholeTilesQuad(sim.Shape, c, c)));
            var bag = sim.GetEntity(me).Inv;
            bag.Add(Items.Chest, 1);
            sim.SetInventory(me, bag);
            Assert.Equal(1, Place(sim, me, ObjectId.Chest, c, c, 0));
            Assert.Equal(ObjectId.Chest, sim.World.GetObject(c));
            Assert.Equal(FloorId.WoodFloor, sim.World.GetFloor(c));
        }

        /// <summary>Пол — по ячейкам: в свободную половину тайла у стены; ячейки уже с полом — «занято»; другой пол — «занято».</summary>
        [Fact]
        public void E215_Floor_Cells_In_Free_Half_Next_To_Wall()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.StoneWall, 10), (Items.WoodFloor, 10), (Items.StoneFloor, 10));
            var t = new TileCoord(102, 100);
            WestWall(sim, me, t);
            // Восточная половина: ячейки СВ (1) и ЮВ (3) — прямоугольник 1 × 2.
            Assert.Equal(1, Place(sim, me, BuildDefs.WoodFloorKey, t, t, 1 | (3 << 2)));
            Assert.Equal(BuildCells.DoorEast, BuildLayers.FloorCellsAt(sim.World, t));
            Assert.Equal(10 - 2, sim.GetEntity(me).Inv.Count(Items.WoodFloor));
            Assert.Equal(EditReply.Busy, Check(sim, me, BuildDefs.WoodFloorKey, t, 1));
            Assert.Equal(EditReply.Busy, Check(sim, me, BuildDefs.StoneFloorKey, t, 0)); // один вид пола в тайле
            Assert.Equal(EditReply.Accepted, Check(sim, me, BuildDefs.WoodFloorKey, t, 0)); // под стеной — можно
        }

        /// <summary>
        /// Цена тайла больше стопки в руке (тайл пола — 4 предмета): недостающее снимается из сумки; всего меньше цены — тайл
        /// не ставится, ничего не снято.
        /// </summary>
        [Fact]
        public void E215_Tile_Price_Above_Hand_Stack_Takes_Rest_From_Bag()
        {
            var inv = new Inventory();
            Assert.True(inv.SetCell(3, Items.WoodFloor, 2));
            Assert.True(inv.SetCell(20, Items.WoodFloor, 1));
            Assert.Equal(0, inv.TakeFromThenAny(3, Items.WoodFloor, 4)); // всего 3 < 4
            Assert.Equal(3, inv.Count(Items.WoodFloor));
            Assert.True(inv.SetCell(21, Items.WoodFloor, 5));
            Assert.Equal(4, inv.TakeFromThenAny(3, Items.WoodFloor, 4));
            Assert.Equal(0, inv.CountIn(3)); // рука — первой
            Assert.Equal(4, inv.Count(Items.WoodFloor));
        }

        // ------------------------------------------------ снос по слоям

        /// <summary>
        /// Снос молотом снимает верх, пол остаётся (возврат — только стена); второй снос — пол (возврат — по ячейке); масса не
        /// меняется ни на одном шаге.
        /// </summary>
        [Fact]
        public void E215_Demolish_Top_Keeps_Floor_Then_Floor_Mass()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.StoneWall, 10), (Items.WoodFloor, 10));
            var t = new TileCoord(102, 100);
            Assert.Equal(1, Place(sim, me, BuildDefs.WoodFloorKey, t, t, BuildCells.WholeTilesQuad(sim.Shape, t, t)));
            WestWall(sim, me, t);
            int walls = 10 - 2, floors = 10 - 4;
            Assert.Equal(walls, Total(sim, me, Items.StoneWall));
            Assert.Equal(floors, Total(sim, me, Items.WoodFloor));

            Dig(sim, me, t);
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(t));
            Assert.Equal(FloorId.WoodFloor, sim.World.GetFloor(t));
            Assert.True(sim.World.TryGetBuild(t, out BuildRecord rec));
            Assert.Equal(BuildCells.Full, BuildLayers.FloorCellsAt(sim.World, t));
            Assert.Equal(FloorId.Grass, rec.NaturalFloor);
            Assert.Equal(10, Total(sim, me, Items.StoneWall)); // стена — 2 ячейки вернулись кучей
            Assert.Equal(floors, Total(sim, me, Items.WoodFloor)); // пол не тронут

            Dig(sim, me, t);
            Assert.Equal(FloorId.Grass, sim.World.GetFloor(t));
            Assert.False(sim.World.TryGetBuild(t, out _));
            Assert.Equal(10, Total(sim, me, Items.WoodFloor));
            Assert.Equal(10, Total(sim, me, Items.StoneWall));
        }

        /// <summary>Разрушение стены на полу (урон до нуля) — обломки верха, пол под ними остаётся и после конца обломков.</summary>
        [Fact]
        public void E215_Destroyed_Wall_Leaves_Floor_Under_Debris()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.StoneWall, 10), (Items.WoodFloor, 10));
            var t = new TileCoord(102, 100);
            Assert.Equal(1, Place(sim, me, BuildDefs.WoodFloorKey, t, t, BuildCells.WholeTilesQuad(sim.Shape, t, t)));
            WestWall(sim, me, t);
            Assert.True(sim.DamageBuilding(t, 100_000));
            sim.Step();
            Assert.True(sim.World.TryGetBuild(t, out BuildRecord debris));
            Assert.True(debris.IsDebris);
            Assert.Equal(FloorId.WoodFloor, sim.World.GetFloor(t));
            for (int i = 0; i < BuildDefs.DebrisTicks + 5; i++)
            {
                sim.Step();
            }

            Assert.True(sim.World.TryGetBuild(t, out BuildRecord after));
            Assert.False(after.IsDebris);
            Assert.Equal(FloorId.WoodFloor, sim.World.GetFloor(t));
            Assert.Equal(BuildCells.Full, BuildLayers.FloorCellsAt(sim.World, t));
        }

        // ------------------------------------------------ мебель со сдвигом

        /// <summary>
        /// Сундук вплотную к тонкой стене в свободной половине её тайла (шаг 0,5): якорь — соседний тайл, сдвиг −1; твёрдые ячейки
        /// — стена + половина сундука в тайле стены, половина сундука в соседнем; столкновения и линия видят ячейки сундука.
        /// </summary>
        [Fact]
        public void E215_Chest_Against_Thin_Wall_Cells_Collision()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.StoneWall, 10), (Items.Chest, 3));
            var w = new TileCoord(102, 100);
            WestWall(sim, me, w);
            Assert.Equal(1, Place(sim, me, ObjectId.Chest, w, w, 1)); // СЗ ячейка — восточная половина тайла стены
            var anchor = new TileCoord(103, 100);
            Assert.Equal(ObjectId.Chest, sim.World.GetObject(anchor));
            Assert.Equal(ObjectId.StoneWall, sim.World.GetObject(w));
            Assert.True(sim.World.TryGetBuild(anchor, out BuildRecord rec));
            Assert.Equal(-1, rec.OffsetX);
            Assert.Equal(0, rec.OffsetY);
            Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, w)); // стена (З) + сундук (В)
            Assert.Equal(BuildCells.DoorWest, sim.SolidCells(sim.Shape, anchor)); // восточная половина якоря свободна
            Assert.True(sim.IsBlocked(sim.Shape, anchor));
            Assert.True(BuildLayers.TryTopOfCell(sim.World, new TileCoord(2 * 102 + 1, 200), out TileCoord top));
            Assert.Equal(anchor, top);
            Assert.Equal(EditRules.CenterOf(w).X + U / 2, BuildLayers.CenterAt(sim.World, anchor).X);

            // Круг идёт с востока на запад по ряду 100 — упирается в восточную грань сундука (x = 103,5).
            WorldPos p = At(106, 100);
            for (int i = 0; i < 200; i++)
            {
                p = Collision.Move(sim.Shape, p, -20, 0, sim.Solid);
            }

            Assert.Equal(103 * U + U / 2 + Collision.RadiusUnits, p.X);

            // В ячейки сундука не встать ни стене, ни второму сундуку. Свободная восточная половина тайла-якоря — тоже не для стены
            // (один объект на тайл; риск записки), а у соседнего тайла 104 — можно.
            Assert.Equal(EditReply.Busy, Check(sim, me, ObjectId.StoneWall, anchor, 0));
            Assert.Equal(EditReply.Busy, Check(sim, me, ObjectId.StoneWall, anchor, 1));
            Assert.Equal(EditReply.Accepted, Check(sim, me, ObjectId.StoneWall, new TileCoord(104, 100), 0));
            Assert.Equal(EditReply.Busy, Check(sim, me, ObjectId.Chest, anchor, 0));
            Assert.Equal(EditReply.Accepted, Check(sim, me, ObjectId.Chest, anchor, 1)); // сдвиг +1: якорь — свой, свободный 104

            // Снос по любой ячейке — через тайл-якорь: возврат — сундук.
            Dig(sim, me, anchor);
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(anchor));
            Assert.Equal(BuildCells.DoorWest, sim.SolidCells(sim.Shape, w));
            Assert.Equal(3, Total(sim, me, Items.Chest));
        }

        /// <summary>Мебель без сдвига (старые миры, щелчок по углу тайла) — весь тайл, как раньше; торшер-факел проходим.</summary>
        [Fact]
        public void E215_Furniture_Aligned_FullTile_Torch_Passable()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Workbench, 1), (Items.Torch, 1));
            var t = new TileCoord(103, 100);
            Assert.Equal(1, Place(sim, me, ObjectId.Workbench, t, t, 0));
            Assert.True(sim.World.TryGetBuild(t, out BuildRecord rec));
            Assert.False(rec.HasOffset);
            Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, t));
            var f = new TileCoord(105, 100);
            Assert.Equal(1, Place(sim, me, ObjectId.Torch, f, f, 3)); // сдвиг (+1, +1): задевает 4 тайла
            Assert.True(sim.World.TryGetBuild(f, out BuildRecord tr));
            Assert.Equal(1, tr.OffsetX);
            Assert.Equal(1, tr.OffsetY);
            Assert.False(sim.IsBlocked(sim.Shape, new TileCoord(106, 101))); // факел проходим
            Assert.Equal(BuildCells.Bit(0, 0), BuildLayers.TopCellsAt(sim.World, new TileCoord(106, 101))); // но ячейку занимает
        }

        /// <summary>
        /// Путь врагов (E2-12b) видит ячейки мебели: проём в 1 тайл в длинной стене наполовину перегорожен сундуком со сдвигом
        /// (щель 0,5) — пути нет; без сундука — есть, точки пути не задевают твёрдого.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void E215_Path_Sees_Furniture_Cells(bool blocked)
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 47, 50, (Items.Chest, 1));
            // Стена целыми тайлами по x = 50 (ряды 10…90) с проёмом в 1 тайл на ряду 50.
            for (int y = 10; y <= 90; y++)
            {
                if (y != 50)
                {
                    Assert.True(sim.RequestEdit(new TileCoord(50, y), FloorId.Grass, ObjectId.StoneWall, BuildRecord.OfPlayer(1, FloorId.Grass)));
                }
            }

            sim.Step();
            if (blocked)
            {
                // Сундук со сдвигом на полтайла: восточная половина тайла 49 и западная — проёма 50; остаётся щель 0,5 — круг 0,8 не проходит.
                Assert.Equal(1, Place(sim, me, ObjectId.Chest, new TileCoord(49, 50), new TileCoord(49, 50), 1));
                Assert.True(sim.World.TryGetBuild(new TileCoord(49, 50), out BuildRecord r));
                Assert.Equal(1, r.OffsetX);
                Assert.Equal(BuildCells.DoorWest, sim.SolidCells(sim.Shape, new TileCoord(50, 50)));
            }

            var terrain = new SimulationPathTerrain(sim);
            var finder = new PathFinder(enclosureFillLimit: 0);
            var path = new PathBuffer();
            WorldPos start = At(45, 52), goal = At(55, 50);
            PathStatus status = finder.Find(terrain, start, goal, PathMode.Walker, path);
            Assert.Equal(blocked ? PathStatus.Partial : PathStatus.Found, status);
            for (int i = 0; i < path.Length; i++)
            {
                Assert.False(Collision.TouchesSolid(sim.Shape, path.Points[i], sim.Solid), $"точка {i} {path.Points[i]} задевает твёрдое");
            }
        }

        // ------------------------------------------------ сеть: предсказание = сервер

        /// <summary>
        /// По сети (сервер + клиент с предсказанием): пол под стену и в свободную половину, сундук вплотную к стене со сдвигом —
        /// мир клиента совпадает с сервером, сумки совпадают, отказов нет.
        /// </summary>
        [Fact]
        public void E215_Net_Floor_Wall_Chest_Prediction_Equals_Server()
        {
            WorldModel world = Flat();
            using var rig = new EditTests.EditRig(world, _ => At(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(5);
            var inv = rig.Sim.GetEntity(c.EntityId).Inv;
            inv.SetCell(5, Items.StoneWall, 20);
            inv.SetCell(6, Items.WoodFloor, 40);
            inv.SetCell(7, Items.Chest, 2);
            Assert.True(rig.Sim.SetInventory(c.EntityId, inv));
            rig.Run(3);

            WorldPos self = rig.Sim.GetEntity(c.EntityId).Mover.Position;
            // Стена — западные половины тайлов 302…302 вниз (вертикаль x = 302, ряды 199…201).
            c.Place(new TileCoord(302, 199), new TileCoord(302, 201), ObjectId.StoneWall, self, 5, 0 | (2 << 2));
            rig.Run(4);
            // Пол 3 × 3 тайла целиком (под стеной тоже): 36 ячеек — по полосе на ряд тайлов.
            for (int y = 199; y <= 201; y++)
            {
                c.Place(new TileCoord(301, y), new TileCoord(303, y), BuildDefs.WoodFloorKey, self, 6);
            }

            rig.Run(4);
            // Сундук вплотную к стене с востока: СЗ ячейка — восточная половина тайла 302.
            c.Place(new TileCoord(302, 200), new TileCoord(302, 200), ObjectId.Chest, self, 7, 1);
            rig.Run(6);

            Assert.All(replies, r => Assert.Equal(EditReply.Accepted, r.Reply));
            Assert.Equal(5, replies.Count);
            for (int x = 299; x <= 305; x++)
            {
                for (int y = 197; y <= 203; y++)
                {
                    var t = new TileCoord(x, y);
                    Assert.Equal(rig.Sim.World.GetFloor(t), c.World!.World.GetFloor(t));
                    Assert.Equal(rig.Sim.World.GetObject(t), c.World.World.GetObject(t));
                    bool a = rig.Sim.World.TryGetBuild(t, out BuildRecord ra), b = c.World.World.TryGetBuild(t, out BuildRecord rb);
                    Assert.Equal(a, b);
                    if (a)
                    {
                        Assert.Equal(ra.FloorMask, rb.FloorMask);
                        Assert.Equal(ra.Cells, rb.Cells);
                        Assert.Equal(ra.Layout, rb.Layout);
                    }
                }
            }

            Assert.Equal(ObjectId.Chest, rig.Sim.World.GetObject(new TileCoord(303, 200)));
            Assert.Equal(FloorId.WoodFloor, rig.Sim.World.GetFloor(new TileCoord(302, 200)));
            Assert.Equal(ObjectId.StoneWall, rig.Sim.World.GetObject(new TileCoord(302, 200)));
            Assert.Equal(rig.Sim.GetEntity(c.EntityId).Inv, c.Inv);
            Assert.Equal(20 - 6, c.Inv.CountIn(5));
            Assert.Equal(40 - 36, c.Inv.CountIn(6));
            Assert.Equal(1, c.Inv.CountIn(7));
        }

        // ------------------------------------------------ старые миры и байты

        /// <summary>
        /// Блок чанка старого формата (записи по 8 байт) расширяется: пол — весь тайл, мебель без сдвига, хэш состояния — тот же,
        /// что у мира, где байт слоёв 0.
        /// </summary>
        [Fact]
        public void E215_Old_Blob_Widened_Floor_Full_Furniture_Aligned_SameHash()
        {
            var chunk = new Chunk();
            chunk.SetBaseTile(5, FloorId.WoodFloor, ObjectId.Empty);
            chunk.SetBaseBuild(5, new BuildRecord(1, 100, 0, FloorId.Grass, 0));
            chunk.SetBaseTile(6, FloorId.Grass, ObjectId.Chest);
            chunk.SetBaseBuild(6, new BuildRecord(1, 100, 0, FloorId.Grass, 0));
            var blob = new byte[chunk.BlobBytes];
            chunk.WriteBlob(blob);

            // Старый блок: те же записи без 9-го байта.
            var old = new List<byte>(blob.AsSpan(0, Chunk.MinBlobBytes).ToArray());
            for (int i = 0; i < 2; i++)
            {
                old.AddRange(blob.AsSpan(Chunk.MinBlobBytes + i * Chunk.BuildEntryBytes, 2 + BuildRecord.LegacySizeBytes).ToArray());
            }

            byte[] widened = Chunk.WidenLegacyBlob(old.ToArray());
            Assert.Equal(blob, widened);
            Assert.Equal(blob, Chunk.WidenLegacyBlob(blob)); // новый блок — как есть
            var back = new Chunk();
            back.ReadBlob(widened);
            Assert.True(back.TryGetBuild(5, out BuildRecord f));
            Assert.Equal(0, f.Layout);
            Assert.Equal(BuildCells.Full, BuildLayers.FloorCellsOf(FloorId.WoodFloor, true, f));
            Assert.True(back.TryGetBuild(6, out BuildRecord ch));
            Assert.Equal(BuildCells.Full, BuildLayers.OwnCells(ObjectId.Chest, true, ch));
            Assert.Equal(0, back.OffsetCount);
        }

        /// <summary>Байт слоёв: маска пола и сдвиг по 2 бита со знаком — запись и чтение 9 байт, сдвиг −1…+1 по осям.</summary>
        [Fact]
        public void E215_Record_Layout_Bits()
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    BuildRecord r = BuildRecord.OfPlayer(7, FloorId.Sand).WithFloorMask(9).WithOffset(dx, dy).WithCells(5);
                    var b = new byte[BuildRecord.SizeBytes];
                    r.Write(b);
                    BuildRecord back = BuildRecord.Read(b);
                    Assert.Equal(r, back);
                    Assert.Equal(dx, back.OffsetX);
                    Assert.Equal(dy, back.OffsetY);
                    Assert.Equal(9, back.FloorMask);
                    Assert.Equal(5, back.Cells);
                    Assert.Equal(FloorId.Sand, back.NaturalFloor);
                    Assert.Equal(dx != 0 || dy != 0, back.HasOffset);
                    BuildRecord over = BuildRecord.Over(back, 3, 50, 0);
                    Assert.Equal(9, over.FloorMask);
                    Assert.False(over.HasOffset);
                    Assert.Equal(FloorId.Sand, over.NaturalFloor);
                }
            }
        }
    }
}
