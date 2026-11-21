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

        /// <summary>E3-1: сообщений «урон» (37) отправлено — по одному на игрока-получателя.</summary>
        public long DamageMessagesSent { get; private set; }

        /// <summary>
        /// E3-1: радиус рассылки «урона», тайлов по каждой оси (тор): шире половины самого дальнего обзора (96 × 54 тайла) —
        /// цифра видна всем, у кого жертва на экране. Жертве и атакующему — всегда.
        /// </summary>
        public const int DamageViewTiles = 56;

        /// <summary>
        /// События боя такта (1-001, `02` 13.5): смерть и возрождение — канал 2 (надёжный) всем в игре; E3-1 — урон
        /// (37) игрокам рядом с жертвой (цифры урона и полоска врага — из событий сервера). Полоска своего здоровья —
        /// снимком (дельта <see cref="DeltaFields.Health"/>), удар по игроку — ещё и «удар решён» (0-029) с уроном.
        /// </summary>
        private void BroadcastCombatEvents()
        {
            ReadOnlySpan<SimEvent> events = _sim.Events;
            int n = 0;
            for (int e = 0; e < events.Length; e++)
            {
                SimEvent ev = events[e];
                if (ev.Kind == SimEventKind.Damage)
                {
                    BroadcastDamage(ev.Victim, ev.Requester, ev.Damage, ev.DamageType, ev.Tag == Simulation.DamageTagStrong);
                    continue;
                }

                if (ev.Kind != SimEventKind.Death && ev.Kind != SimEventKind.Respawn)
                {
                    continue;
                }

                n++;
                FlushEventBatches(); // C-035: «смерть»/«возрождение» вне пачки — что набрано раньше, уходит до них (порядок за тактом)
                int len = ev.Kind == SimEventKind.Death
                    ? DeathOf(ev.Victim, ev.Requester, ev.Damage, ev.DamageType).Write(_buf)
                    : new RespawnMessage { Victim = ev.Victim }.Write(_buf);
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
        /// «Смерть» с данными для экрана смерти (E3-1): персонажу — такты до возрождения по правилу ядра
        /// (<see cref="Simulation.RespawnDelayTicks"/>) и «у кровати» (<see cref="Simulation.RespawnsAtBed"/>); врагу — 0.
        /// </summary>
        private DeathMessage DeathOf(int victim, int attacker, int damage, byte damageType)
        {
            int slot = _sim.SlotOf(victim);
            bool character = slot >= 0 && _sim.Entities[slot].IsCharacter;
            return new DeathMessage
            {
                Victim = victim,
                Attacker = attacker,
                Damage = (byte)Math.Min(255, damage),
                DamageType = damageType,
                RespawnTicks = character ? (ushort)Simulation.RespawnDelayTicks : (ushort)0,
                Flags = character && _sim.RespawnsAtBed(victim) ? DeathMessage.FlagAtBed : (byte)0,
            };
        }

        /// <summary>
        /// «Урон» (E3-1): игрокам в игре, чей персонаж — жертва, атакующий или в <see cref="DamageViewTiles"/> от жертвы.
        /// Зовётся из событий такта и из решения угрозы вне такта (<c>Decide</c>) — события такта к тому моменту разосланы.
        /// </summary>
        private void BroadcastDamage(int victim, int attacker, int damage, byte damageType, bool strong)
        {
            if (damage <= 0)
            {
                return;
            }

            int vs = _sim.SlotOf(victim);
            if (vs < 0)
            {
                return;
            }

            WorldPos at = _sim.Entities[vs].Mover.Position;
            int len = new DamageMessage
            {
                Victim = victim,
                Attacker = attacker,
                Amount = (ushort)Math.Min(ushort.MaxValue, damage),
                DamageType = damageType,
                Flags = strong ? DamageMessage.FlagStrong : (byte)0,
            }.Write(_buf);
            const int reach = DamageViewTiles * Torus.SubTilesPerTile;
            for (int p = 0; p < _players.Count; p++)
            {
                Player pl = _players[p];
                if (pl.State != PlayerState.InGame)
                {
                    continue;
                }

                if (pl.EntityId != victim && pl.EntityId != attacker)
                {
                    int ps = _sim.SlotOf(pl.EntityId);
                    if (ps < 0)
                    {
                        continue;
                    }

                    Int2 d = Torus.Delta(_sim.Shape, _sim.Entities[ps].Mover.Position, at);
                    if (Math.Abs(d.X) > reach || Math.Abs(d.Y) > reach)
                    {
                        continue;
                    }
                }

                AppendEvent(pl, EventRecordKind.Damage, new ReadOnlySpan<byte>(_buf, 0, len)); // C-035: в пачку (40), не отдельным сообщением
                DamageMessagesSent++;
            }
        }

        /// <summary>
        /// Труп от удара, решённого вне такта (решение угрозы сервером, 1-001 / 0-033): события такта к моменту
        /// решения уже разосланы, а буфер событий очистит следующий <c>Step</c> — потому смерть идёт сразу, тем же
        /// сообщением и по тому же Facts-каналу, что и события такта (<see cref="CombatEventsSent"/> общий).
        /// </summary>
        private void BroadcastDeath(int victim, int attacker, int damage, byte damageType)
        {
            FlushEventBatches(); // C-035: смерть вне пачки — накопленные уроны (решение угрозы) уходят до смерти
            int len = DeathOf(victim, attacker, damage, damageType).Write(_buf);
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

        /// <summary>Сообщений «снаряд» отправлено (E3-2; на получателя — одно сообщение).</summary>
        public long ProjectileMessagesSent { get; private set; }

        /// <summary>
        /// Снаряды такта (E3-2, `02` 4.4): выстрелы, затем концы — сообщение «снаряд» (38) каналом 2: стрелку и игрокам в окне (выстрел — 56 т от точки выстрела, конец — 96 т от точки конца), отказ — только стрелку. Полёт
        /// между ними клиент считает сам; летящие до входа игрока снаряды он не видит (короткоживущие, ≤ 1,7 с).
        /// </summary>
        private void BroadcastProjectiles()
        {
            ReadOnlySpan<Projectile> shots = _sim.ShotsThisTick;
            ReadOnlySpan<ProjectileStop> stops = _sim.StopsThisTick;
            for (int k = 0; k < shots.Length + stops.Length; k++)
            {
                bool shot = k < shots.Length;
                ProjectileMessage m = shot ? ProjectileMessage.ShotOf(shots[k]) : ProjectileMessage.StopOf(stops[k - shots.Length]);
                int len = m.Write(_buf);
                bool ownerOnly = !shot && m.Reason == ProjectileEnd.Refused;
                int reach = (shot ? DamageViewTiles : DamageViewTiles + Projectiles.StopViewExtraTiles) * Torus.SubTilesPerTile;
                for (int p = 0; p < _players.Count; p++)
                {
                    Player pl = _players[p];
                    if (pl.State != PlayerState.InGame)
                    {
                        continue;
                    }

                    if (pl.EntityId != m.Owner)
                    {
                        // П E3-2, п. 7: как «урон» — стрелку всегда, остальным — в окне от точки выстрела / конца.
                        int ps = _sim.SlotOf(pl.EntityId);
                        if (ownerOnly || ps < 0)
                        {
                            continue;
                        }

                        Int2 d = Torus.Delta(_sim.Shape, _sim.Entities[ps].Mover.Position, m.Position);
                        if (Math.Abs(d.X) > reach || Math.Abs(d.Y) > reach)
                        {
                            continue;
                        }
                    }

                    AppendEvent(pl, EventRecordKind.Projectile, new ReadOnlySpan<byte>(_buf, 0, len)); // C-035: в пачку (40)
                    ProjectileMessagesSent++;
                }
            }
        }

        /// <summary>C-035: пачек «события» (40) отправлено — по одной на игрока за такт (при переполнении — больше).</summary>
        public long EventBatchesSent { get; private set; }

        /// <summary>C-035: буфер сборки пачки (40) — отдельный: <see cref="_buf"/> в этот момент держит исходное сообщение.</summary>
        private readonly byte[] _eventBatchBuf = new byte[EventBatchMessage.MaxBytes];

        /// <summary>
        /// C-035: запись в пачку событий игрока: <paramref name="msg"/> — целое одиночное сообщение (тип + тело),
        /// тип заменяется видом записи. Переполнение (лимит тела или <see cref="EventBatchMessage.MaxRecords"/>) —
        /// набранное уходит сразу, запись начинает вторую пачку.
        /// </summary>
        private void AppendEvent(Player pl, EventRecordKind kind, ReadOnlySpan<byte> msg)
        {
            int body = msg.Length - 1;
            if (pl.EventRecords == EventBatchMessage.MaxRecords ||
                pl.EventStaged + 1 + body > EventBatchMessage.MaxBodyBytes)
            {
                SendEventBatch(pl);
            }

            if (1 + body > EventBatchMessage.MaxBodyBytes)
            {
                throw new InvalidOperationException($"Пачка: запись {(byte)kind} длиннее пачки ({body} байт).");
            }

            pl.EventStaging[pl.EventStaged++] = (byte)kind;
            msg.Slice(1).CopyTo(pl.EventStaging.AsSpan(pl.EventStaged, body));
            pl.EventStaged += body;
            pl.EventRecords++;
        }

        /// <summary>C-035: пачка игрока наружу (пустая — ничего); счётчик — на пачку, не на запись.</summary>
        private void SendEventBatch(Player pl)
        {
            if (pl.EventRecords == 0)
            {
                return;
            }

            int len = EventBatchMessage.Write(_eventBatchBuf, new ReadOnlySpan<byte>(pl.EventStaging, 0, pl.EventStaged));
            _transport.Send(pl.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_eventBatchBuf, 0, len));
            pl.EventStaged = 0;
            pl.EventRecords = 0;
            EventBatchesSent++;
        }

        /// <summary>
        /// C-035: пачки всех игроков в игре наружу — перед снимками такта (<see cref="SendSnapshots"/>) и перед
        /// внепачечными «смертью»/«возрождением» (24/25), чтобы порядок за тактом не переехал.
        /// </summary>
        public void FlushEventBatches()
        {
            for (int p = 0; p < _players.Count; p++)
            {
                if (_players[p].State == PlayerState.InGame)
                {
                    SendEventBatch(_players[p]);
                }
            }
        }
    }
}
