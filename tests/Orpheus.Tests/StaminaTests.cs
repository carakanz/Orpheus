using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Выносливость (карточка 1-003, `02` 2.2–2.3, приёмка 2.7 п. 2): запас 100, уворот −25, реген 20/с
    /// ( +1 за 3 кадра) через 0,5 с (30 кадров) после последней траты; при &lt; 25 — «щёлк» без рывка.
    /// Всё в кадрах ввода 60 Гц: уворот готов каждые 15 + 24 = 39 кадров.
    /// </summary>
    public class StaminaTests
    {
        private const int U = Torus.SubTilesPerTile;
        private static readonly MapShape Map = MapShape.World(64);

        private static WorldPos At(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        /// <summary>
        /// Приёмка `02` 2.7 п. 2 (карточка 1-003, чек-лист 1): четыре уворота подряд — каждый сразу после отката
        /// (кадры 0, 39, 78, 117) — проходят; пятый на 156-м — нет («щёлк», выносливость 12). Через 1,25 с после
        /// четвёртого (кадр 192) — нет (24), через 1,30 с (кадр 195) — проходит (25). Тест в кадрах, числа точные.
        /// </summary>
        [Fact]
        public void D03_S2_7_FourDodgesThenFifth_InFrames()
        {
            var me = Mover.At(Map, At(20, 32));
            Assert.Equal(Stamina.Max, me.Stamina.Value);

            for (int f = 0; f <= 195; f++)
            {
                bool pressed = f is 0 or 39 or 78 or 117 or 156 or 192 or 195;
                if (pressed)
                {
                    Assert.True(me.Dodge.Ready); // каждое нажатие — в кадре готовности (сразу после отката)
                }

                me.StepFrame(Map, pressed ? Input8.East : Input8.None, pressed, Collision.Empty);

                switch (f)
                {
                    case 0: // первый: полный запас, трата в кадре начала
                        Assert.True(me.DashStarted);
                        Assert.Equal(75, me.Stamina.Value);
                        break;
                    case 39: // реген за паузу: 3 пункта (кадры 31, 34, 37) → 78
                        Assert.True(me.DashStarted);
                        Assert.Equal(78 - 25, me.Stamina.Value);
                        break;
                    case 78: // 56 → 31
                        Assert.True(me.DashStarted);
                        Assert.Equal(56 - 25, me.Stamina.Value);
                        break;
                    case 117: // 34 → 9: четвёртый — последний
                        Assert.True(me.DashStarted);
                        Assert.Equal(34 - 25, me.Stamina.Value);
                        break;
                    case 156: // пятый: реген 12 (148, 151, 154) → «щёлк», рывка нет
                        Assert.False(me.DashStarted);
                        Assert.True(me.Stamina.Clicked);
                        Assert.Equal(12, me.Stamina.Value);
                        Assert.Equal(0, me.Dodge.DashLeft);
                        break;
                    case 192: // через 1,25 с после четвёртого: 9 + 15 = 24 → нет (готовность в этот кадр — «щёлк»)
                        Assert.False(me.DashStarted);
                        Assert.True(me.Stamina.Clicked);
                        Assert.Equal(24, me.Stamina.Value);
                        break;
                    case 195: // через 1,30 с: 9 + 16 = 25 → проходит, в остаток 0
                        Assert.True(me.DashStarted);
                        Assert.Equal(0, me.Stamina.Value);
                        break;
                    default:
                        Assert.False(me.DashStarted);
                        Assert.False(me.Stamina.Clicked);
                        break;
                }
            }
        }

        /// <summary>Реген в кадрах: пауза ровно 30 кадров после траты, затем +1 каждые 3; до траты (свежий) запас полон.</summary>
        [Fact]
        public void D03_Stamina_Regen_Pause30ThenEvery3()
        {
            var me = Mover.At(Map, At(20, 32));
            me.StepFrame(Map, Input8.East, true, Collision.Empty); // трата в кадре 0 → 75
            Assert.Equal(75, me.Stamina.Value);

            for (int f = 1; f <= 254; f++)
            {
                me.StepFrame(Map, Input8.None, false, Collision.Empty);
                int expected = f switch
                {
                    < 31 => 75,                                    // пауза 0,5 с: кадров 1…30 — без регена
                    >= 31 + 25 * 3 => Stamina.Max,                 // 100, дальше реген не растёт
                    _ => 75 + (f - 31) / 3 + 1,                    // кадры 31, 34, 37, … — по пункту
                };
                Assert.Equal(expected, me.Stamina.Value);
            }
        }

        /// <summary>Во время рывка реген прерывается (`02` 2.2, строка «во время рывка»): проверка на прямом <see cref="Stamina.Tick"/>.</summary>
        [Fact]
        public void D03_Stamina_Regen_PausedDuringDash()
        {
            var s = new Stamina { Value = 50, SinceSpend = Stamina.SinceSpendCap, RegenAcc = Stamina.RegenFramesPerPoint - 1 };
            for (int f = 0; f < 30; f++)
            {
                s.Tick(dashing: true);
            }

            Assert.Equal(50, s.Value);
            s.Tick(dashing: false);
            Assert.Equal(51, s.Value); // аккумулятор был в фазе «первый пункт следующим тиком»
        }

        /// <summary>Во время отбрасывания реген не прерывается (`02` 2.2, строка «во время отбрасывания»); уворот в это время недоступен.</summary>
        [Fact]
        public void D03_Stamina_Regen_ContinuesDuringKnock()
        {
            var me = Mover.At(Map, At(20, 32));
            me.StepFrame(Map, Input8.East, true, Collision.Empty); // трата в кадре 0 → 75
            me.StartKnock(Map, At(19, 32), 1, Input8.East); // источник с запада → отбрасывание на восток, 9 кадров

            for (int f = 1; f <= 40; f++)
            {
                me.StepFrame(Map, Input8.None, false, Collision.Empty);
            }

            // кадры 31, 34, 37, 40 — реген шёл сквозь отбрасывание: 75 + 4 = 79
            Assert.Equal(79, me.Stamina.Value);
            Assert.Equal(0, me.KnockLeft);
        }

        /// <summary>«Щёлк» (2.3): нажатие при готовом увороте и направлении, но выносливости &lt; 25 — рывка нет,
        /// событие на один кадр; готовность уворота при этом не тратится (следующий реген → рывок проходит).
        /// Нажатие в откате — не «щёлк» (буфер нажатий — 0-033).</summary>
        [Fact]
        public void D03_Stamina_Click_OneFrameOnly()
        {
            var me = Mover.At(Map, At(20, 32));
            for (int f = 0; f <= 117; f++)
            {
                bool pressed = f is 0 or 39 or 78 or 117;
                me.StepFrame(Map, pressed ? Input8.East : Input8.None, pressed, Collision.Empty);
            }

            Assert.Equal(9, me.Stamina.Value); // после четвёртого

            // кадр 118: шаг без нажатия
            me.StepFrame(Map, Input8.None, false, Collision.Empty);
            Assert.False(me.Stamina.Clicked);

            // кадры 119…155: шаг без нажатия; нажатие в 119-м — рывок ещё идёт, не «щёлк»
            for (int f = 119; f < 156; f++)
            {
                me.StepFrame(Map, f == 119 ? Input8.East : Input8.None, f == 119, Collision.Empty);
                Assert.False(me.Stamina.Clicked);
                Assert.False(me.DashStarted);
            }

            // кадр 156: готовность, запас 12 — «щёлк» на один кадр, рывка нет, готовность не потрачена
            me.StepFrame(Map, Input8.East, true, Collision.Empty);
            Assert.True(me.Stamina.Clicked);
            Assert.False(me.DashStarted);
            Assert.Equal(12, me.Stamina.Value);
            Assert.True(me.Dodge.Ready);

            me.StepFrame(Map, Input8.None, false, Collision.Empty);
            Assert.False(me.Stamina.Clicked);
        }

        /// <summary>Флаг «рывок начат в кадре» (шаг 3, хук для 1-004/1-006): истинен ровно в кадре начала рывка.</summary>
        [Fact]
        public void D03_DashStarted_OneFrameFlag()
        {
            var me = Mover.At(Map, At(20, 32));
            me.StepFrame(Map, Input8.East, true, Collision.Empty);
            Assert.True(me.DashStarted);
            me.StepFrame(Map, Input8.East, false, Collision.Empty);
            Assert.False(me.DashStarted);
        }

        /// <summary>Без направления уворота нет (BRIEF 11b, 0-034): нажатие с Input8.None — ни рывка, ни «щёлка», ни траты.</summary>
        [Fact]
        public void D03_Stamina_PressWithoutDirection_Nothing()
        {
            var me = Mover.At(Map, At(20, 32));
            for (int f = 0; f < 130; f++)
            {
                me.StepFrame(Map, Input8.None, f == 0, Collision.Empty);
                Assert.False(me.DashStarted);
                Assert.False(me.Stamina.Clicked);
            }

            Assert.Equal(Stamina.Max, me.Stamina.Value); // траты не было
        }
    }
}
