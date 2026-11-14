#nullable enable
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Листья с крон (E1-11, план 1.11): общий пул частиц и один меш на все листья — без объекта на частицу.
    /// Лист цвета кроны (средний цвет картинки дерева — <see cref="ObjectAtlas.Entry.Leaf"/>: осенний дуб роняет рыжие)
    /// отрывается от кроны, падает 1–1,5 с, покачиваясь и уходя по ветру, ложится на землю у ствола и гаснет.
    /// Рисуется материалом травы (форма «лист» листа <see cref="GrassLayer"/>) поверх объектов и существ.
    /// Места частиц — в непрерывных координатах <see cref="LifeFx"/>: переезд начала координат их не сдвигает.
    /// </summary>
    public static class LeafFx
    {
        public const int MaxLeaves = 320;

        /// <summary>Лист лежит на земле и гаснет столько секунд.</summary>
        const float RestTime = 0.55f;

        struct Leaf
        {
            public float X0, Y0, Y1;    // откуда и до какой высоты (земля) падает
            public float Drift;         // снос по X за время падения
            public float Fall;          // время падения, с
            public float Age;
            public float Swing, Freq, Phase;
            public float Size;
            public Color32 Color;
        }

        static readonly Leaf[] _leaves = new Leaf[MaxLeaves];
        static int _count;
        static Mesh? _mesh;
        static MeshRenderer? _renderer;
        static readonly Vector3[] _pos = new Vector3[MaxLeaves * 4];
        static readonly Vector2[] _uv = new Vector2[MaxLeaves * 4];
        static readonly Color32[] _color = new Color32[MaxLeaves * 4];
        static readonly ushort[] _index = new ushort[MaxLeaves * 6];
        static uint _rng = 0x9E3779B9u;

        /// <summary>Листьев в воздухе и на земле сейчас; выпущено за всё время.</summary>
        public static int Alive => _count;
        public static int Shedded { get; private set; }

        /// <summary>Случайное 0…1 — свой генератор с постоянным зерном: повтор сцены стенда даёт тот же кадр.</summary>
        public static float Next()
        {
            _rng ^= _rng << 13;
            _rng ^= _rng >> 17;
            _rng ^= _rng << 5;
            return (_rng & 0xFFFFFF) / (float)0x1000000;
        }

        static float Range(float a, float b) => a + (b - a) * Next();

        /// <summary>Целое от a до b − 1.</summary>
        public static int RangeInt(int a, int b) => a + Mathf.Min((int)(Next() * (b - a)), Mathf.Max(b - a - 1, 0));

        public static void Reset()
        {
            _count = 0;
            _rng = 0x9E3779B9u;
            if (_renderer != null)
            {
                _renderer.enabled = false;
            }
        }

        /// <summary>
        /// Листья с кроны растения: опора <paramref name="basePos"/> (непрерывные координаты), толчок в сторону
        /// <paramref name="dir"/>. <paramref name="burst"/> — дерево срублено: листья по всей кроне разом.
        /// </summary>
        public static void Shed(ObjectLook look, in ObjectTable.Placement place, Vector2 basePos, Vector2 dir, int count, bool burst)
        {
            ObjectAtlas.Entry[] entries = ObjectAtlas.Entries;
            if ((uint)place.Sprite >= (uint)entries.Length)
            {
                return;
            }

            ref ObjectAtlas.Entry e = ref entries[place.Sprite];
            float w = e.Width * place.Scale, h = e.Height * place.Scale;
            Color32 c = e.Leaf;
            Color32 tint = look.Tint;
            for (int i = 0; i < count && _count < MaxLeaves; i++)
            {
                float tone = Range(0.66f, 0.95f); // чуть темнее кроны: лист виден и на её фоне
                ref Leaf l = ref _leaves[_count++];
                // Крона — верхние ~60 % картинки (у куста — вся), по ширине — середина.
                float up = burst ? Range(0.25f, 0.95f) : Range(0.34f, 0.8f);
                l.X0 = basePos.x + Range(-0.42f, 0.42f) * w + dir.x * 0.2f;
                l.Y0 = basePos.y + h * up;
                l.Y1 = basePos.y + Range(-0.5f, 0.25f);
                l.Drift = dir.x * Range(0.3f, 0.9f) + Range(-0.35f, 0.35f);
                l.Fall = Range(1f, 1.5f) * (burst ? Range(0.7f, 1f) : 1f);
                l.Age = burst ? -Range(0f, 0.25f) : -Range(0f, 0.18f); // отрываются не все разом
                l.Swing = Range(0.1f, 0.22f);
                l.Freq = Range(5f, 8f);
                l.Phase = Range(0f, 6.28f);
                l.Size = Range(0.24f, 0.36f) * Mathf.Clamp(h / 2.6f, 0.8f, 1.15f);
                // Насыщеннее среднего цвета кроны (бледная олива иначе теряется на траве).
                float cr = c.r * tint.r / 255f, cg = c.g * tint.g / 255f, cb = c.b * tint.b / 255f;
                float grey = (cr + cg + cb) / 3f;
                l.Color = new Color32(
                    (byte)Mathf.Clamp((grey + (cr - grey) * 1.7f) * tone, 0f, 255f), (byte)Mathf.Clamp((grey + (cg - grey) * 1.7f) * tone, 0f, 255f),
                    (byte)Mathf.Clamp((grey + (cb - grey) * 1.7f) * tone, 0f, 255f), 255);
                Shedded++;
            }
        }

        static void Ensure()
        {
            if (_mesh != null)
            {
                return;
            }

            _mesh = new Mesh { name = "Leaves" };
            _mesh.MarkDynamic();
            for (int q = 0; q < MaxLeaves; q++)
            {
                int v = q * 4, i = q * 6;
                _index[i] = (ushort)v;
                _index[i + 1] = (ushort)(v + 2);
                _index[i + 2] = (ushort)(v + 1);
                _index[i + 3] = (ushort)(v + 1);
                _index[i + 4] = (ushort)(v + 2);
                _index[i + 5] = (ushort)(v + 3);
                float u0 = GrassLayer.ShapeLeaf / (float)GrassLayer.ShapeCount, u1 = (GrassLayer.ShapeLeaf + 1f) / GrassLayer.ShapeCount;
                _uv[v] = new Vector2(u0, 0f);
                _uv[v + 1] = new Vector2(u1, 0f);
                _uv[v + 2] = new Vector2(u0, 1f);
                _uv[v + 3] = new Vector2(u1, 1f);
            }

            var go = new GameObject("Leaves");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = GrassLayer.Material;
            _renderer.sortingOrder = WorldView.TallOrder + 5700; // над объектами и существами, под тоном суток
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _renderer.enabled = false;
        }

        /// <summary>Кадр: шаг частиц и меш. <paramref name="offset"/> — сдвиг непрерывных координат, <paramref name="wind"/> — ветер (направление × сила).</summary>
        public static void Frame(float dt, Vector2 offset, Vector2 wind)
        {
            if (_count == 0)
            {
                if (_renderer != null && _renderer.enabled)
                {
                    _renderer.enabled = false;
                }

                return;
            }

            Ensure();
            int n = 0;
            for (int i = 0; i < _count; i++)
            {
                Leaf l = _leaves[i];
                l.Age += dt;
                if (l.Age > l.Fall + RestTime)
                {
                    continue; // погас — место занимает следующий
                }

                _leaves[n] = l;
                if (l.Age < 0f)
                {
                    // Ещё на ветке: квадрат нулевого размера.
                    int z = n * 4;
                    _pos[z] = _pos[z + 1] = _pos[z + 2] = _pos[z + 3] = Vector3.zero;
                    _color[z] = _color[z + 1] = _color[z + 2] = _color[z + 3] = default;
                    n++;
                    continue;
                }

                float q = Mathf.Min(l.Age / l.Fall, 1f);
                float air = 1f - q * q; // у земли покачивание стихает
                float sway = Mathf.Sin(l.Phase + l.Age * l.Freq);
                float x = l.X0 + (l.Drift + wind.x * 0.5f) * q + l.Swing * sway * air - offset.x;
                float y = Mathf.Lerp(l.Y0, l.Y1, q * (0.55f + 0.45f * q)) + l.Swing * 0.3f * Mathf.Cos(l.Phase + l.Age * l.Freq) * air - offset.y;
                float angle = l.Phase + sway * 0.9f * air + (1f - air) * 1.2f;
                float alpha = Mathf.Clamp01(l.Age / 0.08f) * (q < 1f ? 1f : Mathf.Clamp01(1f - (l.Age - l.Fall) / RestTime));
                float s = l.Size * 0.5f;
                // Лист в полёте виден то плашмя, то ребром.
                float flat = q < 1f ? 0.45f + 0.55f * Mathf.Abs(Mathf.Cos(l.Phase * 1.7f + l.Age * l.Freq * 0.6f)) : 0.6f;
                Vector2 ax = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (s * flat), ay = new Vector2(-Mathf.Sin(angle), Mathf.Cos(angle)) * s;
                int v = n * 4;
                _pos[v] = new Vector3(x - ax.x - ay.x, y - ax.y - ay.y);
                _pos[v + 1] = new Vector3(x + ax.x - ay.x, y + ax.y - ay.y);
                _pos[v + 2] = new Vector3(x - ax.x + ay.x, y - ax.y + ay.y);
                _pos[v + 3] = new Vector3(x + ax.x + ay.x, y + ax.y + ay.y);
                Color32 c = l.Color;
                c.a = (byte)(alpha * 255f);
                _color[v] = _color[v + 1] = _color[v + 2] = _color[v + 3] = c;
                n++;
            }

            _count = n;
            Mesh mesh = _mesh!;
            mesh.Clear(keepVertexLayout: true);
            if (n > 0)
            {
                mesh.SetVertices(_pos, 0, n * 4);
                mesh.SetUVs(0, _uv, 0, n * 4);
                mesh.SetColors(_color, 0, n * 4);
                mesh.SetIndices(_index, 0, n * 6, MeshTopology.Triangles, 0, calculateBounds: true);
            }

            _renderer!.enabled = n > 0 && GrassLayer.Material != null;
        }
    }
}
