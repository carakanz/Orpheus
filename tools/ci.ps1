# Проверка ядра Orpheus: сборка решения и все тесты (docs/07, 11.2).
# Запуск: tools\ci.cmd (обёртка для cmd) или powershell -NoProfile -File tools/ci.ps1 (или pwsh tools/ci.ps1).
# Выход 0 — зелёный; иначе код ошибки. Лимит времени — 60 с (0 — без лимита).
# Тяжёлые тесты ([Trait("Category", "Big")], имена D0N_*_Big*: миры 4096² и больше) CI пропускает;
# ночной прогон гоняет всё, кроме Soak: tools\ci.cmd -All -TimeLimitSec 0.
# Soak ([Trait("Category", "Soak")]: долгие прогоны в реальном времени, например 4 круга run_seam по UDP, ~12 мин)
# не входят ни в CI, ни в -All; только они — tools\ci.cmd -Soak -TimeLimitSec 0 (перед приёмкой вехи).
# Таймаут шага (C-026): шаг, висящий дольше -StepTimeoutSec (по умолчанию 3600 с), — ребёнок снимается, код 258, шаг красный.
# Тест-гард: tools\ci.ps1 -SelfTest — EOF по stdin, таймаут шага, экранирование Join-CmdLine (секунды, без dotnet).
#
# Кодировка: консоль переводится в UTF-8, поэтому перенаправление (tools\ci.cmd > log.txt 2>&1)
# и -LogFile дают UTF-8 без BOM — кириллица в логе CI читается без подбора кодировки.
param(
    [string]$Configuration = 'Debug',
    [int]$TimeLimitSec = 60,
    [int]$StepTimeoutSec = 3600,
    [string]$LogFile = '',
    [switch]$All,
    [switch]$Soak,
    [switch]$SelfTest,
    # C-033: шлюз чистоты процессов — на время прогона крутится tools\watch_spawns.ps1
    # (скрыто), в конце — проверка: conhost.exe на столе Автора (WinSta0\Default) из процессов
    # этой копии = 0, иначе CI красный.
    [switch]$Hygiene
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOEX { public STARTUPINFO Info; public IntPtr lpAttributeList; }

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

    // C-026: STARTUPINFOEX + список ручек — схема HiddenProcess.cs (C-022): наследуются ровно
    // три трубы; conhost не получает лишних ручек, и EOF по stdin доходит до ребёнка.
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr attrs, int count, int group, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr attrs, int flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr prevSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern void DeleteProcThreadAttributeList(IntPtr attrs);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attrs, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(IntPtr thread);

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
    // timeoutMs > 0 — таймаут шага (C-026): по истечении ребёнок снимается TerminateProcess(258)
    // + пометка в stderr; 0 — без лимита.
    public static int Run(string exe, string args, string cwd, string desktop, string[] extraEnv, int timeoutMs, out string stdout, out string stdErr)
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
            // Схема наследования (C-026): ребёнку достаются inR (как stdin через STARTF_USESTDHANDLES —
            // он обязан быть наследуемым, иначе у ребёнка stdin битый) и записывающие концы outW/errW.
            // Записывающий конец stdin inW ребёнку НЕ отдаём и закрываем у себя сразу после запуска —
            // тогда пишущих копий не остаётся и читающий stdin ребёнок получает EOF (C-025 П з. 1:
            // в старом коде inW наследовался, EOF не наступал никогда, «cmd /c more» висел).
            SetHandleInformation(inW.DangerousGetHandle(), HandleFlagInherit, 0);
            SetHandleInformation(outR.DangerousGetHandle(), HandleFlagInherit, 0);
            SetHandleInformation(errR.DangerousGetHandle(), HandleFlagInherit, 0);

            // Схема (C-026, по образцу HiddenProcess.cs): STARTUPINFOEX + PROC_THREAD_ATTRIBUTE_HANDLE_LIST —
            // ребёнку (и его conhost) наследуются ровно inR/outW/errW. Записывающий конец stdin inW
            // ребёнку не отдаём и закрываем у себя сразу после запуска — пишущих копий не остаётся,
            // читающий stdin ребёнок получает EOF. Без списка ручек (plain STARTUPINFO) conhost
            // таскал лишние ручки, и EOF по stdin к ребёнку не приходил никогда (C-025 П з. 1:
            // зависший шаг вешал ночной прогон).
            STARTUPINFOEX siex = new STARTUPINFOEX();
            siex.Info.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
            siex.Info.lpDesktop = "WinSta0\\" + desktop;
            siex.Info.dwFlags = 0x100 | 0x01; // STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW
            siex.Info.wShowWindow = 0;        // SW_HIDE
            siex.Info.hStdInput = inR.DangerousGetHandle(); siex.Info.hStdOutput = outW.DangerousGetHandle(); siex.Info.hStdError = errW.DangerousGetHandle();

            IntPtr handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handles, 0, inR.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, outW.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size * 2, errW.DangerousGetHandle());
            IntPtr attrSize = IntPtr.Zero; IntPtr attrs = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
            attrs = Marshal.AllocHGlobal(attrSize);
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref attrSize)
                || !UpdateProcThreadAttribute(attrs, 0, (IntPtr)0x00020002, handles, (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
            {
                if (attrs != IntPtr.Zero) { DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs); }
                Marshal.FreeHGlobal(handles);
                inR.Dispose(); inW.Dispose(); outR.Dispose(); outW.Dispose(); errR.Dispose(); errW.Dispose();
                stdErr = "hidden: ProcThreadAttributeList, ошибка " + Marshal.GetLastWin32Error();
                return 1;
            }
            siex.lpAttributeList = attrs;

            PROCESS_INFORMATION pi;
            var cmd = new StringBuilder(exe + " " + args);
            // env-блок передаём свой (ANSI, пиннуемый буфер — как Process.Start): гасим
            // персистентные серверы сборки (MSBUILDUSESERVER и пр. — задаёт вызывающий).
            // Юникодный блок + CREATE_UNICODE_ENVIRONMENT в этой сборке получает отказ 577.
            byte[] env = EnvBlock(extraEnv);
            GCHandle gh = GCHandle.Alloc(env, GCHandleType.Pinned);
            // ребёнок в объекте-задании (CreateSuspended + Assign + Resume): по таймауту снимаем
            // всё дерево (dotnet → MSBuild → testhost), как HiddenProcess.Kill(entireProcessTree).
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            bool ok = CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, true,
                CreateNoWindow | 0x00000004 | 0x00080000, // NO_WINDOW | SUSPENDED | EXTENDED_STARTUPINFO_PRESENT
                gh.AddrOfPinnedObject(), cwd, ref siex, out pi);
            gh.Free();
            DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs); Marshal.FreeHGlobal(handles);
            if (!ok)
            {
                int e = Marshal.GetLastWin32Error();
                if (job != IntPtr.Zero) CloseHandle(job);
                inR.Dispose(); inW.Dispose(); outR.Dispose(); outW.Dispose(); errR.Dispose(); errW.Dispose();
                stdErr = "hidden: CreateProcess " + exe + ", ошибка " + e;
                return 1;
            }
            if (job != IntPtr.Zero) AssignProcessToJobObject(job, pi.hProcess);
            ResumeThread(pi.hThread);
            inW.Dispose(); // stdin ребёнка — EOF
            outW.Dispose(); errW.Dispose(); // свои копии труб вывода закрыть, иначе EOF не наступит никогда

            // Обе трубы выкачиваем параллельно: vstest пишет результаты в stdout, MSBuild — ошибки
            // сборки в stderr; последовательное чтение deadlock'ится на заполнении буфера второй.
            var oSb = new StringBuilder(); var eSb = new StringBuilder();
            var oTask = Task.Run(() => Drain(outR, oSb));
            var eTask = Task.Run(() => Drain(errR, eSb));

            int code;
            // C-025 П з. 1: INFINITE вешал прогон намертво на зависшем ребёнке — таймаут шага.
            uint wr = WaitForSingleObject(pi.hProcess, timeoutMs > 0 ? (uint)timeoutMs : 0xFFFFFFFF);
            if (wr != 0)
            {
                // 0x102 = WAIT_TIMEOUT: ребёнок (или читающий stdin, у которого нет EOF) не должен
                // вешать ночной прогон без надзора — снимаем, шаг красный. Job снимает всё дерево
                // (dotnet → MSBuild → testhost), как HiddenProcess.Kill(entireProcessTree).
                if (job != IntPtr.Zero) TerminateJobObject(job, 258);
                TerminateProcess(pi.hProcess, 258);
                WaitForSingleObject(pi.hProcess, 5000);
                if (wr == 0x102)
                    lock (eSb) eSb.Append("hidden: шаг превысил таймаут " + (timeoutMs / 1000) + " с — ребёнок снят\n");
                code = 258;
            }
            else
            {
                uint c; GetExitCodeProcess(pi.hProcess, out c);
                code = unchecked((int)c);
            }
            // Потомок с наследуемой трубой (осиротевший тестовый сервер) не должен вешать CI:
            // грейс 60 с на договоривание, дальше берём прочитанное.
            // C-025 П з. 2: грейс параллельный (худший случай 60 с на шаг, а не 120).
            if (!Task.WaitAll(new Task[] { oTask, eTask }, 60000))
                eSb.Append("hidden: поток удерживается потомком, хвост вывода не дочитан\n");
            lock (oSb) stdout = oSb.ToString();
            lock (eSb) stdErr = eSb.ToString();

            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            if (job != IntPtr.Zero) CloseHandle(job);
            inR.Dispose();
            return code;
        }
        finally { CloseDesktop(desk); }
    }

    // C-033: фоновый запуск на скрытом столе без труб (наблюдатель шлюза чистоты): вывод —
    // свой файл, останавливается по -StopFile. Возвращает pid ребёнка, -1 — не вышло.
    public static int RunAsync(string exe, string args, string cwd, string desktop)
    {
        const uint GenericAll = 0x10000000;
        const uint CreateNoWindow = 0x08000000;
        IntPtr desk = OpenDesktop(desktop, 0, (int)GenericAll);
        if (desk == IntPtr.Zero) desk = CreateDesktop(desktop, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
        if (desk == IntPtr.Zero) return -1;
        try
        {
            STARTUPINFOEX siex = new STARTUPINFOEX();
            siex.Info.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
            siex.Info.lpDesktop = "WinSta0\\" + desktop;
            siex.Info.dwFlags = 0x01; // STARTF_USESHOWWINDOW
            siex.Info.wShowWindow = 0; // SW_HIDE
            siex.lpAttributeList = IntPtr.Zero; // ручки не наследуются (ни conhost, ни трубы)
            var cmd = new StringBuilder(exe + " " + args);
            PROCESS_INFORMATION pi;
            if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow, IntPtr.Zero, cwd, ref siex, out pi))
                return -1;
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            return pi.dwProcessId;
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

# C-033: разбор TSV наблюдателя (tools\watch_spawns.ps1): time pid ppid name desktop flags path parent.
function Read-Spawns([string]$tsv) {
    if (-not (Test-Path $tsv)) { return @() }
    @(Get-Content $tsv -Encoding UTF8 | Select-Object -Skip 1 | Where-Object { $_ -ne '' } | ForEach-Object {
        $c = $_ -split "`t"
        if ($c.Count -ge 8) { [pscustomobject]@{ Name = $c[3]; Desktop = $c[4]; Path = $c[6]; Parent = $c[7] } }
    })
}

# C-033: «свои» — то же правило, что в watch_spawns.ps1: путь содержит имя копии, ИЛИ conhost,
# у которого родитель из-под копии. Из них — conhost на столе Автора (WinSta0\Default).
function Get-ConhostOnDefault([array]$rows, [string]$filter) {
    @($rows | Where-Object {
        $filter -eq '' -or $_.Path -like "*$filter*" -or ($_.Name -eq 'conhost.exe' -and $_.Parent -like "*$filter*")
    } | Where-Object { $_.Name -eq 'conhost.exe' -and $_.Desktop -like '*\Default' })
}

function Join-CmdLine([string[]]$argv) {
    # CreateProcessW принимает одной строкой; правила разбора аргументов MSVC/CRT (C-025 П з. 6):
    # n слэшей перед кавычкой → 2n+1 слэшей + " (иначе чётная пачка «съедает» кавычку парсером);
    # хвостовые слэши кавычаемого аргумента удваиваются (не «съедают» закрывающую кавычку);
    # аргумент с пробелами/кавычками — в кавычки.
    ($argv | ForEach-Object {
        if ($_ -notmatch '[\s"]') { $_; return }
        $sb = New-Object System.Text.StringBuilder
        $slashes = 0
        foreach ($ch in $_.ToCharArray()) {
            if ($ch -eq '\') { $slashes++; continue }
            if ($ch -eq '"') { [void]$sb.Append('\' * (2 * $slashes + 1) + '"'); $slashes = 0; continue }
            if ($slashes -gt 0) { [void]$sb.Append('\' * $slashes); $slashes = 0 }
            [void]$sb.Append($ch)
        }
        [void]$sb.Append('\' * (2 * $slashes))
        '"' + $sb.ToString() + '"'
    }) -join ' '
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
    try { $code = [HiddenCi]::Run('dotnet', (Join-CmdLine $dotnetArgs), $root, 'OrpheusHidden', $env_, [int]$StepTimeoutSec * 1000, [ref]$stdOut, [ref]$stdErr) }
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

function Invoke-SelfTest {
    # C-026 тест-гард: экранирование Join-CmdLine, EOF по stdin, таймаут шага.
    # Гоняет только HiddenCi на cmd/powershell (без dotnet): ~10 с. Возвращает число падений.
    $script:selfFail = 0
    function Check([bool]$ok, [string]$what) {
        if ($ok) { Write-Host "  [selftest] ок: $what" -ForegroundColor Green }
        else { Write-Host "  [selftest] FAIL: $what" -ForegroundColor Red; $script:selfFail = $script:selfFail + 1 }
    }

    # таблицы Join-CmdLine: аргумент → ожидаемая строка (правила MSVC, см. функцию)
    $cases = @(
        ,@('plain', 'plain')
        ,@('a b', '"a b"')
        ,@('a"b', '"a\"b"')
        ,@('a\"b', '"a\\\"b"')
        ,@('a b"c', '"a b\"c"')
        ,@('dir \', '"dir \\"')
        ,@('a\ b', '"a\ b"')
    )
    foreach ($c in $cases) {
        $got = Join-CmdLine @($c[0])
        Check ($got -ceq $c[1]) ("Join-CmdLine: '" + $c[0] + "' → '" + $got + "'")
    }

    # stdin — закрытая труба: читающий обязан получить EOF и выйти, не висеть.
    # Читатель — powershell ReadToEnd (один процесс, без консоли-зависимостей): stdin битый
    # (не продублировался) → читает CONIN$ → висит → гард красный. (cmd /c more для гарда не годится:
    # современный more.com после EOF открывает CONIN$ и ждёт клавишу — свойство more, не канализации.)
    $stdOut = ''; $stdErr = ''
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $code = [HiddenCi]::Run('powershell', '-NoProfile -Command "[Console]::In.ReadToEnd() | Out-Null"', $root, 'OrpheusHidden', @(), 60000, [ref]$stdOut, [ref]$stdErr)
    $sec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    Check ($code -eq 0 -and $sec -lt 30) "читающий stdin (powershell ReadToEnd) вышел по EOF: код $code, $sec с"

    # зависший ребёнок (одним процессом, без сирот) — снимается таймаутом шага
    $stdOut = ''; $stdErr = ''
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $code = [HiddenCi]::Run('powershell', '-NoProfile -Command Start-Sleep 59', $root, 'OrpheusHidden', @(), 5000, [ref]$stdOut, [ref]$stdErr)
    $sec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    Check ($code -eq 258 -and $sec -ge 4 -and $sec -le 20 -and $stdErr -match 'таймаут') "зависший ребёнок снят таймаутом: код $code, $sec с"

    # C-033: гард детектора шлюза чистоты — наблюдатель обязан ловить намеренно запущенный
    # conhost на столе Автора. Наблюдатель (без -Filter, считаем всё) стартует в фоне; параллельно
    # запускаем powershell -WindowStyle Hidden: новая консоль → conhost.exe на WinSta0\Default.
    $hygTsv = Join-Path $root 'build\logs\spawns-selftest.tsv'
    New-Item -ItemType Directory -Force (Join-Path $root 'build\logs') | Out-Null
    Remove-Item -Force $hygTsv -ErrorAction SilentlyContinue
    $wa = Join-CmdLine @('powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'watch_spawns.ps1'),
        '-DurationSec', '8', '-IntervalMs', '200', '-OutFile', $hygTsv)
    $watchPid = [HiddenCi]::RunAsync('powershell', $wa, $root, 'OrpheusHidden')
    Check ($watchPid -ge 0) "hygiene: наблюдатель запущен (pid $watchPid)"
    Start-Sleep -Milliseconds 700
    # проба живёт ~6 с: наблюдатель успевает прочитать desktop по PEB (у мгновенно умерших
    # процессов OpenProcess не даёт читать — строка приходит с «?»)
    $probe = Start-Process -FilePath 'powershell' -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep 6') -WindowStyle Hidden -PassThru
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watchPid -ge 0 -and (Get-Process -Id $watchPid -ErrorAction SilentlyContinue) -and $sw.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 500 }
    try { $probe.Kill() } catch { }
    $caught = @(Get-ConhostOnDefault (Read-Spawns $hygTsv) '')
    Check ($caught.Count -ge 1) "hygiene: проба conhost на WinSta0\Default поймана: $($caught.Count)"

    return $script:selfFail
}
if ($SelfTest) { exit (Invoke-SelfTest) }

# C-033: запуск наблюдателя шлюза чистоты (параметр -Hygiene): скрытый стол, TSV в build\logs,
# остановка по файлу-флагу. Фильтр «своих» — имя рабочей копии (в watch_spawns: подстрока пути).
$hygStop = ''; $hygTsv = ''; $hygPid = -1; $hygFilter = (Split-Path -Leaf $root)
if ($Hygiene) {
    $logDirH = Join-Path $root 'build\logs'
    New-Item -ItemType Directory -Force $logDirH | Out-Null
    $hygTsv = Join-Path $logDirH 'spawns.tsv'
    $hygStop = Join-Path $logDirH 'spawns.stop'
    Remove-Item -Force $hygTsv, $hygStop -ErrorAction SilentlyContinue
    $wa = Join-CmdLine @('powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'watch_spawns.ps1'),
        '-DurationSec', '7200', '-IntervalMs', '200', '-Filter', $hygFilter, '-OutFile', $hygTsv, '-StopFile', $hygStop)
    $hygPid = [HiddenCi]::RunAsync('powershell', $wa, $root, 'OrpheusHidden')
    if ($hygPid -lt 0) { Write-Line 'hygiene: наблюдатель не запущен — шлюз будет красный' 'Red' }
    else {
        # Наблюдатель обязан остановиться и при падении шага (exit внутри Invoke-Step):
        # файл-флаг пишем на выходе из движка PowerShell.
        $global:HygStopPath = $hygStop
        $null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action {
            Set-Content -Path $global:HygStopPath -Value 'exit' -Encoding ASCII -ErrorAction SilentlyContinue
        }
    }
    Write-Line "hygiene: наблюдатель запущен (pid $hygPid, фильтр '$hygFilter')" 'Gray'
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

# C-033: остановка наблюдателя и вердикт: conhost.exe на столе Автора (WinSta0\Default)
# среди процессов этой копии = 0. Порог не ослаблять: замеры 14–16.11 (C-022) чистые.
if ($Hygiene) {
    if ($hygStop -ne '') { Set-Content -Path $hygStop -Value 'exit' -Encoding ASCII -ErrorAction SilentlyContinue }
    $swH = [System.Diagnostics.Stopwatch]::StartNew()
    while ($hygPid -ge 0 -and (Get-Process -Id $hygPid -ErrorAction SilentlyContinue) -and $swH.Elapsed.TotalSeconds -lt 15) { Start-Sleep -Milliseconds 300 }
    $rowsH = Read-Spawns $hygTsv
    $ownH = @($rowsH | Where-Object { $_.Path -like "*$hygFilter*" -or ($_.Name -eq 'conhost.exe' -and $_.Parent -like "*$hygFilter*") })
    $badH = Get-ConhostOnDefault $rowsH $hygFilter
    Write-Line ("hygiene: новых процессов {0}, своих {1}, conhost на WinSta0\Default: {2}" -f $rowsH.Count, $ownH.Count, $badH.Count) $(if ($badH.Count -gt 0) { 'Red' } else { 'Green' })
    $badH | Select-Object -First 10 | ForEach-Object { Write-Line "   $($_.Name) desktop $($_.Desktop) родитель $($_.Parent)" 'Red' }
    if ($badH.Count -gt 0) { Write-Line 'hygiene: на столе Автора появились дочерние процессы прогона — шлюз красный' 'Red'; exit 4 }
}

$sec = [math]::Round($total.Elapsed.TotalSeconds, 1)
if ($TimeLimitSec -gt 0 -and $total.Elapsed.TotalSeconds -gt $TimeLimitSec) {
    Write-Line "CI: всё прошло, но за $sec с — больше лимита $TimeLimitSec с" 'Red'
    exit 3
}
Write-Line "CI: зелёный за $sec с" 'Green'
exit 0
