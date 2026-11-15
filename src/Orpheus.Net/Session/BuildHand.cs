using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;

namespace Orpheus.Net.Session
{
    /// <summary>Куда уходят заявки стройки (E2-5): клиент сети (<see cref="NetClient"/>) или показ без сервера.</summary>
    public interface IBuildRequests
    {
        /// <summary>
        /// Поставить линию от тайла до тайла предметом из ячейки панели (с предсказанием, как у клиента сети); номер запроса, 0 — не ушло.
        /// <paramref name="quad"/> (E2-12) — четверти начала (биты 0–1) и конца (2–3): стена, забор, дверь ставятся ячейками 0,5 × 0,5.
        /// </summary>
        ushort Place(TileCoord from, TileCoord to, ushort obj, WorldPos self, byte cell, byte quad);

        /// <summary>Починить постройку на тайле; номер запроса, 0 — не ушло.</summary>
        ushort Repair(TileCoord tile);
    }

    /// <summary>Куда уходят действия с объектами (E2-5, F): клиент сети или показ без сервера.</summary>
    public interface IUseRequests
    {
        /// <summary>Действие с объектом на тайле: сундук — открыть, кровать — точка возрождения.</summary>
        bool RequestUse(TileCoord tile);

        /// <summary>Закрыть открытый сундук.</summary>
        bool RequestCloseChest();
    }

    /// <summary>Что сделает ЛКМ молотом по тайлу (<see cref="BuildHand.Check"/>).</summary>
    public enum BuildAction : byte
    {
        /// <summary>Поставить выбранную постройку.</summary>
        Place = 0,

        /// <summary>Починить повреждённую постройку на тайле.</summary>
        Repair = 1,
    }

    /// <summary>Итог проверки тайла под курсором: что сделает щелчок, можно ли (иначе — причина) и цена.</summary>
    public readonly struct BuildCheck
    {
        public readonly BuildAction Action;
        public readonly EditReply Reply;

        /// <summary>Что ставится (ключ <see cref="BuildDefs"/>) или чинится (объект на тайле).</summary>
        public readonly ushort Key;

        /// <summary>Цена: материалы и количества (у починки — по недостающей доле прочности).</summary>
        public readonly Material Material1, Material2;
        public readonly int Count1, Count2;

        /// <summary>Ячейки 0,5 × 0,5 тайла, которые займёт постановка (E2-12, <see cref="BuildCells.Bit"/>); 0 — весь тайл.</summary>
        public readonly byte Cells;

        /// <summary>
        /// Мебель (E2-15): тайл-якорь и сдвиг в ячейках 0,5 от его СЗ ячейки — где встанет (призрак со сдвигом). Прочее —
        /// тайл щелчка, сдвиг 0.
        /// </summary>
        public readonly TileCoord Anchor;
        public readonly int OffsetX, OffsetY;

        public BuildCheck(BuildAction action, EditReply reply, ushort key, Material m1, int c1, Material m2, int c2, byte cells = 0)
            : this(action, reply, key, m1, c1, m2, c2, cells, default, 0, 0)
        {
        }

        public BuildCheck(BuildAction action, EditReply reply, ushort key, Material m1, int c1, Material m2, int c2, byte cells, TileCoord anchor, int dx, int dy)
        {
            Anchor = anchor;
            OffsetX = dx;
            OffsetY = dy;
            Cells = cells;
            Action = action;
            Reply = reply;
            Key = key;
            Material1 = m1;
            Count1 = c1;
            Material2 = m2;
            Count2 = c2;
        }

        /// <summary>Можно: призрак зелёный, щелчок отправит заявку.</summary>
        public bool Ok => Reply == EditReply.Accepted;

        /// <summary>Причина отказа строкой (`05` 1.3); можно — пусто.</summary>
        public string Reason => Ok ? string.Empty : EditRules.Describe(Reply);
    }

    /// <summary>
    /// «Рука строителя» (E2-5 → E2-11): общий путь для игрока (предмет-постройка в руке, призрак, ЛКМ; молот — починка) и для
    /// ботов / тестов — как <see cref="BagHand"/> у сумки и <see cref="CraftDesk"/> у крафта. Правил игры здесь нет: «можно /
    /// нельзя с причиной» считают функции ядра, которыми проверяет и сервер (<see cref="EditRules.CheckPlaceTile"/>,
    /// <see cref="EditRules.CheckRepair"/>); решает всё равно сервер. С E2-11 постройка ставится предметом из ячейки панели
    /// (стена в руке — ставит стену, снимая один предмет); ленты построек у молота нет — молот только сносит и чинит.
    /// Постановка предсказывается клиентом сети (<see cref="NetClient.Place"/>), починка — нет.
    /// </summary>
    public sealed class BuildHand
    {
        private readonly IBuildRequests _requests;

