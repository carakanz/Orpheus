using System;
using System.IO;
using Orpheus.Client.Anim;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Trig;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-012: прицел мышью (<c>client/Assets/Scripts/Anim/Pose/AimMath.cs</c>) и взмах меча против зоны удара ядра
    /// (<see cref="ActionAnim.SwordHandDeg"/>, <see cref="ActionAnim.HeldPoint"/>).
    /// </summary>
    public class AimMathTests
    {
        private static int Deg(ushort raw) => (int)Math.Round(raw * 360.0 / Angle16.Steps) % 360;

        /// <summary>
        /// Курсор на 2 т в каждую сторону от середины видимого тела — ровно восток, юг, запад, север. Подъём середины тела задан
        /// числом (0,35 т = опора −0,4 + половина спрайта 1,5 т), а не константой из кода: смена <see cref="AimMath.BodyCenterY"/>
        /// уводит угол на восток/запад на ≥ 1° — тест падает.
        /// </summary>
        [Fact]
        public void C012_Aim_FromBodyCenter_FourSides()
        {
            const float x = 10f, y = 5f, c = y + 0.35f;
            Assert.Equal(0, Deg(AimMath.AimRaw(x, y, x + 2f, c)));    // восток
            Assert.Equal(90, Deg(AimMath.AimRaw(x, y, x, c - 2f)));   // юг (экран вниз, ядро Y+)
            Assert.Equal(180, Deg(AimMath.AimRaw(x, y, x - 2f, c)));  // запад
            Assert.Equal(270, Deg(AimMath.AimRaw(x, y, x, c + 2f)));  // север
            Assert.Equal(ActionAnim.SpriteFootY + 1.5f / 2f, AimMath.BodyCenterY, 3); // середина спрайта 1 × 1,5 т над опорой
        }

        /// <summary>
        /// Курсор у колен своего персонажа справа (ниже середины тела, выше точки ядра) — удар на юго-восток. От точки ядра,
        /// как до C-012, выходило на северо-восток.
        /// </summary>
        [Fact]
        public void C012_Aim_CursorBelowBodyCenter_IsSouthward()
        {
            const float x = 10f, y = 5f;
            int deg = Deg(AimMath.AimRaw(x, y, x + 0.5f, y + 0.2f));
            Assert.InRange(deg, 1, 89);
            int old = Deg(Angle16.Atan2(Torus.ToUnits(-0.2f), Torus.ToUnits(0.5f)).Raw);
            Assert.InRange(old, 271, 359);
        }

        /// <summary>Курсор на середине тела врага (тот же подъём спрайта) — прицел ровно на точку врага в ядре.</summary>
        [Fact]
        public void C012_Aim_CursorOnEnemyBody_PointsAtEnemyCore()
        {
            const float x = 10f, y = 5f, ex = 11.2f, ey = 4.1f;
            ushort aim = AimMath.AimRaw(x, y, ex, ey + AimMath.BodyCenterY);
            ushort core = Angle16.Atan2(Torus.ToUnits(-(ey - y)), Torus.ToUnits(ex - x)).Raw;
            Assert.InRange(Math.Abs((short)(aim - core)), 0, 30);
        }

        [Fact]
        public void C012_AxisOf_MatchesCoreAngle()
        {
            AimMath.AxisOf(Angle16.Quarter, out float sx, out float sy); // юг — экран вниз
            Assert.Equal(0f, sx, 3);
            Assert.Equal(-1f, sy, 3);
        }
        private static string Art
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Orpheus.sln")))
                {
                    dir = dir.Parent;
                }

                Assert.NotNull(dir);
                return Path.Combine(dir!.FullName, "client", "Assets", "Art", "_TEMP");
            }
        }

        /// <summary>Остриё меча в <see cref="ActionAnim.SwordTipX"/>/<c>Y</c> посчитано для значка 64 × 64, 128 px на тайл, опора — низ по центру.</summary>
        [Fact]
        public void C012_SwordIcon_ImportMatchesTipConstants()
        {
            byte[] png = File.ReadAllBytes(Path.Combine(Art, "TEMP_item_wooden_sword.png"));
            int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            Assert.Equal(64, w);
            Assert.Equal(64, h);
            string meta = File.ReadAllText(Path.Combine(Art, "TEMP_item_wooden_sword.png.meta"));
            Assert.Contains("spritePixelsToUnits: 128", meta);
            Assert.Contains("spritePivot: {x: 0.5, y: 0}", meta);
        }

        /// <summary>
        /// Слово Автора: радиус удара примерно совпадает с радиусом анимации. Взмах прогоняется тем же <see cref="ProceduralAnimator"/>,
        /// что в игре, кончик считается той же цепочкой преобразований, что рисует <c>ActorRig</c> (<see cref="ActionAnim.HeldPoint"/>:
        /// масштаб <see cref="ActionAnim.HeldScale"/>, кисть <see cref="ActionAnim.HandLocal"/>, значок <see cref="ActionAnim.IconLocal"/>).
        /// У прицела (±25°) кончик доходит до обода зоны ядра 1,4 т (±0,15); за удар клинок проходит дугу от +50° до −55° от прицела.
        /// </summary>
        [Theory]
        [InlineData(0f)]
        [InlineData(45f)]
        [InlineData(90f)]
        [InlineData(135f)]
        [InlineData(180f)]
        [InlineData(225f)]
        [InlineData(270f)]
        [InlineData(315f)]
        public void C012_SwordTip_ReachAndArc_MatchCoreSector(float aimDeg)
        {
            float core = SwordStrike.ReachUnits / (float)Torus.SubTilesPerTile;
            float half = SwordStrike.HalfArcSteps * 360f / Angle16.Steps;
            double ar = aimDeg * Math.PI / 180.0;
            float dx = (float)Math.Cos(ar), dy = (float)Math.Sin(ar); // экранные оси, Y вверх
            var a = new ProceduralAnimator();
            double reachAtAim = 0, minDiff = 999, maxDiff = -999;
            for (int f = 0; f < 18; f++)
            {
                bool windup = f < 9;
                float q = windup ? (f + 1) / 9f : (f - 8) / 9f;
                Pose p = a.Step(1f / 60f, new ActorInput
                {
                    Held = HeldLook.Sword, Swing = windup ? SwingPhase.Windup : SwingPhase.Strike, SwingProgress = q, SwingDirX = dx, SwingDirY = dy,
                });
                if (windup)
                {
                    continue;
                }

                ActionAnim.HeldPoint(p, HeldLook.Sword, ActionAnim.SwordTipX, ActionAnim.SwordTipY, out float x, out float y);
                double r = Math.Sqrt(x * x + y * y);
                double diff = Math.Atan2(y, x) * 180.0 / Math.PI - aimDeg;
                diff -= 360.0 * Math.Floor((diff + 180.0) / 360.0);
                minDiff = Math.Min(minDiff, diff);
                maxDiff = Math.Max(maxDiff, diff);
                if (Math.Abs(diff) <= 25.0)
                {
                    reachAtAim = Math.Max(reachAtAim, r);
                }
            }

            Assert.InRange(reachAtAim, core - 0.15, core + 0.15);
            Assert.True(maxDiff >= half - 10f && minDiff <= -(half - 5f), $"дуга кончика {minDiff:F0}…{maxDiff:F0}° при прицеле {aimDeg}");
        }
    }
}
