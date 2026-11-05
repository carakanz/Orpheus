using System;
using System.Text;
using System.Threading;

namespace Orpheus.Server
{
    /// <summary>
    /// Выделенный сервер без графики (docs/07, 2.3 и 11.2). Сети пока нет: мир, такт 30 Гц, боты без сети,
    /// режим симуляции, журнал ввода, повтор и хэш (задача 0-006). Ключи — <see cref="ServerOptions"/>.
    /// Код выхода: 0 — хорошо, 1 — повтор разошёлся, 2 — ошибка ключей или данных.
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

            ServerOptions options;
            try
            {
                options = ServerOptions.Parse(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine(ServerOptions.Usage);
                return 2;
            }

            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            return ServerRun.Run(options, Console.Out, stop.Token).ExitCode;
        }
    }
}
