using System;
using System.Diagnostics;

namespace Orpheus.Server
{
    /// <summary>
    /// Рантайм процесса <c>OrpheusServer</c> для метрик (0-030; вынесено из ServerMetrics в 0-031): память процесса ОС, куча и
    /// паузы сборки мусора .NET 9 (<c>GC.GetGCMemoryInfo</c>, <c>GC.GetTotalPauseDuration</c> — их нет в netstandard2.1).
    /// </summary>
    public sealed class ProcessRuntimeStats : IRuntimeStats, IDisposable
    {
        private const double Mb = 1024.0 * 1024.0;
        private static readonly GCKind[] Kinds = { GCKind.Ephemeral, GCKind.FullBlocking, GCKind.Background };

        private readonly Process _process = Process.GetCurrentProcess();
        private readonly long[] _gcIndex = new long[3];

        public double SecondsSinceStart() => SecondsSinceProcessStart();

        public RuntimeMemory Memory()
        {
            _process.Refresh();
            return new RuntimeMemory
            {
                WorkingSetMb = _process.WorkingSet64 / Mb,
                PeakWorkingSetMb = _process.PeakWorkingSet64 / Mb,
                PrivateMb = _process.PrivateMemorySize64 / Mb,
                HeapMb = GC.GetGCMemoryInfo().HeapSizeBytes / Mb,
            };
        }

        public double GcPauseTotalMs() => GC.GetTotalPauseDuration().TotalMilliseconds;

        public double TakeGcPauseMaxMs()
        {
            double max = 0;
            foreach (GCKind kind in Kinds)
            {
                GCMemoryInfo info = GC.GetGCMemoryInfo(kind);
                if (info.Index != _gcIndex[(int)kind - 1])
                {
                    _gcIndex[(int)kind - 1] = info.Index;
                    foreach (TimeSpan p in info.PauseDurations)
                    {
                        max = Math.Max(max, p.TotalMilliseconds);
                    }
                }
            }

            return max;
        }

        /// <summary>Секунд с запуска процесса (по времени старта процесса в ОС).</summary>
        public static double SecondsSinceProcessStart()
        {
            using Process p = Process.GetCurrentProcess();
            return (DateTime.Now - p.StartTime).TotalSeconds;
        }

        public void Dispose() => _process.Dispose();
    }
}
