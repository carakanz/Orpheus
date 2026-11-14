using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// E2-5 (шаг 2.5): починка построек, действие с объектом (F: сундук, кровать) и сундук по сети.
    /// <para>
    /// Сундук принадлежит миру (<see cref="Simulation.TryGetChest"/>); игрок его «открывает» — сервер помнит у игрока тайл
    /// открытого сундука и шлёт содержимое (35) при открытии и после каждого такта, в котором оно изменилось. Открыть один
    /// сундук могут несколько игроков сразу. Заявки на перенос — общим сообщением 29 с ячейками 40…69 (ячейки сундука),
    /// тем же лимитом заявок игрока за такт, что у сумки и крафта; без предсказания. Сундук снесли, игрок отошёл дальше
    /// <see cref="EditRules.UseRadiusTiles"/> тайлов, умер или ушёл из мира — сундук закрывается («закрыт», 35).
    /// </para>
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>Заявок действия (33) принято.</summary>
        public long UseRequestsReceived { get; private set; }

        /// <summary>Сообщений «сундук» (35) отправлено.</summary>
        public long ChestStatesSent { get; private set; }

        /// <summary>Заявок починки отклонено сразу (до такта).</summary>
        public long RepairsRefused { get; private set; }

        /// <summary>
        /// Починка (TileOp 5): молот в руках, проверка <see cref="EditRules.CheckRepair"/> (радиус, есть постройка, не цела,
        /// не под ударом) и цена по сумке — отказ сразу с причиной; иначе правка в очередь такта, ответ — после такта
        /// (общий путь линий постановки: одна «линия» из одного тайла).
        /// </summary>
        private void OnRepair(Player p, EditState s, WorldPos pos, in TileRequestMessage m, byte held)
        {
            EditReply check = EditRules.CheckRepair(_sim.World, pos, m.From, out BuildDef def, out int c1, out int c2);
            if (Items.DigToolOf(held) != ToolKind.Hammer)
            {
                check = EditReply.ToolRequired;
            }
            else if (Paused)
            {
                check = EditReply.Paused;
            }
            else if (s.PendingCount == MaxPendingPlaces)
            {
                check = EditReply.QueueFull;
            }

            int slot = _sim.SlotOf(p.EntityId);
            if (check == EditReply.Accepted && !EditRules.RepairAffordable(def, c1, c2, _sim.Entities[slot].Inv))
            {
                check = EditReply.NoResources;
            }

            if (check == EditReply.Accepted && !_sim.RequestRepair(m.From, p.EntityId, m.RequestId << TagLineBits))
            {
                check = QueueRefusal(p.EntityId);
            }

            if (check != EditReply.Accepted)
            {
                RepairsRefused++;
                Reply(p, m.RequestId, check, check, 0);
                return;
            }

            int k = s.PendingCount++;
            s.PendingId[k] = m.RequestId;
            s.PendingMask[k] = 0;
            s.PendingFirst[k] = EditReply.Accepted;
            s.PendingLeft[k] = 1;
        }

        /// <summary>
        /// Действие с объектом (33). «Использовать»: пауза, мёртвый, далеко (<see cref="EditRules.InUseRadius"/>) — отказ;
        /// сундук — открыть (содержимое — 35), кровать — точка возрождения (<see cref="Simulation.RequestSetBed"/>); прочее —
        /// «пусто». Ответ (34) — на каждое «использовать». «Закрыть» — без ответа. Лимит — общий с сумкой и крафтом.
        /// </summary>
        private void OnUseRequest(Player p, in UseRequestMessage m)
        {
            UseRequestsReceived++;
            if (m.Op == UseOp.Close)
            {
                CloseChest(p, notify: false);
                return;
            }

            ushort obj = _sim.World.GetObject(m.Tile);
            int slot = _sim.SlotOf(p.EntityId);
            EditReply reply;
            if (Paused)
            {
                reply = EditReply.Paused;
            }
            else if (p.BagRequestsThisTick >= MaxBagRequestsPerPlayerTick)
            {
                reply = EditReply.QueueFull;
            }
            else if (slot < 0)
            {
                reply = EditReply.NotAllowed;
            }
            else if (!_sim.Entities[slot].Alive)
            {
                reply = EditReply.Dead;
            }
            else if (!EditRules.InUseRadius(_sim.Shape, _sim.Entities[slot].Mover.Position, m.Tile))
            {
                reply = EditReply.OutOfReach;
            }
            else
            {
                p.BagRequestsThisTick++;
                switch (ObjectId.IdOf(obj))
                {
                    case ObjectId.Chest:
                        CloseChest(p, notify: p.ChestOpen && p.ChestTile != m.Tile);
                        p.ChestOpen = true;
                        p.ChestTile = m.Tile;
                        p.ChestSent = false;
                        reply = EditReply.Accepted;
                        break;
                    case ObjectId.Bed:
                        reply = _sim.RequestSetBed(p.EntityId, m.Tile);
                        break;
                    default:
                        reply = EditReply.Empty;
                        break;
                }
            }

            int len = new UseReplyMessage { Tile = m.Tile, Obj = obj, Reply = reply }.Write(_buf);
            _transport.Send(p.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            if (reply == EditReply.Accepted && p.ChestOpen && p.ChestTile == m.Tile && slot >= 0)
            {
                SendChestTo(p, slot); // открыл — содержимое сразу, не ждать такта
            }
        }

        /// <summary>
        /// Заявка на сумку (29) с ячейкой открытого сундука (40…69): лимит и пауза — как у сумки (<see cref="OnBagRequest"/>);
        /// сундук не открыт — отказ без ответа. Решает такт (<see cref="Simulation.RequestChestOp"/>): сундук на месте и рядом.
        /// </summary>
        private void OnChestBagRequest(Player p, in BagRequestMessage m)
        {
            BagRequestsReceived++;
            if (Paused || p.BagRequestsThisTick >= MaxBagRequestsPerPlayerTick || !p.ChestOpen)
            {
                BagRequestsRefused++;
                return;
            }

            p.BagRequestsThisTick++;
            if (!_sim.RequestChestOp(p.EntityId, p.ChestTile, m.Op, m.From, m.To, m.Count))
            {
                BagRequestsRefused++;
            }
        }

        /// <summary>После такта: открытый сундук игрока — закрыть, если его нет, игрок далеко или мёртв; иначе содержимое, если изменилось.</summary>
        private void SendChestTo(Player p, int slot)
        {
            if (!p.ChestOpen)
            {
                return;
            }

            if (slot < 0 || !_sim.Entities[slot].Alive
                || !EditRules.InUseRadius(_sim.Shape, _sim.Entities[slot].Mover.Position, p.ChestTile)
                || !_sim.TryGetChest(p.ChestTile, out Inventory content))
            {
                CloseChest(p, notify: true);
                return;
            }

            if (p.ChestSent && content == p.SentChest)
            {
                return;
            }

            p.SentChest = content;
            p.ChestSent = true;
            SendChestState(p, p.ChestTile, true, content);
        }

        /// <summary>Закрыть сундук игрока; <paramref name="notify"/> — сказать клиенту «закрыт» (35).</summary>
        private void CloseChest(Player p, bool notify)
        {
            if (!p.ChestOpen)
            {
                return;
            }

            p.ChestOpen = false;
            p.ChestSent = false;
            if (notify)
            {
                SendChestState(p, p.ChestTile, false, default);
            }
        }

        private void SendChestState(Player p, TileCoord tile, bool open, in Inventory cells)
        {
            int len = new ChestStateMessage { Tile = tile, Open = open, Cells = cells }.Write(_buf);
            _transport.Send(p.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            ChestStatesSent++;
        }
    }
}
