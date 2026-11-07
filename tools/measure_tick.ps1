# Замеры сервера (карточка 0-030; docs/08 3.0 приёмки 0.8 и 0.9, 4.3 «Такт сервера на 8» — на каждой вехе).
# Сервер и боты — отдельными процессами, сборка Release (копией двоичных в папку замера), сервер слушает только петлю
# (без --public), свой свободный порт из строки «orpheus-ready port=N», свои процессы снимаются по объекту процесса (PID).
#
#   -Mode tick  (0.9): сервер --size 4096 --port 0, мир в папке (автосохранение — как в игре), N ботов wander по UDP с
#               имитатором RTT/потерь (Orpheus.Bots), M минут; сервер сам выходит по --ticks после ботов и пишет
#               отчёт --report (метрики такта, части, память, GC, трафик). Затем тот же сценарий в режиме симуляции
#               (--sim --bots N:wander, те же такты подряд, без сети) — для сравнения.
#     powershell -NoProfile -ExecutionPolicy Bypass -File tools\measure_tick.ps1 -Out <папка> [-Size 4096] [-Bots 8]
#                [-Minutes 10] [-Rtt 100] [-Loss 1] [-NoSim] [-NoBuild]
#   -Mode start (0.8): время от запуска процесса OrpheusServer.exe до строки готовности и память (рабочий набор сразу и
#               через 3 с, пик) — 1024² и 4096², новый мир (--create) и загрузка того же мира; по -Repeat раз.
#     ... -Mode start -Out <папка> [-Repeat 3]
#
# Загрузка машины (рядом работают другие агенты, бывает игра или Unity): раз в 5 с средняя загрузка процессоров (WMI) —
# в load.csv; в начале и в конце — процессы с наибольшим процессорным временем за замер (top.txt). Замер при загрузке
# машины ≥ 50 % без своих процессов — не эталонный, так и пишется в итог.
param(
    [ValidateSet('tick', 'start')][string]$Mode = 'tick',
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Size = 4096,
    [int]$Bots = 8,
    [double]$Minutes = 10,
    [int]$Rtt = 100,
    [double]$Loss = 1,
    [uint64]$Seed = 42,
    [int]$Repeat = 3,
    [switch]$NoSim,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$inv = [Globalization.CultureInfo]::InvariantCulture
$root = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

if (-not $NoBuild) {
    dotnet build "$root\Orpheus.sln" -c Release --nologo -v minimal | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "сборка не прошла"; exit 2 }
}

$bin = Join-Path $Out 'bin'
New-Item -ItemType Directory -Force "$bin\server", "$bin\bots" | Out-Null
Copy-Item "$root\src\Orpheus.Server\bin\Release\net9.0\*" "$bin\server" -Recurse -Force
Copy-Item "$root\src\Orpheus.Bots\bin\Release\net9.0\*" "$bin\bots" -Recurse -Force
$serverExe = "$bin\server\OrpheusServer.exe"
$botsExe = "$bin\bots\Orpheus.Bots.exe"

function Stop-Own([Diagnostics.Process]$p, [string]$name) {
    if ($null -eq $p) { return }
    try {
        if (-not $p.HasExited) {
            $p.Kill()
            [void]$p.WaitForExit(5000)
            Write-Host "снят свой процесс: $name (PID $($p.Id))"
        }
    } catch {
        Write-Warning "не удалось снять $name (PID $($p.Id)): $_"
    }
}