        public BuildHand(IBuildRequests requests)
        {
            _requests = requests;
        }

        /// <summary>Номер последнего отправленного запроса (постановка или починка); 0 — ещё не было.</summary>
        public ushort LastRequest { get; private set; }

        /// <summary>Заявок постановки и починки отправлено.</summary>
        public int PlaceRequests { get; private set; }
        public int RepairRequests { get; private set; }

        /// <summary>Молот в руках: снос (ПКМ / ЛКМ по постройке) и починка.</summary>
        public static bool IsHammer(byte item) => Items.DigToolOf(item) == ToolKind.Hammer;

        /// <summary>Предмет — постройка (E2-11): в руке ставится; ключ постройки — <paramref name="key"/>.</summary>
        public static bool IsBuildItem(byte item, out ushort key) => BuildDefs.TryKeyOf(item, out key);

        /// <summary>Предмет — постройка.</summary>
        public static bool IsBuildItem(byte item) => BuildDefs.TryKeyOf(item, out _);

        /// <summary>
        /// Что сделает ЛКМ по тайлу и можно ли (данные клиента: свой мир, место, сумка, ячейка панели в руке, жив ли).
        /// В руке предмет-постройка — постановка его постройки (<see cref="EditRules.CheckPlaceTile"/>; непроходимое — не на
        /// себя; ячейка пуста — «нет ресурсов»). В руке молот — починка повреждённой постройки (<see cref="EditRules.CheckRepair"/>,
        /// цена по сумке); целая или пусто — <see cref="EditReply.Intact"/> / <see cref="EditReply.Empty"/>. Иначе — «нельзя».
        /// </summary>
        public static BuildCheck Check(WorldModel world, WorldPos self, in Inventory bag, int cell, bool alive, TileCoord tile) =>
            Check(world, self, bag, cell, alive, tile, 0);

        /// <summary>
        /// Как <see cref="Check(WorldModel, WorldPos, in Inventory, int, bool, TileCoord)"/>, с ячейкой 0,5 × 0,5 под курсором
        /// (<paramref name="quad"/>: бит 0 — восточная половина, бит 1 — южная; E2-12): стена и забор — эта ячейка, дверь — пара
        /// ячеек тайла вдоль стены (<see cref="BuildCells.DoorMaskFor"/>); проверка — <see cref="BuildCells.CheckPlace"/>.
        /// </summary>
        public static BuildCheck Check(WorldModel world, WorldPos self, in Inventory bag, int cell, bool alive, TileCoord tile, int quad) =>
            Check(world, self, bag, cell, alive, tile, quad, 0);

