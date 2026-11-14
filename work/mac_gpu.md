# Инструкция для ИИ: GPU-переключатель и локальная LLM на Маке

> **Актуализация 14.11:** переключатель перезапущен Автором и больше НЕ запрашивает ключ (POST /api/switch — без `Authorization`). Для Qwen API ключ (`MAC_AI_KEY`) требуется, если сервер его запрашивает. Остальное по тексту.

**Для кого:** нейросеть-агент (например, Claude Code на ПК пользователя), которой нужно работать с тяжёлыми серверами на Маке в локальной сети. Это либо локальная LLM Qwen, либо генерация ассетов (ComfyUI + AI Asset Server). Документ самодостаточный: весь нужный контекст и API описаны здесь.

---

## 1. Как устроена система

На Маке (`192.168.88.252`, Apple M4 Pro, 48 ГБ общей памяти) есть два тяжёлых сервера. **Одновременно они в память не помещаются**, поэтому в каждый момент работает только один из них. Какой именно, решает **переключатель**.

| id сервера | Что внутри | Порты | Зачем |
|---|---|---|---|
| `comfy` | ComfyUI + AI Asset Server | `8188`, `8765` | Генерация 2D-концептов и 3D-моделей. API asset server описан отдельно, в `UE5_CLIENT_SPEC.md` |
| `llm` | Qwen3.8-27B (Q8_0) в llama-server | `8080` | Локальная LLM: OpenAI-совместимый API, понимает картинки, контекст 65 536 токенов |

- **Переключатель:** `http://192.168.88.252:8090`. По корневому адресу открывается веб-интерфейс для людей, по `/api/...` доступен API для тебя.
- Переключение выключает один сервер и включает другой. Время: ComfyUI запускается за 10–30 с, Qwen загружается около минуты, но может и дольше. Предел, после которого переключатель считает запуск неудачным: 5 минут для ComfyUI, 15 минут для Qwen.
- Если порт `8090` не отвечает, значит переключатель не запущен (например, Мак перезагрузился). Попроси пользователя выполнить на Маке `cd ~/private/switcher && ./start.sh`. Сам по себе переключатель никакие серверы не запускает и не останавливает.

## 2. Ключ

- Один и тот же ключ нужен для `POST /api/switch` и для API Qwen. Передавай его в заголовке `Authorization: Bearer <KEY>`; заголовок `X-Api-Key: <KEY>` тоже принимается.
- Ключ даёт пользователь (на Маке он лежит в `~/private/llm/api-key`). Бери его из переменной окружения, например `MAC_AI_KEY`. **Не записывай ключ в код, коммиты и логи.**
- GET-запросы к переключателю (`/api/status`, `/api/history`) работают без ключа.

## 3. API переключателя

Ответы приходят в JSON (UTF-8). Тексты для людей (`message`, `busy_reason`) написаны по-русски.

### 3.1. `GET /api/status`: что работает прямо сейчас

```json
{
 "time": "2026-11-14T11:53:21+01:00",
 "active": "llm",
 "switching": null,
 "last_switch": {
  "time": "2026-11-14T11:52:54+01:00", "kind": "switch", "target": "llm", "from": "comfy",
  "by": "192.168.88.10", "force": false, "ok": true, "seconds": 41.5,
  "message": "ComfyUI выключен; Qwen включён"
 },
 "servers": {
  "comfy": {
   "state": "stopped", "up": false, "ready": false, "busy": false, "busy_reason": null,
   "title": "ComfyUI + asset server",
   "detail": {"comfyui": false, "asset_server": false, "comfy_running": 0, "comfy_pending": 0,
              "asset_running": null, "asset_pending": 0, "asset_waiting_night": 0}
  },
  "llm": {
   "state": "running", "up": true, "ready": true, "busy": false, "busy_reason": null,
   "title": "Qwen3.8-27B",
   "detail": {"health_http": 200, "processing": false}
  }
 },
 "memory_free_percent": 45
}
```

Поля верхнего уровня:

| Поле | Значение |
|---|---|
| `active` | Что запущено сейчас: `comfy`, `llm`, `off` (ничего) или `both` (оба сразу; так бывает, только если кто-то запустил их вручную в обход переключателя). |
| `switching` | `null`, если переключения нет. Иначе объект `{"target", "from", "by", "started", "seconds", "step": {"server", "action": "stop" \| "start"}}`. |
| `last_switch` | Результат последнего переключения с момента запуска переключателя. `ok: false` означает ошибку, причина записана в `message`. |
| `memory_free_percent` | Свободная память Мака в процентах. Справочное поле. |

Поля каждого сервера в `servers.<id>`:

