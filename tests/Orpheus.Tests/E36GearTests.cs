using System;
using System.IO;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Save;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E3-6 (шаг 3.6): Очаг — точка возрождения (центр мира, тайл, не объект); броня — медный комплект
    /// (шлем / нагрудник / поножи) с рецептами у верстака; экипировка — три ячейки 70…72 заявок сумки;
    /// Armor — сумма брони надетого (`02` 6.1), полный комплект одного SetId — масса 2 («Устойчивость», `06` 4.7).
    /// </summary>
    public sealed class E36GearTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat(ulong seed = 6060) => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), seed);

        /// <summary>Плоский мир с верстаком в 2 тайлах восточнее (300, 200) — как в CraftTests.</summary>
        private static WorldModel WithBench()
        {
            WorldModel world = Flat();
            world.SetBaseTile(new TileCoord(302, 200), FloorId.Grass, ObjectId.Workbench);
            return world;
        }

        private static WorldPos Center(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        /// <summary>Персонаж в (300, 200) с пустой сумкой, в которую положено перечисленное.</summary>
        private static Simulation Sim(out int id, WorldModel world, params (byte Item, int Count)[] bag)
        {
            var sim = new Simulation(world, maxEntities: 8);
            id = sim.AddEntity(Center(300, 200));
            var inv = new Inventory();
            foreach ((byte item, int count) in bag)
            {
                Assert.Equal(count, inv.Add(item, count));
            }

            Assert.True(sim.SetInventory(id, inv));
            return sim;
        }

        private static Simulation Sim(out int id, params (byte Item, int Count)[] bag) => Sim(out id, Flat(), bag);

        private static void Run(Simulation sim, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                sim.Step();
            }
        }

        /// <summary>Заявка на сумку и такт, который её исполняет.</summary>
        private static void DoOp(Simulation sim, int id, BagOp op, int from, int to, int count = 0)
        {
            Assert.True(sim.RequestBagOp(id, op, from, to, count));
            sim.Step();
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

        private static int CellOf(Simulation sim, int id, byte item)
        {
            Inventory inv = sim.GetEntity(id).Inv;
            for (int c = 0; c < Inventory.Cells; c++)
            {
                if (inv.ItemAt(c) == item && inv.CountIn(c) > 0)
                {
                    return c;
                }
            }

            Assert.Fail($"Предмет {item} не найден в сумке.");
            return -1;
        }

        // ---------- Рецепты (верстак, «К-1 / К-2 / К-3» из `05`) ----------

        /// <summary>
        /// Каждый рецепт брони изготавливается: ингредиенты ровно на один раз → через 15 тактов результат
        /// в сумке, ингредиентов нет; на 14-м такте ещё не готово.
        /// </summary>
        [Theory]
        [InlineData(RecipeTable.CopperHelmet, Items.CopperHelmet, 6, 1)]
        [InlineData(RecipeTable.CopperChest, Items.CopperChest, 10, 2)]
        [InlineData(RecipeTable.CopperLegs, Items.CopperLegs, 8, 1)]
        public void E36_Armor_Recipe_IsMade_In15Ticks(byte recipe, byte result, int ingots, int hides)
        {
            Simulation sim = Sim(out int id, WithBench(), (Items.CopperIngot, ingots), (Items.Hide, hides));
            Assert.Equal(CraftReply.Accepted, sim.RequestCraft(id, recipe, 1));
            Run(sim, 14);
            Assert.Equal(0, sim.GetEntity(id).Inv.Count(result));
            sim.Step();
            SimEntity e = sim.GetEntity(id);
            Assert.Equal(1, e.Inv.Count(result));
            Assert.Equal(0, e.Inv.Count(Items.CopperIngot));
            Assert.Equal(0, e.Inv.Count(Items.Hide));
            Assert.True(e.Craft.IsEmpty);
        }

        // ---------- Надеть / снять / обмен ----------

        /// <summary>
        /// Надеть из сумки: предмет уходит из ячейки, Armor = 1; снять (выброс) — кучей у ног, Armor = 0;
        /// обмен: в занятой ячейке надетый возвращается в сумку.
        /// </summary>
        [Fact]
        public void E36_Equip_Wear_Unequip_Swap()
        {
            Simulation sim = Sim(out int id, (Items.CopperHelmet, 1), (Items.CopperChest, 1));
            int headCell = CellOf(sim, id, Items.CopperHelmet);
            int chestCell = CellOf(sim, id, Items.CopperChest);

            DoOp(sim, id, BagOp.Move, headCell, Simulation.EquipBase + 0);
            SimEntity e = sim.GetEntity(id);
            Assert.Equal(Items.CopperHelmet, e.EquipHead);
            Assert.Equal(0, e.Inv.Count(Items.CopperHelmet));
            Assert.Equal(1, e.Armor);

            // Снять выбросом: кучей у ног, ничего не теряется.
            WorldPos feet = e.Mover.Position;
            DoOp(sim, id, BagOp.Drop, Simulation.EquipBase + 0, 0, 0);
            e = sim.GetEntity(id);
            Assert.Equal(Items.None, e.EquipHead);
            Assert.Equal(0, e.Armor);
            Assert.Equal(1, OnGround(sim, Items.CopperHelmet));
            foreach (GroundPile p in sim.Piles)
            {
                Assert.True(Torus.Dist2(sim.Shape, p.Position, feet) <= (long)U * U);
            }

            // Обмен в занятой ячейке: второй нагрудник в «тело» — надетый возвращается в сумку.
            // (Шлем после выброса лежит на земле — кладём в сумку второй нагрудник.)
            var bag2 = sim.GetEntity(id).Inv;
            Assert.True(bag2.SetCell(20, Items.CopperChest, 1));
            Assert.True(sim.SetInventory(id, bag2));
            DoOp(sim, id, BagOp.Move, chestCell, Simulation.EquipBase + 1);
            Assert.Equal(Items.CopperChest, sim.GetEntity(id).EquipBody);
            int free = CellOf(sim, id, Items.CopperChest);
            Assert.NotEqual(chestCell, free); // в сумке остался только второй нагрудник
            DoOp(sim, id, BagOp.Move, free, Simulation.EquipBase + 1);
            e = sim.GetEntity(id);
            Assert.Equal(Items.CopperChest, e.EquipBody);
            Assert.Equal(1, e.Inv.Count(Items.CopperChest)); // старый нагрудник вернулся в сумку
            Assert.Equal(1, e.Armor);
        }

        /// <summary>
        /// Отказы: в «голову» — только шлем; камень — не надевается; из ячейки снаряжения — только
        /// в свою (70 → 71 нельзя); обмен в занятой ячейке — надетый возвращается в сумку.
        /// </summary>
        [Fact]
        public void E36_Equip_Rejects_Wrong_Slot()
        {
            Simulation sim = Sim(out int id, (Items.Stone, 5), (Items.CopperHelmet, 1));

            int stoneCell = CellOf(sim, id, Items.Stone);
            DoOp(sim, id, BagOp.Move, stoneCell, Simulation.EquipBase + 0);
            Assert.Equal(Items.None, sim.GetEntity(id).EquipHead);
            Assert.Equal(5, sim.GetEntity(id).Inv.Count(Items.Stone));

            int helmetCell = CellOf(sim, id, Items.CopperHelmet);
            DoOp(sim, id, BagOp.Move, helmetCell, Simulation.EquipBase + 1); // шлем в «тело» — нельзя
            Assert.Equal(Items.None, sim.GetEntity(id).EquipBody);
            Assert.Equal(1, sim.GetEntity(id).Inv.Count(Items.CopperHelmet));

            DoOp(sim, id, BagOp.Move, helmetCell, Simulation.EquipBase + 0); // наденется
            Assert.Equal(Items.CopperHelmet, sim.GetEntity(id).EquipHead);
            DoOp(sim, id, BagOp.Move, Simulation.EquipBase + 0, Simulation.EquipBase + 1); // 70 → 71 — нельзя
            Assert.Equal(Items.CopperHelmet, sim.GetEntity(id).EquipHead);
            Assert.Equal(Items.None, sim.GetEntity(id).EquipBody);

            // Обмен в занятой ячейке: второй шлем в «голову» — надетый возвращается в сумку (Armor не растёт).
            var inv = sim.GetEntity(id).Inv;
            Assert.True(inv.SetCell(12, Items.CopperHelmet, 1));
            Assert.True(sim.SetInventory(id, inv));
            DoOp(sim, id, BagOp.Move, 12, Simulation.EquipBase + 0);
            SimEntity e = sim.GetEntity(id);
            Assert.Equal(Items.CopperHelmet, e.EquipHead);
            Assert.Equal(1, e.Inv.Count(Items.CopperHelmet)); // старый шлем вернулся в сумку
            Assert.Equal(1, e.Armor);
        }

        /// <summary>Надеть в полную сумку нельзя: надетому нет места обратно — отказ, предмет и надетое на местах.</summary>
        [Fact]
        public void E36_Equip_Full_Bag_Refuses()
        {
            Simulation sim = Sim(out int id, (Items.CopperHelmet, 1), (Items.CopperChest, 1));
            DoOp(sim, id, BagOp.Move, CellOf(sim, id, Items.CopperChest), Simulation.EquipBase + 1);
            Assert.Equal(Items.CopperChest, sim.GetEntity(id).EquipBody);

            var inv = sim.GetEntity(id).Inv;
            for (int c = 0; c < Inventory.Cells; c++)
            {
                if (inv.CountIn(c) == 0)
                {
                    Assert.True(inv.SetCell(c, Items.Torch, ItemTable.MaxStackOf(Items.Torch)));
                }
            }

            Assert.True(sim.SetInventory(id, inv));
            int helmetCell = CellOf(sim, id, Items.CopperHelmet);
            Assert.True(sim.RequestBagOp(id, BagOp.Move, helmetCell, Simulation.EquipBase + 1, 0));
            sim.Step();
            SimEntity e = sim.GetEntity(id);
            Assert.Equal(Items.CopperChest, e.EquipBody); // нагруднику нет места в сумке — обмен отклонён
            Assert.Equal(Items.CopperHelmet, e.Inv.ItemAt(helmetCell));
            Assert.Equal(1, e.Inv.CountIn(helmetCell));
            Assert.Equal(1, e.Armor);
        }

        // ---------- Armor и урон (`02` 6.1) ----------

        /// <summary>Armor — сумма брони надетого; формула урона получает именно её (5/бр3 → 2, 18/бр3 → 15).</summary>
        [Fact]
        public void E36_Armor_Is_Sum_Of_Worn_And_Feeds_DamageMath()
        {
            Assert.Equal(2, DamageMath.Apply(5, 3));
            Assert.Equal(15, DamageMath.Apply(18, 3));

            Simulation sim = Sim(out int id,
                (Items.CopperHelmet, 1), (Items.CopperChest, 1), (Items.CopperLegs, 1));
            SimEntity e = sim.GetEntity(id);
            Assert.Equal(0, e.Armor);
            Assert.Equal(1, e.Mass);
            int h = CellOf(sim, id, Items.CopperHelmet);
            int b = CellOf(sim, id, Items.CopperChest);
            int l = CellOf(sim, id, Items.CopperLegs);
            DoOp(sim, id, BagOp.Move, h, Simulation.EquipBase + 0);
            DoOp(sim, id, BagOp.Move, b, Simulation.EquipBase + 1);
            DoOp(sim, id, BagOp.Move, l, Simulation.EquipBase + 2);
            e = sim.GetEntity(id);
            Assert.Equal(3, e.Armor);

            // Удар сырой силой 10 по персонажу с броней 3 → 7 (`02` 6.10 п. 2).
            int before = e.Health;
            Assert.Equal(DamageMath.Apply(10, 3), sim.ApplyStrike(id, 0, 10));
            Assert.Equal(before - 7, sim.GetEntity(id).Health);
        }

        // ---------- Комплект («Устойчивость», `06` 4.7) ----------

        /// <summary>Полный комплект (все 3 части одного SetId) — масса 2; две части — масса 1.</summary>
        [Fact]
        public void E36_Full_Set_Gives_Mass_2()
        {
            Simulation sim = Sim(out int id,
                (Items.CopperHelmet, 1), (Items.CopperChest, 1), (Items.CopperLegs, 1));
            int h = CellOf(sim, id, Items.CopperHelmet);
            int b = CellOf(sim, id, Items.CopperChest);
            DoOp(sim, id, BagOp.Move, h, Simulation.EquipBase + 0);
            DoOp(sim, id, BagOp.Move, b, Simulation.EquipBase + 1);
            Assert.Equal(1, sim.GetEntity(id).Mass); // две части — без бонуса

            int l = CellOf(sim, id, Items.CopperLegs);
            DoOp(sim, id, BagOp.Move, l, Simulation.EquipBase + 2);
            SimEntity e = sim.GetEntity(id);
            Assert.Equal(2, e.Mass);
            Assert.Equal(3, e.Armor);

            // Снять поножи — бонус уходит.
            DoOp(sim, id, BagOp.Drop, Simulation.EquipBase + 2, 0, 0);
            e = sim.GetEntity(id);
            Assert.Equal(1, e.Mass);
            Assert.Equal(2, e.Armor);
        }

        // ---------- Сейв (формат 16) ----------

        /// <summary>Сейв сохраняет надетое; после загрузки в новую симуляцию Armor и Mass считаются заново по нему.</summary>
        [Fact]
        public void E36_Save_Keeps_Worn_Gear()
        {
            WorldModel world = Flat();
            Simulation sim = Sim(out int id, world,
                (Items.CopperHelmet, 1), (Items.CopperChest, 1), (Items.CopperLegs, 1));
            DoOp(sim, id, BagOp.Move, CellOf(sim, id, Items.CopperHelmet), Simulation.EquipBase + 0);
            DoOp(sim, id, BagOp.Move, CellOf(sim, id, Items.CopperChest), Simulation.EquipBase + 1);
            DoOp(sim, id, BagOp.Move, CellOf(sim, id, Items.CopperLegs), Simulation.EquipBase + 2);
            WorldPos pos = sim.GetEntity(id).Mover.Position;

            byte[] baseBytes = WorldSnapshot.WriteBase(world);
            byte[] sha = WorldSnapshot.StoredHash(baseBytes);
            var characters = new[]
            {
                new CharacterRecord { IsHost = true, Name = "Орфей", EntityId = id, Inv = sim.GetEntity(id).Inv },
            };
            byte[] save = WorldSave.Build(WorldSave.Capture(sim, sha, "экипировка", characters), 1, 0);

            LoadedWorldState st = WorldSave.Load(SaveContainer.Read(save), WorldSnapshot.LoadBase(baseBytes), sha);
            var back = new Simulation(WorldSnapshot.LoadBase(baseBytes), maxEntities: 8);
            st.RestoreInto(back);
            SimEntity e = back.GetEntity(id);
            Assert.Equal(Items.CopperHelmet, e.EquipHead);
            Assert.Equal(Items.CopperChest, e.EquipBody);
            Assert.Equal(Items.CopperLegs, e.EquipLegs);
            Assert.Equal(3, e.Armor);
            Assert.Equal(2, e.Mass);
            Assert.Equal(pos, e.Mover.Position);
        }

        // ---------- Протокол ----------

        /// <summary>
        /// «Сумка» (26): в конце — 3 байта надетого; длина 1+4+2+121+3; round-trip;
        /// надетое не в своей ячейке — ProtocolException. Заявка (29): ячейки до 72 включительно, 73 — отказ.
        /// </summary>
        [Fact]
        public void E36_Inventory_Message_Carries_Gear()
        {
            var bag = new Inventory();
            Assert.Equal(1, bag.Add(Items.CopperHelmet, 1));
            int helmetCell = 0;
            while (bag.ItemAt(helmetCell) == Items.None)
            {
                helmetCell++;
            }
            var m = new InventoryMessage { EntityId = 3, Version = 1, Bag = bag, EquipHead = Items.CopperHelmet, EquipBody = Items.CopperChest, EquipLegs = Items.CopperLegs };
            var buf = new byte[512];
            int n = m.Write(buf);
            Assert.Equal(1 + 4 + 2 + 121 + 3, n);
            InventoryMessage r = InventoryMessage.Read(buf.AsSpan(0, n));
            Assert.Equal(Items.CopperHelmet, r.EquipHead);
            Assert.Equal(Items.CopperChest, r.EquipBody);
            Assert.Equal(Items.CopperLegs, r.EquipLegs);
            Assert.Equal(Items.CopperHelmet, r.Bag.ItemAt(helmetCell));

            // Нагрудник в «голове» — сообщение не читается (тип 1 + сущность 4 + версия 2 + выбранная 1 + ячейки 120 → надетое с 128).
            buf[1 + 4 + 2 + 1 + 120] = Items.CopperChest;
            Assert.Throws<ProtocolException>(() => InventoryMessage.Read(buf.AsSpan(0, n)));
            buf[1 + 4 + 2 + 1 + 120] = Items.CopperHelmet;

            // Заявка (29): 70…72 проходят, 73 — нет.
            Assert.Equal(Inventory.Cells + Simulation.ChestCells + Simulation.EquipCells, BagRequestMessage.MaxCell);
            Assert.Equal(73, BagRequestMessage.MaxCell);
            var req = new BagRequestMessage { Op = BagOp.Move, From = 10, To = 72, Count = 0 };
            n = req.Write(buf);
            BagRequestMessage rr = BagRequestMessage.Read(buf.AsSpan(0, n));
            Assert.Equal(72, rr.To);
            buf[3] = 73;
            Assert.Throws<ProtocolException>(() => BagRequestMessage.Read(buf.AsSpan(0, n)));
        }

        /// <summary>Смена протокола и формата зафиксирована: протокол 35, формат сейва 15.</summary>
        [Fact]
        public void E36_Protocol_And_Format_Bump()
        {
            Assert.Equal(35, NetModule.ProtocolVersion);
            Assert.Equal(16u, SaveModule.FormatVersion);
        }

        // ---------- Очаг ----------

        /// <summary>
        /// Очаг — тайл в центре мира: точка возрождения без кровати — он; сам Очаг в мире не строится
        /// (объекта в центре нет).
        /// </summary>
        [Fact]
        public void E36_Hearth_Is_Center_Tile_And_Respawn_Goes_There()
        {
            WorldModel world = Flat();
            var sim = new Simulation(world, maxEntities: 8);
            int id = sim.AddEntity(Center(300, 200));
            Assert.Equal(sim.Shape.Width / 2, sim.RespawnCenter.X / U);
            Assert.Equal(sim.Shape.Height / 2, sim.RespawnCenter.Y / U);
            var hearth = new TileCoord(sim.Shape.Width / 2, sim.Shape.Height / 2);
            Assert.Equal(0, ObjectId.IdOf(world.GetObject(hearth))); // Очаг — не объект

            Assert.True(sim.ApplyStrike(id, 0, 500) > 0);
            Run(sim, Simulation.RespawnDelayTicks + 2);
            SimEntity e = sim.GetEntity(id);
            Assert.True(e.Alive);
            // Сравниваем с явным центром мира (тайл Очага), а не с RespawnCenter: проверка не должна
            // ссылаться на саму точку возрождения.
            var hearthPos = new WorldPos((sim.Shape.Width / 2 << Torus.SubTileBits) + Torus.SubTilesPerTile / 2,
                (sim.Shape.Height / 2 << Torus.SubTileBits) + Torus.SubTilesPerTile / 2);
            Assert.True(Torus.Dist2(sim.Shape, e.Mover.Position, hearthPos) <= (long)(4 * U) * (4 * U),
                "Возрождение без кровати — у Очага (центр мира).");
        }

        // ---------- Регрессия таблицы ----------

        /// <summary>Таблица предметов: броня — категория Armor, стопка 1, Slot и SetId заданы; у прочего Slot = None.</summary>
        [Fact]
        public void E36_ItemTable_Armor_Rows_Sane()
        {
            for (int id = 1; id < ItemTable.Count; id++)
            {
                ItemDef d = ItemTable.Get((byte)id);
                if (d.Slot == Items.SlotNone)
                {
                    Assert.True(d.Kind != Items.KindArmor, $"Предмет {id}: Kind armor без ячейки снаряжения.");
                    continue;
                }

                Assert.Equal(ItemCategory.Armor, d.Category);
                Assert.Equal(Items.KindArmor, d.Kind);
                Assert.Equal(1, d.MaxStack);
                Assert.True(d.Armor > 0, $"Предмет {id}: броня обязана быть положительной.");
                Assert.InRange(d.Slot, Items.SlotHead, Items.SlotLegs);
            }

            Assert.Equal(Items.SetCopper, Items.SetOf(Items.CopperHelmet));
            Assert.Equal(Items.SetCopper, Items.SetOf(Items.CopperChest));
            Assert.Equal(Items.SetCopper, Items.SetOf(Items.CopperLegs));
            Assert.Equal(Items.SlotHead, Items.SlotOf(Items.CopperHelmet));
            Assert.Equal(Items.SlotBody, Items.SlotOf(Items.CopperChest));
            Assert.Equal(Items.SlotLegs, Items.SlotOf(Items.CopperLegs));
            Assert.Equal(1, Items.ArmorOf(Items.CopperHelmet));
            Assert.Equal(Items.SetNone, Items.SetOf(Items.CopperIngot));
            Assert.Equal(1, ItemTable.MaxStackOf(Items.CopperHelmet)); // снаряжение — по одной штуке на ячейку
        }
    }
}
