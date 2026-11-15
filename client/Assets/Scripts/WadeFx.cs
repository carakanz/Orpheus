#nullable enable
using System.Collections.Generic;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Существо в воде и в кусте (C-018, слово Автора 15.11: «кусты и воду проходимыми, но замедляют скорость») — только вид.
    /// Где стоит существо — тайл под корнем (центр круга, как множитель скорости ядра <see cref="TerrainSlow"/>).
    /// <list type="bullet">
    /// <item><b>Вода:</b> спрайт опущен на <see cref="WaterDepth"/> и обрезан по линии ног — «по колено»; у линии воды — кольцо,
    /// при ходьбе — расходящиеся круги (пул <see cref="MaxRipples"/>), стоя — редкие; тени нет.</item>
    /// <item><b>Куст:</b> низ ног (<see cref="BushHide"/>) обрезан — за ним виден куст, сам куст шевелится (<see cref="LifeFx"/>).</item>
    /// </list>
    /// Обрезанные спрайты — тот же лист, меньший прямоугольник (кэш по числу точек обреза), материал и цвет не меняются.
    /// </summary>
    public static class WadeFx
    {
        /// <summary>Глубина «по колено» в воде, тайлов (спрайт 1,5 тайла — ~20 %).</summary>
        public const float WaterDepth = 0.3f;

        /// <summary>Сколько ног скрывает куст, тайлов.</summary>
        public const float BushHide = 0.2f;

        /// <summary>Скорость входа/выхода (тайлов глубины за секунду): граница не скачком.</summary>
        public const float DepthRate = 2.4f;

        public const int MaxRipples = 40;

        /// <summary>Круг живёт, с; при ходьбе — новый каждые <see cref="WalkRippleEvery"/> с, стоя — <see cref="IdleRippleEvery"/>.</summary>
        public const float RippleLife = 0.9f, WalkRippleEvery = 0.2f, IdleRippleEvery = 1.3f;

        public enum Ground : byte
        {
            Dry = 0,
            Water = 1,
            Bush = 2,
        }

        /// <summary>Вид земли под корнем существа (точка Unity).</summary>
        public static Ground At(Vector3 root)
        {
            if (!LifeFx.TryTileAt(root, out byte floor, out ushort obj))
            {
                return Ground.Dry;
            }

            if (TerrainSlow.FloorQ8(floor) != TerrainSlow.One)
            {
                return Ground.Water;
            }

            return TerrainSlow.ObjectQ8(obj) != TerrainSlow.One ? Ground.Bush : Ground.Dry;
        }

        // ---------- Обрезанные спрайты ----------

        static readonly Dictionary<(Sprite, int), Sprite?> _cropped = new Dictionary<(Sprite, int), Sprite?>();

        /// <summary>
        /// Спрайт без нижних <paramref name="cutPx"/> точек; опора — там же, где у исходного (видимая часть не сдвигается).
        /// Плотная упаковка (прямоугольника нет) — исходный.
        /// </summary>
        public static Sprite Cropped(Sprite sprite, int cutPx)
        {
            if (cutPx <= 0)
            {
                return sprite;
            }

            if (_cropped.TryGetValue((sprite, cutPx), out Sprite? s))
            {
                return s ?? sprite;
            }

            Sprite? made = null;
            if (!(sprite.packed && sprite.packingMode == SpritePackingMode.Tight))
            {
                Rect r = sprite.textureRect;
                Vector2 off = sprite.textureRectOffset;
                int cut = Mathf.Min(cutPx, (int)r.height - 2);
                var rect = new Rect(r.x, r.y + cut, r.width, r.height - cut);
                Vector2 pivotPx = sprite.pivot - off - new Vector2(0f, cut);
                var pivot = new Vector2(pivotPx.x / rect.width, pivotPx.y / rect.height);
                made = Sprite.Create(sprite.texture, rect, pivot, sprite.pixelsPerUnit, 0, SpriteMeshType.FullRect);
                made.name = $"{sprite.name} wade {cut}";
            }

            _cropped[(sprite, cutPx)] = made;
            return made ?? sprite;
        }

        // ---------- Кольцо и круги ----------

        static Sprite? _ring;

        /// <summary>Кольцо-эллипс (лист кодом, 128 × 48, белое; ширина 1 тайл).</summary>
        public static Sprite? Ring
        {
            get
            {
                if (_ring != null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    return _ring;
                }

                const int W = 128, H = 48;
                var tex = new Texture2D(W, H, TextureFormat.RGBA32, mipChain: false) { name = "WadeRing", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                var px = new Color32[W * H];
                for (int y = 0; y < H; y++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        float u = (x + 0.5f) / W * 2f - 1f;
                        float v = (y + 0.5f) / H * 2f - 1f;
                        float r = Mathf.Sqrt(u * u + v * v);
                        float d = Mathf.Abs(r - 0.86f) * W * 0.5f; // точки от средней линии кольца
                        float a = Mathf.Clamp01(2.2f - d) * (v > 0f ? 0.55f : 1f); // дальний край бледнее
                        px[y * W + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                    }
                }

                tex.SetPixels32(px);
                tex.Apply(false, true);
                _ring = Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), W, 0, SpriteMeshType.FullRect);
                _ring.name = "WadeRing";
                return _ring;
            }
        }

        static readonly Color RippleColor = new Color(0.85f, 0.95f, 1f, 1f);

        struct Ripple
        {
            public Vector2 Pos; // непрерывные координаты (точка Unity + сдвиг LifeFx)
            public float Start, Size;
            public int Order;
            public bool Live;
        }

        static readonly Ripple[] _ripples = new Ripple[MaxRipples];
        static SpriteRenderer?[] _rippleSr = new SpriteRenderer?[MaxRipples];
        static GameObject? _root;
        static float _now;

        /// <summary>Круг на воде у ног (точка Unity), порядок отрисовки — чуть позади существа.</summary>
        public static void Emit(Vector2 feet, int order, float size)
        {
            if (Ring == null)
            {
                return;
            }

            int slot = 0;
            float oldest = float.MaxValue;
            for (int i = 0; i < MaxRipples; i++)
            {
                if (!_ripples[i].Live)
                {
                    slot = i;
                    break;
                }

                if (_ripples[i].Start < oldest)
                {
                    oldest = _ripples[i].Start;
                    slot = i;
                }
            }

            _ripples[slot] = new Ripple { Pos = feet + LifeFx.Offset, Start = _now, Size = size, Order = order, Live = true };
        }

        /// <summary>Раз в кадр (<see cref="LifeFx.LateFrame"/>): круги растут и гаснут.</summary>
        public static void Frame(float time, Vector2 offset)
        {
            _now = time;
            for (int i = 0; i < MaxRipples; i++)
            {
                ref Ripple r = ref _ripples[i];
                SpriteRenderer? sr = _rippleSr[i];
                float age = time - r.Start;
                if (r.Live && age > RippleLife)
                {
                    r.Live = false;
                }

                if (!r.Live)
                {
                    if (sr != null)
                    {
                        sr.enabled = false;
                    }

                    continue;
                }

                if (sr == null)
                {
                    if (_root == null)
                    {
                        _root = new GameObject("WadeFx");
                    }

                    var go = new GameObject($"Ripple {i}");
                    go.transform.SetParent(_root.transform, false);
                    sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = Ring;
                    _rippleSr[i] = sr;
                }

                float k = age / RippleLife;
                float scale = r.Size * Mathf.Lerp(0.55f, 1.5f, 1f - (1f - k) * (1f - k));
                sr.enabled = true;
                sr.transform.position = new Vector3(r.Pos.x - offset.x, r.Pos.y - offset.y, 0f);
                sr.transform.localScale = new Vector3(scale, scale, 1f);
                sr.sortingOrder = r.Order;
                sr.color = new Color(RippleColor.r, RippleColor.g, RippleColor.b, 0.5f * (1f - k));
            }
        }
    }
}
