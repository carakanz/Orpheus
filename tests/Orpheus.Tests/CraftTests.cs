using System;
using System.Collections.Generic;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Save;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-3 (шаг 2.3 плана; <c>06</c> 1.5, 2.3, 3.3, раздел 10): рецепты данными, крафт руками и у станков, очередь
    /// изготовления, сеть (протокол 22) и сохранение (формат 8).
    /// </summary>
    public class CraftTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const int T = RecipeTable.DefaultTicks;

        private static WorldPos Center(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        /// <summary>Плоский мир; верстак в <paramref name="benchDx"/> тайлах восточнее (300, 200), костёр — в 2 тайлах южнее (0 — нет).</summary>
        private static WorldModel World(int benchDx = 2, bool campfire = true)
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            if (benchDx != 0)
            {
                world.SetBaseTile(new TileCoord(300 + benchDx, 200), FloorId.Grass, ObjectId.Workbench);
            }

            if (campfire)
            {
                world.SetBaseTile(new TileCoord(300, 202), FloorId.Grass, ObjectId.Campfire);
            }

            return world;
        }

        /// <summary>Персонаж в (300, 200) с пустой сумкой, в которую положено перечисленное.</summary>
        private static Simulation Sim(out int id, WorldModel? world = null, params (byte Item, int Count)[] bag)
        {
            var sim = new Simulation(world ?? World(), maxEntities: 8);
            id = sim.AddEntity(Center(300, 200));
            var inv = new Inventory();
            foreach ((byte item, int count) in bag)
            {
                Assert.Equal(count, inv.Add(item, count));
            }

            Assert.True(sim.SetInventory(id, inv));
            return sim;
        }

        private static void Run(Simulation sim, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                sim.Step();
            }
        }

        private static int OnGround(Simulation sim, byte item)
        {
            int n = 0;
            foreach (GroundPile p in sim.Piles)
            {
                n += p.Item == item ? p.Count : 0;
            }

            return n;
        }

        /// <summary>Сколько предмета всего: в сумке, на земле и в очереди изготовления (как ингредиент).</summary>
        private static int Total(Simulation sim, int id, byte item)
        {
            SimEntity e = sim.GetEntity(id);
            int n = e.Inv.Count(item) + OnGround(sim, item);
            for (int i = 0; i < e.Craft.Count; i++)
            {
                RecipeDef d = RecipeTable.Get(e.Craft.RecipeAt(i));
                for (int k = 0; k < d.IngredientCount; k++)
                {
                    n += d.IngredientItem(k) == item ? d.IngredientCountOf(k) * e.Craft.LeftAt(i) : 0;
                }
            }

            return n;
        }

        // ---------- Таблицы ----------

        /// <summary>Таблица рецептов — по документу: состав, станок, выход, время 0,5 с (<c>06</c> 1.5, 2.3, 3.3, 4.4, 6.3, 6.7, 6.9).</summary>
        [Fact]
        public void E23_RecipeTable_MatchesDesign()
        {
            Assert.Equal(15, RecipeTable.Count); // 14 рецептов + нулевая строка
            Assert.Equal(21, ItemTable.Count);
            Assert.False(RecipeTable.Exists(0));
            Assert.False(RecipeTable.Exists(15));
            Assert.Equal(0, RecipeTable.Get(200).Id);

            void Row(byte id, byte result, int count, CraftStation station, params (byte Item, int N)[] ins)
            {
                RecipeDef d = RecipeTable.Get(id);
                Assert.Equal(id, d.Id);
                Assert.Equal(result, d.Result);
                Assert.Equal(count, d.ResultCount);
                Assert.Equal(station, d.Station);
                Assert.Equal(15, d.Ticks);
                Assert.Equal(ins.Length, d.IngredientCount);
                for (int i = 0; i < ins.Length; i++)
                {
                    Assert.Equal(ins[i].Item, d.IngredientItem(i));
                    Assert.Equal(ins[i].N, d.IngredientCountOf(i));
                }
            }

            const CraftStation Hands = CraftStation.None, Bench = CraftStation.Workbench;
            Row(RecipeTable.StonePickaxe, Items.StonePickaxe, 1, Hands, (Items.Stone, 3), (Items.Wood, 2));
            Row(RecipeTable.StoneAxe, Items.StoneAxe, 1, Hands, (Items.Stone, 3), (Items.Wood, 2));
            Row(RecipeTable.Hammer, Items.Hammer, 1, Hands, (Items.Wood, 4), (Items.Stone, 2));
            Row(RecipeTable.Torch, Items.Torch, 1, Hands, (Items.Wood, 1), (Items.Resin, 1));
            Row(RecipeTable.Rope, Items.Rope, 1, Hands, (Items.Fiber, 3));
            Row(RecipeTable.WoodenSword, Items.WoodenSword, 1, Bench, (Items.Wood, 8));
            Row(RecipeTable.SimpleBow, Items.SimpleBow, 1, Bench, (Items.Wood, 10), (Items.Rope, 3));
            Row(RecipeTable.Arrows, Items.Arrows, 10, Bench, (Items.Wood, 1), (Items.Stone, 1));
            Row(RecipeTable.CopperIngot, Items.CopperIngot, 1, Bench, (Items.CopperOre, 2));
            Row(RecipeTable.CopperPickaxe, Items.CopperPickaxe, 1, Bench, (Items.CopperIngot, 8), (Items.Wood, 3));
            Row(RecipeTable.CopperAxe, Items.CopperAxe, 1, Bench, (Items.CopperIngot, 7), (Items.Wood, 3));
            Row(RecipeTable.CopperSword, Items.CopperSword, 1, Bench, (Items.CopperIngot, 8), (Items.Wood, 2));
            Row(RecipeTable.CopperBow, Items.CopperBow, 1, Bench, (Items.Wood, 10), (Items.CopperIngot, 4), (Items.Rope, 3));
            Row(RecipeTable.RoastMeat, Items.RoastMeat, 1, CraftStation.Campfire, (Items.Meat, 1));

            // Ингредиенты рецепта не повторяются (списание по одному предмету — один раз).
            for (int id = 1; id < RecipeTable.Count; id++)
            {
                RecipeDef d = RecipeTable.Get(id);
                for (int i = 0; i < d.IngredientCount; i++)
                {
                    for (int k = i + 1; k < d.IngredientCount; k++)
                    {
                        Assert.NotEqual(d.IngredientItem(i), d.IngredientItem(k));
                    }
                }
            }

            // Новые предметы (E2-3): стопки и свойства.
            Assert.Equal(99, Items.MaxStackOf(Items.CopperIngot));
            Assert.Equal(99, Items.MaxStackOf(Items.Rope));
            Assert.Equal(99, Items.MaxStackOf(Items.Meat));
            Assert.Equal(30, Items.MaxStackOf(Items.RoastMeat));
            Assert.Equal(1, Items.MaxStackOf(Items.CopperAxe));
            Assert.Equal(Items.TierCopper, Items.TierOf(Items.CopperAxe));
            Assert.True(Items.IsChoppingTool(Items.CopperAxe));
            Assert.Equal(Items.KindMelee, Items.KindOf(Items.CopperSword));
            Assert.Equal(Items.KindRanged, Items.KindOf(Items.CopperBow));
            Assert.Equal("copper_ingot", ItemTable.Get(Items.CopperIngot).Key);

            // Станки — постройки молотом: верстак I — 10 древесины + 6 камня, прочность 100; костёр — 8 камня + 4 древесины.
            Assert.True(EditRules.IsPlaceable(ObjectId.Workbench));
            Assert.True(BuildDefs.TryGet(ObjectId.Workbench, out BuildDef bench));
            Assert.Equal((Material.Wood, 10, Material.Stone, 6, 100), (bench.Material1, bench.Count1, bench.Material2, bench.Count2, (int)bench.MaxIntegrity));
            Assert.True(BuildDefs.TryGet(ObjectId.Campfire, out BuildDef fire));
            Assert.Equal((Material.Stone, 8, Material.Wood, 4), (fire.Material1, fire.Count1, fire.Material2, fire.Count2));
            Assert.Equal(ObjectId.Workbench, RecipeTable.ObjectOf(CraftStation.Workbench));
            Assert.Equal(ObjectId.Campfire, RecipeTable.ObjectOf(CraftStation.Campfire));
        }

        // ---------- Изготовление ----------

        /// <summary>
        /// Каждый рецепт набора изготавливается: ингредиенты ровно на один раз → через 15 тактов результат в сумке,
        /// ингредиентов нет, очередь пуста; на 14-м такте ещё не готово.
        /// </summary>
        [Fact]
        public void E23_EveryRecipe_IsMade_In15Ticks()
        {
            for (int recipe = 1; recipe < RecipeTable.Count; recipe++)
            {
                RecipeDef d = RecipeTable.Get(recipe);
                var bag = new List<(byte, int)>();
                for (int i = 0; i < d.IngredientCount; i++)
                {
                    bag.Add((d.IngredientItem(i), d.IngredientCountOf(i)));
                }

                Simulation sim = Sim(out int id, null, bag.ToArray());
                Assert.Equal(CraftReply.Accepted, sim.CheckCraft(id, recipe, 1, out int units));
                Assert.Equal(1, units);
                Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, recipe, 1));
                Run(sim, 1);
                SimEntity e = sim.GetEntity(id);
                Assert.Equal(0, e.Inv.UsedCells); // ингредиенты сняты в такте заявки
                Assert.Equal(1, e.Craft.Count);
                Assert.Equal(recipe, e.Craft.RecipeAt(0));
                Run(sim, T - 2);
                Assert.Equal(0, sim.GetEntity(id).Inv.Count(d.Result));
                Assert.Equal(T - 1, sim.GetEntity(id).Craft.Progress);
                Run(sim, 1);
                e = sim.GetEntity(id);
                Assert.True(e.Craft.IsEmpty, $"рецепт {recipe}");
                Assert.Equal(d.ResultCount, e.Inv.Count(d.Result));
                Assert.Equal(1, e.Inv.UsedCells);
                Assert.Equal(0, sim.Piles.Length);
                Assert.Contains(sim.Events.ToArray(), ev => ev.Kind == SimEventKind.CraftDone && ev.Requester == id && ev.Tag == recipe);
            }
        }

        /// <summary>Отказы с причиной; при отказе сумка и очередь не меняются.</summary>
        [Fact]
        public void E23_Refusals_WithReason_BagUntouched()
        {
            // Не хватает ингредиента (одного из двух).
            Simulation sim = Sim(out int id, null, (Items.CopperIngot, 8), (Items.Wood, 2));
            Inventory before = sim.GetEntity(id).Inv;
            Assert.Equal(CraftReply.NoIngredients, sim.RequestCraft(id, RecipeTable.CopperPickaxe, 1));
            Assert.Equal(CraftReply.NoRecipe, sim.RequestCraft(id, 0, 1));
            Assert.Equal(CraftReply.NoRecipe, sim.RequestCraft(id, RecipeTable.Count, 1));
            Assert.Equal(CraftReply.NoRecipe, sim.RequestCraft(id, -3, 1));
            Assert.Equal(CraftReply.NoEntity, sim.RequestCraft(id + 100, RecipeTable.Rope, 1));
            Assert.Equal(CraftReply.NoEntry, sim.RequestCraftCancel(id, 0));
            Assert.Equal(CraftReply.NoEntry, sim.RequestCraftCancel(id, -1));
            Run(sim, 20);
            Assert.Equal(before, sim.GetEntity(id).Inv);
            Assert.True(sim.GetEntity(id).Craft.IsEmpty);

            // Станка нет вовсе; станок далеко (4 тайла — «слишком далеко», `06` 3.7 п. 1); в 3 тайлах — принято.
            sim = Sim(out id, World(benchDx: 0, campfire: false), (Items.Wood, 8), (Items.Meat, 1));
            Assert.Equal(CraftReply.NoStation, sim.RequestCraft(id, RecipeTable.WoodenSword, 1));
            Assert.Equal(CraftReply.NoStation, sim.RequestCraft(id, RecipeTable.RoastMeat, 1));
            sim = Sim(out id, World(benchDx: 4), (Items.Wood, 8), (Items.Meat, 1));
            Assert.Equal(CraftReply.StationFar, sim.RequestCraft(id, RecipeTable.WoodenSword, 1));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.RoastMeat, 1)); // костёр в 2 тайлах
            sim = Sim(out id, World(benchDx: 3), (Items.Wood, 8));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.WoodenSword, 1));
            sim = Sim(out id, World(benchDx: 2), (Items.Meat, 1));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.RoastMeat, 1)); // у костра
            Assert.False(sim.CraftStationNear(id + 1, CraftStation.Workbench));
            Assert.True(sim.CraftStationNear(id, CraftStation.Workbench));

            // Верстак не заменяет костёр, костёр — верстак.
            sim = Sim(out id, World(benchDx: 2, campfire: false), (Items.Meat, 1));
            Assert.Equal(CraftReply.NoStation, sim.RequestCraft(id, RecipeTable.RoastMeat, 1));

            // Мёртвый: отказ сразу; умер между заявкой и тактом — заявка не исполнена, сумка цела.
            sim = Sim(out id, null, (Items.Fiber, 9));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Rope, 1));
            Assert.True(sim.ApplyStrike(id, 0, 500) > 0);
            Assert.Equal(CraftReply.Dead, sim.RequestCraft(id, RecipeTable.Rope, 1));
            Assert.Equal(CraftReply.Dead, sim.RequestCraftCancel(id, 0));
            Run(sim, 3);
            Assert.Equal(9, sim.GetEntity(id).Inv.Count(Items.Fiber));
            Assert.True(sim.GetEntity(id).Craft.IsEmpty);
        }

        /// <summary>
        /// Отказ в такте (заявка взята, а к такту ингредиенты ушли) — событие <see cref="SimEventKind.CraftRefused"/> с причиной.
        /// </summary>
        [Fact]
        public void E23_RefusalInsideTick_RaisesEvent()
        {
            Simulation sim = Sim(out int id, null, (Items.Fiber, 3));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Rope, 1));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Rope, 1)); // проверка «сразу» видит те же 3 волокна
            Run(sim, 1);
            SimEvent refused = Assert.Single(sim.Events.ToArray(), ev => ev.Kind == SimEventKind.CraftRefused);
            Assert.Equal(id, refused.Requester);
            Assert.Equal((int)CraftReply.NoIngredients, refused.Tag);
            Assert.Equal(RecipeTable.Rope, refused.Victim);
            Run(sim, T);
            Assert.Equal(1, sim.GetEntity(id).Inv.Count(Items.Rope));
            Assert.Equal(0, sim.GetEntity(id).Inv.Count(Items.Fiber));
        }

        /// <summary>
        /// Очередь и время: «× 10 стрел — за 5 с» (<c>06</c> 3.7 п. 2); позиции делаются по порядку; та же позиция
        /// доливается; шестая разная — «очередь полна»; в позиции не больше 99.
        /// </summary>
        [Fact]
        public void E23_Queue_Order_Time_Merge_Limit()
        {
            Assert.Equal(5, CraftQueue.MaxEntries);
            Simulation sim = Sim(out int id, null, (Items.Wood, 99), (Items.Stone, 99), (Items.Fiber, 99), (Items.Resin, 20), (Items.CopperOre, 40));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Arrows, 10));
            Run(sim, 10 * T - 1);
            Assert.Equal(90, sim.GetEntity(id).Inv.Count(Items.Arrows));
            Run(sim, 1);
            Assert.Equal(100, sim.GetEntity(id).Inv.Count(Items.Arrows)); // 150 тактов = 5 с
            Assert.True(sim.GetEntity(id).Craft.IsEmpty);

            // Пять разных позиций; одна и та же подряд — доливается в последнюю.
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Rope, 2));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Torch, 1));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperIngot, 3));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Arrows, 1));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.StoneAxe, 1));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.StoneAxe, 2));
            Run(sim, 1);
            CraftQueue q = sim.GetEntity(id).Craft;
            Assert.Equal(5, q.Count);
            Assert.Equal((RecipeTable.Rope, 2), (q.RecipeAt(0), q.LeftAt(0)));
            Assert.Equal((RecipeTable.StoneAxe, 3), (q.RecipeAt(4), q.LeftAt(4)));
            Assert.Equal(CraftReply.QueueFull, sim.RequestCraft(id, RecipeTable.Torch, 1)); // шестая позиция
            Assert.Equal(CraftReply.Accepted, sim.CheckCraft(id, RecipeTable.StoneAxe, 1, out _)); // последняя — доливается

            // Порядок: верёвка, верёвка, факел, 3 слитка, стрелы, 3 топора — 10 изготовлений по 15 тактов.
            Run(sim, 2 * T - 1);
            Assert.Equal(2, sim.GetEntity(id).Inv.Count(Items.Rope));
            Assert.Equal(0, sim.GetEntity(id).Inv.Count(Items.Torch));
            Run(sim, T);
            Assert.Equal(1, sim.GetEntity(id).Inv.Count(Items.Torch));
            Assert.Equal(0, sim.GetEntity(id).Inv.Count(Items.CopperIngot));
            Run(sim, 7 * T);
            SimEntity e = sim.GetEntity(id);
            Assert.True(e.Craft.IsEmpty);
            Assert.Equal(3, e.Inv.Count(Items.CopperIngot));
            Assert.Equal(110, e.Inv.Count(Items.Arrows));
            Assert.Equal(3, e.Inv.Count(Items.StoneAxe));

            // В одной позиции — не больше 99 изготовлений: 150 верёвок по заявкам 99 + 99 → 99 в первой, остаток — во вторую.
            sim = Sim(out id, null, (Items.Fiber, 99), (Items.Fiber, 99), (Items.Fiber, 99), (Items.Fiber, 99), (Items.Fiber, 54));
            Assert.Equal(450, sim.GetEntity(id).Inv.Count(Items.Fiber));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Rope, 99));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Rope, 99));
            Run(sim, 1);
            q = sim.GetEntity(id).Craft;
            Assert.Equal(2, q.Count);
            Assert.Equal(99, q.LeftAt(0));
            Assert.Equal(51, q.LeftAt(1)); // на 150 верёвок волокна хватило, больше — нет
            Assert.Equal(0, sim.GetEntity(id).Inv.Count(Items.Fiber));
            Assert.Equal(450, Total(sim, id, Items.Fiber));
        }

        /// <summary>
        /// Прогресс идёт, пока персонаж рядом со станком: ушёл на 4+ тайла — стоит (и не теряется), вернулся — доделывается
        /// (<c>06</c> 3.7 п. 2). Ручной рецепт делается где угодно.
        /// </summary>
        [Fact]
        public void E23_Progress_OnlyNearStation_HandsAnywhere()
        {
            Simulation sim = Sim(out int id, World(benchDx: -3, campfire: false), (Items.CopperOre, 4), (Items.Fiber, 6));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperIngot, 2));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Rope, 2));
            Run(sim, 5);
            Assert.Equal(5, sim.GetEntity(id).Craft.Progress);

            // Уходим на восток, от верстака: дальше 3 тайлов прогресс замирает.
            sim.SetInput(id, new InputFrame(Input8.East));
            Run(sim, 15); // 6 тайлов/с: 3 тайла — до верстака 6 тайлов (дальше радиуса 3, ближе поиска 8)
            sim.SetInput(id, new InputFrame(Input8.None));
            Run(sim, 5);
            SimEntity e = sim.GetEntity(id);
            Assert.False(sim.CraftStationNear(id, CraftStation.Workbench));
            int frozen = e.Craft.Progress;
            Assert.InRange(frozen, 5, 6);
            Assert.Equal(0, e.Inv.Count(Items.CopperIngot));
            Run(sim, 60);
            e = sim.GetEntity(id);
            Assert.Equal(frozen, e.Craft.Progress);
            Assert.Equal(0, e.Inv.Count(Items.Rope)); // очередь не перескакивает через стоящую позицию
            Assert.Equal(2, e.Craft.Count);

            // Отменили стоящую позицию — руды вернулось на оба слитка, верёвка (руками) делается вдали от станка.
            Assert.Equal(CraftReply.Accepted, sim.RequestCraftCancel(id, 0));
            Run(sim, 2 * T);
            e = sim.GetEntity(id);
            Assert.Equal(4, e.Inv.Count(Items.CopperOre));
            Assert.Equal(2, e.Inv.Count(Items.Rope));
            Assert.True(e.Craft.IsEmpty);

            // Вернулись к верстаку — слиток доделывается с места остановки.
            Assert.Equal(CraftReply.StationFar, sim.RequestCraft(id, RecipeTable.CopperIngot, 1));
            sim.SetInput(id, new InputFrame(Input8.West));
            Run(sim, 20);
            sim.SetInput(id, new InputFrame(Input8.None));
            Run(sim, 2);
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperIngot, 1));
            Run(sim, T);
            Assert.Equal(1, sim.GetEntity(id).Inv.Count(Items.CopperIngot));
        }

        /// <summary>Отмена возвращает ингредиенты оставшихся изготовлений (вместе с начатым); готовое остаётся.</summary>
        [Fact]
        public void E23_Cancel_ReturnsIngredients()
        {
            Simulation sim = Sim(out int id, null, (Items.CopperOre, 11), (Items.Wood, 20));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperIngot, 5));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.WoodenSword, 2));
            Run(sim, 3);
            Assert.Equal(1, sim.GetEntity(id).Inv.Count(Items.CopperOre));
            Assert.Equal(4, sim.GetEntity(id).Inv.Count(Items.Wood));

            // Отмена второй позиции — дерево обратно; первая идёт дальше.
            Assert.Equal(CraftReply.Accepted, sim.RequestCraftCancel(id, 1));
            Run(sim, 1);
            Assert.Equal(20, sim.GetEntity(id).Inv.Count(Items.Wood));
            Assert.Equal(1, sim.GetEntity(id).Craft.Count);
            Assert.Equal(4, sim.GetEntity(id).Craft.Progress);

            // Один слиток готов, второй начат — отмена возвращает руду на четыре оставшихся (и начатый).
            Run(sim, T);
            Assert.Equal(1, sim.GetEntity(id).Inv.Count(Items.CopperIngot));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraftCancel(id, 0));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraftCancel(id, 0)); // вторая отмена той же позиции в том же такте — пустая
            Run(sim, 1);
            SimEntity e = sim.GetEntity(id);
            Assert.True(e.Craft.IsEmpty);
            Assert.Equal(0, e.Craft.Progress);
            Assert.Equal(9, e.Inv.Count(Items.CopperOre));
            Assert.Equal(1, e.Inv.Count(Items.CopperIngot));
            Assert.Equal(0, sim.Piles.Length);
            Run(sim, 40);
            Assert.Equal(1, sim.GetEntity(id).Inv.Count(Items.CopperIngot)); // после отмены ничего не доделывается

            // Возврат не влезает в сумку — остаток кучей у ног: 39 ячеек заняты, руда лежала в одной.
            sim = Sim(out id, null, (Items.CopperOre, 99));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperIngot, 49));
            Run(sim, 1);
            Inventory bag = sim.GetEntity(id).Inv;
            for (int cell = 0; cell < Inventory.Cells; cell++)
            {
                if (bag.ItemAt(cell) == Items.None)
                {
                    bag.SetCell(cell, Items.Fiber, 99);
                }
            }

            Assert.True(sim.SetInventory(id, bag));
            Assert.Equal(1, bag.Count(Items.CopperOre));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraftCancel(id, 0));
            Run(sim, 1);
            e = sim.GetEntity(id);
            Assert.Equal(99, e.Inv.Count(Items.CopperOre)); // доложено в свою неполную стопку: 1 + 98
            Assert.Equal(0, OnGround(sim, Items.CopperOre));
            Assert.Equal(99, Total(sim, id, Items.CopperOre));
        }

        /// <summary>Полная сумка: результат падает кучей у ног; освободилось место — поднимается.</summary>
        [Fact]
        public void E23_FullBag_ResultDropsAsPile()
        {
            Simulation sim = Sim(out int id, null, (Items.Wood, 2), (Items.Stone, 2));
            Inventory bag = sim.GetEntity(id).Inv;
            for (int cell = 0; cell < Inventory.Cells; cell++)
            {
                if (bag.ItemAt(cell) == Items.None)
                {
                    bag.SetCell(cell, Items.Fiber, 99);
                }
            }

            Assert.True(sim.SetInventory(id, bag));
            Assert.Equal(0, bag.RoomFor(Items.Arrows));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Arrows, 1));
            Run(sim, T);
            SimEntity e = sim.GetEntity(id);
            Assert.True(e.Craft.IsEmpty);
            Assert.Equal(0, e.Inv.Count(Items.Arrows));
            GroundPile pile = Assert.Single(sim.Piles.ToArray());
            Assert.Equal((Items.Arrows, 10), (pile.Item, (int)pile.Count));
            Assert.True(Torus.Dist2(sim.Shape, pile.Position, e.Mover.Position) <= (long)U * U); // у ног
            Run(sim, 60);
            Assert.Equal(10, OnGround(sim, Items.Arrows)); // места нет — лежит

            // Освободили ячейку — куча поднимается сама (подбор E2-1).
            int fiberCell = 39;
            Assert.Equal(Items.Fiber, e.Inv.ItemAt(fiberCell));
            Assert.True(sim.RequestBagOp(id, BagOp.Drop, fiberCell, fiberCell, 0));
            Run(sim, 3);
            Assert.Equal(10, sim.GetEntity(id).Inv.Count(Items.Arrows));
            Assert.Equal(0, OnGround(sim, Items.Arrows));
        }

        /// <summary>Смерть: очередь сбрасывается, ингредиенты оставшихся изготовлений — кучей у тела; готовое — в сумке.</summary>
        [Fact]
        public void E23_Death_DropsQueueAsPiles()
        {
            Simulation sim = Sim(out int id, null, (Items.CopperOre, 10), (Items.Wood, 16));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperIngot, 5));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.WoodenSword, 2));
            Run(sim, T + 4); // один слиток готов, второй начат
            Assert.Equal(1, sim.GetEntity(id).Inv.Count(Items.CopperIngot));
            WorldPos body = sim.GetEntity(id).Mover.Position;
            Assert.True(sim.ApplyStrike(id, 0, 500) > 0);
            Run(sim, 1);
            SimEntity e = sim.GetEntity(id);
            Assert.False(e.Alive);
            Assert.True(e.Craft.IsEmpty);
            Assert.Equal(0, e.Craft.Progress);
            Assert.Equal(0, e.Inv.Count(Items.CopperOre));
            Assert.Equal(0, e.Inv.Count(Items.Wood));
            Assert.Equal(1, e.Inv.Count(Items.CopperIngot));
            Assert.Equal(8, OnGround(sim, Items.CopperOre));
            Assert.Equal(16, OnGround(sim, Items.Wood));
            foreach (GroundPile p in sim.Piles)
            {
                Assert.True(Torus.Dist2(sim.Shape, p.Position, body) <= (long)U * U);
            }
        }

        /// <summary>
        /// Границы заявок не ломают и не размножают: × 0 и отрицательное — отказ; огромное — обрезается до того, на что
        /// хватает; заявок больше очереди такта — лишние не берутся. Масса: руда до = 2 × слитки + руда после.
        /// </summary>
        [Fact]
        public void E23_Bounds_Zero_Negative_Huge_MassConserved()
        {
            Simulation sim = Sim(out int id, null, (Items.CopperOre, 7));
            Assert.Equal(CraftReply.BadCount, sim.RequestCraft(id, RecipeTable.CopperIngot, 0));
            Assert.Equal(CraftReply.BadCount, sim.RequestCraft(id, RecipeTable.CopperIngot, -1));
            Assert.Equal(CraftReply.BadCount, sim.RequestCraft(id, RecipeTable.CopperIngot, int.MinValue));
            Run(sim, 2);
            Assert.Equal(7, sim.GetEntity(id).Inv.Count(Items.CopperOre));
            Assert.True(sim.GetEntity(id).Craft.IsEmpty);

            // Огромное количество: принимается столько, на сколько хватает (3 слитка из 7 руды), 1 руда остаётся.
            Assert.Equal(CraftReply.Accepted, sim.CheckCraft(id, RecipeTable.CopperIngot, int.MaxValue, out int units));
            Assert.Equal(3, units);
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperIngot, int.MaxValue));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperIngot, 70_000));
            Run(sim, 1);
            Assert.Equal(3, sim.GetEntity(id).Craft.LeftAt(0));
            Assert.Equal(1, sim.GetEntity(id).Inv.Count(Items.CopperOre));
            Assert.Equal(7, Total(sim, id, Items.CopperOre));
            Run(sim, 3 * T);
            SimEntity e = sim.GetEntity(id);
            Assert.Equal(3, e.Inv.Count(Items.CopperIngot));
            Assert.Equal(1, e.Inv.Count(Items.CopperOre));
            Assert.Equal(7, 2 * e.Inv.Count(Items.CopperIngot) + e.Inv.Count(Items.CopperOre));

            // Заявок больше очереди такта (256): лишние не берутся («занято»), масса та же.
            sim = Sim(out id, null, (Items.Fiber, 99), (Items.Fiber, 99), (Items.Fiber, 99));
            int accepted = 0, busy = 0;
            for (int i = 0; i < 600; i++)
            {
                CraftReply r = i % 2 == 0 ? sim.RequestCraft(id, RecipeTable.Rope, 1) : sim.RequestCraftCancel(id, 3);
                accepted += r == CraftReply.Accepted ? 1 : 0;
                busy += r == CraftReply.Busy ? 1 : 0;
            }

            Assert.Equal(Simulation.MaxBagOpsPerTick, accepted);
            Assert.Equal(300 - Simulation.MaxBagOpsPerTick, busy); // отмены несуществующей позиции отсеяны раньше («нет позиции») и очередь такта не занимали
            Assert.Equal(CraftReply.Busy, sim.RequestCraft(id, RecipeTable.Rope, 1));
            Run(sim, 1);
            Assert.Equal(297, Total(sim, id, Items.Fiber));
            Assert.Equal(99, sim.GetEntity(id).Craft.LeftAt(0)); // в позицию влезло 99, остальные заявки отклонены в такте
            Run(sim, 99 * T + 5);
            e = sim.GetEntity(id);
            Assert.True(e.Craft.IsEmpty);
            Assert.Equal(99, e.Inv.Count(Items.Rope));
            Assert.Equal(297, 3 * e.Inv.Count(Items.Rope) + e.Inv.Count(Items.Fiber));
            Assert.Equal(0, sim.Piles.Length);

            // Уход из мира: очередь возвращается в сумку целиком.
            sim = Sim(out id, null, (Items.CopperOre, 10));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.CopperIngot, 5));
            Run(sim, 4);
            sim.RefundCraftQueue(id);
            Assert.True(sim.GetEntity(id).Craft.IsEmpty);
            Assert.Equal(10, sim.GetEntity(id).Inv.Count(Items.CopperOre));
            sim.RefundCraftQueue(id + 50); // нет такой сущности — ничего
        }

        /// <summary>Очередь изготовления входит в хэш состояния: состав, «осталось», прогресс; пустая очередь хэш не меняет.</summary>
        [Fact]
        public void E23_StateHash_CoversQueue()
        {
            ulong HashWith(Action<Simulation, int> act, int ticks)
            {
                Simulation sim = Sim(out int id, null, (Items.Fiber, 30), (Items.CopperOre, 30));
                act(sim, id);
                Run(sim, ticks);
                return StateHash.Compute(sim).Total;
            }

            ulong none = HashWith((_, _) => { }, 3);
            ulong rope1 = HashWith((s, id) => s.RequestCraft(id, RecipeTable.Rope, 1), 3);
            Assert.Equal(rope1, HashWith((s, id) => s.RequestCraft(id, RecipeTable.Rope, 1), 3)); // детерминизм
            Assert.NotEqual(none, rope1);

            // Тот же расход волокна (6), разный состав очереди: «× 2» и «× 1 + отмена не было» — хэши разные по «осталось».
            var a = new CraftQueue();
            var b = new CraftQueue();
            Assert.Equal(2, a.Push(RecipeTable.Rope, 2));
            Assert.Equal(1, b.Push(RecipeTable.Rope, 1));
            ulong H(CraftQueue q)
            {
                Simulation sim = Sim(out int id, null, (Items.Fiber, 30));
                SimEntity e = sim.GetEntity(id);
                e.Craft = q;
                return StateHash.Compute(sim.World, sim.Tick, new[] { e }).Total;
            }

            ulong ha = H(a);
            Assert.NotEqual(ha, H(b)); // «осталось»
            Assert.NotEqual(H(default), ha);
            CraftQueue p = a;
            p.Progress = 7;
            Assert.NotEqual(ha, H(p)); // прогресс
            var c = new CraftQueue();
            c.Push(RecipeTable.Torch, 2);
            Assert.NotEqual(ha, H(c)); // рецепт
            CraftQueue two = a;
            two.Push(RecipeTable.Torch, 1);
            Assert.NotEqual(ha, H(two)); // вторая позиция
            CraftQueue swapped = default;
            swapped.Push(RecipeTable.Torch, 1);
            swapped.Push(RecipeTable.Rope, 2);
            Assert.NotEqual(H(two), H(swapped)); // порядок позиций

            // Пустая очередь хэш не трогает: сущность без очереди хэшируется как до E2-3 (эталоны сценариев целы).
            Simulation plain = Sim(out int pid, null, (Items.Fiber, 30));
            SimEntity pe = plain.GetEntity(pid);
            Assert.Equal(StateHash.Compute(plain.World, plain.Tick, new[] { pe }).Total, H(default));
        }

        // ---------- Сохранение ----------

        /// <summary>
        /// Формат 8: очередь изготовления в записи сущности — круг сходится (хэш тот же), загруженный мир доделывает
        /// очередь так же, как исходный; испорченная очередь — ошибка чтения, а не тихая потеря.
        /// </summary>
        [Fact]
        public void E23_Save_Format8_QueueRoundTrip()
        {
            Assert.Equal(8u, SaveModule.FormatVersion);
            SaveFixtures.LiveWorld live = SaveFixtures.Create(256, 23, bots: 2);
            Simulation sim = live.Sim;
            int id = sim.Entities[0].Id;
            var bag = new Inventory();
            bag.Add(Items.Fiber, 30);
            bag.Add(Items.Wood, 9);
            bag.Add(Items.Resin, 9);
            sim.SetInventory(id, bag);
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Rope, 4));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Torch, 3));
            live.Run(ticks: T + 6, editsPerTick: 0);
            CraftQueue q = sim.GetEntity(id).Craft;
            Assert.Equal(2, q.Count);
            Assert.Equal(3, q.LeftAt(0));
            Assert.Equal(6, q.Progress);

            byte[] save = WorldSave.Build(WorldSave.Capture(sim, live.BaseSha, "крафт"), 3, 0);
            SaveFile f = SaveContainer.Read(save);
            Assert.True(f.IsOk, f.Error);
            Assert.Equal(8u, f.Header.FormatVersion);
            LoadedWorldState st = WorldSave.Load(f, WorldSnapshot.LoadBase(live.BaseBytes), live.BaseSha);
            Assert.Equal(q, st.Entities[0].Craft);
            Assert.True(st.Entities[1].Craft.IsEmpty);

            Simulation loaded = SaveFixtures.Load(live.BaseBytes, save);
            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total);
            for (int t = 0; t < 7 * T; t++)
            {
                sim.Step();
                loaded.Step();
            }

            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total);
            SimEntity e = loaded.GetEntity(id);
            Assert.True(e.Craft.IsEmpty);
            Assert.Equal(4, e.Inv.Count(Items.Rope));
            Assert.Equal(3 + 0, e.Inv.Count(Items.Torch));
            Assert.Equal(18, e.Inv.Count(Items.Fiber));

            // Чтение payload очереди: мусор — исключение.
            var raw = new byte[CraftQueue.PayloadBytes];
            q.WritePayload(raw);
            Assert.Equal(q, CraftQueue.ReadPayload(raw));
            byte[] bad = (byte[])raw.Clone();
            bad[2] = 200; // нет такого рецепта
            Assert.Throws<ArgumentException>(() => CraftQueue.ReadPayload(bad));
            bad = (byte[])raw.Clone();
            bad[3] = 0; // «осталось» 0 при рецепте
            Assert.Throws<ArgumentException>(() => CraftQueue.ReadPayload(bad));
            bad = (byte[])raw.Clone();
            bad[2] = 0;
            bad[3] = 0; // дырка: первая позиция пуста, вторая — нет
            Assert.Throws<ArgumentException>(() => CraftQueue.ReadPayload(bad));
            bad = (byte[])raw.Clone();
            bad[0] = (byte)T; // прогресс не меньше времени рецепта
            Assert.Throws<ArgumentException>(() => CraftQueue.ReadPayload(bad));
            Assert.Throws<ArgumentException>(() => CraftQueue.ReadPayload(new byte[CraftQueue.PayloadBytes - 1]));
        }

        /// <summary>
        /// Настоящий файл формата 7 (записан кодом <c>main</c> 2979414 — до E2-3; генератор — <c>Golden/format7_bag.make.cs.txt</c>:
        /// мир-заглушка 256², зерно 5, 30 тактов, две сущности с сумками, персонаж вне мира, куча руды) читается новым кодом:
        /// сумки, куча и хэш состояния те же, очереди пустые; пересохранение — формат 8.
        /// </summary>
        [Fact]
        public void E23_Save_Format7_RealFile_Reads()
        {
            byte[] v7 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format7_bag.save"));
            byte[] baseBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format7_bag.base"));
            SaveFile f = SaveContainer.Read(v7);
            Assert.True(f.IsOk, f.Error);
            Assert.Equal(7u, f.Header.FormatVersion);
            LoadedWorldState st = WorldSave.Load(f, WorldSnapshot.LoadBase(baseBytes), WorldSnapshot.StoredHash(baseBytes));
            Assert.Equal(7u, st.FormatVersion);
            Assert.Equal(30, st.Tick);
            Assert.Equal(2, st.Entities.Length);
            Assert.Equal(2, st.Characters.Length);

            Inventory a = st.Entities[0].Inv;
            Assert.Equal(150, a.Count(Items.Wood));
            Assert.Equal(40, a.Count(Items.Stone));
            Assert.Equal(12, a.Count(Items.Resin));
            Assert.Equal(5, a.Count(Items.Fiber));
            Assert.Equal(17, a.Count(Items.CopperOre));
            Assert.Equal(Items.StoneAxe, a.Held);
            Assert.Equal(a, st.Characters[0].Inv);
            Assert.Equal(33, st.Entities[1].Inv.Count(Items.Wood));
            Assert.Equal(Items.CopperPickaxe, st.Entities[1].Inv.ItemAt(7));
            Assert.Equal(250, st.Characters[1].Inv.Count(Items.Stone));
            Assert.Equal(1200, st.Characters[1].Inv.Count(Items.Arrows));
            Assert.True(st.Entities[0].Craft.IsEmpty);
            Assert.True(st.Entities[1].Craft.IsEmpty);
            GroundPile pile = Assert.Single(st.Piles);
            Assert.Equal((Items.CopperOre, 9), (pile.Item, (int)pile.Count));

            // Хэш состояния — тот же, что посчитал код, записавший файл.
            Simulation sim = SaveFixtures.Load(baseBytes, v7);
            Assert.Equal("d96dbde9cfe7c8ff", StateHash.Compute(sim).Total.ToString("x16"));

            // Мир из старого файла живёт: руды 17 → слитки у верстака нет (отказ), верёвка руками — делается.
            int id = st.Entities[0].Id;
            Assert.Equal(CraftReply.NoStation, sim.RequestCraft(id, RecipeTable.CopperIngot, 1));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, RecipeTable.Rope, 1));
            Run(sim, T);
            Assert.Equal(1, sim.GetEntity(id).Inv.Count(Items.Rope));

            byte[] again = WorldSave.Build(WorldSave.Capture(sim, WorldSnapshot.StoredHash(baseBytes), "формат7", st.Characters), 5, 0);
            Assert.Equal(8u, SaveContainer.Read(again).Header.FormatVersion);
            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(SaveFixtures.Load(baseBytes, again)).Total);
        }

        // ---------- Сеть ----------

        /// <summary>«Золотые» байты протокола 22: заявка изготовления (30), ответ (31), очередь (32); круг сходится, мусор — отказ.</summary>
        [Fact]
        public void E23_Net_GoldenBytes_CraftRequest_Reply_State()
        {
            Assert.Equal(22, NetModule.ProtocolVersion);
            var buf = new byte[64];

            int n = new CraftRequestMessage { Op = CraftOp.Make, Arg = RecipeTable.CopperPickaxe, Count = 0x0102 }.Write(buf);
            Assert.Equal(CraftRequestMessage.Bytes, n);
            Assert.Equal("1E-01-0A-02-01", BitConverter.ToString(buf, 0, n));
            CraftRequestMessage req = CraftRequestMessage.Read(new ReadOnlySpan<byte>(buf, 0, n));
            Assert.Equal((CraftOp.Make, RecipeTable.CopperPickaxe, 0x0102), (req.Op, req.Arg, (int)req.Count));
            buf[1] = 0; // нет такой операции
            Assert.Throws<ProtocolException>(() => CraftRequestMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));
            buf[1] = 3;
            Assert.Throws<ProtocolException>(() => CraftRequestMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));
            n = new CraftRequestMessage { Op = CraftOp.Cancel, Arg = 4 }.Write(buf);
            Assert.Equal("1E-02-04-00-00", BitConverter.ToString(buf, 0, n));
            buf[2] = 5; // позиции 5 в очереди нет
            Assert.Throws<ProtocolException>(() => CraftRequestMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));
            Assert.Throws<ProtocolException>(() => CraftRequestMessage.Read(new ReadOnlySpan<byte>(buf, 0, n - 1)));

            n = new CraftReplyMessage { Op = CraftOp.Make, Arg = 9, Reply = CraftReply.StationFar }.Write(buf);
            Assert.Equal(CraftReplyMessage.Bytes, n);
            Assert.Equal("1F-01-09-05", BitConverter.ToString(buf, 0, n));
            Assert.Equal(CraftReply.StationFar, CraftReplyMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)).Reply);
            buf[3] = 12; // нет такого ответа
            Assert.Throws<ProtocolException>(() => CraftReplyMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));

            var q = new CraftQueue();
            q.Push(RecipeTable.CopperIngot, 8);
            q.Push(RecipeTable.CopperPickaxe, 1);
            q.Progress = 7;
            n = new CraftStateMessage { EntityId = 0x0A0B, Tick = 0x01020304, Running = true, Queue = q }.Write(buf);
            Assert.Equal(CraftStateMessage.Bytes, n);
            Assert.Equal(22, n);
            Assert.Equal("20-0B-0A-00-00-04-03-02-01-01-07-00-09-08-0A-01-00-00-00-00-00-00", BitConverter.ToString(buf, 0, n));
            CraftStateMessage st = CraftStateMessage.Read(new ReadOnlySpan<byte>(buf, 0, n));
            Assert.Equal(q, st.Queue);
            Assert.True(st.Running);
            Assert.Equal(0x01020304u, st.Tick);
            buf[9] = 2; // признаки
            Assert.Throws<ProtocolException>(() => CraftStateMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));
            buf[9] = 0;
            buf[12] = 99; // нет такого рецепта
            Assert.Throws<ProtocolException>(() => CraftStateMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));

            Assert.True(Codec.IsKnown(30) && Codec.IsKnown(31) && Codec.IsKnown(32));
            Assert.False(Codec.IsKnown(33));
        }

        private static void Give(EditTests.EditRig rig, NetClient c, params (byte Item, int Count)[] items)
        {
            Inventory bag = rig.Sim.GetEntity(c.EntityId).Inv;
            foreach ((byte item, int count) in items)
            {
                Assert.Equal(count, bag.Add(item, count));
            }

            Assert.True(rig.Sim.SetInventory(c.EntityId, bag));
        }

        /// <summary>
        /// По сети: клиент просит «слиток × 3» у верстака → ответ «принято», очередь (32) и сумка (26) от сервера, через
        /// 45 тактов слитки в сумке клиента; отказ приходит с причиной; отмена возвращает руду. Клиент не предсказывает.
        /// </summary>
        [Fact]
        public void E23_Net_Craft_ServerDecides_ReplyQueueBag()
        {
            using var rig = new EditTests.EditRig(World(), _ => Center(300, 200));
            NetClient c = rig.Join();
            var replies = new List<CraftReplyMessage>();
            var states = new List<CraftStateMessage>();
            c.CraftReplied = replies.Add;
            c.CraftChanged = states.Add;
            rig.Run(10);
            Assert.True(c.Craft.IsEmpty);
            Assert.Equal(0, c.CraftStatesSeen); // пустую очередь при входе сервер не шлёт
            Give(rig, c, (Items.CopperOre, 9));
            rig.Run(3);
            Assert.Equal(9, c.Inv.Count(Items.CopperOre));

            Assert.True(c.RequestCraft(RecipeTable.CopperIngot, 3));
            Assert.Equal(9, c.Inv.Count(Items.CopperOre)); // не предсказано: ждём сервер
            rig.Run(4);
            Assert.Equal(CraftReply.Accepted, Assert.Single(replies).Reply);
            Assert.Equal(RecipeTable.CopperIngot, replies[0].Arg);
            Assert.Equal(3, c.Inv.Count(Items.CopperOre));
            Assert.Equal(1, c.Craft.Count);
            Assert.Equal((RecipeTable.CopperIngot, 3), (c.Craft.RecipeAt(0), c.Craft.LeftAt(0)));
            Assert.True(c.CraftRunning);
            Assert.InRange(c.CraftProgressAt(c.CraftTick + 5), 5, T);

            rig.Run(3 * T + 4);
            Assert.True(c.Craft.IsEmpty);
            Assert.Equal(3, c.Inv.Count(Items.CopperIngot));
            Assert.Equal(rig.Sim.GetEntity(c.EntityId).Inv, c.Inv);
            Assert.Equal(4, states.Count); // 3, 2, 1 осталось и пусто — прогресс каждый такт не шлётся

            // Отказ с причиной: на кирку слитков мало; нет такого рецепта — заявку клиент не шлёт вовсе.
            Assert.True(c.RequestCraft(RecipeTable.CopperPickaxe, 1));
            Assert.False(c.RequestCraft(0, 1));
            Assert.False(c.RequestCraft(RecipeTable.Rope, 0));
            rig.Run(4);
            Assert.Equal(2, replies.Count);
            Assert.Equal(CraftReply.NoIngredients, replies[1].Reply);
            Assert.Equal(CraftReply.NoIngredients, c.LastCraftReply.Reply);
            Assert.Equal(1, c.CraftRefusalsSeen);
            Assert.Equal(1, rig.Server.CraftRequestsRefused);

            // Отмена: руда возвращается.
            Assert.True(c.RequestCraft(RecipeTable.CopperIngot, 1));
            rig.Run(4);
            Assert.Equal(1, c.Inv.Count(Items.CopperOre));
            Assert.True(c.RequestCraftCancel(0));
            Assert.False(c.RequestCraftCancel(5));
            rig.Run(4);
            Assert.True(c.Craft.IsEmpty);
            Assert.Equal(3, c.Inv.Count(Items.CopperOre));
            Assert.Equal(3, c.Inv.Count(Items.CopperIngot));
            Assert.Equal(CraftReply.Accepted, replies[3].Reply);
            Assert.Equal(CraftOp.Cancel, replies[3].Op);
            Assert.Equal(0, c.BadMessages);
        }

        /// <summary>
        /// Пауза — отказ «пауза» (заявки не копятся и после паузы не исполняются); мёртвому — «мёртв»; лимит заявок игрока
        /// за такт общий с сумкой: 5 заявок на сумку + 3 крафта проходят, остальные — «занято».
        /// </summary>
        [Fact]
        public void E23_Net_Pause_Dead_SharedLimit()
        {
            using var rig = new EditTests.EditRig(World(), _ => Center(300, 200));
            NetClient c = rig.Join();
            var replies = new List<CraftReplyMessage>();
            c.CraftReplied = replies.Add;
            rig.Run(10);
            Give(rig, c, (Items.Fiber, 90));
            rig.Run(3);
            Inventory bag = rig.Sim.GetEntity(c.EntityId).Inv;

            rig.Server.SetPaused(true);
            Assert.True(c.RequestCraft(RecipeTable.Rope, 2));
            rig.Run(5);
            Assert.Equal(CraftReply.Paused, Assert.Single(replies).Reply);
            rig.Server.SetPaused(false);
            rig.Run(2 * T + 5);
            Assert.Equal(bag, rig.Sim.GetEntity(c.EntityId).Inv); // отклонённое в паузе после паузы не исполнилось
            Assert.True(rig.Sim.GetEntity(c.EntityId).Craft.IsEmpty);

            // Общий лимит 8 заявок за такт: 5 «на сумку» + 6 крафтов в одном кадре → 3 крафта приняты, 3 — «занято».
            replies.Clear();
            int fiberCell = -1;
            for (int cell = 0; cell < Inventory.Cells; cell++)
            {
                if (bag.ItemAt(cell) == Items.Fiber)
                {
                    fiberCell = cell;
                }
            }

            for (int i = 0; i < 5; i++)
            {
                Assert.True(c.RequestBagSplit(fiberCell, 20 + i, 1));
            }

            for (int i = 0; i < 6; i++)
            {
                Assert.True(c.RequestCraft(RecipeTable.Rope, 1));
            }

            rig.Run(4);
            Assert.Equal(6, replies.Count);
            Assert.Equal(3, replies.FindAll(r => r.Reply == CraftReply.Accepted).Count);
            Assert.Equal(3, replies.FindAll(r => r.Reply == CraftReply.Busy).Count);
            Assert.Equal(3, rig.Sim.GetEntity(c.EntityId).Craft.LeftAt(0));
            rig.Run(3 * T);
            SimEntity e = rig.Sim.GetEntity(c.EntityId);
            Assert.Equal(3, e.Inv.Count(Items.Rope));
            Assert.Equal(90, 3 * e.Inv.Count(Items.Rope) + e.Inv.Count(Items.Fiber));

            // Мёртвый: ответ «мёртв», сумка цела.
            replies.Clear();
            Assert.True(rig.Sim.ApplyStrike(c.EntityId, 0, 500) > 0);
            Assert.True(c.RequestCraft(RecipeTable.Rope, 1));
            rig.Run(5);
            Assert.Equal(CraftReply.Dead, Assert.Single(replies).Reply);
            Assert.Equal(81, rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Fiber));
            Assert.Equal(0, c.BadMessages);
        }
    }
}
