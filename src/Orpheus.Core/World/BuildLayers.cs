using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Два слоя тайла и сетка 0,5 для пола и мебели (E2-15, записка <c>work/design/half_tile_all.md</c>). Пол — нижний слой:
    /// построенный пол в байте пола тайла и маска его ячеек в записи (<see cref="BuildRecord.FloorMask"/>). Верх — объект тайла:
    /// стена, забор, дверь (ячейки, E2-12), мебель (верстак, сундук, кровать, костёр, факел: 2 × 2 ячейки со сдвигом на
    /// полтайла, <see cref="BuildRecord.OffsetX"/>) и прочее (весь тайл). Мебель со сдвигом «свешивается» в соседние тайлы:
    /// их ячейки заняты, хотя в самих тайлах её нет. Общий код сервера, предсказания клиента, ботов и отрисовки.
    /// </summary>
    public static class BuildLayers
    {
        /// <summary>Сторона мебели в ячейках 0,5 (E2-15: вся мебель — 1 × 1 тайл; размеры по id и поворот — E2-16).</summary>
        public const int FurnitureCells = 2;

        /// <summary>Мебель: ставится с шагом 0,5 и занимает 2 × 2 ячейки (E2-15).</summary>
        public static bool IsFurniture(ushort obj)
        {
            if (ObjectId.IsPart(obj))
            {
                return false;
            }

            switch (ObjectId.IdOf(obj))
            {
                case ObjectId.Torch:
                case ObjectId.Campfire:
                case ObjectId.Chest:
                case ObjectId.Bed:
                case ObjectId.Workbench:
                    return true;
                default:
                    return false;
            }
        }

        // ---------- Ячейки мебели ----------

        /// <summary>
        /// Ячейки тайла, сдвинутого на (<paramref name="tx"/>, <paramref name="ty"/>) тайлов от якоря, которые занимает мебель
        /// 2 × 2 со сдвигом (<paramref name="dx"/>, <paramref name="dy"/>) ячеек от СЗ ячейки якоря.
        /// </summary>
        public static byte FootMask(int dx, int dy, int tx, int ty)
        {
            byte mask = 0;
            for (int ky = 0; ky < FurnitureCells; ky++)
            {
                int y = dy + ky - 2 * ty;
                if ((uint)y > 1)
                {
                    continue;
                }

                for (int kx = 0; kx < FurnitureCells; kx++)
                {
                    int x = dx + kx - 2 * tx;
                    if ((uint)x <= 1)
                    {
                        mask |= BuildCells.Bit(x, y);
                    }
                }
            }

            return mask;
        }

        /// <summary>
        /// Ячейки верха в собственном тайле: пусто — 0; стена, забор, дверь — маска (<see cref="BuildCells.MaskOf"/>); мебель —
        /// её ячейки в тайле-якоре (без сдвига — весь тайл); прочее (природное, части, ковёр…) — весь тайл.
        /// </summary>
        public static byte OwnCells(ushort obj, bool hasRecord, in BuildRecord rec)
        {
            if (obj == ObjectId.Empty)
            {
                return 0;
            }

            if (BuildCells.IsCellBuilding(obj))
            {
                return BuildCells.MaskOf(obj, hasRecord, rec);
            }

            return hasRecord && rec.HasOffset && IsFurniture(obj) ? FootMask(rec.OffsetX, rec.OffsetY, 0, 0) : BuildCells.Full;
        }

        /// <summary>Тайлы (до 4) и ячейки в них мебели якоря <paramref name="anchor"/> со сдвигом записи. Возвращает число тайлов.</summary>
        public static int Footprint(in MapShape map, TileCoord anchor, in BuildRecord rec, Span<TileCoord> tiles, Span<byte> masks)
        {
            int dx = rec.OffsetX;
            int dy = rec.OffsetY;
            int n = 0;
            for (int ty = -1; ty <= 1; ty++)
            {
                for (int tx = -1; tx <= 1; tx++)
                {
                    byte m = FootMask(dx, dy, tx, ty);
                    if (m == 0)
                    {
                        continue;
                    }

                    tiles[n] = Torus.Wrap(map, new TileCoord(anchor.X + tx, anchor.Y + ty));
                    masks[n] = m;
                    n++;
                }
            }

            return n;
        }

        /// <summary>
        /// СЗ ячейка мебели 2 × 2 под точкой <paramref name="p"/> (курсор, E2-15): центр мебели — ближний к точке узел сетки 0,5,
        /// СЗ ячейка — на одну ячейку к северо-западу от узла (координаты ячеек, свёрнуты в карту ячеек).
        /// </summary>
        public static TileCoord FurnitureCellAt(in MapShape map, WorldPos p)
        {
            WorldPos w = Torus.Wrap(map, p);
            int cx = (w.X + BuildCells.CellUnits / 2) / BuildCells.CellUnits - 1;
            int cy = (w.Y + BuildCells.CellUnits / 2) / BuildCells.CellUnits - 1;
            MapShape cells = BuildCells.CellShape(map);
            if (!cells.Wraps)
            {
                cx = Math.Max(0, Math.Min(cells.Width - FurnitureCells, cx));
                cy = Math.Max(0, Math.Min(cells.Height - FurnitureCells, cy));
            }

            return Torus.Wrap(cells, new TileCoord(cx, cy));
        }

        /// <summary>Центр мебели (или верха без сдвига — центр тайла) в единицах мира: центр якоря + сдвиг × 0,5 тайла.</summary>
        public static WorldPos CenterOf(TileCoord anchor, bool hasRecord, in BuildRecord rec)
        {
            WorldPos c = EditRules.CenterOf(anchor);
            return hasRecord && rec.HasOffset ? new WorldPos(c.X + rec.OffsetX * BuildCells.CellUnits, c.Y + rec.OffsetY * BuildCells.CellUnits) : c;
        }

        /// <summary>Центр верха тайла мира (мебель — со сдвигом).</summary>
        public static WorldPos CenterAt(WorldModel world, TileCoord anchor)
        {
            bool has = world.TryGetBuild(anchor, out BuildRecord rec);
            return Torus.Wrap(world.Shape, CenterOf(anchor, has && IsFurniture(world.GetObject(anchor)), rec));
        }

        // ---------- Свес мебели из соседей ----------

        /// <summary>
        /// Может ли в тайл свешиваться мебель соседей: есть записи со сдвигом (<see cref="Chunk.OffsetCount"/>) в чанке тайла
        /// или, у края чанка, в соседних чанках. Нет — соседей не смотреть (миры без сдвигов — как до E2-15).
        /// </summary>
        public static bool MayOverhang(WorldModel world, TileCoord tile)
        {
            MapShape map = world.Shape;
            Chunk? own = world.FindChunk(Torus.ChunkOf(map, tile));
            if (own != null && own.OffsetCount > 0)
            {
                return true;
            }

            int lx = tile.X & (Torus.ChunkSize - 1);
            int ly = tile.Y & (Torus.ChunkSize - 1);
            if (lx > 0 && lx < Torus.ChunkSize - 1 && ly > 0 && ly < Torus.ChunkSize - 1)
            {
                return false;
            }

            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if ((ox == 0 && oy == 0) || !InMap(map, tile.X + ox, tile.Y + oy))
                    {
                        continue;
                    }

                    Chunk? c = world.FindChunk(Torus.ChunkOf(map, Torus.Wrap(map, new TileCoord(tile.X + ox, tile.Y + oy))));
                    if (c != null && c != own && c.OffsetCount > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Ячейки тайла, занятые мебелью со сдвигом, стоящей в 8 соседях (<paramref name="solidOnly"/> — только непроходимой:
        /// сундук, кровать, верстак; факел и костёр проходимы).
        /// </summary>
        public static byte OverhangAt(WorldModel world, TileCoord tile, bool solidOnly)
        {
            if (!MayOverhang(world, tile))
            {
                return 0;
            }

            MapShape map = world.Shape;
            byte mask = 0;
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if ((ox == 0 && oy == 0) || !InMap(map, tile.X + ox, tile.Y + oy))
                    {
                        continue;
                    }

                    var n = Torus.Wrap(map, new TileCoord(tile.X + ox, tile.Y + oy));
                    ushort obj = world.GetObject(n);
                    if (!IsFurniture(obj) || (solidOnly && ObjectId.IsPassable(obj)) || !world.TryGetBuild(n, out BuildRecord rec) || !rec.HasOffset)
                    {
                        continue;
                    }

                    mask |= FootMask(rec.OffsetX, rec.OffsetY, -ox, -oy);
                }
            }

            return mask;
        }

        /// <summary>Занятые верхом ячейки тайла: свои (<see cref="OwnCells"/>) и свес мебели соседей. Пол не занимает.</summary>
        public static byte TopCellsAt(WorldModel world, TileCoord tile)
        {
            TileCoord t = Torus.Wrap(world.Shape, tile);
            ushort obj = world.GetObject(t);
            bool has = world.TryGetBuild(t, out BuildRecord rec);
            return (byte)(OwnCells(obj, has, rec) | OverhangAt(world, t, solidOnly: false));
        }

        /// <summary>
        /// Тайл, чей верх занимает ячейку <paramref name="cell"/> (координаты ячеек): свой тайл ячейки или якорь мебели-соседа,
        /// свешивающейся в неё. Ячейка свободна — false (снос и починка по ней бьют по своему тайлу: пол).
        /// </summary>
        public static bool TryTopOfCell(WorldModel world, TileCoord cell, out TileCoord anchor)
        {
            MapShape map = world.Shape;
            TileCoord tile = Torus.Wrap(map, BuildCells.TileOfCell(cell));
            byte bit = BuildCells.BitOfCell(cell);
            ushort own = world.GetObject(tile);
            bool has = world.TryGetBuild(tile, out BuildRecord rec);
            anchor = tile;
            if ((OwnCells(own, has, rec) & bit) != 0)
            {
                return true;
            }

            if (!MayOverhang(world, tile))
            {
                return false;
            }

            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if ((ox == 0 && oy == 0) || !InMap(map, tile.X + ox, tile.Y + oy))
                    {
                        continue;
                    }

                    var n = Torus.Wrap(map, new TileCoord(tile.X + ox, tile.Y + oy));
                    ushort obj = world.GetObject(n);
                    if (IsFurniture(obj) && world.TryGetBuild(n, out BuildRecord r) && r.HasOffset
                        && (FootMask(r.OffsetX, r.OffsetY, -ox, -oy) & bit) != 0)
                    {
                        anchor = n;
                        return true;
                    }
                }
            }

            return false;
        }

        // ---------- Пол ----------

        /// <summary>Ячейки построенного пола тайла: пол не построенный — 0; маска 0 в записи (старые миры) или записи нет — весь тайл.</summary>
        public static byte FloorCellsOf(byte floor, bool hasRecord, in BuildRecord rec) =>
            !FloorId.IsBuilt(floor) ? (byte)0 : !hasRecord || rec.FloorMask == 0 ? BuildCells.Full : rec.FloorMask;

        /// <summary>Ячейки построенного пола тайла мира.</summary>
        public static byte FloorCellsAt(WorldModel world, TileCoord tile)
        {
            bool has = world.TryGetBuild(tile, out BuildRecord rec);
            return FloorCellsOf(world.GetFloor(tile), has, rec);
        }

        /// <summary>
        /// Проверка постановки пола <paramref name="floorKey"/> (<see cref="BuildDefs.FloorKey"/>) в ячейки <paramref name="want"/>
        /// тайла (E2-15): пол — нижний слой, стена и мебель ему не мешают (`05` 1.3: «пол строится независимо от объекта»).
        /// Природное — «сначала выкопать», обломки — «обломки»; в тайле уже другой построенный пол — «занято», тот же — свободные
        /// ячейки добавляются (все заняты — «занято»). Деревянный и каменный — на сушу (жидкость — «нужен настил»); настил —
        /// только над жидкостью и только целым тайлом. <paramref name="mask"/> — ячейки пола после, <paramref name="add"/> — новые.
        /// </summary>
        public static EditReply CheckFloor(WorldModel world, WorldPos center, TileCoord tile, ushort floorKey, byte want, out byte mask, out byte add)
        {
            mask = 0;
            add = 0;
            if (!EditRules.IsFloorKey(floorKey))
            {
                return EditReply.NotAllowed;
            }

            if (!EditRules.InPlaceRadius(world.Shape, center, tile))
            {
                return EditReply.OutOfReach;
            }

            TileCoord t = Torus.Wrap(world.Shape, tile);
            ushort here = world.GetObject(t);
            if (ObjectId.IsNatural(here))
            {
                return EditReply.DigFirst;
            }

            bool has = world.TryGetBuild(t, out BuildRecord rec);
            if (has && rec.IsDebris)
            {
                return EditReply.Debris;
            }

            byte floor = world.GetFloor(t);
            byte id = BuildDefs.FloorOf(floorKey);
            bool platform = floorKey == BuildDefs.PlatformKey;
            want = platform ? BuildCells.Full : (byte)(want & BuildCells.Full);
            if (FloorId.IsBuilt(floor))
            {
                if (floor != id)
                {
                    return EditReply.Busy;
                }

                byte before = FloorCellsOf(floor, has, rec);
                add = (byte)(want & ~before);
                if (add == 0)
                {
                    return EditReply.Busy;
                }

                mask = (byte)(before | add);
                return EditReply.Accepted;
            }

            bool liquid = floor == FloorId.Water || floor == FloorId.Lava;
            if (platform)
            {
                if (!liquid)
                {
                    return EditReply.NotAllowed; // настил — только над жидкостью (`05` 1.3)
                }
            }
            else if (liquid)
            {
                return EditReply.NeedPlatform;
            }
            else if (!FloorId.IsSolid(floor))
            {
                return EditReply.NotAllowed;
            }

            if (want == 0)
            {
                return EditReply.NotAllowed;
            }

            mask = want;
            add = want;
            return EditReply.Accepted;
        }

        /// <summary>
        /// Запись тайла после постановки пола с ячейками <paramref name="mask"/>: есть запись (верх, прежний пол) — та же с новой
        /// маской пола; нет — новая запись «только пол» (природный пол — нынешний пол тайла).
        /// </summary>
        public static BuildRecord FloorRecord(WorldModel world, TileCoord tile, ushort owner, ushort integrity, byte mask) =>
            world.TryGetBuild(tile, out BuildRecord rec)
                ? rec.WithFloorMask(mask)
                : new BuildRecord(owner, integrity, 0, world.GetFloor(tile), 0).WithFloorMask(mask);

        /// <summary>
        /// Запись нового верха тайла (стена, мебель…): поверх записи пола, если она есть (<see cref="BuildRecord.Over"/>), иначе
        /// новая (природный пол — нынешний пол тайла).
        /// </summary>
        public static BuildRecord TopRecord(WorldModel world, TileCoord tile, ushort owner, ushort integrity, byte cells) =>
            world.TryGetBuild(tile, out BuildRecord rec) && !rec.IsDebris
                ? BuildRecord.Over(rec, owner, integrity, cells)
                : new BuildRecord(owner, integrity, 0, world.GetFloor(tile), 0, cells);

        /// <summary>Запись обломков верха (`05` 1.3): прочность 0, флаг обломков до <paramref name="endTick"/>; пол под ними — прежний.</summary>
        public static BuildRecord DebrisOf(in BuildRecord rec, ushort endTick) =>
            new BuildRecord(rec.Owner, 0, (byte)BuildFlags.Debris, rec.NaturalFloor, endTick).WithFloorMask(rec.FloorMask);

        /// <summary>Природный пол, который вернёт снос построенного пола (запись старых миров с построенным «природным» — земля).</summary>
        public static byte NaturalUnder(in BuildRecord rec) => FloorId.IsBuilt(rec.NaturalFloor) ? FloorId.Dirt : rec.NaturalFloor;

        /// <summary>
        /// Тайл после сноса молотом (E2-15, снос по слоям): есть верх или обломки — снимаются они, пол остаётся (запись «только
        /// пол», если пол построенный); верха нет — снимается построенный пол целиком (слой пола — природный, записи нет).
        /// </summary>
        public static void AfterDig(WorldModel world, TileCoord tile, out byte floor, out bool hasRecord, out BuildRecord record)
        {
            TileCoord t = Torus.Wrap(world.Shape, tile);
            floor = world.GetFloor(t);
            ushort obj = world.GetObject(t);
            bool has = world.TryGetBuild(t, out BuildRecord rec);
            bool built = FloorId.IsBuilt(floor);
            if (obj != ObjectId.Empty || (has && rec.IsDebris))
            {
                hasRecord = built && has;
                record = hasRecord ? rec.FloorOnly() : default;
                return;
            }

            hasRecord = false;
            record = default;
            if (built)
            {
                floor = has ? NaturalUnder(rec) : FloorId.Dirt;
            }
        }

        /// <summary>Тайл после конца обломков: запись «только пол», если пол построенный, иначе без записи.</summary>
        public static bool AfterDebris(WorldModel world, TileCoord tile, in BuildRecord rec, out BuildRecord record)
        {
            bool built = FloorId.IsBuilt(world.GetFloor(tile));
            record = built ? rec.FloorOnly() : default;
            return built;
        }

        // ---------- Мебель ----------

        /// <summary>
        /// Проверка постановки мебели <paramref name="obj"/> (2 × 2 ячейки) с СЗ ячейкой <paramref name="cell"/> (координаты
        /// ячеек; E2-15, шаг 0,5). Задетые тайлы (1, 2 или 4): радиус постановки, не природное («сначала выкопать»), не обломки,
        /// не жидкость без настила, ячейки свободны от верха (свои и свес соседей) — иначе «занято». Якорь — первый задетый
        /// (СЗ, СВ, ЮЗ, ЮВ) с пустым слотом объекта; сдвиг (<paramref name="dx"/>, <paramref name="dy"/>) — от его СЗ ячейки.
        /// Существ не проверяет (такт).
        /// </summary>
        public static EditReply CheckFurniture(WorldModel world, WorldPos center, ushort obj, TileCoord cell, out TileCoord anchor, out int dx, out int dy)
        {
            anchor = default;
            dx = 0;
            dy = 0;
            if (!IsFurniture(obj) || !EditRules.IsPlaceable(obj))
            {
                return EditReply.NotAllowed;
            }

            MapShape map = world.Shape;
            MapShape cells = BuildCells.CellShape(map);
            if (!InMap(cells, cell.X, cell.Y) || !InMap(cells, cell.X + 1, cell.Y + 1))
            {
                return EditReply.NotAllowed;
            }

            TileCoord c = Torus.Wrap(cells, cell);
            Span<TileCoord> tiles = stackalloc TileCoord[4];
            Span<byte> masks = stackalloc byte[4];
            int n = 0;
            for (int ky = 0; ky < FurnitureCells; ky++)
            {
                for (int kx = 0; kx < FurnitureCells; kx++)
                {
                    TileCoord cc = Torus.Wrap(cells, new TileCoord(c.X + kx, c.Y + ky));
                    TileCoord t = BuildCells.TileOfCell(cc);
                    int k = 0;
                    while (k < n && tiles[k] != t)
                    {
                        k++;
                    }

                    if (k == n)
                    {
                        tiles[n] = t;
                        masks[n] = 0;
                        n++;
                    }

                    masks[k] |= BuildCells.BitOfCell(cc);
                }
            }

            EditReply busy = EditReply.Accepted;
            int free = -1;
            for (int k = 0; k < n; k++)
            {
                TileCoord t = tiles[k];
                if (!EditRules.InPlaceRadius(map, center, t))
                {
                    return EditReply.OutOfReach;
                }

                ushort here = world.GetObject(t);
                if (ObjectId.IsNatural(here))
                {
                    return EditReply.DigFirst;
                }

                bool has = world.TryGetBuild(t, out BuildRecord rec);
                if (has && rec.IsDebris)
                {
                    return EditReply.Debris;
                }

                byte floor = world.GetFloor(t);
                if (floor == FloorId.Water || floor == FloorId.Lava)
                {
                    return EditReply.NeedPlatform;
                }

                if ((TopCellsAt(world, t) & masks[k]) != 0)
                {
                    busy = EditReply.Busy;
                }

                if (free < 0 && here == ObjectId.Empty)
                {
                    free = k;
                }
            }

            if (busy != EditReply.Accepted || free < 0)
            {
                return EditReply.Busy;
            }

            anchor = tiles[free];
            Int2 d = Torus.Delta(cells, BuildCells.CellOf(anchor, 0, 0), c);
            dx = d.X;
            dy = d.Y;
            return EditReply.Accepted;
        }

        private static bool InMap(in MapShape map, int x, int y) =>
            map.Wraps || ((uint)x < (uint)map.Width && (uint)y < (uint)map.Height);
    }
}
