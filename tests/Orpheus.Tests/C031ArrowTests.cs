using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Save;
using Orpheus.Core.Trig;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-031 (`BRIEF` 11n): воткнувшаяся стрела — сама предмет (вид «стрела в преграде / в земле», ровно в точке остановки,
    /// не сливается), подбор проходом рядом; пропадает — подобрали, 10 мин с выстрела и ни у кого на экране, сверх 10 000
    /// — самая старая; стрела не летит вечно (дальность, потолок тактов, край мира без шва); зажатая ЛКМ — непрерывная
    /// стрельба; сохранение с видом, осью и сроком.
    /// </summary>
    public class C031ArrowTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const byte BowSlot = 4; // стартовый набор: лук в ячейке 4, стрелы × 50 в ячейке 8
        private const ushort East = 0;

        private static WorldPos At(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private sealed class Range
        {
            public readonly Simulation Sim;
            public readonly int Char;
            public readonly List<ProjectileStop> Stops = new List<ProjectileStop>();
            public readonly List<Projectile> Shots = new List<Projectile>();
            public readonly List<SimEvent> Pickups = new List<SimEvent>();

            public Range(int size = 64, int cx = 10, int cy = 32, int arrows = 50)
            {
                Sim = new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.World(size), 7));
                for (int x = 0; x < size; x++)
                {
                    Sim.RequestEdit(new TileCoord(x, cy), FloorId.Grass, ObjectId.Empty); // ряд без деревьев и камней
                }

                Sim.Step();
                Char = Sim.AddEntity(At(cx, cy));
                var inv = Inventory.StartingKit();
                inv.Selected = BowSlot;
                inv.SetCell(Inventory.Slots, arrows > 0 ? Items.Arrows : Items.None, arrows);
                Assert.True(Sim.SetInventory(Char, inv));
            }

            public void Tick(bool pressed, ushort aim = East)
            {
                ushort b = pressed ? InputButtons.Attack : (ushort)0;
                Span<InputFrame> frames = stackalloc InputFrame[]
                {
                    new InputFrame(Input8.None, b, aim, BowSlot),
                    new InputFrame(Input8.None, b, aim, BowSlot),
                };
                Sim.SetFramesAt(Sim.SlotOf(Char), frames);
                Sim.Step();
                foreach (ProjectileStop s in Sim.StopsThisTick)
                    Stops.Add(s);
                foreach (Projectile p in Sim.ShotsThisTick)
                    Shots.Add(p);
                foreach (SimEvent ev in Sim.Events)
                    if (ev.Kind == SimEventKind.PilePickup)
                        Pickups.Add(ev);
            }

            public void Shoot(int drawTicks, int flyTicks, ushort aim = East)
            {
                for (int t = 0; t < drawTicks; t++)
                    Tick(true, aim);
                for (int t = 0; t < flyTicks; t++)
                    Tick(false, aim);
            }

            public int Arrows => Sim.GetEntity(Char).Inv.Count(Items.Arrows);

            public List<GroundPile> StuckArrows()
            {
                var list = new List<GroundPile>();
                foreach (GroundPile p in Sim.Piles)
                    if (p.IsStuckArrow)
                        list.Add(p);
                return list;
            }
        }

        // -------------------------------------------------------- стрела сама — предмет

        /// <summary>
        /// Стрела в стену: ровно одна запись на земле — сама стрела (вид «в преграде», 1 штука, ось выстрела) в точке остановки
        /// снаряда; никакой отдельной кучи рядом (раньше: 50 % — куча, сдвинутая на соседний свободный тайл).
        /// </summary>
        [Fact]
        public void C031_WallArrow_IsItselfTheItem_AtStopPoint()
        {
            var r = new Range();
            r.Sim.RequestEdit(new TileCoord(13, 32), FloorId.Grass, ObjectId.StoneWall, BuildRecord.OfPlayer(1, FloorId.Grass));
            r.Shoot(18, 10);
            ProjectileStop stop = Assert.Single(r.Stops);
            Assert.Equal(ProjectileEnd.Wall, stop.Reason);
            Assert.True(stop.Dropped);
            GroundPile p = Assert.Single(r.Sim.Piles.ToArray());
            Assert.Equal(GroundPile.KindArrowWall, p.Kind);
            Assert.Equal(Items.Arrows, p.Item);
            Assert.Equal(1, p.Count);
            Assert.Equal(stop.Position, p.Position); // не сдвинута
            Assert.Equal(r.Shots[0].AimRaw, p.Aim);
            Assert.Equal(r.Shots[0].SpawnTick + Simulation.PileLifetimeTicks, p.ExpireTick); // 10 мин с выстрела
            Assert.Equal(1, r.Sim.StuckArrowCount);
        }

        /// <summary>
        /// Без преграды: стрела в конце дальности остаётся всегда (вид «в земле»); 20 выстрелов — 20 стрел, ни одна не слилась
        /// с другой (выстрелы в одну точку). Попавшая в существо пропадает (`02` 4.2 — решение C-031).
        /// </summary>
        [Fact]
        public void C031_RangeArrows_AlwaysStay_NeverMerge_HitArrowVanishes()
        {
            var r = new Range();
            for (int i = 0; i < 20; i++)
                r.Shoot(18, 40); // полное натяжение — одна точка конца
            Assert.Equal(20, r.Shots.Count);
            List<GroundPile> stuck = r.StuckArrows();
            Assert.Equal(20, stuck.Count);
            Assert.All(stuck, p => Assert.Equal(GroundPile.KindArrowGround, p.Kind));
            Assert.All(stuck, p => Assert.Equal(1, p.Count));
            Assert.All(stuck, p => Assert.Equal(stuck[0].Position, p.Position)); // одна точка — и всё равно 20 записей
            Assert.All(r.Stops, s => Assert.True(s.Dropped));

            // обычная куча стрел, выброшенная рядом, со стрелой-предметом не сливается
            int plain = r.Sim.AddPile(Items.Arrows, 5, stuck[0].Position);
            Assert.DoesNotContain(stuck, p => p.Id == plain);
            Assert.Equal(21, r.Sim.Piles.Length);

            var h = new Range();
            h.Sim.AddEnemy(At(20, 32));
            h.Shoot(18, 20);
            Assert.Equal(ProjectileEnd.Hit, Assert.Single(h.Stops).Reason);
            Assert.Empty(h.Sim.Piles.ToArray());
        }

        /// <summary>Стрела, упавшая в воду, тонет — предмета нет (и не сдвигается на берег).</summary>
        [Fact]
        public void C031_RangeArrowInWater_Sinks()
        {
            var r = new Range();
            for (int x = 36; x <= 44; x++)
                r.Sim.RequestEdit(new TileCoord(x, 32), FloorId.Water, ObjectId.Empty);
            r.Shoot(18, 40); // 30 т от 10,5 — x = 40,5: вода
            ProjectileStop s = Assert.Single(r.Stops);
            Assert.Equal(ProjectileEnd.Range, s.Reason);
            Assert.False(s.Dropped);
            Assert.Empty(r.Sim.Piles.ToArray());
        }

        // -------------------------------------------------------- подбор

        /// <summary>Подбор проходом рядом: в радиусе подбора (1,5 т) стрела уходит в сумку; на 2 т — лежит.</summary>
        [Fact]
        public void C031_Pickup_WalkingNear()
        {
            var r = new Range();
            r.Sim.RequestEdit(new TileCoord(20, 32), FloorId.Grass, ObjectId.StoneWall, BuildRecord.OfPlayer(1, FloorId.Grass));
            r.Shoot(18, 20);
            GroundPile p = Assert.Single(r.StuckArrows());
            Assert.Equal(49, r.Arrows);

            r.Sim.PlaceForTests(r.Char, new WorldPos(p.Position.X - 2 * U, p.Position.Y));
            r.Shoot(0, 5);
            Assert.Single(r.StuckArrows());

            r.Sim.PlaceForTests(r.Char, new WorldPos(p.Position.X - U, p.Position.Y));
            r.Shoot(0, 2);
            Assert.Empty(r.StuckArrows());
            Assert.Equal(50, r.Arrows);
            SimEvent ev = Assert.Single(r.Pickups);
            Assert.Equal(p.Id, ev.Tag);
            Assert.Equal(r.Char, ev.Requester);
            Assert.Equal(0, r.Sim.StuckArrowCount);
        }

        // -------------------------------------------------------- срок жизни

        /// <summary>
        /// 10 мин с выстрела: стрела, которую видит персонаж (в окне экрана 52 × 32 т, вне подбора), не пропадает и спустя
        /// 10 мин; ушёл из окна — пропадает в следующем такте. Без смотрящих — ровно на такте «выстрел + 10 мин».
        /// </summary>
        [Fact]
        public void C031_Expiry_10MinAfterShot_OnlyWhenNobodySees()
        {
            var r = new Range(size: 256, cx: 100, cy: 100);
            int id = r.Sim.AddStuckArrowForTests(At(100, 100), East, GroundPile.KindArrowGround, r.Sim.Tick);
            Assert.True(id > 0);
            long expire = r.Sim.Piles[0].ExpireTick;
            Assert.Equal(r.Sim.Tick + Simulation.PileLifetimeTicks, expire);

            // смотрящий на краю окна экрана (52 т по X), подбор не достаёт
            r.Sim.PlaceForTests(r.Char, At(100 + Simulation.ScreenHalfTilesX, 100));
            while (r.Sim.Tick < expire + 30)
                r.Sim.Step();
            Assert.Single(r.StuckArrows()); // старше 10 мин, но на экране

            r.Sim.PlaceForTests(r.Char, At(100 + Simulation.ScreenHalfTilesX + 1, 100));
            r.Sim.Step();
            Assert.Empty(r.StuckArrows());
            Assert.Equal(0, r.Sim.StuckArrowCount);

            // никого рядом: ровно на такте срока, не раньше
            int id2 = r.Sim.AddStuckArrowForTests(At(100, 100), East, GroundPile.KindArrowWall, r.Sim.Tick);
            long expire2 = r.Sim.Piles[0].ExpireTick;
            while (r.Sim.Tick < expire2 - 1)
                r.Sim.Step();
            Assert.Equal(id2, Assert.Single(r.StuckArrows()).Id);
            r.Sim.Step();
            Assert.Equal(expire2, r.Sim.Tick);
            Assert.Empty(r.StuckArrows());

            // окно по Y: 32 т — видит, 33 — нет (и шов тора: смотрящий за швом)
            int id3 = r.Sim.AddStuckArrowForTests(At(2, 3), East, GroundPile.KindArrowWall, r.Sim.Tick - Simulation.PileLifetimeTicks);
            Assert.True(id3 > 0);
            r.Sim.PlaceForTests(r.Char, At(2, 3 - Simulation.ScreenHalfTilesY + 256));
            r.Sim.Step();
            Assert.Single(r.StuckArrows());
            r.Sim.PlaceForTests(r.Char, At(2, 3 - Simulation.ScreenHalfTilesY - 1 + 256));
            r.Sim.Step();
            Assert.Empty(r.StuckArrows());
        }

        /// <summary>
        /// C-032 (мутация M4 из C-031): «не исчезает у персонажа» — только про стрелы. Обычная куча истекает ровно
        /// на такте «создание + 10 мин», даже когда персонаж видит её в окне экрана.
        /// </summary>
        [Fact]
        public void C031_Expiry_NormalPile_Expires_Under_Character()
        {
            var r = new Range(size: 256, cx: 100, cy: 100);
            int pile = r.Sim.AddPile(Items.Wood, 3, At(100, 100));
            Assert.True(pile > 0);
            long expire = r.Sim.Piles[0].ExpireTick;
            Assert.Equal(r.Sim.Tick + Simulation.PileLifetimeTicks, expire);

            // смотрящий на краю окна экрана (52 т по X), подбор не достаёт (1,5 т)
            r.Sim.PlaceForTests(r.Char, At(100 + Simulation.ScreenHalfTilesX, 100));
            while (r.Sim.Tick < expire - 1)
                r.Sim.Step();
            Assert.Contains(r.Sim.Piles.ToArray(), p => p.Id == pile); // до срока — лежит
            r.Sim.Step();
            Assert.Equal(expire, r.Sim.Tick);
            Assert.DoesNotContain(r.Sim.Piles.ToArray(), p => p.Id == pile); // пропала на такте срока, хотя персонаж видит
        }

        // -------------------------------------------------------- предел

        /// <summary>Предел (уменьшен до 5): 8 стрел в стену — лежат 5 последних, 3 самые старые пропали; обычные кучи не трогаются.</summary>
        [Fact]
        public void C031_Limit_OldestStuckArrowsVanish()
        {
            var r = new Range();
            r.Sim.StuckArrowLimit = 5;
            r.Sim.RequestEdit(new TileCoord(14, 32), FloorId.Grass, ObjectId.StoneWall, BuildRecord.OfPlayer(1, FloorId.Grass));
            int plain = r.Sim.AddPile(Items.Wood, 3, At(10, 34));
            for (int i = 0; i < 8; i++)
                r.Shoot(6, 7);
            Assert.Equal(8, r.Shots.Count);
            List<GroundPile> stuck = r.StuckArrows();
            Assert.Equal(5, stuck.Count);
            Assert.Equal(5, r.Sim.StuckArrowCount);
            Assert.All(r.Stops, s => Assert.True(s.Dropped));

            // остались 5 новейших: номера стрел — plain + 1 … plain + 8, лежат plain + 4 … plain + 8
            for (int i = 0; i < 5; i++)
                Assert.Equal(plain + 4 + i, stuck[i].Id);
            Assert.Contains(r.Sim.Piles.ToArray(), p => p.Id == plain); // обычная куча цела
        }

        /// <summary>Предел по умолчанию — 10 000: 10 001-я стрела убирает самую старую, счёт не растёт.</summary>
        [Fact]
        public void C031_Limit_10000_Default()
        {
            Assert.Equal(10_000, Simulation.MaxStuckArrows);
            var r = new Range(size: 256, cx: 100, cy: 100);
            Assert.Equal(Simulation.MaxStuckArrows, r.Sim.StuckArrowLimit);
            int first = 0;
            for (int i = 0; i < Simulation.MaxStuckArrows; i++)
            {
                int id = r.Sim.AddStuckArrowForTests(At(150 + i % 50, 20 + i / 50 % 50), (ushort)i, GroundPile.KindArrowGround, r.Sim.Tick);
                Assert.True(id > 0);
                first = first == 0 ? id : first;
            }

            Assert.Equal(Simulation.MaxStuckArrows, r.Sim.StuckArrowCount);
            int last = r.Sim.AddStuckArrowForTests(At(150, 20), 1, GroundPile.KindArrowGround, r.Sim.Tick);
            Assert.Equal(Simulation.MaxStuckArrows, r.Sim.StuckArrowCount);
            Assert.Equal(Simulation.MaxStuckArrows, r.Sim.Piles.Length);
            Assert.Equal(first + 1, r.Sim.Piles[0].Id);
            Assert.Equal(last, r.Sim.Piles[^1].Id);

            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (int t = 0; t < 30; t++)
                r.Sim.Step();
            Assert.Equal(Simulation.MaxStuckArrows, r.Sim.Piles.Length); // никто не подобрал (персонаж в 50 т), срок не вышел
            Assert.True(timer.ElapsedMilliseconds < 3000, $"30 тактов с 10 000 стрел — {timer.ElapsedMilliseconds} мс");
        }

        // -------------------------------------------------------- стрела не летит вечно

        /// <summary>
        /// Стрела не летит вечно: дальность лука — конец «дальность»; снаряд со скоростью 1/256 unit за такт — конец по потолку
        /// <see cref="Simulation.MaxFlightTicks"/>; на карте без шва за краем — пропадает без предмета.
        /// </summary>
        [Fact]
        public void C031_Flight_AlwaysEnds()
        {
            var r = new Range();
            Assert.True(r.Sim.SpawnProjectile(r.Char, false, Projectiles.KindArrow, At(10, 32), East, 1, 30 * U, 5, Items.Arrows) > 0);
            int ticks = 0;
            while (r.Stops.Count == 0 && ticks < Simulation.MaxFlightTicks + 50)
            {
                r.Tick(false);
                ticks++;
            }

            Assert.Equal(Simulation.MaxFlightTicks, ticks);
            Assert.Equal(ProjectileEnd.Range, r.Stops[0].Reason);
            Assert.Equal(0, r.Sim.Projectiles.Length);

            var flat = new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.Flat(64, 64), 7));
            for (int x = 0; x < 64; x++)
                flat.RequestEdit(new TileCoord(x, 32), FloorId.Grass, ObjectId.Empty);
            flat.Step();
            int shooter = flat.AddEntity(At(60, 32));
            Assert.True(flat.SpawnProjectile(shooter, false, Projectiles.KindArrow, At(60, 32), East, 28 * U * 256 / 30, 30 * U, 5, Items.Arrows) > 0);
            var stops = new List<ProjectileStop>();
            for (int t = 0; t < 40 && stops.Count == 0; t++)
            {
                flat.Step();
                foreach (ProjectileStop s in flat.StopsThisTick)
                    stops.Add(s);
            }

            ProjectileStop edge = Assert.Single(stops);
            Assert.False(edge.Dropped);
            Assert.True(edge.Position.X < 64 * U);
            Assert.Empty(flat.Piles.ToArray());
            Assert.Equal(0, flat.Projectiles.Length);
        }

        // -------------------------------------------------------- непрерывная стрельба

        /// <summary>
        /// Зажатая ЛКМ — непрерывная стрельба: каждое полное натяжение (36 кадров) — выстрел полной силы, отход 12 кадров,
        /// натяжение снова: 4 с удержания — 5 выстрелов (темп 1,25/с), все «сильные». Стрелы кончились — стрельба кончилась,
        /// отказов нет.
        /// </summary>
        [Fact]
        public void C031_HoldLmb_ContinuousFire_UntilArrowsRunOut()
        {
            var b = new BowAction();
            var fireFrames = new List<int>();
            for (int f = 1; f <= 240; f++)
            {
                b.StepFrame(BowInput.Of(true, true, true), 0, 0, 0);
                if (b.TakeShot())
                {
                    fireFrames.Add(f);
                    Assert.Equal(BowAction.FullDrawFrames, b.ShotDraw);
                }
            }

            Assert.Equal(new[] { 36, 84, 132, 180, 228 }, fireFrames.ToArray());

            var r = new Range(arrows: 3);
            Assert.Equal(3, r.Arrows);
            for (int t = 0; t < 150; t++)
                r.Tick(true);
            Assert.Equal(3, r.Shots.Count);
            Assert.All(r.Shots, s => Assert.True(s.Strong));
            Assert.Equal(0, r.Arrows);
            Assert.DoesNotContain(r.Stops, s => s.Reason == ProjectileEnd.Refused);
            Assert.Equal(BowAction.PhaseIdle, r.Sim.GetEntity(r.Char).Mover.Bow.Phase);
        }

        // -------------------------------------------------------- сохранение

        /// <summary>
        /// Сохранение (формат 14): воткнувшиеся стрелы — с видом, осью и сроком «выстрел + 10 мин»; после загрузки — те же
        /// записи, тот же хэш на каждом такте, срок идёт дальше (старая пропадает на том же такте).
        /// </summary>
        [Fact]
        public void C031_Save_StuckArrows_KindAimAge_SameHash()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(256, 21, 0);
            Simulation sim = live.Sim;
            sim.Step();
            WorldPos spot = default;
            for (int y = 10; y < 250 && spot.X == 0; y++)
            {
                for (int x = 10; x < 250; x++)
                {
                    var t = new TileCoord(x, y);
                    if (FloorId.IsPassable(sim.World.GetFloor(t)) && sim.World.GetFloor(t) != FloorId.Water && !sim.IsBlocked(sim.Shape, t))
                    {
                        spot = At(x, y);
                        break;
                    }
                }
            }

            Assert.NotEqual(0, spot.X);
            for (int i = 0; i < 100; i++)
                sim.Step();
            int old = sim.AddStuckArrowForTests(spot, 12345, GroundPile.KindArrowWall, sim.Tick - Simulation.PileLifetimeTicks + 40);
            int young = sim.AddStuckArrowForTests(new WorldPos(spot.X + 30, spot.Y), 54321, GroundPile.KindArrowGround, sim.Tick);
            sim.AddPile(Items.Wood, 4, new WorldPos(spot.X, spot.Y + 60));
            Assert.True(old > 0 && young > 0);

            byte[] save = WorldSave.Build(WorldSave.Capture(sim, live.BaseSha, "стрелы"), 3, 0);
            Assert.Equal(16u, SaveContainer.Read(save).Header.FormatVersion);
            Simulation loaded = SaveFixtures.Load(live.BaseBytes, save);
            Assert.Equal(sim.Piles.ToArray(), loaded.Piles.ToArray());
            Assert.Equal(2, loaded.StuckArrowCount);
            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total);
            for (int i = 0; i < 60; i++)
            {
                sim.Step();
                loaded.Step();
                Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total);
            }

            Assert.DoesNotContain(loaded.Piles.ToArray(), p => p.Id == old); // срок шёл и после загрузки
            Assert.Contains(loaded.Piles.ToArray(), p => p.Id == young && p.Aim == 54321 && p.Kind == GroundPile.KindArrowGround);
        }

        /// <summary>
        /// Настоящий файл формата 13 (код main 45fce53, генератор <c>Golden/format13_arrows.make.cs.txt</c>): кучи стрел E3-2
        /// (50 %), выброшенная кирка, древесина. Читается, хэш генератора тот же, все кучи — обычные (вид 0, сливаются как
        /// прежде); пересохранение в 14 — тот же хэш.
        /// </summary>
        [Fact]
        public void C031_Save_Format13_RealFile_ArrowPilesStayPlain()
        {
            byte[] v13 = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Golden", "format13_arrows.save"));
            byte[] baseBytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Golden", "format13_arrows.base"));
            Assert.Equal(13u, SaveContainer.Read(v13).Header.FormatVersion);
            Simulation sim = SaveFixtures.Load(baseBytes, v13);
            string hash = StateHash.Compute(sim).Total.ToString("x16");
            Assert.Equal("fde26d9ee8b93917", hash); // хэш генератора после загрузки (код 45fce53); E3-6: формат 16
            Assert.Equal(4, sim.Piles.Length);
            int arrows = 0;
            foreach (GroundPile p in sim.Piles)
            {
                Assert.Equal(GroundPile.KindPlain, p.Kind);
                Assert.Equal(0, p.Aim);
                arrows += p.Item == Items.Arrows ? p.Count : 0;
            }

            Assert.Equal(8, arrows);
            Assert.Equal(0, sim.StuckArrowCount);

            byte[] again = WorldSave.Build(WorldSave.Capture(sim, WorldSnapshot.StoredHash(baseBytes), "формат13→14"), 8, 0);
            Assert.Equal(16u, SaveContainer.Read(again).Header.FormatVersion);
            Assert.Equal(hash, StateHash.Compute(SaveFixtures.Load(baseBytes, again)).Total.ToString("x16"));
        }

        // -------------------------------------------------------- клиент

        /// <summary>
        /// По сети (протокол 32): воткнувшаяся стрела доходит до клиента кучей с видом и осью; подбор проходом — «кучи нет»
        /// с поднявшим, стрела в сумке сервера.
        /// </summary>
        [Fact]
        public void C031_Net_StuckArrow_KindAim_ReachClient_PickedByWalking()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            using var rig = new EditTests.EditRig(world, _ => At(300, 200));
            NetClient c = rig.Join();
            rig.Run(10);
            int id = rig.Sim.AddStuckArrowForTests(At(304, 200), 0x4321, GroundPile.KindArrowWall, rig.Sim.Tick);
            int plain = rig.Sim.AddPile(Items.Wood, 2, At(300, 204));
            rig.Run(3);
            Assert.Equal(2, c.Piles.Count);
            NetPile seen = default, wood = default;
            foreach (NetPile p in c.Piles)
            {
                if (p.Id == id)
                    seen = p;
                if (p.Id == plain)
                    wood = p;
            }

            Assert.Equal(GroundPile.KindArrowWall, seen.Kind);
            Assert.Equal(0x4321, seen.Aim);
            Assert.Equal(1, seen.Count);
            Assert.Equal(At(304, 200), seen.Position);
            Assert.Equal(GroundPile.KindPlain, wood.Kind);

            var gone = new List<PileGoneMessage>();
            c.PileGone = (m, was) => gone.Add(m);
            int before = rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Arrows);
            rig.InputOf = cl => new InputFrame(Input8.East, 0);
            for (int t = 0; t < 120 && gone.Count == 0; t++)
                rig.Run(1);
            PileGoneMessage g = Assert.Single(gone);
            Assert.Equal(id, g.Id);
            Assert.Equal(PileGoneReason.Picked, g.Reason);
            Assert.Equal(c.EntityId, g.Taker);
            Assert.Equal(before + 1, rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Arrows));
        }

        /// <summary>
        /// Клиент: стрела-предмет в точке конца закрыта снарядом, пока тот летит к ней и дрожит (<see cref="ClientProjectiles.StuckUs"/>),
        /// потом снаряд гаснет и виден предмет. Не легла (вода) — не закрывает.
        /// </summary>
        [Fact]
        public void C031_Client_ProjectileCoversStuckArrowUntilHandoff()
        {
            MapShape map = MapShape.World(64);
            var list = new ClientProjectiles(map);
            var p = new Projectile { Id = 5, Owner = 9, Kind = Projectiles.KindArrow, Origin = At(10, 32), AimRaw = East, SpeedX256 = 28 * U * 256 / 30, RangeUnits = 30 * U };
            list.OnMessage(ProjectileMessage.ShotOf(p), 1, 0);
            WorldPos end = At(13, 32);
            list.OnMessage(ProjectileMessage.StopOf(new ProjectileStop(5, 9, ProjectileEnd.Wall, end, -1, true)), 1, 0);
            Assert.True(list.Covers(end));
            Assert.False(list.Covers(At(14, 32)));
            long arrive = 200_000; // 3 т при 28 т/с ≈ 0,107 с
            list.Update(arrive);
            Assert.True(list.Covers(end));
            list.Update(arrive + ClientProjectiles.StuckUs + 1);
            Assert.False(list.Covers(end));
            Assert.Empty(list.Items);

            // C-032 (мутация M5 из C-031): стрела в воду — легла без предмета (Dropped = false) — в той же точке не закрывает.
            var p2 = new Projectile { Id = 6, Owner = 9, Kind = Projectiles.KindArrow, Origin = At(17, 32), AimRaw = East, SpeedX256 = 28 * U * 256 / 30, RangeUnits = 30 * U };
            list.OnMessage(ProjectileMessage.ShotOf(p2), 1, 0);
            WorldPos end2 = At(20, 32);
            list.OnMessage(ProjectileMessage.StopOf(new ProjectileStop(6, 9, ProjectileEnd.Range, end2, -1, false)), 1, 0);
            Assert.False(list.Covers(end2));
            list.Update(arrive);
            Assert.False(list.Covers(end2)); // долетела — гаснет сразу, переходить к предмету не за чем
            Assert.Empty(list.Items);
        }
    }
}
