using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Orpheus.Server
{
    /// <summary>
    /// Часы такта обычного режима (docs/07, 2.4): такт 33,3 мс, ожидание на таймере высокого разрешения —
    /// Windows: ожидающий таймер с <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>, Linux: <c>clock_nanosleep</c>;
    /// не <c>Thread.Sleep</c> (запасной путь — только если таймера нет).
    /// <para>
    /// Отставание: такт длился дольше — следующий сразу; до 3 тактов догоняется. Больше 3 тактов (100 мс) —
    /// лишние такты не догоняются (отсчёт переставляется), <see cref="WaitNext"/> возвращает число брошенных
    /// тактов — сервер пишет предупреждение «перегрузка».
    /// </para>
    /// В режиме симуляции часов нет вовсе — такты идут подряд.
    /// </summary>
    public sealed class TickClock : IDisposable
    {
        /// <summary>Наибольшее догоняемое отставание, тактов.</summary>
        public const int MaxCatchUpTicks = 3;

        private readonly long _tickTicks; // длина такта в тиках Stopwatch
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly IntPtr _winTimer;
        private long _next;

        public TickClock(int tickRate)
        {
            _tickTicks = Stopwatch.Frequency / tickRate;
            _next = _tickTicks;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _winTimer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            }
        }

        /// <summary>Как ждём: для лога.</summary>
        public string Method =>
            _winTimer != IntPtr.Zero ? "waitable-timer-high-res"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "clock_nanosleep"
            : "thread-sleep";

        /// <summary>
        /// Ждать начала следующего такта. Возвращает число брошенных тактов (0 — всё в срок или догнали).
        /// </summary>
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

        public void Dispose()
        {
            if (_winTimer != IntPtr.Zero)
            {
                CloseHandle(_winTimer);
            }
        }

        private void Sleep(long nanos)
        {
            if (_winTimer != IntPtr.Zero)
            {
                long due = -(nanos / 100); // отрицательное — относительное время в 100 нс
                if (due == 0)
                {
                    due = -1;
                }

                if (SetWaitableTimer(_winTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    WaitForSingleObject(_winTimer, Infinite);
                    return;
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var ts = new Timespec { Sec = nanos / 1_000_000_000, Nsec = nanos % 1_000_000_000 };
                if (clock_nanosleep(ClockMonotonic, 0, ref ts, IntPtr.Zero) == 0)
                {
                    return;
                }
            }

            Thread.Sleep((int)Math.Max(1, nanos / 1_000_000));
        }

        // ---------- Windows ----------

        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x001F0003;
        private const uint Infinite = 0xFFFFFFFF;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        // ---------- Linux ----------

        private const int ClockMonotonic = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec
        {
            public long Sec;
            public long Nsec;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int clock_nanosleep(int clockId, int flags, ref Timespec request, IntPtr remain);
    }
}
