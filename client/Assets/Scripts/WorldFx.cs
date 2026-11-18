#nullable enable
using System.Collections.Generic;
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
    /// становятся полупрозрачными в круге <see cref="HeroFadeRadius"/>. E3-4: так же для чужих существ —
    /// ближайшие к своему (<see cref="SetActors"/>, до <see cref="MaxActorSilhouettes"/>).</item>
    /// <item><b>Персонаж за горой</b> (C-027): гора не прозрачна — спрайты своего персонажа (слой <see cref="HeroLayer"/>) рисует
    /// ещё одна камера в маску, и шейдер объектов кладёт по маске силуэт поверх горы, которая стоит перед персонажем.</item>
    /// </list>
    /// </summary>
    public static class WorldFx
    {
        /// <summary>Слой Unity для теней: основная камера его не видит, камера теней видит только его.</summary>
        public const int ShadowLayer = 31;

        /// <summary>C-027: слой спрайтов своего персонажа — их видят основная камера и камера маски силуэта.</summary>
        public const int HeroLayer = 30;

        /// <summary>C-027: силуэт персонажа за горой — заливка (цвет, сила) и контур (предварительно).</summary>
        public static readonly Vector4 SilhouetteFill = new Vector4(0.30f, 0.42f, 0.62f, 0.62f);
        public static readonly Vector4 SilhouetteLine = new Vector4(0.92f, 0.96f, 1f, 0.95f);

        static readonly int HeroMaskId = Shader.PropertyToID("_OrphHeroMask");
        static readonly int HeroRectId = Shader.PropertyToID("_OrphHeroRect");
        static readonly int HeroTexelId = Shader.PropertyToID("_OrphHeroTexel");
        static readonly int HeroFillId = Shader.PropertyToID("_OrphHeroFill");
        static readonly int HeroLineId = Shader.PropertyToID("_OrphHeroLine");
        static Camera? _maskCam;
        static RenderTexture? _maskRt;
        static Transform? _hero;
        static Texture2D? _noMask;
        static readonly System.Collections.Generic.List<Renderer> HeroRenderers = new System.Collections.Generic.List<Renderer>();

        /// <summary>Радиус круга вокруг своего персонажа, в котором кроны перед ним полупрозрачны, тайлов.</summary>
        public const float HeroFadeRadius = 1.7f;

        /// <summary>Непрозрачность кроны в середине круга.</summary>
        public const float HeroFadeAlpha = 0.28f;

        /// <summary>Ноги существа ниже центра круга коллизии (корня) на столько тайлов (опора спрайта).</summary>
        public const float FootOffset = 0.4f;

        static readonly int HeroId = Shader.PropertyToID("_OrphHero");
        static readonly int HeroFadeId = Shader.PropertyToID("_OrphHeroFade");
        // E3-4: силуэты чужих существ за кроной (шейдер Objects, слоты _OrphActors).
        public const int MaxActorSilhouettes = 8;
        static readonly int ActorsId = Shader.PropertyToID("_OrphActors");
        static readonly List<Vector4> _actorBuf = new List<Vector4>(MaxActorSilhouettes);
        static readonly int ShadowTexId = Shader.PropertyToID("_OrphShadowTex");
        static readonly int ShadowRectId = Shader.PropertyToID("_OrphShadowRect");

        static readonly int LightGridId = Shader.PropertyToID("_OrphLightGrid");
        static readonly int LightRectId = Shader.PropertyToID("_OrphLightRect");
        static readonly int LightSizeId = Shader.PropertyToID("_OrphLightSize");

        /// <summary>C-017: мир, начало координат и вид — для теней от огня (меши чанков, существа).</summary>
        public static Orpheus.Core.World.WorldModel? World { get; private set; }
        public static FloatingOrigin? Origin { get; private set; }
        static WorldView? _view;

        /// <summary>Новый мир (<see cref="WorldView"/>): тени от огня берут свет с учётом стен из него.</summary>
        public static void Bind(Orpheus.Core.World.WorldModel world, FloatingOrigin origin, WorldView view)
        {
            World = world;
            Origin = origin;
            _view = view;
        }

        static Texture2D? _noGrid;

        static Camera? _main;
        static Camera? _shadowCam;
        static RenderTexture? _shadowRt;
        static bool _ready;
        // ---------- Запуск ----------

        /// <summary>Создать камеру теней и квадраты наложения. Без видеокарты или набора — ничего не делает.</summary>
        public static void Init(Camera main, Transform hero)
        {
            _main = main;
            _ready = false;
            Shader.SetGlobalVector(HeroId, Vector4.zero);
            Shader.SetGlobalVector(HeroFadeId, new Vector4(HeroFadeAlpha, 0f, 0f, 0f));
            // C-027: без камеры маски — пустая маска (силуэта нет).
            if (_noMask == null)
            {
                _noMask = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { name = "NoHeroMask" };
                _noMask.SetPixel(0, 0, Color.clear);
                _noMask.Apply();
            }

            _hero = hero;
            Shader.SetGlobalTexture(HeroMaskId, _noMask);
            Shader.SetGlobalVector(HeroRectId, Vector4.zero);
            Shader.SetGlobalVector(HeroTexelId, Vector4.zero);
            Shader.SetGlobalVector(HeroFillId, SilhouetteFill);
            Shader.SetGlobalVector(HeroLineId, SilhouetteLine);
            // C-017: без слоя тона (notint) сетки света нет — «снаружи, без огня»; солнце — как днём, пока часы не придут.
            if (_noGrid == null)
            {
                _noGrid = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { name = "NoLightGrid" };
                _noGrid.SetPixel(0, 0, new Color(1f, 0f, 0f, 1f));
                _noGrid.Apply();
            }

            Shader.SetGlobalTexture(LightGridId, _noGrid);
            Shader.SetGlobalVector(LightRectId, new Vector4(0f, 0f, 0f, 0f));
            Shader.SetGlobalVector(LightSizeId, new Vector4(1f, 1f, 1f, 1f));
            SunTable.Apply(Orpheus.Core.World.DayClock.DuskStartTick / 2);

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

            if (!Off("nosilhouette"))
            {
                var mgo = new GameObject("HeroMaskCamera");
                _maskCam = mgo.AddComponent<Camera>();
                _maskCam.orthographic = true;
                _maskCam.clearFlags = CameraClearFlags.SolidColor;
                _maskCam.backgroundColor = Color.clear;
                _maskCam.cullingMask = 1 << HeroLayer;
                _maskCam.depth = main.depth - 9f;
                _maskCam.allowHDR = false;
                _maskCam.allowMSAA = false;
                _maskCam.nearClipPlane = main.nearClipPlane;
                _maskCam.farClipPlane = main.farClipPlane;
                _maskCam.useOcclusionCulling = false;
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

        /// <summary>
        /// Тень существа (C-017): пятно у ног, силуэт по солнцу и тени от огня — <see cref="ActorShadow"/> (потомок корня
        /// существа, слой теней). <paramref name="width"/> — ширина пятна у ног, <paramref name="height"/> — высота силуэта, тайлов.
        /// </summary>
        public static void AddActorShadow(Transform actor, float width, float height = ActorShadow.Height) => ActorShadow.Attach(actor, width, height);

        // ---------- Кадр ----------

        /// <summary>Место своего персонажа (корень: центр круга коллизии) — для полупрозрачных крон перед ним.</summary>
        public static void SetHero(Vector3 root)
        {
            float foot = root.y - FootOffset;
            Shader.SetGlobalVector(HeroId, new Vector4(root.x, foot + 0.75f, foot, HeroFadeRadius));
        }

        /// <summary>
        /// E3-4: силуэты чужих существ (враги и игроки) за кроной, кодировка как у <see cref="SetHero"/>;
        /// свободные слоты — выключены (w = 0). Каждый кадр вместе с <see cref="SetHero"/>.
        /// </summary>
        public static void SetActors(List<Vector3> roots)
        {
            _actorBuf.Clear();
            for (int i = 0; i < MaxActorSilhouettes; i++)
            {
                Vector4 slot = Vector4.zero;
                if (i < roots.Count)
                {
                    float foot = roots[i].y - FootOffset;
                    slot = new Vector4(roots[i].x, foot + 0.75f, foot, HeroFadeRadius);
                }

                _actorBuf.Add(slot);
            }

            Shader.SetGlobalVectorArray(ActorsId, _actorBuf);
        }

        /// <summary>Раз в кадр после того, как камера встала на место (<see cref="WorldFxDriver"/>).</summary>
        public static void LateFrame()
        {
            if (!_ready || _main == null)
            {
                return;
            }

            Sync(_main.aspect, _main.pixelHeight);
            _view?.FlushFire(6);
        }

        // C-027: спрайты своего персонажа (тело, призраки уворота, предмет в руке) — в слой маски; тень (слой 31) не трогаем.
        static void TagHero()
        {
            if (_hero == null)
            {
                return;
            }

            _hero.GetComponentsInChildren(true, HeroRenderers);
            foreach (Renderer r in HeroRenderers)
            {
                if (r is SpriteRenderer && r.gameObject.layer != ShadowLayer && r.gameObject.layer != HeroLayer)
                {
                    r.gameObject.layer = HeroLayer;
                }
            }
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
            LifeFx.BeforeManualRender(_main); // E1-11: правки травы этого кадра и параметры ветра
            DayTintView.Current?.Refresh(w / (float)h); // слой тона — под размер этого кадра
            UI.UiRoot.Current?.Fit(w / (float)h, h);    // E2-2: интерфейс — под размер этого кадра (текст — под его пиксели)
            _view?.FlushFire(int.MaxValue);             // C-017: тени от огня всех чанков — до снимка
            ActorShadow.UpdateAll();
            if (_shadowCam != null)
            {
                _shadowCam.Render();
            }

            if (_maskCam != null)
            {
                _maskCam.Render();
            }
        }

        /// <summary>
        /// C-017: замер CPU слоя теней на кадр (<c>ORPH_SHOT_BENCH</c>): проверка штампов теней огня всех чанков и тени
        /// существ (каждый кадр в игре), мс на кадр; и полная пересборка сетки света/комнат <see cref="DayTintView"/> (камера
        /// перешла линию сетки — раз в 8 тайлов пути), мс.
        /// </summary>
        public static string BenchCpu(int frames)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < frames; i++)
            {
                _view?.FlushFire(6);
                ActorShadow.UpdateAll();
            }

            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            int fills = 20;
            for (int i = 0; i < fills; i++)
            {
                DayTintView.Current?.BenchFill();
            }

            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return $"тени огня и существ {(t1 - t0) * f / frames:0.000} мс/кадр, сетка света {(t2 - t1) * f / fills:0.00} мс/пересборка";
        }

        /// <summary>C-017: замер кадра снимка (<c>ORPH_SHOT_BENCH</c>) — камера теней (и маски силуэта, C-027) рисуется каждый кадр, как в игре.</summary>
        public static void BenchShadows()
        {
            if (_ready && _shadowCam != null)
            {
                _shadowCam.Render();
            }

            if (_ready && _maskCam != null)
            {
                TagHero();
                _maskCam.Render(); // C-027: и камера маски силуэта — её цена входит в кадр
            }
        }

        static void Sync(float aspect, int pixelHeight)
        {
            Camera main = _main!;
            SyncMask(main, aspect, pixelHeight);
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

                // C-017: три канала — солнце, огонь, пятно у ног (Shaders/Shadow.shader).
                RenderTextureFormat format = RenderTextureFormat.ARGB32;
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

        // C-027: камера маски силуэта — обзор основной, половина разрешения (как тени), прямоугольник — в шейдер объектов.
        static void SyncMask(Camera main, float aspect, int pixelHeight)
        {
            if (_maskCam == null)
            {
                return;
            }

            TagHero();
            int h = Mathf.Clamp(pixelHeight / 2, 270, 1080);
            int w = Mathf.Max(16, Mathf.RoundToInt(h * aspect));
            if (_maskRt == null || _maskRt.width != w || _maskRt.height != h)
            {
                if (_maskRt != null)
                {
                    _maskCam.targetTexture = null;
                    _maskRt.Release();
                    UnityEngine.Object.Destroy(_maskRt);
                }

                _maskRt = new RenderTexture(w, h, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = "HeroMask",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                };
                _maskRt.Create();
                _maskCam.targetTexture = _maskRt;
                Shader.SetGlobalTexture(HeroMaskId, _maskRt);
                Shader.SetGlobalVector(HeroTexelId, new Vector4(1f / w, 1f / h, 0f, 0f));
            }

            float half = main.orthographicSize;
            Vector3 p = main.transform.position;
            _maskCam.transform.position = p;
            _maskCam.transform.rotation = main.transform.rotation;
            _maskCam.orthographicSize = half;
            _maskCam.aspect = aspect;
            float halfW = half * aspect;
            Shader.SetGlobalVector(HeroRectId, new Vector4(p.x - halfW, p.y - half, 0.5f / halfW, 0.5f / half));
        }
    }
}
