using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Чужие сущности у клиента (docs/07, 2.9; карточка 0-017 по проверке 0-012): интерполяция между снимками 30 Гц на
    /// время показа «сервер сейчас − <see cref="DelayUs"/>». Чистая сборка ядра: без Unity и рефлексии — клиент Unity
    /// только рисует то, что дал этот класс, тесты гоняют его в CI.
    /// <list type="number">
    /// <item><b>Часы.</b> Оценка «сервер сейчас» в мкс шкалы сервера (такт × 33,3 мс) по местным монотонным часам: первый
    /// снимок ставит часы, каждый следующий — образец «такт снимка в момент прихода». Сглаживание (2.4): ошибка
    /// сглаживается (1/<see cref="ErrorSmoothing"/>), ход часов поправляется не больше чем на 1 %; ошибка больше
    /// 100 мс — часы переставляются сразу. «Сервер сейчас» — без owd: время такта новейшего снимка в момент прихода
    /// (иначе запас 100 мс съедает owd — backlog [док] 2.4/2.9).</item>
    /// <item><b>Кадр.</b> <see cref="BeginFrame"/> раз за кадр рисования: время показа берётся от текущих часов, а не от
    /// прихода снимка — растёт каждый кадр и никогда не идёт назад (часы переставлены назад — показ ждёт).</item>
    /// <item><b>Позиция.</b> Пара снимков вокруг времени показа, разность — <see cref="Torus.Delta(in MapShape, WorldPos, WorldPos)"/>
    /// (шов тора без скачка), доля — по времени тактов. Время показа новее новейшего снимка — стоит на нём
    /// (<see cref="HoldFrames"/>; экстраполяции 2.9 пока нет — backlog); старше старейшего — стоит на старейшем.</item>
    /// <item><b>Замер для net-smoke.</b> Шаг каждой сущности за кадр против предела скорости × приращение времени показа:
    /// без интерполяции (ступеньки 30 Гц) или с разностью без тора шаг выходит за предел (<see cref="StepViolations"/>).</item>
    /// </list>
    /// </summary>
    public sealed class RemoteInterpolation
    {
        /// <summary>Задержка показа: 100 мс (07, 2.4 и 2.9) — три снимка, переживает два потерянных подряд.</summary>
        public const long DelayUs = 100_000;

        /// <summary>Снимков в кольце: 8 × 33 мс ≈ 266 мс истории.</summary>
        public const int RingSize = 8;

        /// <summary>Ошибка часов, после которой они переставляются сразу (2.4): 100 мс.</summary>
        public const long ResetUs = 100_000;

        /// <summary>Наибольшая поправка хода часов (2.4): 1 %.</summary>
        public const double MaxSkew = 0.01;

        /// <summary>Сглаженная ошибка в 10 мс даёт поправку хода 1 %: ошибка уходит примерно за секунду.</summary>
        public const double SlewUs = 1_000_000;

        /// <summary>Сглаживание ошибки часов: новая ошибка входит с весом 1/8.</summary>
        public const int ErrorSmoothing = 8;

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

        // Часы: серверное время = _baseServer + (местное − _baseLocal) × (1 + _rate).
        private bool _synced;
        private long _baseLocal;
        private double _baseServer;
        private double _rate;
        private double _errEma;

        // Кадр.
        private bool _hasRender;
        private long _renderUs;
        private long _frameDtUs;
        private long _frame;
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

        /// <summary>Поправка хода часов сейчас (−0,01…0,01).</summary>
        public double ClockRate => _rate;

        /// <summary>Перестановок часов (ошибка &gt; 100 мс).</summary>
        public long ClockResets { get; private set; }

        /// <summary>Кадров рисования (с временем показа).</summary>
        public long Frames { get; private set; }

        /// <summary>Кадров, где время показа не выросло (стоит на месте или часы назад).</summary>
        public long SameTimeFrames { get; private set; }

        /// <summary>Кадров, где время показа новее новейшего снимка: чужие стоят на нём.</summary>
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

        /// <summary>Шагов больше предела.</summary>
        public long StepViolations { get; private set; }

        /// <summary>Оценка «сервер сейчас» в мкс шкалы сервера; до первого снимка — 0.</summary>
        public double ServerNowUs(long localUs) =>
            _synced ? _baseServer + (localUs - _baseLocal) * (1.0 + _rate) : 0.0;

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

            long server = TickTimeUs(s.Tick);
            if (!_synced)
            {
                _synced = true;
                _baseLocal = localUs;
                _baseServer = server;
                return;
            }

            double est = ServerNowUs(localUs);
            _baseServer = est;
            _baseLocal = localUs;
            double err = server - est;
            if (err > ResetUs || err < -ResetUs)
            {
                _baseServer = server;
                _errEma = 0;
                _rate = 0;
                ClockResets++;
                return;
            }

            _errEma += (err - _errEma) / ErrorSmoothing;
            _rate = Math.Max(-MaxSkew, Math.Min(MaxSkew, _errEma / SlewUs));
        }

        /// <summary>
        /// Начало кадра рисования: время показа = «сервер сейчас» − 100 мс по текущим часам, не меньше прошлого кадра;
        /// пара снимков вокруг него. Снимков нет — false (рисовать нечего).
        /// </summary>
        public bool BeginFrame(long localUs)
        {
            if (!_synced || _count == 0)
            {
                return false;
            }

            _frame++;
            long t = (long)Math.Floor(ServerNowUs(localUs)) - DelayUs;
            if (_hasRender && t <= _renderUs)
            {
                SameTimeFrames++;
                t = _renderUs;
            }

            _frameDtUs = _hasRender ? t - _renderUs : 0;
            _renderUs = t;
            _hasRender = true;
            Frames++;

            int newest = Mod(_head - 1);
            if (TickTimeUs(_ring[newest].Tick) <= t)
            {
                _ia = _ib = newest;
                _frac = 0;
                HoldFrames++;
                return true;
            }

            int next = newest;
            for (int steps = 1; steps < _count; steps++)
            {
                int i = Mod(_head - 1 - steps);
                long ti = TickTimeUs(_ring[i].Tick);
                if (ti <= t)
                {
                    long tn = TickTimeUs(_ring[next].Tick);
                    _ia = i;
                    _ib = next;
                    _frac = tn > ti ? (t - ti) / (double)(tn - ti) : 1.0;
                    return true;
                }

                next = i;
            }

            _ia = _ib = next; // время показа старше всей истории — старейший
            _frac = 0;
            BeforeOldestFrames++;
            return true;
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
            if (_tracks.TryGetValue(id, out Track tr) && tr.Frame == _frame - 1)
            {
                Int2 d = Torus.Delta(_map, tr.From, from);
                double sx = Math.Abs(d.X + (double)dx - tr.Dx);
                double sy = Math.Abs(d.Y + (double)dy - tr.Dy);
                double step = Math.Max(sx, sy);
                double limit = _maxUnitsPerUs * _frameDtUs + StepSlackUnits;
                Steps++;
                MaxStepUnits = Math.Max(MaxStepUnits, step);
                MaxStepRatio = Math.Max(MaxStepRatio, step / limit);
                if (step > limit)
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
