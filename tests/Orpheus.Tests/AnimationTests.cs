using System;
using Orpheus.Client.Anim;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Процедурная анимация клиента (карточка K-001): чистые классы без Unity из <c>client/Assets/Scripts/Anim/Pose</c>
    /// (подключены в проект тестов ссылкой). Поза — из состояния и времени; замахи — по «замах начат» (`02` 15.1).
    /// </summary>
    public class AnimationTests
    {
        const float Dt = 1f / 60f;

        static Pose Run(ProceduralAnimator a, ActorInput s, float seconds, Func<Pose, bool>? each = null)
        {
            Pose p = default;
            int n = (int)Math.Round(seconds / Dt);
            for (int i = 0; i < n; i++)
            {
                p = a.Step(Dt, s);
                if (each != null && !each(p))
                {
                    break;
                }
            }

            return p;
        }

        [Fact]
        public void K001_Idle_Breathes_NoBob_NoLean()
        {
            var a = new ProceduralAnimator();
            float minY = 1, maxY = 0;
            Run(a, default, 3f, p =>
            {
                Assert.Equal(0f, p.OffsetY, 4);
                Assert.Equal(0f, p.RotationDeg, 4);
                Assert.Equal(1f, p.Alpha);
                Assert.False(p.FlipX);
                minY = Math.Min(minY, p.ScaleY);
                maxY = Math.Max(maxY, p.ScaleY);
                return true;
            });
            Assert.True(maxY - minY > AnimParams.BreathAmp, $"дыхание {minY}…{maxY}");
            Assert.True(maxY - minY <= 2 * AnimParams.BreathAmp + 1e-4f);
        }

        [Fact]
        public void K001_Walk_Bobs_LeansIntoMove_MirrorsLeft()
        {
            var a = new ProceduralAnimator();
            var right = new ActorInput { VelX = AnimParams.WalkFullSpeed };
            float maxBob = 0, leanSum = 0;
            int n = 0;
            Run(a, right, 0.5f);
            Run(a, right, 1f, p =>
            {
                maxBob = Math.Max(maxBob, p.OffsetY);
                leanSum += p.RotationDeg;
                n++;
                Assert.False(p.FlipX);
                return true;
            });
            Assert.InRange(maxBob, AnimParams.BobHeight * 0.9f, AnimParams.BobHeight * 1.01f);
            Assert.True(leanSum / n < -AnimParams.LeanDeg * 0.8f, $"средний наклон {leanSum / n}");

            Pose left = Run(a, new ActorInput { VelX = -AnimParams.WalkFullSpeed }, 0.3f);
            Assert.True(left.FlipX);
            Assert.False(a.FacingRight);

            // Остановился — зеркало остаётся.
            Pose stop = Run(a, default, 1f);
            Assert.True(stop.FlipX);
        }

        [Fact]
        public void K001_Walk_StepRate_GrowsWithSpeed()
        {
            var s2 = new ProceduralAnimator();
            var f2 = new ProceduralAnimator();
            int stepsSlow = CountSteps(s2, 3f), stepsFast = CountSteps(f2, 6f);
            Assert.True(stepsFast > stepsSlow, $"шагов {stepsSlow} и {stepsFast}");
        }

        static int CountSteps(ProceduralAnimator a, float speed)
        {
            int steps = 0;
            float last = 0;
            Run(a, new ActorInput { VelX = speed }, 3f, p =>
            {
                if (a.StepPhase < last)
                {
                    steps++;
                }

                last = a.StepPhase;
                return true;
            });
            return steps;
        }

        [Fact]
        public void K001_Dodge_Stretches_Ghosts_Blinks_ThenFades()
        {
            var a = new ProceduralAnimator();
            var s = new ActorInput { VelX = 12f, Dashing = true, DashProgress = 0.5f, DashDirX = 1f, Invulnerable = true };
            Pose p = Run(a, s, 0.1f);
            Assert.True(p.ScaleX > 1.1f && p.ScaleY < 1f, $"смаз {p.ScaleX}×{p.ScaleY}");
            Assert.True(p.RotationDeg < -AnimParams.DodgeLeanDeg * 0.5f);
            Assert.Equal(AnimParams.GhostAlpha, p.GhostAlpha, 4);
            Assert.Equal(-1f, p.GhostDirX);
            Assert.True(p.Alpha <= AnimParams.InvulnAlphaHigh);

            // Неуязвимость кончилась — без мигания; рывок кончился — след гаснет за GhostFadeSec.
            s.Invulnerable = false;
            Assert.Equal(1f, a.Step(Dt, s).Alpha);
            var after = new ActorInput();
            Pose first = a.Step(Dt, after);
            Assert.True(first.GhostAlpha > 0f);
            Pose gone = Run(a, after, AnimParams.GhostFadeSec);
            Assert.Equal(0f, gone.GhostAlpha);
        }

        [Fact]
        public void K001_Swing_BacksOff_ThenLunges_ThenReturns()
        {
            var a = new ProceduralAnimator();
            var s = new ActorInput { Swing = SwingPhase.Windup, SwingProgress = 0.7f, SwingDirX = -1f };
            Pose w = a.Step(Dt, s);
            Assert.True(w.OffsetX > AnimParams.WindupBack * 0.8f, $"отклонение назад {w.OffsetX}"); // бьёт на запад — откланяется на восток
            Assert.True(w.FlipX); // смотрит на цель (запад)
            Assert.True(w.ScaleY < 1f);

            s.Swing = SwingPhase.Strike;
            s.SwingProgress = 1f;
            Pose st = a.Step(Dt, s);
            Assert.Equal(-AnimParams.StrikeLunge, st.OffsetX, 3);

            s.Swing = SwingPhase.Recover;
            s.SwingProgress = 1f;
            Pose r = a.Step(Dt, s);
            Assert.Equal(0f, r.OffsetX, 3);
        }

        [Fact]
        public void K001_Hit_RedFlash_Knockback_Decay()
        {
            var a = new ProceduralAnimator();
            a.OnHit(1f, 0f);
            Pose p = a.Step(0.001f, default);
            Assert.True(p.TintG < 0.4f && p.TintR == 1f, $"вспышка {p.TintR},{p.TintG},{p.TintB}");
            Assert.True(p.OffsetX > AnimParams.HitKnock * 0.9f);
            Pose later = Run(a, default, AnimParams.HitFlashSec + 0.02f);
            Assert.Equal(1f, later.TintG);
            Assert.Equal(0f, later.OffsetX, 4);
        }

        [Fact]
        public void K001_Dig_PokesTowardTile_OneDustPerPoke()
        {
            var a = new ProceduralAnimator();
            var s = new ActorInput { Digging = true, DigDirX = 1f, DigPeriod = 0.3f };
            int pokes = 0;
            float maxX = 0;
            Run(a, s, 0.9f, p =>
            {
                pokes += p.DigPokeNow ? 1 : 0;
                maxX = Math.Max(maxX, p.OffsetX);
                Assert.False(p.FlipX);
                return true;
            });
            Assert.Equal(3, pokes);
            Assert.InRange(maxX, AnimParams.DigPoke * 0.95f, AnimParams.DigPoke * 1.01f);
            Assert.Equal(0f, ProceduralAnimator.PokeCurve(0f), 4);
            Assert.Equal(1f, ProceduralAnimator.PokeCurve(AnimParams.DigThrustShare), 4);
        }

        [Fact]
        public void K001_DigPeriod_ThreePokesPerTile_Clamped()
        {
            Assert.Equal(0.3f, ProceduralAnimator.DigPeriodFor(54), 3); // 0,9 с на тайл — 3 толчка по 0,3 с
            Assert.Equal(AnimParams.DigPokeMinSec, ProceduralAnimator.DigPeriodFor(6));
            Assert.Equal(AnimParams.DigPokeMaxSec, ProceduralAnimator.DigPeriodFor(600));
            Assert.Equal(AnimParams.DigPokeMaxSec, ProceduralAnimator.DigPeriodFor(0));
        }

        [Fact]
        public void K001_SwingTrack_DropsRepeats_ShowsUntilResolveFrame()
        {
            var t = new SwingTrack();
            const long now = 10_000_000;
            // Пришло на 4-м кадре замаха d = 24; решение по игроку — через 24 кадра его времени.
            Assert.True(t.OnSwing(7, 1000, 1124, 1100, now));
            Assert.False(t.OnSwing(7, 1000, 1124, 1102, now + 33_000)); // повтор того же замаха — каждый такт
            Assert.Equal(1, t.Accepted);
            Assert.True(t.TryGet(7, out SwingTrack.Swing sw));
            Assert.Equal(now + 24 * SwingTrack.FrameUs, sw.ContactUs);

            Assert.Equal(SwingPhase.Windup, t.Phase(7, now + sw.ContactUs / 2 - now / 2, out float q));
            Assert.InRange(q, 0.49f, 0.51f);
            Assert.Equal(SwingPhase.Strike, t.Phase(7, sw.ContactUs + 50_000, out q));
            Assert.InRange(q, 0.49f, 0.51f);
            Assert.Equal(SwingPhase.Recover, t.Phase(7, sw.ContactUs + 100_000 + 300_000, out q));
            Assert.InRange(q, 0.49f, 0.51f);
            Assert.Equal(SwingPhase.None, t.Phase(7, sw.ContactUs + 800_000, out _));
            Assert.Equal(SwingPhase.None, t.Phase(8, now, out _));

            t.Expire(sw.ContactUs + 800_000);
            Assert.Equal(0, t.Count);

            // Новый замах того же атакующего заменяет старый; кадр решения уже прошёл — удар сразу.
            Assert.True(t.OnSwing(7, 1090, 1100, 1105, now));
            Assert.Equal(SwingPhase.Strike, t.Phase(7, now, out _));
        }
    }
}
