using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>Ячейки одного тайла в линии постановки (E2-12): тайл, добавляемые ячейки, номер первой ячейки линии и их число.</summary>
    public readonly struct CellGroup
    {
        public readonly TileCoord Tile;

        /// <summary>Маска ячеек линии в этом тайле (<see cref="BuildCells.Bit"/>).</summary>
        public readonly byte Mask;

        /// <summary>Номер первой ячейки группы в линии (бит ответа).</summary>
        public readonly byte First;

        /// <summary>Ячеек группы подряд в линии (1…4).</summary>
        public readonly byte Count;

        public CellGroup(TileCoord tile, byte mask, byte first, byte count)
        {
            Tile = tile;
            Mask = mask;
            First = first;
            Count = count;
        }

        /// <summary>Биты ответа группы: <see cref="Count"/> бит с <see cref="First"/>.</summary>
        public uint ReplyBits => ((1u << Count) - 1) << First;
    }

    /// <summary>
    /// Слой построек на сетке 0,5 × 0,5 (E2-12, решение Автора 15.11 «мелкая сетка, в 2 раза меньше»): у тайла четыре
    /// ячейки — СЗ (бит 1), СВ (2), ЮЗ (4), ЮВ (8). Стена, забор и дверь занимают ячейки (маска — <see cref="BuildRecord.Cells"/>),
    /// остальное — целый тайл. Координата ячейки — тайл × 2 + четверть (по оси). Общий код сервера, клиента и ботов.
    /// Записка — <c>work/design/half_tile_walls.md</c>.
    /// </summary>
    public static class BuildCells
    {
        /// <summary>Весь тайл: 1111.</summary>
        public const byte Full = 15;

        /// <summary>Сторона ячейки в 1/256 тайла.</summary>
        public const int CellUnits = Torus.SubTilesPerTile / 2;

        /// <summary>Дверь — пара ячеек: верхняя (СЗ + СВ).</summary>
        public const byte DoorNorth = 3;

        /// <summary>Дверь — нижняя пара (ЮЗ + ЮВ).</summary>
        public const byte DoorSouth = 12;

        /// <summary>Дверь — левая пара (СЗ + ЮЗ).</summary>
        public const byte DoorWest = 5;

        /// <summary>Дверь — правая пара (СВ + ЮВ).</summary>
        public const byte DoorEast = 10;

        /// <summary>Бит ячейки по четвертям <paramref name="qx"/> (0 — запад) и <paramref name="qy"/> (0 — север).</summary>
        public static byte Bit(int qx, int qy) => (byte)(1 << ((qx & 1) | ((qy & 1) << 1)));

        /// <summary>Постройка занимает ячейки (стена, забор, дверь), а не целый тайл.</summary>
        public static bool IsCellBuilding(ushort obj)
        {
            switch (ObjectId.IdOf(obj))
            {
                case ObjectId.WoodWall:
                case ObjectId.StoneWall:
                case ObjectId.WoodFence:
                case ObjectId.Door:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Маска занятых ячеек тайла с объектом <paramref name="obj"/>: не ячейки, нет записи или маска 0 (старые миры) — весь тайл.</summary>
        public static byte MaskOf(ushort obj, bool hasRecord, in BuildRecord rec) =>
            !IsCellBuilding(obj) || !hasRecord || rec.Cells == 0 ? Full : rec.Cells;

        /// <summary>Маска занятых ячеек тайла мира (пустой тайл — 0).</summary>
        public static byte MaskAt(WorldModel world, TileCoord tile)
        {
            ushort obj = world.GetObject(tile);
            if (obj == ObjectId.Empty)
            {
                return 0;
            }

            bool has = world.TryGetBuild(tile, out BuildRecord rec);
            return MaskOf(obj, has, rec);
        }

        /// <summary>Число ячеек в маске.</summary>
        public static int Count(byte mask)
        {
            int n = 0;
            for (int m = mask & Full; m != 0; m &= m - 1)
            {
                n++;
            }

            return n;
        }

        /// <summary>Маска — пара ячеек двери.</summary>
        public static bool IsDoorMask(byte mask) =>
            mask == DoorNorth || mask == DoorSouth || mask == DoorWest || mask == DoorEast;

        /// <summary>Карта ячеек: вдвое больше по каждой оси (координаты ячеек свёрнуты в неё).</summary>
        public static MapShape CellShape(in MapShape map) => new MapShape(map.Width * 2, map.Height * 2, map.Wraps);

        /// <summary>Ячейка по тайлу и четвертям.</summary>
        public static TileCoord CellOf(TileCoord tile, int qx, int qy) => new TileCoord(tile.X * 2 + (qx & 1), tile.Y * 2 + (qy & 1));

        /// <summary>Ячейка под точкой мира.</summary>
        public static TileCoord CellAt(in MapShape map, WorldPos p)
        {
            WorldPos w = Torus.Wrap(map, p);
            return new TileCoord(w.X / CellUnits, w.Y / CellUnits);
        }

        /// <summary>Тайл ячейки.</summary>
        public static TileCoord TileOfCell(TileCoord cell) => new TileCoord(cell.X >> 1, cell.Y >> 1);

        /// <summary>Бит ячейки в её тайле.</summary>
        public static byte BitOfCell(TileCoord cell) => Bit(cell.X & 1, cell.Y & 1);

        /// <summary>Четверти ячейки одним числом (бит 0 — qx, бит 1 — qy) — поле запроса правки.</summary>
        public static int QuadOf(TileCoord cell) => (cell.X & 1) | ((cell.Y & 1) << 1);

        /// <summary>
        /// Четверти заявки «тайлы целиком» от <paramref name="from"/> до <paramref name="to"/> (E2-15: пол прямоугольником
        /// ячеек): начало — угол тайла начала, конец — противоположный угол тайла конца (по направлению по тору).
        /// </summary>
        public static byte WholeTilesQuad(in MapShape map, TileCoord from, TileCoord to)
        {
            Int2 d = Torus.Delta(map, Torus.Wrap(map, from), Torus.Wrap(map, to));
            int ax = d.X < 0 ? 1 : 0;
            int ay = d.Y < 0 ? 1 : 0;
            return (byte)((ax | (ay << 1)) | (((ax ^ 1) | ((ay ^ 1) << 1)) << 2));
        }

        /// <summary>Ячейка по тайлу и четвертям одним числом (<see cref="QuadOf"/>).</summary>
        public static TileCoord CellOf(TileCoord tile, int quad) => CellOf(tile, quad & 1, (quad >> 1) & 1);

        /// <summary>
        /// Ячейки линии от <paramref name="from"/> до <paramref name="to"/> (координаты ячеек, Брезенхэм по тору ячеек, концы
        /// включены, до <see cref="EditRules.MaxLineTiles"/> = 32 ячеек) — сгруппированы по тайлам в <paramref name="groups"/>
        /// (≥ 32). Линия монотонна, так что ячейки тайла идут подряд. Длиннее — −1.
        /// </summary>
        public static int Line(in MapShape map, TileCoord from, TileCoord to, Span<CellGroup> groups)
        {
            MapShape cells = CellShape(map);
            Span<TileCoord> line = stackalloc TileCoord[EditRules.MaxLineTiles];
            int n = EditRules.Line(cells, from, to, line);
            if (n < 0)
            {
                return -1;
            }

            int g = 0;
            int i = 0;
            while (i < n)
            {
                TileCoord tile = TileOfCell(line[i]);
                byte mask = 0;
                int first = i;
                while (i < n && TileOfCell(line[i]) == tile)
                {
                    mask |= BitOfCell(line[i]);
                    i++;
                }

                groups[g++] = new CellGroup(tile, mask, (byte)first, (byte)(i - first));
            }

            return g;
        }

        /// <summary>
        /// Пара ячеек двери в тайле <paramref name="tile"/> по ячейке под курсором (<paramref name="quad"/>): дверь стоит вдоль
        /// стены. Ячейки стены/забора/двери слева или справа от тайла — пара горизонтальная (верх или низ — та половина, где
        /// стоят соседи; соседи на обеих или ни на одной — половина курсора); только сверху или снизу — вертикальная (лево или
        /// право так же); соседей нет — горизонтальная на половине курсора.
        /// </summary>
        public static byte DoorMaskFor(WorldModel world, TileCoord tile, int quad)
        {
            MapShape map = world.Shape;
            int qx = quad & 1;
            int qy = (quad >> 1) & 1;
            bool hN = HasCell(world, map, tile.X - 1, tile.Y, 1, 0) || HasCell(world, map, tile.X + 1, tile.Y, 0, 0);
            bool hS = HasCell(world, map, tile.X - 1, tile.Y, 1, 1) || HasCell(world, map, tile.X + 1, tile.Y, 0, 1);
            bool vW = HasCell(world, map, tile.X, tile.Y - 1, 0, 1) || HasCell(world, map, tile.X, tile.Y + 1, 0, 0);
            bool vE = HasCell(world, map, tile.X, tile.Y - 1, 1, 1) || HasCell(world, map, tile.X, tile.Y + 1, 1, 0);
            if ((vW || vE) && !(hN || hS))
            {
                bool west = vW && !vE ? true : vE && !vW ? false : qx == 0;
                return west ? DoorWest : DoorEast;
            }

            bool north = hN && !hS ? true : hS && !hN ? false : qy == 0;
            return north ? DoorNorth : DoorSouth;
        }

        /// <summary>
        /// Проверка постановки ячеек <paramref name="want"/> постройки-ячеек <paramref name="obj"/> в тайл (без сущностей — их
        /// проверяет такт). Пустой тайл — как <see cref="EditRules.CheckPlaceTile"/>. В тайле уже тот же вид (не дверь) — можно
        /// добавить свободные ячейки; заняты все — «занято»; другой вид или дверь — «занято»; природное — «сначала выкопай».
        /// <paramref name="mask"/> — маска тайла после постановки, <paramref name="add"/> — добавленные ячейки.
        /// </summary>
        public static EditReply CheckPlace(WorldModel world, WorldPos center, TileCoord tile, ushort obj, byte want, out byte mask, out byte add)
        {
            mask = 0;
            add = 0;
            TileCoord t = Torus.Wrap(world.Shape, tile);
            ushort here = world.GetObject(t);
            if (here == ObjectId.Empty || ObjectId.IdOf(obj) == ObjectId.Door)
            {
                EditReply r = EditRules.CheckPlaceTile(world, center, t, obj);
                if (r == EditReply.Accepted && (want & BuildLayers.OverhangAt(world, t, solidOnly: false)) != 0)
                {
                    r = EditReply.Busy; // E2-15: в эти ячейки свешивается мебель соседнего тайла
                }

                if (r == EditReply.Accepted)
                {
                    mask = want;
                    add = want;
                }

                return r;
            }

            if (!EditRules.IsPlaceable(obj))
            {
                return EditReply.NotAllowed;
            }

            if (!EditRules.InPlaceRadius(world.Shape, center, t))
            {
                return EditReply.OutOfReach;
            }

            if (ObjectId.IsNatural(here))
            {
                return EditReply.DigFirst;
            }

            if (here != obj || !world.TryGetBuild(t, out BuildRecord rec) || rec.IsDebris)
            {
                return EditReply.Busy;
            }

            byte before = MaskOf(here, true, rec);
            add = (byte)(want & ~before & Full);
            if (add == 0 || (add & BuildLayers.OverhangAt(world, t, solidOnly: false)) != 0)
            {
                return EditReply.Busy; // E2-15: заняты — свои ячейки или свес мебели соседа
            }

            mask = (byte)(before | add);
            return EditReply.Accepted;
        }

        private static bool HasCell(WorldModel world, in MapShape map, int x, int y, int qx, int qy)
        {
            if (!map.Wraps && ((uint)x >= (uint)map.Width || (uint)y >= (uint)map.Height))
            {
                return false;
            }

            TileCoord t = Torus.Wrap(map, new TileCoord(x, y));
            ushort obj = world.GetObject(t);
            return IsCellBuilding(obj) && (MaskAt(world, t) & Bit(qx, qy)) != 0;
        }
    }
}
