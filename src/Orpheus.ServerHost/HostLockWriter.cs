using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Orpheus.Save;

namespace Orpheus.Server
{
    /// <summary>
    /// Запись файла-замка хоста <c>host.lock</c> вне потока такта (0-024, замечания 1 и 6 проверки).
    /// <para>
    /// Замок пишется атомарно (<see cref="HostLockFile.Write"/> → <see cref="IPlatformStorage.WriteAtomic"/>), а перенос
    /// <c>.tmp</c> на место отказывает («Access is denied», код 5), пока <c>host.lock</c> открыт кем-то ещё без
    /// FILE_SHARE_DELETE: его раз в секунду читает вернувшаяся игра хоста, заглядывают антивирус и индексатор. Поэтому
    /// запись — в своём потоке: отказ — предупреждение в лог и повтор через 50, 100, 200 … 1000 мс, пока не выйдет
    /// или сервер не остановится. Сервер от замка не падает и мир не теряет; такт не ждёт сброса на диск.
    /// </para>
    /// <para>
    /// Новая просьба, пока прежняя не записана, заменяет её: на диске нужен только последний вид замка.
    /// </para>
    /// </summary>
    public sealed class HostLockWriter : IDisposable
    {
        /// <summary>Пауза перед первым повтором; дальше — вдвое, до <see cref="MaxRetryMs"/>.</summary>
        public const int FirstRetryMs = 50;

        public const int MaxRetryMs = 1000;

        private readonly IPlatformStorage _storage;
        private readonly string _dir;
        private readonly IServerLog _log;
        private readonly object _gate = new object();
        private readonly Thread _thread;

        private HostLock? _pending;
        private long _pendingTick;
        private bool _busy;
        private bool _stop;
        private int _written;
        private int _failures;

        public HostLockWriter(IPlatformStorage storage, string worldDir, IServerLog log)
        {
            _storage = storage;
            _dir = worldDir;
            _log = log;
            _thread = new Thread(Run) { IsBackground = true, Name = "host.lock" };
            _thread.Start();
        }

        /// <summary>Записей замка удалось.</summary>
        public int Written
        {
            get
            {
                lock (_gate)
                {
                    return _written;
                }
            }
        }

        /// <summary>Попыток записи не удалось (каждая — повторена).</summary>
        public int Failures
        {
            get
            {
                lock (_gate)
                {
                    return _failures;
                }
            }
        }

        /// <summary>Записать замок в фоне (копия <paramref name="l"/> берётся сейчас); <paramref name="tick"/> — для лога.</summary>
        public void Request(HostLock l, long tick)
        {
            var copy = new HostLock { Port = l.Port, Token = l.Token, ProcessId = l.ProcessId, Mode = l.Mode, OpenPort = l.OpenPort };
            lock (_gate)
            {
                _pending = copy;
                _pendingTick = tick;
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>Дождаться, пока всё попрошенное записано; не успело за <paramref name="timeoutMs"/> — false (повторы идут дальше).</summary>
        public bool WaitWritten(int timeoutMs)
        {
            var waited = Stopwatch.StartNew(); // Environment.TickCount64 нет в netstandard2.1
            lock (_gate)
            {
                while (_pending != null || _busy)
                {
                    long left = timeoutMs - waited.ElapsedMilliseconds;
                    if (left <= 0)
                    {
                        return false;
                    }

                    Monitor.Wait(_gate, (int)left);
                }

                return true;
            }
        }

        /// <summary>Остановить поток (недописанное бросается — при штатном выходе замок всё равно удаляется).</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _stop = true;
                Monitor.PulseAll(_gate);
            }

            _thread.Join(5000);
        }

        private void Run()
        {
            HostLock? l = null;
            long tick = 0;
            int delay = FirstRetryMs;
            int failed = 0;
            while (true)
            {
                lock (_gate)
                {
                    if (l == null)
                    {
                        _busy = false;
                        Monitor.PulseAll(_gate);
                        while (_pending == null && !_stop)
                        {
                            Monitor.Wait(_gate);
                        }
                    }
                    else
                    {
                        Monitor.Wait(_gate, delay); // пауза перед повтором; новая просьба или остановка будят раньше
                        delay = Math.Min(delay * 2, MaxRetryMs);
                    }

                    if (_stop)
                    {
                        _busy = false;
                        Monitor.PulseAll(_gate);
                        return;
                    }

                    if (_pending != null)
                    {
                        l = _pending;
                        tick = _pendingTick;
                        _pending = null;
                    }

                    _busy = true;
                }

                try
                {
                    HostLockFile.Write(_storage, _dir, l!);
                    lock (_gate)
                    {
                        _written++;
                    }

                    if (failed > 0)
                    {
                        Log(LogLevel.Info, tick, "файл-замок хоста записан с повтора", ("attempts", failed + 1));
                    }

                    l = null;
                    failed = 0;
                    delay = FirstRetryMs;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    lock (_gate)
                    {
                        _failures++;
                    }

                    if (failed++ == 0)
                    {
                        Log(LogLevel.Warn, tick, "файл-замок хоста не записан — повторю вне такта", ("error", e.Message),
                            ("path", HostLockFile.PathIn(_dir)));
                    }
                }
                catch (Exception e)
                {
                    // Не ввод-вывод — повтор не поможет; поток не должен ронять процесс.
                    Log(LogLevel.Error, tick, "файл-замок хоста не записан", ("error", e.Message), ("exception", e.GetType().Name));
                    l = null;
                    failed = 0;
                    delay = FirstRetryMs;
                }
            }
        }

        private void Log(LogLevel level, long tick, string msg, params (string Key, object? Value)[] data)
        {
            try
            {
                _log.Write(level, tick, "save", msg, data);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ObjectDisposedException)
            {
                // лог недоступен — замку это не мешает
            }
        }
    }
}
