using System;
using System.IO;
using Xunit;
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

namespace Orpheus.Tests
{
    /// <summary>
    /// 1-001 (docs/02 6.1, 6.3, 6.5, 6.6; docs/13 8.3, 13.5, 13.6): здоровье и урон, регенерация,
    /// отбрасывание, прерывание замаха, смерть и возрождение. Ядро (<c>Simulation.ApplyStrike</c>, <c>Mover</c>),
    /// журнал ввода с боем (docs/07 11.5) и связка с сетью (docs/07 2.5): удар решает сервер, клиент показывает.
    /// Картинка, звук, интерфейс и сохранения — вне карточки.
    /// </summary>
    public sealed class CombatTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const long TickUs = 1_000_000 / 30;

        private static WorldModel Flat(int tiles, ulong seed) => FlatWorldGenerator.Instance.Generate(MapShape.World(tiles), seed);

        /// <summary>
        /// Сцена удара: враг «Ближний» (id 1, 35 HP) в <paramref name="at"/> + полтайла на восток, персонаж
        /// (id 2, 100 HP) в <paramref name="at"/>; цикл врага выключен — бьёт только включённое явно.
        /// </summary>
        private static (Simulation Sim, int Enemy, int Char, WorldPos At) Scene(WorldModel world, WorldPos at)
        {
            var sim = new Simulation(world);
            int en = sim.AddEnemy(Torus.Offset(sim.Shape, at, new Int2(U / 2, 0)));
            int id = sim.AddEntity(at);
            return (sim, en, id, at);
        }

        /// <summary>
        /// Симуляция с правлеными сущностями (масса, здоровье): сохранение грузится только в пустую
        /// симуляцию до первого такта (<see cref="Simulation.Restore"/>), потому правка — новый объект на тех же сущностях.
        /// </summary>
        private static Simulation Reborn(Simulation from, Action<SimEntity[]> mutate)
        {
            var a = new SimEntity[from.Entities.Length];
            from.Entities.CopyTo(a);
            mutate(a);
            var sim = new Simulation(from.World);
            sim.Restore(0, a, from.NextEntityId);
            return sim;
        }

        private static int Count(Simulation sim, SimEventKind kind)
        {
            int n = 0;
            foreach (SimEvent ev in sim.Events)
            {
                if (ev.Kind == kind)
                {
                    n++;
                }
            }

            return n;
        }

        private static void Steps(Simulation sim, int n)
        {
            for (int i = 0; i < n; i++)
            {
                sim.Step();
            }
        }

        /// <summary>События одного среза буфера такта (смерть, возрождение, урон) — в счётчики прогона.</summary>
        /// <summary><paramref name="from"/> — с какого события считать: буфер <c>sim.Events</c> очищается
        /// только в начале следующего <c>Step</c>, потому события Step после Poll уже сосчитаны.</summary>
        private static void CountCombat(ReadOnlySpan<SimEvent> events, ref int deaths, ref int respawns, ref int damage, int from = 0)
        {
            for (int i = from; i < events.Length; i++)
            {
                switch (events[i].Kind)
                {
                    case SimEventKind.Death:
                        deaths++;
                        break;
                    case SimEventKind.Respawn:
                        respawns++;
                        break;
                    case SimEventKind.Damage:
                        damage++;
                        break;
                }
            }
        }

        /// <summary>
        /// <paramref name="n"/> тактов с суммой событий: <see cref="Simulation.Events"/> — буфер одного такта,
        /// потому за прогон удары и смерти считаются по тактам.
        /// </summary>
        private static void Steps(Simulation sim, int n, out int damage, out int deaths)
        {
            int d = 0, k = 0;
            for (int i = 0; i < n; i++)
            {
                sim.Step();
                foreach (SimEvent ev in sim.Events)
                {
                    if (ev.Kind == SimEventKind.Damage)
                    {
                        d++;
                    }
                    else if (ev.Kind == SimEventKind.Death)
                    {
                        k++;
                    }
                }
            }

            damage = d;
            deaths = k;
        }

        // ---------- 02 6.1: формула урона ----------