| Поле | Значение |
|---|---|
| `state` | Одно из значений: `stopped` (выключен), `starting` (запускается), `loading` (Qwen загружает модель), `running` (работает и свободен), `busy` (работает и занят), `partial` (у `comfy` поднята только одна из двух частей), `stopping` (останавливается), `error` (отвечает, но с ошибкой). |
| `up` | Слушает ли сервер хоть один свой порт. |
| `ready` | Можно ли им пользоваться прямо сейчас. Опирайся **на это поле**. |
| `busy` / `busy_reason` | Сервер что-то выполняет, и если его выключить, работа прервётся. В `busy_reason` написано, что именно. |
| `detail` | Подробности. Для `comfy`: очереди ComfyUI и asset server; в `asset_waiting_night` лежит число финалов, которые ждут ночного окна. Для `llm`: HTTP-код `/health` и флаг генерации. |

### 3.2. `POST /api/switch`: включить один сервер или выключить оба

Нужен ключ. Тело запроса:

```json
{"target": "llm", "force": false, "wait": false}
```

- `target` принимает значения `comfy`, `llm` или `off` (выключить оба). Включение одного сервера всегда выключает другой.
- `force` (по умолчанию `false`): выключить сервер, даже если он занят. Ставь `true` **только с явного разрешения пользователя** (см. раздел 4).
- `wait` (по умолчанию `false`):
  - при `true` ответ придёт только после окончания переключения. Ставь таймаут клиента не меньше 16 минут;
  - при `false` переключение пойдёт в фоне, а за ходом следи через `GET /api/status`.

Ответы:

| Код | Когда | Тело |
|---|---|---|
| `202` | Переключение начато в фоне (`wait: false`) | `{"accepted": true, "target": ..., "switching": {...}}` |
| `200` | `wait: true`, переключение прошло успешно | `{"result": {...запись истории...}, "status": {...как /api/status...}}` |
| `500` | `wait: true`, переключение не удалось | То же; `result.ok = false`, причина в `result.message` |
| `409` + `"error": "busy"` | Сервер, который придётся выключить, занят | `{"error": "busy", "busy": {"comfy": "ComfyUI: выполняется 1, ждут 0"}, "message": "..."}` |
| `409` + `"error": "switch_in_progress"` | Уже идёт другое переключение | `{"error": "switch_in_progress", "switching": {...}}` |
| `401` | Нет ключа или ключ неверный | `{"error": "unauthorized"}` |
| `400` | Неверный `target` или битый JSON | `{"error": "bad_target"}` или `{"error": "bad_json"}` |

Если нужный сервер уже работает, а другой выключен, переключение ничего не меняет и сразу возвращает `ok: true`.

Если запуск не удался, переключатель сам выключает недозапущенный сервер. После этого `active` будет `off`.

### 3.3. `GET /api/history?limit=100`: история (сначала новые события)

```json
{"events": [
 {"time": "...", "kind": "switch", "target": "llm", "from": "comfy", "by": "192.168.88.10",
  "force": false, "ok": true, "seconds": 41.5, "message": "ComfyUI выключен; Qwen включён"},
 {"time": "...", "kind": "observed", "server": "comfy", "event": "down",
  "message": "ComfyUI пропал без переключателя"},
 {"time": "...", "kind": "switcher", "event": "started", "message": "переключатель запущен, сейчас: ComfyUI"}
]}
```

- `switch` — переключение через API или веб-интерфейс. В поле `by` записан IP того, кто его запросил.
- `observed` — сервер поднялся или упал сам, без переключателя: упал с ошибкой или его запустили вручную. Монитор проверяет это раз в 10 секунд.
- `switcher` — сам переключатель запустился.

## 4. Как правильно пользоваться (обязательный порядок)

1. **Всегда начинай с `GET /api/status`.** Не обращайся к серверу, у которого `ready` не равно `true`: соединение будет отклонено.
2. **Если нужный сервер уже `ready` и `active` совпадает с ним**, пользуйся им сразу, переключать ничего не нужно.
3. **Если `switching` не `null`**, кто-то уже переключает. Опрашивай статус раз в 3–5 секунд, пока `switching` не станет `null`, затем вернись к шагу 2.
4. **Иначе отправь `POST /api/switch`** с нужным `target`. Можно с `wait: true` и длинным таймаутом, можно без него, опрашивая статус. После переключения проверь `ok` в ответе (или `last_switch.ok` в статусе) и `ready` у нужного сервера.
5. **Ответ `409 busy`.** Сам `force` не ставь: занятый сервер делает чью-то работу. Например, ComfyUI может генерировать финальную 3D-модель (это около 45 минут), а Qwen может отвечать другому клиенту. Покажи пользователю `busy_reason` и спроси, прервать ли эту работу или подождать. `force: true` допустим только после явного «да». Если ждёшь, проверяй статус раз в 1–2 минуты.
6. **Ошибка переключения** (`500` или `ok: false`). Сообщи пользователю текст `message` (в нём есть хвост лога запуска). Повтори попытку не больше одного раза, в цикле не повторяй.
7. **Не переключай обратно сам**, когда закончил, если пользователь не просил. Другим сервером, возможно, пользуется кто-то ещё.
8. **Ночная очередь ассетов.** Финальные 3D-модели asset server делает ночью, с 01:00 до 08:00, и только когда активен `comfy`. Если ты включаешь `llm`, а у `comfy` `detail.asset_waiting_night` больше нуля, предупреди пользователя: пока Qwen включён, эти финалы не сделаются. Перед ночью стоит вернуть `comfy`, если пользователь хочет получить финалы к утру.
9. Если `active` равно `both`, памяти может не хватить. Переключись на тот сервер, который нужен: второй выключится.

