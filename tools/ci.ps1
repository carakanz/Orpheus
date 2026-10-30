# Проверка ядра Orpheus: сборка решения и все тесты (docs/07, 11.2).
# Запуск: powershell -File tools/ci.ps1   (или pwsh tools/ci.ps1)
# Выход 0 — зелёный; иначе код ошибки. Лимит времени — 60 с (0 — без лимита).
param(
    [string]$Configuration = 'Debug',
    [int]$TimeLimitSec = 60
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'Orpheus.sln'
$total = [System.Diagnostics.Stopwatch]::StartNew()

function Invoke-Step([string]$name, [string[]]$dotnetArgs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "== $name" -ForegroundColor Cyan
    & dotnet @dotnetArgs
    $code = $LASTEXITCODE
    $sec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    if ($code -ne 0) {
        Write-Host "== $name — ОШИБКА (код $code), $sec с" -ForegroundColor Red
        exit $code
    }
    Write-Host "== $name — ок, $sec с" -ForegroundColor Green
}

Invoke-Step 'build' @('build', $sln, '-c', $Configuration, '--nologo', '-v', 'minimal')
Invoke-Step 'test' @('test', $sln, '-c', $Configuration, '--no-build', '--nologo', '-v', 'minimal')

$sec = [math]::Round($total.Elapsed.TotalSeconds, 1)
if ($TimeLimitSec -gt 0 -and $total.Elapsed.TotalSeconds -gt $TimeLimitSec) {
    Write-Host "CI: всё прошло, но за $sec с — больше лимита $TimeLimitSec с" -ForegroundColor Red
    exit 3
}
Write-Host "CI: зелёный за $sec с" -ForegroundColor Green
exit 0
