#nullable enable
using System;
using Orpheus.Core.Geometry;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Обрыв — скальный массив с высотой (C-016) в той же проекции, что стены E2-17: высота откладывается вверх по экрану
    /// без сжатия, у тайла видна верхняя грань и лицо к югу, строки сортируются с персонажем по нижней кромке.
    /// <list type="bullet">
    /// <item><b>Ярусы:</b> у каждого тайла-обрыва свой ярус 1…<see cref="ObjectLook.Levels"/> — длина ряда обрыва к югу,
    /// к западу и к востоку (наименьшая, включая сам тайл). Южная кромка и концы гряды — нижний ярус, вглубь массив
    /// поднимается ступенями к северному гребню: чем шире полоса, тем выше. Высота яруса —
    /// <see cref="ObjectLook.Height"/> + <see cref="ObjectLook.Step"/> × (ярус − 1).</item>
    /// <item><b>Грани:</b> верх (светлее с ярусом), лицо к югу там, где южный сосед ниже (у подошвы — темнее, пласт —
    /// сквозная линия по узлам сетки, трещины), бока со стороны более низкого соседа — наклонные (запад освещён, восток
    /// в тени). Северная кромка над более низким соседом — гребень (светлая полоса и тёмный контур).</item>
    /// <item><b>Контур:</b> тёмная подошва у земли, контур по силуэту; углы тайлов сдвинуты шумом узла сетки (общие углы
    /// соседей совпадают на любой высоте — грани ярусов стыкуются без щелей).</item>
    /// <item>Камни: редкие валуны на гребне и на уступах, осыпь у подошвы; тень — по высоте яруса, в общий слой.</item>
    /// </list>
    /// Рисуется кодом (белый квадрат листа × цвет вершины), картинки <c>cliff_top</c>/<c>cliff_face</c> не нужны.
    /// </summary>
    public sealed partial class ObjectMesh
    {
        /// <summary>Высоты блока-обрыва: свой ярус и высоты соседей (0 — земля или не обрыв).</summary>
        struct Tiers
        {
            public int Level;
            public float H, N, E, S, W, NW, NE, SW, SE;
        }

        const float TierEps = 0.01f;

        /// <summary>Сдвиг узлов сетки у гряды, тайлов (один на все высоты узла).</summary>
        const float NodeJitter = 0.13f;

        /// <summary>
        /// Соседи дальше 1 тайла, от которых зависит вид блока (ярус — по ряду до <see cref="ObjectLook.Levels"/> тайлов):
        /// пришёл соседний чанк — пересобрать столько строк и столбцов у края (<see cref="WorldView"/>).
        /// </summary>
        public const int BlockReach = 4;

        static readonly Color32 CliffLine = Srgb(34, 26, 40, 255), CliffHi = Srgb(236, 232, 244, 255),
            CliffSeam = Srgb(48, 40, 64, 255), CliffSideLit = Srgb(132, 122, 156, 255), CliffSideDark = Srgb(60, 52, 80, 255),
            CliffFaceHi = Srgb(110, 98, 134, 255), CliffFaceLow = Srgb(66, 57, 86, 255);

        static readonly Color32[] CliffTop =
        {
            Srgb(158, 160, 178, 255), Srgb(172, 174, 190, 255), Srgb(188, 190, 204, 255), Srgb(200, 202, 214, 255),
        };

        // Длина ряда блоков от тайла (включая его) в сторону (dx, dy), не больше cap.
        int Run(int tx, int ty, int dx, int dy, int cap)
        {
            int n = 1;
            while (n < cap && BlockAt(tx + dx * n, ty + dy * n))
            {
                n++;
            }

            return n;
        }

        // Ярус блока 1…cap: наименьший из рядов к югу, западу, востоку.
        int LevelAt(int tx, int ty, int cap)
        {
            int l = Run(tx, ty, 0, 1, cap);
            if (l > 1) l = Math.Min(l, Run(tx, ty, 1, 0, l));
            if (l > 1) l = Math.Min(l, Run(tx, ty, -1, 0, l));
            return l;
        }

        static float TierHeight(ObjectLook look, int level) => look.Height + look.Step * (level - 1);

        float NearHeight(ObjectLook look, int near, int bit, int tx, int ty) =>
            (near & bit) != 0 ? TierHeight(look, LevelAt(tx, ty, look.Levels)) : 0f;

        Tiers TiersAt(ObjectLook look, int tx, int ty, int near)
        {
            Tiers t;
            int cap = Math.Max(1, look.Levels);
            t.Level = LevelAt(tx, ty, cap);
            t.H = TierHeight(look, t.Level);
            t.N = NearHeight(look, near, North, tx, ty - 1);
            t.S = NearHeight(look, near, South, tx, ty + 1);
            t.W = NearHeight(look, near, West, tx - 1, ty);
            t.E = NearHeight(look, near, East, tx + 1, ty);
            t.NW = NearHeight(look, near, NorthWest, tx - 1, ty - 1);
            t.NE = NearHeight(look, near, NorthEast, tx + 1, ty - 1);
            t.SW = NearHeight(look, near, SouthWest, tx - 1, ty + 1);
            t.SE = NearHeight(look, near, SouthEast, tx + 1, ty + 1);
            return t;
        }

        // Ширина наклонного бока по перепаду высот.
        static float SideInset(float drop) => Mathf.Min(0.42f, 0.14f + 0.16f * drop);

        static Color32 Lerp(Color32 a, Color32 b, float k) => Color32.Lerp(a, b, k);

        float NodeHash(int cx, int cy, uint salt)
        {
            TileCoord t = Torus.Wrap(_map, new TileCoord(cx, cy));
            return GroundAtlas.Hash(t.X, t.Y, salt);
        }

        // Блок-обрыв: бока, лицо, верх, пласт и трещины, контур.
        void Block(in Item it, int tx, int ty, ObjectAtlas.Entry[] entries)
        {
            ref ObjectAtlas.Entry white = ref entries[it.Look.WhiteSprite];
            Tiers t = it.Tier;
            float h = t.H;
            bool lowN = t.N < h - TierEps, lowE = t.E < h - TierEps, lowS = t.S < h - TierEps, lowW = t.W < h - TierEps;
            float x0 = it.Lx, x1 = x0 + 1f, yb = it.Y, yt = yb + 1f;
            Vector2 nw = new Vector2(x0, yt) + Jitter(tx, ty, NodeJitter), ne = new Vector2(x1, yt) + Jitter(tx + 1, ty, NodeJitter);
            Vector2 sw = new Vector2(x0, yb) + Jitter(tx, ty + 1, NodeJitter), se = new Vector2(x1, yb) + Jitter(tx + 1, ty + 1, NodeJitter);
            Vector2 up = new Vector2(0f, h);
            Vector2 tnw = nw + up, tne = ne + up, tsw = sw + up, tse = se + up;

            // Наклонный бок: верх у более низкого соседа сужен. Угол сужается, только если и соседний по кромке тайл
            // сужен с той же стороны (или сам ниже) — иначе между ними щель.
            float inW = lowW ? SideInset(h - t.W) : 0f, inE = lowE ? SideInset(h - t.E) : 0f;
            if (lowW && (lowN || t.NW < t.N - TierEps)) tnw.x += inW;
            if (lowW && (lowS || t.SW < t.S - TierEps)) tsw.x += inW;
            if (lowE && (lowN || t.NE < t.N - TierEps)) tne.x -= inE;
            if (lowE && (lowS || t.SE < t.S - TierEps)) tse.x -= inE;

            // Выпуклые углы гребня срезаны, у земли — скруглены подошвой.
            if (lowN && lowW) tnw += new Vector2(0.1f, -0.16f);
            if (lowN && lowE) tne += new Vector2(-0.1f, -0.16f);
            Vector2 fsw = sw + new Vector2(0f, t.S), fse = se + new Vector2(0f, t.S);
            if (lowS && lowW && t.S <= TierEps && t.W <= TierEps) { fsw.x += 0.2f; tsw.x += 0.06f; }
            if (lowS && lowE && t.S <= TierEps && t.E <= TierEps) { fse.x -= 0.2f; tse.x -= 0.06f; }
            Vector2 wsw = lowS && t.S <= TierEps && t.W <= TierEps ? fsw : sw + new Vector2(0f, t.W), wnw = nw + new Vector2(0f, t.W);
            Vector2 ese = lowS && t.S <= TierEps && t.E <= TierEps ? fse : se + new Vector2(0f, t.E), ene = ne + new Vector2(0f, t.E);
            Vector2 m = white.Uv.center;

            if (lowW)
            {
                Quad(wsw, tsw, wnw, tnw, m.x, m.y, m.x, m.y, CliffSideLit, CliffSideLit, Lerp(CliffSideLit, CliffHi, 0.12f), Lerp(CliffSideLit, CliffHi, 0.12f));
            }

            if (lowE)
            {
                Quad(tse, ese, tne, ene, m.x, m.y, m.x, m.y, CliffSideDark);
            }

            if (lowS)
            {
                // Лицо: у подошвы темнее. Перепад к соседу сбоку выше, чем к южному, — клин между лицом и боком.
                float k = Mathf.Clamp01((h - t.S) / 2.4f);
                Color32 low = Lerp(CliffFaceHi, CliffFaceLow, 0.5f + 0.5f * k);
                if (lowW && t.W > t.S + TierEps) Quad(fsw, fsw, wsw, tsw, m.x, m.y, m.x, m.y, low, low, CliffFaceHi, CliffFaceHi);
                if (lowE && t.E > t.S + TierEps) Quad(fse, fse, tse, ese, m.x, m.y, m.x, m.y, low, low, CliffFaceHi, CliffFaceHi);
                Quad(fsw, fse, tsw, tse, m.x, m.y, m.x, m.y, low, low, CliffFaceHi, CliffFaceHi);
                FaceMarks(fsw, fse, tsw, tse, tx, ty, white);
            }

            Color32 top = CliffTop[Mathf.Clamp(t.Level - 1, 0, CliffTop.Length - 1)];
            Quad(tsw, tse, tnw, tne, m.x, m.y, m.x, m.y,
                Mul(top, Shade(tx, ty + 1)), Mul(top, Shade(tx + 1, ty + 1)), Mul(top, Shade(tx, ty)), Mul(top, Shade(tx + 1, ty)));
            TopMarks(tsw, tse, tnw, tne, tx, ty, top, white);

            const float o = 0.08f; // контур, тайлов
            if (lowS)
            {
                Line(fsw, fse, t.S <= TierEps ? o * 1.2f : o * 0.7f, white, CliffLine); // подошва / основание уступа
                Line(tsw, tse, o * 0.4f, white, Lerp(top, CliffHi, 0.55f));         // кромка уступа
                if (lowW || t.W > h + TierEps || Mathf.Abs(t.SW - t.S) > TierEps) Line(fsw, tsw, o * 0.8f, white, CliffLine);
                if (lowE || t.E > h + TierEps || Mathf.Abs(t.SE - t.S) > TierEps) Line(fse, tse, o * 0.8f, white, CliffLine);
            }

            if (lowN)
            {
                // Гребень: светлая полоса под тёмным силуэтом.
                Vector2 dn = new Vector2(0f, -0.09f);
                Line(tnw + dn, tne + dn, o * 0.8f, white, Lerp(top, CliffHi, 0.6f));
                Line(tnw, tne, o, white, CliffLine);
            }

            if (lowW)
            {
                Line(wsw, wnw, t.W <= TierEps ? o : o * 0.6f, white, CliffLine);
                Line(tsw, tnw, o * 0.5f, white, CliffHi);
                if (lowN) Line(wnw, tnw, o, white, CliffLine);
            }

            if (lowE)
            {
                Line(ese, ene, t.E <= TierEps ? o : o * 0.6f, white, CliffLine);
                Line(tse, tne, o * 0.6f, white, CliffLine);
                if (lowN) Line(ene, tne, o, white, CliffLine);
            }
        }

        // uv1 квадратов блока: опора — нижний край тайла, «высокий» (в круге своего персонажа полупрозрачен), не качается.
        static Vector4 BlockInfo(in Item it) => new Vector4(it.X, it.Y, 1f, 0f);

        // Лицо: вертикальные трещины-изломы (по хэшу тайла) от подошвы вверх — скала столбами, а не полки.
        void FaceMarks(Vector2 fsw, Vector2 fse, Vector2 tsw, Vector2 tse, int tx, int ty, in ObjectAtlas.Entry white)
        {
            float faceH = Mathf.Min(tsw.y - fsw.y, tse.y - fse.y);
            if (faceH < 0.3f)
            {
                return;
            }

            TileCoord c = Torus.Wrap(_map, new TileCoord(tx, ty));
            int cracks = 2 + (GroundAtlas.Hash(c.X, c.Y, 251) < 0.5f ? 0 : 1);
            for (int i = 0; i < cracks; i++)
            {
                uint k = (uint)(i * 8);
                float u = (i + 0.2f + 0.6f * GroundAtlas.Hash(c.X, c.Y, 257u + k)) / cracks;
                float v1 = 0.55f + 0.4f * GroundAtlas.Hash(c.X, c.Y, 263u + k);
                float bend = (GroundAtlas.Hash(c.X, c.Y, 269u + k) - 0.5f) * 0.16f;
                Vector2 bot = Vector2.Lerp(fsw, fse, u), topP = Vector2.Lerp(tsw, tse, u);
                Vector2 p = bot + new Vector2(0f, 0.04f), mid = Vector2.Lerp(bot, topP, v1 * 0.5f) + new Vector2(bend, 0f);
                Vector2 q = Vector2.Lerp(bot, topP, v1) + new Vector2(bend * 0.4f, 0f);
                Line(p, mid, 0.045f, white, CliffSeam);
                Line(mid, q, 0.035f, white, CliffSeam);
            }
        }

        // Верх: короткая трещина и светлый скол по хэшу тайла — камень, а не ровная плита.
        void TopMarks(Vector2 tsw, Vector2 tse, Vector2 tnw, Vector2 tne, int tx, int ty, Color32 top, in ObjectAtlas.Entry white)
        {
            TileCoord c = Torus.Wrap(_map, new TileCoord(tx, ty));
            float r = GroundAtlas.Hash(c.X, c.Y, 281);
            if (r > 0.7f)
            {
                return;
            }

            float u = 0.25f + 0.5f * GroundAtlas.Hash(c.X, c.Y, 283), v = 0.25f + 0.5f * GroundAtlas.Hash(c.X, c.Y, 293);
            Vector2 p = Vector2.Lerp(Vector2.Lerp(tsw, tse, u), Vector2.Lerp(tnw, tne, u), v);
            float ang = GroundAtlas.Hash(c.X, c.Y, 307) * Mathf.PI, len = 0.18f + 0.2f * r;
            var d = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang) * 0.6f) * len;
            Line(p - d, p + d * 0.4f, 0.04f, white, Mul(top, 0.72f));
            Line(p + d * 0.4f, p + d + new Vector2(0f, 0.06f), 0.035f, white, Mul(top, 0.72f));
            Line(p - d + new Vector2(0f, 0.05f), p + new Vector2(0f, 0.05f), 0.03f, white, Lerp(top, CliffHi, 0.5f));
        }

        void Deco(ObjectLook look, ObjectAtlas.Entry[] entries, int pick, float x, float y, float scale, bool flip, Color32 tint)
        {
            ref ObjectAtlas.Entry d = ref entries[look.DecoSprites[pick % look.DecoSprites.Length]];
            float x0 = x - d.PivotX * scale, x1 = x0 + d.Width * scale;
            float y0 = y - d.PivotY * scale, y1 = y0 + d.Height * scale;
            Quad(new Vector3(x0, y0), new Vector3(x1, y0), new Vector3(x0, y1), new Vector3(x1, y1), d, flip, tint);
        }

        // Камни массива (без коллизий): редкие валуны на гребне и уступах — силуэт не по линейке; осыпь у подошвы.
        void BlockDeco(in Item it, int tx, int ty, ObjectAtlas.Entry[] entries)
        {
            ObjectLook look = it.Look;
            if (look.DecoSprites.Length == 0)
            {
                return;
            }

            TileCoord c = Torus.Wrap(_map, new TileCoord(tx, ty));
            Tiers t = it.Tier;
            float x0 = it.Lx, yb = it.Y, h = t.H;
            bool lowN = t.N < h - TierEps, lowS = t.S < h - TierEps;
            float r1 = GroundAtlas.Hash(c.X, c.Y, 211), r2 = GroundAtlas.Hash(c.X, c.Y, 223), r3 = GroundAtlas.Hash(c.X, c.Y, 227);
            float r4 = GroundAtlas.Hash(c.X, c.Y, 229), r5 = GroundAtlas.Hash(c.X, c.Y, 233);
            int pick = (int)(r3 * 997f);
            if (r1 < (lowN ? look.DecoEdge : look.DecoTop))
            {
                // На гребне — у северной кромки, торчит над силуэтом; на уступе — ближе к южной кромке.
                float x = x0 + 0.5f + (r2 - 0.5f) * 0.5f;
                float y = yb + h + (lowN ? 0.78f : 0.12f + r4 * 0.3f);
                Deco(look, entries, pick, x, y, 0.55f + r5 * 0.35f, r4 < 0.5f, look.DecoTint);
            }

            if (lowS && t.S <= TierEps && r2 < look.DecoFoot)
            {
                Deco(look, entries, pick + 1, x0 + 0.5f + (r1 - 0.5f) * 0.7f, yb - 0.08f + r5 * 0.1f, 0.38f + r4 * 0.25f, r1 < 0.5f, look.RubbleTint);
            }

            if (t.W <= TierEps && (it.Near & West) == 0 && r4 < look.DecoFoot * 0.6f)
            {
                Deco(look, entries, pick + 2, x0 - 0.05f, yb + 0.15f + r2 * 0.5f, 0.35f + r5 * 0.25f, r3 < 0.5f, look.RubbleTint);
            }

            if (t.E <= TierEps && (it.Near & East) == 0 && r5 < look.DecoFoot * 0.6f)
            {
                Deco(look, entries, pick + 3, x0 + 1.05f, yb + 0.15f + r1 * 0.5f, 0.35f + r4 * 0.25f, r3 >= 0.5f, look.RubbleTint);
            }
        }

        // Тень блока: след тайла, сдвинутый по высоте яруса вправо-вниз (и на полпути — чтобы тень была сплошной).
        void BlockShadow(in Item it, int tx, int ty, ObjectAtlas.Entry[] entries)
        {
            ref ObjectAtlas.Entry white = ref entries[it.Look.WhiteSprite];
            float h = it.Tier.H * it.Look.Shadow;
            var d = new Vector2(h * ShearX * 0.6f, -h * ShearY);
            float x0 = it.Lx, yb = it.Y;
            Vector2 sw = new Vector2(x0, yb) + Jitter(tx, ty + 1, NodeJitter), se = new Vector2(x0 + 1f, yb) + Jitter(tx + 1, ty + 1, NodeJitter);
            Vector2 nw = new Vector2(x0, yb + 1f) + Jitter(tx, ty, NodeJitter), ne = new Vector2(x0 + 1f, yb + 1f) + Jitter(tx + 1, ty, NodeJitter);
            var black = new Color32(0, 0, 0, 255);
            Vector2 m = white.Uv.center;
            Quad(sw + d * 0.5f, se + d * 0.5f, nw + d * 0.5f, ne + d * 0.5f, m.x, m.y, m.x, m.y, black);
            Quad(sw + d, se + d, nw + d, ne + d, m.x, m.y, m.x, m.y, black);
        }
    }
}
