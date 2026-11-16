using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Постановка прямо из меню изготовления (E2-16, слова Автора 15.11: «разрешить ставить прямо из меню строительства»):
    /// «Поставить» у рецепта постройки берёт её в руку без ячейки панели. Предмет — из любых ячеек сумки, не хватает —
    /// изготавливается в момент постановки (сервер: <c>Simulation.CraftCell</c>, заявка с <see cref="TileRequestMessage.MenuFlag"/>).
    /// <para>
    /// Клиент и боты ставят «из меню» теми же <see cref="BuildHand"/> и <see cref="BuildRuler"/>, что и из руки: им подаётся
    /// сумка-копия (<see cref="VirtualBag"/>), где в ячейке руки лежит эта постройка в количестве «есть» = готовые в сумке +
    /// сколько можно изготовить (<see cref="Available"/>), а заявки идут через <see cref="Requests"/> — с флагом меню.
    /// </para>
    /// </summary>
    public static class MenuHand
    {
        /// <summary>
        /// Сколько предметов-построек <paramref name="item"/> можно поставить из меню: готовые в сумке + изготовимые на месте
        /// (<see cref="CraftRules.Makeable"/>: станок рецепта рядом, ингредиенты — сумка и сундуки рядом по сводке сервера).
        /// </summary>
        public static int Available(WorldModel world, WorldPos self, in Inventory bag, ChestStock? stock, byte item) =>
            item == Items.None ? 0 : bag.Count(item) + CraftRules.Makeable(world, self, bag, stock, item);

        /// <summary>
        /// Сумка «постройка в руке» для проверок и линейки: копия <paramref name="bag"/>, где готовые <paramref name="item"/> убраны
        /// из своих ячеек, а в ячейке <paramref name="slot"/> лежит <paramref name="item"/> × <paramref name="available"/>
        /// (не больше стопки). Нечего поставить — один предмет «для призрака»: <see cref="Check"/> отвечает «нет ресурсов».
        /// </summary>
        public static Inventory VirtualBag(in Inventory bag, int slot, byte item, int available)
        {
            Inventory v = bag;
            for (int c = 0; c < Inventory.Cells; c++)
            {
                if (v.ItemAt(c) == item)
                {
                    v.SetCell(c, Items.None, 0);
                }
            }

            v.SetCell(slot, item, available > 0 ? available : 1); // 0 — один «для призрака»: ответ «нет ресурсов» ставит Check
            return v;
        }

        /// <summary>
        /// Проверка постановки «из меню» под курсором: как <see cref="BuildHand.Check(WorldModel, WorldPos, in Inventory, int, bool, TileCoord, int, int)"/>
        /// по сумке-копии; <paramref name="available"/> = 0 — «нет ресурсов» (призрак красный).
        /// </summary>
        public static BuildCheck Check(WorldModel world, WorldPos self, in Inventory bag, int slot, byte item, int available, bool alive, TileCoord tile, int quad, int rotation)
        {
            Inventory v = VirtualBag(bag, slot, item, available);
            BuildCheck c = BuildHand.Check(world, self, v, slot, alive, tile, quad, rotation);
            if (!c.Ok)
            {
                return c;
            }

            // C-013: остаток от выхода рецепта (стена — 2, пол — 4) должен влезть в сумку; выход 1 — сумка не нужна.
            int units = c.Cells != 0 ? BuildCells.Count(c.Cells) : 1;
            EditReply refuse = available <= 0 ? EditReply.NoResources
                : !CraftRules.MenuLeftoverFits(in bag, item, units) ? EditReply.BagFull
                : EditReply.Accepted;
            return refuse != EditReply.Accepted
                ? new BuildCheck(c.Action, refuse, c.Key, c.Material1, c.Count1, c.Material2, c.Count2, c.Cells, c.Anchor, c.OffsetX, c.OffsetY)
                : c;
        }

        /// <summary>Заявки «из меню»: те же, что у <paramref name="inner"/>, с флагом <see cref="TileRequestMessage.MenuFlag"/>.</summary>
        public static IBuildRequests Requests(IBuildRequests inner) => new MenuRequests(inner);

        private sealed class MenuRequests : IBuildRequests
        {
            private readonly IBuildRequests _inner;

            public MenuRequests(IBuildRequests inner)
            {
                _inner = inner;
            }

            public ushort Place(TileCoord from, TileCoord to, ushort obj, WorldPos self, byte cell, byte quad) =>
                _inner.Place(from, to, obj, self, cell, (byte)(quad | TileRequestMessage.MenuFlag));

            public ushort Repair(TileCoord tile) => _inner.Repair(tile);
        }
    }
}
