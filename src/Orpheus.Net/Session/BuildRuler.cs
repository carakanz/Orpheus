using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;

namespace Orpheus.Net.Session
{
    /// <summary>Как ставит предмет-постройка в руке (E2-13, решение Автора 15.11, `BRIEF` 11f): линейка (по умолчанию) или поштучно.</summary>
    public enum PlaceMode : byte
    {
        /// <summary>Нажал и тянешь от A до B, отпустил — линия стен/заборов или прямоугольник пола.</summary>
        Ruler = 0,

        /// <summary>Щелчок — одна, зажатая ЛКМ — по ячейкам под курсором (как до E2-13).</summary>
        Single = 1,
    }

    /// <summary>Чем линейка ставит постройку: линией ячеек (стена, забор), прямоугольником тайлов (полы, настил) или одну.</summary>
    public enum RulerKind : byte
    {
        Single = 0,
        Line = 1,
        Rect = 2,
    }

    /// <summary>Клетка плана линейки: тайл, ячейки 0,5 × 0,5 (у пола 0 — тайл), проверка ядра; «нет предмета» — поставить не на что.</summary>
    public readonly struct RulerSpot
    {
        public readonly TileCoord Tile;
        public readonly byte Cells;
        public readonly EditReply Reply;

        /// <summary>Можно, но предметы в руке кончатся раньше (по порядку от A).</summary>
        public readonly bool Short;

        public RulerSpot(TileCoord tile, byte cells, EditReply reply, bool shortOfItems)
        {
            Tile = tile;
            Cells = cells;
            Reply = reply;
            Short = shortOfItems;
        }

        public bool Ok => Reply == EditReply.Accepted;
    }

    /// <summary>
    /// Линейка (E2-13, слова Автора: «Линейка будет стены укладывать линией, пол — квадратом»). Общий класс для игрока и
    /// бота: протягивание от A до B даёт план — клетки по порядку от A с проверкой ядра (<see cref="BuildHand.Check"/>, те же
    /// функции, что у сервера; своих правил нет), «нужно / есть», длину и площадь; отпускание шлёт план <b>пачкой обычных
    /// заявок линии</b> (<see cref="BuildHand.PlaceLine"/>, протокол не менялся): стена и забор — отрезки ячеек до 32
    /// (<see cref="EditRules.MaxLineTiles"/>), пол — ряд тайлов на заявку. Сервер ставит каждую клетку по своим правилам
    /// (радиус, занятость, предмет на клетку, «нельзя» — пропуск), клиент предсказывает как у обычной линии.
    /// Ось линии — по большему смещению; <c>diagonal</c> (Shift) — прямая A–B лесенкой ячеек (Брезенхэм ядра).
    /// Дверь, мебель, факел, верстак — <see cref="RulerKind.Single"/>: ставятся щелчком, как поштучно.
    /// </summary>
    public sealed class BuildRuler
    {
        /// <summary>Длина линии стен не больше (ячеек 0,5): два отрезка заявки.</summary>
        public const int MaxLineCells = 2 * EditRules.MaxLineTiles;

        /// <summary>Сторона прямоугольника пола не больше (тайлов): ряд — одна заявка, рядов не больше ждущих линий сервера.</summary>
        public const int MaxRectSide = 16;

        private const int MaxSegments = 2 * MaxRectSide;

        /// <summary>Прямоугольник пола — по ячейкам 0,5 (false — по тайлам; E2-15: сетка 0,5 для всех построек).</summary>
        public const bool RectOnCells = false;

        private readonly RulerSpot[] _spots = new RulerSpot[MaxRectSide * MaxRectSide];
        private readonly TileCoord[] _segFrom = new TileCoord[MaxSegments];
        private readonly TileCoord[] _segTo = new TileCoord[MaxSegments];
        private readonly byte[] _segFromQuad = new byte[MaxSegments];
        private readonly byte[] _segToQuad = new byte[MaxSegments];
        private readonly int[] _segOk = new int[MaxSegments];
        private readonly byte[] _spotQuad = new byte[MaxRectSide * MaxRectSide];
        private readonly byte[] _spotSeg = new byte[MaxRectSide * MaxRectSide];
        private readonly TileCoord[] _line = new TileCoord[EditRules.MaxLineTiles];

        /// <summary>Режим постановки; по умолчанию — линейка (решение Автора 15.11). Живёт, пока жив объект (сессия).</summary>
        public PlaceMode Mode { get; set; } = PlaceMode.Ruler;

