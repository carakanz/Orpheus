using System;
using System.Collections.Generic;
using Orpheus.Client.Anim;
using Orpheus.Core.Bots;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.Trig;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Server;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E3-1 (шаг 3.1): урон цифрами, смерть и возрождение на экране. Протокол 30: «урон» (37) игрокам рядом с жертвой,
    /// в «смерти» (24) — такты до возрождения и «у кровати». Сильный удар — добивающий серии меча (метка события ядра).
    /// Клиентская лента <see cref="CombatFeed"/> и падение <see cref="DeathAnim"/> — чистый C#.
    /// </summary>
    public sealed class E31CombatFeedTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const long TickUs = 1_000_000 / 30;

        private static WorldModel Flat(int tiles, ulong seed) => FlatWorldGenerator.Instance.Generate(MapShape.World(tiles), seed);

        // ---------- кодек ----------

        [Fact]
        public void E31_Codec_GoldenBytes_Damage_Death()
        {
            var buf = new byte[64];
            int n = new DamageMessage { Victim = 0x01020304, Attacker = 7, Amount = 0x0113, DamageType = 1, Flags = DamageMessage.FlagStrong }.Write(buf);
            Assert.Equal(DamageMessage.Bytes, n);
            Assert.Equal(new byte[] { 37, 0x04, 0x03, 0x02, 0x01, 7, 0, 0, 0, 0x13, 0x01, 1, 1 }, buf.AsSpan(0, n).ToArray());
            DamageMessage d = DamageMessage.Read(buf.AsSpan(0, n));
            Assert.Equal(0x01020304, d.Victim);
            Assert.Equal(7, d.Attacker);
            Assert.Equal(0x0113, d.Amount);
            Assert.True(d.Strong);

            n = new DeathMessage { Victim = 5, Attacker = 9, Damage = 10, DamageType = 1, RespawnTicks = 150, Flags = DeathMessage.FlagAtBed }.Write(buf);
            Assert.Equal(DeathMessage.Bytes, n);
            Assert.Equal(new byte[] { 24, 5, 0, 0, 0, 9, 0, 0, 0, 10, 1, 150, 0, 1 }, buf.AsSpan(0, n).ToArray());
            DeathMessage m = DeathMessage.Read(buf.AsSpan(0, n));
            Assert.Equal(150, m.RespawnTicks);
            Assert.True(m.AtBed);
            Assert.Equal(35, NetModule.ProtocolVersion); // E3-6: 35 (надетое тремя байтами в сумке 26) // C-028: 34 (взмах, тип 39); C-030: 33; C-031: 32 (вид и ось в кучах 27); E3-2: 31 (снаряд, тип 38); E3-1: 30
            Assert.True(Codec.IsKnown((byte)MessageType.Damage));
            Assert.Equal(MessageType.Projectile, (MessageType)((byte)MessageType.Damage + 1));
        }

        [Fact]
        public void E31_Codec_Damage_RejectsZeroAndUnknownFlags()
        {
            var buf = new byte[64];
            int n = new DamageMessage { Victim = 1, Attacker = 2, Amount = 0 }.Write(buf);
            Assert.Throws<ProtocolException>(() => DamageMessage.Read(buf.AsSpan(0, n)));
            n = new DamageMessage { Victim = 1, Attacker = 2, Amount = 3, Flags = 2 }.Write(buf);
            Assert.Throws<ProtocolException>(() => DamageMessage.Read(buf.AsSpan(0, n)));
            n = new DeathMessage { Victim = 1, Flags = 4 }.Write(buf);
            Assert.Throws<ProtocolException>(() => DeathMessage.Read(buf.AsSpan(0, n)));
        }

        // ---------- ядро: сильный удар, кровать ----------

        /// <summary>Серия меча 10/10/13/10: «сильный» — только добивающий (третий) удар серии.</summary>
        [Fact]
        public void E31_Sim_SwordFinisher_TaggedStrong()
        {
            var tags = SwordHits(out var amounts);
            Assert.Equal(new[] { 10, 10, 13, 10 }, amounts);
            Assert.Equal(new[] { 0, 0, Simulation.DamageTagStrong, 0 }, tags);
        }

        private static int[] SwordHits(out int[] amounts)
        {
            var map = MapShape.World(64);
            var sim = new Simulation(FlatWorldGenerator.Instance.Generate(map, 7));
            foreach (int x in new[] { 20, 21, 22 })
            {
                Assert.True(sim.RequestEdit(new TileCoord(x, 32), FloorId.Grass, ObjectId.Empty));
            }

            int ch = sim.AddEntity(new WorldPos(20 * U + U / 2, 32 * U + U / 2));
            var inv = Inventory.StartingKit();
            inv.Selected = 3; // деревянный меч
            Assert.True(sim.SetInventory(ch, inv));
            int en = sim.AddEnemy(new WorldPos(22 * U, 32 * U + U / 2));
            int slot = sim.SlotOf(ch);
            var tags = new List<int>();
            var hits = new List<int>();
            var frames = new InputFrame[2];
            for (int t = 0; t < 120 && sim.GetEntity(en).Health > 0; t++)
            {
                WorldPos me = sim.GetEntity(ch).Mover.Position, him = sim.GetEntity(en).Mover.Position;
                Input8 dir = Torus.Delta(map, me, him).X > 430 ? Input8.East : Input8.None;
                frames[0] = frames[1] = new InputFrame(dir, InputButtons.Attack, 0, 3);
                sim.SetFramesAt(slot, frames);
                sim.Step();
                foreach (SimEvent ev in sim.Events)
                {
                    if (ev.Kind == SimEventKind.Damage && ev.Victim == en)
                    {
                        hits.Add(ev.Damage);
                        tags.Add(ev.Tag);
                    }
                }
            }

            amounts = hits.ToArray();
            return tags.ToArray();
        }

        [Fact]
        public void E31_Sim_RespawnsAtBed_OnlyWhileBedStands()
        {
            WorldModel world = Flat(64, 5);
            var sim = new Simulation(world);
            int id = sim.AddEntity(new WorldPos(10 * U, 10 * U));
            Assert.False(sim.RespawnsAtBed(id)); // кровати нет — у Очага
            var bed = new TileCoord(12, 10);
            world.SetBaseTile(bed, FloorId.Grass, ObjectId.Bed);
            Assert.Equal(EditReply.Accepted, sim.RequestSetBed(id, bed));
            Assert.True(sim.RespawnsAtBed(id));
            world.SetBaseTile(bed, FloorId.Grass, ObjectId.Empty); // кровать снесли
            Assert.False(sim.RespawnsAtBed(id));
            Assert.True(sim.GetEntity(id).HasBed); // только чтение: назначение снимет само возрождение
            Assert.False(sim.RespawnsAtBed(9999));
        }

        // ---------- сеть: урон доходит клиентам, детерминирован ----------

        private sealed class NetRun
        {
            public readonly List<string> SeenA = new List<string>();
            public readonly List<string> SeenB = new List<string>();
            public readonly List<string> Server = new List<string>();
            public readonly List<DeathMessage> DeathsA = new List<DeathMessage>();
            public long DamageSent;
            public int IdA, IdB, IdC, Sword, Enemy, Rogue;
            public long FarSeen;
        }

        /// <summary>
        /// Сервер и два клиента (связь с задержкой 100 мс и разбросом): по A бьёт манекен (урон решает сервер вне такта —
        /// <c>Decide</c>), B прижат к стене и его бьёт враг ядра (событие такта), у B — кровать; рядом персонаж-болванка
        /// ядра бьёт мечом разбойника (серия, добивающий — «сильный»). Счёт событий «урон» сервера — по тактам и решениям.
        /// </summary>
        private static NetRun Run(int seconds)
        {
            WorldModel world = Flat(1024, 31);
            var sim = new Simulation(world, maxEntities: 12);
            WorldPos a = ServerRun.SpawnPoint(world.Shape, 0), b = ServerRun.SpawnPoint(world.Shape, 1);
            TileCoord ta = Torus.TileOf(world.Shape, a), tb = Torus.TileOf(world.Shape, b);
            for (int dy = -1; dy <= 1; dy++)
            {
                world.SetBaseTile(new TileCoord(ta.X - 2, ta.Y + dy), FloorId.Grass, ObjectId.StoneWall);
                world.SetBaseTile(new TileCoord(tb.X - 1, tb.Y + dy), FloorId.Grass, ObjectId.StoneWall);
            }

            var bed = new TileCoord(tb.X, tb.Y + 3);
            world.SetBaseTile(bed, FloorId.Grass, ObjectId.Bed);
            sim.RespawnCenter = b;
            var r = new NetRun();
            r.Enemy = sim.AddEnemy(Torus.Offset(world.Shape, b, new Int2(U / 2, 0)));
            Assert.True(sim.EnableAttackCycle(r.Enemy, Simulation.DummyPeriodTicks, Simulation.StrikeRawMelee));

            // болванка с мечом и разбойник (без мозга) — в 4 тайлах к северу от A: их урон виден обоим клиентам
            WorldPos sw = Torus.Offset(world.Shape, a, new Int2(0, -4 * U));
            for (int dx = 0; dx <= 8; dx++)
            {
                world.SetBaseTile(Torus.Wrap(world.Shape, new TileCoord(ta.X + dx, ta.Y - 4)), FloorId.Grass, ObjectId.Empty);
            }

            r.Sword = sim.AddEntity(sw);
            var inv = Inventory.StartingKit();
            inv.Selected = 3;
            Assert.True(sim.SetInventory(r.Sword, inv));
            r.Rogue = sim.AddEnemy(Torus.Offset(world.Shape, sw, new Int2(3 * U / 2, 0)));
            int swordSlot = sim.SlotOf(r.Sword);

            using LoopbackTransport st = LoopbackTransport.CreateServer(4);
            WorldPos far = Torus.Offset(world.Shape, a, new Int2(200 * U, 0)); // C — в 200 тайлах: урон рядом с A и B ему не шлётся
            var server = new NetServer(st, sim, i => i == 0 ? a : i == 1 ? b : far, 3, NetModule.Version);
            server.SetDummy(new MeleeDummy(1000, Torus.Offset(world.Shape, a, new Int2(U / 2, 0)),
                Angle16.FromDirection(new Int2(-1, 0)), firstSwingFrame: 180, periodFrames: DodgeShipTests.SwingPeriodFrames));
            var ca = new NetClient(new NetSimTransport(LoopbackTransport.ConnectTo(st), NetSimSettings.FromRtt(100, 10, 1.0, 5)),
                LoopbackTransport.ServerPeer, "e31-a", NetModule.Version);
            var cb = new NetClient(new NetSimTransport(LoopbackTransport.ConnectTo(st), NetSimSettings.FromRtt(100, 10, 1.0, 6)),
                LoopbackTransport.ServerPeer, "e31-b", NetModule.Version);
            var cc = new NetClient(new NetSimTransport(LoopbackTransport.ConnectTo(st), NetSimSettings.FromRtt(100, 10, 1.0, 7)),
                LoopbackTransport.ServerPeer, "e31-c", NetModule.Version);
            var botC = new NetBot(cc, IdleBot.Name, 2) { PredictionFactory = c => new LocalPrediction(c.Shape, sim.Solid, sim.RespawnCenter) };
            var botA = new NetBot(ca, IdleBot.Name, 0) { PredictionFactory = c => new LocalPrediction(c.Shape, sim.Solid, sim.RespawnCenter) };
            var botB = new NetBot(cb, IdleBot.Name, 1) { PredictionFactory = c => new LocalPrediction(c.Shape, sim.Solid, sim.RespawnCenter) };
            ca.DamageSeen = m => r.SeenA.Add(Key(m.Victim, m.Attacker, m.Amount, m.Strong));
            cb.DamageSeen = m => r.SeenB.Add(Key(m.Victim, m.Attacker, m.Amount, m.Strong));
            ca.DeathSeen = m => r.DeathsA.Add(m);

            int carried = 0;
            bool bedSet = false;
            var swordFrames = new InputFrame[2];
            long tick = 0, now;
            void Collect(int from)
            {
                ReadOnlySpan<SimEvent> ev = sim.Events;
                for (int i = from; i < ev.Length; i++)
                {
                    if (ev[i].Kind == SimEventKind.Damage)
                    {
                        r.Server.Add(Key(ev[i].Victim, ev[i].Requester, ev[i].Damage, ev[i].Tag == Simulation.DamageTagStrong));
                    }
                }
            }

            void TickOnce(long t)
            {
                server.Poll(t);
                Collect(carried); // решения угроз вне такта (манекен по A)
                if (!bedSet && cb.EntityId > 0)
                {
                    Assert.Equal(EditReply.Accepted, sim.RequestSetBed(cb.EntityId, bed));
                    bedSet = true;
                }

                // болванка с мечом: удержание удара, прицел на восток (0), догоняет отброшенного разбойника
                if (sim.SlotOf(r.Sword) == swordSlot && sim.SlotOf(r.Rogue) >= 0)
                {
                    int dx = Torus.Delta(world.Shape, sim.GetEntity(r.Sword).Mover.Position, sim.GetEntity(r.Rogue).Mover.Position).X;
                    swordFrames[0] = swordFrames[1] = new InputFrame(dx > 430 ? Input8.East : Input8.None, InputButtons.Attack, 0, 3);
                    sim.SetFramesAt(swordSlot, swordFrames);
                }

                sim.Step();
                Collect(0);
                carried = sim.Events.Length;
                server.SendSnapshots();
            }

            for (now = 0; now <= (long)seconds * 1_000_000; now += 1000)
            {
                if (now >= tick * TickUs)
                {
                    TickOnce(now);
                    tick++;
                }

                botA.Frame(now);
                botB.Frame(now);
                botC.Frame(now);
            }

            for (int d = 0; d < 60; d++)
            {
                now += TickUs;
                TickOnce(now);
                botA.Frame(now);
                botB.Frame(now);
                botC.Frame(now);
            }

            r.FarSeen = cc.DamagesSeen;
            r.IdC = cc.EntityId;
            r.IdA = ca.EntityId;
            r.IdB = cb.EntityId;
            r.DamageSent = server.DamageMessagesSent;
            Assert.Equal(r.SeenA.Count, ca.DamagesSeen);
            return r;
        }

        private static string Key(int victim, int attacker, int amount, bool strong) => $"{attacker}>{victim}:{amount}{(strong ? "!" : "")}";

        [Fact]
        public void E31_Net_DamageEvents_ReachBothClients_StrongAndDeathTimer()
        {
            NetRun r = Run(20);

            // каждое событие «урон» сервера (такта и решения угрозы) дошло обоим клиентам — в том же порядке, ровно один раз
            Assert.True(r.Server.Count >= 10, $"Событий урона: {r.Server.Count}: {string.Join(" ", r.Server)}; A #{r.IdA}, B #{r.IdB}, меч #{r.Sword}, разбойник #{r.Rogue}, враг #{r.Enemy}.");
            Assert.Equal(r.Server, r.SeenA);
            Assert.Equal(r.Server, r.SeenB);
            Assert.Equal(2L * r.Server.Count, r.DamageSent);
            Assert.True(r.IdC > 0, "C вошёл в игру");
            Assert.Equal(0, r.FarSeen); // далёкому игроку — ни одного

            // все три источника есть: манекен по A (вне такта), враг ядра по B (такт), меч болванки по разбойнику (сильный)
            Assert.Contains(r.SeenA, k => k.EndsWith($">{r.IdA}:10", StringComparison.Ordinal) && k.StartsWith("1000>", StringComparison.Ordinal));
            Assert.Contains(r.SeenA, k => k == Key(r.IdB, r.Enemy, 10, false));
            Assert.Contains(r.SeenA, k => k == Key(r.Rogue, r.Sword, 13, true));
            Assert.Contains(r.SeenB, k => k == Key(r.Rogue, r.Sword, 10, false));
            Assert.DoesNotContain(r.SeenA, k => k.EndsWith(":10!", StringComparison.Ordinal)); // 10 — никогда не «сильный»

            // смерть персонажа несёт таймер ядра (5 с); у B — кровать, у A — нет; враг не возрождается
            DeathMessage da = r.DeathsA.Find(m => m.Victim == r.IdA);
            DeathMessage db = r.DeathsA.Find(m => m.Victim == r.IdB);
            DeathMessage dr = r.DeathsA.Find(m => m.Victim == r.Rogue);
            Assert.Equal(r.IdA, da.Victim);
            Assert.Equal(r.IdB, db.Victim);
            Assert.Equal(r.Rogue, dr.Victim);
            Assert.Equal(Simulation.RespawnDelayTicks, da.RespawnTicks);
            Assert.Equal(Simulation.RespawnDelayTicks, db.RespawnTicks);
            Assert.False(da.AtBed);
            Assert.True(db.AtBed);
            Assert.Equal(0, dr.RespawnTicks);
        }

        [Fact]
        public void E31_Net_DamageEvents_Deterministic_SameRunSameStream()
        {
            NetRun x = Run(12), y = Run(12);
            Assert.True(x.SeenA.Count >= 6, $"Событий: {x.SeenA.Count}.");
            Assert.Equal(x.SeenA, y.SeenA);
            Assert.Equal(x.SeenB, y.SeenB);
            Assert.Equal(x.Server, y.Server);
        }

        // ---------- лента клиента ----------

        [Fact]
        public void E31_Feed_ToneLaneLife()
        {
            var f = new CombatFeed { LocalId = 5 };
            f.OnDamage(new DamageMessage { Victim = 9, Attacker = 5, Amount = 10 }, default, victimIsCharacter: false, now: 1.0);
            f.OnDamage(new DamageMessage { Victim = 9, Attacker = 6, Amount = 13, Flags = DamageMessage.FlagStrong }, default, false, 1.1);
            f.OnDamage(new DamageMessage { Victim = 5, Attacker = 9, Amount = 7 }, default, true, 1.2);
            f.OnDamage(new DamageMessage { Victim = 6, Attacker = 9, Amount = 4 }, default, true, 1.25);
            Assert.Equal(4, f.Numbers.Count);
            Assert.Equal(DamageTone.ByMe, f.Numbers[0].Tone);
            Assert.Equal(DamageTone.Other, f.Numbers[1].Tone);
            Assert.True(f.Numbers[1].Strong);
            Assert.Equal(DamageTone.ToPlayer, f.Numbers[2].Tone); // урон себе — красный
            Assert.Equal(DamageTone.ToPlayer, f.Numbers[3].Tone); // и другому игроку
            Assert.Equal(0, f.Numbers[0].Lane);
            Assert.Equal(1, f.Numbers[1].Lane); // второй удар по тому же — соседняя дорожка

            f.Expire(1.0 + CombatFeed.NumberSec - 0.001);
            Assert.Equal(4, f.Numbers.Count);
            f.Expire(1.0 + CombatFeed.NumberSec);
            Assert.Equal(3, f.Numbers.Count); // 0,8 с — и цифры нет

            Assert.Equal(0f, CombatFeed.Rise(0));
            Assert.Equal(CombatFeed.RiseTiles, CombatFeed.Rise(CombatFeed.NumberSec), 3);
            Assert.True(CombatFeed.Rise(0.2) > CombatFeed.Rise(0.1));
            Assert.Equal(1f, CombatFeed.Alpha(0.3));
            Assert.Equal(0f, CombatFeed.Alpha(CombatFeed.NumberSec), 3);
            Assert.True(CombatFeed.Scale(0.5, strong: true) > CombatFeed.Scale(0.5, strong: false) * 1.4f);
        }

        [Fact]
        public void E31_Feed_EnemyBar_FourSecondsAfterLastHit_EliteAlways()
        {
            var f = new CombatFeed { LocalId = 5 };
            Assert.False(f.BarVisible(9, 0.0, always: false, healthPct: 100)); // не били — нет
            Assert.True(f.BarVisible(10, 0.0, always: true, healthPct: 100)); // элитный — всегда
            f.OnDamage(new DamageMessage { Victim = 9, Attacker = 5, Amount = 10 }, default, false, 1.0);
            f.OnDamage(new DamageMessage { Victim = 9, Attacker = 6, Amount = 10 }, default, false, 3.0);
            Assert.True(f.BarVisible(9, 6.9, false, 40));
            f.Expire(7.0);
            Assert.True(f.BarVisible(9, 7.0, false, 40)); // 4 с после последнего
            Assert.False(f.BarVisible(9, 7.5, false, 40)); // и без уборки ленты — гаснет по времени
            f.Expire(7.01);
            Assert.False(f.BarVisible(9, 7.01, false, 40));
            Assert.False(f.BarVisible(10, 0.0, true, 0)); // мёртвому полоски нет
            f.OnDamage(new DamageMessage { Victim = 5, Attacker = 9, Amount = 10 }, default, true, 8.0);
            Assert.False(f.BarVisible(5, 8.0, false, 90)); // урон игроку полоску над ним не включает
            f.OnDamage(new DamageMessage { Victim = 9, Attacker = 5, Amount = 10 }, default, false, 8.0);
            f.OnDeath(new DeathMessage { Victim = 9, Attacker = 5 }, 8.1);
            Assert.False(f.BarVisible(9, 8.1, false, 1)); // смерть гасит полоску
            Assert.False(f.Dead); // чужая смерть — не экран смерти
        }

        [Fact]
        public void E31_Feed_DeathScreen_TimerFromServer_RespawnNote()
        {
            var f = new CombatFeed { LocalId = 5 };
            f.OnRespawn(new RespawnMessage { Victim = 5 }, 0.5); // без смерти — ничего
            Assert.False(f.RespawnNote(0.6));
            f.OnDeath(new DeathMessage { Victim = 5, Attacker = 9, RespawnTicks = Simulation.RespawnDelayTicks, Flags = DeathMessage.FlagAtBed }, 10.0);
            Assert.True(f.Dead);
            Assert.True(f.AtBed);
            Assert.Equal(9, f.KillerId);
            Assert.Equal(5, f.RespawnLeftWhole(10.0));
            Assert.Equal(5, f.RespawnLeftWhole(10.2));
            Assert.Equal(4, f.RespawnLeftWhole(11.0));
            Assert.Equal(1, f.RespawnLeftWhole(14.5));
            Assert.Equal(0, f.RespawnLeftWhole(15.0));
            Assert.Equal(0, f.RespawnLeftWhole(30.0));
            Assert.Equal(0f, f.Fade(10.0));
            Assert.Equal(CombatFeed.FadeMax, f.Fade(11.0));
            f.OnRespawn(new RespawnMessage { Victim = 6 }, 15.0); // чужое возрождение
            Assert.True(f.Dead);
            f.OnRespawn(new RespawnMessage { Victim = 5 }, 15.1);
            Assert.False(f.Dead);
            Assert.True(f.RespawnNote(15.2));
            Assert.True(f.Fade(15.2) > 0f && f.Fade(15.2) < CombatFeed.FadeMax);
            Assert.Equal(0f, f.Fade(15.1 + CombatFeed.FadeSec), 4);
            Assert.False(f.RespawnNote(15.1 + CombatFeed.RespawnNoteSec));
        }

        // ---------- падение и растворение ----------

        [Fact]
        public void E31_DeathAnim_FallsThenEnemyDissolves_HeroLies()
        {
            Assert.Equal(0f, DeathAnim.FallProgress(0f));
            Assert.Equal(1f, DeathAnim.FallProgress(DeathAnim.FallSec + 0.2f));
            Assert.True(DeathAnim.FallProgress(DeathAnim.FallSec * 0.5f) < 0.5f); // ускоряется

            Pose p = DeathAnim.Apply(Pose.Identity, 0.6f, fallRight: true, dissolve: true);
            Assert.Equal(-DeathAnim.FallDeg, p.RotationDeg, 3);
            Assert.Equal(1f, p.Alpha); // лежит, ещё не растворяется
            Assert.Equal(DeathAnim.FallDeg, DeathAnim.Apply(Pose.Identity, 0.6f, false, true).RotationDeg, 3);

            float mid = DeathAnim.Apply(Pose.Identity, (DeathAnim.DissolveFromSec + DeathAnim.GoneSec) / 2f, true, true).Alpha;
            Assert.InRange(mid, 0.3f, 0.7f);
            Assert.False(DeathAnim.Gone(DeathAnim.GoneSec - 0.01f, true));
            Assert.True(DeathAnim.Gone(DeathAnim.GoneSec, true));
            Assert.True(DeathAnim.GoneSec < Simulation.CorpseTicks / (float)Simulation.TickRate); // растворился раньше, чем сервер убрал труп

            Assert.Equal(1f, DeathAnim.Apply(Pose.Identity, 30f, true, dissolve: false).Alpha); // свой лежит до возрождения
            Assert.False(DeathAnim.Gone(30f, false));
        }
    }
}
