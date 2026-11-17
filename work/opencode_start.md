# Старт для opencode — вечер 17.11.2026

C-023 ч. 2 (merge `4bd8eb8`), C-024, P-006, P-003, C-025, C-026 (merge `e41592a`, журналы `6efc8a9`) — всё слито. main = `80914c9`. Очередь плана (`work/next.md`) пуста: этап 2 закрыт, остался только **пункт 2.8 — приёмка этапа Автором** (30 минут игры одному). **Сборка Mono с main готова**: `build\client-win\Orpheus.exe` (Succeeded, 0 ошибок, лог `build\logs\build_main_28a.log`; Unity Hub поднят вручную, Store `!UnityHub`).

## Обязательно
- Код — только в ветке `task/…` и копии `.claude\worktrees\…`; `main` — merge `--no-ff -F`. После слияния — сносить копию/ветку (`git worktree remove` + ветку с origin).
- Тяжёлые тесты (D08_3_1, NightBots, Big, `ci.ps1 -All`) — не гонять. Быстрый CI — `tools\ci.ps1 -TimeLimitSec 0` (~150 с, 1238 тестов; дефолт 60 с — легасюк).
- Снимки/запуски игры — только `tools\run_hidden.ps1` / `tools\shots.ps1` (скрытый стол, монитор 2).
- Перед сборкой Unity: Hub запущен (Store-пакет `UnityTechnologies.UnityHub_2vrhnee42bhxm!UnityHub`, иначе код 198), диск C: ≥ 5 ГБ, один Unity на копию.
- Даты — сегодня 17.11.2026.
- Каждый шаг — коммит (`git commit -F`, UTF-8 без BOM, LF, по-русски, строка `Co-Authored-By: Qwen (локально, OpenCode)`) и `git push`. «Место остановки» — в карточке.
- Мак (192.168.88.252) оффлайн (ping/TCP 8090/8080 не отвечают, 17.11 вечер). Reviewer — локальная модель `local/swift-1.5-iq3_xxs` (годится, запись в next.md 17.11).
- Бытовая мелочь: `.claude\worktrees\tc026` — пустой каталог, держит чужой процесс (CWD-ручка); git его не знает (`worktree prune` сделан). Снесётся после перезагрузки, на диск не влияет.
- Всё сделал или упёрся — ответь одной строкой `ЖДУ АВТОРА: <что именно>`.
