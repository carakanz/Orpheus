using System;
using System.Collections.Generic;

namespace Orpheus.Net.Transport
{
    /// <summary>
    /// Несколько слушающих транспортов под одним <see cref="ITransport"/> (0-024, docs/07 2.3 и 2.13 п. 9): сервер хоста
    /// слушает петлю, а «Открыть для друзей» добавляет второй сокет на всех адресах — без остановки первого, поэтому
    /// вошедшие не переподключаются и такт не прерывается. Номера соединений — свои, сквозные: каждому соединению
    /// внутреннего транспорта при открытии выдаётся следующий номер. Выделения — только при открытии соединения.
    /// </summary>
    public sealed class MuxTransport : ITransport
    {
        private readonly List<Inner> _inner = new List<Inner>();
        private readonly Dictionary<int, (Inner Inner, int Peer)> _byOuter = new Dictionary<int, (Inner, int)>();
        private int _nextPeer = 1;

        public MuxTransport(ITransport first)
        {
            Add(first);
        }

        /// <summary>Транспортов внутри.</summary>
        public int Count => _inner.Count;

        /// <summary>
        /// Добавить слушающий транспорт; его соединения получат свои номера. Можно звать и внутри <see cref="Poll"/>
        /// (из обработчика сообщения): новый транспорт опрашивается со следующего вызова.
        /// </summary>
        public void Add(ITransport transport)
        {
            if (transport == null)
            {
                throw new ArgumentNullException(nameof(transport));
            }

            _inner.Add(new Inner(this, transport));
        }

        public void Poll(long nowMicros, ITransportHandler handler)
        {
            int n = _inner.Count; // добавленный во время опроса — со следующего раза
            for (int i = 0; i < n; i++)
            {
                Inner t = _inner[i];
                t.Handler = handler;
                try
                {
                    t.Transport.Poll(nowMicros, t);
                }
                finally
                {
                    t.Handler = null;
                }
            }
        }

        public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data)
        {
            if (_byOuter.TryGetValue(peer, out (Inner Inner, int Peer) p))
            {
                p.Inner.Transport.Send(p.Peer, channel, data);
            }
        }

        public void Flush()
        {
            for (int i = 0; i < _inner.Count; i++)
            {
                _inner[i].Transport.Flush();
            }
        }

        public void Disconnect(int peer, ReadOnlySpan<byte> payload)
        {
            if (_byOuter.TryGetValue(peer, out (Inner Inner, int Peer) p))
            {
                p.Inner.Transport.Disconnect(p.Peer, payload);
            }
        }

        public int RoundTripMs(int peer) =>
            _byOuter.TryGetValue(peer, out (Inner Inner, int Peer) p) ? p.Inner.Transport.RoundTripMs(p.Peer) : -1;

        public void Dispose()
        {
            foreach (Inner t in _inner)
            {
                t.Transport.Dispose();
            }

            _inner.Clear();
            _byOuter.Clear();
        }

        /// <summary>Внутренний транспорт и его таблица номеров: свой → сквозной.</summary>
        private sealed class Inner : ITransportHandler
        {
            private readonly MuxTransport _owner;
            private readonly Dictionary<int, int> _outerOf = new Dictionary<int, int>();

            public Inner(MuxTransport owner, ITransport transport)
            {
                _owner = owner;
                Transport = transport;
            }

            public ITransport Transport { get; }

            public ITransportHandler? Handler { get; set; }

            public void OnConnected(int peer)
            {
                if (!_outerOf.TryGetValue(peer, out int outer))
                {
                    outer = _owner._nextPeer++;
                    _outerOf[peer] = outer;
                    _owner._byOuter[outer] = (this, peer);
                }

                Handler?.OnConnected(outer);
            }

            public void OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload)
            {
                if (!_outerOf.TryGetValue(peer, out int outer))
                {
                    return;
                }

                _outerOf.Remove(peer);
                _owner._byOuter.Remove(outer);
                Handler?.OnDisconnected(outer, reason, payload);
            }

            public void OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
                if (_outerOf.TryGetValue(peer, out int outer))
                {
                    Handler?.OnReceive(outer, channel, data);
                }
            }
        }
    }
}
