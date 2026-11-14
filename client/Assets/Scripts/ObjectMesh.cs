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
            public int Lx;     // столбец тайла в чанке
            public int Near;   // блок: соседи-блоки (биты North…SouthWest)
        }

        const int North = 1, East = 2, South = 4, West = 8, SouthEast = 16, SouthWest = 32;

        readonly WorldModel _world;
        readonly MapShape _map;

        public ObjectMesh(WorldModel world)
        {
            _world = world;
            _map = world.Shape;
        }

        /// <summary>В последней собранной строке были блоки (их вид зависит от соседних тайлов и чанков).</summary>
        public bool RowHadBlocks { get; private set; }

        // Тайл мира (в том числе в соседнем чанке) — блок? Чанка нет (сеть: ещё не пришёл) — нет.
        bool BlockAt(int tx, int ty)
        {
            TileCoord t = Torus.Wrap(_map, new TileCoord(tx, ty));
            Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_map, t));
            return chunk != null && ObjectTable.IsBlock(chunk.GetObject(Chunk.IndexOf(t.X & (Size - 1), t.Y & (Size - 1))));
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
            RowHadBlocks = false;
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
                it.Lx = lx;
                it.Near = 0;
                if (look.Block)
                {
                    int tx = corner.X + lx, ty = corner.Y + ly;
                    it.Place = default;
                    it.X = lx + 0.5f;
                    it.Y = -ly - 1f; // опора блока — нижний край тайла
                    it.Near = (BlockAt(tx, ty - 1) ? North : 0) | (BlockAt(tx + 1, ty) ? East : 0)
                        | (BlockAt(tx, ty + 1) ? South : 0) | (BlockAt(tx - 1, ty) ? West : 0)
                        | (BlockAt(tx + 1, ty + 1) ? SouthEast : 0) | (BlockAt(tx - 1, ty + 1) ? SouthWest : 0);
                    RowHadBlocks = true;
                }
                else
                {
                    it.Place = ObjectTable.Place(look, corner.X + lx, corner.Y + ly);
                    it.X = lx + 0.5f + it.Place.Dx;
                    it.Y = -ly - 1f + ObjectTable.BaseLift + it.Place.Dy;
                }

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
            float u0 = flip ? e.Uv.xMax : e.Uv.xMin, u1 = flip ? e.Uv.xMin : e.Uv.xMax;
            Quad(a, b, c, d, u0, e.Uv.yMin, u1, e.Uv.yMax, color);
        }

        // Прямоугольник (x0, y0)…(x1, y1) сплошным цветом (белый квадрат листа × цвет вершин).
        void Solid(float x0, float y0, float x1, float y1, in ObjectAtlas.Entry white, Color32 color)
        {
            Vector2 m = white.Uv.center;
            Quad(new Vector3(x0, y0), new Vector3(x1, y0), new Vector3(x0, y1), new Vector3(x1, y1), m.x, m.y, m.x, m.y, color);
        }

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float u0, float v0, float u1, float v1, Color32 color)
        {
            int v = _pos.Count;
            _pos.Add(a); // левый нижний, правый нижний, левый верхний, правый верхний
            _pos.Add(b);
            _pos.Add(c);
            _pos.Add(d);
            _uv.Add(new Vector2(u0, v0));
            _uv.Add(new Vector2(u1, v0));
            _uv.Add(new Vector2(u0, v1));
            _uv.Add(new Vector2(u1, v1));
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
            TileCoord corner = Torus.CornerOf(c);
            for (int i = 0; i < n; i++)
            {
                ref Item it = ref _items[i];
                if (it.Look.Block)
                {
                    Block(it, corner.X + it.Lx, corner.Y + ly, entries);
                    continue;
                }

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

            while (_info.Count < _pos.Count)
            {
                _info.Add(Vector2.zero); // квадраты блоков: не высокие
            }

            mesh.SetVertices(_pos);
            mesh.SetUVs(0, _uv);
            mesh.SetUVs(1, _info);
            mesh.SetColors(_color);
            mesh.SetTriangles(_index, 0, calculateBounds: true);
            return n;
        }

        // Кусок картинки на тайл мира (tx, ty): картинка покрывает nx × ny тайлов; отступ внутрь на 1,5 точки — без затёков.
        static void TileUv(in ObjectAtlas.Entry e, int tx, int ty, bool rows, out float u0, out float v0, out float u1, out float v1)
        {
            int nx = Mathf.Max(1, Mathf.RoundToInt(e.Width));
            int ny = rows ? Mathf.Max(1, Mathf.RoundToInt(e.Height)) : 1;
            int ix = ((tx % nx) + nx) % nx, iy = ((ty % ny) + ny) % ny;
            Vector2 pad = ObjectAtlas.Texel * 1.5f;
            u0 = e.Uv.xMin + e.Uv.width * ix / nx + pad.x;
            u1 = e.Uv.xMin + e.Uv.width * (ix + 1) / nx - pad.x;
            v1 = e.Uv.yMax - e.Uv.height * iy / ny - pad.y; // строка 0 картинки — верх
            v0 = e.Uv.yMax - e.Uv.height * (iy + 1) / ny + pad.y;
        }

        // Блок (обрыв): лицо к югу, верхняя грань выше на Height, тёмный контур по свободным краям.
        void Block(in Item it, int tx, int ty, ObjectAtlas.Entry[] entries)
        {
            ObjectLook look = it.Look;
            ref ObjectAtlas.Entry white = ref entries[look.WhiteSprite];
            float x0 = it.Lx, x1 = x0 + 1f, yb = it.Y, yt = yb + 1f, h = look.Height;
            const float o = 0.09f; // толщина контура, тайлов
            Color32 tint = look.Tint, line = look.Outline;
            bool n = (it.Near & North) != 0, e = (it.Near & East) != 0, s = (it.Near & South) != 0, w = (it.Near & West) != 0;
            bool faceW = w && (it.Near & SouthWest) == 0, faceE = e && (it.Near & SouthEast) == 0; // у соседа сбоку тоже лицо
            float u0, v0, u1, v1;
            if (!s)
            {
                TileUv(entries[look.FaceSprite], tx, 0, false, out u0, out v0, out u1, out v1);
                Quad(new Vector3(x0, yb), new Vector3(x1, yb), new Vector3(x0, yb + h), new Vector3(x1, yb + h), u0, v0, u1, v1, tint);
            }

            TileUv(entries[look.TopSprite], tx, ty, true, out u0, out v0, out u1, out v1);
            Quad(new Vector3(x0, yb + h), new Vector3(x1, yb + h), new Vector3(x0, yt + h), new Vector3(x1, yt + h), u0, v0, u1, v1, tint);

            if (!s)
            {
                Solid(x0, yb, x1, yb + o, white, line);                              // подошва лица
                Solid(x0, yb + h - o * 0.5f, x1, yb + h + o * 0.5f, white, line);      // кромка между верхом и лицом
                if (!faceW) Solid(x0, yb, x0 + o, yb + h, white, line);
                if (!faceE) Solid(x1 - o, yb, x1, yb + h, white, line);
            }

            if (!n) Solid(x0, yt + h - o, x1, yt + h, white, line);
            if (!w) Solid(x0, yb + h, x0 + o, yt + h, white, line);
            if (!e) Solid(x1 - o, yb + h, x1, yt + h, white, line);
        }

        // Тень блока: след тайла, сдвинутый по высоте вправо-вниз (и на полпути — чтобы тень была сплошной).
        void BlockShadow(in Item it, ObjectAtlas.Entry[] entries)
        {
            ref ObjectAtlas.Entry white = ref entries[it.Look.WhiteSprite];
            float h = it.Look.Height * it.Look.Shadow;
            float dx = h * ShearX * 0.6f, dy = -h * ShearY;
            float x0 = it.Lx, yb = it.Y;
            var black = new Color32(0, 0, 0, 255);
            Solid(x0 + dx * 0.5f, yb + dy * 0.5f, x0 + 1f + dx * 0.5f, yb + 1f + dy * 0.5f, white, black);
            Solid(x0 + dx, yb + dy, x0 + 1f + dx, yb + 1f + dy, white, black);
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

                    if (it.Look.Block)
                    {
                        BlockShadow(it, entries);
                        shadows++;
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
