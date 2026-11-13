# opencode watchdog for the Orpheus thread (ASCII only: Windows PowerShell 5.1 reads BOM-less files as ANSI).
# Run once: it looks at the opencode session and, if the session has been idle too long for no good reason,
# sends the "continue" prompt (tools/opencode_watchdog_prompt.txt). Meant to be run every 10 minutes by Task Scheduler.
#
#   powershell -ExecutionPolicy Bypass -File tools\opencode_watchdog.ps1 -DryRun     # look, log, send nothing
#   powershell -ExecutionPolicy Bypass -File tools\opencode_watchdog.ps1             # act
#
# It does NOT nudge when:
#   - the file build\logs\watchdog.off exists (manual off switch);
#   - the session is busy, or idle for less than -IdleMinutes (background test runs take up to ~15 min);
#   - the last turn was interrupted (the Author pressed stop) - a manual stop is respected;
#   - the model server (Strata, port 8080) does not answer - nothing to continue with;
#   - the last -MaxNudges nudges produced no new commits in the repository (the agent is stuck - needs a human).
param(
    [string] $Session = 'ses_e539406a3ffdalSP0DbtSx47hd',
    [int] $IdleMinutes = 25,
    [int] $MaxNudges = 3,
    [string] $Repo = (Split-Path -Parent $PSScriptRoot),
    [switch] $DryRun
)
$ErrorActionPreference = 'Stop'
$logDir = Join-Path $Repo 'build\logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$logFile = Join-Path $logDir 'opencode_watchdog.log'
$stateFile = Join-Path $logDir 'opencode_watchdog.state.json'
$offFile = Join-Path $logDir 'watchdog.off'
function Log([string] $msg) {
    $line = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $msg
    Add-Content -Path $logFile -Value $line -Encoding UTF8
    Write-Output $line
}
function Commits { try { [int](& git -C $Repo rev-list --all --count 2>$null) } catch { -1 } }

if (Test-Path $offFile) { Log 'off switch present - nothing to do'; exit 0 }

$oc = Get-CimInstance Win32_Process -Filter "Name='opencode-cli.exe'" | Select-Object -First 1
if (-not $oc) { Log 'opencode-cli is not running - nothing to do'; exit 0 }
$port = (Get-NetTCPConnection -OwningProcess $oc.ProcessId -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).LocalPort
if (-not $port) { Log 'opencode-cli has no listening port'; exit 0 }
$pw = (Get-Content (Join-Path $env:USERPROFILE '.config\opencode\service.json') -Raw -Encoding UTF8 | ConvertFrom-Json).password
$hdr = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("opencode:$pw")) }
$base = "http://127.0.0.1:$port/api/session/$Session"

try { $last = (Invoke-RestMethod -Uri "$base/message?limit=1" -Headers $hdr -TimeoutSec 20).data | Select-Object -First 1 }
catch { Log ("cannot read the session: " + $_.Exception.Message); exit 0 }
if (-not $last) { Log 'session has no messages'; exit 0 }

$now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$idleMin = [math]::Round(($now - [long]$last.time.created) / 60000, 1)
if ($last.type -ne 'idle') { Log "session is working (last message: $($last.type), $idleMin min ago)"; exit 0 }
$outcome = [string]$last.outcome
if ($outcome -eq 'interrupted') { Log "idle $idleMin min after a manual stop - not touching"; exit 0 }
if ($idleMin -lt $IdleMinutes) { Log "idle $idleMin min (outcome: $outcome) - waiting until $IdleMinutes"; exit 0 }

# Is the model server alive? Any HTTP answer (even 401) means yes; connection refused means no.
$modelUp = $false
try { Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:8080/health' -TimeoutSec 5 | Out-Null; $modelUp = $true }
catch { if ($_.Exception.Response) { $modelUp = $true } }
if (-not $modelUp) { Log "idle $idleMin min, but the model server (8080) is down - not nudging"; exit 0 }

$state = @{ nudges = 0; commits = -1 }
if (Test-Path $stateFile) { try { $s = Get-Content $stateFile -Raw | ConvertFrom-Json; $state.nudges = [int]$s.nudges; $state.commits = [int]$s.commits } catch { } }
$commits = Commits
if ($commits -ne $state.commits) { $state.nudges = 0 }   # progress since the last nudge - reset the counter
if ($state.nudges -ge $MaxNudges) { Log "idle $idleMin min; $($state.nudges) nudges gave no new commits - stuck, needs the Author (delete $stateFile to re-arm)"; exit 0 }

$text = (Get-Content (Join-Path $PSScriptRoot 'opencode_watchdog_prompt.txt') -Raw -Encoding UTF8).Replace('{IDLE}', [string][int]$idleMin).Replace('{OUTCOME}', $outcome)
if ($DryRun) { Log "[dry] idle $idleMin min (outcome: $outcome) - would send the continue prompt (nudge $($state.nudges + 1) of $MaxNudges)"; exit 0 }
try {
    $body = [Text.Encoding]::UTF8.GetBytes((@{ text = $text } | ConvertTo-Json -Compress))
    Invoke-RestMethod -Method Post -Uri "$base/prompt" -Headers $hdr -Body $body -ContentType 'application/json; charset=utf-8' -TimeoutSec 120 | Out-Null
    $state.nudges = $state.nudges + 1; $state.commits = $commits
    ($state | ConvertTo-Json -Compress) | Set-Content -Path $stateFile -Encoding UTF8
    Log "idle $idleMin min (outcome: $outcome) - continue prompt sent (nudge $($state.nudges) of $MaxNudges)"
}
catch { Log ("sending failed: " + $_.Exception.Message) }
