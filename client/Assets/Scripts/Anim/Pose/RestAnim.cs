#nullable enable
using System;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// Персонаж лежит в кровати (C-030, процедурно): за <see cref="LieSec"/> спрайт переезжает с места персонажа на свою
    /// половину двуспальной кровати и поворачивается головой к изголовью; низ спрайта (<see cref="Cover"/> тайла) уходит под
    /// одеяло (обрез, как ноги в воде C-018), над одеялом — голова и плечи на подушке; дыхание — лёгкое растяжение. Экран
    /// затемняется (<see cref="Dim"/>). Чистый C# (тесты <c>C030BedTests</c>): поза поверх позы аниматора.
    /// Координаты Unity: тайл = 1, ось y — на север (вверх по экрану).
    /// </summary>
    public static class RestAnim
    {
        /// <summary>Лечь: переезд и поворот, секунд.</summary>
        public const float LieSec = 0.3f;

        /// <summary>Под одеялом — нижние тайлы спрайта персонажа (спрайт 1,5 т): видны голова и плечи (предварительно).</summary>
        public const float Cover = 0.95f;

        /// <summary>Затемнение экрана лёжа (доля чёрного), набирается за <see cref="DimSec"/>.</summary>
        public const float DimMax = 0.42f, DimSec = 0.6f;

        /// <summary>Макушка — от центра кровати к изголовью, тайлов (кровать 2 × 2: изголовье в 1 т от центра).</summary>
        public const float HeadFromCenter = 0.82f;

        /// <summary>Своя половина двуспальной кровати: сдвиг поперёк, тайлов (лежит на левой половине, если смотреть от ног).</summary>
        public const float Side = 0.46f;

        /// <summary>Подъём матраса над землёй на экране (ракурс 3/4: высота × 0,5), тайлов.</summary>
        public const float MattressLift = 0.2f;

        /// <summary>Высота спрайта персонажа, тайлов (<c>actors</c>: 1 × 1,5).</summary>
        public const float BodyLength = 1.5f;

        /// <summary>Направление «к изголовью» для поворота кровати 0…3 (север, восток, юг, запад).</summary>
        public static void HeadDir(int rotation, out float x, out float y)
        {
            switch (rotation & 3)
            {
                case 1: x = 1f; y = 0f; return;
                case 2: x = 0f; y = -1f; return;
                case 3: x = -1f; y = 0f; return;
                default: x = 0f; y = 1f; return;
            }
        }

        /// <summary>Поворот спрайта (градусов, против часовой): макушка смотрит к изголовью.</summary>
        public static float BodyDeg(int rotation) => (rotation & 3) switch { 1 => -90f, 2 => 180f, 3 => 90f, _ => 0f };

        /// <summary>
        /// Опора спрайта (ноги) лежащего относительно центра кровати, тайлов: макушка — у изголовья, тело — к ногам, на своей
        /// половине (слева, если смотреть от ног к изголовью), всё поднято на высоту матраса.
        /// </summary>
        public static void Spot(int rotation, out float x, out float y)
        {
            HeadDir(rotation, out float hx, out float hy);
            float along = HeadFromCenter - BodyLength; // опора — на длину тела от макушки
            // «Слева, если смотреть к изголовью» = поворот направления головы на +90°: (−hy, hx).
            x = hx * along + -hy * Side;
            y = hy * along + hx * Side + MattressLift;
        }

        /// <summary>Доля «лёг» 0…1 (плавно).</summary>
        public static float Progress(float age)
        {
            if (age <= 0f)
            {
                return 0f;
            }

            float t = Math.Min(1f, age / LieSec);
            return t * t * (3f - 2f * t);
        }

        /// <summary>Затемнение экрана 0…<see cref="DimMax"/>.</summary>
        public static float Dim(float age) => age <= 0f ? 0f : DimMax * Math.Min(1f, age / DimSec);

        /// <summary>
        /// Поза лежащего: <paramref name="toBedX"/>, <paramref name="toBedY"/> — от корня персонажа (его точка в ядре) до центра
        /// кровати, тайлов Unity. Сдвиг и поворот — по доле «лёг»; одеяло — с середины; рука с предметом и призраки — нет.
        /// </summary>
        public static Pose Apply(Pose p, float age, int rotation, float toBedX, float toBedY)
        {
            float k = Progress(age);
            Spot(rotation, out float sx, out float sy);
            p.OffsetX = p.OffsetX * (1f - k) + (toBedX + sx) * k;
            p.OffsetY = p.OffsetY * (1f - k) + (toBedY + sy) * k;
            p.RotationDeg = p.RotationDeg * (1f - k) + BodyDeg(rotation) * k;
            p.ScaleX = 1f;
            p.ScaleY = 1f + 0.012f * (float)Math.Sin(age * 2.4f); // дыхание
            p.Cover = k >= 0.5f ? Cover : 0f;
            p.GhostAlpha = 0f;
            p.HandReachX = p.HandReachY = 0f;
            p.HideHand = true;
            p.DigPokeNow = false;
            return p;
        }
    }
}
