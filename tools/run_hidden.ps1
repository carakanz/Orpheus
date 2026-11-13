# Runs a program on a separate invisible Windows desktop, so its windows can never
# take keyboard focus or appear on the user's monitors. Used for game runs that need
# a graphics device (screenshots): -batchmode alone still creates a window that grabs focus.
# Usage: tools\run_hidden.ps1 -Exe <path> -Arguments '<command line>' [-TimeoutSec 120]
# Exit code: the program's exit code; 258 on timeout (process killed); 1 on launch failure.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Arguments = '',
    [int]$TimeoutSec = 120,
    [string]$Desktop = 'OrpheusHidden'
)

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class HiddenDesktopRunner
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool CloseDesktop(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string app, System.Text.StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr h, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    public static int Run(string exe, string args, string cwd, string desktop, int timeoutMs)
    {
        const uint GENERIC_ALL = 0x10000000;
        const uint CREATE_NO_WINDOW = 0x08000000;
        IntPtr desk = CreateDesktop(desktop, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
        if (desk == IntPtr.Zero)
        {
            Console.Error.WriteLine("run_hidden: CreateDesktop failed, error " + Marshal.GetLastWin32Error());
            return 1;
        }

        try
        {
            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.lpDesktop = "WinSta0\\" + desktop;
            PROCESS_INFORMATION pi;
            var cmd = new System.Text.StringBuilder("\"" + exe + "\" " + args);
            if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_NO_WINDOW, IntPtr.Zero, cwd, ref si, out pi))
            {
                Console.Error.WriteLine("run_hidden: CreateProcess failed, error " + Marshal.GetLastWin32Error());
                return 1;
            }

            try
            {
                if (WaitForSingleObject(pi.hProcess, (uint)timeoutMs) != 0)
                {
                    TerminateProcess(pi.hProcess, 258);
                    WaitForSingleObject(pi.hProcess, 5000);
                    return 258;
                }

                uint code;
                GetExitCodeProcess(pi.hProcess, out code);
                return unchecked((int)code);
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            CloseDesktop(desk);
        }
    }
}
"@

$cwd = Split-Path -Parent (Resolve-Path $Exe)
$code = [HiddenDesktopRunner]::Run((Resolve-Path $Exe).Path, $Arguments, $cwd, $Desktop, $TimeoutSec * 1000)
exit $code
