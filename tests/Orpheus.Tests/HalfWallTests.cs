using System;
using System.Collections.Generic;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Save;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-12 — стена в полтайла (решение Автора 15.11 «мелкая сетка, в 2 раза меньше»; записка
    /// <c>work/design/half_tile_walls.md</c>): ячейки 0,5 × 0,5 слоя построек, столкновения круга 0,4 со стенами 0,5, путь
    /// врагов по тайлам (тайл с ячейкой непроходим), правило стен `02` 9, линия удара по ячейке, дверь — пара ячеек,
    /// постановка ячейками по сети, перенос старых миров (настоящий файл формата 9), детерминизм.
    /// </summary>
    public class HalfWallTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const int Half = U / 2;
        private const int R = Collision.RadiusUnits;

        private static WorldModel Flat(ulong seed = 1212) => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), seed);

        private static WorldPos At(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        private static void Cells(Simulation sim, int x, int y, ushort obj, byte mask, bool open = false)
        {
            BuildRecord rec = BuildRecord.OfPlayer(1, FloorId.Grass).WithCells(mask);
            if (open)
            {
                rec = rec.WithFlags(BuildFlags.DoorOpen | BuildFlags.DoorKeepOpen); // «держать открытой»: иначе закроется сама через 1 с
            }

            Assert.True(sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, obj, rec));
        }

        private static void Steps(Simulation sim, int n)
        {
            for (int i = 0; i < n; i++)
            {
                sim.Step();
            }
        }

        private static TileCoord TileOf(Simulation sim, int id) =>
            Torus.TileOf(sim.World.Shape, sim.Entities[sim.SlotOf(id)].Mover.Position);

        private static int Hp(Simulation sim, int id) => sim.GetEntity(id).Health;

        // Круг шагает по 20 unit с упором (тот же код, что у сервера, предсказания и ботов: Collision + Simulation.Solid).
        private static WorldPos Push(Simulation sim, WorldPos p, int dx, int dy, int steps, Func<WorldPos, bool> ok)
        {
            for (int i = 0; i < steps; i++)
            {
                p = Collision.Move(sim.Shape, p, dx, dy, sim.Solid);
                Assert.True(ok(p), $"шаг {i}: круг в {p.X},{p.Y} прошёл стену");
            }

            return p;
        }

        // ------------------------------------------------ столкновения

        /// <summary>
        /// Стена в ячейку толщиной (нижние ячейки ряда 20) непроходима прямо и по диагонали; верхняя половина тайла со стеной
        /// свободна — круг встаёт вплотную к грани стены (y = 20,5 − 0,4).
        /// </summary>
        [Fact]
        public void E212_ThinWall_Player_Blocked_Straight_And_Diagonal_HalfTileFree()
        {
            var sim = new Simulation(Flat());
            for (int x = 10; x <= 30; x++)
            {
                Cells(sim, x, 20, ObjectId.StoneWall, BuildCells.DoorSouth);
            }

            sim.Step();
            int face = 20 * U + Half; // верхняя грань стены
            WorldPos p = Push(sim, At(20, 17), 0, 20, 80, q => q.Y + R <= face);
            Assert.Equal(face - R, p.Y); // вплотную: центр в тайле 20 — его верхняя половина свободна
            Assert.Equal(20, p.Y / U);
            Push(sim, At(14, 17), 20, 20, 120, q => q.Y + R <= face);
            Push(sim, At(26, 17), -20, 20, 120, q => q.Y + R <= face);
            Assert.True(sim.IsBlocked(sim.Shape, new TileCoord(20, 20)));
            Assert.Equal(BuildCells.DoorSouth, sim.SolidCells(sim.Shape, new TileCoord(20, 20)));
        }

        /// <summary>Диагональная стена (Брезенхэм по ячейкам: ячейки касаются углами) — щель нулевая, круг не проходит.</summary>
        [Fact]
        public void E212_DiagonalCellWall_NoGap()
        {
            var sim = new Simulation(Flat());
            Span<CellGroup> groups = stackalloc CellGroup[EditRules.MaxLineTiles];
            int n = BuildCells.Line(sim.Shape, new TileCoord(80, 80), new TileCoord(111, 111), groups);
            Assert.Equal(16, n); // 32 ячейки по диагонали — по 2 в тайле
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(BuildCells.Bit(0, 0) | BuildCells.Bit(1, 1), groups[i].Mask);
                Cells(sim, groups[i].Tile.X, groups[i].Tile.Y, ObjectId.StoneWall, groups[i].Mask);
            }

            sim.Step();
            // Сверху-справа от диагонали (x > y) толкаем к ней поперёк (на юго-запад) и вдоль-поперёк — x − y остаётся > 0.
            WorldPos start = new WorldPos(48 * U, 44 * U);
            Push(sim, start, -20, 20, 200, q => q.X - q.Y > 0);
            Push(sim, start, -20, 0, 200, q => q.X - q.Y > 0);
            Push(sim, start, 0, 20, 200, q => q.X - q.Y > 0);
        }

        /// <summary>
        /// Стык углов: угол Г из верхней и левой тонких стен — угловой тайл 3 ячейки (7) или только диагональные (НВ + ЮЗ, 6):
        /// круг изнутри, толкаемый в угол, наружу не выходит.
        /// </summary>
        [Theory]
        [InlineData(7)]
        [InlineData(6)]
        public void E212_CornerJoint_NoGap(int corner)
        {
            var sim = new Simulation(Flat());
            for (int i = 31; i <= 36; i++)
            {
                Cells(sim, i, 30, ObjectId.WoodWall, BuildCells.DoorNorth);
                Cells(sim, 30, i, ObjectId.WoodWall, BuildCells.DoorWest);
            }

            Cells(sim, 30, 30, ObjectId.WoodWall, (byte)corner);
            sim.Step();
            int edge = 30 * U + Half;
            Push(sim, At(33, 33), -20, -20, 200, q => q.X - R >= edge || q.Y - R >= edge);
            Push(sim, At(31, 33), -6, -20, 300, q => q.X - R >= edge || q.Y - R >= edge);
            Push(sim, At(33, 31), -20, -6, 300, q => q.X - R >= edge || q.Y - R >= edge);
        }

        /// <summary>
        /// Проход между стенами: щель в 1 ячейку (0,5) круг 0,8 не проходит; в 2 ячейки (1,0), сдвинутая на полтайла
        /// (ячейки из двух соседних тайлов), — проходит.
        /// </summary>
        [Fact]
        public void E212_Gap_HalfTile_Blocks_OneTile_Passes()
        {
            var sim = new Simulation(Flat());
            // Ряд 40: щель в одну ячейку — ячейка 2·50+1 (восточная половина тайла 50).
            for (int x = 44; x <= 56; x++)
            {
                Cells(sim, x, 40, ObjectId.StoneWall, x == 50 ? BuildCells.Bit(0, 1) : BuildCells.DoorSouth);
            }

            // Ряд 60: щель в две ячейки — восточная половина тайла 50 и западная тайла 51.
            for (int x = 44; x <= 56; x++)
            {
                byte m = x == 50 ? BuildCells.Bit(0, 1) : x == 51 ? BuildCells.Bit(1, 1) : BuildCells.DoorSouth;
                Cells(sim, x, 60, ObjectId.StoneWall, m);
            }

            sim.Step();
            int face40 = 40 * U + Half;
            Push(sim, new WorldPos(50 * U + Half + Half / 2, 37 * U), 0, 20, 100, q => q.Y < face40); // круг вдавливается между углами, но не проходит
            WorldPos p = new WorldPos(51 * U, 57 * U); // на середине щели (граница тайлов 50 и 51)
            for (int i = 0; i < 100; i++)
            {
                p = Collision.Move(sim.Shape, p, 0, 20, sim.Solid);
            }

            Assert.True(p.Y > 61 * U, $"круг 0,8 прошёл щель 1,0 (y = {p.Y})");
            // Риск записки: путь врагов по тайлам считает оба тайла щели непроходимыми.
            var terrain = new SimulationPathTerrain(sim);
            terrain.BeginSearch();
            Assert.Equal(PathCost.Blocked, terrain.CostOf(new TileCoord(50, 60), PathMode.Walker));
            Assert.Equal(PathCost.Blocked, terrain.CostOf(new TileCoord(51, 60), PathMode.Walker));
            Assert.Equal(PathCost.Normal, terrain.CostOf(new TileCoord(50, 59), PathMode.Walker));
        }

        // ------------------------------------------------ правило стен (`02` 9) и путь врагов

        private static void ThinRoom(Simulation sim, int kind, bool doorOpen = false)
        {
            // Периметр 21 × 21 (тайлы 20…40): тонкая стена на внешних половинах тайлов, углы — 3 ячейки; дверь на западе (20, 30).
            for (int y = 20; y <= 40; y++)
            {
                for (int x = 20; x <= 40; x++)
                {
                    byte mask = 0;
                    mask |= y == 20 ? BuildCells.DoorNorth : (byte)0;
                    mask |= y == 40 ? BuildCells.DoorSouth : (byte)0;
                    mask |= x == 20 ? BuildCells.DoorWest : (byte)0;
                    mask |= x == 40 ? BuildCells.DoorEast : (byte)0;
                    if (mask == 0)
                    {
                        continue;
                    }

                    if (kind == 1 && x == 20 && y == 30)
                    {
                        Cells(sim, x, y, ObjectId.Door, BuildCells.DoorWest, doorOpen);
                    }
                    else
                    {
                        Cells(sim, x, y, kind == 2 ? ObjectId.WoodFence : ObjectId.WoodWall, mask);
                    }
                }
            }
        }

        /// <summary>
        /// Приёмки 1.5–1.6 `08` на тонких стенах: комната 21 × 21 из тонкой стены / тонкой стены с закрытой дверью / тонкого
        /// забора, враг снаружи, 1000 тактов — враг ни разу не внутри (центр западнее грани стены), игрок цел.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void E212_ThinBarrier_Enemy_NoEntry_1000Ticks(int kind)
        {
            var sim = new Simulation(Flat(1301));
            int ch = sim.AddEntity(At(30, 30));
            ThinRoom(sim, kind);
            sim.Step();
            int rogue = sim.AddMeleeRogue(At(17, 30));
            for (int i = 0; i < 1000; i++)
            {
                sim.Step();
                WorldPos p = sim.GetEntity(rogue).Mover.Position;
                Assert.True(p.X + R <= 20 * U, $"такт {i}: враг прошёл тонкую преграду ({p.X}, {p.Y})");
            }

            Assert.Equal(100, Hp(sim, ch));
        }

        /// <summary>Открытая тонкая дверь (пара ячеек, проём 1 тайл) — враг находит её и входит.</summary>
        [Fact]
        public void E212_ThinRoom_OpenDoor_EnemyFindsIt_Enters()
        {
            var sim = new Simulation(Flat(1302));
            int ch = sim.AddEntity(At(30, 30));
            ThinRoom(sim, 1, doorOpen: true);
            sim.Step();
            // Напротив двери. Подход наискосок (из (17, 24)) застревает у косяка проёма в 1 тайл — и с полными стенами до E2-12:
            // дефект движения врага к узлу пути, не тонких стен (backlog [бой], E2-12).
            int rogue = sim.AddMeleeRogue(At(17, 30));
            int entered = -1;
            for (int i = 0; i < 900 && entered < 0; i++)
            {
                sim.Step();
                if (TileOf(sim, rogue).X >= 22)
                {
                    entered = i;
                }
            }

            Assert.True(entered >= 0, $"враг не вошёл в открытую дверь за 900 тактов (тайл {TileOf(sim, rogue)})");
            Assert.True(sim.GetEntity(rogue).Mover.Position.Y / U is >= 21 and <= 39);
        }

        // ------------------------------------------------ линия удара (C-001/C-002) по ячейкам

        /// <summary>
        /// Линия удара (`02` 3.4, 9.3): тонкая стена и закрытая тонкая дверь между врагом и игроком — удара нет; тонкий забор
        /// прозрачен; стена в западной половине тайла линию по восточной половине не режет (точка — по ячейке).
        /// </summary>
        [Theory]
        [InlineData(0, false)] // стена (верхние ячейки ряда 11)
        [InlineData(1, false)] // закрытая дверь (верхние ячейки)
        [InlineData(2, true)]  // забор — прозрачен
        [InlineData(3, true)]  // стена только в западной половине: линия идёт по восточной
        public void E212_StrikeLine_ThinWall_Door_Fence(int kind, bool hits)
        {
            var sim = new Simulation(Flat(1307));
            int ch = sim.AddEntity(new WorldPos(10 * U + U / 2 + 40, 10 * U + 150)); // не задевает тайл двери — дверь сама не открывается
            switch (kind)
            {
                case 0: Cells(sim, 10, 11, ObjectId.StoneWall, BuildCells.DoorNorth); break;
                case 1: Cells(sim, 10, 11, ObjectId.Door, BuildCells.DoorNorth); break;
                case 2: Cells(sim, 10, 11, ObjectId.WoodFence, BuildCells.DoorNorth); break;
                default: Cells(sim, 10, 11, ObjectId.StoneWall, BuildCells.DoorWest); break;
            }

            sim.Step();
            sim.AddMeleeRogue(new WorldPos(10 * U + U / 2 + 40, 12 * U + 40));
            Steps(sim, 300);
            Assert.Equal(hits, Hp(sim, ch) < 100);
        }

        // ------------------------------------------------ постановка ячейками по сети

        /// <summary>
        /// Постановка по сети (протокол 26): линия ячеек одной заявкой, тайл — одна правка (маска растёт, прочность тайла
        /// сохраняется); дверь — пара ячеек вдоль стены (по соседям); другой вид в тайле и дверь в занятый тайл — «занято».
        /// Предсказание клиента = мир сервера.
        /// </summary>
        [Fact]
        public void E212_Net_PlaceCells_Line_Door_Orientation_Busy()
        {
            WorldModel world = Flat();
            using var rig = new EditTests.EditRig(world, _ => At(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(5);
            var inv = rig.Sim.GetEntity(c.EntityId).Inv;
            inv.SetCell(3, Items.StoneWall, 20);
            inv.SetCell(4, Items.WoodWall, 5);
            inv.SetCell(5, Items.Door, 1);
            Assert.True(rig.Sim.SetInventory(c.EntityId, inv));
            rig.Run(3);
            WorldPos self = rig.Sim.GetEntity(c.EntityId).Mover.Position;
            var hand = new BuildHand(c);

            // Нижние ячейки тайлов 298…303 ряда 203, кроме тайла 300 (там будет дверь): две линии.
            Assert.NotEqual(0, hand.PlaceLine(new TileCoord(298, 203), new TileCoord(299, 203), self, c.Inv, 3, 2, 3));
            Assert.NotEqual(0, hand.PlaceLine(new TileCoord(301, 203), new TileCoord(303, 203), self, c.Inv, 3, 2, 3));
            rig.Run(4);
            Assert.Equal(0xFu, replies[0].Mask);
            Assert.Equal(0x3Fu, replies[1].Mask);
            Assert.Equal(10, 20 - rig.Sim.GetEntity(c.EntityId).Inv.CountIn(3));

            // Дверь: курсор в верхней половине, но стены слева и справа — в нижней: дверь встаёт нижней парой (вдоль стены).
            BuildCheck door = BuildHand.Check(c.World!.World, self, c.Inv, 5, true, new TileCoord(300, 203), 0);
            Assert.True(door.Ok, door.Reason);
            Assert.Equal(BuildCells.DoorSouth, door.Cells);
            Assert.True(hand.Click(c.World.World, self, c.Inv, 5, true, new TileCoord(300, 203), 0).Ok);
            rig.Run(4);
            Assert.Equal(ObjectId.Door, rig.Sim.World.GetObject(new TileCoord(300, 203)));
            Assert.Equal(BuildCells.DoorSouth, BuildCells.MaskAt(rig.Sim.World, new TileCoord(300, 203)));

            // Добавить ячейку в тайл со стеной: маска растёт, прочность повреждённого тайла та же.
            var t = new TileCoord(298, 203);
            Assert.True(rig.Sim.DamageBuilding(t, 100));
            rig.Run(400);
            ushort integrity = Integrity(rig.Sim, t);
            Assert.True(hand.Click(c.World.World, self, c.Inv, 3, true, t, 0).Ok);
            rig.Run(4);
            Assert.Equal((byte)(BuildCells.DoorSouth | BuildCells.Bit(0, 0)), BuildCells.MaskAt(rig.Sim.World, t));
            Assert.Equal(integrity, Integrity(rig.Sim, t));

            // Другой вид в тайле — «занято»; ячейка уже занята — «занято»; дверь в тайл со стеной — «занято».
            Assert.Equal(EditReply.Busy, BuildHand.Check(c.World.World, self, c.Inv, 4, true, t, 1).Reply);
            Assert.Equal(EditReply.Busy, BuildHand.Check(c.World.World, self, c.Inv, 3, true, t, 2).Reply);
            Assert.Equal(EditReply.Busy, BuildCells.CheckPlace(c.World.World, self, t, ObjectId.Door, BuildCells.DoorNorth, out _, out _));

            for (int x = 296; x <= 305; x++)
            {
                var tile = new TileCoord(x, 203);
                Assert.Equal(rig.Sim.World.GetObject(tile), c.World.World.GetObject(tile));
                Assert.Equal(BuildCells.MaskAt(rig.Sim.World, tile), BuildCells.MaskAt(c.World.World, tile));
            }
        }

        private static ushort Integrity(Simulation sim, TileCoord t)
        {
            Assert.True(sim.World.TryGetBuild(t, out BuildRecord rec));
            return rec.Integrity;
        }

        /// <summary>Снос тайла стены (тайл целиком): по предмету на ячейку, дверь — один (записка: снос и прочность).</summary>
        [Fact]
        public void E212_Dismantle_RefundPerCell_DoorOne()
        {
            var sim = new Simulation(Flat());
            int me = sim.AddEntity(At(100, 100));
            Cells(sim, 101, 100, ObjectId.StoneWall, 7);
            Cells(sim, 99, 100, ObjectId.Door, BuildCells.DoorEast);
            sim.Step();
            Assert.True(sim.RequestPlayerEdit(new TileCoord(101, 100), FloorId.Grass, ObjectId.Empty, false, default, me, 0));
            Assert.True(sim.RequestPlayerEdit(new TileCoord(99, 100), FloorId.Grass, ObjectId.Empty, false, default, me, 0));
            sim.Step();
            int walls = 0, doors = 0;
            foreach (GroundPile p in sim.Piles)
            {
                walls += p.Item == Items.StoneWall ? p.Count : 0;
                doors += p.Item == Items.Door ? p.Count : 0;
            }

            Assert.Equal(3, walls);
            Assert.Equal(1, doors);
        }

        // ------------------------------------------------ перенос старого мира

        /// <summary>
        /// Настоящий файл формата 9 (записан кодом task/E2-11 e84ebaa, генератор <c>Golden/format9_walls.make.cs.txt</c>):
        /// стены, заборы и дверь читаются сплошным тайлом из 4 ячеек (маски в записи нет), хэш состояния тот же, что у
        /// генератора; круг в тайл стены не входит; снос стены и забора — 4 предмета, двери — 1; прочность сохранена.
        /// </summary>
        [Fact]
        public void E212_Save_Format9_RealFile_WallsBecomeFullTiles()
        {
            byte[] v9 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format9_walls.save"));
            byte[] baseBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format9_walls.base"));
            SaveFile f = SaveContainer.Read(v9);
            Assert.True(f.IsOk, f.Error);
            Assert.Equal(9u, f.Header.FormatVersion);
            Simulation sim = SaveFixtures.Load(baseBytes, v9);
            Assert.Equal("7199a6b6903646f0", StateHash.Compute(sim).Total.ToString("x16")); // хэш генератора (код E2-11)

            var cases = new List<(TileCoord Tile, ushort Obj, byte Item, int Refund)>();
            for (int i = 0; i < 3; i++)
            {
                cases.Add((new TileCoord(125, 127 + i), ObjectId.WoodWall, Items.WoodWall, 4));
                cases.Add((new TileCoord(123, 127 + i), ObjectId.StoneWall, Items.StoneWall, 4));
                cases.Add((new TileCoord(131, 127 + i), ObjectId.WoodFence, Items.WoodFence, 4));
            }

            cases.Add((new TileCoord(128, 131), ObjectId.Door, Items.Door, 1));
            foreach (var k in cases)
            {
                Assert.Equal(k.Obj, ObjectId.IdOf(sim.World.GetObject(k.Tile)));
                Assert.True(sim.World.TryGetBuild(k.Tile, out BuildRecord rec));
                Assert.Equal(0, rec.Cells);
                Assert.Equal(FloorId.Grass, rec.NaturalFloor);
                Assert.Equal(BuildCells.Full, BuildCells.MaskAt(sim.World, k.Tile));
                Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, k.Tile));
                Assert.True(Collision.TouchesSolid(sim.Shape, new WorldPos(k.Tile.X * U + U - 10, k.Tile.Y * U + U - 10), sim.Solid));
            }

            Assert.Equal(150, Integrity(sim, new TileCoord(123, 129)));
            int id = sim.Entities[0].Id;
            foreach (var k in cases)
            {
                int before = PileCount(sim, k.Item) + sim.Entities[0].Inv.Count(k.Item);
                Assert.True(sim.RequestPlayerEdit(k.Tile, FloorId.Grass, ObjectId.Empty, false, default, id, 0));
                sim.Step();
                Assert.Equal(ObjectId.Empty, sim.World.GetObject(k.Tile));
                Assert.Equal(before + k.Refund, PileCount(sim, k.Item) + sim.Entities[0].Inv.Count(k.Item));
            }
        }

        private static int PileCount(Simulation sim, byte item)
        {
            int n = 0;
            foreach (GroundPile p in sim.Piles)
            {
                n += p.Item == item ? p.Count : 0;
            }

            return n;
        }

        // ------------------------------------------------ детерминизм

        /// <summary>Два одинаковых мира с тонкими стенами, дверью и врагом — хэш состояния совпадает такт в такт.</summary>
        [Fact]
        public void E212_Determinism_ThinWalls_SameHash()
        {
            ulong Run()
            {
                var sim = new Simulation(Flat(1309));
                sim.AddEntity(At(30, 30));
                ThinRoom(sim, 1, doorOpen: true);
                sim.Step();
                sim.AddMeleeRogue(At(17, 24));
                Steps(sim, 600);
                return StateHash.Compute(sim).Total;
            }

            Assert.Equal(Run(), Run());
        }
    }
}
