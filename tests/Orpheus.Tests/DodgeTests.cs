using System;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.Trig;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Уворот и ближний удар врага в ядре (карточка 0-029, шаги 1, 2, 6): правила `02` 2.2 и 2.4, приёмка `02` 2.7 п. 1 и 4,
    /// сектор `02` 3.1 (размер — «Ближний» из 8.1), очередь угроз `07` 2.7. Время — кадры ввода 60 Гц; удар решается по
    /// состоянию персонажа после кадра конца замаха (s + d).
    /// </summary>
    public class DodgeTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const int R = Collision.RadiusUnits;

        private static readonly MapShape Map = MapShape.World(64);

        // Ряд стен y = 15 (тайлы x 5…40): нижняя грань — y = 16 × 256 = 4096. Персонаж в упор к ней — y = 4096 + 102.
        private const int WallRow = 15;
        private const int WallFaceY = (WallRow + 1) * U;
        private static readonly TileSolid Wall = (MapShape _, TileCoord t) => t.Y == WallRow && t.X >= 5 && t.X <= 40;

        private readonly ITestOutputHelper _out;

        public DodgeTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- Сценарий `02` 2.7: манекен бьёт на восток, персонаж в секторе ----------

        private struct Outcome
        {
            public bool Hit;
            public Mover AtContact;
            public uint Contact;
            public uint Press;
        }

        /// <summary>
        /// Манекен в (x0, y), бьёт на восток; персонаж в 1,4 т восточнее — на досягаемость «тело + меч» (в секторе: 358 &lt; 358 + 102). Свободный путь до
        /// стены на север — <paramref name="gapUnits"/> (≤ 0 — стены нет). Уворот на север нажат за
        /// <paramref name="pressBefore"/> кадров до касания (−1 — без уворота). Удар — по состоянию после кадра касания.
        /// </summary>
        private static Outcome Run(int gapUnits, int pressBefore, Input8? dir = null)
        {
            int y = gapUnits > 0 ? WallFaceY + R + gapUnits : WallFaceY + R + 4 * U;
            TileSolid solid = gapUnits > 0 ? Wall : Collision.Empty;
            var dummyPos = new WorldPos(20 * U + U / 2, y);
            var dummy = new MeleeDummy(7, dummyPos, Angle16.FromRaw(0), firstSwingFrame: 100);
            var me = Mover.At(Map, new WorldPos(dummyPos.X + EnemyMelee.ReachUnits, y));
            var queue = new ThreatQueue(4);
            uint press = 100 + EnemyMelee.WindupFrames - (uint)pressBefore;
            var o = new Outcome { Press = press, Contact = 100 + EnemyMelee.WindupFrames };
            Input8 d = dir ?? Input8.North;
            for (uint f = 60; f <= o.Contact; f++)
            {
                if (dummy.Step(f, out Threat swing))
                {
                    Assert.True(queue.Add(swing));
                }

                bool pressed = pressBefore >= 0 && f == press;
                me.StepFrame(Map, pressed ? d : Input8.None, pressed, solid);
                if (queue.Count > 0 && f == queue[0].ContactFrame)
                {
                    o.Hit = MeleeHit.Lands(Map, queue[0], me);
                    o.AtContact = me;
                }
            }

            Assert.Equal(1, queue.Count);
            Assert.Equal(100u, queue[0].StartFrame);
            return o;
        }

        /// <summary>Контроль сценария: без уворота удар попадает (персонаж в секторе); уворот, нажатый в кадре касания, спасает.</summary>
        [Fact]
        public void D02_S2_7_Scenario_NoDodge_Hit_PressOnContactFrame_NoHit()
        {
            Assert.True(Run(0, -1).Hit);
            Assert.True(Run(U / 2, -1).Hit);
            Assert.True(Run(U, -1).Hit);
            Assert.False(Run(0, 0).Hit); // нажат в кадре касания: рывок уже идёт, неуязвим
        }

        /// <summary>`02` 2.7 п. 1, первая часть: уворот за 0,05 с (3 кадра) до касания — 0 урона, хотя персонаж ещё в секторе.</summary>
        [Fact]
        public void D02_S2_7_P1_Dodge005sBeforeContact_NoHit()
        {
            Outcome o = Run(0, 3);
            Assert.False(o.Hit);
            Assert.True(o.AtContact.Dodge.Invulnerable);
            Assert.True(o.AtContact.Dodge.Dashing);
            var sector = new MeleeSector(new WorldPos(20 * U + U / 2, WallFaceY + R + 4 * U), Angle16.FromRaw(0), EnemyMelee.ReachUnits, EnemyMelee.HalfArcSteps);
            Assert.True(sector.Overlaps(Map, o.AtContact.Position, R)); // в секторе — спасла неуязвимость
            _out.WriteLine($"за 3 кадра: {o.AtContact}");
        }

        /// <summary>
        /// `02` 2.7 п. 1, вторая часть: уворот за 0,22 с (13 кадров) до касания рывком в стену в 0,5 т — персонаж упёрся,
        /// остался в секторе, неуязвимость (12 кадров) кончилась — урон есть. Граница: за 11 кадров — нет, за 12 (ровно 0,20 с) — есть.
        /// </summary>
        [Fact]
        public void D02_S2_7_P1_Dodge022sIntoWallHalfTile_Hit()
        {
            Outcome o = Run(U / 2, 13);
            _out.WriteLine($"за 13 кадров в стену 0,5 т: {o.AtContact}, попал {o.Hit}");
            Assert.True(o.Hit);
            Assert.False(o.AtContact.Dodge.Invulnerable);
            Assert.Equal(WallFaceY + R, o.AtContact.Position.Y); // у стены: прошёл 0,5 т и встал
            Assert.False(o.AtContact.Dodge.Dashing);

            Assert.False(Run(U / 2, 11).Hit);
            Assert.True(Run(U / 2, 12).Hit);
        }

        /// <summary>
        /// `02` 2.7 п. 4: уворот в стену с 1 т — остановка у стены (рывок кончился на 6-м кадре: 5 кадров × 51,2 = 256 unit),
        /// урона нет; неуязвимость 0,2 с от начала держится и после остановки (2.4), за 11 кадров до касания — всё ещё спасает.
        /// </summary>
        [Fact]
        public void D02_S2_7_P4_DodgeIntoWallFromOneTile_StopsAtWall_InvulnFull()
        {
            Outcome o = Run(U, 6);
            _out.WriteLine($"за 6 кадров в стену 1 т: {o.AtContact}, попал {o.Hit}");
            Assert.False(o.Hit);
            Assert.Equal(WallFaceY + R, o.AtContact.Position.Y);
            Assert.Equal(20 * U + U / 2 + EnemyMelee.ReachUnits, o.AtContact.Position.X);
            Assert.False(o.AtContact.Dodge.Dashing);
            Assert.Equal(7, o.AtContact.Dodge.Elapsed);
            Assert.Equal(Dodge.CooldownFrames - 1, o.AtContact.Dodge.Cooldown); // рывок кончился в 6-м кадре, откат идёт с 7-го

            Outcome late = Run(U, 11);
            Assert.False(late.Hit);
            Assert.True(late.AtContact.Dodge.Invulnerable);
            Assert.Equal(WallFaceY + R, late.AtContact.Position.Y);
            Assert.True(Run(U, 12).Hit);
        }

        // ---------- Правила уворота `02` 2.2, 2.4 ----------

        private static Mover Dash(Input8 dir, int frames, out int dashFrames)
        {
            var m = Mover.At(Map, new WorldPos(30 * U, 30 * U));
            dashFrames = 0;
            for (int f = 0; f < frames; f++)
            {
                bool wasDashing = m.Dodge.Dashing;
                m.StepFrame(Map, f == 0 ? dir : Input8.None, f == 0, Collision.Empty);
                if (wasDashing || f == 0)
                {
                    dashFrames++; // кадр рывка: начатый в нём или шедший до него
                }
            }

            return m;
        }

        /// <summary>Рывок 3,0 т за 0,25 с: 15 кадров, ровно 768 unit по прямой; диагональ — 543 на ось (767,9 unit); ввод в рывке не действует.</summary>
        [Fact]
        public void D02_S2_2_Dash_3Tiles_In15Frames()
        {
            Mover m = Dash(Input8.East, 40, out int frames);
            Assert.Equal(Dodge.DashFrames, frames);
            Assert.Equal(new WorldPos(30 * U + 3 * U, 30 * U), m.Position);

            Mover d = Dash(Input8.SouthWest, 40, out _);
            Assert.Equal(new WorldPos(30 * U - 543, 30 * U + 543), d.Position);

            // Ввод против рывка не тормозит его: 15 кадров на запад при рывке на восток — те же 3 тайла.
            var a = Mover.At(Map, new WorldPos(30 * U, 30 * U));
            a.StepFrame(Map, Input8.East, true, Collision.Empty);
            for (int f = 1; f < Dodge.DashFrames; f++)
            {
                a.StepFrame(Map, Input8.West, false, Collision.Empty);
            }

            Assert.Equal(30 * U + 3 * U, a.Position.X);
        }

        /// <summary>
        /// Неуязвимость — кадры 1…12 от начала (0,00–0,20 с), откат 0,40 с после конца рывка: следующий уворот — не раньше
        /// 39-го кадра (0,65 с); нажатие в рывке и в откате пропадает; без направления уворота нет (вопрос `[док]` карточки).
        /// </summary>
        [Fact]
        public void D02_S2_2_Invuln12_Cooldown24_NextAt39_NoDirectionNoDodge()
        {
            var m = Mover.At(Map, new WorldPos(30 * U, 30 * U));
            m.StepFrame(Map, Input8.None, true, Collision.Empty);
            Assert.False(m.Dodge.Dashing);
            Assert.Equal(0, m.Dodge.Elapsed);
            Assert.Equal(new WorldPos(30 * U, 30 * U), m.Position);

            m.StepFrame(Map, Input8.North, true, Collision.Empty); // кадр 0 рывка
            for (int f = 1; f < 60; f++)
            {
                Assert.Equal(f <= Dodge.InvulnFrames, m.Dodge.Invulnerable);
                bool press = true; // держит каждый кадр: срабатывает, только когда готов
                Input8 dir = Input8.East;
                bool wasReady = m.Dodge.Ready;
                m.StepFrame(Map, dir, press, Collision.Empty);
                if (f < Dodge.DashFrames + Dodge.CooldownFrames)
                {
                    Assert.False(wasReady, $"кадр {f}: уворот готов раньше 39-го");
                }
                else if (f == Dodge.DashFrames + Dodge.CooldownFrames)
                {
                    Assert.True(wasReady, "кадр 39: уворот не готов");
                    Assert.Equal(1, m.Dodge.Elapsed);
                    Assert.Equal(Input8.East.DirectionIndex + 1, m.Dodge.Direction);
                    return;
                }
            }

            Assert.Fail("второй уворот не начался");
        }

        /// <summary>Рывок вдоль стены по диагонали скользит (упёрта одна ось); в угол — кончается в кадре упора; через шов тора — обычный.</summary>
        [Fact]
        public void D02_S2_4_DiagonalSlides_CornerStops_TorusSeam()
        {
            // Упор по Y (стена сверху), рывок на северо-восток: X идёт все 15 кадров.
            var m = Mover.At(Map, new WorldPos(20 * U, WallFaceY + R + 10));
            m.StepFrame(Map, Input8.NorthEast, true, Wall);
            int frames = 1;
            while (m.Dodge.Dashing)
            {
                m.StepFrame(Map, Input8.None, false, Wall);
                frames++;
            }

            Assert.Equal(Dodge.DashFrames, frames);
            Assert.Equal(20 * U + 543, m.Position.X);
            Assert.Equal(WallFaceY + R, m.Position.Y);

            // Угол: стена сверху и справа — оба упора, рывок кончается сразу.
            TileSolid corner = (MapShape _, TileCoord t) => t.Y == WallRow || t.X == 25;
            var c = Mover.At(Map, new WorldPos(25 * U - R, WallFaceY + R));
            c.StepFrame(Map, Input8.NorthEast, true, corner);
            Assert.False(c.Dodge.Dashing);
            Assert.True(c.Dodge.Invulnerable);
            Assert.Equal(Dodge.CooldownFrames, c.Dodge.Cooldown);

            // Шов тора: рывок на восток из x = 63,5 т — через край в 2,5 т.
            var s = Mover.At(Map, new WorldPos(64 * U - U / 2, 10 * U));
            s.StepFrame(Map, Input8.East, true, Collision.Empty);
            for (int f = 1; f < 20; f++)
            {
                s.StepFrame(Map, Input8.None, false, Collision.Empty);
            }

            Assert.Equal(new WorldPos(2 * U + U / 2, 10 * U), s.Position);
        }

        // ---------- Сектор `02` 3.1, угроза и манекен `07` 2.7 ----------

        /// <summary>
        /// Сектор «Ближнего» (дуга 90°, «персонаж + оружие» = 0,4 + 1,0 = 1,4 т, 0-034): по оси — до 1,4 т + радиус (касание —
        /// мимо); сзади — мимо; сбоку у края дуги круг задевает край сектора; через шов тора — так же.
        /// </summary>
        [Fact]
        public void D02_S3_1_Sector_ReachArcEdgesSeam()
        {
            var o = new WorldPos(10 * U, 10 * U);
            var sec = new MeleeSector(o, Angle16.FromRaw(0), EnemyMelee.ReachUnits, EnemyMelee.HalfArcSteps);
            Assert.Equal(358, EnemyMelee.ReachUnits); // 102 + 256 (было 384 = 1,5 т)
            Assert.Equal(Reach.BodyRadiusUnits + Reach.SwordUnits, EnemyMelee.ReachUnits);
            int far = EnemyMelee.ReachUnits + R;
            Assert.False(sec.Overlaps(Map, new WorldPos(o.X + 384 + R - 1, o.Y), R)); // прежние 1,5 т — уже мимо
            Assert.True(sec.Overlaps(Map, new WorldPos(o.X + far - 1, o.Y), R));
            Assert.False(sec.Overlaps(Map, new WorldPos(o.X + far, o.Y), R));
            Assert.True(sec.Overlaps(Map, o, R));
            Assert.False(sec.Overlaps(Map, new WorldPos(o.X - 200, o.Y), R)); // сзади, дальше радиуса от центра
            Assert.True(sec.Overlaps(Map, new WorldPos(o.X - 100, o.Y), R)); // сзади, но круг накрывает центр сектора

            // Сбоку: центр на 90° (вне дуги), на 300 unit к югу — до края-отрезка под 45° расстояние 300·sin45° = 212 > 102 — мимо;
            // центр под 60°, на 300: до края 300·sin15° = 77,6 < 102 — задевает.
            Assert.False(sec.Overlaps(Map, new WorldPos(o.X, o.Y + 300), R));
            Int2 at60 = Angle16.FromRaw(Angle16.Steps / 6).Direction(300);
            Assert.True(sec.Overlaps(Map, new WorldPos(o.X + at60.X, o.Y + at60.Y), R));
            Int2 at80 = Angle16.FromRaw(Angle16.Steps * 80 / 360).Direction(300);
            Assert.False(sec.Overlaps(Map, new WorldPos(o.X + at80.X, o.Y + at80.Y), R)); // 300·sin35° = 172 > 102

            // Шов: манекен у x = 63,8 т бьёт на восток, персонаж у x = 0,8 т (1 т через шов) — попал.
            var seam = new MeleeSector(new WorldPos(64 * U - 51, 10 * U), Angle16.FromRaw(0), EnemyMelee.ReachUnits, EnemyMelee.HalfArcSteps);
            Assert.True(seam.Overlaps(Map, new WorldPos(U - 51, 10 * U), R));
        }

        /// <summary>
        /// Манекен: замах по таймеру с периодом не короче цикла 1,1 с; событие «замах начат» несёт кадр начала, касание —
        /// через d = 0,4 с (24 кадра). Очередь угроз держит угрозу 1 с от начала (60 кадров), полна — отказ.
        /// </summary>
        [Fact]
        public void D07_S2_7_DummyTimer_ThreatQueueOneSecond()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new MeleeDummy(1, default, default, 0, EnemyMelee.CycleFrames - 1));
            var dummy = new MeleeDummy(1, new WorldPos(5 * U, 5 * U), Angle16.FromRaw(Angle16.Quarter), firstSwingFrame: uint.MaxValue - 10, periodFrames: 70);
            var q = new ThreatQueue(2);
            int swings = 0;
            uint first = 0;
            for (uint k = 0; k < 200; k++)
            {
                uint f = unchecked(uint.MaxValue - 20 + k); // через переполнение номера кадра
                q.Expire(f);
                if (dummy.Step(f, out Threat t))
                {
                    if (swings == 0)
                    {
                        first = f;
                    }

                    Assert.Equal(unchecked(first + (uint)(70 * swings)), t.StartFrame);
                    Assert.Equal(unchecked(t.StartFrame + 24u), t.ContactFrame);
                    Assert.Equal(EnemyMelee.ReachUnits, t.Sector.ReachUnits);
                    Assert.True(q.Add(t));
                    swings++;
                }

                Assert.True(q.Count <= 1, $"кадр {f}: угроз {q.Count} — срок 1 с не соблюдён");
                if (q.Count == 1)
                {
                    Assert.True(unchecked((int)(f - q[0].StartFrame)) < ThreatQueue.LifetimeFrames);
                }
            }

            Assert.Equal(unchecked(uint.MaxValue - 10), first);
            Assert.Equal(3, swings);
            var full = new ThreatQueue(1);
            Assert.True(full.Add(default));
            Assert.False(full.Add(default));
        }

        /// <summary>Кнопка уворота — бит кнопок: кадр в журнале — те же 3 байта, по сети — те же 6.</summary>
        [Fact]
        public void D02_S2_2_DodgeButton_FrameFormatUnchanged()
        {
            Assert.Equal(3, InputFrame.WireBytes);
            Assert.Equal(6, Orpheus.Net.Protocol.WireFrame.Bytes);
            var f = new InputFrame(Input8.North, InputButtons.Dodge);
            Span<byte> b = stackalloc byte[InputFrame.WireBytes];
            f.Write(b);
            InputFrame back = InputFrame.Read(b);
            Assert.True(back.DodgePressed);
            Assert.Equal(f, back);
            Assert.True(Orpheus.Net.Protocol.WireFrame.From(f).ToInputFrame().DodgePressed);
            Assert.False(new InputFrame(Input8.North).DodgePressed);
        }
    }
}
