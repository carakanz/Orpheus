using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>Одна правка плана постановки (E2-15): тайл, каким он станет, проверка мира и биты ответа.</summary>
    public struct PlannedEdit
    {
        /// <summary>Тайл правки (свёрнут в карту).</summary>
        public TileCoord Tile;

        /// <summary>Тайл после правки: пол, объект, запись (значимы, только если <see cref="Check"/> — «принято»).</summary>
        public byte Floor;
        public ushort Obj;
        public BuildRecord Record;

        /// <summary>Проверка мира без существ (их проверяет такт).</summary>
        public EditReply Check;

        /// <summary>Номер первой клетки плана в ответе (бит) и число клеток подряд (ячейки стены и пола одного тайла).</summary>
        public byte First;
        public byte Count;

        /// <summary>Предметов-построек на правку: по ячейке у стены, забора и пола, иначе один.</summary>
        public byte Units;

        /// <summary>Правка слоя пола: верх тайла не меняется, существа и враги ей не мешают.</summary>
        public bool FloorOnly;

        /// <summary>
        /// Перенос якоря мебели (C-011): стена в свободные ячейки тайла-якоря — мебель тем же тактом уходит якорем в тайл
        /// <see cref="MoveTile"/> (тот же чанк) объектом <see cref="MoveObj"/> с записью <see cref="MoveRecord"/>
        /// (<see cref="BuildLayers.TryReanchor"/>). Обе правки применяются вместе или ни одна (такт).
        /// </summary>
        public bool Moves;
        public TileCoord MoveTile;
        public ushort MoveObj;
        public BuildRecord MoveRecord;
    }

    /// <summary>
    /// План постановки одной заявки (E2-15) — общий для сервера (<c>NetServer.OnPlace</c>) и предсказания клиента
    /// (<c>NetClient.Place</c>), одни и те же функции ядра:
    /// <list type="bullet">
    /// <item>стена, забор — линия ячеек 0,5 (<see cref="BuildCells.Line"/>, до 32), ячейки тайла — одна правка; дверь — пара
    /// ячеек тайла начала (<see cref="BuildCells.DoorMaskFor"/>);</item>
    /// <item>деревянный и каменный пол — прямоугольник ячеек от ячейки начала до ячейки конца (до 64 ячеек и 32 тайлов;
    /// <see cref="BuildLayers.CheckFloor"/>, нижний слой — верх тайла не меняется), бит ответа — на тайл; настил — линия тайлов
    /// целиком;</item>
    /// <item>мебель (<see cref="BuildLayers.IsFurniture"/>) — линия тайлов (обычно один), СЗ ячейка в каждом — с четвертью
    /// начала (шаг 0,5; <see cref="BuildLayers.CheckFurniture"/>): правка тайла-якоря со сдвигом;</item>
    /// <item>прочее (ковёр, шипы) — линия тайлов (<see cref="EditRules.CheckPlaceTile"/>).</item>
    /// </list>
    /// Запись нового верха кладётся поверх пола (<see cref="BuildLayers.TopRecord"/>).
    /// </summary>
    public static class BuildPlanner
    {
        /// <summary>
        /// План заявки: <paramref name="quad"/> — биты 0–1 ячейка в тайле начала, 2–3 — в тайле конца (поле запроса правки).
        /// Правки — в <paramref name="dest"/> (≥ <see cref="EditRules.MaxLineTiles"/>); возвращает их число, −1 — линия длиннее
        /// 32 или постройку не ставят.
        /// </summary>
        public static int Plan(WorldModel world, WorldPos center, ushort obj, TileCoord from, TileCoord to, int quad, ushort owner, Span<PlannedEdit> dest)
        {
            if (!EditRules.IsPlaceable(obj))
            {
                return -1;
            }

            MapShape map = world.Shape;
            ushort integrity = BuildDefs.TryGet(obj, out BuildDef def) ? def.MaxIntegrity : BuildRecord.MaxIntegrity;
            bool floorKey = EditRules.IsFloorKey(obj);
            if (BuildLayers.IsFurniture(obj))
            {
                // Мебель: линия тайлов (обычно один), в каждом — СЗ ячейка с той же четвертью начала (шаг 0,5).
                Span<TileCoord> tilesLine = stackalloc TileCoord[EditRules.MaxLineTiles];
                int nf = EditRules.Line(map, from, to, tilesLine);
                for (int i = 0; i < nf; i++)
                {
                    TileCoord cell = BuildCells.CellOf(tilesLine[i], quad & 3);
                    ref PlannedEdit e = ref dest[i];
                    e = default;
                    e.Check = BuildLayers.CheckFurniture(world, center, obj, cell, out TileCoord anchor, out int dx, out int dy);
                    e.Tile = e.Check == EditReply.Accepted ? anchor : tilesLine[i];
                    e.First = (byte)i;
                    e.Count = 1;
                    e.Units = 1;
                    if (e.Check == EditReply.Accepted)
                    {
                        e.Floor = world.GetFloor(anchor);
                        e.Obj = obj;
                        e.Record = BuildLayers.TopRecord(world, anchor, owner, integrity, 0).WithOffset(dx, dy);
                        if (BuildLayers.IsWide(obj))
                        {
                            e.Record = e.Record.WithFlags(BuildFlags.Wide); // E2-16: многоклеточная — свес и без сдвига
                        }
                    }
                }

                return nf;
            }

            if (floorKey && obj != BuildDefs.PlatformKey)
            {
                return PlanFloor(world, center, obj, from, to, quad, owner, integrity, dest);
            }

            bool cellWall = BuildCells.IsCellBuilding(obj);
            if (cellWall)
            {
                Span<CellGroup> groups = stackalloc CellGroup[EditRules.MaxLineTiles];
                int n;
                bool door = ObjectId.IdOf(obj) == ObjectId.Door;
                if (door)
                {
                    // E2-16: поворот заявки — ориентация пары, если соседние стены её не задают; в мир дверь идёт без поворота.
                    int turn = ObjectId.RotationOf(obj);
                    obj = ObjectId.Door;
                    groups[0] = new CellGroup(Torus.Wrap(map, from), BuildCells.DoorMaskFor(world, from, quad & 3, turn), 0, 1);
                    n = 1;
                }
                else
                {
                    n = BuildCells.Line(map, BuildCells.CellOf(from, quad & 3), BuildCells.CellOf(to, (quad >> 2) & 3), groups);
                }

                for (int i = 0; i < n; i++)
                {
                    CellGroup g = groups[i];
                    TileCoord t = g.Tile;
                    ref PlannedEdit e = ref dest[i];
                    e = default;
                    e.Tile = t;
                    e.First = g.First;
                    e.Count = g.Count;
                    e.Check = BuildCells.CheckPlace(world, center, t, obj, g.Mask, out byte mask, out byte add);
                    if (e.Check == EditReply.FurnitureInTile && !door && BuildLayers.TryReanchor(world, t, out TileCoord moveTo, out BuildRecord moved))
                    {
                        // C-011: свободная половина тайла-якоря сдвинутой мебели — мебель переносит якорь в свой другой тайл.
                        e.Check = EditReply.Accepted;
                        e.Moves = true;
                        e.MoveTile = moveTo;
                        e.MoveObj = world.GetObject(t);
                        e.MoveRecord = moved;
                        mask = g.Mask;
                        add = g.Mask;
                        e.Floor = world.GetFloor(t);
                        e.Obj = obj;
                        e.Record = BuildLayers.TopRecord(world, t, owner, integrity, mask);
                    }
                    else if (e.Check == EditReply.Accepted)
                    {
                        e.Floor = world.GetFloor(t);
                        e.Obj = obj;
                        e.Record = world.GetObject(t) == obj && world.TryGetBuild(t, out BuildRecord old)
                            ? old.WithCells(mask)
                            : BuildLayers.TopRecord(world, t, owner, integrity, mask);
                    }

                    e.Units = (byte)(door ? 1 : Math.Max(1, BuildCells.Count(add)));
                }

                // C-011: новый якорь не должен быть тайлом другой правки той же заявки (стена через оба тайла) — тогда без переноса.
                for (int i = 0; i < n; i++)
                {
                    if (!dest[i].Moves)
                    {
                        continue;
                    }

                    for (int j = 0; j < n; j++)
                    {
                        if (j != i && dest[j].Check == EditReply.Accepted && dest[j].Tile == dest[i].MoveTile)
                        {
                            dest[i].Check = EditReply.FurnitureInTile;
                            dest[i].Moves = false;
                            break;
                        }
                    }
                }

                return n;
            }

            Span<TileCoord> line = stackalloc TileCoord[EditRules.MaxLineTiles];
            int count = EditRules.Line(map, from, to, line);
            for (int i = 0; i < count; i++)
            {
                TileCoord t = line[i];
                ref PlannedEdit e = ref dest[i];
                e = default;
                e.Tile = t;
                e.First = (byte)i;
                e.Count = 1;
                e.Units = 1;
                e.FloorOnly = floorKey;
                e.Check = EditRules.CheckPlaceTile(world, center, t, obj);
                if (e.Check != EditReply.Accepted)
                {
                    continue;
                }

                if (floorKey)
                {
                    BuildLayers.CheckFloor(world, center, t, obj, BuildCells.Full, out byte mask, out byte add);
                    e.Floor = BuildDefs.FloorOf(obj);
                    e.Obj = world.GetObject(t);
                    e.Record = BuildLayers.FloorRecord(world, t, owner, integrity, mask);
                    e.Units = (byte)BuildCells.Count(add);
                }
                else
                {
                    e.Floor = world.GetFloor(t);
                    e.Obj = obj;
                    e.Record = BuildLayers.TopRecord(world, t, owner, integrity, 0);
                }
            }

            return count;
        }

        /// <summary>Пол — прямоугольник ячеек не больше (E2-15): ряд тайлов 16 × 2 ячейки.</summary>
        public const int MaxFloorCells = 64;

        // Пол (деревянный, каменный): прямоугольник ячеек от ячейки начала до ячейки конца (углы включены), ячейки тайла —
        // одна правка; правки — в порядке первого появления тайла (ряды от A, в ряду — от A); бит ответа — номер правки.
        private static int PlanFloor(WorldModel world, WorldPos center, ushort obj, TileCoord from, TileCoord to, int quad, ushort owner, ushort integrity, Span<PlannedEdit> dest)
        {
            MapShape map = world.Shape;
            MapShape cells = BuildCells.CellShape(map);
            TileCoord a = Torus.Wrap(cells, BuildCells.CellOf(from, quad & 3));
            TileCoord b = Torus.Wrap(cells, BuildCells.CellOf(to, (quad >> 2) & 3));
            Int2 d = Torus.Delta(cells, a, b);
            int w = Math.Abs(d.X) + 1;
            int h = Math.Abs(d.Y) + 1;
            if (w * h > MaxFloorCells)
            {
                return -1;
            }

            int sx = d.X < 0 ? -1 : 1;
            int sy = d.Y < 0 ? -1 : 1;
            Span<TileCoord> tiles = stackalloc TileCoord[EditRules.MaxLineTiles];
            Span<byte> masks = stackalloc byte[EditRules.MaxLineTiles];
            int n = 0;
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    TileCoord c = Torus.Wrap(cells, new TileCoord(a.X + sx * i, a.Y + sy * j));
                    TileCoord t = BuildCells.TileOfCell(c);
                    int k = 0;
                    while (k < n && tiles[k] != t)
                    {
                        k++;
                    }

                    if (k == n)
                    {
                        if (n == tiles.Length)
                        {
                            return -1; // тайлов больше, чем бит ответа
                        }

                        tiles[n] = t;
                        masks[n++] = 0;
                    }

                    masks[k] |= BuildCells.BitOfCell(c);
                }
            }

            for (int k = 0; k < n; k++)
            {
                TileCoord t = tiles[k];
                ref PlannedEdit e = ref dest[k];
                e = default;
                e.Tile = t;
                e.First = (byte)k;
                e.Count = 1;
                e.FloorOnly = true;
                e.Check = BuildLayers.CheckFloor(world, center, t, obj, masks[k], out byte mask, out byte add);
                e.Units = (byte)Math.Max(1, BuildCells.Count(add));
                if (e.Check == EditReply.Accepted)
                {
                    e.Floor = BuildDefs.FloorOf(obj);
                    e.Obj = world.GetObject(t);
                    e.Record = BuildLayers.FloorRecord(world, t, owner, integrity, mask);
                }
            }

            return n;
        }
    }
}
