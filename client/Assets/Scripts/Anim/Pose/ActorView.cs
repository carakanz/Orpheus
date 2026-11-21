#nullable enable
using System;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// Направление взгляда существа на экране (C-053): 4 вида — к зрителю, от зрителя, вбок.
    /// Влево — отражённый спрайт вида вбок (<see cref="Pose.FlipX"/>), отдельной картинки нет.
    /// Ракурс «сверху под небольшим углом», как у стен и построек (E2-17).
    /// </summary>
    public enum ActorView
    {
        /// <summary>Вбок (базовый спрайт, смотрит вправо; влево — <see cref="Pose.FlipX"/>).</summary>
        Side = 0,
        /// <summary>К зрителю (лицом вниз по экрану, y уменьшается).</summary>
        Front = 1,
        /// <summary>От зрителя (спиной, лицом вверх по экрану).</summary>
        Back = 2,
    }

    /// <summary>
    /// Выбор вида по направлению взгляда/движения с гистерезисом (чистый код, тестируется юнит-тестами).
    /// Порог: направление «на нас / от нас», когда |dirY| больше |dirX| × <see cref="FrontWeight"/> —
    /// иначе «вбок». Так диагональ ближе к вертикали даёт вид спереди/сзади, а не боковой.
    /// </summary>
    public static class ViewPick
    {
        /// <summary>Вес вертикали: |dirY| &gt; |dirX| × 1,2 — вид к/от зрителя (сектора ±28,6° от вертикали, боковые — ±61,4°).</summary>
        public const float FrontWeight = 1.2f;

        /// <summary>
        /// Новый вид по направлению (dirX, dirY). Направление нулевое или ниже мёртвой зоны — предыдущий вид.
        /// </summary>
        public static ActorView Pick(float dirX, float dirY, ActorView prev, float deadZone)
        {
            float ax = Math.Abs(dirX);
            float ay = Math.Abs(dirY);
            if (ax <= deadZone && ay <= deadZone)
            {
                return prev;
            }

            if (ay > ax * FrontWeight)
            {
                return dirY < 0f ? ActorView.Front : ActorView.Back;
            }

            return ActorView.Side;
        }
    }
}
