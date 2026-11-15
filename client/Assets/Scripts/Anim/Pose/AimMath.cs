#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Core.Trig;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// Прицел мышью (C-012, `02` 1.1 «Прицел»): угол ядра от своего персонажа к точке под курсором. Чистый C# (тесты
    /// <c>AimMathTests</c>). Камера ортографическая и смотрит прямо вниз, наклон E2-17 — только в рисовании построек, поэтому
    /// точка экрана → точка плоскости земли — это <c>Camera.ScreenToWorldPoint</c> без поправок.
    /// <para>
    /// Начало луча — середина видимого тела, а не точка ядра: спрайт стоит ногами на 0,4 тайла ниже точки ядра и поднимается на
    /// 1,5 тайла, середина тела — на <see cref="BodyCenterY"/> выше точки ядра (туда же смотрит камера, <c>GameBoot.CameraOffset</c>,
    /// и вылетают искры попадания). У врагов то же смещение, поэтому курсор на теле врага даёт ровно направление на врага в ядре;
    /// от точки ядра луч к курсору уходил на север (курсор у колен своего персонажа — удар назад).
    /// </para>
    /// </summary>
    public static class AimMath
    {
        /// <summary>Середина видимого тела выше точки ядра, тайлов (экран: Y вверх).</summary>
        public const float BodyCenterY = 0.35f;

        /// <summary>
        /// Угол прицела (сырой <see cref="Angle16"/>): от середины тела персонажа, нарисованного в
        /// (<paramref name="charX"/>, <paramref name="charY"/>) — корень персонажа в координатах Unity, — к точке плоскости земли под
        /// курсором (<paramref name="cursorX"/>, <paramref name="cursorY"/>). Оси ядра: X на восток, Y на юг (экранный Y вверх — с минусом).
        /// </summary>
        public static ushort AimRaw(float charX, float charY, float cursorX, float cursorY)
        {
            int dx = Torus.ToUnits(cursorX - charX);
            int dy = Torus.ToUnits(-(cursorY - (charY + BodyCenterY)));
            return Angle16.Atan2(dy, dx).Raw;
        }

        /// <summary>Ось удара на экране (единичная, Y вверх) по сырому углу ядра.</summary>
        public static void AxisOf(ushort aimRaw, out float x, out float y)
        {
            double a = aimRaw / 65536.0 * System.Math.PI * 2.0;
            x = (float)System.Math.Cos(a);
            y = (float)-System.Math.Sin(a); // угол ядра: 0 — восток, рост к югу (Y вниз)
        }
    }
}
