#nullable enable
using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Тон суток (план 1.8, карточка E1-8): полноэкранный полупрозрачный слой поверх мира — темнота ночи,
    /// сумерек и рассвета; свет факела и костра — «дырки» в темноте.
    /// <para>
    /// Кадр считается так: для прямоугольника камеры строится сетка тайлов (текстура R8: k биома по полу,
    /// G8: свет источников из <see cref="LightIndex"/>, билинейно — свет кругом). Шейдер «Orpheus/DayTint»
    /// (в <c>Assets/Resources</c> — так шейдер гарантированно в сборке) рисует альфу (1 − L) × <c>_Max</c>,
    /// где L = фон × k + источники (<see cref="DayTint"/>), цвет темноты — холодный синий ночью, тёплый
    /// в сумерках и на рассвете. Днём на открытой земле L = 1 — слой невидим; лес чуть темнее (k = 870).
    /// </para>
    /// <para>
    /// Сетка перестраивается, только если изменился прямоугольник камеры (движение, зум) или ревизии чанков
    /// под ним; фон и цвет темноты — униформы, меняются каждый кадр. Часами владеет вызывающий:
    /// офлайн — <c>Simulation.WorldTime</c> + ключ <c>--day-tick</c>, сеть — <c>WorldClock.TicksAt</c>.
    /// </para>
    /// </summary>
    public sealed class DayTintView
    {
        /// <summary>Максимальная альфа темноты при L = 0 (полная тьма — не чернота: 0,78).</summary>
        public const float MaxDarkness = 0.78f;

        /// <summary>Сторона сетки не больше этого числа тайлов (зум максимально дальний — 96…128 тайлов).</summary>
        const int MaxGrid = 256;

        /// <summary>Темнота ночи: холодный синий.</summary>
        static readonly Color NightColor = new Color(0.02f, 0.05f, 0.18f);

        /// <summary>Темнота сумерек и рассвета: тёплый (полная величина — на границе с днём, ночная — в смеси).</summary>
        static readonly Color DuskColor = new Color(0.32f, 0.13f, 0.06f);

        readonly Camera _camera;
        readonly WorldModel _world;
        readonly MapShape _map;
        readonly FloatingOrigin _origin;
        readonly LightIndex _lights;

        readonly Transform _quad;
        readonly SpriteRenderer _sr;
        readonly Material? _material;
        Texture2D? _grid;
        Sprite? _sprite;
        Color32[] _buf = new Color32[0];

        // Штамп текущей сетки: перестройка — только когда изменился прямоугольник камеры или ревизии чанков под ним.
        int _stamp;

        public DayTintView(Camera camera, WorldModel world, FloatingOrigin origin)
        {
            _camera = camera;
            _world = world;
            _map = world.Shape;
            _origin = origin;
            _lights = world.Lights;

            Shader shader = Shader.Find("Orpheus/DayTint");
            if (shader == null || !shader.isSupported)
            {
                Debug.LogWarning("[daytint] шейдер Orpheus/DayTint не найден/не поддержан — тон суток выключен");
                _material = null;
            }
            else
            {
                _material = new Material(shader) { name = "DayTint" };
            }

            Sprite white = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            GameObject go = new GameObject("DayTint");
            go.transform.SetParent(camera.transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, 5f); // перед камерой (камера на z = −10)
            _sr = go.AddComponent<SpriteRenderer>();
            _sr.sprite = white;
            _sr.sortingOrder = 1000; // выше объектов (WorldView.ObjectOrder = 10)
            _sr.enabled = _material != null;
            if (_material != null)
            {
                _sr.sharedMaterial = _material; // иначе SpriteRenderer возьмёт Sprites/Default и нарисует белый квадрат
            }

            _quad = go.transform;
        }

        /// <summary>
        /// Обновить кадр тона по такту суток: фон и цвет — каждый кадр, сетка тайлов — при движении камеры
        /// или правке чанков под ней.
        /// </summary>
        public void Update(int worldTicks)
        {
            if (_material == null)
            {
                return;
            }

            int bg = DayClock.BackgroundX1024(worldTicks);
            float f = (bg - DayClock.NightBgX1024) / (float)(DayClock.DayBgX1024 - DayClock.NightBgX1024); // 0 ночь → 1 день
            Color tint = Color.LerpUnclamped(NightColor, DuskColor, f);

            // Прямоугольник камеры в тайлах мира (мировая Y вниз → низ экрана = больший Y).
            Vector3 c = _camera.transform.position;
            float hh = _camera.orthographicSize, hw = hh * _camera.aspect;
            TileCoord a = Torus.TileOf(_map, _origin.FromUnity(new Vector3(c.x - hw, c.y + hh, 0f)));
            TileCoord b = Torus.TileOf(_map, _origin.FromUnity(new Vector3(c.x + hw, c.y - hh, 0f)));
            int w = Mod(b.X - a.X, _map.Width) + 3, h = Mod(b.Y - a.Y, _map.Height) + 3; // + поля на случай шва и скруглений
            int minX = a.X - 1, minY = a.Y - 1;
            int step = Math.Max(1, (Math.Max(w, h) + MaxGrid - 1) / MaxGrid);
            int cols = (w + step - 1) / step, rows = (h + step - 1) / step;

            int stamp = Stamp(minX, minY, cols, rows, step);
            if (stamp != _stamp)
            {
                _stamp = stamp;
                Fill(minX, minY, w, h, step, cols, rows);
            }

            _material.SetFloat("_Bg", bg / (float)DayClock.LightScale);
            _material.SetColor("_Tint", tint);
            _material.SetFloat("_Max", MaxDarkness);
            _quad.localScale = new Vector3(2f * hw, 2f * hh, 1f);
        }

        /// <summary>Штамп сетки: геометрия + ревизии чанков под прямоугольником (перестройка — только при изменении).</summary>
        int Stamp(int minX, int minY, int cols, int rows, int step)
        {
            int h = cols * 73856093 ^ rows * 19349663 ^ step * 83492791;
            for (int cy = 0; cy <= (rows * step) / Torus.ChunkSize; cy++)
            {
                for (int cx = 0; cx <= (cols * step) / Torus.ChunkSize; cx++)
                {
                    Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_map, Torus.Wrap(_map, new TileCoord(minX + cx * Torus.ChunkSize, minY + cy * Torus.ChunkSize))));
                    h = unchecked(h * 1000003 + (int)(chunk?.Revision ?? 0));
                }
            }

            return h;
        }

        void Fill(int minX, int minY, int w, int h, int step, int cols, int rows)
        {
            if (_grid == null || _grid.width != cols || _grid.height != rows)
            {
                _grid = new Texture2D(cols, rows, TextureFormat.ARGB32, false)
                {
                    name = "DayTintGrid",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _buf = new Color32[cols * rows];
            }

            int lastX = minX + w - 1, lastY = minY + h - 1;
            for (int r = 0; r < rows; r++)
            {
                // Строка 0 текстуры — низ квада (uv v = 0) = низ экрана = максимальная Y мира.
                int ty = lastY - r * step;
                for (int cIdx = 0; cIdx < cols; cIdx++)
                {
                    int tx = lastX - (cols - 1 - cIdx) * step;
                    TileCoord t = Torus.Wrap(_map, new TileCoord(tx, ty));
                    int k = DayTint.BiomeKX1024ForFloor(_world.GetFloor(t));
                    int s = _lights.SourceLightX1024(t);
                    _buf[r * cols + cIdx] = new Color32((byte)(k >> 2), (byte)(s >> 2), 0, 255);
                }
            }

            _grid.SetPixels32(_buf);
            _grid.Apply(false);

            // SpriteRenderer подставляет в шейдер текстуру своего спрайта — спрайт строим из самой сетки
            // (extrude 0: без обводки краёв, билинейная фильтрация даёт мягкий свет источников).
            if (_sprite != null)
            {
                UnityEngine.Object.Destroy(_sprite);
            }

            _sprite = Sprite.Create(_grid, new Rect(0f, 0f, cols, rows), new Vector2(0.5f, 0.5f), 1f, 0u);
            _sr.sprite = _sprite;
        }

        static int Mod(int v, int m)
        {
            int r = v % m;
            return r < 0 ? r + m : r;
        }
    }
}
