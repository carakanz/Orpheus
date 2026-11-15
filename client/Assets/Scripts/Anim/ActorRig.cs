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
        readonly int _lifeId = ++s_lifeIds; // E1-11: номер существа для следа в траве и толчков деревьев
        static int s_lifeIds;

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

        // E2-7: предмет в руке — значок на дочернем «Hand» спрайта (наследует сдвиг, наклон и приседание тела).
        Transform? _hand;
        Transform? _handIcon;
        SpriteRenderer? _handSr;
        byte _held;
        HeldLook _look;

        /// <summary>Как держится предмет в руке (для <see cref="ActorInput.Held"/>).</summary>
        public HeldLook Held => _look;

        /// <summary>Предмет в руке (id из таблицы предметов; 0 — пусто): значок появляется у кисти.</summary>
        public void SetHeld(byte item)
        {
            if (item == _held && (_hand != null || item == 0))
            {
                return;
            }

            _held = item;
            _look = ActionAnim.LookOf(item);
            if (_look == HeldLook.None)
            {
                if (_handSr != null)
                {
                    _handSr.enabled = false;
                }

                return;
            }

            if (_hand == null)
            {
                var hand = new GameObject("Hand");
                hand.transform.SetParent(_sprite, false);
                var icon = new GameObject("Icon");
                icon.transform.SetParent(hand.transform, false);
                _handSr = icon.AddComponent<SpriteRenderer>();
                _handSr.spriteSortPoint = _sr.spriteSortPoint;
                _hand = hand.transform;
                _handIcon = icon.transform;
            }

            _handSr!.sprite = PileView.SpriteFor(item);
            _handSr.enabled = _handSr.sprite != null;
            ActionAnim.IconLocal(_look, out float ix, out float iy); // C-012: те же числа, что в ActionAnim.HeldPoint (тесты)
            _handIcon!.localPosition = new Vector3(ix, iy, 0f);
        }

        void ApplyHand(in Pose p, int order)
        {
            if (_hand == null || _handSr == null || !_handSr.enabled)
            {
                return;
            }

            bool right = p.FlipX != AnimParams.SpriteFacesRight;
            float sign = right ? 1f : -1f;
            float k = ActionAnim.HeldScale(_look); // C-012: меч — свой масштаб под досягаемость удара ядра
            // C-010: занесённый за голову предмет рисуется позади тела — кисть плавно поднимается к макушке вместе с углом
            // (ActionAnim.HandLocal — тот же расчёт, что проверяют тесты).
            ActionAnim.HandLocal(p, out float hx, out float hy);
            _hand.localPosition = new Vector3(hx * sign, hy, 0f);
            // Поворот после зеркала: зеркальный значок поворачивается в обратную сторону (M = T·R·S).
            _hand.localRotation = Quaternion.Euler(0f, 0f, p.HandDeg * sign);
            _hand.localScale = new Vector3(k * sign, k, 1f);
            _handSr.sortingOrder = p.HandBehind ? order - 1 : order + 1;
            _handSr.color = new Color(p.TintR, p.TintG, p.TintB, _baseColor.a * p.Alpha);
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
            var s = new ActorInput { VelX = v.x, VelY = v.y, Held = _look };
            Apply(Animator.Step(dt, s));
        }

        // C-018: вода и куст — глубина (опускание спрайта в воде) и обрез низа (вода — по линии ног, куст — низ ног).
        Sprite? _baseSprite;
        float _sink, _hide, _rippleWait;
        SpriteRenderer? _waterLine;
        Transform? _shadow;
        bool _shadowLooked;

        void ApplyWade(int order)
        {
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            WadeFx.Ground g = WadeFx.At(_root.position);
            _sink = Mathf.MoveTowards(_sink, g == WadeFx.Ground.Water ? WadeFx.WaterDepth : 0f, WadeFx.DepthRate * dt);
            _hide = Mathf.MoveTowards(_hide, g == WadeFx.Ground.Bush ? WadeFx.BushHide : 0f, WadeFx.DepthRate * dt);
            if (_baseSprite == null)
            {
                _baseSprite = _sr.sprite;
            }

            float cut = Mathf.Max(_sink, _hide);
            if (_baseSprite != null)
            {
                // Обрез — в точках листа, шагом 2 (кэш спрайтов мал); опускание — в воде.
                int px = Mathf.RoundToInt(cut * _baseSprite.pixelsPerUnit * 0.5f) * 2;
                _sr.sprite = WadeFx.Cropped(_baseSprite, px);
            }

            _sprite.localPosition += new Vector3(0f, -_sink, 0f);

            if (!_shadowLooked)
            {
                _shadow = _root.Find("Shadow");
                _shadowLooked = true;
            }

            if (_shadow != null)
            {
                bool dry = _sink < 0.05f; // тени в воде нет
                if (_shadow.gameObject.activeSelf != dry)
                {
                    _shadow.gameObject.SetActive(dry);
                }
            }

            float wet = _sink / WadeFx.WaterDepth;
            Vector2 feet = new Vector2(_root.position.x, _root.position.y + _basePos.y);
            if (wet > 0.05f && WadeFx.Ring != null)
            {
                if (_waterLine == null)
                {
                    var go = new GameObject("WaterLine");
                    go.transform.SetParent(_root, false);
                    _waterLine = go.AddComponent<SpriteRenderer>();
                    _waterLine.sprite = WadeFx.Ring;
                }

                _waterLine.enabled = true;
                float pulse = 1f + 0.05f * Mathf.Sin(LifeFx.Time * 5f + _lifeId);
                _waterLine.transform.localPosition = new Vector3(0f, _basePos.y + 0.02f, 0f);
                _waterLine.transform.localScale = new Vector3(0.62f * pulse, 0.62f * pulse, 1f);
                _waterLine.sortingOrder = order + 2;
                _waterLine.color = new Color(0.85f, 0.95f, 1f, 0.55f * wet);

                _rippleWait -= dt;
                bool moving = _vel.sqrMagnitude > 0.16f;
                if (_rippleWait <= 0f)
                {
                    WadeFx.Emit(feet, order - 1, moving ? 0.9f : 0.75f);
                    _rippleWait = moving ? WadeFx.WalkRippleEvery : WadeFx.IdleRippleEvery;
                }
            }
            else
            {
                if (_waterLine != null)
                {
                    _waterLine.enabled = false;
                }

                _rippleWait = 0f;
            }
        }

        public void Apply(in Pose p)
        {
            _sprite.localPosition = _basePos + new Vector3(p.OffsetX, p.OffsetY, 0f);
            _sprite.localRotation = Quaternion.Euler(0f, 0f, p.RotationDeg);
            _sprite.localScale = new Vector3(p.ScaleX, p.ScaleY, 1f);
            _sr.flipX = p.FlipX;
            // E1-7: существо сортируется с объектами «с высотой» по нижней кромке (ноги — опора спрайта без сдвига позы).
            int order = WorldView.SortOrderAt(_root.position.y + _basePos.y);
            _sr.sortingOrder = order;
            // E1-11: существо приминает траву под ногами и задевает деревья рядом (только вид).
            LifeFx.Touch(_lifeId, new Vector2(_root.position.x, _root.position.y + _basePos.y));
            _sr.color = new Color(_baseColor.r * p.TintR, _baseColor.g * p.TintG, _baseColor.b * p.TintB, _baseColor.a * p.Alpha);
            ApplyHand(p, order);
            ApplyWade(order);

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
                g.sortingOrder = order;
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
