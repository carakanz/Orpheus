# Атлас Орфея — мини-вики для разбора Автором

Биомы (с условной картой), враги, боссы, архетипы, руды и предметы со ссылками друг на друга. На каждой странице Автор ставит «Принято», «Нужна правка» или «Убрать» и пишет комментарий. Работает без интернета: внешних шрифтов и CDN нет.

## Файлы
- `index.html` — вся вики (страница + скрипт).
- `catalog.json` — данные вики; собирается из `src/*.json` скриптом `src/merge.py`.
- `src/` — исходные таблицы (из `docs/` и кода на 16.11.2026; «proposal» — предложения, где docs молчат) и `merge.py`.
- `server.py` — сервер на стандартной библиотеке Python: статика и API отметок.
- `Dockerfile` — образ `python:3.12-slim`, порт 8095, отметки в томе `/data`.

## Запуск
```
python wiki/server.py --host 0.0.0.0 --port 8095 --data <папка для notes.json>
```
Docker:
```
docker build -t orpheus-wiki wiki
docker run -d --name orpheus-wiki -p 8095:8095 -v orpheus-wiki-data:/data --restart unless-stopped orpheus-wiki
```

## Отметки Автора
`GET /api/notes` → `{id: {status, comment, title, kind, updatedAt}}`; `POST /api/notes` с `{id, status?, comment?, title?, kind?}` сливает поля в запись. `status`: `accepted` | `changes` | `rejected` | пусто. Id страницы: `biome-forest`, `enemy-wolf`, `boss-cerberus`, `arch-melee`, `ore-copper_ore`, `item-iron_pickaxe`, `notes-all`.

Файл `notes.json` пишется атомарно (временный файл, fsync, `os.replace`, fsync папки) — в репозиторий не попадает (`.gitignore`). Комментарии Автора переносятся в `BRIEF.md` и карточки `work/tasks/`.

## Обновить данные
Поправить `src/*.json` (или `src/merge.py`) и выполнить `python wiki/src/merge.py` — перепишет `catalog.json`. Отметки привязаны к id страниц, поэтому id не менять.
