using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-4 (шаг 2.4): «стол» окна крафта (<see cref="CraftDesk"/>) — общий путь окна игрока и ботов. Тесты делают то же,
    /// что окно: строка рецепта по своим данным клиента (<see cref="CraftDesk.LineOf"/>: сумка, очередь, свой мир,
    /// место), кнопки «Сделать 1 / 5 / всё» (<see cref="CraftDesk.Make"/>), щелчок по позиции очереди
    /// (<see cref="CraftDesk.Cancel"/>); заявки идут по сети настоящему серверу, окно ничего не предсказывает.
    /// </summary>
    public class CraftDeskTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const int T = RecipeTable.DefaultTicks;

        private static WorldPos Center(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private static readonly WorldPos Spot = Center(300, 200);

        /// <summary>Плоский мир с верстаком в <paramref name="benchDx"/> тайлах восточнее персонажа; костра нет.</summary>
        private static WorldModel World(int benchDx)
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            world.SetBaseTile(new TileCoord(300 + benchDx, 200), FloorId.Grass, ObjectId.Workbench);
            return world;
        }

        private static void Give(EditTests.EditRig rig, NetClient c, params (byte Item, int Count)[] items)
        {
            var bag = new Inventory();
            foreach ((byte item, int count) in items)
            {
                Assert.Equal(count, bag.Add(item, count));
            }

            Assert.True(rig.Sim.SetInventory(c.EntityId, bag));
            rig.Run(3);
        }

        /// <summary>Строка рецепта так, как её считает окно: по сумке, очереди и своему миру клиента.</summary>
        private static CraftLine Line(NetClient c, int recipe, bool alive = true) =>
            CraftDesk.LineOf(recipe, c.Inv, c.Craft, c.World!.World, Spot, alive);

        /// <summary>
        /// Окно у верстака: «сделать всё» слитки → в очереди; стрелы «сделать 1» дважды → вторая позиция × 2; щелчок по
        /// позиции — отмена, материалы вернулись; дождались слитков — сумка клиента равна сумке сервера. Сумка и очередь
        /// меняются только после ответа сервера.
        /// </summary>
        [Fact]
        public void E24_Net_Desk_Queue_Cancel_Result_LikeWindow()
        {
            using var rig = new EditTests.EditRig(World(benchDx: 2), _ => Spot);
            NetClient c = rig.Join();
            var replies = new List<CraftReplyMessage>();
            c.CraftReplied = replies.Add;
            rig.Run(10);
            Give(rig, c, (Items.CopperOre, 9), (Items.Wood, 5), (Items.Stone, 5));
            var desk = new CraftDesk(c);

            // Список: слиток можно (на 4), кирка — не хватает слитков (дерева хватает), жареное мясо — нет костра.
            CraftLine ingot = Line(c, RecipeTable.CopperIngot);
            Assert.True(ingot.CanMake);
            Assert.Equal(4, ingot.Affordable);
            CraftLine pick = Line(c, RecipeTable.CopperPickaxe);
            Assert.Equal(CraftReply.NoIngredients, pick.State);
            Assert.Equal(8, CraftDesk.Missing(RecipeTable.CopperPickaxe, 0, c.Inv));
            Assert.Equal(0, CraftDesk.Missing(RecipeTable.CopperPickaxe, 1, c.Inv));
            Assert.Equal(CraftReply.NoStation, Line(c, RecipeTable.RoastMeat).State);
            Assert.Equal(CraftReply.Dead, Line(c, RecipeTable.CopperIngot, alive: false).State);

            // «Красная» строка заявку не шлёт.
            Assert.False(desk.Make(pick, 1));
            Assert.False(desk.Make(Line(c, RecipeTable.RoastMeat), 1));
            Assert.Equal(0, desk.MakeRequests);

            // «Сделать всё»: заявка ушла, сумка и очередь прежние, пока сервер не ответил.
            Assert.True(desk.Make(ingot, ingot.Affordable));
            Assert.Equal(9, c.Inv.Count(Items.CopperOre));
            Assert.True(c.Craft.IsEmpty);
            rig.Run(4);
            Assert.Equal(CraftReply.Accepted, Assert.Single(replies).Reply);
            Assert.Equal(1, c.Inv.Count(Items.CopperOre));
            Assert.Equal((RecipeTable.CopperIngot, 4), (c.Craft.RecipeAt(0), c.Craft.LeftAt(0)));
            Assert.True(c.CraftRunning);

            // «Сделать 1» стрелы дважды — одна позиция × 2 (доливка); «сделать 5» — сервер берёт, на сколько хватает (ещё 3).
            Assert.True(desk.Make(Line(c, RecipeTable.Arrows), 1));
            rig.Run(4);
            Assert.True(desk.Make(Line(c, RecipeTable.Arrows), 1));
            rig.Run(4);
            Assert.Equal(2, c.Craft.Count);
            Assert.Equal((RecipeTable.Arrows, 2), (c.Craft.RecipeAt(1), c.Craft.LeftAt(1)));
            Assert.Equal(3, Line(c, RecipeTable.Arrows).Affordable);
            Assert.True(desk.Make(Line(c, RecipeTable.Arrows), 5));
            rig.Run(4);
            Assert.Equal((RecipeTable.Arrows, 5), (c.Craft.RecipeAt(1), c.Craft.LeftAt(1)));
            Assert.Equal(0, c.Inv.Count(Items.Wood));
            Assert.Equal(CraftReply.NoIngredients, Line(c, RecipeTable.Arrows).State);

            // Щелчок по второй позиции очереди — отмена: дерево и камень вернулись; мимо очереди — заявки нет.
            Assert.False(desk.Cancel(c.Craft, 2));
            Assert.True(desk.Cancel(c.Craft, 1));
            Assert.Equal(0, c.Inv.Count(Items.Wood)); // не предсказано
            rig.Run(4);
            Assert.Equal(1, c.Craft.Count);
            Assert.Equal(5, c.Inv.Count(Items.Wood));
            Assert.Equal(5, c.Inv.Count(Items.Stone));
            Assert.Equal(0, c.Inv.Count(Items.Arrows));

            // Дождались: 4 слитка, очередь пуста, сумка клиента — сумка сервера.
            rig.Run(4 * T + 6);
            Assert.True(c.Craft.IsEmpty);
            Assert.Equal(4, c.Inv.Count(Items.CopperIngot));
            Assert.Equal(1, c.Inv.Count(Items.CopperOre));
            Assert.Equal(rig.Sim.GetEntity(c.EntityId).Inv, c.Inv);
            Assert.Equal(0, c.CraftProgressAt(c.ServerTick));

            Assert.Equal(4, desk.MakeRequests);
            Assert.Equal(1, desk.CancelRequests);
            Assert.Equal(desk.MakeRequests + desk.CancelRequests, rig.Server.CraftRequestsReceived);
            Assert.Equal(0, rig.Server.CraftRequestsRefused);
            Assert.All(replies, r => Assert.Equal(CraftReply.Accepted, r.Reply));
            Assert.Equal(0, c.BadMessages);
        }

        /// <summary>
        /// Верстак в 4 тайлах: строки верстака — «слишком далеко», кнопка заявку не шлёт; рецепт «руками» делается.
        /// Очередь полна (5 позиций) — строка «очередь полна». Отказ сервера приходит ответом с причиной (пауза).
        /// </summary>
        [Fact]
        public void E24_Net_Desk_FarStation_FullQueue_ServerRefusal()
        {
            using var rig = new EditTests.EditRig(World(benchDx: 4), _ => Spot);
            NetClient c = rig.Join();
            var replies = new List<CraftReplyMessage>();
            c.CraftReplied = replies.Add;
            rig.Run(10);
            Give(rig, c, (Items.CopperOre, 4), (Items.Fiber, 9), (Items.Wood, 20), (Items.Stone, 20), (Items.Resin, 3));
            var desk = new CraftDesk(c);

            CraftLine ingot = Line(c, RecipeTable.CopperIngot);
            Assert.Equal(CraftReply.StationFar, ingot.State);
            Assert.Equal(2, ingot.Affordable); // «на сколько хватает» видно и без станка
            Assert.False(desk.Make(ingot, 1));

            // Сервер на паузе: заявка уходит (окно про паузу не знает), ответ — отказ «пауза», сумка цела.
            rig.Server.SetPaused(true);
            Assert.True(desk.Make(Line(c, RecipeTable.Rope), 1));
            rig.Run(5);
            Assert.Equal(CraftReply.Paused, Assert.Single(replies).Reply);
            Assert.Equal(9, c.Inv.Count(Items.Fiber));
            rig.Server.SetPaused(false);
            rig.Run(3);

            // Пять разных рецептов руками — очередь полна: шестой (другой) рецепт — «очередь полна», заявки нет.
            byte[] five = { RecipeTable.Rope, RecipeTable.StonePickaxe, RecipeTable.StoneAxe, RecipeTable.Hammer, RecipeTable.Torch };
            foreach (byte recipe in five)
            {
                Assert.True(desk.Make(Line(c, recipe), 1));
                rig.Run(2);
            }

            rig.Run(2);
            Assert.InRange(c.Craft.Count, 4, 5); // первая позиция могла уже доделаться
            if (c.Craft.Count == CraftQueue.MaxEntries)
            {
                CraftLine rope = Line(c, RecipeTable.Rope);
                Assert.Equal(CraftReply.QueueFull, rope.State);
                Assert.False(desk.Make(rope, 1));
            }

            rig.Run(5 * T + 6);
            Assert.True(c.Craft.IsEmpty);
            Assert.Equal(1, c.Inv.Count(Items.Rope));
            Assert.Equal(1, c.Inv.Count(Items.Torch));
            Assert.Equal(rig.Sim.GetEntity(c.EntityId).Inv, c.Inv);
            Assert.Equal(1, rig.Server.CraftRequestsRefused);
            Assert.Equal(desk.MakeRequests, rig.Server.CraftRequestsReceived);
        }
    }
}
