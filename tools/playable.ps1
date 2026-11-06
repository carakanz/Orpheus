# Промежуточная playable-сборка Orpheus: прогоняет мир с ботами, кооп по UDP и рисует кадры.
# Запуск: tools\playable.cmd (из корня репозитория). Результат — в playable\: кадры .bmp и логи.
# Шаги: 1) сервер 600 тактов (4 бота) -> кадр A; 2) догрузка из сохранения + 12 000 тактов -> кадр B;
# 3) сервер в реальном времени + 3 UDP-игрока 15 с -> кадр C. Всё на сгенерированном мире (seed фиксирован).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$out = Join-Path $root 'playable'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$out\world" | Out-Null
New-Item -ItemType Directory -Path "$out\net" | Out-Null

Write-Host '== сборка (Release)'
dotnet build .\Orpheus.sln -c Release --nologo -v minimal | Out-Null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host '== запуск 1: создание мира, 600 тактов, 4 бота'
dotnet run --project src\Orpheus.Server -c Release --no-build -- `
  --world "$out\world" --create --seed 202609 --size 1024 --sim --bots 4:wander `
  --ticks 600 --autosave 1 --logs "$out\logs" | Out-Null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project tools\Orpheus.View -c Release --no-build -- --world "$out\world" --out "$out\frame-a.bmp"

Write-Host '== запуск 2: догрузка из сохранения, 12 000 тактов'
dotnet run --project src\Orpheus.Server -c Release --no-build -- `
  --world "$out\world" --sim --bots 4:wander --ticks 12000 --autosave 30 --logs "$out\logs" | Out-Null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project tools\Orpheus.View -c Release --no-build -- --world "$out\world" --out "$out\frame-b.bmp"

Write-Host '== кооп по UDP: сервер (реальное время) + 3 игрока, 15 с'
$server = Start-Process -FilePath 'dotnet' -ArgumentList `
  "run --project src\Orpheus.Server -c Release --no-build -- --world `"$out\net`" --create --seed 777 --size 1024 --bots 2:wander --port 20000 --ticks 700 --autosave 1 --logs `"$out\netlogs`"" `
  -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 6
dotnet run --project src\Orpheus.Bots -c Release --no-build -- --connect 127.0.0.1:20000 --count 3 --seed 7 --seconds 15 --report "$out\bots-report.json"
Wait-Process -Id $server.Id -Timeout 120 -ErrorAction SilentlyContinue
dotnet run --project tools\Orpheus.View -c Release --no-build -- --world "$out\net" --out "$out\frame-c.bmp"

Write-Host ''
Write-Host 'готово: playable\frame-a.bmp (такт 600), frame-b.bmp (после загрузки, такт 12 600), frame-c.bmp (кооп-сессия)'
