<#
.SYNOPSIS
  Прогон стенда «48 Tilemap» (карточка 1-022) на собранном клиенте: fps, стоимость правок, дыры, сверка тайлов.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\run_tilebench.ps1 -Seconds 30
#>
param(
    [int] $Seconds = 30,
    [ValidateSet('Mono','IL2CPP')] [string] $Backend = 'Mono',
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [int] $TimeoutSec = 900,
    # E1-4: frame time WITH rendering: the camera draws to a 1920x1080 texture every frame (--bench-render).
    # Needs a graphics device, so the game runs on the invisible desktop (tools\run_hidden.ps1), never in a window.
    [switch] $Render,
    [ValidateSet('stub','v1')] [string] $Gen = 'stub',
    # Extra client keys, e.g. -Extra '--paint-demo'
    [string] $Extra = ''
)
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$exe = Join-Path $Root 'build\client-win\Orpheus.exe'
if (-not (Test-Path $exe)) { throw "нет сборки: $exe" }

$logDir = Join-Path $Root 'build\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$tag = "tilebench-$Seconds"
if ($Backend -ne 'Mono') { $tag += "-$Backend" }
$log = Join-Path $logDir "$tag.log"
Remove-Item $log -ErrorAction SilentlyContinue

if ($Render) {
    $tag += "-render-$Gen"
    $log = Join-Path $logDir "$tag.log"
    Remove-Item $log -ErrorAction SilentlyContinue
    $line = "-batchmode -monitor 2 -screen-fullscreen 0 --tile-bench $Seconds --bench-render --gen $Gen --seed 202609 --size 1024 $Extra -logFile `"$log`""
    & (Join-Path $PSScriptRoot 'run_hidden.ps1') -Exe $exe -Arguments $line -TimeoutSec $TimeoutSec
    $code = $LASTEXITCODE
} else {
    $p = Start-Process -FilePath $exe -PassThru -NoNewWindow `
        -ArgumentList @('-batchmode', '-nographics', '--tile-bench', "$Seconds", '--gen', $Gen,
                       '--seed', '202609', '--size', '1024',
                       '-screen-width', '1920', '-screen-height', '1080', '-logFile', "`"$log`"")
    $null = $p.Handle
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch { }; throw "прогон завис (>{0} с)" -f $TimeoutSec }
    $code = $p.ExitCode
}
Write-Host "[tilebench_run] код =$code лог: $log"

Select-String -Path $log -Pattern 'tilebench' -Encoding UTF8 | ForEach-Object { $_.Line.Trim() }
Write-Host '[tilebench_run] ошибки в логе:'
Select-String -Path $log -Pattern 'error|Exception|FAIL' -Encoding UTF8 | Select-Object -First 12 | ForEach-Object { $_.Line.Trim() }
exit $code
