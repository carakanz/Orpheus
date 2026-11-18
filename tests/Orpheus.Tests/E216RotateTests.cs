using System;
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
    /// E2-16 (слова Автора 15.11: «Предметы нужно разрешить крутить. И разрешить ставить прямо из меню строительства»):
    /// поворот мебели и двери, многоклеточная мебель (кровать 1 × 2, верстак 2 × 2 тайла), постановка из меню с изготовлением
    /// на месте (сервер — <see cref="Simulation.CraftCell"/>, клиент — <see cref="MenuHand"/>).
    /// </summary>
    public sealed class E216RotateTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat(ulong seed = 1616) => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), seed);

        private static WorldPos At(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        // Постановка игрока по плану ядра (как у сервера): правки плана — в очередь такта от сущности me, такт.
        private static int Place(Simulation sim, int me, ushort key, TileCoord from, TileCoord to, int quad, byte cell = Simulation.AnyCell)
        {
            var plan = new PlannedEdit[EditRules.MaxLineTiles];
            int n = BuildPlanner.Plan(sim.World, sim.GetEntity(me).Mover.Position, key, from, to, quad, (ushort)me, plan);
            int ok = 0;
            for (int i = 0; i < n; i++)
            {
                if (plan[i].Check == EditReply.Accepted
                    && sim.RequestPlayerEdit(plan[i].Tile, plan[i].Floor, plan[i].Obj, true, plan[i].Record, me, i, cell, plan[i].FloorOnly))
                {
                    ok++;
                }
            }

            sim.Step();
            return ok;
        }

        // Ответ такта последней правки игрока me (событие EditApplied/Refused с меткой).
        private static EditResult LastResult(Simulation sim, int me)
        {
            EditResult r = EditResult.Applied;
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.TileEdit && e.Requester == me)
                {
                    r = e.Result;
                }
            }

            return r;
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

        private static ushort Bed(int rot) => BuildLayers.Turned(ObjectId.Bed, rot);

        // ------------------------------------------------ поворот и размер

        /// <summary>
        /// Размеры по повороту: кровать — двуспальная 4 × 4 ячейки при любом повороте (C-030; в E2-16 была 2 × 4, 90° — 4 × 2),
        /// верстак 4 × 4, сундук 2 × 2; факел не поворачивается.
        /// </summary>
        [Fact]
        public void E216_Sizes_By_Rotation()
        {
            for (int r = 0; r < 4; r++)
            {
                BuildLayers.SizeOf(Bed(r), out int bw, out int bh);
                Assert.Equal((4, 4), (bw, bh));
            }

            int w, h;
            BuildLayers.SizeOf(BuildLayers.Turned(ObjectId.Workbench, 3), out w, out h);
            Assert.Equal((4, 4), (w, h));
            BuildLayers.SizeOf(BuildLayers.Turned(ObjectId.Chest, 1), out w, out h);
            Assert.Equal((2, 2), (w, h));
            Assert.Equal(ObjectId.Torch, BuildLayers.Turned(ObjectId.Torch, 1)); // круглое — без поворота
            Assert.Equal(BuildDefs.WoodFloorKey, BuildLayers.Turned(BuildDefs.WoodFloorKey, 2));
            Assert.True(EditRules.IsPlaceable(Bed(3)));
            Assert.False(EditRules.IsPlaceable(ObjectId.MakeAnchor(ObjectId.Torch, 1)));
            Assert.True(BuildDefs.TryGet(Bed(1), out BuildDef def));
            Assert.Equal(Items.Bed, def.Item);
        }

        /// <summary>
        /// C-030: кровать двуспальная 2 × 2 тайла при любом повороте — якорь, южный, восточный и юго-восточный тайлы твёрдые
        /// целиком, дальше свободно. Поворот сохраняется в объекте мира (вид); запись — флаг Wide; хвосты ведут к якорю; центр —
        /// середина 2 × 2; сундук в любой хвост — «занято». (E2-16: была 1 × 2, поворот менял занятые тайлы.)
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void E216_Bed_Rotation_Changes_Occupied_Cells(int rot)
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Bed, 1));
            var t = new TileCoord(102, 100);
            Assert.Equal(1, Place(sim, me, Bed(rot), t, t, 0));
            Assert.Equal(Bed(rot), sim.World.GetObject(t));
            Assert.True(sim.World.TryGetBuild(t, out BuildRecord rec));
            Assert.True((rec.Flags & (byte)BuildFlags.Wide) != 0);
            var tails = new[] { new TileCoord(102, 101), new TileCoord(103, 100), new TileCoord(103, 101) };
            Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, t));
            var plan = new PlannedEdit[EditRules.MaxLineTiles];
            foreach (TileCoord second in tails)
            {
                Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, second));
                Assert.Equal(ObjectId.Empty, sim.World.GetObject(second)); // объект — только в якоре
                Assert.True(BuildLayers.TryTopOfCell(sim.World, BuildCells.CellOf(second, 1, 1), out TileCoord top));
                Assert.Equal(t, top);
                // Сундук в хвост — «занято».
                BuildPlanner.Plan(sim.World, sim.GetEntity(me).Mover.Position, ObjectId.Chest, second, second, 0, (ushort)me, plan);
                Assert.Equal(EditReply.Busy, plan[0].Check);
            }

            foreach (TileCoord free in new[] { new TileCoord(104, 100), new TileCoord(102, 102), new TileCoord(101, 100), new TileCoord(102, 99) })
            {
                Assert.Equal(0, sim.SolidCells(sim.Shape, free));
            }

            WorldPos c = BuildLayers.CenterAt(sim.World, t);
            Assert.Equal(At(102, 100).X + U / 2, c.X);
            Assert.Equal(At(102, 100).Y + U / 2, c.Y);
        }

        /// <summary>
        /// Путь врагов видит кровать: в стене проём 2 тайла (ряды 50–51). C-030: двуспальная 2 × 2 в проёме (якорь 49/50 — тайлы
        /// 49–50 × 50–51) закрывает его при любом повороте — пути нет; та же кровать севернее и западнее (48/47 — тайлы 48–49 × 47–48, у стены
        /// снаружи проёма) проём не трогает — путь есть. (E2-16: кровать 1 × 2 — вдоль закрывала, поперёк нет.)
        /// </summary>
        [Theory]
        [InlineData(0, false)]
        [InlineData(1, false)]
        [InlineData(4, true)]
        public void E216_Path_Sees_Rotated_Bed(int rot, bool passable)
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 46, 50, (Items.Bed, 1));
            for (int y = 10; y <= 90; y++)
            {
                if (y != 50 && y != 51)
                {
                    Assert.True(sim.RequestEdit(new TileCoord(50, y), FloorId.Grass, ObjectId.StoneWall, BuildRecord.OfPlayer(1, FloorId.Grass)));
                }
            }

            sim.Step();
            TileCoord at = rot < 4 ? new TileCoord(49, 50) : new TileCoord(48, 47);
            rot &= 3;
            Assert.Equal(1, Place(sim, me, Bed(rot), at, at, 0));
            var terrain = new SimulationPathTerrain(sim);
            var finder = new PathFinder(enclosureFillLimit: 0);
            var path = new PathBuffer();
            PathStatus status = finder.Find(terrain, At(45, 53), At(55, 50), PathMode.Walker, path);
            Assert.Equal(passable ? PathStatus.Found : PathStatus.Partial, status);
            for (int i = 0; i < path.Length; i++)
            {
                Assert.False(Collision.TouchesSolid(sim.Shape, path.Points[i], sim.Solid), $"точка {i} задевает твёрдое");
            }
        }

        /// <summary>
        /// Верстак 2 × 2 тайла: 4 тайла твёрдые, со сдвигом на полтайла — 9 тайлов задеты (края — половинами); у границы чанка
        /// (якорь 31/31) свес в соседние чанки виден; снос по дальнему тайлу снимает верстак целиком, возврат — 1 предмет.
        /// </summary>
        [Fact]
        public void E216_Workbench_2x2_Across_Chunks_Demolish_Any_Tile()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 29, 29, (Items.Workbench, 2));
            var a = new TileCoord(31, 31);
            Assert.Equal(1, Place(sim, me, ObjectId.Workbench, a, a, 0));
            foreach (var t in new[] { a, new TileCoord(32, 31), new TileCoord(31, 32), new TileCoord(32, 32) })
            {
                Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, t));
            }

            Assert.Equal(0, sim.SolidCells(sim.Shape, new TileCoord(33, 31)));
            // Второй — со сдвигом на полтайла (СЗ ячейка — восточная половина тайла 26/26): задевает 3 × 3 тайла.
            var b = new TileCoord(26, 26);
            Assert.Equal(1, Place(sim, me, ObjectId.Workbench, b, b, 3));
            Assert.True(sim.World.TryGetBuild(b, out BuildRecord rb));
            Assert.Equal((1, 1), (rb.OffsetX, rb.OffsetY));
            Assert.Equal(BuildCells.Bit(1, 1), sim.SolidCells(sim.Shape, b));
            Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, new TileCoord(27, 27)));
            Assert.Equal(BuildCells.Bit(0, 0), sim.SolidCells(sim.Shape, new TileCoord(28, 28)));
            Assert.Equal(BuildCells.Bit(0, 0) | BuildCells.Bit(0, 1), sim.SolidCells(sim.Shape, new TileCoord(28, 27)));

            // Снос по дальнему тайлу (32/32 — его якорь 31/31).
            Assert.True(BuildLayers.TryTopOfCell(sim.World, BuildCells.CellOf(new TileCoord(32, 32), 3), out TileCoord top));
            Assert.Equal(a, top);
            BuildLayers.AfterDig(sim.World, top, out byte fl, out bool keep, out BuildRecord rest);
            Assert.True(sim.RequestPlayerEdit(top, fl, ObjectId.Empty, keep, rest, me, 0));
            sim.Step();
            Assert.Equal(0, sim.SolidCells(sim.Shape, new TileCoord(32, 32)));
            int piles = 0;
            foreach (GroundPile p in sim.Piles)
            {
                piles += p.Item == Items.Workbench ? p.Count : 0;
            }

            Assert.Equal(1, piles);
        }

        /// <summary>Станок 2 × 2 работает от центра предмета: рецепт у верстака, персонаж у дальнего края — в радиусе.</summary>
        [Fact]
        public void E216_Workbench_Station_From_Center()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Workbench, 1));
            Assert.Equal(1, Place(sim, me, ObjectId.Workbench, new TileCoord(102, 100), new TileCoord(102, 100), 0));
            // Центр верстака — угол тайлов (103, 101); 3 тайла от него — 106 по X.
            Assert.Equal(CraftReply.Accepted, CraftRules.StationCheck(sim.World, new WorldPos(106 * U, 101 * U), CraftStation.Workbench));
        }

        // ------------------------------------------------ двери

        /// <summary>
        /// Дверь: в проёме вертикальной стены (стены сверху и снизу) — вертикальная пара при любом повороте; без соседей поворот
        /// задаёт ориентацию (0 — горизонтальная, 90° — вертикальная). В мир дверь идёт без поворота.
        /// </summary>
        [Fact]
        public void E216_Door_Orients_Itself_In_Wall_Rotation_Only_When_Free()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Door, 3), (Items.StoneWall, 10));
            // Вертикальная стена x = 103 (западные половины), проём на 103/101.
            Assert.Equal(1, Place(sim, me, ObjectId.StoneWall, new TileCoord(103, 100), new TileCoord(103, 100), 0 | (2 << 2)));
            Assert.Equal(1, Place(sim, me, ObjectId.StoneWall, new TileCoord(103, 102), new TileCoord(103, 102), 0 | (2 << 2)));
            var gap = new TileCoord(103, 101);
            Assert.Equal(1, Place(sim, me, BuildLayers.Turned(ObjectId.Door, 0), gap, gap, 0));
            Assert.Equal(ObjectId.Door, sim.World.GetObject(gap));
            Assert.Equal(BuildCells.DoorWest, BuildCells.MaskAt(sim.World, gap));

            var free0 = new TileCoord(98, 104);
            var free1 = new TileCoord(98, 98);
            Assert.Equal(1, Place(sim, me, BuildLayers.Turned(ObjectId.Door, 0), free0, free0, 2));
            Assert.Equal(BuildCells.DoorSouth, BuildCells.MaskAt(sim.World, free0));
            Assert.Equal(1, Place(sim, me, BuildLayers.Turned(ObjectId.Door, 1), free1, free1, 1));
            Assert.Equal(ObjectId.Door, sim.World.GetObject(free1));
            Assert.Equal(BuildCells.DoorEast, BuildCells.MaskAt(sim.World, free1));
        }

        // ------------------------------------------------ постановка из меню (изготовление на месте)

        private static int Count(Simulation sim, int me, byte item) => sim.GetEntity(me).Inv.Count(item);

        /// <summary>
        /// Из меню без готовой кровати: материалы сумки сняты ровно по рецепту (10 древесины + 5 волокна), кровать стоит, предмета
        /// «кровать» в сумке не появилось. Второй раз — материалов нет: «нет ресурсов», ничего не снято, кровати нет.
        /// </summary>
        [Fact]
        public void E216_Menu_Crafts_On_Place_Exact_Recipe_Then_NoResources()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Wood, 14), (Items.Fiber, 5));
            var t = new TileCoord(102, 100);
            Assert.Equal(1, Place(sim, me, Bed(1), t, t, 0, Simulation.CraftCell));
            Assert.Equal(EditResult.Applied, LastResult(sim, me));
            Assert.Equal(Bed(1), sim.World.GetObject(t));
            Assert.Equal(4, Count(sim, me, Items.Wood));
            Assert.Equal(0, Count(sim, me, Items.Fiber));
            Assert.Equal(0, Count(sim, me, Items.Bed));

            var t2 = new TileCoord(102, 103);
            Assert.Equal(1, Place(sim, me, Bed(0), t2, t2, 0, Simulation.CraftCell)); // план принят, такт отказал
            Assert.Equal(EditResult.NoResources, LastResult(sim, me));
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(t2));
            Assert.Equal(4, Count(sim, me, Items.Wood));
        }

        /// <summary>Из меню с готовым предметом в сумке (не в руке): берётся он, материалы не тронуты.</summary>
        [Fact]
        public void E216_Menu_Uses_Ready_Item_From_Any_Cell_First()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Wood, 30), (Items.Chest, 1));
            Assert.Equal(1, Place(sim, me, ObjectId.Chest, new TileCoord(102, 100), new TileCoord(102, 100), 0, Simulation.CraftCell));
            Assert.Equal(EditResult.Applied, LastResult(sim, me));
            Assert.Equal(0, Count(sim, me, Items.Chest));
            Assert.Equal(30, Count(sim, me, Items.Wood));
            Assert.Equal(1, Place(sim, me, ObjectId.Chest, new TileCoord(104, 100), new TileCoord(104, 100), 0, Simulation.CraftCell));
            Assert.Equal(30 - 8, Count(sim, me, Items.Wood)); // второй — изготовлен (8 древесины)
        }

        /// <summary>
        /// Стена из меню по ячейке: рецепт даёт 2, нужна 1 — изготовлен один раз, лишняя стена — в сумке (масса: 2 камня → 2 стены,
        /// одна в постройке, одна в сумке). Линия из 3 ячеек при 1 готовой: готовая + одно изготовление (2).
        /// </summary>
        [Fact]
        public void E216_Menu_Wall_Recipe_Output_2_Extra_To_Bag()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Stone, 10));
            Assert.True(CraftRules.RecipeFor(Items.StoneWall) > 0);
            ref readonly RecipeDef r = ref RecipeTable.Get(CraftRules.RecipeFor(Items.StoneWall));
            Assert.Equal(2, r.ResultCount);
            int per = r.N1;
            var t = new TileCoord(102, 100);
            Assert.Equal(1, Place(sim, me, ObjectId.StoneWall, t, t, 0, Simulation.CraftCell));
            Assert.Equal(EditResult.Applied, LastResult(sim, me));
            Assert.Equal(10 - per, Count(sim, me, Items.Stone));
            Assert.Equal(1, Count(sim, me, Items.StoneWall));
            Assert.Equal(BuildCells.Bit(0, 0), BuildCells.MaskAt(sim.World, t));
        }

        /// <summary>
        /// Станковый рецепт (костёр — у верстака) из меню без верстака рядом: «нужен станок», материалы не сняты; с верстаком —
        /// изготовлен и стоит.
        /// </summary>
        [Fact]
        public void E216_Menu_Station_Recipe_Needs_Station()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Stone, 8), (Items.Wood, 4), (Items.Workbench, 1));
            Assert.NotEqual(CraftStation.None, RecipeTable.Get(CraftRules.RecipeFor(Items.Campfire)).Station);
            var t = new TileCoord(97, 100);
            Assert.Equal(1, Place(sim, me, ObjectId.Campfire, t, t, 0, Simulation.CraftCell));
            Assert.Equal(EditResult.NoStation, LastResult(sim, me));
            Assert.Equal(EditReply.NoStation, EditRules.FromResult(EditResult.NoStation));
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(t));
            Assert.Equal(8, Count(sim, me, Items.Stone));
            Assert.Equal(4, Count(sim, me, Items.Wood));

            Assert.Equal(1, Place(sim, me, ObjectId.Workbench, new TileCoord(101, 101), new TileCoord(101, 101), 0));
            Assert.Equal(1, Place(sim, me, ObjectId.Campfire, t, t, 0, Simulation.CraftCell));
            Assert.Equal(EditResult.Applied, LastResult(sim, me));
            Assert.Equal(ObjectId.Campfire, ObjectId.IdOf(sim.World.GetObject(t)));
            Assert.Equal(0, Count(sim, me, Items.Stone));
            Assert.Equal(0, Count(sim, me, Items.Wood));
        }

        /// <summary>Изготовление на месте берёт ингредиенты и из сундука рядом (сначала сумка).</summary>
        [Fact]
        public void E216_Menu_Takes_From_Near_Chest()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Wood, 3), (Items.Chest, 1));
            var chestTile = new TileCoord(102, 100);
            Assert.Equal(1, Place(sim, me, ObjectId.Chest, chestTile, chestTile, 0));
            var chest = new Inventory();
            chest.Add(Items.Wood, 10);
            chest.Add(Items.Fiber, 5);
            Assert.True(sim.SetChest(chestTile, chest));
            var t = new TileCoord(98, 100);
            Assert.Equal(1, Place(sim, me, Bed(0), t, t, 0, Simulation.CraftCell));
            Assert.Equal(EditResult.Applied, LastResult(sim, me));
            Assert.Equal(0, Count(sim, me, Items.Wood));
            Assert.True(sim.TryGetChest(chestTile, out Inventory after));
            Assert.Equal(3, after.Count(Items.Wood)); // 10 нужно: 3 из сумки + 7 из сундука
            Assert.Equal(0, after.Count(Items.Fiber));
        }

        /// <summary>
        /// Клиент: «есть» для меню — готовые + изготовимые (станок рядом), линейка стены по сумке-копии считает их вместе;
        /// без станка станковый рецепт не считается; нечего поставить — «нет ресурсов» у призрака.
        /// </summary>
        [Fact]
        public void E216_MenuHand_Available_And_Ruler_Counts_Craftable()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Stone, 7), (Items.StoneWall, 3));
            Inventory bag = sim.GetEntity(me).Inv;
            WorldPos self = sim.GetEntity(me).Mover.Position;
            int per = RecipeTable.Get(CraftRules.RecipeFor(Items.StoneWall)).N1;
            int expected = 3 + (7 / per) * 2;
            Assert.Equal(expected, MenuHand.Available(sim.World, self, bag, null, Items.StoneWall));
            Assert.Equal(0, MenuHand.Available(sim.World, self, bag, null, Items.Campfire)); // нет станка и материалов

            Inventory v = MenuHand.VirtualBag(bag, 7, Items.StoneWall, expected);
            Assert.Equal(expected, v.CountIn(7));
            Assert.Equal(expected, v.Count(Items.StoneWall));
            Assert.Equal(7, v.Count(Items.Stone));
            var ruler = new BuildRuler();
            ruler.Begin(new TileCoord(102, 98), 0, Items.StoneWall);
            ruler.Update(sim.World, self, v, 7, true, new TileCoord(102, 104), 2, false);
            Assert.Equal(expected, ruler.Have);
            Assert.True(ruler.Need > 0);

            BuildCheck none = MenuHand.Check(sim.World, self, bag, 7, Items.Bed, 0, true, new TileCoord(102, 100), 0, 1);
            Assert.Equal(EditReply.NoResources, none.Reply);
            Assert.Equal(Bed(1), none.Key);
        }

        // ------------------------------------------------ сеть: предсказание = сервер

        /// <summary>
        /// По сети: повёрнутая кровать из руки (поворот в заявке) и постановка из меню без предмета (флаг меню) — мир клиента
        /// совпадает с сервером (объект с поворотом, свес во второй тайл), материалы сняты у сервера по рецепту.
        /// </summary>
        [Fact]
        public void E216_Net_Rotated_Bed_And_Menu_Placement()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            WorldPos spot = At(300, 200);
            using var rig = new EditTests.EditRig(world, _ => spot);
            NetClient c = rig.Join();
            rig.Run(10);
            Inventory bag = Inventory.StartingKit();
            bag.SetCell(7, Items.Bed, 1);
            Assert.Equal(20, bag.Add(Items.Wood, 20));
            Assert.Equal(5, bag.Add(Items.Fiber, 5));
            Assert.True(rig.Sim.SetInventory(c.EntityId, bag));
            rig.InputOf = _ => new InputFrame(Input8.None, 0, 0, 7);
            rig.Run(4);

            var hand = new BuildHand(c);
            var t = new TileCoord(302, 200);
            BuildCheck chk = hand.Click(c.World!.World, spot, c.Inv, 7, true, t, 0, 1);
            Assert.True(chk.Ok, chk.Reason);
            rig.Run(6);
            Assert.Equal(Bed(1), rig.Sim.World.GetObject(t));
            Assert.Equal(Bed(1), c.World.World.GetObject(t));
            Assert.Equal(BuildCells.Full, rig.Sim.SolidCells(rig.Sim.Shape, new TileCoord(303, 200)));

            // Из меню: кровати больше нет — изготовится из 10 древесины + 5 волокна.
            var menu = new BuildHand(MenuHand.Requests(c));
            int avail = MenuHand.Available(c.World.World, spot, c.Inv, c.NearbyStock, Items.Bed);
            Assert.Equal(1, avail);
            Inventory v = MenuHand.VirtualBag(c.Inv, 7, Items.Bed, avail);
            var t2 = new TileCoord(298, 198);
            BuildCheck m = menu.Click(c.World.World, spot, v, 7, true, t2, 0, 2);
            Assert.True(m.Ok, m.Reason);
            rig.Run(6);
            Assert.Equal(Bed(2), rig.Sim.World.GetObject(t2));
            Assert.Equal(Bed(2), c.World.World.GetObject(t2));
            Assert.Equal(10, rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Wood));
            Assert.Equal(0, rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Fiber));
            Assert.Equal(10, c.Inv.Count(Items.Wood));
        }

        // ------------------------------------------------ сохранение

        /// <summary>
        /// Сохранение 12: повёрнутая кровать и верстак со сдвигом переживают запись и чтение (объект с поворотом, флаг Wide,
        /// свес у соседа), хэш тот же. Старый мир (формат 10, файл E2-15): верстак получил Wide, его второй тайл твёрдый, хэш
        /// прежний.
        /// </summary>
        [Fact]
        public void E216_Save_RoundTrip_And_Old_World_Workbench_Wide()
        {
            byte[] v10 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format10_build.save"));
            byte[] baseBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format10_build.base"));
            Simulation sim = SaveFixtures.Load(baseBytes, v10);
            Assert.Equal("0329ad4c20838f97", StateHash.Compute(sim).Total.ToString("x16")); // E3-6: формат 16
            var bench = new TileCoord(129, 132);
            Assert.Equal(ObjectId.Workbench, sim.World.GetObject(bench));
            Assert.True(sim.World.TryGetBuild(bench, out BuildRecord wr));
            Assert.True((wr.Flags & (byte)BuildFlags.Wide) != 0);
            Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, new TileCoord(130, 133)));

            int me = sim.Entities[0].Id;
            var inv = sim.GetEntity(me).Inv;
            inv.Add(Items.Bed, 1);
            Assert.True(sim.SetInventory(me, inv));
            WorldPos self = sim.GetEntity(me).Mover.Position;
            TileCoord near = Torus.TileOf(sim.Shape, self);
            TileCoord bt = new TileCoord(near.X + 2, near.Y - 3);
            Assert.Equal(1, Place(sim, me, Bed(3), bt, bt, 1));
            Assert.Equal(Bed(3), sim.World.GetObject(bt));
            string hash = StateHash.Compute(sim).Total.ToString("x16");
            byte[] again = WorldSave.Build(WorldSave.Capture(sim, WorldSnapshot.StoredHash(baseBytes), "E2-16"), 7, 0);
            Assert.Equal(16u, SaveContainer.Read(again).Header.FormatVersion); // E2-14: 13
            Simulation back = SaveFixtures.Load(baseBytes, again);
            Assert.Equal(hash, StateHash.Compute(back).Total.ToString("x16"));
            Assert.Equal(Bed(3), back.World.GetObject(bt));
            BuildLayers.SizeOf(back.World.GetObject(bt), out int w, out _);
            Assert.Equal(4, w);
            Assert.Equal(sim.SolidCells(sim.Shape, new TileCoord(bt.X + 1, bt.Y)), back.SolidCells(back.Shape, new TileCoord(bt.X + 1, bt.Y)));
        }
    }
}
