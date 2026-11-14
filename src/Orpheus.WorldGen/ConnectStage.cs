using System;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Этап 12 «Перевалы и связность» (карточки 1-017, E1-2; docs/01, 2 «Этап 12», «Гарантии»).
    /// <para>
    /// Проходимый тайл — пол проходим (вода — нет) и объект не твёрдый; соседство — 4 стороны по тору (персонаж — круг 0,4 тайла,
    /// между двумя преградами по диагонали не пролезает). Компонента Очага — главная. Присоединяется каждая другая компонента,
    /// где есть тайл лугов или леса, или размером ≥ 64 тайлов; мелкие карманы в толще пещер остаются.
    /// </para>
    /// <para>
    /// Как: один поиск кратчайших путей от главной компоненты по всему тору (очереди по цене, алгоритм Дейла). Цена входа:
    /// проходимый тайл 0; дерево, куст, валун 1; скальная масса 2; вода 3; обрыв 5; залежь — нельзя. Для каждой
    /// присоединяемой компоненты путь до главной расчищается: <b>обрыв</b> — убирается весь его сегмент 4 × 4 (проход, как
    /// у нарезки линий); <b>вода</b> — брод из песка кистью 3 × 3; скальная масса — кистью 4 × 4; прочее — один тайл.
    /// Обрыв и вода дороже деревьев, но дешевле длинного прокапывания скал: отрезанное ими открывается проходом и бродом.
    /// </para>
    /// Порядок обхода фиксирован (очереди, соседи в порядке +x, −x, +y, −y), результат один на любой машине.
    /// </summary>
    public static class ConnectStage
    {
        /// <summary>Номер этапа (`01` 2).</summary>
        public const int Stage = 12;

        /// <summary>Отрезанная компонента без лугов и леса присоединяется, если в ней не меньше стольких тайлов.</summary>
        public const int MinJoinSize = 64;

        /// <summary>Ширина перевала через скальную массу, тайлов.</summary>
        public const int PassWidth = 4;

        /// <summary>Брод длиннее стольких тайлов воды к области меньше <see cref="IslandJoinSize"/> тайлов не делается (остров остаётся островом).</summary>
        public const int MaxFordLength = 12;

        public const int IslandJoinSize = 6000;

        /// <summary>Карман меньше стольких тайлов присоединяется, только если цена пути не больше <see cref="MaxPocketCost"/>.</summary>
        public const int MinPocketSize = 16;

        public const int MaxPocketCost = 2;

        public const int CostObject = 1;
        public const int CostMass = 2;
        public const int CostWater = 3;
        public const int CostCliff = 5;

        private const int Buckets = CostCliff + 1;
        private const byte DirNone = 255;

        /// <summary>Тайл проходим без копания.</summary>
        public static bool Passable(byte floor, ushort obj) => FloorId.IsPassable(floor) && !ObjectId.IsSolid(obj);

        // Цена входа в непроходимый тайл; 0 — нельзя (залежь).
        private static int CostOf(TileWorld t, int i)
        {
            ushort o = t.Objects[i];
            if (o == ObjectId.Ore)
            {
                return 0;
            }

            if (o == ObjectId.Cliff)
            {
                return CostCliff;
            }

            if (!FloorId.IsPassable(t.Floor[i]))
            {
                return CostWater;
            }

            return IsMass(t, i) ? CostMass : CostObject;
        }

        /// <summary>Этап 12: присоединить отрезанные компоненты к компоненте Очага.</summary>
        public static void Run(TileWorld t)
        {
            int w = t.WorldSize;
            int n = w * w;
            int mask = w - 1;
            int shift = IntMath.Log2(w);
            byte[] floor = t.Floor;
            ushort[] obj = t.Objects;

            int[] label = Label(t, out int comps);
            int hearth = ((w >> 1) * w) + (w >> 1);
            int main = label[hearth];
            if (main < 0)
            {
                return; // Очаг завален — не бывает: стартовая зона пуста
            }

            // Какие компоненты присоединять: есть луга/лес или размер ≥ 64.
            var size = new int[comps];
            var open = new bool[comps];
            for (int i = 0; i < n; i++)
            {
                int c = label[i];
                if (c >= 0)
                {
                    size[c]++;
                    byte b = t.Biome[i];
                    if (b == Biome.Meadow || b == Biome.Forest)
                    {
                        open[c] = true;
                    }
                }
            }

            var want = new bool[comps];
            bool any = false;
            for (int c = 0; c < comps; c++)
            {
                want[c] = c != main && (open[c] || size[c] >= MinJoinSize);
                any |= want[c];
            }

            if (!any)
            {
                return;
            }

            // Кратчайшие пути от главной компоненты: dist — цена расчистки, dir — откуда пришли.
            var dist = new int[n];
            var dir = new byte[n];
            for (int i = 0; i < n; i++)
            {
                dist[i] = int.MaxValue;
                dir[i] = DirNone;
            }

            var bucket = new int[Buckets][];
            var fill = new int[Buckets];
            for (int k = 0; k < Buckets; k++)
            {
                bucket[k] = new int[k == 0 ? n : 1024];
            }

            long pending = 0;
            for (int i = 0; i < n; i++)
            {
                if (label[i] == main)
                {
                    dist[i] = 0;
                    bucket[0][fill[0]++] = i;
                    pending++;
                }
            }

            for (int cur = 0; pending > 0; cur++)
            {
                int slot = cur % Buckets;
                for (int at = 0; at < fill[slot]; at++) // очередь растёт, пока идём: шаги цены 0 — в неё же
                {
                    int u = bucket[slot][at];
                    pending--;
                    if (dist[u] != cur)
                    {
                        continue; // устаревшая запись
                    }

                    int ux = u & mask;
                    int uy = u >> shift;
                    for (int k = 0; k < 4; k++)
                    {
                        int vx = k == 0 ? (ux + 1) & mask : k == 1 ? (ux - 1) & mask : ux;
                        int vy = k == 2 ? (uy + 1) & mask : k == 3 ? (uy - 1) & mask : uy;
                        int v = (vy << shift) | vx;
                        int cost = 0;
                        if (label[v] < 0)
                        {
                            cost = CostOf(t, v);
                            if (cost == 0)
                            {
                                continue; // залежь не прокапываем: штамп меди и жилы целы
                            }
                        }

                        int dv = cur + cost;
                        if (dv >= dist[v])
                        {
                            continue;
                        }

                        dist[v] = dv;
                        dir[v] = (byte)k;
                        int to = dv % Buckets;
                        if (fill[to] == bucket[to].Length)
                        {
                            Array.Resize(ref bucket[to], bucket[to].Length * 2);
                        }

                        bucket[to][fill[to]++] = v;
                        pending++;
                    }
                }

                fill[slot] = 0;
            }

            // Ближайший к главной тайл каждой присоединяемой компоненты (равные — меньший индекс).
            var bestTile = new int[comps];
            for (int c = 0; c < comps; c++)
            {
                bestTile[c] = -1;
            }

            for (int i = 0; i < n; i++)
            {
                int c = label[i];
                if (c >= 0 && want[c] && dist[i] != int.MaxValue && (bestTile[c] < 0 || dist[i] < dist[bestTile[c]]))
                {
                    bestTile[c] = i;
                }
            }

            int joined = 0;
            int carved = 0;
            for (int c = 0; c < comps; c++)
            {
                int i = bestTile[c];
                if (i < 0)
                {
                    continue;
                }

                // Карман в чаще: мал и далеко (рубить больше двух деревьев) — остаётся карманом, длинных просек не рубим.
                if (size[c] < MinPocketSize && dist[i] > MaxPocketCost)
                {
                    continue;
                }

                // Остров: путь к нему — длинный брод, а сам он мал. Не присоединяем: на остров — настилом.
                if (size[c] < IslandJoinSize)
                {
                    int wet = 0;
                    for (int j = i; dist[j] > 0;)
                    {
                        wet += floor[j] == FloorId.Water ? 1 : 0;
                        int kj = dir[j];
                        int jx = j & mask;
                        int jy = j >> shift;
                        jx = kj == 0 ? (jx - 1) & mask : kj == 1 ? (jx + 1) & mask : jx;
                        jy = kj == 2 ? (jy - 1) & mask : kj == 3 ? (jy + 1) & mask : jy;
                        j = (jy << shift) | jx;
                    }

                    if (wet > MaxFordLength)
                    {
                        continue;
                    }
                }

                joined++;
                while (dist[i] > 0)
                {
                    if (!Passable(floor[i], obj[i]))
                    {
                        carved += Carve(t, i & mask, i >> shift);
                    }

                    int k = dir[i];
                    int x = i & mask;
                    int y = i >> shift;
                    // Шаг назад — против направления, которым пришли.
                    x = k == 0 ? (x - 1) & mask : k == 1 ? (x + 1) & mask : x;
                    y = k == 2 ? (y - 1) & mask : k == 3 ? (y + 1) & mask : y;
                    i = (y << shift) | x;
                }
            }

            t.ComponentsJoined = joined;
            t.TilesCarved = carved;
        }

        /// <summary>
        /// Расчистить преграду (x, y): обрыв — весь сегмент 4 × 4; вода — брод из песка 3 × 3; скальная масса — кистью 4 × 4
        /// (x − 1…x + 2); прочее — один тайл. Возвращает число снятых объектов (броды и сегменты считаются в мире отдельно).
        /// </summary>
        private static int Carve(TileWorld t, int x, int y)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            int i = (y * w) + x;
            if (t.Objects[i] == ObjectId.Cliff)
            {
                int x0 = x & ~(GridField.Step - 1);
                int y0 = y & ~(GridField.Step - 1);
                for (int dy = 0; dy < GridField.Step; dy++)
                {
                    for (int dx = 0; dx < GridField.Step; dx++)
                    {
                        int j = ((y0 + dy) * w) + x0 + dx;
                        if (t.Objects[j] == ObjectId.Cliff)
                        {
                            t.Objects[j] = ObjectId.Empty;
                        }
                    }
                }

                t.CliffCellsOpened++;
                return 0;
            }

            if (!FloorId.IsPassable(t.Floor[i]))
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int j = (((y + dy) & mask) * w) + ((x + dx) & mask);
                        if (t.Floor[j] == FloorId.Water)
                        {
                            t.Floor[j] = FloorId.Sand;
                            t.FordTiles++;
                        }
                    }
                }

                return 0;
            }

            if (!IsMass(t, i))
            {
                t.Objects[i] = ObjectId.Empty;
                return 1;
            }

            int carved = 0;
            for (int dy = -1; dy < PassWidth - 1; dy++)
            {
                for (int dx = -1; dx < PassWidth - 1; dx++)
                {
                    int j = (((y + dy) & mask) * w) + ((x + dx) & mask);
                    ushort o = t.Objects[j];
                    if (o != ObjectId.Empty && o != ObjectId.Ore && o != ObjectId.Cliff && ObjectId.IsSolid(o))
                    {
                        t.Objects[j] = ObjectId.Empty;
                        carved++;
                    }
                }
            }

            return carved;
        }

        // Скальная масса: скала или гранит на каменном полу (горы, пещеры).
        private static bool IsMass(TileWorld t, int i) =>
            t.Floor[i] == FloorId.Stone && (t.Objects[i] == ObjectId.Rock || t.Objects[i] == ObjectId.Granite);

        /// <summary>
        /// Компоненты проходимых тайлов (4 стороны, тор): label[i] — номер компоненты или −1 у преграды. Номера — по первому
        /// тайлу в порядке индекса.
        /// </summary>
        public static int[] Label(TileWorld t, out int count)
        {
            int w = t.WorldSize;
            int n = w * w;
            int mask = w - 1;
            int shift = IntMath.Log2(w);
            var label = new int[n];
            for (int i = 0; i < n; i++)
            {
                label[i] = Passable(t.Floor[i], t.Objects[i]) ? int.MaxValue : -1;
            }

            var queue = new int[n];
            count = 0;
            for (int s = 0; s < n; s++)
            {
                if (label[s] != int.MaxValue)
                {
                    continue;
                }

                int c = count++;
                int qh = 0;
                int qt = 0;
                label[s] = c;
                queue[qt++] = s;
                while (qh < qt)
                {
                    int u = queue[qh++];
                    int ux = u & mask;
                    int uy = u >> shift;
                    for (int k = 0; k < 4; k++)
                    {
                        int vx = k == 0 ? (ux + 1) & mask : k == 1 ? (ux - 1) & mask : ux;
                        int vy = k == 2 ? (uy + 1) & mask : k == 3 ? (uy - 1) & mask : uy;
                        int v = (vy << shift) | vx;
                        if (label[v] == int.MaxValue)
                        {
                            label[v] = c;
                            queue[qt++] = v;
                        }
                    }
                }
            }

            return label;
        }

        /// <summary>
        /// Доля проходимых тайлов лугов и леса, достижимых от Очага без копания (приёмка 1-017: ≥ 99 %), и их число.
        /// </summary>
        public static double ReachableOpenShare(TileWorld t, out int open, out int reached)
        {
            int[] label = Label(t, out _);
            int w = t.WorldSize;
            int main = label[((w >> 1) * w) + (w >> 1)];
            open = 0;
            reached = 0;
            for (int i = 0; i < label.Length; i++)
            {
                byte b = t.Biome[i];
                if (label[i] >= 0 && (b == Biome.Meadow || b == Biome.Forest))
                {
                    open++;
                    if (label[i] == main)
                    {
                        reached++;
                    }
                }
            }

            return open == 0 ? 1.0 : (double)reached / open;
        }

        /// <summary>Доля всех проходимых тайлов мира, достижимых от Очага без копания (приёмка E1-2: ≥ 90 %).</summary>
        public static double ReachableShare(TileWorld t)
        {
            int[] label = Label(t, out _);
            int w = t.WorldSize;
            int main = label[((w >> 1) * w) + (w >> 1)];
            long passable = 0;
            long reached = 0;
            for (int i = 0; i < label.Length; i++)
            {
                if (label[i] >= 0)
                {
                    passable++;
                    if (label[i] == main)
                    {
                        reached++;
                    }
                }
            }

            return passable == 0 ? 1.0 : (double)reached / passable;
        }
    }
}
