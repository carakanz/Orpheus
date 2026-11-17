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
    /// C-028: взмах кирки (<see cref="PickStrike"/>) на сервере. Запрос «копать: начало» с киркой к пустому или природному тайлу —
    /// взмах: <see cref="PickStrike.SwingFrames"/> кадров ввода с зажатым копанием (время сервера, как у копания), в последний —
    /// удар: до трёх «каменных» тайлов в секторе получают удар в общей <see cref="Wear"/>; набравший нужное снимается правкой в
    /// такт (добыча — как у копания, кучей). После такта — ответ на запрос («принято», маска — снятые тайлы удара) и «взмах» (39)
    /// ударившему и игрокам рядом (трещины у всех).
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>Метка правки взмаха (вместе с <c>TagDig</c>): номер тайла удара — младшие биты.</summary>
        private const int TagStrike = 1 << 24;

        private sealed class StrikeRecord
        {
            public Player Player = null!;
            public StrikeMessage Msg;
        }

        private readonly List<StrikeRecord> _strikes = new List<StrikeRecord>();
        private readonly TileCoord[] _strikeTiles = new TileCoord[PickStrike.MaxTiles];
        private readonly ushort[] _strikeObjs = new ushort[PickStrike.MaxTiles];
        private readonly byte[] _strikeNeeded = new byte[PickStrike.MaxTiles];

        /// <summary>Удары по тайлам (общие для всех игроков; такты сервера, сброс через 5 с без ударов).</summary>
        public TileWear Wear { get; } = new TileWear(TileWear.ServerResetTicks);

        /// <summary>Взмахов кирки решено / тайлов задето / тайлов снято взмахом.</summary>
        public long StrikesResolved { get; private set; }

        /// <inheritdoc cref="StrikesResolved"/>
        public long StrikeTilesHit { get; private set; }

        /// <inheritdoc cref="StrikesResolved"/>
        public long StrikeTilesBroken { get; private set; }

        /// <summary>Сообщений «взмах» отправлено (на получателя — одно).</summary>
        public long StrikeMessagesSent { get; private set; }

        /// <summary>Кадров на текущее действие копания предметом в руках: взмах — пока кирка; ≤ 0 — прогресс стоит.</summary>
        private int DigFramesOf(EditState s, byte held)
        {
            if (!s.Digging)
            {
                return 0;
            }

            if (s.DigStrike)
            {
                return Items.DigToolOf(held) == ToolKind.Pick ? PickStrike.SwingFrames : 0;
            }

            return DigTimes.FramesFor(s.DigObj, Items.DigToolOf(held), Items.TierOf(held));
        }

        /// <summary>Конец взмаха: тайлы удара из позиции перед тактом, удары, снятие набравших; ответ — после такта.</summary>
        private void Strike(Player p, EditState s, WorldPos pos, byte held)
        {
            s.Digging = false;
            // Линия до тайла — как у меча (проверка П): сквозь стену, закрытую дверь, обрыв не бьёт; камни этой кирки прозрачны.
            WorldModel world = _sim.World;
            int n = PickStrike.Targets(world, pos, s.DigTile, DigTimes, held, _strikeTiles, _strikeObjs, _strikeNeeded,
                (from, t) => _sim.PickLineClear(from, t, c => PickStrike.IsTarget(DigTimes, world.GetObject(c), held)));
            var rec = new StrikeRecord
            {
                Player = p,
                Msg = new StrikeMessage { Striker = p.EntityId, RequestId = s.DigRequest, Count = n },
            };
            for (int k = 0; k < n; k++)
            {
                TileCoord t = _strikeTiles[k];
                int hits = Wear.Hit(t, _strikeObjs[k], _strikeNeeded[k], _sim.Tick);
                if (hits >= _strikeNeeded[k])
                {
                    BuildLayers.AfterDig(_sim.World, t, out byte newFloor, out bool keep, out BuildRecord rest);
                    _sim.RequestPlayerEdit(t, newFloor, ObjectId.Empty, keep, rest, p.EntityId,
                        TagDig | TagStrike | (s.DigRequest << TagLineBits) | k); // отказ очереди — тайл стоит, удары целы
                }

                rec.Msg.SetTile(k, new StrikeTile { Tile = t, Obj = _strikeObjs[k], Hits = (byte)hits, Needed = _strikeNeeded[k] });
            }

            StrikesResolved++;
            StrikeTilesHit += n;
            DigsCompleted++;
            _strikes.Add(rec);
        }

        /// <summary>Событие правки взмаха (из <see cref="SendEditReplies"/>): принятая — тайл «снят».</summary>
        private void OnStrikeEdit(Player p, ushort request, int index, EditReply reply)
        {
            if (reply != EditReply.Accepted)
            {
                return;
            }

            for (int i = 0; i < _strikes.Count; i++)
            {
                StrikeRecord r = _strikes[i];
                if (r.Player == p && r.Msg.RequestId == request && index < r.Msg.Count)
                {
                    StrikeTile t = r.Msg.Get(index);
                    t.Broken = true;
                    r.Msg.SetTile(index, t);
                    StrikeTilesBroken++;
                    return;
                }
            }
        }

        /// <summary>После такта: ответ на каждый взмах (маска — снятые тайлы) и «взмах» ударившему и игрокам рядом; старые удары — сброс.</summary>
        private void FlushStrikes()
        {
            for (int i = 0; i < _strikes.Count; i++)
            {
                StrikeRecord r = _strikes[i];
                uint mask = StrikeMessage.ReplySwingBit; // «принято» требует маски ≠ 0: взмах без снятых тайлов — только этот бит
                for (int k = 0; k < r.Msg.Count; k++)
                {
                    if (r.Msg.Get(k).Broken)
                    {
                        mask |= 1u << k;
                    }
                }

                if (r.Player.State == PlayerState.InGame)
                {
                    Reply(r.Player, r.Msg.RequestId, EditReply.Accepted, EditReply.Accepted, mask);
                }

                BroadcastStrike(r.Msg);
            }

            _strikes.Clear();
            Wear.Expire(_sim.Tick);
        }

        private void BroadcastStrike(in StrikeMessage m)
        {
            int len = m.Write(_buf);
            int striker = _sim.SlotOf(m.Striker);
            const int reach = DamageViewTiles * Torus.SubTilesPerTile;
            for (int p = 0; p < _players.Count; p++)
            {
                Player pl = _players[p];
                if (pl.State != PlayerState.InGame)
                {
                    continue;
                }

                if (pl.EntityId != m.Striker)
                {
                    int ps = _sim.SlotOf(pl.EntityId);
                    if (ps < 0 || striker < 0)
                    {
                        continue;
                    }

                    Int2 d = Torus.Delta(_sim.Shape, _sim.Entities[ps].Mover.Position, _sim.Entities[striker].Mover.Position);
                    if (Math.Abs(d.X) > reach || Math.Abs(d.Y) > reach)
                    {
                        continue;
                    }
                }

                _transport.Send(pl.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
                StrikeMessagesSent++;
            }
        }
    }
}
