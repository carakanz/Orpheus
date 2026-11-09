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
    [int] $TimeoutSec = 900
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

$p = Start-Process -FilePath $exe -PassThru -NoNewWindow `
    -ArgumentList @('-batchmode', '-nographics', '--tile-bench', "$Seconds",
                   '--seed', '202609', '--size', '1024',
                   '-screen-width', '1920', '-screen-height', '1080', '-logFile', "`"$log`"")
$null = $p.Handle
if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch { }; throw "прогон завис (>{0} с)" -f $TimeoutSec }
Write-Host "[tilebench_run] код =$($p.ExitCode) лог: $log"

Select-String -Path $log -Pattern 'tilebench' -Encoding UTF8 | ForEach-Object { $_.Line.Trim() }
Write-Host '[tilebench_run] ошибки в логе:'
Select-String -Path $log -Pattern 'error|Exception|FAIL' -Encoding UTF8 | Select-Object -First 12 | ForEach-Object { $_.Line.Trim() }
exit $p.ExitCode
