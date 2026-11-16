#nullable enable
using System;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// Смерть существа на экране (E3-1, процедурно, слово Автора 08.11): падение набок от удара, затем у врага — растворение.
    /// Не мгновенное исчезновение: труп врага сервер держит 10,5 с (<c>Simulation.CorpseTicks</c>), клиент роняет его за
    /// <see cref="FallSec"/>, даёт полежать и растворяет к <see cref="GoneSec"/>; свой персонаж лежит до возрождения.
    /// Чистый C# (тесты <c>E31CombatFeedTests</c>): поза поверх позы аниматора.
    /// </summary>
    public static class DeathAnim
    {
        /// <summary>Падение: поворот на 90° вокруг ног с ускорением и отскоком.</summary>
        public const float FallSec = 0.35f;

        /// <summary>Начало и конец растворения врага.</summary>
        public const float DissolveFromSec = 0.9f, GoneSec = 1.7f;

        /// <summary>Тело падает к земле: поворот, градусов.</summary>
        public const float FallDeg = 90f;

        /// <summary>Доля падения 0…1 (ускорение «под тяжестью» и отскок 6° в конце).</summary>
        public static float FallProgress(float age)
        {
            if (age <= 0f)
            {
                return 0f;
            }

            if (age < FallSec)
            {
                float t = age / FallSec;
                return t * t;
            }

            float b = age - FallSec; // отскок: 6° вверх и обратно за 0,16 с
            return b < 0.16f ? 1f - 0.066f * (float)Math.Sin(b / 0.16f * Math.PI) : 1f;
        }

        /// <summary>Непрозрачность: 1 до растворения, к <see cref="GoneSec"/> — 0 (свой персонаж не растворяется).</summary>
        public static float Alpha(float age, bool dissolve)
        {
            if (!dissolve || age <= DissolveFromSec)
            {
                return 1f;
            }

            return Math.Max(0f, 1f - (age - DissolveFromSec) / (GoneSec - DissolveFromSec));
        }

        /// <summary>Растворился — можно не рисовать (тень тоже).</summary>
        public static bool Gone(float age, bool dissolve) => dissolve && age >= GoneSec;

        /// <summary>
        /// Поза мёртвого: тело повёрнуто вокруг ног в сторону <paramref name="fallRight"/> (от удара), чуть ниже земли,
        /// темнее; при растворении — прозрачнее и сплющивается. Призраков уворота и руки с предметом нет.
        /// </summary>
        public static Pose Apply(Pose p, float age, bool fallRight, bool dissolve)
        {
            float k = FallProgress(age);
            p.RotationDeg = (fallRight ? -FallDeg : FallDeg) * k;
            p.OffsetX = (fallRight ? 0.25f : -0.25f) * k; // ноги уезжают назад — тело ложится на тайл, а не за него
            p.OffsetY = -0.12f * k;
            float dark = 1f - 0.35f * Math.Min(1f, age / FallSec);
            p.TintR *= dark;
            p.TintG *= dark;
            p.TintB *= dark;
            float a = Alpha(age, dissolve);
            p.Alpha *= a;
            p.ScaleY *= 0.6f + 0.4f * a;
            p.GhostAlpha = 0f;
            p.HandReachX = p.HandReachY = 0f;
            return p;
        }
    }
}
