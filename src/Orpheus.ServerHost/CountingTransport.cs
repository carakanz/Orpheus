using System;
using System.Threading;
using Orpheus.Net.Transport;

namespace Orpheus.Server
{
    /// <summary>
    /// Обёртка транспорта сервера со счётом байт полезной нагрузки (0-030, docs/07 11.6 «байты в обе стороны на клиента»):
    /// всё, что <c>NetServer</c> отдал в <see cref="Send"/>, и всё, что пришло в <see cref="Poll"/>. Заголовки UDP и
    /// LiteNetLib, подтверждения и повторы надёжных каналов сюда не входят — это байты протокола игры.
    /// <para>
    /// Без выделений: обработчик-переходник один на всё время, счёт по соединениям — в массивах на
    /// <see cref="MaxPeers"/> номеров (номер больше — только в общем счёте).
    /// </para>
    /// </summary>
    public sealed class CountingTransport : ITransport, ITransportHandler
    {
        /// <summary>Номеров соединений со своим счётом.</summary>
        public const int MaxPeers = 256;

        private readonly ITransport _inner;
        private readonly long[] _peerOut = new long[MaxPeers];
        private readonly long[] _peerIn = new long[MaxPeers];
        private ITransportHandler? _handler;
        private long _bytesOut;
        private long _bytesIn;
        private long _messagesOut;
        private long _messagesIn;

        public CountingTransport(ITransport inner)
        {
            _inner = inner;
        }

        /// <summary>Байт отправлено (нагрузка) с запуска.</summary>
        public long BytesOut => Interlocked.Read(ref _bytesOut);

        /// <summary>Байт принято (нагрузка) с запуска.</summary>
        public long BytesIn => Interlocked.Read(ref _bytesIn);

        /// <summary>Сообщений отправлено.</summary>
        public long MessagesOut => Interlocked.Read(ref _messagesOut);

        /// <summary>Сообщений принято.</summary>
        public long MessagesIn => Interlocked.Read(ref _messagesIn);

        /// <summary>Байт отправлено соединению <paramref name="peer"/> (номер вне <see cref="MaxPeers"/> — 0).</summary>
        public long PeerOut(int peer) => (uint)peer < MaxPeers ? Volatile.Read(ref _peerOut[peer]) : 0;

        /// <summary>Байт принято от соединения <paramref name="peer"/>.</summary>
        public long PeerIn(int peer) => (uint)peer < MaxPeers ? Volatile.Read(ref _peerIn[peer]) : 0;

        public void Poll(long nowMicros, ITransportHandler handler)
        {
            _handler = handler;
            _inner.Poll(nowMicros, this);
        }

        public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data)
        {
            _inner.Send(peer, channel, data);
            _bytesOut += data.Length;
            _messagesOut++;
            if ((uint)peer < MaxPeers)
            {
                _peerOut[peer] += data.Length;
            }
        }

        public void Flush() => _inner.Flush();

        public void Disconnect(int peer, ReadOnlySpan<byte> payload)
        {
            _inner.Disconnect(peer, payload);
            _bytesOut += payload.Length;
        }

        public int RoundTripMs(int peer) => _inner.RoundTripMs(peer);

        public void Dispose() => _inner.Dispose();

        void ITransportHandler.OnConnected(int peer) => _handler?.OnConnected(peer);

        void ITransportHandler.OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload) =>
            _handler?.OnDisconnected(peer, reason, payload);

        void ITransportHandler.OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
        {
            _bytesIn += data.Length;
            _messagesIn++;
            if ((uint)peer < MaxPeers)
            {
                _peerIn[peer] += data.Length;
            }

            _handler?.OnReceive(peer, channel, data);
        }
    }
}
