"""D-001 шаг 4: candidates.json + tagged.json + rights.json -> wiki/src/bestiary.json (раздел вики «Кандидаты из бестиария»).
В вики идут только размеченные кандидаты; тексты — наши (Qwen), с сайта — только факты и ссылки."""
import json, os, collections
D = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(D)))
J = lambda n: json.load(open(os.path.join(D, n), encoding='utf-8'))
cands, tagged, rights = J('candidates.json'), J('tagged.json'), J('rights.json')
cat = json.load(open(os.path.join(ROOT, 'wiki', 'catalog.json'), encoding='utf-8'))
BN = {b['id']: b['name'] for b in cat['biomes']}

out = []
for c in cands:
    t = tagged['items'].get(c['id'])
    if not t: continue
    out.append({
        'id': c['id'], 'name': c['rus'], 'eng': c['eng'], 'aliases': [c['eng']],
        'act': t['act'], 'tier': t['tier'], 'archetype': t['archetype'], 'archetypeName': t.get('archetype_name') or None,
        'role': t['role'], 'nightOnly': bool(t.get('night_only')), 'sizeTiles': t['size_tiles'],
        'biomes': t['biomes'], 'description': t['description'], 'imageSubject': t['image_subject'],
        'facts': {'тип': c['type'], 'размер': ', '.join(c['size']) or '—', 'CR': c['cr'], 'среда': ', '.join(c['habitats']) or '—'},
        'variants': [f"{v['rus']} (CR {v['cr']})" for v in c['variants']] if len(c['variants']) > 1 else None,
        'sources': [v['url'] for v in c['variants']],
        'rights': 'SRD 5.2 (CC-BY-4.0)' if c['srd52'] else 'миф / фольклор / природа; не SRD — образ общий',
    })

pi = sorted({x['eng'] + ' — ' + x['reason'] for x in rights['pi']})
doubt = [f"{x['rus']} ({x['eng']}, CR {x['cr']}) — {x['reason']}" for x in rights['doubt']]
notes = [
    'Источник: new.ttg.club/bestiary (API страницы /api/v2/bestiary, 16.11.2026). С сайта взяты только факты: имя, тип, размер, CR, среда, ссылка. '
    'Описания и промпты написаны заново моделью qwen3.8-27b на Маке (work/design/bestiary/tagged.json).',
    'Правило отбора: Monster Manual 2024; оставлены SRD 5.2 (CC-BY-4.0) и существа мифов, фольклора и природы; убраны фирменные существа D&D (product identity); '
    'книги сеттингов, именные персонажи, сторонние и UA-источники, формы заклинаний — вне выборки. Драконы одного цвета сведены в одно семейство.',
    f"Всего на сайте {sum(len(v) for v in rights.values()) + sum(len(c['variants']) for c in cands)}; убрано как PI {len(rights['pi'])}; "
    f"спорно {len(rights['doubt'])}; вне выборки {len(rights['out_of_scope'])}; не противники {len(rights['not_mob'])}; "
    f"кандидатов (семейств) {len(cands)}, размечено {len(out)}.",
    'Убрано как PI: ' + '; '.join(pi),
    'Спорно (решает Автор, в раздел не включены): ' + '; '.join(doubt),
]
json.dump({'candidates': out, 'notes': notes}, open(os.path.join(ROOT, 'wiki', 'src', 'bestiary.json'), 'w', encoding='utf-8', newline='\n'),
          ensure_ascii=False, indent=1)
dist = lambda f: dict(collections.Counter(x for o in out for x in ([o[f]] if not isinstance(o[f], list) else o[f])).most_common())
print('в вики', len(out))
print('акты', dist('act')); print('ярусы', dist('tier'))
print('биомы', {BN.get(k, k): v for k, v in dist('biomes').items()})
print('архетипы', dist('archetype'))
