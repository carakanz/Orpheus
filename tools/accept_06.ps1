# Приёмка 0.6 (docs/08 3.0; docs/07 2.13 п. 1, 5; карточка 0-025): сервер и боты — отдельными процессами по UDP на петле,
# боты с предсказанием и имитатором связи (RTT, потери), на заданной секунде процесс сервера замораживается целиком
# снаружи (NtSuspendProcess — как сборка мусора или антивирус: стоят и такт, и поток LiteNetLib) на заданные мс.
# Раз в 10 с пишется загрузка машины (средняя загрузка процессоров, WMI). Итог — отчёт ботов (--report) и сводка.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\accept_06.ps1 -Out <папка> [-Minutes 30] [-FreezeAtSec 600]
#              [-FreezeMs 2000] [-Bots 2] [-Rtt 100] [-Loss 1] [-Port 20250] [-NoBuild]
#
# Двоичные копируются в <Out>\bin: сборки в рабочей копии во время прогона не мешают.
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [double]$Minutes = 30,
    [int]$FreezeAtSec = 600,
    [int]$FreezeMs = 2000,
    [int]$Bots = 2,
    [int]$Rtt = 100,
    [double]$Loss = 1,
    [int]$Port = 20250,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

if (-not $NoBuild) {
    dotnet build "$root\Orpheus.sln" -c Release --nologo -v minimal | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "сборка не прошла" }
}

$bin = Join-Path $Out 'bin'
New-Item -ItemType Directory -Force "$bin\server", "$bin\bots" | Out-Null
Copy-Item "$root\src\Orpheus.Server\bin\Release\net9.0\*" "$bin\server" -Recurse -Force
Copy-Item "$root\src\Orpheus.Bots\bin\Release\net9.0\*" "$bin\bots" -Recurse -Force

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OrpheusSuspend {
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
}
'@

$inv = [Globalization.CultureInfo]::InvariantCulture
$seconds = [int]($Minutes * 60)
$server = Start-Process -FilePath 'dotnet' -PassThru -NoNewWindow `
    -ArgumentList "`"$bin\server\OrpheusServer.dll`" --create --seed 42 --size 1024 --port $Port --logs `"$Out\server-logs`"" `
    -RedirectStandardOutput "$Out\server-out.txt" -RedirectStandardError "$Out\server-err.txt"
Start-Sleep -Seconds 3
$botsProc = Start-Process -FilePath 'dotnet' -PassThru -NoNewWindow `
    -ArgumentList "`"$bin\bots\Orpheus.Bots.dll`" --connect 127.0.0.1:$Port --count $Bots --script wander --rtt $Rtt --loss $($Loss.ToString($inv)) --seconds $seconds --seed 7 --report `"$Out\bots-report.json`"" `
    -RedirectStandardOutput "$Out\bots-out.txt" -RedirectStandardError "$Out\bots-err.txt"

$load = "$Out\load.csv"
"sec,cpu_percent,server_cpu_s,bots_cpu_s" | Set-Content -Encoding utf8 $load
$watch = [Diagnostics.Stopwatch]::StartNew()
$frozen = $false
$nextLoad = 0
$freezeLog = "$Out\freeze.txt"
while (-not $botsProc.HasExited) {
    $t = $watch.Elapsed.TotalSeconds
    if (-not $frozen -and $t -ge $FreezeAtSec) {
        $frozen = $true
        $h = $server.Handle
        $t0 = $watch.Elapsed.TotalMilliseconds
        [void][OrpheusSuspend]::NtSuspendProcess($h)
        Start-Sleep -Milliseconds $FreezeMs
        [void][OrpheusSuspend]::NtResumeProcess($h)
        $t1 = $watch.Elapsed.TotalMilliseconds
        "заморозка процесса сервера: с {0:F0} мс по {1:F0} мс — {2:F0} мс" -f $t0, $t1, ($t1 - $t0) | Set-Content -Encoding utf8 $freezeLog
    }

    if ($t -ge $nextLoad) {
        $nextLoad += 10
        $cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
        $server.Refresh(); $botsProc.Refresh()
        "{0:F0},{1:F0},{2:F1},{3:F1}" -f $t, $cpu, $server.TotalProcessorTime.TotalSeconds, $botsProc.TotalProcessorTime.TotalSeconds | Add-Content -Encoding utf8 $load
    }

    Start-Sleep -Milliseconds 200
}

Start-Sleep -Seconds 1
if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
$cpuAll = Import-Csv $load | ForEach-Object { [double]$_.cpu_percent }
$stat = $cpuAll | Measure-Object -Average -Maximum
"загрузка машины: средняя {0:F0} %, наибольшая {1:F0} % ({2} замеров)" -f $stat.Average, $stat.Maximum, $stat.Count
if (Test-Path $freezeLog) { Get-Content $freezeLog -Encoding utf8 }
Get-Content "$Out\bots-out.txt" -Encoding utf8 | Select-Object -Last 4
exit $botsProc.ExitCode
