#nullable enable
using System;

namespace Orpheus.Client.Anim
{
    /// <summary>Фаза удара существа на экране (`02` 3.1): замах → удар → отход.</summary>
    public enum SwingPhase : byte
    {
        None = 0,
        Windup = 1,
        Strike = 2,
        Recover = 3,
    }

    /// <summary>
    /// Состояние существа, из которого считается поза (K-001): только то, что клиент уже знает — скорость на экране,
    /// уворот предсказания, фаза замаха по «замах начат», копание. Оси — экранные: X вправо, Y вверх; скорость — т/с.
    /// </summary>
    public struct ActorInput
    {
        public float VelX, VelY;

        /// <summary>Рывок уворота идёт; доля рывка 0…1; направление (единичное); кадры неуязвимости.</summary>
        public bool Dashing;
        public float DashProgress;
        public float DashDirX, DashDirY;
        public bool Invulnerable;

        /// <summary>Фаза удара, доля фазы 0…1 и ось удара (единичная).</summary>
        public SwingPhase Swing;
        public float SwingProgress;
        public float SwingDirX, SwingDirY;

        /// <summary>Копание идёт; направление к тайлу (единичное); период толчка, секунд.</summary>
        public bool Digging;
        public float DigDirX, DigDirY;
        public float DigPeriod;
    }

    /// <summary>Поза спрайта: сдвиг от опоры (тайлов), поворот, масштаб, зеркало, цвет-множитель, след уворота.</summary>
    public struct Pose
    {
        public float OffsetX, OffsetY;
        public float RotationDeg;
        public float ScaleX, ScaleY;
        public bool FlipX;
        public float TintR, TintG, TintB, Alpha;

        /// <summary>Призраки уворота: прозрачность первого (0 — нет), шаг между ними вдоль <see cref="GhostDirX"/>/<see cref="GhostDirY"/>.</summary>
        public float GhostAlpha;
        public float GhostDirX, GhostDirY;

        /// <summary>В этом кадре толчок копания дошёл до тайла — выбросить пыль.</summary>
        public bool DigPokeNow;

        public static Pose Identity => new Pose { ScaleX = 1f, ScaleY = 1f, TintR = 1f, TintG = 1f, TintB = 1f, Alpha = 1f };
    }

    /// <summary>
    /// Процедурная анимация одного существа (K-001, вариант В из <c>work/animation_plan.md</c>): поза из состояния и времени,
    /// без Unity — MonoBehaviour только применяет <see cref="Pose"/> к Transform и SpriteRenderer. Числа — <see cref="AnimParams"/>.
    /// Слои складываются: ходьба/дыхание, уворот, удар, копание, попадание (сдвиги и углы суммой, масштаб произведением).
    /// </summary>
    public sealed class ProceduralAnimator
    {
        const float TwoPi = (float)(Math.PI * 2);

        float _time;
        float _speed;       // сглаженная скорость, т/с
        float _stepPhase;   // шагов пройдено (дробная часть — фаза шага)
        bool _facingRight = true;
        float _hitAge = float.MaxValue;
        float _hitDirX, _hitDirY;
        float _digTime;
        bool _wasDigging;
        float _ghostFade;   // 1 в рывке, гаснет после
        float _ghostDirX, _ghostDirY;

        /// <summary>Смотрит вправо (после последнего кадра).</summary>
        public bool FacingRight => _facingRight;

        /// <summary>Сглаженная скорость, т/с.</summary>
        public float Speed => _speed;

        /// <summary>Фаза шага 0…1.</summary>
        public float StepPhase => _stepPhase - (float)Math.Floor(_stepPhase);

        /// <summary>Сдвиг фазы шага и дыхания (чтобы толпа не шагала в ногу).</summary>
        public float PhaseOffset { get; set; }

        /// <summary>Получил удар: красная вспышка и отдача от атакующего (направление — от атакующего к цели, экранные оси).</summary>
        public void OnHit(float dirX, float dirY)
        {
            Normalize(ref dirX, ref dirY);
            _hitAge = 0f;
            _hitDirX = dirX;
            _hitDirY = dirY;
        }

