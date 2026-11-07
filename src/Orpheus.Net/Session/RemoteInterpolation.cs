using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Чужие сущности у клиента (docs/07, 2.9; карточки 0-017 и 0-021): интерполяция между снимками 30 Гц на время показа
    /// «сервер сейчас − <see cref="DelayUs"/>». Чистая сборка ядра: без Unity и рефлексии — клиент Unity только рисует
    /// то, что дал этот класс, тесты гоняют его в CI.
    /// <list type="number">
    /// <item><b>Часы (оценка «сервер сейчас»).</b> Каждый принятый снимок — образец «время такта минус местное время
    /// прихода». Оценка — медиана последних <see cref="ClockWindow"/> образцов (≈ 1 с): один опоздавший снимок (+3σ)
    /// её не двигает, сдвиг задержки дольше полуокна — двигает (0-021, замечание 1 проверки 0-017; раньше часы
    /// переставлялись по ошибке одного образца &gt; 100 мс). «Сервер сейчас» — без owd: время такта новейшего снимка в
    /// момент прихода (иначе запас 100 мс съедает owd — backlog [док] 2.4/2.9).</item>
    /// <item><b>Ход показа.</b> <see cref="BeginFrame"/> раз за кадр: время показа не прыгает за часами, а идёт
    /// со своей скоростью к цели «оценка − 100 мс»: в полосе ±<see cref="FastUs"/> — поправка хода ≤ 1 % (2.4), дальше —
    /// быстрее, до <see cref="CatchUpRate"/> (догон), или медленнее, до <see cref="MinRate"/>; назад — никогда.
    /// Отстало больше чем на <see cref="JumpUs"/> (фриз хоста, 2.4) — перестановка вперёд (<see cref="ClockResets"/>).</item>
    /// <item><b>Позиция.</b> Пара снимков вокруг времени показа, разность — <see cref="Torus.Delta(in MapShape, WorldPos, WorldPos)"/>
    /// (шов тора без скачка), доля — по времени тактов. Время показа не уходит дальше новейшего снимка: снимки опоздали —
    /// показ стоит на новейшем (<see cref="HoldFrames"/>; экстраполяции 2.9 пока нет — backlog), потом догоняет со
    /// скоростью ≤ <see cref="CatchUpRate"/> — без прыжка на всё простоявшее время.</item>
    /// <item><b>Замер для net-smoke.</b> Шаг каждой сущности за кадр — против предела скорости × приращение времени показа
    /// (без интерполяции или с разностью без тора шаг выходит за предел, <see cref="StepViolations"/>) и против предела ×
    /// приращение местного времени (быстрее — догон, <see cref="CatchUpSteps"/>; больше <see cref="CatchUpRate"/> × предел —
    /// тоже нарушение).</item>
    /// </list>
    /// </summary>
    public sealed class RemoteInterpolation
    {
        /// <summary>Задержка показа: 100 мс (07, 2.4 и 2.9) — три снимка, переживает два потерянных подряд.</summary>
        public const long DelayUs = 100_000;

        /// <summary>
        /// Снимков в кольце: 16 × 33 мс ≈ 533 мс истории — показ, отставший от цели до <see cref="JumpUs"/>, догоняет
        /// по настоящим снимкам (300 + 100 + 33 мс &lt; 533).
        /// </summary>
        public const int RingSize = 16;

        /// <summary>Образцов часов в окне медианы: 30 снимков ≈ 1 с.</summary>
        public const int ClockWindow = 30;

        /// <summary>Наибольшая поправка хода показа в полосе ±<see cref="FastUs"/> (2.4): 1 %.</summary>
        public const double MaxSkew = 0.01;

        /// <summary>Ошибка 10 мс даёт поправку хода 1 %.</summary>
        public const double SlewUs = 1_000_000;

        /// <summary>
        /// Полоса плавной поправки, мкс: шум медианы 30 образцов при σ 30 мс ≈ 7 мс — в полосе ±20 мс ход меняется не
        /// больше чем на 1 %, глазу не видно.
        /// </summary>
        public const long FastUs = 20_000;

        /// <summary>За полосой каждые 20 мс ошибки добавляют к ходу 100 %: на 40 мс — уже предел догона.</summary>
        public const double FastTauUs = 20_000;

        /// <summary>
        /// Наибольшая скорость показа при догоне, k = 2: чужой бегун (6 т/с) на догоне идёт 12 т/с — как рывок уворота
        /// (07, 2.4), скорость, которая в игре и так бывает, а не телепорт. Догон 100 мс занимает 100 мс (6 кадров);
        /// при k = 1,25 он шёл бы 400 мс, всё это время запас меньше 100 мс и новая остановка вероятнее.
        /// </summary>
        public const double CatchUpRate = 2.0;

        /// <summary>Наименьшая скорость показа: показ впереди цели (задержка выросла) — замедляется вдвое, не стоит и не идёт назад.</summary>
        public const double MinRate = 0.5;

        /// <summary>
        /// Показ отстал от цели больше чем на 300 мс (фриз хоста, 2.4: сервер прыгнул на десятки тактов) — перестановка
        /// вперёд: догон на k = 2 шёл бы дольше 300 мс.
        /// </summary>
        public const long JumpUs = 300_000;

        /// <summary>
        /// Предел скорости по оси для замера по умолчанию: базовый бег 51,2 unit за такт = 1536 unit/с, × 1,25 — квант
        /// позиции в снимке 1/32 тайла (8 unit) на паре снимков даёт до 56–59 unit за такт.
        /// </summary>
        public const double DefaultMaxUnitsPerSecond = 51.2 * Simulation.TickRate * 1.25;

        /// <summary>Допуск шага на округление float, unit.</summary>
        public const double StepSlackUnits = 2;

        private struct Track
        {
            public WorldPos From;
            public float Dx;
            public float Dy;
            public long Frame;
        }

        private readonly MapShape _map;
        private readonly Snapshot[] _ring = new Snapshot[RingSize];
        private readonly Dictionary<int, Track> _tracks = new Dictionary<int, Track>();
        private readonly double _maxUnitsPerUs;
        private int _head;
        private int _count;

        // Часы: «сервер сейчас» = местное + _offsetUs; _offsetUs — медиана окна образцов (время такта − местное время прихода).
        private readonly long[] _samples = new long[ClockWindow];
        private readonly long[] _sorted = new long[ClockWindow];
        private int _sampleHead;
        private int _sampleCount;
        private long _offsetUs;

        // Кадр.
        private bool _hasRender;
        private double _render;   // время показа, мкс шкалы сервера (с долями — ход показа не теряет мкс)
        private long _renderUs;
        private long _lastLocalUs;
        private long _frameDtUs;  // приращение времени показа за кадр
        private long _realDtUs;   // приращение местного времени за кадр
        private bool _resetFrame; // в этом кадре показ переставлен вперёд — шаг не мерим
        private long _frame;
        private bool _held;
        private int _ia;
        private int _ib;
        private double _frac;

        /// <param name="map">Форма карты: разность позиций — по тору.</param>
        /// <param name="maxUnitsPerSecond">Предел скорости по оси для замера шага (unit/с).</param>
        public RemoteInterpolation(MapShape map, double maxUnitsPerSecond = DefaultMaxUnitsPerSecond)
        {
            _map = map;
            _maxUnitsPerUs = maxUnitsPerSecond / 1_000_000.0;
            for (int i = 0; i < RingSize; i++)
            {
                _ring[i] = new Snapshot();
            }
        }

        /// <summary>Время такта в шкале сервера, мкс.</summary>
        public static long TickTimeUs(uint tick) => (long)tick * 1_000_000 / Simulation.TickRate;

        /// <summary>Есть ли снимки.</summary>
        public bool HasSnapshots => _count > 0;

        /// <summary>Новейший снимок (набор сущностей для рисования). До первого — пустой.</summary>
        public Snapshot Latest => _ring[Mod(_head - 1)];

        /// <summary>Время показа последнего кадра, мкс шкалы сервера.</summary>
        public long RenderTimeUs => _renderUs;

        /// <summary>Запас: время новейшего снимка минус время показа, мкс (≈ 67–100 мс при ровной связи).</summary>
        public long BufferUs => _count == 0 ? 0 : TickTimeUs(Latest.Tick) - _renderUs;

        /// <summary>Скорость показа в последнем кадре (1 — как местные часы; 0,5…2).</summary>
        public double PlaybackRate { get; private set; } = 1.0;

        /// <summary>Перестановок показа вперёд (отстал от цели больше <see cref="JumpUs"/>).</summary>
        public long ClockResets { get; private set; }

        /// <summary>Кадров рисования (с временем показа).</summary>
        public long Frames { get; private set; }

        /// <summary>Кадров, где время показа не выросло (стоит на новейшем снимке).</summary>
        public long SameTimeFrames { get; private set; }

        /// <summary>Кадров, где время показа дошло до новейшего снимка: чужие стоят на нём.</summary>
        public long HoldFrames { get; private set; }

        /// <summary>Кадров, где время показа старше старейшего снимка в кольце.</summary>
        public long BeforeOldestFrames { get; private set; }

        /// <summary>Позиций по паре через шов тора (прямая разность ≠ разности по тору).</summary>
        public long SeamSamples { get; private set; }

        /// <summary>Шагов сущностей, измеренных между соседними кадрами.</summary>
        public long Steps { get; private set; }

        /// <summary>Наибольший шаг сущности за кадр, unit по оси.</summary>
        public double MaxStepUnits { get; private set; }

        /// <summary>Наибольшее отношение шага к пределу (предел скорости × приращение времени показа + допуск).</summary>
        public double MaxStepRatio { get; private set; }

        /// <summary>
        /// Нарушений: шаг больше предела скорости × приращение времени показа (ступеньки, пара без тора) или больше
        /// <see cref="CatchUpRate"/> × предел скорости × приращение местного времени (догон без предела).
        /// </summary>
        public long StepViolations { get; private set; }

        /// <summary>
        /// Шагов догона: быстрее предела скорости × приращение местного времени (показ шёл быстрее часов). Бывают после
        /// остановки на новейшем; много их — показ всё время упирается в новейший снимок (нет задержки показа).
        /// </summary>
        public long CatchUpSteps { get; private set; }

        /// <summary>Наибольшее отношение шага к пределу по местному времени (≤ <see cref="CatchUpRate"/> — догон ограничен).</summary>
        public double MaxCatchUpRatio { get; private set; }

        /// <summary>Оценка «сервер сейчас» в мкс шкалы сервера; до первого снимка — 0.</summary>
        public double ServerNowUs(long localUs) => _sampleCount > 0 ? localUs + _offsetUs : 0.0;

        /// <summary>
        /// Снимок принят (NetClient отдаёт только новее прежнего): копия в кольцо и образец для часов. Время — монотонные
        /// мкс клиента в момент приёма.
        /// </summary>
        public void OnSnapshot(Snapshot s, long localUs)
        {
            if (s == null)
            {
                throw new ArgumentNullException(nameof(s));
            }

            if (_count > 0 && (int)(s.Tick - Latest.Tick) <= 0)
            {
                return; // старый или дубль — порядок держит кольцо
            }

            s.CopyTo(_ring[_head]);
            _head = (_head + 1) % RingSize;
            if (_count < RingSize)
            {
                _count++;
            }

            _samples[_sampleHead] = TickTimeUs(s.Tick) - localUs;
            _sampleHead = (_sampleHead + 1) % ClockWindow;
            if (_sampleCount < ClockWindow)
            {
                _sampleCount++;
            }

            // Медиана окна: 30 чисел — сортировка вставками в готовом массиве, без выделений.
            int n = _sampleCount;
            for (int i = 0; i < n; i++)
            {
                long v = _samples[i];
                int j = i - 1;
                while (j >= 0 && _sorted[j] > v)
                {
                    _sorted[j + 1] = _sorted[j];
                    j--;
                }

                _sorted[j + 1] = v;
            }

            // Нижняя медиана (при чётном счёте — меньший из средних): не среднее двух, чтобы сдвиг задержки давал одну
            // перестановку, а не две по полпути; меньший образец — снимок пришёл позже — даёт запас, а не его нехватку.
            _offsetUs = _sorted[(n - 1) / 2];
        }

        /// <summary>
        /// Начало кадра рисования: время показа идёт к «сервер сейчас» − 100 мс со скоростью <see cref="PlaybackRate"/>,
        /// не дальше новейшего снимка и никогда назад; пара снимков вокруг него. Снимков нет — false (рисовать нечего).
        /// </summary>
        public bool BeginFrame(long localUs)
        {
            if (_sampleCount == 0 || _count == 0)
            {
                return false;
            }

            _frame++;
            _held = false;
            _resetFrame = false;
            long realDt = _hasRender ? Math.Max(0, localUs - _lastLocalUs) : 0; // местные часы назад — ноль
            _lastLocalUs = localUs;
            double target = ServerNowUs(localUs) - DelayUs;
            double t;
            if (!_hasRender)
            {
                t = target;
            }
            else
            {
                double err = target - (_render + realDt); // + — показ отстаёт от цели, − — впереди
                if (err > JumpUs)
                {
                    t = target; // фриз хоста — перестановка вперёд
                    ClockResets++;
                    _resetFrame = true;
                    PlaybackRate = 1.0;
                }
                else
                {
                    double rate = RateFor(err);
                    double extra = realDt * (rate - 1.0); // догон или замедление сверх хода часов
                    extra = err > 0 ? Math.Min(extra, err) : Math.Max(extra, err); // не перескакивать цель
                    t = _render + realDt + extra;
                    PlaybackRate = realDt > 0 ? (realDt + extra) / realDt : 1.0;
                }

                if (t < _render)
                {
                    t = _render; // время показа назад не идёт
                }
            }

            int newest = Mod(_head - 1);
            long newestUs = TickTimeUs(_ring[newest].Tick);
            if (t >= newestUs)
            {
                t = _hasRender ? Math.Max(newestUs, _render) : newestUs; // дальше новейшего данных нет — стоим на нём
                _held = true;
                HoldFrames++;
            }

            if (_hasRender && t <= _render)
            {
                SameTimeFrames++;
            }

            long tUs = (long)Math.Floor(t);
            _frameDtUs = _hasRender ? tUs - _renderUs : 0;
            _realDtUs = realDt;
            _render = t;
            _renderUs = tUs;
            _hasRender = true;
            Frames++;

            if (_held)
            {
                _ia = _ib = newest;
                _frac = 0;
                return true;
            }

            int next = newest;
            for (int steps = 1; steps < _count; steps++)
            {
                int i = Mod(_head - 1 - steps);
                long ti = TickTimeUs(_ring[i].Tick);
                if (ti <= tUs)
                {
                    long tn = TickTimeUs(_ring[next].Tick);
                    _ia = i;
                    _ib = next;
                    _frac = tn > ti ? (tUs - ti) / (double)(tn - ti) : 1.0;
                    return true;
                }

                next = i;
            }

            _ia = _ib = next; // время показа старше всей истории — старейший
            _frac = 0;
            _held = true;
            BeforeOldestFrames++;
            return true;
        }

        /// <summary>
        /// Скорость показа по ошибке «цель − показ»: в полосе ±<see cref="FastUs"/> — 1 ± ошибка/<see cref="SlewUs"/>
        /// (не больше 1 %), за ней — быстрее до <see cref="CatchUpRate"/> или медленнее до <see cref="MinRate"/>.
        /// </summary>
        public static double RateFor(double errUs)
        {
            if (errUs > FastUs)
            {
                return 1.0 + Math.Min(CatchUpRate - 1.0, MaxSkew + (errUs - FastUs) / FastTauUs);
            }

            if (errUs < -FastUs)
            {
                return 1.0 - Math.Min(1.0 - MinRate, MaxSkew + (-errUs - FastUs) / FastTauUs);
            }

            return 1.0 + Math.Max(-MaxSkew, Math.Min(MaxSkew, errUs / SlewUs));
        }

        /// <summary>
        /// Позиция сущности на время показа этого кадра в форме «точка + дельта» (unit, 1/256 тайла): точка — позиция в
        /// раннем снимке пары, дельта — доля пути до позднего по тору. Нет её в позднем снимке пары — false (ещё не
        /// появилась или ушла). Каждый вызов за кадр идёт в замер шага.
        /// </summary>
        public bool TryGet(int id, out WorldPos from, out float dx, out float dy)
        {
            from = default;
            dx = dy = 0f;
            if (!_hasRender || !_ring[_ib].TryFind(id, out SnapshotEntity eb))
            {
                return false;
            }

            if (_ia == _ib || !_ring[_ia].TryFind(id, out SnapshotEntity ea))
            {
                from = eb.Position;
            }
            else
            {
                Int2 d = Torus.Delta(_map, ea.Position, eb.Position);
                long nx = (long)ea.Position.X + d.X; // ход по тору выводит за край карты — пара через шов
                long ny = (long)ea.Position.Y + d.Y;
                if (nx < 0 || ny < 0 || nx >= (long)_map.Width * Torus.SubTilesPerTile || ny >= (long)_map.Height * Torus.SubTilesPerTile)
                {
                    SeamSamples++;
                }

                from = ea.Position;
                dx = (float)(d.X * _frac);
                dy = (float)(d.Y * _frac);
            }

            Measure(id, from, dx, dy);
            return true;
        }

        private void Measure(int id, WorldPos from, float dx, float dy)
        {
            if (!_resetFrame && _tracks.TryGetValue(id, out Track tr) && tr.Frame == _frame - 1)
            {
                Int2 d = Torus.Delta(_map, tr.From, from);
                double sx = Math.Abs(d.X + (double)dx - tr.Dx);
                double sy = Math.Abs(d.Y + (double)dy - tr.Dy);
                double step = Math.Max(sx, sy);
                double limit = _maxUnitsPerUs * _frameDtUs + StepSlackUnits;
                double realLimit = _maxUnitsPerUs * _realDtUs + StepSlackUnits;
                Steps++;
                MaxStepUnits = Math.Max(MaxStepUnits, step);
                MaxStepRatio = Math.Max(MaxStepRatio, step / limit);
                MaxCatchUpRatio = Math.Max(MaxCatchUpRatio, step / realLimit);
                if (step > realLimit)
                {
                    CatchUpSteps++;
                }

                if (step > limit || step > CatchUpRate * realLimit)
                {
                    StepViolations++;
                }
            }

            _tracks[id] = new Track { From = from, Dx = dx, Dy = dy, Frame = _frame };
        }

        private static int Mod(int i)
        {
            int r = i % RingSize;
            return r < 0 ? r + RingSize : r;
        }
    }
}
