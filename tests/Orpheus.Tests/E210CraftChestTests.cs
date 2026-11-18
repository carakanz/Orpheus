using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-10 (шаг 2.10; <c>BRIEF</c> 11e (2), <c>06</c> 3.5): изготовление из сумки и сундуков поблизости — 6 тайлов от
    /// ближайшего станка рецепта (у ручных — от персонажа), по тору; снятие: сумка, затем сундуки по расстоянию, при
    /// равенстве — по номеру; отмена и смерть — в сумку; сводка «сундуки рядом» по сети (тип 36, протокол 24).
    /// </summary>
    public class E210CraftChestTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const int T = RecipeTable.DefaultTicks;

        private static WorldPos Center(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        /// <summary>Плоский тор 1024²; верстак на <paramref name="bench"/> (null — нет), сундуки — объектами на тайлах.</summary>
        private static WorldModel World(TileCoord? bench, params TileCoord[] chests)
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            if (bench.HasValue)
            {
                world.SetBaseTile(bench.Value, FloorId.Grass, ObjectId.Workbench);
            }

            foreach (TileCoord t in chests)
            {
                world.SetBaseTile(t, FloorId.Grass, ObjectId.Chest);
            }

            return world;
        }

        private static Inventory Kit(params (byte Item, int Count)[] items)
        {
            var inv = new Inventory();
            foreach ((byte item, int count) in items)
            {
                Assert.Equal(count, inv.Add(item, count));
            }

            return inv;
        }

        private static int AddPlayer(Simulation sim, WorldPos at, params (byte Item, int Count)[] bag)
        {
            int id = sim.AddEntity(at);
            Assert.True(sim.SetInventory(id, Kit(bag)));
            return id;
        }

        private static void Run(Simulation sim, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                sim.Step();
            }
        }

        /// <summary>Сундук опустел: записи содержимого нет (объект «сундук» на тайле остаётся).</summary>
        private static bool NoRecord(Simulation sim, TileCoord t) => Array.IndexOf(sim.ChestTiles.ToArray(), t) < 0;

        private static int InChest(Simulation sim, TileCoord t, byte item) => sim.TryGetChest(t, out Inventory c) ? c.Count(item) : 0;

        /// <summary>Всего предмета в мире: сумки, сундуки, кучи, ингредиенты в очередях изготовления.</summary>
        private static int Total(Simulation sim, byte item)
        {
            int n = 0;
            foreach (SimEntity e in sim.Entities)
            {
                n += e.Inv.Count(item);
                for (int i = 0; i < e.Craft.Count; i++)
                {
                    ref readonly RecipeDef def = ref RecipeTable.Get(e.Craft.RecipeAt(i));
                    for (int k = 0; k < def.IngredientCount; k++)
                    {
                        n += def.IngredientItem(k) == item ? def.IngredientCountOf(k) * e.Craft.LeftAt(i) : 0;
                    }
                }
            }

            foreach (Inventory c in sim.ChestContents)
            {
                n += c.Count(item);
            }

            foreach (GroundPile p in sim.Piles)
            {
                n += p.Item == item ? p.Count : 0;
            }

            return n;
        }

        /// <summary>
        /// Медная кирка (8 слитков + 3 древесины) из сумки (2 + 1) и двух сундуков: ближний A (3 тайла от верстака) отдаёт
        /// всё, что есть (4 + 1) и опустевает, дальний B (√17) — остаток (2 + 1); сундук C в 7 тайлах не тронут.
        /// Масса по каждому виду: до = после + ингредиенты изготовленного.
        /// </summary>
        [Fact]
        public void E210_BagPlusTwoChests_Order_MassConserved()
        {
            var bench = new TileCoord(302, 200);
            TileCoord a = new TileCoord(305, 200), b = new TileCoord(306, 201), far = new TileCoord(309, 200);
            var sim = new Simulation(World(bench, a, b, far), maxEntities: 8);
            int id = AddPlayer(sim, Center(300, 200), (Items.CopperIngot, 2), (Items.Wood, 1));
            Assert.True(sim.SetChest(a, Kit((Items.CopperIngot, 4), (Items.Wood, 1))));
            Assert.True(sim.SetChest(b, Kit((Items.CopperIngot, 5), (Items.Wood, 5))));
            Assert.True(sim.SetChest(far, Kit((Items.CopperIngot, 50), (Items.Wood, 50))));
            int ingots = Total(sim, Items.CopperIngot), wood = Total(sim, Items.Wood);

            // Хватает ровно на одну кирку (без дальнего сундука: 2 + 4 + 5 = 11 слитков, 1 + 1 + 5 = 7 древесины).
            Assert.Equal(CraftReply.Accepted, sim.CheckCraft(id, RecipeTable.CopperPickaxe, 5, out int units));
            Assert.Equal(1, units);
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperPickaxe, 5));
            Run(sim, 1);
            Assert.Equal(1, sim.GetEntity(id).Craft.LeftAt(0));
            Assert.Equal(0, sim.GetEntity(id).Inv.Count(Items.CopperIngot));
            Assert.Equal(0, sim.GetEntity(id).Inv.Count(Items.Wood));
            Assert.True(NoRecord(sim, a)); // опустел — записи нет
            Assert.Equal((3, 4), (InChest(sim, b, Items.CopperIngot), InChest(sim, b, Items.Wood)));
            Assert.Equal((50, 50), (InChest(sim, far, Items.CopperIngot), InChest(sim, far, Items.Wood)));
            Assert.Equal(ingots, Total(sim, Items.CopperIngot));
            Assert.Equal(wood, Total(sim, Items.Wood));

            Run(sim, T);
            Assert.Equal(1, sim.GetEntity(id).Inv.Count(Items.CopperPickaxe));
            Assert.Equal(ingots - 8, Total(sim, Items.CopperIngot));
            Assert.Equal(wood - 3, Total(sim, Items.Wood));
        }

        /// <summary>
        /// Радиус: сундук ровно в 6 тайлах от верстака участвует, в 7 — нет (хотя до персонажа — 9); у ручного рецепта
        /// (верёвка) сундуки считаются от персонажа; при равном расстоянии первым снимается сундук с меньшим номером.
        /// </summary>
        [Fact]
        public void E210_Radius_FromStation_HandFromPlayer_TieByIndex()
        {
            var bench = new TileCoord(302, 200);
            TileCoord six = new TileCoord(308, 200), seven = new TileCoord(309, 200);
            var sim = new Simulation(World(bench, six, seven), maxEntities: 8);
            int id = AddPlayer(sim, Center(300, 200));
            Assert.True(sim.SetChest(seven, Kit((Items.CopperOre, 40))));
            Assert.Equal(CraftReply.NoIngredients, sim.CheckCraft(id, RecipeTable.CopperIngot, 1, out _));
            Assert.True(sim.SetChest(six, Kit((Items.CopperOre, 4))));
            Assert.Equal(CraftReply.Accepted, sim.CheckCraft(id, RecipeTable.CopperIngot, 10, out int units));
            Assert.Equal(2, units);

            // Ручной рецепт: от персонажа. Сундуки (294, 200) и (306, 200) — оба в 6 тайлах от персонажа; (300, 207) — в 7.
            TileCoord w = new TileCoord(294, 200), e = new TileCoord(306, 200), s = new TileCoord(300, 207);
            sim = new Simulation(World(null, w, e, s), maxEntities: 8);
            id = AddPlayer(sim, Center(300, 200));
            Assert.True(sim.SetChest(s, Kit((Items.Fiber, 30))));
            Assert.True(sim.SetChest(w, Kit((Items.Fiber, 2))));
            Assert.True(sim.SetChest(e, Kit((Items.Fiber, 2))));
            Assert.Equal(CraftReply.Accepted, sim.CheckCraft(id, RecipeTable.Rope, 10, out units));
            Assert.Equal(1, units); // 2 + 2 волокна, верёвка — 3
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Rope, 1));
            Run(sim, 1);

            // Номер — место в списке сундуков (по Y, затем X): (294, 200) раньше (306, 200) — он снят первым.
            Assert.True(NoRecord(sim, w));
            Assert.Equal(1, InChest(sim, e, Items.Fiber));
            Assert.Equal(30, InChest(sim, s, Items.Fiber));
        }

        /// <summary>Через шов тора: верстак в x = 1, сундук в x = 1021 (4 тайла через шов); ручной рецепт — от персонажа через шов.</summary>
        [Fact]
        public void E210_Seam_ChestAcrossSeam()
        {
            var bench = new TileCoord(1, 200);
            var chest = new TileCoord(1021, 200);
            var sim = new Simulation(World(bench, chest), maxEntities: 8);
            int id = AddPlayer(sim, Center(3, 200));
            Assert.True(sim.SetChest(chest, Kit((Items.CopperOre, 6), (Items.Fiber, 3))));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperIngot, 3));
            Run(sim, 1);
            Assert.Equal(3, sim.GetEntity(id).Craft.LeftAt(0));
            Assert.Equal(0, InChest(sim, chest, Items.CopperOre));

            // Ручной: персонаж в x = 3, сундук в 1021 — 6 тайлов через шов.
            Assert.Equal(CraftReply.Accepted, sim.CheckCraft(id, RecipeTable.Rope, 1, out int units));
            Assert.Equal(1, units);
        }

        /// <summary>
        /// Два игрока у одного сундука в одном такте ставят слитки на последнюю руду (3 шт.): заявки обоих приняты, такт
        /// исполняет по порядку прихода — первому 1 слиток (2 руды), второму отказ «не хватает»; лишнего никто не получил.
        /// </summary>
        [Fact]
        public void E210_TwoPlayers_SameChest_SameTick_NoExtra()
        {
            var bench = new TileCoord(302, 200);
            var chest = new TileCoord(302, 202);
            var sim = new Simulation(World(bench, chest), maxEntities: 8);
            int p1 = AddPlayer(sim, Center(300, 200));
            int p2 = AddPlayer(sim, Center(303, 200));
            Assert.True(sim.SetChest(chest, Kit((Items.CopperOre, 3))));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(p1, RecipeTable.CopperIngot, 1));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(p2, RecipeTable.CopperIngot, 1));
            Run(sim, 1);
            Assert.Equal(1, sim.GetEntity(p1).Craft.LeftAt(0));
            Assert.True(sim.GetEntity(p2).Craft.IsEmpty);
            Assert.Contains(sim.Events.ToArray(), ev => ev.Kind == SimEventKind.CraftRefused && ev.Requester == p2
                && ev.Tag == (int)CraftReply.NoIngredients);
            Assert.Equal(1, InChest(sim, chest, Items.CopperOre));
            Assert.Equal(3, Total(sim, Items.CopperOre));
            Run(sim, T + 2);
            Assert.Equal(1, sim.GetEntity(p1).Inv.Count(Items.CopperIngot));
            Assert.Equal(0, sim.GetEntity(p2).Inv.Count(Items.CopperIngot));
            Assert.Equal(1, Total(sim, Items.CopperOre));
        }

        /// <summary>
        /// Отмена и смерть возвращают взятое из сундука в сумку, а не в сундук; не влезшее — кучей; мёртвому — отказ.
        /// </summary>
        [Fact]
        public void E210_CancelAndDeath_RefundToBag_DeadRefused()
        {
            var bench = new TileCoord(302, 200);
            var chest = new TileCoord(302, 202);
            var sim = new Simulation(World(bench, chest), maxEntities: 8);
            int id = AddPlayer(sim, Center(300, 200), (Items.CopperOre, 2));
            Assert.True(sim.SetChest(chest, Kit((Items.CopperOre, 8))));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperIngot, 3)); // 2 из сумки, 4 из сундука
            Run(sim, 1);
            Assert.Equal(4, InChest(sim, chest, Items.CopperOre));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraftCancel(id, 0, RecipeTable.CopperIngot));
            Run(sim, 1);
            Assert.Equal(6, sim.GetEntity(id).Inv.Count(Items.CopperOre));
            Assert.Equal(4, InChest(sim, chest, Items.CopperOre));
            Assert.Equal(10, Total(sim, Items.CopperOre));

            // Смерть: очередь из сундука → в сумку.
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperIngot, 5)); // 6 из сумки, 4 из сундука
            Run(sim, 1);
            Assert.True(NoRecord(sim, chest));
            Assert.True(sim.ApplyStrike(id, 0, 500) > 0);
            Run(sim, 1);
            Assert.False(sim.GetEntity(id).Alive);
            Assert.Equal(10, sim.GetEntity(id).Inv.Count(Items.CopperOre));
            Assert.Equal(0, sim.Piles.Length);
            Assert.Equal(CraftReply.Dead, sim.RequestCraft(id, RecipeTable.CopperIngot, 1));
        }

        /// <summary>
        /// Сундук снесли между заявкой и тактом: заявка принята, правка такта убирает сундук (содержимое — кучей), такт
        /// заявки уже не находит руды — отказ «не хватает»; масса цела.
        /// </summary>
        [Fact]
        public void E210_ChestRemovedBetweenRequestAndTick()
        {
            var bench = new TileCoord(302, 200);
            var chest = new TileCoord(302, 202);
            var sim = new Simulation(World(bench, chest), maxEntities: 8);
            int id = AddPlayer(sim, Center(300, 200));
            Assert.True(sim.SetChest(chest, Kit((Items.CopperOre, 6))));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperIngot, 3));
            Assert.True(sim.RequestEdit(chest, FloorId.Grass, ObjectId.Empty));
            Run(sim, 1);
            Assert.True(sim.GetEntity(id).Craft.IsEmpty);
            Assert.Contains(sim.Events.ToArray(), ev => ev.Kind == SimEventKind.CraftRefused && ev.Tag == (int)CraftReply.NoIngredients);
            Assert.Equal(0, sim.ChestCount);
            Assert.Equal(6, Total(sim, Items.CopperOre));
            Assert.Equal(0, sim.GetEntity(id).Inv.Count(Items.CopperOre));
        }

        /// <summary>Сводка ядра: по видам станка — руки от персонажа, верстак от верстака; костра нет — пусто.</summary>
        [Fact]
        public void E210_FillChestStock_PerStation()
        {
            var bench = new TileCoord(302, 200);
            TileCoord nearBench = new TileCoord(308, 200), nearPlayer = new TileCoord(295, 200);
            var sim = new Simulation(World(bench, nearBench, nearPlayer), maxEntities: 8);
            int id = AddPlayer(sim, Center(300, 200));
            Assert.True(sim.SetChest(nearBench, Kit((Items.CopperIngot, 7))));
            Assert.True(sim.SetChest(nearPlayer, Kit((Items.Fiber, 5))));
            var stock = new ChestStock();
            sim.FillChestStock(id, stock);
            Assert.Equal(7, stock.Count(CraftStation.Workbench, Items.CopperIngot));
            Assert.Equal(0, stock.Count(CraftStation.Workbench, Items.Fiber)); // 5 тайлов от персонажа, 7 — от верстака
            Assert.Equal(5, stock.Count(CraftStation.None, Items.Fiber));
            Assert.Equal(0, stock.Count(CraftStation.None, Items.CopperIngot)); // 8 тайлов от персонажа
            Assert.Equal(0, stock.Count(CraftStation.Campfire, Items.Fiber));
        }

        /// <summary>«Золотые» байты сообщения 36 и проверки чтения.</summary>
        [Fact]
        public void E210_Codec_GoldenBytes_NearbyStock()
        {
            Assert.Equal(37, NetModule.ProtocolVersion); // C-036: 37 (действие в снимке); C-035: 36 (пачка 40); E3-6: 35 (надетое тремя байтами в сумке 26) // C-028: 34 (взмах, тип 39); C-030: 33; C-031: 32; E3-2: 31; E3-1: 30; C-013: 29; E2-16: 28
            Assert.Equal(36, (int)MessageType.NearbyStock);
            Assert.True(Codec.IsKnown(36));
            var buf = new byte[256];
            var stock = new ChestStock();
            int n = new NearbyStockMessage { Stock = stock }.Write(buf);
            Assert.Equal("24-00-00-00", BitConverter.ToString(buf, 0, n));

            stock.Set(CraftStation.None, Items.Fiber, 5);
            stock.Set(CraftStation.Workbench, Items.Wood, 0x0102);
            stock.Set(CraftStation.Workbench, Items.CopperIngot, 70_000); // обрезается до 65 535
            n = new NearbyStockMessage { Stock = stock }.Write(buf);
            Assert.Equal("24-01-0B-05-00-02-08-02-01-0E-FF-FF-00", BitConverter.ToString(buf, 0, n));
            Assert.True(n <= NearbyStockMessage.MaxBytes);
            var back = new ChestStock();
            NearbyStockMessage.Read(new ReadOnlySpan<byte>(buf, 0, n), back);
            Assert.Equal(stock, back);

            byte[] bad = (byte[])buf.Clone();
            bad[6] = 0x0E; bad[9] = 0x08; // предметы не по возрастанию
            Assert.Throws<ProtocolException>(() => NearbyStockMessage.Read(new ReadOnlySpan<byte>(bad, 0, n), back));
            bad = (byte[])buf.Clone();
            bad[3] = 0; bad[4] = 0; // количество 0
            Assert.Throws<ProtocolException>(() => NearbyStockMessage.Read(new ReadOnlySpan<byte>(bad, 0, n), back));
            Assert.Throws<ProtocolException>(() => NearbyStockMessage.Read(new ReadOnlySpan<byte>(buf, 0, n - 1), back));
        }

        /// <summary>
        /// По сети, как окно: сундук у верстака; клиент получает сводку (36), строка <see cref="CraftDesk.LineOf"/> со
        /// сводкой — «можно», без неё — «не хватает»; «сделать» — сервер снимает из сундука; сводка уменьшается; у второго
        /// игрока, открывшего сундук, окно сундука обновилось; в паузе — отказ «пауза».
        /// </summary>
        [Fact]
        public void E210_Net_StockSummary_Desk_ChestWindowUpdates_Pause()
        {
            var bench = new TileCoord(302, 200);
            var chest = new TileCoord(302, 202);
            WorldPos spot = Center(300, 200);
            using var rig = new EditTests.EditRig(World(bench, chest), i => Center(300 + i, 200));
            NetClient a = rig.Join();
            NetClient b = rig.Join();
            var replies = new List<CraftReplyMessage>();
            a.CraftReplied = replies.Add;
            rig.Run(10);
            Assert.True(rig.Sim.SetInventory(a.EntityId, new Inventory()));
            Assert.True(rig.Sim.SetChest(chest, Kit((Items.CopperOre, 5))));
            Assert.True(b.RequestUse(chest));
            rig.Run(NetServer.StockEveryTicks + 3);
            Assert.True(a.NearbyStockSeen > 0);
            Assert.Equal(5, a.NearbyStock.Count(CraftStation.Workbench, Items.CopperOre));
            Assert.True(b.ChestOpen);
            Assert.Equal(5, b.Chest.Count(Items.CopperOre));

            Assert.False(CraftDesk.LineOf(RecipeTable.CopperIngot, a.Inv, a.Craft, a.World!.World, spot).CanMake);
            CraftLine line = CraftDesk.LineOf(RecipeTable.CopperIngot, a.Inv, a.Craft, a.World!.World, spot, stock: a.NearbyStock);
            Assert.True(line.CanMake);
            Assert.Equal(2, line.Affordable);
            Assert.Equal(5, CraftDesk.Have(RecipeTable.CopperIngot, 0, a.Inv, a.NearbyStock));
            Assert.Equal(5, CraftDesk.InChests(RecipeTable.CopperIngot, 0, a.NearbyStock));
            Assert.Equal(0, CraftDesk.Missing(RecipeTable.CopperIngot, 0, a.Inv, a.NearbyStock));

            // Пауза — отказ, сундук цел.
            rig.Server.SetPaused(true);
            var desk = new CraftDesk(a);
            Assert.True(desk.Make(line, 1));
            rig.Run(3);
            Assert.Equal(CraftReply.Paused, Assert.Single(replies).Reply);
            Assert.Equal(5, b.Chest.Count(Items.CopperOre));
            rig.Server.SetPaused(false);
            replies.Clear();

            Assert.True(desk.Make(line, 2));
            rig.Run(3);
            Assert.Equal(CraftReply.Accepted, Assert.Single(replies).Reply);
            Assert.Equal(2, a.Craft.LeftAt(0));
            Assert.Equal(1, a.NearbyStock.Count(CraftStation.Workbench, Items.CopperOre)); // сразу после изготовления
            Assert.Equal(1, b.Chest.Count(Items.CopperOre)); // окно сундука у второго обновилось
            Assert.Equal(1, CraftDesk.InChests(RecipeTable.CopperIngot, 0, a.NearbyStock));
            Assert.False(CraftDesk.LineOf(RecipeTable.CopperIngot, a.Inv, a.Craft, a.World!.World, spot, stock: a.NearbyStock).CanMake);
            rig.Run(2 * T + 3);
            Assert.Equal(2, a.Inv.Count(Items.CopperIngot));
        }
    }
}
