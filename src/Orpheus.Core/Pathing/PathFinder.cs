using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.Pathing
{
    /// <summary>
    /// Поиск пути врага (docs/02, 9.2) — шип 0-023, с E2-12b — на решётке точек через 0,5 тайла. A* в окне 48 × 48 тайлов
    /// вокруг врага. Точка решётки — центр тайла, середина ребра или угол тайла (4 на тайл, 96 × 96 в окне): место, где может
    /// стоять центр круга врага (радиус 0,4). Ячейки построек — 0,5 × 0,5 (E2-12), их стороны лежат на линиях решётки,
    /// поэтому круг 0,4 в точке задевает ровно 4 ячейки вокруг неё: точка свободна, если они все не твёрдые
    /// (<see cref="IPathTerrain.CellsOf"/>), — та же проходимость, что у движения (<c>Collision</c>, тот же
    /// <c>Simulation.IsBlocked</c>/<c>SolidCells</c>). Прямой полшаг между свободными точками круг проходит (его ячейки —
    /// ячейки концов); диагональ — только если обе прямые соседки свободны (без срезания углов). Проём шириной 1 тайл в любом
    /// месте сетки ячеек (и сдвинутый на полтайла) — линия свободных точек посередине; щель 0,5 — ни одной.
    /// <para>
    /// <b>Узлы.</b> Центр тайла — узел всегда; середина ребра и угол — только у тонких построек: если тайл, которого касается
    /// круг в точке, или его сосед (3 × 3) — с частью ячеек твёрдых (маска не 0 и не 15). Из центра — и мелкие полушаги, и
    /// целые шаги к центрам 8 соседних тайлов, если промежуточные точки свободны и середина шага не узел (иначе путь идёт
    /// полушагами через неё). Вдали от тонких стен граф — прежняя сетка тайлов (без срезания углов), узлов столько же; у тонких
    /// стен — решётка 0,5. Старт и цель — узлы всегда. Замер бюджета — карточка E2-12b.
    /// </para>
    /// <para>
    /// Стоимость: прямой полшаг 10, диагональный 14, в удвоенных единицах (<see cref="PathBuffer.Cost"/> — пополам: тайл
    /// по прямой 10, по диагонали 14, как до E2-12b); замедляющий пол — ×1,5 по тайлу, в котором лежит полшаг (меньший
    /// множитель концов). Эвристика — октильная по разности точек. В мире целых тайлов путь и стоимость — прежние.
    /// </para>
    /// <para>
    /// Окно — локальная решётка: точка (lx, ly) — точка мира старт − 48 + (lx, ly) (в полутайлах), свёрнутая по тору;
    /// тайлы читаются у карты один раз за поиск (кэш), не дальше 24 тайлов от старта. На плоской карте тайлы за краем твёрдые.
    /// </para>
    /// <para>
    /// Порядок поиска фиксирован: куча по ключу (f, h, индекс точки окна), соседи — полушаги в порядке <see cref="NeighborDx"/>,
    /// затем целые шаги в том же порядке; при равных оценках раскрывается точка с меньшим h, затем с меньшим индексом.
    /// </para>
    /// <para>
    /// Цель в окне — сначала заливка от цели (не больше <see cref="EnclosureFillLimit"/> центров тайлов — дом до 256 тайлов
    /// внутри): область замкнута в окне и старт снаружи — итог <see cref="PathStatus.Enclosed"/>, путь — к свободному узлу вне
    /// области, ближайшему к цели (слово Автора 08.11, 0-034: «к краю, который ближе всего к игроку»).
    /// </para>
    /// Без выделений после конструктора: все массивы — на окно, метки поиска вместо очистки.
    /// </summary>
    public sealed class PathFinder
    {
        /// <summary>Сторона окна поиска, тайлов (9.2).</summary>
        public const int WindowSize = 48;

        /// <summary>Половина окна, тайлов: окно — от −24 до +24 тайлов от старта.</summary>
        public const int WindowHalf = WindowSize / 2;

        /// <summary>Точек решётки на тайл по оси: шаг решётки 0,5 тайла.</summary>
        public const int PointsPerTile = 2;

        /// <summary>Шаг решётки в 1/256 тайла (128).</summary>
        public const int PointUnits = Torus.SubTilesPerTile / PointsPerTile;

        /// <summary>Сторона окна в точках решётки (96).</summary>
        public const int LatticeSize = WindowSize * PointsPerTile;

        /// <summary>Старт в окне — точка (48, 48).</summary>
        public const int LatticeHalf = LatticeSize / 2;

        /// <summary>Точек в окне.</summary>
        public const int PointCount = LatticeSize * LatticeSize;

        /// <summary>
        /// Предел раскрытых узлов (точек) на поиск по умолчанию (9.2: «не больше 1500 раскрытых узлов»; узел — точка решётки,
        /// замер бюджета — карточка E2-12b).
        /// </summary>
        public const int DefaultMaxExpanded = 1500;

        /// <summary>Наибольший допустимый предел узлов (и длина пути в <see cref="PathBuffer"/>).</summary>
        public const int MaxExpandedLimit = 4096;

        /// <summary>Предел заливки «цель в замкнутой области» по умолчанию, центров тайлов (решение шипа): дом до 256 тайлов внутри.</summary>
        public const int DefaultEnclosureFillLimit = 256;

        /// <summary>Сдвиги соседей по X: сначала 4 прямых, потом 4 диагонали.</summary>
        public static ReadOnlySpan<sbyte> NeighborDx => new sbyte[] { 1, -1, 0, 0, 1, -1, 1, -1 };

        /// <summary>Сдвиги соседей по Y.</summary>
        public static ReadOnlySpan<sbyte> NeighborDy => new sbyte[] { 0, 0, 1, -1, 1, 1, -1, -1 };

        private const byte Unknown = 0xFF;
        private const byte FlagOpen = 1;
        private const byte FlagClosed = 2;
        private const byte FlagFilled = 4;
        private const int StartIndex = LatticeHalf * LatticeSize + LatticeHalf;
        private const int IndexBits = 14;
        private const int HBits = 21;
        private const long HMax = (1L << HBits) - 1;
        private const int TileSpan = WindowSize + 2;
        private const int TileCount = TileSpan * TileSpan;
        private const byte FullCells = 15;

        // Точки окна.
        private readonly int[] _stamp = new int[PointCount];
        private readonly byte[] _cost = new byte[PointCount];
        private readonly byte[] _flags = new byte[PointCount];
        private readonly int[] _g = new int[PointCount];
        private readonly short[] _parent = new short[PointCount];
        private readonly short[] _fill = new short[PointCount];
        private readonly byte[] _node = new byte[PointCount]; // узел ли точка: Unknown / 0 / 1
        private readonly int[] _nbIndex = new int[16];
        private readonly int[] _nbCost = new int[16];
        private readonly long[] _heap;
        private int _heapCount;
        private int _search;

        // Тайлы окна (кэш чтений карты).
        private readonly int[] _tileStamp = new int[TileCount];
        private readonly byte[] _tileCost = new byte[TileCount];
        private readonly byte[] _tileCells = new byte[TileCount];
        private readonly int[] _nearStamp = new int[TileCount];
        private readonly bool[] _near = new bool[TileCount]; // в 3 × 3 вокруг тайла есть тонкая постройка

        // Поиск в работе.
        private IPathTerrain _terrain = null!;
        private MapShape _map;
        private PathMode _mode;
        private int _originX; // точка решётки мира (не свёрнутая) в (0, 0) окна
        private int _originY;
        private int _tileOriginX; // тайл мира (не свёрнутый) в (0, 0) кэша
        private int _tileOriginY;
        private int _tileMinX; // тайлы, которых касаются точки окна (мира, не свёрнутые): читаются только они
        private int _tileMinY;
        private int _tileMaxX;
        private int _tileMaxY;
        private int _goalIndex = -1; // цель — узел всегда
        private int _goalX;
        private int _goalY;
        private int _fillMinX;
        private int _fillMinY;
        private int _fillMaxX;
        private int _fillMaxY;

        public PathFinder(int maxExpanded = DefaultMaxExpanded, int enclosureFillLimit = DefaultEnclosureFillLimit)
        {
            if (maxExpanded < 1 || maxExpanded > MaxExpandedLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(maxExpanded), maxExpanded, $"Предел узлов — от 1 до {MaxExpandedLimit}.");
            }

            if (enclosureFillLimit < 0 || enclosureFillLimit > PointCount)
            {
                throw new ArgumentOutOfRangeException(nameof(enclosureFillLimit), enclosureFillLimit, $"Предел заливки — от 0 до {PointCount}.");
            }

            MaxExpanded = maxExpanded;
            EnclosureFillLimit = enclosureFillLimit;
            _heap = new long[maxExpanded * 8 + 8];
        }

        /// <summary>Предел раскрытых узлов на поиск.</summary>
        public int MaxExpanded { get; }

        /// <summary>Предел заливки от цели, точек; 0 — проверки «цель в замкнутой области» нет.</summary>
        public int EnclosureFillLimit { get; }

        /// <summary>Запусков A* всего.</summary>
        public long AStarRuns { get; private set; }

        /// <summary>Поисков всего.</summary>
        public long Searches { get; private set; }

        /// <summary>Раскрыто узлов A* всего.</summary>
        public long TotalExpanded { get; private set; }

        /// <summary>Узлов залито проверкой замкнутой области всего.</summary>
        public long TotalFilled { get; private set; }

        /// <summary>Тайлов спрошено у карты всего.</summary>
        public long TotalTerrainReads { get; private set; }

        /// <summary>Точка решётки, ближайшая к позиции (свёрнутая): координаты кратны <see cref="PointUnits"/>.</summary>
        public static WorldPos SnapToLattice(in MapShape map, WorldPos p) =>
            Torus.Wrap(map, new WorldPos(((p.X + PointUnits / 2) >> 7) << 7, ((p.Y + PointUnits / 2) >> 7) << 7));

        /// <summary>Поиск от центра тайла <paramref name="start"/> к центру тайла <paramref name="goal"/> (см. другую перегрузку).</summary>
        public PathStatus Find(IPathTerrain terrain, TileCoord start, TileCoord goal, PathMode mode, PathBuffer result) =>
            Find(terrain, TileCenter(start), TileCenter(goal), mode, result);

        /// <summary>Центр тайла в 1/256 тайла (точка решётки).</summary>
        public static WorldPos TileCenter(TileCoord t) =>
            new WorldPos((t.X << Torus.SubTileBits) + PointUnits, (t.Y << Torus.SubTileBits) + PointUnits);

        /// <summary>
        /// Найти путь от точки решётки, ближайшей к <paramref name="start"/>, к ближайшей к <paramref name="goal"/> и записать
        /// в <paramref name="result"/>. Возвращает итог (<see cref="PathBuffer.Status"/>). Старт считается свободным всегда.
        /// </summary>
        public PathStatus Find(IPathTerrain terrain, WorldPos start, WorldPos goal, PathMode mode, PathBuffer result)
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
            start = SnapToLattice(_map, start);
            goal = SnapToLattice(_map, goal);
            NextSearch();
            Searches++;
            result.Clear();

            _originX = (start.X >> 7) - LatticeHalf;
            _originY = (start.Y >> 7) - LatticeHalf;
            _tileOriginX = _tileMinX = (_originX - 1) >> 1;
            _tileOriginY = _tileMinY = (_originY - 1) >> 1;
            _tileMaxX = (_originX + LatticeSize - 1) >> 1;
            _tileMaxY = (_originY + LatticeSize - 1) >> 1;
            Int2 d = Torus.Delta(_map, start, goal);
            _goalX = LatticeHalf + (d.X >> 7);
            _goalY = LatticeHalf + (d.Y >> 7);
            bool goalInside = InWindow(_goalX, _goalY);

            Touch(StartIndex);
            byte startCost = PointCost(StartIndex);
            _cost[StartIndex] = startCost == PathCost.Blocked ? PathCost.Normal : startCost;

            if (d.X == 0 && d.Y == 0)
            {
                result.Status = PathStatus.Found;
                result.EndPoint = start;
                result.End = Torus.TileOf(_map, start);
                return PathStatus.Found;
            }

            int goalIndex = goalInside ? _goalY * LatticeSize + _goalX : -1;
            _goalIndex = goalIndex;
            if (goalInside && GoalEnclosed(goalIndex))
            {
                // К краю области, ближайшему к цели: точка вне области — новая цель A*; итог — «замкнута» с путём к краю.
                int edge = NearestOutside();
                _goalX = edge % LatticeSize;
                _goalY = edge / LatticeSize;
                _goalIndex = edge;
                if (edge != StartIndex)
                {
                    AStar(edge, result);
                }
                else
                {
                    result.EndPoint = start;
                    result.End = Torus.TileOf(_map, start);
                }

                result.Status = PathStatus.Enclosed;
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

                int x = index % LatticeSize;
                int y = index / LatticeSize;
                if (edgeExit && (OnEdge(x) || OnEdge(y)))
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

                int count = Neighbors(x, y);
                for (int j = 0; j < count; j++)
                {
                    int n = _nbIndex[j];
                    if ((_flags[n] & FlagClosed) != 0)
                    {
                        continue;
                    }

                    int ng = g + _nbCost[j];
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
                result.Points[--k] = WorldPoint(i);
            }

            result.Length = length;
            result.Cost = _g[end] / 2;
            result.EndPoint = WorldPoint(end);
            result.End = Torus.TileOf(_map, result.EndPoint);
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
            int centers = IsCenter(goalIndex) ? 1 : 0;
            _fill[tail++] = (short)goalIndex;
            _flags[goalIndex] |= FlagFilled;
            _fillMinX = _fillMaxX = goalIndex % LatticeSize;
            _fillMinY = _fillMaxY = goalIndex / LatticeSize;
            bool enclosed = true;
            while (head < tail)
            {
                int index = _fill[head++];
                int x = index % LatticeSize;
                int y = index / LatticeSize;
                _fillMinX = Math.Min(_fillMinX, x);
                _fillMaxX = Math.Max(_fillMaxX, x);
                _fillMinY = Math.Min(_fillMinY, y);
                _fillMaxY = Math.Max(_fillMaxY, y);
                if (OnEdge(x) || OnEdge(y))
                {
                    enclosed = false;
                    break;
                }

                int count = Neighbors(x, y);
                for (int j = 0; j < count; j++)
                {
                    int n = _nbIndex[j];
                    if ((_flags[n] & FlagFilled) != 0)
                    {
                        continue;
                    }

                    if (n == StartIndex || (IsCenter(n) && centers >= EnclosureFillLimit))
                    {
                        enclosed = false;
                        break;
                    }

                    centers += IsCenter(n) ? 1 : 0;
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

        /// <summary>
        /// Свободная точка окна вне залитой области, ближайшая к цели (квадрат расстояния в окне — оно же по тору); при равенстве —
        /// ближе к старту, затем меньший индекс. Обход: рамка области целиком (r = 0: там бывают точки вне области), затем кольца
        /// вокруг рамки; кольцо r лежит не ближе r к цели, поэтому обход кончается, как только r² больше лучшего. Старт свободен
        /// и вне области — кандидат есть всегда.
        /// </summary>
        private int NearestOutside()
        {
            int best = -1;
            long bestD = long.MaxValue;
            long bestS = long.MaxValue;
            for (int r = 0; r < LatticeSize && (long)r * r <= bestD; r++)
            {
                int x0 = _fillMinX - r;
                int x1 = _fillMaxX + r;
                int y0 = _fillMinY - r;
                int y1 = _fillMaxY + r;
                for (int y = y0; y <= y1; y++)
                {
                    int step = r == 0 || y == y0 || y == y1 ? 1 : x1 - x0;
                    for (int x = x0; x <= x1; x += step)
                    {
                        if (!InWindow(x, y))
                        {
                            continue;
                        }

                        int i = y * LatticeSize + x;
                        long d = Dist2ToGoal(i);
                        if (d > bestD || CostAt(i) == PathCost.Blocked || (_flags[i] & FlagFilled) != 0 || !IsNode(i))
                        {
                            continue; // CostAt — метка поиска на точке, флаг заливки уже не старый
                        }

                        long sd = new Int2(x - LatticeHalf, y - LatticeHalf).LengthSquared;
                        if (d < bestD || sd < bestS || (sd == bestS && i < best))
                        {
                            best = i;
                            bestD = d;
                            bestS = sd;
                        }
                    }
                }
            }

            return best < 0 ? StartIndex : best;
        }

        // ---------- Окно ----------

        /// <summary>
        /// Соседи узла (x, y) в <see cref="_nbIndex"/> / <see cref="_nbCost"/> (удвоенная стоимость шага), число — итог.
        /// Сначала полушаги к узлам (порядок <see cref="NeighborDx"/>; диагональ — если обе прямые соседки свободны), затем у центра
        /// тайла — целые шаги к центрам соседних тайлов, если середина шага свободна и не узел (у диагонали свободны и четыре
        /// точки по бокам — без срезания углов).
        /// </summary>
        private int Neighbors(int x, int y)
        {
            int count = 0;
            int index = y * LatticeSize + x;
            byte own = _cost[index];
            if (IsCenter(index))
            {
                int tx = (_originX + x) >> 1;
                int ty = (_originY + y) >> 1;
                if (!NearThin(tx, ty))
                {
                    return TileNeighbors(x, y, tx, ty, own);
                }
            }

            for (int k = 0; k < 8; k++)
            {
                int dx = NeighborDx[k];
                int dy = NeighborDy[k];
                int nx = x + dx;
                int ny = y + dy;
                if (!InWindow(nx, ny))
                {
                    continue;
                }

                int n = ny * LatticeSize + nx;
                byte fn = CostAt(n);
                if (fn == PathCost.Blocked || !IsNode(n))
                {
                    continue;
                }

                if (k >= 4 && (CostAt(y * LatticeSize + nx) == PathCost.Blocked || CostAt(ny * LatticeSize + x) == PathCost.Blocked))
                {
                    continue;
                }

                _nbIndex[count] = n;
                _nbCost[count] = PathCost.Step(k < 4 ? PathCost.Straight : PathCost.Diagonal, fn < own ? fn : own);
                count++;
            }

            if (!IsCenter(index))
            {
                return count;
            }

            for (int k = 0; k < 8; k++)
            {
                int dx = NeighborDx[k];
                int dy = NeighborDy[k];
                int nx = x + 2 * dx;
                int ny = y + 2 * dy;
                if (!InWindow(nx, ny))
                {
                    continue;
                }

                int m = (y + dy) * LatticeSize + x + dx;
                byte fm = CostAt(m);
                if (fm == PathCost.Blocked || IsNode(m))
                {
                    continue; // середина занята — шага нет; середина — узел — путь идёт полушагами через неё
                }

                int n = ny * LatticeSize + nx;
                byte fn = CostAt(n);
                if (fn == PathCost.Blocked)
                {
                    continue;
                }

                if (k >= 4 &&
                    (CostAt(y * LatticeSize + x + dx) == PathCost.Blocked || CostAt((y + dy) * LatticeSize + x) == PathCost.Blocked ||
                     CostAt((y + dy) * LatticeSize + nx) == PathCost.Blocked || CostAt(ny * LatticeSize + x + dx) == PathCost.Blocked))
                {
                    continue;
                }

                int step = k < 4 ? PathCost.Straight : PathCost.Diagonal;
                _nbIndex[count] = n;
                _nbCost[count] = PathCost.Step(step, fm < own ? fm : own) + PathCost.Step(step, fm < fn ? fm : fn);
                count++;
            }

            return count;
        }

        /// <summary>
        /// Соседи центра тайла вдали от тонких построек (в 3 × 3 вокруг — только целые и пустые тайлы): прежний шаг по тайлам —
        /// к центру соседнего свободного тайла, диагональ — если оба прямых соседа свободны. Середина шага и угол свободны тогда
        /// же, когда свободны их тайлы, — тот же граф, что у полушагов, без расчёта точек. Мелких соседей нет: точки у тонкой
        /// постройки достижимы из центров рядом с ней.
        /// </summary>
        private int TileNeighbors(int x, int y, int tx, int ty, byte own)
        {
            int count = 0;
            for (int k = 0; k < 8; k++)
            {
                int dx = NeighborDx[k];
                int dy = NeighborDy[k];
                int nx = x + 2 * dx;
                int ny = y + 2 * dy;
                if (!InWindow(nx, ny))
                {
                    continue;
                }

                int n = ny * LatticeSize + nx;
                byte fn = CostAt(n);
                if (fn == PathCost.Blocked)
                {
                    continue;
                }

                if (k >= 4 && (_tileCost[TileIndex(tx + dx, ty)] == PathCost.Blocked || _tileCost[TileIndex(tx, ty + dy)] == PathCost.Blocked))
                {
                    continue;
                }

                int step = k < 4 ? PathCost.Straight : PathCost.Diagonal;
                _nbIndex[count] = n;
                _nbCost[count] = PathCost.Step(step, own) + PathCost.Step(step, fn);
                count++;
            }

            // Цель — узел всегда: если она в полушаге (не центр — враг целит в точку у игрока), полушаг к ней.
            if (_goalIndex >= 0)
            {
                int gx = _goalIndex % LatticeSize;
                int gy = _goalIndex / LatticeSize;
                int ddx = gx - x;
                int ddy = gy - y;
                if (ddx >= -1 && ddx <= 1 && ddy >= -1 && ddy <= 1 && (ddx != 0 || ddy != 0))
                {
                    byte fg = CostAt(_goalIndex);
                    bool diag = ddx != 0 && ddy != 0;
                    if (fg != PathCost.Blocked &&
                        (!diag || (CostAt(y * LatticeSize + gx) != PathCost.Blocked && CostAt(gy * LatticeSize + x) != PathCost.Blocked)))
                    {
                        _nbIndex[count] = _goalIndex;
                        _nbCost[count] = PathCost.Step(diag ? PathCost.Diagonal : PathCost.Straight, fg < own ? fg : own);
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>Точка окна — центр тайла (обе координаты решётки мира нечётные).</summary>
        private bool IsCenter(int index) =>
            ((_originX + index % LatticeSize) & 1) != 0 && ((_originY + index / LatticeSize) & 1) != 0;

        /// <summary>
        /// Точка — узел графа: центр тайла, старт, цель или точка у тонкой постройки (тайл, которого касается круг, или его сосед —
        /// с частью ячеек твёрдых).
        /// </summary>
        private bool IsNode(int index)
        {
            if (index == StartIndex || index == _goalIndex || IsCenter(index))
            {
                return true;
            }

            Touch(index);
            byte node = _node[index];
            if (node != Unknown)
            {
                return node != 0;
            }

            int gx = _originX + index % LatticeSize;
            int gy = _originY + index / LatticeSize;
            bool near = false;
            for (int ty = (gy - 1) >> 1; ty <= gy >> 1 && !near; ty++)
            {
                for (int tx = (gx - 1) >> 1; tx <= gx >> 1 && !near; tx++)
                {
                    near = NearThin(tx, ty);
                }
            }

            _node[index] = near ? (byte)1 : (byte)0;
            return near;
        }

        /// <summary>В 3 × 3 вокруг тайла (в пределах читаемых тайлов окна) есть тайл с частью ячеек твёрдых.</summary>
        private bool NearThin(int tx, int ty)
        {
            int t = (ty - _tileOriginY) * TileSpan + (tx - _tileOriginX);
            if (_nearStamp[t] == _search)
            {
                return _near[t];
            }

            bool near = false;
            for (int y = Math.Max(ty - 1, _tileMinY); y <= Math.Min(ty + 1, _tileMaxY) && !near; y++)
            {
                for (int x = Math.Max(tx - 1, _tileMinX); x <= Math.Min(tx + 1, _tileMaxX) && !near; x++)
                {
                    byte cells = _tileCells[TileIndex(x, y)];
                    near = cells != 0 && cells != FullCells;
                }
            }

            _nearStamp[t] = _search;
            _near[t] = near;
            return near;
        }

        /// <summary>Край окна: две крайние точки по оси (крайний ряд центров тайлов и точки за ним).</summary>
        private static bool OnEdge(int v) => v < 2 || v >= LatticeSize - 2;

        private static bool InWindow(int x, int y) => (uint)x < LatticeSize && (uint)y < LatticeSize;

        private byte CostAt(int index)
        {
            Touch(index);
            byte cost = _cost[index];
            if (cost != Unknown)
            {
                return cost;
            }

            cost = PointCost(index);
            _cost[index] = cost;
            return cost;
        }

        /// <summary>
        /// Множитель точки: 0 — круг в ней задевает твёрдую ячейку; иначе наибольший множитель тайлов, чьих ячеек он касается
        /// (свободные ячейки твёрдого тайла — обычный пол). Точка (gx, gy) касается ячеек gx − 1, gx по X и gy − 1, gy по Y.
        /// </summary>
        private byte PointCost(int index)
        {
            int gx = _originX + index % LatticeSize;
            int gy = _originY + index / LatticeSize;
            int tx0 = (gx - 1) >> 1;
            int tx1 = gx >> 1;
            int ty0 = (gy - 1) >> 1;
            int ty1 = gy >> 1;
            byte factor = 0;
            for (int ty = ty0; ty <= ty1; ty++)
            {
                // Ряды ячеек тайла, которых касается круг: оба (точка в середине тайла по Y) или один (на границе).
                int rows = ty0 == ty1 ? 3 : (ty == ty0 ? 2 : 1);
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    int cols = tx0 == tx1 ? 3 : (tx == tx0 ? 2 : 1);
                    int touched = ((rows & 1) != 0 ? cols : 0) | ((rows & 2) != 0 ? cols << 2 : 0);
                    int t = TileIndex(tx, ty);
                    if ((_tileCells[t] & touched) != 0)
                    {
                        return PathCost.Blocked;
                    }

                    byte f = _tileCost[t] == PathCost.Blocked ? PathCost.Normal : _tileCost[t];
                    if (f > factor)
                    {
                        factor = f;
                    }
                }
            }

            return factor;
        }

        /// <summary>Тайл кэша (тайл мира не свёрнутый): при первом взгляде за поиск — множитель и твёрдые ячейки у карты.</summary>
        private int TileIndex(int tx, int ty)
        {
            int t = (ty - _tileOriginY) * TileSpan + (tx - _tileOriginX);
            if (_tileStamp[t] == _search)
            {
                return t;
            }

            _tileStamp[t] = _search;
            TotalTerrainReads++;
            if (!_map.Wraps && ((uint)tx >= (uint)_map.Width || (uint)ty >= (uint)_map.Height))
            {
                _tileCost[t] = PathCost.Blocked;
                _tileCells[t] = FullCells;
                return t;
            }

            TileCoord tile = Torus.Wrap(_map, new TileCoord(tx, ty));
            byte cost = _terrain.CostOf(tile, _mode);
            _tileCost[t] = cost;
            if (cost == PathCost.Blocked)
            {
                byte cells = (byte)(_terrain.CellsOf(tile, _mode) & FullCells);
                _tileCells[t] = cells == 0 ? FullCells : cells;
            }
            else
            {
                _tileCells[t] = 0;
            }

            return t;
        }

        private WorldPos WorldPoint(int index) =>
            Torus.Wrap(_map, new WorldPos((_originX + index % LatticeSize) << 7, (_originY + index / LatticeSize) << 7));

        private int Heuristic(int index) =>
            PathCost.Octile(_goalX - index % LatticeSize, _goalY - index / LatticeSize);

        private long Dist2ToGoal(int index) => new Int2(_goalX - index % LatticeSize, _goalY - index / LatticeSize).LengthSquared;

        private void Touch(int index)
        {
            if (_stamp[index] == _search)
            {
                return;
            }

            _stamp[index] = _search;
            _cost[index] = Unknown;
            _node[index] = Unknown;
            _flags[index] = 0;
        }

        private void NextSearch()
        {
            if (_search == int.MaxValue)
            {
                Array.Clear(_stamp, 0, _stamp.Length);
                Array.Clear(_tileStamp, 0, _tileStamp.Length);
                Array.Clear(_nearStamp, 0, _nearStamp.Length);
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
