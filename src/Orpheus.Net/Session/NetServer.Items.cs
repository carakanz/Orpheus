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

        /// <summary>Заявок изготовления (30) принято.</summary>
        public long CraftRequestsReceived { get; private set; }

        /// <summary>Заявок изготовления отклонено (любая причина, кроме «принято»).</summary>
        public long CraftRequestsRefused { get; private set; }

        /// <summary>Сообщений «очередь изготовления» (32) отправлено.</summary>
        public long CraftStatesSent { get; private set; }

        /// <summary>
        /// Заявка изготовления (30; E2-3): на каждую — <b>ровно один</b> ответ (31). Отказ сразу (пауза — заявки не копятся;
        /// лимит заявок игрока за такт, общий с сумкой; проверка симуляции: нет рецепта, мёртв, нет станка, не хватает…) —
        /// ответ уходит тут же. Заявка, взятая в очередь такта, ответа сразу не получает: его даёт такт — «принято» или
        /// причина (события <see cref="SimEventKind.CraftQueued"/> / <see cref="SimEventKind.CraftCancelled"/> /
        /// <see cref="SimEventKind.CraftRefused"/>, рассылка — <see cref="SendCraftTo"/>). Крафт клиент не предсказывает.
        /// </summary>
        private void OnCraftRequest(Player p, in CraftRequestMessage m)
        {
            CraftRequestsReceived++;
            CraftReply reply;
            if (Paused)
            {
                reply = CraftReply.Paused;
            }
            else if (p.BagRequestsThisTick >= MaxBagRequestsPerPlayerTick)
            {
                reply = CraftReply.Busy;
            }
            else
            {
                p.BagRequestsThisTick++;
                reply = m.Op == CraftOp.Make
                    ? _sim.RequestCraft(p.EntityId, m.Arg, m.Count)
                    : _sim.RequestCraftCancel(p.EntityId, m.Arg, m.Count);
            }

            if (reply != CraftReply.Accepted)
            {
                CraftRequestsRefused++;
                SendCraftReply(p, m.Op, m.Arg, reply);
            }
        }

        private void SendCraftReply(Player p, CraftOp op, byte arg, CraftReply reply)
        {
            int len = new CraftReplyMessage { Op = op, Arg = arg, Reply = reply }.Write(_buf);
            _transport.Send(p.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
        }

        /// <summary>
        /// После такта: ответы (31) на заявки, решённые этим тактом, — по событиям, в порядке заявок; очередь изготовления —
        /// сообщением 32, если изменился состав позиций или признак «идёт». Прогресс каждый такт не шлётся.
        /// </summary>
        private void SendCraftTo(Player pl, in SimEntity self)
        {
            ReadOnlySpan<SimEvent> events = _sim.Events;
            for (int e = 0; e < events.Length; e++)
            {
                if (events[e].Requester != self.Id)
                {
                    continue;
                }

                switch (events[e].Kind)
                {
                    case SimEventKind.CraftQueued:
                        SendCraftReply(pl, CraftOp.Make, (byte)events[e].Victim, CraftReply.Accepted);
                        break;
                    case SimEventKind.CraftCancelled:
                        SendCraftReply(pl, CraftOp.Cancel, (byte)events[e].Victim, CraftReply.Accepted);
                        break;
                    case SimEventKind.CraftRefused:
                        CraftRequestsRefused++;
                        SendCraftReply(pl, events[e].Damage == (int)BagOp.CraftCancel ? CraftOp.Cancel : CraftOp.Make,
                            (byte)events[e].Victim, (CraftReply)events[e].Tag);
                        break;
                }
            }

            bool running = !self.Craft.IsEmpty && self.Alive
                && _sim.CraftStationNear(self.Id, RecipeTable.Get(self.Craft.RecipeAt(0)).Station);
            if (pl.HasSentCraft ? (self.Craft.SameEntries(pl.SentCraft) && running == pl.SentCraftRunning) : self.Craft.IsEmpty)
            {
                return;
            }

            pl.SentCraft = self.Craft;
            pl.SentCraftRunning = running;
            pl.HasSentCraft = true;
            int len = new CraftStateMessage { EntityId = self.Id, Tick = (uint)_sim.Tick, Running = running, Queue = self.Craft }.Write(_buf);
            _transport.Send(pl.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            CraftStatesSent++;
        }

        /// <summary>Сводка «сундуки рядом» пересчитывается раз в столько тактов (и сразу после своего изготовления / отмены).</summary>
        public const int StockEveryTicks = 5;

        /// <summary>Сообщений «сундуки рядом» (36) отправлено.</summary>
        public long NearbyStockSent { get; private set; }

        private readonly ChestStock _stockScratch = new ChestStock();

        /// <summary>
        /// E2-10: после такта — сводка «сколько каждого предмета в сундуках рядом» (36) владельцу, если изменилась. Считается
        /// раз в <see cref="StockEveryTicks"/> тактов (сдвиг по номеру сущности — игроки не в одном такте) и в такте, где
        /// игрок поставил или отменил изготовление (сундуки рядом могли опустеть). Пустая сводка до первой непустой не шлётся.
        /// </summary>
        private void SendStockTo(Player pl)
        {
            bool due = (_sim.Tick + pl.EntityId) % StockEveryTicks == 0;
            if (!due)
            {
                ReadOnlySpan<SimEvent> events = _sim.Events;
                for (int e = 0; e < events.Length && !due; e++)
                {
                    due = events[e].Requester == pl.EntityId
                        && (events[e].Kind == SimEventKind.CraftQueued || events[e].Kind == SimEventKind.CraftCancelled);
                }
            }

            if (!due)
            {
                return;
            }

            _sim.FillChestStock(pl.EntityId, _stockScratch);
            if (pl.HasSentStock ? _stockScratch.Equals(pl.SentStock) : _stockScratch.IsEmpty)
            {
                return;
            }

            pl.SentStock.CopyFrom(_stockScratch);
            pl.HasSentStock = true;
            int len = new NearbyStockMessage { Stock = _stockScratch }.Write(_buf);
            _transport.Send(pl.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            NearbyStockSent++;
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
