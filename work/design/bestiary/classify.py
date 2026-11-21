"""D-001 шаг 2: фильтр прав и сведение семейств. Вход raw/lists.json (+ raw/detail/* для размера), выход:
candidates.json (факты: имя рус/англ, тип, размер, ПО, среда, источник, ссылка), rights.json (PI / спорно / вне выборки), shortlist.txt.
Правило (карточка D-001): оставляем Monster Manual (2024) — SRD 5.2 (CC-BY-4.0) и существа мифов/фольклора/природы;
убираем фирменных монстров WotC (PI) по списку ниже; сомнительное — «спорно» с причиной;
книги сеттингов, именные НИП, сторонние и UA-источники, формы заклинаний — вне выборки."""
import json, os, re, collections
D = os.path.dirname(os.path.abspath(__file__)); RAW = os.path.join(D, 'raw')
L = json.load(open(os.path.join(RAW, 'lists.json'), encoding='utf-8'))
SRD = set(L['srd52'])

# Фирменные существа D&D (product identity WotC): придуманы для D&D и узнаются как бренд.
PI = [
    (r'^Beholder|^Death Tyrant$|^Spectator$|^Gas Spore', 'бехолдер и родня (PI, OGL 1.0a)'),
    (r'^Mind Flayer|^Intellect Devourer$|^Vampire Mind Flayer', 'иллитид / свежеватель разума и его мозг (PI)'),
    (r'^Githyanki|^Githzerai', 'гит: гитьянки и гитзерай (PI)'),
    (r'^Kuo-toa', 'кво-тоа (PI)'),
    (r'Slaad', 'слаады (PI)'),
    (r'^Umber Hulk', 'умбер-хульк (PI)'),
    (r'^Yuan-ti', 'юань-ти (PI)'),
    (r'^Displacer Beast', 'смещающий зверь (PI)'),
    (r'^Carrion Crawler', 'ползающий падальщик (PI)'),
    (r'Owlbear', 'совомедведь: назван Автором как лицензионный (хотя есть в SRD)'),
    (r'^Modron', 'модроны (Planescape)'),
    (r'^Nothic$', 'нотик (D&D)'), (r'^Grell$', 'грелл (D&D)'), (r'^Myconid', 'микониды (D&D)'),
    (r'^Quaggoth', 'квагготы (D&D)'), (r'^Hook Horror', 'крючкастый ужас (D&D)'), (r'^Thri-kreen', 'три-крины (D&D)'),
    (r'loth$', 'юголоты (D&D)'), (r'^Yochlol', 'йоклол (D&D, Ллос)'), (r'^Flumph', 'флампф (D&D)'),
    (r'Yeenoghu|^Gnoll Demoniac', 'гноллы Йеногу: именной демон-лорд D&D'),
    (r'^Helmed Horror', 'шлемоносный ужас (D&D)'), (r'^Galeb Duhr', 'галеб дур (D&D)'),
    (r'^Gulthias Blight', 'именное дерево D&D'), (r'^Blob of Annihilation', 'сфера уничтожения D&D'),
    (r'^Kenku', 'кенку (D&D)'), (r'^Aarakocra', 'ааракокры (D&D)'), (r'^Flameskull', 'пылающий череп (D&D)'),
]
# Спорно: решает Автор (причина рядом)
DOUBT = [
    (r'^Xorn$', 'в SRD 5.2 (CC-BY), но в карточке назван среди PI: нужен ответ Автора'),
    (r'^Bullywug', 'буллиуги придуманы D&D, в SRD нет'), (r'^Troglodyte', 'троглодиты D&D (не SRD), слово общее'),
    (r'^Piercer', 'пронзатель придуман D&D, в SRD нет'), (r'Blight$', 'растения-заразы D&D (не SRD)'),
    (r'^Jackalwere', 'шакал-оборотень D&D, в SRD нет'), (r'^Water Weird', 'водная странность D&D, в SRD нет'),
    (r'Shadow Dragon', 'теневой дракон D&D, в SRD нет'), (r'^Dracolich', 'драколич D&D, в SRD нет'),
    (r'^Demilich', 'демилич D&D, в SRD нет'), (r'^Death Knight', 'рыцарь смерти D&D (Сот), в SRD нет'),
    (r'^Empyrean', 'эмпирей D&D, в SRD нет'), (r'^Faerie Dragon', 'фейский дракон D&D, в SRD нет'),
    (r'^Ogrillon', 'огриллон D&D, в SRD нет'), (r'^Bone Naga', 'костяная нага: вариант D&D'),
    (r'^Grick Ancient|^Psychic Gray Ooze|^Violet Fungus Necrohulk', 'новый вариант 2024 от SRD-существа: проверить'),
    (r'^(Spined|Bearded|Barbed) Devil|^Manes|^Barlgura|^Chasme|^Goristro|^Shadow Demon|^Swarm of (Lemures|Dretches|Larvae)|^Larva$|^Cambion',
     'исчадия по космологии D&D (не SRD); сами слова «демон/дьявол» общие'),
    (r'^Animal Lord|^Arch-hag|^Elemental Cataclysm|^Colossus', 'новые существа MM 2024 вне SRD: оригинальность не проверена'),
    (r'Crawling Claw', 'ползающая рука: D&D, но образ общий'),
    (r'^Shoggoth|^Mi-Go|^Elder Thing|^Yithian|^Gug$|^Nightgaunt|^Cthulhu', 'мифы Лавкрафта: тексты в общественном достоянии, но марки Chaosium'),
]
# Вне MM: только общефольклорные существа из книг сеттингов
EXTRA_OK = {'Jiangshi', 'Strigoi', 'Dullahan', 'Loup Garou', 'Swanmay', 'Giant Fly', 'Polar Serpent'}
# Не противники: формы заклинаний, слуги, спутники
NOT_MOB = (r'Spirit$|^Avatar of Death|Servant$|Companion$|Familiar$|^Beast of the (Sea|Land|Sky)|^Animated Object$'
           r'|^Otherworldly Steed|^Steel Defender|^Living (Thunderwave|Vitriolic Sphere|Cone of Cold)|^Giant Insect$'
           r'|^Primal (Striker|Guardian)|Wonder$|^Troll Limb$')

