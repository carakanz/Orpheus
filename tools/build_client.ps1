<#
.SYNOPSIS
  Сборка клиента Orpheus для Windows командой (задача 0-002; docs/07, 1.3 и 11.1a; docs/08, 3.0).

.DESCRIPTION
  1. Собирает DLL ядра (проекты src/*/ с TargetFramework netstandard2.1) и кладёт их
     в client/Assets/Plugins/Orpheus/. Нет src/ или проектов ядра — шаг пропускается с сообщением.
  2. Запускает Unity в пакетном режиме:
     -batchmode -nographics -quit -executeMethod Orpheus.Editor.BuildScript.BuildWindows -logFile ...
  3. Кладёт сборку в build/client-win/Orpheus.exe (папка build/ — в .gitignore) и рядом — OrpheusServer.exe
     (dotnet publish src/Orpheus.Server, 0-024): одиночная игра и хост запускают его дочерним процессом (docs/07, 2.3).
  4. С ключом -Smoke запускает собранный Orpheus.exe --smoke --gen stub и проверяет код выхода.
  6. С ключом -ThreadSmoke — «сервер в потоке» (0-031): Orpheus.exe --server-thread (ServerHost в потоке, локальная связь,
     PcStorage), новый мир на -ThreadSmokeSeconds, затем загрузка — хэш загруженного совпадает с сохранённым при выходе.
  5. С ключом -HostSmoke — net-smoke хоста: Orpheus.exe --host сам запускает сервер (новый мир в build/logs/host-smoke-world,
     2 бота wander), входит хозяином, «Открыть для друзей» только на 127.0.0.2 (не на всех адресах — без окна брандмауэра),
     ходит, выходит; сервер сохраняет мир и выходит с кодом 0.
  Выход ≠ 0 при любой ошибке.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/build_client.ps1
  powershell -ExecutionPolicy Bypass -File tools/build_client.ps1 -Backend Mono -Clean -Smoke -HostSmoke
#>
[CmdletBinding()]
param(
    # IL2CPP — по умолчанию (08, 3.0). Mono — только пока не установлен модуль IL2CPP (блокер в карточке 0-002).
    [ValidateSet('IL2CPP', 'Mono')] [string] $Backend = 'IL2CPP',
    # Удалить client/Library, client/Temp и прошлую сборку — сборка «с нуля».
    [switch] $Clean,
    # После сборки запустить Orpheus.exe -batchmode -nographics --smoke.
    [switch] $Smoke,
    # После сборки — net-smoke хоста: клиент сам запускает OrpheusServer (0-024).
    [switch] $HostSmoke,
    # После сборки — «сервер в потоке» (0-031): ServerHost в потоке клиента, локальная связь, PcStorage; два запуска —
    # новый мир на -ThreadSmokeSeconds, затем загрузка (хэш загруженного = хэш сохранённого при выходе).
    [switch] $ThreadSmoke,
    # Длина первого запуска «сервера в потоке», с (приёмка 0.12 / 07 1.9 п. 5 — 600).
    [int] $ThreadSmokeSeconds = 20,
    # Только «сервер в потоке» на готовой сборке build/client-win, без сборки Unity (повтор долгого прогона).
    [switch] $NoBuild,
    # Сборка разработки (BuildOptions.Development).
    [switch] $Development,
    # Unity: по умолчанию — версия проекта из client/ProjectSettings/ProjectVersion.txt в папке Unity Hub (план, шаг 0.5).
    [string] $UnityPath = $(if ($env:ORPHEUS_UNITY) { $env:ORPHEUS_UNITY } else { '' }),
    [int] $TimeoutMinutes = 90
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Client = Join-Path $Root 'client'
$Plugins = Join-Path $Client 'Assets\Plugins\Orpheus'
$OutDir = Join-Path $Root 'build\client-win'
$LogDir = Join-Path $Root 'build\logs'
$Exe = Join-Path $OutDir 'Orpheus.exe'
$UnityLog = Join-Path $LogDir 'unity-build.log'
$SmokeLog = Join-Path $LogDir 'smoke.log'

$total = [Diagnostics.Stopwatch]::StartNew()
function Say([string] $msg) { Write-Host ("[build_client {0,6:0.0}s] {1}" -f $total.Elapsed.TotalSeconds, $msg) }
function Fail([string] $msg) { Write-Host "[build_client] ОШИБКА: $msg" -ForegroundColor Red; exit 1 }

if (-not $UnityPath) {
    $versionFile = Join-Path $Client 'ProjectSettings\ProjectVersion.txt'
    if (-not (Test-Path $versionFile)) { Fail "нет $versionFile — не узнать версию Unity (задай -UnityPath или ORPHEUS_UNITY)" }
    $m = Select-String -Path $versionFile -Pattern '^m_EditorVersion:\s*(\S+)' | Select-Object -First 1
    if (-not $m) { Fail "в $versionFile нет строки m_EditorVersion (задай -UnityPath или ORPHEUS_UNITY)" }
    $UnityPath = "C:\Program Files\Unity\Hub\Editor\$($m.Matches[0].Groups[1].Value)\Editor\Unity.exe"
}
if (-not (Test-Path $UnityPath)) { Fail "нет Unity: $UnityPath (версия проекта не установлена? задай -UnityPath или ORPHEUS_UNITY)" }
Say "Unity: $UnityPath"
New-Item -ItemType Directory -Force $LogDir | Out-Null

# Не больше одного процесса Unity на проект: Unity держит client/Temp/UnityLockfile открытым.
$lock = Join-Path $Client 'Temp\UnityLockfile'
if (Test-Path $lock) {
    try { $fs = [IO.File]::Open($lock, 'Open', 'ReadWrite', 'None'); $fs.Close() }
    catch { Fail "проект client/ уже открыт другим процессом Unity ($lock)" }
}

if ($Clean) {
    Say 'чистка: client/Library, client/Temp, build/client-win'
    foreach ($d in @((Join-Path $Client 'Library'), (Join-Path $Client 'Temp'), $OutDir)) {
        if (Test-Path $d) { Remove-Item -Recurse -Force $d }
    }
}

if ($NoBuild) {
    if (-not (Test-Path $Exe)) { Fail "-NoBuild: нет готовой сборки $Exe" }
    Say '-NoBuild: сборка не делается — прогоны на build/client-win'
} else {

# --- 1. DLL ядра → client/Assets/Plugins/Orpheus ------------------------------------------
$coreProjects = @()
$src = Join-Path $Root 'src'
if (Test-Path $src) {
    $coreProjects = @(Get-ChildItem -Path $src -Filter *.csproj -Recurse |
        Where-Object { (Get-Content -Raw $_.FullName) -match '<TargetFrameworks?>[^<]*netstandard2\.1' })
}
if ($coreProjects.Count -eq 0) {
    Say 'ядра нет (src/*/ с netstandard2.1 не найдено) — шаг DLL пропущен'
} else {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { Fail 'нет dotnet — не собрать ядро' }
    $stage = Join-Path $Root 'build\core-dll'
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    foreach ($p in $coreProjects) {
        Say "ядро: dotnet publish $($p.Name)"
        & dotnet publish $p.FullName -c Release -o $stage --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { Fail "dotnet publish $($p.Name) — код $LASTEXITCODE" }
    }
    New-Item -ItemType Directory -Force $Plugins | Out-Null
    $fresh = @(Get-ChildItem $stage -File | Where-Object { $_.Extension -in '.dll', '.pdb' })
    $names = $fresh | ForEach-Object { $_.Name }
    # старые DLL, которых больше нет в ядре, — убрать вместе с .meta
    Get-ChildItem $Plugins -File | Where-Object { $_.Extension -in '.dll', '.pdb' -and $names -notcontains $_.Name } |
        ForEach-Object { Remove-Item -Force $_.FullName, "$($_.FullName).meta" -ErrorAction SilentlyContinue }
    foreach ($f in $fresh) {
        $dst = Join-Path $Plugins $f.Name
        if (-not (Test-Path $dst) -or (Get-FileHash $dst).Hash -ne (Get-FileHash $f.FullName).Hash) {
            Copy-Item -Force $f.FullName $dst
        }
    }
    Say ("ядро: в Assets/Plugins/Orpheus — {0}" -f (($fresh | Where-Object Extension -eq '.dll' | ForEach-Object Name) -join ', '))
}

