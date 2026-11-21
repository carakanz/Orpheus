#nullable enable
using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Orpheus.Client
{
    /// <summary>
    /// Рисование окна чанков (docs/07, 1.2 и 3.5; карточка 0-010, шаг 3).
    /// <list type="bullet">
    /// <item>Набор чанков — <see cref="ChunkWindow.Update"/> ядра: вход — блок 6 × 4 вокруг персонажа, выход — из блока 8 × 6
    /// (гистерезис), не больше 48 чанков.</item>
    /// <item>На чанк — узел: меш земли (<see cref="GroundMesh"/>, E1-4: земля и вода без клетки, с переходами) и два
    /// <see cref="Tilemap"/> под общим <see cref="Grid"/> — построенные полы поверх земли и объекты; заливка каждого —
    /// один вызов <see cref="Tilemap.SetTilesBlock"/> на 32 × 32 клетки.</item>
    /// <item>Правка тайла: меш земли чанка пересобирается, только если в чанке сменился пол (сверка с копией полов узла);
    /// правка у края чанка (ближе <see cref="GroundMesh.Halo"/> тайлов) пересобирает и соседа — всё в том же вызове.</item>
    /// <item>Узлы вышедших чанков уходят в пул и переиспользуются для вошедших — новые <see cref="GameObject"/> создаются,
    /// только пока пул не набрал наибольшего окна.</item>
    /// <item>Место узла — <see cref="FloatingOrigin.ChunkToUnity"/>: Δ от якоря по тору, у шва — ближайшая копия.</item>
    /// </list>
    /// </summary>
    public sealed class WorldView
    {
        const int Size = Torus.ChunkSize;

        /// <summary>Порядок сортировки: земля в самом низу, построенный пол над ней, объекты — вместе с персонажем по оси Y.</summary>
        public const int GroundOrder = -10;
        public const int FloorOrder = 0;
        public const int ObjectOrder = 10;

        /// <summary>Наложение слоя теней (E1-7): над землёй и построенным полом, под объектами и существами.</summary>
        public const int ShadowOrder = 3;

        /// <summary>
        /// Середина полосы порядков для всего «с высотой» (E1-7): строки объектов и существа сортируются по нижней кромке —
        /// <see cref="SortOrderAt"/>. Плоские плитки Tilemap (стены, двери — <see cref="ObjectOrder"/>) лежат ниже.
        /// </summary>
        public const int TallOrder = DrawOrder.TallOrder;

        /// <summary>Тон суток (E1-8) — поверх всего мира.</summary>
        public const int TintOrder = 30000;

        /// <summary>
        /// Порядок отрисовки существа с ногами на высоте y (координата Unity): ниже на экране — позже, шаг — четверть
        /// тайла. Существа получают чётные порядки, строки объектов — нечётные (<see cref="RowOrderAt"/>).
        /// C-049: математика вынесена в <see cref="DrawOrder"/> (чистая, linked в тесты).
        /// </summary>
        public static int SortOrderAt(float y) => DrawOrder.SortOrderAt(y);

        /// <summary>Порядок строки объектов с опорой на высоте y: существо в той же четверти тайла — позади строки.</summary>
        public static int RowOrderAt(float y) => DrawOrder.RowOrderAt(y);

        /// <summary>
        /// C-049: порядок «низкой полосы» строки (костёр — слова Автора «почему костёр надо мной оказался»):
        /// камни и низ пламени рисуются на два шага ПЕРЕД существом той же четверти, чтобы персонаж,
        /// стоящий у костра, был поверх огня; севернее костра — за ним, южнее — перед ним.
        /// </summary>
        public static int LowRowOrderAt(float y) => DrawOrder.LowRowOrderAt(y);

        public sealed class ChunkNode
        {
            public ChunkCoord Coord;
            public GameObject Root = null!;
            public Tilemap Floor = null!;
            public Tilemap Objects = null!;

            /// <summary>E2-15: пол частью тайла — плитки-четверти на сетке 0,5 (<see cref="FloorCellsLook"/>) и что на ней сейчас.</summary>
            public Tilemap FloorCells = null!;
            public readonly byte[] FloorMasks = new byte[Size * Size];
            public readonly byte[] FloorMaskFloors = new byte[Size * Size];

            /// <summary>Меш земли чанка (E1-4).</summary>
            public Mesh Ground = null!;

            /// <summary>Полы чанка, по которым собран меш земли: что сейчас на экране.</summary>
            public readonly byte[] Floors = new byte[Size * Size];

            /// <summary>Копия полов действительна (узел не из пула).</summary>
            public bool HasFloors;

            /// <summary>В Tilemap пола есть плитки построенных полов.</summary>
            public bool HasBuiltFloor;

            /// <summary>Меш собран без какого-то соседнего чанка (сеть: ещё не пришёл) — пересобрать, когда появится.</summary>
            public bool HaloIncomplete;

            /// <summary>Меш ждёт пересборки в <see cref="PlaceNodes"/> этого кадра.</summary>
            public bool GroundDirty;

            /// <summary>Объекты чанка, по которым собраны меши строк и теней (E1-7): что сейчас на экране.</summary>
            public readonly ushort[] Objs = new ushort[Size * Size];

            /// <summary>E2-17: ключи вида построек-ячеек (<see cref="ObjectMesh.CellKey"/>), по которым собраны меши строк.</summary>
            public readonly long[] CellKeys = new long[Size * Size];

            /// <summary>Строки объектов «с высотой»: меш и отрисовщик на строку тайлов; создаются, когда в строке что-то появилось.</summary>
            public readonly Mesh?[] RowMeshes = new Mesh?[Size];
            public readonly MeshRenderer?[] Rows = new MeshRenderer?[Size];

            /// <summary>C-049: «низкая полоса» строки (костёр) — отдельный меш/отрисовщик перед существом той же четверти.</summary>
            public readonly Mesh?[] LowRowMeshes = new Mesh?[Size];
            public readonly MeshRenderer?[] LowRows = new MeshRenderer?[Size];
            public readonly int[] LowRowCounts = new int[Size];

            /// <summary>Объектов в строке сейчас.</summary>
            public readonly int[] RowCounts = new int[Size];

            /// <summary>В строке есть блоки (обрыв): их вид зависит от соседей, в том числе в соседнем чанке.</summary>
            public readonly bool[] RowBlocks = new bool[Size];

            /// <summary>Меш теней чанка (рисуется камерой теней).</summary>
            public Mesh Shadows = null!;
            public MeshRenderer ShadowRenderer = null!;

            /// <summary>C-017: меш теней от огня и штамп окна 3 × 3, по которому он собран (0 — не собран).</summary>
            public Mesh FireShadows = null!;
            public MeshRenderer FireRenderer = null!;
            public long FireStamp;

            /// <summary>Пучки травы чанка (E1-11, план 1.12): полосы мешей над землёй.</summary>
            public GrassLayer.Node Grass = null!;

            /// <summary>Высота узла, для которой строкам выставлен порядок отрисовки.</summary>
            public float OrderedY = float.NaN;
        }

        readonly WorldModel _world;
        readonly MapShape _map;
        readonly FloatingOrigin _origin;
        readonly Transform _grid;
        readonly TileBase _meadow, _stone, _tree, _rock;
        readonly GroundMesh _ground;
        readonly ObjectMesh _objects;
        readonly GrassLayer _grass;
        readonly bool[] _rowDirty = new bool[Size];
        readonly Material? _groundMaterial;
        readonly TileBase?[] _floorBuf = new TileBase?[Size * Size];
        readonly TileBase?[] _objBuf = new TileBase?[Size * Size];
        static readonly BoundsInt Block = new BoundsInt(0, -Size, 0, Size, Size, 1);

        readonly Dictionary<ChunkCoord, ChunkNode> _nodes = new Dictionary<ChunkCoord, ChunkNode>(ChunkWindow.MaxChunks);
        readonly Stack<ChunkNode> _pool = new Stack<ChunkNode>(ChunkWindow.MaxChunks);
        ChunkCoord[] _current = new ChunkCoord[ChunkWindow.MaxChunks];
        ChunkCoord[] _next = new ChunkCoord[ChunkWindow.MaxChunks];
        readonly ChunkCoord[] _added = new ChunkCoord[ChunkWindow.MaxChunks * 2];
        readonly ChunkCoord[] _removed = new ChunkCoord[ChunkWindow.MaxChunks * 2];
        int _count;

        public WorldView(WorldModel world, FloatingOrigin origin, Transform grid,
            TileBase meadow, TileBase stone, TileBase tree, TileBase rock)
        {
            _world = world;
            _map = world.Shape;
            _origin = origin;
            _grid = grid;
            _meadow = meadow;
            _stone = stone;
            _tree = tree;
            _rock = rock;
            _ground = new GroundMesh(world);
            _objects = new ObjectMesh(world);
            _grass = new GrassLayer(world);
            LifeFx.Bind(world, origin, this);
            WorldFx.Bind(world, origin, this); // C-017: тени от огня и у существ — от этого мира // E1-11: ветер, толчки, след в траве — от этого мира и начала координат
            _groundMaterial = GroundAtlas.Material;
            ObjectAtlas.Ensure();
        }

        /// <summary>Чанков в окне сейчас.</summary>
        public int Count => _count;

        /// <summary>Наибольшее число чанков в окне за всё время.</summary>
        public int MaxCount { get; private set; }

        /// <summary>Узлов создано всего (пул + в окне).</summary>
        public int NodesCreated { get; private set; }

        /// <summary>Сколько чанков залито (вход в окно) за всё время.</summary>
        public int Fills { get; private set; }

        /// <summary>Чанки окна (отсортированы, как их отдаёт ядро).</summary>
        public ReadOnlySpan<ChunkCoord> Chunks => new ReadOnlySpan<ChunkCoord>(_current, 0, _count);

        public bool TryGetNode(ChunkCoord c, out ChunkNode node) => _nodes.TryGetValue(c, out node!);

        /// <summary>Построенные узлы окна (для систем, которым нужен обзор: искры костров C-049).</summary>
        public IEnumerable<ChunkNode> Nodes => _nodes.Values;

        /// <summary>Мешей земли собрано за всё время (вход чанка в окно и правки пола).</summary>
        public int GroundBuilds => _ground.Builds;

        /// <summary>
        /// Плитка Tilemap пола по id ядра (E1-4): только построенные полы (<see cref="GroundKind.Built"/>) — каменной плитой
        /// поверх земли; природные полы и воду рисует меш земли (<see cref="GroundTable"/>) — плитки нет.
        /// </summary>
        public TileBase? FloorTile(byte floor) =>
            GroundTable.Of(floor).Kind != GroundKind.Built ? null
            : BuildLook.TileOf(BuildDefs.FloorKey(floor)) ?? _stone; // E2-5: доски, плиты, настил (заглушки скрипта)

        /// <summary>Меш земли узла собран по этому полу тайла (lx, ly) — «правка видна в тот же кадр» для проверок.</summary>
        public static bool GroundShows(ChunkNode node, int lx, int ly, byte floor) =>
            node.HasFloors && !node.GroundDirty && node.Floors[Chunk.IndexOf(lx, ly)] == floor;

        /// <summary>Замер (E1-7): время сборки объектов и теней целого чанка при входе в окно, такты Stopwatch, и число таких сборок.</summary>
        public long FreshObjectTicks { get; private set; }
        public int FreshObjectFills { get; private set; }

        /// <summary>Мешей строк объектов и теней собрано за всё время (E1-7).</summary>
        public int ObjectBuilds => _objects.Builds;

        /// <summary>Объектов «с высотой» в окне сейчас (квадратов в мешах строк).</summary>
        public int ObjectQuads()
        {
            int n = 0;
            for (int i = 0; i < _count; i++)
            {
                int[] rows = _nodes[_current[i]].RowCounts;
                for (int r = 0; r < Size; r++)
                {
                    n += rows[r];
                }
            }

            return n;
        }

        /// <summary>Меши строк узла собраны по этому объекту тайла (lx, ly) — «правка видна в тот же кадр» для проверок.</summary>
        public static bool ObjectShows(ChunkNode node, int lx, int ly, ushort obj) =>
            node.HasFloors && node.Objs[Chunk.IndexOf(lx, ly)] == obj;

        /// <summary>
        /// Тайл Tilemap объекта по сырому id ядра; пусто, неизвестное и всё, что рисуется мешем по таблице
        /// <see cref="ObjectTable"/> (деревья, кусты, валуны, руда, пни, костёр, факел — E1-7), — null.
        /// </summary>
        public TileBase? ObjectTile(ushort obj)
        {
            if (obj == ObjectId.Empty || ObjectId.IsPart(obj))
            {
                return null; // часть многотайлового объекта рисует якорь
            }

            if (ObjectTable.Has(obj))
            {
                return null; // объект «с высотой» — квадрат в меше своей строки
            }

            switch (ObjectId.IdOf(obj))
            {
                case ObjectId.Tree: return _tree;
                case ObjectId.Rock: return _rock;
                // Объекты генератора v1 (1-017) до своих спрайтов (1-024): невидимая преграда хуже похожей.
                case ObjectId.Bush: return _tree;
                case ObjectId.Granite: return _rock;
                case ObjectId.Ore: return _rock;
                // Стены, забор, дверь (E2-17): «с высотой» — в мешах строк объектов (ObjectMesh.Walls), плитки нет.
                case ObjectId.StoneWall:
                case ObjectId.WoodWall:
                case ObjectId.WoodFence:
                case ObjectId.Door:
                    return null;
                // Ковёр (1-011, бесплатная постройка): своего спрайта нет (1-024) — рисуем лугом, проба 1-022 видит клетку.
                case ObjectId.Carpet: return _meadow;
                // Факел и костёр (E1-8: свет источников для тона суток): своего спрайта нет (1-024) — рисуем валуном,
                // чтобы в кадре было видно, от чего круг света.
                case ObjectId.Torch: return _rock;
                case ObjectId.Campfire: return _rock;
                default: return null;
            }
        }

        /// <summary>Сдвинуть окно к персонажу: выбросить вышедшие чанки в пул, залить вошедшие, расставить узлы от якоря.</summary>
        public void UpdateWindow(WorldPos character)
        {
            int n = ChunkWindow.Update(_map, character, new ReadOnlySpan<ChunkCoord>(_current, 0, _count), _next);
            ChunkSets.Diff(new ReadOnlySpan<ChunkCoord>(_current, 0, _count), new ReadOnlySpan<ChunkCoord>(_next, 0, n),
                _added, out int added, _removed, out int removed);

            for (int i = 0; i < removed; i++)
            {
                ChunkCoord c = _removed[i];
                ChunkNode node = _nodes[c];
                _nodes.Remove(c);
                Release(node);
            }

            for (int i = 0; i < added; i++)
            {
                ChunkCoord c = _added[i];
                ChunkNode node = _pool.Count > 0 ? _pool.Pop() : CreateNode();
                node.Coord = c;
                node.Root.name = $"Chunk {c.X},{c.Y}";
                Fill(node, c);
                node.Root.SetActive(true);
                _nodes.Add(c, node);
            }

            ChunkCoord[] t = _current;
            _current = _next;
            _next = t;
            _count = n;
            if (n > MaxCount)
            {
                MaxCount = n;
            }

            PlaceNodes();
        }

        /// <summary>
        /// Сеть (0-026): чанк пришёл от сервера целиком или к нему применилась пачка — залить этот чанк заново в тот же
        /// кадр (<see cref="Tilemap.SetTilesBlock"/>); нового чанка ещё нет на экране — взять узел из пула. Набор чанков
        /// задаёт сервер (<c>ClientWorld</c>), а не позиция персонажа: <see cref="UpdateWindow"/> в сети не зовётся.
        /// </summary>
        public void ShowChunk(ChunkCoord c)
        {
            if (_nodes.TryGetValue(c, out ChunkNode? node))
            {
                Fill(node, c);
                RefreshSeams(c); // E2-12: контур тонкой стены у соседнего чанка зависит от ячеек этого
                return;
            }

            if (_count == _current.Length)
            {
                throw new InvalidOperationException($"Чанков на экране больше {_current.Length}: сервер прислал лишний ({c.X}, {c.Y}).");
            }

            node = _pool.Count > 0 ? _pool.Pop() : CreateNode();
            node.Coord = c;
            node.Root.name = $"Chunk {c.X},{c.Y}";
            Fill(node, c);
            node.Root.SetActive(true);
            node.Root.transform.localPosition = _origin.ChunkToUnity(c);
            ApplyRowOrders(node);
            _nodes.Add(c, node);
            _current[_count++] = c;
            ChunkSets.Sort(new Span<ChunkCoord>(_current, 0, _count));
            if (_count > MaxCount)
            {
                MaxCount = _count;
            }
        }

        /// <summary>Сеть (0-026): чанк выброшен из окна (центр окна от сервера) — узел в пул.</summary>
        public void HideChunk(ChunkCoord c)
        {
            if (!_nodes.TryGetValue(c, out ChunkNode? node))
            {
                return;
            }

            _nodes.Remove(c);
            Release(node);
            int n = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_current[i] != c)
                {
                    _current[n++] = _current[i];
                }
            }

            _count = n;
        }

        /// <summary>Расставить все узлы окна от текущего якоря (после переезда начала — обязательно).</summary>
        public void PlaceNodes()
        {
            for (int i = 0; i < _count; i++)
            {
                ChunkNode node = _nodes[_current[i]];
                node.Root.transform.localPosition = _origin.ChunkToUnity(node.Coord);
                ApplyRowOrders(node);
                if (node.GroundDirty)
                {
                    BuildGround(node); // сосед пришёл позже (сеть): край чанка собирается заново с его тайлами
                }
            }
        }

        /// <summary>Выбросить всё окно в пул (телепорт персонажа в smoke).</summary>
        public void Clear()
        {
            for (int i = 0; i < _count; i++)
            {
                Release(_nodes[_current[i]]);
            }

            _nodes.Clear();
            _count = 0;
        }

        /// <summary>Клетка Tilemap узла для тайла чанка (lx, ly): Y ядра вниз, Y Unity вверх.</summary>
        public static Vector3Int CellOf(int lx, int ly) => new Vector3Int(lx, -ly - 1, 0);

        ChunkNode CreateNode()
        {
            var root = new GameObject("Chunk");
            root.transform.SetParent(_grid, false);
            var node = new ChunkNode
            {
                Root = root,
                Floor = AddTilemap(root.transform, "Floor", FloorOrder, TilemapRenderer.Mode.Chunk),
                FloorCells = FloorCellsLook.Create(root.transform, FloorOrder),
                // Объекты сортируются по одному с персонажем по оси Y (перспектива с наклоном, 00 5.1).
                Objects = AddTilemap(root.transform, "Objects", ObjectOrder, TilemapRenderer.Mode.Individual),
                Ground = GroundMesh.Create(),
            };
            var ground = new GameObject("Ground");
            ground.transform.SetParent(root.transform, false);
            ground.AddComponent<MeshFilter>().sharedMesh = node.Ground;
            var mr = ground.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _groundMaterial;
            mr.enabled = _groundMaterial != null;
            mr.sortingOrder = GroundOrder;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;

            // Тени чанка: отдельный слой, его видит только камера теней (WorldFx).
            node.Shadows = ObjectMesh.Create("Shadows");
            var shadows = new GameObject("Shadows") { layer = WorldFx.ShadowLayer };
            shadows.transform.SetParent(root.transform, false);
            shadows.AddComponent<MeshFilter>().sharedMesh = node.Shadows;
            node.ShadowRenderer = shadows.AddComponent<MeshRenderer>();
            Plain(node.ShadowRenderer, ObjectAtlas.ShadowMaterial);
            node.ShadowRenderer.enabled = false;
            node.FireShadows = ObjectMesh.Create("FireShadows");
            var fire = new GameObject("FireShadows") { layer = WorldFx.ShadowLayer };
            fire.transform.SetParent(root.transform, false);
            fire.AddComponent<MeshFilter>().sharedMesh = node.FireShadows;
            node.FireRenderer = fire.AddComponent<MeshRenderer>();
            Plain(node.FireRenderer, ObjectAtlas.ShadowMaterial);
            node.FireRenderer.enabled = false;
            node.Grass = _grass.CreateNode(root.transform);
            NodesCreated++;
            return node;
        }

        static void Plain(MeshRenderer mr, Material? material)
        {
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        }

        // Меш и отрисовщик строки объектов — при первом объекте в строке; дальше живут вместе с узлом (и в пуле).
        Mesh RowMesh(ChunkNode node, int ly)
        {
            Mesh? mesh = node.RowMeshes[ly];
            if (mesh != null)
            {
                return mesh;
            }

            mesh = ObjectMesh.Create("Objects");
            var go = new GameObject("Row " + ly);
            go.transform.SetParent(node.Root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            Plain(mr, ObjectAtlas.Material);
            mr.enabled = false;
            node.RowMeshes[ly] = mesh;
            node.Rows[ly] = mr;
            node.OrderedY = float.NaN; // новой строке нужен порядок
            return mesh;
        }

        // C-049: то же для «низкой полосы» строки (костёр) — отдельный меш перед существом той же четверти.
        Mesh LowRowMesh(ChunkNode node, int ly)
        {
            Mesh? mesh = node.LowRowMeshes[ly];
            if (mesh != null)
            {
                return mesh;
            }

            mesh = ObjectMesh.Create("Objects low");
            var go = new GameObject("Row low " + ly);
            go.transform.SetParent(node.Root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            Plain(mr, ObjectAtlas.Material);
            mr.enabled = false;
            node.LowRowMeshes[ly] = mesh;
            node.LowRows[ly] = mr;
            node.OrderedY = float.NaN; // новой строке нужен порядок
            return mesh;
        }

        // Порядок отрисовки строк — по высоте их опоры на экране; пересчёт, только когда узел сдвинулся.
        void ApplyRowOrders(ChunkNode node)
        {
            float y = node.Root.transform.localPosition.y;
            if (y == node.OrderedY)
            {
                return;
            }

            node.OrderedY = y;
            for (int ly = 0; ly < Size; ly++)
            {
                MeshRenderer? mr = node.Rows[ly];
                if (mr != null)
                {
                    mr.sortingOrder = RowOrderAt(y - ly - 1f + ObjectTable.BaseLift);
                }

                MeshRenderer? low = node.LowRows[ly]; // C-049: низкая полоса — перед существом той же четверти
                if (low != null)
                {
                    low.sortingOrder = LowRowOrderAt(y - ly - 1f + ObjectTable.BaseLift);
                }
            }
        }

        // Меши строк, где сменился объект или пол под ним, и меш теней чанка — в том же вызове, что и правка.
        void BuildObjects(ChunkNode node, Chunk? chunk, bool all)
        {
            _objects.InvalidateBlocks(); // C-016: ярусы массива — по скале этого чанка и соседей на сейчас
            bool any = false;
            for (int ly = 0; ly < Size; ly++)
            {
                if (!all && !_rowDirty[ly])
                {
                    continue;
                }

                any = true;
                BuildRow(node, chunk, ly);
            }

            if (!any)
            {
                return;
            }

            int shadows = _objects.BuildShadows(chunk, node.Coord, node.Shadows);
            node.ShadowRenderer.enabled = shadows > 0 && ObjectAtlas.ShadowMaterial != null;
            ApplyRowOrders(node);
        }

        void BuildRow(ChunkNode node, Chunk? chunk, int ly)
        {
            if (node.RowMeshes[ly] == null && (chunk == null || !RowHasObjects(chunk, ly)))
            {
                node.RowCounts[ly] = 0;
                node.RowBlocks[ly] = false;
                return; // пустая строка без отрисовщика — нечего собирать
            }

            int n = _objects.BuildRow(chunk, node.Coord, ly, RowMesh(node, ly));
            node.RowCounts[ly] = n;
            node.RowBlocks[ly] = n > 0 && _objects.RowHadBlocks;
            node.Rows[ly]!.enabled = n > 0 && ObjectAtlas.Material != null && !WorldFx.Off("norows");

            // C-049: «низкая полоса» (костёр) строки — своим мешом, перед существом той же четверти.
            int low = _objects.BuildRow(chunk, node.Coord, ly, LowRowMesh(node, ly), lowPass: true);
            node.LowRowCounts[ly] = low;
            node.LowRows[ly]!.enabled = low > 0 && ObjectAtlas.Material != null && !WorldFx.Off("norows");
        }

        // Пришёл чанк-сосед (dx, dy от near): строки near с блоками у общего края собрать заново — контур, лицо и ярус
        // обрыва зависят от тайлов за краем (ярус — до ObjectMesh.BlockReach тайлов); тень блока — по ярусу.
        void RebuildEdgeBlocks(ChunkNode near, int dx, int dy)
        {
            Chunk? chunk = _world.FindChunk(near.Coord);
            if (chunk == null)
            {
                return;
            }

            _objects.InvalidateBlocks();
            const int reach = ObjectMesh.BlockReach;
            int from = dy > 0 ? Size - reach : 0, to = dy < 0 ? reach - 1 : Size - 1;
            bool any = false;
            for (int ly = from; ly <= to; ly++)
            {
                if (!near.RowBlocks[ly])
                {
                    continue;
                }

                if (dx != 0 && !BlockNearEdge(near, ly, dx, reach))
                {
                    continue; // у бокового края в этой строке блока нет
                }

                BuildRow(near, chunk, ly);
                any = true;
            }

            if (any)
            {
                int shadows = _objects.BuildShadows(chunk, near.Coord, near.Shadows);
                near.ShadowRenderer.enabled = shadows > 0 && ObjectAtlas.ShadowMaterial != null;
                near.OrderedY = float.NaN;
                ApplyRowOrders(near);
            }
        }

        // В строке ly чанка есть блок не дальше reach тайлов от бокового края (dx > 0 — восточного).
        static bool BlockNearEdge(ChunkNode node, int ly, int dx, int reach)
        {
            for (int k = 0; k < reach; k++)
            {
                if (ObjectTable.IsBlock(node.Objs[Chunk.IndexOf(dx > 0 ? Size - 1 - k : k, ly)]))
                {
                    return true;
                }
            }

            return false;
        }

        static bool RowHasObjects(Chunk chunk, int ly)
        {
            for (int lx = 0; lx < Size; lx++)
            {
                int index = Chunk.IndexOf(lx, ly);
                ushort obj = chunk.GetObject(index);
                if (BuildCells.IsCellBuilding(obj) || ObjectTable.Find(obj, chunk.GetFloor(index)) != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Пучки травы (E1-11) — до отрисовки кадра: полосы с правкой тайла собираются все (правка видна в тот же кадр),
        /// полосы чанков, только что вошедших в окно (далеко за краем экрана), — не больше <paramref name="freshBudget"/>
        /// за вызов: вход столбца чанков не даёт длинного кадра.
        /// </summary>
        public void FlushGrass(int freshBudget)
        {
            for (int i = 0; i < _count; i++)
            {
                ChunkNode node = _nodes[_current[i]];
                GrassLayer.Node g = node.Grass;
                if (g.Dirty == 0 || (g.Fresh && freshBudget <= 0))
                {
                    continue;
                }

                Chunk? chunk = _world.FindChunk(node.Coord);
                for (int b = 0; b < GrassLayer.Bands; b++)
                {
                    if ((g.Dirty & (1 << b)) == 0)
                    {
                        continue;
                    }

                    if (g.Fresh && freshBudget-- <= 0)
                    {
                        break;
                    }

                    int was = g.Counts[b];
                    g.Counts[b] = _grass.Build(g, chunk, node.Coord, b);
                    GrassTufts += g.Counts[b] - was;
                    g.Dirty &= ~(1 << b);
                }

                if (g.Dirty == 0)
                {
                    g.Fresh = false;
                }
            }
        }

        /// <summary>
        /// C-017: тени от огня — меш чанка пересобирается, когда сменилось окно 3 × 3 вокруг него (поставили или убрали
        /// факел, предмет, стену; пришёл чанк). Не больше <paramref name="budget"/> чанков за вызов.
        /// </summary>
        public void FlushFire(int budget)
        {
            if (ObjectAtlas.ShadowMaterial == null)
            {
                return;
            }

            for (int i = 0; i < _count && budget > 0; i++)
            {
                ChunkNode node = _nodes[_current[i]];
                if (!node.HasFloors)
                {
                    continue;
                }

                long stamp = CellWindow.StampOf(_world, node.Coord);
                if (stamp == node.FireStamp)
                {
                    continue;
                }

                node.FireStamp = stamp;
                budget--;
                int n = _objects.BuildFireShadows(_world.FindChunk(node.Coord), node.Coord, node.FireShadows);
                node.FireRenderer.enabled = n > 0;
                FireBuilds++;
            }
        }

        /// <summary>Мешей теней огня собрано (замер).</summary>
        public int FireBuilds { get; private set; }

        /// <summary>Пучков травы в собранных полосах (замер) и полос собрано за всё время.</summary>
        public int GrassTufts { get; private set; }
        public int GrassBuilds => _grass.Builds;

        void Release(ChunkNode node)
        {
            node.Root.SetActive(false);
            node.HasFloors = false;
            node.GroundDirty = false;
            node.FireStamp = 0;
            node.FireRenderer.enabled = false;
            _pool.Push(node);
        }

        void BuildGround(ChunkNode node)
        {
            node.HaloIncomplete = !_ground.Build(node.Coord, node.Ground);
            node.GroundDirty = false;
        }

        /// <summary>
        /// Постройки-ячеек у границы (E2-12, E2-17): вид стены зависит от ячеек за границей тайла — у соседних чанков
        /// пересобрать строки, где у тайла-постройки в ряду, прилегающем к чанку <paramref name="c"/>, сменился ключ вида.
        /// </summary>
        void RefreshSeams(ChunkCoord c)
        {
            for (int k = 0; k < 4; k++)
            {
                int dx = k == 0 ? 1 : k == 1 ? -1 : 0, dy = k == 2 ? 1 : k == 3 ? -1 : 0;
                if (!TryNeighbour(c, dx, dy, out ChunkNode n) || !n.HasFloors)
                {
                    continue;
                }

                Chunk? chunk = _world.FindChunk(n.Coord);
                if (chunk == null)
                {
                    continue;
                }

                TileCoord corner = Torus.CornerOf(n.Coord);
                Array.Clear(_rowDirty, 0, Size);
                bool any = false;
                for (int i = 0; i < Size; i++)
                {
                    int lx = dx == 1 ? 0 : dx == -1 ? Size - 1 : i;
                    int ly = dy == 1 ? 0 : dy == -1 ? Size - 1 : i;
                    int index = Chunk.IndexOf(lx, ly);
                    long key = BuildCells.IsCellBuilding(chunk.GetObject(index)) ? _objects.CellKey(corner.X + lx, corner.Y + ly) : 0;
                    if (key != n.CellKeys[index])
                    {
                        n.CellKeys[index] = key;
                        _rowDirty[ly] = true;
                        any = true;
                    }
                }

                if (any)
                {
                    BuildObjects(n, chunk, all: false);
                }
            }
        }

        bool TryNeighbour(ChunkCoord c, int dx, int dy, out ChunkNode node) =>
            _nodes.TryGetValue(Torus.Wrap(_map, new ChunkCoord(c.X + dx, c.Y + dy)), out node!);

        static Tilemap AddTilemap(Transform parent, string name, int order, TilemapRenderer.Mode mode)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var tm = go.AddComponent<Tilemap>();
            var r = go.AddComponent<TilemapRenderer>();
            r.sortingOrder = order;
            r.mode = mode;
            return tm;
        }

        void Fill(ChunkNode node, ChunkCoord c)
        {
            Chunk? chunk = _world.FindChunk(c);
            bool fresh = !node.HasFloors; // узел из пула: копии полов нет, меш собирается целиком
            bool built = false;
            int minX = Size, minY = Size, maxX = -1, maxY = -1; // рамка тайлов, где сменился пол
            byte[] shown = node.Floors;
            ushort[] objs = node.Objs;
            for (int ly = 0; ly < Size; ly++)
            {
                _rowDirty[ly] = false;
                int row = (Size - 1 - ly) * Size; // клетка (lx, −ly − 1) в блоке [−32, 0) по Y
                for (int lx = 0; lx < Size; lx++)
                {
                    int index = Chunk.IndexOf(lx, ly);
                    byte floor = chunk == null ? FloorId.Grass : chunk.GetFloor(index);
                    ushort obj = chunk == null ? ObjectId.Empty : chunk.GetObject(index);
                    byte part = FloorCellsLook.PartialMask(chunk, index, floor); // E2-15: пол частью тайла — четвертями
                    if (FloorCellsLook.Sync(node.FloorCells, node.FloorMasks, node.FloorMaskFloors, lx, ly, floor, part) && !fresh)
                    {
                        // Пол стал частью тайла или целым: земля под ним — другая (природная под частью) — меш земли заново.
                        if (lx < minX) minX = lx;
                        if (lx > maxX) maxX = lx;
                        if (ly < minY) minY = ly;
                        if (ly > maxY) maxY = ly;
                    }

                    TileBase? floorTile = part != 0 ? null : FloorTile(floor);
                    built |= floorTile != null;
                    _floorBuf[row + lx] = floorTile;
                    if (objs[index] != obj)
                    {
                        if (!fresh)
                        {
                            GrassLayer.MarkRow(node.Grass, ly); // под объектом травы нет; убрали объект — выросла
                            if (obj == ObjectId.Empty)
                            {
                                TileCoord corner = Torus.CornerOf(c);
                                LifeFx.TreeGone(corner.X + lx, corner.Y + ly, objs[index], shown[index]); // срубили — облачко листьев
                            }
                        }

                        objs[index] = obj;
                        _rowDirty[ly] = true;
                    }

                    if (shown[index] != floor)
                    {
                        if (!fresh)
                        {
                            GrassLayer.MarkRow(node.Grass, ly);
                        }

                        _rowDirty[ly] |= obj != ObjectId.Empty; // вид дерева зависит от земли под ним
                        shown[index] = floor;
                        if (lx < minX) minX = lx;
                        if (lx > maxX) maxX = lx;
                        if (ly < minY) minY = ly;
                        if (ly > maxY) maxY = ly;
                    }

                    // E2-17: стена, забор, дверь — в меше строки; вид зависит от соседних ячеек и двери — по ключу.
                    long key = 0;
                    if (BuildCells.IsCellBuilding(obj))
                    {
                        TileCoord corner = Torus.CornerOf(c);
                        key = _objects.CellKey(corner.X + lx, corner.Y + ly);
                    }

                    if (node.CellKeys[index] != key)
                    {
                        node.CellKeys[index] = key;
                        _rowDirty[ly] = true;
                    }

                    _objBuf[row + lx] = ObjectTile(obj);
                }
            }

            if (built || node.HasBuiltFloor)
            {
                node.Floor.SetTilesBlock(Block, _floorBuf!); // плитки построенных полов; чанк без них Tilemap не трогает
                node.HasBuiltFloor = built;
            }

            node.Objects.SetTilesBlock(Block, _objBuf!);
            node.HasFloors = true;
            Fills++;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            BuildObjects(node, chunk, all: fresh);
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (fresh)
            {
                FreshObjectTicks += dt;
                FreshObjectFills++;
            }

            if (fresh)
            {
                GrassLayer.MarkFresh(node.Grass);
                BuildGround(node);
                RefreshSeams(c); // E2-17: стены соседних чанков у общего края видят ячейки этого
                // Соседи, собранные без этого чанка, пересоберутся в PlaceNodes этого кадра.
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if ((dx != 0 || dy != 0) && TryNeighbour(c, dx, dy, out ChunkNode near))
                        {
                            near.GroundDirty |= near.HaloIncomplete;
                            RebuildEdgeBlocks(near, -dx, -dy); // новый чанк от соседа — в сторону (−dx, −dy)
                        }
                    }
                }

                return;
            }

            if (maxX < 0)
            {
                return; // пол не менялся (правка объекта) — меш земли прежний
            }

            // Правка пола: свой меш и меши соседей, до края которых от правки меньше Halo тайлов, — сразу, в этом же кадре.
            BuildGround(node);
            int fromX = minX < GroundMesh.Halo ? -1 : 0, toX = maxX >= Size - GroundMesh.Halo ? 1 : 0;
            int fromY = minY < GroundMesh.Halo ? -1 : 0, toY = maxY >= Size - GroundMesh.Halo ? 1 : 0;
            for (int dy = fromY; dy <= toY; dy++)
            {
                for (int dx = fromX; dx <= toX; dx++)
                {
                    if ((dx != 0 || dy != 0) && TryNeighbour(c, dx, dy, out ChunkNode near))
                    {
                        BuildGround(near);
                    }
                }
            }
        }
    }
}