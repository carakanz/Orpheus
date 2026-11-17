"""D-001, второй заход: фильтр прав для Starfinder 2e / 1e (Archives of Nethys). Вход raw/aon/aon_list.json.
Правило: материалы Paizo открыты по ORC (SF2e) и OGL 1.0a (SF1e), но «product identity» / Reserved Material Paizo —
собственные имена (миры, божества, персонажи, фракции, придуманные названия видов) — нельзя.
Поэтому берём только существ с описательным или общемифическим названием (оозы, роботы, нежить, драконы-«дрейки», звери,
фольклор) — список KEEP ниже, решение исполнителя по каждому имени; сомнительное — DOUBT с причиной; остальное — PI по умолчанию.
Выход: candidates_aon.json (только факты), rights_aon.json."""
import json, os, re, collections
D = os.path.dirname(os.path.abspath(__file__)); RAW = os.path.join(D, 'raw', 'aon')
L = json.load(open(os.path.join(RAW, 'aon_list.json'), encoding='utf-8'))

KEEP2 = """Smog Scamp; Automated Fire Extinguisher; Zombie Shambler; Scout Drones; Drifting Bonfire; Cinder; Bonecrusher; Lifeleech Ooze;
Wight Scientist; Faceless; Mummy Wasp Swarm; Void Palm; Moonflower; Necrolinked Wight; Dust Manta; Electrovore; Flayer Leech; Powder Beetle;
Vent Viper; Giant Centipede; Foul Homunculus; Juvenile Star Tardigrade; Electrovore Nuclear Mummy; Nuclear Mummy; Electronic Figment;
Derelict Harvester; Mature Moon Bear; Crystalline Ooze; Mechanolocust Swarm; Heart Elemental; Chocolate Ooze; Digital Ghost Researcher;
Crystal-pulse Construct; Minibot; Hardlight Dragonet; Young Sharpwing; Cyberwing; Shroomclaw; Dire Shroomclaw; Scrap Rat; Cybernetic Zombie;
Hardlight Scamp; Scrap Rat Visionary; Juvenile Glass Serpent; Hardlight Havoc; Hardlight Mascot; Protection-Class Security Robot; Boneflayer;
Rigor Mortic; Void Zombie; Voidsage; Assembly Ooze; Animated Firearm; Ferrofluid Ooze; Animated Tech Armor; Warp Troll; Mutant Troll; Tool Rat;
Line-Class Soldier Robot; Vanguard-Class Soldier Robot; Observer-Class Security Robot; Repair-Class Security Robot; Snoop-Class Infiltrator Robot;
Emissary-Class Infiltrator Robot; Ordinance-Class Civil Robot; Tax-Class Civil Robot; Elimination-Class Assassin Robot; Demolition-Class Assassin Robot;
Robotic Tactical War Machine; Robotic Advanced War Machine; Biotech Advanced War Machine; Robotic Superior War Machine; Biotech Superior War Machine;
Robotic Elite War Machine; Biotech Ultimate War Machine; Rendworm; Void Ooze; Elder Psychic Fungus; Psychic Fungus Warden; Psychic Fungus Harvester;
Paradoxical Solipsist; Scavenger Slime; Ferrofluid Magneto-Cluster; Ichor Lord; Plasma Ooze; Malevolent AI; Cloud Skate; Cloud Ray;
Digital Ghost Vidgamer; Ravenous Drake; Blight Drake; Plasmazer; Shipbreaker; Wastewither; Empathnid; Dread Empathnid; Biotech Giant; Moon Giant;
Glass Serpent; Clangit; Gubbin; Gnackle; Hardlight Weaver; Irradiated Undead Plant; Irradiated Undead Flyer; Irradiated Undead Charger;
Fouled Water Scamp; Fouled Brine Shark; Radiation Crawler; Earth Scamp; Fire Scamp; Living Wildfire; Tough; Credshark; Ganglord; String-of-bombs;
Terminite Worker; Terminite Hunter; Terminite Queen; Bounty Hunter; Graviton Assassin; Security Goon; Security Sharpshooter;
Security Arcanotech Agent; Space Goblin Miscreant; Space Goblin Junker; Space Goblin Griefcaster; Entrepreneurial Agent;
Executive Protection Agent; Surveillance Mage; Biotech Sporehall; Dust Worm; Dust Worm Augur; Unspooled Vestige; Phantasmal Minion;
Wayward Warp; Warpstitcher; Shimmerstone Sentinel"""
KEEP1 = """Aerial Devil; Animated Quartz Swarm; Arcane Asp; Astro-krill Swarm; Augerwing; Bodysnatcher Slime; Carnivorous Crystal; Carrion Wheel;
Cerebric Fungus; Clockwork Predator; Collector Crab; Creeping Shade; Dawn Beetle, Dawn Core-diver; Deadfall Stalker; Detonation Ooze; Devil, Imp;
Dinosaur, Ceratopsid; Dinosaur, Plesiosaur; Dinosaur, Sauropod; Dinosaur, Thyreophoran; Elemental, Greater Elemental; Elemental, Large Elemental;
Elemental, Small Elemental; Emotivore; Entropy Slug; Exhaust Ooze; Flying Viper Eel; Genie, Djinni; Genie, Janni; Genie, Shaitan; Ghoul; Ghul;
Giant, Eclipse; Giant, Shadow; Glitch Dog; Golem, Cybernetic Golem; Golem, Nanotech Golem; Hag, Void Hag; Harpy Jasmine; Hellhound;
Herd Animal, Gargantuan Herd Animal; Herd Animal, Large Herd Animal; Herd Animal, Small Herd Animal; Holofang; Kami, Chinjugami; Kami, Tsukumogami;
Kurobozu; Liquescence; Living Apocalypse; Lore Guardian; Magma Ooze; Marooned One; Mephit; Meteorite Drake; Midships Mimic; Nanobot Mimic;
Night Hag; Oni, Oni, Spirit; Philosopher Worm; Plague Ooze; Polyphonic Gel; Predator, Colossal Predator; Predator, Huge Predator;
Predator, Medium Predator; Predator, Small Predator; Psydrake; Pyric Wraith; Quantum Slime; Radiation Drake, Slasher; Ravenous Skull, Ravenous Skull Swarm;
Replicant Ooze; Robot Dragon, Assault Disintegrator Dragon; Robot, Assassin Robot; Robot, Incapacitator; Robot, Mining; Robot, Terminator-Class Security;
Rogue Drone, Rogue Hover Drone; Scouring Swarm; Shadow Mastiff; Silicon Mummy, Mummified Prophet; Sky Fisher; Solar Wisp, Juvenile Solar Wisp;
Goblin (Space), Space Goblin Zaperator; Vermin (Space), Asteroid Louse; Vermin (Space), Necropede; Vermin (Space), Warpmoth Swarm; Spookfish Swarm;
Stormghost; Swamp Strider, Common; Synapse Worm; Tangler; Tattoo Guardian; Tenebrous Worm; Threshwolf; Troll Polyp; Troll, Troll, Quantum;
Tumbletooth Swarm; Tzitzimitl; Undead Minion, Cybernetic Zombie; Undead Minion, Skeletal Undead; Ursine Horror; Valkyrie; Vampire, Jiang-Shi;
Vampire, Nosferatu; Vesper Hound; Void Slime; Weaponized Toy, Weaponized Gamedroid"""
DOUBT = {
    'Techno Graveknight': 'graveknight — термин Paizo', 'Sapient Purple Orb': 'разумные сферы — образ Paizo',
    'Sapient Green Orb': 'разумные сферы — образ Paizo', 'Faltering Sapient Green Orb': 'разумные сферы — образ Paizo',
    'Sapient Purple Orb, Luminant': 'разумные сферы — образ Paizo',
    'Young Cosmic Dragon': 'космический дракон — вид драконов Paizo', 'Adult Cosmic Dragon': 'космический дракон — вид драконов Paizo',
    'Ancient Cosmic Dragon': 'космический дракон — вид драконов Paizo', 'Young Akashic Dragon': 'акашический дракон — Paizo',
    'Adult Akashic Dragon': 'акашический дракон — Paizo', 'Ancient Akashic Dragon': 'акашический дракон — Paizo',
    'Brain Collector': 'аналог ми-го у Paizo', 'Mi-Go, Mi-Go High Priest': 'мифы Лавкрафта (марки Chaosium)',
    'Yithian, Yithian Elder': 'мифы Лавкрафта', 'Shantak': 'мифы Лавкрафта', 'Hound of Tindalos, Thing from Beyond Time': 'мифы Лавкрафта',
    'Hungerer From The Dark': 'лавкрафтовский образ Paizo', 'Thoqqua': 'существо из D&D (не SRD 5.2)',
    'Planar Scion, Tiefling': 'тифлинг — термин D&D', 'Retail Employee of the Month': 'шуточный НИП приключения',
    'Retail Phantasm': 'шуточный НИП приключения', 'Admin Employee of the Month': 'шуточный НИП приключения',
    'Mohrg': 'мохрг — придуман Paizo/D&D 3.5', 'Living Apocalypse': 'оригинальный ооз Paizo; название описательное',
}
KEEP = {s.strip() for s in (KEEP2 + ';' + KEEP1).replace('\n', ' ').split(';') if s.strip()}
FACTION = r'Aeon|Azlanti|Corpse Fleet|Hellknight|Sihedron|Veskarium|Steward|Jinsul|^The Swarm|Swarm,|Free Captain|Directive 9|Drift|Infernex|Inheritorworks|Mechanizer|Anacite|Elindrian|Xenowarden|Lissala|Devourer|Starstone|Hivebound|Golarion|Pact'
STARSHIP = r'Vessel|Freighter|Cruiser|Dreadnought|Clustership|Dropship|Gardenship|Arkship|^UC |^BD |^SC |^BMC |^AAC |^SFS |^Atech|Warshell|Starsplitter|Jolly Roger|Trident$|Cathedralship|Moonhopper|Wanderlust$|Bazaar|Spacer Truck|Comet$|Harrier$|Dominator$|Monitor$|Seeder$|Derelict$|Mauler$|Devastator$|Tyrant$|Breacher$|Carrier$|Voidbringer|Moonlighter|Cantripper|Canopyskipper|Star Dragon$|Galactic Dragon$|Behemoth$|Propagator$|Sentinel$|Scout$|Driller$|Ambassador$|Surestrike|Brightsword|Shieldcraft|Burning Nail|Stalwart$|Justiciar|Citadel|Rustbringer|Heartbreaker|Starcrusher|Pentacorn|Buccaneer|Pod$|Bloom$|Wayfarer$|Jackknife|Vaultship|Necrofighter|Dirgesinger|Eulogy|Cenotaph|Klokworx|Apostle$'


