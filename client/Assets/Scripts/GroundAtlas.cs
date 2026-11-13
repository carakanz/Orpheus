#nullable enable
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Материал земли (E1-4): один на игру. Из картинок <see cref="GroundSet"/> собирает <c>Texture2DArray</c> по списку
    /// <see cref="GroundTable.Layers"/> (номер слоя = место в списке), строит картинку шума и отдаёт материал шейдера
    /// <c>Orpheus/Ground</c>. Картинки с Мака могут смениться (размер, состав): слой без картинки — ровный цвет из таблицы,
    /// картинка другого размера — пересчитывается к размеру первой.
    /// </summary>
    public static class GroundAtlas
    {
        static Material? _material;
        static bool _tried;

        /// <summary>Тайлов на картинку земли по стороне (картинки 512 px = 4 тайла по 128 px).</summary>
        public const float TilesPerTexture = 4f;

        /// <summary>Общий материал земли; null — нет набора или шейдера (ошибка уже в логе).</summary>
        public static Material? Material
        {
            get
            {
                if (!_tried)
                {
                    _tried = true;
                    _material = Build();
                }

                return _material;
            }
        }

        static Material? Build()
        {
            GroundSet? set = Resources.Load<GroundSet>(GroundSet.ResourceName);
            if (set == null || set.Shader == null)
            {
                Debug.LogError("[ground] нет Resources/GroundSet или шейдера в нём — земля не рисуется (Orpheus/Пересобрать набор земли)");
                return null;
            }

            GroundLayer[] layers = GroundTable.Layers;
            int size = 0;
            for (int i = 0; i < layers.Length && size == 0; i++)
            {
                Texture2D? t = set.Find(layers[i].Name);
                if (t != null)
                {
                    size = t.width;
                }
            }

            if (size == 0)
            {
                size = 4;
            }

            var array = new Texture2DArray(size, size, layers.Length, TextureFormat.RGBA32, mipChain: true, linear: false)
            {
                name = "GroundLayers",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };
            int missing = 0;
            var buf = new Color32[size * size];
            for (int i = 0; i < layers.Length; i++)
            {
                Texture2D? t = set.Find(layers[i].Name);
                if (t != null && t.isReadable && t.width == size && t.height == size)
                {
                    array.SetPixels32(t.GetPixels32(0), i, 0);
                    continue;
                }

                if (t != null && t.isReadable)
                {
                    for (int y = 0; y < size; y++)
                    {
                        for (int x = 0; x < size; x++)
                        {
                            buf[y * size + x] = t.GetPixelBilinear((x + 0.5f) / size, (y + 0.5f) / size);
                        }
                    }

                    Debug.LogWarning($"[ground] слой {layers[i].Name}: картинка {t.width}x{t.height}, а набор {size}x{size} — пересчитана");
                }
                else
                {
                    for (int k = 0; k < buf.Length; k++)
                    {
                        buf[k] = layers[i].Fallback;
                    }

                    missing++;
                    Debug.LogWarning($"[ground] слой {layers[i].Name}: {(t == null ? "нет картинки" : "картинка не читается (импорт)")} — ровный цвет");
                }

                array.SetPixels32(buf, i, 0);
            }

            array.Apply(updateMipmaps: true, makeNoLongerReadable: true);

            var mat = new Material(set.Shader) { name = "Ground" };
            mat.SetTexture("_Layers", array);
            mat.SetTexture("_Noise", BuildNoise());
            mat.SetFloat("_TexTiles", TilesPerTexture);
            mat.SetFloat("_WaterLayers", GroundTable.WaterLayers);
            Debug.Log($"[ground] материал: шейдер {set.Shader.name} (поддержан: {set.Shader.isSupported}), слоёв {layers.Length} по {size}x{size}, без картинки {missing}");
            return mat;
        }

        // ---------- Шум ----------

        const int NoiseSize = 256;

        /// <summary>
        /// Бесшовная картинка шума 256 × 256. R — излом границ (шейдер берёт её с шагом 16 тайлов на картинку: волны 2, 1 и
        /// 0,5 тайла); G и B — крупные пятна (с шагом 64 тайла: пятна ~8 и ~16 тайлов) для смешивания против повтора и тона.
        /// </summary>
        static Texture2D BuildNoise()
        {
            var tex = new Texture2D(NoiseSize, NoiseSize, TextureFormat.RGBA32, mipChain: false, linear: true)
            {
                name = "GroundNoise",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[NoiseSize * NoiseSize];
            float[] r = Fbm(new[] { 8, 16, 32 }, new[] { 1f, 0.5f, 0.25f }, 11);
            float[] g = Fbm(new[] { 8, 16 }, new[] { 1f, 0.35f }, 23);
            float[] b = Fbm(new[] { 4, 8 }, new[] { 1f, 0.5f }, 37);
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32((byte)(r[i] * 255f + 0.5f), (byte)(g[i] * 255f + 0.5f), (byte)(b[i] * 255f + 0.5f), 255);
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        // Сумма октав шума значений на решётке cells × cells (замкнутой), растянутая на 0…1.
        static float[] Fbm(int[] cells, float[] amps, uint seed)
        {
            var v = new float[NoiseSize * NoiseSize];
            for (int o = 0; o < cells.Length; o++)
            {
                int n = cells[o];
                float scale = n / (float)NoiseSize;
                for (int y = 0; y < NoiseSize; y++)
                {
                    float fy = y * scale;
                    int y0 = (int)fy;
                    float ty = Smooth(fy - y0);
                    int y1 = (y0 + 1) % n;
                    for (int x = 0; x < NoiseSize; x++)
                    {
                        float fx = x * scale;
                        int x0 = (int)fx;
                        float tx = Smooth(fx - x0);
                        int x1 = (x0 + 1) % n;
                        float a = Mathf.Lerp(Hash(x0, y0, seed + (uint)o), Hash(x1, y0, seed + (uint)o), tx);
                        float c = Mathf.Lerp(Hash(x0, y1, seed + (uint)o), Hash(x1, y1, seed + (uint)o), tx);
                        v[y * NoiseSize + x] += Mathf.Lerp(a, c, ty) * amps[o];
                    }
                }
            }

            float min = float.MaxValue, max = float.MinValue;
            foreach (float f in v)
            {
                min = Mathf.Min(min, f);
                max = Mathf.Max(max, f);
            }

            for (int i = 0; i < v.Length; i++)
            {
                v[i] = (v[i] - min) / (max - min);
            }

            return v;
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);

        /// <summary>Хэш целой точки → 0…1 (один и тот же на любой машине).</summary>
        public static float Hash(int x, int y, uint seed)
        {
            uint h = (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^ seed * 0xC2B2AE3Du;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
