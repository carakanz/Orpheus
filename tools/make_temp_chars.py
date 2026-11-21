"""Генератор временных спрайтов врагов по видам (карточка C-053).

9 заглушек: разбойник с мечом, разбойник с луком, волк — по три вида (side/front/back),
ключи загрузки char_<архетип>_<вид> (EnemyVisuals.TryViews, ObjectImport префикс «char_»).
Правило временных ассетов то же, что у make_temp_assets.py: папка _TEMP, префикс TEMP_,
строка в art/manifest.csv со статусом placeholder, пурпурный уголок #FF00FF 8 x 8 px.

Холст 128 x 192 (00 5.1): нижний квадрат — тайл, ноги/лапы опираются вниз по центру
(центр тайла y = 128, ступни ~y = 182). Вид side смотрит на восток (вправо); запад —
отражение по X делает клиент (ActorRig, FlipX только для Side).
Картинки детерминированы (фиксированное зерно): повторный запуск даёт те же байты.

Запуск из корня рабочей копии:  python tools/make_temp_chars.py
"""

from __future__ import annotations

import random
import sys

from PIL import Image, ImageDraw

from make_temp_assets import jitter, mark_corner, rel, update_manifest, TEMP_DIR

OUTLINE = (30, 24, 40, 255)
SKIN = (214, 170, 130, 255)
STEEL = (188, 190, 196, 255)
WOOD = (120, 84, 50, 255)
BAND = (168, 58, 48, 255)  # повязка разбойника
WOLF = (112, 106, 100, 255)


def shade(color: tuple, k: int) -> tuple:
    """Тот же цвет темнее/светлее на k (без зерна): детерминированный тон для лап/ушей."""
    return tuple(max(0, min(255, c + k)) for c in color[:3]) + (255,)


def canvas() -> tuple[Image.Image, ImageDraw.ImageDraw]:
    img = Image.new("RGBA", (128, 192), (0, 0, 0, 0))
    return img, ImageDraw.Draw(img)


