using System;
using System.Collections.Generic;
using System.Threading;

namespace Orpheus.Net.Transport
{
    /// <summary>
    /// Связь в памяти (docs/07, 1.6 и 2.3): для тестов, режима симуляции и «сервера в потоке» (0-031: клиент Unity и
    /// <c>ServerHost</c> в одном процессе). Доставка мгновенная, без потерь и по порядку; портит её, если надо, обёртка
    /// <see cref="NetSimTransport"/>. Потокобезопасна: сервер и клиенты зовут <see cref="Poll"/> и <see cref="Send"/> из разных
    /// потоков.
    /// <para>Сервер — <see cref="CreateServer"/>; клиент — <see cref="ConnectTo"/>, у клиента сервер — соединение 0.</para>
    /// <para>
    /// Без выделений на пакет (0-031, backlog по П 0-008 з. 9): данные копируются в буфер из пула получателя (классы по
    /// степени двойки, 64 Б … 64 КБ; больше — редкий случай, обычный массив), буфер возвращается в пул после разбора в
    /// <see cref="Poll"/>. Блокировки: на пакет — одна короткая (очередь получателя: взять буфер, скопировать, поставить);
    /// связи ищутся без блокировки — по снимку массива, который меняется только при подключении и отключении (копия при
    /// записи); в <see cref="Poll"/> — две на вызов (обмен очередей и возврат буферов пачкой), а не на пакет.
    /// </para>
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
            public int Length;
            public bool Pooled;
        }

        /// <summary>Связь с другой стороной; <see cref="Remote"/> == null — соединения нет.</summary>
        private readonly struct Link
        {
            public readonly LoopbackTransport? Remote;
            public readonly int RemotePeer;

            public Link(LoopbackTransport remote, int remotePeer)
            {
                Remote = remote;
                RemotePeer = remotePeer;
            }
        }

        /// <summary>Наименьший класс буфера пула: 2^6 = 64 байта.</summary>
        private const int MinClassBits = 6;

        /// <summary>Наибольший класс: 2^16 = 64 КБ; больше — без пула.</summary>
        private const int MaxClassBits = 16;

        private readonly object _lock = new object();
        private readonly bool _isServer;
        private readonly int _maxPeers;
        private readonly List<byte[]>[] _pool = new List<byte[]>[MaxClassBits - MinClassBits + 1];
        private Link[] _links = new Link[4]; // снимок: читается без блокировки, меняется копией под _lock
        private int _linkCount;
        private List<Item> _inbox = new List<Item>();
        private List<Item> _draining = new List<Item>();
        private int _nextPeer = 1;
        private bool _disposed;

        private LoopbackTransport(bool isServer, int maxPeers)
        {
            _isServer = isServer;
            _maxPeers = maxPeers;
            for (int i = 0; i < _pool.Length; i++)
            {
                _pool[i] = new List<byte[]>();
            }
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
                if (!server._disposed && server._linkCount < server._maxPeers)
                {
                    serverSidePeer = server._nextPeer++;
                    server.SetLinkLocked(serverSidePeer, new Link(client, ServerPeer));
                    server._inbox.Add(new Item { Kind = Kind.Connected, Peer = serverSidePeer });
                }
            }

            lock (client._lock)
            {
                if (serverSidePeer >= 0)
                {
                    client.SetLinkLocked(ServerPeer, new Link(server, serverSidePeer));
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
                    return _linkCount;
                }
            }
        }

        /// <summary>Буферов сейчас в пуле (тесты: пул не растёт в установившемся режиме).</summary>
        public int PooledBuffers
        {
            get
            {
                lock (_lock)
                {
                    int n = 0;
                    for (int i = 0; i < _pool.Length; i++)
                    {
                        n += _pool[i].Count;
                    }

                    return n;
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

            try
            {
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
                            handler.OnReceive(it.Peer, it.Channel, new ReadOnlySpan<byte>(it.Data, 0, it.Length));
                            break;
                    }
                }
            }
            finally
            {
                // Буферы — в пул пачкой (одна блокировка на Poll); обработчик данные не держит (контракт ITransportHandler).
                lock (_lock)
                {
                    for (int i = 0; i < _draining.Count; i++)
                    {
                        Item it = _draining[i];
                        if (it.Pooled)
                        {
                            _pool[ClassOf(it.Data!.Length)].Add(it.Data);
                        }
                    }
                }

                _draining.Clear();
            }
        }

        public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data)
        {
            if (channel == NetChannel.Unreliable && data.Length > NetSettings.MaxUnreliablePayload)
            {
                throw new ArgumentException($"Канал 0: {data.Length} байт больше {NetSettings.MaxUnreliablePayload}.", nameof(data));
            }

            Link[] links = Volatile.Read(ref _links);
            if ((uint)peer >= (uint)links.Length || links[peer].Remote == null)
            {
                return;
            }

            Link link = links[peer];
            link.Remote!.EnqueueData(link.RemotePeer, channel, data);
        }

        public void Disconnect(int peer, ReadOnlySpan<byte> payload)
        {
            Link link;
            lock (_lock)
            {
                if ((uint)peer >= (uint)_links.Length || _links[peer].Remote == null)
                {
                    return;
                }

                link = _links[peer];
                SetLinkLocked(peer, default);
                _inbox.Add(new Item { Kind = Kind.Disconnected, Peer = peer, Reason = NetDisconnectReason.Local });
            }

            link.Remote!.DropLink(link.RemotePeer, NetDisconnectReason.Remote, payload.ToArray());
        }

        /// <summary>Доставка и так мгновенная — нечего сбрасывать.</summary>
        public void Flush()
        {
        }

        public int RoundTripMs(int peer) => 0;

        /// <summary>Закрыть все соединения: другим сторонам — «закрыто другой стороной».</summary>
        public void Dispose()
        {
            var links = new List<Link>();
            lock (_lock)
            {
                _disposed = true;
                for (int peer = 0; peer < _links.Length; peer++) // по номеру: порядок закрытия повторяем
                {
                    if (_links[peer].Remote != null)
                    {
                        links.Add(_links[peer]);
                    }
                }

                Volatile.Write(ref _links, new Link[_links.Length]);
                _linkCount = 0;
            }

            for (int i = 0; i < links.Count; i++)
            {
                links[i].Remote!.DropLink(links[i].RemotePeer, NetDisconnectReason.Remote, Array.Empty<byte>());
            }
        }

        /// <summary>Пакет от другой стороны: буфер из своего пула, копия, в очередь — под одной блокировкой.</summary>
        private void EnqueueData(int peer, NetChannel channel, ReadOnlySpan<byte> data)
        {
            lock (_lock)
            {
                if ((uint)peer >= (uint)_links.Length || _links[peer].Remote == null)
                {
                    return;
                }

                bool pooled = data.Length <= 1 << MaxClassBits;
                byte[] buffer;
                if (pooled)
                {
                    List<byte[]> bucket = _pool[ClassOf(data.Length)];
                    if (bucket.Count > 0)
                    {
                        buffer = bucket[bucket.Count - 1];
                        bucket.RemoveAt(bucket.Count - 1);
                    }
                    else
                    {
                        buffer = new byte[1 << (ClassOf(data.Length) + MinClassBits)]; // разогрев пула
                    }
                }
                else
                {
                    buffer = data.ToArray();
                }

                data.CopyTo(buffer);
                _inbox.Add(new Item { Kind = Kind.Data, Peer = peer, Channel = channel, Data = buffer, Length = data.Length, Pooled = pooled });
            }
        }

        private void DropLink(int peer, NetDisconnectReason reason, byte[] payload)
        {
            lock (_lock)
            {
                if ((uint)peer < (uint)_links.Length && _links[peer].Remote != null)
                {
                    SetLinkLocked(peer, default);
                    _inbox.Add(new Item { Kind = Kind.Disconnected, Peer = peer, Reason = reason, Data = payload, Length = payload.Length });
                }
            }
        }

        /// <summary>Связь <paramref name="peer"/> — новой копией массива (читатели без блокировки видят целый снимок).</summary>
        private void SetLinkLocked(int peer, Link link)
        {
            Link[] old = _links;
            var copy = new Link[peer < old.Length ? old.Length : NextPow2(peer + 1)];
            Array.Copy(old, copy, old.Length);
            if (peer < old.Length && old[peer].Remote != null)
            {
                _linkCount--;
            }

            copy[peer] = link;
            if (link.Remote != null)
            {
                _linkCount++;
            }

            Volatile.Write(ref _links, copy);
        }

        /// <summary>Класс буфера для длины: наименьшая степень двойки ≥ длины, от 64 байт.</summary>
        private static int ClassOf(int length)
        {
            int bits = MinClassBits;
            while (1 << bits < length)
            {
                bits++;
            }

            return bits - MinClassBits;
        }

        private static int NextPow2(int n)
        {
            int p = 4;
            while (p < n)
            {
                p <<= 1;
            }

            return p;
        }
    }
}