FAMILY = re.compile(r'^(?:Ancient |Adult |Young |Juvenile )?(.+? Dragon)(?: Wyrmling)?$')


def fam(eng):
    m = FAMILY.match(eng)
    if m and 'Faerie' not in eng: return m.group(1)
    return eng


def cr_num(s):
    s = str(s or '0')
    if '/' in s:
        a, b = s.split('/'); return int(a) / int(b)
    try: return float(s)
    except ValueError: return 0.0


HAB_RU = {'ANY': 'любая', 'ARCTIC': 'арктика', 'COASTAL': 'побережье', 'DESERT': 'пустыня', 'FOREST': 'лес', 'GRASSLAND': 'луга',
          'HILL': 'холмы', 'MOUNTAIN': 'горы', 'SWAMP': 'болота', 'UNDERDARK': 'подземье', 'UNDERWATER': 'под водой', 'URBAN': 'город'}
habs = collections.defaultdict(list)
for k, urls in L['habitat'].items():
    for u in urls: habs[u].append(HAB_RU.get(k, 'план: ' + k.replace('PLANAR_', '').replace('_', ' ').lower()))

rights = {'pi': [], 'doubt': [], 'out_of_scope': [], 'not_mob': []}
keep = []
for x in L['all']:
    eng, src = x['name']['eng'], x['source']['name']['label']
    rec = {'url': x['url'], 'eng': eng, 'rus': x['name']['rus'], 'source': src, 'cr': x['challengeRailing'], 'type': x['type']}
    if re.search(NOT_MOB, eng): rights['not_mob'].append(rec); continue
    hit = next((r for p, r in PI if re.search(p, eng)), None)
    if hit: rights['pi'].append(dict(rec, reason=hit)); continue
    hit = next((r for p, r in DOUBT if re.search(p, eng)), None)
    if hit: rights['doubt'].append(dict(rec, reason=hit, habitats=habs.get(x['url'], []))); continue
    if src != 'MM' and eng not in EXTRA_OK:
        rights['out_of_scope'].append(dict(rec, reason='книга сеттинга / именной персонаж / сторонний или UA-источник')); continue
    keep.append(rec)

SIZE_RU = ['Крошечный', 'Маленький', 'Средний', 'Большой', 'Огромный', 'Громадный']


def size_of(u):
    p = os.path.join(RAW, 'detail', u + '.json')
    if not os.path.exists(p): return None
    h = json.load(open(p, encoding='utf-8')).get('header', '') or ''
    first = h.split(',')[0].lower()
    return next((s for s in SIZE_RU if s.lower()[:5] in first), None)


groups = collections.OrderedDict()
for r in keep: groups.setdefault(fam(r['eng']), []).append(r)
cands = []
for f, rs in groups.items():
    rs.sort(key=lambda r: cr_num(r['cr']))
    rus = rs[0]['rus']
    if len(rs) > 1:  # семейство драконов: «Красный дракон»
        rus = re.sub(r'^(Древний|Взрослый|Молодой|Юный)\s+', '', rs[-1]['rus']); rus = rus[0].upper() + rus[1:]
    cid = re.sub(r'[^a-z0-9]+', '_', f.lower()).strip('_')
    cands.append({
        'id': 'bst_' + cid, 'eng': f, 'rus': rus, 'type': rs[0]['type'],
        'cr': rs[0]['cr'] if len(rs) == 1 else f"{rs[0]['cr']}–{rs[-1]['cr']}",
        'crMin': min(cr_num(r['cr']) for r in rs), 'crMax': max(cr_num(r['cr']) for r in rs),
        'size': [s.capitalize() for s in dict.fromkeys(s for s in (size_of(r['url']) for r in rs) if s)],
        'habitats': sorted({h for r in rs for h in habs.get(r['url'], [])}),
        'srd52': any(r['url'] in SRD for r in rs), 'source': sorted({r['source'] for r in rs}),
        'variants': [{'eng': r['eng'], 'rus': r['rus'], 'cr': r['cr'], 'url': 'https://new.ttg.club/bestiary/' + r['url']} for r in rs],
    })
json.dump(cands, open(os.path.join(D, 'candidates.json'), 'w', encoding='utf-8', newline='\n'), ensure_ascii=False, indent=1)
json.dump(rights, open(os.path.join(D, 'rights.json'), 'w', encoding='utf-8', newline='\n'), ensure_ascii=False, indent=1)
open(os.path.join(D, 'shortlist.txt'), 'w', encoding='utf-8', newline='\n').write('\n'.join(r['url'] for r in keep) + '\n')
print('всего', len(L['all']), 'PI', len(rights['pi']), 'спорно', len(rights['doubt']), 'вне выборки', len(rights['out_of_scope']),
      'не мобы', len(rights['not_mob']), 'оставлено', len(keep), 'кандидатов (семейств)', len(cands),
      'из них SRD', sum(c['srd52'] for c in cands), 'без размера', sum(1 for c in cands if not c['size']))
