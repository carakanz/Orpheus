#nullable enable
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Солнце для теней (C-017, слова Автора 15.11 «тень должна проворачиваться в зависимости от времени суток»): по такту
    /// суток (с сервера) — куда и насколько длинно ложится тень на тайл высоты предмета и сила солнечной тени.
    /// Восход — длинная на запад, полдень — короткая на север, закат — длинная на восток; сумерки и рассвет — тень
    /// бледнеет / проявляется; ночью солнечной тени нет, работают тени от огня (сила огня = 1 − сила солнца).
    /// <para><b>Числа предварительные</b> (подбор по снимкам C-017): азимут — направление тени в градусах (0 — восток,
    /// 90 — север, 180 — запад), длина — тайлов тени на тайл высоты, сила — 0…1. Между опорами — линейно (сила — плавно).</para>
    /// Шейдеры получают глобальный <c>_OrphSun</c>: xy — сдвиг тени на тайл высоты (Unity: x — восток, y — север),
    /// z — сила солнца, w — сила огня (<see cref="Apply"/>, раз в кадр из <see cref="DayTintView.Update"/>).
    /// </summary>
    public static class SunTable
    {
        /// <summary>Опоры: такт суток (от начала рассвета, может быть больше суток), азимут тени, длина, сила.</summary>
        static readonly (float At, float Az, float Len, float K)[] Keys =
        {
            (DayClock.DawnStartTick, 180f, 2.6f, 0f),                         // солнце на горизонте — тени ещё нет
            (DayClock.TicksPerDay, 172f, 1.8f, 1f),                            // утро (начало дня)
            (DayClock.TicksPerDay + 3600f, 150f, 1.1f, 1f),
            (DayClock.TicksPerDay + DayClock.DuskStartTick * 0.5f, 90f, 0.42f, 1f), // полдень — коротко на север
            (DayClock.TicksPerDay + DayClock.DuskStartTick - 3600f, 30f, 1.1f, 1f),
            (DayClock.TicksPerDay + DayClock.DuskStartTick, 8f, 1.8f, 0.8f),   // начало сумерек — длинно на восток, бледнее
            (DayClock.TicksPerDay + DayClock.NightStartTick, 0f, 2.6f, 0f),     // ночь — солнечной тени нет
        };

        static readonly int SunId = Shader.PropertyToID("_OrphSun");

        /// <summary>Сдвиг тени на тайл высоты (Unity) и сила солнечной тени по такту суток.</summary>
        public static Vector3 SunAt(int worldTicks)
        {
            float t = DayClock.Wrap(worldTicks);
            if (t < DayClock.DawnStartTick)
            {
                t += DayClock.TicksPerDay; // день и сумерки — после рассвета той же «дуги»
            }

            if (t >= Keys[Keys.Length - 1].At)
            {
                return Vector3.zero; // ночь
            }

            for (int i = 1; i < Keys.Length; i++)
            {
                if (t <= Keys[i].At)
                {
                    (float At, float Az, float Len, float K) a = Keys[i - 1], b = Keys[i];
                    float f = (t - a.At) / Mathf.Max(1f, b.At - a.At);
                    float az = Mathf.Lerp(a.Az, b.Az, f) * Mathf.Deg2Rad;
                    float len = Mathf.Lerp(a.Len, b.Len, f);
                    float k = Mathf.Lerp(a.K, b.K, f * f * (3f - 2f * f));
                    return new Vector3(Mathf.Cos(az) * len, Mathf.Sin(az) * len, k);
                }
            }

            return Vector3.zero;
        }

        /// <summary>Отдать солнце шейдерам теней на этот такт суток.</summary>
        public static void Apply(int worldTicks)
        {
            Vector3 s = SunAt(worldTicks);
            Shader.SetGlobalVector(SunId, new Vector4(s.x, s.y, s.z, 1f - s.z));
        }
    }
}
