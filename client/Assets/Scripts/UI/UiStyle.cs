#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// Стиль интерфейса (E2-2): тёмный контур, плоская заливка тёплых тонов (пергамент, охра), без градиентов.
    /// Рамки и ячейки — картинки, нарисованные кодом при запуске (скруглённый прямоугольник с контуром, 9 срезов);
    /// файлов-заглушек у интерфейса нет. Размеры — в единицах холста (эталон 1080 по высоте, <see cref="UiRoot"/>).
    /// </summary>
    public static class UiStyle
    {
        public static readonly Color32 Ink = new Color32(38, 28, 24, 255);
        public static readonly Color32 Parchment = new Color32(226, 200, 150, 255);
        public static readonly Color32 Cell = new Color32(196, 164, 112, 255);
        public static readonly Color32 CellBar = new Color32(208, 172, 104, 255);
        public static readonly Color32 CellHover = new Color32(240, 220, 176, 255);
        public static readonly Color32 Ochre = new Color32(240, 150, 36, 255);
        public static readonly Color32 TextDark = new Color32(52, 38, 30, 255);
        public static readonly Color32 TextLight = new Color32(250, 240, 214, 255);
        public static readonly Color32 Health = new Color32(196, 62, 48, 255);
        public static readonly Color32 Stamina = new Color32(226, 178, 58, 255);
        public static readonly Color32 BarBack = new Color32(86, 66, 52, 255);
        public static readonly Color32 Clear = new Color32(0, 0, 0, 0);

        // E2-4: выключенная кнопка / строка, приглушённый текст, «не хватает», фон карточки.
        public static readonly Color32 CellOff = new Color32(206, 184, 142, 255);
        public static readonly Color32 InkSoft = new Color32(128, 106, 84, 255);
        public static readonly Color32 TextDim = new Color32(134, 112, 88, 255);
        public static readonly Color32 TextBad = new Color32(184, 40, 30, 255);
        public static readonly Color32 Card = new Color32(214, 186, 134, 255);

        /// <summary>
        /// Наименьший размер шрифта, единиц холста (E2-4): 18 единиц = 12 px на кадре 1280 × 720 — мельче не читается.
        /// <see cref="UiRoot.Label"/> поднимает до него любой меньший размер.
        /// </summary>
        public const int MinFont = 18;

        /// <summary>Сторона ячейки и зазор между ячейками (C-008: 48 единиц = 48 px при 1080p, как в Terraria; было 64).</summary>
        public const float CellSize = 48f;
        public const float CellGap = 4f;

        /// <summary>Значок предмета в ячейке.</summary>
        public const float IconSize = 38f;

        // C-008: «стекло» — полупрозрачные ячейки поверх мира (сумка, панель, изготовление). Мир виден сквозь подложку
        // (~55 % непрозрачности), тонкая светлая рамка; цифры — светлые с тёмной обводкой. Пергамент остаётся для диалогов.
        public static readonly Color32 Glass = new Color32(46, 70, 150, 140);
        public static readonly Color32 GlassEdge = new Color32(176, 202, 255, 210);
        public static readonly Color32 GlassHover = new Color32(92, 128, 226, 190);
        public static readonly Color32 GlassOff = new Color32(30, 34, 60, 110);
        public static readonly Color32 GlassOffEdge = new Color32(120, 130, 170, 150);
        public static readonly Color32 GlassChest = new Color32(112, 58, 132, 145);
        public static readonly Color32 GlassChestEdge = new Color32(232, 186, 255, 210);
        public static readonly Color32 GlassPanel = new Color32(16, 14, 30, 165);
        public static readonly Color32 Gold = new Color32(255, 204, 72, 255);
        public static readonly Color32 GoldDark = new Color32(150, 104, 30, 255);
        public static readonly Color32 TextGood = new Color32(170, 240, 140, 255);
        public static readonly Color32 TextWarn = new Color32(255, 120, 100, 255);

        // C-008: сферы здоровья и выносливости (как у Diablo IV, цвета ярче — как в Terraria).
        public static readonly Color32 OrbHealth = new Color32(226, 34, 46, 255);
        public static readonly Color32 OrbStamina = new Color32(156, 214, 44, 255);

        const int TexSize = 32;
        const int Slice = 12;

        static readonly Dictionary<long, Sprite> _frames = new Dictionary<long, Sprite>();
        static Font? _font;

        /// <summary>
        /// Шрифт интерфейса. Временно — динамический шрифт ОС (кириллица есть, в репозитории файла шрифта нет);
        /// запасной — встроенный <c>LegacyRuntime.ttf</c> (им рисуется отладочная строка). Свой файл шрифта — потом.
        /// </summary>
        public static Font Font
        {
            get
            {
                if (_font == null)
                {
                    try
                    {
                        _font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial", "Tahoma", "DejaVu Sans" }, 24);
                    }
                    catch (System.Exception)
                    {
                        _font = null;
                    }

                    if (_font == null)
                    {
                        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    }
                }

                return _font!;
            }
        }

        /// <summary>
        /// Рамка с 9 срезами: скруглённый прямоугольник, заливка <paramref name="fill"/>, контур <paramref name="border"/>
        /// толщиной <paramref name="thick"/>. Прозрачная заливка — только контур (рамка выбранной ячейки).
        /// </summary>
        public static Sprite Frame(Color32 fill, Color32 border, float thick = 3f, float radius = 7f)
        {
            long key = ((long)Pack(fill) << 32) ^ Pack(border) ^ ((long)(thick * 4f) << 40) ^ ((long)(radius * 4f) << 52);
            if (_frames.TryGetValue(key, out Sprite? s) && s != null)
            {
                return s;
            }

            var tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var px = new Color32[TexSize * TexSize];
            float half = TexSize / 2f;
            for (int y = 0; y < TexSize; y++)
            {
                for (int x = 0; x < TexSize; x++)
                {
                    // Расстояние до края скруглённого прямоугольника (отрицательное — внутри).
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - radius);
                    float qy = Mathf.Abs(y + 0.5f - half) - (half - radius);
                    float d = Mathf.Min(Mathf.Max(qx, qy), 0f) + new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude - radius;
                    float outside = Mathf.Clamp01(d + 0.5f);             // 1 — снаружи
                    float inner = Mathf.Clamp01(-d - thick + 0.5f);      // 1 — заливка
                    Color c = Color.Lerp((Color)border, (Color)fill, inner);
                    if (fill.a == 0)
                    {
                        c = (Color)border;
                        c.a = (1f - inner) * border.a / 255f;
                    }

                    c.a *= 1f - outside;
                    px[y * TexSize + x] = c;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            s = Sprite.Create(tex, new Rect(0, 0, TexSize, TexSize), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(Slice, Slice, Slice, Slice));
            s.hideFlags = HideFlags.HideAndDontSave;
            _frames[key] = s;
            return s;
        }

        static readonly Dictionary<long, Sprite> _round = new Dictionary<long, Sprite>();

        /// <summary>
        /// C-008: круглые картинки кодом (128 × 128): 0 — «жидкость» сферы цвета <paramref name="c"/> (светлее в середине,
        /// темнее к краю и книзу); 1 — подложка сферы (тёмное стекло, золотое кольцо); 2 — блик (белый овал сверху слева);
        /// 3 — замок (закрытый слот способности). Файлов-заглушек нет.
        /// </summary>
        public static Sprite Round(int kind, Color32 c)
        {
            long key = ((long)kind << 32) ^ Pack(c);
            if (_round.TryGetValue(key, out Sprite? s) && s != null)
            {
                return s;
            }

            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var px = new Color32[n * n];
            float half = n / 2f;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float edge = Mathf.Clamp01((1f - r) * half); // 1 внутри, 0 снаружи, мягкий край в 1 пиксель
                    Color o = Color.clear;
                    switch (kind)
                    {
                        case 0:
                        {
                            float light = 0.62f + 0.5f * (1f - r * r) - 0.18f * (1f - (dy + 1f) / 2f);
                            Color baseC = c;
                            o = new Color(Mathf.Clamp01(baseC.r * light), Mathf.Clamp01(baseC.g * light), Mathf.Clamp01(baseC.b * light), edge);
                            break;
                        }
                        case 1:
                        {
                            float ring = r > 0.86f ? 1f : 0f;
                            Color inner = new Color(0.06f, 0.05f, 0.12f, 0.78f);
                            Color gold = Color.Lerp((Color)GoldDark, (Color)Gold, Mathf.Clamp01((dy + 1f) / 2f));
                            o = r > 0.93f ? (Color)Ink : ring > 0f ? gold : inner;
                            o.a *= edge;
                            break;
                        }
                        case 2:
                        {
                            float ex = (dx + 0.28f) / 0.42f, ey = (dy - 0.42f) / 0.24f;
                            float e = 1f - Mathf.Sqrt(ex * ex + ey * ey);
                            o = new Color(1f, 1f, 1f, Mathf.Clamp01(e * 1.6f) * 0.55f);
                            break;
                        }
                        case 3:
                        {
                            // Замок: тело — прямоугольник снизу, дужка — кольцо сверху.
                            bool body = Mathf.Abs(dx) < 0.46f && dy > -0.62f && dy < 0.02f;
                            float rr = Mathf.Sqrt(dx * dx + (dy - 0.08f) * (dy - 0.08f));
                            bool shackle = dy > 0.02f && rr > 0.22f && rr < 0.36f;
                            bool hole = Mathf.Abs(dx) < 0.07f && dy > -0.4f && dy < -0.14f;
                            o = (body && !hole) || shackle ? (Color)c : Color.clear;
                            break;
                        }
                    }

                    px[y * n + x] = o;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            s = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            s.hideFlags = HideFlags.HideAndDontSave;
            _round[key] = s;
            return s;
        }

        static uint Pack(Color32 c) => (uint)(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
    }
}
