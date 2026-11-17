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
    /// <item><b>Ярусы:</b> у каждого тайла-обрыва свой ярус 1…<see cref="ObjectLook.Levels"/> — длина ряда скалы во все
    /// 4 стороны (наименьшая, включая сам тайл; считается раз на чанк — <c>EnsureLevels</c>). Края массива — нижний ярус,
    /// к середине массив поднимается ступенями: чем шире, тем выше; северный край низкий — верх нависает над ущельем
    /// севернее лишь на высоту нижнего яруса (горы-лабиринт генератора v8 видны сверху). Высота яруса —
    /// <see cref="ObjectLook.Height"/> + <see cref="ObjectLook.Step"/> × (ярус − 1).</item>
    /// <item><b>Грани:</b> верх (светлее с ярусом, пятна лишайника и мха по узлам, трещины, редкие кусты), лицо к югу
    /// там, где южный сосед ниже (у подошвы темнее, тёплые и холодные пятна, трещины), бока со стороны более низкого
    /// соседа — наклонные (запад освещён, восток в тени). Северная кромка над более низким соседом — гребень. Тёмный
    /// контур — только у земли; внутренние уступы — мягкими линиями.</item>
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

        /// <summary>Сдвиг узлов сетки у массива, тайлов (один на все высоты узла).</summary>
        const float NodeJitter = 0.17f;

        /// <summary>Ярусов не больше (таблица <see cref="ObjectLook.Levels"/> — не выше).</summary>
        public const int MaxLevels = 6;

        /// <summary>
        /// Соседи дальше 1 тайла, от которых зависит вид блока (ярус — по ряду до <see cref="MaxLevels"/> тайлов):
        /// пришёл соседний чанк — пересобрать столько строк и столбцов у края (<see cref="WorldView"/>).
        /// </summary>
        public const int BlockReach = MaxLevels + 1;

        static readonly Color32 CliffLine = Srgb(34, 26, 40, 255), CliffHi = Srgb(236, 232, 244, 255),
            CliffSeam = Srgb(48, 40, 64, 255), CliffSideLit = Srgb(132, 122, 156, 255), CliffSideDark = Srgb(60, 52, 80, 255),
            CliffFaceHi = Srgb(110, 98, 134, 255), CliffFaceLow = Srgb(66, 57, 86, 255), CliffFaceWarm = Srgb(128, 104, 112, 255);

        // Верх: холодный серый камень, пятна тёплого лишайника и мха, снег на высоких гребнях.
        static readonly Color32[] CliffTop =
        {
            Srgb(138, 136, 150, 255), Srgb(146, 144, 158, 255), Srgb(154, 152, 166, 255), Srgb(162, 160, 174, 255),
            Srgb(170, 168, 182, 255), Srgb(178, 176, 190, 255),
        };

        static readonly Color32 CliffLichen = Srgb(176, 158, 128, 255), CliffMoss = Srgb(112, 140, 92, 255), CliffSnow = Srgb(238, 242, 250, 255);

        // Ярусы чанка: скала в чанке с полями BlockReach (из 9 чанков мира) и ярус каждого тайла чанка и рамки в 1 тайл.
        const int LvHalo = BlockReach, LvSpan = Size + (2 * LvHalo), LvGrid = Size + 2;
        readonly bool[] _lvBlock = new bool[LvSpan * LvSpan];
        readonly byte[] _lvLevel = new byte[LvGrid * LvGrid];
        ChunkCoord _lvChunk;
        int _lvCap = -1;

        /// <summary>Скала у чанка могла смениться (пришёл сосед, правка): ярусы посчитать заново при следующей сборке.</summary>
        public void InvalidateBlocks() => _lvCap = -1;

        void EnsureLevels(ChunkCoord c, int cap)
        {
            if (_lvCap == cap && _lvChunk.Equals(c))
            {
                return;
            }

            TileCoord corner = Torus.CornerOf(c);
            for (int j = 0; j < LvSpan; j++)
            {
                for (int i = 0; i < LvSpan; i++)
                {
                    _lvBlock[(j * LvSpan) + i] = BlockAt(corner.X - LvHalo + i, corner.Y - LvHalo + j);
                }
            }

            for (int ly = -1; ly <= Size; ly++)
            {
                for (int lx = -1; lx <= Size; lx++)
                {
                    int bi = ((ly + LvHalo) * LvSpan) + lx + LvHalo;
                    _lvLevel[((ly + 1) * LvGrid) + lx + 1] = (byte)(_lvBlock[bi] ? LevelLocal(bi, cap) : 0);
                }
            }

            _lvChunk = c;
            _lvCap = cap;
        }

        // Длина ряда скалы от тайла (включая его) в сторону шага step в сетке _lvBlock, не больше cap.
        int RunLocal(int bi, int step, int cap)
        {
            int n = 1;
            while (n < cap && _lvBlock[bi + (step * n)])
            {
                n++;
            }

            return n;
        }

        // Ярус 1…cap: наименьший из рядов скалы во все 4 стороны — массив поднимается от краёв к середине. Северный край —
        // нижний ярус: верх массива нависает над ущельем севернее лишь на высоту нижнего яруса, ущелье видно.
        int LevelLocal(int bi, int cap)
        {
            int l = RunLocal(bi, LvSpan, cap);
            if (l > 1) l = Math.Min(l, RunLocal(bi, -LvSpan, l));
            if (l > 1) l = Math.Min(l, RunLocal(bi, 1, l));
            if (l > 1) l = Math.Min(l, RunLocal(bi, -1, l));
            return l;
        }

        static float TierHeight(ObjectLook look, int level) => level <= 0 ? 0f : look.Height + look.Step * (level - 1);

        int LevelOf(int lx, int ly) => _lvLevel[((ly + 1) * LvGrid) + lx + 1];

        // Высоты блока и соседей по ярусам чанка c (тайл lx, ly чанка).
        Tiers TiersAt(ObjectLook look, ChunkCoord c, int lx, int ly)
        {
            int cap = Mathf.Clamp(look.Levels, 1, MaxLevels);
            EnsureLevels(c, cap);
            Tiers t;
            t.Level = LevelOf(lx, ly);
            t.H = TierHeight(look, t.Level);
            t.N = TierHeight(look, LevelOf(lx, ly - 1));
            t.S = TierHeight(look, LevelOf(lx, ly + 1));
            t.W = TierHeight(look, LevelOf(lx - 1, ly));
            t.E = TierHeight(look, LevelOf(lx + 1, ly));
            t.NW = TierHeight(look, LevelOf(lx - 1, ly - 1));
            t.NE = TierHeight(look, LevelOf(lx + 1, ly - 1));
            t.SW = TierHeight(look, LevelOf(lx - 1, ly + 1));
            t.SE = TierHeight(look, LevelOf(lx + 1, ly + 1));
            return t;
        }

        // Гладкое поле узлов 0…1 (хэш на сетке cell тайлов, билинейно) — пятна крупнее тайла, общие у соседей.
        float Blot(int cx, int cy, int cell, uint salt)
        {
            int gx = (int)Math.Floor(cx / (float)cell), gy = (int)Math.Floor(cy / (float)cell);
            float fx = (cx - (gx * cell)) / (float)cell, fy = (cy - (gy * cell)) / (float)cell;
            float a = NodeHash(gx * cell, gy * cell, salt), b = NodeHash((gx + 1) * cell, gy * cell, salt);
            float c = NodeHash(gx * cell, (gy + 1) * cell, salt), d = NodeHash((gx + 1) * cell, (gy + 1) * cell, salt);
            fx = fx * fx * (3f - (2f * fx));
            fy = fy * fy * (3f - (2f * fy));
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        // Цвет верха в узле: ярус, затенение, пятна лишайника и мха; снег — на верхнем ярусе.
        Color32 TopAt(int cx, int cy, Color32 baseTop, bool snow)
        {
            Color32 c = Mul(baseTop, Shade(cx, cy));
            float lichen = Blot(cx, cy, 5, 401), moss = Blot(cx, cy, 7, 409);
            c = Lerp(c, CliffLichen, Mathf.Clamp01((lichen - 0.5f) * 2f) * 0.4f);
            c = Lerp(c, CliffMoss, Mathf.Clamp01((moss - 0.66f) * 3f) * 0.5f);
            if (snow)
            {
                c = Lerp(c, CliffSnow, Mathf.Clamp01((Blot(cx, cy, 3, 419) - 0.62f) * 3f) * 0.7f); // редкие пятна снега
            }

            return c;
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

            // Выпуклые углы гребня срезаны, у земли — скруглены подошвой (не «бетонный блок»).
            if (lowN && lowW) tnw += new Vector2(0.18f, -0.24f);
            if (lowN && lowE) tne += new Vector2(-0.18f, -0.24f);
            Vector2 fsw = sw + new Vector2(0f, t.S), fse = se + new Vector2(0f, t.S);
            if (lowS && lowW && t.S <= TierEps && t.W <= TierEps) { fsw.x += 0.3f; tsw.x += 0.12f; }
            if (lowS && lowE && t.S <= TierEps && t.E <= TierEps) { fse.x -= 0.3f; tse.x -= 0.12f; }
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
                // Пятна цвета по узлам: тёплые охристые потёки и холодные участки — лицо не одной заливкой.
                Color32 hiL = Lerp(CliffFaceHi, CliffFaceWarm, Blot(tx, ty + 1, 3, 431) * 0.8f), hiR = Lerp(CliffFaceHi, CliffFaceWarm, Blot(tx + 1, ty + 1, 3, 431) * 0.8f);
                Color32 lowL = Mul(low, 0.9f + (0.2f * NodeHash(tx, ty + 1, 433))), lowR = Mul(low, 0.9f + (0.2f * NodeHash(tx + 1, ty + 1, 433)));
                if (lowW && t.W > t.S + TierEps) Quad(fsw, fsw, wsw, tsw, m.x, m.y, m.x, m.y, lowL, lowL, hiL, hiL);
                if (lowE && t.E > t.S + TierEps) Quad(fse, fse, tse, ese, m.x, m.y, m.x, m.y, lowR, lowR, hiR, hiR);
                Quad(fsw, fse, tsw, tse, m.x, m.y, m.x, m.y, lowL, lowR, hiL, hiR);
                FaceMarks(fsw, fse, tsw, tse, tx, ty, white);
            }

            Color32 top = CliffTop[Mathf.Clamp(t.Level - 1, 0, CliffTop.Length - 1)];
            bool snow = t.Level >= SnowLevel(it.Look);
            Quad(tsw, tse, tnw, tne, m.x, m.y, m.x, m.y,
                TopAt(tx, ty + 1, top, snow), TopAt(tx + 1, ty + 1, top, snow), TopAt(tx, ty, top, snow), TopAt(tx + 1, ty, top, snow));
            TopMarks(tsw, tse, tnw, tne, tx, ty, top, white);

            const float o = 0.08f; // контур, тайлов
            if (lowS)
            {
                if (t.S <= TierEps) Line(fsw, fse, o * 1.2f, white, CliffLine); // подошва
                else Line(fsw, fse, o * 0.45f, white, CliffSeam);               // основание уступа — мягче, не «полки»
                Line(tsw, tse, o * 0.4f, white, Lerp(top, CliffHi, 0.55f));         // кромка уступа
                if (lowW || t.W > h + TierEps || Mathf.Abs(t.SW - t.S) > TierEps) Line(fsw, tsw, o * 0.8f, white, CliffLine);
                if (lowE || t.E > h + TierEps || Mathf.Abs(t.SE - t.S) > TierEps) Line(fse, tse, o * 0.8f, white, CliffLine);
            }

            if (lowN)
            {
                // Гребень: светлая полоса под тёмным силуэтом.
                Vector2 dn = new Vector2(0f, -0.09f);
                Line(tnw + dn, tne + dn, o * 0.8f, white, Lerp(top, CliffHi, 0.6f));
                Line(tnw, tne, t.N <= TierEps ? o : o * 0.5f, white, t.N <= TierEps ? CliffLine : CliffSeam); // внутренний уступ — мягче
            }

            if (lowW)
            {
                if (t.W <= TierEps) Line(wsw, wnw, o, white, CliffLine); // у земли — контур; уступ сбоку — без линий
                Line(tsw, tnw, o * (t.W <= TierEps ? 0.5f : 0.3f), white, Lerp(top, CliffHi, 0.6f));
                if (lowN) Line(wnw, tnw, o, white, CliffLine);
            }

            if (lowE)
            {
                if (t.E <= TierEps) Line(ese, ene, o, white, CliffLine);
                Line(tse, tne, o * (t.E <= TierEps ? 0.6f : 0.35f), white, t.E <= TierEps ? CliffLine : CliffSeam);
                if (lowN) Line(ene, tne, o, white, CliffLine);
            }
        }

        // Снег — на верхнем ярусе массива, если ярусов не меньше 4 (широкий массив).
        static int SnowLevel(ObjectLook look) => look.Snow && look.Levels >= 4 ? look.Levels : int.MaxValue;

        // uv1 квадратов блока: опора — нижний край тайла, «высокий» (в круге своего персонажа полупрозрачен), не качается.
        // uv1.z = 2: гора не становится прозрачной перед своим персонажем — поверх неё силуэт персонажа (C-027, Objects.shader).
        static Vector4 BlockInfo(in Item it) => new Vector4(it.X, it.Y, 2f, 0f);

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
            if (r > 0.25f)
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

            // Редкие кусты и трава на уступах (не у кромки, не в снегу).
            if (look.PlantSprites.Length > 0 && !lowN && !lowS && t.Level < SnowLevel(look) && r5 < look.PlantTop)
            {
                ref ObjectAtlas.Entry pe = ref entries[look.PlantSprites[pick % look.PlantSprites.Length]];
                float sc = 0.45f + (r1 * 0.3f);
                float px = x0 + 0.5f + ((r4 - 0.5f) * 0.4f), py = yb + h + 0.25f + (r2 * 0.4f);
                float px0 = px - (pe.PivotX * sc), py0 = py - (pe.PivotY * sc);
                Quad(new Vector3(px0, py0), new Vector3(px0 + (pe.Width * sc), py0), new Vector3(px0, py0 + (pe.Height * sc)),
                    new Vector3(px0 + (pe.Width * sc), py0 + (pe.Height * sc)), pe, r3 < 0.5f, look.PlantTint);
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

        // Тень блока (C-017): объём тайла на высоте яруса — верх, сдвинутый шейдером по солнцу, и боковые грани по рёбрам,
        // за которыми сосед ниже (у равного соседа грань внутри массива — её закрывает верх соседа).
        void BlockShadow(in Item it, int tx, int ty, ObjectAtlas.Entry[] entries)
        {
            ref ObjectAtlas.Entry white = ref entries[it.Look.WhiteSprite];
            float k = it.Look.Shadow, h = it.Tier.H * k;
            float x0 = it.Lx, yb = it.Y;
            Vector2 sw = new Vector2(x0, yb) + Jitter(tx, ty + 1, NodeJitter), se = new Vector2(x0 + 1f, yb) + Jitter(tx + 1, ty + 1, NodeJitter);
            Vector2 nw = new Vector2(x0, yb + 1f) + Jitter(tx, ty, NodeJitter), ne = new Vector2(x0 + 1f, yb + 1f) + Jitter(tx + 1, ty, NodeJitter);
            Tiers t = it.Tier;
            float eps = TierEps;
            // Рёбра по кругу sw → se → ne → nw: юг, восток, север, запад.
            int edges = (t.S < t.H - eps ? 1 : 0) | (t.E < t.H - eps ? 2 : 0) | (t.N < t.H - eps ? 4 : 0) | (t.W < t.H - eps ? 8 : 0);
            Prism(sw, se, ne, nw, h, 255, edges, white.Uv.center);
        }
    }
}
