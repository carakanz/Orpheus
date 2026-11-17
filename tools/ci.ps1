# Проверка ядра Orpheus: сборка решения и все тесты (docs/07, 11.2).
# Запуск: tools\ci.cmd (обёртка для cmd) или powershell -NoProfile -File tools/ci.ps1 (или pwsh tools/ci.ps1).
# Выход 0 — зелёный; иначе код ошибки. Лимит времени — 60 с (0 — без лимита).
# Тяжёлые тесты ([Trait("Category", "Big")], имена D0N_*_Big*: миры 4096² и больше) CI пропускает;
# ночной прогон гоняет всё, кроме Soak: tools\ci.cmd -All -TimeLimitSec 0.
# Soak ([Trait("Category", "Soak")]: долгие прогоны в реальном времени, например 4 круга run_seam по UDP, ~12 мин)
# не входят ни в CI, ни в -All; только они — tools\ci.cmd -Soak -TimeLimitSec 0 (перед приёмкой вехи).
#
# Кодировка: консоль переводится в UTF-8, поэтому перенаправление (tools\ci.cmd > log.txt 2>&1)
# и -LogFile дают UTF-8 без BOM — кириллица в логе CI читается без подбора кодировки.
param(
    [string]$Configuration = 'Debug',
    [int]$TimeLimitSec = 60,
    [string]$LogFile = '',
    [switch]$All,
    [switch]$Soak
)

$ErrorActionPreference = 'Stop'

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'Orpheus.sln'
$total = [System.Diagnostics.Stopwatch]::StartNew()

# C-025: dotnet (build/test) идёт на скрытом рабочем столе WinSta0\OrpheusHidden,
# stdout/stderr — наследуемые трубы (UTF-8). Прямой `& dotnet` оставляет на столе
# Автора conhost.exe от testhost (см. замер C-022); его дети (серверы тестов) — тем более.
# Образец перенаправления: src/Orpheus.Platform.Pc/HiddenProcess.cs.
Add-Type @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

public static class HiddenCi
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

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenDesktop(string name, int flags, int access);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool CloseDesktop(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES sa, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetHandleInformation(IntPtr h, int mask, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr h, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    // Свой env-блок в ANSI (Encoding.Default — как Process.Start в .NET Framework):
    // копия окружения + доп. переменные. Юникодный блок с CREATE_UNICODE_ENVIRONMENT
    // здесь (Add-Type PS 5.1) почему-то получает отказ 577; ANSI принят, значения доходят.
    static byte[] EnvBlock(string[] extra)
    {
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            map[(string)e.Key] = e.Value == null ? "" : (string)e.Value;
        if (extra != null)
            foreach (var kv in extra)
            {
                int i = kv.IndexOf('=');
                if (i > 0) map[kv.Substring(0, i)] = kv.Substring(i + 1);
            }
        var sb = new StringBuilder();
        foreach (var kv in map) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return Encoding.Default.GetBytes(sb.ToString());
    }

    // Запуск exe на скрытом столе; stdout/stderr — трубы UTF-8. Код возврата процесса.
    public static int Run(string exe, string args, string cwd, string desktop, string[] extraEnv, out string stdout, out string stdErr)
    {
        stdout = ""; stdErr = "";
        const uint GenericAll = 0x10000000;
        const uint CreateNoWindow = 0x08000000;
        const int HandleFlagInherit = 2;

        IntPtr desk = OpenDesktop(desktop, 0, (int)GenericAll);
        if (desk == IntPtr.Zero) desk = CreateDesktop(desktop, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
        if (desk == IntPtr.Zero)
        {
            stdErr = "hidden: нет стола " + desktop + ", ошибка " + Marshal.GetLastWin32Error();
            return 1;
        }
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), bInheritHandle = true };
            SafeFileHandle inR, inW, outR, outW, errR, errW;
            if (!CreatePipe(out inR, out inW, ref sa, 0) || !CreatePipe(out outR, out outW, ref sa, 0)
                || !CreatePipe(out errR, out errW, ref sa, 0))
            {
                stdErr = "hidden: CreatePipe, ошибка " + Marshal.GetLastWin32Error();
                return 1;
            }
            SetHandleInformation(inR.DangerousGetHandle(), HandleFlagInherit, 0);   // ребёнку — только inW/outW/errW
            SetHandleInformation(outR.DangerousGetHandle(), HandleFlagInherit, 0);
            SetHandleInformation(errR.DangerousGetHandle(), HandleFlagInherit, 0);

            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.lpDesktop = "WinSta0\\" + desktop;
            si.dwFlags = 0x100; // STARTF_USESTDHANDLES
            si.hStdInput = inR.DangerousGetHandle(); si.hStdOutput = outW.DangerousGetHandle(); si.hStdError = errW.DangerousGetHandle();

            PROCESS_INFORMATION pi;
            var cmd = new StringBuilder(exe + " " + args);
            // env-блок передаём свой (ANSI, пиннуемый буфер — как Process.Start): гасим
            // персистентные серверы сборки (MSBUILDUSESERVER и пр. — задаёт вызывающий).
            // Юникодный блок + CREATE_UNICODE_ENVIRONMENT в этой сборке получает отказ 577.
            byte[] env = EnvBlock(extraEnv);
            GCHandle gh = GCHandle.Alloc(env, GCHandleType.Pinned);
            bool ok = CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, true, CreateNoWindow, gh.AddrOfPinnedObject(), cwd, ref si, out pi);
            gh.Free();
            if (!ok)
            {
                int e = Marshal.GetLastWin32Error();
                inR.Dispose(); inW.Dispose(); outR.Dispose(); outW.Dispose(); errR.Dispose(); errW.Dispose();
                stdErr = "hidden: CreateProcess " + exe + ", ошибка " + e;
                return 1;
            }
            inW.Dispose(); // stdin ребёнка — EOF
            outW.Dispose(); errW.Dispose(); // свои копии труб вывода закрыть, иначе EOF не наступит никогда

            // Обе трубы выкачиваем параллельно: vstest пишет результаты в stdout, MSBuild — ошибки
            // сборки в stderr; последовательное чтение deadlock'ится на заполнении буфера второй.
            var oSb = new StringBuilder(); var eSb = new StringBuilder();
            var oTask = Task.Run(() => Drain(outR, oSb));
            var eTask = Task.Run(() => Drain(errR, eSb));

            int code;
            if (WaitForSingleObject(pi.hProcess, 0xFFFFFFFF) != 0)
            {
                TerminateProcess(pi.hProcess, 258);
                WaitForSingleObject(pi.hProcess, 5000);
                code = 258;
            }
            else
            {
                uint c; GetExitCodeProcess(pi.hProcess, out c);
                code = unchecked((int)c);
            }
            // Потомок с наследуемой трубой (осиротевший тестовый сервер) не должен вешать CI:
            // грейс 60 с на договоривание, дальше берём прочитанное.
            if (!oTask.Wait(60000) || !eTask.Wait(60000))
                eSb.Append("hidden: поток удерживается потомком, хвост вывода не дочитан\n");
            lock (oSb) stdout = oSb.ToString();
            lock (eSb) stdErr = eSb.ToString();

            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            inR.Dispose();
            return code;
        }
        finally { CloseDesktop(desk); }
    }

    static void Drain(SafeFileHandle h, StringBuilder into)
    {
        try
        {
            using (var fs = new FileStream(h, FileAccess.Read))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                var buf = new char[4096];
                int n;
                while ((n = sr.Read(buf, 0, buf.Length)) > 0)
                    lock (into) into.Append(buf, 0, n);
            }
        }
        catch { }
    }
}
"@

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$logPath = ''
if ($LogFile -ne '') {
    $logPath = if ([System.IO.Path]::IsPathRooted($LogFile)) { $LogFile } else { Join-Path $root $LogFile }
    # в свежей рабочей копии build\logs ещё нет (папка вне git) — создаём сами
    New-Item -ItemType Directory -Force (Split-Path -Parent $logPath) | Out-Null
    [System.IO.File]::WriteAllText($logPath, '', $utf8NoBom)
}

