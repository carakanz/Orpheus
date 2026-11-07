# Прогон по UDP на петле (карточка 0-027; backlog [инструменты] net_smoke, 0-017): сервер и клиенты — отдельными
# процессами, свой свободный порт (сервер слушает --port 0 и печатает «orpheus-ready port=N» — ждём строку, а не паузу),
# коды выхода всех процессов, свои процессы снимаются по PID. Сервер слушает только петлю (без --public).
#
#   Приёмка 0.2 (docs/08 3.0): сервер --debug-run-lanes 1024², наблюдатель seam_watch и бегущий run_seam (Orpheus.Bots)
#   с имитатором RTT/σ/потери, отчёты --report у обоих:
#     powershell -NoProfile -ExecutionPolicy Bypass -File tools\run_seam.ps1 -Out <папка> [-Rtt 100] [-Jitter 10] [-Loss 1]
#                [-Seed 42] [-TimeoutSec 900] [-RunnerSeconds S] [-NoBuild]
#   net-smoke клиента Unity (0-017; собранный Orpheus.exe, tools\build_client.ps1):
#     ... -Mode unity-center -Client build\client-win\Orpheus.exe [-SmokeSec 20]   (сервер --bots 2:wander, середина карты)
#     ... -Mode unity-seam   -Client build\client-win\Orpheus.exe                  (сервер --spawn-seam --bots 2:shuttle, клиент --net-smoke-seam)
#     -Rtt 0 -Jitter 0 -Loss 0 — без имитатора.
#
# Код выхода: 0 — все клиенты вышли с кодом 0, сервер жил до конца; 1 — иначе (упал, вышел раньше, не дождались); 2 — ошибка запуска.
# Раз в 10 с пишется загрузка машины (средняя загрузка процессоров, WMI) — в load.csv и в итог.
param(
    [ValidateSet('run_seam', 'unity-center', 'unity-seam')][string]$Mode = 'run_seam',
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Rtt = 100,
    [int]$Jitter = 10,
    [double]$Loss = 1,
    [uint64]$Seed = 42,
    [int]$TimeoutSec = 900,
    [double]$RunnerSeconds = 0,
    [string]$Client,
    [int]$SmokeSec = 20,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$inv = [Globalization.CultureInfo]::InvariantCulture
$root = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

if ($Mode -ne 'run_seam' -and (-not $Client -or -not (Test-Path $Client))) {
    Write-Error "-Mode ${Mode}: нужен -Client <Orpheus.exe> (сборка: tools\build_client.ps1)"
    exit 2
}

if (-not $NoBuild) {
    dotnet build "$root\Orpheus.sln" -c Release --nologo -v minimal | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "сборка не прошла"; exit 2 }
}

# Двоичные — копией: сборки в рабочей копии во время прогона не мешают.
$bin = Join-Path $Out 'bin'
New-Item -ItemType Directory -Force "$bin\server", "$bin\bots" | Out-Null
Copy-Item "$root\src\Orpheus.Server\bin\Release\net9.0\*" "$bin\server" -Recurse -Force
Copy-Item "$root\src\Orpheus.Bots\bin\Release\net9.0\*" "$bin\bots" -Recurse -Force

# Снять свой процесс: объект Process держит дескриптор именно того процесса — PID, занятый потом другим, не задеть.
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
    "$($p.Id)" | Set-Content -Encoding utf8 "$Out\$name.pid"
    Write-Host "запущен $name (PID $($p.Id))"
    return $p
}

