using System;
using System.Collections.Generic;
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
    public sealed class NetServer : ITransportHandler
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

            /// <summary>Чанки у клиента и их ревизии (3.5, 3.7; 0-026); создаётся с первым окном.</summary>
            public HeldChunks? Held;
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

        /// <summary>
        /// Закрыть мир (7.6): всем соединениям — отключение с <see cref="ClosingMessage"/> (клиент покажет «Хост вышел…»
        /// вместо «связь потеряна»). Сущности игроков остаются как есть — сервер после этого завершается.
        /// </summary>
        public void CloseAll(ClosingReason reason)
        {
            int len = new ClosingMessage { Reason = reason }.Write(_buf);
            for (int i = _players.Count - 1; i >= 0; i--)
            {
                _transport.Disconnect(_players[i].Peer, new ReadOnlySpan<byte>(_buf, 0, len));
            }

            _players.Clear();
            _transport.Flush();
        }

        /// <summary>Номер сущности игрока на соединении <paramref name="peer"/>; не в игре — −1.</summary>
        public int EntityOf(int peer)
        {
            Player? p = Find(peer);
            return p != null && p.State == PlayerState.InGame ? p.EntityId : -1;
        }

        /// <summary>
        /// Последний шагнутый кадр ввода игрока с сущностью <paramref name="entityId"/> (идёт в снимок как
        /// <c>AckFrame</c>); нет такого игрока или кадров ещё не было — false.
        /// </summary>
        public bool TryGetLastFrame(int entityId, out uint frame)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                if (p.State == PlayerState.InGame && p.EntityId == entityId && p.HasApplied)
                {
                    frame = p.LastApplied;
                    return true;
                }
            }

            frame = 0;
            return false;
        }

        /// <summary>Кадров ввода игрока с сущностью <paramref name="entityId"/> пришло и ждёт шага (очередь); нет игрока — −1.</summary>
        public int BacklogOf(int entityId)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                if (p.State == PlayerState.InGame && p.EntityId == entityId)
                {
                    return p.HasNext && p.HasFrame ? Math.Max(0, (int)(p.Newest - p.NextFrame) + 1) : 0;
                }
            }

            return -1;
        }

        /// <summary>До такта: события транспорта, вход, ввод; стоп тем, от кого ввода нет 0,2 с; кадры ввода на такт.</summary>
        public void Poll(long nowMicros)
        {
            _nowMicros = nowMicros;
            _transport.Poll(nowMicros, this);
            DropSilentConnections();
            StopStaleInputs();
            PlanFrames();
        }

        /// <summary>
        /// Кадры ввода на ближайший такт каждому игроку (0-019): пришедшие по порядку номеров, не больше запаса; дыра — ждёт,
        /// потом перескок; кадров нет дольше W — сервер шагает сам с последним вводом (до стопа <see cref="InputStaleTicks"/>).
        /// </summary>
        private void PlanFrames()
        {
            long tick = _sim.Tick;
            for (int pi = 0; pi < _players.Count; pi++)
            {
                Player p = _players[pi];
                if (p.State != PlayerState.InGame)
                {
                    continue;
                }

                int slot = _sim.SlotOf(p.EntityId);
                if (slot < 0)
                {
                    continue;
                }

                p.Credits = Math.Min(p.Credits + MovementTick.FramesPerTick, CreditCap);
                int limit = Math.Min(p.Credits, FramesPerTickLimit);
                int n = 0;
                while (p.HasNext && n < limit)
                {
                    uint c = p.NextFrame;
                    int i = (int)(c % FrameRing);
                    if (p.RingValid[i] && p.RingFrame[i] == c)
                    {
                        p.RingValid[i] = false;
                        _frameBuf[n++] = p.RingInput[i];
                        Advance(p, p.RingInput[i]);
                        FramesApplied++;
                        p.HoleSinceTick = -1;
                        continue;
                    }

                    if (!p.HasFrame || (int)(p.Newest - c) < 0)
                    {
                        break; // кадра ещё нет — время персонажа ждёт
                    }

                    // Дыра: кадр c не пришёл, следующие — да. Ждём переставленный пакет, потом перескок к ближайшему.
                    if (p.HoleSinceTick < 0)
                    {
                        p.HoleSinceTick = tick;
                    }

                    if (tick - p.HoleSinceTick < HoleWaitTicks)
                    {
                        break;
                    }

                    uint next = c + 1;
                    while ((int)(p.Newest - next) > 0 && !(p.RingValid[next % FrameRing] && p.RingFrame[next % FrameRing] == next))
                    {
                        next++;
                    }

                    FramesSkipped += next - c;
                    p.NextFrame = next;
                    p.HoleSinceTick = -1;
                }

                long gap = tick - p.LastInputTick;
                if (n == 0 && p.HasApplied && !p.Stopped && gap >= SelfStepAfterTicks && gap < InputStaleTicks)
                {
                    var self = new InputFrame(p.LastInput.Direction); // 2.7 «Нет ввода»: последнее направление, кнопки отпущены
                    while (n < MovementTick.FramesPerTick && n < limit)
                    {
                        _frameBuf[n++] = self;
                        Advance(p, self);
                        FramesSelfStepped++;
                    }
                }

                // Очередь не копится (проверка 0-019, з. 1): запас +2 за такт при непустой очереди уходит весь, и всё сверх
                // догона осталось бы навсегда (задержка-пачка, рывок клиента, дрейф часов) — время персонажа отстало бы
                // больше W, а стоп 0-015 растянулся бы. После плана такта ждущих кадров больше MaxBacklogFrames — старейшие
                // перескакиваются со счётчиком. Ускорения это не даёт: кадры только выбрасываются.
                if (p.HasNext && p.HasFrame && p.HoleSinceTick < 0 && (int)(p.Newest - p.NextFrame) >= MaxBacklogFrames)
                {
                    TrimTo(p, p.Newest + 1 - MaxBacklogFrames);
                }

                if (n > MovementTick.FramesPerTick)
                {
                    BurstTicks++;
                }

                p.Credits -= n;
                _sim.SetFramesAt(slot, new ReadOnlySpan<InputFrame>(_frameBuf, 0, n));
            }
        }

        /// <summary>Выбросить ждущие кадры до <paramref name="next"/> (не шагать): время персонажа — к нему.</summary>
        private void TrimTo(Player p, uint next)
        {
            for (uint f = p.NextFrame; (int)(next - f) > 0; f++)
            {
                int i = (int)(f % FrameRing);
                if (p.RingValid[i] && p.RingFrame[i] == f)
                {
                    p.RingValid[i] = false;
                }

                FramesTrimmed++;
            }

            p.NextFrame = next;
            p.HoleSinceTick = -1;
        }

        private static void Advance(Player p, InputFrame input)
        {
            p.LastApplied = p.NextFrame;
            p.HasApplied = true;
            p.LastInput = input;
            p.NextFrame++;
        }

        /// <summary>Тайм-аут входа: соединение без входа дольше <see cref="LoginTimeoutTicks"/> тактов — закрыть (место не держит).</summary>
        private void DropSilentConnections()
        {
            long tick = _sim.Tick;
            for (int i = _players.Count - 1; i >= 0; i--)
            {
                Player p = _players[i];
                if (p.State == PlayerState.AwaitingLogin && tick - p.ConnectedTick >= LoginTimeoutTicks)
                {
                    _players.RemoveAt(i);
                    LoginTimeouts++;
                    Log?.Invoke(NetLogLevel.Warn, $"соединение {p.Peer}: нет входа {LoginTimeoutTicks / Simulation.TickRate} с — закрыто");
                    _transport.Disconnect(p.Peer, ReadOnlySpan<byte>.Empty);
                }
            }
        }

        /// <summary>
        /// 2.13 п. 6: от игрока нет нового кадра <see cref="InputStaleTicks"/> тактов — пустой ввод на ближайший такт.
        /// До первого кадра после входа ввод и так пустой — стопа нет и в <see cref="InputStops"/> он не считается.
        /// Зовётся после приёма пакетов такта: если сервер сам стоял (заморозка, п. 5), пакеты, пришедшие за это
        /// время, уже приняты и стопа не будет.
        /// </summary>
        private void StopStaleInputs()
        {
            long tick = _sim.Tick;
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                if (p.State != PlayerState.InGame || !p.HasFrame || p.Stopped || tick - p.LastInputTick < InputStaleTicks)
                {
                    continue;
                }

                p.Stopped = true;
                InputStops++;
                if (p.HasNext && (int)(p.Newest - p.NextFrame) >= 0)
                {
                    TrimTo(p, p.Newest + 1); // стоп 2.13 п. 6: ждавшие кадры не дошагиваются после стопа
                }
                _sim.SetInput(p.EntityId, InputFrame.None);
                p.LastInput = InputFrame.None;
            }
        }

        /// <summary>
        /// После такта: снимок каждому игроку в игре (0-014, 4.3). База — снимок, который клиент подтвердил
        /// (<c>LastSnapshot</c> ввода) и который ещё лежит в кольце «что клиент имеет» (32 такта). Есть база —
        /// дельта к ней; нет (вход, потеря больше секунды) — полный. Плюс контрольный полный раз в
        /// <see cref="KeyframePeriod"/> тактов и полный вместо дельты, если дельта не меньше полного (все вокруг бегут).
        /// </summary>
        public void SendSnapshots()
        {
            BroadcastPatches(); // правки такта — до окон: вошедшие в окно чанки уйдут уже с ревизией после такта (3.7)
            ReadOnlySpan<SimEntity> entities = _sim.Entities;
            uint tick = (uint)_sim.Tick;
            for (int p = 0; p < _players.Count; p++)
            {
                Player pl = _players[p];
                if (pl.State != PlayerState.InGame)
                {
                    continue;
                }

                int selfSlot = _sim.SlotOf(pl.EntityId);
                if (selfSlot < 0)
                {
                    continue;
                }

                Mover me = entities[selfSlot].Mover;
                _self = new SelfState(me.Position, me.Carry); // подтверждённое состояние без кванта (2.6 п. 2; 0-019)
                int curCount = BuildView(pl, entities, selfSlot, _cur);
                SentRing ring = pl.Ring ??= new SentRing();
                int bi = ring.Find(pl.LastSnapshotAck);
                bool keyframe = KeyframePeriod <= 1 || tick % KeyframePeriod == 0;
                uint age = tick - pl.LastSnapshotAck;

                int len = -1;
                bool truncated = false;
                if (bi >= 0 && !keyframe && age >= 1 && age <= SnapshotDeltaMessage.MaxBaselineAge)
                {
                    len = TryWriteDelta(tick, pl, ring.Ents[bi], ring.Count[bi], curCount, out truncated, out int mergedCount);
                    if (len > 0)
                    {
                        ring.Push(tick, _merged, mergedCount);
                        DeltasSent++;
                        DeltaBytes += len;
                        if (len > MaxDeltaBytes)
                        {
                            MaxDeltaBytes = len;
                        }
                    }
                }

                if (len < 0)
                {
                    len = WriteFull(tick, pl, curCount, out int written);
                    truncated = written < curCount;
                    ring.Push(tick, _cur, written); // у клиента будет ровно то, что влезло
                    FullSent++;
                    if (len > MaxSnapshotBytes)
                    {
                        MaxSnapshotBytes = len;
                    }
                }

                if (truncated)
                {
                    SnapshotsTruncated++;
                    if (SnapshotsTruncated == 1 || SnapshotsTruncated % 300 == 0)
                    {
                        Log?.Invoke(NetLogLevel.Error, $"снимок для #{pl.EntityId} больше {NetSettings.SnapshotBudget} байт: обрезан (всего обрезано {SnapshotsTruncated})");
                    }
                }

                _transport.Send(pl.Peer, NetChannel.Unreliable, new ReadOnlySpan<byte>(_buf, 0, len));
                SnapshotsSent++;
                SnapshotBytes += len;
            }

            _transport.Flush(); // снимки такта — сразу, а не по таймеру транспорта (0-015)
        }

        /// <summary>
        /// Дельта к базе в <see cref="_buf"/>; состояние клиента после неё — в <see cref="_merged"/>. Дельта не меньше
        /// полного — −1 (вызывающий шлёт полный). Сервер читает свою же дельту и склеивает её той же
        /// <see cref="SnapshotDeltaMessage.ApplyTo"/>, что и клиент, — номера сущностей в базе совпадают по построению.
        /// <para>
        /// Бюджет 1200 (4.3; 0-022 — ближние первыми): исчезнувшие (≤ 255 байт) влезают всегда; изменившиеся и новые
        /// берутся по порядку обзора — свой, затем ближние — пока влезают; первая не влезшая и все дальше неё не
        /// пишутся (не влезшее изменение клиент оставит из базы, не влезшую новую дошлёт следующая дельта).
        /// </para>
        /// </summary>
        private int TryWriteDelta(uint tick, Player pl, SnapshotEntity[] baseEnts, int baseCount, int curCount,
                                  out bool truncated, out int mergedCount)
        {
            truncated = false;
            mergedCount = 0;
            for (int ci = 0; ci < curCount; ci++)
            {
                _baseOf[ci] = -1;
            }

            int removedN = 0;
            int bits = SnapshotDeltaMessage.HeaderBytes * 8 + _layout.SelfBits;
            for (int j = 0; j < baseCount; j++)
            {
                SnapshotEntity b = baseEnts[j];
                int ci = IndexIn(_cur, curCount, b.Id);
                if (ci < 0 || _cur[ci].Kind != b.Kind)
                {
                    _removedIdx[removedN++] = j; // ушла из обзора или сменила вид (тогда придёт новой)
                    bits += SnapshotDeltaMessage.IndexBits;
                    continue;
                }

                _baseOf[ci] = j;
            }

            // Записи по порядку обзора: размер всей дельты (сравнить с полным) и сколько влезает в бюджет.
            int limitBits = NetSettings.SnapshotBudget * 8;
            int totalBits = bits;
            int updatedN = 0;
            int addedN = 0;
            for (int ci = 0; ci < curCount; ci++)
            {
                int j = _baseOf[ci];
                int rec;
                DeltaFields f = DeltaFields.None;
                if (j >= 0)
                {
                    SnapshotEntity b = baseEnts[j];
                    if (_cur[ci].Position != b.Position)
                    {
                        f |= DeltaFields.Position;
                    }

                    if (_cur[ci].Direction != b.Direction)
                    {
                        f |= DeltaFields.Direction;
                    }

                    if (f == DeltaFields.None)
                    {
                        continue;
                    }

                    rec = SnapshotDeltaMessage.UpdatedBits(f, _layout);
                }
                else
                {
                    rec = _layout.EntityBits;
                }

                totalBits += rec;
                if (truncated || bits + rec > limitBits)
                {
                    truncated = true; // эта и все дальше — в следующую дельту
                    continue;
                }

                bits += rec;
                if (j >= 0)
                {
                    _updBase[updatedN] = j;
                    _updCur[updatedN] = ci;
                    _updFields[updatedN] = f;
                    updatedN++;
                }
                else
                {
                    _addCur[addedN++] = ci;
                }
            }

            if ((totalBits + 7) / 8 >= _layout.SizeFor(curCount))
            {
                truncated = false;
                return -1;
            }

            var w = new SnapshotDeltaWriter(_buf, _layout, tick, pl.LastApplied, pl.LastSnapshotAck, _self);
            w.SetClockEpoch(ClockEpoch);
            for (int i = 0; i < removedN; i++)
            {
                w.TryAddRemoved(_removedIdx[i]);
            }

            for (int i = 0; i < updatedN; i++)
            {
                SnapshotEntity e = _cur[_updCur[i]];
                truncated |= !w.TryAddUpdated(_updBase[i], _updFields[i], e.Position, e.Direction);
            }

            for (int i = 0; i < addedN; i++)
            {
                SnapshotEntity e = _cur[_addCur[i]];
                truncated |= !w.TryAddAdded(e.Id, e.Kind, e.Position, e.Direction);
            }

            int len = w.Finish();
            _delta.Read(new ReadOnlySpan<byte>(_buf, 0, len), _layout);
            mergedCount = _delta.ApplyTo(baseEnts, baseCount, _merged);
            return len;
        }

        /// <summary>Полный снимок из <see cref="_cur"/>; возвращает длину, <paramref name="written"/> — сколько влезло.</summary>
        private int WriteFull(uint tick, Player pl, int curCount, out int written)
        {
            var w = new SnapshotWriter(_buf, _layout, tick, pl.LastApplied, _self);
            w.SetClockEpoch(ClockEpoch);
            for (int i = 0; i < curCount; i++)
            {
                if (!w.TryAdd(_cur[i].Id, _cur[i].Kind, _cur[i].Position, _cur[i].Direction))
                {
                    break;
                }
            }

            written = w.Count;
            return w.Finish();
        }

        /// <summary>
        /// Обзор игрока (docs/07, 4.4; 0-022) в <paramref name="cur"/>: свой — первым; позиции квантованы; число сущностей.
        /// Не влезает в снимок (больше <see cref="Snapshot.MaxEntities"/> или 1200 байт) — остальные ближние первыми
        /// (<see cref="Torus.Dist2"/> до своего), дальние отбрасывает обрезка (4.3: приоритет с копилкой — «потом»).
        /// <list type="bullet">
        /// <item>Окно — <see cref="ChunkWindow.Update"/>, как окно чанков клиента (3.5): пересчёт только когда сменился центр
        /// блока окна (персонаж перешёл к другой ближайшей границе чанков), иначе окно то же.</item>
        /// <item>Сущности — из сетки по чанкам (<see cref="Simulation.Grid"/>) блока выхода 8 × 6: входит сущность, чей чанк
        /// в окне; уже бывшая в обзоре остаётся, пока её чанк в блоке 8 × 6 (гистерезис «как у чанков»).</item>
        /// </list>
        /// </summary>
        private int BuildView(Player pl, ReadOnlySpan<SimEntity> entities, int selfSlot, SnapshotEntity[] cur)
        {
            MapShape shape = _sim.Shape;
            WorldPos self = entities[selfSlot].Mover.Position;
            ViewState v = pl.View ??= new ViewState();
            ChunkCoord center = ChunkWindow.BlockCenter(shape, self);
            if (!v.HasWindow || center != v.Center)
            {
                int wn = ChunkWindow.Update(shape, self, new ReadOnlySpan<ChunkCoord>(v.Window, 0, v.WindowCount), v.WindowNext);
                (v.Window, v.WindowNext) = (v.WindowNext, v.Window);
                v.WindowCount = wn;
                v.KeepCount = ChunkWindow.KeepBlock(shape, center, v.Keep);
                var window = new ReadOnlySpan<ChunkCoord>(v.Window, 0, v.WindowCount);
                for (int k = 0; k < v.KeepCount; k++)
                {
                    v.KeepInWindow[k] = ChunkSets.Contains(window, v.Keep[k]);
                }

                v.Center = center;
                v.HasWindow = true;
                ViewWindowRecalcs++;
                SyncChunks(pl, v);
            }

            EntityChunkGrid grid = _sim.Grid;
            int nc = 0;
            for (int k = 0; k < v.KeepCount; k++)
            {
                ChunkCoord c = v.Keep[k];
                int slot = grid.First(c);
                if (slot < 0)
                {
                    continue;
                }

                bool inWindow = v.KeepInWindow[k];
                for (; slot >= 0; slot = grid.Next(slot))
                {
                    if (slot == selfSlot)
                    {
                        continue;
                    }

                    if (!inWindow)
                    {
                        if (!v.IdsSorted)
                        {
                            Array.Sort(v.Ids, 0, v.IdCount);
                            v.IdsSorted = true;
                        }

                        if (Array.BinarySearch(v.Ids, 0, v.IdCount, entities[slot].Id) < 0)
                        {
                            continue; // вне окна и не была в обзоре — не входит
                        }

                        ViewKeptByHysteresis++;
                    }

                    _candKeys[nc++] = slot;
                }
            }

            // Всё влезает в полный снимок (а дельта шлётся, только если она меньше полного) — обрезки не будет, порядок
            // не важен: остаётся порядок обхода (чанки блока по рядам, в чанке — по номеру). Иначе — ближние первыми.
            if (nc + 1 > Snapshot.MaxEntities || _layout.SizeFor(nc + 1) > NetSettings.SnapshotBudget)
            {
                for (int i = 0; i < nc; i++)
                {
                    long d2 = Torus.Dist2(shape, self, entities[(int)_candKeys[i]].Mover.Position);
                    _candKeys[i] = (d2 << CandSlotBits) | _candKeys[i];
                }

                Array.Sort(_candKeys, 0, nc);
                ViewsSortedByDistance++;
            }

            int n = 0;
            cur[n++] = Quantize(entities[selfSlot]);
            for (int i = 0; i < nc && n < Snapshot.MaxEntities; i++)
            {
                cur[n++] = Quantize(entities[(int)(_candKeys[i] & ((1L << CandSlotBits) - 1))]);
            }

            v.IdCount = n - 1;
            for (int i = 1; i < n; i++)
            {
                v.Ids[i - 1] = cur[i].Id;
            }

            v.IdsSorted = false;
            return n;
        }

        private static SnapshotEntity Quantize(in SimEntity e) => new SnapshotEntity
        {
            Id = e.Id,
            Kind = e.Kind,
            Position = SnapshotLayout.Quantize(e.Mover.Position),
            Direction = e.Input.DirectionCode,
        };

        private static int IndexIn(SnapshotEntity[] ents, int count, int id)
        {
            for (int i = 0; i < count; i++)
            {
                if (ents[i].Id == id)
                {
                    return i;
                }
            }

            return -1;
        }

        // ---------- Мир по сети (docs/07, 3.5–3.7; 0-026) ----------

        /// <summary>
        /// Окно сменилось (<see cref="BuildView"/>): чанки вне нового окна убираются из набора клиента — ему уходит центр
        /// окна (3.5: клиент выбрасывает чанки вне 8 × 6 вокруг него), затем вошедшие в окно чанки, которых у клиента нет, —
        /// свежей копией (3.7 «Вход чанка в окно»). Всё — канал 1, по порядку с пачками правок.
        /// </summary>
        private void SyncChunks(Player pl, ViewState v)
        {
            HeldChunks held = pl.Held ??= new HeldChunks();
            var window = new ReadOnlySpan<ChunkCoord>(v.Window, 0, v.WindowCount);
            if (held.RetainIn(window) > 0)
            {
                int len = new ChunkWindowMessage { Map = ChunkWire.SurfaceMap, Center = v.Center }.Write(_worldBuf);
                _transport.Send(pl.Peer, NetChannel.World, new ReadOnlySpan<byte>(_worldBuf, 0, len));
                WindowMessagesSent++;
            }

            for (int i = 0; i < window.Length; i++)
            {
                if (held.IndexOf(window[i]) < 0)
                {
                    held.Add(window[i], SendChunk(pl, window[i]));
                }
            }
        }

        /// <summary>
        /// Чанк целиком — свежей копией (3.3, 3.6): копия сжимается, только если её ревизия устарела, — один раз на ревизию,
        /// сколько бы клиентов чанк ни получили. Возвращает отправленную ревизию.
        /// </summary>
        private uint SendChunk(Player pl, ChunkCoord c)
        {
            Chunk chunk = _sim.World.GetOrCreateChunk(c);
            if (!chunk.HasFreshCopy)
            {
                chunk.RebuildCompressed();
                ChunkCompressions++;
            }

            int len = ChunkMessage.Write(_worldBuf, ChunkWire.SurfaceMap, c, chunk.Revision, chunk.FreshCopy);
            _transport.Send(pl.Peer, NetChannel.World, new ReadOnlySpan<byte>(_worldBuf, 0, len));
            ChunksSent++;
            ChunkBytes += len;
            return chunk.Revision;
        }

        /// <summary>
        /// Правки такта (3.7 «Рассылка»): по событиям такта — одна пачка на изменённый чанк, каждому, у кого чанк в окне.
        /// У клиента ревизия ровно предыдущая — пачка; уже эта — ничего; иначе (без ошибки не бывает) — чанк целиком.
        /// Пачка пишется из содержимого чанка по индексам принятых правок — <c>Simulation</c> только читается.
        /// </summary>
        private void BroadcastPatches()
        {
            ReadOnlySpan<SimEvent> events = _sim.Events;
            int n = 0;
            ChunkCoord current = default;
            for (int e = 0; e < events.Length; e++)
            {
                SimEvent ev = events[e];
                if (ev.Kind == SimEventKind.TileEdit)
                {
                    if (ev.Chunk != current)
                    {
                        n = 0; // пачка прошлого чанка без новой ревизии (все правки — отказ)
                        current = ev.Chunk;
                    }

                    if (ev.Result == EditResult.Applied)
                    {
                        _patchIdx[n++] = (ushort)ev.TileIndex;
                    }

                    continue;
                }

                if (ev.Kind != SimEventKind.ChunkRevised)
                {
                    continue;
                }

                if (n == 0 || ev.Chunk != current)
                {
                    throw new InvalidOperationException($"Такт {ev.Tick}: ревизия чанка {ev.Chunk} без принятых правок в событиях.");
                }

                int len = -1;
                Chunk chunk = _sim.World.FindChunk(ev.Chunk)!;
                for (int p = 0; p < _players.Count; p++)
                {
                    Player pl = _players[p];
                    HeldChunks? held = pl.Held;
                    int k = held == null || pl.State != PlayerState.InGame ? -1 : held.IndexOf(ev.Chunk);
                    if (k < 0 || held!.Revisions[k] == ev.Revision)
                    {
                        continue;
                    }

                    if (held.Revisions[k] != unchecked(ev.Revision - 1))
                    {
                        held.Revisions[k] = SendChunk(pl, ev.Chunk);
                        continue;
                    }

                    if (len < 0)
                    {
                        len = ChunkPatchMessage.Write(_worldBuf, ChunkWire.SurfaceMap, ev.Chunk, ev.Revision, chunk,
                            new ReadOnlySpan<ushort>(_patchIdx, 0, n));
                    }

                    _transport.Send(pl.Peer, NetChannel.World, new ReadOnlySpan<byte>(_worldBuf, 0, len));
                    held.Revisions[k] = ev.Revision;
                    PatchesSent++;
                    PatchBytes += len;
                }

                n = 0;
            }
        }

        /// <summary>Запрос чанка целиком (3.7 «Порядок»): чанк в окне клиента — свежая копия; нет — пропуск.</summary>
        private void OnChunkRequest(Player p, ChunkRequestMessage m)
        {
            ChunkRequests++;
            HeldChunks? held = p.Held;
            int k = held?.IndexOf(m.Chunk) ?? -1;
            if (k >= 0)
            {
                held!.Revisions[k] = SendChunk(p, m.Chunk);
            }
        }

        /// <summary>
        /// «Часы после фриза» (2.4): сервер отстал больше чем на 3 такта и не догоняет — «часы: кадр N» всем в игре по каналу 2,
        /// эпоха часов +1 (в заголовке каждого следующего снимка). N — такт, который идёт (последний шагнутый + 1): зовётся
        /// между тактами, как и разбор пакетов.
        /// </summary>
        public void BroadcastClock()
        {
            ClockEpoch++;
            int len = new ClockMessage { ServerTick = (uint)_sim.Tick + 1 }.Write(_buf);
            for (int p = 0; p < _players.Count; p++)
            {
                if (_players[p].State == PlayerState.InGame)
                {
                    _transport.Send(_players[p].Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
                }
            }

            _transport.Flush();
        }

        private static bool Add(ref SnapshotWriter w, in SimEntity e) =>
            w.TryAdd(e.Id, e.Kind, e.Mover.Position, e.Input.DirectionCode);

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
                _sim.RemoveEntity(p.EntityId);
                Log?.Invoke(reason == NetDisconnectReason.Timeout ? NetLogLevel.Warn : NetLogLevel.Info,
                    $"{(p.IsHost ? "хозяин" : "игрок")} «{p.Name}» (#{p.EntityId}) ушёл: {reason}");
                if (p.IsHost)
                {
                    HostLeft?.Invoke(p.LastHeardMicros, _nowMicros);
                }
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

                if (type == MessageType.Ping && channel == NetChannel.Unreliable)
                {
                    // Часы клиента (2.4): ответ сразу, кадр — идущий такт (последний шагнутый + 1), как у «часов».
                    PingMessage ping = PingMessage.Read(data);
                    int len = new PongMessage { ClientUs = ping.ClientUs, ServerTick = (uint)_sim.Tick + 1 }.Write(_buf);
                    _transport.Send(p.Peer, NetChannel.Unreliable, new ReadOnlySpan<byte>(_buf, 0, len));
                    PongsSent++;
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

        private void OnLogin(Player p, LoginMessage m)
        {
            if (m.Version != _version)
            {
                Reject(p, LoginRejectReason.VersionMismatch, $"версия клиента {m.Version}, сервера {_version}");
                return;
            }

            if (!m.Token.IsEmpty)
            {
                if (HostToken.IsEmpty || m.Token != HostToken)
                {
                    Reject(p, LoginRejectReason.BadToken, HostToken.IsEmpty ? "у сервера нет хозяина" : "токен хозяина не тот");
                    return;
                }

                // Один токен с двух мест (7.7): новый вход вытесняет прежний — у упавшей игры хоста соединение
                // висит до тайм-аута транспорта, а вернувшийся хост входит сразу.
                for (int i = _players.Count - 1; i >= 0; i--)
                {
                    Player old = _players[i];
                    if (old != p && old.IsHost && old.State == PlayerState.InGame)
                    {
                        _players.RemoveAt(i);
                        _sim.RemoveEntity(old.EntityId);
                        int n = new ClosingMessage { Reason = ClosingReason.Replaced }.Write(_buf);
                        _transport.Disconnect(old.Peer, new ReadOnlySpan<byte>(_buf, 0, n));
                        Log?.Invoke(NetLogLevel.Warn, $"хозяин вошёл заново — прежнее соединение {old.Peer} (#{old.EntityId}) вытеснено");
                    }
                }
            }

            if (PlayersInGame >= _maxPlayers)
            {
                Reject(p, LoginRejectReason.ServerFull, $"мест {_maxPlayers}");
                return;
            }

            int id;
            try
            {
                id = _sim.AddEntity(_spawn(_spawned));
            }
            catch (InvalidOperationException)
            {
                Reject(p, LoginRejectReason.ServerFull, "нет места для сущности");
                return;
            }

            _spawned++;
            p.State = PlayerState.InGame;
            p.EntityId = id;
            p.Name = m.Name;
            p.IsHost = !m.Token.IsEmpty;
            p.LastInputTick = _sim.Tick;
            p.Credits = FramesPerTickLimit;
            LoginsAccepted++;
            int len = new LoginAcceptMessage
            {
                EntityId = id,
                ServerTick = (uint)_sim.Tick,
                MapWidth = _sim.Shape.Width,
                MapHeight = _sim.Shape.Height,
                Wraps = _sim.Shape.Wraps,
                WorldSeed = _sim.World.Seed,
                TickRate = Simulation.TickRate,
                GenerationHash = GenerationHash,
            }.Write(_buf);
            _transport.Send(p.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            Log?.Invoke(NetLogLevel.Info, $"вход{(p.IsHost ? " хозяина" : "")}: «{m.Name}» → #{id}, соединение {p.Peer}");
            if (p.IsHost)
            {
                HostJoined?.Invoke();
            }
        }

        /// <summary>«Открыть для друзей» (2.13 п. 9): только хозяин; уже открыт — тот же порт; итог — хозяину.</summary>
        private void OnOpenForFriends(Player p)
        {
            if (!p.IsHost || p.State != PlayerState.InGame)
            {
                Log?.Invoke(NetLogLevel.Warn, $"«Открыть для друзей» не от хозяина (соединение {p.Peer}) — мимо");
                return;
            }

            if (OpenedPort < 0 && OpenForFriends != null)
            {
                try
                {
                    OpenedPort = OpenForFriends();
                    Log?.Invoke(NetLogLevel.Info, $"открыто для друзей: порт {OpenedPort}");
                }
                catch (Exception e) when (e is System.Net.Sockets.SocketException || e is InvalidOperationException || e is ArgumentException)
                {
                    Log?.Invoke(NetLogLevel.Error, $"«Открыть для друзей» не вышло: {e.Message}");
                }
            }

            int len = new OpenResultMessage { Opened = OpenedPort >= 0, Port = (ushort)Math.Max(0, OpenedPort) }.Write(_buf);
            _transport.Send(p.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
        }

        private void OnInput(Player p, in InputMessage m)
        {
            // Перенумерация назад (0-025): пакет отправлен после всех принятых (подтверждает более новый снимок), а номера
            // не новее принятых — клиент переставил часы назад (фриз сервера: за 2 с он нумеровал вперёд). Обычные пакеты
            // так не бывают: номера клиента растут вместе с подтверждённым снимком. Очередь — заново от номеров пакета
            // (все 4 кадра: перед новым клиент шлёт его копии, предсказание шагает так же).
            if (p.HasFrame && (int)(m.LastSnapshot - p.LastSnapshotAck) > 0 && (int)(m.LastFrame - p.Newest) <= 0)
            {
                Array.Clear(p.RingValid, 0, FrameRing);
                p.NextFrame = m.FirstFrame;
                p.HasNext = true;
                p.Newest = m.FirstFrame - 1;
                p.HoleSinceTick = -1;
                InputRebases++;
            }

            bool isNew = !p.HasFrame || (int)(m.LastFrame - p.Newest) > 0;
            if (!p.HasNext)
            {
                p.NextFrame = m.FirstFrame;
                p.HasNext = true;
            }

            if ((int)(m.LastFrame - p.NextFrame) >= FrameRing)
            {
                // Клиент ушёл вперёд больше кольца (ускорение, долгий обрыв): ждавшие кадры — в отбой, время — к пакету.
                for (uint f = p.NextFrame; (int)(f - m.FirstFrame) < 0 && (int)(f - p.NextFrame) < FrameRing; f++)
                {
                    int i = (int)(f % FrameRing);
                    if (p.RingValid[i] && p.RingFrame[i] == f)
                    {
                        p.RingValid[i] = false;
                        FramesOverflow++;
                    }
                }

                p.NextFrame = m.FirstFrame;
                p.HoleSinceTick = -1;
            }

            // Все четыре кадра пакета — в кольцо по номеру (что уже есть — не трогаем: первый пришедший и есть кадр).
            for (int k = 0; k < InputMessage.FrameCount; k++)
            {
                uint f = m.FirstFrame + (uint)k;
                if ((int)(f - p.NextFrame) < 0)
                {
                    if (isNew && (!p.HasFrame || (int)(f - p.Newest) > 0))
                    {
                        FramesLate++; // номер уже шагнут сервером сам или перескочен
                    }

                    continue;
                }

                int i = (int)(f % FrameRing);
                if (!(p.RingValid[i] && p.RingFrame[i] == f))
                {
                    p.RingFrame[i] = f;
                    p.RingValid[i] = true;
                    p.RingInput[i] = m[k].ToInputFrame();
                }
            }

            if (!isNew)
            {
                InputsDropped++; // старый, дубль или переставленный — новых кадров нет (дыры он закрывает)
                return;
            }

            // 2.7 «Опоздавший кадр»: все кадры пакета уже шагнуты сервером сам — не выбрасывается, его ввод действует
            // с первого ещё не шагнутого кадра.
            if ((int)(m.LastFrame - p.NextFrame) < 0)
            {
                int i = (int)(p.NextFrame % FrameRing);
                if (!(p.RingValid[i] && p.RingFrame[i] == p.NextFrame))
                {
                    p.RingFrame[i] = p.NextFrame;
                    p.RingValid[i] = true;
                    p.RingInput[i] = m.Frame3.ToInputFrame();
                }
            }

            p.Newest = m.LastFrame;
            p.HasFrame = true;
            p.LastSnapshotAck = m.LastSnapshot;
            p.LastInputTick = _sim.Tick;
            p.Stopped = false;
            InputsAccepted++;
        }

        private void Reject(Player p, LoginRejectReason reason, string why)
        {
            LoginsRejected++;
            _players.Remove(p);
            int len = new LoginRejectMessage { Reason = reason, ServerVersion = _version }.Write(_buf);
            Log?.Invoke(NetLogLevel.Warn, $"вход отклонён ({reason}): {why}; соединение {p.Peer}");
            _transport.Disconnect(p.Peer, new ReadOnlySpan<byte>(_buf, 0, len));
        }

        private Player? Find(int peer)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].Peer == peer)
                {
                    return _players[i];
                }
            }

            return null;
        }
    }
}
