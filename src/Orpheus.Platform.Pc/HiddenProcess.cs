using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Orpheus.Platform.Pc
{
    /// <summary>
    /// Дочерний процесс на невидимом рабочем столе <see cref="DesktopName"/> (C-022): тесты и инструменты не должны мигать
    /// экраном Автора. <c>CreateNoWindow</c> мало — рядом с каждым консольным процессом рождается <c>conhost.exe</c> на
    /// столе Автора (замер C-022), а запуск процесса на том же столе включает курсор «запуск приложения». Здесь — то же,
    /// что <c>tools/run_hidden.ps1</c>: <c>CreateDesktop</c> + <c>STARTUPINFO.lpDesktop</c>, плюс <c>CREATE_NO_WINDOW</c>,
    /// <c>STARTF_FORCEOFFFEEDBACK</c>, <c>SW_HIDE</c>. Потомки процесса (<c>lpDesktop</c> не задан) наследуют тот же стол.
    /// stdin/stdout/stderr — анонимные трубы (UTF-8); наследуются только они (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>) —
    /// чужие трубы параллельных запусков ребёнку не достаются. Процесс в своём объекте-задании: <see cref="Kill"/> с
    /// деревом снимает и его потомков; закрытие задания потомков не трогает (сервер живёт после игры, 2.3).
    /// Только Windows (<see cref="IsSupported"/>).
    /// </summary>
    public sealed class HiddenProcess : IDisposable
    {
        /// <summary>Имя невидимого стола (то же, что у <c>tools/run_hidden.ps1</c>).</summary>
        public const string DesktopName = "OrpheusHidden";

        private static readonly object CreateLock = new object();
        private static IntPtr _desktop; // держим до конца процесса: стол живёт, пока на нём есть дети

        private readonly SafeWaitHandle _process;
        private readonly IntPtr _job;
        private readonly ManualResetEvent _exited = new ManualResetEvent(false);
        private readonly List<Thread> _readers = new List<Thread>();
        private RegisteredWaitHandle? _exitWait;
        private int _disposed;

        private HiddenProcess(SafeWaitHandle process, IntPtr job, int id, Stream stdin, Stream stdout, Stream stderr)
        {
            _process = process;
            _job = job;
            Id = id;
            var utf8 = new UTF8Encoding(false);
            StandardInput = new StreamWriter(stdin, utf8) { AutoFlush = true };
            StandardOutput = new StreamReader(stdout, utf8, false);
            StandardError = new StreamReader(stderr, utf8, false);
            var handle = new ProcessWaitHandle(process);
            _exitWait = ThreadPool.RegisterWaitForSingleObject(handle, (_, __) => OnExited(), null, Timeout.Infinite, true);
        }

        /// <summary>Скрытый стол есть только в Windows.</summary>
        public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public int Id { get; }

        public StreamWriter StandardInput { get; }

        public StreamReader StandardOutput { get; }

        public StreamReader StandardError { get; }

        /// <summary>Процесс вышел (вызывается один раз, из пула потоков).</summary>
        public event Action? Exited;

        public bool HasExited => _exited.WaitOne(0) || WaitForSingleObject(_process, 0) == 0;

        public int ExitCode
        {
            get
            {
                if (!HasExited)
                {
                    throw new InvalidOperationException("Процесс ещё работает.");
                }

                GetExitCodeProcess(_process, out uint code);
                return unchecked((int)code);
            }
        }

        /// <summary>
        /// Запустить <paramref name="fileName"/> (полный путь или имя из PATH) с аргументами по правилам
        /// <see cref="PcServerLauncher.JoinArguments"/> на скрытом столе.
        /// </summary>
        public static HiddenProcess Start(string fileName, IEnumerable<string> arguments, string workingDirectory)
        {
            if (!IsSupported)
            {
                throw new PlatformNotSupportedException("Скрытый рабочий стол — только Windows.");
            }

            string args = PcServerLauncher.JoinArguments(arguments);
            var cmd = new StringBuilder(PcServerLauncher.JoinArguments(new[] { fileName }));
            if (args.Length > 0)
            {
                cmd.Append(' ').Append(args);
            }

            lock (CreateLock)
            {
                IntPtr desktop = EnsureDesktop();
                CreatePipe(out SafeFileHandle inRead, out SafeFileHandle inWrite, childReads: true);
                CreatePipe(out SafeFileHandle outRead, out SafeFileHandle outWrite, childReads: false);
                CreatePipe(out SafeFileHandle errRead, out SafeFileHandle errWrite, childReads: false);
                IntPtr deskName = Marshal.StringToHGlobalUni("WinSta0\\" + DesktopName);
                IntPtr handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                IntPtr attrs = IntPtr.Zero;
                IntPtr job = IntPtr.Zero;
                try
                {
                    Marshal.WriteIntPtr(handles, 0, inRead.DangerousGetHandle());
                    Marshal.WriteIntPtr(handles, IntPtr.Size, outWrite.DangerousGetHandle());
                    Marshal.WriteIntPtr(handles, IntPtr.Size * 2, errWrite.DangerousGetHandle());
                    IntPtr size = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    attrs = Marshal.AllocHGlobal(size);
                    Check(InitializeProcThreadAttributeList(attrs, 1, 0, ref size), "InitializeProcThreadAttributeList");
                    Check(UpdateProcThreadAttribute(attrs, 0, (IntPtr)ProcThreadAttributeHandleList, handles,
                        (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero), "UpdateProcThreadAttribute");

                    var si = new STARTUPINFOEX();
                    si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                    si.StartupInfo.lpDesktop = deskName;
                    si.StartupInfo.dwFlags = StartfUseStdHandles | StartfUseShowWindow | StartfForceOffFeedback;
                    si.StartupInfo.wShowWindow = 0; // SW_HIDE
                    si.StartupInfo.hStdInput = inRead.DangerousGetHandle();
                    si.StartupInfo.hStdOutput = outWrite.DangerousGetHandle();
                    si.StartupInfo.hStdError = errWrite.DangerousGetHandle();
                    si.lpAttributeList = attrs;

                    job = CreateJobObject(IntPtr.Zero, null);
                    Check(job != IntPtr.Zero, "CreateJobObject");
                    string? cwd = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory;
                    Check(CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, true,
                        CreateNoWindow | CreateSuspended | ExtendedStartupInfoPresent, IntPtr.Zero, cwd, ref si,
                        out PROCESS_INFORMATION pi), "CreateProcess " + fileName);
                    try
                    {
                        if (!AssignProcessToJobObject(job, pi.hProcess))
                        {
                            int error = Marshal.GetLastWin32Error();
                            TerminateProcess(pi.hProcess, 1);
                            throw new Win32Exception(error, "AssignProcessToJobObject");
                        }

                        ResumeThread(pi.hThread);
                    }
                    catch
                    {
                        CloseHandle(pi.hProcess);
                        throw;
                    }
                    finally
                    {
                        CloseHandle(pi.hThread);
                    }

                    GC.KeepAlive(desktop);
                    var result = new HiddenProcess(new SafeWaitHandle(pi.hProcess, true), job, pi.dwProcessId,
                        new FileStream(inWrite, FileAccess.Write, 1), new FileStream(outRead, FileAccess.Read, 4096),
                        new FileStream(errRead, FileAccess.Read, 4096));
                    job = IntPtr.Zero;
                    return result;
                }
                finally
                {
                    if (job != IntPtr.Zero)
                    {
                        CloseHandle(job);
                    }

                    if (attrs != IntPtr.Zero)
                    {
                        DeleteProcThreadAttributeList(attrs);
                        Marshal.FreeHGlobal(attrs);
                    }

                    Marshal.FreeHGlobal(handles);
                    Marshal.FreeHGlobal(deskName);
                    inRead.Dispose(); // концы ребёнка: у нас они больше не нужны (иначе не будет конца вывода)
                    outWrite.Dispose();
                    errWrite.Dispose();
                }
            }
        }

        /// <summary>
        /// Читать stdout и stderr построчно в фоновых нитях (как <c>BeginOutputReadLine</c>); в конце потока — <c>null</c>.
        /// </summary>
        public void BeginReadLines(Action<string?> onOutput, Action<string?> onError)
        {
            _readers.Add(StartReader(StandardOutput, onOutput, "stdout"));
            _readers.Add(StartReader(StandardError, onError, "stderr"));
        }

        /// <summary>Ждать выхода не дольше <paramref name="timeoutMs"/>; конец вывода не ждётся (как у <c>Process</c>).</summary>
        public bool WaitForExit(int timeoutMs) => _exited.WaitOne(timeoutMs) || WaitForSingleObject(_process, 0) == 0;

        /// <summary>Ждать выхода и конца вывода (нитей <see cref="BeginReadLines"/>).</summary>
        public void WaitForExit()
        {
            WaitForExit(Timeout.Infinite);
            foreach (Thread t in _readers)
            {
                t.Join();
            }
        }

        /// <summary>Снять процесс; с <paramref name="entireProcessTree"/> — и всех его потомков (объект-задание).</summary>
        public void Kill(bool entireProcessTree = false)
        {
            if (HasExited)
            {
                return;
            }

            if (entireProcessTree)
            {
                TerminateJobObject(_job, 1);
            }
            else
            {
                TerminateProcess(_process, 1);
            }
        }

        /// <summary>Отпустить ручки и трубы. Процесс не снимается (это дело вызывающего).</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _exitWait?.Unregister(null);
            _exitWait = null;
            try
            {
                StandardInput.Dispose();
            }
            catch (IOException)
            {
                // труба уже закрыта ребёнком
            }

            StandardOutput.Dispose();
            StandardError.Dispose();
            CloseHandle(_job);
            _process.Dispose();
        }

        private void OnExited()
        {
            _exited.Set();
            Exited?.Invoke();
        }

        private static Thread StartReader(StreamReader reader, Action<string?> onLine, string name)
        {
            var t = new Thread(() =>
            {
                try
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        onLine(line);
                    }
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }

                onLine(null);
            })
            {
                IsBackground = true,
                Name = "HiddenProcess " + name,
            };
            t.Start();
            return t;
        }

        private static IntPtr EnsureDesktop()
        {
            if (_desktop == IntPtr.Zero)
            {
                const uint GenericAll = 0x10000000;
                IntPtr d = CreateDesktop(DesktopName, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
                Check(d != IntPtr.Zero, "CreateDesktop " + DesktopName);
                _desktop = d;
            }

            return _desktop;
        }

        private static void CreatePipe(out SafeFileHandle read, out SafeFileHandle write, bool childReads)
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), bInheritHandle = true };
            Check(CreatePipe(out read, out write, ref sa, 0), "CreatePipe");
            SafeFileHandle ours = childReads ? write : read;
            Check(SetHandleInformation(ours, HandleFlagInherit, 0), "SetHandleInformation");
        }

        private static void Check(bool ok, string what)
        {
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), what);
            }
        }

        private sealed class ProcessWaitHandle : WaitHandle
        {
            public ProcessWaitHandle(SafeWaitHandle process)
            {
                SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), false);
            }
        }

        private const int StartfUseShowWindow = 0x1;
        private const int StartfForceOffFeedback = 0x80;
        private const int StartfUseStdHandles = 0x100;
        private const uint CreateSuspended = 0x4;
        private const uint ExtendedStartupInfoPresent = 0x80000;
        private const uint CreateNoWindow = 0x08000000;
        private const int ProcThreadAttributeHandleList = 0x20002;
        private const uint HandleFlagInherit = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES sa, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(SafeHandle h, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
            uint flags, IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr sa, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(SafeWaitHandle process, uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeWaitHandle h, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(SafeWaitHandle h, out uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
    }
}
