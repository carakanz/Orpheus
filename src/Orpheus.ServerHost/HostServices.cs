using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Orpheus.Net.Transport;
using Orpheus.Save;

namespace Orpheus.Server
{
    /// <summary>Уровень записи лога (docs/07, 11.6).</summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
    }

    /// <summary>
    /// Приёмник лога сервера (0-031): <see cref="ServerHost"/> пишет только сюда. В программе <c>OrpheusServer</c> — лог
    /// JSON-строками (<c>JsonLog</c>, файл и консоль); в клиенте «сервер в потоке» — <see cref="TextServerLog"/> в
    /// <see cref="TextWriter"/> (лог Unity). Пишут поток такта и фоновые (запись <c>host.lock</c>) — реализация потокобезопасна.
    /// </summary>
    public interface IServerLog
    {
        /// <summary>Пишется ли уровень <paramref name="level"/> в категории <paramref name="cat"/>.</summary>
        bool Enabled(string cat, LogLevel level);

        /// <summary>Записать строку лога.</summary>
        void Write(LogLevel level, long tick, string cat, string msg, (string Key, object? Value)[] data);
    }

    /// <summary>Короткие вызовы лога по уровням.</summary>
    public static class ServerLogExtensions
    {
        public static void Info(this IServerLog log, long tick, string cat, string msg, params (string Key, object? Value)[] data) =>
            log.Write(LogLevel.Info, tick, cat, msg, data);

        public static void Warn(this IServerLog log, long tick, string cat, string msg, params (string Key, object? Value)[] data) =>
            log.Write(LogLevel.Warn, tick, cat, msg, data);

        public static void Error(this IServerLog log, long tick, string cat, string msg, params (string Key, object? Value)[] data) =>
            log.Write(LogLevel.Error, tick, cat, msg, data);

        public static void Debug(this IServerLog log, long tick, string cat, string msg, params (string Key, object? Value)[] data) =>
            log.Write(LogLevel.Debug, tick, cat, msg, data);
    }

    /// <summary>
    /// Лог строками <c>tick level cat msg key=value…</c> в <see cref="TextWriter"/> — для «сервера в потоке» (лог Unity) и
    /// консолей, где JSON-писателя нет (docs/07, 1.3: JSON в ядре нет). Уровень ниже <see cref="MinLevel"/> не пишется.
    /// </summary>
    public sealed class TextServerLog : IServerLog
    {
        private readonly object _sync = new object();
        private readonly TextWriter? _out;
        private readonly string _prefix;

        /// <param name="output">Куда писать; null — никуда (только счёт строк).</param>
        /// <param name="prefix">Начало каждой строки (например, <c>[server]</c>).</param>
        public TextServerLog(TextWriter? output, string prefix = "[server]")
        {
            _out = output;
            _prefix = prefix;
        }

        public LogLevel MinLevel { get; set; } = LogLevel.Info;

        /// <summary>Записано строк уровня «ошибка» (тесты и smoke смотрят, что их нет).</summary>
        public int Errors { get; private set; }

        /// <summary>Записано строк уровня «предупреждение».</summary>
        public int Warnings { get; private set; }

        public bool Enabled(string cat, LogLevel level) => level >= MinLevel;

        public void Write(LogLevel level, long tick, string cat, string msg, (string Key, object? Value)[] data)
        {
            if (!Enabled(cat, level))
            {
                return;
            }

            var sb = new System.Text.StringBuilder(128);
            sb.Append(_prefix).Append(' ').Append(tick).Append(' ').Append(LevelName(level)).Append(' ').Append(cat).Append(' ').Append(msg);
            if (data != null)
            {
                foreach ((string key, object? value) in data)
                {
                    sb.Append(' ').Append(key).Append('=');
                    sb.Append(value is IFormattable f ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : value?.ToString());
                }
            }

            lock (_sync)
            {
                if (level == LogLevel.Error)
                {
                    Errors++;
                }
                else if (level == LogLevel.Warn)
                {
                    Warnings++;
                }

                try
                {
                    _out?.WriteLine(sb.ToString());
                }
                catch (IOException)
                {
                    // вывода нет — сервер работает дальше
                }
            }
        }

        public static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Debug => "debug",
            LogLevel.Info => "info",
            LogLevel.Warn => "warn",
            _ => "error",
        };
    }

    /// <summary>Память процесса или рантайма в миг замера, МБ (0 — платформа не знает).</summary>
    public struct RuntimeMemory
    {
        public double WorkingSetMb;
        public double PeakWorkingSetMb;
        public double PrivateMb;
        public double HeapMb;
    }

    /// <summary>
    /// Рантайм для метрик (0-030, 0-031): память процесса и паузы сборки мусора. В <c>OrpheusServer</c> — процесс ОС и
    /// <c>GC.GetGCMemoryInfo</c> (.NET 9); в потоке игры — <see cref="PortableRuntimeStats"/> (куча и время с создания хоста).
    /// Вызывается между тактами.
    /// </summary>
    public interface IRuntimeStats
    {
        /// <summary>Секунд от старта: у процесса сервера — с запуска процесса (приёмка 0.8), в потоке — с создания хоста.</summary>
        double SecondsSinceStart();

        RuntimeMemory Memory();

        /// <summary>Сумма пауз сборки мусора с начала работы рантайма, мс (0 — неизвестно).</summary>
        double GcPauseTotalMs();

        /// <summary>Наибольшая пауза среди сборок с прошлого вызова, мс (0 — сборок не было или неизвестно).</summary>
        double TakeGcPauseMaxMs();
    }

    /// <summary>Рантайм без процесса ОС (netstandard2.1, Unity, консоль): куча — <c>GC.GetTotalMemory</c>, паузы неизвестны.</summary>
    public sealed class PortableRuntimeStats : IRuntimeStats
    {
        private readonly Stopwatch _since = Stopwatch.StartNew();

        public double SecondsSinceStart() => _since.Elapsed.TotalSeconds;

        public RuntimeMemory Memory() => new RuntimeMemory { HeapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0) };

        public double GcPauseTotalMs() => 0;

        public double TakeGcPauseMaxMs() => 0;
    }

    /// <summary>
    /// Отладочный крючок записи сохранений (приёмка 0.4, <c>SaveCrashHook</c> в <c>OrpheusServer</c>): в обычной игре его нет.
    /// </summary>
    public interface ISaveHook
    {
        /// <summary>План для лога.</summary>
        string Plan { get; }

        /// <summary>Сервер будет сохранять в это хранилище — повесить крючок.</summary>
        void Attach(IPlatformStorage storage);

        /// <summary>Мир загружен (или создан): номер поколения, такт, хэш состояния.</summary>
        void OnLoaded(ulong number, long tick, ulong hash);

        void OnBegin(ulong number, long tick, ulong hash);

        void OnDone(ulong number);
    }

    /// <summary>
    /// Всё платформенное, что нужно <see cref="ServerHost"/> (0-031, docs/07 1.6): хранилище, часы такта, лог, рантайм,
    /// журналы, вывод. Программа <c>OrpheusServer</c> подставляет «ПК» (<c>PcStorage</c>, <c>PcTickClock</c>, <c>JsonLog</c>,
    /// процесс ОС); клиент в потоке — то же хранилище и локальную связь вместо UDP.
    /// </summary>
    public sealed class ServerHostServices
    {
        /// <summary>Лог; null — никуда.</summary>
        public IServerLog? Log { get; set; }

        /// <summary>Хранилище платформы для папки мира (<c>--world</c>): замок, снимок генерации, поколения, <c>host.lock</c>.</summary>
        public IPlatformStorage? Storage { get; set; }

        /// <summary>Часы такта обычного режима; null — <see cref="TickPacer"/> (Thread.Sleep, без таймера ОС).</summary>
        public Func<int, TickPacer>? CreateClock { get; set; }

        /// <summary>Рантайм для метрик; null — <see cref="PortableRuntimeStats"/>.</summary>
        public IRuntimeStats? Stats { get; set; }

        /// <summary>
        /// Локальная связь (0-031): серверный конец <see cref="LoopbackTransport"/> — сервер в потоке игры, UDP не открывается,
        /// ключ <c>--port</c> не нужен. null — сеть по <c>--port</c> (UDP).
        /// </summary>
        public ITransport? LocalTransport { get; set; }

        /// <summary>Готов принимать подключения (с сетью) или начинать такты (без сети): порт (0 — локальная связь, −1 — сети нет).</summary>
        public Action<int>? Ready { get; set; }

        /// <summary>Куда писать строку готовности <c>orpheus-ready port=P</c> (stdout программы); null — не писать.</summary>
        public TextWriter? ReadyOutput { get; set; }

        /// <summary>Открыть журнал ввода для повтора (<c>--replay</c>); null — повтор недоступен.</summary>
        public Func<string, Stream>? OpenRead { get; set; }

        /// <summary>Создать файл журнала ввода (<c>--record</c>); null — запись недоступна.</summary>
        public Func<string, Stream>? CreateWrite { get; set; }

        /// <summary>Отладочный крючок сохранений; null — нет.</summary>
        public ISaveHook? SaveHook { get; set; }

        /// <summary>Номер процесса для <c>host.lock</c> (сервер игры, 0-024).</summary>
        public int ProcessId { get; set; }
    }

    /// <summary>
    /// Часы такта обычного режима (docs/07, 2.4): такт 33,3 мс. Отставание: такт длился дольше — следующий сразу; до 3 тактов
    /// догоняется. Больше 3 тактов (100 мс) — лишние такты не догоняются (отсчёт переставляется), <see cref="WaitNext"/>
    /// возвращает число брошенных тактов — сервер пишет «перегрузка». Здесь ожидание — <c>Thread.Sleep</c> (переносимо: поток
    /// игры, консоль); таймер высокого разрешения ОС — у платформы (<c>PcTickClock</c>: ожидающий таймер Windows,
    /// <c>clock_nanosleep</c> Linux) переопределением <see cref="Sleep"/>.
    /// </summary>
    public class TickPacer : IDisposable
    {
        /// <summary>Наибольшее догоняемое отставание, тактов.</summary>
        public const int MaxCatchUpTicks = 3;

        private readonly long _tickTicks; // длина такта в тиках Stopwatch
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private long _next;

        public TickPacer(int tickRate)
        {
            _tickTicks = Stopwatch.Frequency / tickRate;
            _next = _tickTicks;
        }

        /// <summary>Как ждём: для лога.</summary>
        public virtual string Method => "thread-sleep";

        /// <summary>Ждать начала следующего такта. Возвращает число брошенных тактов (0 — всё в срок или догнали).</summary>
        public long WaitNext(CancellationToken stop)
        {
            long now = _watch.ElapsedTicks;
            long behind = now - _next;
            long dropped = 0;
            if (behind > _tickTicks * MaxCatchUpTicks)
            {
                dropped = behind / _tickTicks - MaxCatchUpTicks;
                _next += dropped * _tickTicks;
            }

            while (!stop.IsCancellationRequested)
            {
                long wait = _next - _watch.ElapsedTicks;
                if (wait <= 0)
                {
                    break;
                }

                Sleep(wait * 1_000_000_000 / Stopwatch.Frequency);
            }

            _next += _tickTicks;
            return dropped;
        }

        /// <summary>Подождать около <paramref name="nanos"/> нс.</summary>
        protected virtual void Sleep(long nanos) => Thread.Sleep((int)Math.Max(1, nanos / 1_000_000));

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
        }
    }

    /// <summary>Счёт выделений потока такта (11.6) — переносимо: рантайм без него (старый Mono) даёт 0, а не падение такта.</summary>
    public static class HostRuntime
    {
        private static bool _unsupported;

        /// <summary>Байт выделено текущим потоком с его старта; не поддерживается — 0.</summary>
        public static long AllocatedBytes()
        {
            if (_unsupported)
            {
                return 0;
            }

            try
            {
                return GC.GetAllocatedBytesForCurrentThread();
            }
            catch (Exception e) when (e is NotImplementedException || e is NotSupportedException || e is PlatformNotSupportedException)
            {
                _unsupported = true;
                return 0;
            }
        }
    }
}
