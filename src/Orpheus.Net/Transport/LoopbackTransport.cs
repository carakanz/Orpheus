using System;
using System.Collections.Generic;

namespace Orpheus.Net.Transport
{
    /// <summary>
    /// Связь в памяти (docs/07, 1.6 и 2.3): для тестов, режима симуляции и будущего «сервера в потоке» на консоли.
    /// Доставка мгновенная, без потерь и по порядку; портит её, если надо, обёртка <see cref="NetSimTransport"/>.
    /// Потокобезопасна: сервер и клиенты могут звать <see cref="Poll"/> и <see cref="Send"/> из разных потоков.
    /// <para>Сервер — <see cref="CreateServer"/>; клиент — <see cref="ConnectTo"/>, у клиента сервер — соединение 0.</para>
    /// </summary>
    public sealed class LoopbackTransport : ITransport
    {
        private enum Kind : byte
        {
            Connected,
            Disconnected,
            Data,
        }

        private struct Item
        {
            public Kind Kind;
            public int Peer;
            public NetChannel Channel;
            public NetDisconnectReason Reason;
            public byte[]? Data;
        }

        private readonly struct Link
        {
            public readonly LoopbackTransport Remote;
            public readonly int RemotePeer;

            public Link(LoopbackTransport remote, int remotePeer)
            {
                Remote = remote;
                RemotePeer = remotePeer;
            }
        }

        private readonly object _lock = new object();
        private readonly Dictionary<int, Link> _links = new Dictionary<int, Link>();
        private readonly bool _isServer;
        private readonly int _maxPeers;
        private List<Item> _inbox = new List<Item>();
        private List<Item> _draining = new List<Item>();
        private int _nextPeer = 1;
        private bool _disposed;

        private LoopbackTransport(bool isServer, int maxPeers)
        {
            _isServer = isServer;
            _maxPeers = maxPeers;
        }

        /// <summary>Номер соединения с сервером у клиента — всегда 0.</summary>
        public const int ServerPeer = 0;

        /// <summary>Сервер в памяти на <paramref name="maxPeers"/> соединений.</summary>
        public static LoopbackTransport CreateServer(int maxPeers) => new LoopbackTransport(true, maxPeers);

        /// <summary>
        /// Клиент, подключённый к <paramref name="server"/>. «Открыто» придёт обеим сторонам при их <see cref="Poll"/>;
        /// сервер полон или закрыт — клиенту придёт «подключиться не удалось».
        /// </summary>
        public static LoopbackTransport ConnectTo(LoopbackTransport server)
        {
            if (!server._isServer)
            {
                throw new ArgumentException("Подключаться можно только к серверу.", nameof(server));
            }

            var client = new LoopbackTransport(false, 1);
            int serverSidePeer = -1;
            lock (server._lock)
            {
                if (!server._disposed && server._links.Count < server._maxPeers)
                {
                    serverSidePeer = server._nextPeer++;
                    server._links[serverSidePeer] = new Link(client, ServerPeer);
                    server._inbox.Add(new Item { Kind = Kind.Connected, Peer = serverSidePeer });
                }
            }

            lock (client._lock)
            {
                if (serverSidePeer >= 0)
                {
                    client._links[ServerPeer] = new Link(server, serverSidePeer);
                    client._inbox.Add(new Item { Kind = Kind.Connected, Peer = ServerPeer });
                }
                else
                {
                    client._inbox.Add(new Item { Kind = Kind.Disconnected, Peer = ServerPeer, Reason = NetDisconnectReason.ConnectFailed });
                }
            }

            return client;
        }

        /// <summary>Открытых соединений.</summary>
        public int ConnectedCount
        {
            get
            {
                lock (_lock)
                {
                    return _links.Count;
                }
            }
        }

        public void Poll(long nowMicros, ITransportHandler handler)
        {
            lock (_lock)
            {
                List<Item> t = _inbox;
                _inbox = _draining;
                _draining = t;
            }

            for (int i = 0; i < _draining.Count; i++)
            {
                Item it = _draining[i];
                switch (it.Kind)
                {
                    case Kind.Connected:
                        handler.OnConnected(it.Peer);
                        break;
                    case Kind.Disconnected:
                        handler.OnDisconnected(it.Peer, it.Reason, it.Data);
                        break;
                    default:
                        handler.OnReceive(it.Peer, it.Channel, it.Data);
                        break;
                }
            }

            _draining.Clear();
        }

        public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data)
        {
            if (channel == NetChannel.Unreliable && data.Length > NetSettings.MaxUnreliablePayload)
            {
                throw new ArgumentException($"Канал 0: {data.Length} байт больше {NetSettings.MaxUnreliablePayload}.", nameof(data));
            }

            Link link;
            lock (_lock)
            {
                if (!_links.TryGetValue(peer, out link))
                {
                    return;
                }
            }

            link.Remote.Enqueue(new Item { Kind = Kind.Data, Peer = link.RemotePeer, Channel = channel, Data = data.ToArray() });
        }

        public void Disconnect(int peer, ReadOnlySpan<byte> payload)
        {
            Link link;
            lock (_lock)
            {
                if (!_links.TryGetValue(peer, out link))
                {
                    return;
                }

                _links.Remove(peer);
                _inbox.Add(new Item { Kind = Kind.Disconnected, Peer = peer, Reason = NetDisconnectReason.Local });
            }

            link.Remote.DropLink(link.RemotePeer, NetDisconnectReason.Remote, payload.ToArray());
        }

        public int RoundTripMs(int peer) => 0;

        /// <summary>Закрыть все соединения: другим сторонам — «закрыто другой стороной».</summary>
        public void Dispose()
        {
            var links = new List<Link>();
            lock (_lock)
            {
                _disposed = true;
                for (int peer = 0; peer < _nextPeer; peer++) // по номеру: порядок обхода словаря не гарантирован
                {
                    if (_links.TryGetValue(peer, out Link link))
                    {
                        links.Add(link);
                    }
                }

                _links.Clear();
            }

            for (int i = 0; i < links.Count; i++)
            {
                links[i].Remote.DropLink(links[i].RemotePeer, NetDisconnectReason.Remote, Array.Empty<byte>());
            }
        }

        private void Enqueue(in Item item)
        {
            lock (_lock)
            {
                if (_links.ContainsKey(item.Peer))
                {
                    _inbox.Add(item);
                }
            }
        }

        private void DropLink(int peer, NetDisconnectReason reason, byte[] payload)
        {
            lock (_lock)
            {
                if (_links.Remove(peer))
                {
                    _inbox.Add(new Item { Kind = Kind.Disconnected, Peer = peer, Reason = reason, Data = payload });
                }
            }
        }
    }
}