def lvl(o):
    if o['src'] == 'SF2e': return float(o['level'] if o['level'] is not None else 0)
    s = str(o.get('cr') or '0')
    try: return eval(s) if re.fullmatch(r'\d+(/\d+)?', s) else 0.0
    except Exception: return 0.0


rights = {'pi': [], 'doubt': [], 'not_mob': []}
keep, seen = [], set()
for o in L:
    n = o['name']
    is_ship = o['src'] == 'SF1e' and 'starship' in o.get('type', '') or o['src'] == 'SF2e' and ('Starship' in o.get('traits', []) or re.search(STARSHIP, n) and n not in KEEP)
    rec = {'src': o['src'], 'name': n, 'level': lvl(o), 'url': o['url']}
    if is_ship or o.get('npc'): rights['not_mob'].append(rec); continue
    if n in DOUBT: rights['doubt'].append(dict(rec, reason=DOUBT[n])); continue
    if n not in KEEP:
        why = 'фракция / мир / организация Paizo' if re.search(FACTION, n) else 'собственное имя, вид или роль из сеттинга Paizo (PI / Reserved Material) — по умолчанию не берём'
        rights['pi'].append(dict(rec, reason=why)); continue
    short = n.split(', ')[-1] if ', ' in n else n
    key = re.sub(r'[^a-z0-9]+', '_', short.lower()).strip('_')
    if key in seen: continue
    seen.add(key); o['short'] = short; keep.append(o)

