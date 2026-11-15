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
    /// E2-5 (шаг 2.5): режим стройки (<see cref="BuildHand"/> — тот же путь, что у окна и бота), починка, сундук, кровать.
    /// Сетевые тесты щёлкают «молотом» и «рукой» настоящему серверу в памяти (<see cref="EditTests.EditRig"/>).
    /// </summary>
    public class BuildTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldPos Center(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private static readonly WorldPos Spot = Center(300, 200);

        /// <summary>Молот в руках (ячейка 2 стартового набора).</summary>
        private static readonly InputFrame HammerHold = new InputFrame(Input8.None, 0, 0, 2);

        /// <summary>Копать молотом (снос).</summary>
        private static readonly InputFrame DigHammer = new InputFrame(Input8.None, InputButtons.Dig, 0, 2);

        /// <summary>Плоский мир; тайл воды (306, 199) — для настила; дерево (296, 203).</summary>
        private static WorldModel World()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            world.SetBaseTile(new TileCoord(306, 199), FloorId.Water, ObjectId.Empty);
            world.SetBaseTile(new TileCoord(296, 203), FloorId.Grass, ObjectId.Tree);
            return world;
        }

        private static Inventory Kit(params (byte Item, int Count)[] items)
        {
            Inventory bag = Inventory.StartingKit();
            foreach ((byte item, int count) in items)
            {
                Assert.Equal(count, bag.Add(item, count));
            }

            bag.Select(2);
            return bag;
        }

        private static NetClient Start(EditTests.EditRig rig, Inventory bag)
        {
            NetClient c = rig.Join();
            rig.InputOf = _ => HammerHold;
            rig.Run(10);
            Assert.True(rig.Sim.SetInventory(c.EntityId, bag));
            rig.Run(3);
            return c;
        }

        /// <summary>Сколько предмета у всех: сумки персонажей, сундуки, кучи на земле.</summary>
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

        private static ushort Obj(Simulation sim, TileCoord t) => ObjectId.IdOf(sim.World.GetObject(t));

        /// <summary>Поставить постройку молотом (выбор в ленте → ЛКМ) и дождаться ответа.</summary>
        private static BuildCheck Build(EditTests.EditRig rig, NetClient c, BuildHand hand, ushort key, TileCoord t)
        {
            Assert.True(hand.Select(key));
            BuildCheck chk = hand.Click(c.World!.World, Spot, c.Inv, c.HeldItem, true, t);
            rig.Run(4);
            return chk;
        }

        // ---------- Лента и постановка ----------

        /// <summary>
        /// Каждая постройка ленты ставится через <see cref="BuildHand"/> (как ЛКМ игрока): предсказание сразу в мире клиента,
        /// сервер принимает, материалы списаны ровно по смете; лента — данными <see cref="BuildDefs.Palette"/>.
        /// </summary>
        [Fact]
        public void E25_Palette_EveryBuild_PlacedThroughBuildHand_CostSpent()
        {
            Assert.Equal(12, BuildDefs.Palette.Length);
            Assert.Equal(ObjectId.Chest, BuildDefs.Palette[10]);
            Assert.Equal(ObjectId.Bed, BuildDefs.Palette[11]);
            using var rig = new EditTests.EditRig(World(), _ => Spot);
            NetClient c = Start(rig, Kit((Items.Wood, 99), (Items.Stone, 60), (Items.Fiber, 10), (Items.Resin, 3)));
            var hand = new BuildHand(c);
            int wood = c.Inv.Count(Items.Wood), stone = c.Inv.Count(Items.Stone), fiber = c.Inv.Count(Items.Fiber), resin = c.Inv.Count(Items.Resin);
            int needWood = 0, needStone = 0, needFiber = 0, needResin = 0;
            for (int k = 0; k < BuildDefs.Palette.Length; k++)
            {
                ushort key = BuildDefs.Palette[k];
                var t = key == BuildDefs.PlatformKey ? new TileCoord(306, 199) : new TileCoord(295 + k, 197);
                BuildCheck chk = Build(rig, c, hand, key, t);
                Assert.True(chk.Ok, $"постройка {key}: {chk.Reason}");
                Assert.Equal(BuildAction.Place, chk.Action);
                if (EditRules.IsFloorKey(key))
                {
                    Assert.Equal(BuildDefs.FloorOf(key), rig.Sim.World.GetFloor(t));
                    Assert.Equal(BuildDefs.FloorOf(key), c.World!.World.GetFloor(t));
                }
                else
                {
                    Assert.Equal(key, Obj(rig.Sim, t));
                    Assert.Equal(key, ObjectId.IdOf(c.World!.World.GetObject(t)));
                }

                BuildDefs.TryGet(key, out BuildDef def);
                foreach ((Material m, int n) in new[] { (def.Material1, def.Count1), (def.Material2, def.Count2) })
                {
                    switch (m)
                    {
                        case Material.Wood: needWood += n; break;
                        case Material.Stone: needStone += n; break;
                        case Material.Fiber: needFiber += n; break;
                        case Material.Resin: needResin += n; break;
                    }
                }
            }

            SimEntity e = rig.Sim.GetEntity(c.EntityId);
            Assert.Equal(wood - needWood, e.Inv.Count(Items.Wood));
            Assert.Equal(stone - needStone, e.Inv.Count(Items.Stone));
            Assert.Equal(fiber - needFiber, e.Inv.Count(Items.Fiber));
            Assert.Equal(resin - needResin, e.Inv.Count(Items.Resin));
            Assert.Equal(e.Inv, c.Inv);
            Assert.Equal(12, hand.PlaceRequests);
            Assert.Equal(0, rig.Server.EditsRefused);

            // Лента листается по кругу (Q / E); подпись «есть / нужно» — по сумке.
            hand.Select(BuildDefs.WoodFloorKey);
            hand.Step(-1);
            Assert.Equal(ObjectId.Bed, hand.Key);
            hand.Step(1);
            Assert.Equal(BuildDefs.WoodFloorKey, hand.Key);
            (Material mat, int have, int need) = BuildHand.CostLine(ObjectId.Bed, 1, c.Inv);
            Assert.Equal((Material.Fiber, fiber - needFiber, 5), (mat, have, need));
        }

        /// <summary>
        /// «Нельзя» с причиной — той же функцией ядра, что у сервера: без молота, мёртвый, далеко, дерево, на себя,
        /// нет ресурсов, настил на суше, пол на воде; ЛКМ по красному заявку не шлёт; сервер отвечает той же причиной.
        /// </summary>
        [Fact]
        public void E25_Refusals_WithReason_SameAsServer()
        {
            WorldModel w = World();
            Inventory bag = Kit((Items.Wood, 20), (Items.Stone, 20));
            BuildCheck Check(TileCoord t, ushort key, Inventory? b = null, bool alive = true) =>
                BuildHand.Check(w, Spot, b ?? bag, (b ?? bag).Held, alive, t, key);

            var free = new TileCoord(302, 200);
            Assert.True(Check(free, ObjectId.StoneWall).Ok);
            Inventory noHammer = bag;
            noHammer.Select(0);
            Assert.Equal(EditReply.ToolRequired, Check(free, ObjectId.StoneWall, noHammer).Reply);
            Assert.Equal(EditReply.Dead, Check(free, ObjectId.StoneWall, alive: false).Reply);
            Assert.Equal(EditReply.OutOfReach, Check(new TileCoord(307, 200), ObjectId.StoneWall).Reply);
            Assert.Equal(EditReply.DigFirst, Check(new TileCoord(296, 203), ObjectId.StoneWall).Reply);
            Assert.Equal(EditReply.Occupied, Check(new TileCoord(300, 200), ObjectId.StoneWall).Reply);
            Assert.True(Check(new TileCoord(300, 200), BuildDefs.WoodFloorKey).Ok); // пол — под себя можно
            Assert.Equal(EditReply.NoResources, Check(free, ObjectId.Bed).Reply); // волокна нет
            Assert.Equal(EditReply.NotAllowed, Check(free, BuildDefs.PlatformKey).Reply);
            Assert.Equal(EditReply.NeedPlatform, Check(new TileCoord(306, 199), BuildDefs.WoodFloorKey).Reply);
            Assert.Equal("нет ресурсов", Check(free, ObjectId.Bed).Reason);

            using var rig = new EditTests.EditRig(w, _ => Spot);
            NetClient c = Start(rig, bag);
            var hand = new BuildHand(c);
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            BuildCheck red = Build(rig, c, hand, ObjectId.Bed, free);
            Assert.Equal(EditReply.NoResources, red.Reply);
            Assert.Equal(0, hand.PlaceRequests);
            Assert.Empty(replies);

            // Минуя руку — сервер говорит то же.
            c.Place(free, free, ObjectId.Bed, Spot);
            rig.Run(4);
            Assert.Equal(EditReply.NoResources, Assert.Single(replies).Reply);
            Assert.Equal(ObjectId.Empty, Obj(rig.Sim, free));
        }

        // ---------- Снос и починка ----------

        /// <summary>Снос молотом (ПКМ — копание молотом): постройка исчезает, материалы — кучей на её тайле, масса та же.</summary>
        [Fact]
        public void E25_Remove_WithHammer_ReturnsMaterialsAsPile()
        {
            using var rig = new EditTests.EditRig(World(), _ => Spot);
            NetClient c = Start(rig, Kit((Items.Wood, 30), (Items.Fiber, 10)));
            var hand = new BuildHand(c);
            int wood = Total(rig.Sim, Items.Wood), fiber = Total(rig.Sim, Items.Fiber);
            var t = new TileCoord(301, 200); // снос — копанием: досягаемость молота — соседний тайл
            Assert.True(Build(rig, c, hand, ObjectId.Bed, t).Ok);
            Assert.Equal(ObjectId.Bed, Obj(rig.Sim, t));
            Assert.Equal(wood, Total(rig.Sim, Items.Wood) + 10);

            Assert.Equal(EditReply.Accepted, EditRules.CheckDig(c.World!.World, Spot, t, c.DigTimes, Items.Hammer));
            rig.InputOf = _ => DigHammer;
            c.StartDig(t);
            rig.Run(40);
            rig.InputOf = _ => HammerHold;
            c.StopDig();
            rig.Run(4);
            Assert.Equal(ObjectId.Empty, Obj(rig.Sim, t));
            Assert.Equal(wood, Total(rig.Sim, Items.Wood));
            Assert.Equal(fiber, Total(rig.Sim, Items.Fiber)); // возврат упал кучей рядом и подобран (соседний тайл)
        }

        /// <summary>
        /// Починка (`05` 1.3): ЛКМ молотом по повреждённой постройке; под ударом — «под ударом»; цена ⌈количество × недостающая
        /// доля⌉ (каменная стена на половине — 1 камень); после — полная прочность; целая — не чинится; нет материала — отказ.
        /// </summary>
        [Fact]
        public void E25_Repair_CostByMissingShare_UnderAttack_Intact_NoResources()
        {
            using var rig = new EditTests.EditRig(World(), _ => Spot);
            NetClient c = Start(rig, Kit((Items.Stone, 3), (Items.Wood, 4)));
            var hand = new BuildHand(c);
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            var t = new TileCoord(302, 200);
            Assert.True(Build(rig, c, hand, ObjectId.StoneWall, t).Ok);
            Assert.Equal(1, c.Inv.Count(Items.Stone));

            Assert.True(rig.Sim.DamageBuilding(t, 150));
            rig.Run(3);
            Assert.True(rig.Sim.World.TryGetBuild(t, out BuildRecord rec));
            Assert.Equal(150, rec.Integrity);
            BuildCheck chk = hand.Click(c.World!.World, Spot, c.Inv, c.HeldItem, true, t);
            Assert.Equal((BuildAction.Repair, EditReply.UnderAttack), (chk.Action, chk.Reply));
            Assert.Equal(0, hand.RepairRequests);

            rig.Run(BuildDefs.DamageFlagTicks + 5);
            chk = BuildHand.Check(c.World!.World, Spot, c.Inv, c.HeldItem, true, t, hand.Key);
            Assert.Equal((BuildAction.Repair, EditReply.Accepted, Material.Stone, 1), (chk.Action, chk.Reply, chk.Material1, chk.Count1));
            chk = hand.Click(c.World!.World, Spot, c.Inv, c.HeldItem, true, t);
            Assert.True(chk.Ok);
            rig.Run(BuildDefs.RepairFrames / 2 + 4); // C-005: починка идёт 0,5 с
            Assert.Equal(EditReply.Accepted, replies[^1].Reply);
            Assert.True(rig.Sim.World.TryGetBuild(t, out rec));
            Assert.Equal(300, rec.Integrity);
            Assert.True(c.World!.World.TryGetBuild(t, out rec));
            Assert.Equal(300, rec.Integrity); // патч чанка дошёл до клиента
            Assert.Equal(0, c.Inv.Count(Items.Stone));
            Assert.Equal(EditReply.Busy, BuildHand.Check(c.World!.World, Spot, c.Inv, c.HeldItem, true, t, ObjectId.StoneWall).Reply); // цела — ЛКМ «ставит», занято

            // Целая — сервер отвечает «цело»; камня нет — «нет ресурсов» (и у руки, и у сервера).
            c.Repair(t);
            rig.Run(4);
            Assert.Equal(EditReply.Intact, replies[^1].Reply);
            Assert.True(rig.Sim.DamageBuilding(t, 30));
            rig.Run(BuildDefs.DamageFlagTicks + 5);
            Assert.Equal(EditReply.NoResources, BuildHand.Check(c.World!.World, Spot, c.Inv, c.HeldItem, true, t, hand.Key).Reply);
            c.Repair(t);
            rig.Run(4);
            Assert.Equal(EditReply.NoResources, replies[^1].Reply);

            // Деревянная стена 120, урон 30: ⌈2 × 30 / 120⌉ = 1 древесина.
            Assert.Equal(1, BuildDefs.RepairCostOf(2, 90, 120));
            Assert.Equal(1, BuildDefs.RepairCostOf(2, 150, 300));
            Assert.Equal(2, BuildDefs.RepairCostOf(2, 1, 300));
            Assert.Equal(0, BuildDefs.RepairCostOf(2, 300, 300));
        }

        /// <summary>
        /// C-005 (замечание 3 проверки E2-5), `05` 1.3 и приёмка 1.7 п. 7: стену били 2 с назад — «под ударом» (и у руки,
        /// и у сервера); повторный удар отсчитывает 3 с заново; через 3 с после последнего удара — можно. Починка идёт 0,5 с:
        /// раньше ни прочности, ни списания; через 0,5 с — полная прочность и ⌈2 × недостающая доля⌉ камня. Ушёл из радиуса
        /// во время починки — «прервано», постройка и сумка не тронуты.
        /// </summary>
        [Fact]
        public void C005_Repair_HalfSecond_InterruptedByLeaving_Blocked3sAfterHit()
        {
            Assert.Equal(90, BuildDefs.RepairBlockTicks); // 3 с при 30 Гц
            Assert.Equal(30, BuildDefs.RepairFrames); // 0,5 с кадрами 60 Гц
            using var rig = new EditTests.EditRig(World(), _ => Spot);
            NetClient c = Start(rig, Kit((Items.Stone, 8)));
            var hand = new BuildHand(c);
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            var t = new TileCoord(302, 200);
            var edge = new TileCoord(306, 200); // на краю радиуса постановки (6 тайлов)
            Assert.True(Build(rig, c, hand, ObjectId.StoneWall, t).Ok);
            Assert.True(Build(rig, c, hand, ObjectId.StoneWall, edge).Ok);
            Assert.Equal(4, c.Inv.Count(Items.Stone));

            // Удар; через 2 с — «под ударом»; второй удар на 2 с; через 1,2 с после него (3,2 с после первого) — всё ещё нельзя.
            Assert.True(rig.Sim.DamageBuilding(t, 150));
            rig.Run(60);
            Assert.Equal(EditReply.UnderAttack, BuildHand.Check(c.World!.World, Spot, c.Inv, c.HeldItem, true, t, hand.Key).Reply);
            c.Repair(t);
            rig.Run(3);
            Assert.Equal(EditReply.UnderAttack, replies[^1].Reply);
            Assert.True(rig.Sim.DamageBuilding(t, 10));
            rig.Run(36);
            Assert.Equal(EditReply.UnderAttack, BuildHand.Check(c.World!.World, Spot, c.Inv, c.HeldItem, true, t, hand.Key).Reply);
            rig.Run(60);
            Assert.True(rig.Sim.World.TryGetBuild(t, out BuildRecord rec));
            Assert.True(rec.IsDamagedRecent); // «урон последние 10 с» (снос — в обломки) ещё стоит, чинить уже можно
            BuildCheck chk = BuildHand.Check(c.World!.World, Spot, c.Inv, c.HeldItem, true, t, hand.Key);
            Assert.Equal((BuildAction.Repair, EditReply.Accepted, 2), (chk.Action, chk.Reply, chk.Count1)); // прочность 140: ⌈2 × 160 / 300⌉ = 2

            // Починка: 0,5 с ничего не меняется, потом — полная прочность и списание.
            int replied = replies.Count;
            c.Repair(t);
            rig.Run(BuildDefs.RepairFrames / 2 - 3);
            Assert.Equal(replied, replies.Count);
            Assert.True(rig.Sim.World.TryGetBuild(t, out rec));
            Assert.Equal(140, rec.Integrity);
            Assert.Equal(4, rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Stone));
            rig.Run(6);
            Assert.Equal(EditReply.Accepted, replies[^1].Reply);
            Assert.True(rig.Sim.World.TryGetBuild(t, out rec));
            Assert.Equal(300, rec.Integrity);
            int stone = rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Stone);
            Assert.Equal(4 - 2, stone);

            // Стена на краю радиуса: починку начали и ушли на тайл западнее — «прервано», ничего не изменилось.
            Assert.True(rig.Sim.DamageBuilding(edge, 100));
            rig.Run(BuildDefs.RepairBlockTicks + 3);
            Assert.True(BuildHand.Check(c.World!.World, Spot, c.Inv, c.HeldItem, true, edge, hand.Key).Ok);
            replied = replies.Count;
            c.Repair(edge);
            rig.InputOf = _ => new InputFrame(Input8.West, 0, 0, 2);
            rig.Run(10);
            rig.InputOf = _ => HammerHold;
            rig.Run(BuildDefs.RepairFrames);
            Assert.Equal(replied + 1, replies.Count);
            Assert.Equal(EditReply.Interrupted, replies[^1].Reply);
            Assert.True(rig.Sim.World.TryGetBuild(edge, out rec));
            Assert.Equal(200, rec.Integrity);
            Assert.Equal(stone, rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Stone));
        }

        /// <summary>
        /// C-005: истечения обоих флагов урона в один такт (удар, через 7 с второй удар: 10 с первого и 3 с второго кончаются
        /// вместе) — одна правка на тайл, оба флага сняты; ни один не «застревает» навсегда. Ядро без сети.
        /// </summary>
        [Fact]
        public void C005_Repair_DamageFlags_ExpireSameTick_BothCleared()
        {
            var sim = new Simulation(World(), maxEntities: 4);
            var t = new TileCoord(310, 210);
            Assert.True(sim.RequestEdit(t, FloorId.Grass, ObjectId.StoneWall, new BuildRecord(1, 300, 0, FloorId.Grass, 0)));
            sim.Step();
            Assert.True(sim.DamageBuilding(t, 10));
            for (int i = 0; i < BuildDefs.DamageFlagTicks - BuildDefs.RepairBlockTicks; i++)
            {
                sim.Step();
            }

            Assert.True(sim.DamageBuilding(t, 10));
            for (int i = 0; i < BuildDefs.RepairBlockTicks + 3; i++)
            {
                sim.Step();
                Assert.True(sim.World.TryGetBuild(t, out BuildRecord r));
                Assert.True(r.IsRepairBlocked == r.IsDamagedRecent, $"такт {sim.Tick}: флаги {r.Flags}"); // уходят вместе
            }

            Assert.True(sim.World.TryGetBuild(t, out BuildRecord rec));
            Assert.Equal(280, rec.Integrity);
            Assert.Equal(0, rec.Flags);
        }

        /// <summary>
        /// C-005 (замечание 2 проверки (П)): идущую починку прерывают — новая починка (прежней «прервано» сразу, новая идёт
        /// своим чередом), молот убран из рук, смерть. На каждую заявку ровно один ответ; прерванная — сразу (не через 0,5 с
        /// и не отказом «нужен инструмент»), постройка и сумка не тронуты.
        /// </summary>
        [Fact]
        public void C005_Repair_Interrupted_ByNewRepair_HammerAway_Death_OneReplyEach()
        {
            using var rig = new EditTests.EditRig(World(), _ => Spot);
            NetClient c = Start(rig, Kit((Items.Stone, 12)));
            var hand = new BuildHand(c);
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            var a = new TileCoord(302, 200);
            var b = new TileCoord(298, 200);
            Assert.True(Build(rig, c, hand, ObjectId.StoneWall, a).Ok);
            Assert.True(Build(rig, c, hand, ObjectId.StoneWall, b).Ok);
            Assert.True(rig.Sim.DamageBuilding(a, 150));
            Assert.True(rig.Sim.DamageBuilding(b, 150));
            rig.Run(BuildDefs.RepairBlockTicks + 3);
            int stone = rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Stone);
            replies.Clear();

            int Of(ushort id) => replies.FindAll(r => r.RequestId == id).Count;
            EditReply ReplyOf(ushort id) => replies.Find(r => r.RequestId == id).Reply;
            int Integrity(TileCoord t) => rig.Sim.World.TryGetBuild(t, out BuildRecord r) ? r.Integrity : -1;

            // Новая починка вытесняет прежнюю: прежней — «прервано» сразу, новая доходит до конца.
            ushort first = c.Repair(a);
            rig.Run(4);
            Assert.Empty(replies);
            ushort second = c.Repair(b);
            rig.Run(3);
            Assert.Equal(1, Of(first));
            Assert.Equal(EditReply.Interrupted, ReplyOf(first));
            Assert.Equal(0, Of(second));
            rig.Run(BuildDefs.RepairFrames / 2 + 4);
            Assert.Equal((1, 1, 2), (Of(first), Of(second), replies.Count));
            Assert.Equal(EditReply.Accepted, ReplyOf(second));
            Assert.Equal(150, Integrity(a));
            Assert.Equal(300, Integrity(b));
            stone -= BuildDefs.RepairCostOf(2, 150, 300); // списано только за вторую
            Assert.Equal(stone, rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Stone));

            // Молот убран из рук: «прервано» сразу (не «нужен инструмент» через 0,5 с), стена и сумка не тронуты.
            ushort third = c.Repair(a);
            rig.Run(3);
            rig.InputOf = _ => new InputFrame(Input8.None, 0, 0, 0); // в руки — кирка
            rig.Run(3);
            Assert.Equal(1, Of(third));
            Assert.Equal(EditReply.Interrupted, ReplyOf(third));
            rig.InputOf = _ => HammerHold;
            rig.Run(BuildDefs.RepairFrames);
            Assert.Equal(1, Of(third));
            Assert.Equal(150, Integrity(a));
            Assert.Equal(stone, rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Stone));

            // Смерть во время починки: «прервано» до возрождения, ровно один ответ; стена и сумка не тронуты.
            rig.Run(Simulation.InvulnRespawnTicks + 2);
            ushort fourth = c.Repair(a);
            rig.Run(3);
            Assert.Equal(0, Of(fourth));
            rig.Sim.ApplyStrike(c.EntityId, -1, 1000, rig.Sim.GetEntity(c.EntityId).Mover.Position, knockback: false);
            Assert.False(rig.Sim.GetEntity(c.EntityId).Alive);
            rig.Run(3);
            Assert.False(rig.Sim.GetEntity(c.EntityId).Alive);
            Assert.Equal(1, Of(fourth));
            Assert.Equal(EditReply.Interrupted, ReplyOf(fourth));
            rig.Run(Simulation.RespawnDelayTicks + BuildDefs.RepairFrames);
            Assert.Equal(1, Of(fourth));
            Assert.Equal(150, Integrity(a));
            Assert.Equal(stone, Total(rig.Sim, Items.Stone)); // сумка могла выпасть кучей — масса та же
            Assert.Equal(4, replies.Count);
        }

        // ---------- Сундук ----------

        private static BagHand Hand(NetClient c)
        {
            var h = new BagHand(c);
            h.Sync(c.Inv, false);
            h.SyncChest(c.ChestOpen, c.Chest);
            return h;
        }

        /// <summary>
        /// Сундук открывают двое сразу: F (действие) → содержимое у обоих; первый кладёт древесину, второй видит и забирает
        /// половину; Shift+ЛКМ перекидывает сумка ↔ сундук; масса сохраняется; далёкому — «далеко»; заявки без предсказания.
        /// </summary>
        [Fact]
        public void E25_Chest_TwoPlayers_Open_Move_Split_Quick_MassConserved()
        {
            using var rig = new EditTests.EditRig(World(), i => Center(300 + 2 * i - (i == 2 ? -20 : 0), 200));
            NetClient a = Start(rig, Kit((Items.Wood, 40)));
            NetClient b = rig.Join();
            NetClient far = rig.Join();
            rig.Run(10);
            Assert.True(rig.Sim.SetInventory(b.EntityId, Kit((Items.Stone, 9))));
            rig.Run(3);
            var t = new TileCoord(301, 202);
            Assert.True(Build(rig, a, new BuildHand(a), ObjectId.Chest, t).Ok);
            int wood = Total(rig.Sim, Items.Wood), stone = Total(rig.Sim, Items.Stone);

            var uses = new List<UseReplyMessage>();
            a.UseReplied = uses.Add;
            far.UseReplied = uses.Add;
            Assert.True(a.RequestUse(t));
            Assert.True(b.RequestUse(t));
            Assert.True(far.RequestUse(t));
            rig.Run(3);
            Assert.True(a.ChestOpen && b.ChestOpen);
            Assert.False(far.ChestOpen);
            Assert.Contains(uses, u => u.Reply == EditReply.OutOfReach);
            Assert.Equal(0, a.Chest.UsedCells);

            // a: ЛКМ по древесине в сумке → ЛКМ по ячейке сундука 0.
            BagHand ha = Hand(a);
            int woodCell = -1;
            for (int i = 0; i < Inventory.Cells; i++)
            {
                woodCell = a.Inv.ItemAt(i) == Items.Wood ? i : woodCell;
            }

            int inBag = a.Inv.CountIn(woodCell);
            Assert.Equal(BagClick.Taken, ha.Left(woodCell));
            Assert.Equal(BagClick.Moved, ha.Left(BagHand.ChestBase + 0));
            Assert.Equal(0, a.Chest.UsedCells); // не предсказано
            rig.Run(3);
            Assert.Equal((Items.Wood, inBag), (a.Chest.ItemAt(0), a.Chest.CountIn(0)));
            Assert.Equal(a.Chest, b.Chest);
            Assert.True(rig.Sim.TryGetChest(t, out Inventory server));
            Assert.Equal(server, b.Chest);

            // b: ПКМ — половина из сундука → в пустую ячейку сумки; Shift+ЛКМ камень → сундук.
            BagHand hb = Hand(b);
            int empty = BagHand.FitTarget(b.Inv, Items.Meat, Inventory.Slots, Inventory.Cells);
            Assert.Equal(BagClick.Taken, hb.Right(BagHand.ChestBase + 0));
            Assert.Equal(BagClick.Split, hb.Left(empty));
            rig.Run(3);
            hb = Hand(b);
            int stoneCell = BagHand.FitTarget(b.Inv, Items.Stone, 0, Inventory.Cells);
            Assert.Equal(BagClick.Moved, hb.Quick(stoneCell));
            rig.Run(3);
            Assert.Equal((inBag + 1) / 2, b.Inv.Count(Items.Wood));
            Assert.Equal(inBag - (inBag + 1) / 2, a.Chest.Count(Items.Wood));
            Assert.Equal(9, a.Chest.Count(Items.Stone));
            Assert.Equal(0, b.Inv.Count(Items.Stone));

            // Shift+ЛКМ по ячейке сундука — в сумку; сундук → вне окна (выброс) — кучей у ног.
            ha = Hand(a);
            Assert.Equal(BagClick.Moved, ha.Quick(BagHand.ChestBase + BagHand.FitTarget(a.Chest, Items.Stone, 0, Simulation.ChestCells)));
            rig.Run(3);
            Assert.Equal(9, a.Inv.Count(Items.Stone));
            Assert.Equal(wood, Total(rig.Sim, Items.Wood));
            Assert.Equal(stone, Total(rig.Sim, Items.Stone));

            // Закрыл — заявки на ячейки сундука сервер не принимает.
            Assert.True(a.RequestCloseChest());
            rig.Run(2);
            long refused = rig.Server.BagRequestsRefused;
            Assert.True(a.RequestBagMove(0, BagHand.ChestBase + 5));
            rig.Run(3);
            Assert.Equal(refused + 1, rig.Server.BagRequestsRefused);
            Assert.False(a.ChestOpen);
            Assert.True(b.ChestOpen);
            Assert.Equal(0, a.BadMessages + b.BadMessages);
        }

        /// <summary>
        /// Снос сундука с содержимым — содержимое кучами на его тайле (кучи не исчезают) плюс возврат материалов сундука;
        /// у открывших сундук — «закрыт»; разрушение уроном — тоже кучами. Масса сохраняется.
        /// </summary>
        [Fact]
        public void E25_Chest_RemovedOrDestroyed_ContentSpillsAsPiles()
        {
            using var rig = new EditTests.EditRig(World(), _ => Spot);
            NetClient c = Start(rig, Kit((Items.Wood, 30)));
            var hand = new BuildHand(c);
            var t = new TileCoord(301, 200);
            var t2 = new TileCoord(303, 198);
            Assert.True(Build(rig, c, hand, ObjectId.Chest, t).Ok);
            Assert.True(Build(rig, c, hand, ObjectId.Chest, t2).Ok);
            var content = new Inventory();
            content.SetCell(0, Items.CopperOre, 50);
            content.SetCell(29, Items.Arrows, 300);
            Assert.True(rig.Sim.SetChest(t, content));
            Assert.True(rig.Sim.SetChest(t2, content));
            int ore = Total(rig.Sim, Items.CopperOre), wood = Total(rig.Sim, Items.Wood);
            Assert.True(c.RequestUse(t));
            rig.Run(3);
            Assert.True(c.ChestOpen);
            Assert.Equal(50, c.Chest.Count(Items.CopperOre));

            rig.InputOf = _ => DigHammer;
            c.StartDig(t);
            rig.Run(40);
            rig.InputOf = _ => HammerHold;
            c.StopDig();
            rig.Run(4);
            Assert.Equal(ObjectId.Empty, Obj(rig.Sim, t));
            Assert.False(c.ChestOpen); // сервер закрыл
            Assert.False(rig.Sim.TryGetChest(t, out _));
            Assert.Equal(1, rig.Sim.ChestCount);
            Assert.Equal(ore, Total(rig.Sim, Items.CopperOre));
            Assert.Equal(wood + 8, Total(rig.Sim, Items.Wood)); // + возврат самого сундука

            // Разрушение уроном: обломки, содержимое кучами, сундук материалов не возвращает.
            Assert.True(rig.Sim.DamageBuilding(t2, 1000));
            rig.Run(3);
            Assert.Equal(ObjectId.Empty, Obj(rig.Sim, t2));
            Assert.Equal(0, rig.Sim.ChestCount);
            Assert.Equal(ore, Total(rig.Sim, Items.CopperOre));
            Assert.Equal(50 + 600, Total(rig.Sim, Items.Arrows)); // 50 — стартовый набор, по 300 в сундуках
            Assert.Contains(rig.Sim.Piles.ToArray(), p => p.Item == Items.Arrows && p.ExpireTick == Simulation.PileNeverExpires); // россыпь сундука не исчезает
        }

        /// <summary>
        /// Сундук принадлежит миру: игрок с открытым сундуком умер — сундук закрыт, содержимое на месте; ушёл из мира —
        /// содержимое на месте, второй игрок открывает и видит то же; масса сохраняется.
        /// </summary>
        [Fact]
        public void E25_Chest_OwnerDiesOrLeaves_ContentStays()
        {
            using var rig = new EditTests.EditRig(World(), i => Center(300 + i, 200));
            NetClient a = Start(rig, Kit((Items.Wood, 30)));
            NetClient b = rig.Join();
            rig.Run(10);
            var t = new TileCoord(302, 201);
            Assert.True(Build(rig, a, new BuildHand(a), ObjectId.Chest, t).Ok);
            var content = new Inventory();
            content.SetCell(4, Items.CopperIngot, 12);
            Assert.True(rig.Sim.SetChest(t, content));
            Assert.True(a.RequestUse(t));
            rig.Run(3);
            Assert.True(a.ChestOpen);

            rig.Run(Simulation.InvulnRespawnTicks + 2);
            rig.Sim.ApplyStrike(a.EntityId, -1, 1000, rig.Sim.GetEntity(a.EntityId).Mover.Position, knockback: false);
            rig.Run(3);
            Assert.False(a.ChestOpen); // мёртвому сервер закрыл
            long refused = rig.Server.BagRequestsRefused;
            Assert.True(a.RequestBagMove(BagHand.ChestBase + 4, 20));
            rig.Run(3);
            Assert.Equal(refused + 1, rig.Server.BagRequestsRefused);
            Assert.True(rig.Sim.TryGetChest(t, out Inventory after));
            Assert.Equal(content, after);

            a.Disconnect();
            rig.Clients.Remove(a);
            rig.Run(5);
            a.Transport.Dispose();
            Assert.True(b.RequestUse(t));
            rig.Run(3);
            Assert.True(b.ChestOpen);
            Assert.Equal(12, b.Chest.Count(Items.CopperIngot));
            Assert.Equal(12, Total(rig.Sim, Items.CopperIngot));
        }

        /// <summary>Ядро без сети: персонаж в (300, 200) со стартовым набором, сундук на тайле, в сундуке 20 меди (ячейка 0).</summary>
        private static (Simulation Sim, int Id) ChestCore(TileCoord chest)
        {
            var sim = new Simulation(World(), maxEntities: 8);
            int id = sim.AddEntity(Spot);
            Assert.True(sim.SetInventory(id, Kit()));
            Assert.True(sim.RequestEdit(chest, FloorId.Grass, ObjectId.Chest));
            for (int i = 0; i < Simulation.InvulnRespawnTicks + 2; i++)
            {
                sim.Step(); // и неуязвимость появления прошла — убить можно
            }

            Assert.Equal(ObjectId.Chest, Obj(sim, chest));
            var content = new Inventory();
            content.SetCell(0, Items.CopperIngot, 20);
            Assert.True(sim.SetChest(chest, content));
            return (sim, id);
        }

        /// <summary>
        /// C-005 (замечание 1 проверки E2-5): расстояние до сундука проверяет такт ядра, а не только сервер. Заявка
        /// <see cref="Simulation.RequestChestOp"/> к сундуку в 4 тайлах в очередь встаёт (проверка — в такте), но такт её не
        /// исполняет: ни сундук, ни сумка не меняются; та же заявка к сундуку в 3 тайлах — исполняется.
        /// </summary>
        [Fact]
        public void C005_Chest_Core_FarRequester_RefusedInTick()
        {
            foreach ((int dx, int dy, bool reach) in new[] { (4, 0, false), (0, -4, false), (4, 4, false), (3, 0, true), (-3, 3, true) })
            {
                var t = new TileCoord(300 + dx, 200 + dy);
                (Simulation sim, int id) = ChestCore(t);
                Assert.Equal(reach, EditRules.InUseRadius(sim.Shape, sim.GetEntity(id).Mover.Position, t));
                Inventory bag = sim.GetEntity(id).Inv;
                Assert.True(sim.TryGetChest(t, out Inventory chest));
                int to = BagHand.FitTarget(bag, Items.CopperIngot, 0, Inventory.Cells);

                // Взять из сундука (ячейка 40 → свободная ячейка сумки) и положить в сундук (факелы, ячейка 5 → 40 + 7).
                Assert.True(sim.RequestChestOp(id, t, BagOp.Move, Simulation.ChestCellBase + 0, to, 0));
                Assert.True(sim.RequestChestOp(id, t, BagOp.Move, 5, Simulation.ChestCellBase + 7, 0));
                Assert.True(sim.RequestChestOp(id, t, BagOp.Drop, Simulation.ChestCellBase + 0, 0, 0));
                sim.Step();

                Assert.True(sim.TryGetChest(t, out Inventory after));
                if (reach)
                {
                    Assert.Equal(20, sim.GetEntity(id).Inv.Count(Items.CopperIngot));
                    Assert.Equal(3, after.Count(Items.Torch));
                    Assert.Equal(0, after.Count(Items.CopperIngot));
                }
                else
                {
                    Assert.Equal(chest, after);
                    Assert.Equal(bag, sim.GetEntity(id).Inv);
                    Assert.Equal(0, sim.Piles.Length);
                }
            }
        }

        /// <summary>
        /// C-005 (замечание 1 проверки E2-5): заявитель жив при заявке, убит до такта — такт ядра заявку к сундуку не
        /// исполняет (в обход сервера: <see cref="Simulation.RequestChestOp"/> напрямую). Сундук в 2 тайлах — по расстоянию
        /// заявка прошла бы (контроль — тот же сценарий без смерти).
        /// </summary>
        [Fact]
        public void C005_Chest_Core_DeadRequester_RefusedInTick()
        {
            foreach (bool kill in new[] { true, false })
            {
                var t = new TileCoord(302, 200);
                (Simulation sim, int id) = ChestCore(t);
                Assert.True(sim.TryGetChest(t, out Inventory chest));
                Inventory bag = sim.GetEntity(id).Inv;
                int to = BagHand.FitTarget(bag, Items.CopperIngot, 0, Inventory.Cells);
                Assert.True(sim.RequestChestOp(id, t, BagOp.Move, Simulation.ChestCellBase + 0, to, 0));
                Assert.True(sim.RequestChestOp(id, t, BagOp.Move, 5, Simulation.ChestCellBase + 7, 0));
                Assert.True(sim.RequestChestOp(id, t, BagOp.Split, 3, Simulation.ChestCellBase + 9, 1)); // меч из сумки в сундук
                if (kill)
                {
                    sim.ApplyStrike(id, -1, 1000, sim.GetEntity(id).Mover.Position, knockback: false);
                    Assert.False(sim.GetEntity(id).Alive);
                    Assert.False(sim.RequestChestOp(id, t, BagOp.Move, Simulation.ChestCellBase + 0, to, 0)); // мёртвый — отказ сразу
                }

                sim.Step();
                Assert.True(sim.TryGetChest(t, out Inventory after));
                if (kill)
                {
                    Assert.Equal(chest, after);
                    Assert.Equal(bag, sim.GetEntity(id).Inv);
                    Assert.Equal(0, sim.Piles.Length);
                }
                else
                {
                    Assert.Equal(0, after.Count(Items.CopperIngot));
                    Assert.Equal(3, after.Count(Items.Torch));
                    Assert.Equal(1, after.Count(Items.WoodenSword));
                    Assert.Equal(20, sim.GetEntity(id).Inv.Count(Items.CopperIngot));
                }
            }
        }

        // ---------- Кровать ----------

        /// <summary>
        /// Кровать: F рядом — точка возрождения; смерть → возрождение у кровати (на свободном тайле рядом); кровать снесли →
        /// следующее возрождение у старта. Далеко и не кровать — отказ с причиной.
        /// </summary>
        [Fact]
        public void E25_Bed_RespawnAtBed_ThenAtStartAfterRemoval()
        {
            using var rig = new EditTests.EditRig(World(), _ => Spot);
            rig.Sim.RespawnCenter = Center(250, 250);
            NetClient c = Start(rig, Kit((Items.Wood, 30), (Items.Fiber, 10)));
            var bed = new TileCoord(303, 200);
            Assert.True(Build(rig, c, new BuildHand(c), ObjectId.Bed, bed).Ok);
            var uses = new List<UseReplyMessage>();
            c.UseReplied = uses.Add;
            Assert.True(c.RequestUse(new TileCoord(302, 200)));
            Assert.True(c.RequestUse(bed));
            rig.Run(3);
            Assert.Equal(new[] { EditReply.Empty, EditReply.Accepted }, uses.ConvertAll(u => u.Reply).ToArray());
            Assert.Equal(ObjectId.Bed, ObjectId.IdOf(uses[1].Obj));
            SimEntity e = rig.Sim.GetEntity(c.EntityId);
            Assert.True(e.HasBed);
            Assert.Equal(bed, e.Bed);

            Kill(rig, c);
            e = rig.Sim.GetEntity(c.EntityId);
            Assert.True(e.Alive);
            Assert.True(Torus.Dist2(rig.Sim.Shape, e.Mover.Position, EditRules.CenterOf(bed)) <= 2L * U * 2L * U, $"возродился в {e.Mover.Position}");
            Assert.NotEqual(bed, Torus.TileOf(rig.Sim.Shape, e.Mover.Position));

            // Снесли кровать (правка мира) — возрождение у старта, точка снята.
            Assert.True(rig.Sim.RequestEdit(bed, FloorId.Grass, ObjectId.Empty));
            rig.Run(2);
            Kill(rig, c);
            e = rig.Sim.GetEntity(c.EntityId);
            Assert.Equal(rig.Sim.RespawnCenter, e.Mover.Position);
            Assert.False(e.HasBed);
        }

        private static void Kill(EditTests.EditRig rig, NetClient c)
        {
            rig.Run(Simulation.InvulnRespawnTicks + 2);
            SimEntity e = rig.Sim.GetEntity(c.EntityId);
            rig.Sim.ApplyStrike(c.EntityId, -1, 1000, e.Mover.Position, knockback: false);
            Assert.False(rig.Sim.GetEntity(c.EntityId).Alive);
            rig.Run(Simulation.RespawnDelayTicks + 2);
        }

        // ---------- Сохранение ----------

        /// <summary>
        /// Формат 9: сундуки с содержимым и кровати (у сущности в мире и у персонажа вне мира) — круг сходится, хэш тот же;
        /// перезапуск сервера (загрузка + <see cref="CharacterRoster.Restore"/>) — кровать остаётся за персонажем, вход
        /// возвращает её сущности; испорченный сундук — ошибка чтения.
        /// </summary>
        [Fact]
        public void E25_Save_Format9_Chests_Beds_RoundTrip_ServerRestart()
        {
            Assert.Equal(9u, SaveModule.FormatVersion);
            SaveFixtures.LiveWorld live = SaveFixtures.Create(256, 25, bots: 0);
            Simulation sim = live.Sim;
            WorldPos at = Orpheus.Server.ServerRun.SpawnPoint(sim.Shape, 0);
            int id = sim.AddEntity(at);
            TileCoord me = Torus.TileOf(sim.Shape, at);
            var c1 = new TileCoord(me.X + 2, me.Y);
            var c2 = new TileCoord(me.X - 2, me.Y + 1);
            var bed = new TileCoord(me.X, me.Y + 2);
            Assert.True(sim.RequestEdit(c1, FloorId.Grass, ObjectId.Chest));
            Assert.True(sim.RequestEdit(c2, FloorId.Grass, ObjectId.Chest));
            Assert.True(sim.RequestEdit(bed, FloorId.Grass, ObjectId.Bed));
            live.Run(ticks: 2, editsPerTick: 0);
            var a = new Inventory();
            a.SetCell(3, Items.Wood, 77);
            a.SetCell(29, Items.CopperPickaxe, 1);
            var b = new Inventory();
            b.SetCell(0, Items.Arrows, 999);
            Assert.True(sim.SetChest(c1, a));
            Assert.True(sim.SetChest(c2, b));
            Assert.Equal(EditReply.Accepted, sim.RequestSetBed(id, bed));
            ulong hash = StateHash.Compute(sim).Total;

            var chars = new[]
            {
                new CharacterRecord { IsHost = true, Name = "Орфей", EntityId = id, Inv = sim.GetEntity(id).Inv },
                new CharacterRecord { Name = "Эвридика", Inv = Inventory.StartingKit(), Mover = new Mover(at), HasBed = true, Bed = bed },
            };
            byte[] save = WorldSave.Build(WorldSave.Capture(sim, live.BaseSha, "сундуки", chars), 2, 0);
            SaveFile f = SaveContainer.Read(save);
            Assert.True(f.IsOk, f.Error);
            Assert.Equal(9u, f.Header.FormatVersion);
            LoadedWorldState st = WorldSave.Load(f, WorldSnapshot.LoadBase(live.BaseBytes), live.BaseSha);
            Assert.Equal(2, st.ChestTiles.Length);
            Assert.Equal(c1, st.ChestTiles[0]); // по (Y, X): c2 южнее (Y больше) — второй
            Assert.Equal(a, st.Chests[0]);
            Assert.Equal(b, st.Chests[1]);
            Assert.True(st.Entities[0].HasBed);
            Assert.Equal(bed, st.Entities[0].Bed);
            Assert.True(st.Characters[1].HasBed);
            Assert.Equal(bed, st.Characters[1].Bed);

            Simulation loaded = SaveFixtures.Load(live.BaseBytes, save);
            Assert.Equal(hash, StateHash.Compute(loaded).Total);
            Assert.True(loaded.TryGetChest(c1, out Inventory got));
            Assert.Equal(a, got);

            // Перезапуск сервера: персонаж из мира — вне мира с кроватью за ключом; вошёл — кровать снова у сущности.
            WorldModel w3 = WorldSnapshot.LoadBase(live.BaseBytes);
            LoadedWorldState st3 = WorldSave.Load(SaveContainer.Read(save), w3, live.BaseSha);
            var sim3 = new Simulation(w3, maxEntities: 8);
            st3.RestoreInto(sim3);
            CharacterRoster roster = CharacterRoster.Restore(st3.Characters, sim3);
            Assert.Equal(0, roster[0].EntityId);
            Assert.True(roster[0].HasBed);
            Assert.Equal(bed, roster[0].Bed);
            Assert.Equal(2, sim3.ChestCount);
            int back = sim3.AddEntity(at);
            Assert.True(sim3.SetBed(back, roster[0].HasBed, roster[0].Bed));
            Assert.True(sim3.GetEntity(back).HasBed);

            // StateHash покрывает сундуки (содержимое и тайл) и кровать; без них — хэш как до E2-5.
            Assert.True(sim.SetChest(c1, b));
            Assert.NotEqual(hash, StateHash.Compute(sim).Total);
            Assert.True(sim.SetChest(c1, a));
            Assert.Equal(hash, StateHash.Compute(sim).Total);
            Assert.True(sim.SetBed(id, false, default));
            Assert.NotEqual(hash, StateHash.Compute(sim).Total);

            // Сундук с занятой 31-й ячейкой — ошибка чтения.
            var bad = new Inventory();
            bad.SetCell(35, Items.Wood, 1);
            WorldSaveData d = WorldSave.Capture(sim, live.BaseSha, "плохой", chars);
            d.Chests[0] = bad;
            byte[] badSave = WorldSave.Build(d, 3, 0);
            Assert.Throws<InvalidDataException>(() => WorldSave.Load(SaveContainer.Read(badSave), WorldSnapshot.LoadBase(live.BaseBytes), live.BaseSha));
        }

        /// <summary>
        /// Настоящий файл формата 8 (записан кодом ветки task/E2-3 43d3377 — до E2-5; генератор —
        /// <c>Golden/format8_craft.make.cs.txt</c>: заглушка 256², зерно 5, очередь изготовления, сундук и кровать на тайлах,
        /// стена, персонаж вне мира) читается: хэш тот же, сундук пустой, кровати нет; сундук работает, пересохранение — формат 9.
        /// </summary>
        [Fact]
        public void E25_Save_Format8_RealFile_Reads()
        {
            byte[] v8 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format8_craft.save"));
            byte[] baseBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format8_craft.base"));
            SaveFile f = SaveContainer.Read(v8);
            Assert.True(f.IsOk, f.Error);
            Assert.Equal(8u, f.Header.FormatVersion);
            LoadedWorldState st = WorldSave.Load(f, WorldSnapshot.LoadBase(baseBytes), WorldSnapshot.StoredHash(baseBytes));
            Assert.Empty(st.ChestTiles);
            Assert.False(st.Entities[0].HasBed);
            Assert.False(st.Characters[1].HasBed);
            Assert.False(st.Entities[0].Craft.IsEmpty);
            Assert.Equal(77, st.Characters[1].Inv.Count(Items.Stone));

            Simulation sim = SaveFixtures.Load(baseBytes, v8);
            Assert.Equal("e09eff630b0899ff", StateHash.Compute(sim).Total.ToString("x16"));
            var chest = new TileCoord(124, 127);
            var bed = new TileCoord(124, 129);
            Assert.Equal(ObjectId.Chest, Obj(sim, chest));
            Assert.Equal(ObjectId.Bed, Obj(sim, bed));
            Assert.Equal(ObjectId.StoneWall, Obj(sim, new TileCoord(118, 127)));
            Assert.True(sim.TryGetChest(chest, out Inventory empty));
            Assert.Equal(0, empty.UsedCells);

            var content = new Inventory();
            content.SetCell(2, Items.Stone, 5);
            Assert.True(sim.SetChest(chest, content));
            int id = st.Entities[0].Id;
            Assert.Equal(EditReply.Accepted, sim.RequestSetBed(id, bed));
            byte[] again = WorldSave.Build(WorldSave.Capture(sim, WorldSnapshot.StoredHash(baseBytes), "формат8", st.Characters), 7, 0);
            Assert.Equal(9u, SaveContainer.Read(again).Header.FormatVersion);
            Simulation re = SaveFixtures.Load(baseBytes, again);
            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(re).Total);
            Assert.True(re.TryGetChest(chest, out Inventory back));
            Assert.Equal(content, back);
        }

        /// <summary>
        /// C-005 (замечание 2 проверки E2-5): настоящий файл формата 8 с постройками (записан кодом слияния task/E2-3 3846d7a
        /// — до E2-5, где ключ пола в заявке совпадал с id пола; генератор — <c>Golden/format8_build.make.cs.txt</c>: игрок
        /// по сети ставит молотом по три деревянных и каменных пола, настила на воде, деревянных и каменных стены; по одной
        /// стене каждого вида — на половине прочности). После чтения постройки те же (хэш, слой пола, объект, запись постройки),
        /// снос каждой возвращает материалы по прочности: пол — 1, целая стена — 2, стена на половине — 1.
        /// </summary>
        [Fact]
        public void C005_Save_Format8_RealFile_Floors_Walls_Refund()
        {
            byte[] v8 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format8_build.save"));
            byte[] baseBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format8_build.base"));
            SaveFile f = SaveContainer.Read(v8);
            Assert.True(f.IsOk, f.Error);
            Assert.Equal(8u, f.Header.FormatVersion);
            Simulation sim = SaveFixtures.Load(baseBytes, v8);
            Assert.Equal("5b86237e5673c26d", StateHash.Compute(sim).Total.ToString("x16")); // хэш, записанный генератором

            int id = sim.Entities[0].Id;
            var cases = new List<(TileCoord Tile, byte Floor, ushort Obj, byte Natural, int Integrity, byte Item, int Refund)>();
            for (int i = 0; i < 3; i++)
            {
                cases.Add((new TileCoord(130 + i, 128), FloorId.WoodFloor, ObjectId.Empty, FloorId.Grass, BuildRecord.MaxIntegrity, Items.Wood, 1));
                cases.Add((new TileCoord(130 + i, 130), FloorId.StoneFloor, ObjectId.Empty, FloorId.Grass, BuildRecord.MaxIntegrity, Items.Stone, 1));
                cases.Add((new TileCoord(130 + i, 125), FloorId.Platform, ObjectId.Empty, FloorId.Water, BuildRecord.MaxIntegrity, Items.Wood, 1));
                cases.Add((new TileCoord(125, 127 + i), FloorId.Grass, ObjectId.WoodWall, FloorId.Grass, i == 2 ? 60 : 120, Items.Wood, i == 2 ? 1 : 2));
                cases.Add((new TileCoord(123, 127 + i), FloorId.Grass, ObjectId.StoneWall, FloorId.Grass, i == 2 ? 150 : 300, Items.Stone, i == 2 ? 1 : 2));
            }

            foreach (var k in cases)
            {
                Assert.Equal(k.Floor, sim.World.GetFloor(k.Tile));
                Assert.Equal(k.Obj, Obj(sim, k.Tile));
                Assert.True(sim.World.TryGetBuild(k.Tile, out BuildRecord rec), $"нет записи постройки на {k.Tile}");
                Assert.Equal(k.Natural, rec.NaturalFloor);
                Assert.True(k.Integrity == rec.Integrity, $"{k.Tile}: прочность {rec.Integrity}, ждали {k.Integrity}");
            }

            // Снос — та же правка, что ставит сервер, докопав молотом (`NetServer.Edits`: пол — природному из записи).
            foreach (var k in cases)
            {
                int before = Total(sim, k.Item);
                byte newFloor = k.Obj == ObjectId.Empty ? k.Natural : k.Floor;
                Assert.True(sim.RequestPlayerEdit(k.Tile, newFloor, ObjectId.Empty, false, default, id, 0));
                sim.Step();
                Assert.Equal(k.Natural, sim.World.GetFloor(k.Tile));
                Assert.Equal(ObjectId.Empty, Obj(sim, k.Tile));
                Assert.False(sim.World.TryGetBuild(k.Tile, out _));
                Assert.True(before + k.Refund == Total(sim, k.Item), $"{k.Tile} (пол {k.Floor}, объект {k.Obj}): возврат {Total(sim, k.Item) - before}, ждали {k.Refund}");
            }
        }

        // ---------- Сеть ----------

        /// <summary>«Золотые» байты протокола 23: действие (33), ответ (34), сундук (35), починка (TileOp 5); мусор — отказ.</summary>
        [Fact]
        public void E25_Net_GoldenBytes_Use_Reply_Chest_Repair()
        {
            Assert.Equal(24, NetModule.ProtocolVersion);
            var buf = new byte[256];
            int n = new UseRequestMessage { Op = UseOp.Use, Tile = new TileCoord(0x0102, 0x0304) }.Write(buf);
            Assert.Equal(UseRequestMessage.Bytes, n);
            Assert.Equal("21-01-02-01-04-03", BitConverter.ToString(buf, 0, n));
            MapShape map = MapShape.World(1024);
            Assert.Equal(new TileCoord(0x0102, 0x0304), UseRequestMessage.Read(new ReadOnlySpan<byte>(buf, 0, n), map).Tile);
            buf[1] = 3;
            Assert.Throws<ProtocolException>(() => UseRequestMessage.Read(new ReadOnlySpan<byte>(buf, 0, n), map));

            n = new UseReplyMessage { Tile = new TileCoord(5, 6), Obj = ObjectId.Bed, Reply = EditReply.Paused }.Write(buf);
            Assert.Equal("22-05-00-06-00-0A-00-12", BitConverter.ToString(buf, 0, n));
            Assert.Equal(EditReply.Paused, UseReplyMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)).Reply);
            buf[7] = 19;
            Assert.Throws<ProtocolException>(() => UseReplyMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));

            var cells = new Inventory();
            cells.SetCell(0, Items.Wood, 0x63);
            cells.SetCell(29, Items.Arrows, 0x0102);
            n = new ChestStateMessage { Tile = new TileCoord(7, 8), Open = true, Cells = cells }.Write(buf);
            Assert.Equal(ChestStateMessage.Bytes, n);
            Assert.Equal(96, n);
            Assert.Equal("23-07-00-08-00-01-08-63-00-00-00-00", BitConverter.ToString(buf, 0, 12));
            Assert.Equal("0D-02-01", BitConverter.ToString(buf, n - 3, 3));
            ChestStateMessage back = ChestStateMessage.Read(new ReadOnlySpan<byte>(buf, 0, n));
            Assert.Equal(cells, back.Cells);
            buf[7] = 0; // количество без предмета
            Assert.Throws<ProtocolException>(() => ChestStateMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));

            n = new TileRequestMessage { RequestId = 1, Op = TileOp.Repair, Frame = 2, From = new TileCoord(3, 4), To = new TileCoord(3, 4) }.Write(buf);
            Assert.Equal(TileOp.Repair, TileRequestMessage.Read(new ReadOnlySpan<byte>(buf, 0, n), map).Op);
            Assert.Equal(5, buf[3]);
            n = new BagRequestMessage { Op = BagOp.Move, From = 69, To = 0 }.Write(buf);
            Assert.Equal(69, BagRequestMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)).From);
            buf[2] = 70;
            Assert.Throws<ProtocolException>(() => BagRequestMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));
        }

        /// <summary>Пауза, мёртвый и общий лимит заявок: действие — отказ с причиной; заявки на сундук в паузе не исполняются.</summary>
        [Fact]
        public void E25_Net_Use_Pause_Dead_Refused()
        {
            using var rig = new EditTests.EditRig(World(), _ => Spot);
            NetClient c = Start(rig, Kit((Items.Wood, 30)));
            var t = new TileCoord(302, 200);
            Assert.True(Build(rig, c, new BuildHand(c), ObjectId.Chest, t).Ok);
            var uses = new List<UseReplyMessage>();
            c.UseReplied = uses.Add;

            rig.Server.SetPaused(true);
            rig.Run(3);
            Assert.True(rig.Server.Paused);
            Assert.True(c.RequestUse(t));
            rig.Run(3);
            Assert.Equal(EditReply.Paused, uses[^1].Reply);
            Assert.False(c.ChestOpen);
            rig.Server.SetPaused(false);
            rig.Run(3);

            rig.Run(Simulation.InvulnRespawnTicks + 2);
            rig.Sim.ApplyStrike(c.EntityId, -1, 1000, rig.Sim.GetEntity(c.EntityId).Mover.Position, knockback: false);
            Assert.False(rig.Sim.GetEntity(c.EntityId).Alive);
            Assert.True(c.RequestUse(t));
            rig.Run(3);
            Assert.Equal(EditReply.Dead, uses[^1].Reply);
            Assert.Equal(EditReply.Dead, BuildHand.Check(c.World!.World, Spot, c.Inv, c.HeldItem, false, new TileCoord(303, 200), ObjectId.WoodWall).Reply);
        }
    }
}
