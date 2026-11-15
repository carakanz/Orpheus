using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-013 (слова Автора 15.11: «Крафтится он если я нажимаю на землю или в рюкзак. Во втором случае предмет крафтится в
    /// рюкзак … Если крафтится 1 предмет … можно делать» при полной сумке): изготовление сразу в ячейку сумки
    /// (<see cref="Simulation.RequestCraftInto"/>, по сети — <see cref="CraftOp.MakeInto"/>) и постановка из меню при полной
    /// сумке (выход 1 — ставится, остаток от выхода 2/4 некуда положить — «сумка полна», ничего не снято).
    /// </summary>
    public sealed class C013CraftIntoTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat(ulong seed = 1313) => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), seed);

        private static WorldPos At(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        private static int Me(Simulation sim, Inventory bag)
        {
            int me = sim.AddEntity(At(100, 100));
            Assert.True(sim.SetInventory(me, bag));
            return me;
        }

        // Сумка: названные ячейки, остальные — «забиты» стопками filler × 99.
        private static Inventory Full(byte filler, params (int Cell, byte Item, int N)[] cells)
        {
            var bag = new Inventory();
            for (int c = 0; c < Inventory.Cells; c++)
            {
                bag.SetCell(c, filler, ItemTable.MaxStackOf(filler));
            }

            foreach (var (cell, item, n) in cells)
            {
                bag.SetCell(cell, item, n);
            }

            return bag;
        }

        private static int Count(Simulation sim, int me, byte item) => sim.GetEntity(me).Inv.Count(item);

        private static long Mass(in Inventory inv)
        {
            long m = 0;
            for (int c = 0; c < Inventory.Cells; c++)
            {
                m += inv.CountIn(c);
            }

            return m;
        }

        private static CraftReply LastCraft(Simulation sim, int me, out int units)
        {
            units = 0;
            CraftReply r = (CraftReply)255;
            foreach (SimEvent e in sim.Events)
            {
                if (e.Requester != me)
                {
                    continue;
                }

                if (e.Kind == SimEventKind.CraftMade)
                {
                    r = CraftReply.Accepted;
                    units = e.Damage;
                }
                else if (e.Kind == SimEventKind.CraftRefused)
                {
                    r = (CraftReply)e.Tag;
                }
            }

            return r;
        }

        private static int Recipe(byte item) => CraftRules.RecipeFor(item);

        // Постановка «из меню» (ячейка заявки CraftCell) по плану ядра, такт; ответ такта.
        private static EditResult PlaceFromMenu(Simulation sim, int me, ushort key, TileCoord t)
        {
            var plan = new PlannedEdit[EditRules.MaxLineTiles];
            int n = BuildPlanner.Plan(sim.World, sim.GetEntity(me).Mover.Position, key, t, t, 0, (ushort)me, plan);
            Assert.True(n > 0);
            Assert.Equal(EditReply.Accepted, plan[0].Check);
            Assert.True(sim.RequestPlayerEdit(plan[0].Tile, plan[0].Floor, plan[0].Obj, true, plan[0].Record, me, 0, Simulation.CraftCell, plan[0].FloorOnly));
            sim.Step();
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

        // ------------------------------------------------ изготовление в ячейку

        /// <summary>Щелчок по пустой ячейке 20: кирка ровно там, ингредиенты сняты по рецепту, сразу (без очереди).</summary>
        [Fact]
        public void C013_Into_Empty_Cell_Exact_Cell_Instant()
        {
            var sim = new Simulation(Flat());
            var bag = new Inventory();
            bag.Add(Items.Stone, 5);
            bag.Add(Items.Wood, 5);
            int me = Me(sim, bag);
            Assert.Equal(CraftReply.Accepted, sim.RequestCraftInto(me, Recipe(Items.StonePickaxe), 20, 1));
            sim.Step();
            Assert.Equal(CraftReply.Accepted, LastCraft(sim, me, out int units));
            Assert.Equal(1, units);
            Inventory after = sim.GetEntity(me).Inv;
            Assert.Equal(Items.StonePickaxe, after.ItemAt(20));
            Assert.Equal(1, after.CountIn(20));
            Assert.Equal(2, after.Count(Items.Stone));
            Assert.Equal(3, after.Count(Items.Wood));
            Assert.True(sim.CraftQueueOf(me).IsEmpty);
        }

        /// <summary>
        /// Ячейка с той же стопкой — доукладка, сколько влезет (факелы: 97 + 5 просили → 2, остальное не изготовлено);
        /// ячейка с другим предметом — «некуда», ничего не снято.
        /// </summary>
        [Fact]
        public void C013_Into_Same_Stack_Up_To_Room_Other_Item_NoRoom()
        {
            var sim = new Simulation(Flat());
            var bag = new Inventory();
            bag.SetCell(12, Items.Torch, 97);
            bag.SetCell(13, Items.Rope, 1);
            bag.Add(Items.Wood, 10);
            bag.Add(Items.Resin, 10);
            int me = Me(sim, bag);
            Assert.Equal(CraftReply.Accepted, sim.RequestCraftInto(me, Recipe(Items.Torch), 12, 5));
            sim.Step();
            Assert.Equal(CraftReply.Accepted, LastCraft(sim, me, out int units));
            Assert.Equal(2, units);
            Assert.Equal(99, sim.GetEntity(me).Inv.CountIn(12));
            Assert.Equal(8, Count(sim, me, Items.Wood));

            Assert.Equal(CraftReply.NoRoom, sim.CheckCraftInto(me, Recipe(Items.Torch), 13, 1, out _));
            Assert.Equal(CraftReply.NoRoom, sim.RequestCraftInto(me, Recipe(Items.Torch), 13, 1));
            Assert.Equal(CraftReply.NoRoom, sim.RequestCraftInto(me, Recipe(Items.Torch), 12, 1)); // стопка полна
            Assert.Equal(8, Count(sim, me, Items.Wood));
            Assert.Equal(1, sim.GetEntity(me).Inv.CountIn(13));
        }

        /// <summary>
        /// Полная сумка, ингредиенты кирки — ровно в ячейках 10 и 11: изготовление в ячейку 10 проходит (ингредиенты её
        /// освобождают); в забитую ячейку — «некуда». Масса: 5 ингредиентов → 1 кирка.
        /// </summary>
        [Fact]
        public void C013_Into_Cell_Freed_By_Ingredients_Full_Bag()
        {
            var sim = new Simulation(Flat());
            Inventory bag = Full(Items.Fiber, (10, Items.Stone, 3), (11, Items.Wood, 2));
            int me = Me(sim, bag);
            long before = Mass(bag);
            Assert.Equal(CraftReply.NoRoom, sim.RequestCraftInto(me, Recipe(Items.StonePickaxe), 20, 1));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraftInto(me, Recipe(Items.StonePickaxe), 10, 1));
            sim.Step();
            Inventory after = sim.GetEntity(me).Inv;
            Assert.Equal(Items.StonePickaxe, after.ItemAt(10));
            Assert.Equal(0, after.CountIn(11));
            Assert.Equal(before - 5 + 1, Mass(after));
            Assert.Equal(0, sim.Piles.Length);
        }

        /// <summary>«Куда влезет» (Enter): с доукладкой; полная сумка без освобождения — «некуда», на землю ничего не падает.</summary>
        [Fact]
        public void C013_Into_Any_Cell_And_Full_Bag_NoRoom()
        {
            var sim = new Simulation(Flat());
            Inventory bag = Full(Items.Fiber, (10, Items.Stone, 50), (11, Items.Wood, 50));
            int me = Me(sim, bag);
            Assert.Equal(CraftReply.NoRoom, sim.RequestCraftInto(me, Recipe(Items.StonePickaxe), Simulation.AnyBagCell, 1));
            bag.SetCell(30, Items.None, 0);
            Assert.True(sim.SetInventory(me, bag));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraftInto(me, Recipe(Items.StonePickaxe), Simulation.AnyBagCell, 3));
            sim.Step();
            Assert.Equal(CraftReply.Accepted, LastCraft(sim, me, out int units));
            Assert.Equal(1, units); // кирка — стопка 1, свободна одна ячейка
            Assert.Equal(Items.StonePickaxe, sim.GetEntity(me).Inv.ItemAt(30));
            Assert.Equal(0, sim.Piles.Length);
        }

        /// <summary>Станковый рецепт без станка — «нет станка»; мёртвому — «мёртв»; неверная ячейка — «неверно».</summary>
        [Fact]
        public void C013_Into_Refusals()
        {
            var sim = new Simulation(Flat());
            var bag = new Inventory();
            bag.Add(Items.Wood, 20);
            int me = Me(sim, bag);
            Assert.Equal(CraftReply.NoStation, sim.RequestCraftInto(me, Recipe(Items.WoodenSword), 20, 1));
            Assert.Equal(CraftReply.BadCount, sim.RequestCraftInto(me, Recipe(Items.Chest), 40, 1));
            Assert.Equal(CraftReply.BadCount, sim.RequestCraftInto(me, Recipe(Items.Chest), 20, 0));
            Assert.Equal(CraftReply.NoIngredients, sim.RequestCraftInto(me, Recipe(Items.Bed), 20, 1));
            Assert.Equal(20, Count(sim, me, Items.Wood));
        }

        // ------------------------------------------------ постановка из меню при полной сумке

        /// <summary>
        /// Полная сумка (ни одной свободной ячейки и после снятия материалов), кровать — выход 1: ставится из меню, материалы
        /// сняты по рецепту. Пункт 5 толкования.
        /// </summary>
        [Fact]
        public void C013_Menu_Full_Bag_Output_1_Places()
        {
            var sim = new Simulation(Flat());
            Inventory bag = Full(Items.Stone, (10, Items.Wood, 99), (11, Items.Fiber, 99));
            int me = Me(sim, bag);
            Assert.Equal(0, sim.GetEntity(me).Inv.RoomFor(Items.Bed));
            var t = new TileCoord(102, 100);
            Assert.Equal(EditResult.Applied, PlaceFromMenu(sim, me, ObjectId.Bed, t));
            Assert.Equal(ObjectId.Bed, ObjectId.IdOf(sim.World.GetObject(t)));
            Assert.Equal(89, Count(sim, me, Items.Wood));
            Assert.Equal(94, Count(sim, me, Items.Fiber));
            Assert.Equal(0, sim.Piles.Length);
        }

        /// <summary>
        /// Полная сумка, каменная стена — выход 2: остаток некуда положить — «сумка полна», камень не снят, стены нет; клиентский
        /// призрак говорит то же (<see cref="MenuHand.Check"/>). Освободили ячейку — ставится, остаток в сумке.
        /// </summary>
        [Fact]
        public void C013_Menu_Full_Bag_Output_2_BagFull_Then_Places_With_Room()
        {
            var sim = new Simulation(Flat());
            Inventory bag = Full(Items.Stone);
            int me = Me(sim, bag);
            int stone = Count(sim, me, Items.Stone);
            var t = new TileCoord(102, 100);

            WorldPos self = sim.GetEntity(me).Mover.Position;
            int avail = MenuHand.Available(sim.World, self, bag, null, Items.StoneWall);
            BuildCheck ghost = MenuHand.Check(sim.World, self, bag, 0, Items.StoneWall, avail, true, t, 0, 0);
            Assert.Equal(EditReply.BagFull, ghost.Reply);

            Assert.Equal(EditResult.BagFull, PlaceFromMenu(sim, me, ObjectId.StoneWall, t));
            Assert.Equal(EditReply.BagFull, EditRules.FromResult(EditResult.BagFull));
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(t));
            Assert.Equal(stone, Count(sim, me, Items.Stone));
            Assert.Equal(0, sim.Piles.Length);

            bag.SetCell(39, Items.None, 0);
            Assert.True(sim.SetInventory(me, bag));
            Assert.True(MenuHand.Check(sim.World, self, bag, 0, Items.StoneWall, avail, true, t, 0, 0).Ok);
            Assert.Equal(EditResult.Applied, PlaceFromMenu(sim, me, ObjectId.StoneWall, t));
            Assert.Equal(1, Count(sim, me, Items.StoneWall));
            Assert.Equal(stone - 99 - 2, Count(sim, me, Items.Stone));
        }

        /// <summary>
        /// Расчёт остатка: стена в ячейку без готовых и без места — нет; две ячейки стены (2 = выход) — остатка нет; готовая
        /// стена освобождает свою ячейку под остаток; стопка стен неполна — остаток в неё; выход 1 — всегда.
        /// </summary>
        [Fact]
        public void C013_MenuLeftoverFits_Cases()
        {
            Inventory bag = Full(Items.Stone);
            Assert.False(CraftRules.MenuLeftoverFits(bag, Items.StoneWall, 1));
            Assert.True(CraftRules.MenuLeftoverFits(bag, Items.StoneWall, 2));
            Assert.True(CraftRules.MenuLeftoverFits(bag, Items.Bed, 1));
            bag.SetCell(5, Items.StoneWall, 1);
            Assert.True(CraftRules.MenuLeftoverFits(bag, Items.StoneWall, 2)); // готовая уходит, её ячейка — под остаток
            bag.SetCell(5, Items.StoneWall, 50);
            bag.SetCell(6, Items.StoneWall, 1);
            Assert.True(CraftRules.MenuLeftoverFits(bag, Items.StoneWall, 52)); // 51 готовая + 1 изготовление → остаток 1 в стопку
            bag.SetCell(5, Items.StoneWall, 99);
            Assert.True(CraftRules.MenuLeftoverFits(bag, Items.StoneWall, 1)); // готовых хватает
        }

        // ------------------------------------------------ протокол и сеть

        /// <summary>Заявка 30 с ячейкой: туда и обратно; ячейка у «изготовить в очередь» или вне 0…39 — ошибка протокола.</summary>
        [Fact]
        public void C013_Protocol_MakeInto_RoundTrip_And_Bad_Cell()
        {
            var buf = new byte[16];
            int n = new CraftRequestMessage { Op = CraftOp.MakeInto, Arg = 3, Count = 5, Cell = 17 }.Write(buf);
            Assert.Equal(CraftRequestMessage.Bytes, n);
            CraftRequestMessage m = CraftRequestMessage.Read(buf.AsSpan(0, n));
            Assert.Equal(CraftOp.MakeInto, m.Op);
            Assert.Equal(17, m.Cell);
            Assert.Equal(5, m.Count);

            n = new CraftRequestMessage { Op = CraftOp.Make, Arg = 3, Count = 5, Cell = 17 }.Write(buf);
            Assert.Equal(0xFF, CraftRequestMessage.Read(buf.AsSpan(0, n)).Cell); // у очереди ячейка не едет

            buf[n - 1] = 17;
            byte[] bad = (byte[])buf.Clone();
            Assert.Throws<ProtocolException>(() => CraftRequestMessage.Read(bad.AsSpan(0, n)));
            n = new CraftRequestMessage { Op = CraftOp.MakeInto, Arg = 3, Count = 5, Cell = 40 }.Write(buf);
            byte[] bad2 = (byte[])buf.Clone();
            Assert.Throws<ProtocolException>(() => CraftRequestMessage.Read(bad2.AsSpan(0, n)));

            n = new CraftReplyMessage { Op = CraftOp.MakeInto, Arg = 3, Reply = CraftReply.NoRoom }.Write(buf);
            Assert.Equal(CraftReply.NoRoom, CraftReplyMessage.Read(buf.AsSpan(0, n)).Reply);
        }

        /// <summary>
        /// По сети через <see cref="CraftDesk.MakeInto"/>: кирка — в ячейку 25 сумки сервера и клиента, ответ «принято»
        /// (операция 3); в занятую ячейку — ответ «некуда», сумка не изменилась.
        /// </summary>
        [Fact]
        public void C013_Net_CraftDesk_MakeInto_Cell()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            WorldPos spot = At(300, 200);
            using var rig = new EditTests.EditRig(world, _ => spot);
            NetClient c = rig.Join();
            rig.Run(10);
            var bag = new Inventory();
            bag.Add(Items.Stone, 6);
            bag.Add(Items.Wood, 4);
            bag.SetCell(26, Items.Fiber, 3);
            Assert.True(rig.Sim.SetInventory(c.EntityId, bag));
            rig.Run(4);

            var desk = new CraftDesk(c);
            int recipe = Recipe(Items.StonePickaxe);
            CraftLine line = CraftDesk.LineOf(recipe, c.Inv, c.Craft, c.World!.World, spot);
            Assert.True(desk.MakeInto(line, 25));
            rig.Run(6);
            Assert.Equal(Items.StonePickaxe, rig.Sim.GetEntity(c.EntityId).Inv.ItemAt(25));
            Assert.Equal(Items.StonePickaxe, c.Inv.ItemAt(25));
            Assert.Equal(CraftOp.MakeInto, c.LastCraftReply.Op);
            Assert.Equal(CraftReply.Accepted, c.LastCraftReply.Reply);
            Assert.True(c.Craft.IsEmpty);

            line = CraftDesk.LineOf(recipe, c.Inv, c.Craft, c.World.World, spot);
            Assert.True(desk.MakeInto(line, 26));
            rig.Run(6);
            Assert.Equal(CraftOp.MakeInto, c.LastCraftReply.Op);
            Assert.Equal(CraftReply.NoRoom, c.LastCraftReply.Reply);
            Assert.Equal(3, c.Inv.Count(Items.Stone));
            Assert.Equal(Items.Fiber, c.Inv.ItemAt(26));
            Assert.Equal(2, desk.MakeIntoRequests);
        }
        // ------------------------------------------------ п. 8: постановка с курсора из сумки

        /// <summary>
        /// П. 8 (приёмка Автора 15.11: «установка с инвентаря не работает. Только с панели внизу»): постройка, взятая из окна
        /// сумки (ячейка 20, вне панели 0…7) на курсор, — щелчок в мир идёт в мир (<see cref="PointerRoute.World"/>), а не
        /// выбрасывает; не-постройка на курсоре и постройка из сундука — по-прежнему «выбросить».
        /// </summary>
        [Fact]
        public void C013_Cursor_Building_Routes_To_World_Not_Drop()
        {
            Assert.Equal(PointerRoute.World, UiPointer.Route(overUi: false, holding: true, captured: false, placeable: true));
            Assert.Equal(PointerRoute.Drop, UiPointer.Route(false, true, false, placeable: false));
            Assert.Equal(PointerRoute.Ui, UiPointer.Route(true, true, false, placeable: true));
            Assert.Equal(PointerRoute.Ui, UiPointer.Route(false, true, true, placeable: true));
            Assert.Equal(PointerRoute.Idle, UiPointer.Route(false, false, false, false, armed: true, armedBuilds: false));
            Assert.Equal(PointerRoute.World, UiPointer.Route(false, false, false, false, armed: true, armedBuilds: true));
            Assert.True(UiPointer.Blocks(PointerRoute.Idle));
            Assert.False(UiPointer.Blocks(UiPointer.Route(false, true, false, placeable: true)));

            var hand = new BagHand(new NoBag());
            var bag = new Inventory();
            bag.SetCell(20, Items.StoneWall, 10);
            bag.SetCell(21, Items.Stone, 10);
            hand.Sync(bag, false);
            Assert.Equal(BagClick.Taken, hand.Left(20));
            Assert.True(hand.HoldsPlaceable);
            hand.Cancel();
            Assert.Equal(BagClick.Taken, hand.Left(21));
            Assert.False(hand.HoldsPlaceable); // камень — не постройка
        }

        /// <summary>
        /// На курсоре часть стопки (ПКМ — 2 из 10): сервер снял предмет из её ячейки — в руке остаётся остаток (1), а не
        /// прежнее число; снял последний из руки — рука пуста (дальше ставить с курсора нечем).
        /// </summary>
        [Fact]
        public void C013_Cursor_Hand_Count_Follows_Spent_Items()
        {
            var hand = new BagHand(new NoBag());
            var bag = new Inventory();
            bag.SetCell(20, Items.WoodWall, 10);
            hand.Sync(bag, false);
            Assert.Equal(BagClick.Taken, hand.Right(20));
            Assert.Equal(BagClick.Taken, hand.Right(20));
            Assert.Equal(2, hand.Count);
            bag.SetCell(20, Items.WoodWall, 9);
            hand.Sync(bag, false);
            Assert.Equal(1, hand.Count);
            Assert.Equal(8, hand.ShownCount(20));
            bag.SetCell(20, Items.WoodWall, 8);
            hand.Sync(bag, false);
            Assert.False(hand.Holding);
        }

        /// <summary>
        /// По сети: постройка из ячейки сумки 20 (не панели) — заявка постановки с ячейкой 20 проходит протокол (29: 0…39) и
        /// сервер, кровать стоит, из ячейки 20 снят один предмет; линия стен из ячейки 25 — тоже. Ячейка 40 — ошибка протокола.
        /// </summary>
        [Fact]
        public void C013_Net_Place_From_Bag_Cell_Not_Hotbar()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            WorldPos spot = At(300, 200);
            using var rig = new EditTests.EditRig(world, _ => spot);
            NetClient c = rig.Join();
            rig.Run(10);
            Inventory bag = Inventory.StartingKit();
            bag.SetCell(20, Items.Bed, 3);
            bag.SetCell(25, Items.StoneWall, 10);
            Assert.True(rig.Sim.SetInventory(c.EntityId, bag));
            rig.Run(4);
            Assert.NotEqual(Items.Bed, c.Inv.SlotAt(c.Inv.Selected)); // в руке (панель) — не кровать

            var build = new BuildHand(c);
            var t = new TileCoord(302, 200);
            BuildCheck chk = build.Click(c.World!.World, spot, c.Inv, 20, true, t, 0, 0);
            Assert.True(chk.Ok, chk.Reason);
            rig.Run(6);
            Assert.Equal(ObjectId.Bed, ObjectId.IdOf(rig.Sim.World.GetObject(t)));
            Assert.Equal(2, rig.Sim.GetEntity(c.EntityId).Inv.CountIn(20));
            Assert.Equal(2, c.Inv.CountIn(20));

            Assert.True(build.PlaceLine(new TileCoord(298, 197), new TileCoord(298, 197), spot, c.Inv, 25, 0, 1) != 0);
            rig.Run(6);
            Assert.Equal(ObjectId.StoneWall, ObjectId.IdOf(rig.Sim.World.GetObject(new TileCoord(298, 197))));
            Assert.True(rig.Sim.GetEntity(c.EntityId).Inv.CountIn(25) < 10);

            var buf = new byte[32];
            var map = MapShape.World(1024);
            int n = new TileRequestMessage { RequestId = 1, Op = TileOp.Place, From = t, To = t, Obj = ObjectId.Bed, Cell = 39 }.Write(buf);
            Assert.Equal(39, TileRequestMessage.Read(buf.AsSpan(0, n), map).Cell);
            buf[18] = 40;
            byte[] bad = (byte[])buf.Clone();
            Assert.Throws<ProtocolException>(() => TileRequestMessage.Read(bad.AsSpan(0, n), map));
        }

        /// <summary>
        /// П. 9 (слово Автора 15.11: «F — верстак не нужно»): у верстака и костра нет действия F — ни под курсором, ни ближайшим
        /// (F берёт сундук рядом); рецепты верстака доступны в окне изготовления, когда верстак рядом (строка рецепта — «можно»).
        /// </summary>
        [Fact]
        public void C013_F_Does_Not_Use_Workbench_Recipes_Near_Station()
        {
            var sim = new Simulation(Flat());
            var bag = new Inventory();
            bag.Add(Items.Workbench, 1);
            bag.Add(Items.Campfire, 1);
            bag.Add(Items.Chest, 1);
            bag.Add(Items.Wood, 20);
            int me = Me(sim, bag);
            var bench = new TileCoord(101, 101);
            var fire = new TileCoord(98, 100);
            var chest = new TileCoord(100, 103);
            Assert.Equal(EditResult.Applied, PlaceItem(sim, me, ObjectId.Workbench, bench));
            Assert.Equal(EditResult.Applied, PlaceItem(sim, me, ObjectId.Campfire, fire));
            WorldPos self = sim.GetEntity(me).Mover.Position;
            Assert.Equal(UseKind.None, ObjectUse.KindOf(ObjectId.Workbench));
            Assert.Equal(UseKind.None, ObjectUse.KindOf(ObjectId.Campfire));
            Assert.Equal(UseKind.None, ObjectUse.At(sim.World, self, bench));
            Assert.False(ObjectUse.Find(sim.World, self, bench, out _, out _));

            Assert.Equal(EditResult.Applied, PlaceItem(sim, me, ObjectId.Chest, chest));
            Assert.True(ObjectUse.Find(sim.World, self, bench, out TileCoord t, out UseKind k));
            Assert.Equal((chest, UseKind.Chest), (t, k));

            CraftLine sword = CraftDesk.LineOf(Recipe(Items.WoodenSword), sim.GetEntity(me).Inv, default, sim.World, self);
            Assert.Equal(CraftReply.Accepted, sword.State); // верстак рядом — рецепт верстака в обычном окне
        }

        // Постановка предмета-постройки из сумки (любая ячейка с ним), такт; ответ такта.
        private static EditResult PlaceItem(Simulation sim, int me, ushort key, TileCoord t)
        {
            var plan = new PlannedEdit[EditRules.MaxLineTiles];
            int n = BuildPlanner.Plan(sim.World, sim.GetEntity(me).Mover.Position, key, t, t, 0, (ushort)me, plan);
            Assert.True(n > 0);
            Assert.Equal(EditReply.Accepted, plan[0].Check);
            Assert.True(sim.RequestPlayerEdit(plan[0].Tile, plan[0].Floor, plan[0].Obj, true, plan[0].Record, me, 0, Simulation.AnyCell, plan[0].FloorOnly));
            sim.Step();
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

        private sealed class NoBag : IBagRequests
        {
            public bool RequestBagMove(int from, int to) => true;

            public bool RequestBagSplit(int from, int to, int count) => true;

            public bool RequestBagDrop(int cell, int count = 0) => true;
        }
    }
}
