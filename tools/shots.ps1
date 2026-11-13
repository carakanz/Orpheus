<#
.SYNOPSIS
  Screenshot stand of Orpheus (plan step 0.3): one command - a set of PNG from fixed seeds and cameras.

.DESCRIPTION
  Reads scenes from tools/shots.json (name, seed, size, tile, width) and runs the built client
  (build/client-win/Orpheus.exe, tools/build_client.ps1) once per scene, offline, in a 1920x1080 window:
    Orpheus.exe --seed S --size N --screenshot <png> [--shot-tile X,Y] --shot-width W
  PNG go to build/shots/<yyyyMMdd-HHmm>/<name>.png, then a contact sheet sheet.png is glued
  (python + Pillow: tools/shots_sheet.py; without them - System.Drawing).
  The client fixes the frame time in this mode, so a rerun gives the same frames; -Check runs every scene
  twice and compares the files byte by byte.
  This file is ASCII only: Windows PowerShell 5.1 breaks on UTF-8 without BOM.
  Exit code 0 - all scenes shot (and equal with -Check), 1 - otherwise.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\shots.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\shots.ps1 -Only 01_start_64 -Check
#>
[CmdletBinding()]
param(
    [string] $Exe = '',
    [string] $Scenes = '',
    # Output folder; default build/shots/<yyyyMMdd-HHmm>.
    [string] $OutDir = '',
    # Shoot only scenes whose name matches this wildcard.
    [string] $Only = '*',
    # Run every scene twice and compare the PNG (proof that a rerun gives the same frames).
    [switch] $Check,
    # Per-scene limit, seconds.
    [int] $TimeoutSec = 60
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Exe) { $Exe = Join-Path $Root 'build\client-win\Orpheus.exe' }
if (-not $Scenes) { $Scenes = Join-Path $PSScriptRoot 'shots.json' }
if (-not $OutDir) { $OutDir = Join-Path $Root ('build\shots\' + (Get-Date -Format 'yyyyMMdd-HHmm')) }

function Fail([string] $msg) { Write-Host "[shots] ERROR: $msg" -ForegroundColor Red; exit 1 }

if (-not (Test-Path $Exe)) { Fail "no client build: $Exe (run tools\build_client.ps1)" }
if (-not (Test-Path $Scenes)) { Fail "no scene list: $Scenes" }

$cfg = Get-Content -Raw -Encoding UTF8 $Scenes | ConvertFrom-Json
$width = 1920; $height = 1080
if ($cfg.window) { $width = [int]$cfg.window.width; $height = [int]$cfg.window.height }
$list = @($cfg.scenes | Where-Object { $_.name -like $Only })
if ($list.Count -eq 0) { Fail "no scenes match '$Only' in $Scenes" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$logDir = Join-Path $OutDir 'logs'
New-Item -ItemType Directory -Force $logDir | Out-Null

$total = [Diagnostics.Stopwatch]::StartNew()

function Invoke-Shot($scene, [string] $png) {
    $log = Join-Path $logDir ($scene.name + '.log')
    if (Test-Path $png) { Remove-Item -Force $png }
    $a = @('--seed', "$($scene.seed)", '--size', "$($scene.size)", '--screenshot', "`"$png`"")
    if ($scene.tile) { $a += @('--shot-tile', ("{0},{1}" -f [int]$scene.tile[0], [int]$scene.tile[1])) }
    if ($scene.width) { $a += @('--shot-width', "$([int]$scene.width)") }
    $a += @('-screen-width', "$width", '-screen-height', "$height", '-screen-fullscreen', '0', '-logFile', "`"$log`"")
    $p = Start-Process -FilePath $Exe -ArgumentList $a -PassThru
    $null = $p.Handle
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        try { $p.Kill() } catch { }
        Fail "$($scene.name): the client did not exit in $TimeoutSec s, log: $log"
    }
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { Fail "$($scene.name): exit code $($p.ExitCode), log: $log" }
    if (-not (Test-Path $png) -or (Get-Item $png).Length -eq 0) { Fail "$($scene.name): no file $png, log: $log" }
}

$different = 0
foreach ($scene in $list) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $png = Join-Path $OutDir ($scene.name + '.png')
    Invoke-Shot $scene $png
    $note = ''
    if ($Check) {
        $again = Join-Path $logDir ($scene.name + '.again.png')
        Invoke-Shot $scene $again
        if ((Get-FileHash $png).Hash -eq (Get-FileHash $again).Hash) {
            $note = ', rerun: same'
            Remove-Item -Force $again
        } else {
            $note = ', rerun: DIFFERENT'
            $different++
        }
    }
    Write-Host ("[shots] {0,-22} {1,5:0.0} s{2}" -f $scene.name, $sw.Elapsed.TotalSeconds, $note)
}

# --- contact sheet ---------------------------------------------------------------------------
$sheet = Join-Path $OutDir 'sheet.png'
if (Test-Path $sheet) { Remove-Item -Force $sheet }
$python = Get-Command python -ErrorAction SilentlyContinue
$pillow = $false
if ($python) {
    $prevPref = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & python -c "import PIL" 2>$null
    $pillow = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevPref
}
if ($pillow) {
    & python (Join-Path $PSScriptRoot 'shots_sheet.py') $OutDir --out $sheet
    if ($LASTEXITCODE -ne 0) { Fail 'shots_sheet.py failed' }
} else {
    Add-Type -AssemblyName System.Drawing
    $files = @(Get-ChildItem $OutDir -Filter *.png | Sort-Object Name)
    $tw = 640; $th = 360; $cap = 28; $gap = 8; $cols = [Math]::Min(3, $files.Count)
    $rows = [int][Math]::Ceiling($files.Count / $cols)
    $bmp = New-Object System.Drawing.Bitmap ($gap + $cols * ($tw + $gap)), ($gap + $rows * ($th + $cap + $gap))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(24, 24, 28))
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $font = New-Object System.Drawing.Font 'Segoe UI', 12
    for ($i = 0; $i -lt $files.Count; $i++) {
        $x = $gap + ($i % $cols) * ($tw + $gap)
        $y = $gap + [int][Math]::Floor($i / $cols) * ($th + $cap + $gap)
        $img = [System.Drawing.Image]::FromFile($files[$i].FullName)
        $g.DrawImage($img, $x, $y, $tw, $th)
        $g.DrawString(("{0}  {1}x{2}" -f $files[$i].BaseName, $img.Width, $img.Height), $font, [System.Drawing.Brushes]::Gainsboro, ($x + 4), ($y + $th + 2))
        $img.Dispose()
    }
    $g.Dispose()
    $bmp.Save($sheet, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "[shots] sheet by System.Drawing: $sheet"
}

Write-Host ("[shots] done: {0} scenes, {1:0.0} s, folder {2}" -f $list.Count, $total.Elapsed.TotalSeconds, $OutDir)
if ($different -gt 0) { Fail "$different scene(s) differ between two runs (kept as logs\*.again.png)" }
exit 0
