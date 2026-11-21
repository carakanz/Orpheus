#nullable enable
using UnityEngine;

namespace Orpheus.Client.Anim.Dragon
{
    /// <summary>
    /// D-004: огонь дракона — пул мягких круглых спрайтов: вылетают из пасти веером ±11°, растут, остывают по цвету
    /// (белый → жёлтый → оранжевый → красный → дым) и гаснут; сопротивление воздуха тормозит, дым поднимается.
    /// </summary>
    public sealed class FireFx
    {
        struct P
        {
            public Vector2 Pos, Vel;
            public float Age, Life, Size, Spin;
        }

        static Sprite? _soft;
        readonly SpriteRenderer[] _r;
        readonly P[] _p;
        int _next;
        float _carry;

        static Sprite Soft
        {
            get
            {
                if (_soft != null)
                {
                    return _soft;
                }

                const int n = 48;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01(1f - d);
                        a = a * a * (3f - 2f * a);
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                }

                tex.Apply();
                _soft = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
                return _soft;
            }
        }

        public FireFx(Transform parent, Material material, int capacity = 220)
        {
            var root = new GameObject("DragonFire").transform;
            root.SetParent(parent, false);
            _r = new SpriteRenderer[capacity];
            _p = new P[capacity];
            for (int i = 0; i < capacity; i++)
            {
                var go = new GameObject("f");
                go.transform.SetParent(root, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = Soft;
                sr.sharedMaterial = material;
                sr.enabled = false;
                _r[i] = sr;
                _p[i].Life = -1f;
            }
        }

        /// <summary>Кадр: выпускать (<paramref name="on"/>) из точки по направлению; порядок — поверх дракона.</summary>
        public void Step(float dt, bool on, Vector2 at, Vector2 dir, int order, float rate = 110f)
        {
            if (on)
            {
                _carry += rate * dt;
                float baseA = Mathf.Atan2(dir.y, dir.x);
                while (_carry >= 1f)
                {
                    _carry -= 1f;
                    int k = _next;
                    _next = (_next + 1) % _p.Length;
                    float a = baseA + Mathf.Deg2Rad * 11f * (Random.value * 2f - 1f);
                    float s = 3.4f * (0.75f + 0.25f * Random.value);
                    _p[k] = new P
                    {
                        Pos = at + Random.insideUnitCircle * 0.03f,
                        Vel = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * s,
                        Age = Random.value * dt,
                        Life = 0.5f + 0.25f * Random.value,
                        Size = 0.10f + 0.05f * Random.value,
                        Spin = Random.value * 360f,
                    };
                    _r[k].sortingOrder = order + 1 + (k % 3);
                }
            }
            else
            {
                _carry = 0f;
            }

            for (int i = 0; i < _p.Length; i++)
            {
                ref P p = ref _p[i];
                SpriteRenderer r = _r[i];
                if (p.Life <= 0f)
                {
                    if (r.enabled)
                    {
                        r.enabled = false;
                    }

                    continue;
                }

                p.Age += dt;
                float u = p.Age / p.Life;
                if (u >= 1f)
                {
                    p.Life = -1f;
                    r.enabled = false;
                    continue;
                }

                p.Vel *= Mathf.Exp(-dt * 2.6f);
                p.Vel.y += dt * 1.4f * u; // остывающий дым поднимается
                p.Pos += p.Vel * dt;
                float size = p.Size + 0.55f * Mathf.Sqrt(u);
                r.enabled = true;
                r.transform.position = new Vector3(p.Pos.x, p.Pos.y, 0f);
                r.transform.localScale = new Vector3(size, size * 0.85f, 1f);
                r.transform.localRotation = Quaternion.Euler(0f, 0f, p.Spin);
                r.color = Ramp(u);
            }
        }

        static Color Ramp(float u)
        {
            // белый → жёлтый → оранжевый → красный → дым
            if (u < 0.12f)
            {
                return Color.Lerp(new Color(1f, 1f, 0.85f, 0.95f), new Color(1f, 0.88f, 0.3f, 0.95f), u / 0.12f);
            }

            if (u < 0.4f)
            {
                return Color.Lerp(new Color(1f, 0.88f, 0.3f, 0.95f), new Color(1f, 0.5f, 0.1f, 0.9f), (u - 0.12f) / 0.28f);
            }

            if (u < 0.68f)
            {
                return Color.Lerp(new Color(1f, 0.5f, 0.1f, 0.9f), new Color(0.78f, 0.16f, 0.06f, 0.7f), (u - 0.4f) / 0.28f);
            }

            return Color.Lerp(new Color(0.42f, 0.26f, 0.22f, 0.5f), new Color(0.3f, 0.28f, 0.28f, 0f), (u - 0.68f) / 0.32f);
        }
    }
}
