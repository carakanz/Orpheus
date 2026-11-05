# Проверка ядра Orpheus: сборка решения и все тесты (docs/07, 11.2).
# Запуск: tools\ci.cmd (обёртка для cmd) или powershell -NoProfile -File tools/ci.ps1 (или pwsh tools/ci.ps1).
# Выход 0 — зелёный; иначе код ошибки. Лимит времени — 60 с (0 — без лимита).
#
# Кодировка: консоль переводится в UTF-8, поэтому перенаправление (tools\ci.cmd > log.txt 2>&1)
# и -LogFile дают UTF-8 без BOM — кириллица в логе CI читается без подбора кодировки.
param(
    [string]$Configuration = 'Debug',
    [int]$TimeLimitSec = 60,
    [string]$LogFile = ''
)

$ErrorActionPreference = 'Stop'

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'Orpheus.sln'
$total = [System.Diagnostics.Stopwatch]::StartNew()

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$logPath = ''
if ($LogFile -ne '') {
    $logPath = if ([System.IO.Path]::IsPathRooted($LogFile)) { $LogFile } else { Join-Path $root $LogFile }
    [System.IO.File]::WriteAllText($logPath, '', $utf8NoBom)
}

function Write-Line([string]$text, [string]$color = 'Gray') {
    Write-Host $text -ForegroundColor $color
    if ($logPath -ne '') {
        [System.IO.File]::AppendAllText($logPath, $text + "`n", $utf8NoBom)
    }
}

function Invoke-Step([string]$name, [string]$color, [string[]]$dotnetArgs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Line "== $name" $color
    # dotnet пишет падения тестов в stderr; с ErrorActionPreference=Stop PowerShell
    # оборвал бы CI на первой строке FAIL и лог не добрался бы до сводки.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $lines = & dotnet @dotnetArgs 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    foreach ($line in $lines) { Write-Line "   $line" }
    $sec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    if ($code -ne 0) {
        Write-Line "== $name — ОШИБКА (код $code), $sec с" 'Red'
        exit $code
    }
    Write-Line "== $name — ок, $sec с" 'Green'
}

Invoke-Step 'build' 'Cyan' @('build', $sln, '-c', $Configuration, '--nologo', '-v', 'minimal')
Invoke-Step 'test' 'Cyan' @('test', $sln, '-c', $Configuration, '--no-build', '--nologo', '-v', 'minimal')

$sec = [math]::Round($total.Elapsed.TotalSeconds, 1)
if ($TimeLimitSec -gt 0 -and $total.Elapsed.TotalSeconds -gt $TimeLimitSec) {
    Write-Line "CI: всё прошло, но за $sec с — больше лимита $TimeLimitSec с" 'Red'
    exit 3
}
Write-Line "CI: зелёный за $sec с" 'Green'
exit 0
