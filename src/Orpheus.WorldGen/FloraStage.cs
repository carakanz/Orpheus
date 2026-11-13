using System;
using Orpheus.Core.Rng;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Пол и объекты тайлов (карточка E1-2, план 1.2–1.3; вторая половина этапа 11) — после воды, троп и обрывов.
    /// <list type="bullet">
    /// <item><b>Земля.</b> Луг — трава; пятна сухой травы (верхние <see cref="DryPerMille"/> ‰ шума пятен, ячейки 32, 16) и
    /// земли (нижние <see cref="DirtPerMille"/> ‰). Лес — подстилка, на полянах — трава. Горы и пещеры — камень, в горах
    /// пятна земли. Тропы и проходы в обрывах — земля. Берег (песок) и вода положены раньше.</item>
    /// <item><b>Лес.</b> Плотность деревьев тайла = <see cref="ForestCorePerMille"/> × опушка × поляна: опушка редеет на
    /// <see cref="EdgeTiles"/> тайлов от края леса (и от берега), поляны — верхние <see cref="GladePerMille"/> ‰ шума полян
    /// (ячейки 64, 32) с редеющей каймой, на тропах деревьев нет. Дерево стоит, если «ранг» тайла в шуме куртин
    /// (ячейки 8, 4; ранг — доля тайлов леса с меньшим значением) выше 1 − плотность: деревья растут куртинами по 4–10 с
    /// ходами между ними, а не солью. Ранг считается по гистограмме, поэтому средняя плотность равна заданной.</item>
    /// <item><b>Скалы.</b> Горы — кучки валунов (мелкий шум массы), пещеры — гряды (крупный); залежи — как раньше.</item>
    /// <item><b>Кучки</b> (<see cref="BiomeTileRule.Groups"/>): деревья лугов по 2–6, кусты, валуны.</item>
    /// </list>
    /// Виды деревьев в данных не хранятся: один <see cref="ObjectId.Tree"/>, вид и вариант спрайта клиент берёт по хэшу
    /// координаты и биому. Только целые; гистограммы вместо сортировки.
    /// </summary>
    public static class FloraStage
    {
        public const int FieldGlade = 2;
        public const int FieldPatch = 3;
        public const int FieldClump = 4;
        public const int FieldFineMass = 5;

        /// <summary>Потоки хэшей тайла (номер «x» в <see cref="SplitMix64.Hash"/> зерна этапа).</summary>
        private const int StreamJitter = 101;
        private const int StreamGroup = 110;

        public static readonly int[] CellsGlade = { 64, 32 };
        public static readonly int[] CellsPatch = { 32, 16, 8 };
        public static readonly int[] CellsClump = { 8, 4 };
        public static readonly int[] CellsFineMass = { 16, 8 };

        /// <summary>Плотность деревьев в глубине леса, ‰ тайлов.</summary>
        public const int ForestCorePerMille = 600;

        /// <summary>Опушка: плотность растёт от <see cref="EdgeStartPerMille"/> у края до полной за столько тайлов.</summary>
        public const int EdgeTiles = 10;

        public const int EdgeStartPerMille = 450;

        /// <summary>Поляны — доля тайлов леса, ‰.</summary>
        public const int GladePerMille = 120;

        /// <summary>Кайма поляны: плотность растёт от 0 до полной на этом отрезке шума полян, Q16.</summary>
        public const int GladeMargin = 2500;

        /// <summary>Разброс ранга тайла (чтобы край куртины не был гладким), ‰.</summary>
        public const int RankJitter = 150;

        /// <summary>Сухая трава — доля тайлов луга, ‰; земля пятнами — луг и горы.</summary>
        public const int DryPerMille = 150;

        public const int DirtPerMille = 30;
        public const int MountainDirtPerMille = 80;

        public static void Run(TileWorld t)
        {
            int w = t.WorldSize;
            int n = w * w;
            ulong seed = t.Macro.Map.Seed;
            ulong tileSeed = MacroGenerator.StageSeed(seed, TileStage.Stage);
            ushort[] glade = GridField.Upsample(Field(w, seed, FieldGlade, CellsGlade), w);
            ushort[] patch = GridField.Upsample(Field(w, seed, FieldPatch, CellsPatch), w);
            FieldNoise clump = Field(w, seed, FieldClump, CellsClump);
            FieldNoise massCoarse = TileStage.CreateRockMass(w, seed);
            FieldNoise massFine = Field(w, seed, FieldFineMass, CellsFineMass);
            byte[] edge = ForestEdge(t);

            // Проход 1: шумы на тайл и гистограммы по биому.
            var value = new ushort[n]; // лес — шум куртин; биом с массой — шум массы
            var hist = new int[Biome.Count][];
            var patchHist = new int[Biome.Count][];
            var gladeHist = new int[LatticeNoise.One];
            for (int y = 0; y < w; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    byte b = t.Biome[i];
                    if (TileWorld.InStartZone(w, x, y))
                    {
                        continue;
                    }

                    BiomeTileRule rule = TileRules.For(b);
                    (patchHist[b] ??= new int[LatticeNoise.One])[patch[i]]++;
                    if (rule.Biome == Biome.Forest)
                    {
                        int v = clump.Sample(x, y);
                        value[i] = (ushort)v;
                        (hist[b] ??= new int[LatticeNoise.One])[v]++;
                        gladeHist[glade[i]]++;
                    }
                    else if (rule.RockSharePercent > 0)
                    {
                        int v = (rule.FineMass ? massFine : massCoarse).Sample(x, y);
                        value[i] = (ushort)v;
                        (hist[b] ??= new int[LatticeNoise.One])[v]++;
                    }
                }
            }

            // Пороги.
            var rockFrom = new int[Biome.Count];
            var graniteFrom = new int[Biome.Count];
            var dryFrom = new int[Biome.Count];
            var dirtBelow = new int[Biome.Count];
            ushort[]? rank = null; // ранг значения шума куртин среди тайлов леса, ‰
            int gladeFrom = int.MaxValue;
            for (int b = 0; b < Biome.Count; b++)
            {
                rockFrom[b] = int.MaxValue;
                graniteFrom[b] = int.MaxValue;
                dryFrom[b] = int.MaxValue;
                dirtBelow[b] = 0;
                BiomeTileRule rule = TileRules.For((byte)b);
                if (patchHist[b] != null && rule.Biome == b)
                {
                    long total = Sum(patchHist[b]!);
                    if (b == Biome.Meadow)
                    {
                        dryFrom[b] = TileStage.Threshold(patchHist[b]!, total * DryPerMille / 1000);
                        dirtBelow[b] = GridField.QuantileBelow(patchHist[b]!, total * DirtPerMille / 1000);
                    }
                    else if (b == Biome.Mountain)
                    {
                        dirtBelow[b] = GridField.QuantileBelow(patchHist[b]!, total * MountainDirtPerMille / 1000);
                    }
                }

                if (hist[b] == null)
                {
                    continue;
                }

                long count = Sum(hist[b]!);
                if (b == Biome.Forest)
                {
                    rank = new ushort[LatticeNoise.One];
                    long below = 0;
                    for (int v = 0; v < LatticeNoise.One; v++)
                    {
                        rank[v] = (ushort)(below * 1000 / count);
                        below += hist[b]![v];
                    }

                    gladeFrom = TileStage.Threshold(gladeHist, count * GladePerMille / 1000);
                }
                else
                {
                    long rockCount = ((count * rule.RockSharePercent) + 50) / 100;
                    long graniteCount = ((rockCount * rule.GraniteOfRockPercent) + 50) / 100;
                    rockFrom[b] = TileStage.Threshold(hist[b]!, rockCount);
                    graniteFrom[b] = TileStage.Threshold(hist[b]!, graniteCount);
                }
            }

            // Проход 2: пол и объекты.
            ulong jitterSeed = SplitMix64.Hash(StreamJitter, 0, tileSeed);
            for (int y = 0; y < w; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    byte b = t.Biome[i];
                    BiomeTileRule rule = TileRules.For(b);
                    if (TileWorld.InStartZone(w, x, y))
                    {
                        t.Floor[i] = FloorId.Grass;
                        t.Objects[i] = ObjectId.Empty;
                        continue;
                    }

                    bool wet = t.Floor[i] != FloorId.Empty; // вода или берег
                    bool path = t.Marks[i] != 0;
                    bool isForest = rule.Biome == Biome.Forest;
                    bool inGlade = isForest && glade[i] >= gladeFrom;
                    if (!wet)
                    {
                        byte floor = rule.Floor;
                        if (path)
                        {
                            floor = FloorId.Dirt;
                        }
                        else if (inGlade)
                        {
                            floor = FloorId.Grass;
                        }
                        else if (patch[i] >= dryFrom[b])
                        {
                            floor = FloorId.DryGrass;
                        }
                        else if (patch[i] < dirtBelow[b])
                        {
                            floor = FloorId.Dirt;
                        }

                        t.Floor[i] = floor;
                    }

                    if (wet || path || t.Objects[i] != ObjectId.Empty)
                    {
                        continue; // вода, берег, тропа, проход, обрыв
                    }

                    ushort obj = ObjectId.Empty;
                    bool inMass = false;
                    if (rule.RockSharePercent > 0 && value[i] >= rockFrom[b])
                    {
                        inMass = true;
                        obj = value[i] >= graniteFrom[b] ? ObjectId.Granite : ObjectId.Rock;
                    }

                    int u = TileStage.Roll(SplitMix64.Hash(x, y, tileSeed));
                    int acc = 0;
                    bool placed = false;
                    for (int k = 0; k < rule.Deposits.Count && !placed; k++)
                    {
                        acc += rule.Deposits[k].Per100k;
                        if (u < acc)
                        {
                            obj = rule.Deposits[k].Object;
                            placed = true;
                        }
                    }

                    if (!inMass)
                    {
                        for (int k = 0; k < rule.Scatter.Count && !placed; k++)
                        {
                            acc += rule.Scatter[k].Per100k;
                            if (u < acc)
                            {
                                obj = rule.Scatter[k].Object;
                                placed = true;
                            }
                        }
                    }

                    if (isForest && !placed && !inGlade && rank != null)
                    {
                        int gladeF = (int)Math.Min(1000L, ((long)gladeFrom - glade[i]) * 1000 / GladeMargin);
                        int edgeF = edge[i] >= EdgeTiles ? 1000 : EdgeStartPerMille + ((1000 - EdgeStartPerMille) * edge[i] / EdgeTiles);
                        int density = ForestCorePerMille * edgeF / 1000 * gladeF / 1000;
                        int jitter = (int)((SplitMix64.Hash(x, y, jitterSeed) >> 40) % (2 * RankJitter + 1)) - RankJitter;
                        if (density > 0 && rank[value[i]] + jitter >= 1000 - density)
                        {
                            obj = ObjectId.Tree;
                        }
                    }

                    t.Objects[i] = obj;
                }
            }

            Groups(t, tileSeed);
        }

        private static FieldNoise Field(int w, ulong seed, int field, int[] cells) =>
            new FieldNoise(MacroGenerator.FieldSeed(seed, TileStage.Stage, field), w, cells, ridged: false);

        private static long Sum(int[] hist)
        {
            long total = 0;
            foreach (int c in hist)
            {
                total += c;
            }

            return total;
        }

        /// <summary>Расстояние тайла леса до края леса (другой биом, вода, берег) по 4 сторонам, 1…<see cref="EdgeTiles"/> (дальше — <see cref="EdgeTiles"/>); вне леса — 0.</summary>
        private static byte[] ForestEdge(TileWorld t)
        {
            int w = t.WorldSize;
            int n = w * w;
            int mask = w - 1;
            int shift = IntMath.Log2(w);
            var dist = new byte[n];
            var queue = new int[n];
            int qt = 0;
            for (int i = 0; i < n; i++)
            {
                if (TileRules.For(t.Biome[i]).Biome == Biome.Forest && t.Floor[i] == FloorId.Empty)
                {
                    dist[i] = EdgeTiles;
                }
                else
                {
                    queue[qt++] = i;
                }
            }

            int sources = qt;
            for (int qh = 0; qh < qt; qh++)
            {
                int u = queue[qh];
                int du = qh < sources ? 0 : dist[u];
                if (du + 1 >= EdgeTiles)
                {
                    continue;
                }

                int ux = u & mask;
                int uy = u >> shift;
                for (int k = 0; k < 4; k++)
                {
                    int vx = k == 0 ? (ux + 1) & mask : k == 1 ? (ux - 1) & mask : ux;
                    int vy = k == 2 ? (uy + 1) & mask : k == 3 ? (uy - 1) & mask : uy;
                    int v = (vy << shift) | vx;
                    if (dist[v] > du + 1)
                    {
                        dist[v] = (byte)(du + 1);
                        queue[qt++] = v;
                    }
                }
            }

            return dist;
        }

        // Кучки: центр — по хэшу тайла; объекты — центр и случайные тайлы квадрата 5 × 5 вокруг, только на свободную сушу.
        private static void Groups(TileWorld t, ulong tileSeed)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            for (int y = 0; y < w; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    BiomeTileRule rule = TileRules.For(t.Biome[row + x]);
                    for (int g = 0; g < rule.Groups.Count; g++)
                    {
                        GroupRow group = rule.Groups[g];
                        ulong h = SplitMix64.Hash(x, y, SplitMix64.Hash(StreamGroup + g, rule.Biome, tileSeed));
                        if (TileStage.Roll(h) >= group.CentersPer100k)
                        {
                            continue;
                        }

                        int count = group.Min + (int)((h & 0xFFFF) % (ulong)(group.Max - group.Min + 1));
                        ulong bits = SplitMix64.Hash(y, x, h);
                        for (int k = 0; k < count; k++)
                        {
                            int ox = k == 0 ? 0 : (int)((bits >> (k * 8)) & 0xF) % 5 - 2;
                            int oy = k == 0 ? 0 : (int)((bits >> ((k * 8) + 4)) & 0xF) % 5 - 2;
                            int tx = (x + ox) & mask;
                            int ty = (y + oy) & mask;
                            int j = (ty * w) + tx;
                            if (t.Objects[j] == ObjectId.Empty && t.Marks[j] == 0 && t.Shore[j] == WaterStage.ShoreFar
                                && !TileWorld.InStartZone(w, tx, ty))
                            {
                                t.Objects[j] = group.Object;
                            }
                        }
                    }
                }
            }
        }
    }
}