        [Fact]
        public void D02_S6_1_DamageMath_ArmorPierceAndMinimum()
        {
            Assert.Equal(10, DamageMath.Apply(10, 0));           // броня 0 — полный урон
            Assert.Equal(7, DamageMath.Apply(10, 3));            // max(25 % = 2, 10 − 3 = 7)
            Assert.Equal(22, DamageMath.Apply(25, 3));           // max(6, 22)
            Assert.Equal(2, DamageMath.Apply(5, 3));             // max(1, 2)
            Assert.Equal(1, DamageMath.Apply(5, 5));             // max(1, 0) → минимум 1
            Assert.Equal(1, DamageMath.Apply(1, 10));            // удар всегда не меньше 1
            Assert.Equal(0, DamageMath.Apply(0, 0));             // без урона — 0
            Assert.Equal(10, DamageMath.Apply(10, 3, 100));      // пробой 100 % — броня не действует
            Assert.Throws<ArgumentOutOfRangeException>(() => DamageMath.Apply(10, 0, 101));

            // прерывание замаха (6.6): доля от МАКСИМУМУ здоровья, целыми без float
            Assert.True(DamageMath.AtLeastPercent(6, 35, 15));
            Assert.False(DamageMath.AtLeastPercent(5, 35, 15));
            Assert.True(DamageMath.AtLeastPercent(10, 100, 10));
            Assert.False(DamageMath.AtLeastPercent(9, 100, 10));
        }

        // ---------- 02 6.3: регенерация через 6 с после урона ----------

        [Fact]
        public void D02_S6_3_Regen_StartsAfterSixSecondsAndStopsAtMax()
        {
            var sim = new Simulation(Flat(64, 7));
            int id = sim.AddEntity(new WorldPos(100, 100));

            Assert.Equal(60, sim.ApplyStrike(id, -1, 60, new WorldPos(100, 100), knockback: false));
            Assert.Equal(40, sim.GetEntity(id).Health);

            // 180 тактов (6 с) тишины, затем 1 % максимума в секунду: +100 сотых за такт, +1 HP на 3000-й
            Steps(sim, 209);
            Assert.Equal(40, sim.GetEntity(id).Health);
            Steps(sim, 30);
            Assert.Equal(41, sim.GetEntity(id).Health);

            // 60 с тишины — до полного здоровья, выше не растёт
            Steps(sim, 1980);
            Assert.Equal(100, sim.GetEntity(id).Health);
            Steps(sim, 600);
            Assert.Equal(100, sim.GetEntity(id).Health);
        }

        // ---------- 02 6.3: 0,4 с неуязвимости персонажа после удара ----------

        [Fact]
        public void D02_S6_3_Strike_InvulnBlocksNextStrike()
        {
            var sim = new Simulation(Flat(64, 7));
            int id = sim.AddEntity(new WorldPos(100, 100));

            Assert.Equal(10, sim.ApplyStrike(id, -1, 10, new WorldPos(100, 100), knockback: false));
            Assert.Equal(Simulation.InvulnStrikeTicks, sim.GetEntity(id).InvulnTicks);

            // в окне — удары мимо: возврат 0, здоровье не тает
            Assert.Equal(0, sim.ApplyStrike(id, -1, 10, new WorldPos(100, 100), knockback: false));
            Assert.Equal(90, sim.GetEntity(id).Health);

            // окно истекло — удар снова проходит
            Steps(sim, Simulation.InvulnStrikeTicks);
            Assert.Equal(0, sim.GetEntity(id).InvulnTicks);
            Assert.Equal(10, sim.ApplyStrike(id, -1, 10, new WorldPos(100, 100), knockback: false));
            Assert.Equal(80, sim.GetEntity(id).Health);
        }

        // ---------- 02 6.6: удар ≥ 15 % снимает замах, отход и иммунитет ----------

