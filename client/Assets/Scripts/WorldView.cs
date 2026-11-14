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

        public sealed class ChunkNode
        {
            public ChunkCoord Coord;
            public GameObject Root = null!;
            public Tilemap Floor = null!;
            public Tilemap Objects = null!;

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
        }

        readonly WorldModel _world;
        readonly MapShape _map;
        readonly FloatingOrigin _origin;
        readonly Transform _grid;
        readonly TileBase _meadow, _stone, _tree, _rock;
        readonly GroundMesh _ground;
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
            _groundMaterial = GroundAtlas.Material;
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

        /// <summary>Мешей земли собрано за всё время (вход чанка в окно и правки пола).</summary>
        public int GroundBuilds => _ground.Builds;

        /// <summary>
        /// Плитка Tilemap пола по id ядра (E1-4): только построенные полы (<see cref="GroundKind.Built"/>) — каменной плитой
        /// поверх земли; природные полы и воду рисует меш земли (<see cref="GroundTable"/>) — плитки нет.
        /// </summary>
        public TileBase? FloorTile(byte floor) => GroundTable.Of(floor).Kind == GroundKind.Built ? _stone : null;

        /// <summary>Меш земли узла собран по этому полу тайла (lx, ly) — «правка видна в тот же кадр» для проверок.</summary>
        public static bool GroundShows(ChunkNode node, int lx, int ly, byte floor) =>
            node.HasFloors && !node.GroundDirty && node.Floors[Chunk.IndexOf(lx, ly)] == floor;

        /// <summary>Тайл объекта по сырому id ядра; пусто и неизвестное — null.</summary>
        public TileBase? ObjectTile(ushort obj)
        {
            if (obj == ObjectId.Empty || ObjectId.IsPart(obj))
            {
                return null; // часть многотайлового объекта рисует якорь
            }

            switch (ObjectId.IdOf(obj))
            {
                case ObjectId.Tree: return _tree;
                case ObjectId.Rock: return _rock;
                // Объекты генератора v1 (1-017) до своих спрайтов (1-024): невидимая преграда хуже похожей.
                case ObjectId.Bush: return _tree;
                case ObjectId.Granite: return _rock;
                case ObjectId.Ore: return _rock;
                // Стена игрока (0-028, правки): камня-спрайта нет (1-024) — рисуем камнем пола, стена видна (1-022, шаг 4).
                case ObjectId.StoneWall: return _stone;
                // Ковёр (1-011, бесплатная постройка): своего спрайта нет (1-024) — рисуем лугом, проба 1-022 видит клетку.
                case ObjectId.Carpet: return _meadow;
                // Дверь (1-012): закрытая — камнем (видная преграда), открытая — светлее (лугом); свои спрайты — 1-024.
                case ObjectId.Door: return _stone;
                // Факел и костёр (E1-8: свет источников для тона суток): своего спрайта нет (1-024) — рисуем валуном,
                // чтобы в кадре было видно, от чего круг света.
                case ObjectId.Torch: return _rock;
                case ObjectId.Campfire: return _rock;
                default: return null;
            }
        }

        /// <summary>Тайл двери по состоянию (1-012): открытая — светлее (луг), закрытая — как <see cref="ObjectTile"/>.</summary>
        public TileBase DoorTile(bool open) => open ? _meadow : _stone;

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
            NodesCreated++;
            return node;
        }

        void Release(ChunkNode node)
        {
            node.Root.SetActive(false);
            node.HasFloors = false;
            node.GroundDirty = false;
            _pool.Push(node);
        }

        void BuildGround(ChunkNode node)
        {
            node.HaloIncomplete = !_ground.Build(node.Coord, node.Ground);
            node.GroundDirty = false;
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
            for (int ly = 0; ly < Size; ly++)
            {
                int row = (Size - 1 - ly) * Size; // клетка (lx, −ly − 1) в блоке [−32, 0) по Y
                for (int lx = 0; lx < Size; lx++)
                {
                    int index = Chunk.IndexOf(lx, ly);
                    byte floor = chunk == null ? FloorId.Grass : chunk.GetFloor(index);
                    ushort obj = chunk == null ? ObjectId.Empty : chunk.GetObject(index);
                    TileBase? floorTile = FloorTile(floor);
                    built |= floorTile != null;
                    _floorBuf[row + lx] = floorTile;
                    if (shown[index] != floor)
                    {
                        shown[index] = floor;
                        if (lx < minX) minX = lx;
                        if (lx > maxX) maxX = lx;
                        if (ly < minY) minY = ly;
                        if (ly > maxY) maxY = ly;
                    }

                    TileBase? tile = ObjectTile(obj);
                    if (obj == ObjectId.Door && chunk != null && chunk.TryGetBuild(index, out BuildRecord rec))
                    {
                        tile = DoorTile(rec.IsDoorOpen); // 1-012: состояние двери видно (открытая светлее)
                    }

                    _objBuf[row + lx] = tile;
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

            if (fresh)
            {
                BuildGround(node);
                // Соседи, собранные без этого чанка, пересоберутся в PlaceNodes этого кадра.
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if ((dx != 0 || dy != 0) && TryNeighbour(c, dx, dy, out ChunkNode near) && near.HaloIncomplete)
                        {
                            near.GroundDirty = true;
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