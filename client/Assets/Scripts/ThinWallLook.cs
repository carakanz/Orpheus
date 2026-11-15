#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Orpheus.Client
{
    /// <summary>
    /// Тонкие стены (E2-12, записка <c>work/design/half_tile_walls.md</c>): тайл стены, забора, двери рисуется по ячейкам
    /// 0,5 × 0,5 — каждая занятая ячейка залита плоским цветом вида, тёмный контур — по краям ячейки, где соседняя ячейка
    /// (и через границу тайла) не той же постройки. Так сами собой выходят прямые, углы, Т, крест и торцы.
    /// Заглушка кодом (стиль «тёмный контур, плоский цвет», файла картинки нет — как квадраты призрака <see cref="BuildGhost"/>):
    /// плитка Tilemap 32 × 32 px на ключ «вид, состояние двери, маска, 8 соседних ячеек», создаётся по требованию.
    /// Вид — таблица «объект → цвет» (<see cref="Looks"/>), не ветки по коду.
    /// </summary>
    public static class ThinWallLook
    {
        const int Px = 32;          // пикселей на тайл
        const int CellPx = Px / 2;  // на ячейку
        const int Line = 3;         // толщина контура

        readonly struct Look
        {
            public readonly Color32 Fill, Stripe;
            public readonly bool Stripes; // забор — прутья

            public Look(Color32 fill, Color32 stripe, bool stripes)
            {
                Fill = fill;
                Stripe = stripe;
                Stripes = stripes;
            }
        }

        static readonly Color32 Outline = new Color32(0x22, 0x19, 0x14, 0xFF);

        static readonly Dictionary<ushort, Look> Looks = new Dictionary<ushort, Look>
        {
            { ObjectId.WoodWall, new Look(new Color32(0x9A, 0x6B, 0x3F, 0xFF), new Color32(0x7E, 0x55, 0x30, 0xFF), false) },
            { ObjectId.StoneWall, new Look(new Color32(0x8C, 0x8C, 0x88, 0xFF), new Color32(0x6E, 0x6E, 0x6A, 0xFF), false) },
            { ObjectId.WoodFence, new Look(new Color32(0xC0, 0x92, 0x5A, 0xFF), new Color32(0x6A, 0x48, 0x28, 0xFF), true) },
            { ObjectId.Door, new Look(new Color32(0x6E, 0x44, 0x22, 0xFF), new Color32(0xD8, 0xB0, 0x5C, 0xFF), false) },
        };

        static readonly Dictionary<int, TileBase> _tiles = new Dictionary<int, TileBase>();

        /// <summary>Плиток создано (замер).</summary>
        public static int Created => _tiles.Count;

        /// <summary>
        /// Плитка тайла постройки-ячеек <paramref name="tile"/> мира: маска, состояние двери и соседние ячейки — из мира.
        /// Не постройка-ячеек — null.
        /// </summary>
        public static TileBase? TileAt(WorldModel world, TileCoord tile, ushort obj)
        {
            ushort id = ObjectId.IdOf(obj);
            if (!BuildCells.IsCellBuilding(id) || !Looks.ContainsKey(id))
            {
                return null;
            }

            bool has = world.TryGetBuild(tile, out BuildRecord rec);
            byte mask = BuildCells.MaskOf(obj, has, rec);
            bool open = id == ObjectId.Door && has && rec.IsDoorOpen;
            int nb = Neighbours(world, tile, id);
            int key = id | (open ? 1 << 8 : 0) | (mask << 9) | (nb << 13);
            if (!_tiles.TryGetValue(key, out TileBase? t))
            {
                t = Make(Looks[id], id == ObjectId.Door, open, mask, nb);
                _tiles[key] = t;
            }

            return t;
        }

        // 8 бит: занята ли той же постройкой соседняя ячейка через границу тайла — сверху (qx 0, 1), снизу (qx 0, 1),
        // слева (qy 0, 1), справа (qy 0, 1).
        static int Neighbours(WorldModel world, TileCoord t, ushort id)
        {
            int nb = 0;
            byte up = MaskIf(world, t.X, t.Y - 1, id), down = MaskIf(world, t.X, t.Y + 1, id);
            byte left = MaskIf(world, t.X - 1, t.Y, id), right = MaskIf(world, t.X + 1, t.Y, id);
            nb |= (up & BuildCells.Bit(0, 1)) != 0 ? 1 : 0;
            nb |= (up & BuildCells.Bit(1, 1)) != 0 ? 2 : 0;
            nb |= (down & BuildCells.Bit(0, 0)) != 0 ? 4 : 0;
            nb |= (down & BuildCells.Bit(1, 0)) != 0 ? 8 : 0;
            nb |= (left & BuildCells.Bit(1, 0)) != 0 ? 16 : 0;
            nb |= (left & BuildCells.Bit(1, 1)) != 0 ? 32 : 0;
            nb |= (right & BuildCells.Bit(0, 0)) != 0 ? 64 : 0;
            nb |= (right & BuildCells.Bit(0, 1)) != 0 ? 128 : 0;
            return nb;
        }

        static byte MaskIf(WorldModel world, int x, int y, ushort id)
        {
            MapShape map = world.Shape;
            if (!map.Wraps && ((uint)x >= (uint)map.Width || (uint)y >= (uint)map.Height))
            {
                return 0;
            }

            TileCoord t = Torus.Wrap(map, new TileCoord(x, y));
            return ObjectId.IdOf(world.GetObject(t)) == id ? BuildCells.MaskAt(world, t) : (byte)0;
        }

        static TileBase Make(in Look look, bool door, bool open, byte mask, int nb)
        {
            var px = new Color32[Px * Px];
            for (int qy = 0; qy < 2; qy++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    if ((mask & BuildCells.Bit(qx, qy)) == 0)
                    {
                        continue;
                    }

                    // Есть ли продолжение за каждым краем ячейки (внутри тайла — по маске, за границей — по соседям).
                    bool n = qy == 1 ? (mask & BuildCells.Bit(qx, 0)) != 0 : (nb & (1 << qx)) != 0;
                    bool s = qy == 0 ? (mask & BuildCells.Bit(qx, 1)) != 0 : (nb & (4 << qx)) != 0;
                    bool w = qx == 1 ? (mask & BuildCells.Bit(0, qy)) != 0 : (nb & (16 << qy)) != 0;
                    bool e = qx == 0 ? (mask & BuildCells.Bit(1, qy)) != 0 : (nb & (64 << qy)) != 0;
                    Paint(px, look, door, qx, qy, n, s, w, e);
                }
            }

            if (open)
            {
                for (int i = 0; i < px.Length; i++)
                {
                    px[i].a = (byte)(px[i].a / 3); // открытая дверь — бледная, проём виден
                }
            }

            var tex = new Texture2D(Px, Px, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = "thin_wall",
            };
            tex.SetPixels32(px);
            tex.Apply(false, true);
            var tile = ScriptableObject.CreateInstance<Tile>();
            tile.sprite = Sprite.Create(tex, new Rect(0, 0, Px, Px), new Vector2(0.5f, 0.5f), Px);
            tile.name = "thin_wall";
            return tile;
        }

        // Ячейка (qx, qy) ядра: Y ядра вниз, строки текстуры — вверх (qy 0 — верхняя половина).
        static void Paint(Color32[] px, in Look look, bool door, int qx, int qy, bool n, bool s, bool w, bool e)
        {
            int x0 = qx * CellPx;
            int y0 = (1 - qy) * CellPx;
            for (int y = 0; y < CellPx; y++)
            {
                for (int x = 0; x < CellPx; x++)
                {
                    bool edge = (!w && x < Line) || (!e && x >= CellPx - Line) || (!s && y < Line) || (!n && y >= CellPx - Line);
                    Color32 c = look.Fill;
                    if (look.Stripes && ((x0 + x) % 6 < 2 || (y0 + y) % 6 < 2))
                    {
                        c = look.Stripe; // прутья забора
                    }
                    else if (door && ((x0 + x) % 8 == 4 || (y0 + y) % 8 == 4))
                    {
                        c = look.Stripe; // доски двери
                    }
                    else if (!look.Stripes && !door && (x0 + x + y0 + y) % 16 == 0)
                    {
                        c = look.Stripe; // кладка
                    }

                    px[(y0 + y) * Px + x0 + x] = edge ? Outline : c;
                }
            }
        }
    }
}
