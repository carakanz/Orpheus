using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;

namespace Orpheus.Net.Session
{
    /// <summary>Куда уходят заявки стройки (E2-5): клиент сети (<see cref="NetClient"/>) или показ без сервера.</summary>
    public interface IBuildRequests
    {
        /// <summary>Поставить линию от тайла до тайла предметом из ячейки панели (с предсказанием, как у клиента сети); номер запроса, 0 — не ушло.</summary>
        ushort Place(TileCoord from, TileCoord to, ushort obj, WorldPos self, byte cell);

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

        public BuildCheck(BuildAction action, EditReply reply, ushort key, Material m1, int c1, Material m2, int c2)
        {
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
        public static BuildCheck Check(WorldModel world, WorldPos self, in Inventory bag, int cell, bool alive, TileCoord tile)
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

            BuildDefs.TryGet(key, out BuildDef def);
            EditReply reply = EditRules.CheckPlaceTile(world, self, tile, key);
            if (reply == EditReply.Accepted && EditRules.BlocksCreature(key) && !EditRules.IsFloorKey(key) && EditRules.Overlaps(world.Shape, self, tile))
            {
                reply = EditReply.Occupied; // непроходимое — не на себя (`05` 1.3); чужих существ проверит сервер
            }

            if (reply == EditReply.Accepted && bag.CountIn(cell) <= 0)
            {
                reply = EditReply.NoResources;
            }

            return new BuildCheck(BuildAction.Place, alive ? reply : EditReply.Dead, key, def.Material1, def.Count1, def.Material2, def.Count2);
        }

        /// <summary>
        /// ЛКМ по тайлу предметом ячейки <paramref name="cell"/>: проверка <see cref="Check"/>; можно — заявка (постановка с
        /// предсказанием или починка), иначе заявки нет. Возвращает проверку (причину покажет призрак / строка у курсора).
        /// </summary>
        public BuildCheck Click(WorldModel world, WorldPos self, in Inventory bag, int cell, bool alive, TileCoord tile)
        {
            BuildCheck c = Check(world, self, bag, cell, alive, tile);
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
                Sent(_requests.Place(tile, tile, c.Key, self, (byte)cell));
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
            if ((uint)cell >= Inventory.Slots || !IsBuildItem(bag.ItemAt(cell), out ushort key))
            {
                return 0;
            }

            return Sent(_requests.Place(from, to, key, self, (byte)cell));
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
