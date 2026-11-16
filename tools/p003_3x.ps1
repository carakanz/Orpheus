# P-003: 3 progonov -All podryad. Bez gejta svobodnoj mashiny (slovo Avtora 14.11:
# "pus' gonyayutsya i na nagruzhennoj mashine"). Padeniya v progone - tolko iz work/flaky.md.
$root = $PSScriptRoot | Split-Path -Parent
$ErrorActionPreference = "Stop"
$log = Join-Path $root "build\logs"

Remove-Item (Join-Path $log "p003_3x_stop.txt"),(Join-Path $log "p003_3x_done.txt") -ErrorAction SilentlyContinue
for ($i = 1; $i -le 3; $i++) {
  "round r$i starting $(Get-Date -Format 'dd.MM HH:mm')" | Out-File (Join-Path $log "p003_gate.txt") -Append -Encoding utf8
  & "$root\tools\ci.ps1" -Configuration Release -All -TimeLimitSec 0 -LogFile (Join-Path $log "all_p003_r$i.txt")
  if ($LASTEXITCODE -ne 0) {
    "run $i FAILED (code $LASTEXITCODE)" | Out-File (Join-Path $log "p003_3x_stop.txt") -Encoding utf8
    break
  }
}
"p003_3x finished $(Get-Date -Format 'dd.MM HH:mm')" | Out-File (Join-Path $log "p003_3x_done.txt") -Encoding utf8
