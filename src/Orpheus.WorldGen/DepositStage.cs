using System;
using System.Collections.Generic;
using Orpheus.Core.Rng;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>Вид пятна залежи (E2-6).</summary>
    public enum DepositKind : byte
    {
        /// <summary>Медная руда (<see cref="ObjectId.Ore"/>): горы, пещеры, предгорья.</summary>
        Copper = 0,

        /// <summary>Россыпь валунов (<see cref="ObjectId.Rock"/>): луг и подножие гряд.</summary>
        Rocks = 1,

        /// <summary>Ядро гранита (<see cref="ObjectId.Granite"/>): глубина горной области.</summary>
        Granite = 2,
    }

    /// <summary>Пятно залежи: центр, радиус, вид и сколько тайлов поставлено (до этапа 12 — он может убрать часть валунов и гранита).</summary>
    public readonly struct DepositPatch
    {
        public DepositPatch(int x, int y, int radius, DepositKind kind, int tiles, bool stamped)
        {
            X = x;
            Y = y;
            Radius = radius;
            Kind = kind;
            Tiles = tiles;
            Stamped = stamped;
        }

        public int X { get; }

        public int Y { get; }

        public int Radius { get; }

        public DepositKind Kind { get; }

        public int Tiles { get; }

        /// <summary>Пятно поставлено штампом «медь в 600 от Очага», а не сеткой.</summary>
        public bool Stamped { get; }
    }

    /// <summary>
    /// Залежи пятнами (карточка E2-6, план 2.6; после <see cref="FloraStage"/>) — «точечный шум», как в Factorio.
    /// <para>
    /// Мир делится на ячейки <see cref="Cell"/> × <see cref="Cell"/>. Хэш ячейки даёт точку-центр внутри неё, броски вида и
    /// радиус. Вид решает место центра: <b>медь</b> — центр в горах или пещерах, реже — в предгорье (горы в
    /// <see cref="FoothillReach"/> тайлах); дальше <see cref="FarFrom"/> тайлов от Очага пятна чаще и крупнее.
    /// <b>Гранит</b> — центр в глубине горной области (горы со всех 8 сторон в <see cref="FoothillReach"/> тайлах).
    /// <b>Россыпь валунов</b> — центр на лугу или у гряды обрыва.
    /// </para>
    /// <para>
    /// Тайл пятна: расстояние до центра меньше радиуса с неровным краем (радиус ± <see cref="EdgeJagTiles"/> тайла по
    /// шуму ячеек 8, 4); объект ставится с вероятностью, убывающей от центра к краю (<see cref="Density"/>), бросок — хэш
    /// тайла. Не ставится: в воду и на берег, на обрыв, тропу и проход, в стартовую зону и ближе
    /// <see cref="NoDepositRadius"/> тайлов к Очагу. Медь и гранит замещают деревья и валуны, россыпь — только на пустое.
    /// </para>
    /// Залежь конечна: выкопанное не возвращается (пополнение — «потом», `06` 2.5). Только целые.
    /// </summary>
    public static class DepositStage
    {
        /// <summary>Поле неровности края (поток этапа 11).</summary>
        public const int FieldEdge = 6;

        private const int StreamCell = 120;
        private const int StreamTile = 121;

        /// <summary>Сторона ячейки сетки центров, тайлов (степень двойки: сетка замкнута по тору).</summary>
        public const int Cell = 32;

        /// <summary>Залежей нет ближе стольких тайлов к Очагу.</summary>
        public const int NoDepositRadius = 24;

        /// <summary>Дальше стольких тайлов от Очага медь чаще и крупнее.</summary>
        public const int FarFrom = 420;

        /// <summary>«Предгорье» и «глубина гор»: 8 проб биома на этом расстоянии от центра.</summary>
        public const int FoothillReach = 14;

        /// <summary>Дрожание радиуса по шуму, тайлов в каждую сторону.</summary>
        public const int EdgeJagTiles = 2;

        public static readonly int[] CellsEdge = { 8, 4 };

        /// <summary>Шансы пятна на ячейку, ‰: медь в горах и пещерах (близко / далеко), в предгорье (близко / далеко).</summary>
        public const int CopperRockyNear = 110;
        public const int CopperRockyFar = 200;
        public const int CopperFoothillNear = 50;
        public const int CopperFoothillFar = 90;

        /// <summary>Гранит: шанс на ячейку в глубине гор, ‰.</summary>
        public const int GraniteDeep = 160;

        /// <summary>Россыпь валунов: шанс на ячейку луга и ячейку у гряды, ‰.</summary>
        public const int RocksMeadow = 70;
        public const int RocksByCliff = 220;

        /// <summary>Радиус пятна, тайлов: от и до (включительно) — близко и далеко.</summary>
        public const int RadiusMin = 4;
        public const int RadiusNearMax = 7;
        public const int RadiusFarMin = 5;
        public const int RadiusMax = 9;

        /// <summary>Радиус пятна-штампа «медь в 600».</summary>
        public const int StampRadius = 6;

        private static readonly int[] DensityCenter = { 920, 760, 950 };
        private static readonly int[] DensityEdge = { 380, 320, 550 };

        /// <summary>Плотность объектов вида в центре и у края пятна, ‰ тайлов.</summary>
        public static (int Center, int Edge) Density(DepositKind kind) => (DensityCenter[(int)kind], DensityEdge[(int)kind]);

        /// <summary>Объект вида пятна.</summary>
        public static ushort ObjectOf(DepositKind kind) =>
            kind == DepositKind.Copper ? ObjectId.Ore : kind == DepositKind.Granite ? ObjectId.Granite : ObjectId.Rock;

        public static void Run(TileWorld t)
        {
            int w = t.WorldSize;
            if (w < Cell * 2)
            {
                return;
            }

            ulong seed = t.Macro.Map.Seed;
            ulong tileSeed = MacroGenerator.StageSeed(seed, TileStage.Stage);
            ulong cellSeed = SplitMix64.Hash(StreamCell, 0, tileSeed);
            FieldNoise edge = EdgeField(w, seed);
            int cells = w / Cell;
            int half = w >> 1;
            for (int cy = 0; cy < cells; cy++)
            {
                for (int cx = 0; cx < cells; cx++)
                {
                    ulong h = SplitMix64.Hash(cx, cy, cellSeed);
                    int px = (cx * Cell) + (int)(h & (Cell - 1));
                    int py = (cy * Cell) + (int)((h >> 8) & (Cell - 1));
                    long dx = IntMath.Wrap(px - half, w);
                    long dy = IntMath.Wrap(py - half, w);
                    int dist = (int)IntMath.Isqrt((dx * dx) + (dy * dy));
                    if (dist < NoDepositRadius)
                    {
                        continue;
                    }

                    bool far = dist >= FarFrom;
                    int rollCopper = (int)((h >> 16) % 1000);
                    int rollGranite = (int)((h >> 28) % 1000);
                    int rollRocks = (int)((h >> 40) % 1000);
                    int rSpan = (int)((h >> 52) & 0xFF);

                    int rocky = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        rocky += IsRocky(t, px + (Dx8[k] * FoothillReach), py + (Dy8[k] * FoothillReach)) ? 1 : 0;
                    }

                    bool centerRocky = IsRocky(t, px, py);
                    DepositKind kind;
                    if (centerRocky && rollCopper < (far ? CopperRockyFar : CopperRockyNear))
                    {
                        kind = DepositKind.Copper;
                    }
                    else if (!centerRocky && rocky > 0 && rollCopper < (far ? CopperFoothillFar : CopperFoothillNear))
                    {
                        kind = DepositKind.Copper;
                    }
                    else if (centerRocky && rocky == 8 && rollGranite < GraniteDeep)
                    {
                        kind = DepositKind.Granite;
                    }
                    else if (rollRocks < RocksMeadow && !CliffNear(t, px, py)
                             && TileRules.For(t.Biome[(py * w) + px]).Biome == Biome.Meadow) // C-016 (v8): у скалы россыпей нет — ущелья не заваливаются
                    {
                        kind = DepositKind.Rocks;
                    }
                    else
                    {
                        continue;
                    }

                    int rMin = kind == DepositKind.Copper && far ? RadiusFarMin : RadiusMin;
                    int rMax = kind == DepositKind.Copper && !far ? RadiusNearMax : RadiusMax;
                    if (kind == DepositKind.Rocks)
                    {
                        rMax = RadiusMax - 1;
                    }

                    int radius = rMin + (rSpan % (rMax - rMin + 1));
                    if (kind != DepositKind.Rocks)
                    {
                        ClearRock(t, edge, px, py, radius + RockClearMargin); // C-016: жила в массиве — в своей выемке
                    }

                    int placed = Place(t, edge, tileSeed, px, py, radius, kind, forced: false);
                    if (placed > 0)
                    {
                        t.Deposits.Add(new DepositPatch(px, py, radius, kind, placed, stamped: false));
                    }
                }
            }
        }

        /// <summary>C-016 (v8): медь и гранит в скальном массиве лежат в выемке — скала убирается на столько тайлов шире пятна.</summary>
        public const int RockClearMargin = 2;

        /// <summary>Убрать скалу массива (обрыв) в круге радиуса <paramref name="radius"/> с неровным краем пятна.</summary>
        public static void ClearRock(TileWorld t, FieldNoise edge, int cx, int cy, int radius)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            int reach = radius + EdgeJagTiles;
            for (int oy = -reach; oy <= reach; oy++)
            {
                for (int ox = -reach; ox <= reach; ox++)
                {
                    int x = (cx + ox) & mask;
                    int y = (cy + oy) & mask;
                    int j = (y * w) + x;
                    if (t.Objects[j] != ObjectId.Cliff)
                    {
                        continue;
                    }

                    int jag = (int)(((long)(edge.Sample(x, y) - (LatticeNoise.One >> 1)) * EdgeJagTiles * 2 * 256) >> 16);
                    int d256 = (int)IntMath.Isqrt((long)((ox * ox) + (oy * oy)) << 16);
                    if (d256 < (radius * 256) + jag)
                    {
                        t.Objects[j] = ObjectId.Empty;
                    }
                }
            }

            // Пометка «у скалы» в выемке и вокруг — заново по оставшейся скале.
            for (int oy = -reach - 1; oy <= reach + 1; oy++)
            {
                for (int ox = -reach - 1; ox <= reach + 1; ox++)
                {
                    int x = (cx + ox) & mask;
                    int y = (cy + oy) & mask;
                    int j = (y * w) + x;
                    if ((t.Marks[j] & TileWorld.MarkGorge) == 0)
                    {
                        continue;
                    }

                    bool near = false;
                    for (int k = 0; k < 9 && !near; k++)
                    {
                        near = k != 4 && t.Objects[(((y + (k / 3) - 1) & mask) * w) + ((x + (k % 3) - 1) & mask)] == ObjectId.Cliff;
                    }

                    if (!near)
                    {
                        t.Marks[j] &= unchecked((byte)~TileWorld.MarkGorge);
                    }
                }
            }
        }

        public static FieldNoise EdgeField(int w, ulong seed) =>
            new FieldNoise(MacroGenerator.FieldSeed(seed, TileStage.Stage, FieldEdge), w, CellsEdge, ridged: false);

        /// <summary>
        /// Поставить пятно с центром (cx, cy). <paramref name="forced"/> — штамп у Очага: берег и тропа не мешают
        /// (воду под пятном штамп убрал заранее). Возвращает число поставленных объектов.
        /// </summary>
        public static int Place(TileWorld t, FieldNoise edge, ulong tileSeed, int cx, int cy, int radius, DepositKind kind, bool forced)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            int half = w >> 1;
            ushort obj = ObjectOf(kind);
            int center = DensityCenter[(int)kind];
            int rim = DensityEdge[(int)kind];
            ulong rollSeed = SplitMix64.Hash(StreamTile, (int)kind, tileSeed);
            long noStart2 = (long)NoDepositRadius * NoDepositRadius;
            int reach = radius + EdgeJagTiles;
            int placed = 0;
            for (int oy = -reach; oy <= reach; oy++)
            {
                for (int ox = -reach; ox <= reach; ox++)
                {
                    int x = (cx + ox) & mask;
                    int y = (cy + oy) & mask;
                    int j = (y * w) + x;
                    int jag = (int)(((long)(edge.Sample(x, y) - (LatticeNoise.One >> 1)) * EdgeJagTiles * 2 * 256) >> 16);
                    int r256 = (radius * 256) + jag;
                    int d256 = (int)IntMath.Isqrt((long)((ox * ox) + (oy * oy)) << 16);
                    if (r256 <= 0 || d256 >= r256)
                    {
                        continue;
                    }

                    int density = center - (int)((long)(center - rim) * d256 / r256);
                    if ((int)((SplitMix64.Hash(x, y, rollSeed) >> 32) % 1000) >= density)
                    {
                        continue;
                    }

                    ushort was = t.Objects[j];
                    if (was == ObjectId.Cliff || was == ObjectId.Ore || !FloorId.IsPassable(t.Floor[j]) || TileWorld.InStartZone(w, x, y))
                    {
                        continue;
                    }

                    long sx = IntMath.Wrap(x - half, w);
                    long sy = IntMath.Wrap(y - half, w);
                    if ((sx * sx) + (sy * sy) < noStart2)
                    {
                        continue;
                    }

                    if (!forced && (t.Marks[j] != 0 || t.Shore[j] != WaterStage.ShoreFar))
                    {
                        continue;
                    }

                    if (kind == DepositKind.Rocks && was != ObjectId.Empty)
                    {
                        continue; // россыпь не валит лес
                    }

                    if (kind == DepositKind.Granite && !IsRocky(t, x, y))
                    {
                        continue; // гранит — только на каменистой земле гор и пещер
                    }

                    t.Objects[j] = obj;
                    placed++;
                }
            }

            return placed;
        }

        private static readonly int[] Dx8 = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] Dy8 = { 0, 1, 1, 1, 0, -1, -1, -1 };

        private static bool IsRocky(TileWorld t, int x, int y)
        {
            int w = t.WorldSize;
            byte b = TileRules.For(t.Biome[((y & (w - 1)) * w) + (x & (w - 1))]).Biome;
            return b == Biome.Mountain || b == Biome.Cave;
        }

        // Гряда обрыва рядом с точкой: 8 направлений на расстоянии 4 и 8 тайлов.
        private static bool CliffNear(TileWorld t, int x, int y)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            for (int k = 0; k < 8; k++)
            {
                for (int d = 4; d <= 8; d += 4)
                {
                    if (t.Objects[(((y + (Dy8[k] * d)) & mask) * w) + ((x + (Dx8[k] * d)) & mask)] == ObjectId.Cliff)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Числа залежей (E2-6): связные куски (8 соседей) тайлов одного объекта и «пятнистость» — доля тайлов, у которых
    /// среди 8 соседей не меньше двух таких же. Считают тесты и <c>gen_preview --map</c>.
    /// </summary>
    public sealed class DepositStats
    {
        /// <summary>Тайлов объекта.</summary>
        public long Tiles;

        /// <summary>Тайлов, у которых ≥ 2 соседей (из 8) — тот же объект.</summary>
        public long Clustered;

        /// <summary>Связных кусков не меньше <see cref="PatchMinTiles"/> тайлов — «пятен».</summary>
        public int Patches;

        /// <summary>Тайлов в самом большом и самом малом пятне; тайлов вне пятен (одиночки и крошки).</summary>
        public int Largest;
        public int Smallest;
        public long Loose;

        /// <summary>Наибольший размах пятна по оси, тайлов.</summary>
        public int WidestSpan;

        /// <summary>Тайлов объекта в воде, на берегу-воде, в <see cref="DepositStage.NoDepositRadius"/> от Очага (должно быть 0).</summary>
        public long OnWater;
        public long NearStart;

        /// <summary>Тайлов в <see cref="HearthStage.CopperWithin"/> от Очага.</summary>
        public long Within600;

        public const int PatchMinTiles = 12;

        public double ClusteredPercent => Tiles == 0 ? 100.0 : 100.0 * Clustered / Tiles;

        /// <summary>Числа объекта <paramref name="obj"/>; <paramref name="only"/> — считать только тайлы с этой пометкой (null — все).</summary>
        public static DepositStats Of(TileWorld t, ushort obj, bool[]? only = null)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            int half = w >> 1;
            int n = w * w;
            var s = new DepositStats { Smallest = int.MaxValue };
            var seen = new bool[n];
            var stack = new Stack<int>();
            long near2 = (long)DepositStage.NoDepositRadius * DepositStage.NoDepositRadius;
            long r600 = (long)HearthStage.CopperWithin * HearthStage.CopperWithin;
            for (int i = 0; i < n; i++)
            {
                if (t.Objects[i] != obj || (only != null && !only[i]))
                {
                    continue;
                }

                int x = i & mask;
                int y = i / w;
                s.Tiles++;
                s.OnWater += FloorId.IsPassable(t.Floor[i]) ? 0 : 1;
                long dx = IntMath.Wrap(x - half, w);
                long dy = IntMath.Wrap(y - half, w);
                long d2 = (dx * dx) + (dy * dy);
                s.NearStart += d2 < near2 ? 1 : 0;
                s.Within600 += d2 <= r600 ? 1 : 0;
                int around = 0;
                for (int k = 0; k < 9; k++)
                {
                    int j = (((y + (k / 3) - 1) & mask) * w) + ((x + (k % 3) - 1) & mask);
                    if (k != 4 && t.Objects[j] == obj && (only == null || only[j]))
                    {
                        around++;
                    }
                }

                s.Clustered += around >= 2 ? 1 : 0;
                if (seen[i])
                {
                    continue;
                }

                // Кусок: обход в глубину по 8 соседям; размах считается от первого тайла (кусок меньше половины мира).
                int size = 0;
                int minX = 0, maxX = 0, minY = 0, maxY = 0;
                seen[i] = true;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int u = stack.Pop();
                    size++;
                    int ux = u & mask;
                    int uy = u / w;
                    int rx = IntMath.Wrap(ux - x, w);
                    int ry = IntMath.Wrap(uy - y, w);
                    minX = Math.Min(minX, rx);
                    maxX = Math.Max(maxX, rx);
                    minY = Math.Min(minY, ry);
                    maxY = Math.Max(maxY, ry);
                    for (int k = 0; k < 9; k++)
                    {
                        int v = (((uy + (k / 3) - 1) & mask) * w) + ((ux + (k % 3) - 1) & mask);
                        if (k != 4 && !seen[v] && t.Objects[v] == obj && (only == null || only[v]))
                        {
                            seen[v] = true;
                            stack.Push(v);
                        }
                    }
                }

                if (size >= PatchMinTiles)
                {
                    s.Patches++;
                    s.Largest = Math.Max(s.Largest, size);
                    s.Smallest = Math.Min(s.Smallest, size);
                    s.WidestSpan = Math.Max(s.WidestSpan, Math.Max(maxX - minX, maxY - minY) + 1);
                }
                else
                {
                    s.Loose += size;
                }
            }

            if (s.Patches == 0)
            {
                s.Smallest = 0;
            }

            return s;
        }

        /// <summary>Пометка тайлов внутри кругов пятен вида <paramref name="kind"/> (радиус + дрожание края).</summary>
        public static bool[] MaskOf(TileWorld t, DepositKind kind)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            var m = new bool[w * w];
            foreach (DepositPatch p in t.Deposits)
            {
                if (p.Kind != kind)
                {
                    continue;
                }

                int r = p.Radius + DepositStage.EdgeJagTiles;
                for (int oy = -r; oy <= r; oy++)
                {
                    for (int ox = -r; ox <= r; ox++)
                    {
                        if ((ox * ox) + (oy * oy) <= r * r)
                        {
                            m[(((p.Y + oy) & mask) * w) + ((p.X + ox) & mask)] = true;
                        }
                    }
                }
            }

            return m;
        }
    }
}
