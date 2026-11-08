#nullable enable
using UnityEngine;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// Применение позы к существу на экране (K-001): корень существа ставит сеть/офлайн (позиция ядра — не трогаем),
    /// анимация двигает только дочерний спрайт — сдвиг, поворот вокруг опоры, масштаб, зеркало, цвет — и «призраки»
    /// уворота (копии спрайта рядом). Скорость для ходьбы — по сдвигу корня между кадрами.
    /// </summary>
    public sealed class ActorRig
    {
        /// <summary>Сдвиг корня за кадр больше этого (тайлов) — переезд «плавающего начала» или телепорт: не скорость.</summary>
        const float MaxFrameMove = 1.5f;

        readonly Transform _root;
        readonly Transform _sprite;
        readonly SpriteRenderer _sr;
        readonly Vector3 _basePos;
        readonly Color _baseColor;
        readonly SpriteRenderer[] _ghosts;
        Vector3 _lastRoot;
        bool _hasLast;
        Vector2 _vel;
        static int s_created;

        public readonly ProceduralAnimator Animator = new ProceduralAnimator();

        public ActorRig(Transform root)
        {
            _root = root;
            _sprite = root.GetChild(0);
            _sr = _sprite.GetComponent<SpriteRenderer>();
            _basePos = _sprite.localPosition;
            _baseColor = _sr.color;
            _ghosts = new SpriteRenderer[AnimParams.GhostCount];
            for (int i = 0; i < _ghosts.Length; i++)
            {
                var go = new GameObject($"Ghost {i}");
                go.transform.SetParent(root, false);
                var g = go.AddComponent<SpriteRenderer>();
                g.sprite = _sr.sprite;
                g.sortingOrder = _sr.sortingOrder;
                g.spriteSortPoint = _sr.spriteSortPoint;
                g.enabled = false;
                _ghosts[i] = g;
            }

            // Толпа не шагает в ногу: фаза — по порядку создания.
            Animator.PhaseOffset = (s_created++ % 7) * 0.37f;
        }

        public Transform Root => _root;
        public SpriteRenderer Renderer => _sr;

        /// <summary>Скорость корня на экране, т/с (сглажена).</summary>
        public Vector2 Velocity => _vel;

        /// <summary>Скорость по сдвигу корня за кадр; скачок (переезд начала, телепорт) — не движение.</summary>
        public Vector2 MeasureVelocity(float dt)
        {
            Vector3 p = _root.position;
            if (_hasLast && dt > 1e-5f)
            {
                Vector3 d = p - _lastRoot;
                if (d.sqrMagnitude < MaxFrameMove * MaxFrameMove)
                {
                    Vector2 v = new Vector2(d.x, d.y) / dt;
                    // Кадры рисования и такты 30 Гц не совпадают — без сглаживания скорость «пилит».
                    _vel = Vector2.Lerp(_vel, v, 1f - Mathf.Exp(-20f * dt));
                }
            }

            _lastRoot = p;
            _hasLast = true;
            return _vel;
        }

        /// <summary>Только ходьба по измеренной скорости (офлайн, чужие без уворота).</summary>
        public void AnimateWalk(float dt)
        {
            Vector2 v = MeasureVelocity(dt);
            var s = new ActorInput { VelX = v.x, VelY = v.y };
            Apply(Animator.Step(dt, s));
        }

        public void Apply(in Pose p)
        {
            _sprite.localPosition = _basePos + new Vector3(p.OffsetX, p.OffsetY, 0f);
            _sprite.localRotation = Quaternion.Euler(0f, 0f, p.RotationDeg);
            _sprite.localScale = new Vector3(p.ScaleX, p.ScaleY, 1f);
            _sr.flipX = p.FlipX;
            _sr.color = new Color(_baseColor.r * p.TintR, _baseColor.g * p.TintG, _baseColor.b * p.TintB, _baseColor.a * p.Alpha);

            for (int i = 0; i < _ghosts.Length; i++)
            {
                SpriteRenderer g = _ghosts[i];
                float a = p.GhostAlpha * (1f - i / (float)_ghosts.Length);
                if (a <= 0.01f)
                {
                    g.enabled = false;
                    continue;
                }

                g.enabled = true;
                float d = AnimParams.GhostSpacing * (i + 1);
                g.transform.localPosition = _basePos + new Vector3(p.GhostDirX * d, p.GhostDirY * d, 0f);
                g.transform.localRotation = _sprite.localRotation;
                g.transform.localScale = _sprite.localScale;
                g.flipX = p.FlipX;
                float[] c = AnimParams.GhostTint;
                g.color = new Color(c[0], c[1], c[2], a);
            }
        }
    }
}
