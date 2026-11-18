using System;
using System.Collections.Generic;
using System.Linq;
using Orpheus.Client.Anim;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-028 (BRIEF 11n): камень ×4; кирка бьёт взмахом к прицелу, задевает до 3 «каменных» тайлов в секторе; на камень нужно
    /// несколько ударов (каменная кирка 3, медная 2), удары копятся по тайлу у всех игроков и сбрасываются через 5 с без ударов.
    /// </summary>
    public class C028PickStrikeTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat(int size = 1024) => FlatWorldGenerator.Instance.Generate(MapShape.World(size), 1);

        private static WorldPos Center(int x, int y) => EditTests.TileCenter(x, y);

        private static readonly InputFrame DigPick = new InputFrame(Input8.None, InputButtons.Dig, 0, 0); // ячейка 0 — каменная кирка

        // ---------- Ядро: удары, сектор, прочность ----------

        [Fact]
        public void C028_HitsFor_StonePick3_Copper2_OreAndGranite_RefusalCodes()
        {
            var t = new DigTimes();
            Assert.Equal(3, t.HitsFor(ObjectId.Rock, ToolKind.Pick, Items.TierOf(Items.StonePickaxe)));
            Assert.Equal(2, t.HitsFor(ObjectId.Rock, ToolKind.Pick, Items.TierOf(Items.CopperPickaxe)));
            Assert.Equal(1, t.HitsFor(ObjectId.Rock, ToolKind.Pick, 3)); // 0,75 → 1
            Assert.Equal(6, t.HitsFor(ObjectId.Ore, ToolKind.Pick, 1));
            Assert.Equal(3, t.HitsFor(ObjectId.Ore, ToolKind.Pick, 2));
            Assert.Equal(DigTimes.NeedTier, t.HitsFor(ObjectId.Granite, ToolKind.Pick, 1));
            Assert.Equal(5, t.HitsFor(ObjectId.Granite, ToolKind.Pick, 2)); // 4,5 → 5
            Assert.Equal(DigTimes.NeedTool, t.HitsFor(ObjectId.Tree, ToolKind.Pick, 1));
            Assert.Equal(DigTimes.Cannot, t.HitsFor(ObjectId.Cliff, ToolKind.Pick, 1));
            Assert.Equal(0, PickStrike.HitsOf(t, ObjectId.StoneWall, Items.StonePickaxe)); // постройки взмахом не бьются
            Assert.Equal(0, PickStrike.HitsOf(t, ObjectId.Rock, Items.StoneAxe));
        }

        [Fact]
        public void C028_StoneLoot_FourTimes()
        {
            Assert.True(DigLoot.TryGetDrop(ObjectId.Rock, out Material m, out int min, out int max));
            Assert.Equal((Material.Stone, 4, 8), (m, min, max));
            Assert.True(DigLoot.TryGetDrop(ObjectId.Granite, out _, out min, out max));
            Assert.Equal((4, 8), (min, max));
            Assert.True(DigLoot.TryGetDrop(ObjectId.Ore, out _, out min, out max));
            Assert.Equal((2, 4), (min, max)); // C-028, слово Автора: медь — несколько с тайла (было 2…3)
        }

        private static (TileCoord[] Tiles, byte[] Needed) Targets(WorldModel w, WorldPos pos, TileCoord aim, byte held = Items.StonePickaxe)
        {
            var tiles = new TileCoord[PickStrike.MaxTiles];
            var objs = new ushort[PickStrike.MaxTiles];
            var needed = new byte[PickStrike.MaxTiles];
            int n = PickStrike.Targets(w, pos, aim, new DigTimes(), held, tiles, objs, needed);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(w.GetObject(tiles[i]), objs[i]);
            }

            return (tiles.Take(n).ToArray(), needed.Take(n).ToArray());
        }

        /// <summary>Стена скал к востоку: удар на восток — прицел и две соседние по дуге (сектор), не позади и не сбоку за 45°.</summary>
        [Fact]
        public void C028_Targets_RockFace_AimFirst_ThenArc_Max3()
        {
            WorldModel w = Flat();
            for (int y = 196; y <= 204; y++)
            {
                for (int x = 297; x <= 303; x++)
                {
                    if (x != 300 || y != 200)
                    {
                        w.SetBaseTile(new TileCoord(x, y), FloorId.Stone, ObjectId.Rock);
                    }
                }
            }

            var (tiles, needed) = Targets(w, Center(300, 200), new TileCoord(301, 200));
            Assert.Equal(new[] { new TileCoord(301, 200), new TileCoord(301, 199), new TileCoord(301, 201) }.OrderBy(t => t.Y), tiles.Skip(0).OrderBy(t => t.Y));
            Assert.Equal(new TileCoord(301, 200), tiles[0]);
            Assert.All(needed, n => Assert.Equal(3, n));

            // Прицел — дальний тайл в досягаемости (2 т по оси): он первым, затем ближайшие в секторе.
            (tiles, _) = Targets(w, Center(300, 200), new TileCoord(302, 200));
            Assert.Equal(3, tiles.Length);
            Assert.Equal(new TileCoord(302, 200), tiles[0]);
            Assert.Contains(new TileCoord(301, 200), tiles);

            // На север: только северная полуплоскость в секторе.
            (tiles, _) = Targets(w, Center(300, 200), new TileCoord(300, 199));
            Assert.All(tiles, t => Assert.True(t.Y < 200 && Math.Abs(t.X - 300) <= 200 - t.Y));
        }

        /// <summary>Коридор: скалы по линии удара (1 и 2 тайла) — обе; третья (3 т) — дальше досягаемости; сбоку за 45° — нет.</summary>
        [Fact]
        public void C028_Targets_Line_Reach_Sector()
        {
            WorldModel w = Flat();
            w.SetBaseTile(new TileCoord(301, 200), FloorId.Stone, ObjectId.Rock);
            w.SetBaseTile(new TileCoord(302, 200), FloorId.Stone, ObjectId.Rock);
            w.SetBaseTile(new TileCoord(303, 200), FloorId.Stone, ObjectId.Rock);
            w.SetBaseTile(new TileCoord(300, 198), FloorId.Stone, ObjectId.Rock); // на север — вне сектора востока
            w.SetBaseTile(new TileCoord(299, 200), FloorId.Stone, ObjectId.Rock); // позади
            w.SetBaseTile(new TileCoord(301, 201), FloorId.Grass, ObjectId.Tree); // не камень
            var (tiles, _) = Targets(w, Center(300, 200), new TileCoord(310, 200)); // курсор далеко — ось та же
            Assert.Equal(new[] { new TileCoord(301, 200), new TileCoord(302, 200) }, tiles);

            // Пустой прицел рядом, ось на северо-восток: (301,199) пуст, (301,200) — в секторе (45°) — бьётся.
            (tiles, _) = Targets(w, Center(300, 200), new TileCoord(301, 199));
            Assert.Contains(new TileCoord(301, 200), tiles);
            Assert.DoesNotContain(new TileCoord(299, 200), tiles);

            // Прицел в точке персонажа — оси нет, удара нет.
            (tiles, _) = Targets(w, Center(300, 200), new TileCoord(300, 200));
            Assert.Empty(tiles);

            // Граница сектора ±45°: на север — (301,198) в 26,6° от оси — да, (302,199) в 63,4° — нет.
            WorldModel w2 = Flat();
            w2.SetBaseTile(new TileCoord(301, 198), FloorId.Stone, ObjectId.Rock);
            w2.SetBaseTile(new TileCoord(302, 199), FloorId.Stone, ObjectId.Rock);
            (tiles, _) = Targets(w2, Center(300, 200), new TileCoord(300, 199));
            Assert.Equal(new[] { new TileCoord(301, 198) }, tiles);

            // Гранит каменной киркой не бьётся, медной — да (5 ударов).
            w.SetBaseTile(new TileCoord(301, 200), FloorId.Stone, ObjectId.Granite);
            (tiles, _) = Targets(w, Center(300, 200), new TileCoord(301, 200));
            Assert.DoesNotContain(new TileCoord(301, 200), tiles);
            var (ct, cn) = Targets(w, Center(300, 200), new TileCoord(301, 200), Items.CopperPickaxe);
            Assert.Equal(new TileCoord(301, 200), ct[0]);
            Assert.Equal(5, cn[0]);
        }

        /// <summary>Тайлы удара с линией, как на сервере (<see cref="Simulation.PickLineClear"/>; камни этой кирки прозрачны).</summary>
        private static TileCoord[] TargetsWithLine(Simulation sim, WorldPos pos, TileCoord aim, byte held = Items.StonePickaxe)
        {
            var tiles = new TileCoord[PickStrike.MaxTiles];
            var objs = new ushort[PickStrike.MaxTiles];
            var needed = new byte[PickStrike.MaxTiles];
            var times = new DigTimes();
            int n = PickStrike.Targets(sim.World, pos, aim, times, held, tiles, objs, needed,
                (from, t) => sim.PickLineClear(from, t, c => PickStrike.IsTarget(times, sim.World.GetObject(c), held)));
            return tiles.Take(n).ToArray();
        }

        /// <summary>
        /// Проверка П C-028: взмах не бьёт сквозь стену, столб стен, закрытую дверь, обрыв, дерево (тем же обходом ячеек, что
        /// меч); в коридоре скал — 1-й и 2-й камень по линии (камни этой кирки прозрачны); без линии — бил бы (контроль).
        /// </summary>
        [Fact]
        public void C028_Targets_LineBlockedByWall_Door_Cliff_CorridorPasses()
        {
            WorldPos me = Center(300, 200);
            var rock = new TileCoord(302, 200);

            // Стена между — скала за ней не задета; без проверки линии была бы (контроль мутации).
            var sim = new Simulation(Flat(), maxEntities: 4);
            sim.World.SetBaseTile(new TileCoord(301, 200), FloorId.Grass, ObjectId.StoneWall);
            sim.World.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            Assert.Empty(TargetsWithLine(sim, me, rock));
            Assert.Equal(new[] { rock }, Targets(sim.World, me, rock).Tiles);

            // Столб стен x = 301, y = 198…202, скалы за ним — ни одной.
            sim = new Simulation(Flat(), maxEntities: 4);
            for (int y = 198; y <= 202; y++)
            {
                sim.World.SetBaseTile(new TileCoord(301, y), FloorId.Grass, ObjectId.StoneWall);
            }

            for (int y = 199; y <= 201; y++)
            {
                sim.World.SetBaseTile(new TileCoord(302, y), FloorId.Stone, ObjectId.Rock);
            }

            Assert.Empty(TargetsWithLine(sim, me, rock));

            // Закрытая дверь между.
            sim = new Simulation(Flat(), maxEntities: 4);
            sim.RequestEdit(new TileCoord(301, 200), FloorId.Grass, ObjectId.Door, BuildRecord.OfPlayer(1, FloorId.Grass));
            sim.Step();
            Assert.Equal(ObjectId.Door, ObjectId.IdOf(sim.World.GetObject(new TileCoord(301, 200))));
            sim.World.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            Assert.Empty(TargetsWithLine(sim, me, rock));

            // Обрыв между.
            sim = new Simulation(Flat(), maxEntities: 4);
            sim.World.SetBaseTile(new TileCoord(301, 200), FloorId.DryGrass, ObjectId.Cliff);
            sim.World.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            Assert.Empty(TargetsWithLine(sim, me, rock));

            // Дерево между (топору, не кирке) — тоже преграда.
            sim = new Simulation(Flat(), maxEntities: 4);
            sim.World.SetBaseTile(new TileCoord(301, 200), FloorId.Grass, ObjectId.Tree);
            sim.World.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            Assert.Empty(TargetsWithLine(sim, me, rock));

            // Коридор скал: 1-й и 2-й по линии — да (передняя скала прозрачна для своей кирки).
            sim = new Simulation(Flat(), maxEntities: 4);
            sim.World.SetBaseTile(new TileCoord(301, 200), FloorId.Stone, ObjectId.Rock);
            sim.World.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            Assert.Equal(new[] { new TileCoord(301, 200), rock }, TargetsWithLine(sim, me, new TileCoord(310, 200)));
        }

        /// <summary>
        /// C-029 + C-028: цель кирки — <see cref="BuildCursor.Target"/> (как у сноса и пипетки). Курсор на скале — скала, взмах;
        /// на пустой ячейке над нарисованным верхом стены — сама стена, и кирка по ней — прежний снос по времени (не взмах);
        /// курсор на траве — взмах в воздух к курсору.
        /// </summary>
        [Fact]
        public void C028_CursorTarget_RockStrikes_WallTopDemolishes()
        {
            WorldModel w = Flat();
            var rock = new TileCoord(301, 200);
            var wall = new TileCoord(305, 200);
            w.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            w.SetBaseTile(wall, FloorId.Grass, ObjectId.StoneWall);

            TileCoord onRock = BuildCursor.Target(w, w.Shape, Center(301, 200), 0, out _);
            Assert.Equal(rock, onRock);
            Assert.True(PickStrike.IsStrike(w, onRock, Items.StonePickaxe));

            // Нарисованный верх стены: южная половина тайла над стеной (305,199).
            var overWallTop = new WorldPos(305 * U + U / 2, 199 * U + U * 3 / 4);
            TileCoord onWallTop = BuildCursor.Target(w, w.Shape, overWallTop, 0, out _);
            Assert.Equal(wall, onWallTop);
            Assert.False(PickStrike.IsStrike(w, onWallTop, Items.StonePickaxe)); // постройка киркой — снос по времени (как до C-028)
            Assert.Equal(EditReply.Accepted, EditRules.CheckDig(w, Center(304, 200), onWallTop, new DigTimes(), Items.StonePickaxe));

            TileCoord onGrass = BuildCursor.Target(w, w.Shape, Center(301, 205), 0, out _);
            Assert.Equal(new TileCoord(301, 205), onGrass);
            Assert.True(PickStrike.IsStrike(w, onGrass, Items.StonePickaxe));
        }

        [Fact]
        public void C028_TileWear_Accumulates_ResetsAfterPause_AndOnObjectChange()
        {
            var wear = new TileWear(150);
            var t = new TileCoord(5, 5);
            Assert.Equal(1, wear.Hit(t, ObjectId.Rock, 3, 0));
            Assert.Equal(2, wear.Hit(t, ObjectId.Rock, 3, 150)); // ровно 150 — ещё копится
            Assert.Equal(2, wear.HitsOn(t, ObjectId.Rock));
            Assert.Equal(1, wear.Hit(t, ObjectId.Rock, 3, 301)); // пауза 151 — с нуля
            Assert.Equal(1, wear.Hit(t, ObjectId.Ore, 6, 302));  // объект сменился — с нуля
            Assert.Equal(0, wear.HitsOn(t, ObjectId.Rock));
            wear.Expire(452);
            Assert.Equal(1, wear.Count);
            wear.Expire(453);
            Assert.Equal(0, wear.Count);

            for (int i = 0; i < TileWear.Capacity + 5; i++)
            {
                wear.Hit(new TileCoord(i, 0), ObjectId.Rock, 3, 1000 + i);
            }

            Assert.Equal(TileWear.Capacity, wear.Count);
            Assert.Equal(0, wear.HitsOn(new TileCoord(0, 0), ObjectId.Rock)); // вытеснены самые старые
            Assert.Equal(1, wear.HitsOn(new TileCoord(TileWear.Capacity + 4, 0), ObjectId.Rock));
        }

        // ---------- Сеть ----------

        private static void Give(EditTests.EditRig rig, NetClient c, int cell, byte item)
        {
            var inv = rig.Sim.GetEntity(c.EntityId).Inv;
            inv.SetSlotAt(cell, item);
            Assert.True(rig.Sim.SetInventory(c.EntityId, inv));
        }

        /// <summary>Один взмах: начало, кадры до удара; ответ на запрос. Возвращает ответ.</summary>
        private static TileReplyMessage Swing(EditTests.EditRig rig, NetClient c, List<TileReplyMessage> replies, TileCoord aim)
        {
            replies.Clear();
            ushort id = c.StartDig(aim, rig.Sim.GetEntity(c.EntityId).Mover.Position);
            Assert.NotEqual(0, id);
            for (int t = 0; t < 40 && replies.All(r => r.RequestId != id); t++)
            {
                rig.Run(1);
            }

            return Assert.Single(replies, r => r.RequestId == id);
        }

        /// <summary>
        /// Каменная кирка: скала снимается третьим взмахом (первые два — «принято» без снятия, трещины у клиента 1/3, 2/3), добыча —
        /// кучей 4…8 камня; медной — вторым. Взмах — 24 кадра (12 тактов), удар своим счётом в тот же кадр, что у сервера.
        /// </summary>
        [Theory]
        [InlineData(Items.StonePickaxe, 3)]
        [InlineData(Items.CopperPickaxe, 2)]
        public void C028_Net_RockBreaksOnNthSwing_Loot4to8(byte pick, int swings)
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditTests.EditRig(world, _ => Center(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            var strikes = new List<StrikeMessage>();
            c.StrikeSeen = strikes.Add;
            rig.Run(5);
            Give(rig, c, 0, pick);
            rig.InputOf = _ => DigPick;
            rig.Run(6);
            Assert.Equal(pick, c.HeldItem);

            for (int i = 1; i <= swings; i++)
            {
                long ticks0 = rig.Ticks;
                TileReplyMessage r = Swing(rig, c, replies, rock);
                Assert.Equal(EditReply.Accepted, r.Reply);
                Assert.InRange(rig.Ticks - ticks0, 12, 13); // 24 кадра
                StrikeMessage s = strikes.Last();
                Assert.Equal(c.EntityId, s.Striker);
                Assert.Equal(r.RequestId, s.RequestId);
                Assert.Equal(1, s.Count);
                Assert.Equal(rock, s.T0.Tile);
                Assert.Equal((byte)i, s.T0.Hits);
                Assert.Equal((byte)swings, s.T0.Needed);
                if (i < swings)
                {
                    Assert.Equal(StrikeMessage.ReplySwingBit, r.Mask);
                    Assert.False(s.T0.Broken);
                    Assert.Equal(ObjectId.Rock, rig.Sim.World.GetObject(rock));
                    Assert.Equal(i / (float)swings, c.WearOf(rock), 3);
                    Assert.Equal(i, rig.Server.Wear.HitsOn(rock, ObjectId.Rock));
                }
                else
                {
                    Assert.Equal(StrikeMessage.ReplySwingBit | 1u, r.Mask);
                    Assert.True(s.T0.Broken);
                    Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(rock));
                }
            }

            rig.Run(3);
            Assert.Equal(ObjectId.Empty, c.World!.World.GetObject(rock));
            Assert.Equal(0f, c.WearOf(rock));
            int expected = DigLoot.DropFor(ObjectId.Rock, rock.X, rock.Y, world.Seed, out _);
            Assert.InRange(expected, 4, 8);
            rig.Run(Simulation.LootPickupDelayTicks + 6); // стоящий рядом подобрал кучу
            Assert.Equal(expected, rig.Sim.GetEntity(c.EntityId).Inv.CountAt(Material.Stone));
            Assert.Equal(swings, c.StrikesPredicted);
            Assert.Equal(0, c.World.PredictionsRolledBack);
        }

        /// <summary>
        /// Один взмах — три скалы стены (прицел и две по дуге): удар каждой; наблюдатель рядом получает «взмах» и видит те же
        /// трещины; третий взмах снимает все три разом (маска 0b111).
        /// </summary>
        [Fact]
        public void C028_Net_OneSwing_ThreeRocks_ObserverSeesWear_ThirdBreaksAll()
        {
            WorldModel world = Flat();
            TileCoord[] face = { new TileCoord(301, 199), new TileCoord(301, 200), new TileCoord(301, 201) };
            foreach (TileCoord t in face)
            {
                world.SetBaseTile(t, FloorId.Stone, ObjectId.Rock);
            }

            using var rig = new EditTests.EditRig(world, n => n == 0 ? Center(300, 200) : Center(296, 200));
            NetClient c = rig.Join();
            NetClient obs = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(5);
            rig.InputOf = k => k == c ? DigPick : InputFrame.None;
            rig.Run(4);

            TileReplyMessage r = Swing(rig, c, replies, face[1]);
            Assert.Equal(StrikeMessage.ReplySwingBit, r.Mask);
            rig.Run(1);
            foreach (TileCoord t in face)
            {
                Assert.Equal(1, rig.Server.Wear.HitsOn(t, ObjectId.Rock));
                Assert.Equal(1f / 3f, obs.WearOf(t), 3);
                Assert.Equal(1f / 3f, c.WearOf(t), 3);
            }

            Assert.Equal(1, obs.StrikesSeen);
            Swing(rig, c, replies, face[1]);
            r = Swing(rig, c, replies, face[1]);
            Assert.Equal(StrikeMessage.ReplySwingBit | 0b111u, r.Mask);
            rig.Run(3);
            foreach (TileCoord t in face)
            {
                Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(t));
                Assert.Equal(ObjectId.Empty, obs.World!.World.GetObject(t));
            }
        }

        /// <summary>Два удара, пауза 5 с без ударов — сброс: третий удар снова первый (скала стоит); у клиента трещины тоже гаснут.</summary>
        [Fact]
        public void C028_Net_WearResetsAfter5s()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditTests.EditRig(world, _ => Center(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(5);
            rig.InputOf = _ => DigPick;
            rig.Run(4);
            Swing(rig, c, replies, rock);
            Swing(rig, c, replies, rock);
            Assert.Equal(2, rig.Server.Wear.HitsOn(rock, ObjectId.Rock));
            rig.InputOf = _ => InputFrame.None;
            rig.Run(TileWear.ServerResetTicks + 2);
            Assert.Equal(0, rig.Server.Wear.HitsOn(rock, ObjectId.Rock));
            Assert.Equal(0f, c.WearOf(rock));
            rig.InputOf = _ => DigPick;
            TileReplyMessage r = Swing(rig, c, replies, rock);
            Assert.Equal(StrikeMessage.ReplySwingBit, r.Mask);
            Assert.Equal(1, rig.Server.Wear.HitsOn(rock, ObjectId.Rock));
            Assert.Equal(ObjectId.Rock, rig.Sim.World.GetObject(rock));
        }

        /// <summary>Удары общие: два взмаха первого игрока и один второго — скалу снимает второй (ответ ему с маской), добыча у скалы.</summary>
        [Fact]
        public void C028_Net_TwoPlayers_ShareWear_LastHitterBreaks()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditTests.EditRig(world, n => n == 0 ? Center(300, 200) : Center(302, 200));
            NetClient a = rig.Join();
            NetClient b = rig.Join();
            var ra = new List<TileReplyMessage>();
            var rb = new List<TileReplyMessage>();
            a.EditReplied = ra.Add;
            b.EditReplied = rb.Add;
            rig.Run(5);
            rig.InputOf = _ => DigPick;
            rig.Run(4);
            Assert.Equal(StrikeMessage.ReplySwingBit, Swing(rig, a, ra, rock).Mask);
            Assert.Equal(StrikeMessage.ReplySwingBit, Swing(rig, a, ra, rock).Mask);
            Assert.Equal(2f / 3f, b.WearOf(rock), 3); // чужие удары видны
            Assert.Equal(StrikeMessage.ReplySwingBit | 1u, Swing(rig, b, rb, rock).Mask);
            Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(rock));
        }

        /// <summary>Сервер: стена между персонажем и скалой — взмах «принят», скала за стеной не получает удара и стоит.</summary>
        [Fact]
        public void C028_Net_WallBetween_RockBehindNotHit()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(302, 200);
            world.SetBaseTile(new TileCoord(301, 200), FloorId.Grass, ObjectId.StoneWall);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditTests.EditRig(world, _ => Center(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            var strikes = new List<StrikeMessage>();
            c.StrikeSeen = strikes.Add;
            rig.Run(5);
            rig.InputOf = _ => DigPick;
            rig.Run(4);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(StrikeMessage.ReplySwingBit, Swing(rig, c, replies, rock).Mask);
            }

            Assert.All(strikes, m => Assert.Equal(0, m.Count));
            Assert.Equal(0, rig.Server.Wear.HitsOn(rock, ObjectId.Rock));
            Assert.Equal(ObjectId.Rock, rig.Sim.World.GetObject(rock));
        }

        /// <summary>«Взмах» (39) — ударившему и игрокам в 56 т; игрок в 60 т его не получает.</summary>
        [Fact]
        public void C028_Net_StrikeBroadcast_Within56Tiles_Only()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditTests.EditRig(world, n => n == 0 ? Center(300, 200) : n == 1 ? Center(350, 200) : Center(360, 200));
            NetClient c = rig.Join();
            NetClient near = rig.Join();
            NetClient far = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(5);
            rig.InputOf = k => k == c ? DigPick : InputFrame.None;
            rig.Run(4);
            Swing(rig, c, replies, rock);
            rig.Run(2);
            Assert.Equal(1, c.StrikesSeen);
            Assert.Equal(1, near.StrikesSeen);  // 50 т
            Assert.Equal(0, far.StrikesSeen);   // 60 т
            Assert.Equal(0f, far.WearOf(rock));
        }

        /// <summary>
        /// Двое добивают одну скалу в одном такте (удары 2/3, оба взмаха начаты в одном кадре): правка одна, «снят» — у одного
        /// ударившего (бит маски и флаг во «взмахе»), камень выдан один раз.
        /// </summary>
        [Fact]
        public void C028_Net_TwoFinishSameTick_BrokenOnlyOnce()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditTests.EditRig(world, n => n == 0 ? Center(300, 200) : Center(302, 200));
            NetClient a = rig.Join();
            NetClient b = rig.Join();
            var ra = new List<TileReplyMessage>();
            var rb = new List<TileReplyMessage>();
            a.EditReplied = ra.Add;
            b.EditReplied = rb.Add;
            var seen = new List<StrikeMessage>();
            a.StrikeSeen = seen.Add;
            rig.Run(5);
            rig.InputOf = _ => DigPick;
            rig.Run(4);
            Swing(rig, a, ra, rock);
            Swing(rig, a, ra, rock);
            Assert.Equal(2, rig.Server.Wear.HitsOn(rock, ObjectId.Rock));

            ra.Clear();
            seen.Clear();
            a.StartDig(rock, rig.Sim.GetEntity(a.EntityId).Mover.Position);
            b.StartDig(rock, rig.Sim.GetEntity(b.EntityId).Mover.Position);
            rig.Run(16);
            TileReplyMessage qa = Assert.Single(ra);
            TileReplyMessage qb = Assert.Single(rb);
            Assert.Equal(1, ((qa.Mask & 1u) != 0 ? 1 : 0) + ((qb.Mask & 1u) != 0 ? 1 : 0));
            Assert.Equal(2, seen.Count);
            Assert.Equal(1, seen.Count(m => m.Count == 1 && m.T0.Broken));
            Assert.Equal(1, rig.Server.StrikeTilesBroken);
            Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(rock));
            int expected = DigLoot.DropFor(ObjectId.Rock, rock.X, rock.Y, world.Seed, out _);
            Assert.Equal(expected, rig.Sim.Piles.ToArray().Where(p => p.Item == Items.Stone).Sum(p => p.Count));
        }

        /// <summary>Кирка по дереву в досягаемости — «нужен топор»; по граниту каменной — «нужна кирка: медная»; взмах в воздух — «принято».</summary>
        [Fact]
        public void C028_Net_Refusals_Tree_Granite_AirSwingAccepted()
        {
            WorldModel world = Flat();
            var tree = new TileCoord(301, 200);
            var granite = new TileCoord(299, 200);
            world.SetBaseTile(tree, FloorId.Grass, ObjectId.Tree);
            world.SetBaseTile(granite, FloorId.Stone, ObjectId.Granite);
            using var rig = new EditTests.EditRig(world, _ => Center(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(5);
            rig.InputOf = _ => DigPick;
            rig.Run(4);
            Assert.Equal(EditReply.ToolRequired, Swing(rig, c, replies, tree).Reply);
            Assert.Equal(EditReply.TierRequired, Swing(rig, c, replies, granite).Reply);
            TileReplyMessage air = Swing(rig, c, replies, new TileCoord(300, 203));
            Assert.Equal(EditReply.Accepted, air.Reply);
            Assert.Equal(StrikeMessage.ReplySwingBit, air.Mask);
            Assert.Equal(ObjectId.Tree, rig.Sim.World.GetObject(tree));
            Assert.Equal(ObjectId.Granite, rig.Sim.World.GetObject(granite));
            Assert.Equal(0, rig.Server.Wear.Count);
        }

        /// <summary>
        /// Взмах доигрывается: отпустил кнопку (StopDig) в середине — удар всё равно в конце, ответ «принято»; следующий взмах можно
        /// начать сразу после своего удара, не дожидаясь ответа (цепочка без пауз).
        /// </summary>
        [Fact]
        public void C028_Net_SwingCommits_AndChainsWithoutWaiting()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditTests.EditRig(world, _ => Center(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(5);
            rig.InputOf = _ => DigPick;
            rig.Run(4);

            c.StartDig(rock, rig.Sim.GetEntity(c.EntityId).Mover.Position);
            rig.Run(3);
            c.StopDig();
            Assert.True(c.Digging);
            rig.InputOf = _ => c.Digging ? DigPick : new InputFrame(Input8.None, 0, 0, 0);
            rig.Run(12);
            Assert.Equal(EditReply.Accepted, Assert.Single(replies).Reply);
            Assert.Equal(1, rig.Server.Wear.HitsOn(rock, ObjectId.Rock));

            // Цепочка: следующий взмах — в кадр после своего удара; два удара за ≤ 25 тактов.
            replies.Clear();
            rig.InputOf = _ => DigPick;
            int started = 0;
            rig.AfterClientPoll = cl =>
            {
                if (cl == c && started < 2 && (!c.Digging || c.DigPredicted))
                {
                    c.StartDig(rock, rig.Sim.GetEntity(c.EntityId).Mover.Position);
                    started++;
                }
            };
            rig.Run(26);
            rig.AfterClientPoll = null;
            Assert.Equal(2, replies.Count(r => r.Reply == EditReply.Accepted));
            Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(rock)); // 1 + 2 = 3 удара
        }

        /// <summary>
        /// Анимация: взмах кирки горизонтальный, как меч, — головка (рука + 45°) идёт дугой вокруг прицела от +75° до −75° при
        /// любом угле прицела (угол руки следует прицелу целиком), а не пробивом сверху вниз с частью угла цели.
        /// </summary>
        [Theory]
        [InlineData(0f)]
        [InlineData(60f)]
        [InlineData(-70f)]
        public void C028_Anim_PickSwing_ArcAroundAim(float aimDeg)
        {
            Assert.Equal(ActionKind.Mine, ActionAnim.DigActionOf(Items.StonePickaxe));
            float follow = ActionAnim.AimFollowOf(ActionKind.Mine);
            float top = ActionAnim.ToolSwing(ActionKind.Mine, ActionAnim.StrikeShare, false).HandDeg + aimDeg * follow + 45f;
            float hit = ActionAnim.ToolSwing(ActionKind.Mine, 1f, false).HandDeg + aimDeg * follow + 45f;
            Assert.Equal(aimDeg + 75f, top, 2);
            Assert.Equal(aimDeg - 75f, hit, 2);
            Assert.Equal(ActionAnim.AimFollow, ActionAnim.AimFollowOf(ActionKind.Chop)); // топор не менялся
            Assert.Equal(1, ActionAnim.BeatsFor(PickStrike.SwingFrames)); // один удар на взмах — в кадр удара ядра
        }

        [Fact]
        public void C028_Codec_GoldenBytes_Strike()
        {
            Assert.Equal(35, NetModule.ProtocolVersion); // E3-6: 35 (надетое тремя байтами в сумке 26) // C-028: 34 (C-031 занял 32, C-030 — 33)
            Assert.Equal(39, (int)MessageType.Strike);
            var m = new StrikeMessage { Striker = 0x01020304, RequestId = 0x0506, Count = 1 };
            m.SetTile(0, new StrikeTile { Tile = new TileCoord(0x0A0B, 0x0C0D), Obj = ObjectId.Rock, Hits = 2, Needed = 3, Broken = true });
            var buf = new byte[64];
            int n = m.Write(buf);
            Assert.Equal(StrikeMessage.HeaderBytes + StrikeMessage.TileBytes, n);
            Assert.Equal(new byte[]
            {
                39, 0x04, 0x03, 0x02, 0x01, 0x06, 0x05, 1,
                0x0B, 0x0A, 0, 0, 0x0D, 0x0C, 0, 0, 21, 0, 2, 3, 1,
            }, buf.Take(n).ToArray());
            StrikeMessage back = StrikeMessage.Read(new ReadOnlySpan<byte>(buf, 0, n));
            Assert.Equal(m.ToString(), back.ToString());
            Assert.True(back.T0.Broken);

            var empty = new StrikeMessage { Striker = 7, RequestId = 1 };
            Assert.Equal(StrikeMessage.HeaderBytes, empty.Write(buf));
            Assert.Equal(0, StrikeMessage.Read(new ReadOnlySpan<byte>(buf, 0, StrikeMessage.HeaderBytes)).Count);
            m.Write(buf);
            buf[20] = 2; // флаги
            Assert.Throws<ProtocolException>(() => StrikeMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));
            m.Count = 4;
            Assert.Throws<InvalidOperationException>(() => m.Write(buf));
        }
    }
}
