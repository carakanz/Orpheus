"""Сводит wiki/src/*.json (биомы, враги, боссы, руды, предметы) в wiki/catalog.json для мини-вики."""
import json, re, os
D = os.path.dirname(os.path.abspath(__file__))  # wiki/src: исходные таблицы
OUT = os.path.join(os.path.dirname(D), 'catalog.json')  # wiki/catalog.json
J = lambda n: json.load(open(os.path.join(D, n), encoding='utf-8'))
b, e, o, i = J('biomes.json'), J('enemies.json'), J('ores.json'), J('items.json')
bo = J('bosses.json') if os.path.exists(os.path.join(D, 'bosses.json')) else {'bosses': [], 'notes': []}

def alias(x):
    n = x['name']
    short = re.split(r'\s*[(«,]', n)[0].strip()
    al = set(x.get('aliases', []))
    if short and short != n: al.add(short)
    m = re.search(r'«([^»]+)»', n)
    if m: al.add(m.group(1))
    if al: x['aliases'] = sorted(al)
    return x

def norm(s): return re.sub(r'\s+', ' ', str(s or '').lower().replace('ё', 'е')).strip()

biomes = [alias(x) for x in b['biomes']]
for z in b['zones']:
    z = dict(z); z.setdefault('act', 1); z['zone'] = True
    z['placement'] = z.get('placement') or z.get('code')
    if z.get('differences'): z['proposals'] = None; z['features'] = z.get('differences')
    z.pop('code', None)
    biomes.append(alias(z))

bosses = [alias(x) for x in bo.get('bosses', [])]
if os.path.exists(os.path.join(D, 'bosses_author.json')):
    bosses += [alias(x) for x in J('bosses_author.json')['bosses']]
bnames = {norm(x['name']) for x in bosses} | {norm(a) for x in bosses for a in x.get('aliases', [])}
enemies = []
# предложения 16.11 (слово Автора о разнообразии мобов): новые архетипы и мобы актов II–III
extra_arch = J('archetypes_more.json')['archetypes'] if os.path.exists(os.path.join(D, 'archetypes_more.json')) else []
extra_en = []
for n in ('enemies_author.json', 'enemies_more_act2.json', 'enemies_more_act3.json'):
    if os.path.exists(os.path.join(D, n)):
        extra_en += J(n)['enemies']
for x in e['enemies'] + extra_en:
    if norm(x['name']) in bnames:
        continue
    if x.get('kind') in ('босс', 'мини-босс') and bosses:
        # босс есть только у агента врагов — переносим в раздел боссов
        y = dict(x); y.setdefault('where', ', '.join(x.get('biomes') or []))
        y.setdefault('rewards', x.get('drops')); y.setdefault('kind', x['kind'])
        bosses.append(alias(y)); continue
    enemies.append(alias(x))

items = [alias(x) for x in i['items']]
inames = {norm(x['name']) for x in items} | {norm(a) for x in items for a in x.get('aliases', [])}
for x in o.get('items', []):
    if norm(x['name']) in inames: continue
    y = {'id': x['id'], 'name': x['name'], 'category': 'материал', 'tier': x.get('tier'), 'use': x.get('from'), 'implemented': x.get('implemented')}
    items.append(alias(y))
ores = [alias(x) for x in o['ores']]
for x in ores:
    if x.get('proposed_fields') and not x.get('proposedFields'): x['proposedFields'] = x.pop('proposed_fields')

# уникальные id внутри раздела
for lst in (biomes, enemies, bosses, ores, items):
    seen = {}
    for x in lst:
        k = x['id']
        if k in seen: seen[k] += 1; x['id'] = f"{k}_{seen[k]}"
        else: seen[k] = 1

# D-001: кандидаты в мобы из бестиария (разметка Мак-Qwen, work/design/bestiary) — отдельный раздел с меткой «кандидат»
bst = J('bestiary.json') if os.path.exists(os.path.join(D, 'bestiary.json')) else {'candidates': [], 'notes': []}
candidates = [alias(dict(x, candidate=True)) for x in bst['candidates']]

L = b['layout']
layout = {'grid': L['grid32'], 'hearth': [15.5, 15.5],
          'description': L['summary'] + ' ' + L['gridNote'] + ' Сейчас в игре (срез): ' + L['sliceSummary']}

cat = {
    'biomes': biomes, 'enemies': enemies, 'bosses': bosses, 'archetypes': e['archetypes'] + extra_arch,
    'ores': ores, 'items': items, 'candidates': candidates, 'layout': layout,
    'notes': {'Биомы и генератор': b['notes'], 'Враги': e['notes'], 'Боссы': bo.get('notes', []),
              'Руды и ресурсы': o['notes'], 'Предметы': i['notes'],
              'Буквы архетипов в docs/01': [b.get('archetypeLegend')],
              'Бестиарий: права и отбор (D-001)': bst.get('notes', [])},
}
json.dump(cat, open(OUT, 'w', encoding='utf-8', newline=chr(10)), ensure_ascii=False)
print({k: len(v) for k, v in cat.items() if isinstance(v, list)})
