using System;
using System.Collections.Generic;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// E2-1 (шаг 2.1): кучи на земле, которые клиент знает от сервера (сообщения 27 и 28), и заявки на операции с
    /// сумкой (29). Ни подбор, ни операции с ячейками клиент не предсказывает: ждёт сумку сервера (тип 26).
    /// </summary>
    public sealed partial class NetClient
    {
        private readonly List<NetPile> _piles = new List<NetPile>();
        private readonly NetPile[] _pileScratch = new NetPile[PilesMessage.MaxPiles];

        /// <summary>Кучи на земле в обзоре, по возрастанию номера.</summary>
        public IReadOnlyList<NetPile> Piles => _piles;

        /// <summary>Сообщений «кучи» (27) принято.</summary>
        public long PileMessagesSeen { get; private set; }

        /// <summary>Сообщений «кучи нет» (28) принято.</summary>
        public long PileGoneSeen { get; private set; }

        /// <summary>Куча появилась или изменила количество (после записи в <see cref="Piles"/>).</summary>
        public Action<NetPile>? PileSeen { get; set; }

        /// <summary>Куча исчезла (после удаления из <see cref="Piles"/>): причина и кто поднял; вторым — какой она была.</summary>
        public Action<PileGoneMessage, NetPile>? PileGone { get; set; }

        /// <summary>Заявка серверу: переместить ячейку <paramref name="from"/> в <paramref name="to"/> (перенос, доукладка, обмен).</summary>
        public bool RequestBagMove(int from, int to) => SendBagRequest(BagOp.Move, from, to, 0);

        /// <summary>Заявка серверу: отделить <paramref name="count"/> штук из ячейки в другую.</summary>
        public bool RequestBagSplit(int from, int to, int count) => SendBagRequest(BagOp.Split, from, to, count);

        /// <summary>Заявка серверу: выбросить из ячейки на землю (<paramref name="count"/> = 0 — всю стопку).</summary>
        public bool RequestBagDrop(int cell, int count = 0) => SendBagRequest(BagOp.Drop, cell, cell, count);

        private bool SendBagRequest(BagOp op, int from, int to, int count)
        {
            if (State != NetClientState.InGame || (uint)from >= Inventory.Cells || (uint)to >= Inventory.Cells || count < 0)
            {
                return false;
            }

            int len = new BagRequestMessage { Op = op, From = (byte)from, To = (byte)to, Count = (ushort)Math.Min(count, ushort.MaxValue) }.Write(_buf);
            _transport.Send(_serverPeer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            return true;
        }

        private void OnPiles(ReadOnlySpan<byte> data)
        {
            int n = PilesMessage.Read(data, _pileScratch);
            PileMessagesSeen++;
            for (int i = 0; i < n; i++)
            {
                NetPile p = _pileScratch[i];
                int at = IndexOfPile(p.Id);
                if (at >= 0)
                {
                    _piles[at] = p;
                }
                else
                {
                    _piles.Insert(~at, p);
                }

                PileSeen?.Invoke(p);
            }
        }

        private void OnPileGone(in PileGoneMessage m)
        {
            PileGoneSeen++;
            int at = IndexOfPile(m.Id);
            if (at < 0)
            {
                return;
            }

            NetPile was = _piles[at];
            _piles.RemoveAt(at);
            PileGone?.Invoke(m, was);
        }

        /// <summary>Место кучи в списке; нет — дополнение места вставки (как у BinarySearch).</summary>
        private int IndexOfPile(int id)
        {
            int lo = 0, hi = _piles.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int v = _piles[mid].Id;
                if (v == id)
                {
                    return mid;
                }

                if (v < id)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return ~lo;
        }
    }
}
