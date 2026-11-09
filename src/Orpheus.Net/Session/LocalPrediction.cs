using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Предсказание и сверка своего персонажа (docs/07, 2.6; карточки 0-013, 0-019). Чистая сборка ядра: без Unity и
    /// рефлексии — клиент Unity берёт её DLL, тесты гоняют её в CI.
    /// <list type="number">
    /// <item>На каждый отправленный кадр ввода 60 Гц персонаж шагает <see cref="Mover.StepFrame"/> ядра — тем же шагом,
    /// каким сервер шагает пришедший кадр (<c>NetServer</c>, 2.7 «Применение ввода»); (кадр, ввод, состояние после
    /// кадра) — в кольцо на <see cref="BufferSize"/> кадров.</item>
    /// <item>Снимок несёт <c>AckFrame</c> — последний шагнутый сервером кадр — и своё состояние после него без кванта
    /// (<see cref="SelfState"/>: позиция 1/256 и остаток скорости). Клиент сравнивает с тем, что предсказал для этого
    /// кадра: ближе 0,02 тайла (по расстоянию, не по оси) и остаток тот же — ничего.</item>
    /// <item>Не совпало — принимает серверное состояние и заново шагает свои кадры новее ack.</item>
    /// <item>Картинка не прыгает: смещение «картинка → новое предсказание» гасится за 0,1 с (до 0,5 тайла),
    /// 0,3 с (до 2 тайлов), больше — сразу.</item>
    /// </list>
    /// Первая сверка — установка (клиент не знает точки появления): серверное состояние принимается без счёта правки и
    /// без гашения. Кольцо переполнено (ack старше кольца — RTT больше <see cref="BufferSize"/> кадров): правка без
    /// сравнения, кадры между ack и кольцом потеряны — счётчик <see cref="Overflows"/> (backlog [док] 2.6/2.7).
    /// </summary>
    public sealed class LocalPrediction
    {
        /// <summary>
        /// Кадров в кольце: 256 (≈ 4,3 с при 60 Гц). В 2.6 — 64 кадра (1 с), но тогда при RTT больше ~1 с каждый снимок —
        /// правка, а 2.7 обещает «персонаж слушается» и при RTT &gt; 1 с; решение 0-019, вопрос — backlog [док].
        /// </summary>
        public const int BufferSize = 256;

        /// <summary>Порог совпадения 0,02 тайла = 5,12 unit (2.6 п. 3): квадрат расстояния не больше 26.</summary>
        public const long MatchDist2 = 26;

        /// <summary>Границы гашения: 0,5 и 2 тайла (2.6 п. 4), квадраты расстояния в unit.</summary>
        public const long NearDist2 = 128L * 128;

        /// <inheritdoc cref="NearDist2"/>
        public const long FarDist2 = 512L * 512;

        /// <summary>Длительность гашения: 0,1 с (ближе 0,5 тайла) и 0,3 с (до 2 тайлов), мкс.</summary>
        public const long NearUs = 100_000;

        /// <inheritdoc cref="NearUs"/>
        public const long FarUs = 300_000;

        private struct Slot
        {
            public uint Frame;
            public InputFrame Input;
            public Mover After;
        }

        private readonly MapShape _map;
        private readonly TileSolid _solid;
        private readonly Slot[] _slots = new Slot[BufferSize];
        private readonly Slot[] _replay = new Slot[BufferSize];
        private Mover _mover;
        private Mover _before;     // состояние перед старейшим кадром кольца
        private uint _beforeFrame; // после какого кадра это состояние (если не _beforePre)
        private bool _beforePre = true; // _before — до всей истории: годится для любого ack раньше первого кадра
        private int _head;
        private int _count;
        private uint _firstFrame;
        private bool _hasHistory;
        private bool _synced;
        private bool _await;     // после перенумерации: ack прежних номеров не сравнивать (0-025)
        private uint _awaitFrom; // первый кадр новых номеров
        private byte _health = 100; // 1-001: здоровье последнего снимка (0 — труп)
        private byte _mass = 1;     // 1-001: масса снимка — по ней скорость отбрасывания
        private bool _dead;         // 1-001: труп — сервер не шагнёт ввод, предсказание стоит
        private byte _slot;         // 1-002: ячейка последнего отправленного кадра — предмет в руках у клиента

        /// <summary>Порог приёмки 2.13 п. 1: 0,5 тайла = 128 unit.</summary>
        public const int HalfTileUnits = 128;

        // Гашение: смещение картинки от предсказанной позиции, затухает к нулю за _errDurUs.
        private Int2 _err;
        private long _errStartUs;
        private long _errDurUs;

        /// <param name="map">Форма карты (тор — шаг и сравнение через <see cref="Torus"/>).</param>
        /// <param name="solid">Проходимость — та же, что в такте сервера.</param>
        /// <param name="start">Где стоять до первой сверки (точка появления придёт с сервером).</param>
        /// <param name="unitsPerTickX10">Скорость персонажа за такт (как у сущности на сервере).</param>
        public LocalPrediction(MapShape map, TileSolid solid, WorldPos start, int unitsPerTickX10 = MovementTick.BaseSpeedX10)
        {
            _map = map;
            _solid = solid ?? throw new ArgumentNullException(nameof(solid));
            _mover = Mover.At(map, start, unitsPerTickX10);
            _before = _mover;
        }

        /// <summary>Сверок выполнено (каждый снимок со своим состоянием).</summary>
        public long Reconciled { get; private set; }

        /// <summary>Сверок, закончившихся правкой (приём серверного состояния и переигровка); установка не считается.</summary>
        public long Resynced { get; private set; }

        /// <summary>Правок из-за переполнения кольца (ack старше кольца): сравнить не с чем, кадры между ними потеряны.</summary>
        public long Overflows { get; private set; }

        /// <summary>Установок по первой сверке (и после <see cref="Teleport"/>).</summary>
        public long Initial { get; private set; }

        /// <summary>Наибольшее расхождение предсказания с сервером, unit (1/256 тайла), по всем сверкам после установки.</summary>
        public int MaxDevUnits { get; private set; }

        /// <summary>
        /// Сверок со сравнением (11.3, 0-025): предсказанная позиция в кадре ack против серверной в тот же кадр — каждый
        /// снимок после установки, кроме переполнения и ack прежних номеров после перенумерации.
        /// </summary>
        public long Compared { get; private set; }

        /// <summary>Из них расхождение больше 0,5 тайла (<see cref="HalfTileUnits"/>; 2.13 п. 1).</summary>
        public long ComparedOverHalfTile { get; private set; }

        /// <summary>Перенумераций (номер кадра не подряд: часы клиента переставлены, 0-025).</summary>
        public long Renumbers { get; private set; }

        /// <summary>Снимков, пропущенных после перенумерации: ack ещё в прежних номерах.</summary>
        public long IgnoredAcks { get; private set; }

        /// <summary>Расхождение на последней сверке, unit.</summary>
        public int LastDevUnits { get; private set; }

        /// <summary>Была сверка с сервером (позиция — не начальная заглушка).</summary>
        public bool Synced => _synced;

        /// <summary>Предсказанная позиция после последнего кадра.</summary>
        public WorldPos Position => _mover.Position;

        /// <summary>Предсказанный уворот после последнего кадра (0-029).</summary>
        public Dodge Dodge => _mover.Dodge;

        /// <summary>Здоровье последнего снимка (1-001): для полоски; 0 — труп.</summary>
        public int Health => _health;

        /// <summary>Масса из снимка (1-001): по ней скорость отбрасывания.</summary>
        public int Mass => _mass;

        /// <summary>
        /// Ячейка быстрой панели в последнем отправленном кадре (1-002) — «предмет в руках» для картинки и HUD:
        /// сервер применяет её в том же кадре, что и предсказание, поэтому клиент не ждёт снимка.
        /// </summary>
        public byte SelectedSlot => _slot;

        /// <summary>Труп (1-001): здоровье 0 — предсказание стоит, сервер не шагнёт ввод.</summary>
        public bool Dead => _dead;

        /// <summary>Кадров отбрасывания осталось (1-001, снимок + переигровка).</summary>
        public int KnockLeft => _mover.KnockLeft;

        /// <summary>Направление отбрасывания 1…8, 0 — нет (1-001).</summary>
        public int KnockDir => _mover.KnockDir;

        /// <summary>Отбрасываний принята из снимка (1-001): сервер задал нокбэк, предсказать его клиент не мог — это не правка предсказания.</summary>
        public long KnockAdopted { get; private set; }

        /// <summary>Предсказанный остаток скорости после последнего кадра.</summary>
        public Int2 Carry => _mover.Carry;

        /// <summary>Кадров в кольце (отправлено и ещё хранится).</summary>
        public int Count => _count;

        /// <summary>
        /// Кадр ввода отправлен на сервер под номером <paramref name="frame"/> (номера идут подряд) — тем же шагом персонаж
        /// идёт вперёд, кадр — в кольцо.
        /// </summary>
        public void Sent(uint frame, InputFrame input)
        {
            _slot = input.Slot; // 1-002: ячейка едет кадром — клиент видит её в том же кадре, что и сервер
            if (_hasHistory && _count > 0 && frame != NewestFrame + 1)
            {
                // Перенумерация (NetClient.SendFrame, 0-025): часы переставлены, номер — по оценке. Клиент шлёт три кадра
                // перед новым копиями нового — шагаем так же; прежние номера в кольце несравнимы с новыми — кольцо заново,
                // ack прежних номеров до первого нового пропускаются.
                Renumbers++;
                _before = _mover;
                _beforePre = true;
                _head = _count = 0;
                _firstFrame = frame - 3;
                _await = true;
                _awaitFrom = frame - 3;
                for (uint k = 3; k >= 1; k--)
                {
                    if (!_dead)
                    {
                        _mover.StepFrame(_map, input.Direction, input.DodgePressed, _solid); // копии — с кнопками, как шлёт клиент (0-029)
                    }

                    Push(frame - k, input);
                }
            }

            if (!_hasHistory)
            {
                _hasHistory = true;
                _firstFrame = frame;
                _before = _mover;
            }
            else if (_count == BufferSize)
            {
                _before = _slots[_head].After; // старейший уходит из кольца
                _beforeFrame = _slots[_head].Frame;
                _beforePre = false;
            }

            if (!_dead)
            {
                _mover.StepFrame(_map, input.Direction, input.DodgePressed, _solid); // 1-001: труп стоит — сервер не шагнёт ввод
            }

            Push(frame, input);
        }

        /// <summary>Поставить персонажа в точку без пути (телепорт): кольцо и гашение — чисто, следующая сверка — установка.</summary>
        public void Teleport(WorldPos p)
        {
            _mover = Mover.At(_map, p, _mover.UnitsPerTickX10);
            _before = _mover;
            _beforePre = true;
            _head = _count = 0;
            _hasHistory = false;
            _synced = false;
            _await = false;
            _err = default;
            _errDurUs = 0;
        }

        /// <summary>
        /// Сверка со снимком: <paramref name="ackFrame"/> — последний шагнутый сервером кадр (0 — ещё ни одного),
        /// <paramref name="server"/> — своё состояние после него (без кванта), время — монотонные мкс.
        /// </summary>
        public void Reconcile(uint ackFrame, in SelfState server, long nowUs)
        {
            if (!server.Present)
            {
                return;
            }

            if (_await)
            {
                if (_count > 0 && (int)(ackFrame - _awaitFrom) >= 0 && (int)(NewestFrame - ackFrame) >= 0 || _count >= BufferSize)
                {
                    _await = false;
                }
                else
                {
                    IgnoredAcks++;
                    return;
                }
            }

            Reconciled++;
            bool initial = !_synced;
            _synced = true;

            // 1-001: здоровье и масса — авторитетно с сервера; труп — предсказание стоит (сервер не шагнёт ввод).
            _health = server.Health;
            _mass = (byte)Math.Max(1, Math.Min(4, (int)server.Mass));
            _dead = server.Health == 0;

            // Что предсказано для ack: слот кольца; ack раньше истории — состояние до неё; старше кольца — нечего сравнить.
            bool overflow = false;
            Mover predicted = default;
            bool known = true;
            if (_count > 0 && (int)(ackFrame - OldestFrame) >= 0)
            {
                int back = (int)(NewestFrame - ackFrame);
                if (back < 0)
                {
                    known = false; // сервер «впереди» отправленного — так не бывает; правка без переигровки
                }
                else
                {
                    predicted = _slots[Mod(_head - 1 - back)].After;
                }
            }
            else if (_beforePre ? !_hasHistory || (int)(ackFrame - _firstFrame) < 0 : ackFrame == _beforeFrame)
            {
                predicted = _before; // до первого своего кадра или ровно перед старейшим в кольце
            }
            else
            {
                overflow = true;
                known = false;
            }

            if (known && !initial)
            {
                long d2 = Torus.Dist2(_map, predicted.Position, server.Position);
                LastDevUnits = Units(d2);
                MaxDevUnits = Math.Max(MaxDevUnits, LastDevUnits);
                Compared++;
                if (LastDevUnits > HalfTileUnits)
                {
                    ComparedOverHalfTile++;
                }

                if (d2 <= MatchDist2 && predicted.Carry == server.Carry && SelfState.SameDodge(predicted.Dodge, server.Dodge) &&
                    predicted.KnockLeft == server.KnockLeft && predicted.KnockDir == server.KnockDir)
                {
                    return; // совпало (2.6 п. 3); нокбэк — тоже (1-001): шаги совпали с серверными
                }
            }

            // Отбрасывание задал сервер (предсказание его не имело — удар решает сервер): расхождение позиции
            // «от нокбэка» — не ошибка предсказания, приём серверного состояния считаем отдельно (1-001).
            bool knockDriven = !_dead && server.KnockLeft > 0 && (known ? predicted.KnockLeft == 0 : true);

            if (initial)
            {
                Initial++;
            }
            else if (knockDriven)
            {
                KnockAdopted++;
            }
            else
            {
                Resynced++;
                if (overflow)
                {
                    Overflows++;
                }
            }

            // Точка, где картинка сейчас (предсказание + остаток гашения), — от неё гасим новый скачок.
            RenderDelta(nowUs, out WorldPos renderFrom, out float rdx, out float rdy);
            WorldPos render = Torus.Offset(_map, renderFrom, new Int2((int)rdx, (int)rdy));

            // Кадры новее ack сохранить, остальные — с ними сервер разобрался.
            int n = 0;
            for (int steps = 0; steps < _count; steps++)
            {
                Slot s = _slots[Mod(_head - 1 - steps)];
                if ((int)(s.Frame - ackFrame) <= 0)
                {
                    break;
                }

                _replay[n++] = s;
            }

            // Серверное состояние — уже после ack: переигровка только новее. Уворот — тоже серверный (0-029, протокол 6):
            // сервер мог шагнуть кадры сам (без нажатий) или перескочить их — тогда предсказанный рывок не тот.
            // Отбрасывание — с сервера целиком (1-001): скорость по массе снимка, предсказание шагает его до «новее ack».
            _mover.Position = server.Position;
            _mover.Carry = server.Carry;
            _mover.Dodge = server.Dodge;
            _mover.KnockLeft = server.KnockLeft;
            _mover.KnockSpeedX10 = server.KnockLeft == 0 ? 0 : Mover.KnockSpeedFor(_mass);
            _mover.KnockDir = server.KnockLeft == 0 ? (byte)0 : server.KnockDir;
            _before = _mover;
            _beforeFrame = ackFrame;
            _beforePre = !_hasHistory || (int)(ackFrame - _firstFrame) < 0;
            _head = _count = 0;
            for (int k = n - 1; k >= 0; k--)
            {
                if (!_dead)
                {
                    _mover.StepFrame(_map, _replay[k].Input.Direction, _replay[k].Input.DodgePressed, _solid);
                }

                Push(_replay[k].Frame, _replay[k].Input);
            }

            if (initial)
            {
                _err = default; // установка — без гашения: прежней картинки «своего» ещё не было
                _errDurUs = 0;
                return;
            }

            Int2 e = Torus.Delta(_map, _mover.Position, render);
            long ed2 = e.LengthSquared;
            _errDurUs = ed2 == 0 ? 0 : ed2 <= NearDist2 ? NearUs : ed2 <= FarDist2 ? FarUs : 0;
            _err = _errDurUs == 0 ? default : e;
            _errStartUs = nowUs;
        }

        /// <summary>
        /// Позиция для рисования в форме «точка + дельта»: предсказанная позиция и остаток гашения в unit (1/256 тайла).
        /// </summary>
        public void RenderDelta(long nowUs, out WorldPos from, out float dxUnits, out float dyUnits)
        {
            from = _mover.Position;
            if (_errDurUs <= 0)
            {
                dxUnits = dyUnits = 0f;
                return;
            }

            long t = nowUs - _errStartUs;
            if (t >= _errDurUs)
            {
                _err = default;
                _errDurUs = 0;
                dxUnits = dyUnits = 0f;
                return;
            }

            float k = 1f - t / (float)_errDurUs;
            dxUnits = _err.X * k;
            dyUnits = _err.Y * k;
        }

        /// <summary>Текущее гасящее смещение, unit (длина вектора, для HUD и тестов).</summary>
        public int ErrorUnits(long nowUs)
        {
            RenderDelta(nowUs, out _, out float dx, out float dy);
            return Units((long)(dx * dx + dy * dy));
        }

        /// <summary>Длительность текущего гашения, мкс (0 — гасить нечего или переставлено сразу).</summary>
        public long ErrorDurationUs => _errDurUs;

        // ---------- внутреннее ----------

        private uint OldestFrame => _slots[Mod(_head - _count)].Frame;

        private uint NewestFrame => _slots[Mod(_head - 1)].Frame;

        private void Push(uint frame, InputFrame input)
        {
            _slots[_head] = new Slot { Frame = frame, Input = input, After = _mover };
            _head = (_head + 1) % BufferSize;
            if (_count < BufferSize)
            {
                _count++;
            }
        }

        /// <summary>Целая длина по квадрату (вниз, метод Ньютона): без Math.Sqrt ОС.</summary>
        private static int Units(long d2)
        {
            if (d2 <= 0)
            {
                return 0;
            }

            long x = d2;
            long y = (x + 1) / 2;
            while (y < x)
            {
                x = y;
                y = (x + d2 / x) / 2;
            }

            return (int)x;
        }

        private static int Mod(int i)
        {
            int r = i % BufferSize;
            return r < 0 ? r + BufferSize : r;
        }
    }
}
