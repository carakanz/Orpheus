using System;
using Orpheus.Core.Rng;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Тропы (карточка E1-2, план 1.3): линии проходов шириной 2–3 тайла через весь мир — линии уровня ½ двух независимых
    /// гладких полей (ячейки 128, 64). Ширина постоянная: тайл на тропе, если расстояние до линии |v − ½| / |∇v| ≤ 1,3.
    /// Тропы вычитаются из леса и обрывов (там — проход), пол на них — земля.
    /// </summary>
    public static class TrailStage
    {
        public const int Stage = 16;

        /// <summary>Сколько полей троп (линии разных полей пересекаются — получается сеть).</summary>
        public const int Fields = 2;

        public static readonly int[] Cells = { 256, 128 };

        /// <summary>Полуширина тропы, сотые тайла.</summary>
        public const int HalfWidth100 = 130;

        /// <summary>Пометить тропы в <see cref="TileWorld.Marks"/> (<see cref="TileWorld.MarkTrail"/>).</summary>
        public static void Run(TileWorld t)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            ulong seed = t.Macro.Map.Seed;
            for (int f = 0; f < Fields; f++)
            {
                ushort[] v = GridField.Upsample(new FieldNoise(MacroGenerator.FieldSeed(seed, Stage, f + 1), w, Cells, ridged: false), w);
                for (int y = 0; y < w; y++)
                {
                    int row = y * w;
                    int up = ((y - 1) & mask) * w;
                    int down = ((y + 1) & mask) * w;
                    for (int x = 0; x < w; x++)
                    {
                        long d = v[row + x] - (LatticeNoise.One >> 1);
                        long gx = v[row + ((x + 1) & mask)] - v[row + ((x - 1) & mask)]; // 2 × градиент
                        long gy = v[down + x] - v[up + x];
                        // |d| ≤ hw · |∇v|, |∇v| = √(gx² + gy²) / 2  ⇔  4·d²·100² ≤ hw100² · (gx² + gy²)
                        if (4 * d * d * 10000 <= (long)HalfWidth100 * HalfWidth100 * ((gx * gx) + (gy * gy)))
                        {
                            t.Marks[row + x] |= TileWorld.MarkTrail;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Обрывы-границы (карточки E1-2, E1-6b; план 1.2, 1.6б; образец — Factorio): непроходимые и неломаемые гряды скал —
    /// плавные извилистые полосы толщиной 2–3 тайла вокруг возвышенностей, с редкими ясными проходами.
    /// <para>
    /// 1. <b>Поле.</b> Высота рельефа (<see cref="ReliefMap"/>) берётся в ячейках 2 × 2 тайла с искривлением координат шумом
    /// (<see cref="CellsWarp"/>, до ±<see cref="WarpTiles"/> тайлов). Возвышенность уровня k — ячейки выше квантиля
    /// <see cref="LevelPerMille"/>[k] (уровней — один-два).
    /// </para>
    /// <para>
    /// 2. <b>Чистка формы.</b> Маска возвышенности закрывается и открывается кругом (<see cref="CloseR2"/>, <see cref="OpenR2"/>):
    /// узкие протоки и языки уже ~16 тайлов исчезают, углы скругляются; острова и дыры меньше <see cref="MinAreaCells"/> ячеек
    /// убираются. Поэтому линия не идёт рядом сама с собой и не штрихует луг.
    /// </para>
    /// <para>
    /// 3. <b>Полоса.</b> По маске считается расстояние до её края со знаком (евклидово, в тайлах; между центрами ячеек —
    /// билинейно). Обрыв — тайлы возвышенности, у которых расстояние до края плюс дрожание (шум <see cref="CellsWobble"/>,
    /// ±1,3 тайла) лежит в [0, толщина); толщина — 2–3 тайла, местами осыпи до 4 (шум <see cref="CellsThick"/>). Дрожание
    /// ломает прямые отрезки. Полоса второго уровня ближе <see cref="MinApartTiles"/> тайлов к краю первого не ставится.
    /// </para>
    /// <para>
    /// 4. <b>Где обрыва нет:</b> в <see cref="WaterStage.NoWaterRadius"/> от Очага, в воде и на берегу, на тропе и в тайле от
    /// неё (проход по тропе — 4–5 тайлов).
    /// </para>
    /// <para>
    /// 5. <b>Проходы.</b> Каждая связная гряда короче <see cref="MinLineTiles"/> убирается; по остальным идёт обход в ширину от
    /// конца гряды, и каждые <see cref="GapPeriod"/> шагов вырезается проход в <see cref="GapTiles"/> шагов; обрубки короче
    /// <see cref="MinRunTiles"/> убираются. Проход и тайлы вокруг помечаются <see cref="TileWorld.MarkClear"/>: этап 11 кладёт
    /// туда землю и не ставит объекты.
    /// </para>
    /// Только целые числа; порядок обхода фиксирован.
    /// </summary>
    public static class CliffStage
    {
        public const int Stage = 17;

        public const int FieldWarpX = 2;
        public const int FieldWarpY = 3;
        public const int FieldWobble = 4;
        public const int FieldThick = 5;

        /// <summary>Доля ячеек ниже края возвышенности уровня k, ‰ (по возрастанию; один-два уровня).</summary>
        public static readonly int[] LevelPerMille = { 740 };

        /// <summary>Вес размытой маски горных биомов против рельефа и радиус размытия, ячеек (два прохода).</summary>
        public const int RockyWeight = 2;
        public const int BlurCells = 8;

        public static readonly int[] CellsWarp = { 64, 32 };
        public static readonly int[] CellsWobble = { 8, 4 };
        public static readonly int[] CellsThick = { 32, 16 };

        /// <summary>Размах дрожания края: поле Q16 − ½, умноженное на это, — сдвиг в 1/256 тайла (2000 — обычно ±1, изредка до ±3 тайлов).</summary>
        public const int WobbleQ8 = 1900;

        /// <summary>Гряда длиннее после нарезки (кольцо: обход сходится с двух сторон) режется ещё раз посередине.</summary>
        public const int MaxRunTiles = 80;

        /// <summary>Наибольшее искривление координат, тайлов.</summary>
        public const int WarpTiles = 10;

        /// <summary>Квадраты радиусов закрытия и открытия маски, ячеек² (ячейка — 2 тайла): 4,5 и 5 ячеек.</summary>
        public const int CloseR2 = 20;
        public const int OpenR2 = 26;

        /// <summary>Остров возвышенности или дыра в ней меньше — убирается, ячеек (400 ячеек = 1600 тайлов).</summary>
        public const int MinAreaCells = 400;

        /// <summary>Полоса верхнего уровня не ближе стольких тайлов к краю нижнего.</summary>
        public const int MinApartTiles = 20;

        /// <summary>Гряда короче — мусор, убирается до нарезки проходов.</summary>
        public const int MinLineTiles = 28;

        /// <summary>Период проходов вдоль гряды и ширина прохода, шагов обхода (шаг — тайл по оси, 1,4 тайла по диагонали).</summary>
        public const int GapPeriod = 64;
        public const int GapTiles = 5;

        /// <summary>Обрубок после нарезки короче — убирается.</summary>
        public const int MinRunTiles = 12;

        /// <summary>Предел счёта расстояния до края маски, ячеек.</summary>
        private const int DistCap = 12;

        private const int Far = 1 << 20;

        /// <summary>Поставить обрывы (после воды и троп, до объектов этапа 11).</summary>
        public static void Run(TileWorld t)
        {
            int w = t.WorldSize;
            int m = w >> 1;
            int mmask = m - 1;
            int half = w >> 1;
            ulong seed = t.Macro.Map.Seed;
            ReliefMap relief = t.Relief;

            // 1. Поле высот в ячейках 2 × 2 с искривлением координат.
            var warpX = new FieldNoise(MacroGenerator.FieldSeed(seed, Stage, FieldWarpX), w, CellsWarp, ridged: false);
            var warpY = new FieldNoise(MacroGenerator.FieldSeed(seed, Stage, FieldWarpY), w, CellsWarp, ridged: false);
            var field = new ushort[m * m];
            var rocky = new int[m * m];
            int mask = w - 1;
            var hist = new int[LatticeNoise.One];
            for (int j = 0; j < m; j++)
            {
                for (int i = 0; i < m; i++)
                {
                    int x = (i << 1) + 1;
                    int y = (j << 1) + 1;
                    int sx = Warp(warpX.Sample(x, y));
                    int sy = Warp(warpY.Sample(x, y));
                    field[(j * m) + i] = (ushort)relief.HeightAt(x + sx, y + sy);
                    byte b = t.Biome[(((y + sy) & mask) * w) + ((x + sx) & mask)];
                    rocky[(j * m) + i] = b == Biome.Mountain || b == Biome.Cave ? LatticeNoise.One - 1 : 0;
                }
            }

            // Горы тянут поле вверх: размытая маска горных биомов весит вдвое больше рельефа — гряды опоясывают горы.
            rocky = Blur(Blur(rocky, m, BlurCells), m, BlurCells);
            for (int c = 0; c < field.Length; c++)
            {
                int v = ((RockyWeight * rocky[c]) + field[c]) / (RockyWeight + 1);
                field[c] = (ushort)v;
                hist[v]++;
            }

            var wobble = new FieldNoise(MacroGenerator.FieldSeed(seed, Stage, FieldWobble), w, CellsWobble, ridged: false);
            var thick = new FieldNoise(MacroGenerator.FieldSeed(seed, Stage, FieldThick), w, CellsThick, ridged: false);
            var cliff = new bool[w * w];
            int[]? lower = null; // расстояние до края нижнего уровня в ячейках, Q8 тайлов
            long r = WaterStage.NoWaterRadius + GridField.Step;
            var sqrtQ8 = new int[(DistCap * DistCap) + 1];
            for (int d2 = 0; d2 < sqrtQ8.Length; d2++)
            {
                sqrtQ8[d2] = (int)IntMath.Isqrt((long)d2 << 18); // 2·√d2 тайлов, Q8
            }

            for (int level = 0; level < LevelPerMille.Length; level++)
            {
                int threshold = GridField.QuantileBelow(hist, (long)field.Length * LevelPerMille[level] / 1000);
                var high = new bool[m * m];
                for (int c = 0; c < high.Length; c++)
                {
                    high[c] = field[c] >= threshold;
                }

                // 2. Чистка формы: закрыть, открыть, убрать мелкие острова и дыры.
                high = Not(Near(Not(Near(high, m, CloseR2)), m, CloseR2));
                high = Near(Not(Near(Not(high), m, OpenR2)), m, OpenR2);
                DropSmall(high, m, true);
                DropSmall(high, m, false);

                // 3. Расстояние до края со знаком в ячейках, Q8 тайлов: внутри > 0.
                int[] inside = Dist2(Not(high), m);
                int[] outside = Dist2(high, m);
                var sd = new int[m * m];
                for (int c = 0; c < sd.Length; c++)
                {
                    sd[c] = high[c] ? sqrtQ8[inside[c]] - 256 : 256 - sqrtQ8[outside[c]];
                }

                var band = new bool[w * w];
                var depthQ8 = new short[w * w];
                for (int y = 0; y < w; y++)
                {
                    int j0 = ((y - 1) >> 1) & mmask;
                    int j1 = (j0 + 1) & mmask;
                    int fy = (y & 1) == 0 ? 3 : 1;
                    for (int x = 0; x < w; x++)
                    {
                        int i0 = ((x - 1) >> 1) & mmask;
                        int i1 = (i0 + 1) & mmask;
                        int fx = (x & 1) == 0 ? 3 : 1;
                        int a = sd[(j0 * m) + i0];
                        int b = sd[(j0 * m) + i1];
                        int c = sd[(j1 * m) + i0];
                        int d = sd[(j1 * m) + i1];
                        if (a < -1024 && b < -1024 && c < -1024 && d < -1024)
                        {
                            continue; // далеко снаружи
                        }

                        int s = (((a * (4 - fx)) + (b * fx)) * (4 - fy) + (((c * (4 - fx)) + (d * fx)) * fy)) >> 4;
                        if (s > 2048)
                        {
                            continue; // далеко внутри
                        }

                        if (lower != null)
                        {
                            int la = lower[(j0 * m) + i0];
                            int lb = lower[(j0 * m) + i1];
                            int lc = lower[(j1 * m) + i0];
                            int ld = lower[(j1 * m) + i1];
                            int ls = (((la * (4 - fx)) + (lb * fx)) * (4 - fy) + (((lc * (4 - fx)) + (ld * fx)) * fy)) >> 4;
                            if (ls < MinApartTiles << 8)
                            {
                                continue;
                            }
                        }

                        int th = 740 + (((thick.Sample(x, y) - 30000) * 2300) >> 16);
                        th = th < 620 ? 620 : (th > 1100 ? 1100 : th);
                        depthQ8[(y * w) + x] = (short)s;
                        s += ((wobble.Sample(x, y) - (LatticeNoise.One >> 1)) * WobbleQ8) >> 16;
                        band[(y * w) + x] = s >= 0 && s < th;
                    }
                }

                // Полоса не тоньше двух тайлов по каждой оси: к тайлу без соседей сверху и снизу (слева и справа)
                // добавляется сосед с той стороны, где глубже в возвышенность.
                for (int y = 0; y < w; y++)
                {
                    long dy = IntMath.Wrap(y - half, w);
                    int up = ((y - 1) & mask) * w;
                    int down = ((y + 1) & mask) * w;
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (!band[row + x])
                        {
                            continue;
                        }

                        long dx = IntMath.Wrap(x - half, w);
                        bool near = (dx * dx) + (dy * dy) <= r * r;
                        Put(t, cliff, x, y, near);
                        int xl = (x - 1) & mask;
                        int xr = (x + 1) & mask;
                        if (!band[up + x] && !band[down + x])
                        {
                            Put(t, cliff, x, depthQ8[up + x] > depthQ8[down + x] ? (y - 1) & mask : (y + 1) & mask, near);
                        }

                        if (!band[row + xl] && !band[row + xr])
                        {
                            Put(t, cliff, depthQ8[row + xl] > depthQ8[row + xr] ? xl : xr, y, near);
                        }
                    }
                }

                lower = sd;
            }

            // 5. Короткие гряды — прочь; длинные — нарезать проходами; обрубки — прочь.
            var depth = new int[w * w];
            var queue = new int[w * w];
            Runs(cliff, w, MinLineTiles, depth, queue, MacroGenerator.StageSeed(seed, Stage), t, periodic: true);
            Runs(cliff, w, MinRunTiles, depth, queue, 0, t, periodic: false);
            Runs(cliff, w, MinRunTiles, depth, queue, 0, null, periodic: false);

            for (int i = 0; i < cliff.Length; i++)
            {
                if (cliff[i])
                {
                    t.Objects[i] = ObjectId.Cliff;
                }
            }
        }

        /// <summary>Размытие квадратом (2r + 1)² по тору: скользящие суммы по строкам, затем по столбцам.</summary>
        private static int[] Blur(int[] a, int m, int r)
        {
            int mmask = m - 1;
            int n = (2 * r) + 1;
            var tmp = new int[a.Length];
            var res = new int[a.Length];
            for (int j = 0; j < m; j++)
            {
                int row = j * m;
                long sum = 0;
                for (int k = -r; k <= r; k++)
                {
                    sum += a[row + (k & mmask)];
                }

                for (int i = 0; i < m; i++)
                {
                    tmp[row + i] = (int)(sum / n);
                    sum += a[row + ((i + r + 1) & mmask)] - a[row + ((i - r) & mmask)];
                }
            }

            for (int i = 0; i < m; i++)
            {
                long sum = 0;
                for (int k = -r; k <= r; k++)
                {
                    sum += tmp[((k & mmask) * m) + i];
                }

                for (int j = 0; j < m; j++)
                {
                    res[(j * m) + i] = (int)(sum / n);
                    sum += tmp[(((j + r + 1) & mmask) * m) + i] - tmp[(((j - r) & mmask) * m) + i];
                }
            }

            return res;
        }

        /// <summary>После этапа 12 (он прорезает проходы): убрать обрубки гряд короче <see cref="MinRunTiles"/>.</summary>
        public static void DropStubs(TileWorld t)
        {
            int w = t.WorldSize;
            var cliff = new bool[w * w];
            for (int i = 0; i < cliff.Length; i++)
            {
                cliff[i] = t.Objects[i] == ObjectId.Cliff;
            }

            Runs(cliff, w, MinRunTiles, new int[w * w], new int[w * w], 0, null, periodic: false);
            for (int i = 0; i < cliff.Length; i++)
            {
                if (!cliff[i] && t.Objects[i] == ObjectId.Cliff)
                {
                    t.Objects[i] = ObjectId.Empty;
                }
            }
        }

        // Тайл обрыва, если он не у Очага, не в воде, не на берегу и не у тропы.
        private static void Put(TileWorld t, bool[] cliff, int x, int y, bool nearStart)
        {
            if (!nearStart && !Busy(t, x, y))
            {
                cliff[(y * t.WorldSize) + x] = true;
            }
        }

        /// <summary>Искривление координаты из значения поля Q16: −<see cref="WarpTiles"/>…+<see cref="WarpTiles"/> тайлов.</summary>
        private static int Warp(int q16)
        {
            int s = (int)(((long)(q16 - (LatticeNoise.One >> 1)) * 2 * ((2 * WarpTiles) + 1)) >> 16);
            return s < -WarpTiles ? -WarpTiles : (s > WarpTiles ? WarpTiles : s);
        }

        // Вода, берег, тропа или тайл рядом с тропой — обрыва нет.
        private static bool Busy(TileWorld t, int x, int y)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            if (t.Floor[(y * w) + x] != FloorId.Empty)
            {
                return true;
            }

            for (int dy = -1; dy <= 1; dy++)
            {
                int row = ((y + dy) & mask) * w;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((t.Marks[row + ((x + dx) & mask)] & TileWorld.MarkTrail) != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool[] Not(bool[] a)
        {
            var r = new bool[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                r[i] = !a[i];
            }

            return r;
        }

        /// <summary>Расширение маски кругом: ячейка в ответе, если в маске есть ячейка не дальше √<paramref name="r2"/> (тор).</summary>
        private static bool[] Near(bool[] src, int m, int r2)
        {
            int[] d2 = Dist2(src, m);
            var r = new bool[src.Length];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = d2[i] <= r2;
            }

            return r;
        }

        /// <summary>
        /// Квадрат евклидова расстояния (в ячейках) до ближайшей ячейки маски, не больше <see cref="DistCap"/>² (тор):
        /// расстояние по строке двумя проходами, затем наименьшее g² + dy² по столбцу в окне ±<see cref="DistCap"/>.
        /// </summary>
        private static int[] Dist2(bool[] src, int m)
        {
            int mmask = m - 1;
            var g = new int[m * m];
            for (int j = 0; j < m; j++)
            {
                int row = j * m;
                int run = DistCap;
                for (int k = 0; k < 2 * m; k++) // слева направо, два круга — тор
                {
                    int i = k & mmask;
                    run = src[row + i] ? 0 : (run < DistCap ? run + 1 : DistCap);
                    if (k >= m)
                    {
                        g[row + i] = run;
                    }
                }

                run = DistCap;
                for (int k = (2 * m) - 1; k >= 0; k--)
                {
                    int i = k & mmask;
                    run = src[row + i] ? 0 : (run < DistCap ? run + 1 : DistCap);
                    if (k < m && run < g[row + i])
                    {
                        g[row + i] = run;
                    }
                }
            }

            const int cap2 = DistCap * DistCap;
            var d2 = new int[m * m];
            for (int j = 0; j < m; j++)
            {
                for (int i = 0; i < m; i++)
                {
                    int best = g[(j * m) + i];
                    best *= best;
                    if (best > 0)
                    {
                        for (int dy = 1; dy <= DistCap && dy * dy < best; dy++)
                        {
                            int up = g[(((j - dy) & mmask) * m) + i];
                            int dn = g[(((j + dy) & mmask) * m) + i];
                            int lo = up < dn ? up : dn;
                            int v = (lo * lo) + (dy * dy);
                            if (v < best)
                            {
                                best = v;
                            }
                        }
                    }

                    d2[(j * m) + i] = best > cap2 ? cap2 : best;
                }
            }

            return d2;
        }

        /// <summary>Убрать связные куски значения <paramref name="value"/> (4 стороны, тор) меньше <see cref="MinAreaCells"/> ячеек.</summary>
        private static void DropSmall(bool[] a, int m, bool value)
        {
            int mmask = m - 1;
            var seen = new bool[a.Length];
            var queue = new int[a.Length];
            for (int s = 0; s < a.Length; s++)
            {
                if (a[s] != value || seen[s])
                {
                    continue;
                }

                int qt = 0;
                queue[qt++] = s;
                seen[s] = true;
                for (int qh = 0; qh < qt; qh++)
                {
                    int u = queue[qh];
                    int ui = u & mmask;
                    int uj = u / m;
                    for (int k = 0; k < 4; k++)
                    {
                        int vi = k == 0 ? (ui + 1) & mmask : k == 1 ? (ui - 1) & mmask : ui;
                        int vj = k == 2 ? (uj + 1) & mmask : k == 3 ? (uj - 1) & mmask : uj;
                        int v = (vj * m) + vi;
                        if (a[v] == value && !seen[v])
                        {
                            seen[v] = true;
                            queue[qt++] = v;
                        }
                    }
                }

                if (qt < MinAreaCells)
                {
                    for (int k = 0; k < qt; k++)
                    {
                        a[queue[k]] = !value;
                    }
                }
            }
        }

        // Обход в ширину куска гряды от тайла start (8 соседей, тор): depth — шаги; возвращает число тайлов в queue.
        private static int Bfs(bool[] cliff, int w, int start, int[] depth, int[] queue, int[] stamp, int mark)
        {
            int mask = w - 1;
            int qt = 0;
            queue[qt++] = start;
            stamp[start] = mark;
            depth[start] = 0;
            for (int qh = 0; qh < qt; qh++)
            {
                int u = queue[qh];
                int ux = u & mask;
                int uy = u / w;
                for (int k = 0; k < 9; k++)
                {
                    if (k == 4)
                    {
                        continue;
                    }

                    int v = (((uy + (k / 3) - 1) & mask) * w) + ((ux + (k % 3) - 1) & mask);
                    if (cliff[v] && stamp[v] != mark)
                    {
                        stamp[v] = mark;
                        depth[v] = depth[u] + 1;
                        queue[qt++] = v;
                    }
                }
            }

            return qt;
        }

        /// <summary>
        /// Связные гряды (8 соседей): длина — глубина обхода от конца гряды (самого дальнего тайла от первого по индексу).
        /// Гряда короче <paramref name="minTiles"/> убирается. С <paramref name="world"/> — ещё и нарезка: шаги
        /// <see cref="GapPeriod"/> − <see cref="GapTiles"/> … <see cref="GapPeriod"/> − 1 каждого периода (со сдвигом от хэша
        /// гряды) — проход; он и тайлы в двух шагах вокруг помечаются в мире.
        /// </summary>
        private static void Runs(bool[] cliff, int w, int minTiles, int[] depth, int[] queue, ulong cutSeed, TileWorld? world, bool periodic)
        {
            int mask = w - 1;
            var stamp = new int[cliff.Length];
            int mark = 0;
            for (int s = 0; s < cliff.Length; s++)
            {
                if (!cliff[s] || stamp[s] != 0)
                {
                    continue;
                }

                int qt = Bfs(cliff, w, s, depth, queue, stamp, ++mark);
                int end = queue[qt - 1];
                qt = Bfs(cliff, w, end, depth, queue, stamp, ++mark);
                int length = depth[queue[qt - 1]] + 1;
                if (length < minTiles)
                {
                    for (int k = 0; k < qt; k++)
                    {
                        cliff[queue[k]] = false;
                    }

                    continue;
                }

                if (world == null || (!periodic && length <= MaxRunTiles))
                {
                    continue;
                }

                // Сдвиг: первый проход не ближе MinRunTiles от конца гряды.
                int span = GapPeriod - GapTiles - MinRunTiles;
                int offset = (int)((SplitMix64.Hash(end & mask, end / w, cutSeed) >> 33) % (ulong)span);
                for (int k = 0; k < qt; k++)
                {
                    int c = queue[k];
                    int phase = (depth[c] + offset) % GapPeriod;
                    bool gap = periodic
                        ? phase >= GapPeriod - GapTiles && length - depth[c] > MinRunTiles
                        : depth[c] >= (length - GapTiles) / 2 && depth[c] < (length + GapTiles) / 2;
                    if (gap)
                    {
                        cliff[c] = false;
                        int cx = c & mask;
                        int cy = c / w;
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            int row = ((cy + dy) & mask) * w;
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                world.Marks[row + ((cx + dx) & mask)] |= TileWorld.MarkClear;
                            }
                        }
                    }
                }
            }
        }
    }
}
