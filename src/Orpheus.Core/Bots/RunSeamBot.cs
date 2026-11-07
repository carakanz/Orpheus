using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;

namespace Orpheus.Core.Bots
{
    /// <summary>Итог одного круга <see cref="RunSeamBot"/>.</summary>
    public readonly struct RunSeamLap
    {
        public RunSeamLap(int index, Input8 direction, long steps, Int2 residual, int approaches, int maxCrossUnits)
        {
            Index = index;
            Direction = direction;
            Steps = steps;
            Residual = residual;
            Approaches = approaches;
            MaxCrossUnits = maxCrossUnits;
        }

        /// <summary>Номер круга, 0…3.</summary>
        public int Index { get; }

        /// <summary>Направление круга.</summary>
        public Input8 Direction { get; }

        /// <summary>Решений бота за круг (такты в режиме симуляции, кадры ввода по сети).</summary>
        public long Steps { get; }

        /// <summary>Позиция после круга минус позиция в начале круга (по тору), unit (1/256 тайла). Приёмка 0.2 — (0, 0).</summary>
        public Int2 Residual { get; }

        /// <summary>Подходов пачкой с ожиданием подтверждения (1 — с одного раза).</summary>
        public int Approaches { get; }

        /// <summary>Наибольший уход поперёк оси бега за круг, unit по модулю.</summary>
        public int MaxCrossUnits { get; }

        /// <summary>Имя направления: east, west, north, south.</summary>
        public string DirectionName => RunSeamBot.NameOf(Direction);

        public override string ToString() => $"круг {Index} {DirectionName}: невязка ({Residual.X}, {Residual.Y}) unit, решений {Steps}, подходов {Approaches}";
    }

    /// <summary>
    /// Сценарий <c>run_seam</c> (docs/07, 11.3; docs/08, 3.0 приёмка 0.2; карточка 0-027): четыре круга по тору — по
    /// экватору на восток и на запад, по меридиану на север и на юг (docs/01, 1: круг — |U_x| ≥ W или |U_y| ≥ H); круг —
    /// W (H) тайлов от точки появления до неё же. После круга позиция сверяется со стартом круга точно, в unit (1/256 тайла).
    /// <para>
    /// Бег — по своей подтверждённой позиции (снимок сервера, <c>Self</c>, без кванта; в режиме симуляции — сама сущность),
    /// а не по числу тактов: по сети сервер может перескочить кадр ввода или шагнуть сам (07, 2.7), и круг по счёту кадров
    /// не замкнётся. Пройдено — сумма разностей по тору между соседними позициями, поэтому шов не прибавляет и не теряет ни
    /// unit. За <see cref="ApproachUnits"/> до конца круга — подход: ждать, пока все отправленные шаги подтверждены, затем
    /// пачка ровно из round(осталось / шаг) шагов (или назад, если перебежал), и снова ждать. Шаг кадра — 25,6 unit
    /// (остаток копится в <c>Carry</c> в десятых unit, сумма «10 × позиция + Carry» растёт ровно на 256 за кадр), поэтому
    /// круг W × 256 unit = W × 10 кадров ровно, и с нулевого остатка позиция после круга совпадает со стартом точно.
    /// Невязка круга ≠ 0 значит, что шов (заворот, сервер, клиент) потерял или добавил unit.
    /// </para>
    /// <para>
    /// Сверка кадров (по сети, проверка 0-027 п. 2): невязка круга не видит рывок, кратный шагу кадра (0,5 т = 5 кадров),
    /// поэтому на каждом снимке сдвиг своей подтверждённой позиции (<c>Self</c> с остатком) сверяется с отправленными
    /// кадрами, которые сервер шагнул за это время (номера в (AckFrame прошлого снимка, AckFrame]): «10 × позиция + Carry»
    /// растёт ровно на 256 за кадр по оси, поэтому сдвиг — целое число кадров m, поперёк — 0, и вперёд m не больше, чем
    /// отправлено кадров вперёд (назад — назад). Меньше — можно: сервер выбрасывает лишнее из очереди и перескакивает дыры
    /// (07 2.7). Иначе — <see cref="FrameMismatches"/>, сценарий не выполнен.
    /// </para>
    /// </summary>
    public sealed class RunSeamBot : SimBot
    {
        public const string Name = "run_seam";

