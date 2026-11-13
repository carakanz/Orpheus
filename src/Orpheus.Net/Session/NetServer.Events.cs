using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// P-002: partial NetServer - per-file code motion, no behavior change.
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>
        /// «Часы после фриза» (2.4): сервер отстал больше чем на 3 такта и не догоняет — «часы: кадр N» всем в игре по каналу 2,
        /// эпоха часов +1 (в заголовке каждого следующего снимка). N — такт, который идёт (последний шагнутый + 1): зовётся
        /// между тактами, как и разбор пакетов.
        /// </summary>
        public void BroadcastClock()
        {
            ClockEpoch++;
            int len = new ClockMessage { ServerTick = (uint)_sim.Tick + 1 }.Write(_buf);
            for (int p = 0; p < _players.Count; p++)
            {
                if (_players[p].State == PlayerState.InGame)
                {
                    _players[p].ClockStale = true; // до перенумерации назад (OnInput)
                    _players[p].ClockStaleSnapshot = (uint)_sim.Tick + 1;
                    _transport.Send(_players[p].Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
                }
            }

            _transport.Flush();
        }

        /// <summary>
        /// События боя такта (1-001, `02` 13.5): смерть и возрождение — канал 2 (надёжный) всем в игре; урон
        /// отдельно не шлётся — полоска здоровья приходит снимком (дельта <see cref="DeltaFields.Health"/>), удар
        /// по игроку — «удар решён» (0-029) с уроном.
        /// </summary>
        private void BroadcastCombatEvents()
        {
            ReadOnlySpan<SimEvent> events = _sim.Events;
            int n = 0;
            for (int e = 0; e < events.Length; e++)
            {
                SimEvent ev = events[e];
                if (ev.Kind != SimEventKind.Death && ev.Kind != SimEventKind.Respawn)
                {
                    continue;
                }

                n++;
                int len;
                if (ev.Kind == SimEventKind.Death)
                {
                    len = new DeathMessage
                    {
                        Victim = ev.Victim,
                        Attacker = ev.Requester,
                        Damage = (byte)Math.Min(255, ev.Damage),
                        DamageType = ev.DamageType,
                    }.Write(_buf);
                }
                else
                {
                    len = new RespawnMessage { Victim = ev.Victim }.Write(_buf);
                }

                for (int p = 0; p < _players.Count; p++)
                {
                    Player pl = _players[p];
                    if (pl.State == PlayerState.InGame)
                    {
                        _transport.Send(pl.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
                    }
                }
            }

            CombatEventsSent += n;
        }

        /// <summary>
        /// Труп от удара, решённого вне такта (решение угрозы сервером, 1-001 / 0-033): события такта к моменту
        /// решения уже разосланы, а буфер событий очистит следующий <c>Step</c> — потому смерть идёт сразу, тем же
        /// сообщением и по тому же Facts-каналу, что и события такта (<see cref="CombatEventsSent"/> общий).
        /// </summary>
        private void BroadcastDeath(int victim, int attacker, int damage, byte damageType)
        {
            int len = new DeathMessage
            {
                Victim = victim,
                Attacker = attacker,
                Damage = (byte)Math.Min(255, damage),
                DamageType = damageType,
            }.Write(_buf);
            for (int p = 0; p < _players.Count; p++)
            {
                Player pl = _players[p];
                if (pl.State == PlayerState.InGame)
                {
                    _transport.Send(pl.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
                }
            }

            CombatEventsSent++;
        }

    }
}
