"""D-001, второй заход (слово Автора 16.11: «для тир 3»): Archives of Nethys — Starfinder 2e (2e.aonsrd.com/creatures)
и Starfinder 1e (www.aonsrd.com/Aliens.aspx?Letter=All). robots.txt на обоих сайтах нет (404), лицензии — ORC (SF2e) и OGL 1.0a (SF1e).
Бережно: SF2e — один запрос к тому же Elasticsearch, что использует страница, только поля-факты; SF1e — одна страница списка.
Кэш — raw/aon/ (вне git). В наши файлы идут только факты: имя, тип/признаки, размер, уровень/CR, среда, источник, ссылка."""
import json, os, re, html, time, urllib.request
D = os.path.dirname(os.path.abspath(__file__)); RAW = os.path.join(D, 'raw', 'aon'); os.makedirs(RAW, exist_ok=True)
UA = {'User-Agent': 'Mozilla/5.0 (Orpheus-design; one-off list)'}
FIELDS = ['name', 'level', 'size', 'trait_raw', 'rarity', 'primary_source', 'primary_source_category', 'url', 'npc', 'type',
          'creature_family', 'speed', 'element']


def cached(name, fn):
    p = os.path.join(RAW, name)
    if not os.path.exists(p):
        time.sleep(1.2); data = fn(); open(p, 'wb').write(data)
    return open(p, 'rb').read()


def sf2():
    body = json.dumps({'size': 2000, '_source': FIELDS, 'query': {'term': {'category': 'creature'}}}).encode()
    req = urllib.request.Request('https://elasticsearch.aonprd.com/aonsf/_search', data=body,
                                 headers=dict(UA, **{'Content-Type': 'application/json', 'Origin': 'https://2e.aonsrd.com'}))
    return urllib.request.urlopen(req, timeout=60).read()


def sf1():
    return urllib.request.urlopen(urllib.request.Request('https://www.aonsrd.com/Aliens.aspx?Letter=All', headers=UA), timeout=60).read()


j = json.loads(cached('sf2_creatures.json', sf2))
out = []
for h in j['hits']['hits']:
    s = h['_source']
    out.append({'src': 'SF2e', 'name': s['name'], 'level': s.get('level'), 'size': s.get('size') or [], 'traits': s.get('trait_raw') or [],
                'rarity': s.get('rarity'), 'book': s.get('primary_source'), 'bookCat': s.get('primary_source_category'),
                'npc': s.get('npc'), 'speed': {k: v for k, v in (s.get('speed') or {}).items() if k != 'max'},
                'url': 'https://2e.aonsrd.com' + s.get('url', '')})
page = cached('sf1_aliens.html', sf1).decode('utf-8', 'replace')
for r in re.findall(r'<tr>(.*?)</tr>', page, re.S):
    cells = [html.unescape(re.sub(r'<[^>]+>', '', c)).strip() for c in re.findall(r'<td[^>]*>(.*?)</td>', r, re.S)]
    link = re.findall(r'href="([^"]+)"', r)
    if len(cells) < 4 or not link: continue
    out.append({'src': 'SF1e', 'name': cells[0], 'cr': cells[1], 'type': cells[2], 'env': cells[3],
                'url': 'https://www.aonsrd.com/' + html.unescape(link[0]).replace(' ', '%20')})
json.dump(out, open(os.path.join(RAW, 'aon_list.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
print('SF2e', sum(o['src'] == 'SF2e' for o in out), 'SF1e', sum(o['src'] == 'SF1e' for o in out))