        /// <summary>Прошло <paramref name="dt"/> секунд: поза для этого кадра.</summary>
        public Pose Step(float dt, in ActorInput s)
        {
            if (dt < 0f)
            {
                dt = 0f;
            }

            _time += dt;
            if (_hitAge < float.MaxValue)
            {
                _hitAge += dt;
            }

            Pose p = Pose.Identity;
            float t = _time + PhaseOffset;

            // --- ходьба и дыхание ---
            float rawSpeed = (float)Math.Sqrt(s.VelX * s.VelX + s.VelY * s.VelY);
            float k = 1f - (float)Math.Exp(-AnimParams.SpeedSmoothing * dt);
            _speed += (Math.Min(rawSpeed, AnimParams.WalkFullSpeed * 1.5f) - _speed) * k;
            float walk = s.Dashing ? 0f : Clamp01((_speed - AnimParams.WalkMinSpeed) / (AnimParams.WalkFullSpeed - AnimParams.WalkMinSpeed));
            if (walk > 0f)
            {
                _stepPhase += AnimParams.StepHz * Math.Max(0.5f, _speed / AnimParams.WalkFullSpeed) * dt;
            }

            float step = StepPhase;
            float bump = (float)Math.Abs(Math.Sin(Math.PI * step * 2)); // два касания на цикл: левая, правая
            p.OffsetY += AnimParams.BobHeight * bump * walk;
            float vxNorm = Clamp(s.VelX / AnimParams.WalkFullSpeed, -1f, 1f);
            p.RotationDeg += -AnimParams.LeanDeg * vxNorm * walk;
            p.RotationDeg += AnimParams.SwayDeg * (float)Math.Sin(TwoPi * step) * walk;
            float squash = AnimParams.StepSquash * (1f - bump) * walk;
            p.ScaleY *= 1f - squash;
            p.ScaleX *= 1f + squash * 0.5f;

            float idle = 1f - walk;
            float breath = (float)Math.Sin(TwoPi * AnimParams.BreathHz * t) * AnimParams.BreathAmp * idle;
            p.ScaleY *= 1f + breath;
            p.ScaleX *= 1f - breath * 0.5f;

            // --- направление: удар, копание, рывок, затем движение ---
            float faceX = 0f;
            if (s.Swing != SwingPhase.None)
            {
                faceX = s.SwingDirX;
            }
            else if (s.Digging)
            {
                faceX = s.DigDirX;
            }
            else if (s.Dashing)
            {
                faceX = s.DashDirX;
            }
            else if (_speed > AnimParams.WalkMinSpeed && Math.Abs(s.VelX) > AnimParams.WalkMinSpeed)
            {
                faceX = s.VelX;
            }

            bool aimed = s.Swing != SwingPhase.None || s.Digging || s.Dashing;
            if (faceX != 0f && Math.Abs(faceX) > (aimed ? AnimParams.FacingDeadZone : 0f))
            {
                _facingRight = faceX > 0f;
            }

            p.FlipX = _facingRight != AnimParams.SpriteFacesRight;

            // --- уворот ---
            if (s.Dashing)
            {
                float env = 0.6f + 0.4f * (float)Math.Sin(Math.PI * Clamp01(s.DashProgress)); // сильнее в середине рывка
                float ax = Math.Abs(s.DashDirX), ay = Math.Abs(s.DashDirY);
                p.ScaleX *= 1f + AnimParams.DodgeStretch * ax * env - AnimParams.DodgeThin * ay * env;
                p.ScaleY *= 1f + AnimParams.DodgeStretch * ay * env - AnimParams.DodgeThin * ax * env;
                p.RotationDeg += -AnimParams.DodgeLeanDeg * s.DashDirX * env;
                _ghostFade = 1f;
                _ghostDirX = s.DashDirX;
                _ghostDirY = s.DashDirY;
            }
            else if (_ghostFade > 0f)
            {
                _ghostFade = Math.Max(0f, _ghostFade - dt / AnimParams.GhostFadeSec);
            }

            if (_ghostFade > 0f)
            {
                p.GhostAlpha = AnimParams.GhostAlpha * _ghostFade;
                p.GhostDirX = -_ghostDirX;
                p.GhostDirY = -_ghostDirY;
            }

            if (s.Invulnerable)
            {
                bool high = (t * AnimParams.InvulnBlinkHz) % 1f < 0.5f;
                p.Alpha *= high ? AnimParams.InvulnAlphaHigh : AnimParams.InvulnAlphaLow;
            }

            // --- удар ---
            if (s.Swing != SwingPhase.None)
            {
                SwingOffset(s.Swing, Clamp01(s.SwingProgress), s.SwingDirX, s.SwingDirY, t, ref p);
            }

            // --- копание ---
            if (s.Digging)
            {
                float period = Clamp(s.DigPeriod, AnimParams.DigPokeMinSec, AnimParams.DigPokeMaxSec);
                float before = _wasDigging ? _digTime / period : -1f;
                _digTime += dt;
                float after = _digTime / period;
                float u = after - (float)Math.Floor(after);
                float curve = PokeCurve(u);
                p.OffsetX += s.DigDirX * AnimParams.DigPoke * curve;
                p.OffsetY += s.DigDirY * AnimParams.DigPoke * curve;
                p.RotationDeg += -AnimParams.DigLeanDeg * s.DigDirX * curve;
                // Толчок «дошёл» — пересекли долю DigThrustShare очередного периода.
                double peakBefore = Math.Floor(before - AnimParams.DigThrustShare);
                double peakAfter = Math.Floor(after - AnimParams.DigThrustShare);
                p.DigPokeNow = before >= 0f ? peakAfter > peakBefore : u >= AnimParams.DigThrustShare;
                _wasDigging = true;
            }
            else
            {
                _wasDigging = false;
                _digTime = 0f;
            }

            // --- попадание ---
            if (_hitAge < AnimParams.HitFlashSec)
            {
                float f = 1f - _hitAge / AnimParams.HitFlashSec;
                p.TintR *= Lerp(1f, AnimParams.HitTint[0], f);
                p.TintG *= Lerp(1f, AnimParams.HitTint[1], f);
                p.TintB *= Lerp(1f, AnimParams.HitTint[2], f);
            }

            if (_hitAge < AnimParams.HitKnockSec)
            {
                float f = 1f - _hitAge / AnimParams.HitKnockSec;
                f *= f;
                p.OffsetX += _hitDirX * AnimParams.HitKnock * f;
                p.OffsetY += _hitDirY * AnimParams.HitKnock * f;
            }

            return p;
        }

