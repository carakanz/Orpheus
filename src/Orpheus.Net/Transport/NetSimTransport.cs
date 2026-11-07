using System;
using System.Collections.Generic;
using Orpheus.Core.Rng;

namespace Orpheus.Net.Transport
{
    /// <summary>
    /// Настройки имитатора связи (docs/07, 2.10 и 11.3). Доли — в миллионных (1 % = 10 000): целые числа,
    /// повторяемо на любой платформе. Задержка — в каждую сторону через обёрнутый транспорт: обёртка на клиенте
    /// с <see cref="DelayMs"/> = 50 даёт RTT 100 мс.
    /// </summary>
    public struct NetSimSettings
    {
        /// <summary>Миллионных в 100 %.</summary>
        public const int Million = 1_000_000;

        /// <summary>Задержка в каждую сторону, мс.</summary>
        public int DelayMs;

        /// <summary>Разброс задержки: σ нормального распределения, мс.</summary>
        public int JitterMs;

        /// <summary>Разброс пачками: одна добавка на всё окно <see cref="BurstMs"/>, а не своя у каждого пакета.</summary>
        public bool BurstJitter;

        /// <summary>Длина пачки при <see cref="BurstJitter"/>, мс (по умолчанию 250).</summary>
        public int BurstMs;

        /// <summary>Потери, миллионных. Ненадёжный канал — пакет пропадает; надёжный — приходит позже на переотправку.</summary>
        public int LossPpm;

        /// <summary>Дубли (только канал 0), миллионных.</summary>
        public int DuplicatePpm;

        /// <summary>Перестановка (только канал 0), миллионных: пакет задерживается ещё на 1…<see cref="ReorderMs"/> мс.</summary>
        public int ReorderPpm;

        /// <summary>Наибольшая добавка при перестановке, мс (по умолчанию 40).</summary>
        public int ReorderMs;

        /// <summary>Зерно случайности.</summary>
        public ulong Seed;

        /// <summary>Портит ли что-нибудь.</summary>
        public bool IsActive => DelayMs > 0 || JitterMs > 0 || LossPpm > 0 || DuplicatePpm > 0 || ReorderPpm > 0;

        /// <summary>Настройки бота: RTT (мс), разброс σ (мс), потери (%), зерно — задержка RTT/2 в каждую сторону.</summary>
        public static NetSimSettings FromRtt(int rttMs, int jitterMs, double lossPercent, ulong seed) => new NetSimSettings
        {
            DelayMs = rttMs / 2,
            JitterMs = jitterMs,
            LossPpm = PercentToPpm(lossPercent),
            Seed = seed,
        };

        /// <summary>Проценты → миллионные (один раз при разборе ключей; дальше — только целые).</summary>
        public static int PercentToPpm(double percent)
        {
            if (percent < 0 || percent > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(percent), percent, "Доля — от 0 до 100 %.");
            }

            return (int)Math.Round(percent * (Million / 100), MidpointRounding.AwayFromZero);
        }

