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
    /// Владелец функции «тайл твёрдый?» (<see cref="TileSolid"/>, его <c>Target</c>), знающий ячейки 0,5 × 0,5 (E2-12):
    /// для твёрдого тайла — маска твёрдых ячеек (бит 1 — СЗ, 2 — СВ, 4 — ЮЗ, 8 — ЮВ; 15 — весь тайл). Так одна и та же
    /// <see cref="TileSolid"/> (<c>Simulation.IsBlocked</c>) даёт тонкие стены серверу, предсказанию клиента и ботам;
    /// функции без такого владельца (лямбды тестов) — целые тайлы.
    /// </summary>
    public interface ITileCells
    {
        /// <summary>Маска твёрдых ячеек тайла, о котором <see cref="TileSolid"/> ответила «твёрдый».</summary>
        byte SolidCells(MapShape map, TileCoord tile);
    }

    /// <summary>
    /// Коллизия персонажа — круг радиусом 0,4 тайла = <see cref="RadiusUnits"/> unit — с сеткой
    /// твёрдых тайлов (docs/00, 5.1; work/refs/movement.md). Тайл — квадрат 256 unit.
    /// <para>
    /// Обход по осям раздельно: <see cref="MoveX"/> («сверху» всегда X), потом <see cref="MoveY"/>.
    /// Упор в стену по одной оси не мешает движению по другой — персонаж скользит вдоль стены;
    /// в углу обе оси упёрты — стоит. Зазор в один тайл (256 unit) круг 204 unit проходит;
    /// в «диагональный зазор» между тайлами, касающимися углами, — нет (ширина зазора 0).
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
            OverlapsTile(map, center.X, center.Y, tile, FullMask);

        /// <summary>Пересекает ли круг ячейки тайла по маске <paramref name="cells"/> (E2-12; 15 — весь тайл).</summary>
        public static bool CircleOverlapsCells(in MapShape map, WorldPos center, TileCoord tile, byte cells) =>
            OverlapsTile(map, center.X, center.Y, tile, cells);

        /// <summary>Маска твёрдых ячеек тайла по функции <paramref name="solid"/>: не твёрдый — 0; владелец без ячеек — 15.</summary>
        public static byte SolidCellsOf(in MapShape map, TileCoord tile, TileSolid solid)
        {
            if (!solid(map, tile))
            {
                return 0;
            }

            return solid.Target is ITileCells cells ? (byte)(cells.SolidCells(map, tile) & FullMask) : FullMask;
        }

        private const byte FullMask = 15;
        private const int Half = Torus.SubTilesPerTile / 2;

        /// <summary>
        /// Сдвиг по X на <paramref name="dx"/> unit (только X, со сдвигом через <see cref="Torus.Offset"/>)
        /// с откатом в стену: если круг вошёл в твёрдый тайл, позиция откатывается до касания
        /// (гранью — на радиус от грани, углом — ближе, см. <see cref="ContactReach"/>), но не дальше
        /// точки, где круг стоял до шага: итог по оси всегда между стартом и целью.
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
            int push = AxisPush(map, pos, q, d, alongX, solid);
            if (push == 0)
            {
                return q;
            }

            return Torus.Offset(map, q, alongX ? new Int2(push, 0) : new Int2(0, push));
        }

        // Наименьший (d > 0) или наибольший (d < 0) сдвиг вдоль оси, при котором круг ни с одним
        // твёрдым тайлом не пересекается: 0 — места хватит, откат не нужен.
        // Упор — точное касание: если круг задевает тайл гранью (поперёк оси тайл под центром,
        // across = 0), центр встаёт на радиус от грани; если углом (across > 0), центр встаёт
        // на ContactReach(across) = ⌈√(r² − across²)⌉ от грани — ближе, чем на радиус. Так упор
        // не уводит круг за точку, где он стоял до шага (pos): от тайла, которого круг в pos не
        // касался, он был не ближе ContactReach (поперёк оси при сдвиге по оси ничего не меняется),
        // значит, откат по оси не больше пройденного. Итог — между стартом и целью.
        // Тайл, который круг касался уже в pos (блок поставили на персонажа, спавн в стене), упора
        // не даёт: из такого тайла можно выйти, а откат от него увёл бы назад, за старт.
        private static int AxisPush(in MapShape map, WorldPos pos, WorldPos q, int d, bool alongX, TileSolid solid)
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
                    byte mask = SolidCellsOf(map, t, solid);
                    if (mask == 0)
                    {
                        continue;
                    }

                    // Целый тайл — один квадрат 256 (как до E2-12, бит в бит); ячейки — квадраты 128 по маске.
                    int size = mask == FullMask ? Torus.SubTilesPerTile : Half;
                    for (int k = 0; k < 4; k++)
                    {
                        if (mask != FullMask && (mask & (1 << k)) == 0)
                        {
                            continue;
                        }

                        int bx = (t.X << Torus.SubTileBits) + (mask == FullMask ? 0 : (k & 1) * Half);
                        int by = (t.Y << Torus.SubTileBits) + (mask == FullMask ? 0 : (k >> 1) * Half);
                        int near = alongX ? Torus.DeltaX(map, q.X, bx) : Torus.DeltaY(map, q.Y, by);
                        int across = AxisDist(alongX ? Torus.DeltaY(map, q.Y, by) : Torus.DeltaX(map, q.X, bx), size);
                        if (OverlapsDist(AxisDist(near, size), across) && !OverlapsBox(map, pos.X, pos.Y, bx, by, size))
                        {
                            // грань со стороны движения + досягаемость касания: пересечения нет
                            int reach = ContactReach(across);
                            int cand = d > 0 ? near - reach : near + size + reach;
                            if (d > 0 ? cand < best : cand > best)
                            {
                                best = cand;
                            }
                        }

                        if (mask == FullMask)
                        {
                            break;
                        }
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// Наименьшее целое h ≥ 0, при котором h² + across² ≥ r²: на таком расстоянии вдоль оси
        /// от грани тайла круг его не пересекает (касание — не пересечение). across = 0 → r = 102;
        /// across ≥ r → 0. Целочисленно, без квадратного корня (docs/07, 11.5).
        /// </summary>
        public static int ContactReach(int across)
        {
            if (across >= RadiusUnits)
            {
                return 0;
            }

            long rest = RadiusSquared - (long)across * across;
            int h = RadiusUnits;
            while (h > 0 && (long)(h - 1) * (h - 1) >= rest)
            {
                h--;
            }

            return h;
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
                    byte mask = SolidCellsOf(map, t, solid);
                    if (mask != 0 && OverlapsTile(map, cx, cy, t, mask))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Круг задевает твёрдые ячейки тайла по маске (15 — весь тайл одним квадратом).
        private static bool OverlapsTile(in MapShape map, int cx, int cy, TileCoord tile, byte mask)
        {
            int ox = tile.X << Torus.SubTileBits;
            int oy = tile.Y << Torus.SubTileBits;
            if (mask == FullMask)
            {
                return OverlapsBox(map, cx, cy, ox, oy, Torus.SubTilesPerTile);
            }

            for (int k = 0; k < 4; k++)
            {
                if ((mask & (1 << k)) != 0 && OverlapsBox(map, cx, cy, ox + (k & 1) * Half, oy + (k >> 1) * Half, Half))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool OverlapsBox(in MapShape map, int cx, int cy, int bx, int by, int size) =>
            OverlapsDist(AxisDist(Torus.DeltaX(map, cx, bx), size), AxisDist(Torus.DeltaY(map, cy, by), size));

        // Расстояние по одной оси от центра круга до квадрата стороной size (0 — под центром).
        // near — дельта до левого (верхнего) края; near + size — до правого (нижнего).
        private static int AxisDist(int near, int size)
        {
            if (near > 0)
            {
                return near;
            }

            int far = near + size;
            return far < 0 ? -far : 0;
        }

        private static bool OverlapsDist(long dx, long dy) => dx * dx + dy * dy < RadiusSquared;

        private static bool OnMap(in MapShape map, int tx, int ty) =>
            map.Wraps || (tx >= 0 && tx < map.Width && ty >= 0 && ty < map.Height);

        private static bool IsNeverSolid(MapShape map, TileCoord tile) => false;

        private static bool IsAlwaysSolid(MapShape map, TileCoord tile) => true;
    }
}
