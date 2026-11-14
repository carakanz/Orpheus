#nullable enable
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Слой теней и видимость своего персонажа за кроной (E1-7, план 1.7). Тон суток — <see cref="DayTintView"/> (E1-8).
    /// Только вид: ничего не решает и ядро не трогает.
    /// <list type="bullet">
    /// <item><b>Тени.</b> Меши теней чанков и пятна под существами лежат в слое <see cref="ShadowLayer"/>; его рисует
    /// отдельная камера в текстуру (половина разрешения, смешивание «наибольшее») — тени не складываются в чёрное.
    /// Текстура ложится на землю один раз большим квадратом перед основной камерой (<see cref="WorldView.ShadowOrder"/>).</item>
    /// <item><b>Персонаж за кроной.</b> Шейдеру объектов каждый кадр отдаётся место своего персонажа: кроны перед ним
    /// становятся полупрозрачными в круге <see cref="HeroFadeRadius"/>.</item>
    /// </list>
    /// </summary>
    public static class WorldFx
    {
        /// <summary>Слой Unity для теней: основная камера его не видит, камера теней видит только его.</summary>
        public const int ShadowLayer = 31;

        /// <summary>Радиус круга вокруг своего персонажа, в котором кроны перед ним полупрозрачны, тайлов.</summary>
        public const float HeroFadeRadius = 1.7f;

        /// <summary>Непрозрачность кроны в середине круга.</summary>
        public const float HeroFadeAlpha = 0.28f;

        /// <summary>Ноги существа ниже центра круга коллизии (корня) на столько тайлов (опора спрайта).</summary>
        public const float FootOffset = 0.4f;

        static readonly int HeroId = Shader.PropertyToID("_OrphHero");
        static readonly int HeroFadeId = Shader.PropertyToID("_OrphHeroFade");
        static readonly int ShadowTexId = Shader.PropertyToID("_OrphShadowTex");
        static readonly int ShadowRectId = Shader.PropertyToID("_OrphShadowRect");

        static Camera? _main;
        static Camera? _shadowCam;
        static RenderTexture? _shadowRt;
        static Mesh? _blobMesh;
        static bool _ready;
        // ---------- Запуск ----------

        /// <summary>Создать камеру теней и квадраты наложения. Без видеокарты или набора — ничего не делает.</summary>
        public static void Init(Camera main, Transform hero)
        {
            _main = main;
            _ready = false;
            Shader.SetGlobalVector(HeroId, Vector4.zero);
            Shader.SetGlobalVector(HeroFadeId, new Vector4(HeroFadeAlpha, 0f, 0f, 0f));

            ObjectAtlas.Ensure();
            ObjectSet? set = ObjectAtlas.Set;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null || set == null)
            {
                return;
            }

            main.cullingMask &= ~(1 << ShadowLayer);
            if (main.GetComponent<WorldFxDriver>() == null)
            {
                main.gameObject.AddComponent<WorldFxDriver>();
            }

            if (set.ShadowCompositeShader != null && ObjectAtlas.ShadowMaterial != null && !Off("noshadow"))
            {
                var go = new GameObject("ShadowCamera");
                _shadowCam = go.AddComponent<Camera>();
                _shadowCam.orthographic = true;
                _shadowCam.clearFlags = CameraClearFlags.SolidColor;
                _shadowCam.backgroundColor = Color.clear;
                _shadowCam.cullingMask = 1 << ShadowLayer;
                _shadowCam.depth = main.depth - 10f;
                _shadowCam.allowHDR = false;
                _shadowCam.allowMSAA = false;
                _shadowCam.nearClipPlane = main.nearClipPlane;
                _shadowCam.farClipPlane = main.farClipPlane;
                _shadowCam.useOcclusionCulling = false;
                Overlay(main, "ShadowComposite", new Material(set.ShadowCompositeShader) { name = "ShadowComposite" }, WorldView.ShadowOrder);
                AddActorShadow(hero, 0.8f);
            }

            _ready = true;
            Sync(main.aspect, main.pixelHeight);
            Debug.Log($"[fx] слой теней: {(_shadowCam != null ? "есть" : "нет")}");
        }

        /// <summary>Отладка замеров: переменная среды ORPH_FX со словами noshadow, notint, norows выключает слой.</summary>
        public static bool Off(string word) => (System.Environment.GetEnvironmentVariable("ORPH_FX") ?? "").Contains(word);

        // Большой квадрат перед камерой (едет вместе с ней): закрывает любой обзор.
        static void Overlay(Camera main, string name, Material material, int order)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(main.transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, 5f);
            go.transform.localScale = new Vector3(600f, 600f, 1f);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.sortingOrder = order;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        }

        /// <summary>Пятно-тень под существом: маленький эллипс у ног (потомок корня существа, слой теней).</summary>
        public static void AddActorShadow(Transform actor, float width)
        {
            Material? material = ObjectAtlas.ShadowMaterial;
            int blob = ObjectAtlas.Find(ObjectAtlas.BlobName);
            if (material == null || blob < 0)
            {
                return;
            }

            if (_blobMesh == null)
            {
                Rect uv = ObjectAtlas.Entries[blob].Uv;
                _blobMesh = new Mesh { name = "ActorShadow" };
                _blobMesh.SetVertices(new[]
                {
                    new Vector3(-0.5f, -0.5f), new Vector3(0.5f, -0.5f), new Vector3(-0.5f, 0.5f), new Vector3(0.5f, 0.5f),
                });
                _blobMesh.SetUVs(0, new[]
                {
                    new Vector2(uv.xMin, uv.yMin), new Vector2(uv.xMax, uv.yMin), new Vector2(uv.xMin, uv.yMax), new Vector2(uv.xMax, uv.yMax),
                });
                var c = new Color32(0, 0, 0, 235);
                _blobMesh.SetColors(new[] { c, c, c, c });
                _blobMesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3 }, 0);
            }

            var go = new GameObject("Shadow") { layer = ShadowLayer };
            go.transform.SetParent(actor, false);
            go.transform.localPosition = new Vector3(0.1f, -FootOffset - 0.02f, 0f);
            go.transform.localScale = new Vector3(width, width * 0.42f, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = _blobMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        }

        // ---------- Кадр ----------

        /// <summary>Место своего персонажа (корень: центр круга коллизии) — для полупрозрачных крон перед ним.</summary>
        public static void SetHero(Vector3 root)
        {
            float foot = root.y - FootOffset;
            Shader.SetGlobalVector(HeroId, new Vector4(root.x, foot + 0.75f, foot, HeroFadeRadius));
        }

        /// <summary>Раз в кадр после того, как камера встала на место (<see cref="WorldFxDriver"/>).</summary>
        public static void LateFrame()
        {
            if (!_ready || _main == null)
            {
                return;
            }

            Sync(_main.aspect, _main.pixelHeight);
        }

        /// <summary>
        /// Перед ручной отрисовкой основной камеры в текстуру w × h (снимок, замер): камера теней встаёт под этот кадр и
        /// рисуется сразу — снимок не зависит от размера окна.
        /// </summary>
        public static void BeforeManualRender(int w, int h)
        {
            if (!_ready || _main == null)
            {
                return;
            }

            Sync(w / (float)h, h);
            DayTintView.Current?.Refresh(w / (float)h); // слой тона — под размер этого кадра
            if (_shadowCam != null)
            {
                _shadowCam.Render();
            }
        }

        static void Sync(float aspect, int pixelHeight)
        {
            Camera main = _main!;
            if (_shadowCam == null)
            {
                return;
            }

            // Текстура теней — половина разрешения кадра (мягкий край даром); растёт, но не уменьшается.
            int h = Mathf.Clamp(pixelHeight / 2, 270, 1080);
            int w = Mathf.Max(16, Mathf.RoundToInt(h * aspect));
            if (_shadowRt == null || _shadowRt.width < w || _shadowRt.height < h)
            {
                if (_shadowRt != null)
                {
                    w = Mathf.Max(w, _shadowRt.width);
                    h = Mathf.Max(h, _shadowRt.height);
                    _shadowCam.targetTexture = null;
                    _shadowRt.Release();
                    UnityEngine.Object.Destroy(_shadowRt);
                }

                RenderTextureFormat format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8)
                    ? RenderTextureFormat.R8
                    : RenderTextureFormat.ARGB32;
                _shadowRt = new RenderTexture(w, h, 16, format, RenderTextureReadWrite.Linear)
                {
                    name = "Shadows",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                };
                _shadowRt.Create();
                _shadowCam.targetTexture = _shadowRt;
                Shader.SetGlobalTexture(ShadowTexId, _shadowRt);
            }

            // Прямоугольник камеры теней = обзор основной; место — по сетке точек текстуры, чтобы край тени не «дышал» на бегу.
            float half = main.orthographicSize;
            float texel = 2f * half / _shadowRt.height;
            Vector3 p = main.transform.position;
            p.x = Mathf.Round(p.x / texel) * texel;
            p.y = Mathf.Round(p.y / texel) * texel;
            half += texel; // запас на округление места
            _shadowCam.transform.position = p;
            _shadowCam.orthographicSize = half;
            _shadowCam.aspect = aspect;
            float halfW = half * aspect;
            Shader.SetGlobalVector(ShadowRectId, new Vector4(p.x - halfW, p.y - half, 0.5f / halfW, 0.5f / half));
        }
    }
}
