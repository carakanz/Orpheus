using System;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Этап 7 «Очаг» (карточка 1-017; docs/01, 2 «Этапы», 7): стартовая зона у (W/2, W/2).
    /// <list type="bullet">
    /// <item>луга Ø 300: ячейки с d ≤ 150 — луга (как <c>one_world2</c> модели: <c>b[d &lt;= 150] = MEADOW</c>);</item>
    /// <item>лес в 200: нет ячейки леса с d ≤ 200 — штамп: круг r = 24 вокруг ячейки с наибольшей m среди 150 &lt; d ≤ 200
    /// (вне лугов Очага);</item>
    /// <item>медь в 600: после этапа 11 залежей меди в 600 меньше 20 — штамп-пятно радиуса 6 (E2-6) (<see cref="StampCopper"/>);</item>
    /// <item>медь у старта (C-027, v9; слово Автора 17.11 «Я не нашел ни 1 куска меди»): 2–3 выхода меди на лугу в
    /// <see cref="NearCopperMin"/>…<see cref="NearCopperMax"/> тайлах от Очага, в разные стороны, на поляне без деревьев
    /// (<see cref="StampNearCopper"/>);</item>
    /// <item>озеро в 150 — не в вехе 1 (вода — этап 5, веха 2; допущение `[док]` 1-017); пещеры в 900 — на 1024² это весь мир,
    /// пещеры среза есть всегда (8 % по построению этапа 3), штамп не нужен.</item>
    /// </list>
    /// Случайных чисел нет: выбор места — по ключу с равенством по индексу.
    /// </summary>
    public static class HearthStage
    {
        /// <summary>Номер этапа (`01` 2).</summary>
        public const int Stage = 7;

        /// <summary>Луга вокруг Очага: d ≤ 150 (Ø 300).</summary>
        public const int MeadowRadius = 150;

        /// <summary>Лес — не дальше 200 от Очага.</summary>
        public const int ForestWithin = 200;

        /// <summary>Радиус штампа леса, тайлов (3 ячейки).</summary>
        public const int ForestStampRadius = 24;

        /// <summary>Медь: не меньше <see cref="CopperNeeded"/> залежей в <see cref="CopperWithin"/> тайлов от Очага.</summary>
        public const int CopperWithin = 600;

        public const int CopperNeeded = 20;

        /// <summary>Штамп меди: квадрат 17 × 17 расчищается (вода — в песок), в нём ставится пятно радиуса <see cref="DepositStage.StampRadius"/>.</summary>
        public const int CopperStampHalf = 8;

        /// <summary>Штамп меди без своих гор в 600 — на этом расстоянии от Очага.</summary>
        public const int CopperStampFallbackDistance = 300;

        /// <summary>C-027: выходы меди у старта — от и до стольких тайлов от Очага (предварительно; в 150 — слово карточки).</summary>
        public const int NearCopperMin = 80;

        public const int NearCopperMax = 140;

        /// <summary>C-027: радиус пятна выхода меди (край ± <see cref="DepositStage.EdgeJagTiles"/>) и поляны вокруг него.</summary>
        public const int NearCopperRadius = 4;

        public const int NearCopperClear = 9;

        /// <summary>Поток хэшей выходов меди у старта (зерно этапа 7).</summary>
        private const int StreamNearCopper = 70;

        // 8 направлений, длина ×1000 (диагональ — 707).
        private static readonly int[] NearDx = { 1000, 707, 0, -707, -1000, -707, 0, 707 };
        private static readonly int[] NearDy = { 0, 707, 1000, 707, 0, -707, -1000, -707 };

        /// <summary>Этап 7 на макрокарте (луга у Очага, лес в 200). Возвращает true, если поставлен штамп леса.</summary>
        public static bool RunMacro(MacroWorld world)
        {
            MacroMap map = world.Map;
            int[] d = world.Zones.D;
            int n = map.Size * map.Size;
            bool forestNear = false;
            for (int i = 0; i < n; i++)
            {
                if (d[i] <= MeadowRadius)
                {
                    map.Biome[i] = Biome.Meadow;
                }
                else if (d[i] <= ForestWithin && map.Biome[i] == Biome.Forest)
                {
                    forestNear = true;
                }
            }

            if (forestNear)
            {
                return false;
            }

            // Штамп леса: центр — ячейка с наибольшей m в кольце 150 < d ≤ 200 (равные — меньший индекс).
            int best = -1;
            for (int i = 0; i < n; i++)
            {
                if (d[i] > MeadowRadius && d[i] <= ForestWithin && (best < 0 || world.Fields.M[i] > world.Fields.M[best]))
                {
                    best = i;
                }
            }

            if (best < 0)
            {
                return false; // кольца нет (мир меньше 400) — нечего штамповать
            }

            int size = map.Size;
            int bx = best % size;
            int by = best / size;
            int cells = ForestStampRadius >> MacroMap.CellBits;
            for (int dy = -cells; dy <= cells; dy++)
            {
                for (int dx = -cells; dx <= cells; dx++)
                {
                    if (dx * dx + dy * dy > cells * cells)
                    {
                        continue;
                    }

                    int i = map.IndexOf(bx + dx, by + dy);
                    if (d[i] > MeadowRadius)
                    {
                        map.Biome[i] = Biome.Forest;
                    }
                }
            }

            return true;
        }

        /// <summary>Залежей меди в <see cref="CopperWithin"/> тайлов от Очага (расстояние по тору до центра тайла).</summary>
        public static int CountCopper(TileWorld t)
        {
            int w = t.WorldSize;
            int half = w >> 1;
            long r2 = (long)CopperWithin * CopperWithin;
            int count = 0;
            for (int y = 0; y < w; y++)
            {
                long dy = IntMath.Wrap(y - half, w);
                if (dy * dy > r2)
                {
                    continue;
                }

                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (t.Objects[row + x] != ObjectId.Ore)
                    {
                        continue;
                    }

                    long dx = IntMath.Wrap(x - half, w);
                    if (dx * dx + dy * dy <= r2)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Выходы меди у старта (C-027, после <see cref="StampCopper"/>): 2 или 3 пятна радиуса <see cref="NearCopperRadius"/> в
        /// <see cref="NearCopperMin"/>…<see cref="NearCopperMax"/> тайлах от Очага, направления разнесены (из 8, по хэшу зерна);
        /// вокруг — поляна радиуса <see cref="NearCopperClear"/> без деревьев, кустов и валунов (залежь видна издали и подходима
        /// со всех сторон). В 150 от Очага нет воды и обрывов, поэтому пятно всегда на суше. Возвращает центры пятен.
        /// </summary>
        public static Orpheus.Core.Geometry.TileCoord[] StampNearCopper(TileWorld t)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            int half = w >> 1;
            ulong seed = t.Macro.Map.Seed;
            ulong h = Orpheus.Core.Rng.SplitMix64.Hash(StreamNearCopper, 0, MacroGenerator.StageSeed(seed, Stage));
            int count = 2 + (int)(h & 1);
            int first = (int)((h >> 1) & 7);
            int step = count == 2 ? 4 : 3;
            int span = Math.Max(1, Math.Min(NearCopperMax, half - NearCopperClear - 2) - NearCopperMin + 1);
            FieldNoise edge = DepositStage.EdgeField(w, seed);
            ulong tileSeed = MacroGenerator.StageSeed(seed, TileStage.Stage);
            var centers = new Orpheus.Core.Geometry.TileCoord[count];
            for (int k = 0; k < count; k++)
            {
                int dir = (first + (k * step)) & 7;
                int dist = NearCopperMin + (int)((h >> (8 + (k * 8))) & 0xFF) % span;
                int cx = (half + (NearDx[dir] * dist / 1000)) & mask;
                int cy = (half + (NearDy[dir] * dist / 1000)) & mask;
                int r2 = NearCopperClear * NearCopperClear;
                for (int dy = -NearCopperClear; dy <= NearCopperClear; dy++)
                {
                    for (int dx = -NearCopperClear; dx <= NearCopperClear; dx++)
                    {
                        int j = (((cy + dy) & mask) * w) + ((cx + dx) & mask);
                        ushort o = t.Objects[j];
                        if ((dx * dx) + (dy * dy) <= r2 && o != ObjectId.Cliff && o != ObjectId.Ore)
                        {
                            t.Objects[j] = ObjectId.Empty;
                        }
                    }
                }

                DepositStage.Place(t, edge, tileSeed, cx, cy, NearCopperRadius, DepositKind.Copper, forced: true);
                centers[k] = new Orpheus.Core.Geometry.TileCoord(cx, cy);
            }

            t.NearCopper = centers;
            return centers;
        }

        /// <summary>
        /// Медь в 600 (после этапа 11): своих залежей меньше 8 — штамп. Место — ближайшая к Очагу ячейка гор с d ≤ 600 − 8
        /// за лугами Очага («медь — в предгорье», `01` 3 «Луга»), иначе точка в 300 от Очага на восток. Возвращает true, если штамп поставлен.
        /// </summary>
        public static bool StampCopper(TileWorld t)
        {
            if (CountCopper(t) >= CopperNeeded)
            {
                return false;
            }

            MacroWorld macro = t.Macro;
            MacroMap map = macro.Map;
            int[] d = macro.Zones.D;
            int n = map.Size * map.Size;
            int best = -1;
            for (int i = 0; i < n; i++)
            {
                if (map.Biome[i] == Biome.Mountain && d[i] > MeadowRadius && d[i] <= CopperWithin - 8 && (best < 0 || d[i] < d[best]))
                {
                    best = i;
                }
            }

            int w = t.WorldSize;
            int cx;
            int cy;
            if (best >= 0)
            {
                cx = ((best % map.Size) << MacroMap.CellBits) + (MacroMap.CellTiles >> 1);
                cy = ((best / map.Size) << MacroMap.CellBits) + (MacroMap.CellTiles >> 1);
            }
            else
            {
                cx = (w >> 1) + Math.Min(CopperStampFallbackDistance, (w >> 1) - 16);
                cy = w >> 1;
            }

            int mask = w - 1;
            for (int dy = -CopperStampHalf; dy <= CopperStampHalf; dy++)
            {
                for (int dx = -CopperStampHalf; dx <= CopperStampHalf; dx++)
                {
                    int j = (((cy + dy) & mask) * w) + ((cx + dx) & mask);
                    t.Objects[j] = ObjectId.Empty;
                    if (t.Floor[j] == FloorId.Water)
                    {
                        t.Floor[j] = FloorId.Sand; // штамп на озере — отмель (E1-2)
                    }
                }
            }

            // E2-6: штамп — такое же пятно, как у сетки залежей (неровный край, плотность убывает к краю).
            ulong seed = map.Seed;
            int placed = DepositStage.Place(t, DepositStage.EdgeField(w, seed), MacroGenerator.StageSeed(seed, TileStage.Stage),
                cx & mask, cy & mask, DepositStage.StampRadius, DepositKind.Copper, forced: true);
            t.Deposits.Add(new DepositPatch(cx & mask, cy & mask, DepositStage.StampRadius, DepositKind.Copper, placed, stamped: true));
            t.CopperStamp = new Orpheus.Core.Geometry.TileCoord(cx & mask, cy & mask);
            return true;
        }
    }
}
