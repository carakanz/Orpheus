using System;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Часы мира клиента (карточка 1-014, <c>01</c> 6 «Сеть»): интерполяция между снимками, рывок при расхождении
    /// больше 2 с, сглаживание ходом ±1 %, пауза (0-036), круг суток. Времени внутри часов нет — мкс задаёт тест.
    /// </summary>
    public class WorldClockTests
    {
        private const int Day = DayClock.TicksPerDay; // 36 000
        private const int PerSecond = DayClock.TicksPerSecond; // 30
        private const long Second = 1_000_000L;

        private static void Feed(WorldClock c, int startTick, long startUs, long untilUs, long gapUs)
        {
            for (long t = startUs; t <= untilUs; t += gapUs)
            {
                c.OnSample(DayClock.Wrap(startTick + (int)((t - startUs) * PerSecond / Second)), 0, t);
            }
        }

        [Fact]
        public void W01_WorldClock_TracksSamples_StaysWithinOneTick()
        {
            var c = new WorldClock();
            // Снимки раз в 200 мс (чаще требования «раз в 5 с»), оwd нулевой — часы идут местными мкс между ними.
            Feed(c, 1000, 0, 30 * Second, 200_000);
            for (long t = 0; t <= 30 * Second; t += 7_000)
            {
                int want = 1000 + (int)(t * PerSecond / Second);
                Assert.InRange(c.TicksAt(t), want - 1, want + 1);
            }

            Assert.Equal(0, c.Jumps);
            Assert.True(c.Samples > 100, $"{c.Samples}");
        }

        [Fact]
        public void W02_WorldClock_GapOverTwoSeconds_JumpsToSample()
        {
            var c = new WorldClock();
            Feed(c, 5000, 0, 5 * Second, 200_000);
            // Связь молчала 3 с: своя оценка ушла по местным мкс — 5240 тактов.
            long gap = 8 * Second;
            long ahead = c.TicksAt(gap);
            Assert.Equal(5000 + 8 * PerSecond, ahead);
            c.OnSample((int)(ahead + 61), 0, gap); // образец за 2 с + 1 такт (порог <c>01</c> 6 «Сеть» — фиксированные 2 с)
            Assert.Equal(1, c.Jumps);
            Assert.InRange(c.TicksAt(gap), ahead + WorldClock.JumpTicks, ahead + WorldClock.JumpTicks + 2);
            Assert.Equal(1_000_000, c.RatePpm); // после рывка ход ровный
        }

        [Fact]
        public void W03_WorldClock_SkewOnePercent_ConvergesNoJump()
        {
            var c = new WorldClock();
            // Клиент стартовал с оценкой на 30 тактов (1 с) раньше образца — меньше порога рывка:
            // ход ускоряется на 1 % и нагоняет без рывка (нагон 10 мс на секунду → 100 с).
            c.OnSample(10_000, 0, 0);
            c.OnSample(10_030, 0, 0);
            Assert.Equal(0, c.Jumps);
            Assert.Equal(1_000_000 + WorldClock.SkewPpm, c.RatePpm);
            Feed(c, 10_030, 0, 100 * Second, 200_000);
            Assert.Equal(0, c.Jumps);
            int want = 10_030 + 100 * PerSecond;
            Assert.InRange(c.TicksAt(100 * Second), want - 1, want + 1);
            Assert.Equal(1_000_000, c.RatePpm); // нагнали — ход ровный
        }

        [Fact]
        public void W04_WorldClock_Pause_HoldRelease_EstimateFrozen()
        {
            var c = new WorldClock();
            Feed(c, 900, 0, 4 * Second, 200_000);
            c.Hold(4 * Second);
            Assert.True(c.Held);
            long at = c.EstimateX256(4 * Second);
            Assert.Equal(at, c.EstimateX256(9 * Second)); // в паузе часы стоят (0-036)
            long held = c.Release(9 * Second);
            Assert.Equal(5 * Second, held);
            Assert.False(c.Held);
            // Идут с того же места: пауза вынута из линии часов.
            Assert.InRange(c.TicksAt(9 * Second + Second), 900 + 5 * PerSecond - 1, 900 + 5 * PerSecond + 1);
        }

        [Fact]
        public void W05_WorldClock_SampleDuringPause_SetsEstimate_StaysHeld()
        {
            var c = new WorldClock();
            Feed(c, 2000, 0, 2 * Second, 200_000);
            c.Hold(2 * Second);
            c.OnSample(2000 + 2 * PerSecond, 0, 3 * Second); // сервер в паузе не шагает: образец = время на момент остановки, рывка нет
            Assert.Equal(0, c.Jumps);
            Assert.True(c.Held);
            long a = c.EstimateX256(3 * Second);
            Assert.Equal(a, c.EstimateX256(8 * Second));
            c.Release(8 * Second);
            // Идут с того же места: простояли паузу, шагают от 2060 по местным мкс (1 с после снятия).
            Assert.InRange(c.TicksAt(9 * Second), 2000 + 3 * PerSecond - 1, 2000 + 3 * PerSecond + 1);
        }

        [Fact]
        public void W06_WorldClock_WrapsAroundDay_NoJump()
        {
            var c = new WorldClock();
            Feed(c, Day - 100, 0, 20 * Second, 200_000); // сутки кончаются на 100-м такте ≈ через 3,3 с
            Assert.Equal(0, c.Jumps);
            // Переход через полночь: оценка в круге суток, расхождение считается по кругу.
            int t = c.TicksAt(20 * Second);
            Assert.InRange(t, DayClock.Wrap(Day - 100 + 20 * PerSecond) - 1, DayClock.Wrap(Day - 100 + 20 * PerSecond) + 1);
            Assert.True(t < Day);
        }

        [Fact]
        public void W07_WorldClock_ServerSilent_EstimateCappedThenJump()
        {
            var c = new WorldClock();
            Feed(c, 700, 0, Second, 200_000);
            // Сервер замолчал: свободный ход не больше MaxFreeRunUs — часы встают, а не уходят вперёд.
            long far = Second + WorldClock.MaxFreeRunUs + 5 * Second;
            long capped = c.EstimateX256(Second + WorldClock.MaxFreeRunUs);
            Assert.Equal(capped, c.EstimateX256(far));
            // Новый образец после молчания — расхождение за 2 с: рывок (01 6 «Сеть»).
            c.OnSample(700 + PerSecond, 0, far);
            Assert.Equal(1, c.Jumps);
            Assert.InRange(c.TicksAt(far), 700 + PerSecond - 1, 700 + PerSecond + 1);
        }

        [Fact]
        public void W08_WorldClock_PhaseAndBackground_FollowEstimate()
        {
            var c = new WorldClock();
            c.OnSample(DayClock.DuskStartTick + 100, 0, 0);
            Assert.Equal(DayPhase.Dusk, c.PhaseAt(0));
            Assert.Equal(DayClock.BackgroundX1024(DayClock.DuskStartTick + 100), c.BackgroundX1024(0));
        }

        [Fact]
        public void W09_WorldClock_NoSample_Zero_Invalid()
        {
            var c = new WorldClock();
            Assert.False(c.Valid);
            Assert.Equal(0, c.TicksAt(1_000_000));
            c.OnSample(123, 50_000, 100_000); // owd 50 мс = 1,5 такта — образец относится к будущему на owd
            Assert.True(c.Valid);
            Assert.Equal(123 + 1, c.TicksAt(100_000)); // целых тактов: 1,5 → 1 (дробь в запасе)
        }
    }
}
