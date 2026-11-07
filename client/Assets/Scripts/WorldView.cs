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
    /// <item>На чанк — узел с двумя <see cref="Tilemap"/> под общим <see cref="Grid"/>: пол и объекты; заливка каждого —
    /// один вызов <see cref="Tilemap.SetTilesBlock"/> на 32 × 32 клетки.</item>
    /// <item>Узлы вышедших чанков уходят в пул и переиспользуются для вошедших — новые <see cref="GameObject"/> создаются,
    /// только пока пул не набрал наибольшего окна.</item>
    /// <item>Место узла — <see cref="FloatingOrigin.ChunkToUnity"/>: Δ от якоря по тору, у шва — ближайшая копия.</item>
    /// </list>
    /// </summary>
    public sealed class WorldView
    {
        const int Size = Torus.ChunkSize;

        /// <summary>Порядок сортировки: пол ниже, объекты — вместе с персонажем по оси Y.</summary>
        public const int FloorOrder = 0;
        public const int ObjectOrder = 10;

        public sealed class ChunkNode
        {
            public ChunkCoord Coord;
            public GameObject Root = null!;
            public Tilemap Floor = null!;
            public Tilemap Objects = null!;
        }

        readonly WorldModel _world;
        readonly MapShape _map;
        readonly FloatingOrigin _origin;
        readonly Transform _grid;
        readonly TileBase _meadow, _stone, _tree, _rock;
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

        /// <summary>Тайл пола по id ядра (неизвестный пол — луг: заглушек других полов пока нет).</summary>
        public TileBase FloorTile(byte floor) => floor == FloorId.Stone ? _stone : _meadow;

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
                node.Root.SetActive(false);
                _pool.Push(node);
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
            node.Root.SetActive(false);
            _pool.Push(node);
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
            }
        }

        /// <summary>Выбросить всё окно в пул (телепорт персонажа в smoke).</summary>
        public void Clear()
        {
            for (int i = 0; i < _count; i++)
            {
                ChunkNode node = _nodes[_current[i]];
                node.Root.SetActive(false);
                _pool.Push(node);
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
            };
            NodesCreated++;
            return node;
        }

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
            for (int ly = 0; ly < Size; ly++)
            {
                int row = (Size - 1 - ly) * Size; // клетка (lx, −ly − 1) в блоке [−32, 0) по Y
                for (int lx = 0; lx < Size; lx++)
                {
                    int index = Chunk.IndexOf(lx, ly);
                    byte floor = chunk == null ? FloorId.Grass : chunk.GetFloor(index);
                    ushort obj = chunk == null ? ObjectId.Empty : chunk.GetObject(index);
                    _floorBuf[row + lx] = FloorTile(floor);
                    _objBuf[row + lx] = ObjectTile(obj);
                }
            }

            node.Floor.SetTilesBlock(Block, _floorBuf!);
            node.Objects.SetTilesBlock(Block, _objBuf!);
            Fills++;
        }
    }
}
