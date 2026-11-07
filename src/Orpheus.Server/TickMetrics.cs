using System;
using System.Diagnostics;

namespace Orpheus.Server
{
    /// <summary>Части такта сервера для метрик (docs/07 11.6 «по подсистемам»; 0-030).</summary>
    public enum TickPart
    {
        /// <summary>Приём: события транспорта, вход, ввод игроков (<c>NetServer.Poll</c>), ожидание хоста.</summary>
        Input = 0,

        /// <summary>Решения ботов сервера (<c>SimBot.Think</c>) и ввод из журнала — то, что потом станет ИИ.</summary>
        Ai = 1,

        /// <summary>Шаг симуляции (<c>Simulation.Step</c>).</summary>
        Sim = 2,

        /// <summary>
        /// Рассылка (<c>NetServer.SendSnapshots</c>): правки тайлов, окна обзора и чанки, снимки и дельты, <c>Flush</c>.
        /// Внутри <c>NetServer</c> не делится — карточка 0-030 его не трогает.
        /// </summary>
        Net = 3,

        /// <summary>Сохранение: запас пулов (<c>RefillPools</c>) и автосохранение на границе такта (<c>WorldSaver.AfterTick</c>).</summary>
        Save = 4,

        /// <summary>Прочее: запись журнала ввода, хэш на точке сверки, сверка повтора, крючок тестов.</summary>
        Other = 5,
    }

    /// <summary>
    /// Метрики такта сервера (docs/07 11.6; 0-030): время такта — среднее, процентили (по гистограмме с шагом
    /// <see cref="BinMicros"/> мкс), наибольшее; время по частям (<see cref="TickPart"/>); простои — промежуток между
    /// началами соседних тактов больше такта на <see cref="StallSlackMicros"/> (только с часами); байты, выделенные потоком
    /// такта внутри такта. Два счёта: окно (сбрасывается при каждом выводе в лог) и весь запуск.
    /// <para>
    /// Запись в такте — <see cref="BeginTick"/>, <see cref="Mark"/>, <see cref="EndTick"/> — без выделений памяти
    /// (<c>AllocationTests</c>): массивы заведены заранее, время — <see cref="Stopwatch.GetTimestamp"/>. Память, сборки
    /// мусора и трафик снимаются при выводе (<see cref="ServerMetrics"/>), вне такта.
    /// </para>
    /// </summary>
    public sealed class TickMetrics
    {
        /// <summary>Частей такта.</summary>
        public const int PartCount = 6;

        /// <summary>Шаг гистограммы, мкс.</summary>
        public const int BinMicros = 10;

        /// <summary>Корзин гистограммы: до 200 мс; длиннее — в последнюю (наибольшее хранится точно).</summary>
        public const int Bins = 20_000;

        /// <summary>Запас сверх такта, после которого промежуток между тактами — простой (как в Big UDP, 0-032), мкс.</summary>
        public const long StallSlackMicros = 5000;

        /// <summary>Имена частей для лога и отчёта.</summary>
        public static readonly string[] PartNames = { "input", "ai", "sim", "net", "save", "other" };

        private readonly long _tickMicros;
        private readonly long[] _partTs = new long[PartCount];
        private long _tickStart;
        private long _mark;
        private long _prevStart = -1;
        private long _alloc0;

        /// <param name="tickMicros">Длина такта по часам, мкс; 0 — часов нет (<c>--sim</c>), простои не считаются.</param>
        public TickMetrics(long tickMicros)
        {
            _tickMicros = tickMicros;
        }

        /// <summary>Текущее окно (с прошлого <see cref="ResetWindow"/>).</summary>
        public TickStats Window { get; } = new TickStats();

        /// <summary>Весь запуск.</summary>
        public TickStats Total { get; } = new TickStats();

        /// <summary>Считаются ли простои (есть часы).</summary>
        public bool TracksStalls => _tickMicros > 0;

        /// <summary>Порог простоя, мкс (такт + запас); 0 — не считаются.</summary>
        public long StallThresholdMicros => _tickMicros > 0 ? _tickMicros + StallSlackMicros : 0;

        /// <summary>Начало такта: до опроса транспорта.</summary>
        public void BeginTick()
        {
            long now = Stopwatch.GetTimestamp();
            if (_prevStart >= 0 && _tickMicros > 0)
            {
                long gap = Micros(now - _prevStart);
                bool stall = gap > _tickMicros + StallSlackMicros;
                Window.AddGap(gap, stall);
                Total.AddGap(gap, stall);
            }

            _prevStart = now;
            _tickStart = now;
            _mark = now;
            Array.Clear(_partTs, 0, PartCount);
            _alloc0 = GC.GetAllocatedBytesForCurrentThread();
        }

        /// <summary>Закончилась часть <paramref name="part"/>: время с прошлой отметки — ей.</summary>
        public void Mark(TickPart part)
        {
            long now = Stopwatch.GetTimestamp();
            _partTs[(int)part] += now - _mark;
            _mark = now;
        }

