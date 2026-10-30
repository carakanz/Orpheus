// noise_bench — измерение скорости шумовой генерации мира на C# (.NET 9).
// Критик, смена 00_vision, 28.10. Модель: перлин-шум (улучшенный, таблица 256) + fBm из 5 октав,
// проход классификации проходимости и заливка (BFS) по тору 4096² — то, что генератор обязан сделать,
// чтобы выполнить столп С1 «нет биомов, которых нельзя достичь пешком».
//
// Запуск: dotnet run -c Release --project C:\Users\carak\source\Orpheus\design_room\threads\00_vision\noise_bench
// (bin/ и obj/ появятся рядом; их можно удалить)

using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;

static class Noise
{
    static readonly int[] p = new int[512];

    static Noise()
    {
        int[] perm = new int[256];
        for (int i = 0; i < 256; i++) perm[i] = i;
        var rng = new Random(12345);
        for (int i = 255; i > 0; i--) { int j = rng.Next(i + 1); (perm[i], perm[j]) = (perm[j], perm[i]); }
        for (int i = 0; i < 512; i++) p[i] = perm[i & 255];
    }

    static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
    static float Lerp(float a, float b, float t) => a + t * (b - a);

    static float Grad(int h, float x, float y)
    {
        switch (h & 7)
        {
            case 0: return x + y;
            case 1: return -x + y;
            case 2: return x - y;
            case 3: return -x - y;
            case 4: return x;
            case 5: return -x;
            case 6: return y;
            default: return -y;
        }
    }

    // Период решётки — 256 ячеек по каждой оси (маска & 255): шум сам замыкается на торе, если
    // период × размер ячейки = сторона мира. Для 4096² и ячейки 64 тайла нужна маска & 63 (период 64):
    // градиентов меньше, скорость та же, «шва» на торе нет. Здесь оставлена маска 255 — на время замера это не влияет.
    public static float Perlin(float x, float y)
    {
        float fx = MathF.Floor(x), fy = MathF.Floor(y);
        int X = (int)fx & 255, Y = (int)fy & 255;
        x -= fx; y -= fy;
        float u = Fade(x), v = Fade(y);
        int A = p[X] + Y, B = p[X + 1] + Y;
        return Lerp(Lerp(Grad(p[A], x, y), Grad(p[B], x - 1, y), u),
                    Lerp(Grad(p[A + 1], x, y - 1), Grad(p[B + 1], x - 1, y - 1), u), v);
    }

    public static float Fbm(float x, float y, int octaves)
    {
        float sum = 0, amp = 1, freq = 1, norm = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Perlin(x * freq, y * freq);
            norm += amp;
            amp *= 0.5f;
            freq *= 2;
        }
        return sum / norm;
    }
}

static class Program
{
    static float Checksum(float[] a)
    {
        float s = 0;
        for (int i = 0; i < a.Length; i += 4099) s += a[i];
        return s;
    }

    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($".NET {Environment.Version}, логических ядер: {Environment.ProcessorCount}, x64: {Environment.Is64BitProcess}");

        const int oct = 5;
        const float freq = 1f / 64f; // ячейка решётки первой октавы — 64 тайла (горы и равнины), пятой — 4 тайла
        double msps1, mspsP;

        // 1. Один поток, 2048² × 5 октав
        {
            int n = 2048;
            var a = new float[n * n];
            var sw = Stopwatch.StartNew();
            for (int y = 0; y < n; y++)
            {
                int row = y * n;
                for (int x = 0; x < n; x++) a[row + x] = Noise.Fbm(x * freq, y * freq, oct);
            }
            sw.Stop();
            double s = (double)n * n * oct;
            msps1 = s / sw.Elapsed.TotalSeconds / 1e6;
            Console.WriteLine($"1 поток:  {n}² × {oct} октав = {s / 1e6:F1} M выборок за {sw.ElapsedMilliseconds} мс → {msps1:F1} M выборок/с  (контроль {Checksum(a):F3})");
        }

        // 2. Все потоки, 4096² × 5 октав
        int N = 4096;
        int shift = BitOperations.Log2((uint)N);
        var h = new float[N * N];
        {
            var sw = Stopwatch.StartNew();
            Parallel.For(0, N, y =>
            {
                int row = y * N;
                for (int x = 0; x < N; x++) h[row + x] = Noise.Fbm(x * freq, y * freq, oct);
            });
            sw.Stop();
            double s = (double)N * N * oct;
            mspsP = s / sw.Elapsed.TotalSeconds / 1e6;
            Console.WriteLine($"{Environment.ProcessorCount} потоков: {N}² × {oct} октав = {s / 1e6:F1} M выборок за {sw.ElapsedMilliseconds} мс → {mspsP:F1} M выборок/с  (контроль {Checksum(h):F3})");
        }

        // 3. Проход классификации: высота < порога → проходимо
        var pass = new byte[N * N];
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < h.Length; i++) pass[i] = h[i] < 0.12f ? (byte)1 : (byte)0;
            sw.Stop();
            long cnt = 0;
            for (int i = 0; i < pass.Length; i++) cnt += pass[i];
            Console.WriteLine($"классификация {N}² (1 поток): {sw.ElapsedMilliseconds} мс, проходимо {100.0 * cnt / pass.Length:F1} %");
        }

        // 4. Заливка (BFS) по тору от первого проходимого тайла — проверка связности мира
        {
            var sw = Stopwatch.StartNew();
            var seen = new byte[N * N];
            var queue = new int[N * N];
            int qh = 0, qt = 0;
            int start = -1;
            for (int i = 0; i < pass.Length; i++) if (pass[i] == 1) { start = i; break; }
            int mask = N - 1;
            long reach = 0;

            void Push(int idx)
            {
                if (pass[idx] == 1 && seen[idx] == 0) { seen[idx] = 1; queue[qt++] = idx; }
            }

            seen[start] = 1; queue[qt++] = start;
            while (qh < qt)
            {
                int c = queue[qh++];
                reach++;
                int x = c & mask, y = c >> shift;
                Push(((x + 1) & mask) | (y << shift));
                Push(((x - 1) & mask) | (y << shift));
                Push(x | (((y + 1) & mask) << shift));
                Push(x | (((y - 1) & mask) << shift));
            }
            sw.Stop();
            long passable = 0;
            for (int i = 0; i < pass.Length; i++) passable += pass[i];
            Console.WriteLine($"заливка по тору {N}² (1 поток): {sw.ElapsedMilliseconds} мс, из первой проходимой точки достижимо {100.0 * reach / passable:F1} % проходимых тайлов");
        }

        Console.WriteLine($"память процесса после всех массивов: {GC.GetTotalMemory(false) / 1e6:F0} МБ (float 4096² = 67 МБ, byte 4096² = 17 МБ, int 4096² = 67 МБ)");

        // 5. Экстраполяция на модель генератора
        int samplesPerTile = args.Length > 0 ? int.Parse(args[0]) : 18; // высота 5 + влажность 3 + температура 3 + пещеры 3 + руда 2 + детали 2
        double tiles = (double)N * N;
        double samples = tiles * samplesPerTile;
        Console.WriteLine($"модель: {samplesPerTile} выборок/тайл × {tiles / 1e6:F1} M тайлов = {samples / 1e6:F0} M выборок → " +
                          $"шум 1 поток {samples / msps1 / 1e6:F1} с, все потоки {samples / mspsP / 1e6:F1} с; плюс проходы по 20–60 мс каждый");
        Console.WriteLine($"мир 8192² (×4): шум 1 поток {4 * samples / msps1 / 1e6:F1} с, все потоки {4 * samples / mspsP / 1e6:F1} с");
    }
}
