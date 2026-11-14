#nullable enable
using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Тон суток (план 1.8, карточка E1-8; правка E1-7): полноэкранный слой поверх мира, кадр умножается на тон —
    /// день без изменений, сумерки тёплые, ночь тёмно-синяя, рассвет холодно-розовый; свет факела и костра —
    /// тёплые круги в темноте.
    /// <para>
    /// Кадр считается так: для прямоугольника камеры строится сетка тайлов (текстура R8: k биома по полу,
    /// G8: свет источников из <see cref="LightIndex"/>, билинейно — свет кругом). Шейдер «Orpheus/DayTint»
    /// (в <c>Assets/Resources</c> — так шейдер гарантированно в сборке) умножает кадр на цвет неба
    /// (<see cref="ToneAt"/> по такту суток), ночью в лесу — ещё на k; где светит источник, тон уходит в тёплый свет.
    /// Днём множитель ровно 1 — слой невидим (E1-7: раньше k = 1024 не помещался в байт сетки и день выходил
    /// тёмно-бурым; поправка леса днём убрана — лес затеняют тени крон).
    /// </para>
    /// <para>
    /// Сетка перестраивается, только если изменился прямоугольник камеры (движение, зум) или ревизии чанков
    /// под ним; фон и цвет темноты — униформы, меняются каждый кадр. Часами владеет вызывающий:
    /// офлайн — <c>Simulation.WorldTime</c> + ключ <c>--day-tick</c>, сеть — <c>WorldClock.TicksAt</c>.
    /// </para>
    /// </summary>
    public sealed class DayTintView
    {
        /// <summary>Слой последнего созданного мира — для ручной отрисовки кадра другого размера (<see cref="Refresh"/>).</summary>
        public static DayTintView? Current { get; private set; }

        // Цвет неба — множитель кадра. Яркость ночи ~0,35–0,4: персонаж и земля рядом различимы.
        static readonly Color DayTone = new Color(1f, 1f, 1f);
        static readonly Color DuskTone = new Color(1f, 0.66f, 0.42f);      // тёплый оранжеватый
        static readonly Color NightTone = new Color(0.25f, 0.34f, 0.62f);  // тёмно-синий
        static readonly Color DawnTone = new Color(0.80f, 0.62f, 0.74f);   // холодно-розовый

        /// <summary>Тёплый свет факела и костра (множитель кадра в середине круга света).</summary>
        static readonly Color FireTone = new Color(1f, 0.88f, 0.66f);

        // Опорные точки тона: (такт суток, цвет); между ними — плавно. Границы фаз — DayClock.
        static readonly (float At, Color Tone)[] Keys =
        {
            (0f, DayTone),
            (DayClock.DuskStartTick - 900f, DayTone),                                   // за полминуты до сумерек свет теплеет
            (DayClock.DuskStartTick, Color.Lerp(DayTone, DuskTone, 0.45f)),
            ((DayClock.DuskStartTick + DayClock.NightStartTick) * 0.5f, DuskTone),
            (DayClock.NightStartTick, Color.Lerp(DuskTone, NightTone, 0.8f)),
            (DayClock.NightStartTick + 400f, NightTone),
            (DayClock.DawnStartTick, NightTone),
            ((DayClock.DawnStartTick + DayClock.TicksPerDay) * 0.5f, DawnTone),
            (DayClock.TicksPerDay, DayTone),
        };

        /// <summary>Цвет неба (множитель кадра) по такту суток: день — ровно белый.</summary>
        public static Color ToneAt(int worldTicks)
        {
            float t = DayClock.Wrap(worldTicks);
            for (int i = 1; i < Keys.Length; i++)
            {
                if (t <= Keys[i].At)
                {
                    float span = Keys[i].At - Keys[i - 1].At;
                    float f = span > 1e-3f ? (t - Keys[i - 1].At) / span : 1f;
                    return Color.Lerp(Keys[i - 1].Tone, Keys[i].Tone, f * f * (3f - 2f * f));
                }
            }

            return DayTone;
        }

        /// <summary>
        /// Время суток словом или долей (ключ <c>--shot-time</c>, поле <c>time</c> сцены стенда) → такт суток:
        /// «день», «сумерки», «ночь», «рассвет» (day, dusk, night, dawn) — середина фазы; число 0…1 — доля суток.
        /// </summary>
        public static bool TryParseTime(string text, out int ticks)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "день":
                case "day":
                    ticks = DayClock.DuskStartTick / 2;
                    return true;
                case "сумерки":
                case "dusk":
                    ticks = (DayClock.DuskStartTick + DayClock.NightStartTick) / 2;
                    return true;
                case "ночь":
                case "night":
                    ticks = (DayClock.NightStartTick + DayClock.DawnStartTick) / 2;
                    return true;
                case "рассвет":
                case "dawn":
                    ticks = (DayClock.DawnStartTick + DayClock.TicksPerDay) / 2;
                    return true;
            }

            ticks = 0;
            if (!float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f)
                || f < 0f || f > 1f)
            {
                return false;
            }

            ticks = Math.Min(DayClock.TicksPerDay - 1, (int)(f * DayClock.TicksPerDay));
            return true;
        }

        /// <summary>Сторона сетки не больше этого числа тайлов (зум максимально дальний — 96…128 тайлов).</summary>
        const int MaxGrid = 256;

        readonly Camera _camera;
        readonly WorldModel _world;
        readonly MapShape _map;
        readonly FloatingOrigin _origin;
        readonly LightIndex _lights;

        readonly GameObject _quad;
        readonly Material? _material;
        Texture2D? _grid;
        Color32[] _buf = new Color32[0];

        // Штамп текущей сетки: перестройка — только когда изменился прямоугольник камеры или ревизии чанков под ним.
        int _stamp;
        int _ticks;

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

            // Большой квадрат перед камерой (едет с ней, закрывает любой обзор); место точки в сетке шейдер берёт по
            // координате мира (_GridRect) — слой не зависит от размера окна и отношения сторон кадра (E1-7).
            if (Current != null && Current._quad != null)
            {
                UnityEngine.Object.Destroy(Current._quad); // новый мир (повторный вход) — прежний слой убрать, иначе тон умножится дважды
            }

            _quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _quad.name = "DayTint";
            UnityEngine.Object.Destroy(_quad.GetComponent<Collider>());
            _quad.transform.SetParent(camera.transform, false);
            _quad.transform.localPosition = new Vector3(0f, 0f, 5f); // перед камерой (камера на z = −10)
            _quad.transform.localScale = new Vector3(600f, 600f, 1f);
            var mr = _quad.GetComponent<MeshRenderer>();
            mr.sortingOrder = WorldView.TintOrder; // выше земли, объектов «с высотой» и существ
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.enabled = _material != null;
            if (_material != null)
            {
                mr.sharedMaterial = _material;
            }

            Current = this;
        }

        /// <summary>
        /// Перед ручной отрисовкой камеры в текстуру с другим отношением сторон (снимок, замер): слой и сетка — под
        /// этот кадр, а не под окно.
        /// </summary>
        public void Refresh(float aspect) => Update(_ticks, aspect);

        /// <summary>
        /// Обновить кадр тона по такту суток: фон и цвет — каждый кадр, сетка тайлов — при движении камеры
        /// или правке чанков под ней.
        /// </summary>
        public void Update(int worldTicks, float aspect = 0f)
        {
            _ticks = worldTicks;
            if (_material == null)
            {
                return;
            }

            if (aspect <= 0f)
            {
                aspect = _camera.aspect;
            }

            int bg = DayClock.BackgroundX1024(worldTicks);
            float day = (bg - DayClock.NightBgX1024) / (float)(DayClock.DayBgX1024 - DayClock.NightBgX1024); // 0 ночь → 1 день
            Color sky = ToneAt(worldTicks);

            // Прямоугольник камеры в тайлах мира (мировая Y вниз → низ экрана = больший Y).
            Vector3 c = _camera.transform.position;
            float hh = _camera.orthographicSize, hw = hh * aspect;
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

            _material.SetFloat("_Day", day);
            _material.SetVector("_Sky", new Vector4(sky.r, sky.g, sky.b, 1f));       // векторы: цвет — уже множитель, без пересчёта гаммы
            _material.SetVector("_Fire", new Vector4(FireTone.r, FireTone.g, FireTone.b, 1f));

            // Прямоугольник сетки в координатах Unity: точка сетки (c, r) — центр тайла (lastX − (cols − 1 − c) × step,
            // lastY − r × step); строка 0 — низ.
            Vector3 corner = _origin.ToUnity(Torus.CornerOf(a)); // левый верхний угол тайла a
            float x0 = corner.x + (w - 2 - (cols - 1) * step) + 0.5f - 0.5f * step;
            float y0 = corner.y - (h - 2) - 0.5f - 0.5f * step;
            _material.SetVector("_GridRect", new Vector4(x0, y0, 1f / (cols * step), 1f / (rows * step)));
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
                _grid = new Texture2D(cols, rows, TextureFormat.ARGB32, false, true)
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
                    // 1024 → 255 (E1-7: было k >> 2 = 256 → байт 0: открытая земля считалась полной тьмой, день выходил бурым).
                    _buf[r * cols + cIdx] = new Color32(ToByte(k), ToByte(s), 0, 255);
                }
            }

            _grid.SetPixels32(_buf);
            _grid.Apply(false);

            _material!.SetTexture("_MainTex", _grid);
        }

        static byte ToByte(int x1024) => (byte)(x1024 <= 0 ? 0 : x1024 >= DayClock.LightScale ? 255 : x1024 * 255 / DayClock.LightScale);

        static int Mod(int v, int m)
        {
            int r = v % m;
            return r < 0 ? r + m : r;
        }
    }
}
