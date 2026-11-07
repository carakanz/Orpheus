using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace Orpheus.Net.Transport
{
    /// <summary>
    /// UDP через LiteNetLib 2.x (docs/07, 1.3 и 2.10) — реализация «ПК». Настройки — из <see cref="NetSettings"/>,
    /// одним местом в <see cref="Configure"/>. События разбираются только в <see cref="Poll"/> (UnsyncedEvents
    /// выключено) — в потоке такта сервера или в потоке бота.
    /// <para>
    /// Для IL2CPP: используется только <c>NetManager</c>, <c>NetPeer</c>, <c>ConnectionRequest</c> и чтение
    /// пакета как <see cref="ReadOnlySpan{T}"/>; <c>NetSerializer</c> и <c>NetPacketProcessor</c> (рефлексия) не
    /// используются; <c>ReuseAddress</c> выключен (на IL2CPP его нет); встроенная имитация задержки и потерь
    /// не используется — для этого <see cref="NetSimTransport"/>.
    /// </para>
    /// </summary>
    public sealed class LiteNetTransport : ITransport, INetEventListener
    {
        private readonly NetManager _manager;
        private readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>();
        private readonly bool _isServer;
        private readonly int _maxPeers;
        private ITransportHandler? _handler;

        private LiteNetTransport(bool isServer, int maxPeers)
        {
            _isServer = isServer;
            _maxPeers = maxPeers;
            _manager = new NetManager(this);
            Configure(_manager);
        }

        /// <summary>Номер соединения с сервером (у клиента); у сервера — −1.</summary>
        public int ServerPeer { get; private set; } = -1;

        /// <summary>Порт, на котором слушает транспорт (сервер — заданный или выданный ОС при 0).</summary>
        public int LocalPort => _manager.LocalPort;

        /// <summary>Открытых соединений.</summary>
        public int ConnectedCount => _manager.ConnectedPeersCount;

        /// <summary>Настройки LiteNetLib по таблице docs/07, 2.10 — все здесь.</summary>
        public static void Configure(NetManager m)
        {
            m.ChannelsCount = NetSettings.ChannelsCount;
            m.MtuOverride = NetSettings.Mtu;
            m.MtuDiscovery = false;
            m.DisconnectTimeout = NetSettings.DisconnectTimeoutMs;
            m.PingInterval = NetSettings.PingIntervalMs;
            m.UnsyncedEvents = false;
            m.UnsyncedReceiveEvent = false;
            m.UnsyncedDeliveryEvent = false;
            m.SimulateLatency = false;
            m.SimulatePacketLoss = false;
            m.ReuseAddress = false;
            m.AutoRecycle = true;
            m.UnconnectedMessagesEnabled = false;
            m.NatPunchEnabled = false;
            m.BroadcastReceiveEnabled = false;
        }

        /// <summary>
        /// Сервер: слушать UDP-порт <paramref name="port"/> (0 — выдаст ОС). По умолчанию — на всех адресах;
        /// <paramref name="loopbackOnly"/> — только 127.0.0.1 и ::1 (тесты, smoke, локальная игра: брандмауэр Windows
        /// не спрашивает разрешения у программы, которая слушает только петлю).
        /// </summary>
        public static LiteNetTransport Listen(int port, int maxPeers, bool loopbackOnly = false)
        {
            var t = new LiteNetTransport(true, maxPeers);
            bool started = loopbackOnly ? t._manager.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, port) : t._manager.Start(port);
            if (!started)
            {
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
            }

            return t;
        }

        /// <summary>
        /// Клиент: подключиться к <paramref name="host"/>:<paramref name="port"/>. Событие «открыто» — в <see cref="Poll"/>.
        /// К серверу на этой же машине (<see cref="IsLoopbackHost"/>) сокет клиента открывается только на петле — без окна брандмауэра.
        /// </summary>
        public static LiteNetTransport Connect(string host, int port)
        {
            var t = new LiteNetTransport(false, 1);
            bool started = IsLoopbackHost(host) ? t._manager.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0) : t._manager.Start();
            if (!started)
            {
                throw new SocketException((int)SocketError.SocketError);
            }

            NetPeer? peer = t._manager.Connect(host, port, NetSettings.ConnectionKey);
            if (peer == null)
            {
                t.Dispose();
                throw new SocketException((int)SocketError.HostNotFound);
            }

            t.ServerPeer = peer.Id;
            t._peers[peer.Id] = peer;
            return t;
        }

        /// <summary>Сервер на этой же машине: <c>localhost</c> или адрес петли (127.x.x.x, ::1) — без запроса DNS.</summary>
        public static bool IsLoopbackHost(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
        }

        public void Poll(long nowMicros, ITransportHandler handler)
        {
            _handler = handler;
            try
            {
                _manager.PollEvents();
            }
            finally
            {
                _handler = null;
            }
        }

        public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data)
        {
            if (channel == NetChannel.Unreliable && data.Length > NetSettings.MaxUnreliablePayload)
            {
                throw new ArgumentException($"Канал 0: {data.Length} байт больше {NetSettings.MaxUnreliablePayload}.", nameof(data));
            }

            if (_peers.TryGetValue(peer, out NetPeer? p) && p.ConnectionState == ConnectionState.Connected)
            {
                p.Send(data, (byte)channel, DeliveryOf(channel));
            }
        }

        public void Disconnect(int peer, ReadOnlySpan<byte> payload)
        {
            if (_peers.TryGetValue(peer, out NetPeer? p))
            {
                _manager.DisconnectPeer(p, payload);
            }
        }

        public int RoundTripMs(int peer) =>
            _peers.TryGetValue(peer, out NetPeer? p) ? p.RoundTripTime : -1;

        public void Dispose() => _manager.Stop(true);

        /// <summary>Доставка по номеру канала (таблица 2.10).</summary>
        public static DeliveryMethod DeliveryOf(NetChannel channel) =>
            channel == NetChannel.Unreliable ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered;

        // ---------- События LiteNetLib (только внутри PollEvents) ----------

        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            if (_isServer && _manager.ConnectedPeersCount < _maxPeers)
            {
                request.AcceptIfKey(NetSettings.ConnectionKey);
            }
            else
            {
                request.Reject();
            }
        }

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            _peers[peer.Id] = peer;
            _handler?.OnConnected(peer.Id);
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (_peers.TryGetValue(peer.Id, out NetPeer? known) && ReferenceEquals(known, peer))
            {
                _peers.Remove(peer.Id);
            }

            ReadOnlySpan<byte> payload = info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0
                ? info.AdditionalData.GetRemainingBytesSpan()
                : ReadOnlySpan<byte>.Empty;
            _handler?.OnDisconnected(peer.Id, ReasonOf(info.Reason), payload);
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (channelNumber < NetSettings.ChannelsCount)
            {
                _handler?.OnReceive(peer.Id, (NetChannel)channelNumber, reader.GetRemainingBytesSpan());
            }
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            // Ошибки сокета видны по отключению (тайм-аут); отдельного события нет.
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }

        private static NetDisconnectReason ReasonOf(DisconnectReason r)
        {
            switch (r)
            {
                case DisconnectReason.DisconnectPeerCalled:
                    return NetDisconnectReason.Local;
                case DisconnectReason.RemoteConnectionClose:
                    return NetDisconnectReason.Remote;
                case DisconnectReason.Timeout:
                    return NetDisconnectReason.Timeout;
                case DisconnectReason.ConnectionFailed:
                case DisconnectReason.ConnectionRejected:
                case DisconnectReason.HostUnreachable:
                case DisconnectReason.NetworkUnreachable:
                case DisconnectReason.UnknownHost:
                    return NetDisconnectReason.ConnectFailed;
                default:
                    return NetDisconnectReason.Other;
            }
        }
    }
}
