#nullable enable
using System;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Лист картинок объектов (E1-7): один на игру. Из спрайтов <see cref="ObjectSet"/> при запуске собирается одна
    /// текстура с mip-картами (<see cref="Texture2D.PackTextures(Texture2D[],int,int,bool)"/>) — все объекты и их тени
    /// рисуются одним материалом. Размеры и опора — из самих спрайтов (128 px на тайл, опора внизу по центру), от
    /// содержимого картинок код не зависит: с Мака их заменяют под теми же именами.
    /// Без видеокарты (<c>-nographics</c>) листа и материалов нет, размеры картинок известны — меши собираются как обычно.
    /// </summary>
    public static class ObjectAtlas
    {
        /// <summary>Картинка листа: место в текстуре и размеры в тайлах.</summary>
        public struct Entry
        {
            public string Name;
            public Rect Uv;
            public float Width, Height;   // тайлов
            public float PivotX, PivotY;  // опора от левого нижнего угла, тайлов
        }

        /// <summary>Имя пятна-тени под персонажами и врагами (рисуется кодом).</summary>
        public const string BlobName = "_blob";

        static Entry[] _entries = new Entry[0];
        static bool _tried;

        /// <summary>Материал объектов; null — нет набора, шейдера или видеокарты.</summary>
        public static Material? Material { get; private set; }

        /// <summary>Материал теней (тот же лист, рисуется в текстуру теней).</summary>
        public static Material? ShadowMaterial { get; private set; }

        public static ObjectSet? Set { get; private set; }

        public static Entry[] Entries
        {
            get
            {
                Ensure();
                return _entries;
            }
        }

        /// <summary>Номер картинки по имени; −1 — нет.</summary>
        public static int Find(string name)
        {
            Ensure();
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i].Name == name)
                {
                    return i;
                }
            }

            return -1;
        }

        public static void Ensure()
        {
            if (_tried)
            {
                return;
            }

            _tried = true;
            try
            {
                Build();
            }
            catch (Exception e)
            {
                Debug.LogError("[objects] лист объектов не собран: " + e);
            }

            ObjectTable.Resolve();
        }

        static void Build()
        {
            ObjectSet? set = Resources.Load<ObjectSet>(ObjectSet.ResourceName);
            Set = set;
            if (set == null)
            {
                Debug.LogError("[objects] нет Resources/ObjectSet — объекты рисуются по-старому (Orpheus/Пересобрать набор объектов)");
                return;
            }

            int n = Mathf.Min(set.Names.Length, set.Sprites.Length);
            var entries = new Entry[n + 1];
            var textures = new Texture2D[n + 1];
            for (int i = 0; i < n; i++)
            {
                Sprite s = set.Sprites[i];
                float ppu = s.pixelsPerUnit;
                entries[i] = new Entry
                {
                    Name = set.Names[i],
                    Width = s.rect.width / ppu,
                    Height = s.rect.height / ppu,
                    PivotX = s.pivot.x / ppu,
                    PivotY = s.pivot.y / ppu,
                };
                textures[i] = s.texture;
            }

            entries[n] = new Entry { Name = BlobName, Width = 1f, Height = 1f, PivotX = 0.5f, PivotY = 0.5f };
            _entries = entries;

            bool noDevice = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
            if (noDevice || set.ObjectShader == null || set.ShadowShader == null)
            {
                Debug.Log(noDevice
                    ? $"[objects] устройство {SystemInfo.graphicsDeviceType}: объекты не рисуются (меши собираются), картинок {n}"
                    : "[objects] в наборе нет шейдера объектов или теней — объекты не рисуются");
                return;
            }

            textures[n] = BuildBlob();
            var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true)
            {
                name = "ObjectAtlas",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 2,
            };
            // Поля 16 px: соседние картинки не затекают друг в друга на первых четырёх mip-уровнях.
            Rect[] rects = atlas.PackTextures(textures, 16, 4096, makeNoLongerReadable: true);
            if (rects == null || rects.Length != textures.Length)
            {
                Debug.LogError("[objects] PackTextures не собрал лист (картинки не читаются?) — объекты не рисуются");
                return;
            }

            for (int i = 0; i < entries.Length; i++)
            {
                entries[i].Uv = rects[i];
            }

            Material = new Material(set.ObjectShader) { name = "Objects" };
            Material.SetTexture("_MainTex", atlas);
            ShadowMaterial = new Material(set.ShadowShader) { name = "ObjectShadows" };
            ShadowMaterial.SetTexture("_MainTex", atlas);
            Debug.Log($"[objects] лист {atlas.width}x{atlas.height}, картинок {n} + пятно; шейдеры: {set.ObjectShader.name} " +
                      $"(поддержан: {set.ObjectShader.isSupported}), {set.ShadowShader.name} (поддержан: {set.ShadowShader.isSupported})");
        }

        // Мягкое круглое пятно 64 × 64: тень под персонажем (в меше сплющивается в эллипс).
        static Texture2D BuildBlob()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "ShadowBlob" };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                    float a = Mathf.Clamp01((0.92f - Mathf.Sqrt(dx * dx + dy * dy)) / 0.25f);
                    px[y * size + x] = new Color32(0, 0, 0, (byte)(a * 255f + 0.5f));
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }
    }
}
