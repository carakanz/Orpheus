# Промежуточная playable-сборка Orpheus: прогоняет мир с ботами, кооп по UDP и рисует кадры.
# Запуск: tools\playable.cmd (из корня репозитория). Результат — в playable\: кадры .bmp и логи.
# Шаги: 1) сервер 600 тактов (4 бота) -> кадр A; 2) догрузка из сохранения + 12 000 тактов -> кадр B;
# 3) сервер в реальном времени + 3 UDP-игрока 15 с -> кадр C. Всё на сгенерированном мире (seed фиксирован).
# Любой упавший шаг (сервер, View, боты) -> выход с его кодом (не 0). Кооп-сервер слушает только петлю, порт
# выдаёт ОС (ключ --port 0, строка готовности orpheus-ready) — две рабочие копии не сталкиваются; зависший
# сервер снимается по своему номеру процесса (0-024, backlog [инструменты] по проверке 0-012).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$out = Join-Path $root 'playable'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$out\world" | Out-Null
New-Item -ItemType Directory -Path "$out\net" | Out-Null

# Шаг упал -> сообщение и выход с его кодом (0 у упавшего шага не бывает: тогда 1).
function Assert-Step([string]$name, $code) {
    if ($null -eq $code -or $code -ne 0) {
        if ($null -eq $code -or $code -eq 0) { $code = 1 }
        Write-Host "== $name — ОШИБКА (код $code)" -ForegroundColor Red
        exit $code
    }
}

Write-Host '== сборка (Release)'
dotnet build .\Orpheus.sln -c Release --nologo -v minimal | Out-Null
Assert-Step 'сборка' $LASTEXITCODE

Write-Host '== запуск 1: создание мира, 600 тактов, 4 бота'
dotnet run --project src\Orpheus.Server -c Release --no-build -- `
  --world "$out\world" --create --seed 202609 --size 1024 --sim --bots 4:wander `
  --ticks 600 --autosave 1 --logs "$out\logs" | Out-Null
Assert-Step 'сервер, запуск 1' $LASTEXITCODE
dotnet run --project tools\Orpheus.View -c Release --no-build -- --world "$out\world" --out "$out\frame-a.bmp"
Assert-Step 'кадр A' $LASTEXITCODE

Write-Host '== запуск 2: догрузка из сохранения, 12 000 тактов'
dotnet run --project src\Orpheus.Server -c Release --no-build -- `
  --world "$out\world" --sim --bots 4:wander --ticks 12000 --autosave 30 --logs "$out\logs" | Out-Null
Assert-Step 'сервер, запуск 2' $LASTEXITCODE
dotnet run --project tools\Orpheus.View -c Release --no-build -- --world "$out\world" --out "$out\frame-b.bmp"
Assert-Step 'кадр B' $LASTEXITCODE

Write-Host '== кооп по UDP: сервер (реальное время, только петля, свободный порт) + 3 игрока, 15 с'
# Сам OrpheusServer.exe, а не dotnet run: номер процесса — сервера, его и снимаем, если завис.
$serverExe = Join-Path $root 'src\Orpheus.Server\bin\Release\net9.0\OrpheusServer.exe'
$serverOut = Join-Path $out 'server-stdout.txt'
$server = Start-Process -FilePath $serverExe -PassThru -WindowStyle Hidden `
  -RedirectStandardOutput $serverOut -RedirectStandardError (Join-Path $out 'server-stderr.txt') `
  -ArgumentList @('--world', "`"$out\net`"", '--create', '--seed', '777', '--size', '1024', '--bots', '2:wander',
                  '--port', '0', '--ticks', '700', '--autosave', '1', '--logs', "`"$out\netlogs`"")
$null = $server.Handle # без этого PowerShell 5.1 теряет код выхода процесса из Start-Process
try {
    # Ждать строку готовности с портом (сервер сам сообщает выбранный ОС порт), а не паузу «на всякий случай».
    $port = $null
    $wait = [System.Diagnostics.Stopwatch]::StartNew()
    while ($null -eq $port) {
        if ($server.HasExited) { Assert-Step 'кооп-сервер вышел до готовности' $server.ExitCode }
        if ($wait.Elapsed.TotalSeconds -gt 30) { Write-Host '== кооп-сервер не готов за 30 с' -ForegroundColor Red; exit 4 }
        $line = Get-Content $serverOut -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -like 'orpheus-ready port=*' } | Select-Object -First 1
        if ($line) { $port = [int]($line.Substring('orpheus-ready port='.Length)) } else { Start-Sleep -Milliseconds 100 }
    }
    Write-Host "   сервер готов за $([math]::Round($wait.Elapsed.TotalSeconds, 1)) с, порт $port"

    dotnet run --project src\Orpheus.Bots -c Release --no-build -- --connect "127.0.0.1:$port" --count 3 --seed 7 --seconds 15 --report "$out\bots-report.json"
    Assert-Step 'боты' $LASTEXITCODE

    if (-not $server.WaitForExit(120000)) {
        Write-Host '== кооп-сервер не завершился за 120 с — снимаю' -ForegroundColor Red
        exit 5
    }
    Assert-Step 'кооп-сервер' $server.ExitCode
}
finally {
    if (-not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
}
dotnet run --project tools\Orpheus.View -c Release --no-build -- --world "$out\net" --out "$out\frame-c.bmp"
Assert-Step 'кадр C' $LASTEXITCODE

Write-Host ''
Write-Host 'готово: playable\frame-a.bmp (такт 600), frame-b.bmp (после загрузки, такт 12 600), frame-c.bmp (кооп-сессия)'
exit 0
