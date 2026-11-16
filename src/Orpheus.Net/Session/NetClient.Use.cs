using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// E2-5 (шаг 2.5): починка, действие с объектом (F) и открытый сундук на стороне клиента. Ничего не предсказывается:
    /// починка ждёт ответ на правку (21) и патч чанка, сундук — содержимое от сервера (35).
    /// </summary>
    public sealed partial class NetClient : IBuildRequests, IUseRequests
    {
        /// <summary>Открыт ли сундук (сервер прислал содержимое и ещё не сказал «закрыт»).</summary>
        public bool ChestOpen { get; private set; }

        /// <summary>Тайл открытого сундука.</summary>
        public TileCoord ChestTile { get; private set; }

        /// <summary>Содержимое открытого сундука (ячейки 0…29).</summary>
        public Inventory Chest { get; private set; }

        /// <summary>Сообщений «сундук» (35) и ответов на действие (34) принято.</summary>
        public int ChestStatesSeen { get; private set; }
        public int UseRepliesSeen { get; private set; }

        /// <summary>Последний ответ на действие с объектом.</summary>
        public UseReplyMessage LastUseReply { get; private set; }

        /// <summary>Ответ на действие (34) — для строки у курсора / в окне.</summary>
        public Action<UseReplyMessage>? UseReplied { get; set; }

        /// <summary>Сундук открылся, изменился или закрылся (35).</summary>
        public Action<ChestStateMessage>? ChestChanged { get; set; }

        /// <summary>
        /// Починить постройку на тайле (TileOp 5): без предсказания, ответ — общий ответ на правку (21, <see cref="EditReplied"/>);
        /// прочность придёт патчем чанка. Возвращает номер запроса; не в игре — 0.
        /// </summary>
        public ushort Repair(TileCoord tile)
        {
            if (State != NetClientState.InGame || World == null)
            {
                return 0;
            }

            ushort id = NextRequest();
            TileCoord t = Torus.Wrap(World.Shape, tile);
            SendRequest(new TileRequestMessage { RequestId = id, Op = TileOp.Repair, Frame = _input.LastFrame + 1, From = t, To = t });
            return id;
        }

        /// <summary>Действие с объектом на тайле (F): сундук — открыть, кровать — точка возрождения. Ответ — <see cref="UseReplied"/>.</summary>
        public bool RequestUse(TileCoord tile)
        {
            _chestWanted = true;
            return SendUse(UseOp.Use, tile);
        }

        // Окно сундука закрыто клиентом: «сундук» (35), отправленный сервером до «закрыть», окно не открывает.
        private bool _chestWanted;

        /// <summary>Закрыть открытый сундук (окно закрыто). Сразу считается закрытым — ответа нет.</summary>
        public bool RequestCloseChest()
        {
            if (!ChestOpen)
            {
                return false;
            }

            ChestOpen = false;
            _chestWanted = false;
            return SendUse(UseOp.Close, ChestTile);
        }

        private bool SendUse(UseOp op, TileCoord tile)
        {
            if (State != NetClientState.InGame || World == null)
            {
                return false;
            }

            int len = new UseRequestMessage { Op = op, Tile = Torus.Wrap(World.Shape, tile) }.Write(_buf);
            _transport.Send(_serverPeer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            return true;
        }

        private void OnUseReply(in UseReplyMessage m)
        {
            UseRepliesSeen++;
            LastUseReply = m;
            UseReplied?.Invoke(m);
        }

        private void OnChestState(in ChestStateMessage m)
        {
            ChestStatesSeen++;
            if (m.Open && !_chestWanted)
            {
                return;
            }

            ChestOpen = m.Open;
            ChestTile = m.Tile;
            Chest = m.Open ? m.Cells : default;
            ChestChanged?.Invoke(m);
        }
    }
}
