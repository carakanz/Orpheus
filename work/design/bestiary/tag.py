"""D-001 шаг 3: разметка кандидатов на Мак-Qwen (qwen3.8-27b, :8080). Вход candidates.json, выход tagged.json.
Прогресс сохраняется после каждого батча; повторный запуск продолжает с места (уже размеченные id пропускаются).
Ключ — только из переменной окружения MAC_AI_KEY (в файлы и журнал не пишется).
  python tag.py [--batch 8] [--limit N] [--only id1,id2]"""
import json, os, re, sys, time, argparse, urllib.request, urllib.error
D = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(D)))
URL = 'http://192.168.88.252:8080/v1/chat/completions'
OUT = os.path.join(D, 'tagged.json')
LOG = os.path.join(D, 'raw', 'tag.log')

cat = json.load(open(os.path.join(ROOT, 'wiki', 'catalog.json'), encoding='utf-8'))
BIOMES = {b['id']: b['name'] for b in cat['biomes']}
ARCH = {a['id']: a['name'] for a in cat['archetypes']}
ROLES = ['стая', 'одиночка', 'засада', 'танк', 'стрелок', 'заклинатель', 'призыватель', 'летун', 'роющий', 'пловец',
         'поддержка', 'взрывник', 'ловец', 'мерцающий', 'вожак', 'страж', 'вор', 'рой', 'прилипала', 'чаровник']
TIERS = ['1', '2', '3', 'элита', 'босс']

STYLE = ('clean hand-drawn 2D game illustration, bold confident dark plum-brown outlines of even weight around every shape, '
         'flat color fills with simple two-step cel shading and one crisp highlight, strong readable silhouette, graphic simplified shapes, '
         'bright saturated Mediterranean palette of turquoise, leaf green, gold and warm red, smooth vector-like finish')
CAMERA = ('high overhead three-quarter view, top-down game camera tilted at about 45 degrees looking down at the subject like in a '
          'top-down survival crafting game, a single subject centered in the frame, the whole subject fully visible with empty margin '
          'around it, nothing cropped, key light from the upper left at 45 degrees, soft cast shadow falling toward the lower right, '
          'isolated on a plain flat very light warm grey background, no ground scenery, no other objects, ancient Greek mythological world, '
          'no text, no letters, no watermark, no border, no frame')

SYSTEM = f"""Ты геймдизайнер игры «Орфей»: 2D-песочница с видом сверху (как Terraria/Factorio), мир — древнегреческий миф об Орфее, мир-тор из биомов, три акта, ночью выходят агрессивные враги, днём спокойнее.
Тебе дают существ (только факты: имя, тип, размер, уровень опасности CR, среда). Для каждого предложи, каким врагом (мобом) он станет у нас.

Биомы (id: имя): {json.dumps(BIOMES, ensure_ascii=False)}
Архетипы поведения (id: имя): {json.dumps(ARCH, ensure_ascii=False)}. Если ни один не подходит — новый id латиницей с префиксом new_ (например new_charger) и коротким именем в archetype_name.
Роли: {', '.join(ROLES)} (можно 1–3).
Акт и ярус по CR (подсказка, можно сдвинуть на один шаг с причиной): CR 0–2 → акт I, ярус 1; CR 3–6 → акт II, ярус 2; CR 7–12 → акт III, ярус 3; редкий сильный враг между обычным и мини-боссом → «элита»; CR 13+ или легенда (дракон, кракен, гидра) → «босс» (act — где встречается).
size_tiles — сколько клеток (тайлов 1×1) занимает спрайт: крошечный 0.5, маленький/средний 1, большой 2, огромный 3, громадный 4–6.

Правила текста:
- description — СВОИ 2–3 предложения по-русски: как этот враг выглядит в нашем мире и как с ним драться. НЕ пересказывай D&D и его тексты, не упоминай D&D, планы, богов D&D. Можно привязать к греческому мифу.
- image_subject — по-английски, 20–45 слов: только сам персонаж — форма крупными частями, материал, 3–5 деталей, поза, «full body», без стиля, камеры и фона.
- night_only: true, если враг выходит только ночью.

Ответ — ТОЛЬКО JSON-массив, по объекту на существо, в том же порядке:
[{{"id": "...", "biomes": ["forest"], "act": "I|II|III", "tier": "1|2|3|элита|босс", "archetype": "melee", "archetype_name": "", "role": ["стая"], "night_only": false, "size_tiles": 1, "description": "...", "image_subject": "..."}}]"""


def log(*a):
    with open(LOG, 'a', encoding='utf-8') as f: f.write(time.strftime('%H:%M:%S ') + ' '.join(map(str, a)) + '\n')