function Write-Line([string]$text, [string]$color = 'Gray') {
    Write-Host $text -ForegroundColor $color
    if ($logPath -ne '') {
        [System.IO.File]::AppendAllText($logPath, $text + "`n", $utf8NoBom)
    }
}

function Join-CmdLine([string[]]$argv) {
    # CreateProcessW принимает одной строкой; аргументы с пробелами/кавычками — в кавычки.
    ($argv | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
}

function Invoke-Step([string]$name, [string]$color, [string[]]$dotnetArgs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Line "== $name" $color
    # dotnet пишет падения тестов в stderr; с ErrorActionPreference=Stop PowerShell
    # оборвал бы CI на первой строке FAIL и лог не добрался бы до сводки.
    # C-025: запуск на скрытом столе (HiddenCi), вывод — трубы; `& dotnet` рождал
    # conhost от testhost на столе Автора.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    # Персистентные серверы сборки (MSBuild node reuse / MSBuild Server): узлы, рождённые
    # на столе вызывающей консоли, живут после сборки и при следующем билде спавнят сирот
    # (conhost) на столе Автора. Гасим их в env-блоке ребёнка.
    $env_ = @('DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1', 'MSBUILDUSESERVER=0')
    $code = 1; $stdOut = ''; $stdErr = ''
    try { $code = [HiddenCi]::Run('dotnet', (Join-CmdLine $dotnetArgs), $root, 'OrpheusHidden', $env_, [ref]$stdOut, [ref]$stdErr) }
    catch { $code = 1; $stdErr = "hidden: " + $_.Exception.Message }
    $ErrorActionPreference = $prev
    foreach ($line in ($stdOut -split "`r?`n")) { Write-Line "   $line" }
    foreach ($line in ($stdErr -split "`r?`n")) { Write-Line "   $line" }
    $sec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    if ($code -ne 0) {
        Write-Line "== $name — ОШИБКА (код $code), $sec с" 'Red'
        exit $code
    }
    Write-Line "== $name — ок, $sec с" 'Green'
}

Invoke-Step 'build' 'Cyan' @('build', $sln, '-c', $Configuration, '--nologo', '-v', 'minimal')
$testArgs = @('test', $sln, '-c', $Configuration, '--no-build', '--nologo', '-v', 'minimal')
if ($Soak) {
    $testArgs += @('--filter', 'Category=Soak')
    $title = 'test (только Category=Soak)'
} elseif ($All) {
    $testArgs += @('--filter', 'Category!=Soak')
    $title = 'test (все, с Big, без Soak)'
} else {
    $testArgs += @('--filter', 'Category!=Big&Category!=Soak')
    $title = 'test (без Category=Big и Soak)'
}
Invoke-Step $title 'Cyan' $testArgs

$sec = [math]::Round($total.Elapsed.TotalSeconds, 1)
if ($TimeLimitSec -gt 0 -and $total.Elapsed.TotalSeconds -gt $TimeLimitSec) {
    Write-Line "CI: всё прошло, но за $sec с — больше лимита $TimeLimitSec с" 'Red'
    exit 3
}
Write-Line "CI: зелёный за $sec с" 'Green'
exit 0
