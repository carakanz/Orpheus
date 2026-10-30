// net_bench — замеры для docs/07_multiplayer_tech.md (смена Дизайнера 28.10).
// Запуск: dotnet run -c Release --project C:\Users\carak\source\Orpheus\design_room\threads\07_multiplayer_tech\net_bench
// Что меряет (всё в один поток, чистый C# без Godot — так же будет работать ядро сервера):
//   1) поиск пути A* по правилам 02 (9.2): окно 48 × 48, 8 соседей без срезания углов, предел 1500 раскрытых узлов;
//   2) шаг движения сущности: круг r = 0,4 против сетки тайлов + мягкое расталкивание соседей (02, 2.1), такт 1/30 с;
//   3) Deflate чанка 32 × 32 (пол 1 байт + объект 2 байта, по плоскостям) — время сжатия и разжатия.
// Числа идут в модель бюджетов net_budget.py (параметры --astar-*, --move-us, --deflate-us).

using System.Diagnostics;
using System.IO.Compression;

static class Program
{
    const int Win = 48;           // окно поиска пути, 02 9.2
    const int MaxExpand = 1500;   // предел раскрытых узлов, 02 9.2

    static void Main()
    {
        Console.WriteLine($".NET {Environment.Version}, логических ядер: {Environment.ProcessorCount}");
        AStarBench();
        MoveBench();
        DeflateBench();
    }

    // ---------------- 1. A* ----------------
    sealed class Pathfinder
    {
        readonly int[] g = new int[Win * Win];
        readonly int[] stamp = new int[Win * Win];
        readonly int[] closedStamp = new int[Win * Win];
        readonly int[] heapNode = new int[Win * Win * 8];
        readonly int[] heapF = new int[Win * Win * 8];
        int heapCount, cur;
        public long Expanded;

        static int H(int a, int b)
        {
            int dx = Math.Abs(a % Win - b % Win), dy = Math.Abs(a / Win - b / Win);
            return 10 * Math.Max(dx, dy) + 4 * Math.Min(dx, dy); // октильная эвристика, стоимость 10 / 14
        }

        void Push(int node, int f)
        {
            int i = heapCount++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (heapF[p] <= f) break;
                heapNode[i] = heapNode[p]; heapF[i] = heapF[p]; i = p;
            }
            heapNode[i] = node; heapF[i] = f;
        }

        int Pop()
        {
            int top = heapNode[0];
            int lastN = heapNode[--heapCount], lastF = heapF[heapCount];
            int i = 0;
            while (true)
            {
                int c = 2 * i + 1;
                if (c >= heapCount) break;
                if (c + 1 < heapCount && heapF[c + 1] < heapF[c]) c++;
                if (heapF[c] >= lastF) break;
                heapNode[i] = heapNode[c]; heapF[i] = heapF[c]; i = c;
            }
            heapNode[i] = lastN; heapF[i] = lastF;
            return top;
        }