        public override string ToString() =>
            $"delay {DelayMs} мс, σ {JitterMs} мс{(BurstJitter ? " пачками" : "")}, потери {LossPpm / 10000.0:0.###} %, " +
            $"дубли {DuplicatePpm / 10000.0:0.###} %, перестановка {ReorderPpm / 10000.0:0.###} %, зерно {Seed}";
    }

    /// <summary>Счётчики имитатора по одному направлению.</summary>
    public struct NetSimStats
    {
        /// <summary>Пакетов пришло на вход имитатора.</summary>
        public long Packets;

        /// <summary>Отдано дальше (с дублями).</summary>
        public long Delivered;

        /// <summary>Потеряно (канал 0).</summary>
        public long Lost;

        /// <summary>Дублей.</summary>
        public long Duplicated;

        /// <summary>Переставлено (дополнительная задержка).</summary>
        public long Reordered;

        /// <summary>Переотправок надёжных (потеря на надёжном канале).</summary>
        public long Retransmits;

        /// <summary>Выброшено из очереди: соединение закрылось раньше срока доставки (0-015).</summary>
        public long Discarded;

        /// <summary>Сумма задержек отданных пакетов, мкс.</summary>
        public long DelaySumMicros;

        /// <summary>Средняя задержка, мс.</summary>
        public double MeanDelayMs => Delivered > 0 ? DelaySumMicros / 1000.0 / Delivered : 0;
    }

    /// <summary>
    /// Имитатор связи — своя прослойка над <see cref="ITransport"/> (docs/07, 2.10): задержка, разброс (свой у пакета
    /// или пачками), потери, дубли, перестановка. Работает в Release (своя, не встроенная LiteNetLib) и одинаково над
    /// UDP и над связью в памяти. Случайность — от зерна: два потока <see cref="DetRandom"/> (отправка и приём), так
    /// что одинаковая последовательность вызовов с одинаковым временем даёт одинаковую доставку.
    /// <para>
    /// Канал 0 (ненадёжный): пакеты теряются, дублируются, переставляются. Каналы 1–4 (надёжные): потеря — это
    /// переотправка (пакет приходит позже на 2 × задержка + 25 мс), порядок внутри канала сохраняется, дублей нет —
    /// как у настоящего надёжного канала. События «открыто» и «закрыто» проходят без задержки.
    /// </para>
    /// <para>
    /// «Закрыто» (своё <see cref="Disconnect"/> или от обёрнутого транспорта) выбрасывает пакеты этого соединения,
    /// ещё ждущие в очередях: LiteNetLib переиспользует номера соединений, и застрявший пакет иначе ушёл бы новому.
    /// </para>
    /// </summary>
    public sealed class NetSimTransport : ITransport, ITransportHandler
    {
        /// <summary>Номер системы потока «отправка» (11.5: поток на систему).</summary>
        public const int SendSystemId = 0x5E01;

        /// <summary>Номер системы потока «приём».</summary>
        public const int ReceiveSystemId = 0x5E02;

        /// <summary>Добавка к переотправке надёжного пакета сверх двух задержек, мкс.</summary>
        public const int RetransmitExtraMicros = 25_000;

        private const int MaxRetransmits = 16;

        private readonly ITransport _inner;
        private readonly NetSimSettings _s;
        private readonly PendingHeap _outbound = new PendingHeap();
        private readonly PendingHeap _inbound = new PendingHeap();
        private readonly Stack<byte[]> _pool = new Stack<byte[]>();
        private readonly Dictionary<int, long[]> _reliableDueOut = new Dictionary<int, long[]>();
        private readonly Dictionary<int, long[]> _reliableDueIn = new Dictionary<int, long[]>();
        private Direction _send;
        private Direction _receive;
        private ITransportHandler? _handler;
        private long _now;
        private long _seq;

        private struct Direction
        {
            public DetRandom Rng;
            public long BurstUntil;
            public long BurstOffset;
            public NetSimStats Stats;
        }

        public NetSimTransport(ITransport inner, NetSimSettings settings)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (settings.DelayMs < 0 || settings.JitterMs < 0 || settings.LossPpm < 0 || settings.LossPpm > NetSimSettings.Million
                || settings.DuplicatePpm < 0 || settings.DuplicatePpm > NetSimSettings.Million
                || settings.ReorderPpm < 0 || settings.ReorderPpm > NetSimSettings.Million)
            {
                throw new ArgumentOutOfRangeException(nameof(settings), settings.ToString());
            }

            if (settings.BurstMs <= 0)
            {
                settings.BurstMs = 250;
            }

            if (settings.ReorderMs <= 0)
            {
                settings.ReorderMs = 40;
            }

            _s = settings;
            _send.Rng = DetRandom.ForSystem(settings.Seed, SendSystemId);
            _receive.Rng = DetRandom.ForSystem(settings.Seed, ReceiveSystemId);
        }

        /// <summary>Настройки.</summary>
        public NetSimSettings Settings => _s;

        /// <summary>Счётчики: отправка через имитатор.</summary>
        public NetSimStats SendStats => _send.Stats;

        /// <summary>Счётчики: приём через имитатор.</summary>
        public NetSimStats ReceiveStats => _receive.Stats;

        /// <summary>Пакетов в очереди (обе стороны).</summary>
        public int PendingCount => _outbound.Count + _inbound.Count;

        public void Poll(long nowMicros, ITransportHandler handler)
        {
            _now = nowMicros;
            FlushOutbound();

            _handler = handler;
            try
            {
                _inner.Poll(nowMicros, this);
                while (_inbound.Count > 0 && _inbound.PeekDue() <= _now)
                {
                    Pending p = _inbound.Pop();
                    handler.OnReceive(p.Peer, p.Channel, new ReadOnlySpan<byte>(p.Data, 0, p.Length));
                    Return(p.Data);
                }
            }
            finally
            {
                _handler = null;
            }

            FlushOutbound();
        }

        public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data)
        {
            if (channel == NetChannel.Unreliable && data.Length > NetSettings.MaxUnreliablePayload)
            {
                throw new ArgumentException($"Канал 0: {data.Length} байт больше {NetSettings.MaxUnreliablePayload}.", nameof(data));
            }

            Schedule(ref _send, _outbound, _reliableDueOut, peer, channel, data);
            FlushOutbound();
        }

        public void Disconnect(int peer, ReadOnlySpan<byte> payload)
        {
            FlushOutbound();
            _inner.Disconnect(peer, payload);
            Forget(peer);
        }

        public int RoundTripMs(int peer)
        {
            int rtt = _inner.RoundTripMs(peer);
            return rtt < 0 ? rtt : rtt + 2 * _s.DelayMs;
        }

        public void Dispose() => _inner.Dispose();

        // ---------- События обёрнутого транспорта ----------

        void ITransportHandler.OnConnected(int peer) => _handler?.OnConnected(peer);

        void ITransportHandler.OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload)
        {
            Forget(peer);
            _handler?.OnDisconnected(peer, reason, payload);
        }

        /// <summary>Соединение закрыто: его пакеты из очередей — выбросить, сроки надёжных каналов — забыть.</summary>
        private void Forget(int peer)
        {
            _reliableDueIn.Remove(peer);
            _reliableDueOut.Remove(peer);
            _send.Stats.Discarded += _outbound.RemovePeer(peer, _pool);
            _receive.Stats.Discarded += _inbound.RemovePeer(peer, _pool);
        }

        void ITransportHandler.OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data) =>
            Schedule(ref _receive, _inbound, _reliableDueIn, peer, channel, data);

        // ---------- Порча ----------

        private void Schedule(ref Direction d, PendingHeap heap, Dictionary<int, long[]> reliableDue, int peer, NetChannel channel, ReadOnlySpan<byte> data)
        {
            d.Stats.Packets++;
            if (channel == NetChannel.Unreliable)
            {
                if (Roll(ref d, _s.LossPpm))
                {
                    d.Stats.Lost++;
                    return;
                }

                int copies = Roll(ref d, _s.DuplicatePpm) ? 2 : 1;
                if (copies == 2)
                {
                    d.Stats.Duplicated++;
                }

                for (int c = 0; c < copies; c++)
                {
                    long delay = BaseDelay(ref d);
                    if (Roll(ref d, _s.ReorderPpm))
                    {
                        d.Stats.Reordered++;
                        delay += d.Rng.NextInt(1000, _s.ReorderMs * 1000 + 1);
                    }

                    Enqueue(ref d, heap, peer, channel, data, _now + delay);
                }

                return;
            }

            long reliableDelay = BaseDelay(ref d);
            for (int r = 0; r < MaxRetransmits && Roll(ref d, _s.LossPpm); r++)
            {
                d.Stats.Retransmits++;
                reliableDelay += 2L * _s.DelayMs * 1000 + RetransmitExtraMicros;
            }

            if (!reliableDue.TryGetValue(peer, out long[]? last))
            {
                last = new long[NetSettings.ChannelsCount];
                reliableDue[peer] = last;
            }

            long due = Math.Max(_now + reliableDelay, last[(int)channel]); // надёжный канал не обгоняет сам себя
            last[(int)channel] = due;
            Enqueue(ref d, heap, peer, channel, data, due);
        }

        private void Enqueue(ref Direction d, PendingHeap heap, int peer, NetChannel channel, ReadOnlySpan<byte> data, long due)
        {
            byte[] buf = Rent(data.Length);
            data.CopyTo(buf);
            heap.Push(new Pending { Due = due, Seq = _seq++, Peer = peer, Channel = channel, Data = buf, Length = data.Length });
            d.Stats.Delivered++;
            d.Stats.DelaySumMicros += due - _now;
        }

        private long BaseDelay(ref Direction d)
        {
            long delay = _s.DelayMs * 1000L;
            if (_s.JitterMs > 0)
            {
                if (_s.BurstJitter)
                {
                    if (_now >= d.BurstUntil)
                    {
                        d.BurstOffset = Gaussian(ref d.Rng, _s.JitterMs);
                        d.BurstUntil = _now + _s.BurstMs * 1000L;
                    }

                    delay += d.BurstOffset;
                }
                else
                {
                    delay += Gaussian(ref d.Rng, _s.JitterMs);
                }
            }

            return delay < 0 ? 0 : delay;
        }

        /// <summary>
        /// Нормальное с σ = <paramref name="sigmaMs"/> мс, в мкс: сумма 12 равномерных минус 6 (Ирвин — Холл) — целыми,
        /// без логарифма и косинуса ОС (11.5).
        /// </summary>
        public static long Gaussian(ref DetRandom rng, int sigmaMs)
        {
            long sum = 0;
            for (int i = 0; i < 12; i++)
            {
                sum += rng.NextInt(NetSimSettings.Million);
            }

            sum -= 6L * NetSimSettings.Million; // в миллионных σ
            return sum * sigmaMs * 1000L / NetSimSettings.Million;
        }

        private static bool Roll(ref Direction d, int ppm) =>
            ppm > 0 && (ppm >= NetSimSettings.Million || d.Rng.NextInt(NetSimSettings.Million) < ppm);

        private void FlushOutbound()
        {
            while (_outbound.Count > 0 && _outbound.PeekDue() <= _now)
            {
                Pending p = _outbound.Pop();
                _inner.Send(p.Peer, p.Channel, new ReadOnlySpan<byte>(p.Data, 0, p.Length));
                Return(p.Data);
            }
        }

        private byte[] Rent(int length)
        {
            if (length <= NetSettings.Mtu)
            {
                return _pool.Count > 0 ? _pool.Pop() : new byte[NetSettings.Mtu];
            }

            return new byte[length];
        }

        private void Return(byte[] buf)
        {
            if (buf.Length == NetSettings.Mtu)
            {
                _pool.Push(buf);
            }
        }

        private struct Pending
        {
            public long Due;
            public long Seq;
            public int Peer;
            public NetChannel Channel;
            public byte[] Data;
            public int Length;
        }

        /// <summary>Двоичная куча по (срок, номер постановки): равные сроки уходят в порядке постановки.</summary>
        private sealed class PendingHeap
        {
            private Pending[] _items = new Pending[64];

            public int Count { get; private set; }

            public long PeekDue() => _items[0].Due;

            public void Push(in Pending p)
            {
                if (Count == _items.Length)
                {
                    Array.Resize(ref _items, _items.Length * 2);
                }

                int i = Count++;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (!Less(p, _items[parent]))
                    {
                        break;
                    }

                    _items[i] = _items[parent];
                    i = parent;
                }

                _items[i] = p;
            }

            public Pending Pop()
            {
                Pending top = _items[0];
                Pending last = _items[--Count];
                _items[Count] = default;
                if (Count > 0)
                {
                    SiftDown(0, last);
                }

                return top;
            }

            /// <summary>
            /// Выбросить все пакеты соединения <paramref name="peer"/> (буферы — в <paramref name="pool"/>), кучу
            /// перестроить; возвращает, сколько выброшено. Порядок оставшихся — тот же (срок, номер постановки).
            /// </summary>
            public int RemovePeer(int peer, Stack<byte[]> pool)
            {
                int kept = 0;
                for (int i = 0; i < Count; i++)
                {
                    if (_items[i].Peer != peer)
                    {
                        _items[kept++] = _items[i];
                    }
                    else if (_items[i].Data.Length == NetSettings.Mtu)
                    {
                        pool.Push(_items[i].Data);
                    }
                }

                int removed = Count - kept;
                if (removed == 0)
                {
                    return 0;
                }

                Array.Clear(_items, kept, removed);
                Count = kept;
                for (int i = Count / 2 - 1; i >= 0; i--)
                {
                    SiftDown(i, _items[i]);
                }

                return removed;
            }

            /// <summary>Поставить <paramref name="item"/> на место <paramref name="i"/> и просеять вниз.</summary>
            private void SiftDown(int i, Pending item)
            {
                while (true)
                {
                    int child = 2 * i + 1;
                    if (child >= Count)
                    {
                        break;
                    }

                    if (child + 1 < Count && Less(_items[child + 1], _items[child]))
                    {
                        child++;
                    }

                    if (!Less(_items[child], item))
                    {
                        break;
                    }

                    _items[i] = _items[child];
                    i = child;
                }

                _items[i] = item;
            }

            private static bool Less(in Pending a, in Pending b) => a.Due < b.Due || (a.Due == b.Due && a.Seq < b.Seq);
        }
    }
}
