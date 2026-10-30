# Orpheus

2D-песочница сверху в духе Terraria по легенде об Орфее: кооператив до 8 игроков на планете-торе.
Клиент — Unity 6, ядро игры — своя библиотека C#, совместимая с IL2CPP (одна DLL для Unity, сервера и тестов).

- Дизайн-документы — [`docs/README.md`](docs/README.md) (источник правды; техника — `docs/07_multiplayer_tech.md`, план — `docs/08_roadmap.md`).
- Правила работы в репозитории — [`CLAUDE.md`](CLAUDE.md); карточки задач — `work/tasks/`.

## Что где

| Путь | Что | Сборка |
|---|---|---|
| `src/Orpheus.Core` | `Torus` (тор, координаты, чанки), мир, правила | `netstandard2.1`, C# 9 |
| `src/Orpheus.WorldGen` | генератор мира | `netstandard2.1`, C# 9 |
| `src/Orpheus.Net` | протокол, снимки, транспорт | `netstandard2.1`, C# 9 |
| `src/Orpheus.Save` | сохранения | `netstandard2.1`, C# 9 |
| `src/Orpheus.Server` | выделенный сервер `OrpheusServer` (пока цикл тактов без сети) | `net9.0` |
| `src/Orpheus.Bots` | боты без графики (пока пусто) | `net9.0` |
| `tests/Orpheus.Tests` | тесты ядра (xUnit) | `net9.0` |
| `client/` | проект Unity | Unity 6 |
| `tools/` | скрипты разработки | |

Общие настройки проектов — `Directory.Build.props`: C# 9, `Nullable`, предупреждения как ошибки.
В ядре нет Unity, `System.Text.Json`, рефлексии и генерации кода — это проверяют тесты `D00_Core_*`.
По `docs/07` сервер, боты и тесты — `net10.0`; пока на машинах .NET SDK 9, они собираются под `net9.0`.

## Сборка и тесты

Нужен .NET SDK 9 (или новее).

```powershell
powershell -File tools/ci.ps1        # сборка + все тесты; код выхода ≠ 0 при ошибке, лимит 60 с
dotnet build Orpheus.sln             # только сборка
dotnet test Orpheus.sln              # только тесты
dotnet test Orpheus.sln --filter D00_Torus   # тесты тора
dotnet run --project src/Orpheus.Server -- --ticks 90   # сервер: 90 тактов (3 с) и выход
```
