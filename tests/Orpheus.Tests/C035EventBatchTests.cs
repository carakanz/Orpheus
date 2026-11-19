using System;
using System.Collections.Generic;
using System.Linq;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-035: «пачка событий» (40) — золотые байты и строгий разбор кодека, переполнение; по сети — такт боя
    /// одним сообщением на получателя (лук: выстрел + конец + урон), радиусы прежние (далёкий — ноль), порядок
    /// за тактом (смерть после урона того же такта), детерминизм; лимит новых куч (27) на такт — выгрузка
    /// поля стрел за K тактов.
    /// </summary>
    public class C035EventBatchTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldPos At(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private static string Hex(ReadOnlySpan<byte> b)
        {
            var parts = new string[b.Length];
            for (int i = 0; i < b.Length; i++)
                parts[i] = b[i].ToString("x2");
            return string.Join(" ", parts);
        }

        private static byte[] DamageBody()
        {
            var m = new DamageMessage { Victim = 9, Attacker = 4, Amount = 13, DamageType = 1, Flags = DamageMessage.FlagStrong };
            var full = new byte[DamageMessage.Bytes];
            Assert.Equal(DamageMessage.Bytes, m.Write(full));
            return full.AsSpan(1).ToArray();
        }

        private static byte[] StopBody()
        {
            var m = new ProjectileMessage
            {
                Op = ProjectileOp.Stop, Id = 5, Reason = ProjectileEnd.Wall, Position = new WorldPos(256, 512), Victim = -1, Dropped = true,
            };
            var full = new byte[ProjectileMessage.StopBytes];
            Assert.Equal(ProjectileMessage.StopBytes, m.Write(full));
            return full.AsSpan(1).ToArray();
        }

        private static byte[] StrikeBody(int tiles)
        {
            var m = new StrikeMessage { Striker = 4, RequestId = 7, Count = tiles };
            for (int i = 0; i < tiles; i++)
                m.SetTile(i, new StrikeTile { Tile = new TileCoord(300, 200), Obj = 12, Hits = 2, Needed = 3, Broken = true });
            var full = new byte[StrikeMessage.HeaderBytes + StrikeMessage.TileBytes * tiles];
            Assert.Equal(full.Length, m.Write(full));
            return full.AsSpan(1).ToArray();
        }

        /// <summary>Записи подряд (вид 1 + тело), как складывает сервер в staging.</summary>
        private static byte[] RecordsOf(params (EventRecordKind Kind, byte[] Body)[] recs)
        {
            var buf = new byte[4096];
            int pos = 0;
            foreach ((EventRecordKind kind, byte[] body) in recs)
            {
                buf[pos++] = (byte)kind;
                body.CopyTo(buf.AsSpan(pos));
                pos += body.Length;
            }

            return buf.AsSpan(0, pos).ToArray();
        }

        /// <summary>Тело записи + байт одиночного сообщения → целое сообщение (как делает клиент).</summary>
        private static byte[] Msg(MessageType type, ReadOnlySpan<byte> body)
        {
            var m = new byte[1 + body.Length];
            m[0] = (byte)type;
            body.CopyTo(m.AsSpan(1));
            return m;
        }

        /// <summary>Разобрать пачку до конца (бросает ProtocolException на битой, как читает сервер/клиент).</summary>
        private static void Drains(byte[] msg)
        {
            EventBatchReader r = EventBatchMessage.Open(msg);
            while (r.TryNext(out _, out _))
            {
            }

            r.ExpectEnd();
        }

        [Fact]
        public void C035_Codec_GoldenBytes_StrictRejects()
        {
            Assert.Equal(40, (int)MessageType.EventBatch);
            Assert.Equal(38, NetModule.ProtocolVersion); // E3-3b: 38 (владелец личной капли в кучах 27); C-036: 37 (действие в снимке); C-035: 36 (пачка событий, тип 40)

            byte[] db = DamageBody(), pb = StopBody(), sb = StrikeBody(1);
            Assert.Equal(DamageMessage.Bytes - 1, db.Length); // 12
            Assert.Equal(ProjectileMessage.StopBytes - 1, pb.Length); // 19
            Assert.Equal(20, sb.Length);

            // Тело записи по виду: урон — всегда 12 (лишние байты не берёт), снаряд — по оп-байту, взмах — по числу тайлов.
            Assert.Equal(12, EventBatchMessage.RecordBodyBytes(EventRecordKind.Damage, db));
            Assert.Equal(19, EventBatchMessage.RecordBodyBytes(EventRecordKind.Projectile, pb));
            Assert.Equal(20, EventBatchMessage.RecordBodyBytes(EventRecordKind.Strike, sb));
            Assert.Equal(46, EventBatchMessage.RecordBodyBytes(EventRecordKind.Strike, StrikeBody(3)));
            Assert.True(1 + 46 <= 48, "наибольшая пачная запись (взмах 3 тайла) влезает в scratch клиента");

            byte[] records = RecordsOf((EventRecordKind.Damage, db), (EventRecordKind.Projectile, pb), (EventRecordKind.Strike, sb));
            var buf = new byte[EventBatchMessage.MaxBytes];
            int n = EventBatchMessage.Write(buf, records);
            Assert.Equal(2 + 13 + 20 + 21, n); // заголовок 2 + три записи (вид + тело)

            // Золотые байты: заголовок + запись урона + запись конца снаряда.
            Assert.Equal("28 03 00 09 00 00 00 04 00 00 00 0d 00 01 01 01 02 05 00 00 00 02 00 01 00 00 00 02 00 00 ff ff ff ff 01",
                Hex(new ReadOnlySpan<byte>(buf, 0, 2 + 13 + 20)));

            EventBatchReader r = EventBatchMessage.Open(new ReadOnlySpan<byte>(buf, 0, n));
            Assert.True(r.TryNext(out EventRecordKind k0, out ReadOnlySpan<byte> b0));
            Assert.Equal(EventRecordKind.Damage, k0);
            DamageMessage d = DamageMessage.Read(Msg(MessageType.Damage, b0));
            Assert.Equal(9, d.Victim);
            Assert.Equal(4, d.Attacker);
            Assert.Equal(13, d.Amount);
            Assert.True(d.Strong);
            Assert.True(r.TryNext(out EventRecordKind k1, out ReadOnlySpan<byte> b1));
            Assert.Equal(EventRecordKind.Projectile, k1);
            ProjectileMessage stop = ProjectileMessage.Read(Msg(MessageType.Projectile, b1));
            Assert.Equal(5, stop.Id);
            Assert.Equal(ProjectileEnd.Wall, stop.Reason);
            Assert.Equal(-1, stop.Victim);
            Assert.True(stop.Dropped);
            Assert.True(r.TryNext(out EventRecordKind k2, out ReadOnlySpan<byte> b2));
            Assert.Equal(EventRecordKind.Strike, k2);
            StrikeMessage strike = StrikeMessage.Read(Msg(MessageType.Strike, b2));
            Assert.Equal(4, strike.Striker);
            Assert.Equal(7, strike.RequestId);
            Assert.Equal(1, strike.Count);
            Assert.True(strike.Get(0).Broken);
            Assert.False(r.TryNext(out _, out _));
            r.ExpectEnd();

            // Строгий разбор: пустые записи, ноль записей, вид вне 0…2, оп снаряда 0, обрыв, хвост, недобор.
            Assert.Throws<InvalidOperationException>(() => EventBatchMessage.Write(buf, default));
            var zero = new byte[2];
            zero[0] = (byte)MessageType.EventBatch;
            Assert.Throws<ProtocolException>(() => EventBatchMessage.Open(zero)); // ноль записей

            var bad = new byte[n];
            Array.Copy(buf, bad, n);
            bad[2] = 3; // вид записи вне схемы
            Assert.Throws<ProtocolException>(() => Drains(bad));
            bad[2] = 0;
            bad[16] = 0; // оп снаряда 0 (запись начинается с 15: вид, затем оп)
            Assert.Throws<ProtocolException>(() => Drains(bad));
            bad[16] = 2;
            Assert.Throws<ProtocolException>(() => Drains(bad.AsSpan(0, n - 1).ToArray())); // обрыв последней записи
            Assert.Throws<ProtocolException>(() => Drains(bad.AsSpan(0, n - 13).ToArray())); // обрыв второй
            Assert.Throws<ProtocolException>(() => Drains(bad.Concat(new byte[] { 0 }).ToArray())); // хвост-ползаписи
            Assert.Throws<ProtocolException>(() => Drains(bad.AsSpan(0, 15).ToArray())); // недобор: записей обещано 3

            // 300 записей — Write отказывает (больше байта числа); лишние байты после тела — staging больше не разбирается (битый вид).
            byte[] many = RecordsOf(Enumerable.Repeat((EventRecordKind.Damage, db), 300).ToArray());
            Assert.Throws<InvalidOperationException>(() => EventBatchMessage.Write(new byte[many.Length + 8], many));
            byte[] extra = RecordsOf((EventRecordKind.Damage, db.Concat(new byte[] { 0, 0 }).ToArray()));
            Assert.Throws<InvalidOperationException>(() => EventBatchMessage.Write(new byte[64], extra)); // остаток — битая запись (недобор)
        }

        [Fact]
        public void C035_Codec_Overflow_SecondBatch_NothingLost()
        {
            // 100 записей урона (13 байт) при лимите тела 1024 — правило переполнения AppendEvent: вторая пачка в такте.
            int per = 1 + DamageMessage.Bytes - 1;
            var lens = new List<int>();
            var counts = new List<int>();
            var buf = new byte[EventBatchMessage.MaxBytes];
            int staged = 0, inBatch = 0;
            for (int i = 0; i < 100; i++)
            {
                if (staged + per > EventBatchMessage.MaxBodyBytes)
                {
                    byte[] records = RecordsOf(Enumerable.Repeat((EventRecordKind.Damage, DamageBody()), inBatch).ToArray());
                    lens.Add(EventBatchMessage.Write(buf, records));
                    counts.Add(inBatch);
                    staged = inBatch = 0;
                }

                staged += per;
                inBatch++;
            }

            if (inBatch > 0)
            {
                byte[] records = RecordsOf(Enumerable.Repeat((EventRecordKind.Damage, DamageBody()), inBatch).ToArray());
                lens.Add(EventBatchMessage.Write(buf, records));
                counts.Add(inBatch);
            }

            Assert.Equal(2, lens.Count);
            Assert.Equal(new[] { 78, 22 }, counts); // 78 × 13 = 1014 ≤ 1024 < 79 × 13

            // Оба куска читаются целиком, все 100 записей на месте.
            byte[] first = RecordsOf(Enumerable.Repeat((EventRecordKind.Damage, DamageBody()), 78).ToArray());
            byte[] second = RecordsOf(Enumerable.Repeat((EventRecordKind.Damage, DamageBody()), 22).ToArray());
            int total = 0;
            foreach (byte[] records in new[] { first, second })
            {
                int len = EventBatchMessage.Write(buf, records);
                EventBatchReader r = EventBatchMessage.Open(new ReadOnlySpan<byte>(buf, 0, len));
                while (r.TryNext(out EventRecordKind k, out ReadOnlySpan<byte> body))
                {
                    Assert.Equal(EventRecordKind.Damage, k);
                    Assert.Equal(9, DamageMessage.Read(Msg(MessageType.Damage, body)).Victim);
                    total++;
                }

                r.ExpectEnd();
            }

            Assert.Equal(100, total);
        }

        /// <summary>
        /// По сети: такт с выстрелом лука (выстрел + конец + урон) — у стрелка и свидетеля в 50 т ровно по одной
        /// пачке (40) на такт, счётчики клиента как раньше; игрок в 500 т — ни одной записи; враг жив (35 хп).
        /// Два прогона идентичны (детерминизм).
        /// </summary>
        [Fact]
        public void C035_Net_BowTick_OneBatchPerReceiver_Radii_Determinism()
        {
            (long batches, List<int> perTick, long farBad) Run()
            {
                var shape = MapShape.World(1024);
                WorldModel world = FlatWorldGenerator.Instance.Generate(shape, 3);
                using var rig = new EditTests.EditRig(world, j => j == 0 ? At(1021, 500) : j == 1 ? At(52, 500) : At(500, 500));
                for (int x = 1018; x < 1080; x++)
                    rig.Sim.RequestEdit(new TileCoord(x % 1024, 500), FloorId.Grass, ObjectId.Empty);
                NetClient archer = rig.Join();
                NetClient witness = rig.Join();
                NetClient far = rig.Join();
                rig.Run(20);
                Assert.Equal(NetClientState.InGame, archer.State);
                rig.Sim.AddEnemy(At(2, 500)); // в 5 т от стрелка через шов (враг жив: 35 хп, стрела 10)

                int frame = 0;
                rig.InputOf = c =>
                {
                    if (c != archer)
                        return new InputFrame(Input8.None, 0, 0, 0); // свидетели без ввода — их стрелы не портят счётчик выстрелов
                    frame++;
                    ushort b = frame <= 40 ? InputButtons.Attack : (ushort)0;
                    return new InputFrame(Input8.None, b, 0, 4); // лук
                };

                var perTick = new List<int>();
                long prev = rig.Server.EventBatchesSent;
                rig.AfterTick = () =>
                {
                    perTick.Add((int)(rig.Server.EventBatchesSent - prev));
                    prev = rig.Server.EventBatchesSent;
                };
                rig.Run(60);

                Assert.Equal(1, archer.Projectiles.ShotsSeen);
                Assert.Equal(1, archer.Projectiles.StopsSeen);
                Assert.Equal(1, archer.DamagesSeen);
                Assert.Equal(ProjectileEnd.Hit, archer.Projectiles.LastStop.Reason);
                Assert.Equal(1, witness.Projectiles.ShotsSeen); // 55 т от выстрела, 50 т от конца и жертвы — в окнах 56/96/56
                Assert.Equal(1, witness.Projectiles.StopsSeen);
                Assert.Equal(1, witness.DamagesSeen);
                Assert.Equal(0, far.Projectiles.ShotsSeen); // 500 т — ни выстрела, ни конца, ни урона
                Assert.Equal(0, far.Projectiles.StopsSeen);
                Assert.Equal(0, far.DamagesSeen);
                Assert.Equal(0, archer.DeathsSeen + witness.DeathsSeen + far.DeathsSeen); // враг жив
                Assert.Equal(0, archer.BadMessages + witness.BadMessages + far.BadMessages);
                Assert.Equal(new[] { 2, 2 }, perTick.FindAll(d => d > 0).ToArray()); // такт выстрела и такт конца: по пачке на ближнего
                return (rig.Server.EventBatchesSent, perTick, archer.BadMessages + witness.BadMessages + far.BadMessages);
            }

            var a = Run();
            var b = Run();
            Assert.Equal(a.batches, b.batches);
            Assert.Equal(a.perTick, b.perTick);
            Assert.Equal(0, a.farBad);
        }

        /// <summary>
        /// Смерть вне пачки и порядок за тактом: вражеские удары (10 × 10) копят пачки, смертельный урон того же
        /// такта уходит ДО смерти (24) — клиент видит «…D X». Мутация «не флешить перед смертью» краснеет:
        /// без флеша клиент видит X, затем D.
        /// </summary>
        [Fact]
        public void C035_Net_DeathFlush_OrderAfterDamage()
        {
            var shape = MapShape.World(1024);
            WorldModel world = FlatWorldGenerator.Instance.Generate(shape, 3);
            using var rig = new EditTests.EditRig(world, j => j == 0 ? At(1021, 500) : At(1010, 500));
            for (int x = 995; x < 1024; x++)
                for (int y = 494; y < 507; y++)
                    rig.Sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, ObjectId.Empty);
            NetClient player = rig.Join();
            NetClient witness = rig.Join();
            rig.Run(20);
            Assert.Equal(NetClientState.InGame, player.State);
            rig.Sim.AddMeleeRogue(At(1019, 500)); // в 2 т — «Ближний» с мозгом (1-007), удар 10, период 33 т

            string seq = "";
            player.DamageSeen = d => seq += "D";
            player.DeathSeen = d => seq += "X";

            for (int f = 0; f < 2000 && player.DeathsSeen == 0; f += 10)
                rig.Run(10);
            Assert.Equal(1, player.DeathsSeen);
            rig.Run(60); // 30 т после смерти — возрождение (150 т) не успевает

            Assert.Equal(0, player.RespawnsSeen);
            Assert.Equal(10, player.DamagesSeen); // 100 хп / урон 10
            Assert.Equal(10, seq.Count(c => c == 'D')); // 10 уронов до смерти
            Assert.EndsWith("DX", seq); // пачка с последним уроном — перед смертью
            Assert.Equal(1, witness.DeathsSeen);
            Assert.Equal(10, witness.DamagesSeen);
            Assert.Equal(20, rig.Server.EventBatchesSent); // по одной пачке на получателя за такт удара (2 × 10)
            Assert.Equal(0, player.BadMessages + witness.BadMessages);
        }

        /// <summary>
        /// Лимит новых куч (C-035): 3550 воткнувшихся стрел в обзоре — за такт игроку не больше одной пачки 27
        /// (48 куч); за 80 тактов выгрузка полная (74 пачки), ничего не потеряно и не убрано.
        /// </summary>
        [Fact]
        public void C035_PilesEnterLimit_ArrowField_OneBatchPerTick_AllKnownInK()
        {
            var shape = MapShape.World(1024);
            WorldModel world = FlatWorldGenerator.Instance.Generate(shape, 5);
            using var rig = new EditTests.EditRig(world, _ => At(300, 300));
            for (int x = 305; x < 385; x++)
                for (int y = 295; y < 355; y++)
                    rig.Sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, ObjectId.Empty);
            NetClient c = rig.Join();
            rig.Run(4);
            Assert.Equal(NetClientState.InGame, c.State);

            for (int x = 310; x <= 380; x++) // в окне входа 80 × 56 т от (300,300)
                for (int y = 300; y <= 349; y++)
                    rig.Sim.AddStuckArrowForTests(At(x, y), 0, GroundPile.KindArrowGround, rig.Sim.Tick);
            int added = rig.Sim.Piles.Length;
            Assert.Equal(3550, added);

            var perTick = new List<long>();
            long prev = rig.Server.PileMessagesSent;
            rig.AfterTick = () =>
            {
                perTick.Add(rig.Server.PileMessagesSent - prev);
                prev = rig.Server.PileMessagesSent;
            };
            rig.Run(160); // 80 тактов

            Assert.All(perTick, d => Assert.InRange(d, 0, 1)); // не больше одной пачки 27 за такт
            Assert.Equal(74, rig.Server.PileMessagesSent); // ceil(3550 / 48)
            Assert.Equal(added, c.Piles.Count); // выгрузка полная
            Assert.Equal(0, c.PileGoneSeen); // и ничего не пропало (задержка подбора, в стороне от игрока)
            Assert.Equal(added, rig.Sim.Piles.Length); // на земле всё целое
        }
    }
}