# --- 2. Unity: сборка ----------------------------------------------------------------------
if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
$unityArgs = @(
    '-batchmode', '-nographics', '-quit',
    '-projectPath', "`"$Client`"",
    '-executeMethod', 'Orpheus.Editor.BuildScript.BuildWindows',
    '-orpheusBackend', $Backend,
    '-orpheusOut', "`"$Exe`"",
    '-logFile', "`"$UnityLog`""
)
if ($Development) { $unityArgs += '-orpheusDevelopment' }
Say "Unity: сборка $Backend → build/client-win (лог: build/logs/unity-build.log)"
$unityTime = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $UnityPath -ArgumentList $unityArgs -PassThru -NoNewWindow
$null = $proc.Handle  # без этого ExitCode после WaitForExit(таймаут) бывает пустым
if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
    try { $proc.Kill() } catch { }
    Fail "Unity не уложилась в $TimeoutMinutes мин — процесс остановлен"
}
$proc.WaitForExit()
$code = $proc.ExitCode
$unityTime.Stop()
Say ("Unity: код {0}, {1:0.0} с" -f $code, $unityTime.Elapsed.TotalSeconds)

if (Test-Path $UnityLog) {
    Select-String -Path $UnityLog -Pattern '^\[build\] (result|error|exit)' | ForEach-Object { Write-Host "  $($_.Line)" }
    Select-String -Path $UnityLog -Pattern 'error CS\d+|BuildFailedException|IL2CPP.*(not installed|is not supported)' |
        Select-Object -First 20 | ForEach-Object { Write-Host "  $($_.Line)" -ForegroundColor Red }
}
if ($code -ne 0) { Fail "сборка Unity не прошла (код $code), лог: $UnityLog" }
if (-not (Test-Path $Exe)) { Fail "нет $Exe после сборки" }

# --- 2a. OrpheusServer рядом с клиентом (0-024) ---------------------------------------------
# Одиночная и хост запускают его дочерним процессом: PcServerLauncher.DefaultPath(папка Orpheus.exe).
# Зависит от рантайма .NET (как прежде сервер для playable); самодостаточная сборка — вместе с установщиком.
$ServerExe = Join-Path $OutDir 'OrpheusServer.exe'
$serverProj = Join-Path $Root 'src\Orpheus.Server\Orpheus.Server.csproj'
if (-not (Test-Path $serverProj)) { Fail "нет $serverProj" }
Say 'сервер: dotnet publish Orpheus.Server → build/client-win'
& dotnet publish $serverProj -c Release -o $OutDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish Orpheus.Server — код $LASTEXITCODE" }
if (-not (Test-Path $ServerExe)) { Fail "нет $ServerExe после publish" }

}  # -NoBuild

# --- 3. Дымовая проверка --------------------------------------------------------------------
if ($Smoke) {
    # --gen stub: проверки smoke (полоса через шов, образцы для check_smoke_tiles.py) сверяются с заглушкой 0-007;
    # без ключа клиент офлайн строит мир генератором сервера (v1, шаг 0.5).
    Say 'smoke: Orpheus.exe -batchmode -nographics --smoke --gen stub'
    if (Test-Path $SmokeLog) { Remove-Item -Force $SmokeLog }
    $sp = Start-Process -FilePath $Exe -ArgumentList @('-batchmode', '-nographics', '--smoke', '--gen', 'stub', '-logFile', "`"$SmokeLog`"") -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $LogDir 'smoke.stdout.txt')
    $null = $sp.Handle
    if (-not $sp.WaitForExit(60 * 1000)) { try { $sp.Kill() } catch { }; Fail 'smoke: Orpheus.exe не вышел за 60 с' }
    $sp.WaitForExit()
    Select-String -Path $SmokeLog -Pattern '^\[smoke\]' | ForEach-Object { Write-Host "  $($_.Line)" }
    if ($sp.ExitCode -ne 0) { Fail "smoke: код выхода $($sp.ExitCode), лог: $SmokeLog" }
    if (-not (Select-String -Path $SmokeLog -Pattern '^\[smoke\] done' -Quiet)) { Fail "smoke: нет строки '[smoke] done' в $SmokeLog" }
    # ошибки до старта SmokeExit (загрузка движка и сцены) компонент не видит — ищем их в логе
    $bad = @(Select-String -Path $SmokeLog -Pattern '(?i)exception|error' | Where-Object { $_.Line -notmatch '^\[smoke\]' })
    if ($bad.Count -gt 0) {
        $bad | Select-Object -First 10 | ForEach-Object { Write-Host "  $($_.Line)" -ForegroundColor Red }
        Fail "smoke: в логе $($bad.Count) строк с ошибками, лог: $SmokeLog"
    }
    Say 'smoke: код 0'
}

