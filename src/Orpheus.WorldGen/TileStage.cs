using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Rng;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>Мир на тайлах между этапами 11 и 12: биом тайла, пол, объект — плоские массивы [y · W + x].</summary>
    public sealed class TileWorld
    {
        public TileWorld(MacroWorld macro)
        {
            Macro = macro;
            WorldSize = macro.Map.WorldSize;
            int n = WorldSize * WorldSize;
            Biome = new byte[n];
            Floor = new byte[n];
            Objects = new ushort[n];
        }

        public MacroWorld Macro { get; }

        public int WorldSize { get; }

        /// <summary>Биом тайла — ячейка макрокарты со сдвигом деталью (граница с деталью, `01` 2 «Чанки»).</summary>
        public byte[] Biome { get; }

        public byte[] Floor { get; }

        public ushort[] Objects { get; }

        /// <summary>Этап 7: поставлен штамп леса.</summary>
        public bool ForestStamped { get; set; }

        /// <summary>Этап 7: центр штампа меди (null — своей меди хватило).</summary>
        public TileCoord? CopperStamp { get; set; }

        /// <summary>Этап 12: сколько областей присоединено и сколько тайлов прокопано.</summary>
        public int ComponentsJoined { get; set; }

        public int TilesCarved { get; set; }

        /// <summary>Тайл в стартовой зоне 32 × 32 у Очага (координаты свёрнуты): пусто, трава — там появляются игроки и боты.</summary>
        public static bool InStartZone(int worldSize, int x, int y)
        {
            int x0 = (worldSize >> 1) - (GeneratorV1.StartZoneSize >> 1);
            return (uint)(x - x0) < GeneratorV1.StartZoneSize && (uint)(y - x0) < GeneratorV1.StartZoneSize;
        }
    }

    /// <summary>
    /// Этап 11 «Чанки» (карточка 1-017; docs/01, 2 «Чанки (этап 11)», 3 «Пол и преграды»): по ячейкам макрокарты — пол,
    /// скальная масса и объекты; граница биомов с деталью.
    /// <para>
    /// <b>Биом тайла</b> — ячейка, в которую попадает тайл, сдвинутый деталью на −6…+6 по каждой оси (поле «Деталь границ»,
    /// ячейка 32, 2 октавы — ось x; второе поле той же формы — ось y; <see cref="Shift"/>). Сдвиг и номер ячейки сворачиваются тором.
    /// </para>
    /// <para>
    /// <b>Скальная масса</b> (горы 45 %, пещеры 35 % площади) — тайлы биома с наибольшим шумом массы (ячейки 32, 16): доля
    /// набирается точно, по гистограмме значений тайлов этого биома; так получаются хребты и гряды шириной 10–40, а не
    /// россыпь. Верх массы — гранит (ядро хребта), остальное — скала.
    /// </para>
    /// <para>
    /// <b>Объекты</b> — hash(зерно, этап 11, x, y) против суммы плотностей (`01` 2 «Чанки»): сначала залежи (на любой тайл
    /// биома), потом рассыпанные (только вне массы). Стартовая зона 32 × 32 — трава без объектов.
    /// </para>
    /// Только целые; гистограммы вместо сортировки — результат не зависит от порядка обхода.
    /// </summary>
    public static class TileStage
    {
        /// <summary>Номер этапа (`01` 2).</summary>
        public const int Stage = 11;

        /// <summary>Поля этапа 11 (поток этапа 11): шум скальной массы и вторая ось детали.</summary>
        public const int FieldRockMass = 1;

        /// <summary>Наибольший сдвиг границы деталью, тайлов (`01` 2: «со сдвигом деталью ±6»).</summary>
        public const int DetailShift = 6;

        /// <summary>Октавы шума скальной массы.</summary>
        public static readonly int[] CellsRockMass = { 32, 16 };

        /// <summary>Деталь границ по x — поле этапа 2 (<see cref="MacroGenerator.FieldDetail"/>).</summary>
        public static FieldNoise CreateDetailX(int w, ulong seed) => MacroGenerator.CreateDetail(w, seed);

        /// <summary>Деталь границ по y — поле той же формы, номер <see cref="MacroGenerator.FieldDetailY"/> (допущение 1-017).</summary>
        public static FieldNoise CreateDetailY(int w, ulong seed) =>
            new FieldNoise(MacroGenerator.FieldSeed(seed, MacroGenerator.StageFields, MacroGenerator.FieldDetailY), w, MacroGenerator.CellsDetail, ridged: false);

        public static FieldNoise CreateRockMass(int w, ulong seed) =>
            new FieldNoise(MacroGenerator.FieldSeed(seed, Stage, FieldRockMass), w, CellsRockMass, ridged: false);

        /// <summary>
        /// Сдвиг деталью из значения поля Q16: (v − ½) × 2 × 13 → −6…+6 с обрезкой. Усиление ×2 — потому что сумма двух октав
        /// value-шума редко выходит из 0,2…0,8: без него сдвиг почти всегда в пределах ±2 и граница остаётся ступенькой ячеек.
        /// </summary>
        public static int Shift(int q16)
        {
            int s = (int)(((long)(q16 - (LatticeNoise.One >> 1)) * 2 * (2 * DetailShift + 1)) >> 16);
            return s < -DetailShift ? -DetailShift : (s > DetailShift ? DetailShift : s);
        }

        /// <summary>Биом тайла (x, y) по макрокарте: ячейка тайла (x + сдвиг x, y + сдвиг y), всё по тору.</summary>
        public static byte TileBiome(MacroMap map, FieldNoise detailX, FieldNoise detailY, int x, int y)
        {
            int mask = map.WorldSize - 1;
            int sx = (x + Shift(detailX.Sample(x, y))) & mask;
            int sy = (y + Shift(detailY.Sample(x, y))) & mask;
            return map.Biome[((sy >> MacroMap.CellBits) * map.Size) + (sx >> MacroMap.CellBits)];
        }

        /// <summary>Этап 11 по макрокарте (после этапа 7): биом, пол и объекты тайлов.</summary>
        public static void Run(TileWorld t)
        {
            MacroMap map = t.Macro.Map;
            int w = t.WorldSize;
            ulong seed = map.Seed;
            FieldNoise detailX = CreateDetailX(w, seed);
            FieldNoise detailY = CreateDetailY(w, seed);
            FieldNoise mass = CreateRockMass(w, seed);
            var massValue = new ushort[w * w];

            // Проход 1: биом тайла; для биомов со скальной массой — шум массы и гистограмма по биому.
            var hist = new int[Biome.Count][];
            for (int y = 0; y < w; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte b = TileBiome(map, detailX, detailY, x, y);
                    t.Biome[row + x] = b;
                    if (TileRules.For(b).RockSharePercent > 0 && !TileWorld.InStartZone(w, x, y))
                    {
                        int v = mass.Sample(x, y);
                        massValue[row + x] = (ushort)v;
                        (hist[b] ??= new int[LatticeNoise.One])[v]++;
                    }
                }
            }

            // Пороги: масса — значения ≥ rock[b], гранит — ≥ granite[b] (доли считаются от тайлов биома вне стартовой зоны).
            var rockFrom = new int[Biome.Count];
            var graniteFrom = new int[Biome.Count];
            for (int b = 0; b < Biome.Count; b++)
            {
                rockFrom[b] = int.MaxValue;
                graniteFrom[b] = int.MaxValue;
                if (hist[b] == null)
                {
                    continue;
                }

                BiomeTileRule rule = TileRules.For((byte)b);
                long total = 0;
                foreach (int c in hist[b]!)
                {
                    total += c;
                }

                long rockCount = (total * rule.RockSharePercent + 50) / 100;
                long graniteCount = (rockCount * rule.GraniteOfRockPercent + 50) / 100;
                rockFrom[b] = Threshold(hist[b]!, rockCount);
                graniteFrom[b] = Threshold(hist[b]!, graniteCount);
            }

            // Проход 2: пол и объекты.
            ulong tileSeed = MacroGenerator.StageSeed(seed, Stage);
            for (int y = 0; y < w; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    byte b = t.Biome[i];
                    BiomeTileRule rule = TileRules.For(b);
                    t.Floor[i] = rule.Floor;
                    if (TileWorld.InStartZone(w, x, y))
                    {
                        t.Floor[i] = FloorId.Grass;
                        continue;
                    }

                    ushort obj = ObjectId.Empty;
                    bool inMass = false;
                    if (rule.RockSharePercent > 0 && massValue[i] >= rockFrom[b])
                    {
                        inMass = true;
                        obj = massValue[i] >= graniteFrom[b] ? ObjectId.Granite : ObjectId.Rock;
                    }

                    int u = Roll(SplitMix64.Hash(x, y, tileSeed));
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

                    t.Objects[i] = obj;
                }
            }
        }

        /// <summary>Число 0…99 999 из старших 32 бит хэша тайла (равномерно с точностью 2⁻³²).</summary>
        public static int Roll(ulong hash) => (int)(((hash >> 32) * 100000UL) >> 32);

        /// <summary>
        /// Порог по гистограмме: наименьшее значение v такое, что тайлов со значением ≥ v не больше <paramref name="count"/>
        /// и набор сверху вниз ровно до него (равные значения входят целиком, поэтому доля точна до ширины одного значения Q16).
        /// count = 0 — порог выше всех значений.
        /// </summary>
        public static int Threshold(int[] hist, long count)
        {
            long acc = 0;
            int v = hist.Length;
            while (v > 0 && acc + hist[v - 1] <= count)
            {
                v--;
                acc += hist[v];
            }

            return v == hist.Length ? int.MaxValue : v;
        }
    }
}
