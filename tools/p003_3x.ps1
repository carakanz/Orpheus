# P-003: 3 progonov -All podryad. Pered kazhdim: pitanie ot seti (AC) i svobodnyj CPU.
# Padenie - ostanavlivajemsya (razbir v karto4ke). Faily: build\logs\all_p003_r1..r3.txt
$ErrorActionPreference = "Stop"
$root = "C:\Users\carak\source\Orpheus\.claude\worktrees\tp003"
$log = Join-Path $root "build\logs"

function Test-Ac {
  $b = Get-WmiObject Win32_Battery -ErrorAction SilentlyContinue
  if (-not $b) { return $true }   # net bataree - schitaem set'
  return $b.BatteryStatus -eq 2
}
function Get-Load {
  $p = Get-CimInstance win32_processor -ErrorAction SilentlyContinue | Measure-Object -Property LoadPercentage -Average
  if ($p) { return $p.Average }
  return 100
}

for ($i = 1; $i -le 3) {
  $tag = "r$i"
  "gate ${tag}: waiting for AC..." | Out-File (Join-Path $log "p003_gate.txt") -Append -Encoding utf8
  while (-not (Test-Ac)) { Start-Sleep 300 }
  while ((Get-Load) -ge 20) { Start-Sleep 120 }
  Start-Sleep 60
  "gate ${tag}: AC ok, load ok, starting" | Out-File (Join-Path $log "p003_gate.txt") -Append -Encoding utf8
  & "$root\tools\ci.ps1" -Configuration Release -All -TimeLimitSec 0 -LogFile (Join-Path $log "all_p003_${tag}.txt")
  if ($LASTEXITCODE -ne 0) {
    "run $i FAILED (code $LASTEXITCODE)" | Out-File (Join-Path $log "p003_3x_stop.txt") -Encoding utf8
    break
  }
}
"p003_3x finished $(Get-Date -Format 'dd.MM HH:mm')" | Out-File (Join-Path $log "p003_3x_done.txt") -Encoding utf8
