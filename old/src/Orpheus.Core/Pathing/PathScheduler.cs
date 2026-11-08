using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.Pathing
{
    /// <summary>
    /// Монотонные часы для бюджета поиска по времени (docs/07, 2.4). В ядре настенных часов нет (ORP005):
    /// реализацию даёт хост (сервер — <c>Stopwatch</c>). Нет часов — нет бюджета по времени (режим симуляции, 11.5).
    /// </summary>
    public interface IMonotonicClock
    {
        /// <summary>Текущее значение счётчика.</summary>
        long Timestamp { get; }

        /// <summary>Отсчётов счётчика в секунду.</summary>
        long Frequency { get; }
    }

    /// <summary>Запрос поиска в очереди.</summary>
    public readonly struct PathRequest
    {
        public readonly int Owner;
        public readonly TileCoord Start;
        public readonly TileCoord Goal;
        public readonly PathMode Mode;
        public readonly long RequestedTick;

        public PathRequest(int owner, TileCoord start, TileCoord goal, PathMode mode, long requestedTick)
        {
            Owner = owner;
            Start = start;
            Goal = goal;
            Mode = mode;
            RequestedTick = requestedTick;
        }
    }

    /// <summary>
    /// Бюджет поиска пути за такт (docs/07, 2.4; docs/02, 9.2): очередь запросов по порядку поступления;
    /// за такт — не больше <see cref="MaxPerTick"/> поисков <b>и</b> не больше <see cref="BudgetMicroseconds"/>,
    /// остальные ждут следующего такта. Время проверяется только перед началом поиска: начатый поиск
    /// доводится до конца, поэтому путь от бюджета не зависит — меняется лишь такт, в который он готов.
    /// Без часов (<c>clock</c> = null) — только предел по числу: такт применения повторяем (режим симуляции, 11.5).
    /// Без выделений после конструктора: кольцо запросов, результаты — в буферах владельцев.
    /// </summary>
    public sealed class PathScheduler
    {
        /// <summary>Поисков за такт (07 2.4).</summary>
        public const int DefaultMaxPerTick = 40;

        /// <summary>Время поиска за такт, мкс (07 2.4).</summary>
        public const long DefaultBudgetMicroseconds = 3000;

        private readonly PathFinder _finder;
        private readonly IPathTerrain _terrain;
        private readonly PathBuffer[] _results;
        private readonly PathRequest[] _queue;
        private readonly IMonotonicClock? _clock;
        private readonly long _budgetTicks;
        private int _head;
        private int _count;

        /// <param name="finder">Поиск.</param>
        /// <param name="terrain">Карта.</param>
        /// <param name="results">Буфер результата на каждого владельца (номер владельца — индекс).</param>
        /// <param name="queueCapacity">Наибольшая длина очереди.</param>
        /// <param name="maxPerTick">Поисков за такт.</param>
        /// <param name="clock">Монотонные часы; null — без бюджета по времени.</param>
        /// <param name="budgetMicroseconds">Время поиска за такт, мкс.</param>
        public PathScheduler(
            PathFinder finder,
            IPathTerrain terrain,
            PathBuffer[] results,
            int queueCapacity,
            int maxPerTick = DefaultMaxPerTick,
            IMonotonicClock? clock = null,
            long budgetMicroseconds = DefaultBudgetMicroseconds)
        {
            _finder = finder ?? throw new ArgumentNullException(nameof(finder));
            _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
            _results = results ?? throw new ArgumentNullException(nameof(results));
            if (queueCapacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(queueCapacity), queueCapacity, "Очередь — хотя бы на 1 запрос.");
            }

            if (maxPerTick < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPerTick), maxPerTick, "За такт — хотя бы 1 поиск.");
            }

            if (budgetMicroseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(budgetMicroseconds), budgetMicroseconds, "Бюджет не может быть отрицательным.");
            }

            _queue = new PathRequest[queueCapacity];
            MaxPerTick = maxPerTick;
            _clock = clock;
            BudgetMicroseconds = budgetMicroseconds;
            if (clock != null)
            {
                if (clock.Frequency <= 0)
                {
                    throw new ArgumentException("Частота часов должна быть положительной.", nameof(clock));
                }

                _budgetTicks = budgetMicroseconds * clock.Frequency / 1_000_000;
            }
        }

        /// <summary>Поисков за такт.</summary>
        public int MaxPerTick { get; }

        /// <summary>Время поиска за такт, мкс (действует только с часами).</summary>
        public long BudgetMicroseconds { get; }

        /// <summary>Запросов в очереди.</summary>
        public int Pending => _count;

        /// <summary>Поисков выполнено всего.</summary>
        public long Completed { get; private set; }

        /// <summary>Сумма отложенных запросов по тактам (запрос, ждущий 3 такта, считается 3 раза).</summary>
        public long DeferredTotal { get; private set; }

        /// <summary>Тактов, в которых сработал предел по времени.</summary>
        public long TimeLimitedTicks { get; private set; }

        /// <summary>Итогов «частичный путь».</summary>
        public long PartialCount { get; private set; }

        /// <summary>Итогов «цель в замкнутой области».</summary>
        public long EnclosedCount { get; private set; }

        /// <summary>Итогов «до края окна».</summary>
        public long WindowEdgeCount { get; private set; }

        /// <summary>Наибольшее ожидание запроса, тактов (0 — выполнен в такт постановки).</summary>
        public long MaxWaitTicks { get; private set; }

        /// <summary>Выполнено в последнем такте.</summary>
        public int LastTickCompleted { get; private set; }

        /// <summary>Время поиска в последнем такте, в отсчётах часов (0 без часов).</summary>
        public long LastTickElapsed { get; private set; }

        /// <summary>Поставить запрос; false — очередь полна.</summary>
        public bool Enqueue(int owner, TileCoord start, TileCoord goal, PathMode mode, long tick)
        {
            if ((uint)owner >= (uint)_results.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(owner), owner, "Нет буфера результата для владельца.");
            }

            if (_count == _queue.Length)
            {
                return false;
            }

            int tail = _head + _count;
            if (tail >= _queue.Length)
            {
                tail -= _queue.Length;
            }

            _queue[tail] = new PathRequest(owner, start, goal, mode, tick);
            _count++;
            return true;
        }

        /// <summary>Выполнить поиски такта <paramref name="tick"/>; возвращает их число.</summary>
        public int RunTick(long tick)
        {
            long t0 = _clock != null ? _clock.Timestamp : 0;
            long elapsed = 0;
            int done = 0;
            while (_count > 0 && done < MaxPerTick)
            {
                if (_clock != null)
                {
                    elapsed = _clock.Timestamp - t0;
                    if (elapsed >= _budgetTicks)
                    {
                        TimeLimitedTicks++;
                        break;
                    }
                }

                PathRequest r = _queue[_head];
                _head++;
                if (_head == _queue.Length)
                {
                    _head = 0;
                }

                _count--;
                PathBuffer result = _results[r.Owner];
                PathStatus status = _finder.Find(_terrain, r.Start, r.Goal, r.Mode, result);
                result.RequestedTick = r.RequestedTick;
                result.DoneTick = tick;
                long wait = tick - r.RequestedTick;
                if (wait > MaxWaitTicks)
                {
                    MaxWaitTicks = wait;
                }

                switch (status)
                {
                    case PathStatus.Partial:
                        PartialCount++;
                        break;
                    case PathStatus.Enclosed:
                        EnclosedCount++;
                        break;
                    case PathStatus.WindowEdge:
                        WindowEdgeCount++;
                        break;
                }

                done++;
            }

            if (_clock != null)
            {
                elapsed = _clock.Timestamp - t0;
            }

            Completed += done;
            DeferredTotal += _count;
            LastTickCompleted = done;
            LastTickElapsed = elapsed;
            return done;
        }
    }
}
