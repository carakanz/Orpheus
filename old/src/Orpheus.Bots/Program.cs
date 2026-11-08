using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Orpheus.Bots
{
    /// <summary>
    /// Боты без графики по сети (docs/07, 11.3). Ключи — <see cref="BotOptions"/>.
    /// Код выхода: 0 — все вошли, ни одного отключения; 1 — отказ входа или отключение; 2 — ошибка ключей или сети.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (System.IO.IOException)
            {
                // консоли нет — выводим как есть
            }

            BotOptions options;
            try
            {
                options = BotOptions.Parse(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine(BotOptions.Usage);
                return 2;
            }

            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            try
            {
                return BotRun.Run(options, Console.Out, stop.Token).ExitCode;
            }
            catch (SocketException e)
            {
                Console.Error.WriteLine($"сеть: {e.Message}");
                return 2;
            }
            catch (Exception e) when (e is Orpheus.Net.Hosting.ServerLaunchException || e is System.IO.IOException)
            {
                Console.Error.WriteLine($"сервер: {e.Message}");
                return 2;
            }
        }
    }
}
