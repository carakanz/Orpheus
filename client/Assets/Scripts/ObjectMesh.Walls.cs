#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Стены, забор и дверь «с высотой» (E2-17, вид сверху с наклоном — между Factorio и RimWorld): постройка-ячеек
    /// (E2-12, ячейки 0,5 × 0,5) рисуется кодом в меше строки объектов, вместе с деревьями и мебелью, и сортируется по
    /// нижней кромке с существами.
    /// <list type="bullet">
    /// <item><b>Стена:</b> у каждой ячейки — верхняя грань, поднятая на <see cref="WallHeight"/> (светлая: каменные блоки или
    /// доски, светлая кромка с севера), и лицевая грань к югу там, где южнее ячейки нет стены (темнее: кладка рядами
    /// со сдвигом или вертикальные доски, цоколь). Тёмный контур — по свободным краям верха и лица. Соединение — по
    /// соседним ячейкам (и через границу тайла): прямые, углы, Т, крест, торцы выходят сами; вертикальный участок
    /// виден сверху колонной блоков, лицо — только у южного конца; угловые и концевые блоки светлее (блок-столб).</item>
    /// <item><b>Забор:</b> столбик в середине ячейки и две жерди к соседним ячейкам забора (или стены) — сквозь видно.</item>
    /// <item><b>Дверь:</b> закрытая — сплошное светлое полотно досками от косяка до косяка, высотой со стену, с оковкой и
    /// ручкой; открытая — полотно повёрнуто на петле на 90° внутрь тайла и видно ребром у одного косяка, проём пуст.</item>
    /// <item><b>Тень</b> — в общий слой теней: след ячейки, протянутый вправо-вниз на высоту.</item>
    /// </list>
    /// Вид — таблица «объект → цвета» (<see cref="Looks"/>), высота лица — одна константа <see cref="WallHeight"/>.
    /// Свой персонаж за стеной: квадраты стены помечены «высокими» — в круге вокруг персонажа полупрозрачны (как кроны).
    /// </summary>
    public sealed partial class ObjectMesh
    {
        /// <summary>
        /// Высота стены (лицевой грани), тайлов: одна настройка ракурса (E2-17, слово Автора 15.11 «что-то между»
        /// Factorio ≈ 0,9 и RimWorld ≈ 0,25). Забор и полотно двери — от неё.
        /// </summary>
        public const float WallHeight = 0.5f;

        /// <summary>Забор ниже стены во столько раз.</summary>
        public const float FenceShare = 0.72f;

        /// <summary>Тень стены длиннее тени дерева той же высоты (низкая стена иначе почти без тени).</summary>
        const float WallShadow = 1.5f;

        const byte KNone = 0, KStone = 1, KWood = 2, KDoor = 3, KFence = 4;

        sealed class WallLook
        {
            public Color32 Top, TopSeam, TopHi, Face, FaceSeam, Plinth;
            public bool Planks;
        }

        /// <summary>Цвет палитры (sRGB) → цвет вершины. Проект в линейном пространстве: цвет вершины шейдер не переводит,
        /// и без перевода 124 из палитры выходит на экран как ≈186 — стены белёсые, лицо не темнее верха.</summary>
        static Color32 Srgb(byte r, byte g, byte b, byte a) =>
            QualitySettings.activeColorSpace == ColorSpace.Linear ? (Color32)new Color(r / 255f, g / 255f, b / 255f, a / 255f).linear : new Color32(r, g, b, a);

        static readonly Color32 WallOutline = Srgb(36, 27, 24, 255);

        static readonly Dictionary<byte, WallLook> Looks = new Dictionary<byte, WallLook>
        {
            {
                KStone, new WallLook
                {
                    Top = Srgb(222, 214, 198, 255), TopSeam = Srgb(140, 132, 122, 255), TopHi = Srgb(246, 242, 230, 255),
                    Face = Srgb(124, 116, 132, 255), FaceSeam = Srgb(70, 64, 78, 255), Plinth = Srgb(92, 86, 100, 255),
                }
            },
            {
                KWood, new WallLook
                {
                    Top = Srgb(230, 176, 104, 255), TopSeam = Srgb(146, 94, 50, 255), TopHi = Srgb(250, 212, 146, 255),
                    Face = Srgb(168, 100, 48, 255), FaceSeam = Srgb(92, 52, 26, 255), Plinth = Srgb(112, 66, 34, 255),
                    Planks = true,
                }
            },
        };

        // Дверь: полотно светлее и насыщеннее пола, оковка, латунная ручка.
        static readonly Color32 DoorWood = Srgb(236, 150, 58, 255), DoorTop = Srgb(252, 196, 112, 255),
            DoorSeam = Srgb(150, 80, 30, 255), DoorIron = Srgb(64, 66, 86, 255), DoorBrass = Srgb(255, 222, 96, 255);

        // Забор.
        static readonly Color32 FencePost = Srgb(170, 108, 58, 255), FenceCap = Srgb(228, 174, 112, 255),
            FenceRail = Srgb(206, 146, 84, 255);

        /// <summary>Пустышка строки таблицы для квадратов построек-ячеек (тень есть, не блок).</summary>
        static readonly ObjectLook CellLook = new ObjectLook { Shadow = 1f };

        struct TileCells
        {
            public byte Kind, Mask;
            public bool Open;
        }

        // Центр и 4 соседа: 0 — сам, 1 — север, 2 — юг, 3 — запад, 4 — восток.
        readonly TileCells[] _near = new TileCells[5];

        static byte KindOf(ushort obj)
        {
            switch (ObjectId.IdOf(obj))
            {
                case ObjectId.StoneWall: return KStone;
                case ObjectId.WoodWall: return KWood;
                case ObjectId.Door: return KDoor;
                case ObjectId.WoodFence: return KFence;
                default: return KNone;
            }
        }

        TileCells ReadCells(int tx, int ty)
        {
            TileCells c = default;
            MapShape map = _map;
            if (!map.Wraps && ((uint)tx >= (uint)map.Width || (uint)ty >= (uint)map.Height))
            {
                return c;
            }

            TileCoord t = Torus.Wrap(map, new TileCoord(tx, ty));
            Chunk? chunk = _world.FindChunk(Torus.ChunkOf(map, t));
            if (chunk == null)
            {
                return c;
            }

            int index = Chunk.IndexOf(t.X & (Size - 1), t.Y & (Size - 1));
            ushort obj = chunk.GetObject(index);
            byte kind = KindOf(obj);
            if (kind == KNone)
            {
                return c;
            }

            bool has = chunk.TryGetBuild(index, out BuildRecord rec);
            c.Kind = kind;
            c.Mask = BuildCells.MaskOf(obj, has, rec);
            c.Open = kind == KDoor && has && rec.IsDoorOpen;
            return c;
        }

        void ReadNear(int tx, int ty)
        {
            _near[0] = ReadCells(tx, ty);
            _near[1] = ReadCells(tx, ty - 1);
            _near[2] = ReadCells(tx, ty + 1);
            _near[3] = ReadCells(tx - 1, ty);
            _near[4] = ReadCells(tx + 1, ty);
        }

        // Вид ячейки (cx, cy) в осях ячеек тайла (−1…2; Y ядра вниз); за пределами тайла — только по 4 сторонам.
        byte CellKind(int cx, int cy)
        {
            int dx = cx < 0 ? -1 : cx > 1 ? 1 : 0, dy = cy < 0 ? -1 : cy > 1 ? 1 : 0;
            int k = dx == 0 && dy == 0 ? 0 : dx == 0 ? (dy < 0 ? 1 : 2) : dy == 0 ? (dx < 0 ? 3 : 4) : -1;
            if (k < 0)
            {
                return KNone;
            }

            ref TileCells t = ref _near[k];
            return (t.Mask & BuildCells.Bit(cx & 1, cy & 1)) != 0 ? t.Kind : KNone;
        }

        static bool IsWall(byte k) => k == KStone || k == KWood;

        /// <summary>
        /// Ключ вида тайла постройки-ячеек (E2-17): вид, маска, дверь открыта — свои и 4 соседей. Сменился — строку
        /// пересобрать (вид зависит от соседних ячеек, в том числе в соседнем чанке). Не постройка-ячеек — 0.
        /// </summary>
        public long CellKey(int tx, int ty)
        {
            ReadNear(tx, ty);
            if (_near[0].Kind == KNone)
            {
                return 0;
            }

            long key = 0;
            for (int i = 0; i < 5; i++)
            {
                ref TileCells t = ref _near[i];
                key |= (long)(t.Kind | (t.Mask << 3) | (t.Open ? 128 : 0)) << (8 * i);
            }

            return key;
        }

        static float CellHash(int x, int y, uint seed) => GroundAtlas.Hash(x & 0xFFFF, y & 0xFFFF, seed);

        // Прямоугольник с вертикальным переходом цвета: низ — bottom, верх — top.
        void SolidV(float x0, float y0, float x1, float y1, in ObjectAtlas.Entry white, Color32 bottom, Color32 top)
        {
            Vector2 m = white.Uv.center;
            Quad(new Vector3(x0, y0), new Vector3(x1, y0), new Vector3(x0, y1), new Vector3(x1, y1), m.x, m.y, m.x, m.y, bottom, bottom, top, top);
        }

        void FillInfo(Vector4 info)
        {
            while (_info.Count < _pos.Count)
            {
                _info.Add(info);
            }
        }

        // Тайл постройки-ячеек строки: (tx, ty) — тайл мира, it.X/it.Lx — место в узле.
        void Cells(in Item it, int tx, int ty, ObjectAtlas.Entry[] entries)
        {
            ReadNear(tx, ty);
            TileCells me = _near[0];
            ref ObjectAtlas.Entry white = ref entries[ObjectAtlas.Find(ObjectAtlas.WhiteName)];
            float lx = it.Lx, top = it.Y + 0.75f; // верх тайла в узле (it.Y = низ тайла + 0,25)
            if (me.Kind == KDoor)
            {
                Door(lx, top, me, white);
                FillInfo(new Vector4(lx + 0.5f, top - 1f, 1f, 0f));
                return;
            }

            for (int qy = 0; qy < 2; qy++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    if ((me.Mask & BuildCells.Bit(qx, qy)) == 0)
                    {
                        continue;
                    }

                    float x0 = lx + 0.5f * qx, yb = top - 0.5f * qy - 0.5f;
                    int gx = tx * 2 + qx, gy = ty * 2 + qy;
                    if (me.Kind == KFence)
                    {
                        Fence(x0, yb, qx, qy, white);
                    }
                    else
                    {
                        WallCell(x0, yb, qx, qy, gx, gy, Looks[me.Kind], white);
                    }

                    FillInfo(new Vector4(x0 + 0.25f, yb, 1f, 0f));
                }
            }
        }

        void WallCell(float x0, float yb, int qx, int qy, int gx, int gy, WallLook look, in ObjectAtlas.Entry white)
        {
            const float o = 0.032f, plinth = 0.06f, seam = 0.04f;
            float h = WallHeight, x1 = x0 + 0.5f, yt = yb + 0.5f;
            bool n = IsWall(CellKind(qx, qy - 1)), s = IsWall(CellKind(qx, qy + 1));
            bool w = IsWall(CellKind(qx - 1, qy)), e = IsWall(CellKind(qx + 1, qy));
            int links = (n ? 1 : 0) + (s ? 1 : 0) + (w ? 1 : 0) + (e ? 1 : 0);
            bool straight = links == 2 && ((n && s) || (w && e));
            bool joint = !straight; // угол, Т, крест, торец, одиночная ячейка — блок-столб
            float shade = 0.94f + 0.1f * CellHash(gx, gy, 301);

            // Лицо к зрителю: южнее стены нет.
            if (!s)
            {
                Solid(x0, yb, x1, yb + h, white, look.FaceSeam);
                float body = h - plinth;
                if (look.Planks)
                {
                    const int boards = 4;
                    float bw = 0.5f / boards;
                    for (int k = 0; k < boards; k++)
                    {
                        float b0 = x0 + k * bw, k1 = 0.9f + 0.16f * CellHash(gx * 4 + k, gy, 307);
                        SolidV(b0 + seam * 0.5f, yb + plinth, b0 + bw - seam * 0.5f, yb + h - 0.05f, white,
                            Mul(look.Face, 0.8f * k1), Mul(look.Face, 1.04f * k1));
                    }

                    Solid(x0, yb + h - 0.05f, x1, yb + h, white, look.Plinth); // обвязка поверху
                }
                else
                {
                    int courses = Mathf.Max(1, Mathf.RoundToInt(body / 0.15f));
                    float ch = body / courses;
                    for (int i = 0; i < courses; i++)
                    {
                        float c0 = yb + plinth + i * ch, c1 = c0 + ch;
                        bool odd = ((i + gy) & 1) != 0;
                        int bricks = odd ? 2 : 1;
                        for (int k = 0; k < bricks; k++)
                        {
                            float b0 = odd ? x0 + 0.25f * k : x0, b1 = odd ? b0 + 0.25f : x1;
                            float k1 = 0.88f + 0.18f * CellHash(gx * 2 + k, gy * 5 + i, 311);
                            SolidV(b0 + seam * 0.5f, c0 + seam * 0.5f, b1 - seam * 0.5f, c1 - seam * 0.5f, white,
                                Mul(look.Face, 0.84f * k1), Mul(look.Face, 1.06f * k1));
                        }
                    }
                }

                Solid(x0, yb, x1, yb + plinth, white, look.Plinth); // цоколь
                if (joint)
                {
                    // Блок-столб: светлая лопатка по лицу.
                    SolidV(x0 + 0.05f, yb + plinth, x1 - 0.05f, yb + h, white, Mul(look.Face, 0.98f), Mul(look.Face, 1.18f));
                }
            }

            // Верх: блоки (камень) или доски (дерево) вдоль хода стены.
            float t0 = yb + h, t1 = yt + h;
            Solid(x0, t0, x1, t1, white, look.TopSeam);
            Color32 topC = Mul(look.Top, joint ? Mathf.Min(1.08f, shade * 1.06f) : shade);
            if (look.Planks && !joint)
            {
                bool along = w || e; // доски вдоль стены
                for (int k = 0; k < 2; k++)
                {
                    float k1 = 0.92f + 0.12f * CellHash(gx + k * 17, gy + k * 5, 313);
                    if (along)
                    {
                        float p0 = t0 + 0.25f * k;
                        SolidV(x0, p0 + seam * 0.5f, x1, p0 + 0.25f - seam * 0.5f, white, Mul(topC, 0.94f * k1), Mul(topC, 1.04f * k1));
                    }
                    else
                    {
                        float p0 = x0 + 0.25f * k;
                        SolidV(p0 + seam * 0.5f, t0, p0 + 0.25f - seam * 0.5f, t1, white, Mul(topC, 0.94f * k1), Mul(topC, 1.04f * k1));
                    }
                }
            }
            else
            {
                float g = joint ? 0.035f : seam;
                SolidV(x0 + g, t0 + g, x1 - g, t1 - g, white, Mul(topC, 0.93f), topC);
                if (joint)
                {
                    SolidV(x0 + 0.1f, t0 + 0.1f, x1 - 0.1f, t1 - 0.1f, white, topC, Mul(look.TopHi, 1f)); // шапка столба
                }
            }

            if (!n)
            {
                Solid(x0, t1 - 0.05f, x1, t1 - 0.015f, white, look.TopHi); // светлая кромка с севера
            }

            // Контур по свободным краям.
            if (!n) Line(new Vector2(x0, t1), new Vector2(x1, t1), o, white, WallOutline);
            if (!w) Line(new Vector2(x0, t0), new Vector2(x0, t1), o, white, WallOutline);
            if (!e) Line(new Vector2(x1, t0), new Vector2(x1, t1), o, white, WallOutline);
            if (!s)
            {
                Line(new Vector2(x0, t0), new Vector2(x1, t0), o * 0.8f, white, WallOutline); // кромка верх/лицо
                Line(new Vector2(x0, yb), new Vector2(x1, yb), o, white, WallOutline);        // подошва
                bool wFace = w && !IsWall(CellKind(qx - 1, qy + 1)); // у западного соседа тоже лицо — стыка нет
                bool eFace = e && !IsWall(CellKind(qx + 1, qy + 1));
                if (!wFace) Line(new Vector2(x0, yb), new Vector2(x0, t0), o, white, WallOutline);
                if (!eFace) Line(new Vector2(x1, yb), new Vector2(x1, t0), o, white, WallOutline);
            }
        }

        // Забор: столбик в середине ячейки, жерди к соседним ячейкам забора или стены (дальние — до столбика, ближние — после).
        void Fence(float x0, float yb, int qx, int qy, in ObjectAtlas.Entry white)
        {
            float f = WallHeight * FenceShare, cx = x0 + 0.25f, cy = yb + 0.25f;
            byte kn = CellKind(qx, qy - 1), ks = CellKind(qx, qy + 1), kw = CellKind(qx - 1, qy), ke = CellKind(qx + 1, qy);
            bool n = kn == KFence || IsWall(kn), s = ks == KFence || IsWall(ks), w = kw == KFence || IsWall(kw), e = ke == KFence || IsWall(ke);
            const float r = 0.026f, ro = 0.042f;
            float h0 = f * 0.38f, h1 = f * 0.78f;
            if (n)
            {
                for (int k = 0; k < 2; k++)
                {
                    float hy = cy + (k == 0 ? h0 : h1);
                    Solid(cx - ro, hy, cx + ro, hy + 0.25f, white, WallOutline);
                    Solid(cx - r, hy, cx + r, hy + 0.25f, white, Mul(FenceRail, 0.9f));
                }
            }

            for (int k = 0; k < 2; k++)
            {
                float hy = cy + (k == 0 ? h0 : h1);
                if (w)
                {
                    Solid(x0, hy - ro, cx, hy + ro, white, WallOutline);
                    SolidV(x0, hy - r, cx, hy + r, white, Mul(FenceRail, 0.82f), FenceRail);
                }

                if (e)
                {
                    Solid(cx, hy - ro, x0 + 0.5f, hy + ro, white, WallOutline);
                    SolidV(cx, hy - r, x0 + 0.5f, hy + r, white, Mul(FenceRail, 0.82f), FenceRail);
                }
            }

            // Столбик: лицо и верх.
            const float pw = 0.06f;
            float py = cy - pw;
            Solid(cx - pw - 0.02f, py - 0.02f, cx + pw + 0.02f, py + f + 2f * pw + 0.02f, white, WallOutline);
            SolidV(cx - pw, py, cx + pw, py + f, white, Mul(FencePost, 0.8f), FencePost);
            Solid(cx - pw, py + f, cx + pw, py + f + 2f * pw, white, FenceCap);
            if (s)
            {
                for (int k = 0; k < 2; k++)
                {
                    float hy = cy + (k == 0 ? h0 : h1);
                    Solid(cx - ro, hy - 0.25f, cx + ro, hy, white, WallOutline);
                    Solid(cx - r, hy - 0.25f, cx + r, hy, white, FenceRail);
                }
            }
        }

        /// <summary>Толщина полотна двери, тайлов.</summary>
        const float DoorLeaf = 0.16f;

        /// <summary>Ширина короба закрытой вертикальной двери (C-011): шире полотна — сверху читается как дверь, а не щель в стене.</summary>
        const float DoorLeafV = 0.4f;

        /// <summary>Порог открытой вертикальной двери — тёмная полоса пустого проёма (C-011).</summary>
        static readonly Color32 DoorSill = Srgb(70, 46, 30, 255);

        // Ориентация двери: горизонтальная — ряд ячеек qy (0 — северный), вертикальная — столбец qx.
        static bool DoorVertical(byte mask, out int q)
        {
            if (mask == BuildCells.DoorWest || mask == BuildCells.DoorEast)
            {
                q = mask == BuildCells.DoorWest ? 0 : 1;
                return true;
            }

            q = mask == BuildCells.DoorNorth ? 0 : 1; // южная пара и старая дверь во весь тайл — у южного края
            return false;
        }

        // Брусок высотой WallHeight с основанием (x0…x1, y0…y1): лицо к югу и верх; planks — доски поперёк лица.
        void Slab(float x0, float y0, float x1, float y1, in ObjectAtlas.Entry white, bool faceDetail)
        {
            const float o = 0.03f;
            float h = WallHeight;
            Solid(x0, y0, x1, y0 + h, white, DoorSeam);
            float wdt = x1 - x0;
            int boards = Mathf.Max(1, Mathf.RoundToInt(wdt / 0.17f));
            float bw = wdt / boards;
            for (int k = 0; k < boards; k++)
            {
                float b0 = x0 + k * bw, k1 = 0.92f + 0.12f * ((k * 7) % 3) / 2f;
                SolidV(b0 + 0.008f, y0, b0 + bw - 0.008f, y0 + h, white, Mul(DoorWood, 0.82f * k1), Mul(DoorWood, 1.05f * k1));
            }

            if (faceDetail)
            {
                Solid(x0, y0 + h * 0.2f, x1, y0 + h * 0.2f + 0.04f, white, DoorIron);   // оковка
                Solid(x0, y0 + h * 0.72f, x1, y0 + h * 0.72f + 0.04f, white, DoorIron);
            }

            Solid(x0, y0 + h, x1, y1 + h, white, DoorTop);
            Line(new Vector2(x0, y1 + h), new Vector2(x1, y1 + h), o, white, WallOutline);
            Line(new Vector2(x0, y0 + h), new Vector2(x1, y0 + h), o * 0.8f, white, WallOutline);
            Line(new Vector2(x0, y0), new Vector2(x1, y0), o, white, WallOutline);
            Line(new Vector2(x0, y0), new Vector2(x0, y1 + h), o, white, WallOutline);
            Line(new Vector2(x1, y0), new Vector2(x1, y1 + h), o, white, WallOutline);
        }

        // Дверь: закрытая — полотно в проёме во всю ширину тайла; открытая — полотно ребром у петли, повёрнуто внутрь тайла.
        void Door(float lx, float top, in TileCells me, in ObjectAtlas.Entry white)
        {
            const float t = DoorLeaf, len = 0.92f;
            bool vertical = DoorVertical(me.Mask, out int q);
            if (!vertical)
            {
                float yc = top - 0.5f * q - 0.25f; // середина ряда ячеек
                if (!me.Open)
                {
                    Slab(lx, yc - t * 0.5f, lx + 1f, yc + t * 0.5f, white, true);
                    float hx = lx + 0.8f, hy = yc - t * 0.5f + WallHeight * 0.46f; // ручка
                    Solid(hx - 0.045f, hy - 0.045f, hx + 0.045f, hy + 0.045f, white, WallOutline);
                    Solid(hx - 0.03f, hy - 0.03f, hx + 0.03f, hy + 0.03f, white, DoorBrass);
                }
                else
                {
                    // Петля у западного косяка; полотно — внутрь тайла (южная пара — к северу).
                    float x0 = lx + 0.02f;
                    float y0 = q == 1 ? yc - t * 0.5f : yc + t * 0.5f - len, y1 = q == 1 ? yc - t * 0.5f + len : yc + t * 0.5f;
                    Slab(x0, y0, x0 + t, y1, white, false);
                }

                return;
            }

            float x = lx + 0.5f * q + 0.25f; // середина столбца ячеек
            float yTop = top, yBot = top - 1f;
            const float tv = DoorLeafV;
            if (!me.Open)
            {
                // C-011: закрытая вертикальная дверь — короб шире стены-колонны: лицо к югу с досками и оковкой (как у
                // горизонтальной), сверху — полотно досками поперёк, две полосы оковки у косяков и латунная ручка сбоку.
                Slab(x - tv * 0.5f, yBot, x + tv * 0.5f, yTop, white, true);
                float y0 = yBot + WallHeight, y1 = yTop + WallHeight;
                for (int k = 1; k < 8; k++)
                {
                    float y = y0 + (y1 - y0) * k / 8f;
                    Solid(x - tv * 0.5f + 0.02f, y - 0.007f, x + tv * 0.5f - 0.02f, y + 0.007f, white, DoorSeam);
                }

                foreach (float f in new[] { 0.14f, 0.86f })
                {
                    float y = y0 + (y1 - y0) * f;
                    Solid(x - tv * 0.5f, y - 0.03f, x + tv * 0.5f, y + 0.03f, white, DoorIron); // оковка у косяков
                }

                float hx = x + tv * 0.5f - 0.06f, hy = y0 + (y1 - y0) * 0.5f;
                Solid(hx - 0.05f, hy - 0.05f, hx + 0.05f, hy + 0.05f, white, WallOutline);
                Solid(hx - 0.035f, hy - 0.035f, hx + 0.035f, hy + 0.035f, white, DoorBrass);
            }
            else
            {
                // C-011: открытая — проём явно пуст: тёмный порог по столбцу и косяки-упоры у концов; полотно — ребром у
                // северного косяка, повёрнуто внутрь тайла (восточный столбец — к западу) и видно лицом.
                Solid(x - tv * 0.5f, yBot + 0.04f, x + tv * 0.5f, yTop - 0.04f, white, DoorSill);
                Solid(x - tv * 0.5f, yBot, x + tv * 0.5f, yBot + 0.07f, white, DoorIron);
                Solid(x - tv * 0.5f, yTop - 0.07f, x + tv * 0.5f, yTop, white, DoorIron);
                float y0 = yTop - 0.02f - t;
                float x0 = q == 1 ? x + t * 0.5f - len : x - t * 0.5f, x1 = q == 1 ? x + t * 0.5f : x - t * 0.5f + len;
                Slab(x0, y0, x1, y0 + t, white, true);
            }
        }

        // Тень постройки: основание (x0…x1, y0…y1), протянутое вправо-вниз на высоту h (выпуклая оболочка из двух четырёхугольников).
        void HullShadow(float x0, float y0, float x1, float y1, float h, float strength, in ObjectAtlas.Entry white)
        {
            float dx = h * ShearX * WallShadow, dy = -h * ShearY * WallShadow;
            var c = new Color32(0, 0, 0, (byte)(255 * strength));
            Vector2 m = white.Uv.center;
            Vector3 p0 = new Vector3(x0, y0), p1 = new Vector3(x0 + dx, y0 + dy), p2 = new Vector3(x1 + dx, y0 + dy);
            Vector3 p3 = new Vector3(x1 + dx, y1 + dy), p4 = new Vector3(x1, y1), p5 = new Vector3(x0, y1);
            Quad(p0, p1, p5, p4, m.x, m.y, m.x, m.y, c);
            Quad(p1, p2, p4, p3, m.x, m.y, m.x, m.y, c);
        }

        // Тень тайла постройки-ячеек.
        void CellShadows(in Item it, int tx, int ty, ObjectAtlas.Entry[] entries)
        {
            ReadNear(tx, ty);
            TileCells me = _near[0];
            ref ObjectAtlas.Entry white = ref entries[ObjectAtlas.Find(ObjectAtlas.WhiteName)];
            float lx = it.Lx, top = it.Y + 0.75f, h = WallHeight;
            if (me.Kind == KDoor)
            {
                const float t = DoorLeaf, len = 0.92f;
                bool vertical = DoorVertical(me.Mask, out int q);
                if (!vertical)
                {
                    float yc = top - 0.5f * q - 0.25f;
                    if (!me.Open) HullShadow(lx, yc - t * 0.5f, lx + 1f, yc + t * 0.5f, h, 1f, white);
                    else HullShadow(lx + 0.02f, q == 1 ? yc - t * 0.5f : yc + t * 0.5f - len, lx + 0.02f + t, q == 1 ? yc - t * 0.5f + len : yc + t * 0.5f, h, 1f, white);
                }
                else
                {
                    float x = lx + 0.5f * q + 0.25f;
                    if (!me.Open) HullShadow(x - DoorLeafV * 0.5f, top - 1f, x + DoorLeafV * 0.5f, top, h, 1f, white);
                    else HullShadow(q == 1 ? x + t * 0.5f - len : x - t * 0.5f, top - 0.02f - t, q == 1 ? x + t * 0.5f : x - t * 0.5f + len, top - 0.02f, h, 1f, white);
                }

                return;
            }

            for (int qy = 0; qy < 2; qy++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    if ((me.Mask & BuildCells.Bit(qx, qy)) == 0)
                    {
                        continue;
                    }

                    float x0 = lx + 0.5f * qx, yb = top - 0.5f * qy - 0.5f;
                    if (me.Kind == KFence)
                    {
                        // Забор сквозной — тень бледная, по столбику и жердям.
                        float cx = x0 + 0.25f, cy = yb + 0.25f, f = h * FenceShare;
                        HullShadow(cx - 0.06f, cy - 0.06f, cx + 0.06f, cy + 0.06f, f, 0.55f, white);
                        byte kw = CellKind(qx - 1, qy), ke = CellKind(qx + 1, qy), kn = CellKind(qx, qy - 1), ks = CellKind(qx, qy + 1);
                        if (kw != KNone && kw != KDoor) HullShadow(x0, cy - 0.02f, cx, cy + 0.02f, f, 0.4f, white);
                        if (ke != KNone && ke != KDoor) HullShadow(cx, cy - 0.02f, x0 + 0.5f, cy + 0.02f, f, 0.4f, white);
                        if (kn != KNone && kn != KDoor) HullShadow(cx - 0.02f, cy, cx + 0.02f, yb + 0.5f, f, 0.4f, white);
                        if (ks != KNone && ks != KDoor) HullShadow(cx - 0.02f, yb, cx + 0.02f, cy, f, 0.4f, white);
                        continue;
                    }

                    HullShadow(x0, yb, x0 + 0.5f, yb + 0.5f, h, 1f, white);
                }
            }
        }
    }
}
