using System;
using System.Runtime.InteropServices;
using System.Threading;
using Orpheus.Server;

namespace Orpheus.Platform.Pc
{
    /// <summary>
    /// Часы такта «ПК» (docs/07, 2.4; вынесено из OrpheusServer в 0-031): правила такта и отставания — <see cref="TickPacer"/>
    /// (ServerHost), ожидание — на таймере высокого разрешения: Windows — ожидающий таймер с
    /// <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>, Linux — <c>clock_nanosleep</c>; не <c>Thread.Sleep</c> (запасной путь — только
    /// если таймера нет). P/Invoke — только здесь, в платформенном слое (1.6). Те же часы — кадр 60 Гц у ботов.
    /// </summary>
    public sealed class PcTickClock : TickPacer
    {
        private readonly IntPtr _winTimer;

        public PcTickClock(int tickRate)
            : base(tickRate)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _winTimer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            }
        }

        /// <summary>Как ждём: для лога.</summary>
        public override string Method =>
            _winTimer != IntPtr.Zero ? "waitable-timer-high-res"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "clock_nanosleep"
            : "thread-sleep";

        protected override void Dispose(bool disposing)
        {
            if (_winTimer != IntPtr.Zero)
            {
                CloseHandle(_winTimer);
            }
        }

        protected override void Sleep(long nanos)
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