        // true — путь найден; иначе упёрся (02 9.3: лучший частичный путь, здесь не строим)
        public bool Find(bool[] solid, int start, int goal)
        {
            cur++; heapCount = 0;
            g[start] = 0; stamp[start] = cur;
            Push(start, H(start, goal));
            int expanded = 0;
            while (heapCount > 0)
            {
                int n = Pop();
                if (closedStamp[n] == cur) continue;
                closedStamp[n] = cur;
                if (n == goal) { Expanded += expanded; return true; }
                if (++expanded > MaxExpand) break;
                int x = n % Win, y = n / Win;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= Win || (uint)ny >= Win) continue;
                    int m = ny * Win + nx;
                    if (solid[m]) continue;
                    if (dx != 0 && dy != 0 && (solid[y * Win + nx] || solid[ny * Win + x])) continue; // без срезания углов
                    int ng = g[n] + (dx != 0 && dy != 0 ? 14 : 10);
                    if (stamp[m] == cur && g[m] <= ng) continue;
                    stamp[m] = cur; g[m] = ng;
                    Push(m, ng + H(m, goal));
                }
            }
            Expanded += expanded;
            return false;
        }
    }

    static float ValueNoise(int x, int y, int cell, int seed)
    {
        int cx = x / cell, cy = y / cell;
        float fx = (x % cell) / (float)cell, fy = (y % cell) / (float)cell;
        float a = Hash01(cx, cy, seed), b = Hash01(cx + 1, cy, seed), c = Hash01(cx, cy + 1, seed), d = Hash01(cx + 1, cy + 1, seed);
        float sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
        return (a * (1 - sx) + b * sx) * (1 - sy) + (c * (1 - sx) + d * sx) * sy;
    }

    static float Hash01(int x, int y, int seed)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    static void AStarBench()
    {
        var rnd = new Random(42);
        var pf = new Pathfinder();
        Console.WriteLine();
        Console.WriteLine("1) A*: окно 48 × 48, 8 соседей, без срезания углов, предел 1500 узлов");
        Console.WriteLine("| Сценарий | Поисков | Найдено, % | Узлов на поиск | мкс на поиск | нс на узел |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var scen in new[] { "поле 10 %", "пещера 35 %", "цель в коробке" })
        {
            const int Maps = 64, Searches = 20000;
            var maps = new bool[Maps][];
            var starts = new int[Maps]; var goals = new int[Maps];
            for (int k = 0; k < Maps; k++)
            {
                var s = new bool[Win * Win];
                int seed = rnd.Next();
                for (int y = 0; y < Win; y++)
                for (int x = 0; x < Win; x++)
                {
                    bool solid = scen switch
                    {
                        "пещера 35 %" => ValueNoise(x, y, 6, seed) * 0.6f + Hash01(x, y, seed + 7) * 0.4f > 0.62f,
                        _ => Hash01(x, y, seed) < 0.10f,
                    };
                    s[y * Win + x] = solid;
                }
                int st, gl;
                if (scen == "цель в коробке")
                {
                    // комната 9 × 9 из стен без двери вокруг цели, враг в 20 тайлах (02 9.3: упёрся)
                    int gx = 34, gy = 24;
                    for (int y = gy - 4; y <= gy + 4; y++)
                    for (int x = gx - 4; x <= gx + 4; x++)
                    {
                        bool border = x == gx - 4 || x == gx + 4 || y == gy - 4 || y == gy + 4;
                        s[y * Win + x] = border;
                    }
                    gl = gy * Win + gx; st = 24 * Win + 10; s[st] = false;
                }
                else
                {
                    do
                    {
                        st = rnd.Next(Win * Win); gl = rnd.Next(Win * Win);
                    } while (s[st] || s[gl] || Dist(st, gl) < 15 || Dist(st, gl) > 30);
                }
                maps[k] = s; starts[k] = st; goals[k] = gl;
            }
            for (int w = 0; w < 2000; w++) pf.Find(maps[w % Maps], starts[w % Maps], goals[w % Maps]); // прогрев
            pf.Expanded = 0;
            int found = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Searches; i++)
                if (pf.Find(maps[i % Maps], starts[i % Maps], goals[i % Maps])) found++;
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000 / Searches;
            double perNode = sw.Elapsed.TotalMilliseconds * 1e6 / Math.Max(1, pf.Expanded);
            Console.WriteLine($"| {scen} | {Searches} | {100.0 * found / Searches:F0} | {pf.Expanded / (double)Searches:F0} | {us:F1} | {perNode:F0} |");
        }
    }

    static double Dist(int a, int b)
    {
        int dx = a % Win - b % Win, dy = a / Win - b / Win;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ---------------- 2. Шаг движения ----------------
    static void MoveBench()
    {
        const int Size = 256, N = 1000, Ticks = 600;
        const float R = 0.4f, Dt = 1f / 30f;
        var solid = new bool[Size * Size];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
            solid[y * Size + x] = Hash01(x, y, 99) < 0.20f;
        var rnd = new Random(7);
        var px = new float[N]; var py = new float[N]; var tx = new float[N]; var ty = new float[N];
        for (int i = 0; i < N; i++)
        {
            int t;
            do t = rnd.Next(Size * Size); while (solid[t]);
            px[i] = t % Size + 0.5f; py[i] = t / Size + 0.5f;
            tx[i] = rnd.Next(Size); ty[i] = rnd.Next(Size);
        }
        // пространственная сетка ячейкой 2 тайла: голова списка и следующий
        const int Cell = 2, G = Size / Cell;
        var head = new int[G * G]; var next = new int[N];
        long pairs = 0;
        var sw = new Stopwatch();
        for (int tick = 0; tick < Ticks + 60; tick++)
        {
            if (tick == 60) sw.Start(); // прогрев 60 тактов
            Array.Fill(head, -1);
            for (int i = 0; i < N; i++)
            {
                int c = ((int)py[i] / Cell) * G + (int)px[i] / Cell;
                next[i] = head[c]; head[c] = i;
            }
            for (int i = 0; i < N; i++)
            {
                // желаемая скорость 4,5 т/с к цели (ближний враг, 02 8.1)
                float dx = tx[i] - px[i], dy = ty[i] - py[i];
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1f) { tx[i] = rnd.Next(Size); ty[i] = rnd.Next(Size); continue; }
                float vx = dx / len * 4.5f, vy = dy / len * 4.5f;
                // мягкое расталкивание 2 т/с от соседей ближе 0,8 т
                int cx = (int)px[i] / Cell, cy = (int)py[i] / Cell;
                for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                {
                    int gx = cx + ox, gy = cy + oy;
                    if ((uint)gx >= G || (uint)gy >= G) continue;
                    for (int j = head[gy * G + gx]; j >= 0; j = next[j])
                    {
                        if (j == i) continue;
                        float ex = px[i] - px[j], ey = py[i] - py[j];
                        float d2 = ex * ex + ey * ey;
                        pairs++;
                        if (d2 < 0.64f && d2 > 1e-6f)
                        {
                            float d = MathF.Sqrt(d2);
                            vx += ex / d * 2f; vy += ey / d * 2f;
                        }
                    }
                }
                float nx = px[i] + vx * Dt, ny = py[i] + vy * Dt;
                // круг против тайлов: выталкивание из пересечённых твёрдых тайлов
                int x0 = (int)(nx - R), x1 = (int)(nx + R), y0 = (int)(ny - R), y1 = (int)(ny + R);
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    if ((uint)x >= Size || (uint)y >= Size) continue;
                    if (!solid[y * Size + x]) continue;
                    float qx = Math.Clamp(nx, x, x + 1), qy = Math.Clamp(ny, y, y + 1);
                    float ex = nx - qx, ey = ny - qy;
                    float d2 = ex * ex + ey * ey;
                    if (d2 < R * R)
                    {
                        float d = MathF.Sqrt(d2);
                        if (d < 1e-5f) { nx = px[i]; ny = py[i]; continue; }
                        nx += ex / d * (R - d); ny += ey / d * (R - d);
                    }
                }
                px[i] = Math.Clamp(nx, 1, Size - 2); py[i] = Math.Clamp(ny, 1, Size - 2);
            }
        }
        sw.Stop();
        double usPerEntity = sw.Elapsed.TotalMilliseconds * 1000 / (Ticks * (double)N);
        Console.WriteLine();
        Console.WriteLine("2) Шаг движения: 1000 сущностей, круг r = 0,4 против тайлов, расталкивание, такт 1/30 с");
        Console.WriteLine($"   {usPerEntity:F2} мкс на сущность за такт; проверок пар за такт: {pairs / (double)(Ticks + 60):F0}");
    }

    // ---------------- 3. Deflate чанка ----------------
    static void DeflateBench()
    {
        const int Chunks = 2000;
        var rnd = new Random(3);
        var raw = new byte[Chunks][];
        for (int k = 0; k < Chunks; k++)
        {
            // лес: трава/хвоя с рваной границей, деревья 120/1000, валуны 2 × 2 — 8/1000, кусты 15/1000 (01, «Лес»)
            var b = new byte[3072];
            int seed = rnd.Next();
            for (int i = 0; i < 1024; i++)
            {
                int x = i % 32, y = i / 32;
                b[i] = (byte)(ValueNoise(x + 1000, y + 1000, 12, seed) > 0.55f ? 3 : 2);
                float h = Hash01(x, y, seed + 1);
                ushort obj = h < 0.120f ? (ushort)(100 + (int)(h * 1000) % 3) : h < 0.135f ? (ushort)140 : h < 0.143f ? (ushort)120 : (ushort)0;
                b[1024 + i] = (byte)(obj & 0xFF);       // младшие байты объекта
                b[2048 + i] = (byte)(obj >> 8);         // старшие байты — плоскостью
            }
            raw[k] = b;
        }
        foreach (var level in new[] { CompressionLevel.Fastest, CompressionLevel.Optimal })
        {
            long bytes = 0;
            var packed = new byte[Chunks][];
            var sw = Stopwatch.StartNew();
            for (int k = 0; k < Chunks; k++)
            {
                using var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, level, leaveOpen: true)) z.Write(raw[k]);
                packed[k] = ms.ToArray();
                bytes += packed[k].Length;
            }
            sw.Stop();
            var sw2 = Stopwatch.StartNew();
            var outBuf = new byte[3072];
            for (int k = 0; k < Chunks; k++)
            {
                using var z = new ZLibStream(new MemoryStream(packed[k]), CompressionMode.Decompress);
                int read = 0;
                while (read < outBuf.Length) { int r = z.Read(outBuf, read, outBuf.Length - read); if (r == 0) break; read += r; }
            }
            sw2.Stop();
            Console.WriteLine();
            Console.WriteLine($"3) Deflate чанка леса, уровень {level}: {bytes / (double)Chunks:F0} байт из 3072; " +
                              $"сжатие {sw.Elapsed.TotalMilliseconds * 1000 / Chunks:F1} мкс, разжатие {sw2.Elapsed.TotalMilliseconds * 1000 / Chunks:F1} мкс на чанк");
        }
    }
}
