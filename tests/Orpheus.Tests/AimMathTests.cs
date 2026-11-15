using System;
using Orpheus.Client.Anim;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Trig;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-012: прицел мышью (<c>client/Assets/Scripts/Anim/Pose/AimMath.cs</c>) и взмах меча против зоны удара ядра
    /// (<see cref="ActionAnim.SwordDeg(SwingPhase, float, float)"/>, <see cref="ActionAnim.SwordTipReach"/>).
    /// </summary>
    public class AimMathTests
    {
        private static int Deg(ushort raw) => (int)Math.Round(raw * 360.0 / Angle16.Steps) % 360;

        [Fact]
        public void C012_Aim_FromBodyCenter_FourSides()
        {
            const float x = 10f, y = 5f, c = y + AimMath.BodyCenterY;
            Assert.Equal(0, Deg(AimMath.AimRaw(x, y, x + 2f, c)));    // восток
            Assert.Equal(90, Deg(AimMath.AimRaw(x, y, x, c - 2f)));   // юг (экран вниз, ядро Y+)
            Assert.Equal(180, Deg(AimMath.AimRaw(x, y, x - 2f, c)));  // запад
            Assert.Equal(270, Deg(AimMath.AimRaw(x, y, x, c + 2f)));  // север
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

        /// <summary>Слово Автора: радиус удара примерно совпадает с радиусом анимации — кончик меча в миг удара ≈ 1,4 т.</summary>
        [Fact]
        public void C012_SwordTip_MatchesCoreReach()
        {
            float core = SwordStrike.ReachUnits / (float)Torus.SubTilesPerTile;
            Assert.InRange(ActionAnim.SwordTipReach(), core - 0.1f, core + 0.1f);
        }

        /// <summary>Клинок (рука + 45°) за удар проходит дугу ядра: прицел ±60° (±5°), при любом прицеле.</summary>
        [Theory]
        [InlineData(0f)]
        [InlineData(45f)]
        [InlineData(90f)]
        [InlineData(-45f)]
        [InlineData(-90f)]
        public void C012_SwordSweep_CoversCoreArc(float aimDeg)
        {
            float half = SwordStrike.HalfArcSteps * 360f / Angle16.Steps;
            float start = ActionAnim.SwordDeg(SwingPhase.Strike, 0f, aimDeg) + ActionAnim.BladeAxisDeg;
            float end = ActionAnim.SwordDeg(SwingPhase.Strike, 1f, aimDeg) + ActionAnim.BladeAxisDeg;
            Assert.True(start >= aimDeg + half - 5f, $"начало {start} при прицеле {aimDeg}");
            Assert.InRange(end, aimDeg - half - 5f, aimDeg - half + 5f);
        }
    }
}
