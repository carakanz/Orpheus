#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// «Живой мир» (E1-11, план 1.11–1.12; слово Автора 14.11): общий ветер, отклик деревьев и травы на существ.
    /// Только вид: ядро не трогает, на правила и сеть не влияет, детерминизма не требует; от частоты кадров не зависит
    /// (всё считается от времени).
    /// <list type="bullet">
    /// <item><b>Ветер.</b> Сила и направление плавно гуляют; порыв — волна, бегущая по миру (фаза от координаты мира —
    /// считается в шейдерах, <c>Shaders/OrphLife.hlsl</c>). Качаются объекты с <see cref="ObjectLook.Sway"/> (кроны,
    /// кусты), их тени и пучки травы.</item>
    /// <item><b>Толчки.</b> Существо (свой, чужой игрок, враг — все идут через <see cref="Touch"/>) проходит ближе
    /// <see cref="PushRadius"/> от ствола — дерево вздрагивает (затухающее колебание от существа, список до
    /// <see cref="MaxPushes"/> толчков уходит в шейдер массивом) и роняет листья (<see cref="LeafFx"/>); повтор на одно
    /// дерево — не чаще <see cref="PushCooldown"/>. Удар по дереву (<see cref="HitTile"/>) — сильнее; срубили
    /// (<see cref="TreeGone"/>) — облачко листьев.</item>
    /// <item><b>След в траве.</b> Текстура 256 × 256, полтайла на точку, привязана к миру (повтор каждые 128 тайлов):
    /// под ногами существ ставится пятно «трава легла от существа», пятна гаснут за <see cref="TrailRecover"/> с.</item>
    /// </list>
    /// Координаты: всё хранится в «непрерывных» координатах = точка Unity + сдвиг <c>_OrphOrigin</c>; сдвиг меняется
    /// при переезде плавающего начала, поэтому ветер, толчки, след и листья не скачут.
    /// </summary>
    public static class LifeFx
    {
        public const int MaxPushes = 24; // = ORPH_PUSH_COUNT в OrphLife.hlsl

        /// <summary>Существо ближе этого к стволу — толчок, тайлов.</summary>
        public const float PushRadius = 1.2f;

        /// <summary>Повторный толчок одного дерева — не чаще, с.</summary>
        public const float PushCooldown = 0.5f;

        /// <summary>Толчок живёт столько секунд (колебание затухает раньше — ~0,9 с).</summary>
        public const float PushLife = 1.3f;

        /// <summary>Сила толчка: смещение верха, тайлов на тайл высоты (ветер — 0,03–0,05).</summary>
        public const float PushStrength = 0.085f, HitStrength = 0.14f;

        /// <summary>Трава ложится в этом радиусе от ног (в половине радиуса — совсем), тайлов, и распрямляется за столько секунд.</summary>
        public const float TrailRadius = 1.0f, TrailRecover = 1.2f;

        const int TrailSize = 256;
        const float TrailTexelsPerTile = 2f;

        static readonly int WindId = Shader.PropertyToID("_OrphWind");
        static readonly int OriginId = Shader.PropertyToID("_OrphOrigin");
        static readonly int PushId = Shader.PropertyToID("_OrphPush");
        static readonly int PushDirId = Shader.PropertyToID("_OrphPushDir");
        static readonly int TrailId = Shader.PropertyToID("_OrphTrail");
        static readonly int GrassId = Shader.PropertyToID("_OrphGrass");

        static WorldModel? _world;
        static FloatingOrigin? _origin;
        static WorldView? _view;
        static MapShape _map;
        static ChunkCoord _anchor;
        static Vector2 _offset;
        static float _time;
        static bool _gpu;

        struct Push
        {
            public Vector2 Base; // непрерывные координаты
            public Vector2 Dir;
            public float Start, Strength;
        }

        static readonly Push[] _pushes = new Push[MaxPushes];
        static readonly Vector4[] _pushData = new Vector4[MaxPushes];
        static readonly Vector4[] _pushDirData = new Vector4[MaxPushes];
        static readonly Dictionary<long, float> _lastPush = new Dictionary<long, float>();

        struct Walker
        {
            public Vector2 Pos;      // непрерывные координаты ног
            public Vector2 Checked;  // где в последний раз искали деревья рядом
            public float Seen;
        }

        static readonly Dictionary<int, Walker> _walkers = new Dictionary<int, Walker>();
        static readonly List<int> _gone = new List<int>();

        static Texture2D? _trailTex;
        static byte[] _trail = new byte[0];
        static float _trailDecay;
        static float _trailLiveUntil;
        static bool _trailDirty;

        /// <summary>Время ветра, с.</summary>
        public static float Time => _time;

        /// <summary>Сдвиг: точка Unity + сдвиг = непрерывная координата.</summary>
        public static Vector2 Offset => _offset;

        /// <summary>Толчков дано за всё время (замер, лог).</summary>
        public static int Pushes { get; private set; }

        /// <summary>Точек следа, где трава сейчас примята (лог ленты).</summary>
        public static int TrailTexels()
        {
            int n = 0;
            for (int i = 2; i < _trail.Length; i += 4)
            {
                n += _trail[i] > 0 ? 1 : 0;
            }

            return n;
        }

        /// <summary>Отладка: ORPH_FX со словом nolife выключает ветер, толчки и след (замер «до/после»).</summary>
        static bool _off;

        /// <summary>Новый мир на экране (<see cref="WorldView"/> создан): привязка к миру и началу координат, сброс состояния.</summary>
        public static void Bind(WorldModel world, FloatingOrigin origin, WorldView view)
        {
            _world = world;
            _origin = origin;
            _view = view;
            _map = world.Shape;
            _anchor = origin.Anchor;
            _offset = new Vector2(_anchor.X * Torus.ChunkSize, -_anchor.Y * Torus.ChunkSize);
            _time = 0f;
            _off = WorldFx.Off("nolife");
            _gpu = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
            _walkers.Clear();
            _lastPush.Clear();
            for (int i = 0; i < MaxPushes; i++)
            {
                _pushes[i] = default;
            }

            if (!_gpu)
            {
                return;
            }

            if (_trailTex == null)
            {
                _trailTex = new Texture2D(TrailSize, TrailSize, TextureFormat.RGBA32, mipChain: false, linear: true)
                {
                    name = "GrassTrail",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear,
                };
                _trail = new byte[TrailSize * TrailSize * 4];
            }

            for (int i = 0; i < _trail.Length; i += 4)
            {
                _trail[i] = 128;
                _trail[i + 1] = 128;
                _trail[i + 2] = 0;
                _trail[i + 3] = 255;
            }

            if (WorldFx.Off("trailtest"))
            {
                // Отладка: вся трава лежит вправо — проверка, что шейдер читает след.
                for (int i = 0; i < _trail.Length; i += 4)
                {
                    _trail[i] = 255;
                    _trail[i + 2] = 255;
                }
            }

            _trailTex.LoadRawTextureData(_trail);
            _trailTex.Apply(false, false);
            _trailDirty = false;
            _trailLiveUntil = 0f;
            Shader.SetGlobalTexture(TrailId, _trailTex);
            LeafFx.Reset();
            Upload(0f);
        }

        // ---------- Существа ----------

        /// <summary>
        /// Существо стоит ногами в этой точке Unity (каждый кадр, из <c>ActorRig.Apply</c>): трава под ним ложится,
        /// деревья и кусты рядом вздрагивают, если оно идёт.
        /// </summary>
        public static void Touch(int id, Vector2 feet)
        {
            if (!_gpu || _off || _origin == null)
            {
                return;
            }

            SyncOrigin();
            Vector2 p = feet + _offset;
            bool known = _walkers.TryGetValue(id, out Walker w);
            Vector2 from = known ? w.Pos : p;
            Vector2 step = p - from;
            if (step.sqrMagnitude > 2.25f)
            {
                step = Vector2.zero; // телепорт — не шаг
                from = p;
                w.Checked = p;
            }

            if (!known)
            {
                w.Checked = p + new Vector2(100f, 0f); // первая проверка — сразу, но без толчка (не шёл)
            }

            w.Pos = p;
            w.Seen = _time;
            Stamp(from, p, step);

            // Деревья рядом ищем, когда существо сдвинулось на четверть тайла: стоящий деревья не трясёт.
            if (known && (p - w.Checked).sqrMagnitude > 0.0625f)
            {
                Vector2 move = p - w.Checked;
                w.Checked = p;
                if (move.sqrMagnitude < 4f)
                {
                    PushNear(feet, move.normalized);
                }
            }

            _walkers[id] = w;
        }

        static void PushNear(Vector2 feet, Vector2 moveDir)
        {
            TileCoord at = Torus.TileOf(_map, _origin!.FromUnity(new Vector3(feet.x, feet.y, 0f)));
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    TileCoord t = Torus.Wrap(_map, new TileCoord(at.X + dx, at.Y + dy));
                    if (!TryPlant(t, out ObjectLook? look, out ObjectTable.Placement place, out Vector2 basePos))
                    {
                        continue;
                    }

                    Vector2 d = basePos - feet;
                    if (d.sqrMagnitude > PushRadius * PushRadius)
                    {
                        continue;
                    }

                    // От существа; крона — плоская картинка, вбок видно лучше, чем вверх-вниз: у вертикали берём бок по ходу.
                    var dir = new Vector2(d.x, d.y * 0.35f);
                    if (Mathf.Abs(d.x) < 0.15f)
                    {
                        dir.x = Mathf.Abs(moveDir.x) > 0.2f ? Mathf.Sign(moveDir.x) * 0.5f : (GroundAtlas.Hash(t.X, t.Y, 151) < 0.5f ? -0.5f : 0.5f);
                    }

                    AddPush(t, look!, place, basePos, dir.normalized, PushStrength, LeafFx.RangeInt(look!.LeavesMin, look.LeavesMax + 1), PushCooldown);
                }
            }
        }

        // Растение, которое качается, на тайле t: вид, место и опора в координатах Unity.
        static bool TryPlant(TileCoord t, out ObjectLook? look, out ObjectTable.Placement place, out Vector2 basePos)
        {
            look = null;
            place = default;
            basePos = default;
            Chunk? chunk = _world!.FindChunk(Torus.ChunkOf(_map, t));
            if (chunk == null)
            {
                return false;
            }

            int index = Chunk.IndexOf(t.X & (Torus.ChunkSize - 1), t.Y & (Torus.ChunkSize - 1));
            return TryPlant(t, chunk.GetObject(index), chunk.GetFloor(index), out look, out place, out basePos);
        }

        static bool TryPlant(TileCoord t, ushort obj, byte floor, out ObjectLook? look, out ObjectTable.Placement place, out Vector2 basePos)
        {
            place = default;
            basePos = default;
            look = ObjectTable.Find(obj, floor);
            if (look == null || look.Sway <= 0f || look.Block)
            {
                return false;
            }

            place = ObjectTable.Place(look, t.X, t.Y);
            Vector3 corner = _origin!.ToUnity(Torus.CornerOf(t)); // левый верхний угол тайла
            basePos = new Vector2(corner.x + 0.5f + place.Dx, corner.y - 1f + ObjectTable.BaseLift + place.Dy);
            return true;
        }

        static void AddPush(TileCoord t, ObjectLook look, in ObjectTable.Placement place, Vector2 basePos, Vector2 dir, float strength, int leaves, float cooldown)
        {
            long key = ((long)t.X << 32) | (uint)t.Y;
            if (_lastPush.TryGetValue(key, out float last) && _time - last < cooldown)
            {
                return;
            }

            if (_lastPush.Count > 512)
            {
                _lastPush.Clear();
            }

            _lastPush[key] = _time;
            Vector2 cont = basePos + _offset;
            int slot = 0;
            float oldest = float.MaxValue;
            for (int i = 0; i < MaxPushes; i++)
            {
                // То же дерево — тот же слот (новый толчок сменяет старый); иначе свободный или самый старый.
                if (_pushes[i].Strength > 0f && (_pushes[i].Base - cont).sqrMagnitude < 0.0064f)
                {
                    slot = i;
                    break;
                }

                float start = _pushes[i].Strength > 0f ? _pushes[i].Start : float.MinValue;
                if (start < oldest)
                {
                    oldest = start;
                    slot = i;
                }
            }

            _pushes[slot] = new Push { Base = cont, Dir = dir, Start = _time, Strength = strength };
            Pushes++;
            if (leaves > 0)
            {
                LeafFx.Shed(look, place, cont, dir, leaves, burst: false);
            }
        }

        /// <summary>Удар по тайлу (шаг копания/рубки; <c>NetAnimation</c>): дерево или куст на нём встряхивается сильнее и роняет больше листьев.</summary>
        public static void HitTile(TileCoord tile, Vector2 dirFromHitter)
        {
            if (!_gpu || _off || _origin == null || _world == null)
            {
                return;
            }

            SyncOrigin();
            TileCoord t = Torus.Wrap(_map, tile);
            if (!TryPlant(t, out ObjectLook? look, out ObjectTable.Placement place, out Vector2 basePos))
            {
                return;
            }

            var dir = new Vector2(Mathf.Abs(dirFromHitter.x) > 0.15f ? dirFromHitter.x : 0.5f, dirFromHitter.y * 0.35f);
            AddPush(t, look!, place, basePos, dir.normalized, HitStrength, look!.LeavesMax + LeafFx.RangeInt(2, 6), 0.12f);
        }

        /// <summary>
        /// На тайле мира (tx, ty) пропал объект <paramref name="was"/> (срубили, выкопали; из <c>WorldView.Fill</c>):
        /// если это было дерево или куст — облачко листьев на его месте.
        /// </summary>
        public static void TreeGone(int tx, int ty, ushort was, byte floor)
        {
            if (!_gpu || _off || _origin == null)
            {
                return;
            }

            SyncOrigin();
            var t = new TileCoord(tx, ty);
            if (TryPlant(t, was, floor, out ObjectLook? look, out ObjectTable.Placement place, out Vector2 basePos))
            {
                LeafFx.Shed(look!, place, basePos + _offset, Vector2.zero, look!.LeavesMax * 2 + 4, burst: true);
            }
        }

        // ---------- След в траве ----------

        // Пятно под ногами: точки следа в радиусе — «трава легла от существа» (на бегу — ещё и по ходу).
        static void Stamp(Vector2 from, Vector2 to, Vector2 step)
        {
            float len = step.magnitude;
            int n = Mathf.Clamp(Mathf.CeilToInt(len / 0.25f), 1, 6); // быстрый шаг — без дыр в следе
            Vector2 along = len > 1e-4f ? step / len : Vector2.zero;
            for (int k = 1; k <= n; k++)
            {
                Vector2 p = Vector2.Lerp(from, to, k / (float)n);
                int cx = Mathf.FloorToInt(p.x * TrailTexelsPerTile), cy = Mathf.FloorToInt(p.y * TrailTexelsPerTile);
                for (int y = cy - 3; y <= cy + 3; y++)
                {
                    for (int x = cx - 3; x <= cx + 3; x++)
                    {
                        var c = new Vector2((x + 0.5f) / TrailTexelsPerTile, (y + 0.5f) / TrailTexelsPerTile);
                        Vector2 d = c - p;
                        float dist = d.magnitude;
                        float a = 1f - Mathf.Clamp01((dist - TrailRadius * 0.5f) / (TrailRadius * 0.5f)); // 1 под ногами, 0 на краю пятна
                        if (a <= 0.02f)
                        {
                            continue;
                        }

                        int at = (((y % TrailSize) + TrailSize) % TrailSize * TrailSize + ((x % TrailSize) + TrailSize) % TrailSize) * 4;
                        byte amount = (byte)(a * 255f);
                        if (amount < _trail[at + 2])
                        {
                            continue; // здесь трава уже примята сильнее
                        }

                        Vector2 dir = (dist > 0.05f ? d / dist : Vector2.down) * 0.75f + along * 0.45f;
                        dir = Vector2.ClampMagnitude(dir, 1f);
                        _trail[at] = (byte)(128f + dir.x * 127f);
                        _trail[at + 1] = (byte)(128f + dir.y * 127f);
                        _trail[at + 2] = amount;
                        _trailDirty = true;
                    }
                }
            }

            _trailLiveUntil = _time + TrailRecover + 0.2f;
        }

        static void DecayTrail(float dt)
        {
            if (_time > _trailLiveUntil && !_trailDirty)
            {
                return; // следов нет — текстура не трогается
            }

            _trailDecay += dt * 255f / TrailRecover;
            int step = (int)_trailDecay;
            if (step > 0)
            {
                _trailDecay -= step;
                byte[] t = _trail;
                for (int i = 2; i < t.Length; i += 4)
                {
                    int b = t[i];
                    if (b == 0)
                    {
                        continue;
                    }

                    b -= step;
                    if (b <= 0)
                    {
                        b = 0;
                        t[i - 2] = 128;
                        t[i - 1] = 128;
                    }

                    t[i] = (byte)b;
                }

                _trailDirty = true;
            }

            if (_trailDirty && _trailTex != null)
            {
                _trailTex.LoadRawTextureData(_trail);
                _trailTex.Apply(false, false);
                _trailDirty = false;
            }
        }

        // ---------- Кадр ----------

        // Плавающее начало переехало — сдвиг меняется на тот же вектор: непрерывные координаты остаются на месте.
        static void SyncOrigin()
        {
            ChunkCoord a = _origin!.Anchor;
            if (a.X == _anchor.X && a.Y == _anchor.Y)
            {
                return;
            }

            Int2 d = Torus.Delta(_map, _anchor, a);
            _offset += new Vector2(d.X * Torus.ChunkSize, -d.Y * Torus.ChunkSize);
            _anchor = a;
        }

        /// <summary>Раз в кадр после расстановки мира и существ (<see cref="WorldFxDriver"/>): время, ветер, толчки, след, листья, трава.</summary>
        public static void LateFrame(Camera camera)
        {
            if (!_gpu || _origin == null)
            {
                return;
            }

            float dt = Mathf.Min(UnityEngine.Time.deltaTime, 0.1f);
            _time += dt;
            SyncOrigin();
            DecayTrail(dt);

            // Существа, которых давно не видно (ушли из снимка, выключены), забываются.
            if (_walkers.Count > 0 && UnityEngine.Time.frameCount % 120 == 0)
            {
                _gone.Clear();
                foreach (KeyValuePair<int, Walker> kv in _walkers)
                {
                    if (_time - kv.Value.Seen > 1f)
                    {
                        _gone.Add(kv.Key);
                    }
                }

                foreach (int id in _gone)
                {
                    _walkers.Remove(id);
                }
            }

            LeafFx.Frame(dt, _offset, WindDir(_time) * WindStrength(_time));
            Upload(camera.orthographicSize * 2f * 16f / 9f);
            _view?.FlushGrass(GrassLayer.Bands * 2);
        }

        /// <summary>Перед ручной отрисовкой кадра (снимок, замер): правки травы этого кадра — в меши, параметры — в шейдеры.</summary>
        public static void BeforeManualRender(Camera camera)
        {
            if (!_gpu || _origin == null)
            {
                return;
            }

            SyncOrigin();
            Upload(camera.orthographicSize * 2f * 16f / 9f);
            _view?.FlushGrass(GrassLayer.Bands * 2);
        }

        // Направление гуляет ±35° вокруг «на юго-восток», сила — 0,35…1,2: то тихо, то порыв.
        static Vector2 WindDir(float t)
        {
            float a = -0.45f + 0.4f * Mathf.Sin(t * 0.045f) + 0.22f * Mathf.Sin(t * 0.13f + 1.3f);
            return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }

        static float WindStrength(float t) => _off ? 0f : 0.78f + 0.27f * Mathf.Sin(t * 0.08f + 0.6f) + 0.15f * Mathf.Sin(t * 0.21f);

        static void Upload(float viewTiles)
        {
            Vector2 dir = WindDir(_time);
            Shader.SetGlobalVector(WindId, new Vector4(dir.x, dir.y, WindStrength(_time), _time));
            Shader.SetGlobalVector(OriginId, new Vector4(_offset.x, _offset.y, 0f, 0f));
            for (int i = 0; i < MaxPushes; i++)
            {
                ref Push p = ref _pushes[i];
                float age = _time - p.Start;
                if (p.Strength > 0f && age > PushLife)
                {
                    p.Strength = 0f;
                }

                _pushData[i] = p.Strength > 0f ? new Vector4(p.Base.x - _offset.x, p.Base.y - _offset.y, age, p.Strength) : Vector4.zero;
                _pushDirData[i] = new Vector4(p.Dir.x, p.Dir.y, 0f, 0f);
            }

            Shader.SetGlobalVectorArray(PushId, _pushData);
            Shader.SetGlobalVectorArray(PushDirId, _pushDirData);
            // Дальний обзор (шире 72 тайлов): часть пучков плавно скрывается — до 45 % на обзоре 96 (мелкие, сливаются в рябь).
            float hide = Mathf.Clamp01((viewTiles - 72f) / 24f) * 0.45f;
            Shader.SetGlobalVector(GrassId, new Vector4(hide, 0f, 0f, 0f));
        }
    }
}
