using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Orpheus.Server
{
    /// <summary>Часть такта в выборке метрик.</summary>
    public sealed class PartSample
    {
        public string Name { get; set; } = "";

        /// <summary>Среднее на такт, мс.</summary>
        public double MeanMs { get; set; }

        /// <summary>Наибольшее за такт, мс.</summary>
        public double MaxMs { get; set; }

        /// <summary>Доля в сумме тактов, %.</summary>
        public double Percent { get; set; }

        /// <summary>Байт, выделенных потоком такта в этой части, в среднем на такт (11.6: должно быть 0).</summary>
        public double AllocatedBytesPerTick { get; set; }
    }

    /// <summary>Выборка метрик сервера: окно (раз в N с) или весь запуск (0-030, docs/07 11.6).</summary>
    public sealed class MetricsSample
    {
        /// <summary>Длина окна (запуска), с.</summary>
        public double Seconds { get; set; }

        public long Ticks { get; set; }
        public double TickMeanMs { get; set; }
        public double TickP50Ms { get; set; }
        public double TickP99Ms { get; set; }
        public double TickP999Ms { get; set; }
        public double TickMaxMs { get; set; }
        public PartSample[] Parts { get; set; } = Array.Empty<PartSample>();

        /// <summary>Простоев такта (промежуток между началами тактов больше такта + 5 мс); без часов — 0.</summary>
        public long Stalls { get; set; }

        /// <summary>Промежутков между тактами (знаменатель доли простоев).</summary>
        public long Gaps { get; set; }

        /// <summary>Доля простоев, %.</summary>
        public double StallPercent { get; set; }

        /// <summary>Наибольший промежуток между началами тактов, мс.</summary>
        public double MaxGapMs { get; set; }

        /// <summary>Байт, выделенных потоком такта внутри такта, в среднем на такт (11.6: в установившейся игре — 0).</summary>
        public double AllocatedBytesPerTick { get; set; }

        /// <summary>Тактов, в которых выделено хоть что-то.</summary>
        public long TicksWithAllocations { get; set; }

        /// <summary>Сборок мусора по поколениям за окно.</summary>
        public int Gc0 { get; set; }
        public int Gc1 { get; set; }
        public int Gc2 { get; set; }

        /// <summary>Наибольшая пауза сборки, мс: по последней сборке каждого вида (эфемерная, полная, фоновая) за окно.</summary>
        public double GcPauseMaxMs { get; set; }

        /// <summary>Сумма пауз сборки за окно, мс (<see cref="GC.GetTotalPauseDuration"/>).</summary>
        public double GcPauseTotalMs { get; set; }

        /// <summary>Рабочий набор процесса, МБ (в итоге — наибольший из окон).</summary>
        public double WorkingSetMb { get; set; }

        /// <summary>Пик рабочего набора процесса с запуска (включая создание мира), МБ.</summary>
        public double PeakWorkingSetMb { get; set; }

        /// <summary>Частная память процесса, МБ (в итоге — наибольшая).</summary>
        public double PrivateMb { get; set; }

        /// <summary>Управляемая куча после последней сборки, МБ (в итоге — наибольшая).</summary>
        public double HeapMb { get; set; }

        /// <summary>Игроков в игре на конце окна (в итоге — среднее по времени).</summary>
        public double Clients { get; set; }

        /// <summary>Сервер → клиенту, КБ/с на клиента: в среднем и у самого нагруженного.</summary>
        public double KbpsOutPerClient { get; set; }
        public double KbpsOutMaxClient { get; set; }

        /// <summary>Клиент → серверу, КБ/с на клиента.</summary>
        public double KbpsInPerClient { get; set; }
        public double KbpsInMaxClient { get; set; }

        /// <summary>Байт нагрузки за окно в обе стороны.</summary>
        public long BytesOut { get; set; }
        public long BytesIn { get; set; }

        /// <summary>Поля для строки лога.</summary>
        public (string Key, object? Value)[] ToLogData()
        {
            var d = new List<(string, object?)>
            {
                ("seconds", Math.Round(Seconds, 2)), ("ticks", Ticks),
                ("tickMeanMs", R(TickMeanMs)), ("tickP50Ms", R(TickP50Ms)), ("tickP99Ms", R(TickP99Ms)),
                ("tickP999Ms", R(TickP999Ms)), ("tickMaxMs", R(TickMaxMs)),
            };
            foreach (PartSample p in Parts)
            {
                d.Add((p.Name + "Ms", R(p.MeanMs)));
                d.Add((p.Name + "MaxMs", R(p.MaxMs)));
                if (p.AllocatedBytesPerTick > 0)
                {
                    d.Add((p.Name + "AllocBytesPerTick", R(p.AllocatedBytesPerTick)));
                }
            }

            d.Add(("stalls", Stalls));
            d.Add(("stallPercent", R(StallPercent)));
            d.Add(("maxGapMs", R(MaxGapMs)));
            d.Add(("allocatedBytesPerTick", R(AllocatedBytesPerTick)));
            d.Add(("gc0", Gc0));
            d.Add(("gc1", Gc1));
            d.Add(("gc2", Gc2));
            d.Add(("gcPauseMaxMs", R(GcPauseMaxMs)));
            d.Add(("gcPauseTotalMs", R(GcPauseTotalMs)));
            d.Add(("workingSetMb", R(WorkingSetMb)));
            d.Add(("peakWorkingSetMb", R(PeakWorkingSetMb)));
            d.Add(("privateMb", R(PrivateMb)));
            d.Add(("heapMb", R(HeapMb)));
            d.Add(("clients", R(Clients)));
            d.Add(("kbpsOutPerClient", R(KbpsOutPerClient)));
            d.Add(("kbpsOutMaxClient", R(KbpsOutMaxClient)));
            d.Add(("kbpsInPerClient", R(KbpsInPerClient)));
            d.Add(("kbpsInMaxClient", R(KbpsInMaxClient)));
            return d.ToArray();
        }

        private static double R(double v) => Math.Round(v, 4);
    }

    /// <summary>Отчёт сервера (<c>--report</c>, 0-030): итог запуска и все окна метрик.</summary>
    public sealed class ServerReport
    {
        public string Mode { get; set; } = "";
        public int Size { get; set; }
        public string Bots { get; set; } = "";
        public int Port { get; set; } = -1;
        public string Machine { get; set; } = "";
        public string Configuration { get; set; } = "";

        /// <summary>От запуска процесса до строки готовности (порт слушается), с; без сети — до начала цикла тактов.</summary>
        public double ReadySeconds { get; set; }

        /// <summary>Рабочий набор и пик рабочего набора в миг готовности, МБ.</summary>
        public double ReadyWorkingSetMb { get; set; }
        public double ReadyPeakWorkingSetMb { get; set; }
        public double ReadyPrivateMb { get; set; }

        public MetricsSample Total { get; set; } = new MetricsSample();
        public List<MetricsSample> Windows { get; set; } = new List<MetricsSample>();

        public void Write(string path)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// Сборщик метрик сервера (0-030): такт из <see cref="TickMetrics"/>, память и сборки мусора процесса, трафик из
    /// <see cref="CountingTransport"/>. <see cref="Due"/> — пора ли выводить окно (раз в <c>--metrics</c> с), <see cref="TakeWindow"/>
    /// и <see cref="TakeTotal"/> — выборки. Всё, кроме <see cref="TickMetrics"/>, вызывается между тактами: выделяет память.
    /// </summary>
    public sealed class ServerMetrics : IDisposable
    {
        private readonly CountingTransport? _traffic;
        private readonly Func<int> _clients;
        private readonly long _periodTs;
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly long[] _peerOut0 = new long[CountingTransport.MaxPeers];
        private readonly long[] _peerIn0 = new long[CountingTransport.MaxPeers];
        private readonly int[] _gc0 = new int[3];
        private readonly int[] _gcStart = new int[3];
        private readonly long[] _gcIndex = new long[3];
        private long _windowTs;
        private long _startTs;
        private TimeSpan _pause0;
        private TimeSpan _pauseStart;
        private long _out0;
        private long _in0;

        // Итог по окнам.
        private double _clientSeconds;
        private double _maxWorkingSet;
        private double _maxPrivate;
        private double _maxHeap;
        private double _maxPause;
        private double _maxOutClient;
        private double _maxInClient;

        /// <param name="periodSeconds">Окно, с; 0 — окна не выводятся (только итог).</param>
        public ServerMetrics(TickMetrics tick, CountingTransport? traffic, Func<int> clients, double periodSeconds)
        {
            Tick = tick;
            _traffic = traffic;
            _clients = clients;
            _periodTs = periodSeconds > 0 ? (long)(periodSeconds * Stopwatch.Frequency) : 0;
        }

        public TickMetrics Tick { get; }

        /// <summary>Выведенные окна (для отчёта).</summary>
        public List<MetricsSample> Windows { get; } = new List<MetricsSample>();

        /// <summary>Начало счёта: перед первым тактом.</summary>
        public void Start()
        {
            _startTs = _windowTs = Stopwatch.GetTimestamp();
            for (int g = 0; g < 3; g++)
            {
                _gc0[g] = _gcStart[g] = GC.CollectionCount(g);
            }

            _pause0 = _pauseStart = GC.GetTotalPauseDuration();
            foreach (GCKind kind in Kinds)
            {
                _gcIndex[(int)kind - 1] = GC.GetGCMemoryInfo(kind).Index;
            }

            _out0 = _traffic?.BytesOut ?? 0;
            _in0 = _traffic?.BytesIn ?? 0;
            for (int p = 0; p < CountingTransport.MaxPeers && _traffic != null; p++)
            {
                _peerOut0[p] = _traffic.PeerOut(p);
                _peerIn0[p] = _traffic.PeerIn(p);
            }
        }

        /// <summary>Пора выводить окно (между тактами).</summary>
        public bool Due() => _periodTs > 0 && Stopwatch.GetTimestamp() - _windowTs >= _periodTs;

        /// <summary>Выборка за окно; окно начинается заново.</summary>
        public MetricsSample TakeWindow()
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - _windowTs) / (double)Stopwatch.Frequency;
            var s = new MetricsSample { Seconds = seconds };
            FillTick(s, Tick.Window);

            for (int g = 0; g < 3; g++)
            {
                int c = GC.CollectionCount(g);
                int d = c - _gc0[g];
                _gc0[g] = c;
                if (g == 0)
                {
                    s.Gc0 = d;
                }
                else if (g == 1)
                {
                    s.Gc1 = d;
                }
                else
                {
                    s.Gc2 = d;
                }
            }

            TimeSpan pause = GC.GetTotalPauseDuration();
            s.GcPauseTotalMs = (pause - _pause0).TotalMilliseconds;
            _pause0 = pause;
            foreach (GCKind kind in Kinds)
            {
                GCMemoryInfo info = GC.GetGCMemoryInfo(kind);
                if (info.Index != _gcIndex[(int)kind - 1])
                {
                    _gcIndex[(int)kind - 1] = info.Index;
                    foreach (TimeSpan p in info.PauseDurations)
                    {
                        s.GcPauseMaxMs = Math.Max(s.GcPauseMaxMs, p.TotalMilliseconds);
                    }
                }
            }

            FillMemory(s);

            int clients = _clients();
            s.Clients = clients;
            if (_traffic != null && seconds > 0)
            {
                long o = _traffic.BytesOut, i = _traffic.BytesIn;
                s.BytesOut = o - _out0;
                s.BytesIn = i - _in0;
                _out0 = o;
                _in0 = i;
                if (clients > 0)
                {
                    s.KbpsOutPerClient = s.BytesOut / 1024.0 / seconds / clients;
                    s.KbpsInPerClient = s.BytesIn / 1024.0 / seconds / clients;
                }

                for (int p = 0; p < CountingTransport.MaxPeers; p++)
                {
                    long po = _traffic.PeerOut(p), pi = _traffic.PeerIn(p);
                    s.KbpsOutMaxClient = Math.Max(s.KbpsOutMaxClient, (po - _peerOut0[p]) / 1024.0 / seconds);
                    s.KbpsInMaxClient = Math.Max(s.KbpsInMaxClient, (pi - _peerIn0[p]) / 1024.0 / seconds);
                    _peerOut0[p] = po;
                    _peerIn0[p] = pi;
                }
            }

            _clientSeconds += clients * seconds;
            _maxWorkingSet = Math.Max(_maxWorkingSet, s.WorkingSetMb);
            _maxPrivate = Math.Max(_maxPrivate, s.PrivateMb);
            _maxHeap = Math.Max(_maxHeap, s.HeapMb);
            _maxPause = Math.Max(_maxPause, s.GcPauseMaxMs);
            _maxOutClient = Math.Max(_maxOutClient, s.KbpsOutMaxClient);
            _maxInClient = Math.Max(_maxInClient, s.KbpsInMaxClient);

            Tick.ResetWindow();
            _windowTs = now;
            Windows.Add(s);
            return s;
        }

        /// <summary>
        /// Итог запуска. Хвост после последнего окна сначала закрывается окном (если в нём были такты), чтобы GC, память и
        /// трафик итога сошлись с суммой окон.
        /// </summary>
        public MetricsSample TakeTotal()
        {
            if (Tick.Window.Ticks > 0 || Windows.Count == 0)
            {
                TakeWindow();
            }

            double seconds = (_windowTs - _startTs) / (double)Stopwatch.Frequency;
            var s = new MetricsSample { Seconds = seconds };
            FillTick(s, Tick.Total);
            s.Gc0 = GC.CollectionCount(0) - _gcStart[0];
            s.Gc1 = GC.CollectionCount(1) - _gcStart[1];
            s.Gc2 = GC.CollectionCount(2) - _gcStart[2];
            s.GcPauseTotalMs = (GC.GetTotalPauseDuration() - _pauseStart).TotalMilliseconds;
            s.GcPauseMaxMs = _maxPause;
            FillMemory(s);
            s.WorkingSetMb = Math.Max(s.WorkingSetMb, _maxWorkingSet);
            s.PrivateMb = Math.Max(s.PrivateMb, _maxPrivate);
            s.HeapMb = Math.Max(s.HeapMb, _maxHeap);
            s.Clients = seconds > 0 ? _clientSeconds / seconds : 0;
            foreach (MetricsSample w in Windows)
            {
                s.BytesOut += w.BytesOut;
                s.BytesIn += w.BytesIn;
            }

            if (_clientSeconds > 0)
            {
                s.KbpsOutPerClient = s.BytesOut / 1024.0 / _clientSeconds;
                s.KbpsInPerClient = s.BytesIn / 1024.0 / _clientSeconds;
            }

            s.KbpsOutMaxClient = _maxOutClient;
            s.KbpsInMaxClient = _maxInClient;
            return s;
        }

        /// <summary>Память процесса сейчас: рабочий набор, пик, частная, МБ.</summary>
        public static (double WorkingSetMb, double PeakWorkingSetMb, double PrivateMb) ProcessMemory()
        {
            using Process p = Process.GetCurrentProcess();
            return (p.WorkingSet64 / Mb, p.PeakWorkingSet64 / Mb, p.PrivateMemorySize64 / Mb);
        }

        /// <summary>Секунд с запуска процесса (по времени старта процесса в ОС).</summary>
        public static double SecondsSinceProcessStart()
        {
            using Process p = Process.GetCurrentProcess();
            return (DateTime.Now - p.StartTime).TotalSeconds;
        }

        public void Dispose() => _process.Dispose();

        private const double Mb = 1024.0 * 1024.0;

        private static readonly GCKind[] Kinds = { GCKind.Ephemeral, GCKind.FullBlocking, GCKind.Background };

        private static void FillTick(MetricsSample s, TickStats t)
        {
            s.Ticks = t.Ticks;
            s.TickMeanMs = t.MeanMs;
            s.TickP50Ms = t.PercentileMs(0.5);
            s.TickP99Ms = t.PercentileMs(0.99);
            s.TickP999Ms = t.PercentileMs(0.999);
            s.TickMaxMs = t.MaxMs;
            s.Parts = new PartSample[TickMetrics.PartCount];
            for (int i = 0; i < TickMetrics.PartCount; i++)
            {
                var part = (TickPart)i;
                s.Parts[i] = new PartSample
                {
                    Name = TickMetrics.PartNames[i],
                    MeanMs = t.PartMeanMs(part),
                    MaxMs = t.PartMaxMs(part),
                    Percent = 100 * t.PartShare(part),
                    AllocatedBytesPerTick = t.PartAllocatedPerTick(part),
                };
            }

            s.Stalls = t.Stalls;
            s.Gaps = t.Gaps;
            s.StallPercent = 100 * t.StallShare;
            s.MaxGapMs = t.MaxGapMicros / 1000.0;
            s.AllocatedBytesPerTick = t.Ticks > 0 ? t.AllocatedInTicks / (double)t.Ticks : 0;
            s.TicksWithAllocations = t.TicksWithAllocations;
        }

        private void FillMemory(MetricsSample s)
        {
            _process.Refresh();
            s.WorkingSetMb = _process.WorkingSet64 / Mb;
            s.PeakWorkingSetMb = _process.PeakWorkingSet64 / Mb;
            s.PrivateMb = _process.PrivateMemorySize64 / Mb;
            s.HeapMb = GC.GetGCMemoryInfo().HeapSizeBytes / Mb;
        }
    }
}
