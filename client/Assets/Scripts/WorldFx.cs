#nullable enable
using System;
using System.Collections.Generic;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Слой теней, тон суток и свет источников, видимость своего персонажа за кроной (E1-7 и E1-8, план 1.7–1.8).
    /// Только вид: ничего не решает и ядро не трогает.
    /// <list type="bullet">
    /// <item><b>Тени.</b> Меши теней чанков и пятна под существами лежат в слое <see cref="ShadowLayer"/>; его рисует
    /// отдельная камера в текстуру (половина разрешения, смешивание «наибольшее») — тени не складываются в чёрное.
    /// Текстура ложится на землю один раз большим квадратом перед основной камерой (<see cref="WorldView.ShadowOrder"/>).</item>
    /// <item><b>Тон суток.</b> Квадрат поверх мира умножает кадр на цвет неба по доле суток; в кругах света факелов и
    /// костров тон плавно уходит в тёплый свет. Время — такт суток сервера (сеть), локальные часы без сервера или
    /// ключ <c>--shot-time</c>.</item>
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

        const int MaxLights = 32;

        static readonly int HeroId = Shader.PropertyToID("_OrphHero");
        static readonly int HeroFadeId = Shader.PropertyToID("_OrphHeroFade");
        static readonly int ShadowTexId = Shader.PropertyToID("_OrphShadowTex");
        static readonly int ShadowRectId = Shader.PropertyToID("_OrphShadowRect");
        static readonly int AmbientId = Shader.PropertyToID("_OrphAmbient");
        static readonly int LightColorId = Shader.PropertyToID("_OrphLightColor");
        static readonly int LightsId = Shader.PropertyToID("_OrphLights");
        static readonly int LightCountId = Shader.PropertyToID("_OrphLightCount");

        static Camera? _main;
        static Camera? _shadowCam;
        static RenderTexture? _shadowRt;
        static Mesh? _blobMesh;
        static bool _ready;
        static double _localTicks;
        static float _fixedTime = -1f;
        static Vector3 _hero;
        static bool _hasHero;
        static readonly List<Vector4> Found = new List<Vector4>(64);
        static readonly Vector4[] Lights = new Vector4[MaxLights];

        /// <summary>Окно чанков, из которого берутся источники света (ставит <see cref="WorldView"/>).</summary>
        public static WorldView? View;

        /// <summary>Сеть: такт суток по часам сервера (0…35 999) или −1, пока часы не сверены. Без сети — null.</summary>
        public static Func<int>? NetDayTicks;

        /// <summary>Доля суток 0…1 сейчас (0 — начало дня, 0,6 — сумерки, 0,65 — ночь, 0,95 — рассвет).</summary>
        public static float DayFraction
        {
            get
            {
                if (_fixedTime >= 0f)
                {
                    return _fixedTime;
                }

                int net = NetDayTicks != null ? NetDayTicks() : -1;
                double ticks = net >= 0 ? net : _localTicks;
                return (float)(ticks % DayClock.TicksPerDay / DayClock.TicksPerDay);
            }
        }

        /// <summary>
        /// Ключ <c>--shot-time</c>: «день», «сумерки», «ночь», «рассвет» (или day, dusk, night, dawn) либо доля суток 0…1.
        /// Возвращает false, если значение не понято.
        /// </summary>
        public static bool TryParseTime(string text, out float fraction)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "день":
                case "day":
                    fraction = 0.3f;
                    return true;
                case "сумерки":
                case "dusk":
                    fraction = (DayClock.DuskStartTick + DayClock.NightStartTick) * 0.5f / DayClock.TicksPerDay;
                    return true;
                case "ночь":
                case "night":
                    fraction = (DayClock.NightStartTick + DayClock.DawnStartTick) * 0.5f / DayClock.TicksPerDay;
                    return true;
                case "рассвет":
                case "dawn":
                    fraction = (DayClock.DawnStartTick + DayClock.TicksPerDay) * 0.5f / DayClock.TicksPerDay;
                    return true;
            }

            return float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out fraction)
                   && fraction >= 0f && fraction <= 1f;
        }

        // ---------- Тон суток: цвет неба по доле суток ----------

        static readonly Color Day = new Color(1f, 1f, 1f);
        static readonly Color Dusk = new Color(1f, 0.66f, 0.42f);       // тёплый оранжеватый
        static readonly Color Night = new Color(0.25f, 0.34f, 0.62f);   // тёмно-синий, яркость ~0,35–0,4
        static readonly Color Dawn = new Color(0.80f, 0.62f, 0.74f);    // холодно-розовый

        // Опорные точки: (доля суток, цвет); между ними — плавно. Границы фаз — DayClock.
        static readonly (float At, Color Tone)[] Keys = BuildKeys();

        static (float, Color)[] BuildKeys()
        {
            const float day = DayClock.TicksPerDay;
            float dusk = DayClock.DuskStartTick / day, night = DayClock.NightStartTick / day, dawn = DayClock.DawnStartTick / day;
            return new[]
            {
                (0f, Day),
                (dusk - 0.03f, Day),                                     // к вечеру свет теплеет ещё до сумерек
                (dusk, Color.Lerp(Day, Dusk, 0.45f)),
                ((dusk + night) * 0.5f, Dusk),
                (night, Color.Lerp(Dusk, Night, 0.8f)),
                (night + 0.012f, Night),
                (dawn, Night),
                ((dawn + 1f) * 0.5f, Dawn),
                (1f, Day),
            };
        }

        /// <summary>Цвет неба (множитель кадра) по доле суток 0…1.</summary>
        public static Color ToneAt(float f)
        {
            f = Mathf.Repeat(f, 1f);
            for (int i = 1; i < Keys.Length; i++)
            {
                if (f <= Keys[i].At)
                {
                    float span = Keys[i].At - Keys[i - 1].At;
                    float t = span > 1e-6f ? (f - Keys[i - 1].At) / span : 1f;
                    return Color.Lerp(Keys[i - 1].Tone, Keys[i].Tone, t * t * (3f - 2f * t));
                }
            }

            return Day;
        }

        // ---------- Запуск ----------

        /// <summary>Создать камеру теней и квадраты наложения. Без видеокарты или набора — ничего не делает.</summary>
        public static void Init(Camera main, Transform hero, ClientArgs args)
        {
            _main = main;
            _fixedTime = args.ShotTime;
            _localTicks = 0;
            _ready = false;
            _hasHero = false;
            Shader.SetGlobalVector(HeroId, Vector4.zero);
            Shader.SetGlobalVector(HeroFadeId, new Vector4(HeroFadeAlpha, 0f, 0f, 0f));
            Shader.SetGlobalVector(AmbientId, Vector4.one);
            Shader.SetGlobalVector(LightColorId, new Vector4(1f, 0.86f, 0.62f, 1f));
            Shader.SetGlobalVectorArray(LightsId, Lights);
            Shader.SetGlobalFloat(LightCountId, 0f);

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

            if (set.ShadowCompositeShader != null && ObjectAtlas.ShadowMaterial != null)
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

            if (set.DayTintShader != null)
            {
                Overlay(main, "DayTint", new Material(set.DayTintShader) { name = "DayTint" }, WorldView.TintOrder);
            }

            _ready = true;
            Sync(main.aspect, main.pixelHeight);
            Debug.Log($"[fx] тени: {(_shadowCam != null ? "есть" : "нет")}, тон суток: {(set.DayTintShader != null ? "есть" : "нет")}, " +
                      $"время {(_fixedTime >= 0f ? $"задано {_fixedTime:0.000}" : "по часам")}");
        }

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

        /// <summary>Место своего персонажа (корень: центр круга коллизии) — для полупрозрачных крон и ночного света рядом.</summary>
        public static void SetHero(Vector3 root)
        {
            _hero = root;
            _hasHero = true;
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

            _localTicks += Time.deltaTime * DayClock.TicksPerSecond;
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
            if (_shadowCam != null)
            {
                _shadowCam.Render();
            }
        }

        static void Sync(float aspect, int pixelHeight)
        {
            Camera main = _main!;
            UpdateTone(main, aspect);
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

        static void UpdateTone(Camera main, float aspect)
        {
            Color tone = ToneAt(DayFraction);
            Shader.SetGlobalVector(AmbientId, new Vector4(tone.r, tone.g, tone.b, 1f));
            int n = 0;
            float dark = 1f - (0.21f * tone.r + 0.72f * tone.g + 0.07f * tone.b);
            if (dark > 0.03f)
            {
                // Рядом со своим персонажем ночью чуть светлее: он и земля вокруг различимы.
                if (_hasHero)
                {
                    Lights[n++] = new Vector4(_hero.x, _hero.y, 3.4f, 0.3f);
                }

                WorldView? view = View;
                if (view != null)
                {
                    Found.Clear();
                    view.CollectLights(Found);
                    Vector3 c = main.transform.position;
                    float halfH = main.orthographicSize, halfW = halfH * aspect;
                    float t = Time.time;
                    for (int i = 0; i < Found.Count && n < MaxLights; i++)
                    {
                        Vector4 l = Found[i];
                        if (Mathf.Abs(l.x - c.x) > halfW + l.z || Mathf.Abs(l.y - c.y) > halfH + l.z)
                        {
                            continue; // круг света за экраном
                        }

                        l.z *= 1f + 0.025f * Mathf.Sin(t * 9f + l.x * 1.7f + l.y); // огонь слегка дрожит
                        Lights[n++] = l;
                    }
                }
            }

            Shader.SetGlobalVectorArray(LightsId, Lights);
            Shader.SetGlobalFloat(LightCountId, n);
        }
    }
}