def shadow(d: ImageDraw.ImageDraw, cx: int = 64, w: int = 60) -> None:
    """Тень на земле у опоры (центр нижнего тайла y ≈ 178)."""
    d.ellipse([cx - w // 2, 168, cx + w // 2, 188], fill=(0, 0, 0, 90))


def sword_blade(d: ImageDraw.ImageDraw, x0: int, y0: int, x1: int, y1: int, w: int = 4) -> None:
    """Клинок с гардой: линия стали, гарда и рукоять на конце у руки."""
    d.line([x0, y0, x1, y1], fill=STEEL, width=w)
    d.line([x0, y0, x1, y1], fill=(150, 152, 158, 255), width=1)
    dx, dy = x1 - x0, y1 - y0
    n = max(1, (dx * dx + dy * dy) ** 0.5)
    px, py = -dy / n, dx / n  # перпендикуляр клинку — гарда
    d.line([x1 - px * 6, y1 - py * 6, x1 + px * 6, y1 + py * 6], fill=(70, 50, 30, 255), width=3)


def bandit_body(rng: random.Random, d: ImageDraw.ImageDraw, tunic: tuple) -> None:
    """Общее туловище разбойника: ноги, пояс, торс, голова с повязкой."""
    d.rectangle([50, 132, 60, 182], fill=(74, 60, 46, 255), outline=OUTLINE)
    d.rectangle([68, 132, 78, 182], fill=(74, 60, 46, 255), outline=OUTLINE)
    d.polygon([(42, 138), (86, 138), (80, 78), (48, 78)], fill=tunic, outline=OUTLINE)
    d.rectangle([42, 128, 86, 138], fill=(64, 44, 32, 255), outline=OUTLINE)  # пояс
    d.ellipse([46, 34, 82, 74], fill=SKIN, outline=OUTLINE)
    d.pieslice([44, 30, 84, 66], 180, 360, fill=(56, 40, 28, 255))  # капюшон/волосы
    d.rectangle([46, 48, 82, 56], fill=BAND, outline=OUTLINE)  # повязка


def bandit_sword_side(rng: random.Random) -> Image.Image:
    """Разбойник с мечом, вид сбоку (смотрит на восток): меч вытянут вперёд."""
    img, d = canvas()
    shadow(d)
    d.rectangle([54, 130, 64, 182], fill=(74, 60, 46, 255), outline=OUTLINE)  # задняя нога
    d.rectangle([66, 132, 76, 180], fill=(84, 68, 52, 255), outline=OUTLINE)  # передняя, шаг
    d.polygon([(46, 138), (84, 138), (80, 78), (50, 78)], fill=(98, 76, 54, 255), outline=OUTLINE)
    d.ellipse([50, 34, 84, 72], fill=SKIN, outline=OUTLINE)
    d.pieslice([48, 30, 86, 64], 180, 360, fill=(56, 40, 28, 255))
    d.rectangle([50, 46, 84, 54], fill=BAND, outline=OUTLINE)
    d.rectangle([62, 84, 96, 94], fill=SKIN, outline=OUTLINE)  # рука с мечом вперёд
    sword_blade(d, 96, 89, 124, 84)
    for _ in range(10):  # заплатки на тунике
        x, y = rng.randint(52, 78), rng.randint(88, 128)
        d.ellipse([x - 3, y - 3, x + 3, y + 3], fill=jitter(rng, (98, 76, 54), 16))
    return img


def bandit_sword_front(rng: random.Random) -> Image.Image:
    """Разбойник с мечом, вид спереди: меч поднят у плеча, смотрит на зрителя."""
    img, d = canvas()
    shadow(d)
    bandit_body(rng, d, (98, 76, 54, 255))
    d.rectangle([36, 80, 46, 126], fill=SKIN, outline=OUTLINE)  # левая рука опущена
    d.rectangle([82, 62, 92, 110], fill=SKIN, outline=OUTLINE)  # правая рука поднята
    sword_blade(d, 88, 62, 98, 18)  # клинок вверх у плеча
    d.ellipse([58, 52, 64, 58], fill=(30, 24, 40, 255))  # глаза смотрят на зрителя
    d.ellipse([66, 52, 72, 58], fill=(30, 24, 40, 255))
    for _ in range(10):
        x, y = rng.randint(50, 78), rng.randint(88, 128)
        d.ellipse([x - 3, y - 3, x + 3, y + 3], fill=jitter(rng, (98, 76, 54), 16))
    return img


def bandit_sword_back(rng: random.Random) -> Image.Image:
    """Разбойник с мечом, вид со спины: плащ, меч за спиной, лица нет."""
    img, d = canvas()
    shadow(d)
    d.rectangle([50, 132, 60, 182], fill=(74, 60, 46, 255), outline=OUTLINE)
    d.rectangle([68, 132, 78, 182], fill=(74, 60, 46, 255), outline=OUTLINE)
    d.polygon([(40, 150), (88, 150), (80, 74), (48, 74)], fill=(84, 60, 46, 255), outline=OUTLINE)  # плащ
    d.ellipse([46, 34, 82, 74], fill=(56, 40, 28, 255), outline=OUTLINE)  # затылок
    d.rectangle([46, 48, 82, 56], fill=BAND, outline=OUTLINE)  # повязка сзади
    sword_blade(d, 44, 128, 86, 66, w=5)  # меч за спиной по диагонали
    for _ in range(12):  # складки плаща
        x = rng.randint(46, 82)
        d.line([x, rng.randint(90, 120), x + rng.randint(-4, 4), rng.randint(130, 146)],
               fill=jitter(rng, (64, 44, 32), 12), width=2)
    return img


def bandit_bow_side(rng: random.Random) -> Image.Image:
    """Разбойник с луком, вид сбоку: лук выставлен вперёд (на восток), тетива натянута."""
    img, d = canvas()
    shadow(d)
    d.rectangle([54, 130, 64, 182], fill=(64, 56, 70, 255), outline=OUTLINE)
    d.rectangle([66, 132, 76, 180], fill=(74, 66, 80, 255), outline=OUTLINE)
    d.polygon([(46, 138), (84, 138), (80, 78), (50, 78)], fill=(70, 84, 66, 255), outline=OUTLINE)
    d.ellipse([50, 34, 84, 72], fill=SKIN, outline=OUTLINE)
    d.pieslice([48, 30, 86, 64], 180, 360, fill=(56, 40, 28, 255))
    d.rectangle([50, 46, 84, 54], fill=BAND, outline=OUTLINE)
    d.arc([84, 56, 116, 122], 270, 90, fill=WOOD, width=5)  # лук вперёд (дуга на восток)
    d.line([90, 62, 90, 116], fill=(222, 214, 196, 255), width=1)  # тетива
    d.rectangle([62, 82, 92, 90], fill=SKIN, outline=OUTLINE)  # рука держит лук
    d.line([56, 86, 88, 88], fill=(150, 120, 80, 255), width=2)  # стрела
    for _ in range(8):
        x, y = rng.randint(52, 78), rng.randint(88, 128)
        d.ellipse([x - 3, y - 3, x + 3, y + 3], fill=jitter(rng, (70, 84, 66), 16))
    return img


def bandit_bow_front(rng: random.Random) -> Image.Image:
    """Разбойник с луком, вид спереди: лук у ноги, колчан за плечом."""
    img, d = canvas()
    shadow(d)
    bandit_body(rng, d, (70, 84, 66, 255))
    d.line([50, 118, 36, 62], fill=WOOD, width=4)  # лук вертикально у бока
    d.arc([28, 58, 52, 122], 240, 340, fill=WOOD, width=3)
    d.rectangle([36, 80, 46, 126], fill=SKIN, outline=OUTLINE)
    d.rectangle([82, 80, 92, 126], fill=SKIN, outline=OUTLINE)
    d.line([70, 76, 88, 120], fill=(96, 70, 44, 255), width=8)  # ремень колчана
    d.rectangle([78, 96, 94, 132], fill=(96, 70, 44, 255), outline=OUTLINE)  # колчан
    for i in range(3):  # стрелы в колчане
        d.line([82 + i * 4, 96, 84 + i * 4, 82], fill=(150, 120, 80, 255), width=1)
    d.ellipse([58, 52, 64, 58], fill=(30, 24, 40, 255))
    d.ellipse([66, 52, 72, 58], fill=(30, 24, 40, 255))
    return img


def bandit_bow_back(rng: random.Random) -> Image.Image:
    """Разбойник с луком, вид со спины: колчан с оперением, лук за спиной."""
    img, d = canvas()
    shadow(d)
    d.rectangle([50, 132, 60, 182], fill=(64, 56, 70, 255), outline=OUTLINE)
    d.rectangle([68, 132, 78, 182], fill=(64, 56, 70, 255), outline=OUTLINE)
    d.polygon([(42, 138), (86, 138), (80, 78), (48, 78)], fill=(70, 84, 66, 255), outline=OUTLINE)
    d.ellipse([46, 34, 82, 74], fill=(56, 40, 28, 255), outline=OUTLINE)
    d.rectangle([46, 48, 82, 56], fill=BAND, outline=OUTLINE)
    d.line([58, 74, 42, 116], fill=WOOD, width=4)  # лук за спиной
    d.rectangle([70, 84, 88, 126], fill=(96, 70, 44, 255), outline=OUTLINE)  # колчан
    for i in range(3):  # оперение стрел
        d.line([74 + i * 5, 84, 74 + i * 5, 70], fill=(150, 120, 80, 255), width=1)
        d.polygon([(74 + i * 5 - 2, 74), (74 + i * 5 + 2, 74), (74 + i * 5, 68)],
                  fill=(210, 205, 195, 255))
    d.line([70, 76, 52, 122], fill=(64, 44, 32, 255), width=6)  # ремень
    return img


def wolf_side(rng: random.Random) -> Image.Image:
    """Волк, вид сбоку (смотрит на восток): четыре лапы, морда вперёд, хвост назад."""
    img, d = canvas()
    shadow(d, w=84)
    for x in (38, 52, 76, 90):  # лапы
        d.rectangle([x, 148, x + 9, 182], fill=shade(WOLF, -8), outline=OUTLINE)
    d.ellipse([24, 104, 104, 162], fill=WOLF, outline=OUTLINE)  # корпус
    d.ellipse([84, 88, 120, 126], fill=jitter(rng, WOLF, 6), outline=OUTLINE)  # голова
    d.polygon([(112, 96), (126, 102), (112, 112)], fill=shade(WOLF, -10), outline=OUTLINE)  # морда
    d.polygon([(88, 84), (94, 66), (100, 86)], fill=shade(WOLF, -10), outline=OUTLINE)  # ухо
    d.polygon([(100, 84), (106, 68), (112, 88)], fill=shade(WOLF, -10), outline=OUTLINE)
    d.ellipse([104, 98, 110, 104], fill=(28, 24, 20, 255))  # глаз
    d.polygon([(24, 112), (4, 96), (10, 122), (26, 128)], fill=shade(WOLF, -6), outline=OUTLINE)  # хвост
    d.ellipse([30, 116, 60, 146], fill=jitter(rng, WOLF, 14))  # пятно шерсти
    d.ellipse([60, 112, 88, 138], fill=jitter(rng, WOLF, 10))
    return img


def wolf_front(rng: random.Random) -> Image.Image:
    """Волк, вид спереди: морда на зрителя, грудь, две передние лапы."""
    img, d = canvas()
    shadow(d, w=76)
    d.rectangle([42, 140, 54, 182], fill=shade(WOLF, -8), outline=OUTLINE)
    d.rectangle([74, 140, 86, 182], fill=shade(WOLF, -8), outline=OUTLINE)
    d.polygon([(30, 150), (98, 150), (90, 96), (38, 96)], fill=WOLF, outline=OUTLINE)  # грудь
    d.ellipse([40, 40, 88, 96], fill=jitter(rng, WOLF, 6), outline=OUTLINE)  # голова
    d.polygon([(52, 72), (76, 72), (68, 92), (60, 92)], fill=shade(WOLF, -14), outline=OUTLINE)  # морда
    d.ellipse([61, 82, 67, 88], fill=(28, 24, 20, 255))  # нос
    d.polygon([(40, 48), (46, 22), (56, 44)], fill=shade(WOLF, -10), outline=OUTLINE)  # уши
    d.polygon([(72, 44), (82, 22), (88, 48)], fill=shade(WOLF, -10), outline=OUTLINE)
    d.ellipse([50, 58, 56, 64], fill=(28, 24, 20, 255))  # глаза
    d.ellipse([72, 58, 78, 64], fill=(28, 24, 20, 255))
    for _ in range(8):
        x, y = rng.randint(42, 86), rng.randint(104, 140)
        d.ellipse([x - 4, y - 3, x + 4, y + 3], fill=jitter(rng, WOLF, 12))
    return img


def wolf_back(rng: random.Random) -> Image.Image:
    """Волк, вид со спины: затылок, спина, хвост, задние лапы."""
    img, d = canvas()
    shadow(d, w=80)
    for x in (40, 56, 72, 82):
        d.rectangle([x, 146, x + 9, 182], fill=shade(WOLF, -8), outline=OUTLINE)
    d.polygon([(28, 152), (100, 152), (92, 96), (36, 96)], fill=WOLF, outline=OUTLINE)  # спина
    d.ellipse([44, 44, 84, 92], fill=shade(WOLF, -6), outline=OUTLINE)  # затылок
    d.polygon([(44, 52), (50, 26), (60, 48)], fill=shade(WOLF, -12), outline=OUTLINE)
    d.polygon([(68, 48), (78, 26), (84, 52)], fill=shade(WOLF, -12), outline=OUTLINE)
    d.polygon([(30, 120), (10, 138), (24, 150), (36, 136)], fill=shade(WOLF, -6), outline=OUTLINE)  # хвост
    d.line([64, 100, 64, 148], fill=shade(WOLF, -16), width=3)  # хребет
    for _ in range(8):
        x, y = rng.randint(40, 88), rng.randint(104, 140)
        d.ellipse([x - 4, y - 3, x + 4, y + 3], fill=jitter(rng, WOLF, 12))
    return img


# имя файла → (генератор, примечание для манифеста)
ASSETS = {
    "TEMP_char_bandit_sword_side.png": (bandit_sword_side, "разбойник с мечом, вид сбоку 128x192 (C-053)"),
    "TEMP_char_bandit_sword_front.png": (bandit_sword_front, "разбойник с мечом, вид спереди 128x192 (C-053)"),
    "TEMP_char_bandit_sword_back.png": (bandit_sword_back, "разбойник с мечом, вид со спины 128x192 (C-053)"),
    "TEMP_char_bandit_bow_side.png": (bandit_bow_side, "разбойник с луком, вид сбоку 128x192 (C-053)"),
    "TEMP_char_bandit_bow_front.png": (bandit_bow_front, "разбойник с луком, вид спереди 128x192 (C-053)"),
    "TEMP_char_bandit_bow_back.png": (bandit_bow_back, "разбойник с луком, вид со спины 128x192 (C-053)"),
    "TEMP_char_wolf_side.png": (wolf_side, "волк, вид сбоку 128x192 (C-053)"),
    "TEMP_char_wolf_front.png": (wolf_front, "волк, вид спереди 128x192 (C-053)"),
    "TEMP_char_wolf_back.png": (wolf_back, "волк, вид со спины 128x192 (C-053)"),
}


def main() -> int:
    TEMP_DIR.mkdir(parents=True, exist_ok=True)
    entries = []
    for i, (name, (gen, notes)) in enumerate(ASSETS.items()):
        img = gen(random.Random(20261121 + i))
        mark_corner(img)
        out = TEMP_DIR / name
        img.save(out, format="PNG", optimize=False)
        entries.append((rel(out), notes))
        print(f"{rel(out)}  {img.width}x{img.height}")
    added = update_manifest(entries)
    print(f"готово: {len(entries)} картинок, в манифест добавлено строк: {added}")
    return 0


if __name__ == "__main__":
    # консоль Windows в cp1252 не печатает кириллицу
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parent))
    sys.exit(main())
