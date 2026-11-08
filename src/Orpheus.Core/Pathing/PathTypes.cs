using Orpheus.Core.Geometry;

namespace Orpheus.Core.Pathing
{
    /// <summary>Кто ищет путь (docs/02, 9.2): пеший — по правилу такта, летун — непроходимы только постройки и стены данжа.</summary>
    public enum PathMode : byte
    {
        /// <summary>Пеший: проходимость — как в такте (<see cref="Sim.Simulation.IsBlocked"/>).</summary>
        Walker = 0,

        /// <summary>Летун: непроходимы только постройки игрока и стены данжа.</summary>
        Flyer = 1,
    }

    /// <summary>Итог одного поиска.</summary>
    public enum PathStatus : byte
    {
        /// <summary>Поиска не было.</summary>
        None = 0,

        /// <summary>Путь до цели (длина 0 — уже на цели).</summary>
        Found = 1,

        /// <summary>
        /// Цель вне окна 48 × 48: путь до края окна — до первого раскрытого тайла края (наименьшая оценка g + h
        /// среди тайлов края). Решение шипа 0-023: в `02` 9.2 этот случай не описан.
        /// </summary>
        WindowEdge = 2,

        /// <summary>Неудача (предел узлов или окно исчерпано): «лучший частичный путь» к тайлу, ближайшему к цели по тору — Упёрся (9.2, 9.3).</summary>
        Partial = 3,

        /// <summary>
        /// Цель в замкнутой области, враг снаружи: путь — к проходимому тайлу вне области, ближайшему к цели («к краю, который
        /// ближе всего к игроку» — слово Автора 08.11, 0-034; в `02` 9.2 пока «сразу упёрся»). Враг уже там — путь пуст.
        /// </summary>
        Enclosed = 4,
    }

    /// <summary>
    /// Стоимости шагов (docs/02, 9.2). Тайл стоит «1» по длине: прямой шаг — 10, диагональный — 14 (≈ 10·√2,
    /// иначе октильная эвристика не допустима); замедляющий пол — ×1,5 к шагу, входящему в тайл.
    /// Множитель тайла — в половинках: <see cref="Normal"/> = 2/2, <see cref="Slow"/> = 3/2.
    /// </summary>
    public static class PathCost
    {
        /// <summary>Тайл непроходим.</summary>
        public const byte Blocked = 0;

        /// <summary>Обычный тайл: множитель 2/2.</summary>
        public const byte Normal = 2;

        /// <summary>Замедляющий пол (`01`): множитель 3/2.</summary>
        public const byte Slow = 3;

        /// <summary>Прямой шаг по обычному полу.</summary>
        public const int Straight = 10;

        /// <summary>Диагональный шаг по обычному полу.</summary>
        public const int Diagonal = 14;

        /// <summary>Стоимость шага с базой <paramref name="step"/> в тайл с множителем <paramref name="factor"/> (в половинках).</summary>
        public static int Step(int step, byte factor) => (step * factor) >> 1;

        /// <summary>Октильная оценка расстояния по вектору в тайлах: 10·max + 4·min.</summary>
        public static int Octile(int dx, int dy)
        {
            if (dx < 0)
            {
                dx = -dx;
            }

            if (dy < 0)
            {
                dy = -dy;
            }

            return dx > dy ? Straight * dx + (Diagonal - Straight) * dy : Straight * dy + (Diagonal - Straight) * dx;
        }
    }

    /// <summary>
    /// Карта для поиска пути: стоимость входа в тайл (<see cref="PathCost.Blocked"/>, <see cref="PathCost.Normal"/>,
    /// <see cref="PathCost.Slow"/>). Поиск спрашивает каждый тайл окна не больше одного раза.
    /// </summary>
    public interface IPathTerrain
    {
        /// <summary>Форма карты (заворот координат).</summary>
        MapShape Shape { get; }

        /// <summary>Начало поиска: мир между вызовами мог измениться — сбросить свои кэши.</summary>
        void BeginSearch();

        /// <summary>Множитель стоимости тайла (свёрнутого) для режима; 0 — непроходим.</summary>
        byte CostOf(TileCoord tile, PathMode mode);
    }
}
