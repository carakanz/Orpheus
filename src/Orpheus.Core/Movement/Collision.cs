using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.Movement
{
    /// <summary>
    /// Проходимость тайла: вопрос «тайл твёрдый?» (docs/05, 1.3: пол и объекты).
    /// Код движения знает только это — про мир, генератор и сохранения он не знает ничего
    /// (docs/07, 11.5), поэтому один и тот же код коллизии работает на клиенте, сервере и ботах.
    /// </summary>
    /// <param name="map">Форма карты (нужна для заворота координат тайла).</param>
    /// <param name="tile">Координаты тайла.</param>
    public delegate bool TileSolid(MapShape map, TileCoord tile);

    /// <summary>
    /// Коллизия персонажа — круг радиусом 0,4 тайла = <see cref="RadiusUnits"/> unit — с сеткой
    /// твёрдых тайлов (docs/00, 5.1; work/refs/movement.md). Тайл — квадрат 256 unit.
    /// <para>
    /// Обход по осям раздельно: <see cref="MoveX"/> («сверху» всегда X), потом <see cref="MoveY"/>.
    /// Упор в стену по одной оси не мешает движению по другой — персонаж скользит вдоль стены;
    /// в углу обе оси упёрты — стоит. Зазор в один тайл (256 unit) круг 204 unit проходит.
    /// </para>
    /// <para>
    /// Пересечение круга с квадратом тайла — целочисленное: ближайшая точка квадрата к центру круга
    /// (clamp по осям) и сравнение квадрата расстояния с <see cref="RadiusSquared"/> в long.
    /// <see cref="System.Math"/> для этого не нужен. Касание стены (расстояние ровно = радиус) —
    /// не пересечение: персонаж стоит в упор, вжатый в стену, и сползает по ней.
    /// </para>
    /// <para>
    /// Разности координат — только <see cref="Torus.DeltaX"/> / <see cref="Torus.DeltaY"/>,
    /// сдвиг — только <see cref="Torus.Offset"/> (docs/07, 3.2), поэтому шов тора не даёт скачка.
    /// На плоской карте (<see cref="MapShape.Wraps"/> = false) тайлов за краем нет, они не твёрдые;
    /// край карты держит персонажа <see cref="Torus.Wrap"/>.
    /// </para>
    /// </summary>
    public static class Collision
    {
        /// <summary>Радиус круга персонажа в 1/256 тайла: 0,4 × 256 = 102,4 → 102 (docs/00, 5.1).</summary>
        public const int RadiusUnits = 102;

        /// <summary>Квадрат радиуса: 102² = 10 404 (unit²).</summary>
        public const long RadiusSquared = (long)RadiusUnits * RadiusUnits;

        /// <summary>Мир без тайлов: ничего не твёрдое. Для пробега по пустому тору.</summary>
        public static readonly TileSolid Empty = IsNeverSolid;

        /// <summary>Все тайлы твёрдые: проверка «встать негде» и границы worlds, где мир целиком твёрдый.</summary>
        public static readonly TileSolid AllSolid = IsAlwaysSolid;

        /// <summary>Пересекает ли круг персонажа в <paramref name="pos"/> хотя бы один твёрдый тайл.</summary>
        public static bool TouchesSolid(in MapShape map, WorldPos pos, TileSolid solid) =>
            OverlapsAny(map, pos.X, pos.Y, solid);

        /// <summary>Пересекает ли круг с центром в <paramref name="center"/> указанный тайл (без учёта его твёрдости).</summary>
        public static bool CircleOverlapsTile(in MapShape map, WorldPos center, TileCoord tile) =>
            OverlapsTile(map, center.X, center.Y, tile);

        /// <summary>
        /// Сдвиг по X на <paramref name="dx"/> unit (только X, со сдвигом через <see cref="Torus.Offset"/>)
        /// с откатом в стену: если круг вошёл в твёрдый тайл, позиция прижимается к его грани
        /// на расстоянии радиуса — касание, не пересечение.
        /// </summary>
        public static WorldPos MoveX(in MapShape map, WorldPos pos, int dx, TileSolid solid) =>
            dx == 0 ? pos : MoveOnAxis(map, pos, dx, true, solid);

        /// <summary>Сдвиг по Y на <paramref name="dy"/> unit (см. <see cref="MoveX"/>).</summary>
        public static WorldPos MoveY(in MapShape map, WorldPos pos, int dy, TileSolid solid) =>
            dy == 0 ? pos : MoveOnAxis(map, pos, dy, false, solid);

        /// <summary>
        /// Шаг за такт: сначала X, потом Y (docs/00, 5.1, work/refs/movement.md: обход по осям
        /// раздельно). Порядок фиксирован — он часть правил повторяемости (docs/07, 11.5).
        /// </summary>
        public static WorldPos Move(in MapShape map, WorldPos pos, int dx, int dy, TileSolid solid) =>
            MoveY(map, MoveX(map, pos, dx, solid), dy, solid);

        // ---------- Одноосный сдвиг с откатом ----------

        private static WorldPos MoveOnAxis(in MapShape map, WorldPos pos, int d, bool alongX, TileSolid solid)
        {
            WorldPos q = Torus.Offset(map, pos, alongX ? new Int2(d, 0) : new Int2(0, d));
            int push = AxisPush(map, q, d, alongX, solid);
            if (push == 0)
            {
                return q;
            }

            return alongX
                ? Torus.Wrap(map, new WorldPos(q.X + push, q.Y))
                : Torus.Wrap(map, new WorldPos(q.X, q.Y + push));
        }

        // Наименьший (d > 0) или наибольший (d < 0) сдвиг вдоль оси, при котором круг ни с одним
        // твёрдым тайлом не пересекается: 0 — места хватит, откат не нужен.
        private static int AxisPush(in MapShape map, WorldPos q, int d, bool alongX, TileSolid solid)
        {
            int x0 = (q.X - RadiusUnits) >> Torus.SubTileBits;
            int x1 = (q.X + RadiusUnits) >> Torus.SubTileBits;
            int y0 = (q.Y - RadiusUnits) >> Torus.SubTileBits;
            int y1 = (q.Y + RadiusUnits) >> Torus.SubTileBits;

            int best = 0;
            for (int ty = y0; ty <= y1; ty++)
            {
                for (int tx = x0; tx <= x1; tx++)
                {
                    if (!OnMap(map, tx, ty))
                    {
                        continue;
                    }

                    TileCoord t = Torus.Wrap(map, new TileCoord(tx, ty));
                    int near = alongX ? Torus.DeltaX(map, q.X, t.X << Torus.SubTileBits)
                                      : Torus.DeltaY(map, q.Y, t.Y << Torus.SubTileBits);
                    int across = alongX ? Torus.DeltaY(map, q.Y, t.Y << Torus.SubTileBits)
                                        : Torus.DeltaX(map, q.X, t.X << Torus.SubTileBits);
                    if (!OverlapsDist(AxisDist(near), AxisDist(across)) || !solid(map, t))
                    {
                        continue;
                    }

                    // грань тайла со стороны движения + радиус: касание, пересечения нет
                    int cand = d > 0 ? near - RadiusUnits : near + Torus.SubTilesPerTile + RadiusUnits;
                    if (d > 0 ? cand < best : cand > best)
                    {
                        best = cand;
                    }
                }
            }

            return best;
        }

        // ---------- Пересечение круга с тайлом ----------

        private static bool OverlapsAny(in MapShape map, int cx, int cy, TileSolid solid)
        {
            int x0 = (cx - RadiusUnits) >> Torus.SubTileBits;
            int x1 = (cx + RadiusUnits) >> Torus.SubTileBits;
            int y0 = (cy - RadiusUnits) >> Torus.SubTileBits;
            int y1 = (cy + RadiusUnits) >> Torus.SubTileBits;

            for (int ty = y0; ty <= y1; ty++)
            {
                for (int tx = x0; tx <= x1; tx++)
                {
                    if (!OnMap(map, tx, ty))
                    {
                        continue;
                    }

                    TileCoord t = Torus.Wrap(map, new TileCoord(tx, ty));
                    if (solid(map, t) && OverlapsTile(map, cx, cy, t))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool OverlapsTile(in MapShape map, int cx, int cy, TileCoord tile) =>
            OverlapsDist(
                AxisDist(Torus.DeltaX(map, cx, tile.X << Torus.SubTileBits)),
                AxisDist(Torus.DeltaY(map, cy, tile.Y << Torus.SubTileBits)));

        // Расстояние по одной оси от центра круга до квадрата тайла (0 — под центром).
        // near — дельта до левого (верхнего) края тайла; d + 256 — до правого (нижнего).
        private static int AxisDist(int near)
        {
            if (near > 0)
            {
                return near;
            }

            int far = near + Torus.SubTilesPerTile;
            return far < 0 ? -far : 0;
        }

        private static bool OverlapsDist(long dx, long dy) => dx * dx + dy * dy < RadiusSquared;

        private static bool OnMap(in MapShape map, int tx, int ty) =>
            map.Wraps || (tx >= 0 && tx < map.Width && ty >= 0 && ty < map.Height);

        private static bool IsNeverSolid(MapShape map, TileCoord tile) => false;

        private static bool IsAlwaysSolid(MapShape map, TileCoord tile) => true;
    }
}