        [Fact]
        public void D02_S6_6_Strike_InterruptsEnemyWindup()
        {
            WorldPos at = new(400 * U, 512 * U + 128);

            // «Ближний» (35 HP): порог 15 % = 6 — удар 5 замах не снимает
            var (sim, en, id, _) = Scene(Flat(1024, 9), at);
            Assert.True(sim.StartAttack(en, id));
            Assert.Equal(Simulation.WindupTicks, sim.GetEntity(en).AttackWindup);
            Steps(sim, 3);
            Assert.Equal(5, sim.ApplyStrike(en, id, 5, at, knockback: false));
            Assert.Equal(Simulation.WindupTicks - 3, sim.GetEntity(en).AttackWindup);
            Assert.Equal(30, sim.GetEntity(en).Health);
            Assert.Equal(0, sim.GetEntity(en).AttackImmune);

            sim.Step();
            Assert.Equal(6, sim.ApplyStrike(en, id, 6, at, knockback: false));
            SimEntity e = sim.GetEntity(en);
            Assert.Equal(0, e.AttackWindup);                           // замах снят
            Assert.Equal(Simulation.InterruptRecoverTicks, e.AttackRecover); // отход 0,5 с
            Assert.Equal(Simulation.InterruptImmuneTicks, e.AttackImmune);   // иммунитет 1,5 с
            Assert.Equal(-1, e.AttackTarget);
            Assert.Equal(24, e.Health);
            Assert.Equal(100, sim.GetEntity(id).Health);             // удара не было — персонаж цел

            // доля — от МАКСИМУМА (35), а не от текущего здоровья: врагу с 17 HP удар 3 (порог те же 6) замах не снимает
            var (raw, en2, id2, at2) = Scene(Flat(1024, 11), at);
            var sim2 = Reborn(raw, a => a[0].Health = 17);
            Assert.True(sim2.StartAttack(en2, id2));
            Steps(sim2, 3);
            Assert.Equal(2, sim2.ApplyStrike(en2, id2, 2, at2, knockback: false));
            Assert.Equal(Simulation.WindupTicks - 3, sim2.GetEntity(en2).AttackWindup);
            Assert.Equal(15, sim2.GetEntity(en2).Health);
            sim2.Step();
            Assert.Equal(3, sim2.ApplyStrike(en2, id2, 3, at2, knockback: false));
            e = sim2.GetEntity(en2);
            Assert.Equal(Simulation.WindupTicks - 4, e.AttackWindup); // замах цел: 3 < 6
            Assert.Equal(0, e.AttackRecover);
            Assert.Equal(0, e.AttackImmune);
            Assert.Equal(12, e.Health);

            sim2.Step();
            Assert.Equal(6, sim2.ApplyStrike(en2, id2, 6, at2, knockback: false));
            e = sim2.GetEntity(en2);
            Assert.Equal(0, e.AttackWindup);
            Assert.Equal(Simulation.InterruptRecoverTicks, e.AttackRecover);
            Assert.Equal(Simulation.InterruptImmuneTicks, e.AttackImmune);
            Assert.Equal(6, e.Health);
        }

        // ---------- 02 6.5: отбрасывание — скорость от массы, стена останавливает ----------

