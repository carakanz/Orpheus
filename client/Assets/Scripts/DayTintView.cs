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
    /// G8: свет источников, билинейно — свет кругом; C-017: по ячейкам 0,5 тайла, свет — с учётом стен
    /// (<see cref="LightSight"/>), B8 — закрытая комната (<see cref="RoomIndex"/>, для теней)). Шейдер «Orpheus/DayTint»
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

        /// <summary>Сторона сетки не больше этого числа точек (ячеек 0,5; зум максимально дальний — 96…128 тайлов).</summary>
        const int MaxGrid = 320;

        readonly Camera _camera;
        readonly WorldModel _world;
        readonly MapShape _map;
        readonly FloatingOrigin _origin;

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

            Shader shader = Shader.Find("Orpheus/DayTint");
            if (shader == null || !shader.isSupported || WorldFx.Off("notint"))
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
            SunTable.Apply(worldTicks); // C-017: солнце теней — каждый кадр, и без слоя тона
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

            // Прямоугольник камеры в тайлах мира (мировая Y вниз → низ экрана = больший Y), расширенный до сетки в
            // Align тайлов: пока камера не перешла её линию, сетка та же (E1-7). C-017: сетка — по ячейкам 0,5 тайла
            // (свет с учётом тонких стен и комнаты — без размытия на полтайла).
            Vector3 c = _camera.transform.position;
            float hh = _camera.orthographicSize, hw = hh * aspect;
            TileCoord a = Torus.TileOf(_map, _origin.FromUnity(new Vector3(c.x - hw, c.y + hh, 0f)));
            TileCoord b = Torus.TileOf(_map, _origin.FromUnity(new Vector3(c.x + hw, c.y - hh, 0f)));
            int minX = FloorTo(a.X - 1, Align), minY = FloorTo(a.Y - 1, Align);
            int w = CeilTo(Mod(b.X - a.X, _map.Width) + (a.X - minX) + 2, Align);
            int h = CeilTo(Mod(b.Y - a.Y, _map.Height) + (a.Y - minY) + 2, Align);
            int step = Math.Max(1, (2 * Math.Max(w, h) + MaxGrid - 1) / MaxGrid); // в ячейках
            int cols = (2 * w + step - 1) / step, rows = (2 * h + step - 1) / step;

            int stamp = Stamp(minX, minY, w, h, step);
            if (stamp != _stamp)
            {
                _stamp = stamp;
                Fill(minX, minY, step, cols, rows);
            }

            _material.SetFloat("_Day", day);
            _material.SetVector("_Sky", new Vector4(sky.r, sky.g, sky.b, 1f));       // векторы: цвет — уже множитель, без пересчёта гаммы
            _material.SetVector("_Fire", new Vector4(FireTone.r, FireTone.g, FireTone.b, 1f));

            // Прямоугольник сетки в координатах Unity: точка (col, row) — середина ячейки (2 × minX + col × step,
            // 2 × minY + (rows − 1 − row) × step) по сетке ячеек мира; строка 0 — низ.
            Vector3 corner = _origin.ToUnity(Torus.CornerOf(a)); // левый верхний угол тайла a
            float size = 0.5f * step;                            // точка сетки, тайлов
            float x0 = corner.x - a.X + minX + 0.25f - 0.5f * size;
            float y0 = corner.y + a.Y - (minY + (rows - 1) * step * 0.5f) - 0.25f - 0.5f * size;
            var rect = new Vector4(x0, y0, 1f / (cols * size), 1f / (rows * size));
            _material.SetVector("_GridRect", rect);
            Shader.SetGlobalVector(LightRectId, rect);
            Shader.SetGlobalVector(LightSizeId, new Vector4(cols, rows, 1f / cols, 1f / rows));
        }

        static readonly int LightGridId = Shader.PropertyToID("_OrphLightGrid");
        static readonly int LightRectId = Shader.PropertyToID("_OrphLightRect");
        static readonly int LightSizeId = Shader.PropertyToID("_OrphLightSize");

        /// <summary>Сетка привязана к линиям через столько тайлов.</summary>
        const int Align = 8;

        static int FloorTo(int v, int m) => v - Mod(v, m);

        static int CeilTo(int v, int m) => (v + m - 1) / m * m;

        /// <summary>Штамп сетки: место, размер + ревизии чанков под прямоугольником (перестройка — только при изменении).</summary>
        int Stamp(int minX, int minY, int w, int h, int step)
        {
            int s = (minX * 73856093) ^ (minY * 19349663) ^ (w * 83492791) ^ (h * 2654435) ^ step;
            // C-017: и чанки на один за краем — свет и комнаты зависят от окна 3 × 3.
            int cx0 = FloorTo(minX, Torus.ChunkSize) - Torus.ChunkSize, cy0 = FloorTo(minY, Torus.ChunkSize) - Torus.ChunkSize;
            for (int y = cy0; y < minY + h + Torus.ChunkSize; y += Torus.ChunkSize)
            {
                for (int x = cx0; x < minX + w + Torus.ChunkSize; x += Torus.ChunkSize)
                {
                    Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_map, Torus.Wrap(_map, new TileCoord(x, y))));
                    s = unchecked(s * 1000003 + (chunk == null ? -7 : (int)chunk.Revision));
                }
            }

            return s;
        }

        // Есть ли в чанке источник света (факел, костёр) — по ревизии чанка, чтобы не просматривать его каждый раз.
        readonly System.Collections.Generic.Dictionary<ChunkCoord, (uint Revision, bool Has)> _hasLight =
            new System.Collections.Generic.Dictionary<ChunkCoord, (uint, bool)>();

        bool HasLight(ChunkCoord cc)
        {
            Chunk? chunk = _world.FindChunk(Torus.Wrap(_map, cc));
            if (chunk == null)
            {
                return false;
            }

            if (_hasLight.TryGetValue(cc, out (uint Revision, bool Has) e) && e.Revision == chunk.Revision)
            {
                return e.Has;
            }

            bool has = false;
            for (int i = 0; i < Torus.ChunkSize * Torus.ChunkSize && !has; i++)
            {
                ushort id = ObjectId.IdOf(chunk.GetObject(i));
                has = id == ObjectId.Torch || id == ObjectId.Campfire;
            }

            _hasLight[cc] = (chunk.Revision, has);
            return has;
        }

        // Свет источника достаёт не дальше соседнего чанка (так же считает сетка света ядра: окно 3 × 3).
        bool LightNear(ChunkCoord cc)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (HasLight(new ChunkCoord(cc.X + dx, cc.Y + dy)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Сетка чанка на время одной заливки: k полов, свет с учётом стен и комнаты (C-017).
        ChunkCoord _fillCc;
        bool _fillHave;
        Chunk? _fillChunk;
        ushort[]? _fillLight;
        byte[]? _fillRooms;

        void FillChunk(ChunkCoord cc)
        {
            _fillHave = true;
            _fillCc = cc;
            _fillChunk = _world.FindChunk(cc);
            _fillLight = LightNear(cc) && _world.Sight.AnySource(cc) ? _world.Sight.GridOf(cc) : null;
            _fillRooms = _fillChunk != null ? _world.Rooms.LabelsOf(cc) : null;
        }

        void Fill(int minX, int minY, int step, int cols, int rows)
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

            // Чанк, свет и комнаты — один раз на отрезок строки внутри чанка, а не на каждую ячейку.
            // R — k биома по полу, G — свет источников с учётом стен (LightSight; правила врагов — LightIndex без стен),
            // B — 255 в закрытой комнате (RoomIndex: солнечной тени нет).
            _fillHave = false;
            const int n = CellWindow.CellsPerChunk;
            int cx0 = 2 * minX, cy0 = 2 * minY;
            for (int r = 0; r < rows; r++)
            {
                // Строка 0 текстуры — низ (v = 0) = низ экрана = наибольшая Y мира.
                int cy = cy0 + (rows - 1 - r) * step;
                for (int cIdx = 0; cIdx < cols; cIdx++)
                {
                    int cx = cx0 + cIdx * step;
                    TileCoord t = Torus.Wrap(_map, new TileCoord(cx >> 1, cy >> 1));
                    ChunkCoord cc = Torus.ChunkOf(_map, t);
                    if (!_fillHave || cc != _fillCc)
                    {
                        FillChunk(cc);
                    }

                    int lx = t.X & (Torus.ChunkSize - 1), ly = t.Y & (Torus.ChunkSize - 1);
                    byte floor = _fillChunk == null ? FloorId.Grass : _fillChunk.GetFloor(Chunk.IndexOf(lx, ly));
                    int k = DayTint.BiomeKX1024ForFloor(floor);
                    int cell = ((ly << 1) | (cy & 1)) * n + ((lx << 1) | (cx & 1));
                    int s = _fillLight != null ? _fillLight[cell] : 0;
                    byte room = _fillRooms != null && _fillRooms[cell] != RoomIndex.Outside ? (byte)255 : (byte)0;
                    // 1024 → 255 (E1-7: было k >> 2 = 256 → байт 0: открытая земля считалась полной тьмой, день выходил бурым).
                    _buf[r * cols + cIdx] = new Color32(ToByte(k), ToByte(s), room, 255);
                }
            }

            _grid.SetPixels32(_buf);
            _grid.Apply(false);
            _material!.SetTexture("_MainTex", _grid);
            Shader.SetGlobalTexture(LightGridId, _grid);
        }

        static byte ToByte(int x1024) => (byte)(x1024 <= 0 ? 0 : x1024 >= DayClock.LightScale ? 255 : x1024 * 255 / DayClock.LightScale);

        static int Mod(int v, int m)
        {
            int r = v % m;
            return r < 0 ? r + m : r;
        }
    }
}
