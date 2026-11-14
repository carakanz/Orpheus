#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Сборка мешей объектов «с высотой» (E1-7, план 1.7) по таблице <see cref="ObjectTable"/>.
    /// <list type="bullet">
    /// <item><b>Строка тайлов чанка — один меш</b> (до 32 квадратов из общего листа <see cref="ObjectAtlas"/>): строки
    /// сортируются между собой и с существами по нижней кромке (порядок отрисовки строки —
    /// <see cref="WorldView.SortOrderAt"/>), внутри строки квадраты идут от дальнего к ближнему.</item>
    /// <item><b>Тени чанка — один меш:</b> тот же квадрат картинки, положенный на землю вправо-вниз от опоры
    /// (сплющен и сдвинут по высоте), рисуется в текстуру теней (<see cref="WorldFx"/>).</item>
    /// </list>
    /// Координаты — в узле чанка: тайл (lx, ly) занимает x ∈ [lx; lx + 1], y ∈ [−ly − 1; −ly].
    /// </summary>
    public sealed class ObjectMesh
    {
        const int Size = Torus.ChunkSize;

        /// <summary>Тень точки на высоте h лежит на земле в (h × ShearX; −h × ShearY) от опоры: вправо-вниз.</summary>
        public const float ShearX = 0.55f, ShearY = 0.3f;

        struct Item
        {
            public ObjectLook Look;
            public ObjectTable.Placement Place;
            public float X, Y; // опора в узле чанка
        }

        readonly Item[] _items = new Item[Size];
        readonly List<Vector3> _pos = new List<Vector3>(Size * Size * 4);
        readonly List<Vector2> _uv = new List<Vector2>(Size * Size * 4);
        readonly List<Vector2> _info = new List<Vector2>(Size * 4);
        readonly List<Color32> _color = new List<Color32>(Size * Size * 4);
        readonly List<ushort> _index = new List<ushort>(Size * Size * 6);

        /// <summary>Мешей строк и теней собрано за всё время.</summary>
        public int Builds { get; private set; }

        public static Mesh Create(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            return mesh;
        }

        // Объекты строки ly чанка — в _items от дальнего (выше на экране) к ближнему; возвращает их число.
        int Collect(Chunk chunk, ChunkCoord c, int ly)
        {
            TileCoord corner = Torus.CornerOf(c);
            int n = 0;
            for (int lx = 0; lx < Size; lx++)
            {
                int index = Chunk.IndexOf(lx, ly);
                ObjectLook? look = ObjectTable.Find(chunk.GetObject(index), chunk.GetFloor(index));
                if (look == null)
                {
                    continue;
                }

                Item it;
                it.Look = look;
                it.Place = ObjectTable.Place(look, corner.X + lx, corner.Y + ly);
                it.X = lx + 0.5f + it.Place.Dx;
                it.Y = -ly - 1f + ObjectTable.BaseLift + it.Place.Dy;
                int at = n++;
                while (at > 0 && _items[at - 1].Y < it.Y) // вставка: выше на экране — раньше
                {
                    _items[at] = _items[at - 1];
                    at--;
                }

                _items[at] = it;
            }

            return n;
        }

        void Begin()
        {
            _pos.Clear();
            _uv.Clear();
            _info.Clear();
            _color.Clear();
            _index.Clear();
        }

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, in ObjectAtlas.Entry e, bool flip, Color32 color)
        {
            int v = _pos.Count;
            _pos.Add(a); // левый нижний, правый нижний, левый верхний, правый верхний
            _pos.Add(b);
            _pos.Add(c);
            _pos.Add(d);
            float u0 = flip ? e.Uv.xMax : e.Uv.xMin, u1 = flip ? e.Uv.xMin : e.Uv.xMax;
            _uv.Add(new Vector2(u0, e.Uv.yMin));
            _uv.Add(new Vector2(u1, e.Uv.yMin));
            _uv.Add(new Vector2(u0, e.Uv.yMax));
            _uv.Add(new Vector2(u1, e.Uv.yMax));
            _color.Add(color);
            _color.Add(color);
            _color.Add(color);
            _color.Add(color);
            _index.Add((ushort)v);
            _index.Add((ushort)(v + 2));
            _index.Add((ushort)(v + 1));
            _index.Add((ushort)(v + 1));
            _index.Add((ushort)(v + 2));
            _index.Add((ushort)(v + 3));
        }

        /// <summary>Меш объектов строки ly; возвращает число объектов (0 — меш пуст).</summary>
        public int BuildRow(Chunk? chunk, ChunkCoord c, int ly, Mesh mesh)
        {
            Builds++;
            int n = chunk == null ? 0 : Collect(chunk, c, ly);
            mesh.Clear();
            if (n == 0)
            {
                return 0;
            }

            Begin();
            ObjectAtlas.Entry[] entries = ObjectAtlas.Entries;
            for (int i = 0; i < n; i++)
            {
                ref Item it = ref _items[i];
                ref ObjectAtlas.Entry e = ref entries[it.Place.Sprite];
                float s = it.Place.Scale;
                float x0 = it.X - e.PivotX * s, x1 = x0 + e.Width * s;
                float y0 = it.Y - e.PivotY * s, y1 = y0 + e.Height * s;
                Quad(new Vector3(x0, y0), new Vector3(x1, y0), new Vector3(x0, y1), new Vector3(x1, y1), e, it.Place.Flip, it.Look.Tint);
                var info = new Vector2(it.Y, it.Look.Tall ? 1f : 0f);
                _info.Add(info);
                _info.Add(info);
                _info.Add(info);
                _info.Add(info);
            }

            mesh.SetVertices(_pos);
            mesh.SetUVs(0, _uv);
            mesh.SetUVs(1, _info);
            mesh.SetColors(_color);
            mesh.SetTriangles(_index, 0, calculateBounds: true);
            return n;
        }

        /// <summary>Меш теней всего чанка; возвращает число теней.</summary>
        public int BuildShadows(Chunk? chunk, ChunkCoord c, Mesh mesh)
        {
            Builds++;
            mesh.Clear();
            if (chunk == null)
            {
                return 0;
            }

            Begin();
            ObjectAtlas.Entry[] entries = ObjectAtlas.Entries;
            int shadows = 0;
            for (int ly = 0; ly < Size; ly++)
            {
                int n = Collect(chunk, c, ly);
                for (int i = 0; i < n; i++)
                {
                    ref Item it = ref _items[i];
                    float k = it.Look.Shadow;
                    if (k <= 0f)
                    {
                        continue;
                    }

                    ref ObjectAtlas.Entry e = ref entries[it.Place.Sprite];
                    float s = it.Place.Scale;
                    float x0 = -e.PivotX * s, x1 = x0 + e.Width * s;
                    float h0 = -e.PivotY * s, h1 = h0 + e.Height * s;
                    // Точка картинки (x, h) ложится на землю в (x + h × ShearX; −h × ShearY) от опоры.
                    float sx = ShearX * k, sy = ShearY * k;
                    Quad(new Vector3(it.X + x0 + h0 * sx, it.Y - h0 * sy), new Vector3(it.X + x1 + h0 * sx, it.Y - h0 * sy),
                        new Vector3(it.X + x0 + h1 * sx, it.Y - h1 * sy), new Vector3(it.X + x1 + h1 * sx, it.Y - h1 * sy),
                        e, it.Place.Flip, new Color32(0, 0, 0, 255));
                    shadows++;
                }
            }

            if (shadows == 0)
            {
                return 0;
            }

            mesh.SetVertices(_pos);
            mesh.SetUVs(0, _uv);
            mesh.SetColors(_color);
            mesh.SetTriangles(_index, 0, calculateBounds: true);
            return shadows;
        }
    }
}