        /// <summary>Переключить режим (клавиша R).</summary>
        public PlaceMode Toggle() => Mode = Mode == PlaceMode.Ruler ? PlaceMode.Single : PlaceMode.Ruler;

        /// <summary>Идёт протягивание (ЛКМ нажата в режиме линейки).</summary>
        public bool Dragging { get; private set; }

        /// <summary>Начало протягивания: тайл и ячейка 0,5 × 0,5 в нём.</summary>
        public TileCoord AnchorTile { get; private set; }
        public int AnchorQuad { get; private set; }

        /// <summary>План: вид, клетки по порядку от A.</summary>
        public RulerKind Kind { get; private set; }
        public int Count { get; private set; }
        public ReadOnlySpan<RulerSpot> Spots => new ReadOnlySpan<RulerSpot>(_spots, 0, Count);

        /// <summary>Ставится клеток (можно по проверке; предмет на клетку) и предметов в руке.</summary>
        public int Need { get; private set; }
        public int Have { get; private set; }

        /// <summary>Линия: длина в ячейках 0,5 (метры = ÷ 2). Прямоугольник: ширина и высота в тайлах.</summary>
        public int LengthCells { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>Отрезков (заявок) в плане.</summary>
        public int Segments { get; private set; }

        /// <summary>Заявок отправлено последним <see cref="Release"/>.</summary>
        public int LastSent { get; private set; }

        /// <summary>Вид линейки для постройки: стена и забор — линия ячеек, полы и настил — прямоугольник, прочее — одна.</summary>
        public static RulerKind KindOf(ushort key)
        {
            if (EditRules.IsFloorKey(key))
            {
                return RulerKind.Rect;
            }

            return BuildCells.IsCellBuilding(key) && ObjectId.IdOf(key) != ObjectId.Door ? RulerKind.Line : RulerKind.Single;
        }

        /// <summary>Вид линейки для предмета в руке; не постройка — <see cref="RulerKind.Single"/>.</summary>
        public static RulerKind KindOfItem(byte item) => BuildHand.IsBuildItem(item, out ushort key) ? KindOf(key) : RulerKind.Single;

        /// <summary>Предмет в руке при начале протягивания (сменили — клиент отменяет протягивание).</summary>
        public byte AnchorItem { get; private set; }

        /// <summary>Начать протягивание от тайла / ячейки (ЛКМ нажата) предметом <paramref name="item"/>.</summary>
        public void Begin(TileCoord tile, int quad, byte item = 0)
        {
            Dragging = true;
            AnchorItem = item;
            AnchorTile = tile;
            AnchorQuad = quad & 3;
            Count = 0;
            Segments = 0;
        }

        /// <summary>Отменить протягивание (ПКМ, Esc, постройку убрали из руки): ничего не ставится.</summary>
        public void Cancel()
        {
            Dragging = false;
            Count = 0;
            Segments = 0;
        }

        /// <summary>
        /// План от начала до тайла / ячейки курсора <paramref name="tile"/> / <paramref name="quad"/> предметом ячейки
        /// <paramref name="cell"/> (данные клиента: свой мир, место, сумка). Клетки — по порядку от A; каждая проверена
        /// <see cref="BuildHand.Check"/>; «нужно» — можно, «есть» — предметов в ячейке; можно, но сверх «есть» — <see cref="RulerSpot.Short"/>.
        /// </summary>
        public void Update(WorldModel world, WorldPos self, in Inventory bag, int cell, bool alive, TileCoord tile, int quad, bool diagonal)
        {
            Count = 0;
            Segments = 0;
            Need = 0;
            LengthCells = 0;
            Width = 0;
            Height = 0;
            Kind = KindOfItem(bag.ItemAt(cell));
            Have = BuildHand.IsBuildItem(bag.ItemAt(cell)) ? bag.CountIn(cell) : 0;
            MapShape map = world.Shape;
            if (Kind == RulerKind.Line)
            {
                PlanLine(map, tile, quad, diagonal);
            }
            else if (Kind == RulerKind.Rect)
            {
                PlanRect(map, tile, quad);
            }
            else
            {
                AddSegment(AnchorTile, AnchorQuad, AnchorTile, AnchorQuad);
            }

            for (int s = 0; s < Segments; s++)
            {
                _segOk[s] = 0;
                if (Kind == RulerKind.Line || (Kind == RulerKind.Rect && RectOnCells))
                {
                    TileCoord a = BuildCells.CellOf(_segFrom[s], _segFromQuad[s]);
                    TileCoord b = BuildCells.CellOf(_segTo[s], _segToQuad[s]);
                    int n = EditRules.Line(BuildCells.CellShape(map), a, b, _line);
                    for (int i = 0; i < n; i++)
                    {
                        AddSpot(world, self, bag, cell, alive, BuildCells.TileOfCell(_line[i]), BuildCells.QuadOf(_line[i]), s);
                    }
                }
                else
                {
                    int n = EditRules.Line(map, _segFrom[s], _segTo[s], _line);
                    for (int i = 0; i < n; i++)
                    {
                        AddSpot(world, self, bag, cell, alive, _line[i], _segFromQuad[s], s);
                    }
                }
            }
        }

        /// <summary>
        /// Отпустил ЛКМ: план уходит заявками линии через <paramref name="hand"/> (по отрезку, где есть хоть одна «можно»,
        /// пока ставящихся клеток меньше «есть»). Без предметов или без «можно» — ничего. Протягивание заканчивается.
        /// Возвращает число отправленных заявок.
        /// </summary>
        public int Release(BuildHand hand, WorldPos self, in Inventory bag, int cell)
        {
            int sent = 0;
            int placed = 0;
            for (int s = 0; s < Segments && placed < Have; s++)
            {
                if (_segOk[s] == 0)
                {
                    continue;
                }

                // Предметов меньше, чем «можно» в отрезке: отрезок кончается на последней клетке, на которую хватает —
                // сервер платит за тайл целиком (ячейки тайла — одна правка), так последний тайл не пропадёт из-за лишней ячейки.
                TileCoord to = _segTo[s];
                int toQuad = _segToQuad[s];
                int ok = _segOk[s];
                if (placed + ok > Have)
                {
                    ok = Have - placed;
                    LastOk(s, ok, out to, out toQuad);
                }

                if (hand.PlaceLine(_segFrom[s], to, self, bag, cell, _segFromQuad[s], toQuad) != 0)
                {
                    sent++;
                    placed += ok;
                }
            }

            LastSent = sent;
            Cancel();
            return sent;
        }

        // Клетка отрезка seg, на которой набирается k-я «можно».
        private void LastOk(int seg, int k, out TileCoord tile, out int quad)
        {
            tile = _segTo[seg];
            quad = _segToQuad[seg];
            for (int i = 0; i < Count && k > 0; i++)
            {
                if (_spotSeg[i] == seg && _spots[i].Ok && --k == 0)
                {
                    tile = _spots[i].Tile;
                    quad = _spotQuad[i];
                }
            }
        }

        private void AddSpot(WorldModel world, WorldPos self, in Inventory bag, int cell, bool alive, TileCoord tile, int quad, int seg)
        {
            if (Count >= _spots.Length)
            {
                return;
            }

            BuildCheck c = BuildHand.Check(world, self, bag, cell, alive, tile, quad);
            bool ok = c.Ok;
            if (ok)
            {
                _segOk[seg]++;
                Need++;
            }

            _spotQuad[Count] = (byte)(quad & 3);
            _spotSeg[Count] = (byte)seg;
            _spots[Count++] = new RulerSpot(Torus.Wrap(world.Shape, tile), c.Cells, c.Reply, ok && Need > Have);
        }

        // Стена, забор: ячейки от A до B по большей оси (Shift — прямая A–B), до MaxLineCells.
        private void PlanLine(in MapShape map, TileCoord tile, int quad, bool diagonal)
        {
            MapShape cells = BuildCells.CellShape(map);
            TileCoord a = Torus.Wrap(cells, BuildCells.CellOf(AnchorTile, AnchorQuad));
            TileCoord b = Torus.Wrap(cells, BuildCells.CellOf(tile, quad));
            Int2 d = Torus.Delta(cells, a, b);
            int dx = d.X, dy = d.Y;
            if (!diagonal)
            {
                if (Math.Abs(dx) >= Math.Abs(dy))
                {
                    dy = 0;
                }
                else
                {
                    dx = 0;
                }
            }

            int n = Math.Max(Math.Abs(dx), Math.Abs(dy)) + 1;
            if (n > MaxLineCells)
            {
                dx = Scale(dx, MaxLineCells - 1, n - 1);
                dy = Scale(dy, MaxLineCells - 1, n - 1);
                n = MaxLineCells;
            }

            LengthCells = n;
            AddRun(map, true, a, dx, dy);
        }

        // Пол: прямоугольник A–B (стороны до MaxRectSide), ряды от ряда A, в ряду — от столбца A. Сетка — RectOnCells
        // (E2-15 переведёт пол на ячейки 0,5 этим флагом: развёртка рядов — та же AddRun).
        private void PlanRect(in MapShape map, TileCoord tile, int quad)
        {
            bool onCells = RectOnCells;
            MapShape grid = onCells ? BuildCells.CellShape(map) : map;
            TileCoord a = onCells ? Torus.Wrap(grid, BuildCells.CellOf(AnchorTile, AnchorQuad)) : Torus.Wrap(map, AnchorTile);
            TileCoord b = onCells ? Torus.Wrap(grid, BuildCells.CellOf(tile, quad)) : Torus.Wrap(map, tile);
            Int2 d = Torus.Delta(grid, a, b);
            int dx = Math.Max(-(MaxRectSide - 1), Math.Min(MaxRectSide - 1, d.X));
            int dy = Math.Max(-(MaxRectSide - 1), Math.Min(MaxRectSide - 1, d.Y));
            Width = Math.Abs(dx) + 1;
            Height = Math.Abs(dy) + 1;
            int sy = dy < 0 ? -1 : 1;
            for (int j = 0; j < Height; j++)
            {
                AddRun(map, onCells, new TileCoord(a.X, a.Y + sy * j), dx, 0);
            }
        }

        /// <summary>
        /// Развёртка прямой от <paramref name="a"/> на (<paramref name="dx"/>, <paramref name="dy"/>) в отрезки заявок (одна
        /// функция для линии и рядов прямоугольника): по сетке ячеек 0,5 (<paramref name="onCells"/>) или тайлов, отрезок — до
        /// 32 клеток (<see cref="EditRules.MaxLineTiles"/>). На сетке ячеек стык отрезков — только на границе тайлов: ячейки
        /// одного тайла — одна правка сервера, тайл в двух заявках одного такта поставил бы одну (проверка П E2-13, з. 1).
        /// </summary>
        private void AddRun(in MapShape map, bool onCells, TileCoord a, int dx, int dy)
        {
            MapShape grid = onCells ? BuildCells.CellShape(map) : map;
            int n = Math.Max(Math.Abs(dx), Math.Abs(dy)) + 1;
            int i0 = 0;
            while (i0 < n)
            {
                int i1 = Math.Min(i0 + EditRules.MaxLineTiles - 1, n - 1);
                while (onCells && i1 > i0 && i1 < n - 1 && BuildCells.TileOfCell(RunPoint(grid, a, dx, dy, n, i1)) == BuildCells.TileOfCell(RunPoint(grid, a, dx, dy, n, i1 + 1)))
                {
                    i1--;
                }

                TileCoord p0 = RunPoint(grid, a, dx, dy, n, i0);
                TileCoord p1 = RunPoint(grid, a, dx, dy, n, i1);
                if (onCells)
                {
                    AddSegment(BuildCells.TileOfCell(p0), BuildCells.QuadOf(p0), BuildCells.TileOfCell(p1), BuildCells.QuadOf(p1));
                }
                else
                {
                    AddSegment(p0, 0, p1, 0);
                }

                i0 = i1 + 1;
            }
        }

        private static TileCoord RunPoint(in MapShape grid, TileCoord a, int dx, int dy, int n, int i) =>
            Torus.Wrap(grid, new TileCoord(a.X + Scale(dx, i, n - 1), a.Y + Scale(dy, i, n - 1)));

        private void AddSegment(TileCoord from, int fromQuad, TileCoord to, int toQuad)
        {
            if (Segments >= MaxSegments)
            {
                return;
            }

            _segFrom[Segments] = from;
            _segTo[Segments] = to;
            _segFromQuad[Segments] = (byte)(fromQuad & 3);
            _segToQuad[Segments] = (byte)(toQuad & 3);
            Segments++;
        }

        // round(v · num / den), половина — от нуля; den = 0 — 0.
        private static int Scale(int v, int num, int den)
        {
            if (den == 0)
            {
                return 0;
            }

            long p = (long)v * num;
            long q = den;
            return (int)(p >= 0 ? (2 * p + q) / (2 * q) : -((-2 * p + q) / (2 * q)));
        }
    }
}