$serverArgs = "--create --seed $Seed --size 1024 --port 0 --logs `"$Out\server-logs`""
switch ($Mode) {
    'run_seam' { $serverArgs += ' --debug-run-lanes' }
    'unity-center' { $serverArgs += ' --bots 2:wander' }
    'unity-seam' { $serverArgs += ' --spawn-seam --bots 2:shuttle' }
}

$sim = "--rtt $Rtt --jitter $Jitter --loss $($Loss.ToString($inv))"
$server = $null
$clients = [ordered]@{}
$exitCode = 1
$watch = [Diagnostics.Stopwatch]::StartNew()
$load = "$Out\load.csv"
"sec,cpu_percent" | Set-Content -Encoding utf8 $load
try {
    $server = Start-Own 'server' 'dotnet' "`"$bin\server\OrpheusServer.dll`" $serverArgs"

    # Порт — из строки готовности сервера (свой свободный, без гонки за «свободный» порт снаружи).
    $port = 0
    while ($port -eq 0) {
        if ($server.HasExited) { throw "сервер вышел до готовности, код $($server.ExitCode); см. $Out\server-err.txt" }
        if ($watch.Elapsed.TotalSeconds -gt 120) { throw "сервер не готов за 120 с" }
        $line = Select-String -Path "$Out\server-out.txt" -Pattern 'orpheus-ready port=(\d+)' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($line) { $port = [int]$line.Matches[0].Groups[1].Value } else { Start-Sleep -Milliseconds 100 }
    }
    Write-Host ("сервер готов: порт {0}, {1:F1} с" -f $port, $watch.Elapsed.TotalSeconds)

    $bots = "`"$bin\bots\Orpheus.Bots.dll`" --connect 127.0.0.1:$port --count 1 $sim"
    if ($Mode -eq 'run_seam') {
        # Наблюдатель первым: стоит у угла швов (полосы, места 0 и 2) и кончает сам, когда бегущий ушёл.
        $clients['observer'] = Start-Own 'observer' 'dotnet' "$bots --script seam_watch --name watch --seed 8 --seconds $TimeoutSec --report `"$Out\observer-report.json`""
        $rs = if ($RunnerSeconds -gt 0) { $RunnerSeconds } else { $TimeoutSec - 30 }
        $clients['runner'] = Start-Own 'runner' 'dotnet' "$bots --script run_seam --name run --seed 7 --seconds $($rs.ToString($inv)) --report `"$Out\runner-report.json`""
    } else {
        $netSim = if ($Rtt -gt 0 -or $Jitter -gt 0 -or $Loss -gt 0) { " --net-sim $Rtt,$Jitter,$($Loss.ToString($inv))" } else { '' }
        $seamKey = if ($Mode -eq 'unity-seam') { ' --net-smoke-seam' } else { '' }
        $clients['client'] = Start-Own 'client' $Client "-batchmode -nographics -logFile `"$Out\client.log`" --connect 127.0.0.1:$port --net-smoke $SmokeSec$seamKey$netSim"
    }

    $nextLoad = 0
    $serverDiedEarly = $false
    while ($true) {
        $alive = @($clients.Values | Where-Object { -not $_.HasExited })
        if ($alive.Count -eq 0) { break }
        if ($server.HasExited) { $serverDiedEarly = $true; Write-Warning "сервер вышел раньше клиентов, код $($server.ExitCode)"; break }
        $t = $watch.Elapsed.TotalSeconds
        if ($t -gt $TimeoutSec + 60) { Write-Warning "клиенты не вышли за $($TimeoutSec + 60) с"; break }
        if ($t -ge $nextLoad) {
            $nextLoad += 10
            $cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
            [string]::Format($inv, "{0:F0},{1:F0}", $t, $cpu) | Add-Content -Encoding utf8 $load
        }

        Start-Sleep -Milliseconds 200
    }

    # Коды выхода всех: не вышедший сам — снят и считается упавшим.
    $failed = 0
    foreach ($name in $clients.Keys) {
        $p = $clients[$name]
        if (-not $p.HasExited) { Stop-Own $p $name; $failed++; Write-Host "${name}: не вышел — снят"; continue }
        $p.WaitForExit()
        $code = $p.ExitCode
        Write-Host "${name}: код выхода $code"
        if ($code -ne 0) { $failed++ }
    }

    if ($serverDiedEarly) { $failed++ }
    $exitCode = if ($failed -eq 0) { 0 } else { 1 }
} catch {
    Write-Warning "$_"
    $exitCode = 2
} finally {
    foreach ($name in $clients.Keys) { Stop-Own $clients[$name] $name }
    Stop-Own $server 'server' # сервер без команды выхода: снимается свой, по объекту процесса
}

$cpuAll = @(Import-Csv $load | ForEach-Object { [double]$_.cpu_percent })
if ($cpuAll.Count -gt 0) {
    $stat = $cpuAll | Measure-Object -Average -Maximum
    $loadText = "загрузка машины: средняя {0:F0} %, наибольшая {1:F0} % ({2} замеров)" -f $stat.Average, $stat.Maximum, $stat.Count
    Write-Host $loadText
    $loadText | Set-Content -Encoding utf8 "$Out\load.txt"
}

foreach ($f in @('runner-out.txt', 'observer-out.txt')) {
    if (Test-Path "$Out\$f") {
        Write-Host "--- $f (итог)"
        Get-Content "$Out\$f" -Encoding utf8 | Select-String -Pattern '^(итог|  бот|    круг)' | ForEach-Object { Write-Host $_.Line }
    }
}

Write-Host ("прогон {0}: код {1}, {2:F0} с" -f $Mode, $exitCode, $watch.Elapsed.TotalSeconds)
exit $exitCode
