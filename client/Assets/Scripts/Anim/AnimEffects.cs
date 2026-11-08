#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace Orpheus.Client.Anim
{
    /// <summary>Общие ресурсы эффектов K-001: белый спрайт и материал спрайтов — из кода, без ассетов (`_TEMP` не нужен).</summary>
    public static class AnimAssets
    {
        static Sprite? _dot;
        static Material? _material;

        /// <summary>Круглая точка 8 × 8 px, белая, размером <see cref="AnimParams.ParticleSize"/> тайла.</summary>
        public static Sprite Dot
        {
            get
            {
                if (_dot != null)
                {
                    return _dot;
                }

                const int n = 8;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        float dx = x + 0.5f - n / 2f, dy = y + 0.5f - n / 2f;
                        float a = Mathf.Clamp01(n / 2f - Mathf.Sqrt(dx * dx + dy * dy));
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                }

                tex.Apply();
                _dot = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n / AnimParams.ParticleSize);
                return _dot;
            }
        }

        /// <summary>Материал спрайтов по умолчанию (цвет вершин × текстура) — для сетки сектора.</summary>
        public static Material SpriteMaterial(SpriteRenderer sample)
        {
            if (_material == null)
            {
                _material = new Material(sample.sharedMaterial) { name = "K001 Sector" };
            }

            return _material;
        }

        public static Color ToColor(float[] c, float alpha) => new Color(c[0], c[1], c[2], alpha);
    }

    /// <summary>
    /// Сектор удара на земле (`02` 3.1: центр — центр атакующего, радиус — досягаемость, угол — дуга, ось — прицел): обод
    /// показывает всю зону сразу, заливка растёт от центра к краю по доле замаха — когда дошла до обода, ударит.
    /// В миг удара сектор вспыхивает золотом и гаснет. Сетка пересобирается каждый кадр (≈ 100 вершин).
    /// </summary>
    public sealed class SectorView
    {
        const int Segments = 24;

        /// <summary>Над полом (0), под объектами и существами (10).</summary>
        public const int SortingOrder = 5;

        readonly GameObject _go;
        readonly Mesh _mesh;
        readonly List<Vector3> _v = new List<Vector3>(4 * (Segments + 1));
        readonly List<Color> _c = new List<Color>(4 * (Segments + 1));
        readonly List<int> _t = new List<int>(12 * Segments);

        public SectorView(Transform parent, Material material, string name)
        {
            _go = new GameObject(name);
            _go.transform.SetParent(parent, false);
            _mesh = new Mesh { name = name };
            _mesh.MarkDynamic();
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = _go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.sortingOrder = SortingOrder;
            _go.SetActive(false);
        }

        public void Hide() => _go.SetActive(false);

        /// <summary>
        /// Показать: центр (Unity), ось (единичная, экранные оси), радиус (тайлов), половина дуги (радианы), доля заливки 0…1,
        /// вспышка 0…1 (1 — миг удара), общая прозрачность (гаснет в отходе).
        /// </summary>
        public void Show(Vector3 center, Vector2 axis, float reach, float halfArc, float fill, float flash, float fade)
        {
            _go.SetActive(true);
            _go.transform.position = new Vector3(center.x, center.y + AnimParams.SectorGroundY, 0f);
            _v.Clear();
            _c.Clear();
            _t.Clear();
            float axisAngle = Mathf.Atan2(axis.y, axis.x);
            Color fillCol = AnimAssets.ToColor(AnimParams.SectorFill, AnimParams.SectorFillAlpha * fade);
            Color rimCol = AnimAssets.ToColor(AnimParams.SectorRim, AnimParams.SectorRimAlpha * fade);
            if (flash > 0f)
            {
                Color fl = AnimAssets.ToColor(AnimParams.StrikeFlash, 1f);
                fillCol = Color.Lerp(fillCol, new Color(fl.r, fl.g, fl.b, 0.75f), flash);
                rimCol = Color.Lerp(rimCol, fl, flash);
                fill = Mathf.Max(fill, flash > 0.01f ? 1f : fill);
            }

            // Заливка: веер от центра до радиуса reach × fill.
            if (fill > 0.01f)
            {
                AddRing(0f, reach * Mathf.Clamp01(fill), axisAngle, halfArc, fillCol);
            }

            // Обод — полоса у края, и тонкие края дуги.
            float rim = reach * AnimParams.SectorRimWidth;
            AddRing(reach - rim, reach, axisAngle, halfArc, rimCol);
            AddEdge(axisAngle - halfArc, reach, rim * 0.6f, rimCol);
            AddEdge(axisAngle + halfArc, reach, rim * 0.6f, rimCol);

            _mesh.Clear();
            _mesh.SetVertices(_v);
            _mesh.SetColors(_c);
            _mesh.SetTriangles(_t, 0);
            _mesh.RecalculateBounds();
        }

        void AddRing(float r0, float r1, float axis, float half, Color col)
        {
            int start = _v.Count;
            for (int i = 0; i <= Segments; i++)
            {
                float a = axis - half + 2f * half * i / Segments;
                var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                _v.Add(d * r0);
                _v.Add(d * r1);
                _c.Add(col);
                _c.Add(col);
            }

            for (int i = 0; i < Segments; i++)
            {
                int k = start + i * 2;
                _t.Add(k); _t.Add(k + 1); _t.Add(k + 3);
                _t.Add(k); _t.Add(k + 3); _t.Add(k + 2);
            }
        }

        void AddEdge(float angle, float reach, float width, Color col)
        {
            var d = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
            var n = new Vector3(-d.y, d.x, 0f) * (width * 0.5f);
            int k = _v.Count;
            _v.Add(-n); _v.Add(n); _v.Add(d * reach + n); _v.Add(d * reach - n);
            for (int i = 0; i < 4; i++)
            {
                _c.Add(col);
            }

            _t.Add(k); _t.Add(k + 2); _t.Add(k + 1);
            _t.Add(k); _t.Add(k + 3); _t.Add(k + 2);
        }
    }

    /// <summary>
    /// Частицы K-001 — пыль и щепки копания, искры попадания: пул спрайтов-точек, без ParticleSystem. Летят из точки
    /// веером, «падают» вниз экрана (вид 3/4) и гаснут. Позиции — в координатах Unity; переезд начала за их короткую жизнь
    /// (≤ 0,5 с) сдвинул бы их на кадр — допустимо.
    /// </summary>
    public sealed class Sparks
    {
        struct P
        {
            public Vector2 Pos, Vel;
            public float Age, Life;
            public Color Col;
            public bool Fall;
        }

        readonly SpriteRenderer[] _r;
        readonly P[] _p;
        int _next;

        public Sparks(Transform parent, int capacity = 160)
        {
            var root = new GameObject("Sparks");
            root.transform.SetParent(parent, false);
            _r = new SpriteRenderer[capacity];
            _p = new P[capacity];
            for (int i = 0; i < capacity; i++)
            {
                var go = new GameObject("p");
                go.transform.SetParent(root.transform, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = AnimAssets.Dot;
                sr.sortingOrder = WorldView.ObjectOrder + 2;
                sr.enabled = false;
                _r[i] = sr;
            }
        }


        /// <summary>Живых частиц.</summary>
        public int Alive { get; private set; }

        /// <summary>Выбросить <paramref name="count"/> частиц из точки веером ±<paramref name="spreadDeg"/> вокруг направления.</summary>
        public void Emit(Vector2 at, Vector2 dir, int count, float speed, float life, float spreadDeg, bool fall, params float[][] colors)
        {
            float baseAngle = dir.sqrMagnitude > 1e-6f ? Mathf.Atan2(dir.y, dir.x) : Mathf.PI * 0.5f;
            for (int i = 0; i < count; i++)
            {
                int k = _next;
                _next = (_next + 1) % _p.Length;
                float a = baseAngle + Mathf.Deg2Rad * spreadDeg * (Random.value * 2f - 1f);
                float s = speed * (0.55f + 0.45f * Random.value);
                float[] c = colors[i % colors.Length];
                _p[k] = new P
                {
                    Pos = at,
                    Vel = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * s,
                    Age = 0f,
                    Life = life * (0.7f + 0.3f * Random.value),
                    Col = new Color(c[0], c[1], c[2], 1f),
                    Fall = fall,
                };
                _r[k].enabled = true;
            }
        }

        public void Update(float dt)
        {
            int alive = 0;
            for (int i = 0; i < _p.Length; i++)
            {
                if (!_r[i].enabled)
                {
                    continue;
                }

                ref P p = ref _p[i];
                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    _r[i].enabled = false;
                    continue;
                }

                if (p.Fall)
                {
                    p.Vel.y -= AnimParams.ParticleGravity * dt;
                }
                else
                {
                    p.Vel *= Mathf.Exp(-6f * dt); // искры тормозят
                }

                p.Pos += p.Vel * dt;
                float f = 1f - p.Age / p.Life;
                Transform t = _r[i].transform;
                t.position = new Vector3(p.Pos.x, p.Pos.y, 0f);
                t.localScale = Vector3.one * (0.6f + 0.6f * f);
                Color c = p.Col;
                c.a = f;
                _r[i].color = c;
                alive++;
            }

            Alive = alive;
        }
    }
}