cands = []
for o in keep:
    traits = [t for t in o.get('traits', []) if t not in ('Common', 'Uncommon', 'Rare', 'Unique')]
    typ = ', '.join(traits) if o['src'] == 'SF2e' else o.get('type', '')
    sz = [s for s in o.get('size', [])] if o['src'] == 'SF2e' else []
    env = ', '.join(k for k in o.get('speed', {}) if k != 'land') if o['src'] == 'SF2e' else re.sub(r'\s*\([^)]*\)', '', o.get('env', ''))
    lv = lvl(o)
    cands.append({'id': 'aon_' + re.sub(r'[^a-z0-9]+', '_', o['short'].lower()).strip('_'), 'eng': o['short'], 'rus': o['short'],
                  'type': typ, 'cr': ('ур. ' if o['src'] == 'SF2e' else 'CR ') + (str(int(lv)) if lv == int(lv) else str(o.get('cr'))),
                  'crMin': lv, 'crMax': lv, 'size': sz, 'habitats': [env] if env else [], 'srd52': False,
                  'source': ['Starfinder 2e (ORC)' if o['src'] == 'SF2e' else 'Starfinder 1e (OGL)'],
                  'variants': [{'eng': o['name'], 'rus': o['short'], 'cr': lv, 'url': o['url']}]})
json.dump(cands, open(os.path.join(D, 'candidates_aon.json'), 'w', encoding='utf-8', newline='\n'), ensure_ascii=False, indent=1)
json.dump(rights, open(os.path.join(D, 'rights_aon.json'), 'w', encoding='utf-8', newline='\n'), ensure_ascii=False, indent=1)
miss = KEEP - {o['name'] for o in L}
print('всего', len(L), 'PI', len(rights['pi']), 'спорно', len(rights['doubt']), 'не мобы', len(rights['not_mob']),
      'кандидатов', len(cands), 'SF2e', sum(c['source'][0].startswith('Starfinder 2') for c in cands), 'не найдено в KEEP:', sorted(miss))
print('по уровню', collections.Counter('0-2' if c['crMax'] < 3 else '3-6' if c['crMax'] < 7 else '7-12' if c['crMax'] < 13 else '13+' for c in cands))
