using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.Pathing
{
    /// <summary>
    /// Поиск пути врага (docs/02, 9.2) — шип 0-023. A* на сетке тайлов в окне 48 × 48 вокруг врага:
    /// 8 соседей, без срезания углов у непроходимых; прямой шаг 10, диагональный 14, замедляющий пол ×1,5;
    /// эвристика — октильная по вектору <see cref="Torus.Delta(in MapShape, TileCoord, TileCoord)"/>.
    /// <para>
    /// Окно — локальная сетка: тайл (lx, ly) окна — тайл мира Wrap(старт − 24 + (lx, ly)), поэтому окно,
    /// пересекающее шов или угол тора, — обычное. Цель переводится в координаты окна один раз через
    /// <see cref="Torus.Delta(in MapShape, TileCoord, TileCoord)"/>; дальше арифметика только в окне.
    /// На плоской карте (данж) тайлы за краем непроходимы.
    /// </para>
    /// <para>
    /// Порядок поиска фиксирован: куча по ключу (f, h, индекс тайла окна), соседи — в порядке
    /// <see cref="NeighborDx"/>; при равных оценках раскрывается тайл с меньшим h, затем с меньшим индексом.
    /// </para>
    /// <para>
    /// Цель в окне — сначала заливка от цели (не больше <see cref="EnclosureFillLimit"/> тайлов): область
    /// замкнута в окне и старт снаружи — A* не запускается, итог <see cref="PathStatus.Enclosed"/> (9.2: «сразу
    /// упёрся»). Заливка — замена записей областей `05`, которых в вехе 0 нет.
    /// </para>
    /// Без выделений после конструктора: все массивы — на окно, метки поиска вместо очистки.
    /// </summary>
    public sealed class PathFinder
    {
        /// <summary>Сторона окна поиска, тайлов (9.2).</summary>
        public const int WindowSize = 48;

        /// <summary>Старт в окне — тайл (24, 24): окно — от −24 до +23 тайлов по каждой оси.</summary>
        public const int WindowHalf = WindowSize / 2;

        /// <summary>Тайлов в окне.</summary>
        public const int CellCount = WindowSize * WindowSize;

        /// <summary>Предел раскрытых узлов на поиск (9.2).</summary>
        public const int DefaultMaxExpanded = 1500;

        /// <summary>Предел заливки «цель в замкнутой области» по умолчанию, тайлов (решение шипа).</summary>
        public const int DefaultEnclosureFillLimit = 256;

        /// <summary>Сдвиги соседей по X: сначала 4 прямых, потом 4 диагонали.</summary>
        public static ReadOnlySpan<sbyte> NeighborDx => new sbyte[] { 1, -1, 0, 0, 1, -1, 1, -1 };

        /// <summary>Сдвиги соседей по Y.</summary>
        public static ReadOnlySpan<sbyte> NeighborDy => new sbyte[] { 0, 0, 1, -1, 1, 1, -1, -1 };

        private const byte Unknown = 0xFF;
        private const byte FlagOpen = 1;
        private const byte FlagClosed = 2;
        private const byte FlagFilled = 4;
        private const int StartIndex = WindowHalf * WindowSize + WindowHalf;
        private const int IndexBits = 12;
        private const int HBits = 21;
        private const long HMax = (1L << HBits) - 1;

        private readonly int[] _stamp = new int[CellCount];
        private readonly byte[] _cost = new byte[CellCount];
        private readonly byte[] _flags = new byte[CellCount];
        private readonly int[] _g = new int[CellCount];
        private readonly short[] _parent = new short[CellCount];
        private readonly long[] _heap = new long[CellCount * 8 + 1];
        private readonly short[] _fill = new short[CellCount];
        private int _heapCount;
        private int _search;

        // Поиск в работе.
        private IPathTerrain _terrain = null!;
        private MapShape _map;
        private PathMode _mode;
        private int _originX;
        private int _originY;
        private int _goalX;
        private int _goalY;

        public PathFinder(int maxExpanded = DefaultMaxExpanded, int enclosureFillLimit = DefaultEnclosureFillLimit)
        {
            if (maxExpanded < 1 || maxExpanded > CellCount)
            {
                throw new ArgumentOutOfRangeException(nameof(maxExpanded), maxExpanded, $"Предел узлов — от 1 до {CellCount}.");
            }

            if (enclosureFillLimit < 0 || enclosureFillLimit > CellCount)
            {
                throw new ArgumentOutOfRangeException(nameof(enclosureFillLimit), enclosureFillLimit, $"Предел заливки — от 0 до {CellCount}.");
            }

            MaxExpanded = maxExpanded;
            EnclosureFillLimit = enclosureFillLimit;
        }

        /// <summary>Предел раскрытых узлов на поиск.</summary>
        public int MaxExpanded { get; }

        /// <summary>Предел заливки от цели; 0 — проверки «цель в замкнутой области» нет.</summary>
        public int EnclosureFillLimit { get; }

        /// <summary>Запусков A* всего.</summary>
        public long AStarRuns { get; private set; }

        /// <summary>Поисков всего.</summary>
        public long Searches { get; private set; }

        /// <summary>Раскрыто узлов A* всего.</summary>
        public long TotalExpanded { get; private set; }

        /// <summary>Тайлов залито проверкой замкнутой области всего.</summary>
        public long TotalFilled { get; private set; }

        /// <summary>Тайлов спрошено у карты всего.</summary>
        public long TotalTerrainReads { get; private set; }

        /// <summary>
        /// Найти путь от <paramref name="start"/> к <paramref name="goal"/> и записать в <paramref name="result"/>.
        /// Возвращает итог (<see cref="PathBuffer.Status"/>). Старт считается проходимым всегда.
        /// </summary>
        public PathStatus Find(IPathTerrain terrain, TileCoord start, TileCoord goal, PathMode mode, PathBuffer result)
        {
            if (terrain == null)
            {
                throw new ArgumentNullException(nameof(terrain));
            }

            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            _terrain = terrain;
            terrain.BeginSearch();
            _map = terrain.Shape;
            _mode = mode;
            start = Torus.Wrap(_map, start);
            goal = Torus.Wrap(_map, goal);
            NextSearch();
            Searches++;
            result.Clear();

            _originX = start.X - WindowHalf;
            _originY = start.Y - WindowHalf;
            Int2 d = Torus.Delta(_map, start, goal);
            _goalX = WindowHalf + d.X;
            _goalY = WindowHalf + d.Y;
            bool goalInside = InWindow(_goalX, _goalY);

            Touch(StartIndex);
            _cost[StartIndex] = PathCost.Normal;

            if (d.X == 0 && d.Y == 0)
            {
                result.Status = PathStatus.Found;
                result.End = start;
                return PathStatus.Found;
            }

            int goalIndex = goalInside ? _goalY * WindowSize + _goalX : -1;
            if (goalInside && GoalEnclosed(goalIndex))
            {
                result.Status = PathStatus.Enclosed;
                result.End = start;
                return PathStatus.Enclosed;
            }

            return AStar(goalIndex, result);
        }

        // ---------- A* ----------

        private PathStatus AStar(int goalIndex, PathBuffer result)
        {
            AStarRuns++;
            _heapCount = 0;
            _g[StartIndex] = 0;
            _flags[StartIndex] = FlagOpen;
            Push(StartIndex, 0, Heuristic(StartIndex));

            int best = StartIndex;
            long bestDist2 = Dist2ToGoal(StartIndex);
            int bestG = 0;
            int expanded = 0;
            bool edgeExit = goalIndex < 0;
            PathStatus status = PathStatus.Partial;
            int end = -1;

            while (_heapCount > 0)
            {
                int index = Pop();
                if ((_flags[index] & FlagClosed) != 0)
                {
                    continue;
                }

                _flags[index] |= FlagClosed;
                expanded++;
                int g = _g[index];

                if (index == goalIndex)
                {
                    status = PathStatus.Found;
                    end = index;
                    break;
                }

                int x = index % WindowSize;
                int y = index / WindowSize;
                if (edgeExit && (x == 0 || y == 0 || x == WindowSize - 1 || y == WindowSize - 1))
                {
                    status = PathStatus.WindowEdge;
                    end = index;
                    break;
                }

                long dist2 = Dist2ToGoal(index);
                if (dist2 < bestDist2 || (dist2 == bestDist2 && g < bestG))
                {
                    best = index;
                    bestDist2 = dist2;
                    bestG = g;
                }

                if (expanded >= MaxExpanded)
                {
                    break;
                }

                for (int k = 0; k < 8; k++)
                {
                    int n = Step(x, y, k);
                    if (n < 0 || (_flags[n] & FlagClosed) != 0)
                    {
                        continue;
                    }

                    int step = PathCost.Step(k < 4 ? PathCost.Straight : PathCost.Diagonal, _cost[n]);
                    int ng = g + step;
                    if ((_flags[n] & FlagOpen) == 0 || ng < _g[n])
                    {
                        _g[n] = ng;
                        _parent[n] = (short)index;
                        _flags[n] |= FlagOpen;
                        Push(n, ng, Heuristic(n));
                    }
                }
            }

            TotalExpanded += expanded;
            if (end < 0)
            {
                end = best;
            }

            WritePath(end, result);
            result.Status = status;
            result.Expanded = expanded;
            return status;
        }

        private void WritePath(int end, PathBuffer result)
        {
            int length = 0;
            for (int i = end; i != StartIndex; i = _parent[i])
            {
                length++;
            }

            int k = length;
            for (int i = end; i != StartIndex; i = _parent[i])
            {
                result.Steps[--k] = WorldTile(i);
            }

            result.Length = length;
            result.Cost = _g[end];
            result.End = WorldTile(end);
        }

        // ---------- Цель в замкнутой области ----------

        /// <summary>
        /// Заливка от цели по тем же шагам, что у A*. Дошла до старта или до края окна, или превысила предел —
        /// не замкнута (A* решает сам); исчерпалась внутри окна — замкнута.
        /// </summary>
        private bool GoalEnclosed(int goalIndex)
        {
            if (EnclosureFillLimit == 0 || CostAt(goalIndex) == PathCost.Blocked)
            {
                return false;
            }

            int head = 0;
            int tail = 0;
            _fill[tail++] = (short)goalIndex;
            _flags[goalIndex] |= FlagFilled;
            bool enclosed = true;
            while (head < tail)
            {
                int index = _fill[head++];
                int x = index % WindowSize;
                int y = index / WindowSize;
                if (x == 0 || y == 0 || x == WindowSize - 1 || y == WindowSize - 1)
                {
                    enclosed = false;
                    break;
                }

                for (int k = 0; k < 8 && enclosed; k++)
                {
                    int n = Step(x, y, k);
                    if (n < 0 || (_flags[n] & FlagFilled) != 0)
                    {
                        continue;
                    }

                    if (n == StartIndex || tail >= EnclosureFillLimit)
                    {
                        enclosed = false;
                        break;
                    }

                    _flags[n] |= FlagFilled;
                    _fill[tail++] = (short)n;
                }

                if (!enclosed)
                {
                    break;
                }
            }

            TotalFilled += tail;
            return enclosed;
        }

        // ---------- Окно ----------

        /// <summary>
        /// Шаг из (x, y) к соседу k: индекс соседа или −1 (вне окна, непроходим, срезание угла).
        /// Диагональ — только если оба прямых соседа на пути проходимы.
        /// </summary>
        private int Step(int x, int y, int k)
        {
            int dx = NeighborDx[k];
            int dy = NeighborDy[k];
            int nx = x + dx;
            int ny = y + dy;
            if (!InWindow(nx, ny))
            {
                return -1;
            }

            int n = ny * WindowSize + nx;
            if (CostAt(n) == PathCost.Blocked)
            {
                return -1;
            }

            if (k >= 4 && (CostAt(y * WindowSize + nx) == PathCost.Blocked || CostAt(ny * WindowSize + x) == PathCost.Blocked))
            {
                return -1;
            }

            return n;
        }

        private static bool InWindow(int x, int y) => (uint)x < WindowSize && (uint)y < WindowSize;

        private byte CostAt(int index)
        {
            Touch(index);
            byte cost = _cost[index];
            if (cost != Unknown)
            {
                return cost;
            }

            TotalTerrainReads++;
            int wx = _originX + index % WindowSize;
            int wy = _originY + index / WindowSize;
            if (!_map.Wraps && ((uint)wx >= (uint)_map.Width || (uint)wy >= (uint)_map.Height))
            {
                cost = PathCost.Blocked;
            }
            else
            {
                cost = _terrain.CostOf(Torus.Wrap(_map, new TileCoord(wx, wy)), _mode);
            }

            _cost[index] = cost;
            return cost;
        }

        private TileCoord WorldTile(int index) =>
            Torus.Wrap(_map, new TileCoord(_originX + index % WindowSize, _originY + index / WindowSize));

        private int Heuristic(int index) =>
            PathCost.Octile(_goalX - index % WindowSize, _goalY - index / WindowSize);

        private long Dist2ToGoal(int index) => new Int2(_goalX - index % WindowSize, _goalY - index / WindowSize).LengthSquared;

        private void Touch(int index)
        {
            if (_stamp[index] == _search)
            {
                return;
            }

            _stamp[index] = _search;
            _cost[index] = Unknown;
            _flags[index] = 0;
        }

        private void NextSearch()
        {
            if (_search == int.MaxValue)
            {
                Array.Clear(_stamp, 0, _stamp.Length);
                _search = 0;
            }

            _search++;
        }

        // ---------- Куча на массиве: ключ (f, h, индекс) ----------

        private void Push(int index, int g, int h)
        {
            long hk = h > HMax ? HMax : h;
            long key = ((long)(g + h) << (HBits + IndexBits)) | (hk << IndexBits) | (long)index;
            int i = _heapCount++;
            long[] heap = _heap;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (heap[parent] <= key)
                {
                    break;
                }

                heap[i] = heap[parent];
                i = parent;
            }

            heap[i] = key;
        }

        private int Pop()
        {
            long[] heap = _heap;
            long top = heap[0];
            long last = heap[--_heapCount];
            int count = _heapCount;
            int i = 0;
            while (true)
            {
                int child = 2 * i + 1;
                if (child >= count)
                {
                    break;
                }

                if (child + 1 < count && heap[child + 1] < heap[child])
                {
                    child++;
                }

                if (heap[child] >= last)
                {
                    break;
                }

                heap[i] = heap[child];
                i = child;
            }

            if (count > 0)
            {
                heap[i] = last;
            }

            return (int)(top & ((1L << IndexBits) - 1));
        }
    }
}
