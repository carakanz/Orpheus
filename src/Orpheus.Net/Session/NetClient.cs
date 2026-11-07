using System;
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
    public sealed class NetClient : ITransportHandler
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

        /// <summary>Пакетов ввода отправлено.</summary>
        public long InputsSent { get; private set; }

        /// <summary>Каждый принятый (более новый) снимок — после того, как стал <see cref="Latest"/>.</summary>
        public Action<Snapshot>? SnapshotApplied { get; set; }

        /// <summary>Разобрать пришедшее.</summary>
        public void Poll(long nowMicros) => _transport.Poll(nowMicros, this);

        /// <summary>
        /// Новый кадр ввода (60 Гц): номер +1, пакет из трёх прошлых и текущего — по каналу 0. Не в игре — ничего.
        /// </summary>
        public void SendFrame(InputFrame frame)
        {
            if (State != NetClientState.InGame)
            {
                return;
            }

            _input.Frame0 = _input.Frame1;
            _input.Frame1 = _input.Frame2;
            _input.Frame2 = _input.Frame3;
            _input.Frame3 = WireFrame.From(frame);
            _input.FirstFrame++;
            _input.LastSnapshot = _hasSnapshot ? _latest.Tick : 0;
            _input.PictureTime = _input.LastSnapshot * 2;
            _input.Lead = 0;
            int len = _input.Write(_buf);
            _transport.Send(_serverPeer, NetChannel.Unreliable, new ReadOnlySpan<byte>(_buf, 0, len));
            _transport.Flush(); // кадр ввода — сразу: задержка ввода — бюджет уворота (2.13 п. 3; 0-015)
            InputsSent++;
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

            int len = new LoginMessage { Version = _version, Name = _name }.Write(_buf);
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
            if (State == NetClientState.LoggingIn && payload.Length > 0)
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
                        if ((int)(c.ServerTick - ServerTick) > 0)
                        {
                            ServerTick = c.ServerTick;
                        }

                        break;
                    case MessageType.Snapshot:
                        break; // снимок обогнал «вход принят» (другой канал) — пропускаем
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
            _input = default;
            _input.FirstFrame = m.ServerTick * 2;
            _hasSnapshot = false;
            Array.Clear(_baseValid, 0, BaseDepth);
            State = NetClientState.InGame;
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
            _scratch.IsDelta = true;
            DeltasApplied++;
            CommitSnapshot();
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
            if ((int)(tick - ServerTick) > 0)
            {
                ServerTick = tick;
            }

            SnapshotApplied?.Invoke(_latest);
        }
    }
}
