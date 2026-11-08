"""Проверка временных ассетов (правило временных ассетов, CLAUDE.md).

Печатает список оставшихся заглушек (файлы TEMP_* в client/Assets) и их число.
Сами заглушки — не ошибка. Ошибка (выход 1) — нарушение правила пометки:
  - файл TEMP_* лежит не в папке _TEMP;
  - файла нет в art/manifest.csv или его статус не placeholder;
  - на картинке нет пурпурного уголка #FF00FF 8 x 8 px (левый верхний угол);
  - в манифесте строка placeholder, а файла нет;
  - в манифесте строка final, а имя файла начинается с TEMP_ (или наоборот: placeholder без TEMP_).
Без нарушений — выход 0.

Запуск из корня репозитория:  python tools/check_temp_assets.py
"""

from __future__ import annotations

import csv
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "client" / "Assets"
MANIFEST = ROOT / "art" / "manifest.csv"

PREFIX = "TEMP_"
TEMP_FOLDER = "_TEMP"
MARK_COLOR = (255, 0, 255, 255)
MARK_SIZE = 8
IMAGE_EXT = {".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".gif", ".tif", ".tiff", ".webp"}


def rel(p: Path) -> str:
    return p.relative_to(ROOT).as_posix()


def has_mark(path: Path) -> bool:
    with Image.open(path) as img:
        rgba = img.convert("RGBA")
        if rgba.width < MARK_SIZE or rgba.height < MARK_SIZE:
            return False
        px = rgba.load()
        return all(px[x, y] == MARK_COLOR for x in range(MARK_SIZE) for y in range(MARK_SIZE))


def main() -> int:
    problems: list[str] = []

    found = sorted(
        p for p in ASSETS.rglob(PREFIX + "*")
        if p.is_file() and p.suffix != ".meta"
    ) if ASSETS.exists() else []

    rows: dict[str, dict] = {}
    if MANIFEST.exists():
        with MANIFEST.open(encoding="utf-8", newline="") as f:
            for r in csv.DictReader(f):
                rows[r["path"]] = r
    else:
        problems.append(f"нет файла {rel(MANIFEST)}")

    print("Заглушки (TEMP_):")
    for p in found:
        path = rel(p)
        row = rows.get(path)
        status = row["status"] if row else "—"
        print(f"  {path}  [{status}]")
        if TEMP_FOLDER not in p.relative_to(ASSETS).parts:
            problems.append(f"{path}: лежит не в папке {TEMP_FOLDER}")
        if row is None:
            problems.append(f"{path}: нет строки в {rel(MANIFEST)}")
        elif row["status"] != "placeholder":
            problems.append(f"{path}: в манифесте статус {row['status']!r}, ожидается 'placeholder'")
        if p.suffix.lower() in IMAGE_EXT and not has_mark(p):
            problems.append(f"{path}: нет пурпурного уголка #FF00FF {MARK_SIZE}x{MARK_SIZE} px")
    print(f"Всего заглушек: {len(found)}")

    found_set = {rel(p) for p in found}
    for path, r in rows.items():
        name = Path(path).name
        if r["status"] == "placeholder":
            if path not in found_set:
                problems.append(f"манифест: {path} — placeholder, но файла нет")
            if not name.startswith(PREFIX):
                problems.append(f"манифест: {path} — placeholder без префикса {PREFIX}")
        elif r["status"] == "final" and name.startswith(PREFIX):
            problems.append(f"манифест: {path} — final, но имя начинается с {PREFIX}")

    placeholders = sum(1 for r in rows.values() if r["status"] == "placeholder")
    print(f"В манифесте placeholder: {placeholders}")

    if problems:
        print("\nНАРУШЕНИЯ правила временных ассетов:")
        for msg in problems:
            print("  - " + msg)
        return 1
    print("Нарушений нет: файлы совпадают с манифестом, у каждой картинки пурпурный уголок.")
    return 0


if __name__ == "__main__":
    # консоль Windows в cp1252 не печатает кириллицу
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.exit(main())