def ask(items):
    facts = [{'id': c['id'], 'имя': c['rus'], 'eng': c['eng'], 'тип': c['type'], 'размер': ', '.join(c['size']) or '?',
              'CR': c['cr'], 'среда': ', '.join(c['habitats']) or '?'} for c in items]
    body = {'model': 'qwen3.8-27b', 'temperature': 0.7, 'top_p': 0.8, 'max_tokens': 450 * len(items) + 200,
            'chat_template_kwargs': {'enable_thinking': False},
            'messages': [{'role': 'system', 'content': SYSTEM},
                         {'role': 'user', 'content': 'Существа:\n' + json.dumps(facts, ensure_ascii=False, indent=0)}]}
    req = urllib.request.Request(URL, data=json.dumps(body).encode('utf-8'), headers={
        'Content-Type': 'application/json', 'Authorization': 'Bearer ' + os.environ['MAC_AI_KEY']})
    t = time.time()
    with urllib.request.urlopen(req, timeout=1800) as r: res = json.load(r)
    txt = res['choices'][0]['message'].get('content') or ''
    log('batch', len(items), 'sec', round(time.time() - t), 'usage', res.get('usage'))
    m = re.search(r'\[.*\]', txt, re.S)
    if not m: raise ValueError('нет JSON-массива: ' + txt[:200])
    return json.loads(m.group(0))


def check(c, r):
    errs = []
    bad_b = [b for b in r.get('biomes', []) if b not in BIOMES]
    if bad_b or not r.get('biomes'): errs.append(f'biomes {bad_b}')
    if r.get('act') not in ('I', 'II', 'III'): errs.append('act')
    if str(r.get('tier')) not in TIERS: errs.append('tier')
    a = r.get('archetype', '')
    if a not in ARCH and not str(a).startswith('new_'): errs.append('archetype')
    if not isinstance(r.get('role'), list) or not r['role']: errs.append('role')
    d = r.get('description', '')
    if not (60 <= len(d) <= 600) or re.search(r'D&D|Подземь|план[аеуы]? ', d): errs.append('description')
    s = r.get('image_subject', '')
    if not (60 <= len(s) <= 500) or re.search(r'[а-яё]', s, re.I): errs.append('image_subject')
    try: float(r.get('size_tiles'))
    except (TypeError, ValueError): errs.append('size_tiles')
    return errs


def main():
    ap = argparse.ArgumentParser(); ap.add_argument('--batch', type=int, default=8); ap.add_argument('--limit', type=int, default=0)
    ap.add_argument('--only', default='')
    a = ap.parse_args()
    cands = json.load(open(os.path.join(D, 'candidates.json'), encoding='utf-8'))
    done = json.load(open(OUT, encoding='utf-8')) if os.path.exists(OUT) else {'model': 'qwen3.8-27b', 'items': {}, 'failed': {}}
    todo = [c for c in cands if c['id'] not in done['items']]
    if a.only: todo = [c for c in todo if c['id'] in a.only.split(',')]
    if a.limit: todo = todo[:a.limit]
    log('start', len(todo), 'осталось из', len(cands))
    queue = [todo[i:i + a.batch] for i in range(0, len(todo), a.batch)]
    conn_err = 0
    while queue:
        batch = queue.pop(0)
        try:
            ans = {r.get('id'): r for r in ask(batch) if isinstance(r, dict)}
            conn_err = 0
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            conn_err += 1
            log('нет связи', conn_err, repr(e)[:200])
            if conn_err >= 15: log('стоп: Qwen недоступен'); sys.exit(2)
            queue.insert(0, batch); time.sleep(60); continue
        except (ValueError, json.JSONDecodeError, KeyError) as e:
            log('ошибка батча', [c['id'] for c in batch], repr(e)[:300])
            if len(batch) > 1: queue = [[c] for c in batch] + queue  # дробим и повторяем по одному
            else:
                n = done['failed'].get(batch[0]['id'], 0) + 1; done['failed'][batch[0]['id']] = n
                if n < 3: queue.append(batch)
                time.sleep(20)
            continue
        retry = []
        for c in batch:
            r = ans.get(c['id'])
            errs = check(c, r) if r else ['нет ответа']
            if errs:
                n = done['failed'].get(c['id'], 0) + 1; done['failed'][c['id']] = n
                log('плохо', c['id'], errs)
                if n < 3: retry.append([c])
                continue
            r['tier'] = str(r['tier']); r['size_tiles'] = float(r['size_tiles'])
            r['image_prompt'] = STYLE + ', ' + r['image_subject'].rstrip('. ') + ', ' + CAMERA
            done['items'][c['id']] = r; done['failed'].pop(c['id'], None)
        queue = retry + queue
        tmp = OUT + '.tmp'
        json.dump(done, open(tmp, 'w', encoding='utf-8', newline='\n'), ensure_ascii=False, indent=1)
        os.replace(tmp, OUT)
        log('готово', len(done['items']), 'из', len(cands))
    log('конец', len(done['items']), 'сбоев', len(done['failed']))


if __name__ == '__main__':
    main()
