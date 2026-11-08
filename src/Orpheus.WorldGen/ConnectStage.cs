using System;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Этап 12 «Перевалы и связность», минимум вехи 1 (карточка 1-017; docs/01, 2 «Этап 12», «Гарантии»; `08` 3.1 — без
    /// гарантий расстояний «перевал не реже 200»).
    /// <para>
    /// Проходимый тайл — природный пол проходим и объект не твёрдый; соседство — 4 стороны по тору (персонаж — круг 0,4 тайла,
    /// между двумя преградами по диагонали не пролезает). Компонента Очага — главная. Присоединяется каждая другая компонента,
    /// где есть тайл лугов или леса, или размером ≥ 64 тайлов (`01` 2, этап 6: «область ≥ 64»); мелкие карманы в толще гор и
    /// пещер остаются — до них докапываются.
    /// </para>
    /// <para>
    /// Как: один проход 0-1 BFS от главной компоненты по всему тору — цена входа в преграду 1, в проходимый тайл 0, залежь
    /// не прокапывается никогда. Для каждой присоединяемой компоненты берётся её тайл с наименьшей ценой, путь по родителям
    /// до главной расчищается: преграда в скальной массе (скала или гранит на камне гор и пещер) — кистью 4 × 4 (перевал
    /// шириной 4, `01` 2 (а)), прочая (дерево, куст, валун) — один тайл. Пути — кратчайшие по числу преград: рубится минимум.
    /// </para>
    /// Порядок обхода фиксирован (очередь, соседи в порядке +x, −x, +y, −y), результат один на любой машине.
    /// </summary>
    public static class ConnectStage
    {
        /// <summary>Номер этапа (`01` 2).</summary>
        public const int Stage = 12;

        /// <summary>Отрезанная компонента без лугов и леса присоединяется, если в ней не меньше стольких тайлов.</summary>
        public const int MinJoinSize = 64;

        /// <summary>Ширина перевала через скальную массу, тайлов.</summary>
        public const int PassWidth = 4;

        private const byte DirNone = 255;

        /// <summary>Тайл проходим без копания.</summary>
        public static bool Passable(byte floor, ushort obj) => FloorId.IsPassable(floor) && !ObjectId.IsSolid(obj);

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

            // 0-1 BFS от главной компоненты: dist — сколько преград прокопать, dir — откуда пришли.
            var dist = new int[n];
            var dir = new byte[n];
            for (int i = 0; i < n; i++)
            {
                dist[i] = int.MaxValue;
                dir[i] = DirNone;
            }

            // Двухсторонняя очередь на кольцевом буфере: 0-шаги — в голову, 1-шаги — в хвост.
            int cap = 2 * n;
            var dq = new int[cap];
            int head = 0;
            int tail = 0;
            for (int i = 0; i < n; i++)
            {
                if (label[i] == main)
                {
                    dist[i] = 0;
                    dq[tail++] = i;
                }
            }

            int count = tail;
            while (count > 0)
            {
                int u = dq[head];
                head = head + 1 == cap ? 0 : head + 1;
                count--;
                int du = dist[u];
                int ux = u & mask;
                int uy = u >> shift;
                for (int k = 0; k < 4; k++)
                {
                    int vx = k == 0 ? (ux + 1) & mask : k == 1 ? (ux - 1) & mask : ux;
                    int vy = k == 2 ? (uy + 1) & mask : k == 3 ? (uy - 1) & mask : uy;
                    int v = (vy << shift) | vx;
                    if (obj[v] == ObjectId.Ore)
                    {
                        continue; // залежь не прокапываем: штамп меди и жилы целы
                    }

                    int cost = label[v] >= 0 ? 0 : 1;
                    int dv = du + cost;
                    if (dv >= dist[v])
                    {
                        continue;
                    }

                    dist[v] = dv;
                    dir[v] = (byte)k;
                    if (cost == 0)
                    {
                        head = head == 0 ? cap - 1 : head - 1;
                        dq[head] = v;
                    }
                    else
                    {
                        dq[tail] = v;
                        tail = tail + 1 == cap ? 0 : tail + 1;
                    }

                    count++;
                }
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

        /// <summary>Расчистить преграду (x, y): скальная масса — кистью 4 × 4 (x − 1…x + 2), прочее — один тайл. Возвращает число снятых объектов.</summary>
        private static int Carve(TileWorld t, int x, int y)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            int i = (y * w) + x;
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
                    if (o != ObjectId.Empty && o != ObjectId.Ore && ObjectId.IsSolid(o))
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
    }
}
