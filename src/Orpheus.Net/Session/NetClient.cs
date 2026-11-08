using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>Состояние клиента.</summary>
    public enum NetClientState : byte
    {
        /// <summary>Ждём «открыто» от транспорта.</summary>
        Connecting,

        /// <summary>Вход отправлен, ждём ответа.</summary>
        LoggingIn,

        /// <summary>В игре: шлём ввод, получаем снимки.</summary>
        InGame,

        /// <summary>Сервер отказал во входе (<see cref="NetClient.RejectText"/>).</summary>
        Rejected,

        /// <summary>Соединение закрыто.</summary>
        Disconnected,
    }

    /// <summary>
    /// Клиент без графики (docs/07, 11.3): подключение, вход, ввод 60 Гц пакетами по 4 кадра, приём снимков 30 Гц: полных и дельт (0-014), дельта склеивается с базой в полный.
    /// Предсказания пока нет — клиент только применяет снимки: <see cref="Latest"/> — новейший по такту, старые и
    /// дубли (перестановка, имитатор) отбрасываются. Времени внутри нет: темп задаёт вызывающий.
    /// </summary>
    public sealed partial class NetClient : ITransportHandler
    {
        private readonly ITransport _transport;
        private readonly int _serverPeer;
        private readonly string _name;
        private readonly NetVersion _version;
        private readonly byte[] _buf = new byte[NetSettings.MaxUnreliablePayload];
        private Snapshot _latest = new Snapshot();
        private Snapshot _scratch = new Snapshot();
        private readonly SnapshotDeltaMessage _delta = new SnapshotDeltaMessage();

        /// <summary>
        /// Базы дельт (0-014, 4.3): применённые снимки последней секунды, ячейка — такт mod 32. Сервер строит
        /// дельту от снимка, который клиент подтвердил (<c>LastSnapshot</c> ввода), а подтверждение отстаёт на RTT —
        /// поэтому нужна не одна база, а кольцо.
        /// </summary>
        private readonly Snapshot[] _bases = new Snapshot[BaseDepth];
        private readonly bool[] _baseValid = new bool[BaseDepth];
        private SnapshotLayout _layout;
        private bool _hasSnapshot;
        private InputMessage _input;
        private readonly ClientClock _clock = new ClientClock();
        private long _nowUs;
        private long _lastPingUs;
        private byte _epoch;
        private bool _epochKnown;

        /// <summary>Пинг часов — раз в столько мкс (2.10 <c>PingInterval</c>).</summary>
        public const long PingIntervalUs = NetSettings.PingIntervalMs * 1000L;

        /// <summary>
        /// Номер кадра расходится с оценкой часов больше чем на 6 кадров (100 мс — порог перестановки часов, 2.4) —
        /// перенумерация к оценке (<see cref="FrameRenumbers"/>); меньше — номера идут подряд, темп держит <see cref="FramesDue"/>.
        /// </summary>
        public const int RenumberFrames = 6;

        /// <summary>Кадров за один вызов <see cref="FramesDue"/> не больше 4 (пакет несёт 4 кадра).</summary>
        public const int MaxFramesDue = InputMessage.FrameCount;

        // ---------- Опережение L (0-033, docs/07 2.7) ----------

        /// <summary>
        /// Цель «запаса прихода кадров» из отчёта сервера, кадров: 1-й процентиль за 2 с не меньше 0 на сетке опроса такта
        /// (задержка прихода у сервера уже включает ожидание опроса 0–33 мс — это и есть запас «≥ 8 мс» 2.7; допущение [док]).
        /// Запас меньше — L растёт на нехватку.
        /// </summary>
        public const int LeadMarginTargetFrames = 0;

        /// <summary>Запас не меньше этого (50 мс сверх цели) 2 с подряд — L уменьшается на кадр.</summary>
        public const int LeadDownMarginFrames = 3;

        /// <summary>L меняется не быстрее кадра за 100 мс: номера идут подряд, без дыр и повторов (сервер видит лишний кадр или паузу).</summary>
        public const long LeadSlewUs = 100_000;

        /// <summary>После изменения L — не уменьшать 2 с (окно отчёта сервера).</summary>
        public const long LeadDownHoldUs = 2_000_000;

        private int _leadTarget;
        private long _leadStepUs;
        private long _leadHoldUntilUs;
        private long _leadChangedUs;

        /// <summary>Опережение L, кадров (0…24): номер кадра ввода = оценка кадра сервера + L (2.7).</summary>
        public int LeadFrames { get; private set; }

        /// <summary>Цель L по отчёту сервера, кадров; к ней L идёт по кадру за <see cref="LeadSlewUs"/>.</summary>
        public int LeadTarget => _leadTarget;

        /// <summary>Наибольшее L за сессию, кадров.</summary>
        public int MaxLeadFrames { get; private set; }

        /// <summary>Последний отчёт сервера: «запас прихода кадров», кадров.</summary>
        public int LeadMargin => _hasSnapshot ? _latest.Self.LeadMargin : 0;

        /// <summary>Последний отчёт сервера: задержка показа D_p, кадров.</summary>
        public int DelayFrames => _hasSnapshot ? _latest.Self.DelayFrames : 0;

        /// <summary>Значок «высокая задержка» (2.7): D_p больше 150 мс.</summary>
        public bool HighLatency => DelayFrames > ThreatTiming.HighLatencyFrames;

        /// <summary>Номер своего кадра ввода «сейчас»: оценка кадра сервера + L (2.7, 2.9 «свой персонаж»).</summary>
        public uint OwnFrameAt(long nowMicros) => unchecked(_clock.FrameAt(nowMicros) + (uint)LeadFrames);

        /// <summary>Глубина кольца баз: 32 снимка ≈ 1 с (как у сервера, 4.3).</summary>
        public const int BaseDepth = 32;

        public NetClient(ITransport transport, int serverPeer, string name, NetVersion version)
        {
            for (int i = 0; i < BaseDepth; i++)
            {
                _bases[i] = new Snapshot();
            }

            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _serverPeer = serverPeer;
            _name = name;
            _version = version;
        }

        public NetClientState State { get; private set; } = NetClientState.Connecting;

        /// <summary>
        /// Мир клиента — чанки окна от сервера (docs/07, 3.4–3.7; 0-026); создаётся по «вход принят». Генератора и зерна
        /// у клиента нет: тайлы — только отсюда.
        /// </summary>
        public ClientWorld? World { get; private set; }

        // Сообщения мира, обогнавшие «вход принят» (канал 1 против канала 2): разбираются сразу после него.
        private readonly List<byte[]> _early = new List<byte[]>();

        /// <summary>Сообщений мира, ждущих «вход принят», не больше: окно входа — 24 чанка и центр (0-026).</summary>
        public const int MaxEarlyWorldMessages = 64;

        /// <summary>Транспорт клиента.</summary>
        public ITransport Transport => _transport;

        /// <summary>Номер соединения с сервером в транспорте.</summary>
        public int ServerPeer => _serverPeer;

        /// <summary>Ответ на вход (в игре).</summary>
        public LoginAcceptMessage Accept { get; private set; }

        /// <summary>Своя сущность; не в игре — 0.</summary>
        public int EntityId => Accept.EntityId;

        /// <summary>Форма карты из входа.</summary>
        public MapShape Shape => _layout.Shape;

        /// <summary>Отказ входа: текст для игрока.</summary>
        public string? RejectText { get; private set; }

        /// <summary>Отказ входа: сообщение сервера.</summary>
        public LoginRejectMessage? Reject { get; private set; }

        /// <summary>Почему закрыто соединение.</summary>
        public NetDisconnectReason? DisconnectReason { get; private set; }

        /// <summary>Новейший снимок (по такту). До первого — <see cref="HasSnapshot"/> = false.</summary>
        public Snapshot Latest => _latest;

        public bool HasSnapshot => _hasSnapshot;

        /// <summary>Такт сервера: из новейшего снимка или часов.</summary>
        public uint ServerTick { get; private set; }

        /// <summary>Номер текущего кадра ввода (последнего отправленного).</summary>
        public uint Frame => _input.LastFrame;

        /// <summary>Часы клиента: оценка кадра сервера (2.4; 0-025).</summary>
        public ClientClock Clock => _clock;

        /// <summary>Перенумераций кадров ввода к оценке часов (перестановка часов больше 100 мс).</summary>
        public long FrameRenumbers { get; private set; }

        /// <summary>Снимков с новой эпохой часов («часы после фриза» в заголовке, 2.4).</summary>
        public long EpochResets { get; private set; }

        /// <summary>Пингов отправлено / ответов принято.</summary>
        public long PingsSent { get; private set; }

        /// <inheritdoc cref="PingsSent"/>
        public long PongsReceived { get; private set; }

        /// <summary>
        /// «Часы после фриза» пришли (сообщение «часы» или новая эпоха в снимке) — до <see cref="SnapshotApplied"/> этого снимка.
        /// Часы показа чужих (RemoteInterpolation) сбрасывают по нему окно медианы.
        /// </summary>
        public Action? ClockReset { get; set; }

        /// <summary>Снимков принято (новее прежнего) / отброшено (старее или дубль).</summary>
        public long SnapshotsApplied { get; private set; }

        /// <inheritdoc cref="SnapshotsApplied"/>
        public long SnapshotsStale { get; private set; }

        /// <summary>Пропущено тактов между принятыми снимками (потери).</summary>
        public long SnapshotsMissed { get; private set; }

        /// <summary>Дельт применено (0-014).</summary>
        public long DeltasApplied { get; private set; }

        /// <summary>
        /// Дельт отброшено: базы <c>BaselineTick</c> нет в кольце (старше секунды или не дошла). Клиент продолжает
        /// подтверждать последний применённый снимок — сервер построит следующую дельту от него или пришлёт полный.
        /// </summary>
        public long DeltasUnmerged { get; private set; }

        /// <summary>Испорченных сообщений.</summary>
        public long BadMessages { get; private set; }

        /// <summary>Последняя отвергнутая схема (диагностика в тестах и логах).</summary>
        public string? LastBadMessage { get; private set; }

        /// <summary>Часов принято.</summary>
        public long ClocksReceived { get; private set; }

        /// <summary>
        /// «Часов» пропущено как опоздавших: их такт не новее принятого снимка (снимок с новой эпохой пришёл раньше них —
        /// часы уже переставлены по нему).
        /// </summary>
        public long ClocksStale { get; private set; }

        /// <summary>Пакетов ввода отправлено.</summary>
        public long InputsSent { get; private set; }

        /// <summary>Каждый принятый (более новый) снимок — после того, как стал <see cref="Latest"/>.</summary>
        public Action<Snapshot>? SnapshotApplied { get; set; }

        // ---------- Хозяин и закрытие мира (0-024) ----------

        /// <summary>Токен хозяина для входа (до подключения): игра, запустившая сервер, входит хозяином.</summary>
        public HostToken HostToken { get; set; }

        /// <summary>«Замах начат» (0-029): каждое пришедшее сообщение, с повторами — отсеивает получатель (атакующий + s).</summary>
        public Action<SwingStartMessage>? SwingStarted { get; set; }

        /// <summary>«Удар решён» по своему персонажу (0-029).</summary>
        public Action<MeleeResultMessage>? MeleeResolved { get; set; }

        /// <summary>Сообщений «замах начат» принято (с повторами).</summary>
        public long SwingMessages { get; private set; }

        /// <summary>Сообщений «удар решён» принято.</summary>
        public long MeleeResults { get; private set; }

        /// <summary>Ответ на «Открыть для друзей»; не было — null.</summary>
        public OpenResultMessage? OpenResult { get; private set; }

        /// <summary>Сервер закрыл мир (7.6): причина из данных отключения; не было — null.</summary>
        public ClosingReason? Closing { get; private set; }

        /// <summary>
        /// Текст для игрока после отключения (0-024): «Хост вышел. Мир сохранён у него» и т. п. по причине закрытия; без
        /// причины по тайм-ауту — «Связь с хостом потеряна» (7.6, допущение до ответа на вопрос [док] карточки 0-024).
        /// В игре — null.
        /// </summary>
        public string? DisconnectText =>
            State != NetClientState.Disconnected ? null
            : Closing.HasValue ? new ClosingMessage { Reason = Closing.Value }.Describe()
            : DisconnectReason == NetDisconnectReason.Local ? "Вы вышли"
            : "Связь с хостом потеряна";

        /// <summary>«Открыть для друзей» (2.13 п. 9): только хозяину; ответ — в <see cref="OpenResult"/>.</summary>
        public void RequestOpenForFriends()
        {
            if (State != NetClientState.InGame)
            {
                return;
            }

            int len = new OpenForFriendsMessage().Write(_buf);
            _transport.Send(_serverPeer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            _transport.Flush();
        }

        /// <summary>Разобрать пришедшее.</summary>
        public void Poll(long nowMicros)
        {
            _nowUs = nowMicros;
            _transport.Poll(nowMicros, this);
            if (!_paused)
            {
                StepLead(nowMicros); // 0-036: в паузе L не шагает
            }

            if (State == NetClientState.InGame && nowMicros - _lastPingUs >= PingIntervalUs)
            {
                _lastPingUs = nowMicros;
                int len = new PingMessage { ClientUs = unchecked((uint)nowMicros) }.Write(_buf);
                _transport.Send(_serverPeer, NetChannel.Unreliable, new ReadOnlySpan<byte>(_buf, 0, len));
                _transport.Flush();
                PingsSent++;
            }
        }

        /// <summary>
        /// Сколько кадров ввода отправить сейчас (часы ввода, 2.4 и 2.7, L = 0): номер кадра идёт за оценкой кадра сервера —
        /// разница оценки и последнего отправленного, 0…4. Расхождение больше <see cref="RenumberFrames"/> — 1: следующий
        /// <see cref="SendFrame"/> перенумерует. Не в игре — 0.
        /// </summary>
        public int FramesDue(long nowMicros)
        {
            if (State != NetClientState.InGame || !_clock.Valid)
            {
                return 0;
            }

            int d = (int)(OwnFrameAt(nowMicros) - _input.LastFrame);
            if (d > RenumberFrames || d < -RenumberFrames)
            {
                return 1;
            }

            return Math.Max(0, Math.Min(d, MaxFramesDue));
        }

        /// <summary>
        /// Новый кадр ввода (60 Гц): номер +1, пакет из трёх прошлых и текущего — по каналу 0. Не в игре — ничего.
        /// </summary>
        public void SendFrame(InputFrame frame)
        {
            if (State != NetClientState.InGame)
            {
                return;
            }

            uint target = OwnFrameAt(_nowUs);
            int d = (int)(target - (_input.LastFrame + 1));
            if (InputsSent > 0 && _clock.Valid && (d > RenumberFrames || d < -RenumberFrames))
            {
                // Перестановка часов (фриз сервера, скачок): номер — по оценке. Прошлые три кадра пакета — копии нового:
                // сервер не шагнёт под новыми номерами старый ввод, предсказание (LocalPrediction.Sent) делает так же.
                WireFrame w = WireFrame.From(frame);
                _input.Frame0 = w;
                _input.Frame1 = w;
                _input.Frame2 = w;
                _input.Frame3 = w;
                _input.FirstFrame = target - (InputMessage.FrameCount - 1);
                FrameRenumbers++;
            }
            else
            {
                _input.Frame0 = _input.Frame1;
                _input.Frame1 = _input.Frame2;
                _input.Frame2 = _input.Frame3;
                _input.Frame3 = WireFrame.From(frame);
                _input.FirstFrame++;
            }
            _input.LastSnapshot = _hasSnapshot ? _latest.Tick : 0;
            _input.PictureTime = _input.LastSnapshot * 2;
            _input.Lead = (sbyte)LeadFrames;
            int len = _input.Write(_buf);
            _transport.Send(_serverPeer, NetChannel.Unreliable, new ReadOnlySpan<byte>(_buf, 0, len));
            _transport.Flush(); // кадр ввода — сразу: задержка ввода — бюджет уворота (2.13 п. 3; 0-015)
            InputsSent++;
            OnFrameSent(frame); // 0-028: прогресс копания своим счётом
        }

        /// <summary>Выйти.</summary>
        public void Disconnect() => _transport.Disconnect(_serverPeer, ReadOnlySpan<byte>.Empty);

        // ---------- События транспорта ----------

        void ITransportHandler.OnConnected(int peer)
        {
            if (peer != _serverPeer || State != NetClientState.Connecting)
            {
                return;
            }

            int len = new LoginMessage { Version = _version, Name = _name, Token = HostToken }.Write(_buf);
            _transport.Send(_serverPeer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            State = NetClientState.LoggingIn;
        }

        void ITransportHandler.OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload)
        {
            if (peer != _serverPeer)
            {
                return;
            }

            DisconnectReason = reason;
            if (payload.Length > 0 && payload[0] == (byte)MessageType.Closing)
            {
                try
                {
                    Closing = ClosingMessage.Read(payload).Reason;
                }
                catch (ProtocolException)
                {
                    BadMessages++;
                }
            }
            else if (State == NetClientState.LoggingIn && payload.Length > 0)
            {
                try
                {
                    LoginRejectMessage r = LoginRejectMessage.Read(payload);
                    Reject = r;
                    RejectText = r.Describe(_version);
                    State = NetClientState.Rejected;
                    return;
                }
                catch (ProtocolException)
                {
                    BadMessages++;
                }
            }

            State = NetClientState.Disconnected;
        }

        void ITransportHandler.OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
        {
            if (peer != _serverPeer)
            {
                return;
            }

            try
            {
                switch (Codec.TypeOf(data))
                {
                    case MessageType.LoginAccept when State == NetClientState.LoggingIn:
                        OnAccept(LoginAcceptMessage.Read(data));
                        break;
                    case MessageType.Snapshot when State == NetClientState.InGame:
                        OnSnapshot(data);
                        break;
                    case MessageType.SnapshotDelta when State == NetClientState.InGame:
                        OnDelta(data);
                        break;
                    case MessageType.Clock when State == NetClientState.InGame:
                        ClockMessage c = ClockMessage.Read(data);
                        ClocksReceived++;
                        if (_hasSnapshot && (int)(c.ServerTick - _latest.Tick) <= 0)
                        {
                            // Опоздавшие «часы» (переотправка по каналу 2 после потери): уже принят снимок этого такта или новее —
                            // он отправлен после «часов» и несёт новую эпоху, часы переставлены по нему. Применить — увести
                            // оценку назад на задержку переотправки (проверка 0-025 з. 3).
                            ClocksStale++;
                            break;
                        }

                        _clock.OnServerReset(c.ServerTick, _nowUs); // 2.4 «часы после фриза»: сразу
                        ClockReset?.Invoke();
                        break;
                    case MessageType.Pong when State == NetClientState.InGame:
                        PongMessage pong = PongMessage.Read(data);
                        PongsReceived++;
                        _clock.OnPong(pong.ServerTick, unchecked((uint)_nowUs - pong.ClientUs), _nowUs);
                        break;
                    case MessageType.Pong:
                        break;
                    case MessageType.SwingStart when State == NetClientState.InGame:
                        SwingMessages++;
                        SwingStarted?.Invoke(SwingStartMessage.Read(data));
                        break;
                    case MessageType.MeleeResult when State == NetClientState.InGame:
                        MeleeResults++;
                        MeleeResolved?.Invoke(MeleeResultMessage.Read(data));
                        break;
                    case MessageType.TileReply when State == NetClientState.InGame:
                        OnTileReply(TileReplyMessage.Read(data)); // 0-028
                        break;
                    case MessageType.PauseState when State == NetClientState.InGame:
                        OnPauseState(PauseStateMessage.Read(data)); // 0-036
                        break;
                    case MessageType.OpenResult when State == NetClientState.InGame:
                        OpenResult = OpenResultMessage.Read(data);
                        break;
                    case MessageType.Chunk:
                    case MessageType.ChunkPatch:
                    case MessageType.ChunkWindow:
                        OnWorld(channel, data);
                        break;
                    case MessageType.Snapshot:
                        break; // снимок обогнал «вход принят» (другой канал) — пропускаем
                    case MessageType.SwingStart:
                        break; // замах обогнал «вход принят» — так же
                    default:
                        BadMessages++;
                        LastBadMessage = $"тип {data[0]} в состоянии {State}";
                        break;
                }
            }
            catch (ProtocolException e)
            {
                BadMessages++;
                LastBadMessage = e.Message;
            }
        }

        private void OnAccept(in LoginAcceptMessage m)
        {
            Accept = m;
            _layout = new SnapshotLayout(new MapShape(m.MapWidth, m.MapHeight, m.Wraps));
            ServerTick = m.ServerTick;

            // Кадры 60 Гц: два на такт; первый пакет несёт кадры «до входа» — пустые.
            _clock.SeedRtt(_transport.RoundTripMs(_serverPeer) * 1000L);
            _clock.Start(m.ServerTick + 1, _nowUs); // вход отправлен в разборе такта ServerTick + 1 (идущего)
            _input = default;
            LeadFrames = 0;
            _leadTarget = 0;
            _leadHoldUntilUs = 0;
            _leadChangedUs = _nowUs;
            _input.FirstFrame = OwnFrameAt(_nowUs) - InputMessage.FrameCount; // первый кадр — по оценке
            _lastPingUs = long.MinValue / 2; // первый пинг — сразу: RTT для часов
            _epochKnown = false;
            _hasSnapshot = false;
            Array.Clear(_baseValid, 0, BaseDepth);
            State = NetClientState.InGame;
            World = new ClientWorld(_layout.Shape, RequestChunk);
            for (int i = 0; i < _early.Count; i++)
            {
                OnWorld(NetChannel.World, _early[i]);
            }

            _early.Clear();
        }

        private void OnWorld(NetChannel channel, ReadOnlySpan<byte> data)
        {
            if (channel != NetChannel.World)
            {
                throw new ProtocolException($"Сообщение мира {data[0]} по каналу {channel}.");
            }

            if (State == NetClientState.LoggingIn)
            {
                if (_early.Count == MaxEarlyWorldMessages)
                {
                    throw new ProtocolException($"До «вход принят» — больше {MaxEarlyWorldMessages} сообщений мира.");
                }

                _early.Add(data.ToArray());
                return;
            }

            if (State != NetClientState.InGame || World == null)
            {
                return;
            }

            World.Handle(Codec.TypeOf(data), data);
        }

        /// <summary>«Пришли чанк целиком» (3.7 «Порядок») — канал 1.</summary>
        private void RequestChunk(ChunkCoord c)
        {
            int len = new ChunkRequestMessage { Map = ChunkWire.SurfaceMap, Chunk = c }.Write(_buf);
            _transport.Send(_serverPeer, NetChannel.World, new ReadOnlySpan<byte>(_buf, 0, len));
        }

        private void OnSnapshot(ReadOnlySpan<byte> data)
        {
            uint tick = Snapshot.PeekTick(data);
            if (_hasSnapshot && (int)(tick - _latest.Tick) <= 0)
            {
                SnapshotsStale++;
                return;
            }

            _scratch.Read(data, _layout);
            _scratch.IsDelta = false;
            CommitSnapshot();
        }

        /// <summary>
        /// Дельта (0-014): склеить с базой из кольца (такт базы — в заголовке) в новый полный снимок; дальше всё
        /// как у полного — <see cref="Latest"/>, интерполяция и предсказание видят только полные склеенные снимки.
        /// Базы нет — отбросить (см. <see cref="DeltasUnmerged"/>).
        /// </summary>
        private void OnDelta(ReadOnlySpan<byte> data)
        {
            uint tick = Snapshot.PeekTick(data);
            if (_hasSnapshot && (int)(tick - _latest.Tick) <= 0)
            {
                SnapshotsStale++;
                return;
            }

            _delta.Read(data, _layout);
            int slot = (int)(_delta.BaselineTick % BaseDepth);
            Snapshot b = _bases[slot];
            if (!_baseValid[slot] || b.Tick != _delta.BaselineTick)
            {
                DeltasUnmerged++;
                return;
            }

            _scratch.Count = _delta.ApplyTo(b.Entities, b.Count, _scratch.Entities);
            _scratch.Tick = tick;
            _scratch.AckFrame = _delta.AckFrame;
            _scratch.Self = _delta.Self;
            _scratch.IsDelta = true;
            _scratch.ClockEpoch = _delta.ClockEpoch;
            DeltasApplied++;
            CommitSnapshot();
        }

        /// <summary>
        /// Подстройка L по отчёту сервера (2.7): запас меньше цели — цель L растёт на нехватку; запас с избытком 2 с — на кадр
        /// вниз. Пока L идёт к цели и ещё RTT + 100 мс после (сервер должен увидеть новое L) — отчёты не трогают цель.
        /// </summary>
        private void OnLeadReport(int margin, long nowUs)
        {
            if (LeadFrames != _leadTarget || nowUs < _leadHoldUntilUs)
            {
                return;
            }

            if (margin < LeadMarginTargetFrames)
            {
                SetLeadTarget(_leadTarget + (LeadMarginTargetFrames - margin), nowUs);
            }
            else if (margin >= LeadDownMarginFrames && nowUs - _leadChangedUs >= LeadDownHoldUs)
            {
                SetLeadTarget(_leadTarget - 1, nowUs);
            }
        }

        private void SetLeadTarget(int target, long nowUs)
        {
            target = Math.Max(0, Math.Min(InputMessage.MaxLeadFrames, target));
            if (target != _leadTarget)
            {
                _leadTarget = target;
                _leadChangedUs = nowUs;
            }
        }

        /// <summary>L — к цели по кадру за <see cref="LeadSlewUs"/>; дошло — пауза отчётов на RTT + 100 мс.</summary>
        private void StepLead(long nowUs)
        {
            if (LeadFrames == _leadTarget || nowUs - _leadStepUs < LeadSlewUs)
            {
                return;
            }

            _leadStepUs = nowUs;
            LeadFrames += Math.Sign(_leadTarget - LeadFrames);
            MaxLeadFrames = Math.Max(MaxLeadFrames, LeadFrames);
            if (LeadFrames == _leadTarget)
            {
                _leadHoldUntilUs = nowUs + 2 * _clock.OwdUs + 100_000;
                _leadChangedUs = nowUs;
            }
        }

        /// <summary>Собранный в <see cref="_scratch"/> полный снимок становится <see cref="Latest"/> и базой в кольце.</summary>
        private void CommitSnapshot()
        {
            uint tick = _scratch.Tick;
            if (_hasSnapshot)
            {
                SnapshotsMissed += tick - _latest.Tick - 1;
            }

            Snapshot t = _latest;
            _latest = _scratch;
            _scratch = t;
            _hasSnapshot = true;
            int slot = (int)(tick % BaseDepth);
            _latest.CopyTo(_bases[slot]);
            _baseValid[slot] = true;
            SnapshotsApplied++;
            ResumeIfSnapshotNewer(tick); // 0-036: до образца часов — он уже на идущих часах
            if (!_paused)
            {
                OnLeadReport(_latest.Self.LeadMargin, _nowUs); // в паузе (снимок вошедшему) отчёт старый — не трогает L
            }

            if ((int)(tick - ServerTick) > 0)
            {
                ServerTick = tick;
            }

            // Часы клиента (2.4): заголовок снимка — образец; новая эпоха — «часы после фриза», перестановка сразу.
            if (_epochKnown && _latest.ClockEpoch != _epoch)
            {
                EpochResets++;
                _clock.OnServerReset(tick, _nowUs);
                ClockReset?.Invoke();
            }
            else
            {
                _clock.OnSample(tick, _nowUs);
            }

            _epoch = _latest.ClockEpoch;
            _epochKnown = true;
            SnapshotApplied?.Invoke(_latest);
        }
    }
}
