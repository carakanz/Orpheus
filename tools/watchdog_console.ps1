# Сторож Orpheus в открытой консоли: раз в 10 минут проверяет тред OpenCode и пишет, что видит.
# Закрыть окно (или Ctrl+C) — сторож выключен. Подталкивает тред сам opencode_watchdog.ps1.
param([int] $EveryMinutes = 10)
$Host.UI.RawUI.WindowTitle = 'Сторож Orpheus — работает (закрыть окно = выключить)'
schtasks /delete /tn "Orpheus opencode watchdog" /f 2>$null | Out-Null   # чтобы не было двух сторожей сразу
$off = 'C:\Users\carak\source\Orpheus\build\logs\watchdog.off'
if (Test-Path $off) { Remove-Item $off -Force }
Write-Host 'Сторож Orpheus запущен.' -ForegroundColor Green
Write-Host "Каждые $EveryMinutes мин смотрю тред Orpheus в OpenCode. Если он простаивает больше 25 мин — отправляю «продолжай»."
Write-Host 'Не трогаю: после ручного «стоп», если модель (порт 8080) не отвечает, если 3 подталкивания подряд не дали коммитов.'
Write-Host 'Выключить — закрыть это окно.' -ForegroundColor Yellow
Write-Host ''
while ($true) {
    $stamp = Get-Date -Format 'HH:mm'
    $line = & powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\Users\carak\source\Orpheus\tools\opencode_watchdog.ps1' 2>&1 | Select-Object -Last 1
    $t = [string]$line
    $ru = $t
    $ru = $ru -replace 'session is working \(last message: (\w+), ([\d.,]+) min ago\)', 'тред работает (последнее сообщение $2 мин назад)'
    $ru = $ru -replace 'idle ([\d.,]+) min \(outcome: (\w+)\) - waiting until (\d+)', 'тред ждёт $1 мин — подтолкну после $3 мин'
    $ru = $ru -replace 'idle ([\d.,]+) min after a manual stop - not touching', 'тред остановлен вручную $1 мин назад — не трогаю'
    $ru = $ru -replace 'idle ([\d.,]+) min, but the model server \(8080\) is down - not nudging', 'тред ждёт $1 мин, но модель (8080) не отвечает — не подталкиваю'
    $ru = $ru -replace 'idle ([\d.,]+) min \(outcome: (\w+)\) - continue prompt sent \(nudge (\d+) of (\d+)\)', 'тред простаивал $1 мин — отправил «продолжай» ($3 из $4)'
    $ru = $ru -replace 'idle ([\d.,]+) min; (\d+) nudges gave no new commits - stuck.*', 'тред простаивает $1 мин; $2 подталкивания без коммитов — застрял, нужен Автор'
    $ru = $ru -replace 'opencode-cli is not running - nothing to do', 'OpenCode не запущен — нечего делать'
    $color = 'Gray'
    if ($t -match 'nudge|sent|SENT') { $color = 'Cyan' }
    if ($t -match 'down|stuck|cannot|not running') { $color = 'Red' }
    if ($t -match 'working') { $color = 'Green' }
    Write-Host ($stamp + '  ' + ($ru -replace '^\S+ \S+  ','')) -ForegroundColor $color
    Start-Sleep -Seconds ($EveryMinutes * 60)
}
