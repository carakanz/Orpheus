#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Orpheus.Client
{
    /// <summary>
    /// Пол по ячейкам 0,5 и мебель со сдвигом (E2-15, записка <c>work/design/half_tile_all.md</c>). Пол целым тайлом — как
    /// раньше, плиткой в Tilemap пола узла; пол частью тайла (у тонкой стены) — плитками-четвертями на своей сетке 0,5 × 0,5
    /// (отдельный <see cref="Grid"/> узла): четверть — та же картинка пола, её часть (своего файла нет). Мебель со сдвигом —
    /// объект строки якоря, сдвинутый на полтайла (<see cref="ShiftFurniture"/>).
    /// </summary>
    public static class FloorCellsLook
    {
        const int Size = Torus.ChunkSize;

        static readonly Dictionary<int, TileBase?> _quads = new Dictionary<int, TileBase?>();

        /// <summary>Сетка 0,5 × 0,5 и Tilemap частичных полов узла (порядок — как у плиток пола).</summary>
        public static Tilemap Create(Transform root, int order)
        {
            var go = new GameObject("FloorCells");
            go.transform.SetParent(root, false);
            var grid = go.AddComponent<Grid>();
            grid.cellSize = new Vector3(0.5f, 0.5f, 1f);
            var tmGo = new GameObject("Tiles");
            tmGo.transform.SetParent(go.transform, false);
            var tm = tmGo.AddComponent<Tilemap>();
            var r = tmGo.AddComponent<TilemapRenderer>();
            r.sortingOrder = order;
            r.mode = TilemapRenderer.Mode.Chunk;
            return tm;
        }

        /// <summary>Ячейки пола тайла, если пол построен частью тайла; целый тайл и не построенный — 0.</summary>
        public static byte PartialMask(Chunk? chunk, int index, byte floor)
        {
            if (chunk == null || !FloorId.IsBuilt(floor))
            {
                return 0;
            }

            bool has = chunk.TryGetBuild(index, out BuildRecord rec);
            byte mask = BuildLayers.FloorCellsOf(floor, has, rec);
            return mask == BuildCells.Full ? (byte)0 : mask;
        }

        /// <summary>
        /// Плитки-четверти тайла (lx, ly) в Tilemap частичных полов: <paramref name="mask"/> — ячейки пола (0 — нет частичного
        /// пола), <paramref name="shown"/> — что стоит сейчас (меняется только разница).
        /// </summary>
        public static bool Sync(Tilemap map, byte[] shown, byte[] shownFloor, int lx, int ly, byte floor, byte mask)
        {
            int index = Chunk.IndexOf(lx, ly);
            if (shown[index] == mask && (mask == 0 || shownFloor[index] == floor))
            {
                return false;
            }

            for (int q = 0; q < 4; q++)
            {
                int qx = q & 1, qy = q >> 1;
                bool on = (mask & (1 << q)) != 0;
                map.SetTile(new Vector3Int(2 * lx + qx, -2 * ly - 1 - qy, 0), on ? Quad(floor, q) : null);
            }

            shown[index] = mask;
            shownFloor[index] = floor;
            return true;
        }

        /// <summary>
        /// Пол тайла для меша земли: под построенным полом (кроме настила — под ним вода) — природный пол из записи: свободная
        /// половина у стены — трава, и полоса «земли построенного пола» не выходит за стены; иначе — пол тайла.
        /// </summary>
        public static byte GroundFloor(Chunk chunk, int index)
        {
            byte floor = chunk.GetFloor(index);
            if (!FloorId.IsBuilt(floor) || floor == FloorId.Platform || !chunk.TryGetBuild(index, out BuildRecord rec))
            {
                return floor;
            }

            return BuildLayers.NaturalUnder(rec);
        }

        /// <summary>Плитка-четверть пола: часть картинки целой плитки (<see cref="BuildLook.TileOf"/>), опора по центру.</summary>
        static TileBase? Quad(byte floor, int q)
        {
            int key = (floor << 2) | q;
            if (_quads.TryGetValue(key, out TileBase? t))
            {
                return t;
            }

            string? name = BuildLook.NameOf(BuildDefs.FloorKey(floor));
            Sprite? src = name != null ? BuildLook.Centered(name) : null;
            Tile? tile = null;
            if (src != null)
            {
                Rect r = src.rect;
                float w = r.width / 2f, h = r.height / 2f;
                int qx = q & 1, qy = q >> 1;
                var sub = new Rect(r.x + qx * w, r.y + (1 - qy) * h, w, h); // в текстуре Y вверх: северная ячейка — верхняя половина
                tile = ScriptableObject.CreateInstance<Tile>();
                tile.sprite = Sprite.Create(src.texture, sub, new Vector2(0.5f, 0.5f), src.pixelsPerUnit);
                tile.name = name + "_q" + q;
            }

            _quads[key] = tile;
            return tile;
        }

        /// <summary>
        /// Мебель со сдвигом на полтайла и многоклеточная (запись тайла-якоря, E2-15 / E2-16): опора (низ по центру — передняя
        /// кромка) сдвигается к середине предмета по X и к его южному краю по Y (Y Unity вверх). 1 × 1 без сдвига — как было.
        /// </summary>
        public static void ShiftFurniture(Chunk chunk, int index, ref float x, ref float y)
        {
            ushort obj = chunk.GetObject(index);
            bool has = chunk.TryGetBuild(index, out BuildRecord rec);
            if (BuildLayers.Spreads(obj, has, rec) || BuildLayers.IsWide(obj)) // без записи (сцены PaintDemo) — без сдвига, по размеру
            {
                BuildLayers.SizeOf(obj, out int w, out int h);
                x += (2 * rec.OffsetX + w - 2) * 0.25f;
                y -= (rec.OffsetY + h) * 0.5f - 1f;
            }
        }
    }
}