        /// <summary>Конец такта: до ожидания часов (и до отладочной заморозки).</summary>
        public void EndTick()
        {
            long now = Stopwatch.GetTimestamp();
            long alloc = GC.GetAllocatedBytesForCurrentThread() - _alloc0;
            _partTs[(int)TickPart.Other] += now - _mark; // неразмеченный хвост — в «прочее»
            long total = Micros(now - _tickStart);
            Window.AddTick(total, _partTs, alloc);
            Total.AddTick(total, _partTs, alloc);
        }

        /// <summary>Начать новое окно.</summary>
        public void ResetWindow() => Window.Reset();

        internal static long Micros(long timestampDelta) =>
            timestampDelta / Stopwatch.Frequency * 1_000_000 + timestampDelta % Stopwatch.Frequency * 1_000_000 / Stopwatch.Frequency;
    }

    /// <summary>Накопленная статистика тактов (окно или весь запуск).</summary>
    public sealed class TickStats
    {
        private readonly int[] _hist = new int[TickMetrics.Bins];
        private readonly long[] _partSumTs = new long[TickMetrics.PartCount];
        private readonly long[] _partMaxTs = new long[TickMetrics.PartCount];

        /// <summary>Тактов.</summary>
        public long Ticks { get; private set; }

        /// <summary>Сумма времени тактов, мкс.</summary>
        public long SumMicros { get; private set; }

        /// <summary>Наибольший такт, мкс.</summary>
        public long MaxMicros { get; private set; }

        /// <summary>Промежутков между началами тактов (с часами).</summary>
        public long Gaps { get; private set; }

        /// <summary>Из них простоев (больше такта + запас).</summary>
        public long Stalls { get; private set; }

        /// <summary>Наибольший промежуток между началами тактов, мкс.</summary>
        public long MaxGapMicros { get; private set; }

        /// <summary>Байт выделено потоком такта внутри тактов.</summary>
        public long AllocatedInTicks { get; private set; }

        /// <summary>Тактов, в которых что-то выделено.</summary>
        public long TicksWithAllocations { get; private set; }

        /// <summary>Средний такт, мс.</summary>
        public double MeanMs => Ticks > 0 ? SumMicros / 1000.0 / Ticks : 0;

        /// <summary>Наибольший такт, мс.</summary>
        public double MaxMs => MaxMicros / 1000.0;

        /// <summary>Доля простоев такта (0…1).</summary>
        public double StallShare => Gaps > 0 ? Stalls / (double)Gaps : 0;

        /// <summary>Процентиль <paramref name="q"/> (0…1) времени такта, мс — верхняя граница корзины, не больше наибольшего.</summary>
        public double PercentileMs(double q)
        {
            if (Ticks == 0)
            {
                return 0;
            }

            long rank = (long)Math.Ceiling(q * Ticks);
            if (rank < 1)
            {
                rank = 1;
            }

            long seen = 0;
            for (int i = 0; i < _hist.Length; i++)
            {
                seen += _hist[i];
                if (seen >= rank)
                {
                    return Math.Min((i + 1) * (double)TickMetrics.BinMicros, MaxMicros) / 1000.0;
                }
            }

            return MaxMs;
        }

        /// <summary>Среднее время части на такт, мс.</summary>
        public double PartMeanMs(TickPart part) =>
            Ticks > 0 ? TickMetrics.Micros(_partSumTs[(int)part]) / 1000.0 / Ticks : 0;

        /// <summary>Наибольшее время части за такт, мс.</summary>
        public double PartMaxMs(TickPart part) => TickMetrics.Micros(_partMaxTs[(int)part]) / 1000.0;

        /// <summary>Доля части в сумме тактов (0…1).</summary>
        public double PartShare(TickPart part) =>
            SumMicros > 0 ? TickMetrics.Micros(_partSumTs[(int)part]) / (double)SumMicros : 0;

        internal void AddTick(long micros, long[] partTs, long allocated)
        {
            Ticks++;
            SumMicros += micros;
            if (micros > MaxMicros)
            {
                MaxMicros = micros;
            }

            long bin = micros / TickMetrics.BinMicros;
            _hist[bin < TickMetrics.Bins ? (int)bin : TickMetrics.Bins - 1]++;
            for (int i = 0; i < TickMetrics.PartCount; i++)
            {
                _partSumTs[i] += partTs[i];
                if (partTs[i] > _partMaxTs[i])
                {
                    _partMaxTs[i] = partTs[i];
                }
            }

            AllocatedInTicks += allocated;
            if (allocated > 0)
            {
                TicksWithAllocations++;
            }
        }

        internal void AddGap(long micros, bool stall)
        {
            Gaps++;
            if (stall)
            {
                Stalls++;
            }

            if (micros > MaxGapMicros)
            {
                MaxGapMicros = micros;
            }
        }

        internal void Reset()
        {
            Ticks = 0;
            SumMicros = 0;
            MaxMicros = 0;
            Gaps = 0;
            Stalls = 0;
            MaxGapMicros = 0;
            AllocatedInTicks = 0;
            TicksWithAllocations = 0;
            Array.Clear(_hist, 0, _hist.Length);
            Array.Clear(_partSumTs, 0, _partSumTs.Length);
            Array.Clear(_partMaxTs, 0, _partMaxTs.Length);
        }
    }
}
