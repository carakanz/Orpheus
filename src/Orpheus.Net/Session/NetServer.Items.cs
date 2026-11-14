using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// E2-1 (шаг 2.1): предметы на земле по сети и заявки на операции с сумкой.
    /// <para>
    /// Кучи идут не в снимках, а надёжными сообщениями канала 2 (как правки мира): куча меняется редко, а терять её
    /// нельзя. У каждого игрока сервер помнит, какие кучи и с каким количеством тот знает; после такта сверяет со
    /// списком симуляции в обзоре игрока и шлёт разницу: «кучи» (27) — новые и изменившиеся, «кучи нет» (28).
    /// Движение своего персонажа кучи не трогают — правок предсказания от них нет.
    /// </para>
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>Куча входит в обзор игрока ближе этого, тайлов по X / Y (с запасом к обзору 96 × 54 тайла).</summary>
        public const int PileEnterTilesX = 80;
        public const int PileEnterTilesY = 56;

        /// <summary>Куча уходит из обзора дальше этого, тайлов по X / Y (гистерезис: на границе не мигает).</summary>
        public const int PileLeaveTilesX = 96;
        public const int PileLeaveTilesY = 72;

        /// <summary>Сообщений «кучи» (27) отправлено.</summary>
        public long PileMessagesSent { get; private set; }

        /// <summary>Сообщений «кучи нет» (28) отправлено.</summary>
        public long PileGoneSent { get; private set; }

        /// <summary>Заявок на сумку (29) принято.</summary>
        public long BagRequestsReceived { get; private set; }

        private readonly NetPile[] _pileBatch = new NetPile[PilesMessage.MaxPiles];

        /// <summary>Заявок на сумку от одного игрока за такт; сверх — отказ (общая очередь такта — 256, хватает на 32 игрока).</summary>
        public const int MaxBagRequestsPerPlayerTick = 8;

        /// <summary>Заявок на сумку отклонено: пауза, лимит игрока за такт, мёртвый персонаж, очередь такта полна.</summary>
        public long BagRequestsRefused { get; private set; }

        /// <summary>
        /// Заявка на сумку (29). Отказ без ответа (сумка не меняется, сообщение 26 не придёт): во время паузы — заявки
        /// не копятся и после паузы не исполняются; больше <see cref="MaxBagRequestsPerPlayerTick"/> за такт от одного
        /// игрока — один клиент не вытесняет чужие заявки из общей очереди такта; персонаж мёртв; очередь такта полна.
        /// </summary>
        private void OnBagRequest(Player p, in BagRequestMessage m)
        {
            BagRequestsReceived++;
            if (Paused || p.BagRequestsThisTick >= MaxBagRequestsPerPlayerTick)
            {
                BagRequestsRefused++;
                return;
            }

            p.BagRequestsThisTick++;
            if (!_sim.RequestBagOp(p.EntityId, m.Op, m.From, m.To, m.Count)) // решает такт; итог — сумка (тип 26), если изменилась
            {
                BagRequestsRefused++;
            }
        }

        /// <summary>
        /// Почему симуляция не взяла правку игрока в очередь: персонаж мёртв — «ты мёртв» (C-004; раньше уходило
        /// «очередь полна»), иначе очередь такта полна.
        /// </summary>
        private EditReply QueueRefusal(int entityId)
        {
            int slot = _sim.SlotOf(entityId);
            return slot >= 0 && !_sim.Entities[slot].Alive ? EditReply.Dead : EditReply.QueueFull;
        }

        /// <summary>После такта: разница «что игрок знает о кучах» ↔ «что лежит в его обзоре» — сообщениями 27 и 28.</summary>
        private void SendPilesTo(Player pl, WorldPos self)
        {
            ReadOnlySpan<GroundPile> piles = _sim.Piles;
            List<KnownPile> known = pl.KnownPiles;
            if (piles.Length == 0 && known.Count == 0)
            {
                return;
            }

            MapShape shape = _sim.Shape;
            List<KnownPile> next = pl.KnownPilesNext;
            next.Clear();
            int batch = 0;
            int k = 0;
            for (int i = 0; i <= piles.Length; i++)
            {
                int id = i < piles.Length ? piles[i].Id : int.MaxValue;

                // Известные игроку кучи с номером меньше текущей — в симуляции их больше нет: подобраны или истекли.
                while (k < known.Count && known[k].Id < id)
                {
                    int taker = TakerOf(known[k].Id);
                    SendPileGone(pl, known[k].Id, taker != 0 ? PileGoneReason.Picked : PileGoneReason.Expired, taker);
                    k++;
                }

                if (i == piles.Length)
                {
                    break;
                }

                Int2 d = Torus.Delta(shape, self, piles[i].Position);
                int tx = Math.Abs(d.X) >> Torus.SubTileBits, ty = Math.Abs(d.Y) >> Torus.SubTileBits;
                bool wasKnown = k < known.Count && known[k].Id == id;
                if (wasKnown)
                {
                    ushort sentCount = known[k].Count;
                    k++;
                    if (tx > PileLeaveTilesX || ty > PileLeaveTilesY)
                    {
                        SendPileGone(pl, id, PileGoneReason.OutOfView, 0);
                        continue;
                    }

                    next.Add(new KnownPile { Id = id, Count = piles[i].Count });
                    if (sentCount == piles[i].Count)
                    {
                        continue;
                    }
                }
                else
                {
                    if (tx > PileEnterTilesX || ty > PileEnterTilesY)
                    {
                        continue;
                    }

                    next.Add(new KnownPile { Id = id, Count = piles[i].Count });
                }

                _pileBatch[batch++] = new NetPile { Id = id, Item = piles[i].Item, Count = piles[i].Count, Position = piles[i].Position };
                if (batch == _pileBatch.Length)
                {
                    FlushPiles(pl, batch);
                    batch = 0;
                }
            }

            if (batch > 0)
            {
                FlushPiles(pl, batch);
            }

            pl.KnownPiles = next;
            pl.KnownPilesNext = known;
        }

        private void FlushPiles(Player pl, int count)
        {
            int len = PilesMessage.Write(_buf, new ReadOnlySpan<NetPile>(_pileBatch, 0, count));
            _transport.Send(pl.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            PileMessagesSent++;
        }

        private void SendPileGone(Player pl, int id, PileGoneReason reason, int taker)
        {
            int len = new PileGoneMessage { Id = id, Reason = reason, Taker = taker }.Write(_buf);
            _transport.Send(pl.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            PileGoneSent++;
        }

        /// <summary>Кто в этом такте поднял кучу (последнее событие подбора с её номером); никто — 0.</summary>
        private int TakerOf(int pileId)
        {
            ReadOnlySpan<SimEvent> events = _sim.Events;
            for (int e = events.Length - 1; e >= 0; e--)
            {
                if (events[e].Kind == SimEventKind.PilePickup && events[e].Tag == pileId)
                {
                    return events[e].Requester;
                }
            }

            return 0;
        }
    }
}