        /// <summary>Кругов по умолчанию (<see cref="LapsAll"/>): восток, запад, север, юг.</summary>
        public const int LapCount = 4;

        /// <summary>Круги (<c>--laps</c>): все четыре — восток, запад, север, юг (приёмка 0.2, ≈ 11,5 мин по сети).</summary>
        public const string LapsAll = "all";

        /// <summary>Круги (<c>--laps</c>): по одному на ось — восток и север (оба шва, ≈ 5,7 мин по сети; `-All`).</summary>
        public const string LapsAxes = "axes";

        /// <summary>Отправленных кадров в памяти сверки.</summary>
        public const int FrameRing = 256;

        /// <summary>
        /// Подход начинается за 3 тайла до конца круга: в полёте (отправлено, ещё не в снимке) по сети при RTT ≤ 300 мс —
        /// ≤ 0,25 с бега = 1,5 тайла, бег с запасом не перебегает.
        /// </summary>
        public const int ApproachUnits = 3 * Torus.SubTilesPerTile;

        /// <summary>Шаг решения в режиме симуляции: такт базового бега, десятые unit.</summary>
        public const int TickStepX10 = MovementTick.BaseSpeedX10;

        /// <summary>Шаг решения по сети: кадр ввода (полшага такта), десятые unit.</summary>
        public const int FrameStepX10 = MovementTick.BaseSpeedX10 / MovementTick.FramesPerTick;

        private static readonly Input8[] AllDirections = { Input8.East, Input8.West, Input8.North, Input8.South };
        private static readonly Input8[] AxisDirections = { Input8.East, Input8.North };

        private readonly MapShape _map;
        private readonly Input8[] _dirs;
        private readonly List<RunSeamLap> _laps = new List<RunSeamLap>(LapCount);
        private readonly uint[] _sentFrame = new uint[FrameRing];
        private readonly Input8[] _sentDir = new Input8[FrameRing];
        private readonly bool[] _sentValid = new bool[FrameRing];
        private bool _checkBase;
        private uint _checkAck;
        private WorldPos _checkPos;
        private Int2 _checkCarry;
        private bool _started;
        private WorldPos _origin;
        private WorldPos _lapStart;
        private WorldPos _last;
        private long _travelled;
        private int _maxCross;
        private int _cross;
        private long _steps;
        private int _approaches;
        private bool _approach;
        private int _burstLeft;
        private Input8 _burstDir;

        /// <param name="laps"><see cref="LapsAll"/> — четыре круга, <see cref="LapsAxes"/> — восток и север.</param>
        public RunSeamBot(int entityId, in MapShape map, string laps = LapsAll)
            : base(entityId)
        {
            _map = map;
            _dirs = laps == LapsAll ? AllDirections
                : laps == LapsAxes ? AxisDirections
                : throw new ArgumentException($"Круги run_seam: «{laps}» — нет; есть {LapsAll}, {LapsAxes}.", nameof(laps));
        }

        public override string Script => Name;

        /// <summary>Пройденные круги по порядку.</summary>
        public IReadOnlyList<RunSeamLap> Laps => _laps;

        /// <summary>Кругов задано (4 или 2).</summary>
        public int PlannedLaps => _dirs.Length;

        /// <summary>Все заданные круги пройдены.</summary>
        public bool Done => _laps.Count == _dirs.Length;

        /// <summary>Сверок кадров по снимкам (по сети).</summary>
        public long FrameChecks { get; private set; }

        /// <summary>Сверок, где сдвиг своей позиции не сошёлся с шагнутыми кадрами (рывок, не кратный кадру, или лишние кадры).</summary>
        public long FrameMismatches { get; private set; }

        /// <summary>Кадров, отправленных и подтверждённых, но не шагнутых (сервер выбросил из очереди или перескочил) — для справки.</summary>
        public long FramesShort { get; private set; }

