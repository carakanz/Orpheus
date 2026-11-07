using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Orpheus.Tests
{
    /// <summary>
    /// Загрузка процессора машины за время замера (0-032): опрос раз в 250 мс — вся машина (Windows: <c>GetSystemTimes</c>,
    /// Linux: <c>/proc/stat</c>) и сам процесс теста. Числа идут в вывод сетевых тестов рядом с отставанием, чтобы
    /// красный под нагрузкой отличался от красного на спокойной машине.
    /// </summary>
    internal sealed class CpuLoad : IDisposable
    {
        private const int PeriodMs = 250;

        private readonly Timer _timer;
        private readonly object _lock = new object();
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly TimeSpan _process0;
        private (long Idle, long Total) _last;
        private double _sum;
        private double _max;
        private int _count;
        private bool _stopped;
        private Summary _summary;

        public CpuLoad()
        {
            _process0 = Process.GetCurrentProcess().TotalProcessorTime;
            _last = Read();
            _timer = new Timer(_ => Sample(), null, PeriodMs, PeriodMs);
        }

        /// <summary>Итог замера.</summary>
        public struct Summary
        {
            /// <summary>Есть ли данные о машине (ОС умеет).</summary>
            public bool Known;

            /// <summary>Средняя загрузка машины, %.</summary>
            public double MeanPercent;

            /// <summary>Наибольшая загрузка машины за окно 250 мс, %.</summary>
            public double MaxPercent;

            /// <summary>Процесс теста: процессорное время ÷ (время × ядер), %.</summary>
            public double OwnPercent;

            public override string ToString() => Known
                ? $"машина ср. {MeanPercent:F0} %, max {MaxPercent:F0} %, из них процесс теста {OwnPercent:F1} % ({Environment.ProcessorCount} логич. ядер)"
                : $"загрузка машины неизвестна, процесс теста {OwnPercent:F1} %";
        }

        /// <summary>Остановить опрос и вернуть итог.</summary>
        public Summary Stop()
        {
            lock (_lock)
            {
                if (_stopped)
                {
                    return _summary;
                }

                _stopped = true;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                double seconds = _watch.Elapsed.TotalSeconds;
                double own = (Process.GetCurrentProcess().TotalProcessorTime - _process0).TotalSeconds;
                _summary = new Summary
                {
                    Known = _count > 0,
                    MeanPercent = _count > 0 ? _sum / _count : 0,
                    MaxPercent = _max,
                    OwnPercent = seconds > 0 ? 100 * own / (seconds * Environment.ProcessorCount) : 0,
                };
                return _summary;
            }
        }

        public void Dispose()
        {
            Stop();
            _timer.Dispose();
        }

        private void Sample()
        {
            lock (_lock)
            {
                if (_stopped)
                {
                    return;
                }

                (long idle, long total) = Read();
                long dTotal = total - _last.Total;
                if (total > 0 && dTotal > 0)
                {
                    double busy = 100.0 * (dTotal - (idle - _last.Idle)) / dTotal;
                    _sum += busy;
                    _max = Math.Max(_max, busy);
                    _count++;
                }

                _last = (idle, total);
            }
        }

        /// <summary>Время простоя и всё время процессоров с запуска ОС (единицы — какие отдаёт ОС); (0, 0) — неизвестно.</summary>
        private static (long Idle, long Total) Read()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // kernel включает idle: всего = kernel + user.
                    return GetSystemTimes(out long idle, out long kernel, out long user) ? (idle, kernel + user) : (0, 0);
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    string[] f = System.IO.File.ReadAllLines("/proc/stat")[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    long total = 0;
                    for (int i = 1; i < f.Length; i++)
                    {
                        total += long.Parse(f[i], System.Globalization.CultureInfo.InvariantCulture);
                    }

                    long idleAll = long.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture) + long.Parse(f[5], System.Globalization.CultureInfo.InvariantCulture);
                    return (idleAll, total);
                }
            }
            catch (Exception e) when (e is System.IO.IOException || e is FormatException || e is IndexOutOfRangeException || e is UnauthorizedAccessException)
            {
            }

            return (0, 0);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
    }
}