# --- 4. net-smoke хоста (0-024) ---------------------------------------------------------------
if ($HostSmoke) {
    $hostLog = Join-Path $LogDir 'host-smoke.log'
    $hostWorld = Join-Path $LogDir 'host-smoke-world'
    foreach ($p in @($hostLog, $hostWorld)) { if (Test-Path $p) { Remove-Item -Recurse -Force $p } }
    Say 'host-smoke: Orpheus.exe --host --net-smoke 6 (сервер запускает сам клиент; «для друзей» — 127.0.0.2)'
    $hp = Start-Process -FilePath $Exe -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $LogDir 'host-smoke.stdout.txt') -ArgumentList @(
        '-batchmode', '-nographics', '--host', '--world', "`"$hostWorld`"", '--net-smoke', '6',
        '--open-for-friends', '--open-address', '127.0.0.2', '--open-port', '0',
        '--server-arg', '--bots', '--server-arg', '2:wander', '-logFile', "`"$hostLog`"")
    $null = $hp.Handle
    if (-not $hp.WaitForExit(90 * 1000)) { try { $hp.Kill() } catch { }; Fail 'host-smoke: Orpheus.exe не вышел за 90 с' }
    $hp.WaitForExit()
    Select-String -Path $hostLog -Pattern '^\[(smoke|host)\]' | ForEach-Object { Write-Host "  $($_.Line)" }
    # Сервер, запущенный клиентом, не должен пережить клиента: номер процесса — из строки «сервер готов».
    $ready = Select-String -Path $hostLog -Pattern '^\[host\] сервер готов .* процесс (\d+)' | Select-Object -First 1
    if ($ready) {
        $serverPid = [int]$ready.Matches[0].Groups[1].Value
        $left = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
        if ($left -and $left.ProcessName -eq 'OrpheusServer') {
            Stop-Process -Id $serverPid -Force
            Fail "host-smoke: сервер (процесс $serverPid) пережил клиента — снят"
        }
    }
    if ($hp.ExitCode -ne 0) { Fail "host-smoke: код выхода $($hp.ExitCode), лог: $hostLog" }
    if (-not (Select-String -Path $hostLog -Pattern '^\[smoke\] done' -Quiet)) { Fail "host-smoke: нет строки '[smoke] done' в $hostLog" }
    if (-not $ready) { Fail "host-smoke: клиент не запускал сервер (нет строки '[host] сервер готов')" }
    if (-not (Select-String -Path $hostLog -Pattern '^\[host\] сервер остановлен: код 0' -Quiet)) { Fail 'host-smoke: сервер не вышел штатно с кодом 0' }
    Say 'host-smoke: код 0'
}

