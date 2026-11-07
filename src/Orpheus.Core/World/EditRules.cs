using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Ответ на запрос правки (docs/07, 3.7 «Ответ»; `05` 1.3 — строки отказа): «принято» или «отказ: причина».
    /// Один байт по сети (0-028, протокол 7). Строки — <see cref="EditRules.Describe"/>.
    /// </summary>
    public enum EditReply : byte
    {
        /// <summary>Принято (для линии — хоть один тайл; какие — маска ответа).</summary>
        Accepted = 0,

        /// <summary>«занято»: тайл в этом такте уже правлен пришедшим раньше (3.7 «Конфликт», `05` 1.4) или на нём постройка.</summary>
        Busy = 1,

        /// <summary>«далеко»: копание дальше 1,5 тайла от центра персонажа, постановка — дальше радиуса 6.</summary>
        OutOfReach = 2,

        /// <summary>«пусто»: копать нечего — тайл уже пуст или не тот, что в начале копания (`07` 3.7 «Как играется»).</summary>
        Empty = 3,

        /// <summary>«занято существом» (`05` 1.3): непроходимое на тайл с существом.</summary>
        Occupied = 4,

        /// <summary>«враг ближе 2 тайлов» (`05` 1.3).</summary>
        EnemyNear = 5,

        /// <summary>«сначала выкопать» (`05` 1.3): на тайле природный объект.</summary>
        DigFirst = 6,

        /// <summary>«нужен настил» (`05` 1.3): пол — жидкость.</summary>
        NeedPlatform = 7,

        /// <summary>«слишком быстро»: больше 20 тайлов в секунду (3.7) — строки в `05` нет, допущение 0-028.</summary>
        TooFast = 8,

        /// <summary>«прервано»: кнопку отпустили или начали копать другой тайл до конца копания (допущение 0-028).</summary>
        Interrupted = 9,

        /// <summary>«нельзя»: не копается / не ставится в вехе 0, линия длиннее 32, испорченный запрос (допущение 0-028).</summary>
        NotAllowed = 10,
    }

    /// <summary>
    /// Правила правок тайлов вехи 0 (docs/07, 3.7; `05` 1.3–1.5) — одно место для сервера, клиента и ботов. Все числа —
    /// <i>(прикидка)</i>; время копания скалы — вопрос <c>[док]</c> карточки 0-028, до ответа — <see cref="DigFrames"/>.
    /// Ярус инструмента, зоны `01`, права, ресурсы и инвентарь — не в вехе 0 (допущения 0-028, backlog <c>[док]</c>).
    /// </summary>
    public static class EditRules
    {
        private const int U = Torus.SubTilesPerTile;

        /// <summary>
        /// Время копания природного объекта — кадров ввода 60 Гц с зажатой кнопкой копания: 15 = 0,25 с
        /// <i>(прикидка 0-028; `[док]`: чисел копания в вехе 0 нет)</i>. Одно место на всё.
        /// </summary>
        public const int DigFrames = 15;

        /// <summary>Досягаемость копания — 1,5 тайла от центра персонажа до центра тайла (`05` 1.3), в единицах.</summary>
        public const int DigReachUnits = U * 3 / 2;

        /// <summary>Радиус постановки — 6 тайлов от тайла персонажа по оси максимума (квадрат 13 × 13, `05` 1.3).</summary>
        public const int PlaceRadiusTiles = 6;

        /// <summary>Линия постановки — до 32 тайлов (`05` 1.5).</summary>
        public const int MaxLineTiles = 32;

        /// <summary>Постановок не больше 20 тайлов в секунду (3.7).</summary>
        public const int PlaceTilesPerSecond = 20;

        /// <summary>Непроходимое и ловушку — не ближе 2 тайлов от центра врага до центра тайла (`05` 1.3), в единицах.</summary>
        public const int EnemyClearanceUnits = 2 * U;

        /// <summary>Строка отказа для игрока (`05` 1.3; свои — помечены в <see cref="EditReply"/>).</summary>
        public static string Describe(EditReply reply)
        {
            switch (reply)
            {
                case EditReply.Accepted: return "принято";
                case EditReply.Busy: return "занято";
                case EditReply.OutOfReach: return "далеко";
                case EditReply.Empty: return "пусто";
                case EditReply.Occupied: return "занято существом";
                case EditReply.EnemyNear: return "враг ближе 2 тайлов";
                case EditReply.DigFirst: return "сначала выкопать";
                case EditReply.NeedPlatform: return "нужен настил";
                case EditReply.TooFast: return "слишком быстро";
                case EditReply.Interrupted: return "прервано";
                default: return "нельзя";
            }
        }

        /// <summary>Исход правки в такте → ответ клиенту.</summary>
        public static EditReply FromResult(EditResult result)
        {
            switch (result)
            {
                case EditResult.Applied: return EditReply.Accepted;
                case EditResult.Busy: return EditReply.Busy;
                case EditResult.Occupied: return EditReply.Occupied;
                case EditResult.EnemyNear: return EditReply.EnemyNear;
                default: return EditReply.NotAllowed;
            }
        }

        /// <summary>Центр тайла в единицах мира.</summary>
        public static WorldPos CenterOf(TileCoord t) => new WorldPos(t.X * U + U / 2, t.Y * U + U / 2);

        /// <summary>Тайл в досягаемости копания: центр персонажа — центр тайла ≤ 1,5 тайла, по тору.</summary>
        public static bool InDigReach(in MapShape map, WorldPos center, TileCoord tile) =>
            Torus.Dist2(map, center, CenterOf(Torus.Wrap(map, tile))) <= (long)DigReachUnits * DigReachUnits;

        /// <summary>Тайл в радиусе постановки: от тайла персонажа ≤ 6 по оси максимума, по тору (`05` 1.4: через шов — тоже).</summary>
        public static bool InPlaceRadius(in MapShape map, WorldPos center, TileCoord tile)
        {
            Int2 d = Torus.Delta(map, Torus.TileOf(map, center), Torus.Wrap(map, tile));
            return Math.Abs(d.X) <= PlaceRadiusTiles && Math.Abs(d.Y) <= PlaceRadiusTiles;
        }

        /// <summary>Объект копается в вехе 0: природный одиночный (скала, дерево, залежь, пень). Постройки — сносом (потом).</summary>
        public static bool IsDiggable(ushort obj) =>
            obj != ObjectId.Empty && ObjectId.IsNatural(obj) && !ObjectId.IsPart(obj) && !ObjectId.IsMultiTileAnchor(obj);

        /// <summary>
        /// Объект ставится в вехе 0 (без ресурсов и палитры — допущение 0-028): одиночные постройки — стены, забор, факел,
        /// ковёр, шипы. Двери, многотайловые, природные — нет.
        /// </summary>
        public static bool IsPlaceable(ushort obj)
        {
            if (ObjectId.RotationOf(obj) != 0)
            {
                return false;
            }

            switch (obj)
            {
                case ObjectId.WoodWall:
                case ObjectId.StoneWall:
                case ObjectId.WoodFence:
                case ObjectId.Torch:
                case ObjectId.Carpet:
                case ObjectId.Spikes:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Непроходимый объект или ловушка: их не ставят на существо и ближе 2 тайлов к врагу (`05` 1.3).</summary>
        public static bool NeedsClearance(ushort obj) => !ObjectId.IsPassable(obj) || ObjectId.IdOf(obj) == ObjectId.Spikes;

        /// <summary>Непроходимый объект: его не ставят на тайл с существом (ловушка — проходима, на существо можно).</summary>
        public static bool BlocksCreature(ushort obj) => !ObjectId.IsPassable(obj);

        /// <summary>
        /// Проверка копания по тайлу мира (без сущностей): что копать и досягаемость. Принято — <see cref="EditReply.Accepted"/>.
        /// </summary>
        public static EditReply CheckDig(WorldModel world, WorldPos center, TileCoord tile)
        {
            if (!InDigReach(world.Shape, center, tile))
            {
                return EditReply.OutOfReach;
            }

            ushort obj = world.GetObject(Torus.Wrap(world.Shape, tile));
            if (obj == ObjectId.Empty)
            {
                return EditReply.Empty;
            }

            return IsDiggable(obj) ? EditReply.Accepted : EditReply.NotAllowed;
        }

        /// <summary>
        /// Проверка постановки одного тайла по миру (без сущностей — их проверяет такт, <c>Simulation</c>): радиус, тайл
        /// свободен, пол не жидкость.
        /// </summary>
        public static EditReply CheckPlaceTile(WorldModel world, WorldPos center, TileCoord tile, ushort obj)
        {
            if (!IsPlaceable(obj))
            {
                return EditReply.NotAllowed;
            }

            if (!InPlaceRadius(world.Shape, center, tile))
            {
                return EditReply.OutOfReach;
            }

            TileCoord t = Torus.Wrap(world.Shape, tile);
            ushort here = world.GetObject(t);
            if (here != ObjectId.Empty)
            {
                return ObjectId.IsNatural(here) ? EditReply.DigFirst : EditReply.Busy;
            }

            byte floor = world.GetFloor(t);
            if (floor == FloorId.Water || floor == FloorId.Lava)
            {
                return EditReply.NeedPlatform;
            }

            return EditReply.Accepted;
        }

        /// <summary>
        /// Тайлы линии от <paramref name="from"/> до <paramref name="to"/> (Брезенхэм по кратчайшему пути тора, концы включены) —
        /// в <paramref name="dest"/> (≥ <see cref="MaxLineTiles"/>). Длиннее 32 тайлов — −1. Тайлы свёрнуты в карту.
        /// </summary>
        public static int Line(in MapShape map, TileCoord from, TileCoord to, Span<TileCoord> dest)
        {
            Int2 d = Torus.Delta(map, Torus.Wrap(map, from), Torus.Wrap(map, to));
            int dx = Math.Abs(d.X);
            int dy = Math.Abs(d.Y);
            int n = Math.Max(dx, dy) + 1;
            if (n > MaxLineTiles || dest.Length < n)
            {
                return -1;
            }

            int sx = d.X < 0 ? -1 : 1;
            int sy = d.Y < 0 ? -1 : 1;
            int x = from.X;
            int y = from.Y;
            int err = dx - dy;
            for (int i = 0; i < n; i++)
            {
                dest[i] = Torus.Wrap(map, new TileCoord(x, y));
                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x += sx;
                }

                if (e2 < dx)
                {
                    err += dx;
                    y += sy;
                }
            }

            return n;
        }

        /// <summary>Персонаж (круг радиуса <see cref="Collision.RadiusUnits"/>) задевает тайл.</summary>
        public static bool Overlaps(in MapShape map, WorldPos center, TileCoord tile) => Collision.CircleOverlapsTile(map, center, tile);

        /// <summary>Враг ближе 2 тайлов: центр врага — центр тайла &lt; 2 тайлов, по тору.</summary>
        public static bool EnemyTooClose(in MapShape map, WorldPos enemy, TileCoord tile) =>
            Torus.Dist2(map, enemy, CenterOf(Torus.Wrap(map, tile))) < (long)EnemyClearanceUnits * EnemyClearanceUnits;
    }
}
