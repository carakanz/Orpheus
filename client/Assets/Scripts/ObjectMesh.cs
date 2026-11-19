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
    /// <item><b>Тени чанка — один меш:</b> тот же квадрат картинки, положенный на землю у опоры; точка на высоте h
    /// сдвигается в шейдере на h × вектор солнца (C-017, <see cref="SunTable"/>) — тень поворачивается и вытягивается
    /// по времени суток без пересборки; у построек и гор — объём (низ, верх и боковые грани, сдвинутые по солнцу).
    /// Рисуется в текстуру теней (<see cref="WorldFx"/>).</item>
    /// <item><b>Тени от огня — свой меш чанка</b> (C-017, <see cref="BuildFireShadows"/>): силуэт предмета прочь от каждого
    /// из 3 сильнейших видимых источников (<see cref="LightSight"/>), темнота ∝ вкладу источника.</item>
    /// </list>
    /// Координаты — в узле чанка: тайл (lx, ly) занимает x ∈ [lx; lx + 1], y ∈ [−ly − 1; −ly].
    /// </summary>
    public sealed partial class ObjectMesh
    {
        const int Size = Torus.ChunkSize;

        /// <summary>Каналы теней (uv2.y меша теней, Shaders/Shadow.shader): солнце, огонь, пятно у ног.</summary>
        public const float SunChannel = 0f, FireChannel = 1f, FootChannel = 2f;

        struct Item
        {
            public ObjectLook Look;
            public ObjectTable.Placement Place;
            public float X, Y; // опора в узле чанка
            public int Lx;     // столбец тайла в чанке
            public int Near;   // блок: соседи-блоки (биты North…SouthWest)
            public Tiers Tier; // блок: высота своего яруса и соседей (C-016, ObjectMesh.Cliffs)
            public bool Cell;  // постройка-ячеек (E2-17: стена, забор, дверь — ObjectMesh.Walls)
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
        readonly List<Vector4> _info = new List<Vector4>(Size * 4);
        readonly List<Color32> _color = new List<Color32>(Size * Size * 4);
        readonly List<Vector2> _sun = new List<Vector2>(Size * 4); // C-017: uv2 меша теней — солнечная высота точки, канал
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
                ushort obj = chunk.GetObject(index);
                bool cell = BuildCells.IsCellBuilding(obj);
                ObjectLook? look = cell ? CellLook : ObjectTable.Find(obj, chunk.GetFloor(index));
                if (look == null)
                {
                    continue;
                }

                Item it;
                it.Look = look;
                it.Lx = lx;
                it.Near = 0;
                it.Tier = default;
                it.Cell = cell;
                if (cell)
                {
                    it.Place = default;
                    it.X = lx + 0.5f;
                    it.Y = -ly - 1f + 0.25f; // в строке — среди объектов с опорой у середины ячеек
                }
                else if (look.Block)
                {
                    int tx = corner.X + lx, ty = corner.Y + ly;
                    it.Place = default;
                    it.X = lx + 0.5f;
                    it.Y = -ly - 1f; // опора блока — нижний край тайла
                    it.Near = (BlockAt(tx, ty - 1) ? North : 0) | (BlockAt(tx + 1, ty) ? East : 0)
                        | (BlockAt(tx, ty + 1) ? South : 0) | (BlockAt(tx - 1, ty) ? West : 0)
                        | (BlockAt(tx + 1, ty + 1) ? SouthEast : 0) | (BlockAt(tx - 1, ty + 1) ? SouthWest : 0)
                        | (BlockAt(tx + 1, ty - 1) ? NorthEast : 0) | (BlockAt(tx - 1, ty - 1) ? NorthWest : 0);
                    it.Tier = TiersAt(look, c, lx, ly);
                    RowHadBlocks = true;
                }
                else
                {
                    int tx = corner.X + lx, ty = corner.Y + ly;
                    int rot = ObjectId.RotationOf(obj);
                    // C-041: запись старой кровати 1 × 2 (окно 2 × 2 занято соседом) — старый рисунок и старый сдвиг.
                    bool narrow = look.Id == ObjectId.Bed && FloorCellsLook.BedIsNarrow(_world, _map, tx, ty, rot);
                    it.Place = narrow
                        ? ObjectTable.PlaceNarrow(look, tx, ty, rot)
                        : ObjectTable.Place(look, tx, ty, rot); // E2-16: вид по повороту
                    it.X = lx + 0.5f + it.Place.Dx;
                    it.Y = -ly - 1f + ObjectTable.BaseLift + it.Place.Dy;
                    FloorCellsLook.ShiftFurniture(chunk, index, narrow, ref it.X, ref it.Y); // E2-15: сдвиг на полтайла; E2-16 — размером; C-041 — старой узкой
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
            _sun.Clear();
        }

        // C-017: солнечная высота для вершин последнего квадрата (левый нижний, правый нижний, левый верхний, правый верхний).
        void Sun4(float h0, float h1, float channel)
        {
            while (_sun.Count < _pos.Count - 4)
            {
                _sun.Add(new Vector2(0f, channel));
            }

            _sun.Add(new Vector2(h0, channel));
            _sun.Add(new Vector2(h0, channel));
            _sun.Add(new Vector2(h1, channel));
            _sun.Add(new Vector2(h1, channel));
        }

        // C-017: объём постройки или горы на солнце — выпуклый след (4 угла по кругу) на земле, его копия на высоте h и
        // боковые грани по рёбрам (edges: бит i — ребро corners[i]…corners[i + 1]); шейдер сдвигает верх по солнцу —
        // при любом направлении солнца выходит «оболочка» основания и верха (параллелограмм).
        void Prism(Vector2 c0, Vector2 c1, Vector2 c2, Vector2 c3, float h, byte alpha, int edges, Vector2 white)
        {
            var c = new Color32(0, 0, 0, alpha);
            // Верх: c0 c1 — нижние, c3 c2 — верхние (порядок Quad: ЛН, ПН, ЛВ, ПВ).
            Quad(c0, c1, c3, c2, white.x, white.y, white.x, white.y, c);
            Sun4(h, h, SunChannel);
            for (int i = 0; i < 4; i++)
            {
                if ((edges & (1 << i)) == 0)
                {
                    continue;
                }

                Vector2 p = i == 0 ? c0 : i == 1 ? c1 : i == 2 ? c2 : c3;
                Vector2 q = i == 0 ? c1 : i == 1 ? c2 : i == 2 ? c3 : c0;
                Quad(p, q, p, q, white.x, white.y, white.x, white.y, c);
                Sun4(0f, h, SunChannel);
            }
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

        /// <summary>Ветер объекта для шейдера (E1-11): целая часть — фаза растения × 64 (по хэшу тайла), дробная — амплитуда.</summary>
        public static float SwayOf(ObjectLook look, int tx, int ty) =>
            look.Sway <= 0f ? 0f : Mathf.Floor(GroundAtlas.Hash(tx, ty, 149) * 63f) + Mathf.Min(look.Sway, 0.99f);

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
                if (it.Cell)
                {
                    while (_info.Count < _pos.Count)
                    {
                        _info.Add(Vector4.zero); // квадраты блоков до постройки
                    }

                    Cells(it, corner.X + it.Lx, corner.Y + ly, entries);
                    continue;
                }

                if (it.Look.Block)
                {
                    FillInfo(Vector4.zero); // квадраты до блока
                    Block(it, corner.X + it.Lx, corner.Y + ly, entries);
                    FillInfo(BlockInfo(it)); // C-016: массив перед своим персонажем полупрозрачен в круге, как стена
                    continue;
                }

                ref ObjectAtlas.Entry e = ref entries[it.Place.Sprite];
                float s = it.Place.Scale;
                float x0 = it.X - e.PivotX * s, x1 = x0 + e.Width * s;
                float y0 = it.Y - e.PivotY * s, y1 = y0 + e.Height * s;
                Quad(new Vector3(x0, y0), new Vector3(x1, y0), new Vector3(x0, y1), new Vector3(x1, y1), e, it.Place.Flip, it.Look.Tint);
                // uv1: опора, «высокий», ветер (E1-11: амплитуда на тайл высоты + фаза растения — Shaders/OrphLife.hlsl).
                var info = new Vector4(it.X, it.Y, it.Look.Tall ? 1f : 0f, SwayOf(it.Look, corner.X + it.Lx, corner.Y + ly));
                while (_info.Count < _pos.Count - 4)
                {
                    _info.Add(Vector4.zero); // квадраты блоков до этого объекта
                }

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
                    if (it.Look.Block && !it.Cell)
                    {
                        FillInfo(Vector4.zero);
                        BlockDeco(it, corner.X + it.Lx, corner.Y + ly, entries);
                        FillInfo(BlockInfo(it));
                    }
                }
            }

            while (_info.Count < _pos.Count)
            {
                _info.Add(Vector4.zero); // хвост: не высокие, не качаются
            }

            mesh.SetVertices(_pos);
            mesh.SetUVs(0, _uv);
            mesh.SetUVs(1, _info);
            mesh.SetColors(_color);
            mesh.SetTriangles(_index, 0, calculateBounds: true);
            return n;
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

        static Color32 Mul(Color32 c, float k) =>
            new Color32((byte)Mathf.Min(255f, c.r * k), (byte)Mathf.Min(255f, c.g * k), (byte)Mathf.Min(255f, c.b * k), c.a);

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

                    if (it.Cell)
                    {
                        while (_info.Count < _pos.Count)
                        {
                            _info.Add(Vector4.zero);
                        }

                        CellShadows(it, Torus.CornerOf(c).X + it.Lx, Torus.CornerOf(c).Y + ly, entries);
                        shadows++;
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
                    // C-017: точка картинки (x, h) лежит у опоры в (x; 0), шейдер сдвигает её на h × k × солнце.
                    Quad(new Vector3(it.X + x0, it.Y), new Vector3(it.X + x1, it.Y), new Vector3(it.X + x0, it.Y), new Vector3(it.X + x1, it.Y),
                        e, it.Place.Flip, new Color32(0, 0, 0, 255));
                    Sun4(h0 * k, h1 * k, SunChannel);
                    // E1-11: тень качается вместе с кроной — uv1: опора, высота точки над опорой, ветер.
                    float sway = SwayOf(it.Look, Torus.CornerOf(c).X + it.Lx, Torus.CornerOf(c).Y + ly);
                    while (_info.Count < _pos.Count - 4)
                    {
                        _info.Add(Vector4.zero); // тени блоков до этой
                    }

                    _info.Add(new Vector4(it.X, it.Y, h0, sway));
                    _info.Add(new Vector4(it.X, it.Y, h0, sway));
                    _info.Add(new Vector4(it.X, it.Y, h1, sway));
                    _info.Add(new Vector4(it.X, it.Y, h1, sway));
                    shadows++;
                }
            }

            if (shadows == 0)
            {
                return 0;
            }

            FinishShadowMesh(mesh);
            return shadows;
        }
    }
}
