using System;
using System.Diagnostics;
using System.Threading;

namespace Orpheus.Server
{
    /// <summary>
    /// Заготовка выделенного сервера (задача 0-001): цикл тактов 30 Гц без сети и мира.
    /// Ключ <c>--ticks N</c> — остановиться после N тактов; без него — до Ctrl+C.
    /// </summary>
    public static class Program
    {
        public const int TickRate = 30; // docs/07, 2.4

        public static int Main(string[] args)
        {
            long maxTicks = long.MaxValue;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--ticks" && i + 1 < args.Length && long.TryParse(args[i + 1], out long n) && n >= 0)
                {
                    maxTicks = n;
                    i++;
                }
                else
                {
                    Console.Error.WriteLine($"Неизвестный ключ: {args[i]}. Есть только --ticks N.");
                    return 2;
                }
            }

            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            Console.WriteLine($"Orpheus server: hello. Такт {TickRate} Гц, сети пока нет.");
            var clock = Stopwatch.StartNew();
            long tick = 0;
            while (tick < maxTicks && !stop.IsCancellationRequested)
            {
                tick++;
                if (tick % TickRate == 0)
                {
                    Console.WriteLine($"такт {tick}");
                }

                long nextTickMs = tick * 1000 / TickRate;
                long waitMs = nextTickMs - clock.ElapsedMilliseconds;
                if (waitMs > 0)
                {
                    stop.Token.WaitHandle.WaitOne((int)waitMs);
                }
            }

            Console.WriteLine($"Orpheus server: стоп на такте {tick}.");
            return 0;
        }
    }
}
