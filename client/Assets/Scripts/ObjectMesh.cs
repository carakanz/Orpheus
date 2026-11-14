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

        const int North = 1, East = 2, South = 4, West = 8, SouthEast = 16, SouthWest = 32, NorthEast = 64, NorthWest = 128;

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
                        | (BlockAt(tx + 1, ty + 1) ? SouthEast : 0) | (BlockAt(tx - 1, ty + 1) ? SouthWest : 0)
                        | (BlockAt(tx + 1, ty - 1) ? NorthEast : 0) | (BlockAt(tx - 1, ty - 1) ? NorthWest : 0);
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

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float u0, float v0, float u1, float v1, Color32 color) =>
            Quad(a, b, c, d, u0, v0, u1, v1, color, color, color, color);

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float u0, float v0, float u1, float v1, Color32 ca, Color32 cb, Color32 cc, Color32 cd)
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
            _color.Add(ca);
            _color.Add(cb);
            _color.Add(cc);
            _color.Add(cd);
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

            if (RowHadBlocks)
            {
                for (int i = 0; i < n; i++)
                {
                    ref Item it = ref _items[i];
                    if (it.Look.Block)
                    {
                        BlockDeco(it, corner.X + it.Lx, corner.Y + ly, entries);
                    }
                }
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
        // cells > 1 — картинка растянута: на её ширину приходится в cells раз меньше тайлов (рисунок крупнее).
        static void TileUv(in ObjectAtlas.Entry e, int tx, int ty, int cells, bool rows, out float u0, out float v0, out float u1, out float v1)
        {
            int nx = Mathf.Max(1, Mathf.RoundToInt(e.Width) / cells);
            int ny = rows ? Mathf.Max(1, Mathf.RoundToInt(e.Height) / cells) : 1;
            int ix = ((tx % nx) + nx) % nx, iy = ((ty % ny) + ny) % ny;
            Vector2 pad = ObjectAtlas.Texel * 1.5f;
            u0 = e.Uv.xMin + e.Uv.width * ix / nx + pad.x;
            u1 = e.Uv.xMin + e.Uv.width * (ix + 1) / nx - pad.x;
            v1 = e.Uv.yMax - e.Uv.height * iy / ny - pad.y; // строка 0 картинки — верх
            v0 = e.Uv.yMax - e.Uv.height * (iy + 1) / ny + pad.y;
        }

        // Смещение узла сетки тайлов мира (cx, cy) — одно на все тайлы вокруг узла: общие углы соседних блоков совпадают.
        Vector2 Jitter(int cx, int cy, float amount)
        {
            TileCoord t = Torus.Wrap(_map, new TileCoord(cx, cy));
            return new Vector2((GroundAtlas.Hash(t.X, t.Y, 131) * 2f - 1f) * amount, (GroundAtlas.Hash(t.X, t.Y, 137) * 2f - 1f) * amount);
        }

        // Затенение узла сетки 0,86…1: верх гряды не одного тона.
        float Shade(int cx, int cy)
        {
            TileCoord t = Torus.Wrap(_map, new TileCoord(cx, cy));
            return 0.86f + 0.14f * GroundAtlas.Hash(t.X, t.Y, 139);
        }

        static Color32 Mul(Color32 c, float k) => new Color32((byte)(c.r * k), (byte)(c.g * k), (byte)(c.b * k), c.a);

        // Отрезок контура p–q толщиной o (сплошной цвет).
        void Line(Vector2 p, Vector2 q, float o, in ObjectAtlas.Entry white, Color32 color)
        {
            Vector2 d = q - p;
            float len = d.magnitude;
            if (len < 1e-4f)
            {
                return;
            }

            d /= len;
            Vector2 n = new Vector2(-d.y, d.x) * (o * 0.5f);
            Vector2 a = p - d * (o * 0.5f), b = q + d * (o * 0.5f);
            Vector2 m = white.Uv.center;
            Quad(a - n, b - n, a + n, b + n, m.x, m.y, m.x, m.y, color);
        }

        // Углы блока: земля (G) и верх (T), поднятый на высоту; порядок — SW, SE, NW, NE.
        struct BlockShape
        {
            public Vector2 Gsw, Gse, Gnw, Gne, Tsw, Tse, Tnw, Tne;
        }

        /// <summary>
        /// Форма блока-гряды (E1-6b): углы тайла сдвинуты шумом узла сетки (нет прямых линий), верх поднят на высоту и со
        /// свободных боков сужен (бок гряды — наклонная грань), выпуклые углы срезаны.
        /// </summary>
        BlockShape Shape(in Item it, int tx, int ty)
        {
            ObjectLook look = it.Look;
            float x0 = it.Lx, x1 = x0 + 1f, yb = it.Y, yt = yb + 1f, h = look.Height;
            bool n = (it.Near & North) != 0, e = (it.Near & East) != 0, s = (it.Near & South) != 0, w = (it.Near & West) != 0;
            float inset = !w && !e ? look.Inset * 0.7f : look.Inset;
            const float g = 0.12f, j = 0.2f;
            BlockShape q;
            q.Gnw = new Vector2(x0, yt) + Jitter(tx, ty, g);
            q.Gne = new Vector2(x1, yt) + Jitter(tx + 1, ty, g);
            q.Gsw = new Vector2(x0, yb) + Jitter(tx, ty + 1, g);
            q.Gse = new Vector2(x1, yb) + Jitter(tx + 1, ty + 1, g);
            var up = new Vector2(0f, h);
            q.Tnw = new Vector2(x0, yt) + up + Jitter(tx, ty, j);
            q.Tne = new Vector2(x1, yt) + up + Jitter(tx + 1, ty, j);
            q.Tsw = new Vector2(x0, yb) + up + Jitter(tx, ty + 1, j);
            q.Tse = new Vector2(x1, yb) + up + Jitter(tx + 1, ty + 1, j);
            if (!w)
            {
                q.Tnw.x += inset;
                q.Tsw.x += inset;
            }

            if (!e)
            {
                q.Tne.x -= inset;
                q.Tse.x -= inset;
            }

            // Выпуклые углы: срез.
            if (!n && !w) q.Tnw += new Vector2(0.16f, -0.3f);
            if (!n && !e) q.Tne += new Vector2(-0.16f, -0.3f);
            if (!s && !w) { q.Tsw.x += 0.14f; q.Gsw.x += 0.22f; }
            if (!s && !e) { q.Tse.x -= 0.14f; q.Gse.x -= 0.22f; }
            return q;
        }

        // Блок-гряда (обрыв): наклонные бока, лицо к югу, тёмный неровный верх, контур по свободным краям.
        void Block(in Item it, int tx, int ty, ObjectAtlas.Entry[] entries)
        {
            ObjectLook look = it.Look;
            ref ObjectAtlas.Entry white = ref entries[look.WhiteSprite];
            const float o = 0.085f; // толщина контура, тайлов
            Color32 line = look.Outline;
            bool n = (it.Near & North) != 0, e = (it.Near & East) != 0, s = (it.Near & South) != 0, w = (it.Near & West) != 0;
            BlockShape q = Shape(it, tx, ty);
            float u0, v0, u1, v1;

            // Бока: западный освещён, восточный в тени (тени падают вправо-вниз).
            if (!w)
            {
                TileUv(entries[look.FaceSprite], ty, 0, 1, false, out u0, out v0, out u1, out v1);
                Quad(q.Gsw, q.Tsw, q.Gnw, q.Tnw, u0, v0, u1, v1, look.SideLit);
            }

            if (!e)
            {
                TileUv(entries[look.FaceSprite], ty + 2, 0, 1, false, out u0, out v0, out u1, out v1);
                Quad(q.Tse, q.Gse, q.Tne, q.Gne, u0, v0, u1, v1, look.SideDark);
            }

            if (!s)
            {
                TileUv(entries[look.FaceSprite], tx, 0, 1, false, out u0, out v0, out u1, out v1);
                Quad(q.Gsw, q.Gse, q.Tsw, q.Tse, u0, v0, u1, v1, look.FaceTint);
            }

            TileUv(entries[look.TopSprite], tx, ty, look.TopCells, true, out u0, out v0, out u1, out v1);
            Quad(q.Tsw, q.Tse, q.Tnw, q.Tne, u0, v0, u1, v1,
                Mul(look.Tint, Shade(tx, ty + 1)), Mul(look.Tint, Shade(tx + 1, ty + 1)), Mul(look.Tint, Shade(tx, ty)), Mul(look.Tint, Shade(tx + 1, ty)));

            if (!s)
            {
                Line(q.Gsw, q.Gse, o, white, line);         // подошва лица
                Line(q.Tsw, q.Tse, o * 0.8f, white, line);  // кромка между верхом и лицом
                if (!w || (it.Near & SouthWest) != 0) Line(q.Gsw, q.Tsw, o, white, line);
                if (!e || (it.Near & SouthEast) != 0) Line(q.Gse, q.Tse, o, white, line);
            }

            if (!n) Line(q.Tnw, q.Tne, o, white, line);
            if (!w)
            {
                Line(q.Gsw, q.Gnw, o, white, line);
                Line(q.Tsw, q.Tnw, o * 0.8f, white, line);
                if (!n) Line(q.Gnw, q.Tnw, o, white, line);
            }

            if (!e)
            {
                Line(q.Gse, q.Gne, o, white, line);
                Line(q.Tse, q.Tne, o * 0.8f, white, line);
                if (!n) Line(q.Gne, q.Tne, o, white, line);
            }
        }

        void Deco(ObjectLook look, ObjectAtlas.Entry[] entries, int pick, float x, float y, float scale, bool flip, Color32 tint)
        {
            ref ObjectAtlas.Entry d = ref entries[look.DecoSprites[pick % look.DecoSprites.Length]];
            float x0 = x - d.PivotX * scale, x1 = x0 + d.Width * scale;
            float y0 = y - d.PivotY * scale, y1 = y0 + d.Height * scale;
            Quad(new Vector3(x0, y0), new Vector3(x1, y0), new Vector3(x0, y1), new Vector3(x1, y1), d, flip, tint);
        }

        // Камни гряды (без коллизий — тайл и так непроходим): валуны на верху разбивают силуэт, осыпь у подошвы и боков.
        void BlockDeco(in Item it, int tx, int ty, ObjectAtlas.Entry[] entries)
        {
            ObjectLook look = it.Look;
            if (look.DecoSprites.Length == 0)
            {
                return;
            }

            TileCoord t = Torus.Wrap(_map, new TileCoord(tx, ty));
            float x0 = it.Lx, yb = it.Y, h = look.Height;
            bool n = (it.Near & North) != 0, e = (it.Near & East) != 0, s = (it.Near & South) != 0, w = (it.Near & West) != 0;
            float r1 = GroundAtlas.Hash(t.X, t.Y, 211), r2 = GroundAtlas.Hash(t.X, t.Y, 223), r3 = GroundAtlas.Hash(t.X, t.Y, 227);
            float r4 = GroundAtlas.Hash(t.X, t.Y, 229), r5 = GroundAtlas.Hash(t.X, t.Y, 233);
            int pick = (int)(r3 * 997f);
            bool edge = !n || !e || !w;
            if (r1 < (edge ? look.DecoEdge : look.DecoTop))
            {
                // На верху; у северного края — выше, чтобы камень торчал над кромкой.
                float x = x0 + 0.5f + (r2 - 0.5f) * 0.5f + (!w ? 0.2f : 0f) - (!e ? 0.2f : 0f);
                float y = yb + h + (!n ? 0.55f : 0.1f + r4 * 0.4f);
                Deco(look, entries, pick, x, y, 0.8f + r5 * 0.55f, r4 < 0.5f, look.DecoTint);
            }

            if (!s && r2 < look.DecoFoot)
            {
                Deco(look, entries, pick + 1, x0 + 0.5f + (r1 - 0.5f) * 0.7f, yb - 0.05f + r5 * 0.12f, 0.5f + r4 * 0.35f, r1 < 0.5f, look.RubbleTint);
            }

            if (!w && r4 < look.DecoFoot)
            {
                Deco(look, entries, pick + 2, x0 + 0.02f + r1 * 0.1f, yb + 0.2f + r2 * 0.5f, 0.45f + r5 * 0.35f, r3 < 0.5f, look.RubbleTint);
            }

            if (!e && r5 < look.DecoFoot)
            {
                Deco(look, entries, pick + 3, x0 + 0.98f - r2 * 0.1f, yb + 0.2f + r1 * 0.5f, 0.45f + r4 * 0.35f, r3 >= 0.5f, look.RubbleTint);
            }
        }

        // Тень блока: след тайла, сдвинутый по высоте вправо-вниз (и на полпути — чтобы тень была сплошной).
        void BlockShadow(in Item it, int tx, int ty, ObjectAtlas.Entry[] entries)
        {
            ref ObjectAtlas.Entry white = ref entries[it.Look.WhiteSprite];
            float h = it.Look.Height * it.Look.Shadow;
            var d = new Vector2(h * ShearX * 0.6f, -h * ShearY);
            float x0 = it.Lx, yb = it.Y;
            const float g = 0.12f;
            Vector2 sw = new Vector2(x0, yb) + Jitter(tx, ty + 1, g), se = new Vector2(x0 + 1f, yb) + Jitter(tx + 1, ty + 1, g);
            Vector2 nw = new Vector2(x0, yb + 1f) + Jitter(tx, ty, g), ne = new Vector2(x0 + 1f, yb + 1f) + Jitter(tx + 1, ty, g);
            var black = new Color32(0, 0, 0, 255);
            Vector2 m = white.Uv.center;
            Quad(sw + d * 0.5f, se + d * 0.5f, nw + d * 0.5f, ne + d * 0.5f, m.x, m.y, m.x, m.y, black);
            Quad(sw + d, se + d, nw + d, ne + d, m.x, m.y, m.x, m.y, black);
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
                        BlockShadow(it, Torus.CornerOf(c).X + it.Lx, Torus.CornerOf(c).Y + ly, entries);
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