        /// <summary>Первое несовпадение сверки кадров — для отчёта; нет — null.</summary>
        public string? FirstMismatch { get; private set; }

        /// <summary>Сценарий выполнен: круги без невязки и сверка кадров без несовпадений.</summary>
        public bool Passed => AllExact && FrameMismatches == 0;

        /// <summary>Точка появления (старт первого круга); до первого решения — default.</summary>
        public WorldPos Origin => _origin;

        /// <summary>Есть ли старт.</summary>
        public bool Started => _started;

        /// <summary>Номер текущего круга (= <see cref="LapCount"/>, когда всё пройдено).</summary>
        public int Lap => _laps.Count;

        /// <summary>Пройдено в текущем круге вдоль оси, unit.</summary>
        public long Travelled => _travelled;

        /// <summary>Все круги без невязки (и пройдены все).</summary>
        public bool AllExact
        {
            get
            {
                if (!Done)
                {
                    return false;
                }

                foreach (RunSeamLap l in _laps)
                {
                    if (l.Residual.X != 0 || l.Residual.Y != 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>Наибольшая невязка круга по модулю (по осям), unit.</summary>
        public int MaxResidualUnits
        {
            get
            {
                int m = 0;
                foreach (RunSeamLap l in _laps)
                {
                    m = Math.Max(m, Math.Max(Math.Abs(l.Residual.X), Math.Abs(l.Residual.Y)));
                }

                return m;
            }
        }

        /// <summary>Режим симуляции: решение на такт; позиция сущности — уже после прошлого такта (всё подтверждено).</summary>
        public override InputFrame Think(long tick, in SimEntity self) =>
            new InputFrame(Decide(self.Mover.Position, settled: true, TickStepX10));

        /// <summary>
        /// По сети (NetBot): решение на кадр ввода. <paramref name="confirmed"/> — позиция последнего снимка (<c>Self</c>, без
        /// кванта); <paramref name="settled"/> — все кадры с движением, отправленные раньше, сервер уже шагнул (AckFrame
        /// снимка ≥ номера последнего такого кадра), т. е. позиция их учитывает.
        /// </summary>
        public InputFrame ThinkFrame(WorldPos confirmed, bool settled) => new InputFrame(Decide(confirmed, settled, FrameStepX10));

        /// <summary>По сети: кадр ввода <paramref name="frame"/> ушёл с направлением <paramref name="direction"/> (для сверки кадров).</summary>
        public void SentFrame(uint frame, Input8 direction)
        {
            int i = (int)(frame % FrameRing);
            _sentFrame[i] = frame;
            _sentDir[i] = direction;
            _sentValid[i] = true;
        }

        /// <summary>Номера кадров начались заново (перенумерация часов): сверка — с следующего снимка.</summary>
        public void ResetFrameCheck()
        {
            _checkBase = false;
            Array.Clear(_sentValid, 0, FrameRing);
        }

        /// <summary>
        /// По сети: новый снимок — <paramref name="ackFrame"/> (последний шагнутый кадр) и своё подтверждённое состояние после
        /// него. Сдвиг от прошлого снимка сверяется с отправленными кадрами из (прошлый AckFrame, <paramref name="ackFrame"/>].
        /// Кадра из этого промежутка нет в памяти (до первого кадра, перенумерация) — сверки нет, отсчёт отсюда.
        /// </summary>
        public void CheckConfirmed(uint ackFrame, WorldPos position, Int2 carry)
        {
            if (!_checkBase || (int)(ackFrame - _checkAck) < 0 || (int)(ackFrame - _checkAck) > FrameRing)
            {
                SetCheckBase(ackFrame, position, carry);
                return;
            }

            int fwdX = 0, backX = 0, fwdY = 0, backY = 0;
            for (uint f = _checkAck + 1; (int)(ackFrame - f) >= 0; f++)
            {
                int i = (int)(f % FrameRing);
                if (!_sentValid[i] || _sentFrame[i] != f)
                {
                    SetCheckBase(ackFrame, position, carry);
                    return;
                }

                Input8 d = _sentDir[i];
                fwdX += d.DX > 0 ? 1 : 0;
                backX += d.DX < 0 ? 1 : 0;
                fwdY += d.DY > 0 ? 1 : 0;
                backY += d.DY < 0 ? 1 : 0;
            }

            Int2 delta = Torus.Delta(_map, _checkPos, position);
            long ex = (long)delta.X * MovementTick.SpeedScale + carry.X - _checkCarry.X;
            long ey = (long)delta.Y * MovementTick.SpeedScale + carry.Y - _checkCarry.Y;
            FrameChecks++;
            bool okX = Frames(ex, fwdX, backX, out int shortX);
            bool okY = Frames(ey, fwdY, backY, out int shortY);
            if (okX && okY)
            {
                FramesShort += shortX + shortY;
            }
            else
            {
                FrameMismatches++;
                FirstMismatch ??= $"кадры {_checkAck + 1}…{ackFrame}: x +{fwdX}/−{backX}, y +{fwdY}/−{backY} кадров, сдвиг ({ex / 10.0:0.0}, {ey / 10.0:0.0}) unit";
            }

            SetCheckBase(ackFrame, position, carry);
        }

        // Сдвиг по оси в десятых unit — целое число кадров m (256 десятых), −назад ≤ m ≤ вперёд; не шагнуто — разница.
        private static bool Frames(long shiftX10, int forward, int back, out int notStepped)
        {
            notStepped = 0;
            if (shiftX10 % FrameStepX10 != 0)
            {
                return false;
            }

            long m = shiftX10 / FrameStepX10;
            if (m > forward || m < -back)
            {
                return false;
            }

            notStepped = (int)Math.Abs(forward - back - m);
            return true;
        }

        private void SetCheckBase(uint ackFrame, WorldPos position, Int2 carry)
        {
            _checkBase = true;
            _checkAck = ackFrame;
            _checkPos = position;
            _checkCarry = carry;
        }

        /// <summary>Имя направления для отчёта.</summary>
        public static string NameOf(Input8 d) =>
            d == Input8.East ? "east" : d == Input8.West ? "west" : d == Input8.North ? "north" : d == Input8.South ? "south" : d.ToString();

        private Input8 Decide(WorldPos pos, bool settled, int stepX10)
        {
            if (!_started)
            {
                _started = true;
                _origin = pos;
                BeginLap(pos);
            }

            Track(pos);
            if (Done)
            {
                return Input8.None;
            }

            _steps++;
            if (_burstLeft > 0)
            {
                _burstLeft--;
                return _burstDir;
            }

            Input8 dir = _dirs[_laps.Count];
            long remaining = LapUnits() - _travelled;
            if (!_approach)
            {
                if (remaining > ApproachUnits)
                {
                    return dir;
                }

                _approach = true;
            }

            if (!settled)
            {
                return Input8.None; // шаги в полёте — позиция их ещё не учла
            }

            // Число шагов до конца круга: шаг — stepX10 / 10 unit; остаток Carry (|c| < 10 десятых) округление не сдвигает.
            long k = (remaining * 10 + (remaining >= 0 ? stepX10 / 2 : -stepX10 / 2)) / stepX10;
            if (k != 0)
            {
                _approaches++;
                _burstDir = k > 0 ? dir : new Input8(-dir.DX, -dir.DY);
                _burstLeft = (int)Math.Min(Math.Abs(k), int.MaxValue) - 1;
                return _burstDir;
            }

            Int2 residual = Torus.Delta(_map, _lapStart, pos);
            _laps.Add(new RunSeamLap(_laps.Count, dir, _steps, residual, _approaches, _maxCross));
            BeginLap(pos);
            return Input8.None;
        }

        private void BeginLap(WorldPos pos)
        {
            _lapStart = pos;
            _last = pos;
            _travelled = 0;
            _cross = 0;
            _maxCross = 0;
            _steps = 0;
            _approaches = 0;
            _approach = false;
            _burstLeft = 0;
        }

        private void Track(WorldPos pos)
        {
            Int2 d = Torus.Delta(_map, _last, pos);
            _last = pos;
            if (Done)
            {
                return;
            }

            Input8 dir = _dirs[_laps.Count];
            if (dir.DX != 0)
            {
                _travelled += d.X * dir.DX;
                _cross += d.Y;
            }
            else
            {
                _travelled += d.Y * dir.DY;
                _cross += d.X;
            }

            _maxCross = Math.Max(_maxCross, Math.Abs(_cross));
        }

        private long LapUnits() => _dirs[_laps.Count].DX != 0 ? _map.WidthUnits : _map.HeightUnits;
    }

    /// <summary>
    /// Сценарий <c>seam_watch</c> (карточка 0-027): наблюдатель — стоит (ввод пустой); показ чужих и замер скачка у шва —
    /// на стороне сети (<c>Orpheus.Net.Session.SeamWatch</c>), как у клиента Unity.
    /// </summary>
    public sealed class SeamWatchBot : SimBot
    {
        public const string Name = "seam_watch";

        public SeamWatchBot(int entityId)
            : base(entityId)
        {
        }

        public override string Script => Name;

        public override InputFrame Think(long tick, in SimEntity self) => InputFrame.None;
    }

    /// <summary>
    /// Полосы бега для <c>run_seam</c> в тестовом мире (ключ сервера <c>--debug-run-lanes</c>, карточка 0-027, «Решения»):
    /// на мире заглушки ни одна строка и ни один столбец 1024² не свободны целиком (деревья и скалы; проверено на 5 зёрнах),
    /// поэтому полосы расчищаются на тайлах генерации: строка y = 0 (экватор у шва y), столбцы x = 0, 2, …, 14 (меридианы
    /// под 8 мест появления) и квадрат 9 × 9 у угла (0, 0), где сходятся оба шва. Появление — <see cref="SpawnCenter"/>:
    /// игрок № i (i &lt; 8) — в тайле (2i, 0). Круг персонажа (радиус 0,4 тайла) по центру строки задевает только её.
    /// </summary>
    public static class RunSeamLanes
    {
        /// <summary>Центр появления: места (cx − 7 + 2i, cy − 1) = (2i, 0), i = 0…7.</summary>
        public static readonly TileCoord SpawnCenter = new TileCoord(7, 1);

        /// <summary>Мест появления на полосах.</summary>
        public const int Places = 8;

        /// <summary>Полуширина квадрата у угла, тайлов.</summary>
        public const int CornerRadius = 4;

        /// <summary>Тайл на полосе: строка 0, столбцы 0, 2, …, 14 или квадрат у угла.</summary>
        public static bool IsLane(in MapShape map, TileCoord tile)
        {
            TileCoord t = Torus.Wrap(map, tile);
            if (t.Y == 0 || (t.X < 2 * Places && (t.X & 1) == 0))
            {
                return true;
            }

            Int2 d = Torus.Delta(map, new TileCoord(0, 0), t);
            return Math.Abs(d.X) <= CornerRadius && Math.Abs(d.Y) <= CornerRadius;
        }

        /// <summary>
        /// Расчистить полосы на тайлах генерации: непроходимый пол — трава, объект — убрать. Возвращает число изменённых тайлов.
        /// Тот же вызов на мире того же зерна даёт тот же мир (бот сверяет хэш генерации).
        /// </summary>
        public static int Clear(WorldModel world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            MapShape map = world.Shape;
            int changed = 0;
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    var t = new TileCoord(x, y);
                    if (!IsLane(map, t))
                    {
                        continue;
                    }

                    byte floor = world.GetFloor(t);
                    ushort obj = world.GetObject(t);
                    bool badFloor = !FloorId.IsPassable(floor);
                    if (badFloor || obj != ObjectId.Empty)
                    {
                        world.SetBaseTile(t, badFloor ? FloorId.Grass : floor, ObjectId.Empty);
                        changed++;
                    }
                }
            }

            return changed;
        }
    }
}
