using System;
using System.Collections.Generic;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Save;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-011 — хвосты E2-15/E2-16: флаг Wide сверяется при чтении, настоящие файлы форматов 11 и 12, F по хвосту мебели,
    /// снос кровати по хвосту сбрасывает точку возрождения, стена в свободную половину тайла-якоря сдвинутой мебели (перенос
    /// якоря), выравнивание pre-pass правок после сортировки по чанкам.
    /// </summary>
    public sealed class C011TailTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat(ulong seed = 1111) => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), seed);

        private static WorldPos At(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        private static ushort Bed(int rot) => BuildLayers.Turned(ObjectId.Bed, rot);

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

        // План ядра → очередь такта (как у сервера, с переносом якоря); такт не делается. Возвращает план.
        private static PlannedEdit[] Enqueue(Simulation sim, int me, ushort key, TileCoord from, TileCoord to, int quad, out int queued)
        {
            var plan = new PlannedEdit[EditRules.MaxLineTiles];
            int n = BuildPlanner.Plan(sim.World, sim.GetEntity(me).Mover.Position, key, from, to, quad, (ushort)me, plan);
            queued = 0;
            for (int i = 0; i < n; i++)
            {
                PlannedEdit e = plan[i];
                if (e.Check == EditReply.Accepted
                    && sim.RequestPlayerEdit(e.Tile, e.Floor, e.Obj, true, e.Record, me, i, Simulation.AnyCell, e.FloorOnly, e.Moves, e.MoveTile, e.MoveObj, e.MoveRecord))
                {
                    queued++;
                }
            }

            Array.Resize(ref plan, Math.Max(n, 0));
            return plan;
        }

        private static int Place(Simulation sim, int me, ushort key, TileCoord from, TileCoord to, int quad)
        {
            Enqueue(sim, me, key, from, to, quad, out int queued);
            sim.Step();
            return queued;
        }

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

        private static void Demolish(Simulation sim, int me, TileCoord anchor)
        {
            BuildLayers.AfterDig(sim.World, anchor, out byte fl, out bool keep, out BuildRecord rest);
            Assert.True(sim.RequestPlayerEdit(anchor, fl, ObjectId.Empty, keep, rest, me, 0));
            sim.Step();
        }

        private static void Kill(Simulation sim, int me)
        {
            for (int i = 0; i < Simulation.InvulnRespawnTicks + 2; i++)
            {
                sim.Step();
            }

            sim.ApplyStrike(me, -1, 1000, sim.GetEntity(me).Mover.Position, knockback: false);
            Assert.False(sim.GetEntity(me).Alive);
            for (int i = 0; i < Simulation.RespawnDelayTicks + 2; i++)
            {
                sim.Step();
            }

            Assert.True(sim.GetEntity(me).Alive);
        }

        // ------------------------------------------------ 1. флаг Wide при чтении

        /// <summary>
        /// Флаг «многоклеточная» хранится в записи, но хэш его не видит — при чтении он обязан совпадать с предметом: кровать без
        /// флага и факел с флагом — испорченный файл (<see cref="InvalidDataException"/>), правильные записи читаются.
        /// </summary>
        [Theory]
        [InlineData(ObjectId.Bed, false, true)]
        [InlineData(ObjectId.Workbench, false, true)]
        [InlineData(ObjectId.Torch, true, true)]
        [InlineData(ObjectId.Bed, true, false)]
        [InlineData(ObjectId.Chest, false, false)]
        public void C011_Save_Wide_Must_Match_Object(ushort obj, bool wide, bool bad)
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(256, 11, 0);
            Simulation sim = live.Sim;
            var t = new TileCoord(100, 100);
            byte flags = wide ? (byte)BuildFlags.Wide : (byte)0;
            Assert.True(sim.RequestEdit(t, FloorId.Grass, obj, new BuildRecord(1, 50, flags, FloorId.Grass, 0)));
            sim.Step();
            byte[] save = WorldSave.Build(WorldSave.Capture(sim, live.BaseSha, "wide"), 4, 0);
            if (bad)
            {
                var ex = Assert.Throws<InvalidDataException>(() => WorldSave.Load(SaveContainer.Read(save), WorldSnapshot.LoadBase(live.BaseBytes), live.BaseSha));
                Assert.Contains("многоклеточная", ex.Message);
            }
            else
            {
                Simulation back = SaveFixtures.Load(live.BaseBytes, save);
                Assert.Equal(obj, back.World.GetObject(t));
            }
        }

        // ------------------------------------------------ 2. настоящие файлы форматов 11 и 12

        /// <summary>
        /// Настоящий файл формата 11 (код слияния task/E2-15 4848c8c, генератор <c>Golden/format11_layers.make.cs.txt</c>): пол
        /// под каменной стеной, сундук со сдвигом, верстак и кровать (тогда 1 × 1), факел со сдвигом. Читается, хэш генератора тот
        /// же; верстак и кровать стали многоклеточными (Wide, второй тайл твёрдый); пересохранение в 13 — тот же хэш.
        /// </summary>
        [Fact]
        public void C011_Save_Format11_RealFile_Reads_Layers_Furniture()
        {
            byte[] v11 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format11_layers.save"));
            byte[] baseBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format11_layers.base"));
            Assert.Equal(11u, SaveContainer.Read(v11).Header.FormatVersion);
            Simulation sim = SaveFixtures.Load(baseBytes, v11);
            string hash = StateHash.Compute(sim).Total.ToString("x16");
            Assert.Equal("3d3d027b3fa96dc8", hash); // хэш генератора (код 4848c8c); E3-6: формат 15

            for (int x = 130; x <= 132; x++)
            {
                for (int y = 126; y <= 127; y++)
                {
                    Assert.Equal(FloorId.WoodFloor, sim.World.GetFloor(new TileCoord(x, y)));
                    Assert.Equal(BuildCells.Full, BuildLayers.FloorCellsAt(sim.World, new TileCoord(x, y)));
                }
            }

            foreach (int y in new[] { 126, 127 })
            {
                var w = new TileCoord(130, y);
                Assert.Equal(ObjectId.StoneWall, sim.World.GetObject(w)); // стена поверх пола
                Assert.Equal(BuildCells.DoorWest, sim.SolidCells(sim.Shape, w));
            }

            var chest = new TileCoord(126, 130);
            Assert.Equal(ObjectId.Chest, sim.World.GetObject(chest));
            Assert.True(sim.World.TryGetBuild(chest, out BuildRecord cr));
            Assert.Equal((1, 0), (cr.OffsetX, cr.OffsetY));
            Assert.Equal(BuildCells.DoorWest, sim.SolidCells(sim.Shape, new TileCoord(127, 130))); // свес сундука

            foreach ((TileCoord anchor, ushort obj, TileCoord tail) in new[]
            {
                (new TileCoord(129, 132), ObjectId.Workbench, new TileCoord(130, 133)),
                (new TileCoord(124, 132), ObjectId.Bed, new TileCoord(124, 133)),
            })
            {
                Assert.Equal(obj, sim.World.GetObject(anchor));
                Assert.True(sim.World.TryGetBuild(anchor, out BuildRecord r));
                Assert.True((r.Flags & (byte)BuildFlags.Wide) != 0);
                Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, tail));
                Assert.True(BuildLayers.TryFurnitureAt(sim.World, tail, out TileCoord found));
                Assert.Equal(anchor, found);
            }

            var torch = new TileCoord(132, 130);
            Assert.Equal(ObjectId.Torch, sim.World.GetObject(torch));
            Assert.True(sim.World.TryGetBuild(torch, out BuildRecord tr));
            Assert.Equal((1, 1), (tr.OffsetX, tr.OffsetY));

            byte[] again = WorldSave.Build(WorldSave.Capture(sim, WorldSnapshot.StoredHash(baseBytes), "формат11→13"), 6, 0);
            Assert.Equal(16u, SaveContainer.Read(again).Header.FormatVersion);
            Assert.Equal(hash, StateHash.Compute(SaveFixtures.Load(baseBytes, again)).Total.ToString("x16"));
        }

        /// <summary>
        /// Настоящий файл формата 12 (код слияния task/E2-16 72e93d0, генератор <c>Golden/format12_wide.make.cs.txt</c>): пол под
        /// стеной, сундук со сдвигом, верстак 2 × 2 со сдвигом, кровать боком (90°) и кровать 0°. Читается (флаг Wide у
        /// многоклеточной на месте — проверка C-011 проходит), хэш генератора тот же; мебель на месте; пересохранение — тот же хэш.
        /// </summary>
        [Fact]
        public void C011_Save_Format12_RealFile_Reads_Rotated_Wide()
        {
            byte[] v12 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format12_wide.save"));
            byte[] baseBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format12_wide.base"));
            Assert.Equal(12u, SaveContainer.Read(v12).Header.FormatVersion);
            Simulation sim = SaveFixtures.Load(baseBytes, v12);
            string hash = StateHash.Compute(sim).Total.ToString("x16");
            Assert.Equal("9ac0a72706b7c295", hash); // хэш генератора (код 72e93d0); E3-6: формат 15

            Assert.Equal(FloorId.WoodFloor, sim.World.GetFloor(new TileCoord(130, 126)));
            Assert.Equal(ObjectId.StoneWall, sim.World.GetObject(new TileCoord(130, 126)));
            Assert.Equal(BuildCells.DoorWest, sim.SolidCells(sim.Shape, new TileCoord(130, 126)));

            Assert.True(sim.World.TryGetBuild(new TileCoord(126, 130), out BuildRecord cr));
            Assert.Equal(ObjectId.Chest, sim.World.GetObject(new TileCoord(126, 130)));
            Assert.Equal(1, cr.OffsetX);

            var bench = new TileCoord(128, 132);
            Assert.Equal(ObjectId.Workbench, sim.World.GetObject(bench));
            Assert.True(sim.World.TryGetBuild(bench, out BuildRecord br));
            Assert.Equal(1, br.OffsetX);
            Assert.True((br.Flags & (byte)BuildFlags.Wide) != 0);
            Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, new TileCoord(129, 133)));
            Assert.Equal(BuildCells.DoorWest, sim.SolidCells(sim.Shape, new TileCoord(130, 132))); // восточный край 2 × 2

            // C-030 [док]: кровать теперь двуспальная 2 × 2 тайла при любом повороте — старые кровати 1 × 2 из файла читаются
            // двуспальными (поворот хранится, вид — по повороту); в этом файле рядом с ними свободно.
            Assert.Equal(Bed(1), sim.World.GetObject(new TileCoord(122, 130)));
            Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, new TileCoord(123, 130)));
            Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, new TileCoord(122, 131)));
            Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, new TileCoord(123, 131)));
            Assert.Equal(0, sim.SolidCells(sim.Shape, new TileCoord(124, 130)));
            Assert.Equal(Bed(0), sim.World.GetObject(new TileCoord(120, 132)));
            Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, new TileCoord(120, 133)));
            Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, new TileCoord(121, 133)));

            byte[] again = WorldSave.Build(WorldSave.Capture(sim, WorldSnapshot.StoredHash(baseBytes), "формат12→13"), 6, 0);
            Assert.Equal(16u, SaveContainer.Read(again).Header.FormatVersion);
            Assert.Equal(hash, StateHash.Compute(SaveFixtures.Load(baseBytes, again)).Total.ToString("x16"));
        }

        // ------------------------------------------------ 3. F по хвосту мебели

        /// <summary>
        /// F по любой клетке мебели: курсор на хвосте кровати (южный тайл) и на дальнем тайле верстака 2 × 2 — действие их якоря
        /// и тайл якоря для заявки. Досягаемость — до любого тайла предмета: персонаж в 3 тайлах от ножной части кровати (4 от
        /// изголовья) — достаёт; сервер (<see cref="Simulation.RequestSetBed"/>) соглашается по хвосту и ставит точку у якоря.
        /// </summary>
        [Fact]
        public void C011_Use_By_Tail_Of_Bed_And_Workbench()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Bed, 1), (Items.Workbench, 1));
            var bed = new TileCoord(102, 98);
            var bench = new TileCoord(97, 100);
            Assert.Equal(1, Place(sim, me, Bed(0), bed, bed, 0));
            Assert.Equal(1, Place(sim, me, ObjectId.Workbench, bench, bench, 0));
            WorldPos self = sim.GetEntity(me).Mover.Position;

            var bedTail = new TileCoord(102, 99);
            Assert.True(ObjectUse.Find(sim.World, self, bedTail, out TileCoord t, out UseKind k));
            Assert.Equal((bed, UseKind.Bed), (t, k));
            var benchTail = new TileCoord(98, 101);
            Assert.Equal(UseKind.None, ObjectUse.At(sim.World, self, benchTail)); // C-013 [док]: у верстака нет действия F («F — верстак не нужно»)
            Assert.Equal(UseKind.Bed, ObjectUse.At(sim.World, self, new TileCoord(103, 99))); // C-030: двуспальная 2 × 2 — и восточная половина
            Assert.Equal(UseKind.None, ObjectUse.At(sim.World, self, new TileCoord(104, 99))); // рядом с хвостом — пусто

            // Персонаж южнее ножной части на 3 тайла: до изголовья 4 — достаёт по хвосту.
            WorldPos far = At(102, 102);
            Assert.False(EditRules.InUseRadius(sim.Shape, far, bed));
            Assert.Equal(UseKind.Bed, ObjectUse.At(sim.World, far, bedTail, out TileCoord target));
            Assert.Equal(bed, target);

            Assert.Equal(EditReply.Accepted, sim.RequestSetBed(me, bedTail));
            Assert.True(sim.GetEntity(me).HasBed);
            Assert.Equal(bed, sim.GetEntity(me).Bed);
        }

        /// <summary>По сети: F (заявка 33) по хвосту кровати — сервер принимает, точка возрождения — якорь кровати.</summary>
        [Fact]
        public void C011_Use_By_Tail_Over_Network()
        {
            using var rig = new EditTests.EditRig(Flat(), _ => EditTests.TileCenter(300, 200));
            NetClient c = rig.Join();
            rig.Run(5);
            var bed = new TileCoord(302, 198);
            Assert.True(rig.Sim.RequestEdit(bed, FloorId.Grass, Bed(0), new BuildRecord(1, 40, (byte)BuildFlags.Wide, FloorId.Grass, 0)));
            rig.Run(4);
            var uses = new List<UseReplyMessage>();
            c.UseReplied = uses.Add;
            Assert.True(c.RequestUse(new TileCoord(302, 199)));
            rig.Run(3);
            Assert.Single(uses);
            Assert.Equal(EditReply.Accepted, uses[0].Reply);
            Assert.Equal(ObjectId.Bed, ObjectId.IdOf(uses[0].Obj));
            Assert.Equal(bed, rig.Sim.GetEntity(c.EntityId).Bed);
        }

        // ------------------------------------------------ 4. снос кровати по хвосту

        /// <summary>
        /// Снос кровати по хвосту (тайл без объекта, якорь ищет <see cref="BuildLayers.TryTopOfCell"/>): кровать снята целиком,
        /// куча — один предмет «кровать», ячейки свободны; точка возрождения сброшена — смерть возвращает к старту.
        /// </summary>
        [Fact]
        public void C011_Bed_Demolished_By_Tail_Resets_Respawn()
        {
            var sim = new Simulation(Flat());
            sim.RespawnCenter = At(200, 200);
            int me = Me(sim, 100, 100, (Items.Bed, 1));
            var bed = new TileCoord(102, 100);
            Assert.Equal(1, Place(sim, me, Bed(0), bed, bed, 0));
            Assert.Equal(EditReply.Accepted, sim.RequestSetBed(me, bed));

            var tail = new TileCoord(102, 101);
            Assert.True(BuildLayers.TryTopOfCell(sim.World, BuildCells.CellOf(tail, 1, 1), out TileCoord anchor));
            Assert.Equal(bed, anchor);
            int piles = sim.Piles.Length;
            Demolish(sim, me, anchor);
            Assert.Equal(EditResult.Applied, LastResult(sim, me));
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(bed));
            Assert.Equal(0, sim.SolidCells(sim.Shape, tail));
            Assert.Equal(piles + 1, sim.Piles.Length);
            GroundPile pile = sim.Piles[sim.Piles.Length - 1];
            Assert.Equal((Items.Bed, 1), (pile.Item, pile.Count));

            Kill(sim, me);
            Assert.Equal(sim.RespawnCenter, sim.GetEntity(me).Mover.Position);
            Assert.False(sim.GetEntity(me).HasBed);
        }

        // ------------------------------------------------ 5. стена в свободную половину тайла-якоря

        /// <summary>
        /// Сундук со сдвигом (+1, 0) в тайле 102: восточная половина 102 и западная 103. Стена в западную половину 102 (тайл-якорь)
        /// ставится: сундук тем же тактом уходит якорем в 103 (сдвиг −1), его ячейки и содержимое прежние; снято 2 стены, сундук
        /// не возвращён кучей. Стена и сундук твёрдые там же, где выглядят.
        /// </summary>
        [Fact]
        public void C011_Wall_Into_Free_Half_Of_Chest_Anchor_Moves_Anchor()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Chest, 1), (Items.StoneWall, 4));
            var a = new TileCoord(102, 100);
            var b = new TileCoord(103, 100);
            Assert.Equal(1, Place(sim, me, ObjectId.Chest, a, a, 1)); // СЗ ячейка — СВ четверть 102
            Assert.True(sim.World.TryGetBuild(a, out BuildRecord cr));
            Assert.Equal(1, cr.OffsetX);
            var stuff = new Inventory();
            stuff.Add(Items.Stone, 7);
            sim.RestoreChests(new[] { a }, new[] { stuff });
            int piles = sim.Piles.Length;

            PlannedEdit[] plan = Enqueue(sim, me, ObjectId.StoneWall, a, a, 0 | (2 << 2), out int queued);
            Assert.Equal(1, queued);
            Assert.True(plan[0].Moves);
            Assert.Equal(b, plan[0].MoveTile);
            sim.Step();
            Assert.Equal(EditResult.Applied, LastResult(sim, me));

            Assert.Equal(ObjectId.StoneWall, sim.World.GetObject(a));
            Assert.Equal(BuildCells.DoorWest, BuildCells.MaskAt(sim.World, a));
            Assert.Equal(ObjectId.Chest, sim.World.GetObject(b));
            Assert.True(sim.World.TryGetBuild(b, out BuildRecord moved));
            Assert.Equal((-1, 0), (moved.OffsetX, moved.OffsetY));
            Assert.Equal(cr.Owner, moved.Owner);
            Assert.Equal(cr.Integrity, moved.Integrity);
            Assert.Equal(BuildCells.Full, sim.SolidCells(sim.Shape, a)); // стена запад + сундук восток
            Assert.Equal(BuildCells.DoorWest, sim.SolidCells(sim.Shape, b));
            Assert.True(sim.TryGetChest(b, out Inventory content));
            Assert.Equal(7, content.Count(Items.Stone));
            Assert.False(sim.TryGetChest(a, out _));
            Assert.Equal(piles, sim.Piles.Length); // сундук не рассыпан
            Assert.Equal(2, sim.GetEntity(me).Inv.Count(Items.StoneWall));
            Assert.Equal(0, sim.GetEntity(me).Inv.Count(Items.Chest));

            // Снос по любой клетке сундука — новый якорь, один предмет; стена остаётся.
            Assert.True(BuildLayers.TryTopOfCell(sim.World, BuildCells.CellOf(a, 1, 0), out TileCoord top));
            Assert.Equal(b, top);
        }

        /// <summary>
        /// Кровать со сдвигом (+1, 0): стена в западную половину тайла-якоря переносит якорь, точка возрождения идёт за кроватью —
        /// смерть возвращает к кровати, а не к старту. По сети клиент видит тот же мир (пачка несёт оба тайла).
        /// </summary>
        [Fact]
        public void C011_Wall_Into_Bed_Anchor_Keeps_Respawn_And_Client_Sees_Both_Tiles()
        {
            using var rig = new EditTests.EditRig(Flat(), _ => EditTests.TileCenter(300, 200));
            rig.Sim.RespawnCenter = At(250, 250);
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(5);
            var inv = rig.Sim.GetEntity(c.EntityId).Inv;
            inv.SetCell(5, Items.StoneWall, 4);
            inv.SetCell(6, Items.Bed, 1);
            Assert.True(rig.Sim.SetInventory(c.EntityId, inv));
            rig.Run(3);
            WorldPos self = rig.Sim.GetEntity(c.EntityId).Mover.Position;
            var a = new TileCoord(302, 197);
            c.Place(a, a, ObjectId.Bed, self, 6, 1); // сдвиг (+1, 0): восточная половина 302 и западная 303, ряды 197–198
            rig.Run(6);
            Assert.Equal(ObjectId.Bed, rig.Sim.World.GetObject(a));
            Assert.Equal(EditReply.Accepted, rig.Sim.RequestSetBed(c.EntityId, a));

            c.Place(a, new TileCoord(302, 198), ObjectId.StoneWall, self, 5, 0 | (2 << 2)); // западные половины 302 (197–198)
            rig.Run(6);
            Assert.All(replies, r => Assert.Equal(EditReply.Accepted, r.Reply));
            var b = new TileCoord(303, 197);
            Assert.Equal(ObjectId.StoneWall, rig.Sim.World.GetObject(a));
            Assert.Equal(ObjectId.StoneWall, rig.Sim.World.GetObject(new TileCoord(302, 198)));
            Assert.Equal(ObjectId.Bed, rig.Sim.World.GetObject(b));
            for (int x = 300; x <= 305; x++)
            {
                for (int y = 195; y <= 200; y++)
                {
                    var t = new TileCoord(x, y);
                    Assert.Equal(rig.Sim.World.GetObject(t), c.World!.World.GetObject(t));
                    bool sa = rig.Sim.World.TryGetBuild(t, out BuildRecord ra), sb = c.World.World.TryGetBuild(t, out BuildRecord rb);
                    Assert.Equal(sa, sb);
                    Assert.Equal(ra, rb);
                }
            }

            rig.Run(Simulation.InvulnRespawnTicks + 2);
            rig.Sim.ApplyStrike(c.EntityId, -1, 1000, rig.Sim.GetEntity(c.EntityId).Mover.Position, knockback: false);
            rig.Run(Simulation.RespawnDelayTicks + 2);
            SimEntity e = rig.Sim.GetEntity(c.EntityId);
            Assert.True(e.Alive);
            Assert.True(e.HasBed);
            Assert.Equal(b, e.Bed);
            Assert.True(Torus.Dist2(rig.Sim.Shape, e.Mover.Position, BuildLayers.CenterAt(rig.Sim.World, b)) <= 9L * U * U, $"возродился в {e.Mover.Position}");
        }

        /// <summary>
        /// Перенос невозможен — по-прежнему «мешает мебель»: слот нового якоря занят (стена в восточной половине 103), мебель
        /// повреждена; стена через оба тайла (её правка в тайле нового якоря). Мир не меняется, предметы целы.
        /// </summary>
        [Fact]
        public void C011_Wall_Into_Anchor_Refused_When_Move_Impossible()
        {
            var sim = new Simulation(Flat());
            int me = Me(sim, 100, 100, (Items.Chest, 3), (Items.StoneWall, 10));
            // Слот 103 занят стеной в восточной половине.
            var a = new TileCoord(102, 100);
            var b = new TileCoord(103, 100);
            Assert.Equal(1, Place(sim, me, ObjectId.StoneWall, b, b, 1 | (3 << 2)));
            Assert.Equal(1, Place(sim, me, ObjectId.Chest, a, a, 1));
            Assert.Equal(ObjectId.Chest, sim.World.GetObject(a));
            PlannedEdit[] plan = Enqueue(sim, me, ObjectId.StoneWall, a, a, 0 | (2 << 2), out int queued);
            Assert.Equal(0, queued);
            Assert.Equal(EditReply.FurnitureInTile, plan[0].Check);

            // Повреждённый сундук.
            var a2 = new TileCoord(102, 103);
            Assert.Equal(1, Place(sim, me, ObjectId.Chest, a2, a2, 1));
            Assert.True(sim.DamageBuilding(a2, 10));
            sim.Step();
            plan = Enqueue(sim, me, ObjectId.StoneWall, a2, a2, 0 | (2 << 2), out queued);
            Assert.Equal(0, queued);
            Assert.Equal(EditReply.FurnitureInTile, plan[0].Check);

            // Стена линией по северным половинам 97…98 (вторая правка — в тайле нового якоря 98).
            var a3 = new TileCoord(97, 100);
            Assert.Equal(1, Place(sim, me, ObjectId.Chest, a3, a3, 3)); // сдвиг (+1, +1): ЮВ четверть 97 и соседи
            plan = Enqueue(sim, me, ObjectId.StoneWall, a3, new TileCoord(98, 100), 0 | (1 << 2), out queued);
            Assert.Equal(EditReply.FurnitureInTile, plan[0].Check);
            Assert.False(plan[0].Moves);
            sim.Step();
            Assert.Equal(ObjectId.Chest, sim.World.GetObject(a3));
            Assert.Equal(1, queued); // встала только северная половина 98 (2 ячейки)
            Assert.Equal(ObjectId.StoneWall, sim.World.GetObject(new TileCoord(98, 100)));
            Assert.Equal(10 - 2 - 2, sim.GetEntity(me).Inv.Count(Items.StoneWall));
        }

        // ------------------------------------------------ выравнивание pre-pass после сортировки

        /// <summary>
        /// Две постановки одного такта в разных чанках, первая пришла из чанка «позже» и без предмета: после сортировки по чанкам
        /// отказ «нет ресурсов» и оплата не переезжают к соседней правке (до C-011 вторая получала чужой отказ, а первая вставала
        /// бесплатно).
        /// </summary>
        [Fact]
        public void C011_PrePass_Follows_Edits_After_Chunk_Sort()
        {
            var sim = new Simulation(Flat());
            int poor = Me(sim, 70, 10);
            int rich = Me(sim, 10, 10, (Items.Torch, 1));
            var tp = new TileCoord(72, 10); // чанк (2, 0)
            var tr = new TileCoord(12, 10); // чанк (0, 0)
            Assert.True(Torus.ChunkOf(sim.Shape, tr).X < Torus.ChunkOf(sim.Shape, tp).X);
            Enqueue(sim, poor, ObjectId.Torch, tp, tp, 0, out int q1);
            Enqueue(sim, rich, ObjectId.Torch, tr, tr, 0, out int q2);
            Assert.Equal((1, 1), (q1, q2));
            sim.Step();
            Assert.Equal(EditResult.NoResources, LastResult(sim, poor));
            Assert.Equal(EditResult.Applied, LastResult(sim, rich));
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(tp));
            Assert.Equal(ObjectId.Torch, sim.World.GetObject(tr));
            Assert.Equal(0, sim.GetEntity(rich).Inv.Count(Items.Torch));
            Assert.Equal(0, sim.GetEntity(poor).Inv.Count(Items.Torch)); // чужая оплата не возвращается бедному (П, з. 2)
        }

        /// <summary>
        /// То же для починки (оплата <c>CostPaidRepair</c>): бедный (без камня) чинит стену в чанке «позже» первым, богатый — в
        /// чанке «раньше» вторым. Богатый починил и заплатил, бедному ничего не вернулось, его стена не починена.
        /// </summary>
        [Fact]
        public void C011_PrePass_Repair_Follows_Edits_After_Chunk_Sort()
        {
            var sim = new Simulation(Flat());
            int poor = Me(sim, 70, 10);
            int rich = Me(sim, 10, 10, (Items.Stone, 20));
            var tp = new TileCoord(72, 10);
            var tr = new TileCoord(12, 10);
            Assert.True(BuildDefs.TryGet(ObjectId.StoneWall, out BuildDef def));
            foreach (TileCoord t in new[] { tp, tr })
            {
                Assert.True(sim.RequestEdit(t, FloorId.Grass, ObjectId.StoneWall, new BuildRecord(1, (ushort)(def.MaxIntegrity / 2), 0, FloorId.Grass, 0)));
            }

            sim.Step();
            Assert.True(sim.RequestRepair(tp, poor, 1));
            Assert.True(sim.RequestRepair(tr, rich, 2));
            sim.Step();
            Assert.Equal(EditResult.Applied, LastResult(sim, rich));
            Assert.NotEqual(EditResult.Applied, LastResult(sim, poor));
            Assert.True(sim.World.TryGetBuild(tr, out BuildRecord rr));
            Assert.Equal(def.MaxIntegrity, rr.Integrity);
            Assert.True(sim.World.TryGetBuild(tp, out BuildRecord pr));
            Assert.Equal(def.MaxIntegrity / 2, pr.Integrity);
            Assert.True(sim.GetEntity(rich).Inv.Count(Items.Stone) < 20);
            Assert.Equal(0, sim.GetEntity(poor).Inv.Count(Items.Stone));
        }

        // ------------------------------------------------ ответ на проверку (П)

        /// <summary>
        /// З. 1: один игрок сносит сундук, другой тем же тактом ставит стену в свободную половину его якоря (стена пришла
        /// второй). Перенос не применяется (тайл якоря уже в пачке, <c>MoveFits</c>): сундук ровно один — кучей, нового якоря
        /// нет, стена не встала, её предметы возвращены.
        /// </summary>
        [Fact]
        public void C011_Demolish_And_Wall_Into_Anchor_Same_Tick_No_Duplicate()
        {
            var sim = new Simulation(Flat());
            int a = Me(sim, 100, 100, (Items.Chest, 1));
            int b = Me(sim, 100, 102, (Items.StoneWall, 4));
            var anchor = new TileCoord(102, 100);
            var next = new TileCoord(103, 100);
            Assert.Equal(1, Place(sim, a, ObjectId.Chest, anchor, anchor, 1));
            int piles = sim.Piles.Length;

            BuildLayers.AfterDig(sim.World, anchor, out byte fl, out bool keep, out BuildRecord rest);
            Assert.True(sim.RequestPlayerEdit(anchor, fl, ObjectId.Empty, keep, rest, a, 0));
            PlannedEdit[] plan = Enqueue(sim, b, ObjectId.StoneWall, anchor, anchor, 0 | (2 << 2), out int queued);
            Assert.Equal(1, queued);
            Assert.True(plan[0].Moves);
            sim.Step();

            Assert.Equal(EditResult.Applied, LastResult(sim, a));
            Assert.Equal(EditResult.Busy, LastResult(sim, b));
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(anchor));
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(next));
            Assert.Equal(0, sim.SolidCells(sim.Shape, next));
            int chests = 0;
            for (int i = piles; i < sim.Piles.Length; i++)
            {
                chests += sim.Piles[i].Item == Items.Chest ? sim.Piles[i].Count : 0;
            }

            Assert.Equal(1, chests);
            Assert.Equal(4, sim.GetEntity(b).Inv.Count(Items.StoneWall));
        }

        /// <summary>
        /// З. 3: по сети открыт сундук; стена в свободную половину его якоря — окно не закрывается, тайл в сообщении 35 — новый
        /// якорь, содержимое прежнее, операция «из сундука в сумку» проходит.
        /// </summary>
        [Fact]
        public void C011_Open_Chest_Window_Follows_Moved_Chest()
        {
            using var rig = new EditTests.EditRig(Flat(), _ => EditTests.TileCenter(300, 200));
            NetClient c = rig.Join();
            rig.Run(5);
            var inv = rig.Sim.GetEntity(c.EntityId).Inv;
            inv.SetCell(5, Items.StoneWall, 4);
            inv.SetCell(6, Items.Chest, 1);
            Assert.True(rig.Sim.SetInventory(c.EntityId, inv));
            rig.Run(3);
            WorldPos self = rig.Sim.GetEntity(c.EntityId).Mover.Position;
            var a = new TileCoord(302, 199);
            var b = new TileCoord(303, 199);
            c.Place(a, a, ObjectId.Chest, self, 6, 1);
            rig.Run(6);
            Assert.Equal(ObjectId.Chest, rig.Sim.World.GetObject(a));
            var stuff = new Inventory();
            stuff.SetCell(0, Items.Stone, 7);
            rig.Sim.RestoreChests(new[] { a }, new[] { stuff });
            Assert.True(c.RequestUse(a));
            rig.Run(4);
            Assert.True(c.ChestOpen);
            Assert.Equal(a, c.ChestTile);

            c.Place(a, a, ObjectId.StoneWall, self, 5, 0 | (2 << 2));
            rig.Run(6);
            Assert.Equal(ObjectId.Chest, rig.Sim.World.GetObject(b));
            Assert.True(c.ChestOpen);
            Assert.Equal(b, c.ChestTile);
            Assert.Equal(7, c.Chest.Count(Items.Stone));

            int before = rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Stone);
            Assert.True(c.RequestBagMove(BagHand.ChestBase + 0, 20));
            rig.Run(4);
            Assert.Equal(before + 7, rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Stone));
            rig.Sim.TryGetChest(b, out Inventory left);
            Assert.Equal(0, left.Count(Items.Stone));
        }

        /// <summary>
        /// З. 4: точка возрождения переходит на новый якорь в такте переноса, а не поиском по старому тайлу. Кровать A
        /// переносит якорь стеной; затем её сносят, а в ячейку старого якоря свешивается чужая кровать — точка не
        /// перескакивает на чужую кровать: смерть — у старта.
        /// </summary>
        [Fact]
        public void C011_Respawn_Follows_Move_At_Once_Not_Foreign_Bed()
        {
            var sim = new Simulation(Flat());
            sim.RespawnCenter = At(200, 200);
            int me = Me(sim, 100, 100, (Items.Bed, 2), (Items.StoneWall, 6));
            var a = new TileCoord(102, 98);
            var b = new TileCoord(103, 98);
            Assert.Equal(1, Place(sim, me, Bed(0), a, a, 1)); // восточная половина 102 и западная 103, ряды 98–99
            Assert.Equal(EditReply.Accepted, sim.RequestSetBed(me, a));
            Assert.Equal(2, Place(sim, me, ObjectId.StoneWall, a, new TileCoord(102, 99), 0 | (2 << 2))); // западные половины 102, ряды 98–99
            Assert.Equal(ObjectId.Bed, ObjectId.IdOf(sim.World.GetObject(b)));
            Assert.Equal(b, sim.GetEntity(me).Bed); // сразу, в такте переноса

            // Снос своей кровати; другая кровать 90° свешивается в свободную восточную половину старого якоря (102, 98).
            Demolish(sim, me, b);
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(b));
            var other = new TileCoord(102, 97);
            Assert.Equal(1, Place(sim, me, Bed(1), other, other, 3)); // 90°: 4 × 2 ячейки от СЗ ячейки (205, 195): ряд 98 тайлов 102–104
            Assert.True(BuildLayers.TryFurnitureAt(sim.World, a, out TileCoord foreign));
            Assert.Equal(ObjectId.Bed, ObjectId.IdOf(sim.World.GetObject(foreign)));

            Kill(sim, me);
            Assert.Equal(sim.RespawnCenter, sim.GetEntity(me).Mover.Position);
            Assert.False(sim.GetEntity(me).HasBed);
        }
    }
}
