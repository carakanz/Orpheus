"""D-001 шаг 4: candidates*.json + tagged*.json + rights*.json -> wiki/src/bestiary.json (раздел вики «Кандидаты из бестиария»).
Два захода: D&D 5e (new.ttg.club) и Starfinder 2e/1e (Archives of Nethys, для акта III).
В вики идут только размеченные кандидаты; тексты — наши (Qwen), с сайтов — только факты и ссылки."""
import json, os, collections
D = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(D)))
J = lambda n, d=None: json.load(open(os.path.join(D, n), encoding='utf-8')) if os.path.exists(os.path.join(D, n)) else d
cat = json.load(open(os.path.join(ROOT, 'wiki', 'catalog.json'), encoding='utf-8'))
BN = {b['id']: b['name'] for b in cat['biomes']}


def build(cands, tagged, source_of, rights_of):
    out = []
    for c in cands:
        t = tagged['items'].get(c['id'])
        if not t: continue
        out.append({
            'id': c['id'], 'name': (t.get('name_ru') or c['rus']).strip(), 'eng': c['eng'], 'aliases': [c['eng']],
            'source': source_of(c),
            'act': t['act'], 'tier': t['tier'], 'archetype': t['archetype'], 'archetypeName': t.get('archetype_name') or None,
            'role': t['role'], 'nightOnly': bool(t.get('night_only')), 'sizeTiles': t['size_tiles'],
            'biomes': [BN.get(x, x) for x in t['biomes']], 'description': t['description'], 'imageSubject': t['image_subject'],
            'facts': {'тип': c['type'], 'размер': ', '.join(c['size']) or '—', 'CR / уровень': c['cr'], 'среда': ', '.join(c['habitats']) or '—'},
            'variants': [f"{v['rus']} (CR {v['cr']})" for v in c['variants']] if len(c['variants']) > 1 else None,
            'sources': [v['url'] for v in c['variants']],
            'rights': rights_of(c),
        })
    return out


EMPTY = {'items': {}}
cands, rights = J('candidates.json'), J('rights.json')
out = build(cands, J('tagged.json', EMPTY), lambda c: 'D&D 5e (ttg.club)',
            lambda c: 'SRD 5.2 (CC-BY-4.0)' if c['srd52'] else 'миф / фольклор / природа; не SRD — образ общий')
ca, ra = J('candidates_aon.json', []), J('rights_aon.json', {'pi': [], 'doubt': [], 'not_mob': []})
out_aon = build(ca, J('tagged_aon.json', EMPTY), lambda c: c['source'][0].split(' (')[0],
                lambda c: c['source'][0].split('(')[-1].rstrip(')') + '; название описательное, не собственное имя Paizo')

pi = sorted({x['eng'] + ' — ' + x['reason'] for x in rights['pi']})
doubt = [f"{x['rus']} ({x['eng']}, CR {x['cr']}) — {x['reason']}" for x in rights['doubt']]
notes = [
    'Заход 1 — D&D 5e: new.ttg.club/bestiary (API страницы /api/v2/bestiary, 16.11.2026). С сайта взяты только факты: имя, тип, размер, CR, среда, ссылка. '
    'Описания и промпты написаны заново моделью qwen3.8-27b на Маке (work/design/bestiary/tagged.json).',
    'Правило отбора D&D: Monster Manual 2024; оставлены SRD 5.2 (CC-BY-4.0) и существа мифов, фольклора и природы; убраны фирменные существа D&D (product identity); '
    'книги сеттингов, именные персонажи, сторонние и UA-источники, формы заклинаний — вне выборки. Драконы одного цвета сведены в одно семейство.',
    f"D&D: всего на сайте {sum(len(v) for v in rights.values()) + sum(len(c['variants']) for c in cands)}; убрано как PI {len(rights['pi'])}; "
    f"спорно {len(rights['doubt'])}; вне выборки {len(rights['out_of_scope'])}; не противники {len(rights['not_mob'])}; "
    f"кандидатов (семейств) {len(cands)}, размечено {len(out)}.",
    'D&D, убрано как PI: ' + '; '.join(pi),
    'D&D, спорно (решает Автор, в раздел не включены): ' + '; '.join(doubt),
]
if ca:
    notes += [
        'Заход 2 — Starfinder (слово Автора 16.11, «для тир 3»): 2e.aonsrd.com/creatures (Starfinder 2e, лицензия ORC; один запрос к поиску сайта) и '
        'www.aonsrd.com/Aliens.aspx (Starfinder 1e, OGL 1.0a; одна страница списка). robots.txt на сайтах нет. Взяты только факты: имя, признаки/тип, размер, уровень/CR, среда, ссылка.',
        'Правило отбора Starfinder: «product identity» / Reserved Material Paizo — собственные имена (миры, божества, персонажи, фракции, придуманные названия видов) — убраны; '
        'оставлены существа с описательными или общемифическими названиями (оозы, роботы, нежить, дрейки, звери, фольклор); звездолёты и НИП — не противники.',
        f"Starfinder: всего {sum(len(v) for v in ra.values()) + len(ca)}; убрано как PI {len(ra['pi'])}; спорно {len(ra['doubt'])}; "
        f"не противники {len(ra['not_mob'])}; кандидатов {len(ca)}, размечено {len(out_aon)}.",
        'Starfinder, спорно: ' + '; '.join(f"{x['name']} ({x['src']}) — {x['reason']}" for x in ra['doubt']),
    ]
allc = out + out_aon
json.dump({'candidates': allc, 'notes': notes}, open(os.path.join(ROOT, 'wiki', 'src', 'bestiary.json'), 'w', encoding='utf-8', newline='\n'),
          ensure_ascii=False, indent=1)
for title, lst in (('D&D', out), ('Starfinder', out_aon), ('всего', allc)):
    dist = lambda f: dict(collections.Counter(x for o in lst for x in ([o[f]] if not isinstance(o[f], list) else o[f])).most_common())
    print(f'== {title}: в вики {len(lst)}')
    print('акты', dist('act')); print('ярусы', dist('tier')); print('биомы', dist('biomes')); print('архетипы', dist('archetype'))
