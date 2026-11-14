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
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-1 (шаг 2.1 плана, <c>06</c> 5.3): добыча лежит на земле и подбирается; сумка — ячейки и стопки.
    /// Условие шага: срубил дерево → на земле куча → подошёл → она в ячейке; это сохраняется и передаётся по сети.
    /// </summary>
    public class GroundItemTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat(int size = 1024) => FlatWorldGenerator.Instance.Generate(MapShape.World(size), 1);

        private static WorldPos Center(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private static Simulation SimWithPlayer(out int id, int x = 300, int y = 200, WorldModel? world = null)
        {
            var sim = new Simulation(world ?? Flat(), maxEntities: 8);
            id = sim.AddEntity(Center(x, y));
            Assert.True(sim.SetInventory(id, Inventory.StartingKit()));
            return sim;
        }

        private static void SetBag(Simulation sim, int id, Action<InvBox> edit)
        {
            var box = new InvBox { Inv = sim.GetEntity(id).Inv };
            edit(box);
            Assert.True(sim.SetInventory(id, box.Inv));
        }

        private sealed class InvBox
        {
            public Inventory Inv;
        }

        // ---------- Таблица предметов ----------

        /// <summary>Таблица предметов данными: категории и размеры стопок (<c>06</c> 1.3, 5.3); материалы — те же предметы.</summary>
        [Fact]
        public void E21_ItemTable_Categories_Stacks_MaterialsAreItems()
        {
            Assert.Equal(14, ItemTable.Count);
            for (int id = 1; id < ItemTable.Count; id++)
            {
                ItemDef d = ItemTable.Get((byte)id);
                Assert.Equal(id, d.Id);
                Assert.False(string.IsNullOrEmpty(d.Key));
                Assert.True(d.MaxStack >= 1, d.Key);
                Assert.NotEqual(ItemCategory.None, d.Category);
            }

            Assert.Equal(99, Items.MaxStackOf(Items.Wood));
            Assert.Equal(99, Items.MaxStackOf(Items.Stone));
            Assert.Equal(99, Items.MaxStackOf(Items.CopperOre));
            Assert.Equal(999, Items.MaxStackOf(Items.Arrows));
            Assert.Equal(99, Items.MaxStackOf(Items.Torch));
            Assert.Equal(1, Items.MaxStackOf(Items.StonePickaxe));
            Assert.Equal(1, Items.MaxStackOf(Items.WoodenSword));
            Assert.Equal(0, Items.MaxStackOf(Items.None));
            Assert.Equal(0, Items.MaxStackOf((byte)ItemTable.Count));

            Assert.Equal(ItemCategory.Material, ItemTable.Get(Items.Wood).Category);
            Assert.Equal(ItemCategory.Tool, ItemTable.Get(Items.Hammer).Category);
            Assert.Equal(ItemCategory.Weapon, ItemTable.Get(Items.SimpleBow).Category);
            Assert.Equal(ItemCategory.Consumable, ItemTable.Get(Items.Arrows).Category);
            Assert.Equal(ItemCategory.Building, ItemTable.Get(Items.Torch).Category);

            Assert.Equal(Items.Wood, ItemTable.OfMaterial(Material.Wood));
            Assert.Equal(Items.CopperOre, ItemTable.OfMaterial(Material.Copper));
            Assert.Equal(Items.Torch, ItemTable.OfMaterial(Material.Torch));
            Assert.Equal(Items.None, ItemTable.OfMaterial(Material.Count));
        }

        // ---------- Сумка: ячейки и стопки ----------

        /// <summary>Стартовый набор — раскладка по ячейкам: панель 0…5, стрелы в первой ячейке сумки; всего ячеек 40.</summary>
        [Fact]
        public void E21_Bag_StartingKit_Cells()
        {
            Assert.Equal(40, Inventory.Cells);
            Assert.Equal(8, Inventory.Slots);
            Inventory kit = Inventory.StartingKit();
            Assert.Equal(7, kit.UsedCells);
            Assert.Equal(Items.Torch, kit.ItemAt(5));
            Assert.Equal(3, kit.CountIn(5));
            Assert.Equal(Items.Arrows, kit.ItemAt(8));
            Assert.Equal(50, kit.CountIn(8));
            Assert.Equal(1, kit.CountIn(0));
            Assert.Equal(Items.None, kit.ItemAt(40));
            Assert.Equal(0, kit.CountIn(-1));
        }

        /// <summary>
        /// «Положить»: доукладка в неполные стопки, потом пустые ячейки сумки, потом панель; 150 камня — 2 ячейки
        /// (99 + 51), 1500 стрел — 2 ячейки (999 + 501) (<c>06</c> 5.7 п. 2). Снаряжение не стопкуется.
        /// </summary>
        [Fact]
        public void E21_Bag_Add_TopsUpStacks_ThenEmptyCells()
        {
            var inv = new Inventory();
            Assert.Equal(150, inv.Add(Items.Stone, 150));
            Assert.Equal(99, inv.CountIn(8));
            Assert.Equal(51, inv.CountIn(9));
            Assert.Equal(2, inv.UsedCells);

            Assert.Equal(1500, inv.Add(Items.Arrows, 1500));
            Assert.Equal(999, inv.CountIn(10));
            Assert.Equal(501, inv.CountIn(11));

            // Доукладка: 60 камня — 48 в неполную ячейку 9, остальные 12 — в новую.
            Assert.Equal(60, inv.Add(Items.Stone, 60));
            Assert.Equal(99, inv.CountIn(9));
            Assert.Equal(Items.Stone, inv.ItemAt(12));
            Assert.Equal(12, inv.CountIn(12));
            Assert.Equal(210, inv.Count(Items.Stone));

            // Снаряжение — по одному в ячейке.
            Assert.Equal(2, inv.Add(Items.StoneAxe, 2));
            Assert.Equal(1, inv.CountIn(13));
            Assert.Equal(1, inv.CountIn(14));

            // Сумка (8…39) кончилась — дальше занимается панель.
            var full = new Inventory();
            Assert.Equal(32 * 99, full.Add(Items.Wood, 32 * 99));
            Assert.Equal(Items.None, full.ItemAt(0));
            Assert.Equal(5, full.Add(Items.Wood, 5));
            Assert.Equal(Items.Wood, full.ItemAt(0));
            Assert.Equal(8 * 99 - 5, full.RoomFor(Items.Wood));
            Assert.Equal(7 * 99, full.RoomFor(Items.Stone));
            Assert.Equal(0, full.Add((byte)200, 5)); // нет такого предмета
        }

        /// <summary>Перемещение, обмен, доукладка, деление стопки; снятие предмета из нескольких стопок — целиком или никак.</summary>
        [Fact]
        public void E21_Bag_Move_Swap_Merge_Split_RemoveAcrossStacks()
        {
            var inv = Inventory.StartingKit();
            inv.SetCell(10, Items.Wood, 60);
            inv.SetCell(11, Items.Wood, 70);
            inv.SetCell(12, Items.Stone, 5);

            // Перенос в пустую ячейку.
            Assert.True(inv.Move(12, 20));
            Assert.Equal(Items.None, inv.ItemAt(12));
            Assert.Equal(5, inv.CountIn(20));

            // Обмен разных предметов: кирка (панель 0) ↔ камень.
            Assert.True(inv.Move(0, 20));
            Assert.Equal(Items.Stone, inv.ItemAt(0));
            Assert.Equal(Items.StonePickaxe, inv.ItemAt(20));
            Assert.Equal(Items.Stone, inv.Held); // выбранная ячейка 0 — теперь камень в руках

            // Доукладка того же предмета: 60 → в 70: стало 99, осталось 31.
            Assert.True(inv.Move(10, 11));
            Assert.Equal(99, inv.CountIn(11));
            Assert.Equal(31, inv.CountIn(10));

            // Деление: 10 из 31 в пустую; в чужой предмет и больше, чем есть, — отказ без изменений.
            Assert.True(inv.Split(10, 30, 10));
            Assert.Equal(21, inv.CountIn(10));
            Assert.Equal(10, inv.CountIn(30));
            Inventory before = inv;
            Assert.False(inv.Split(10, 0, 5));    // там камень
            Assert.False(inv.Split(10, 31, 22));  // столько нет
            Assert.False(inv.Split(10, 11, 1));   // стопка-цель полная
            Assert.False(inv.Split(10, 10, 1));
            Assert.False(inv.Move(33, 34));       // пустая ячейка
            Assert.False(inv.Move(10, 40));       // вне сумки
            Assert.Equal(before, inv);

            // Снятие из нескольких стопок (стоимость постройки): 130 дерева есть в трёх ячейках.
            Assert.Equal(130, inv.Count(Items.Wood));
            Assert.False(inv.Remove(Items.Wood, 131));
            Assert.Equal(before, inv);
            Assert.True(inv.Remove(Items.Wood, 105)); // с конца: 10 из ячейки 30, 95 из ячейки 11
            Assert.Equal(Items.None, inv.ItemAt(30));
            Assert.Equal(4, inv.CountIn(11));
            Assert.Equal(21, inv.CountIn(10));

            // Два материала: не хватает второго — не списан и первый.
            Assert.False(inv.Spend(Material.Wood, 2, Material.Resin, 1));
            Assert.Equal(25, inv.CountAt(Material.Wood));
            Assert.True(inv.Spend(Material.Wood, 25, Material.Stone, 5));
            Assert.Equal(0, inv.Count(Items.Wood));
            Assert.Equal(Items.None, inv.ItemAt(0));
        }

        /// <summary>Запись сумки: 121 байт, круг сходится; мусор (неизвестный предмет, стопка больше размера) — отказ.</summary>
        [Fact]
        public void E21_Bag_Payload_RoundTrip_RejectsGarbage()
        {
            var inv = Inventory.StartingKit();
            inv.Add(Items.Wood, 150);
            inv.Select(4);
            var buf = new byte[Inventory.PayloadBytes];
            Assert.Equal(121, buf.Length);
            inv.WritePayload(buf);
            Assert.Equal(inv, Inventory.ReadPayload(buf));

            byte[] bad = (byte[])buf.Clone();
            bad[1 + 3 * 20] = 200; // предмет без количества
            Assert.Throws<ArgumentException>(() => Inventory.ReadPayload(bad));
            bad = (byte[])buf.Clone();
            bad[2] = 2; // две кирки в ячейке 0
            Assert.Throws<ArgumentException>(() => Inventory.ReadPayload(bad));
            bad = (byte[])buf.Clone();
            bad[0] = 8; // выбранная ячейка вне панели
            Assert.Throws<ArgumentException>(() => Inventory.ReadPayload(bad));
            Assert.Throws<ArgumentException>(() => Inventory.ReadPayload(new byte[50]));
        }

        /// <summary>
        /// Сумка-счётчики (23 байта, сохранения формата 4…6) → ячейки: панель на месте, факелы — стопкой в своей ячейке,
        /// материалы — стопками в сумке; ячейка факела со счётчиком 0 — пустая.
        /// </summary>
        [Fact]
        public void E21_Bag_LegacyCounters_ToCells()
        {
            // выбранная 2; панель: кирка, топор, молот, меч, лук, факел, —, —; счётчики u16 LE: дерево 150, камень 40,
            // смола 0, волокно 3, медь 7, стрелы 1200, факелы 12.
            byte[] old =
            {
                2, 1, 2, 3, 4, 5, 6, 0, 0,
                150, 0, 40, 0, 0, 0, 3, 0, 7, 0, 0xB0, 0x04, 12, 0,
            };
            Assert.Equal(Inventory.LegacyPayloadBytes, old.Length);
            Inventory inv = Inventory.ReadPayload(old);
            Assert.Equal(2, inv.Selected);
            Assert.Equal(Items.Hammer, inv.Held);
            Assert.Equal(Items.Torch, inv.ItemAt(5));
            Assert.Equal(12, inv.CountIn(5));
            Assert.Equal(150, inv.CountAt(Material.Wood));
            Assert.Equal(99, inv.CountIn(8));
            Assert.Equal(51, inv.CountIn(9));
            Assert.Equal(40, inv.CountAt(Material.Stone));
            Assert.Equal(3, inv.CountAt(Material.Fiber));
            Assert.Equal(7, inv.CountAt(Material.Copper));
            Assert.Equal(1200, inv.CountAt(Material.Arrows));
            Assert.Equal(0, inv.CountAt(Material.Resin));
            Assert.Equal(6 + 2 + 1 + 1 + 1 + 2, inv.UsedCells);

            old[22 - 1] = 0; // факелов 0 → ячейка факела пустая
            Assert.Equal(Items.None, Inventory.ReadPayload(old).ItemAt(5));
        }

        // ---------- Кучи на земле ----------

        /// <summary>
        /// Условие шага 2.1: срубил дерево → на тайле дерева лежит куча древесины (в сумке пока пусто) → подошёл →
        /// древесина в ячейке сумки, кучи нет; событие подбора в такте.
        /// </summary>
        [Fact]
        public void E21_Chop_Tree_PileOnGround_WalkUp_PickedIntoCell()
        {
            WorldModel world = Flat();
            var tree = new TileCoord(304, 200);
            world.SetBaseTile(tree, FloorId.Grass, ObjectId.Tree);
            Simulation sim = SimWithPlayer(out int id, 300, 200, world);

            Assert.True(sim.RequestPlayerEdit(tree, FloorId.Grass, ObjectId.Empty, false, default, id, 1));
            sim.Step();
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(tree));
            GroundPile pile = Assert.Single(sim.Piles.ToArray());
            Assert.Equal(Items.Wood, pile.Item);
            Assert.Equal(3, pile.Count);
            Assert.Equal(tree, Torus.TileOf(sim.Shape, pile.Position)); // на тайле объекта, с разбросом внутри тайла
            Assert.InRange(Math.Abs(pile.Position.X - Center(304, 200).X), 0, Simulation.PileScatterUnits);
            Assert.InRange(Math.Abs(pile.Position.Y - Center(304, 200).Y), 0, Simulation.PileScatterUnits);
            Assert.Equal(0, sim.GetEntity(id).Inv.Count(Items.Wood)); // в сумку само не падает

            // Стоим в 4 тайлах — куча лежит.
            for (int t = 0; t < 60; t++)
            {
                sim.Step();
            }

            Assert.Single(sim.Piles.ToArray());

            // Идём к куче: подбор с 1,5 тайла.
            bool picked = false;
            for (int t = 0; t < 90 && sim.Piles.Length > 0; t++)
            {
                sim.SetInput(id, new InputFrame(Input8.East, 0));
                sim.Step();
                foreach (SimEvent e in sim.Events)
                {
                    if (e.Kind == SimEventKind.PilePickup)
                    {
                        picked = true;
                        Assert.Equal(id, e.Requester);
                        Assert.Equal(pile.Id, e.Tag);
                        Assert.Equal(Items.Wood, e.Victim);
                        Assert.Equal(3, e.Damage);
                    }
                }
            }

            Assert.True(picked);
            Assert.Equal(0, sim.Piles.Length);
            Inventory inv = sim.GetEntity(id).Inv;
            Assert.Equal(Items.Wood, inv.ItemAt(9)); // первая свободная ячейка сумки (в 8 — стрелы стартового набора)
            Assert.Equal(3, inv.CountIn(9));
            long d2 = Torus.Dist2(sim.Shape, sim.GetEntity(id).Mover.Position, pile.Position);
            Assert.InRange(d2, 1, (long)Simulation.PickupRadiusUnits * Simulation.PickupRadiusUnits); // подобрал на подходе, не вплотную
        }

        /// <summary>Свежая добыча не подбирается сразу (задержка 20 тактов), даже если персонаж стоит рядом.</summary>
        [Fact]
        public void E21_DigLoot_PickupDelay_ThenAuto()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            world.SetBaseTile(rock, FloorId.Grass, ObjectId.Rock);
            Simulation sim = SimWithPlayer(out int id, 300, 200, world);
            sim.RequestPlayerEdit(rock, FloorId.Grass, ObjectId.Empty, false, default, id, 1);
            sim.Step();
            int n = Assert.Single(sim.Piles.ToArray()).Count;
            Assert.InRange(n, 1, 2);
            for (int t = 1; t < Simulation.LootPickupDelayTicks; t++)
            {
                sim.Step();
                Assert.Equal(1, sim.Piles.Length);
            }

            sim.Step();
            Assert.Equal(0, sim.Piles.Length);
            Assert.Equal(n, sim.GetEntity(id).Inv.Count(Items.Stone));
        }

        /// <summary>Подбор доукладывает неполную стопку, остаток идёт в новую ячейку.</summary>
        [Fact]
        public void E21_Pickup_TopsUpStack()
        {
            Simulation sim = SimWithPlayer(out int id);
            SetBag(sim, id, b => b.Inv.SetCell(15, Items.Wood, 97));
            sim.AddPile(Items.Wood, 5, Center(300, 201));
            sim.Step();
            Inventory inv = sim.GetEntity(id).Inv;
            Assert.Equal(99, inv.CountIn(15));
            Assert.Equal(Items.Wood, inv.ItemAt(9));
            Assert.Equal(3, inv.CountIn(9));
            Assert.Equal(0, sim.Piles.Length);
        }

        /// <summary>
        /// Полная сумка — куча остаётся (<c>06</c> 5.7 п. 3); место под часть — подбирается часть, остаток лежит;
        /// освободил ячейку — поднимается.
        /// </summary>
        [Fact]
        public void E21_FullBag_PileStays_PartialPickup_ThenFreeCell()
        {
            Simulation sim = SimWithPlayer(out int id);
            SetBag(sim, id, b =>
            {
                for (int i = 0; i < Inventory.Cells; i++)
                {
                    b.Inv.SetCell(i, Items.Stone, 99);
                }
            });
            int wood = sim.AddPile(Items.Wood, 3, Center(300, 200));
            for (int t = 0; t < 100; t++)
            {
                sim.Step();
            }

            Assert.Equal(3, Assert.Single(sim.Piles.ToArray()).Count); // дерево лежит под ногами
            Assert.Equal(0, sim.GetEntity(id).Inv.Count(Items.Wood));

            // Камня влезает только 2 (одна стопка 97) — из кучи в 5 поднимается 2, остаётся 3.
            SetBag(sim, id, b => b.Inv.SetCell(20, Items.Stone, 97));
            sim.AddPile(Items.Stone, 5, Center(301, 200));
            sim.Step();
            Assert.Equal(2, sim.Piles.Length);
            Assert.Equal(3, sim.Piles[1].Count);
            Assert.Equal(Items.Stone, sim.Piles[1].Item);
            Assert.Equal(40 * 99, sim.GetEntity(id).Inv.Count(Items.Stone));
            sim.Step();
            Assert.Equal(2, sim.Piles.Length);

            // Освободил ячейку — дерево (куча старше, номер меньше) поднимается первой.
            SetBag(sim, id, b => b.Inv.SetCell(30, Items.None, 0));
            sim.Step();
            GroundPile left = Assert.Single(sim.Piles.ToArray());
            Assert.Equal(Items.Stone, left.Item);
            Assert.NotEqual(wood, left.Id);
            Assert.Equal(3, sim.GetEntity(id).Inv.CountIn(30));
            Assert.Equal(Items.Wood, sim.GetEntity(id).Inv.ItemAt(30));
        }

        /// <summary>Одинаковые кучи рядом сливаются; другой предмет и дальняя куча — отдельно.</summary>
        [Fact]
        public void E21_Piles_Merge_SameItemNearby()
        {
            var sim = new Simulation(Flat(), maxEntities: 4);
            int a = sim.AddPile(Items.Wood, 3, Center(10, 10));
            int b = sim.AddPile(Items.Wood, 2, new WorldPos(Center(10, 10).X + 200, Center(10, 10).Y - 100));
            Assert.Equal(a, b);
            Assert.Equal(5, Assert.Single(sim.Piles.ToArray()).Count);

            int c = sim.AddPile(Items.Stone, 4, Center(10, 10));
            int d = sim.AddPile(Items.Wood, 1, Center(13, 10));
            Assert.NotEqual(a, c);
            Assert.NotEqual(a, d);
            Assert.Equal(3, sim.Piles.Length);
            Assert.True(sim.Piles[0].Id < sim.Piles[1].Id && sim.Piles[1].Id < sim.Piles[2].Id); // по номеру

            // Потолок кучи: сверх него рядом ложится вторая.
            sim.AddPile(Items.Stone, Simulation.PileMaxCount, Center(10, 10));
            Assert.Equal(4, sim.Piles.Length);
            Assert.Equal(Simulation.PileMaxCount, sim.Piles[1].Count);
            Assert.Equal(4, sim.Piles[3].Count);
            Assert.Equal(0, sim.AddPile(Items.None, 5, Center(10, 10)));
            Assert.Equal(0, sim.AddPile(Items.Wood, 0, Center(10, 10)));
        }

        /// <summary>Куча не лежит в воде, на обрыве и в стене: сдвигается в центр соседнего проходимого тайла.</summary>
        [Fact]
        public void E21_Piles_NotInWaterOrCliff_ShiftToPassableTile()
        {
            WorldModel world = Flat();
            world.SetBaseTile(new TileCoord(50, 50), FloorId.Water, ObjectId.Empty);
            world.SetBaseTile(new TileCoord(51, 50), FloorId.Grass, ObjectId.Cliff); // восток занят обрывом
            var sim = new Simulation(world, maxEntities: 4);
            Assert.True(sim.IsBlocked(sim.Shape, new TileCoord(50, 50)));
            sim.AddPile(Items.Wood, 1, Center(50, 50));
            GroundPile p = Assert.Single(sim.Piles.ToArray());
            Assert.Equal(Center(50, 51), p.Position); // следующий по порядку сосед (юг) — проходим

            sim.AddPile(Items.Stone, 1, Center(51, 50)); // на обрыве — сдвиг на восток
            Assert.Equal(Center(52, 50), sim.Piles[1].Position);
        }

        /// <summary>Время жизни кучи — 10 минут (18 000 тактов): лежит до последнего такта, потом исчезает.</summary>
        [Fact]
        public void E21_Piles_Lifetime_10Minutes()
        {
            Assert.Equal(18_000, Simulation.PileLifetimeTicks);
            var sim = new Simulation(Flat(256), maxEntities: 4);
            sim.AddPile(Items.Wood, 3, Center(10, 10));
            for (int t = 0; t < Simulation.PileLifetimeTicks - 1; t++)
            {
                sim.Step();
            }

            Assert.Equal(1, sim.Piles.Length);
            sim.Step();
            Assert.Equal(0, sim.Piles.Length);
        }

        /// <summary>Кучи не мешают движению: персонаж проходит сквозь кучу (сумка полна — не подбирает) без задержки.</summary>
        [Fact]
        public void E21_Piles_DoNotBlockMovement()
        {
            Simulation free = SimWithPlayer(out int a);
            Simulation withPile = SimWithPlayer(out int b);
            SetBag(withPile, b, x =>
            {
                for (int i = 0; i < Inventory.Cells; i++)
                {
                    x.Inv.SetCell(i, Items.Stone, 99);
                }
            });
            withPile.AddPile(Items.Wood, 50, Center(302, 200));
            for (int t = 0; t < 60; t++)
            {
                free.SetInput(a, new InputFrame(Input8.East, 0));
                withPile.SetInput(b, new InputFrame(Input8.East, 0));
                free.Step();
                withPile.Step();
            }

            Assert.Equal(free.GetEntity(a).Mover.Position, withPile.GetEntity(b).Mover.Position);
            Assert.Equal(1, withPile.Piles.Length);
        }

        // ---------- Заявки игрока: перемещение, деление, выброс ----------

        /// <summary>
        /// Заявки исполняются тактом по порядку прихода: перемещение, деление, выброс. Выброшенное лежит у ног и
        /// 3 секунды не подбирается; неверная заявка сумку не меняет.
        /// </summary>
        [Fact]
        public void E21_BagOps_Move_Split_Drop_ToGround()
        {
            Simulation sim = SimWithPlayer(out int id);
            SetBag(sim, id, b => b.Inv.SetCell(10, Items.Wood, 40));

            Assert.True(sim.RequestBagOp(id, BagOp.Move, 10, 6, 0));   // дерево на панель
            Assert.True(sim.RequestBagOp(id, BagOp.Split, 6, 11, 15)); // 15 обратно в сумку
            Assert.True(sim.RequestBagOp(id, BagOp.Split, 6, 0, 1));   // в кирку — отказ
            Assert.True(sim.RequestBagOp(id, BagOp.Move, 39, 38, 0));  // пустая — отказ
            Assert.Equal(40, sim.GetEntity(id).Inv.CountIn(10));       // до такта ничего не меняется
            sim.Step();
            Inventory inv = sim.GetEntity(id).Inv;
            Assert.Equal(Items.None, inv.ItemAt(10));
            Assert.Equal(25, inv.CountIn(6));
            Assert.Equal(15, inv.CountIn(11));
            Assert.Equal(Items.StonePickaxe, inv.ItemAt(0));
            Assert.Equal(0, sim.Piles.Length);

            // Выброс 10 из 25: куча у ног, в ячейке 15.
            sim.RequestBagOp(id, BagOp.Drop, 6, 6, 10);
            sim.Step();
            GroundPile p = Assert.Single(sim.Piles.ToArray());
            Assert.Equal(Items.Wood, p.Item);
            Assert.Equal(10, p.Count);
            Assert.Equal(sim.GetEntity(id).Mover.Position, p.Position);
            Assert.Equal(15, sim.GetEntity(id).Inv.CountIn(6));
            Assert.Equal(30, sim.GetEntity(id).Inv.Count(Items.Wood));

            // 3 с не подбирается, хотя лежит под ногами; потом поднимается обратно.
            for (int t = 1; t < Simulation.DropPickupDelayTicks; t++)
            {
                sim.Step();
            }

            Assert.Equal(1, sim.Piles.Length);
            sim.Step();
            Assert.Equal(0, sim.Piles.Length);
            Assert.Equal(40, sim.GetEntity(id).Inv.Count(Items.Wood));

            // Выброс всей стопки (количество 0) — инструмент тоже можно выбросить.
            sim.RequestBagOp(id, BagOp.Drop, 1, 1, 0);
            sim.Step();
            Assert.Equal(Items.None, sim.GetEntity(id).Inv.ItemAt(1));
            Assert.Equal(Items.StoneAxe, Assert.Single(sim.Piles.ToArray()).Item);

            // Чужая сущность и мусорные ячейки — без изменений и без исключений.
            Inventory was = sim.GetEntity(id).Inv;
            sim.RequestBagOp(999, BagOp.Drop, 0, 0, 0);
            sim.RequestBagOp(id, BagOp.Move, 200, 0, 0);
            sim.RequestBagOp(id, BagOp.Split, 0, 200, 1);
            sim.RequestBagOp(id, (BagOp)77, 0, 1, 1);
            sim.Step();
            Assert.Equal(was, sim.GetEntity(id).Inv);
        }

        // ---------- Стройка от ячеек ----------

        /// <summary>
        /// Стоимость постройки снимается из нескольких стопок; не хватает — «нет ресурсов», сумка цела. Снос возвращает
        /// материалы кучей на тайл постройки (а не в сумку); подошёл — подобрал.
        /// </summary>
        [Fact]
        public void E21_Build_CostFromSeveralStacks_RefundDropsOnGround()
        {
            Simulation sim = SimWithPlayer(out int id, 300, 200);
            SetBag(sim, id, b =>
            {
                b.Inv.SetCell(12, Items.Stone, 1);
                b.Inv.SetCell(25, Items.Stone, 1);
                b.Inv.SetCell(26, Items.Wood, 5);
            });
            var wall = new TileCoord(304, 200);
            var door = new TileCoord(304, 202);
            Assert.True(BuildDefs.TryGet(ObjectId.StoneWall, out BuildDef wallDef));
            var whole = new BuildRecord(1, wallDef.MaxIntegrity, 0, FloorId.Grass, 0); // целая стена: возврат — рецепт целиком
            sim.RequestPlayerEdit(wall, FloorId.Grass, ObjectId.StoneWall, true, whole, id, 1);
            sim.RequestPlayerEdit(door, FloorId.Grass, ObjectId.Door, true, BuildRecord.OfPlayer(1, FloorId.Grass), id, 2); // 6 дерева, есть 5
            sim.Step();
            Assert.Equal(ObjectId.StoneWall, sim.World.GetObject(wall));
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(door));
            Inventory inv = sim.GetEntity(id).Inv;
            Assert.Equal(0, inv.Count(Items.Stone));
            Assert.Equal(Items.None, inv.ItemAt(12));
            Assert.Equal(Items.None, inv.ItemAt(25));
            Assert.Equal(5, inv.Count(Items.Wood));
            bool noResources = false;
            foreach (SimEvent e in sim.Events)
            {
                noResources |= e.Kind == SimEventKind.TileEdit && e.Tag == 2 && e.Result == EditResult.NoResources;
            }

            Assert.True(noResources);

            // Снос: 2 камня — кучей на тайле стены.
            sim.RequestPlayerEdit(wall, FloorId.Grass, ObjectId.Empty, false, default, id, 3);
            sim.Step();
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(wall));
            GroundPile p = Assert.Single(sim.Piles.ToArray());
            Assert.Equal(Items.Stone, p.Item);
            Assert.Equal(2, p.Count);
            Assert.Equal(wall, Torus.TileOf(sim.Shape, p.Position));
            Assert.Equal(0, sim.GetEntity(id).Inv.Count(Items.Stone));

            for (int t = 0; t < 90 && sim.Piles.Length > 0; t++)
            {
                sim.SetInput(id, new InputFrame(Input8.East, 0));
                sim.Step();
            }

            Assert.Equal(2, sim.GetEntity(id).Inv.Count(Items.Stone));
        }

        // ---------- Детерминизм и хэш ----------

        /// <summary>Два одинаковых прогона дают один хэш; куча входит в хэш состояния (другое количество — другой хэш).</summary>
        [Fact]
        public void E21_StateHash_CoversPiles_Deterministic()
        {
            static Simulation Run(int extra)
            {
                WorldModel world = Flat();
                for (int i = 0; i < 6; i++)
                {
                    world.SetBaseTile(new TileCoord(302 + i, 200), FloorId.Grass, i % 2 == 0 ? ObjectId.Tree : ObjectId.Rock);
                }

                Simulation sim = SimWithPlayer(out int id, 300, 200, world);
                for (int i = 0; i < 6; i++)
                {
                    sim.RequestPlayerEdit(new TileCoord(302 + i, 200), FloorId.Grass, ObjectId.Empty, false, default, id, i + 1);
                    sim.Step();
                }

                for (int t = 0; t < 24; t++)
                {
                    sim.SetInput(id, new InputFrame(Input8.East, 0));
                    sim.Step();
                }

                if (extra > 0)
                {
                    sim.AddPile(Items.Fiber, extra, Center(350, 200));
                }

                return sim;
            }

            Simulation a = Run(0), b = Run(0), c = Run(1), d = Run(2);
            Assert.True(a.GetEntity(1).Inv.Count(Items.Wood) > 0);
            Assert.True(a.Piles.Length > 0); // дальние ещё лежат
            Assert.Equal(StateHash.Compute(a).Total, StateHash.Compute(b).Total);
            Assert.NotEqual(StateHash.Compute(a).Total, StateHash.Compute(c).Total);
            Assert.NotEqual(StateHash.Compute(c).Total, StateHash.Compute(d).Total);
        }

        // ---------- Сохранение ----------

        /// <summary>Формат 7: ячейки сумки и кучи на земле сохраняются и читаются; хэш состояния после загрузки тот же.</summary>
        [Fact]
        public void E21_Save_Format7_Bag_And_Piles_RoundTrip()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(256, 21, bots: 2);
            Simulation sim = live.Sim;
            int id = sim.Entities[0].Id;
            Inventory bag = Inventory.StartingKit();
            bag.Add(Items.Wood, 150);
            bag.SetCell(39, Items.CopperOre, 7);
            bag.Select(1);
            sim.SetInventory(id, bag);
            live.Run(ticks: 30, editsPerTick: 1);
            WorldPos far = Torus.Offset(sim.Shape, sim.Entities[0].Mover.Position, new Int2(40 * U, 0));
            int p1 = sim.AddPile(Items.Stone, 12, far);
            int p2 = sim.AddPile(Items.StoneAxe, 1, Torus.Offset(sim.Shape, far, new Int2(5 * U, 0)), Simulation.DropPickupDelayTicks);
            sim.Step();
            Assert.Equal(2, sim.Piles.Length);
            bag = sim.Entities[0].Inv;

            var chars = new[] { new CharacterRecord { IsHost = true, Name = "Орфей", EntityId = id, Inv = bag } };
            byte[] save = WorldSave.Build(WorldSave.Capture(sim, live.BaseSha, "кучи", chars), 3, 0);
            SaveFile f = SaveContainer.Read(save);
            Assert.True(f.IsOk, f.Error);
            Assert.Equal(7u, f.Header.FormatVersion);
            Assert.Equal(7u, SaveModule.FormatVersion);

            LoadedWorldState st = WorldSave.Load(f, WorldSnapshot.LoadBase(live.BaseBytes), live.BaseSha);
            Assert.Equal(bag, st.Characters[0].Inv);
            Assert.Equal(bag, st.Entities[0].Inv);
            Assert.Equal(2, st.Piles.Length);
            Assert.Equal(sim.NextPileId, st.NextPileId);
            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(sim.Piles[i].Id, st.Piles[i].Id);
                Assert.Equal(sim.Piles[i].Item, st.Piles[i].Item);
                Assert.Equal(sim.Piles[i].Count, st.Piles[i].Count);
                Assert.Equal(sim.Piles[i].Position, st.Piles[i].Position);
                Assert.Equal(sim.Piles[i].ExpireTick, st.Piles[i].ExpireTick);
                Assert.Equal(sim.Piles[i].PickupTick, st.Piles[i].PickupTick);
            }

            Assert.Equal(p1, st.Piles[0].Id);
            Assert.Equal(p2, st.Piles[1].Id);

            Simulation loaded = SaveFixtures.Load(live.BaseBytes, save);
            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total);

            // Загруженный мир живёт дальше так же: одинаковые такты — одинаковый хэш; новая куча получает следующий номер.
            for (int t = 0; t < 30; t++)
            {
                sim.Step();
                loaded.Step();
            }

            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total);
            Assert.Equal(sim.AddPile(Items.Fiber, 1, far), loaded.AddPile(Items.Fiber, 1, far));
        }

        /// <summary>
        /// Настоящий файл формата 6 (записан сборкой <c>b2cc0b9</c> = main до E2-1: мир-заглушка 256², зерно 5, 30 тактов;
        /// хозяин «Орфей» в мире — стартовый набор + дерево 150, камень 40, медь 7, факелов 12, в руках топор; «Эвридика»
        /// вне мира — молот, пустая ячейка факела, волокно 3, стрелы 1200) читается: счётчики разложены по ячейкам,
        /// куч нет. Пересохранение даёт формат 7 с теми же сумками.
        /// </summary>
        [Fact]
        public void E21_Save_Format6_RealFile_CountersBecomeCells()
        {
            byte[] v6 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format6_bag.save"));
            byte[] baseBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format6_bag.base"));
            SaveFile f = SaveContainer.Read(v6);
            Assert.True(f.IsOk, f.Error);
            Assert.Equal(6u, f.Header.FormatVersion);

            LoadedWorldState st = WorldSave.Load(f, WorldSnapshot.LoadBase(baseBytes), WorldSnapshot.StoredHash(baseBytes));
            Assert.Equal(6u, st.FormatVersion);
            Assert.Equal(30, st.Tick);
            Assert.Empty(st.Piles);
            Assert.Equal(1, st.NextPileId);
            Assert.Equal(2, st.Characters.Length);

            Inventory orpheus = st.Characters[0].Inv;
            Assert.Equal("Орфей", st.Characters[0].Name);
            Assert.Equal(1, orpheus.Selected);
            Assert.Equal(Items.StoneAxe, orpheus.Held);
            Assert.Equal(Items.StonePickaxe, orpheus.ItemAt(0));
            Assert.Equal(Items.Hammer, orpheus.ItemAt(2));
            Assert.Equal(Items.WoodenSword, orpheus.ItemAt(3));
            Assert.Equal(Items.SimpleBow, orpheus.ItemAt(4));
            Assert.Equal(Items.Torch, orpheus.ItemAt(5));
            Assert.Equal(12, orpheus.CountIn(5));
            Assert.Equal(150, orpheus.Count(Items.Wood));
            Assert.Equal(99, orpheus.CountIn(8));
            Assert.Equal(51, orpheus.CountIn(9));
            Assert.Equal(40, orpheus.Count(Items.Stone));
            Assert.Equal(7, orpheus.Count(Items.CopperOre));
            Assert.Equal(50, orpheus.Count(Items.Arrows));
            Assert.Equal(11, orpheus.UsedCells);
            Assert.Equal(orpheus, st.Entities[0].Inv); // сумка сущности в мире — та же

            Inventory eurydice = st.Characters[1].Inv;
            Assert.Equal(Items.Hammer, eurydice.ItemAt(0));
            Assert.Equal(Items.None, eurydice.ItemAt(5)); // ячейка факела при счётчике 0 — пустая
            Assert.Equal(Items.None, eurydice.Held);
            Assert.Equal(3, eurydice.Count(Items.Fiber));
            Assert.Equal(1200, eurydice.Count(Items.Arrows));
            Assert.Equal(4, eurydice.UsedCells);

            // Пересохранение: формат 7, сумки те же.
            WorldModel world = WorldSnapshot.LoadBase(baseBytes);
            LoadedWorldState again = WorldSave.Load(SaveContainer.Read(v6), world, WorldSnapshot.StoredHash(baseBytes));
            var sim = new Simulation(world);
            again.RestoreInto(sim);
            byte[] v7 = WorldSave.Build(WorldSave.Capture(sim, WorldSnapshot.StoredHash(baseBytes), again.Name, again.Characters), 5, 0);
            Assert.Equal(7u, SaveContainer.Read(v7).Header.FormatVersion);
            LoadedWorldState st7 = WorldSave.Load(SaveContainer.Read(v7), WorldSnapshot.LoadBase(baseBytes), WorldSnapshot.StoredHash(baseBytes));
            Assert.Equal(orpheus, st7.Characters[0].Inv);
            Assert.Equal(eurydice, st7.Characters[1].Inv);
            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(SaveFixtures.Load(baseBytes, v7)).Total);
        }

        // ---------- Сеть ----------

        /// <summary>«Золотые» байты протокола 21: кучи (27), «кучи нет» (28), заявка на сумку (29); круг сходится, мусор — отказ.</summary>
        [Fact]
        public void E21_Net_GoldenBytes_Piles_PileGone_BagRequest()
        {
            Assert.Equal(21, NetModule.ProtocolVersion);

            var buf = new byte[PilesMessage.MaxBytes];
            var piles = new[]
            {
                new NetPile { Id = 1, Item = Items.Wood, Count = 3, Position = new WorldPos(0x12345, 0x6789A) },
                new NetPile { Id = 0x01020304, Item = Items.Arrows, Count = 999, Position = new WorldPos(256, 512) },
            };
            int n = PilesMessage.Write(buf, piles);
            Assert.Equal(2 + 2 * NetPile.Bytes, n);
            Assert.Equal(
                "1B-02-01-00-00-00-08-03-00-45-23-01-00-9A-78-06-00-04-03-02-01-0D-E7-03-00-01-00-00-00-02-00-00",
                BitConverter.ToString(buf, 0, n));
            var read = new NetPile[PilesMessage.MaxPiles];
            Assert.Equal(2, PilesMessage.Read(new ReadOnlySpan<byte>(buf, 0, n), read));
            Assert.Equal(piles[1].Id, read[1].Id);
            Assert.Equal(piles[1].Count, read[1].Count);
            Assert.Equal(piles[0].Position, read[0].Position);
            buf[6] = 200; // нет такого предмета
            Assert.Throws<ProtocolException>(() => PilesMessage.Read(new ReadOnlySpan<byte>(buf, 0, n), read));

            n = new PileGoneMessage { Id = 7, Reason = PileGoneReason.Picked, Taker = 0x0A0B }.Write(buf);
            Assert.Equal("1C-07-00-00-00-00-0B-0A-00-00", BitConverter.ToString(buf, 0, n));
            PileGoneMessage gone = PileGoneMessage.Read(new ReadOnlySpan<byte>(buf, 0, n));
            Assert.Equal(7, gone.Id);
            Assert.Equal(0x0A0B, gone.Taker);
            buf[5] = 9;
            Assert.Throws<ProtocolException>(() => PileGoneMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));

            n = new BagRequestMessage { Op = BagOp.Split, From = 6, To = 11, Count = 15 }.Write(buf);
            Assert.Equal("1D-02-06-0B-0F-00", BitConverter.ToString(buf, 0, n));
            BagRequestMessage req = BagRequestMessage.Read(new ReadOnlySpan<byte>(buf, 0, n));
            Assert.Equal(BagOp.Split, req.Op);
            Assert.Equal(15, req.Count);
            buf[2] = 40; // ячейка вне сумки
            Assert.Throws<ProtocolException>(() => BagRequestMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));
            buf[2] = 6;
            buf[1] = 0; // нет такой операции
            Assert.Throws<ProtocolException>(() => BagRequestMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));

            // Сумка (26) ячейками: 7 + 121 байт, начало — тип, сущность, версия, выбранная ячейка, ячейка 0.
            var bag = Inventory.StartingKit();
            n = new InventoryMessage { EntityId = 5, Version = 2, Bag = bag }.Write(buf);
            Assert.Equal(InventoryMessage.Bytes, n);
            Assert.Equal(128, n);
            Assert.Equal("1A-05-00-00-00-02-00-00-01-01-00-02-01-00", BitConverter.ToString(buf, 0, 14));
            Assert.Equal(bag, InventoryMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)).Bag);
        }

        /// <summary>
        /// Два клиента видят одну кучу; один подходит и подбирает — у него она в ячейке сумки (сумка сервера, тип 26),
        /// у второго куча исчезает с пометкой «подобрал первый». Подбор не даёт правок предсказанию движения.
        /// </summary>
        [Fact]
        public void E21_Net_TwoClients_SeeOnePile_OnePicks_OtherSeesItGone()
        {
            using var rig = new EditTests.EditRig(Flat(), i => i == 0 ? Center(300, 200) : Center(300, 206));
            NetClient a = rig.Join();
            NetClient b = rig.Join();
            rig.Run(10);
            Assert.Empty(a.Piles);

            int pileId = rig.Sim.AddPile(Items.Wood, 3, Center(304, 200));
            rig.Run(3);
            NetPile seenA = Assert.Single(a.Piles);
            NetPile seenB = Assert.Single(b.Piles);
            Assert.Equal(pileId, seenA.Id);
            Assert.Equal(Items.Wood, seenB.Item);
            Assert.Equal(3, seenB.Count);
            Assert.Equal(Center(304, 200), seenB.Position);

            var goneAtB = new List<PileGoneMessage>();
            b.PileGone = (m, was) => goneAtB.Add(m);
            rig.InputOf = c => c == a ? new InputFrame(Input8.East, 0) : InputFrame.None;
            for (int t = 0; t < 120 && a.Piles.Count > 0; t++)
            {
                rig.Run(1);
            }

            rig.InputOf = null;
            rig.Run(5);
            Assert.Empty(a.Piles);
            Assert.Empty(b.Piles);
            PileGoneMessage g = Assert.Single(goneAtB);
            Assert.Equal(pileId, g.Id);
            Assert.Equal(PileGoneReason.Picked, g.Reason);
            Assert.Equal(a.EntityId, g.Taker);
            Assert.Equal(3, a.Inv.Count(Items.Wood));
            Assert.Equal(Items.Wood, a.Inv.ItemAt(9));
            Assert.Equal(0, b.Inv.Count(Items.Wood));
            Assert.Equal(0, rig.Sim.Piles.Length);
            Assert.Equal(0, a.BadMessages + b.BadMessages);
        }

        /// <summary>
        /// Заявки на сумку по сети: клиент просит (деление, перемещение, выброс), сервер решает и присылает сумку;
        /// выброшенное видят оба клиента; вошедший позже получает кучи, которые уже лежат.
        /// </summary>
        [Fact]
        public void E21_Net_BagRequests_ServerDecides_DropSeenByAll_LateJoinerGetsPiles()
        {
            using var rig = new EditTests.EditRig(Flat(), i => Center(300 + 3 * i, 200));
            NetClient a = rig.Join();
            NetClient b = rig.Join();
            rig.Run(10);
            Inventory bag = rig.Sim.GetEntity(a.EntityId).Inv;
            bag.SetCell(10, Items.Stone, 40);
            rig.Sim.SetInventory(a.EntityId, bag);
            rig.Run(3);
            Assert.Equal(40, a.Inv.CountIn(10));

            Assert.True(a.RequestBagSplit(10, 11, 15));
            Assert.True(a.RequestBagMove(11, 7));
            Assert.Equal(40, a.Inv.CountIn(10)); // клиент не предсказывает — ждёт сервер
            rig.Run(4);
            Assert.Equal(25, a.Inv.CountIn(10));
            Assert.Equal(Items.Stone, a.Inv.ItemAt(7));
            Assert.Equal(15, a.Inv.CountIn(7));
            Assert.Equal(a.Inv, rig.Sim.GetEntity(a.EntityId).Inv);

            Assert.True(a.RequestBagDrop(7));
            rig.Run(4);
            Assert.Equal(Items.None, a.Inv.ItemAt(7));
            Assert.Equal(15, Assert.Single(a.Piles).Count);
            Assert.Equal(15, Assert.Single(b.Piles).Count);
            Assert.False(a.RequestBagMove(0, 40));
            Assert.Equal(3, rig.Server.BagRequestsReceived);

            NetClient late = rig.Join();
            rig.Run(10);
            Assert.Equal(Items.Stone, Assert.Single(late.Piles).Item);
            Assert.Equal(0, a.BadMessages + b.BadMessages + late.BadMessages);
        }
    }
}
