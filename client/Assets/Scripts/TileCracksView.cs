#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// C-028: трещины на камнях, по которым били взмахом кирки (<see cref="NetClient.Wear"/>: свои удары — сразу, чужие — по
    /// «взмаху» сервера). Три стадии по доле набранных ударов (1/3, 2/3, почти снят); гаснут через 5 с без ударов и при снятии
    /// объекта. Спрайты процедурные (тёмные изломы со светлой кромкой), пул без выделений в кадре.
    /// </summary>
    public sealed class TileCracksView
    {
        /// <summary>Тайлов с трещинами на экране одновременно, не больше.</summary>
        public const int PoolSize = 48;

        /// <summary>Подъём трещин над центром тайла (тело валуна стоит на <see cref="ObjectTable.BaseLift"/>), тайлов.</summary>
        public const float Lift = 0.5f;

        /// <summary>Размер спрайта трещин, тайлов.</summary>
        public const float Size = 1.2f;

        static Sprite[]? _stages;

        readonly SpriteRenderer[] _r = new SpriteRenderer[PoolSize];
        readonly float[] _pulse = new float[PoolSize];
        readonly int[] _hits = new int[PoolSize];
        readonly TileCoord[] _tile = new TileCoord[PoolSize];

        public TileCracksView(Transform parent)
        {
            var root = new GameObject("TileCracks");
            root.transform.SetParent(parent, false);
            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject("crack");
                go.transform.SetParent(root.transform, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.enabled = false;
                _r[i] = sr;
            }
        }

        /// <summary>Трещин видно сейчас (для smoke и снимков).</summary>
        public int Visible { get; private set; }

        /// <summary>Стадия трещин (1…3) по ударам из нужных; 0 — нет.</summary>
        public static int StageOf(int hits, int needed) =>
            hits <= 0 || needed <= 0 ? 0 : Mathf.Clamp(1 + (hits * 3 - 1) / needed, 1, 3);

        /// <summary>Кадр игры: удары клиента (свои и чужие), живы — пока объект тайла тот же и удары не истекли.</summary>
        public void Frame(NetClient client, FloatingOrigin origin, float dt) =>
            Frame(client.Wear, t => client.WearOf(t) > 0f, origin, dt);

        /// <summary>Кадр по любой таблице ударов (показ анимации — своя, без сети); <paramref name="live"/> — рисовать ли тайл.</summary>
        public void Frame(TileWear wear, System.Func<TileCoord, bool> live, FloatingOrigin origin, float dt)
        {
            Sprite[] stages = Stages();
            int used = 0;
            for (int i = 0; i < wear.Count && used < PoolSize; i++)
            {
                WearEntry e = wear[i];
                if (!live(e.Tile))
                {
                    continue; // объект сменился или удары истекли
                }

                int stage = StageOf(e.Hits, e.Needed);
                SpriteRenderer sr = _r[used];
                if (!sr.enabled || _tile[used] != e.Tile || _hits[used] != e.Hits)
                {
                    _pulse[used] = sr.enabled && _tile[used] == e.Tile ? 1f : 0.6f; // новый удар — трещины вздрагивают
                }

                _tile[used] = e.Tile;
                _hits[used] = e.Hits;
                _pulse[used] = Mathf.Max(0f, _pulse[used] - dt * 6f);
                var center = new WorldPos(e.Tile.X * Torus.SubTilesPerTile + Torus.SubTilesPerTile / 2,
                    e.Tile.Y * Torus.SubTilesPerTile + Torus.SubTilesPerTile / 2);
                Vector3 at = origin.ToUnity(center) + new Vector3(0f, Lift, 0f);
                Transform t = sr.transform;
                t.position = at;
                t.localScale = Vector3.one * (1f + 0.12f * _pulse[used]);
                sr.sprite = stages[stage - 1];
                sr.sortingOrder = WorldView.RowOrderAt(at.y - Lift - 0.5f + ObjectTable.BaseLift) + 1;
                sr.enabled = true;
                used++;
            }

            for (int i = used; i < PoolSize; i++)
            {
                _r[i].enabled = false;
            }

            Visible = used;
        }

        /// <summary>Три спрайта трещин: каждая следующая стадия — те же изломы плюс новые (детерминированно, без Random).</summary>
        static Sprite[] Stages()
        {
            if (_stages != null)
            {
                return _stages;
            }

            const int n = 64;
            var pixels = new Color32[n * n];
            _stages = new Sprite[3];
            uint seed = 0x9E3779B9u;
            for (int stage = 0; stage < 3; stage++)
            {
                int branches = stage == 0 ? 3 : 3;
                for (int b = 0; b < branches; b++)
                {
                    // Излом от середины (стадия 1) или от точки на прежнем изломе (дальше) — 5–8 шагов по 3–5 px.
                    float x = n / 2f + (stage == 0 ? 0f : (Next(ref seed) - 0.5f) * 20f);
                    float y = n / 2f + (stage == 0 ? 0f : (Next(ref seed) - 0.5f) * 16f);
                    float ang = (b + Next(ref seed) * 0.6f) * Mathf.PI * 2f / branches + stage * 0.7f;
                    int steps = 5 + (int)(Next(ref seed) * 4f);
                    for (int k = 0; k < steps; k++)
                    {
                        ang += (Next(ref seed) - 0.5f) * 1.1f;
                        float len = 3f + Next(ref seed) * 2.5f;
                        float nx = x + Mathf.Cos(ang) * len, ny = y + Mathf.Sin(ang) * len * 0.8f;
                        Line(pixels, n, x, y, nx, ny, k < 3 ? 3 : 2);
                        x = nx;
                        y = ny;
                        if (x < 3 || y < 3 || x > n - 4 || y > n - 4)
                        {
                            break;
                        }
                    }
                }

                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                tex.SetPixels32(pixels);
                tex.Apply();
                _stages[stage] = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n / Size);
            }

            return _stages;
        }

        // Тёмный излом шириной w px и светлая кромка снизу-справа (свет сверху-слева, как у валунов).
        static void Line(Color32[] px, int n, float x0, float y0, float x1, float y1, int w)
        {
            int steps = Mathf.CeilToInt(Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0)) * 2f) + 1;
            for (int i = 0; i <= steps; i++)
            {
                float f = i / (float)steps;
                int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, f));
                int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, f));
                for (int d = 0; d < w; d++)
                {
                    Put(px, n, x + 1, y - 1 - d, new Color32(214, 206, 186, 150), false);
                    Put(px, n, x, y - d, new Color32(28, 22, 20, 235), true);
                }
            }
        }

        static void Put(Color32[] px, int n, int x, int y, Color32 c, bool over)
        {
            if (x < 0 || y < 0 || x >= n || y >= n)
            {
                return;
            }

            int i = y * n + x;
            if (over || px[i].a == 0)
            {
                px[i] = c;
            }
        }

        static float Next(ref uint s)
        {
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            return (s & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
