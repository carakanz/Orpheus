using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>Уровень строки лога сети.</summary>
    public enum NetLogLevel : byte
    {
        Info,
        Warn,
        Error,
    }

    /// <summary>
    /// Сервер по сети над тактом (docs/07, 2.4, 2.10, 4.3): приём соединений, вход со сверкой версии, сущность игрока,
    /// пакеты ввода → ввод сущности такта, полный снимок каждый такт (30 Гц), часы.
    /// <para>
    /// Порядок в цикле сервера: <see cref="Poll"/> (до такта: события транспорта, вход, ввод) → <c>Simulation.Step</c>
    /// → <see cref="SendSnapshots"/> (после такта). Всё — в потоке такта; времени внутри нет.
    /// </para>
    /// <para>
    /// Ввод покадрово (2.7 «Применение ввода»; 0-019), без опережения L: кадры из пакетов (все четыре — потеря пакета
    /// не теряет кадр) ложатся в кольцо игрока по номеру; каждый такт сервер шагает персонажа всеми пришедшими кадрами
    /// по порядку, полшага на кадр (<c>Simulation.SetFramesAt</c>) — тем же шагом, что предсказание клиента, поэтому
    /// без чужого влияния сверка «совпало» (2.6 п. 5). <c>AckFrame</c> снимка — последний шагнутый кадр. Против ускорения —
    /// не больше <see cref="FramesPerTickLimit"/> кадров за такт и в среднем 2 (запас копится, пока кадров нет); ждущих больше <see cref="MaxBacklogFrames"/> — старейшие перескакиваются; дыра в
    /// номерах (кадр не пришёл, а следующие — да) ждёт <see cref="HoleWaitTicks"/> такта (перестановка) и перескакивается.
    /// </para>
    /// <para>
    /// Нет кадров (2.7 «Нет ввода»): время персонажа ждёт; нового кадра нет <see cref="SelfStepAfterTicks"/> такта (W = 100 мс) —
    /// сервер шагает сам кадрами с последним вводом, 6 кадров (100 мс), опоздавшие кадры с этими номерами потом не
    /// шагаются. Обрыв (2.13 п. 6, 0-015): нового кадра нет <see cref="InputStaleTicks"/> тактов (0,2 с) — ввод пустой,
    /// персонаж стоит; соединение держится до обычного тайм-аута транспорта.
    /// </para>
    /// </summary>
    public sealed partial class NetServer : ITransportHandler
    {
        private enum PlayerState : byte
        {
            AwaitingLogin,
            InGame,
        }

        private sealed class Player
        {
            public int Peer;
            public PlayerState State;
            public int EntityId;
            public string Name = string.Empty;
            /// <summary>Новейший принятый кадр (по номеру); <see cref="HasFrame"/> — принят хоть один.</summary>
            public uint Newest;
            public bool HasFrame;
            public uint LastSnapshotAck;

            /// <summary>Следующий кадр к шагу (время персонажа + 1); задаётся первым пакетом.</summary>
            public uint NextFrame;
            public bool HasNext;

            /// <summary>Последний шагнутый кадр — <c>AckFrame</c> снимка; <see cref="HasApplied"/> — шагнут хоть один.</summary>
            public uint LastApplied;
            public bool HasApplied;

            /// <summary>Ввод последнего шагнутого кадра (с ним сервер шагает сам, когда кадров нет).</summary>
            public InputFrame LastInput;

            /// <summary>Запас кадров: +2 за такт, не больше <see cref="CreditCap"/>; за такт тратится не больше <see cref="FramesPerTickLimit"/>.</summary>
            public int Credits;

            /// <summary>Такт, с которого ждёт дыра в номерах кадров; нет дыры — −1.</summary>
            public long HoleSinceTick = -1;

            /// <summary>Кольцо пришедших кадров по номеру (ячейка — номер mod <see cref="FrameRing"/>).</summary>
            public readonly uint[] RingFrame = new uint[FrameRing];
            public readonly bool[] RingValid = new bool[FrameRing];
            public readonly InputFrame[] RingInput = new InputFrame[FrameRing];

            /// <summary>Такт сервера, на котором пришёл последний новый кадр ввода (или вход).</summary>
            public long LastInputTick;

            /// <summary>Такт сервера, на котором открылось соединение (тайм-аут входа, 0-025).</summary>
            public long ConnectedTick;

            /// <summary>Ввод подменён пустым по обрыву; снимается новым кадром.</summary>
            public bool Stopped;

            /// <summary>Кольцо отправленных снимков для дельт (0-014); создаётся при входе в игру.</summary>
            public SentRing? Ring;

            /// <summary>Вошёл с токеном хозяина (0-024).</summary>
            public bool IsHost;

            /// <summary>Время последнего пришедшего сообщения (мкс по часам <see cref="Poll"/>; 0-024 — отсчёт 60 с хоста).</summary>
            public long LastHeardMicros;

            /// <summary>Обзор (4.4; 0-022); создаётся при входе в игру.</summary>
            public ViewState? View;

            /// <summary>Угрозы (0-029): решаются по игроку замахи, начатые с этого кадра сервера (вход в игру).</summary>
            public uint ThreatsFrom;

            /// <summary>Кадр начала последней решённой по игроку угрозы; <see cref="HasResolved"/> — решена хоть одна.</summary>
            public uint ResolvedStart;
            public bool HasResolved;

            /// <summary>Чанки у клиента и их ревизии (3.5, 3.7; 0-026); создаётся с первым окном.</summary>
            public HeldChunks? Held;

            /// <summary>
            /// Сумка, отправленная игроку (1-002): после такта сверяется с сумкой сущности — изменилась → «сумка» (тип 26).
            /// <see cref="HasSentInv"/> — слана хоть раз; <see cref="InvVersion"/> — версия в последнем сообщении.
            /// </summary>
            public Inventory SentInv;
            public bool HasSentInv;
            public ushort InvVersion;

            /// <summary>
            /// Опережение L из последнего нового пакета ввода, кадров (0-033, 2.7) — подсказка только для отчёта о запасе (им клиент
            /// правит своё же L); D_p от него не зависит (з. 2 проверки 0-033).
            /// </summary>
            public int Lead;

            /// <summary>
            /// Задержка прихода кадров o = кадр сервера при разборе − номер новейшего кадра пакета + L пакета (owd вверх + ожидание
            /// опроса такта − ошибка часов) — по новым пакетам за 2 с (0-033). L пакета здесь — чтобы запас W + L − o₉₉ отзывался
            /// на смену L сразу, а не через окно 2 с (без него L перелетал вдвое при входе).
            /// </summary>
            public readonly FrameWindow Arrival = new FrameWindow(OwdWindow);

            /// <summary>
            /// Задержка показа по эху снимков (з. 2 проверки 0-033): номер кадра клиента в первом пакете, подтвердившем снимок S,
            /// минус кадр сервера 2·S, когда снимок (и «замах начат» того же такта) ушёл. Это RTT, замеренный сервером (от
            /// отправки снимка до пакета с его подтверждением), минус (кадр сервера − номер кадра) того же пакета: owd вниз + ошибка
            /// часов + L, кадр сервера при разборе сокращается. Поле L сюда не входит. По новым подтверждениям за 2 с (снимок —
            /// раз в такт).
            /// </summary>
            public readonly FrameWindow Echo = new FrameWindow(EchoWindow);

            /// <summary>99-й процентиль o за окно, кадров (считается раз в такт).</summary>
            public int OwdP99;

            /// <summary>99-й процентиль задержки показа по эху за окно, кадров.</summary>
            public int EchoP99;

            /// <summary>«Запас прихода кадров» при нынешнем L — отчёт в снимке: W + L − o₉₉, кадров (1-й процентиль запаса).</summary>
            public int Margin;

            /// <summary>
            /// Задержка показа D_p (≤ 30 кадров) — по эху снимков, без поля L, с которой решаются удары по игроку и которая уходит
            /// ему в «замах начат». Пересчитывается, только когда по игроку нет нерешённых угроз: кадр решения угрозы не меняется.
            /// </summary>
            public int DelayFrames;

            /// <summary>«Липкие» нажатия (2.7 «Опоздавший кадр»): кнопки опоздавших и выброшенных кадров — в первый шагнутый кадр.</summary>
            public ushort Sticky;

            /// <summary>
            /// Часы клиента — на старой шкале (з. 1 проверки 0-033): после «часов» (фриз сервера) и до перенумерации назад кадры
            /// клиента впереди времени сервера на длину фриза — это не опережение L, и потолок кадров «из будущего» их не держит
            /// (шагаются по запасу, как до 0-033). Иначе персонаж стоит до перенумерации, а предсказание идёт — лишняя сверка.
            /// </summary>
            public bool ClockStale;

            /// <summary>Такт заголовка первого снимка после «часов» — пакет, подтвердивший его, уже знает новую шкалу.</summary>
            public uint ClockStaleSnapshot;

            /// <summary>Такт последнего снимка этому игроку (0-036: в паузе снимок такта паузы — только тем, кто его не получал).</summary>
            public uint SentTick;
            public bool HasSentTick;

            /// <summary>
            /// После паузы (0-036): на сколько кадров время персонажа отставало от кадра сервера на снятии. Кадры, пришедшие в
            /// паузе, шагаются по два за такт с тем же отставанием, а не залпом: иначе очередь кончится раньше, чем дойдут
            /// кадры, отправленные после снятия, — сервер шагнул бы сам. 0 — как обычно; обнуляется, когда очередь паузы
            /// кончилась.
            /// </summary>
            public int ResumeLag;

            /// <summary>Правки тайлов (0-028): копание, ведро постановок, ждущие ответа; создаётся с первым запросом.</summary>
            public EditState? Edits;
        }

        /// <summary>
        /// Обзор игрока (docs/07, 4.4; 0-022): окно чанков 6 × 4 с выходом 8 × 6 (<see cref="ChunkWindow"/>, то же, что
        /// у клиента, 3.5) и сущности, бывшие в обзоре прошлого такта, — по номеру, отсортированы.
        /// </summary>
        private sealed class ViewState
        {
            /// <summary>Центр блока окна (<see cref="ChunkWindow.BlockCenter"/>); окно пересчитывается, когда он сменился.</summary>
            public ChunkCoord Center;
            public bool HasWindow;
            public ChunkCoord[] Window = new ChunkCoord[ChunkWindow.MaxChunks];
            public ChunkCoord[] WindowNext = new ChunkCoord[ChunkWindow.MaxChunks];
            public int WindowCount;

            /// <summary>Блок выхода 8 × 6 вокруг центра: отсюда берутся сущности обзора.</summary>
            public readonly ChunkCoord[] Keep = new ChunkCoord[ChunkWindow.MaxChunks];
            public int KeepCount;

            /// <summary>Чанк блока 8 × 6 (по номеру в <see cref="Keep"/>) — в окне: вход в обзор.</summary>
            public readonly bool[] KeepInWindow = new bool[ChunkWindow.MaxChunks];

            /// <summary>
            /// Номера чужих сущностей в обзоре прошлого такта; сортируются при первом поиске (<see cref="IdsSorted"/>) —
            /// пока все сущности обзора в окне, поиска нет и сортировки тоже.
            /// </summary>
            public readonly int[] Ids = new int[Snapshot.MaxEntities];
            public int IdCount;
            public bool IdsSorted;
        }

        /// <summary>
        /// Кольцо «что клиент имеет»: состояние (сущности), отправленное игроку на такте tick.
        /// Дельта строится от записи с тактом = подтверждение клиента (<c>LastSnapshot</c> ввода).
        /// </summary>
        private sealed class SentRing
        {
            public const int Depth = 32; // 1 с снимков 30 Гц: клиент, подтверждая, всегда внутри

            public readonly uint[] Tick = new uint[Depth];
            public readonly bool[] Valid = new bool[Depth];
            public readonly int[] Count = new int[Depth];
            public readonly SnapshotEntity[][] Ents = new SnapshotEntity[Depth][];
            public int Head;

            public SentRing()
            {
                for (int i = 0; i < Depth; i++)
                {
                    Ents[i] = new SnapshotEntity[Snapshot.MaxEntities];
                }
            }

            /// <summary>Индекс записи с тактом <paramref name="tick"/>; нет (клиент подтвердил старое) — −1.</summary>
            public int Find(uint tick)
            {
                for (int i = 0; i < Depth; i++)
                {
                    if (Valid[i] && Tick[i] == tick)
                    {
                        return i;
                    }
                }

                return -1;
            }

            public void Push(uint tick, SnapshotEntity[] src, int count)
            {
                Tick[Head] = tick;
                Valid[Head] = true;
                Count[Head] = count;
                Array.Copy(src, Ents[Head], count);
                Head = (Head + 1) % Depth;
            }
        }

        private readonly ITransport _transport;
        private readonly Simulation _sim;
        private readonly Func<int, WorldPos> _spawn;
        private readonly int _maxPlayers;
        private readonly NetVersion _version;
        private readonly SnapshotLayout _layout;
        private readonly List<Player> _players = new List<Player>();
        private readonly byte[] _buf = new byte[NetSettings.MaxUnreliablePayload];
        private readonly SnapshotEntity[] _cur = new SnapshotEntity[Snapshot.MaxEntities];
        private readonly SnapshotEntity[] _merged = new SnapshotEntity[Snapshot.MaxEntities];
        private readonly SnapshotDeltaMessage _delta = new SnapshotDeltaMessage();
        private readonly int[] _baseOf = new int[Snapshot.MaxEntities];
        private readonly int[] _removedIdx = new int[Snapshot.MaxEntities];
        private readonly int[] _updBase = new int[Snapshot.MaxEntities];
        private readonly int[] _updCur = new int[Snapshot.MaxEntities];
        private readonly DeltaFields[] _updFields = new DeltaFields[Snapshot.MaxEntities];
        private readonly int[] _addCur = new int[Snapshot.MaxEntities];
        private readonly InputFrame[] _frameBuf = new InputFrame[FramesPerTickLimit];
        private readonly uint[] _frameNum = new uint[FramesPerTickLimit]; // номера кадров _frameBuf (угрозы, 0-029)

        // Мир по сети (0-026): буфер надёжных сообщений (чанк и пачка — до 13 КБ) и индексы принятых правок чанка.
        private readonly byte[] _worldBuf = new byte[NetSettings.MaxReliablePayload];
        private readonly ushort[] _patchIdx = new ushort[Chunk.TileCount];

        // Обзор (0-022): ключи кандидатов «квадрат расстояния до своего << 20 | место» — сортировка без выделений,
        // ближние первыми, при равенстве — по месту (по номеру).
        private readonly long[] _candKeys;
        private const int CandSlotBits = 20;
        private SelfState _self;
        private int _spawned;
        private long _nowMicros;

        /// <summary>Кольцо кадров ввода игрока: 64 кадра (≈ 1 с) вперёд от времени персонажа.</summary>
        public const int FrameRing = 64;

        /// <summary>Окно задержки прихода кадров: 120 новых пакетов (2 с при 60 Гц; 2.7 «1-й процентиль запаса за 2 с»).</summary>
        public const int OwdWindow = 120;

        /// <summary>Окно эха снимков: 60 подтверждений (2 с при снимке раз в такт).</summary>
        public const int EchoWindow = 60;

        /// <summary>Ячеек окна: значения от <see cref="OwdMin"/> до <see cref="OwdMin"/> + 127 кадров (дальше — в крайние).</summary>
        public const int OwdBins = 128;

        /// <summary>Наименьшее учитываемое значение окна, кадров (часы клиента впереди — o меньше нуля).</summary>
        public const int OwdMin = -32;

        /// <summary>Скользящее окно значений в кадрах: кольцо ячеек и гистограмма — процентиль за проход по ячейкам без выделений.</summary>
        public sealed class FrameWindow
        {
            private readonly byte[] _ring;
            private readonly short[] _hist = new short[OwdBins];
            private int _head;

            public FrameWindow(int size) => _ring = new byte[size];

            /// <summary>Значений в окне.</summary>
            public int Count { get; private set; }

            public void Record(int value)
            {
                int bin = Math.Max(0, Math.Min(OwdBins - 1, value - OwdMin));
                if (Count == _ring.Length)
                {
                    _hist[_ring[_head]]--;
                }
                else
                {
                    Count++;
                }

                _ring[_head] = (byte)bin;
                _hist[bin]++;
                _head = (_head + 1) % _ring.Length;
            }

            /// <summary>99-й процентиль (1 % окна сверху, не меньше одного значения); пустое окно — <paramref name="empty"/>.</summary>
            public int P99(int empty)
            {
                if (Count == 0)
                {
                    return empty;
                }

                int need = Math.Max(1, (Count + 99) / 100);
                int bin = OwdBins - 1;
                for (int seen = 0; bin > 0; bin--)
                {
                    seen += _hist[bin];
                    if (seen >= need)
                    {
                        break;
                    }
                }

                return bin + OwdMin;
            }
        }

        /// <summary>Опоздавший кадр старше этого (кадров до времени персонажа) — не «липкий»: копия из давнего пакета.</summary>
        public const int StickyHorizonFrames = 32;

        /// <summary>Кадров за такт не больше 6 (2 + 4 на пачку после затишья; 0-019); в среднем — 2 (<see cref="CreditCap"/>).</summary>
        public const int FramesPerTickLimit = MovementTick.FramesPerTick + 4;

        /// <summary>Дыра в номерах ждёт столько тактов (переставленный пакет успевает), потом — перескок.</summary>
        public const int HoleWaitTicks = 2;

        /// <summary>Нового кадра нет столько тактов (W = 100 мс, 2.7) — сервер шагает персонажа сам.</summary>
        public const int SelfStepAfterTicks = 3;

        /// <summary>
        /// Запас кадров (ведро): +2 за такт, не больше 20 (≈ 0,33 с). Пока кадров нет — копится, пачка после затишья
        /// шагается по <see cref="FramesPerTickLimit"/> за такт, и очередь рассасывается (проверка 0-019, з. 1). За T тактов
        /// шагается не больше 2T + 20 кадров — ускорения нет.
        /// </summary>
        public const int CreditCap = 20;

        /// <summary>
        /// Ждущих кадров после плана такта не больше 6 (кадры одного такта с пачкой, 0,1 с; 0-025 — было 10, проверка 0-019
        /// з. 2): лишние старейшие — перескок (<see cref="FramesTrimmed"/>), чтобы время персонажа не отставало навсегда
        /// (задержка-пачка, спешащие часы клиента). Пока ждёт дыра в номерах — не режется.
        /// </summary>
        public const int MaxBacklogFrames = FramesPerTickLimit;

        /// <summary>
        /// Соединение без входа держится не дольше 300 тактов (10 с, как <c>DisconnectTimeout</c>; 0-025, проверка 0-008 з. 8).
        /// Счёт — тактами сервера: заморозка сервера (2.13 п. 5) тайм-аут не приближает.
        /// </summary>
        public const int LoginTimeoutTicks = 10 * Simulation.TickRate;

        /// <summary>Самоход (2.7 «Нет ввода»): кадров за один разрыв — 6 (100 мс), затем стоп.</summary>
        public const int SelfStepFrames = (InputStaleTicks - SelfStepAfterTicks) * MovementTick.FramesPerTick;

        /// <summary>
        /// Нового кадра ввода нет столько тактов (6 = 0,2 с, 2.13 п. 6) — ввод игрока пустой: движение 0, кнопки 0.
        /// </summary>
        public const int InputStaleTicks = 6;

        /// <summary>
        /// Контрольный полный кадр — раз в столько тактов (30 = 1 с; 0-014, 4.3). 1 — всегда полный (эталон для тестов).
        /// </summary>
        public uint KeyframePeriod { get; set; } = 30;

        /// <param name="transport">Транспорт сервера (слушает).</param>
        /// <param name="sim">Такт; сущности игроков добавляются и убираются вне такта.</param>
        /// <param name="spawn">Точка появления игрока № n (по счёту входов).</param>
        /// <param name="maxPlayers">Мест.</param>
        /// <param name="version">Версия сборки сервера (тесты подставляют свою).</param>
        public NetServer(ITransport transport, Simulation sim, Func<int, WorldPos> spawn, int maxPlayers, NetVersion version)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _sim = sim ?? throw new ArgumentNullException(nameof(sim));
            _spawn = spawn ?? throw new ArgumentNullException(nameof(spawn));
            _maxPlayers = maxPlayers;
            _version = version;
            _layout = new SnapshotLayout(sim.Shape);
            if (sim.Capacity >= 1 << CandSlotBits)
            {
                throw new ArgumentException($"Ёмкость симуляции {sim.Capacity} ≥ 2^{CandSlotBits}: не помещается в ключ обзора.", nameof(sim));
            }

            _candKeys = new long[sim.Capacity];
            GenerationHash = StateHash.Generation(sim.World);
        }

        /// <summary>
        /// Хэш снимка генерации для «входа принят» (0-017). По умолчанию — от мира при создании сервера (верно для мира
        /// без правок); загруженный мир с правками — сервер ставит хэш, посчитанный до наложения сохранения.
        /// </summary>
        public ulong GenerationHash { get; set; }

        /// <summary>Лог: уровень, категория (<c>net</c>), текст.</summary>
        public Action<NetLogLevel, string>? Log { get; set; }

        /// <summary>Игроков в игре.</summary>
        public int PlayersInGame
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _players.Count; i++)
                {
                    if (_players[i].State == PlayerState.InGame)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        /// <summary>Снимков отправлено (полных и дельт).</summary>
        public long SnapshotsSent { get; private set; }

        /// <summary>Пересчётов окна обзора (4.4: только когда центр блока окна сменился; 0-022).</summary>
        public long ViewWindowRecalcs { get; private set; }

        /// <summary>Обзоров, не влезших в снимок целиком: отсортированы ближними первыми (4.3; 0-022).</summary>
        public long ViewsSortedByDistance { get; private set; }

        /// <summary>Чанков целиком отправлено (вход в окно, запрос; 0-026).</summary>
        public long ChunksSent { get; private set; }

        /// <summary>Байт сообщений «чанк» (с типом и заголовком).</summary>
        public long ChunkBytes { get; private set; }

        /// <summary>Сжатий чанка сетью: копия собирается один раз на ревизию (3.6) — по числу ревизий, а не отправок.</summary>
        public long ChunkCompressions { get; private set; }

        /// <summary>Пачек правок отправлено (по одной на чанк за такт каждому, у кого чанк в окне; 3.7).</summary>
        public long PatchesSent { get; private set; }

        /// <summary>Байт пачек правок.</summary>
        public long PatchBytes { get; private set; }

        /// <summary>Сообщений «центр окна» (клиент выбросил чанки вне 8 × 6).</summary>
        public long WindowMessagesSent { get; private set; }

        /// <summary>Запросов «чанк целиком» от клиентов (пачка не к той ревизии, 3.7 «Порядок»).</summary>
        public long ChunkRequests { get; private set; }

        /// <summary>Сущностей вне окна, оставленных в обзоре гистерезисом (были в обзоре, ещё в блоке 8 × 6).</summary>
        public long ViewKeptByHysteresis { get; private set; }

        /// <summary>Событий боя разослано (смерть/возрождение, 1-001; каждому в игре).</summary>
        public long CombatEventsSent { get; private set; }

        /// <summary>Сообщений «сумка» отправлено (1-002; вход + каждое изменение).</summary>
        public long InventoriesSent { get; private set; }

        /// <summary>Байт снимков отправлено (полных и дельт).</summary>
        public long SnapshotBytes { get; private set; }

        /// <summary>Наибольший полный снимок, байт (дельты — <see cref="MaxDeltaBytes"/>).</summary>
        public int MaxSnapshotBytes { get; private set; }

        /// <summary>Снимков, обрезанных по бюджету 1200 байт (4.3: ошибка в лог).</summary>
        public long SnapshotsTruncated { get; private set; }

        /// <summary>Дельт отправлено / байт (0-014).</summary>
        public long DeltasSent { get; private set; }

        /// <inheritdoc cref="DeltasSent"/>
        public long DeltaBytes { get; private set; }

        /// <summary>Полных снимков отправлено (контрольные, первый, база потеряна, дельта не меньше полного).</summary>
        public long FullSent { get; private set; }

        /// <summary>Наибольшая дельта, байт.</summary>
        public int MaxDeltaBytes { get; private set; }

        /// <summary>Пакетов ввода принято / отброшено (старые, испорченные).</summary>
        public long InputsAccepted { get; private set; }

        /// <inheritdoc cref="InputsAccepted"/>
        public long InputsDropped { get; private set; }

        /// <summary>Соединений закрыто по тайм-ауту входа (<see cref="LoginTimeoutTicks"/>).</summary>
        public long LoginTimeouts { get; private set; }

        /// <summary>
        /// Перенумераций ввода назад (0-025): пакет новее по подтверждённому снимку, а номера не новее принятых — клиент
        /// переставил часы назад (фриз сервера); очередь игрока — заново от номеров пакета.
        /// </summary>
        public long InputRebases { get; private set; }

        /// <summary>Ответов на пинг часов.</summary>
        public long PongsSent { get; private set; }

        /// <summary>Эпоха часов: +1 при каждой рассылке «часы» (<see cref="BroadcastClock"/>), в заголовке каждого снимка.</summary>
        public byte ClockEpoch { get; private set; }

        /// <summary>
        /// Время мира в заголовке каждого снимка (<c>01</c> 6 «Сеть»; 1-014): такт суток из <see cref="Simulation.WorldTime"/>.
        /// Частота снимков (&gt; 5 Гц) чаще требования «раз в 5 с» — отдельного сообщения о времени нет, 2 байта в заголовке.
        /// </summary>
        public ushort WorldTimeTicks => (ushort)_sim.WorldTime;

        /// <summary>Сколько раз ввод игрока подменён пустым: нового кадра нет <see cref="InputStaleTicks"/> тактов.</summary>
        public long InputStops { get; private set; }

        /// <summary>Кадров ввода шагнуто (пришедших).</summary>
        public long FramesApplied { get; private set; }

        /// <summary>Кадров, шагнутых сервером самим (кадров нет дольше W, 2.7 «Нет ввода»).</summary>
        public long FramesSelfStepped { get; private set; }

        /// <summary>Кадров перескочено: дыра в номерах не закрылась за <see cref="HoleWaitTicks"/> такта.</summary>
        public long FramesSkipped { get; private set; }

        /// <summary>Кадров пришло после того, как их номер уже шагнут сервером сам или перескочен (2.7 «Опоздавший кадр»).</summary>
        public long FramesLate { get; private set; }

        /// <summary>Кадров отброшено: клиент ушёл вперёд больше кольца (<see cref="FrameRing"/>) — ускорение или обрыв.</summary>
        public long FramesOverflow { get; private set; }

        /// <summary>Нажатий перенесено «липкими» (2.7): из опоздавших и выброшенных кадров (0-033).</summary>
        public long StickyPresses { get; private set; }

        /// <summary>Кадров выброшено без шага: очередь длиннее <see cref="MaxBacklogFrames"/> или стоп по обрыву.</summary>
        public long FramesTrimmed { get; private set; }

        /// <summary>Тактов, в которых шагнуто больше 2 кадров (пачка после затишья).</summary>
        public long BurstTicks { get; private set; }

        /// <summary>Входов принято / отклонено.</summary>
        public int LoginsAccepted { get; private set; }

        /// <inheritdoc cref="LoginsAccepted"/>
        public int LoginsRejected { get; private set; }

        /// <summary>Отключений игроков в игре (любая причина).</summary>
        public int Disconnects { get; private set; }

        // ---------- Хозяин (0-024, docs/07 2.3 и 7.6) ----------

        /// <summary>Токен хозяина из ключей сервера; пустой — хозяина нет (выделенный сервер), вход с токеном — отказ.</summary>
        public HostToken HostToken { get; set; }

        /// <summary>
        /// «Открыть для друзей» от хозяина: начать слушать на всех адресах и вернуть порт; не вышло — исключение
        /// (текст — в лог, хозяину — «не открыт»). Зовётся в потоке такта из <see cref="Poll"/>. Null — команды нет.
        /// </summary>
        public Func<int>? OpenForFriends { get; set; }

        /// <summary>Хозяин вошёл (первый раз или вернулся).</summary>
        public Action? HostJoined { get; set; }

        /// <summary>
        /// Хозяин ушёл (отключение, тайм-аут): аргументы — время последнего сообщения от него и текущее (мкс, часы
        /// <see cref="Poll"/>); отсчёт 60 с — от последнего сообщения: упавшая игра молчит, а транспорт замечает обрыв
        /// только через тайм-аут.
        /// </summary>
        public Action<long, long>? HostLeft { get; set; }

        // ---------- Персонажи мира (0-035, docs/07 7.4, 7.5) ----------

        /// <summary>
        /// Персонажи мира: кто бывал и где стоял. Сервер ставит загруженных из сохранения (<see cref="CharacterRoster.Restore"/>)
        /// до первого входа; по умолчанию — пусто.
        /// </summary>
        public CharacterRoster Characters { get; set; } = new CharacterRoster();

        /// <summary>
        /// Персонаж ушёл из мира (отключение, тайм-аут, вытеснение не в счёт): аргумент — имя. Зовётся после <see cref="HostLeft"/>
        /// в потоке такта из <see cref="Poll"/> — сервер сохраняет мир (docs/07 6.4, 7.5: «в течение 1 с после исчезновения тела»).
        /// </summary>
        public Action<string>? CharacterLeft { get; set; }

        /// <summary>
        /// Создан новый персонаж (первый вход под этим ключом; вернувшийся не в счёт): аргумент — имя. Зовётся в потоке такта
        /// из <see cref="Poll"/> после «вход принят» — сервер сохраняет мир (docs/07 6.4: «игрок вошёл (создан персонаж) —
        /// в течение 1 с»; 0-037).
        /// </summary>
        public Action<string>? CharacterCreated { get; set; }

        /// <summary>Вернувшихся персонажей (вход по известному ключу).</summary>
        public int CharactersReturned { get; private set; }

        /// <summary>Из них поставлены не на место выхода (тайл занят) — рядом или у точки появления.</summary>
        public int CharactersMoved { get; private set; }

        /// <summary>Тайлов вокруг места выхода, где ищется свободное, если место занято (7.4: «иначе ближайший свободный тайл»).</summary>
        public const int PlaceSearchTiles = 8;

        /// <summary>Хозяин в игре.</summary>
        public bool HostInGame
        {
            get
            {
                for (int i = 0; i < _players.Count; i++)
                {
                    if (_players[i].IsHost && _players[i].State == PlayerState.InGame)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>Порт «Открыть для друзей» (команда хозяина выполнена); не открыт — −1.</summary>
        public int OpenedPort { get; private set; } = -1;


        /// <summary>До такта: события транспорта, вход, ввод; стоп тем, от кого ввода нет 0,2 с; кадры ввода на такт.</summary>
        public void Poll(long nowMicros)
        {
            _nowMicros = nowMicros;
            _transport.Poll(nowMicros, this);
            DropSilentConnections();
            ClearPauseIfEmpty(); // 0-036
            if (Paused)
            {
                return; // 0-036: пауза — кадров на такт нет, такта не будет; стоп по молчанию не считается (такт стоит)
            }

            StopStaleInputs();
            PlanFrames();
        }


        // ---------- Мир по сети (docs/07, 3.5–3.7; 0-026) ----------


        // ---------- События транспорта ----------

        void ITransportHandler.OnConnected(int peer)
        {
            if (Find(peer) == null)
            {
                _players.Add(new Player { Peer = peer, State = PlayerState.AwaitingLogin, ConnectedTick = _sim.Tick });
            }
        }

        void ITransportHandler.OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload)
        {
            Player? p = Find(peer);
            if (p == null)
            {
                return;
            }

            _players.Remove(p);
            if (p.State == PlayerState.InGame)
            {
                Disconnects++;
                LeaveWorld(p.EntityId);
                Log?.Invoke(reason == NetDisconnectReason.Timeout ? NetLogLevel.Warn : NetLogLevel.Info,
                    $"{(p.IsHost ? "хозяин" : "игрок")} «{p.Name}» (#{p.EntityId}) ушёл: {reason}");
                if (p.IsHost)
                {
                    HostLeft?.Invoke(p.LastHeardMicros, _nowMicros);
                }

                CharacterLeft?.Invoke(p.Name);
            }
        }

        void ITransportHandler.OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
        {
            Player? p = Find(peer);
            if (p == null)
            {
                return;
            }

            p.LastHeardMicros = _nowMicros;
            try
            {
                MessageType type = Codec.TypeOf(data);
                if (p.State == PlayerState.AwaitingLogin)
                {
                    if (type != MessageType.Login || channel != NetChannel.Facts)
                    {
                        if (type == MessageType.Input)
                        {
                            return; // ввод мог обогнать вход по ненадёжному каналу
                        }

                        Reject(p, LoginRejectReason.BadLogin, $"первым пришло {type} по каналу {channel}");
                        return;
                    }

                    // Версия — до разбора остального входа: у другой сборки он может быть другого вида (2.10).
                    NetVersion version = LoginMessage.ReadVersion(data);
                    if (version != _version)
                    {
                        Reject(p, LoginRejectReason.VersionMismatch, $"версия клиента {version}, сервера {_version}");
                        return;
                    }

                    OnLogin(p, LoginMessage.Read(data));
                    return;
                }

                if (type == MessageType.Input && channel == NetChannel.Unreliable)
                {
                    OnInput(p, InputMessage.Read(data));
                    return;
                }

                if (type == MessageType.ChunkRequest && channel == NetChannel.World)
                {
                    OnChunkRequest(p, ChunkRequestMessage.Read(data, _sim.Shape));
                    return;
                }

                if (type == MessageType.TileRequest && channel == NetChannel.Facts)
                {
                    OnTileRequest(p, TileRequestMessage.Read(data, _sim.Shape)); // 0-028
                    return;
                }

                if (type == MessageType.Ping && channel == NetChannel.Unreliable)
                {
                    // Часы клиента (2.4): ответ сразу, кадр — идущий такт (последний шагнутый + 1), как у «часов».
                    PingMessage ping = PingMessage.Read(data);
                    int len = new PongMessage { ClientUs = ping.ClientUs, ServerTick = (uint)_sim.Tick + 1 }.Write(_buf);
                    _transport.Send(p.Peer, NetChannel.Unreliable, new ReadOnlySpan<byte>(_buf, 0, len));
                    PongsSent++;
                    return;
                }

                if (type == MessageType.PauseRequest && channel == NetChannel.Facts)
                {
                    OnPauseRequest(p, PauseRequestMessage.Read(data)); // 0-036
                    return;
                }

                if (type == MessageType.OpenForFriends && channel == NetChannel.Facts)
                {
                    OpenForFriendsMessage.Read(data);
                    OnOpenForFriends(p);
                    return;
                }

                InputsDropped++;
            }
            catch (ProtocolException e)
            {
                if (p.State == PlayerState.AwaitingLogin)
                {
                    Reject(p, LoginRejectReason.BadLogin, e.Message);
                }
                else
                {
                    InputsDropped++;
                }
            }
        }


    }
}