        /// <summary>Замах: отклонение назад с приседанием и дрожью; удар — выпад вперёд; отход — плавный возврат.</summary>
        static void SwingOffset(SwingPhase phase, float q, float dx, float dy, float t, ref Pose p)
        {
            float back, lean, squash = 0f;
            switch (phase)
            {
                case SwingPhase.Windup:
                {
                    float e = 1f - (1f - q) * (1f - q); // быстро назад, потом держит
                    back = -AnimParams.WindupBack * e;
                    lean = AnimParams.WindupLeanDeg * e;
                    squash = AnimParams.WindupSquash * e;
                    if (q > 0.75f)
                    {
                        float shake = AnimParams.WindupShake * (float)Math.Sin(TwoPi * AnimParams.WindupShakeHz * t);
                        p.OffsetX += shake;
                    }

                    break;
                }
                case SwingPhase.Strike:
                {
                    float e = Math.Min(1f, q * 2f);
                    back = Lerp(-AnimParams.WindupBack, AnimParams.StrikeLunge, e);
                    lean = Lerp(AnimParams.WindupLeanDeg, -AnimParams.StrikeLeanDeg, e);
                    squash = -0.06f * e; // вытягивается в выпаде
                    break;
                }
                default:
                {
                    float e = q * q * (3f - 2f * q);
                    back = AnimParams.StrikeLunge * (1f - e);
                    lean = -AnimParams.StrikeLeanDeg * (1f - e);
                    break;
                }
            }

            p.OffsetX += dx * back;
            p.OffsetY += dy * back;
            // Наклон назад от цели: бьёт вправо — верх уходит влево (против часовой, +Z в Unity).
            p.RotationDeg += lean * Math.Sign(dx == 0f ? 1f : dx);
            p.ScaleY *= 1f - squash;
            p.ScaleX *= 1f + squash * 0.6f;
        }

        /// <summary>Толчок: быстрый выпад на доле <see cref="AnimParams.DigThrustShare"/>, плавный возврат.</summary>
        public static float PokeCurve(float u)
        {
            float a = AnimParams.DigThrustShare;
            if (u < a)
            {
                float x = u / a;
                return 1f - (1f - x) * (1f - x);
            }

            float y = (u - a) / (1f - a);
            return 1f - y * y * (3f - 2f * y);
        }

        /// <summary>Период толчка копания: <see cref="AnimParams.DigPokesPerTile"/> толчков на время копания тайла, в пределах.</summary>
        public static float DigPeriodFor(int digFrames, int framesPerSecond = 60)
        {
            if (digFrames <= 0)
            {
                return AnimParams.DigPokeMaxSec;
            }

            float sec = digFrames / (float)framesPerSecond / AnimParams.DigPokesPerTile;
            return Clamp(sec, AnimParams.DigPokeMinSec, AnimParams.DigPokeMaxSec);
        }

        public static void Normalize(ref float x, ref float y)
        {
            float len = (float)Math.Sqrt(x * x + y * y);
            if (len < 1e-6f)
            {
                x = 0f;
                y = 0f;
                return;
            }

            x /= len;
            y /= len;
        }

        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        static float Lerp(float a, float b, float f) => a + (b - a) * f;
    }
}