function Start-Own([string]$name, [string]$file, [string]$argLine) {
    $p = Start-Process -FilePath $file -ArgumentList $argLine -PassThru -NoNewWindow `
        -RedirectStandardOutput "$Out\$name-out.txt" -RedirectStandardError "$Out\$name-err.txt"
    $null = $p.Handle # без дескриптора, взятого сразу, ExitCode после выхода пустой (Start-Process -PassThru)
    Write-Host "запущен $name (PID $($p.Id))"
    return $p
}

function Wait-Ready([Diagnostics.Process]$p, [string]$name, [Diagnostics.Stopwatch]$since) {
    while ($true) {
        if ($p.HasExited) { throw "$name вышел до готовности, код $($p.ExitCode); см. $Out\$name-err.txt" }
        if ($since.Elapsed.TotalSeconds -gt 180) { throw "$name не готов за 180 с" }
        $line = Select-String -Path "$Out\$name-out.txt" -Pattern 'orpheus-ready port=(\d+)' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($line) { return [int]$line.Matches[0].Groups[1].Value }
        Start-Sleep -Milliseconds 20
    }
}

function Get-MachineCpu {
    return (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
}

function Get-CpuTimes {
    $h = @{}
    foreach ($p in Get-Process) {
        try { $h["$($p.ProcessName)#$($p.Id)"] = $p.TotalProcessorTime.TotalSeconds } catch { }
    }
    return $h
}

# Процессы с наибольшим процессорным временем между двумя снимками — в % всей машины (все логические ядра).
function Write-Top($before, $after, [double]$seconds, [string]$file, [string]$title) {
    $cores = [Environment]::ProcessorCount
    $rows = foreach ($k in $after.Keys) {
        $b = if ($before.ContainsKey($k)) { $before[$k] } else { 0 }
        [pscustomobject]@{ Process = $k; MachinePercent = 100 * ($after[$k] - $b) / ($seconds * $cores) }
    }
    $top = $rows | Sort-Object MachinePercent -Descending | Select-Object -First 15
    Add-Content -Encoding utf8 $file "== $title ($([math]::Round($seconds)) с, $cores логич. ядер; % всей машины)"
    foreach ($r in $top) { Add-Content -Encoding utf8 $file ([string]::Format($inv, "{0,-40} {1,6:F1}", $r.Process, $r.MachinePercent)) }
    return $top
}

$machine = "{0}, {1} логич. ядер, {2:F0} ГБ" -f (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim(), [Environment]::ProcessorCount, ((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
Write-Host "машина: $machine"
$machine | Set-Content -Encoding utf8 "$Out\machine.txt"

if ($Mode -eq 'start') {
    $csv = "$Out\start.csv"
    "size,kind,run,ready_sec_outside,ready_sec_inside,ws_ready_mb,ws_3s_mb,peak_ws_mb,private_3s_mb,cpu_machine" | Set-Content -Encoding utf8 $csv
    foreach ($sz in @(1024, 4096)) {
        $world = "$Out\world-$sz"
        if (Test-Path $world) { Remove-Item -Recurse -Force $world }
        for ($run = 1; $run -le $Repeat; $run++) {
            foreach ($kind in @('create', 'load')) {
                if ($kind -eq 'create') {
                    if (Test-Path $world) { Remove-Item -Recurse -Force $world }
                    $sargs = "--world `"$world`" --create --seed $Seed --size $sz --port 0 --metrics 0"
                } else {
                    $sargs = "--world `"$world`" --port 0 --metrics 0"
                }

                $name = "start-$sz-$kind-$run"
                $cpu = Get-MachineCpu
                $w = [Diagnostics.Stopwatch]::StartNew()
                $p = Start-Own $name $serverExe $sargs
                try {
                    [void](Wait-Ready $p $name $w)
                    $outside = $w.Elapsed.TotalSeconds
                    $p.Refresh(); $wsReady = $p.WorkingSet64 / 1MB
                    Start-Sleep -Seconds 3
                    $p.Refresh(); $ws3 = $p.WorkingSet64 / 1MB; $peak = $p.PeakWorkingSet64 / 1MB; $priv = $p.PrivateMemorySize64 / 1MB
                    $inside = 0
                    $l = Select-String -Path "$Out\$name-out.txt" -Pattern '"sinceProcessStartSec":([0-9.]+)' | Select-Object -First 1
                    if ($l) { $inside = [double]::Parse($l.Matches[0].Groups[1].Value, $inv) }
                    $row = [string]::Format($inv, "{0},{1},{2},{3:F3},{4:F3},{5:F1},{6:F1},{7:F1},{8:F1},{9:F0}", $sz, $kind, $run, $outside, $inside, $wsReady, $ws3, $peak, $priv, $cpu)
                    Add-Content -Encoding utf8 $csv $row
                    Write-Host "  $row"
                } finally {
                    Stop-Own $p $name # выделенный сервер без команды выхода; замок мира отпустит ОС
                }
                Start-Sleep -Milliseconds 500
            }
        }
    }

    Write-Host "итог: $csv"
    exit 0
}

# ---------- -Mode tick ----------
$ticks = [long](($Minutes * 60 + 30) * 30) # боты идут M минут после входа; сервер — с запасом на старт и выход
$world = "$Out\world"
if (Test-Path $world) { Remove-Item -Recurse -Force $world }
$load = "$Out\load.csv"
"sec,cpu_percent" | Set-Content -Encoding utf8 $load
$top = "$Out\top.txt"
Set-Content -Encoding utf8 $top ''
$server = $null
$botsProc = $null
$exitCode = 1
$watch = [Diagnostics.Stopwatch]::StartNew()
$cpu0 = Get-CpuTimes
try {
    $server = Start-Own 'server' $serverExe "--world `"$world`" --create --seed $Seed --size $Size --port 0 --max-players $($Bots + 2) --ticks $ticks --metrics 10 --report `"$Out\server-report.json`""
    $port = Wait-Ready $server 'server' $watch
    Write-Host ("сервер готов: порт {0}, {1:F1} с" -f $port, $watch.Elapsed.TotalSeconds)
    $botsProc = Start-Own 'bots' $botsExe ("--connect 127.0.0.1:$port --count $Bots --script wander --rtt $Rtt --loss $($Loss.ToString($inv)) --seed 7 --minutes $($Minutes.ToString($inv)) --report `"$Out\bots-report.json`"")

    $nextLoad = 0
    while (-not $server.HasExited) {
        $t = $watch.Elapsed.TotalSeconds
        if ($t -gt $Minutes * 60 + 180) { Write-Warning "сервер не вышел сам за $($Minutes * 60 + 180) с"; break }
        if ($t -ge $nextLoad) {
            $nextLoad += 5
            [string]::Format($inv, "{0:F0},{1:F0}", $t, (Get-MachineCpu)) | Add-Content -Encoding utf8 $load
        }
        Start-Sleep -Milliseconds 500
    }

    if ($botsProc -ne $null -and -not $botsProc.WaitForExit(30000)) { Write-Warning "боты не вышли" }
    $server.WaitForExit()
    $exitCode = if ($server.ExitCode -eq 0 -and $botsProc.HasExited -and $botsProc.ExitCode -eq 0) { 0 } else { 1 }
    Write-Host "сервер: код $($server.ExitCode); боты: код $($botsProc.ExitCode)"
} catch {
    Write-Warning "$_"
    $exitCode = 2
} finally {
    Stop-Own $botsProc 'bots'
    Stop-Own $server 'server'
}

$cpu1 = Get-CpuTimes
[void](Write-Top $cpu0 $cpu1 $watch.Elapsed.TotalSeconds $top 'прогон UDP')

$cpuAll = @(Import-Csv $load | ForEach-Object { [double]$_.cpu_percent })
if ($cpuAll.Count -gt 0) {
    $stat = $cpuAll | Measure-Object -Average -Maximum
    $loadText = "загрузка машины: средняя {0:F0} %, наибольшая {1:F0} % ({2} замеров по 5 с)" -f $stat.Average, $stat.Maximum, $stat.Count
    Write-Host $loadText
    $loadText | Set-Content -Encoding utf8 "$Out\load.txt"
}

if (-not $NoSim) {
    $simTicks = [long]($Minutes * 60 * 30)
    $w2 = [Diagnostics.Stopwatch]::StartNew()
    $c0 = Get-CpuTimes
    $sim = Start-Own 'sim' $serverExe "--create --seed $Seed --size $Size --sim --bots ${Bots}:wander --ticks $simTicks --metrics 0 --report `"$Out\sim-report.json`""
    $sim.WaitForExit()
    [void](Write-Top $c0 (Get-CpuTimes) $w2.Elapsed.TotalSeconds $top 'прогон --sim')
    Write-Host ("симуляция: код {0}, {1:F1} с" -f $sim.ExitCode, $w2.Elapsed.TotalSeconds)
}

foreach ($f in @('server-report.json', 'sim-report.json')) {
    if (-not (Test-Path "$Out\$f")) { continue }
    $r = Get-Content -Raw -Encoding utf8 "$Out\$f" | ConvertFrom-Json
    $t = $r.Total
    Write-Host ([string]::Format($inv, "{0}: {1} тактов за {2:F0} с; такт ср. {3:F3} мс, p50 {4:F3}, p99 {5:F3}, p99.9 {6:F3}, max {7:F2}; простоев {8} ({9:F2} %), промежуток max {10:F1} мс",
        $f, $t.Ticks, $t.Seconds, $t.TickMeanMs, $t.TickP50Ms, $t.TickP99Ms, $t.TickP999Ms, $t.TickMaxMs, $t.Stalls, $t.StallPercent, $t.MaxGapMs))
    Write-Host ("  части, мс ср./max (%): " + (($t.Parts | ForEach-Object { [string]::Format($inv, "{0} {1:F3}/{2:F2} ({3:F0} %)", $_.Name, $_.MeanMs, $_.MaxMs, $_.Percent) }) -join '; '))
    Write-Host ([string]::Format($inv, "  память: рабочий набор до {0:F0} МБ, пик {1:F0} МБ, частная {2:F0} МБ, куча {3:F0} МБ; GC 0/1/2: {4}/{5}/{6}, пауза max {7:F2} мс, сумма {8:F1} мс; в такте {9:F1} байт/такт",
        $t.WorkingSetMb, $t.PeakWorkingSetMb, $t.PrivateMb, $t.HeapMb, $t.Gc0, $t.Gc1, $t.Gc2, $t.GcPauseMaxMs, $t.GcPauseTotalMs, $t.AllocatedBytesPerTick))
    if ($t.Clients -gt 0) {
        Write-Host ([string]::Format($inv, "  трафик на клиента: из сервера {0:F2} КБ/с (max {1:F2}), в сервер {2:F2} КБ/с (max {3:F2}); клиентов в среднем {4:F1}; старт {5:F2} с",
            $t.KbpsOutPerClient, $t.KbpsOutMaxClient, $t.KbpsInPerClient, $t.KbpsInMaxClient, $t.Clients, $r.ReadySeconds))
    }
}

Write-Host ("замер tick: код {0}, {1:F0} с" -f $exitCode, $watch.Elapsed.TotalSeconds)
exit $exitCode