## 5. Как пользоваться Qwen (когда `servers.llm.ready = true`)

- **API:** OpenAI-совместимый. `base_url = http://192.168.88.252:8080/v1`, модель `qwen3.8-27b`, ключ тот же: `Authorization: Bearer <KEY>`.
- **Эндпоинт:** `POST /v1/chat/completions`. Поддерживаются `stream: true` и картинки через `{"type": "image_url", "image_url": {"url": "data:image/png;base64,..."}}`.
- **Веб-чат для людей:** `http://192.168.88.252:8080`.
- **Лимиты:**
  - контекст 65 536 токенов на всё сразу: промпт и ответ вместе;
  - запросы обрабатываются по одному, параллельные встают в очередь;
  - скорость порядка 8–10 токенов в секунду (это оценка). Используй стриминг и таймауты от 10 минут.
- **Режим размышлений (thinking) включён по умолчанию.**
  - Размышления приходят отдельным полем `message.reasoning_content` (в стриме это `delta.reasoning_content`), а сам ответ лежит в `message.content`.
  - Чтобы модель отвечала сразу, без размышлений, добавь в тело запроса `"chat_template_kwargs": {"enable_thinking": false}`.
- **Рекомендованные параметры сэмплинга** (по карточке модели):
  - с размышлениями: `temperature 1.0, top_p 0.95, top_k 20, min_p 0, presence_penalty 0`. Это значения по умолчанию на сервере;
  - без размышлений: `temperature 0.7, top_p 0.8, top_k 20, min_p 0, presence_penalty 1.5`.

## 6. Как пользоваться генерацией ассетов (когда `servers.comfy.ready = true`)

- AI Asset Server: `http://192.168.88.252:8765`. Его API описан в `UE5_CLIENT_SPEC.md`, там же цены по времени на черновик и финал.
- ComfyUI: `http://192.168.88.252:8188`. Напрямую к нему обычно обращаться не нужно, asset server делает это сам.

## 7. Примеры

### curl

```bash
# статус
curl -s http://192.168.88.252:8090/api/status

# включить Qwen и дождаться конца
curl -s -X POST http://192.168.88.252:8090/api/switch \
  -H "Authorization: Bearer $MAC_AI_KEY" -H "Content-Type: application/json" \
  -d '{"target": "llm", "wait": true}' --max-time 1000

# выключить оба
curl -s -X POST http://192.168.88.252:8090/api/switch \
  -H "Authorization: Bearer $MAC_AI_KEY" -d '{"target": "off"}'

# история
curl -s "http://192.168.88.252:8090/api/history?limit=20"

# запрос к Qwen (когда он включён)
curl -s http://192.168.88.252:8080/v1/chat/completions \
  -H "Authorization: Bearer $MAC_AI_KEY" -H "Content-Type: application/json" \
  -d '{"model": "qwen3.8-27b", "messages": [{"role": "user", "content": "Привет!"}],
       "chat_template_kwargs": {"enable_thinking": false}}'
```

### Python (только стандартная библиотека)

```python
import json
import os
import time
import urllib.error
import urllib.request

MAC = "http://192.168.88.252"
KEY = os.environ["MAC_AI_KEY"]


def switcher(method, path, body=None, timeout=30):
    req = urllib.request.Request(
        f"{MAC}:8090{path}", method=method,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Authorization": f"Bearer {KEY}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, json.load(r)
    except urllib.error.HTTPError as e:
        return e.code, json.load(e)


class Busy(Exception):
    """The server that would be stopped is working; ask the user before retrying with force=True."""


def ensure(target, force=False):
    """Make `target` ('comfy' | 'llm' | 'off') the only running server and return the status."""
    while True:
        _, st = switcher("GET", "/api/status")
        if st["switching"] is None:
            break
        time.sleep(5)
    if st["active"] == target and (target == "off" or st["servers"][target]["ready"]):
        return st
    code, body = switcher("POST", "/api/switch", {"target": target, "force": force, "wait": True}, timeout=1000)
    if code == 409 and body.get("error") == "busy":
        raise Busy(body["message"])
    if code != 200:
        raise RuntimeError(body.get("result", {}).get("message") or body)
    return body["status"]


def ask_qwen(prompt, think=False):
    ensure("llm")
    req = urllib.request.Request(
        f"{MAC}:8080/v1/chat/completions",
        data=json.dumps({"model": "qwen3.8-27b",
                         "messages": [{"role": "user", "content": prompt}],
                         "chat_template_kwargs": {"enable_thinking": think}}).encode(),
        headers={"Authorization": f"Bearer {KEY}", "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=900) as r:
        return json.load(r)["choices"][0]["message"]["content"]
```

Если вылетело `Busy`, покажи пользователю `message` и спроси, прервать ли работу (`ensure(target, force=True)`) или подождать.
