"""D-001: список существ new.ttg.club (API самой страницы), кэш в raw/. Пауза >= 1,2 с, повтор не качает уже скачанное.
Шаг 1: python fetch.py list    — общий список, SRD 5.2, по местам обитания
Шаг 2: python fetch.py detail  — карточки только для отобранных (размер из заголовка), файл shortlist.txt
В наши файлы переносятся только факты (имя, тип, размер, ПО, среда, источник, ссылка)."""
import json, os, sys, time, urllib.request, urllib.parse
D = os.path.dirname(os.path.abspath(__file__)); RAW = os.path.join(D, 'raw')
BASE = 'https://new.ttg.club/api/v2/bestiary'
SRC = 'MM,DRP,PHB,AU,DMG,FRAIF,ABH,LFL,NF,RHW,EFA,AUD,VTMBBB,PUG,GHMG,GHPG,FEQ,UAHS,UAVO,UAA,UAMS,UAPU,UAFRS'
UA = 'Mozilla/5.0 (Orpheus-design; one-off list, 1 req/s)'

def get(url):
    time.sleep(1.2)
    req = urllib.request.Request(url, headers={'User-Agent': UA, 'Accept': 'application/json'})
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)

def cached(name, url):
    p = os.path.join(RAW, name)
    if os.path.exists(p): return json.load(open(p, encoding='utf-8'))
    j = get(url); json.dump(j, open(p, 'w', encoding='utf-8'), ensure_ascii=False); return j

def search(tag, extra=''):
    out, page = [], 0
    while True:
        j = cached(f'search_{tag}_{page}.json', f'{BASE}/search?page={page}&size=200&sorting=CHALLENGE_RATING&source={SRC}{extra}')
        out += j
        if len(j) < 200: return out
        page += 1

if sys.argv[1] == 'list':
    filters = cached('filters.json', f'{BASE}/filters')
    allc = search('all'); srd = search('srd52', '&srdVersion=5.2')
    hab = {}
    for v in next(f for f in filters['filters'] if f['key'] == 'habitat')['values']:
        hab[v['id']] = [x['url'] for x in search('hab_' + v['id'], '&habitat=' + v['id'])]
    json.dump({'all': allc, 'srd52': [x['url'] for x in srd], 'habitat': hab}, open(os.path.join(RAW, 'lists.json'), 'w', encoding='utf-8'), ensure_ascii=False)
    print('all', len(allc), 'srd', len(srd), 'habitats', {k: len(v) for k, v in hab.items() if v})
elif sys.argv[1] == 'detail':
    urls = [l.strip() for l in open(os.path.join(D, 'shortlist.txt'), encoding='utf-8') if l.strip()]
    os.makedirs(os.path.join(RAW, 'detail'), exist_ok=True)
    for i, u in enumerate(urls):
        cached(os.path.join('detail', u + '.json'), f'{BASE}/{urllib.parse.quote(u)}')
        if i % 50 == 0: print(i, len(urls), flush=True)
    print('done', len(urls))
