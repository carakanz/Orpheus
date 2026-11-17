using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Цель курсора мыши в мире (C-029): тайл и ячейка 0,5 под точкой земли — одним расчётом для призрака, постановки, сноса,
    /// копания, F и пипетки. Раньше призрак считался здесь (клиентский <c>CursorTile</c>), а щелчок — простым тайлом под точкой:
    /// мебель вставала на тайл ниже (и правее), чем призрак (её СЗ ячейка — от ближнего узла сетки 0,5, а тайл заявки — тайл
    /// точки), а снос верстака по любому его тайлу, кроме якоря, не начинался (на тайле свеса объекта нет — «пусто»).
    /// <para>
    /// Наклонный вид (E2-17): постройки рисуются с высотой — верх стены поднят на полтайла (одна ячейка), мебель — выше своего
    /// следа. Поэтому при сносе / пипетке пустая ячейка под курсором проверяется ещё на ячейку (мебель — на две) к югу: курсор
    /// на нарисованном верху стены попадает в саму стену.
    /// </para>
    /// </summary>
    public static class BuildCursor
    {
        /// <summary>На сколько ячеек 0,5 к югу искать постройку под нарисованной высотой: стена, забор, дверь — 1, мебель — 2.</summary>
        public const int WallLiftCells = 1, FurnitureLiftCells = 2;

        /// <summary>
        /// Тайл и четверть (<paramref name="quad"/>: бит 0 — восточная, бит 1 — южная половина) для курсора в точке земли
        /// <paramref name="p"/>. <paramref name="placeKey"/> — ключ постройки в руке с поворотом (0 — не постройка):
        /// мебель — её СЗ ячейка центром к ближнему узлу сетки 0,5; иначе — ячейка точки. Не постройка (молот, кирка, пустая
        /// рука) и есть мир — якорь постройки под курсором (мебель соседа, нарисованная высота — см. выше).
        /// </summary>
        public static TileCoord Target(WorldModel? world, in MapShape map, WorldPos p, ushort placeKey, out int quad)
        {
            WorldPos w = Torus.Wrap(map, p);
            TileCoord cell = BuildCells.CellAt(map, w);
            if (placeKey != 0 && BuildLayers.IsFurniture(placeKey))
            {
                cell = BuildLayers.FurnitureCellAt(map, w, placeKey);
                quad = BuildCells.QuadOf(cell);
                return BuildCells.TileOfCell(cell);
            }

            quad = BuildCells.QuadOf(cell);
            if (placeKey == 0 && world != null && TryPick(world, cell, out TileCoord top))
            {
                return top;
            }

            return Torus.TileOf(map, w);
        }

        /// <summary>
        /// Постройка (или любой верх тайла) под ячейкой курсора <paramref name="cell"/>: сама ячейка, иначе — постройка
        /// с высотой к югу (стена / забор / дверь — на ячейку, мебель — до двух). Якорь — в <paramref name="anchor"/>.
        /// </summary>
        public static bool TryPick(WorldModel world, TileCoord cell, out TileCoord anchor)
        {
            if (BuildLayers.TryTopOfCell(world, cell, out anchor))
            {
                return true;
            }

            MapShape cells = BuildCells.CellShape(world.Shape);
            for (int dy = 1; dy <= FurnitureLiftCells; dy++)
            {
                if (!cells.Wraps && cell.Y + dy >= cells.Height)
                {
                    break;
                }

                TileCoord below = Torus.Wrap(cells, new TileCoord(cell.X, cell.Y + dy));
                if (!BuildLayers.TryTopOfCell(world, below, out TileCoord a))
                {
                    continue;
                }

                ushort obj = world.GetObject(a);
                if (BuildLayers.IsFurniture(obj) || (dy <= WallLiftCells && BuildCells.IsCellBuilding(obj)))
                {
                    anchor = a;
                    return true;
                }

                break; // ближайший к югу верх — не постройка с высотой (валун, дерево): дальше не ищем
            }

            anchor = default;
            return false;
        }
    }
}
