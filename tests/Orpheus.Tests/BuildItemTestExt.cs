using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using Orpheus.Net.Session;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-11: постановка идёт предметом из ячейки панели. Тестам, писанным до E2-11, — короткая запись «поставить» без ячейки:
    /// берётся ячейка панели, где лежит предмет этой постройки (как игрок, взявший его в руку); нет на панели — ячейка 7.
    /// </summary>
    internal static class BuildItemTestExt
    {
        public static ushort Place(this NetClient c, TileCoord from, TileCoord to, ushort obj, WorldPos self)
        {
            int slot = BuildHand.SlotWith(c.Inv, BuildDefs.ItemOf(obj));
            return c.Place(from, to, obj, self, (byte)(slot < 0 ? 7 : slot));
        }
    }
}
