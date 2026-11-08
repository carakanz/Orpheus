"""Тесты конвейера tools/art_process.py (1-024): порог покрытия cover, шов с уголком, поворот оружия, стена на всю ширину.

    python tools/test_art_process.py          # или: python -m unittest discover -s tools -p "test_*.py"

Картинки синтетические (512 px, в памяти): предмет с тёмным контуром стиля Б на ровном фоне.
"""
from __future__ import annotations

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
import art_process as ap  # noqa: E402

GREY_BG = (182, 177, 166)     # фон эталонов («very light warm grey»)
SKY_BG = (150, 205, 235)      # контрастный фон для серого предмета (наборы 1024: camera_*_blue)
OUTLINE = (50, 12, 30)        # контур стиля Б


def blob(bg, fill, size=512, box=(126, 116, 386, 396), line=8) -> Image.Image:
    """Валун: скруглённый блок с контуром и светлой верхней гранью."""
    im = Image.new("RGB", (size, size), bg)
    d = ImageDraw.Draw(im)
    d.rounded_rectangle(box, radius=40, fill=fill, outline=OUTLINE, width=line)
    x0, y0, x1, y1 = box
    d.line([(x0 + 20, y0 + 90), (x1 - 20, y0 + 90)], fill=OUTLINE, width=line)  # ребро верхней грани
    return im


class CoverTest(unittest.TestCase):
    def test_grey_on_grey_is_rejected(self):
        # backlog «[арт]», П по A-002: грани цвета фона уходят в дыры — от силуэта остаётся контур
        cut, info = ap.cut_object(blob(GREY_BG, (188, 183, 172)))
        self.assertLess(info["cover"], ap.MIN_COVER)
        with self.assertRaises(ap.CoverError):
            ap.check_cover(info)

    def test_grey_on_contrast_background_passes(self):
        cut, info = ap.cut_object(blob(SKY_BG, (188, 183, 172)))
        self.assertGreaterEqual(info["cover"], ap.MIN_COVER)
        ap.check_cover(info)

    def test_coloured_on_grey_passes(self):
        _, info = ap.cut_object(blob(GREY_BG, (59, 147, 133)))
        self.assertGreaterEqual(info["cover"], 0.2)
        ap.check_cover(info)

    def test_thin_item_needs_explicit_threshold(self):
        im = Image.new("RGB", (512, 512), GREY_BG)
        ImageDraw.Draw(im).line([(90, 420), (420, 90)], fill=OUTLINE, width=40)  # стрела по диагонали (≈ 80 px древка на 1024)
        ImageDraw.Draw(im).line([(90, 420), (420, 90)], fill=(161, 126, 98), width=26)
        _, info = ap.cut_object(im)
        with self.assertRaises(ap.CoverError):
            ap.check_cover(info)
        ap.check_cover(info, 0.02)

    def test_process_and_cli_fail_on_low_cover(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "grey.png"
            blob(GREY_BG, (188, 183, 172)).save(src)
            with self.assertRaises(ap.CoverError):
                ap.process(src, "object", (128, 192), 182)
            out = Path(tmp) / "out.png"
            r = subprocess.run([sys.executable, str(Path(ap.__file__)), "object", str(src), str(out)],
                               capture_output=True, text=True, encoding="utf-8")
            self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
            self.assertIn("cover", r.stderr)
            self.assertFalse(out.exists())


class SeamTest(unittest.TestCase):
    def test_mark_corner_does_not_break_seam(self):
        rng = np.random.default_rng(3)
        noise = rng.normal(0, 1, (32, 32, 3))
        base = np.kron(noise, np.ones((4, 4, 1)))  # плавная, по краю бесшовная (np.roll не меняет перепады)
        tile = Image.fromarray(np.clip(120 + 20 * base, 0, 255).astype(np.uint8))
        tile = ap.make_seamless(tile)
        clean = ap.seam_ratio(tile)
        self.assertLessEqual(ap.seam_ratio(ap.mark(tile.convert("RGBA"))), max(clean * 1.1, ap.SEAM_MAX))


class ShapeTest(unittest.TestCase):
    def test_upright_puts_upper_end_on_top(self):
        im = Image.new("RGBA", (400, 400), (0, 0, 0, 0))
        d = ImageDraw.Draw(im)
        d.line([(60, 340), (340, 60)], fill=(160, 120, 90, 255), width=24)   # рукоять снизу-слева
        d.ellipse([300, 20, 380, 100], fill=(255, 0, 0, 255))                # «голова» сверху-справа
        up = ap.bbox_crop(ap.upright(im))
        self.assertGreater(up.height, up.width * 2)                          # стоит вертикально
        a = np.asarray(up)
        red = (a[..., 0] > 200) & (a[..., 1] < 60) & (a[..., 3] > 200)
        ys = np.nonzero(red)[0]
        self.assertLess(ys.mean(), up.height * 0.3)                          # голова наверху

    def test_fill_spans_full_width(self):
        im = Image.new("RGBA", (600, 300), (0, 0, 0, 0))
        ImageDraw.Draw(im).rectangle([50, 40, 550, 260], fill=(149, 57, 32, 255))
        out = ap.fit_fill(im, (128, 192), 192, 192)
        a = np.asarray(out.getchannel("A"))
        self.assertTrue((a[:, 0] > 200).all() and (a[:, -1] > 200).all())
        self.assertTrue((a[-1] > 200).all())


if __name__ == "__main__":
    unittest.main(verbosity=2)
