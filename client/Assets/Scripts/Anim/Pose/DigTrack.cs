#nullable enable
using System;
using Orpheus.Core.World;

namespace Orpheus.Client.Anim
{
    /// <summary>Каким откликом отвечает удар инструментом (E2-7): пыль, дерево (встряска, щепки), камень (искры, крошка).</summary>
    public enum ImpactLook : byte
    {
        Dust = 0,
        Wood = 1,
        Stone = 2,
    }

    /// <summary>
    /// Копание своего тайла глазами анимации (E2-7, правка по проверке): без Unity.
    /// <list type="bullet">
    /// <item>Объект тайла запоминается в начале копания: к последнему удару предсказание уже сняло его из мира — отклик
    /// (щепки/искры) и встряска берутся по запомненному, а не по пустому тайлу.</item>
    /// <item>Время копания для позы — счёт кадров ядра (<c>DigProgress</c>) плюс доля кадра, но удар не раньше кадра, в
    /// котором счёт дошёл до границы замаха (<see cref="ClockSec"/>): на любой частоте кадров удар — в кадр счёта,
    /// последний — в кадр снятия объекта.</item>
    /// </list>
    /// </summary>
    public sealed class DigTrack
    {
        bool _active;
        int _tx, _ty;
        int _prog = -1;
        float _sub;
        ushort _obj;

        /// <summary>Объект тайла, запомненный в начале копания (или первый непустой за копание).</summary>
        public ushort Object => _obj;

        /// <summary>Вид отклика по запомненному объекту.</summary>
        public ImpactLook Look => LookOf(_obj);

        /// <summary>
        /// Кадр: копание идёт по тайлу (<paramref name="tx"/>, <paramref name="ty"/>), в мире сейчас <paramref name="objNow"/>,
        /// счёт кадров копания <paramref name="progress"/> из <paramref name="digFrames"/>; <paramref name="predictedDone"/> — докопано
        /// своим счётом (ждём ответа). Возвращает время копания для позы, секунд. <paramref name="started"/> — новое копание.
        /// </summary>
        public float Update(int tx, int ty, ushort objNow, int progress, int digFrames, bool predictedDone, float dt, out bool started)
        {
            started = !_active || tx != _tx || ty != _ty || progress < _prog;
            if (started)
            {
                _obj = objNow;
                _sub = 0f;
                _prog = progress;
            }
            else
            {
                if (_obj == ObjectId.Empty && objNow != ObjectId.Empty)
                {
                    _obj = objNow; // начали копать до прихода объекта в мир
                }

                if (progress != _prog)
                {
                    _sub = 0f;
                    _prog = progress;
                }
                else
                {
                    _sub += dt;
                }
            }

            _active = true;
            _tx = tx;
            _ty = ty;
            return ClockSec(progress, predictedDone ? 0f : _sub, digFrames);
        }

        /// <summary>Копание кончилось (ответ пришёл, кнопку отпустили).</summary>
        public void Stop()
        {
            _active = false;
            _prog = -1;
            _sub = 0f;
        }

        /// <summary>
        /// Время копания для позы: <c>progress / 60 + sub</c> (доля кадра — строго меньше кадра), но не за границу следующего
        /// замаха, пока счёт кадров её не прошёл: удар (<see cref="ActionAnim.ImpactsBetween"/>) — только в кадр, когда
        /// <paramref name="progress"/> дошёл до конца замаха.
        /// </summary>
        public static float ClockSec(int progress, float sub, int digFrames)
        {
            const float frame = 1f / ActionAnim.FramesPerSecond;
            float baseSec = Math.Max(0, progress) * frame;
            if (sub <= 0f)
            {
                return baseSec;
            }

            float t = baseSec + Math.Min(sub, frame * 0.999f);
            float len = ActionAnim.BeatSec(digFrames);
            // Граница замаха, которой счёт ещё не достиг (с тем же допуском, что у ImpactsBetween).
            double next = Math.Floor(baseSec / (double)len + 1e-6) + 1;
            float boundary = (float)(next * len);
            float guard = boundary - 1e-4f;
            return t >= guard && baseSec < guard ? Math.Max(baseSec, guard - 1e-4f) : t;
        }

        /// <summary>Вид отклика по объекту: дерево и куст — дерево; пусто — пыль; прочее (скала, руда, гранит, стены) — камень.</summary>
        public static ImpactLook LookOf(ushort obj)
        {
            if (obj == ObjectId.Empty)
            {
                return ImpactLook.Dust;
            }

            ushort id = ObjectId.IdOf(obj);
            return id == ObjectId.Tree || id == ObjectId.Bush || id == ObjectId.Stump ? ImpactLook.Wood : ImpactLook.Stone;
        }
    }
}
