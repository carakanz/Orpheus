using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E3-2 (шаг 3.2) «лук и стрелы» (`02` 4.1–4.3, 4.6): автомат натяжения <see cref="BowAction"/> в кадрах 60 Гц,
    /// снаряд ядра (<c>Simulation.Projectiles</c>) — полёт, попадание, что останавливает (стена, стена в полтайла, закрытая
    /// дверь, скала, дерево, обрыв; забор и вода — нет), шов тора, расход стрел, сила натяжения, кучи 50 %, детерминизм.
    /// </summary>
    public class BowTests
    {
        private const int U = Torus.SubTilesPerTile;
        private static readonly MapShape Map = MapShape.World(64);
        private const byte BowSlot = 4; // стартовый набор: лук в ячейке 4, стрелы × 50 в ячейке 8
        private const ushort East = 0;

        private static WorldPos At(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private static WorldModel Flat(ulong seed = 7) => FlatWorldGenerator.Instance.Generate(MapShape.World(64), seed);

        // -------------------------------------------------------- автомат лука

        private static byte In(bool pressed, bool arrows = true) => BowInput.Of(true, pressed, arrows);

        /// <summary>
        /// Натяжение 0,6 с (36 кадров): C-031 — при зажатой ЛКМ выстрел полной силы сам в 36-м кадре (раньше натяжение
        /// держалось до отпускания); отпускание раньше — выстрел в кадре отпускания с набранной силой; отход 12 кадров, бег 60 %.
        /// </summary>
        [Fact]
        public void E32_Bow_FullDraw_FiresOnRelease_Recover12()
        {
            var b = new BowAction();
            for (int f = 0; f < BowAction.FullDrawFrames - 1; f++)
            {
                b.StepFrame(In(true), 1234, 0, 0);
                Assert.Equal(BowAction.PhaseDraw, b.Phase);
                Assert.False(b.Fired);
            }

            Assert.Equal(BowAction.FullDrawFrames - 1, b.Frames);
            Assert.Equal(BowAction.DrawSpeedX1000, b.SpeedX1000);
            b.StepFrame(In(true), 1234, 77, 88); // 36-й кадр — выстрел сам
            Assert.True(b.Fired);
            Assert.Equal(BowAction.FullDrawFrames, b.ShotDraw);
            Assert.Equal(1234, b.ShotAim); // ось — прицел последнего кадра с зажатой ЛКМ
            Assert.Equal(77, b.ShotX);
            Assert.Equal(256, BowAction.PowerQ8(b.ShotDraw));

            var early = new BowAction();
            for (int f = 0; f < 20; f++)
                early.StepFrame(In(true), 1234, 0, 0);
            early.StepFrame(In(false), 999, 5, 6);
            Assert.True(early.Fired);
            Assert.Equal(20, early.ShotDraw);
            Assert.Equal(1234, early.ShotAim);
            Assert.True(b.TakeShot());
            Assert.False(b.TakeShot());
            int recover = 0;
            while (b.Phase == BowAction.PhaseRecover)
            {
                b.StepFrame(In(true), 0, 0, 0); // ЛКМ в отходе не натягивает
                recover++;
            }

            Assert.Equal(BowAction.RecoverFrames, recover);
            Assert.Equal(BowAction.PhaseIdle, b.Phase);
        }

        /// <summary>Щелчок короче 0,2 с: тетива дотягивается до 12 кадров, выстрел — наименьшей силы.</summary>
        [Fact]
        public void E32_Bow_ShortClick_FiresAtMinDraw()
        {
            var b = new BowAction();
            b.StepFrame(In(true), 0, 0, 0);
            int frames = 1;
            while (!b.Fired)
            {
                b.StepFrame(In(false), 0, 0, 0);
                frames++;
                Assert.True(frames < 30);
            }

            Assert.Equal(BowAction.MinDrawFrames, b.ShotDraw);
            Assert.Equal(BowAction.MinDrawFrames, frames);
            Assert.Equal(0, BowAction.PowerQ8(b.ShotDraw));
        }

        /// <summary>Без стрел натяжения нет; лук убран из рук в натяжении — сброс без выстрела; уворот сбрасывает натяжение.</summary>
        [Fact]
        public void E32_Bow_NoArrows_SwitchItem_Dodge_NoShot()
        {
            var b = new BowAction();
            b.StepFrame(In(true, arrows: false), 0, 0, 0);
            Assert.Equal(BowAction.PhaseIdle, b.Phase);

            b.StepFrame(In(true), 0, 0, 0);
            Assert.Equal(BowAction.PhaseDraw, b.Phase);
            b.StepFrame(0, 0, 0, 0); // лук не в руках
            Assert.Equal(BowAction.PhaseIdle, b.Phase);
            Assert.False(b.Fired);

            var m = Mover.At(Map, At(10, 10));
            for (int f = 0; f < 20; f++)
            {
                m.StepFrame(Map, Input8.East, false, false, 0, In(true), Collision.Empty);
            }

            Assert.Equal(BowAction.PhaseDraw, m.Bow.Phase);
            m.StepFrame(Map, Input8.East, true, false, 0, In(false), Collision.Empty); // рывок + отпускание
            Assert.Equal(BowAction.PhaseIdle, m.Bow.Phase);
            Assert.False(m.Bow.Fired);
        }

        /// <summary>Бег при натяжении — 60 % (`02` 4.1).</summary>
        [Fact]
        public void E32_Bow_DrawSlowsRunTo60Percent()
        {
            var free = Mover.At(Map, At(10, 10));
            var draw = Mover.At(Map, At(10, 20));
            for (int f = 0; f < BowAction.FullDrawFrames - 1; f++) // C-031: дальше — выстрел сам и отход на полной скорости
            {
                free.StepFrame(Map, Input8.East, false, false, 0, 0, Collision.Empty);
                draw.StepFrame(Map, Input8.East, false, false, 0, In(true), Collision.Empty);
            }

            int dFree = Torus.Delta(Map, At(10, 10), free.Position).X;
            int dDraw = Torus.Delta(Map, At(10, 20), draw.Position).X;
            Assert.InRange(dDraw * 1000 / dFree, 590, 610);
        }

        // -------------------------------------------------------- снаряд в ядре

        private sealed class Range
        {
            public readonly Simulation Sim;
            public readonly int Char;
            public readonly int Slot;
            public readonly List<SimEvent> Damage = new List<SimEvent>();
            public readonly List<ProjectileStop> Stops = new List<ProjectileStop>();
            public readonly List<Projectile> Shots = new List<Projectile>();

            public Range(ulong seed = 7, int cx = 10, int cy = 32)
            {
                Sim = new Simulation(Flat(seed));
                for (int x = 0; x < 64; x++)
                {
                    Sim.RequestEdit(new TileCoord(x, cy), FloorId.Grass, ObjectId.Empty); // ряд без деревьев и камней
                }

                Sim.Step();
                Char = Sim.AddEntity(At(cx, cy));
                var inv = Inventory.StartingKit();
                inv.Selected = BowSlot;
                Assert.True(Sim.SetInventory(Char, inv));
                Slot = Sim.SlotOf(Char);
            }

            public void Edit(int x, int y, ushort obj, byte floor = FloorId.Grass) =>
                Assert.True(Sim.RequestEdit(new TileCoord(x, y), floor, obj));

            public void Build(int x, int y, ushort obj, BuildRecord rec) =>
                Assert.True(Sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, obj, rec));

            /// <summary>Такт: два кадра с ЛКМ <paramref name="pressed"/>, прицел <paramref name="aim"/>.</summary>
            public void Tick(bool pressed, ushort aim = East, byte slot = BowSlot)
            {
                ushort b = pressed ? InputButtons.Attack : (ushort)0;
                Span<InputFrame> frames = stackalloc InputFrame[]
                {
                    new InputFrame(Input8.None, b, aim, slot),
                    new InputFrame(Input8.None, b, aim, slot),
                };
                Sim.SetFramesAt(Sim.SlotOf(Char), frames);
                Sim.Step();
                foreach (SimEvent ev in Sim.Events)
                    if (ev.Kind == SimEventKind.Damage)
                        Damage.Add(ev);
                foreach (ProjectileStop s in Sim.StopsThisTick)
                    Stops.Add(s);
                foreach (Projectile p in Sim.ShotsThisTick)
                    Shots.Add(p);
            }

            /// <summary>Натянуть <paramref name="drawTicks"/> тактов, отпустить и ждать <paramref name="flyTicks"/> тактов.</summary>
            public void Shoot(int drawTicks, int flyTicks, ushort aim = East)
            {
                for (int t = 0; t < drawTicks; t++)
                    Tick(true, aim);
                for (int t = 0; t < flyTicks; t++)
                    Tick(false, aim);
            }

            public int Arrows => Sim.GetEntity(Char).Inv.Count(Items.Arrows);
        }

        /// <summary>Приёмка `02` 4.6 п. 1: полное натяжение — 28 т/с и урон 10; 0,2 с — 18 т/с и урон 5. Отбрасывание слабое.</summary>
        [Fact]
        public void E32_Accept_FullDraw28tps_Damage10_MinDraw18tps_Damage5()
        {
            var r = new Range();
            int enemy = r.Sim.AddEnemy(At(20, 32)); // 10 тайлов на восток
            r.Shoot(18, 20); // 36 кадров натяжения
            Assert.Single(r.Shots);
            Assert.Equal(28 * U * 256 / 30, r.Shots[0].SpeedX256);
            Assert.Single(r.Damage);
            Assert.Equal(10, r.Damage[0].Damage);
            Assert.Equal(enemy, r.Damage[0].Victim);
            Assert.Equal(ProjectileEnd.Hit, r.Stops[0].Reason);
            Assert.Equal(49, r.Arrows); // стрела снята
            Assert.Equal(Simulation.MaxHealthCharacter, r.Sim.GetEntity(r.Char).Health);

            var q = new Range();
            q.Sim.AddEnemy(At(20, 32));
            q.Shoot(1, 30); // щелчок: 2 кадра — дотягивается до 0,2 с
            Assert.Single(q.Shots);
            Assert.Equal(18 * U * 256 / 30, q.Shots[0].SpeedX256);
            Assert.Single(q.Damage);
            Assert.Equal(5, q.Damage[0].Damage);
        }

        /// <summary>Время полёта до цели в 10 т по скорости: полное натяжение — ≤ 12 тактов (≈ 0,4 с, `02` 4 «как играется»).</summary>
        [Fact]
        public void E32_Flight_TimeMatchesSpeed()
        {
            var r = new Range();
            r.Sim.AddEnemy(At(20, 32));
            r.Shoot(18, 0);
            long shot = r.Sim.Tick;
            int t = 0;
            while (r.Damage.Count == 0 && t < 40)
            {
                r.Tick(false);
                t++;
            }

            // путь до касания: 10 т − (0,4 + 0,2) т = 9,4 т при 28 т/с = 10,07 тактов
            Assert.InRange(r.Sim.Tick - shot, 10, 12);
        }

        /// <summary>Без целей стрела летит 30 т и кончается дальностью; без стрел — выстрела нет.</summary>
        [Fact]
        public void E32_Range30_AndNoArrows_NoShot()
        {
            var r = new Range();
            r.Shoot(18, 40);
            Assert.Single(r.Stops);
            Assert.Equal(ProjectileEnd.Range, r.Stops[0].Reason);
            int dx = Torus.Delta(Map, At(10, 32), r.Stops[0].Position).X;
            Assert.InRange(dx, 30 * U - 2, 30 * U + 1);
            Assert.Equal(0, r.Sim.Projectiles.Length);

            var empty = new Range();
            var inv = Inventory.StartingKit();
            inv.Selected = BowSlot;
            inv.SetCell(Inventory.Slots, Items.None, 0);
            Assert.Equal(0, inv.Count(Items.Arrows));
            Assert.True(empty.Sim.SetInventory(empty.Char, inv));
            empty.Shoot(18, 5);
            Assert.Empty(empty.Shots);
            Assert.Equal(BowAction.PhaseIdle, empty.Sim.GetEntity(empty.Char).Mover.Bow.Phase);
        }

        /// <summary>Стена, закрытая дверь, скала, дерево, обрыв останавливают стрелу перед собой; урона за ними нет.</summary>
        [Theory]
        [InlineData(ObjectId.StoneWall)]
        [InlineData(ObjectId.Rock)]
        [InlineData(ObjectId.Tree)]
        [InlineData(ObjectId.Cliff)]
        [InlineData(ObjectId.Door)]
        public void E32_Obstacles_StopArrow(ushort obj)
        {
            var r = new Range();
            if (obj == ObjectId.Door)
                r.Build(15, 32, ObjectId.Door, BuildRecord.OfPlayer(1, FloorId.Grass)); // закрытая
            else if (obj == ObjectId.StoneWall)
                r.Build(15, 32, obj, BuildRecord.OfPlayer(1, FloorId.Grass));
            else
                r.Edit(15, 32, obj);
            int enemy = r.Sim.AddEnemy(At(20, 32));
            r.Tick(false);
            Assert.Equal(obj, ObjectId.IdOf(r.Sim.World.GetObject(new TileCoord(15, 32))));
            r.Shoot(18, 30);
            Assert.Empty(r.Damage);
            Assert.Single(r.Stops);
            Assert.Equal(ProjectileEnd.Wall, r.Stops[0].Reason);
            Assert.Equal(new TileCoord(14, 32), Torus.TileOf(Map, r.Stops[0].Position)); // перед преградой, со стороны стрелка
            Assert.Equal(35, r.Sim.GetEntity(enemy).Health);
        }

        /// <summary>Забор, открытая дверь, вода, куст — стрела пролетает (`02` 4.2; C-018) и попадает.</summary>
        [Theory]
        [InlineData(ObjectId.WoodFence, FloorId.Grass)]
        [InlineData(ObjectId.Door, FloorId.Grass)]
        [InlineData(ObjectId.Empty, FloorId.Water)]
        [InlineData(ObjectId.Bush, FloorId.Grass)]
        public void E32_FenceOpenDoorWaterBush_DoNotStop(ushort obj, byte floor)
        {
            var r = new Range();
            if (obj == ObjectId.Door)
            {
                r.Build(15, 32, obj, BuildRecord.OfPlayer(1, FloorId.Grass).WithFlags(BuildFlags.DoorOpen));
                r.Sim.AddEntity(At(15, 32)); // товарищ в проёме держит дверь открытой (автозакрытие C-014); стрела его не задевает
            }
            else if (obj == ObjectId.WoodFence)
                r.Build(15, 32, obj, BuildRecord.OfPlayer(1, FloorId.Grass));
            else
                r.Edit(15, 32, obj, floor);
            r.Sim.AddEnemy(At(20, 32));
            r.Tick(false);
            r.Shoot(18, 30);
            Assert.True(obj != ObjectId.Door || (r.Sim.World.TryGetBuild(new TileCoord(15, 32), out BuildRecord rec) && rec.IsDoorOpen), "дверь открыта");
            Assert.Single(r.Damage);
            Assert.Equal(ProjectileEnd.Hit, r.Stops[0].Reason);
        }

        /// <summary>Стена в полтайла (E2-12): стрела вдоль свободной половины тайла пролетает, в твёрдую половину — нет.</summary>
        [Fact]
        public void E32_HalfWall_ByCells()
        {
            // северная половина тайла (15,32) — стена; по южной четверти (y = 32,75) — попадание, по северной (32,25) — воткнулась
            Assert.Equal(ProjectileEnd.Hit, HalfShot(192));
            Assert.Equal(ProjectileEnd.Wall, HalfShot(64));
        }

        private static ProjectileEnd HalfShot(int sub)
        {
            var s = new Range();
            s.Build(15, 32, ObjectId.WoodWall, BuildRecord.OfPlayer(1, FloorId.Grass).WithCells(BuildCells.DoorNorth));
            s.Tick(false);
            Assert.Equal(BuildCells.DoorNorth, BuildCells.MaskAt(s.Sim.World, new TileCoord(15, 32)));
            s.Sim.RemoveEntity(s.Char);
            int c = s.Sim.AddEntity(new WorldPos(10 * U + U / 2, 32 * U + sub));
            var inv = Inventory.StartingKit();
            inv.Selected = BowSlot;
            Assert.True(s.Sim.SetInventory(c, inv));
            s.Sim.AddEnemy(new WorldPos(20 * U + U / 2, 32 * U + sub));
            ProjectileEnd last = 0;
            var frames = new InputFrame[2];
            for (int t = 0; t < 50; t++)
            {
                ushort b = t < 18 ? InputButtons.Attack : (ushort)0;
                frames[0] = frames[1] = new InputFrame(Input8.None, b, East, BowSlot);
                s.Sim.SetFramesAt(s.Sim.SlotOf(c), frames);
                s.Sim.Step();
                foreach (ProjectileStop st in s.Sim.StopsThisTick)
                    last = st.Reason;
            }

            return last;
        }

        /// <summary>Приёмка `02` 4.6 п. 6: выстрел через шов тора в цель за швом на 5 т попадает.</summary>
        [Fact]
        public void E32_Accept_ThroughTorusSeam()
        {
            var r = new Range(cx: 61);
            int enemy = r.Sim.AddEnemy(At(2, 32)); // 61 → 63 | 0 → 2: 5 тайлов через шов
            r.Shoot(18, 20);
            Assert.Single(r.Damage);
            Assert.Equal(enemy, r.Damage[0].Victim);
            Assert.InRange(r.Stops[0].Position.X, 0, 2 * U + U / 2);

            // и на запад: стрелок у левого края, цель за швом справа
            var w = new Range(cx: 2);
            int e2 = w.Sim.AddEnemy(At(61, 32));
            w.Shoot(18, 20, aim: (ushort)Orpheus.Core.Trig.Angle16.Half);
            Assert.Single(w.Damage);
            Assert.Equal(e2, w.Damage[0].Victim);
        }

        /// <summary>Дружественного огня нет: персонаж на линии не задет, стрела летит дальше в врага.</summary>
        [Fact]
        public void E32_NoFriendlyFire()
        {
            var r = new Range();
            int friend = r.Sim.AddEntity(At(14, 32));
            int enemy = r.Sim.AddEnemy(At(20, 32));
            r.Shoot(18, 20);
            Assert.Single(r.Damage);
            Assert.Equal(enemy, r.Damage[0].Victim);
            Assert.Equal(Simulation.MaxHealthCharacter, r.Sim.GetEntity(friend).Health);
        }

        /// <summary>Снаряд врага (общий механизм, шаг 3.3) ранит персонажа и проходит сквозь врагов; уворот уводит из-под него.</summary>
        [Fact]
        public void E32_HostileProjectile_HitsCharacter_PassesEnemies()
        {
            var r = new Range();
            int shooter = r.Sim.AddEnemy(At(20, 32));
            r.Sim.AddEnemy(At(15, 32)); // свой — пропускает
            int id = r.Sim.SpawnProjectile(shooter, true, Projectiles.KindArrow, At(20, 32), (ushort)Orpheus.Core.Trig.Angle16.Half,
                10 * U * 256 / 30, 20 * U, 10, 0);
            Assert.True(id > 0);
            for (int t = 0; t < 40; t++)
                r.Tick(false);
            Assert.Single(r.Damage);
            Assert.Equal(r.Char, r.Damage[0].Victim);
        }

        /// <summary>
        /// Приёмка `02` 4.6 п. 2, пересмотрена C-031 (`BRIEF` 11n): 100 стрел в стену — у стены лежат все 100, каждая — сама
        /// стрела-предмет (1 штука, вид «в преграде») в точке своей остановки перед стеной (было: 40–60, шанс 50 %, кучами).
        /// </summary>
        [Fact]
        public void E32_Accept_100ShotsIntoWall_AllRemain_C031()
        {
            var r = new Range();
            r.Build(13, 32, ObjectId.StoneWall, BuildRecord.OfPlayer(1, FloorId.Grass));
            var inv = Inventory.StartingKit();
            inv.Selected = BowSlot;
            inv.SetCell(Inventory.Slots, Items.Arrows, 100);
            Assert.True(r.Sim.SetInventory(r.Char, inv));
            r.Tick(false);
            for (int i = 0; i < 100; i++)
            {
                r.Shoot(6, 7); // 12 кадров натяжения + выстрел и отход 12 кадров
            }

            for (int t = 0; t < 10; t++)
                r.Tick(false);

            Assert.Equal(100, r.Shots.Count);
            Assert.Equal(0, r.Arrows); // стрелок в 3 тайлах — до куч у стены не достаёт (подбор 1,5 т)
            int onGround = 0;
            foreach (GroundPile p in r.Sim.Piles)
            {
                if (p.Item == Items.Arrows)
                {
                    onGround += p.Count;
                    Assert.Equal(1, p.Count);
                    Assert.Equal(GroundPile.KindArrowWall, p.Kind);
                    Assert.Equal(new TileCoord(12, 32), Torus.TileOf(Map, p.Position));
                }
            }

            int dropped = 0;
            foreach (ProjectileStop s in r.Stops)
                if (s.Dropped)
                    dropped++;
            Assert.Equal(dropped, onGround);
            Assert.Equal(100, onGround);
        }

        /// <summary>Детерминизм: два прогона с одним вводом — один хэш состояния по тактам; стрельба меняет хэш (стрелы и лук в нём).</summary>
        [Fact]
        public void E32_Determinism_SameInput_SameHash()
        {
            ulong[] Run(bool shoot)
            {
                var r = new Range(seed: 11);
                r.Sim.AddEnemy(At(20, 32));
                r.Build(13, 31, ObjectId.StoneWall, BuildRecord.OfPlayer(1, FloorId.Grass));
                var hashes = new List<ulong>();
                for (int k = 0; k < 6; k++)
                {
                    ushort aim = (ushort)(k * 3000 - 6000);
                    for (int t = 0; t < 10; t++)
                    {
                        r.Tick(shoot && t < 3 + k, aim);
                        hashes.Add(StateHash.Compute(r.Sim).Total);
                    }
                }

                return hashes.ToArray();
            }

            ulong[] a = Run(true), b = Run(true), c = Run(false);
            Assert.Equal(a, b);
            Assert.NotEqual(a[^1], c[^1]);
        }

        /// <summary>
        /// «Золотой» хэш: сценарий лука (натяжения разной силы, оси, стена, враг) — один и тот же хэш состояния на любой машине.
        /// Меняется только осознанно (правила лука или снаряда) — с записью в карточке.
        /// </summary>
        [Fact]
        public void E32_Golden_StateHash()
        {
            var r = new Range(seed: 11);
            r.Sim.AddEnemy(At(20, 32));
            r.Build(13, 31, ObjectId.StoneWall, BuildRecord.OfPlayer(1, FloorId.Grass));
            for (int k = 0; k < 6; k++)
            {
                ushort aim = (ushort)(k * 3000 - 6000);
                for (int t = 0; t < 10; t++)
                    r.Tick(t < 3 + k, aim);
            }

            Assert.True(r.Shots.Count >= 5, $"выстрелов {r.Shots.Count}");
            Assert.Equal(GoldenHash, StateHash.Compute(r.Sim).Total);
        }

        private const ulong GoldenHash = 7982884849753425172UL; // C-031: стрелы остаются всегда (вид и ось в хэше); E3-2: 2109277538302106159

        // -------------------------------------------------------- П E3-2: стык стен углом, туннелирование, куча при попадании, уворот, отказ

        /// <summary>Очистить прямоугольник тайлов от природных объектов (правка применится тактом).</summary>
        private static void ClearArea(Range r, int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (y != 32)
                        r.Sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, ObjectId.Empty);
            r.Tick(false);
        }

        /// <summary>Снаряд персонажа под 45° (на юго-восток) от точки <paramref name="origin"/>; итог — причина конца.</summary>
        private static ProjectileEnd Diagonal(Range r, WorldPos origin, int speedX256 = 28 * U * 256 / 30)
        {
            r.Stops.Clear();
            Assert.True(r.Sim.SpawnProjectile(r.Char, false, Projectiles.KindArrow, origin, 8192, speedX256, 12 * U, 10, 0) > 0);
            for (int t = 0; t < 30 && r.Stops.Count == 0; t++)
                r.Tick(false);
            Assert.Single(r.Stops);
            return r.Stops[0].Reason;
        }

        /// <summary>
        /// П E3-2, п. 1: «лесенка» — две стены, касающиеся углом ((16,32) и (15,33)): стрела под 45° через угол останавливается при
        /// любом смещении линии в ±16 unit от угла (было: проходила при −1…+10). Без стен та же стрела попадает во врага за углом.
        /// </summary>
        [Fact]
        public void E32_Staircase45_CornerJoint_StopsArrow()
        {
            var r = new Range();
            ClearArea(r, 9, 26, 22, 38);
            var corner = new WorldPos(16 * U, 33 * U);
            for (int o = -16; o <= 16; o++)
            {
                int e = r.Sim.AddEnemy(new WorldPos(corner.X + 3 * U + o, corner.Y + 3 * U));
                Assert.Equal(ProjectileEnd.Hit, Diagonal(r, new WorldPos(corner.X - 5 * U + o, corner.Y - 5 * U))); // без стен — попал
                r.Sim.RemoveEntity(e);
            }

            r.Build(16, 32, ObjectId.StoneWall, BuildRecord.OfPlayer(1, FloorId.Grass));
            r.Build(15, 33, ObjectId.StoneWall, BuildRecord.OfPlayer(1, FloorId.Grass));
            r.Tick(false);
            for (int o = -16; o <= 16; o++)
            {
                int e = r.Sim.AddEnemy(new WorldPos(corner.X + 3 * U + o, corner.Y + 3 * U));
                Assert.True(Diagonal(r, new WorldPos(corner.X - 5 * U + o, corner.Y - 5 * U)) == ProjectileEnd.Wall, $"смещение {o}");
                Assert.Equal(35, r.Sim.GetEntity(e).Health);
                r.Sim.RemoveEntity(e);
            }
        }

        /// <summary>П E3-2, п. 1: та же «лесенка» на ячейках 0,5 внутри одного тайла (СВ и ЮЗ ячейки — стена) — стык закрыт.</summary>
        [Fact]
        public void E32_Staircase45_HalfCells_StopsArrow()
        {
            var r = new Range();
            ClearArea(r, 9, 26, 22, 38);
            byte mask = (byte)(BuildCells.Bit(1, 0) | BuildCells.Bit(0, 1));
            r.Build(15, 33, ObjectId.WoodWall, BuildRecord.OfPlayer(1, FloorId.Grass).WithCells(mask));
            r.Tick(false);
            Assert.Equal(mask, BuildCells.MaskAt(r.Sim.World, new TileCoord(15, 33)));
            var corner = new WorldPos(15 * U + U / 2, 33 * U + U / 2);
            for (int o = -16; o <= 16; o++)
            {
                Assert.True(Diagonal(r, new WorldPos(corner.X - 5 * U + o, corner.Y - 5 * U)) == ProjectileEnd.Wall, $"смещение {o}");
            }
        }

        /// <summary>
        /// П E3-2, п. 1: туннелирования нет — стена в полтайла (ячейки 0,5 по западному краю) останавливает стрелу при любой
        /// скорости: 28 т/с и 3 тайла за такт (шаг такта больше ширины стены вшестеро), прямо и под углом.
        /// </summary>
        [Theory]
        [InlineData(28 * U * 256 / 30, 0)]
        [InlineData(3 * U * 256, 0)]
        [InlineData(3 * U * 256, 4000)]
        [InlineData(5 * U * 256, -3000)]
        public void E32_ThinWall_NoTunneling(int speedX256, int aim)
        {
            var r = new Range();
            ClearArea(r, 9, 26, 22, 38);
            for (int y = 27; y <= 37; y++)
                r.Build(15, y, ObjectId.WoodWall, BuildRecord.OfPlayer(1, FloorId.Grass).WithCells(BuildCells.DoorWest)); // столбец стены в полтайла
            r.Tick(false);
            int e = r.Sim.AddEnemy(At(20, 32));
            Assert.True(r.Sim.SpawnProjectile(r.Char, false, Projectiles.KindArrow, new WorldPos(12 * U + U / 2, 32 * U + U / 2), (ushort)aim,
                speedX256, 20 * U, 10, 0) > 0);
            for (int t = 0; t < 20 && r.Stops.Count == 0; t++)
                r.Tick(false);
            Assert.Single(r.Stops);
            Assert.Equal(ProjectileEnd.Wall, r.Stops[0].Reason);
            Assert.True(Torus.Delta(Map, r.Stops[0].Position, new WorldPos(15 * U, 32 * U)).X >= 0, "остановилась перед стеной");
            Assert.Equal(35, r.Sim.GetEntity(e).Health);
        }

        /// <summary>П E3-2, п. 3 (`02` 4.2): попавшая в существо стрела не остаётся кучей — 20 разных миров, ни одной кучи.</summary>
        [Fact]
        public void E32_HitArrow_LeavesNoPile()
        {
            int hits = 0;
            for (ulong seed = 1; seed <= 20; seed++)
            {
                var r = new Range(seed: seed);
                r.Sim.AddEnemy(At(14, 32));
                r.Shoot(18, 20);
                Assert.Single(r.Stops);
                Assert.Equal(ProjectileEnd.Hit, r.Stops[0].Reason);
                Assert.False(r.Stops[0].Dropped);
                hits++;
                foreach (GroundPile p in r.Sim.Piles)
                    Assert.NotEqual(Items.Arrows, p.Item);
            }

            Assert.Equal(20, hits);
        }

        /// <summary>
        /// П E3-2, п. 4 (`02` 4.6 п. 5): снаряд врага летит в персонажа; рывок навстречу стреле в последний миг — стрела проходит
        /// сквозь (неуязвимость рывка), урона нет; без рывка — попадание.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void E32_DodgeInvulnerability_ArrowPassesThrough(bool dodge)
        {
            var r = new Range();
            int shooter = r.Sim.AddEnemy(At(24, 32));
            Assert.True(r.Sim.SpawnProjectile(shooter, true, Projectiles.KindArrow, At(22, 32), Orpheus.Core.Trig.Angle16.Half,
                20 * U * 256 / 30, 20 * U, 10, 0) > 0);
            bool dashed = false;
            var frames = new InputFrame[2];
            for (int t = 0; t < 40; t++)
            {
                WorldPos me = r.Sim.GetEntity(r.Char).Mover.Position;
                bool press = false;
                if (dodge && !dashed && r.Sim.Projectiles.Length > 0)
                {
                    Projectile p = r.Sim.Projectiles[0];
                    int dx = Torus.Delta(Map, me, Torus.Wrap(Map, p.PositionAt(p.Age))).X;
                    press = dx < 2 * U; // стрела в 2 т — рывок на восток, навстречу ей (остаётся на линии)
                }

                frames[0] = new InputFrame(press ? Input8.East : Input8.None,
                    press ? InputButtons.Dodge : (ushort)0, 0, BowSlot);
                frames[1] = new InputFrame(press ? Input8.East : Input8.None, 0, 0, BowSlot);
                dashed |= press;
                r.Sim.SetFramesAt(r.Sim.SlotOf(r.Char), frames);
                r.Sim.Step();
                foreach (SimEvent ev in r.Sim.Events)
                    if (ev.Kind == SimEventKind.Damage)
                        r.Damage.Add(ev);
            }

            if (dodge)
            {
                Assert.True(dashed);
                Assert.Empty(r.Damage);
                Assert.Equal(Simulation.MaxHealthCharacter, r.Sim.GetEntity(r.Char).Health);
            }
            else
            {
                Assert.Single(r.Damage);
                Assert.Equal(r.Char, r.Damage[0].Victim);
            }
        }

        /// <summary>
        /// П E3-2, п. 5: автомат лука выстрелил, а стрел уже нет (выброшены в натяжении) — снаряда нет, в такте — запись «отказ»
        /// (номер 0, владелец — стрелок) для клиента.
        /// </summary>
        [Fact]
        public void E32_ShotWithoutArrows_Refused()
        {
            var r = new Range();
            for (int t = 0; t < 10; t++)
                r.Tick(true);
            Assert.Equal(BowAction.PhaseDraw, r.Sim.GetEntity(r.Char).Mover.Bow.Phase);
            Assert.True(r.Sim.RequestBagOp(r.Char, BagOp.Drop, Inventory.Slots, Inventory.Slots, 0)); // все 50 стрел на землю
            r.Tick(true);
            Assert.Equal(0, r.Arrows);
            r.Stops.Clear();
            for (int t = 0; t < 5; t++)
                r.Tick(false);
            Assert.Empty(r.Shots);
            Assert.Single(r.Stops);
            Assert.Equal(ProjectileEnd.Refused, r.Stops[0].Reason);
            Assert.Equal(0, r.Stops[0].Id);
            Assert.Equal(r.Char, r.Stops[0].Owner);
            Assert.Equal(0, r.Sim.Projectiles.Length);
        }
    }
}
