"""Contact sheet for the screenshot stand (tools/shots.ps1, plan step 0.3).

Usage: python tools/shots_sheet.py <folder with PNG> [--out sheet.png] [--cols 3] [--thumb-width 640]
Every *.png of the folder except the sheet itself goes to a grid, sorted by name, with the file name as a caption.
Exit code 0 - sheet written, 1 - no pictures.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("folder")
    ap.add_argument("--out", default=None)
    ap.add_argument("--cols", type=int, default=3)
    ap.add_argument("--thumb-width", type=int, default=640)
    a = ap.parse_args()

    folder = Path(a.folder)
    out = Path(a.out) if a.out else folder / "sheet.png"
    files = sorted(p for p in folder.glob("*.png") if p.resolve() != out.resolve())
    if not files:
        print(f"[shots_sheet] no PNG in {folder}", file=sys.stderr)
        return 1

    tw = a.thumb_width
    th = tw * 9 // 16
    caption = 28
    gap = 8
    cols = max(1, min(a.cols, len(files)))
    rows = (len(files) + cols - 1) // cols
    sheet = Image.new("RGB", (gap + cols * (tw + gap), gap + rows * (th + caption + gap)), (24, 24, 28))
    draw = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.load_default(size=20)
    except TypeError:  # Pillow < 10.1
        font = ImageFont.load_default()

    for i, p in enumerate(files):
        x = gap + (i % cols) * (tw + gap)
        y = gap + (i // cols) * (th + caption + gap)
        with Image.open(p) as im:
            thumb = im.convert("RGB").resize((tw, th), Image.LANCZOS)
            size = im.size
        sheet.paste(thumb, (x, y))
        draw.text((x + 4, y + th + 2), f"{p.stem}  {size[0]}x{size[1]}", fill=(230, 230, 230), font=font)

    sheet.save(out)
    print(f"[shots_sheet] {out} ({sheet.size[0]}x{sheet.size[1]}, {len(files)} shots)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
