using System;
using System.Collections.Generic;
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

        // E4-2 (соакер №8, 21.11): чужие тела последнего снимка (враги, напарники) — метрика «расхождение
        // задано раздвиганием тел» (1-007 8.5: сервер раздвигает до 2·RadiusUnits, предсказание этого не имеет).
        const int BodyBandUnits = 2 * Collision.RadiusUnits;
        const long BodyBand2 = (long)BodyBandUnits * BodyBandUnits;
        readonly WorldPos[] _bodies = new WorldPos[Snapshot.MaxEntities];
        int _bodyCount;

        // E4-2 (соакер №11): зеркало серверного раздвигания тел — позиции врагов с мозгом из последнего снимка.
        const int PushUnitsPerFrame = 34; // 68 unit/такт (Simulation.Movement.StepSeparation) ÷ FramesPerTick (2)
        readonly WorldPos[] _pushers = new WorldPos[Snapshot.MaxEntities];
        int _pushCount;
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
        private bool _hitch;        // E4-2 (доводка 16): цикл клиента отстал (пауза > LoopHitchUs) — сверка после простоя

        // E4-2 (доводка 10, 21.11): разбор кадров с крупным расхождением «без нокбэка» (соакеры №2–9: 50…102 unit
        // при норме 68). Метрику и ворота это НЕ меняет: тут только контекст топ-6 таких кадров — позиции сервера
        // и предсказания, нокбэк с обеих сторон, попадание в блок и в полосу раздвигания с обеих сторон. По этим
        // строкам видно, какой класс расхождения атрибуция (417–446) не покрыла, и его чинят точечно.
        const int DevTraceSlots = 6;
        readonly DevTrace[] _devTrace = new DevTrace[DevTraceSlots];

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

        /// <summary>Кадр подтверждения, на котором получено <see cref="MaxDevUnits"/> (для разбора длинных соакеров: фаза сценария по кадру).</summary>
        public long MaxDevAckFrame { get; private set; }

        /// <summary>
        /// Наибольшее расхождение предсказания в кадре, где расхождение задано НЕ серверным нокбэком
        /// (нокаут/толчок — серверное состояние, оно идёт в <see cref="KnockAdopted"/>, 1-001) и не
        /// выталкиванием из блока, появившегося в окне предсказания (E4-2).
        /// Это и есть ошибка предсказания: норма 68 unit (касание врага, <c>PredictionTests…EnemyTouch</c>).
        /// </summary>
        public int MaxDevNoKnockUnits { get; private set; }

        /// <summary>Кадр подтверждения, на котором получено <see cref="MaxDevNoKnockUnits"/>.</summary>
        public long MaxDevNoKnockFrame { get; private set; }

        /// <summary>Норма расхождения «без нокбэка», unit (68 = такт серверного раздвигания; гейт соакера E4-2).</summary>
        public const int NoKnockBreachUnits = 68;

        /// <summary>
        /// Первый кадр «без нокбэка» с расхождением выше <see cref="NoKnockBreachUnits"/> и его контекст
        /// (E4-2, доводка 13): соак №12 — пик 77 стоит кадрами 97251–97273 уже «осевшим» смещением
        /// (сервер X 29466 / пред 29543, Y равны, полосы и нокбэка нет), т.е. пик — не момент рождения
        /// расхождения. Этот трейс показывает МОМЕНТ, где смещение возникло, — по нему ищут причину.
        /// </summary>
        public DevTrace NoKnockFirstBreach { get; private set; }

        /// <summary>Был ли кадр с превышением нормы «без нокбэка» (иначе <see cref="NoKnockFirstBreach"/> не заполнен).</summary>
        public bool NoKnockFirstBreachKnown { get; private set; }

        /// <summary>
        /// Кадров расхождения «без нокбэка» больше 0,5 тайла (<see cref="HalfTileUnits"/>) — их в топ-<see cref="DevTraceSlots"/>
        /// больше всего, и именно они выходят за норму соакера. Счётчик для разбора (E4-2, доводка 10), ворота не трогает.
        /// </summary>
        public long NoKnockOverHalfTile { get; private set; }

        /// <summary>Кадров, списанных в «расхождение задано нокбэком с любой стороны» (ветка knockRelated, 1-001/E4-2).</summary>
        public long KnockRelatedExcluded { get; private set; }

        /// <summary>Кадров, списанных в «расхождение задано выталкиванием из нового блока» (E4-2, соакеры 20.11).</summary>
        public long PushOutExcluded { get; private set; }

        /// <summary>
        /// Порог «запинки цикла клиента» (E4-2, доводка 16): пауза между кадрами больше ДВУХ кадров (60 Гц).
        /// Пока цикл стоял, сервер шагнул лишние такты, и правку задаёт сервер (1-001), а не ходьба: первый
        /// крупный остаток после простоя — не ошибка предсказания (соак №15, кадр 97253: 77 unit по X,
        /// «в блоке False/False», нокбэка нет, тел 0, держится 90 кадров, ресинков 326 против 29 у тихого
        /// соака №14, где этот класс не появлялся).
        /// </summary>
        public const long LoopHitchUs = 2 * SeamWatch.FrameUs;

        /// <summary>Кадров, списанных в «расхождение задано запинкой цикла клиента» (E4-2, доводка 16).</summary>
        public long LoopHitchExcluded { get; private set; }

        /// <summary>Запинкок цикла (кадров, пришедших позже <see cref="LoopHitchUs"/> после предыдущего) — для отчёта (доводка 16).</summary>
        public long LoopHitchTicks { get; private set; }

        /// <summary>Самая длинная запинка цикла, мкс: видно, что списание задано настоящим простоем (доводка 16).</summary>
        public long LoopHitchMaxUs { get; private set; }

        /// <summary>
        /// Кадр с крупным расхождением «без нокбэка» и его контекст: видно, был ли нокбэк с одной из сторон,
        /// стояло ли тело в блоке/в полосе раздвигания на сервере и в предсказании (E4-2, доводка 10).
        /// </summary>
        public struct DevTrace
        {
            /// <summary>Кадр подтверждения этой сверки.</summary>
            public uint AckFrame;
            /// <summary>Расхождение предсказания с сервером, unit.</summary>
            public int DevUnits;
            /// <summary>Серверная позиция в кадре ack.</summary>
            public WorldPos ServerPos;
            /// <summary>Предсказанная позиция в кадре ack.</summary>
            public WorldPos PredPos;
            /// <summary>Серверный остаток отбрасывания (0 — сервер не толкает).</summary>
            public int ServerKnockLeft;
            /// <summary>Остаток отбрасывания в предсказании (0, если ack вне окна истории).</summary>
            public int PredKnockLeft;
            /// <summary>Серверная позиция в проходимом блоке.</summary>
            public bool ServerInSolid;
            /// <summary>Предсказанная позиция в проходимом блоке.</summary>
            public bool PredInSolid;
            /// <summary>Серверная позиция в полосе раздвигания чужих тел (2·RadiusUnits).</summary>
            public bool ServerInBodyBand;
            /// <summary>Предсказанная позиция в полосе раздвигания чужих тел.</summary>
            public bool PredInBodyBand;
            /// <summary>Чужих тел в последнем снимке.</summary>
            public int BodyCount;
            /// <summary>Труп (предсказание стоит, сервер не шагнёт ввод).</summary>
            public bool Dead;
            /// <summary>Состояние ack найдено в кольце истории (иначе сравнивать не с чем).</summary>
            public bool Known;
        }

        /// <summary>
        /// Кадров, исключённых из <see cref="MaxDevNoKnockUnits"/> как «расхождение задано раздвиганием тел»
        /// (E4-2, соакер №8 21.11): предсказание в полосе касания чужого тела (враг/напарник), серверная позиция —
        /// вне полосы, т.е. сервер уже раздвинул тела (<c>Simulation.Movement.StepSeparation</c>, 1-007 8.5).
        /// Счётчик виден в отчёте: исключение точечное, а не «ковровое». MaxDevUnits и ComparedOverHalfTile не тронуты.
        /// </summary>
        public long BodyPushExcluded { get; private set; }

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

        /// <summary>Предсказанная фаза удара после последнего кадра (1-004): для анимации своего персонажа.</summary>
        public MeleeAction Melee => _mover.Melee;

        /// <summary>Здоровье последнего снимка (1-001): для полоски; 0 — труп.</summary>
        public int Health => _health;

        /// <summary>Масса из снимка (1-001): по ней скорость отбрасывания.</summary>
        public int Mass => _mass;

        /// <summary>Выносливость (1-003): запас предсказания, правится снимком при сверке; для HUD и полоски (1-019).</summary>
        public byte Stamina => _mover.Stamina.Value;

        /// <summary>
        /// Ячейка быстрой панели в последнем отправленном кадре (1-002) — «предмет в руках» для картинки и HUD:
        /// сервер применяет её в том же кадре, что и предсказание, поэтому клиент не ждёт снимка.
        /// </summary>
        public byte SelectedSlot => _slot;

        /// <summary>
        /// Предмет в руках клиента (id <see cref="Items"/>, 1-004): с ним кадр предсказывает удар — сервер начинает
        /// замах только с ближним оружием в руках (гейт в такте), и предсказание повторяет тот же гейт.
        /// Клиент ставит по своей сумке (она приходит при входе и изменении — тип 26).
        /// </summary>
        public byte HeldItem { get; set; }

        /// <summary>Бит удара кадра для шага предсказания: нажат и в руках ближнее оружие (гейт как в такте сервера).</summary>
        private bool AttackOf(in InputFrame f) =>
            f.AttackPressed && Items.KindOf(HeldItem) == Items.KindMelee;

        /// <summary>В сумке есть стрелы (E3-2): без них лук не натягивается — тот же гейт, что в такте сервера. Ставит клиент по своей сумке.</summary>
        public bool HasArrows { get; set; }

        /// <summary>Вход лука кадра для шага предсказания (E3-2): лук в руках, ЛКМ, стрелы — как <c>Simulation.BowInputOf</c>.</summary>
        private byte BowOf(in InputFrame f) =>
            BowInput.Of(Items.KindOf(HeldItem) == Items.KindRanged, f.AttackPressed, HasArrows);

        /// <summary>Предсказанная фаза лука после последнего кадра (E3-2): поза натяжения и кольцо силы у курсора.</summary>
        public BowAction Bow => _mover.Bow;

        private readonly Queue<BowAction> _shots = new Queue<BowAction>(16);

        /// <summary>
        /// Свой выстрел, предсказанный в отправленном кадре (E3-2, `02` 4.4 «клиент рисует свой снаряд сразу»): сила, ось, точка —
        /// поля <see cref="BowAction.ShotDraw"/>…; переигровка сверки новых выстрелов не даёт. false — очередь пуста.
        /// </summary>
        public bool TryTakeShot(out BowAction shot)
        {
            if (_shots.Count == 0)
            {
                shot = default;
                return false;
            }

            shot = _shots.Dequeue();
            return true;
        }

        /// <summary>Выстрел кадра в очередь картинки (только первый шаг кадра, не переигровка) — флаг снят.</summary>
        private void NoteShot()
        {
            if (_mover.Bow.Fired)
            {
                if (_shots.Count < 16)
                {
                    _shots.Enqueue(_mover.Bow);
                }

                _mover.Bow.Fired = false;
            }
        }

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
                        _mover.StepFrame(_map, input.Direction, input.DodgePressed, AttackOf(input), input.Angle16, BowOf(input), _solid); // копии — с кнопками, как шлёт клиент (0-029, 1-004)
                        NoteShot();
                        ApplySeparation(); // E4-2: зеркало серверного раздвигания тел (шаг 34 unit/кадр)
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
                _mover.StepFrame(_map, input.Direction, input.DodgePressed, AttackOf(input), input.Angle16, BowOf(input), _solid); // 1-001: труп стоит — сервер не шагнёт ввод; 1-004: удар — с melee-гейтом, как в такте
                NoteShot(); // E3-2: свой выстрел — картинка сразу
                ApplySeparation(); // E4-2: зеркало серверного раздвигания тел (шаг 34 unit/кадр)
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
        /// E4-2 (соакер №8): чужие тела последнего снимка (враги и напарники) — для метрики «расхождение задано
        /// раздвиганием тел» (<see cref="BodyPushExcluded"/>). Позиции чужих тел серверные: своего шага врага
        /// предсказание не знает, поэтому кадры раздвигания не пишутся в «ошибку предсказания без нокбэка».
        /// Пустой снимок (нет снимка) — тел нет, метрика работает как раньше.
        /// </summary>
        public void SetBodies(in Snapshot snap, int selfId)
        {
            _bodyCount = 0;
            _pushCount = 0;
            for (int i = 0; i < snap.Count && _bodyCount < _bodies.Length; i++)
            {
                SnapshotEntity e = snap.Entities[i];
                if (e.Id != selfId && (e.Kind == SimEntity.KindEnemy || e.Kind == SimEntity.KindCharacter))
                {
                    _bodies[_bodyCount++] = e.Position;
                    if (e.Kind == SimEntity.KindEnemy && (e.Enemy >> 3) != 0 && e.HealthPct > 0)
                    {
                        _pushers[_pushCount++] = e.Position; // враг с мозгом — инициатор раздвигания (E4-2 зеркало)
                    }
                }
            }
        }

        /// <summary>Тело в полосе раздвигания (центр ближе 2·<see cref="Collision.RadiusUnits"/> к чужому телу).</summary>
        bool BodiesTouch(WorldPos p)
        {
            for (int i = 0; i < _bodyCount; i++)
            {
                if (Torus.Dist2(_map, p, _bodies[i]) < BodyBand2)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// E4-2 (соакер №11): ЗЕРКАЛО серверного раздвигания тел (<c>Simulation.Movement.StepSeparation</c>,
        /// 1-007 8.5 «толкаются») в предсказании. Сервер: инициатор — враг с мозгом, пара «враг↔свой» в полосе
        /// 2·RadiusUnits разъезжается по 68 unit/такт вдоль оси, целочисленно и с упором в твёрдое
        /// (<c>Collision.Move</c>). Смещение СВОЕГО тела при этом повторяемо: та же ось (позиции врага из
        /// последнего снимка), та же целочисленная арифметика, только шаг дробится на кадры (34 unit/кадр).
        /// Соакер №11: все top-6 расхождений «без нокбэка» (80 &gt; 68 — такт раздвигания + шаг) — кадры, где
        /// и предсказание, и сервер в полосе тела: сервер раздвинул, предсказание стояло. Остаток (снимок врага
        /// устарел на такт, шаг самого врага) — в пределах нормы 68 unit.
        /// </summary>
        void ApplySeparation()
        {
            for (int i = 0; i < _pushCount; i++)
            {
                Int2 d = Torus.Delta(_map, _pushers[i], _mover.Position); // ось «враг → свой», как d = Delta(a, b) на сервере
                Int2 step = SeparationStep(d, d.LengthSquared, PushUnitsPerFrame);
                if (step.X == 0 && step.Y == 0)
                {
                    continue;
                }

                _mover.Position = Collision.Move(_map, _mover.Position, step.X, step.Y, _solid);
                SeparationFrames++;
                SeparationUnits += Math.Abs(step.X) + Math.Abs(step.Y);
            }
        }

        /// <summary>Кадры, в которых сработало зеркало раздвигания (0 — чужих тел с мозгом в полосе не было).</summary>
        public int SeparationFrames { get; private set; }

        /// <summary>Сумма unit, на которую зеркало сдвинуло предсказание (для тестов и разбора соакера).</summary>
        public int SeparationUnits { get; private set; }

        /// <summary>
        /// Шаг раздвигания за один кадр предсказания: та же ось и та же целочисленная арифметика, что у сервера
        /// (<c>Simulation.Movement.StepSeparation</c>), только <paramref name="pushUnits"/> = 34 (68/такт ÷ 2 кадра).
        /// Вне полосы 2·RadiusUnits — нулевой шаг; совпавшие позиции — ось по +X (детерминированно, как на сервере).
        /// </summary>
        public static Int2 SeparationStep(Int2 d, long d2, int pushUnits)
        {
            if (d2 >= BodyBand2)
            {
                return default;
            }

            if (d2 == 0)
            {
                d = new Int2(BodyBandUnits - 1, 0); // позиции совпали — ось по X (детерминированно, как на сервере)
                d2 = (long)(BodyBandUnits - 1) * (BodyBandUnits - 1);
            }

            int dist = ISqrt(d2); // ≥ 1, т.к. d2 ≥ 1
            return new Int2(d.X * pushUnits / dist, d.Y * pushUnits / dist);
        }

        /// <summary>Целочисленный √ — тот же метод по разрядам, что у сервера (<c>Simulation.ISqrt</c>): без float, детерминирован.</summary>
        static int ISqrt(long n)
        {
            long res = 0;
            long bit = 1L << 62;
            while (bit > n)
            {
                bit >>= 2;
            }

            while (bit != 0)
            {
                if (n >= res + bit)
                {
                    n -= res + bit;
                    res = (res >> 1) + bit;
                }
                else
                {
                    res >>= 1;
                }

                bit >>= 2; // как на сервере (Simulation.ISqrt): без сдвига разряда цикл не завершается
            }

            return (int)res;
        }

        /// <summary>
        /// Топ-<see cref="DevTraceSlots"/> кадров расхождения «без нокбэка» (по убыванию unit) с контекстом —
        /// для разбора соакера (E4-2, доводка 10). Копия: вызывается один раз на отчёт.
        /// </summary>
        public DevTrace[] DevTraceTop()
        {
            DevTrace[] copy = new DevTrace[DevTraceSlots];
            Array.Copy(_devTrace, copy, DevTraceSlots);
            Array.Sort(copy, static (a, b) => b.DevUnits.CompareTo(a.DevUnits));
            return copy;
        }

        /// <summary>
        /// Контекст кадра сверки для разбора (общая сборка для топ-трейса и первого превышения).
        /// </summary>
        DevTrace TraceOf(uint ackFrame, int devUnits, in SelfState server, in Mover predicted, bool known) => new DevTrace
        {
            AckFrame = ackFrame,
            DevUnits = devUnits,
            ServerPos = server.Position,
            PredPos = predicted.Position,
            ServerKnockLeft = server.KnockLeft,
            PredKnockLeft = known ? predicted.KnockLeft : 0,
            ServerInSolid = Collision.TouchesSolid(_map, server.Position, _solid),
            PredInSolid = Collision.TouchesSolid(_map, predicted.Position, _solid),
            ServerInBodyBand = _bodyCount > 0 && BodiesTouch(server.Position),
            PredInBodyBand = _bodyCount > 0 && BodiesTouch(predicted.Position),
            BodyCount = _bodyCount,
            Dead = _dead,
            Known = known,
        };

        /// <summary>
        /// Записать кадр в топ-<see cref="DevTraceSlots"/> (замена наименьшего, аллокаций в горячем пути нет).
        /// </summary>
        void TraceNote(uint ackFrame, int devUnits, in SelfState server, in Mover predicted, bool known)
        {
            int worst = 0;
            for (int i = 1; i < DevTraceSlots; i++)
            {
                if (_devTrace[i].DevUnits < _devTrace[worst].DevUnits)
                {
                    worst = i;
                }
            }

            if (_devTrace[worst].DevUnits >= devUnits)
            {
                return; // все слоты крупнее — кадр в топ не попадает
            }

            _devTrace[worst] = TraceOf(ackFrame, devUnits, in server, in predicted, known);
        }

        /// <summary>
        /// Такт цикла клиента (E4-2, доводка 16): <paramref name="gapUs"/> — пауза от прошлого кадра до этого.
        /// Флаг = «цикл отстаёт ИМЕННО в этом кадре»: пауза длиннее <see cref="LoopHitchUs"/> — окно
        /// предсказания между сверками вышло за штатный шаг, и крупные «готовые» остатки этого кадра
        /// атрибутируются запинке (LoopHitchExcluded), а не ходьбе. Снимается следующим тактом с обычной
        /// паузой, поэтому все снимки, пришедшие после простоя (в одном кадре их бывает 2…3), видят флаг.
        /// </summary>
        public void NoteLoopHitch(long gapUs)
        {
            _hitch = gapUs > LoopHitchUs;
            if (_hitch)
            {
                LoopHitchTicks++;
                if (gapUs > LoopHitchMaxUs)
                {
                    LoopHitchMaxUs = gapUs;
                }
            }
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

            // Отбрасывание задал сервер (предсказание его не имело — удар решает сервер): расхождение позиции
            // «от нокбэка» — не ошибка предсказания, приём серверного состояния считаем отдельно (1-001).
            bool knockDriven = !_dead && server.KnockLeft > 0 && (known ? predicted.KnockLeft == 0 : true);

            // Кадры, где нокбэк «живёт» с любой стороны: сервер оттолкнул (KnockLeft > 0) ИЛИ предсказание
            // ещё шагает принятый нокбэк (server 0, предсказание > 0 — остаток после конца отбрасывания,
            // у стены расхождение выталкивания доходит до 77–81). Такую правку нельзя писать в «ошибку
            // предсказания без нокбэка» — она задана отбрасыванием, а не ходьбой (E4-2, соакер 20.11:
            // «noKnock 81 > 68, кадр 103285» — кадр сразу после конца нокбэка в ночи). Ветku Resynced/
            // KnockAdopted не трогаем: счётчики и приём состояния остаются как в 1-001.
            bool knockRelated = !_dead && (server.KnockLeft > 0 || (known && predicted.KnockLeft > 0));

            // Кадры, где расхождение задано серверным ВЫТАЛКИВАНИЕМ из блока, о котором предсказание в окне
            // не знало (поставили стену/дверь/мебель на тело, блок появился в (ackFrame, now]): тело стоит
            // в блоке у ОДНОГО из двух (сервер уже вытолкнул/вдавило, предсказание про новый блок не знало,
            // или наоборот — 1-001: правку задаёт сервер, предсказание её не имело — это не ошибка
            // предсказания). Соакеры 20.11: «noKnock 77/80/81 > 68» — все в полосе выталкивания (радиус тела
            // RadiusUnits = 102). Соак №14, кадр 84801 — ОБРАТНАЯ ориентация того же класса: сервер вдавлен
            // в твёрдый тайл («в блоке серв True»), предсказание стоит на линии среза («пред False»),
            // расхождение 77 unit (доводка 15): поэтому признак — «в блоке ровно у одного из двух», а не
            // односторонняя проверка. Кадры «оба в блоке» (соак №14, кадр 99353, 68 unit) признаком НЕ
            // снимаются и остаются в норме. Статистика: MaxDevUnits, ComparedOverHalfTile, счётчики
            // Resynced/KnockAdopted НЕ тронуты.
            bool pushOutDriven = known && !knockRelated && !_dead &&
                Collision.TouchesSolid(_map, predicted.Position, _solid) !=
                Collision.TouchesSolid(_map, server.Position, _solid);

            // Кадры, где расхождение задано РАЗДВИГАНИЕМ ТЕЛ (1-007, 8.5 «расталкивание»: сервер раздвигает
            // тела до 2·RadiusUnits = 204 unit со скоростью 68 unit/такт, Simulation.Movement.StepSeparation).
            // Предсказание этих шагов иметь не может: позиции чужих тел авторитетны на сервере, шага врага
            // в окне предсказания нет. Тот же класс 1-001, что нокбэк (решает сервер) и выталкивание из нового
            // блока. Соакеры 21.11: «noKnock 77 > 68, кадр 97267» (старт ночи) = 68 (такт раздвигания) + 9 (шаг).
            // Счётчик BodyPushExcluded — в отчёт, чтобы исключение не было «ковровым».
            bool bodyPushDriven = known && !knockRelated && !pushOutDriven && !_dead &&
                _bodyCount > 0 && BodiesTouch(predicted.Position) && !BodiesTouch(server.Position);
            if (bodyPushDriven)
            {
                BodyPushExcluded++;
            }

            if (knockRelated)
            {
                KnockRelatedExcluded++;
            }

            if (pushOutDriven)
            {
                PushOutExcluded++;
            }

            if (known && !initial)
            {
                long d2 = Torus.Dist2(_map, predicted.Position, server.Position);
                LastDevUnits = Units(d2);

                // Расхождение в кадре ОТСТАЮЩЕГО ЦИКЛА КЛИЕНТА (доводка 16): пока цикл стоял (кадр пришёл
                // позже 2·FrameUs), сервер шагнул лишние такты, и правку задаёт сервер (1-001), а не ходьба
                // предсказания. Соак №15, кадры 97253…97343: 77 unit по X, «в блоке False/False», нокбэка
                // нет, чужих тел 0, ресинков 326 (у тихого соака №14 — 29, класс не появлялся). Признак
                // точечный: кадр отстающего цикла (NoteLoopHitch) и только крупное расхождение (> нормы
                // 68 unit), поэтому это не «ковёр»: соаки №12/№14 (запинок 0) им не снимают ничего.
                // Счётчик LoopHitchExcluded — в отчёте, списание видно.
                bool loopHitchDriven = _hitch && LastDevUnits > NoKnockBreachUnits;
                if (loopHitchDriven)
                {
                    LoopHitchExcluded++;
                }

                if (LastDevUnits > MaxDevUnits)
                {
                    MaxDevUnits = LastDevUnits;
                    MaxDevAckFrame = ackFrame;
                }

                // То же без кадров, заданных нокбэком (начало, середина, остаток после конца) и серверным
                // выталкиванием из нового блока: это и есть ошибка предсказания (норма 68 unit = касание
                // врага). Нужна для длинных прогонов с боем и стройкой (E4-2).
                if (!knockDriven && !knockRelated && !pushOutDriven && !bodyPushDriven && !loopHitchDriven)
                {
                    if (LastDevUnits > MaxDevNoKnockUnits)
                    {
                        MaxDevNoKnockUnits = LastDevUnits;
                        MaxDevNoKnockFrame = ackFrame;
                    }

                    if (!NoKnockFirstBreachKnown && LastDevUnits > NoKnockBreachUnits)
                    {
                        NoKnockFirstBreachKnown = true;
                        NoKnockFirstBreach = TraceOf(ackFrame, LastDevUnits, in server, predicted, known);
                    }

                    // Разбор (доводка 10): контекст крупных кадров — по нему ищут класс расхождения, не учтённый
                    // атрибуцией выше. На пороги и счётчики ворот не влияет.
                    if (LastDevUnits > HalfTileUnits)
                    {
                        NoKnockOverHalfTile++;
                    }

                    TraceNote(ackFrame, LastDevUnits, in server, predicted, known);
                }
                Compared++;
                if (LastDevUnits > HalfTileUnits)
                {
                    ComparedOverHalfTile++;
                }

                if (loopHitchDriven)
                {
                    // Кадры, снятые запинкой, тоже видны в разборе (доводка 16): по ним проверяют, что
                    // атрибуция сработала на настоящем простое, а не съела штатную ошибку ходьбы.
                    TraceNote(ackFrame, LastDevUnits, in server, predicted, known);
                }

                if (d2 <= MatchDist2 && predicted.Carry == server.Carry && SelfState.SameDodge(predicted.Dodge, server.Dodge) &&
                    predicted.KnockLeft == server.KnockLeft && predicted.KnockDir == server.KnockDir)
                {
                    return; // совпало (2.6 п. 3); нокбэк — тоже (1-001): шаги совпали с серверными
                }
            }

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
            if (known)
            {
                // P-004: фазы удара и таймеров выносливости в снимке нет — берём предсказанные для ack. Текущие уже
                // прошли кадры новее ack: переигровка шагнула бы их второй раз, замах убежал бы вперёд сервера на длину
                // переигровки, замедление 70 % встало бы не на те кадры — и каждая граница фазы давала бы новую правку.
                _mover.Melee = predicted.Melee;
                _mover.Bow = predicted.Bow; // E3-2: фазы лука в снимке нет — как удар
                _mover.Stamina = predicted.Stamina;
            }

            _mover.Stamina.Value = server.Stamina; // 1-003: запас — с сервера (страховка), таймеры регена — предсказанные для ack
            _before = _mover;
            _beforeFrame = ackFrame;
            _beforePre = !_hasHistory || (int)(ackFrame - _firstFrame) < 0;
            _head = _count = 0;
            for (int k = n - 1; k >= 0; k--)
            {
                if (!_dead)
                {
                    _mover.StepFrame(_map, _replay[k].Input.Direction, _replay[k].Input.DodgePressed, AttackOf(_replay[k].Input), _replay[k].Input.Angle16, BowOf(_replay[k].Input), _solid);
                    _mover.Bow.Fired = false; // E3-2: выстрел переигровки уже нарисован
                    ApplySeparation(); // E4-2: зеркало серверного раздвигания тел (шаг 34 unit/кадр)
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