# --- 5. «Сервер в потоке» (0-031) ------------------------------------------------------------
if ($ThreadSmoke) {
    $threadWorld = Join-Path $LogDir 'thread-smoke-world'
    if (Test-Path $threadWorld) { Remove-Item -Recurse -Force $threadWorld }
    # Автосохранение раз в 30 с мира на длинном прогоне, раз в 5 с — на коротком: оба пути записи проходят.
    $autosave = if ($ThreadSmokeSeconds -ge 120) { '30' } else { '5' }
    $runs = @(
        @{ Name = 'thread-smoke'; Seconds = $ThreadSmokeSeconds },
        @{ Name = 'thread-smoke-load'; Seconds = 6 }
    )
    $hashes = @{}
    foreach ($run in $runs) {
        $log = Join-Path $LogDir "$($run.Name).log"
        if (Test-Path $log) { Remove-Item -Force $log }
        Say "$($run.Name): Orpheus.exe --server-thread --net-smoke $($run.Seconds) (мир build/logs/thread-smoke-world, 2 бота wander)"
        $tp = Start-Process -FilePath $Exe -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $LogDir "$($run.Name).stdout.txt") -ArgumentList @(
            '-batchmode', '-nographics', '--server-thread', '--world', "`"$threadWorld`"", '--net-smoke', "$($run.Seconds)",
            '--server-arg', '--bots', '--server-arg', '2:wander', '--server-arg', '--autosave', '--server-arg', $autosave,
            '-logFile', "`"$log`"")
        $null = $tp.Handle
        $limit = ($run.Seconds + 90) * 1000
        if (-not $tp.WaitForExit($limit)) { try { $tp.Kill() } catch { }; Fail "$($run.Name): Orpheus.exe не вышел за $($limit / 1000) с" }
        $tp.WaitForExit()
        Select-String -Path $log -Pattern '^\[(smoke|thread)\]' | ForEach-Object { Write-Host "  $($_.Line)" }
        if ($tp.ExitCode -ne 0) { Fail "$($run.Name): код выхода $($tp.ExitCode), лог: $log" }
        if (-not (Select-String -Path $log -Pattern '^\[smoke\] done' -Quiet)) { Fail "$($run.Name): нет строки '[smoke] done' в $log" }
        if (-not (Select-String -Path $log -Pattern '^\[thread\] сервер остановлен: код 0' -Quiet)) { Fail "$($run.Name): сервер в потоке не вышел штатно с кодом 0" }
        $saves = Select-String -Path $log -Pattern '^\[thread\] сохранения: загружено №(\d+) \(хэш ([0-9a-f]{16})\), записано (\d+), последнее №(\d+); хэш в конце ([0-9a-f]{16})' | Select-Object -First 1
        if (-not $saves) { Fail "$($run.Name): нет строки итога сохранений в $log" }
        $g = $saves.Matches[0].Groups
        $hashes[$run.Name] = @{ Loaded = [int]$g[1].Value; LoadedHash = $g[2].Value; Written = [int]$g[3].Value; Last = [int]$g[4].Value; Final = $g[5].Value }
    }
    $a = $hashes['thread-smoke']; $b = $hashes['thread-smoke-load']
    if ($a.Written -lt 1) { Fail 'thread-smoke: ни одного сохранения' }
    if ($b.Loaded -ne $a.Last) { Fail "thread-smoke-load: загружено №$($b.Loaded), а последнее сохранённое — №$($a.Last)" }
    if ($b.LoadedHash -ne $a.Final) { Fail "thread-smoke-load: хэш загруженного $($b.LoadedHash) ≠ хэш при выходе $($a.Final)" }
    Say "thread-smoke: код 0; сохранений $($a.Written), загружено №$($b.Loaded), хэш совпал ($($a.Final))"
}

Say ("готово: {0} ({1}), всего {2:0.0} с" -f $Exe, $Backend, $total.Elapsed.TotalSeconds)
exit 0