        /// <summary>
        /// Как <see cref="Check(WorldModel, WorldPos, in Inventory, int, bool, TileCoord, int)"/> с поворотом <paramref name="rotation"/>
        /// (0…3, R; E2-16): мебель — размер и ячейки по повороту (<see cref="BuildLayers.SizeOf"/>), дверь без соседних стен —
        /// ориентация пары. <see cref="BuildCheck.Key"/> — ключ с поворотом (его же шлёт <see cref="Click(WorldModel, WorldPos, in Inventory, int, bool, TileCoord, int, int)"/>).
        /// </summary>
        public static BuildCheck Check(WorldModel world, WorldPos self, in Inventory bag, int cell, bool alive, TileCoord tile, int quad, int rotation)
        {
            byte held = bag.ItemAt(cell);
            if (IsHammer(held))
            {
                EditReply repair = EditRules.CheckRepair(world, self, tile, out BuildDef rdef, out int r1, out int r2);
                if (repair == EditReply.Accepted && !EditRules.RepairAffordable(rdef, r1, r2, bag))
                {
                    repair = EditReply.NoResources;
                }

                return new BuildCheck(BuildAction.Repair, alive ? repair : EditReply.Dead, world.GetObject(Torus.Wrap(world.Shape, tile)),
                    rdef.Material1, r1, rdef.Material2, r2);
            }

            if (!IsBuildItem(held, out ushort key))
            {
                return new BuildCheck(BuildAction.Place, alive ? EditReply.NotAllowed : EditReply.Dead, 0, default, 0, default, 0);
            }

            // E2-15: тот же план, что у сервера и предсказания (BuildPlanner): стена, забор, пол — ячейка под курсором, дверь —
            // пара ячеек, мебель — СЗ ячейка под курсором (шаг 0,5), прочее — тайл.
            ushort plain = key;
            key = BuildLayers.Turned(key, rotation);
            BuildDefs.TryGet(key, out BuildDef def);
            Span<PlannedEdit> plan = stackalloc PlannedEdit[EditRules.MaxLineTiles];
            int q = quad & 3;
            int n = BuildPlanner.Plan(world, self, key, tile, tile, q | (q << 2), 0, plan);
            EditReply reply = n > 0 ? plan[0].Check : EditReply.NotAllowed;
            TileCoord anchor = n > 0 ? plan[0].Tile : Torus.Wrap(world.Shape, tile);
            int dx = 0, dy = 0;
            byte cells = 0;
            if (BuildLayers.IsFurniture(key))
            {
                if (reply == EditReply.Accepted)
                {
                    dx = plan[0].Record.OffsetX;
                    dy = plan[0].Record.OffsetY;
                }
                else
                {
                    TileCoord c = BuildCells.CellOf(Torus.Wrap(world.Shape, tile), q); // отказ: призрак там, где просили
                    anchor = BuildCells.TileOfCell(c);
                    dx = c.X & 1;
                    dy = c.Y & 1;
                }

                BuildLayers.SizeOf(key, out int fw, out int fh);
                cells = BuildLayers.FootMask(fw, fh, dx, dy, 0, 0);
                if (reply == EditReply.Accepted && EditRules.BlocksCreature(key))
                {
                    Span<TileCoord> tiles = stackalloc TileCoord[BuildLayers.MaxFootTiles];
                    Span<byte> masks = stackalloc byte[BuildLayers.MaxFootTiles];
                    int parts = BuildLayers.Footprint(world.Shape, anchor, key, plan[0].Record, tiles, masks);
                    for (int k = 0; k < parts; k++)
                    {
                        if (Orpheus.Core.Movement.Collision.CircleOverlapsCells(world.Shape, self, tiles[k], masks[k]))
                        {
                            reply = EditReply.Occupied; // непроходимое — не на себя (`05` 1.3); чужих существ проверит сервер
                        }
                    }
                }
            }
            else if (BuildCells.IsCellBuilding(plain) || (EditRules.IsFloorKey(plain) && plain != BuildDefs.PlatformKey))
            {
                byte want = plain == ObjectId.Door ? BuildCells.DoorMaskFor(world, tile, q, rotation) : BuildCells.Bit(q & 1, (q >> 1) & 1);
                cells = reply == EditReply.Accepted ? AddedCells(world, plain, anchor, plan[0]) : want;
                if (reply == EditReply.Accepted && BuildCells.IsCellBuilding(plain)
                    && Orpheus.Core.Movement.Collision.CircleOverlapsCells(world.Shape, self, anchor, cells))
                {
                    reply = EditReply.Occupied; // ячейка — не на себя (`05` 1.3)
                }
            }
            else if (reply == EditReply.Accepted && EditRules.BlocksCreature(key) && !EditRules.IsFloorKey(key) && EditRules.Overlaps(world.Shape, self, tile))
            {
                reply = EditReply.Occupied; // непроходимое — не на себя (`05` 1.3); чужих существ проверит сервер
            }

            if (reply == EditReply.Accepted && bag.CountIn(cell) <= 0)
            {
                reply = EditReply.NoResources;
            }

            return new BuildCheck(BuildAction.Place, alive ? reply : EditReply.Dead, key, def.Material1, def.Count1, def.Material2, def.Count2, cells, anchor, dx, dy);
        }

        // Ячейки, которые добавит правка плана: стена/забор/дверь — новые ячейки верха, пол — новые ячейки пола.
        private static byte AddedCells(WorldModel world, ushort key, TileCoord tile, in PlannedEdit e)
        {
            if (EditRules.IsFloorKey(key))
            {
                return (byte)(BuildLayers.FloorCellsOf(e.Floor, true, e.Record) & ~BuildLayers.FloorCellsAt(world, tile) & BuildCells.Full);
            }

            byte before = world.GetObject(tile) == key ? BuildCells.MaskAt(world, tile) : (byte)0;
            return (byte)(BuildCells.MaskOf(key, true, e.Record) & ~before & BuildCells.Full);
        }

        /// <summary>
        /// ЛКМ по тайлу предметом ячейки <paramref name="cell"/>: проверка <see cref="Check"/>; можно — заявка (постановка с
        /// предсказанием или починка), иначе заявки нет. Возвращает проверку (причину покажет призрак / строка у курсора).
        /// </summary>
        public BuildCheck Click(WorldModel world, WorldPos self, in Inventory bag, int cell, bool alive, TileCoord tile) =>
            Click(world, self, bag, cell, alive, tile, 0);

        /// <summary>ЛКМ по ячейке 0,5 × 0,5 (<paramref name="quad"/> — четверть в тайле, E2-12); см. <see cref="Check(WorldModel, WorldPos, in Inventory, int, bool, TileCoord, int)"/>.</summary>
        public BuildCheck Click(WorldModel world, WorldPos self, in Inventory bag, int cell, bool alive, TileCoord tile, int quad) =>
            Click(world, self, bag, cell, alive, tile, quad, 0);

