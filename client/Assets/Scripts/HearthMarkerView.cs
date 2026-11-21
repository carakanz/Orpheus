#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// E3-6b: видимый маркер Очага — тайл (W/2, H/2), центр мира (ядро знает его как RespawnCenter,
    /// ObjectId у него нет). Земляной круг из камней с тёплым дыханием в середине: лежачий диск света
    /// (порядок <see cref="FloorLift"/> — над полом и травой, под юнитами, — персонаж его не закрывает),
    /// ядро-уголёк и искры по кругу. Рисуется только когда чанк с тайлом на руках (окно стрима), —
    /// маркер не показывает то, чего игрок не видит. Спрайты процедурные, кадр детерминирован по времени снимка.
    /// </summary>
    public sealed class HearthMarkerView
    {
        /// <summary>Порядок рисования: над полом (0), травой (1) и тенями (3), под персонажами и предметами (10).</summary>
        public const int FloorLift = WorldView.FloorOrder + 5;

        const int Stones = 10;
        const int Sparks = 6;
        const float RingR = 0.62f; // радиус каменного кольца, тайлов (тайл = 1 м)

        readonly MapShape _map;
        readonly int _cx, _cy;
        readonly GameObject _root;
        readonly SpriteRenderer[] _stone = new SpriteRenderer[Stones];
        readonly SpriteRenderer[] _stoneTop = new SpriteRenderer[Stones];
        readonly SpriteRenderer[] _spark = new SpriteRenderer[Sparks];
        readonly SpriteRenderer _glow, _core;

        public HearthMarkerView(Transform parent, MapShape map, int centerTileX, int centerTileY)
        {
            _map = map;
            _cx = centerTileX;
            _cy = centerTileY;
            _root = new GameObject("HearthMarker");
            _root.transform.SetParent(parent, false);
            for (int i = 0; i < Stones; i++)
            {
                _stone[i] = MakeRenderer("stone");
                _stoneTop[i] = MakeRenderer("stone-top");
            }

            for (int i = 0; i < Sparks; i++)
            {
                _spark[i] = MakeRenderer("spark");
            }

            _glow = MakeRenderer("glow");
            _core = MakeRenderer("core");
        }

        /// <summary>Элементов маркера видно сейчас (для smoke и снимков): 0 — чанк с Очагом вне окна.</summary>
        public int Visible { get; private set; }

        SpriteRenderer MakeRenderer(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.enabled = false;
            sr.sortingOrder = FloorLift;
            return sr;
        }

        /// <summary>Кадр игры: чанк на руках — маркер стоит на тайле центра; время пульса — часы снимков.</summary>
        public void Frame(WorldView view, FloatingOrigin origin, long nowUs)
        {
            ChunkCoord cc = Torus.ChunkOf(_map, new TileCoord(_cx, _cy));
            if (!view.TryGetNode(cc, out _))
            {
                if (Visible != 0)
                {
                    HideAll();
                }

                Visible = 0;
                return;
            }

            float t = nowUs / 1_000_000.0f;
            float pulse = 0.5f + 0.5f * Mathf.Sin(t * 2.1f);
            float breathe = 0.5f + 0.5f * Mathf.Sin(t * 0.9f); // медленный «дох» жара
            Vector3 at = origin.ToUnity(new WorldPos(_cx * Torus.SubTilesPerTile + Torus.SubTilesPerTile / 2,
                _cy * Torus.SubTilesPerTile + Torus.SubTilesPerTile / 2));

            Sprites s = Assets();
            for (int i = 0; i < Stones; i++)
            {
                float ang = i * Mathf.PI * 2f / Stones + 0.31f; // сдвиг фазы — камни не по осям тайла
                Vector3 pos = at + new Vector3(Mathf.Cos(ang) * RingR, Mathf.Sin(ang) * RingR, 0f);
                // Свет сверху-слева, как у валунов (C-028): яркость камня по углу кольца.
                float lit = 0.5f + 0.5f * Mathf.Sin(ang + Mathf.PI * 0.25f);
                SpriteRenderer baseR = _stone[i];
                baseR.transform.position = pos;
                baseR.transform.localScale = Vector3.one * 0.28f; // основание крупнее — тёмная кромка-тень
                baseR.sprite = s.Stone;
                baseR.color = Color32.Lerp(new Color32(58, 56, 54, 255), new Color32(96, 92, 88, 255), lit);
                baseR.enabled = true;
                SpriteRenderer top = _stoneTop[i];
                top.transform.position = pos + new Vector3(-0.03f, 0.045f, 0f); // свет сверху-слева
                top.transform.localScale = Vector3.one * 0.17f;
                top.sprite = s.Stone;
                top.color = Color32.Lerp(new Color32(140, 136, 132, 255), new Color32(208, 204, 196, 255), lit);
                top.enabled = true;
            }

            _glow.sprite = s.Glow;
            _glow.transform.position = at;
            _glow.transform.localScale = Vector3.one * (1.35f + 0.12f * breathe);
            _glow.color = new Color32(255, 126, 42, (byte)(120 + 80 * breathe));
            _glow.enabled = true;

            _core.sprite = s.Core;
            _core.transform.position = at;
            _core.transform.localScale = Vector3.one * (0.30f + 0.07f * pulse);
            _core.color = new Color32(255, 214, 120, (byte)(170 + 70 * pulse));
            _core.enabled = true;

            for (int i = 0; i < Sparks; i++)
            {
                float ang = t * 0.7f + i * Mathf.PI * 2f / Sparks;
                float a = Mathf.Max(0f, Mathf.Sin(t * 1.3f + i * 1.05f));
                SpriteRenderer sr = _spark[i];
                sr.transform.position = at + new Vector3(Mathf.Cos(ang) * (RingR - 0.08f), Mathf.Sin(ang) * (RingR - 0.08f), 0f);
                sr.transform.localScale = Vector3.one * (0.10f + 0.05f * a);
                sr.sprite = s.Spark;
                sr.color = new Color32(255, 170, 70, (byte)(60 + 150 * a));
                sr.enabled = a > 0.02f;
            }

            Visible = Stones * 2 + 2 + Sparks;
        }

        void HideAll()
        {
            for (int i = 0; i < Stones; i++)
            {
                _stone[i].enabled = false;
                _stoneTop[i].enabled = false;
            }

            for (int i = 0; i < Sparks; i++)
            {
                _spark[i].enabled = false;
            }

            _glow.enabled = false;
            _core.enabled = false;
        }

        public void Dispose()
        {
            Object.Destroy(_root);
        }

        /// <summary>Спрайты маркера, общие на все copies-входы: диск-камень, радиальные glow и ядро, искра.</summary>
        sealed class Sprites
        {
            public Sprite Stone = null!, Glow = null!, Core = null!, Spark = null!;
        }

        static Sprites? _assets;

        /// <summary>Спрайт искры (C-049: им же рисуются искры у поставленного костра, CampfireSparkFx).</summary>
        public static Sprite SparkAsset() => Assets().Spark;

        static Sprites Assets()
        {
            if (_assets != null)
            {
                return _assets;
            }

            var s = new Sprites();
            s.Stone = Disc(32, 32f, 0.42f);  // 1 м / 32 px → спрайт 1 тайл, рисуем 0,5 т; мягкий край
            s.Glow = Disc(128, 128f, 0f);    // диск 1 т, убывание от центра к краю
            s.Core = Disc(48, 48f, 0f);
            s.Spark = Disc(16, 16f, 0.25f);
            _assets = s;
            return s;
        }

        /// <summary>Круглый спрайт: непрозрачен до <paramref name="solid"/>, дальше линейно до нуля на краю; ppm — пикселей на тайл.</summary>
        static Sprite Disc(int n, float ppm, float solid)
        {
            var px = new Color32[n * n];
            float c = (n - 1) / 2f;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (n * 0.5f);
                    byte a;
                    if (d <= solid)
                    {
                        a = 255;
                    }
                    else if (d >= 1f)
                    {
                        a = 0;
                    }
                    else
                    {
                        a = (byte)Mathf.RoundToInt(255f * (1f - (d - solid) / Mathf.Max(0.001f, 1f - solid)));
                    }

                    px[y * n + x] = new Color32(255, 255, 255, a);
                }
            }

            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), ppm);
        }
    }
}