        [Fact]
        public void D02_S6_5_Knockback_DividesByMassAndStopsAtWall()
        {
            Assert.Equal(284, Mover.KnockSpeedFor(1)); // 2560 units/такт ÷ 9 кадров ÷ масса
            Assert.Equal(94, Mover.KnockSpeedFor(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => Mover.KnockSpeedFor(0));

            // чистое поле
            WorldPos at = new(400 * U, 512 * U + 128);

            var (sim, en, id, _) = Scene(Flat(1024, 5), at);
            WorldPos origin = Torus.Offset(sim.Shape, at, new Int2(U, 0)); // источник на востоке → бросок на запад
            Assert.Equal(10, sim.ApplyStrike(id, en, Simulation.StrikeRawMelee, origin, knockback: true));
            SimEntity c = sim.GetEntity(id);
            Assert.Equal(90, c.Health);
            Assert.Equal(Mover.KnockFrames, c.Mover.KnockLeft);
            Assert.Equal(284, c.Mover.KnockSpeedX10);
            Assert.Equal(Input8.West, c.Mover.KnockDirection);

            Steps(sim, 5); // 10 кадров: 9 кадров рывка + кадр без ввода
            Int2 d = Torus.Delta(sim.Shape, at, sim.GetEntity(id).Mover.Position);
            Assert.Equal(0, sim.GetEntity(id).Mover.KnockLeft);
            Assert.Equal(-255, d.X); // тайл за 9 кадров (28,4 unit/кадр с остатком в носилках)
            Assert.Equal(0, d.Y);

            // масса 3 — втрое короче
            var (raw, en3, id3, _) = Scene(Flat(1024, 6), at);
            var sim3 = Reborn(raw, a => a[1].Mass = 3);
            Assert.Equal(10, sim3.ApplyStrike(id3, en3, Simulation.StrikeRawMelee, origin, knockback: true));
            Steps(sim3, 5);
            Assert.Equal(-84, Torus.Delta(sim3.Shape, at, sim3.GetEntity(id3).Mover.Position).X);

            // стена на пути: персонаж стоит на её восточной грани (впритык радиусом) — рывок коротается
            // в первый же кадр, урона от стены нет
            WorldModel world = Flat(1024, 8);
            for (int dy = -1; dy <= 1; dy++)
            {
                world.SetBaseTile(new TileCoord(399, 512 + dy), FloorId.Grass, ObjectId.StoneWall);
            }

            WorldPos atWall = new(400 * U + Collision.RadiusUnits, 512 * U + 128);
            var (sim4, en4, id4, _) = Scene(world, atWall);
            Assert.Equal(10, sim4.ApplyStrike(id4, en4, Simulation.StrikeRawMelee, origin, knockback: true));
            Steps(sim4, 5);
            c = sim4.GetEntity(id4);
            Assert.Equal(0, c.Mover.KnockLeft);
            Assert.Equal(atWall, c.Mover.Position);
            Assert.Equal(90, c.Health);
        }

        // ---------- 13 8.3, 13.5, 13.6: смерть, 5 с, возрождение в точке старта ----------

        [Fact]
        public void D02_S13_5_Death_GivesFiveSecondsThenRespawnAtCenter()
        {
            var sim = new Simulation(Flat(64, 3));
            WorldPos at = new(100, 100);
            int id = sim.AddEntity(at);
            sim.RespawnCenter = at;

            Assert.Equal(200, sim.ApplyStrike(id, -1, 200, at, knockback: false)); // смерть: урон после брони 200 (не режется по HP), рывка нет
            Assert.Equal(1, Count(sim, SimEventKind.Damage));
            Assert.Equal(1, Count(sim, SimEventKind.Death));
            SimEntity e = sim.GetEntity(id);
            Assert.Equal(0, e.Health);
            Assert.Equal(Simulation.RespawnDelayTicks, e.DeadTicks);
            Assert.Equal(at, e.Mover.Position); // труп лежит там, где упал

            Steps(sim, Simulation.RespawnDelayTicks - 1);
            e = sim.GetEntity(id);
            Assert.Equal(0, e.Health);
            Assert.Equal(at, e.Mover.Position);
            Assert.Equal(0, Count(sim, SimEventKind.Respawn));

            sim.Step(); // 150-й такт после удара
            e = sim.GetEntity(id);
            Assert.Equal(100, e.Health);
            Assert.Equal(sim.RespawnCenter, e.Mover.Position);
            Assert.Equal(Simulation.InvulnRespawnTicks, e.InvulnTicks);
            Assert.Equal(0, e.DeadTicks);
            Assert.Equal(1, Count(sim, SimEventKind.Respawn));

            // неуязвимость снимается первой атакой; после неё урон идёт
            Assert.Equal(0, sim.ApplyStrike(id, -1, 10, at, knockback: false));
            Assert.Equal(100, sim.GetEntity(id).Health);
            Steps(sim, Simulation.InvulnRespawnTicks);
            Assert.Equal(0, sim.GetEntity(id).InvulnTicks);
            Assert.Equal(10, sim.ApplyStrike(id, -1, Simulation.StrikeRawMelee, at, knockback: false));
            Assert.Equal(90, sim.GetEntity(id).Health);
        }

        // ---------- приёмка 1-001: «Ближний» (35 HP, удар 10) убивает персонажа за 10 ударов ----------

        [Fact]
        public void D02_S8_2_MeleeEnemyCycle_KillsCharacterInTenStrikes()
        {
            WorldModel world = Flat(1024, 21);
            for (int dy = -1; dy <= 1; dy++)
            {
                world.SetBaseTile(new TileCoord(399, 512 + dy), FloorId.Grass, ObjectId.StoneWall); // не убежать
            }

            WorldPos at = new(400 * U + Collision.RadiusUnits, 512 * U + 128); // впритык к грани стены; враг в 128 unit — в дистанции удара
            var (sim, en, id, _) = Scene(world, at);
            Assert.True(sim.EnableAttackCycle(en, Simulation.DummyPeriodTicks, Simulation.StrikeRawMelee));

            Steps(sim, 400, out int hits, out int deaths);

            Assert.Equal(10, hits);
            Assert.Equal(1, deaths);
            SimEntity e = sim.GetEntity(id);
            Assert.Equal(0, e.Health);
            Assert.Equal(at, e.Mover.Position); // стоял у стены — удары доставали
        }

        // ---------- 07 11.5: журнал ввода с боем повторяется в тот же хэш ----------

        [Fact]
        public void D02_S11_5_Journal_WithCombat_ReplaysToTheSameHash()
        {
            const long ticks = 3000; // удар раз в ~120 тактов: за записью две смерти и возрождение между ними
            WorldModel world = Flat(1024, 5);
            for (int dy = -1; dy <= 1; dy++)
            {
                world.SetBaseTile(new TileCoord(399, 512 + dy), FloorId.Grass, ObjectId.StoneWall);
            }

            var (sim, en, id, at) = Scene(world, new(400 * U + Collision.RadiusUnits, 512 * U + 128)); // впритык к стене — удары достают и после рывка
            sim.RespawnCenter = at;
            Assert.True(sim.EnableAttackCycle(en, Simulation.DummyPeriodTicks, Simulation.StrikeRawMelee));

            int strikes = 0, dead = 0;
            var ms = new MemoryStream();
            var pending = new EntityInput[sim.Entities.Length];
            using (var w = new InputJournalWriter(ms, JournalHeader.Of(sim), ownsStream: false))
            {
                for (long tick = 1; tick <= ticks; tick++)
                {
                    if ((tick - 1) % 30 == 0)
                    {
                        sim.SetInput(id, new InputFrame(tick % 60 == 1 ? Input8.East : Input8.West));
                    }

                    int n = sim.CopyPendingInputs(pending);
                    w.WriteInputs(tick, new ReadOnlySpan<EntityInput>(pending, 0, n));
                    sim.Step();
                    foreach (SimEvent ev in sim.Events)
                    {
                        if (ev.Kind == SimEventKind.Damage)
                        {
                            strikes++;
                        }
                        else if (ev.Kind == SimEventKind.Death)
                        {
                            dead++;
                        }
                    }

                    if (tick % Simulation.HashInterval == 0)
                    {
                        w.WriteCheckpoint(StateHash.Compute(sim), sim.Entities);
                    }
                }

                w.WriteEnd(ticks);
            }

            Assert.True(dead >= 2, $"Смертей в записи: {dead}.");
            Assert.True(strikes >= 20, $"Ударов в записи: {strikes}.");

            // повтор: те же сущности, тот же ввод — хэш сверки совпадает на каждой точке
            ms.Position = 0;
            using var r = new InputJournalReader(ms);
            var sim2 = new Simulation(world);
            JournalSpawn[] spawns = r.Header.Spawns;
            sim2.RespawnCenter = at;   // как в первой записи: смерть — возрождение у стены
            int en2 = sim2.AddEnemy(spawns[0].Position);
            Assert.Equal(spawns[0].EntityId, en2);
            Assert.True(sim2.EnableAttackCycle(en2, Simulation.DummyPeriodTicks, Simulation.StrikeRawMelee));
            int id2 = sim2.AddEntityWithId(spawns[1].EntityId, spawns[1].Position, spawns[1].UnitsPerTickX10);

            long t = 1;
            int points = 0;
            JournalRecord next = r.Next();
            while (next == JournalRecord.Inputs || next == JournalRecord.Checkpoint)
            {
                while (next == JournalRecord.Inputs && r.Tick == t)
                {
                    foreach (EntityInput input in r.Inputs)
                    {
                        Assert.True(sim2.SetInput(input.EntityId, input.Frame), $"Ввод такта {t}: сущность #{input.EntityId}.");
                    }

                    next = r.Next();
                }

                sim2.Step();
                if (t % Simulation.HashInterval == 0)
                {
                    Assert.Equal(JournalRecord.Checkpoint, next);
                    Assert.Equal(t, r.Tick);
                    StateHashResult h = StateHash.Compute(sim2);
                    Assert.Equal(r.CheckpointHash.Total, h.Total);
                    Assert.Equal(r.CheckpointHash.World, h.World);
                    for (int i = 0; i < r.CheckpointEntityCount; i++)
                    {
                        Assert.Equal(r.CheckpointEntityHash(i), StateHash.Entity(sim2.GetEntity(r.CheckpointEntityId(i))));
                    }

                    points++;
                    next = r.Next();
                }

                t++;
            }

            Assert.Equal(ticks + 1, t);
            Assert.Equal(ticks / Simulation.HashInterval, points);
            Assert.Equal(sim.GetEntity(id).Health, sim2.GetEntity(id2).Health);
            Assert.Equal(sim.GetEntity(id).Mover.Position, sim2.GetEntity(id2).Mover.Position);
        }

        // ---------- 07 2.5: удар решает сервер; клиент показывает нокбэк, труп и здоровье ----------

        [Fact]
        public void D07_S2_7_CombatNet_KnockDeathsAndHealth_AgreeWithServer()
        {
            const int seconds = 40;
            WorldModel world = Flat(1024, 31);
            var sim = new Simulation(world, maxEntities: 8);
            WorldPos a = ServerRun.SpawnPoint(world.Shape, 0), b = ServerRun.SpawnPoint(world.Shape, 1);

            // стена в двух тайлах к западу от спавна A: рывок упирается в неё, манекен продолжает доставать;
            // враг «Ближний» — в полтайла к востоку от B, стена у B за спиной (на западе): B прижат и получает удары подряд
            TileCoord ta = Torus.TileOf(world.Shape, a), tb = Torus.TileOf(world.Shape, b);
            for (int dy = -1; dy <= 1; dy++)
            {
                world.SetBaseTile(new TileCoord(ta.X - 2, ta.Y + dy), FloorId.Grass, ObjectId.StoneWall);
                world.SetBaseTile(new TileCoord(tb.X - 1, tb.Y + dy), FloorId.Grass, ObjectId.StoneWall);
            }

            sim.RespawnCenter = b;
            int en = sim.AddEnemy(Torus.Offset(world.Shape, b, new Int2(U / 2, 0))); // в 128 unit к востоку: B прижат к стене на западе — удары достают
            Assert.True(sim.EnableAttackCycle(en, Simulation.DummyPeriodTicks, Simulation.StrikeRawMelee));

            using LoopbackTransport st = LoopbackTransport.CreateServer(4);
            var server = new NetServer(st, sim, i => i == 0 ? a : b, 2, NetModule.Version);

            // манекен бьёт ближним по спавну A; удар летит Facts-каналом, урон и нокбэк считает сервер
            server.SetDummy(new MeleeDummy(1000, Torus.Offset(world.Shape, a, new Int2(U / 2, 0)),
                Angle16.FromDirection(new Int2(-1, 0)), firstSwingFrame: 180, periodFrames: DodgeShipTests.SwingPeriodFrames));

            var ca = new NetClient(new NetSimTransport(LoopbackTransport.ConnectTo(st), NetSimSettings.FromRtt(100, 10, 1.0, 5)),
                LoopbackTransport.ServerPeer, "combat-a", NetModule.Version);
            var cb = new NetClient(new NetSimTransport(LoopbackTransport.ConnectTo(st), NetSimSettings.FromRtt(100, 10, 1.0, 6)),
                LoopbackTransport.ServerPeer, "combat-b", NetModule.Version);
            var botA = new NetBot(ca, IdleBot.Name, 0)
            {
                PredictionFactory = c => new LocalPrediction(c.Shape, sim.Solid, sim.RespawnCenter),
            };
            var botB = new NetBot(cb, IdleBot.Name, 1)
            {
                PredictionFactory = c => new LocalPrediction(c.Shape, sim.Solid, sim.RespawnCenter),
            };

            // здоровье сервера по тактам — для сверки с долей HP в снимке (с запасом на дренаж)
            var hp = new int[seconds * 30 + 64 + 8];

            // снимок B: сверка своей доли HP с сервером на такте снимка (у клиента нет списка снимков — проверка в хуке)
            int snapA = 0, snapB = 0, meleeA = 0, parity = 0, parityBad = 0;
            Action<Snapshot>? prevA = ca.SnapshotApplied;
            ca.SnapshotApplied = s => { prevA?.Invoke(s); snapA++; };
            Action<Snapshot>? prevB = cb.SnapshotApplied;
            cb.SnapshotApplied = s =>
            {
                prevB?.Invoke(s);
                snapB++;
                if (s.Tick > 0 && s.Tick < hp.Length && s.TryFind(cb.EntityId, out SnapshotEntity ent))
                {
                    int want = Math.Max(0, Math.Min(100, hp[(int)s.Tick]));
                    if (ent.HealthPct == want)
                    {
                        parity++;
                    }
                    else
                    {
                        parityBad++;
                    }
                }
            };
            Action<MeleeResultMessage>? prevMelee = ca.MeleeResolved;
            ca.MeleeResolved = m => { prevMelee?.Invoke(m); meleeA++; };

            // решения сервера — по игроку (манекен решает угрозу по каждому в игре, цели летит только её решение)
            Action<int, MeleeResultMessage>? prevResolved = server.MeleeResolved;
            int resolvedA = 0, resolvedB = 0;
            server.MeleeResolved = (id, m) =>
            {
                prevResolved?.Invoke(id, m);
                if (id == ca.EntityId)
                {
                    resolvedA++;
                }
                else if (id == cb.EntityId)
                {
                    resolvedB++;
                }
            };
            // труп нокбэк не принимает (предсказание заморожено) — смерти A снимаем с его счёта попаданий
            Action<DeathMessage>? prevDeathA = ca.DeathSeen;
            int deathsA = 0;
            ca.DeathSeen = m => { prevDeathA?.Invoke(m); if (m.Victim == ca.EntityId) deathsA++; };

            int deathsAll = 0, respawnsAll = 0, dmgAll = 0, carried = 0;
            long tick = 0, now = 0;
            for (now = 0; now <= (long)seconds * 1_000_000; now += 1000)
            {
                if (now >= tick * TickUs)
                {
                    // решение сервера (Poll) бьёт вне такта: его события видны в буфере до следующего Step;
                    // события последнего Step (carried) уже сосчитаны — считаем только добавленные Poll'ом
                    server.Poll(now);
                    CountCombat(sim.Events, ref deathsAll, ref respawnsAll, ref dmgAll, carried);
                    sim.Step();
                    CountCombat(sim.Events, ref deathsAll, ref respawnsAll, ref dmgAll);
                    carried = sim.Events.Length; // остаток буфера: он уйдёт на следующий такт, не считаясь дважды

                    if (sim.SlotOf(3) >= 0)
                    {
                        hp[(int)sim.Tick] = sim.GetEntity(3).Health;
                    }

                    server.SendSnapshots();
                    tick++;
                }

                botA.Frame(now);
                botB.Frame(now);
            }

            // дренаж: сервер досчитывает такты, досылает последние Death/Respawn, клиенты их обрабатывают
            for (int d = 0; d < 60; d++)
            {
                now += TickUs;
                server.Poll(now);
                CountCombat(sim.Events, ref deathsAll, ref respawnsAll, ref dmgAll, carried);
                sim.Step();
                CountCombat(sim.Events, ref deathsAll, ref respawnsAll, ref dmgAll);
                carried = sim.Events.Length;
                if (sim.SlotOf(3) >= 0 && sim.Tick < (uint)hp.Length)
                {
                    hp[(int)sim.Tick] = sim.GetEntity(3).Health;
                }
                server.SendSnapshots();
                botA.Frame(now);
                botB.Frame(now);
            }

            LocalPrediction pa = botA.Prediction!, pb = botB.Prediction!;
            Assert.True(server.ThreatHits >= 8, $"Попаданий манекена по A: {server.ThreatHits}.");
            Assert.Equal(server.ThreatsResolved, resolvedA + resolvedB); // каждое решение — одному игроку
            Assert.Equal(resolvedA, meleeA); // и дошло цели: Facts-канал, потери 0
            Assert.True(resolvedA >= 8, $"Решений по A: {resolvedA}.");
            Assert.True(deathsAll >= 2, $"Смертей на сервере: {deathsAll}, возрождений: {respawnsAll}, урона: {dmgAll}, попаданий: {server.ThreatHits}.");
            Assert.Equal(deathsAll, ca.DeathsSeen);   // труп показан обоим клиентам
            Assert.Equal(deathsAll, cb.DeathsSeen);
            Assert.Equal(respawnsAll, ca.RespawnsSeen); // и возрождение
            Assert.Equal(respawnsAll, cb.RespawnsSeen);
            Assert.Equal(deathsAll + respawnsAll, server.CombatEventsSent);
            Assert.True(snapA > 500 && snapB > 500, $"Снимков: A {snapA}, B {snapB}.");

            // отбрасывание принято из снимка, а не выдумано предсказанием. A прижат к стене: удары гасятся ею,
            // позиция снимка совпадает с предсказанием — принятий мало, но они есть и не больше решений; массу
            // принимает B (свободный ход отбрасывания) — ниже
            Assert.True(pa.KnockAdopted >= 2 && pa.KnockAdopted <= resolvedA,
                $"A принял нокбэк: {pa.KnockAdopted} из {resolvedA}, смертей A: {deathsA} (всего попаданий: {server.ThreatHits}).");
            Assert.True(pb.KnockAdopted >= 2 && pb.KnockAdopted <= resolvedB, $"B принял нокбэк: {pb.KnockAdopted} при решениях по B: {resolvedB}.");

            // здоровье в снимке = здоровье сервера на такте снимка (персонаж: 100 HP → доля совпадает с HP)
            Assert.Equal(0, parityBad);
            Assert.True(parity >= seconds * 15, $"Снимков B с проверкой здоровья: {parity}.");
        }

        // ---------- связка с 0-033: чистая связь — нокбэк без единой правки предсказания ----------

        [Fact]
        public void D07_S2_7_DodgeShip_CleanLink_NoResyncWithKnocks()
        {
            DodgeShipTests.ShipRun r = DodgeShipTests.Run(null, swings: 20, latePress: 12);

            Assert.True(r.Server.ThreatHits >= 12, $"Попаданий: {r.Server.ThreatHits}.");
            Assert.True(r.Server.ThreatHits == 0 || r.Pred!.KnockAdopted >= r.Server.ThreatHits - 8,
                $"Отбрасывание принято из снимка: {r.Pred!.KnockAdopted} из {r.Server.ThreatHits}.");
            // удар и отбрасывание приходят на данных сервера: правка предсказания — только приём нокбэка, не больше одной на удар
            Assert.True(r.Pred!.Resynced <= r.Server.ThreatHits,
                $"Правок предсказания {r.Pred.Resynced} при ударах {r.Server.ThreatHits}.");
            Assert.True(r.Seconds >= 4, "Прогон короче 4 с.");
        }
    }
}
