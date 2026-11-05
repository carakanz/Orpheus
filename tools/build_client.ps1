<#
.SYNOPSIS
  Сборка клиента Orpheus для Windows командой (задача 0-002; docs/07, 1.3 и 11.1a; docs/08, 3.0).

.DESCRIPTION
  1. Собирает DLL ядра (проекты src/*/ с TargetFramework netstandard2.1) и кладёт их
     в client/Assets/Plugins/Orpheus/. Нет src/ или проектов ядра — шаг пропускается с сообщением.
  2. Запускает Unity в пакетном режиме:
     -batchmode -nographics -quit -executeMethod Orpheus.Editor.BuildScript.BuildWindows -logFile ...
  3. Кладёт сборку в build/client-win/Orpheus.exe (папка build/ — в .gitignore).
  4. С ключом -Smoke запускает собранный Orpheus.exe --smoke и проверяет код выхода.
  Выход ≠ 0 при любой ошибке.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/build_client.ps1
  powershell -ExecutionPolicy Bypass -File tools/build_client.ps1 -Backend Mono -Clean -Smoke
#>
[CmdletBinding()]
param(
    # IL2CPP — по умолчанию (08, 3.0). Mono — только пока не установлен модуль IL2CPP (блокер в карточке 0-002).
    [ValidateSet('IL2CPP', 'Mono')] [string] $Backend = 'IL2CPP',
    # Удалить client/Library, client/Temp и прошлую сборку — сборка «с нуля».
    [switch] $Clean,
    # После сборки запустить Orpheus.exe -batchmode -nographics --smoke.
    [switch] $Smoke,
    # Сборка разработки (BuildOptions.Development).
    [switch] $Development,
    [string] $UnityPath = $(if ($env:ORPHEUS_UNITY) { $env:ORPHEUS_UNITY } else { 'C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe' }),
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

if (-not (Test-Path $UnityPath)) { Fail "нет Unity: $UnityPath (задай -UnityPath или ORPHEUS_UNITY)" }
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

# --- 3. Дымовая проверка --------------------------------------------------------------------
if ($Smoke) {
    Say 'smoke: Orpheus.exe -batchmode -nographics --smoke'
    if (Test-Path $SmokeLog) { Remove-Item -Force $SmokeLog }
    $sp = Start-Process -FilePath $Exe -ArgumentList @('-batchmode', '-nographics', '--smoke', '-logFile', "`"$SmokeLog`"") -PassThru -NoNewWindow `
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

Say ("готово: {0} ({1}), всего {2:0.0} с" -f $Exe, $Backend, $total.Elapsed.TotalSeconds)
exit 0
