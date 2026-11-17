#nullable enable
using System.Collections.Generic;
using Orpheus.Client.Anim;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Net.Session;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Стрелы на экране (E3-2, шаг 3.2): спрайт стрелы с наклоном по оси полёта, след за ней; воткнувшаяся — дрожит
    /// <see cref="ClientProjectiles.StuckUs"/>, дальше на её месте предмет «воткнувшаяся стрела» той же позы (C-031,
    /// <see cref="StuckPose"/>, рисует <see cref="PileView"/>); попала — исчезает в теле. Высота полёта — середина тела
    /// (<see cref="AimMath.BodyCenterY"/>): прицел C-012 считается от неё, поэтому стрела на экране идёт ровно в курсор.
    /// Плюс прицел натяжения у курсора: тонкая линия от тела к курсору и кольцо силы, растущее до полного натяжения (без сектора).
    /// Только вид: полёт считает <see cref="ClientProjectiles"/>, попадания — сервер. Картинки — процедурные (заглушки).
    /// </summary>
    public sealed class ArrowView
    {
        /// <summary>Длина стрелы, тайлов.</summary>
        public const float ArrowTiles = 0.8f;

        /// <summary>Длина следа на полной скорости, тайлов.</summary>
        public const float TrailTiles = 1.3f;

        /// <summary>Дрожь воткнувшейся стрелы: градусов и секунд.</summary>
        public const float WobbleDeg = 9f;
        public const float WobbleSec = 0.35f;

        /// <summary>Воткнувшаяся стрела уходит в преграду, тайлов (точка ядра — последняя свободная перед ней, до 1/8 т от грани).</summary>
        public const float StuckEmbedTiles = 0.35f;

        /// <summary>Кольцо силы у курсора: радиус полного, тайлов.</summary>
        public const float RingTiles = 0.55f;

        /// <summary>Порядок отрисовки прицела — поверх мира.</summary>
        public const int AimOrder = 32000;

        sealed class Arrow
        {
            public GameObject Root = null!;
            public SpriteRenderer Body = null!;
            public SpriteRenderer Trail = null!;
            public bool Used;
        }

        readonly Transform _parent;
        readonly List<Arrow> _pool = new List<Arrow>();
        static Sprite? _arrow, _trail, _ring, _dot;

        readonly GameObject _aim;
        readonly SpriteRenderer _aimLine, _ringFull, _ringPower;

        public ArrowView(Transform parent)
        {
            _parent = parent;
            _aim = new GameObject("BowAim");
            _aim.transform.SetParent(parent, false);
            _aimLine = Child(_aim.transform, "Line", Dot(), AimOrder, new Color(1f, 1f, 1f, 0.22f));
            _ringFull = Child(_aim.transform, "RingFull", Ring(), AimOrder + 1, new Color(1f, 1f, 1f, 0.28f));
            _ringPower = Child(_aim.transform, "RingPower", Ring(), AimOrder + 2, Color.white);
            _aim.SetActive(false);
        }

        /// <summary>Стрел нарисовано в последнем кадре (для снимков и HUD).</summary>
        public int Shown { get; private set; }

        /// <summary>
        /// Кадр: стрелы <paramref name="list"/> на миг <paramref name="nowUs"/>; прицел натяжения — если <paramref name="bow"/> в натяжении
        /// (<paramref name="body"/> — корень своего персонажа, <paramref name="cursor"/> — точка под курсором; null — без прицела).
        /// </summary>
        public void Frame(ClientProjectiles list, long nowUs, FloatingOrigin origin, in BowAction bow, Vector3 body, Vector3? cursor)
        {
            list.Update(nowUs);
            foreach (Arrow a in _pool)
            {
                a.Used = false;
            }

            IReadOnlyList<FlyingProjectile> items = list.Items;
            int n = 0;
            for (int i = 0; i < items.Count; i++)
            {
                FlyingProjectile f = items[i];
                Arrow a = Take(n++);
                WorldPos at = list.PositionOf(f, nowUs);
                Vector3 ground = origin.ToUnity(at);
                AimMath.AxisOf(f.P.AimRaw, out float ax, out float ay);
                float deg = Mathf.Atan2(ay, ax) * Mathf.Rad2Deg;
                bool arrived = list.Arrived(f, nowUs);
                float trail = 0f;
                float wobble = 0f;
                if (arrived)
                {
                    float since = f.ArrivedUs == 0 ? 0f : (nowUs - f.ArrivedUs) / 1e6f;
                    float k = Mathf.Clamp01(1f - since / WobbleSec);
                    wobble = Mathf.Sin(since * 55f) * WobbleDeg * k * k;
                    deg += wobble;
                }
                else
                {
                    // след растёт от выстрела: у самого лука его нет
                    float flown = list.TravelX256(f, nowUs) / 65536f;
                    trail = Mathf.Min(TrailTiles * f.P.SpeedX256 / (28f * 256f * 256f / 30f), flown);
                }

                if (arrived && (f.Reason == ProjectileEnd.Wall || f.Dropped))
                {
                    // C-031: воткнулась — та же поза, что у предмета «воткнувшаяся стрела» (PileView), плюс дрожь; потом её сменит предмет
                    StuckPose(f.Reason == ProjectileEnd.Wall ? Orpheus.Core.Sim.GroundPile.KindArrowWall : Orpheus.Core.Sim.GroundPile.KindArrowGround,
                        f.P.AimRaw, out float sdeg, out Vector3 local, out Vector3 sscale);
                    a.Root.transform.position = ground;
                    a.Root.transform.rotation = Quaternion.identity;
                    a.Body.transform.localPosition = local;
                    a.Body.transform.localRotation = Quaternion.Euler(0f, 0f, sdeg + wobble);
                    a.Body.transform.localScale = sscale;
                    a.Trail.enabled = false;
                    int sorder = WorldView.SortOrderAt(ground.y - 0.05f) + 1;
                    a.Body.sortingOrder = sorder + 1;
                    continue;
                }

                Vector3 pos = ground + new Vector3(0f, AimMath.BodyCenterY, 0f);
                a.Root.transform.position = pos;
                a.Root.transform.rotation = Quaternion.Euler(0f, 0f, deg);
                a.Body.transform.localRotation = Quaternion.identity;
                a.Body.transform.localScale = Vector3.one;
                // наконечник — в точке ядра
                a.Body.transform.localPosition = new Vector3(-ArrowTiles * 0.5f, 0f, 0f);
                a.Trail.enabled = trail > 0.05f;
                a.Trail.transform.localPosition = new Vector3(-ArrowTiles * 0.6f - trail * 0.5f, 0f, 0f);
                a.Trail.transform.localScale = new Vector3(trail / (32f / 32f), 1f, 1f);
                int order = WorldView.SortOrderAt(ground.y - 0.05f) + 1;
                a.Body.sortingOrder = order + 1;
                a.Trail.sortingOrder = order;
            }

            Shown = n;
            foreach (Arrow a in _pool)
            {
                if (a.Root.activeSelf != a.Used)
                {
                    a.Root.SetActive(a.Used);
                }
            }

            AimFrame(bow, body, cursor);
        }

        void AimFrame(in BowAction bow, Vector3 body, Vector3? cursor)
        {
            bool show = bow.Phase == BowAction.PhaseDraw && cursor.HasValue;
            if (_aim.activeSelf != show)
            {
                _aim.SetActive(show);
            }

            if (!show)
            {
                return;
            }

            Vector3 c = cursor!.Value;
            c.z = body.z;
            Vector3 from = body + new Vector3(0f, AimMath.BodyCenterY, 0f);
            Vector3 d = c - from;
            float len = d.magnitude;
            _aim.transform.position = Vector3.zero;
            _aimLine.transform.position = from + d * 0.5f;
            _aimLine.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            _aimLine.transform.localScale = new Vector3(Mathf.Max(0f, len - RingTiles) * 16f, 0.04f * 16f, 1f);

            float power = BowAction.PowerQ8(bow.Frames) / 256f;
            bool full = bow.Frames >= BowAction.FullDrawFrames;
            _ringFull.transform.position = c;
            _ringFull.transform.localScale = Vector3.one * RingTiles * 2f;
            _ringPower.transform.position = c;
            float r = Mathf.Lerp(0.25f, 1f, power);
            _ringPower.transform.localScale = Vector3.one * RingTiles * 2f * r;
            _ringPower.color = full ? new Color(1f, 0.82f, 0.3f, 0.95f) : new Color(1f, 1f, 1f, 0.55f + 0.35f * power);
        }

        Arrow Take(int i)
        {
            while (_pool.Count <= i)
            {
                var root = new GameObject("Arrow");
                root.transform.SetParent(_parent, false);
                var a = new Arrow
                {
                    Root = root,
                    Body = Child(root.transform, "Body", ArrowSprite(), 0, Color.white),
                    Trail = Child(root.transform, "Trail", TrailSprite(), 0, new Color(1f, 0.97f, 0.88f, 0.55f)),
                };
                _pool.Add(a);
            }

            _pool[i].Used = true;
            return _pool[i];
        }

        static SpriteRenderer Child(Transform parent, string name, Sprite sprite, int order, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = order;
            sr.color = color;
            return sr;
        }

        /// <summary>
        /// C-031: поза воткнувшейся стрелы относительно точки ядра (корень предмета на земле): угол, место и масштаб спрайта.
        /// В преграде (1) — на высоте середины тела, наконечник уходит в преграду (как дрожащий снаряд у стены); в земле (2) —
        /// круто воткнута: короче в проекции, хвост приподнят, наконечник в точке. Одна функция для снаряда и предмета —
        /// предмет сменяет снаряд без скачка.
        /// </summary>
        public static void StuckPose(byte kind, ushort aim, out float deg, out Vector3 local, out Vector3 scale)
        {
            AimMath.AxisOf(aim, out float ax, out float ay);
            deg = Mathf.Atan2(ay, ax) * Mathf.Rad2Deg;
            if (kind == Orpheus.Core.Sim.GroundPile.KindArrowGround)
            {
                const float k = GroundLengthScale;
                Vector3 back = new Vector3(-ax, -ay, 0f) * (ArrowTiles * k * 0.5f - GroundEmbedTiles);
                local = back + new Vector3(0f, GroundTailLift, 0f);
                deg += ax >= 0f ? -GroundTiltDeg : GroundTiltDeg; // хвост вверх: наконечник смотрит в землю
                scale = new Vector3(k, 1f, 1f);
                return;
            }

            local = new Vector3(0f, AimMath.BodyCenterY, 0f) + new Vector3(ax, ay, 0f) * (-ArrowTiles * 0.5f + StuckEmbedTiles);
            scale = Vector3.one;
        }

        /// <summary>Стрела в земле: длина в проекции, наконечник в земле, подъём хвоста, наклон к земле (C-031, картинка).</summary>
        public const float GroundLengthScale = 0.6f;
        public const float GroundEmbedTiles = 0.08f;
        public const float GroundTailLift = 0.12f;
        public const float GroundTiltDeg = 18f;

        /// <summary>Стрела 32 × 5 px (0,8 тайла): наконечник серый, древко дерево, оперение светлое с красной полосой; ось — +X.</summary>
        internal static Sprite ArrowSprite()
        {
            if (_arrow != null)
            {
                return _arrow;
            }

            const int w = 32, h = 5;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            var clear = new Color32(0, 0, 0, 0);
            var shaft = new Color32(138, 92, 46, 255);
            var shaftDark = new Color32(96, 62, 30, 255);
            var head = new Color32(205, 208, 214, 255);
            var headDark = new Color32(120, 124, 132, 255);
            var feather = new Color32(236, 228, 210, 255);
            var stripe = new Color32(190, 52, 40, 255);
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = clear;
            }

            for (int x = 5; x < 27; x++)
            {
                px[2 * w + x] = shaft;
                px[1 * w + x] = x % 3 == 0 ? shaftDark : clear;
            }

            for (int x = 27; x < 32; x++)
            {
                int half = (31 - x) / 2; // клин
                for (int y = 2 - half; y <= 2 + half; y++)
                {
                    px[y * w + x] = y == 2 ? head : headDark;
                }
            }

            px[2 * w + 31] = head;
            for (int x = 0; x < 7; x++)
            {
                Color32 c = x == 3 ? stripe : feather;
                px[0 * w + x] = c;
                px[1 * w + x] = c;
                px[3 * w + x] = c;
                px[4 * w + x] = c;
                px[2 * w + x] = shaftDark;
            }

            tex.SetPixels32(px);
            tex.Apply();
            _arrow = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), w / ArrowTiles);
            return _arrow;
        }

        /// <summary>След: полоса 32 × 3 px, прозрачная к хвосту (1 тайл при масштабе 1).</summary>
        static Sprite TrailSprite()
        {
            if (_trail != null)
            {
                return _trail;
            }

            const int w = 32, h = 3;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            for (int x = 0; x < w; x++)
            {
                byte a = (byte)(255 * x * x / ((w - 1) * (w - 1)));
                px[0 * w + x] = new Color32(255, 255, 255, (byte)(a / 3));
                px[1 * w + x] = new Color32(255, 255, 255, a);
                px[2 * w + x] = new Color32(255, 255, 255, (byte)(a / 3));
            }

            tex.SetPixels32(px);
            tex.Apply();
            _trail = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), w);
            return _trail;
        }

        /// <summary>Кольцо 64 px, толщина 3 px (диаметр 1 тайл при масштабе 1).</summary>
        static Sprite Ring()
        {
            if (_ring != null)
            {
                return _ring;
            }

            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            float r = n * 0.5f - 2f;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Abs(new Vector2(x + 0.5f - n * 0.5f, y + 0.5f - n * 0.5f).magnitude - r);
                    byte a = (byte)(255 * Mathf.Clamp01(1.6f - d));
                    px[y * n + x] = new Color32(255, 255, 255, a);
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            _ring = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            return _ring;
        }

        /// <summary>Белая точка 1 px (16 px на тайл) — линия прицела растяжением.</summary>
        static Sprite Dot()
        {
            if (_dot != null)
            {
                return _dot;
            }

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            _dot = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 16f);
            return _dot;
        }
    }
}