        /// <summary>Как <see cref="Click(WorldModel, WorldPos, in Inventory, int, bool, TileCoord, int)"/> с поворотом (E2-16, R).</summary>
        public BuildCheck Click(WorldModel world, WorldPos self, in Inventory bag, int cell, bool alive, TileCoord tile, int quad, int rotation)
        {
            BuildCheck c = Check(world, self, bag, cell, alive, tile, quad, rotation);
            if (!c.Ok)
            {
                return c;
            }

            if (c.Action == BuildAction.Repair)
            {
                ushort id = _requests.Repair(tile);
                if (id != 0)
                {
                    RepairRequests++;
                    LastRequest = id;
                }
            }
            else
            {
                Sent(_requests.Place(tile, tile, c.Key, self, (byte)cell, (byte)((quad & 3) | ((quad & 3) << 2))));
            }

            return c;
        }

        /// <summary>
        /// Линия предметом ячейки <paramref name="cell"/> (ведение с зажатой ЛКМ, ряд пола у бота): заявка уходит целиком — по
        /// тайлам решает сервер (`05` 1.4: линия до 32 тайлов, по предмету на тайл; где предметы кончились — «нет ресурсов»).
        /// В ячейке не постройка — 0. Возвращает номер запроса; 0 — не ушло.
        /// </summary>
        public ushort PlaceLine(TileCoord from, TileCoord to, WorldPos self, in Inventory bag, int cell)
        {
            // E2-15: пол — прямоугольник ячеек; линия тайлов без четвертей — тайлы целиком (бот: ряд пола).
            if ((uint)cell < Inventory.Slots && IsBuildItem(bag.ItemAt(cell), out ushort key) && EditRules.IsFloorKey(key))
            {
                int ax = to.X < from.X ? 1 : 0; // без карты: по координатам (у шва — линейка с четвертями)
                int ay = to.Y < from.Y ? 1 : 0;
                return PlaceLine(from, to, self, bag, cell, ax | (ay << 1), (ax ^ 1) | ((ay ^ 1) << 1));
            }

            return PlaceLine(from, to, self, bag, cell, 0, 0);
        }

        /// <summary>
        /// Линия с четвертями (E2-12): стена и забор — ячейки от ячейки <paramref name="fromQuad"/> тайла <paramref name="from"/>
        /// до ячейки <paramref name="toQuad"/> тайла <paramref name="to"/> (до 32 ячеек, по предмету на ячейку).
        /// </summary>
        public ushort PlaceLine(TileCoord from, TileCoord to, WorldPos self, in Inventory bag, int cell, int fromQuad, int toQuad)
        {
            if ((uint)cell >= Inventory.Slots || !IsBuildItem(bag.ItemAt(cell), out ushort key))
            {
                return 0;
            }

            return Sent(_requests.Place(from, to, key, self, (byte)cell, (byte)((fromQuad & 3) | ((toQuad & 3) << 2))));
        }

        private ushort Sent(ushort id)
        {
            if (id != 0)
            {
                PlaceRequests++;
                LastRequest = id;
            }

            return id;
        }

        /// <summary>Ячейка панели с предметом <paramref name="item"/> (первая по номеру); нет — −1.</summary>
        public static int SlotWith(in Inventory bag, byte item)
        {
            for (int s = 0; s < Inventory.Slots; s++)
            {
                if (item != Items.None && bag.ItemAt(s) == item)
                {
                    return s;
                }
            }

            return -1;
        }

        /// <summary>
        /// Как взять предмет в руку (бот, «взять стену из инвентаря»): ячейка сумки <paramref name="from"/> с предметом и ячейка
        /// панели <paramref name="to"/> — пустая, иначе последняя, где не инструмент и не оружие (обмен местами). Уже на панели
        /// или предмета нет — false.
        /// </summary>
        public static bool StockMove(in Inventory bag, byte item, out int from, out int to)
        {
            from = -1;
            to = -1;
            if (item == Items.None || SlotWith(bag, item) >= 0)
            {
                return false;
            }

            for (int c = Inventory.Slots; c < Inventory.Cells && from < 0; c++)
            {
                from = bag.ItemAt(c) == item ? c : -1;
            }

            for (int s = Inventory.Slots - 1; s >= 0 && to < 0; s--)
            {
                to = bag.ItemAt(s) == Items.None ? s : -1;
            }

            for (int s = Inventory.Slots - 1; s >= 0 && to < 0; s--)
            {
                ItemCategory cat = ItemTable.Get(bag.ItemAt(s)).Category;
                to = cat != ItemCategory.Tool && cat != ItemCategory.Weapon ? s : -1;
            }

            return from >= 0 && to >= 0;
        }
    }
}
