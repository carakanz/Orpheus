using System;
using System.Collections.Generic;
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
    /// E3-2 (шаг 3.2), сеть (протокол 30): «снаряд» (37) — «золотые» байты выстрела и конца, отказ на испорченное; по сети —
    /// стрела через шов тора попадает, клиент видит выстрел и конец, его полёт по событию доходит до точки сервера;
    /// предсказанная своя стрела подменяется выстрелом сервера.
    /// </summary>
    public class BowNetTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static string Hex(ReadOnlySpan<byte> b)
        {
            var parts = new string[b.Length];
            for (int i = 0; i < b.Length; i++)
                parts[i] = b[i].ToString("x2");
            return string.Join(" ", parts);
        }

        [Fact]
        public void E32_Codec_GoldenBytes_Projectile()
        {
            Assert.Equal(38, (int)MessageType.Projectile);
            Assert.Equal(31, NetModule.ProtocolVersion);
            var buf = new byte[64];
            var shot = new ProjectileMessage
            {
                Op = ProjectileOp.Shot, Id = 0x01020304, Owner = 7, Kind = Projectiles.KindArrow, Hostile = true, SpawnTick = 0x0A0B0C0D,
                Position = new WorldPos(0x00112233, 0x00445566), AimRaw = 0x8000, SpeedX256 = 61166, RangeUnits = 7680,
            };
            int n = shot.Write(buf);
            Assert.Equal(ProjectileMessage.ShotBytes, n);
            Assert.Equal("26 01 04 03 02 01 07 00 00 00 01 01 0d 0c 0b 0a 33 22 11 00 66 55 44 00 00 80 ee ee 00 00 00 1e 00 00",
                Hex(new ReadOnlySpan<byte>(buf, 0, n)));
            ProjectileMessage back = ProjectileMessage.Read(new ReadOnlySpan<byte>(buf, 0, n));
            Assert.Equal(shot.Id, back.Id);
            Assert.Equal(shot.Owner, back.Owner);
            Assert.True(back.Hostile);
            Assert.Equal(shot.Position, back.Position);
            Assert.Equal(shot.AimRaw, back.AimRaw);
            Assert.Equal(shot.SpeedX256, back.SpeedX256);
            Assert.Equal(shot.RangeUnits, back.RangeUnits);
            Assert.Equal(shot.SpawnTick, back.SpawnTick);

            var stop = new ProjectileMessage
            {
                Op = ProjectileOp.Stop, Id = 5, Reason = ProjectileEnd.Wall, Position = new WorldPos(256, 512), Victim = -1, Dropped = true,
            };
            n = stop.Write(buf);
            Assert.Equal(ProjectileMessage.StopBytes, n);
            Assert.Equal("26 02 05 00 00 00 02 00 01 00 00 00 02 00 00 ff ff ff ff 01", Hex(new ReadOnlySpan<byte>(buf, 0, n)));
            back = ProjectileMessage.Read(new ReadOnlySpan<byte>(buf, 0, n));
            Assert.Equal(ProjectileEnd.Wall, back.Reason);
            Assert.Equal(-1, back.Victim);
            Assert.True(back.Dropped);

            buf[6] = 9; // причина вне 1…3
            Assert.Throws<ProtocolException>(() => ProjectileMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));
            buf[6] = 2;
            buf[1] = 3; // операция
            Assert.Throws<ProtocolException>(() => ProjectileMessage.Read(new ReadOnlySpan<byte>(buf, 0, n)));
            Assert.Throws<ProtocolException>(() => ProjectileMessage.Read(new ReadOnlySpan<byte>(buf, 0, n - 1)));
        }

        /// <summary>
        /// По сети (`02` 4.6 п. 6): игрок у правого края мира натягивает лук 0,6 с и стреляет на восток — враг за швом в 5 т
        /// получает 10; клиент видит выстрел и конец «попал», его полёт по событию кончается в точке сервера; стрела снята.
        /// </summary>
        [Fact]
        public void E32_Net_ArrowHitsThroughSeam()
        {
            var shape = MapShape.World(1024);
            WorldModel world = FlatWorldGenerator.Instance.Generate(shape, 3);
            using var rig = new EditTests.EditRig(world, _ => new WorldPos(1021 * U + U / 2, 500 * U + U / 2));
            for (int x = 1018; x < 1030; x++)
                rig.Sim.RequestEdit(new TileCoord(x % 1024, 500), FloorId.Grass, ObjectId.Empty);
            NetClient c = rig.Join();
            rig.Run(20);
            Assert.Equal(NetClientState.InGame, c.State);
            int enemy = rig.Sim.AddEnemy(new WorldPos(2 * U + U / 2, 500 * U + U / 2)); // 1021,5 → 2,5: 5 тайлов через шов

            int frame = 0;
            rig.InputOf = _ =>
            {
                frame++;
                ushort b = frame <= 40 ? InputButtons.Attack : (ushort)0;
                return new InputFrame(Input8.None, b, 0, 4); // ячейка 4 — простой лук
            };
            var damage = new List<SimEvent>();
            rig.AfterTick = () =>
            {
                foreach (SimEvent ev in rig.Sim.Events)
                    if (ev.Kind == SimEventKind.Damage)
                        damage.Add(ev);
            };
            rig.Run(60);

            Assert.Single(damage);
            Assert.Equal(enemy, damage[0].Victim);
            Assert.Equal(10, damage[0].Damage);
            Assert.Equal(49, rig.Sim.GetEntity(c.EntityId).Inv.Count(Items.Arrows));
            Assert.Equal(1, c.Projectiles.ShotsSeen);
            Assert.Equal(1, c.Projectiles.StopsSeen);
            Assert.Equal(ProjectileEnd.Hit, c.Projectiles.LastStop.Reason);
            Assert.Equal(enemy, c.Projectiles.LastStop.Victim);
            Assert.InRange(c.Projectiles.LastStop.Position.X, 0, 3 * U);
            Assert.Equal(1, c.DamagesSeen); // E3-1: попадание стрелой — то же событие «урон» (37), цифра у стрелка
        }

        /// <summary>
        /// E3-1 × E3-2 (просьба проверяющего E3-1): стрелок дальше 56 тайлов от жертвы всё равно получает событие «урон» своего
        /// попадания (атакующему — всегда); посторонний игрок у стрелка (тоже дальше 56 т от жертвы) — не получает. Общий механизм
        /// снаряда с дальностью 90 т (у лука 30 т — так далеко стрелок оказывается, только убежав за время полёта).
        /// </summary>
        [Fact]
        public void E32_Net_FarArcher_GetsOwnHitDamageEvent()
        {
            var shape = MapShape.World(1024);
            WorldModel world = FlatWorldGenerator.Instance.Generate(shape, 5);
            using var rig = new EditTests.EditRig(world, _ => new WorldPos(100 * U + U / 2, 300 * U + U / 2));
            for (int x = 98; x < 190; x++)
                rig.Sim.RequestEdit(new TileCoord(x, 300), FloorId.Grass, ObjectId.Empty);
            NetClient archer = rig.Join();
            NetClient other = rig.Join();
            rig.Run(20);
            Assert.Equal(NetClientState.InGame, archer.State);
            Assert.Equal(NetClientState.InGame, other.State);
            int enemy = rig.Sim.AddEnemy(new WorldPos(180 * U + U / 2, 300 * U + U / 2)); // 80 т на восток
            var seen = new List<DamageMessage>();
            archer.DamageSeen = m => seen.Add(m);
            long otherBefore = other.DamagesSeen;
            int id = rig.Sim.SpawnProjectile(archer.EntityId, false, Projectiles.KindArrow, new WorldPos(100 * U + U / 2, 300 * U + U / 2), 0,
                28 * U * 256 / 30, 90 * U, 10, 0, strong: true);
            Assert.True(id > 0);
            rig.Run(110);
            Assert.Single(seen);
            Assert.Equal(enemy, seen[0].Victim);
            Assert.Equal(archer.EntityId, seen[0].Attacker);
            Assert.Equal(10, seen[0].Amount);
            Assert.True((seen[0].Flags & DamageMessage.FlagStrong) != 0, "полное натяжение — сильный");
            Assert.Equal(otherBefore, other.DamagesSeen); // далеко от жертвы и не участник
            Assert.Equal(ProjectileEnd.Hit, archer.Projectiles.LastStop.Reason);
        }

        /// <summary>
        /// Полёт клиента по выстрелу — тот же счёт, что у ядра: точка через N тактов совпадает с сервером; конец по пути — стрела
        /// стоит в точке конца; своя предсказанная стрела подменяется выстрелом сервера (одна картинка, не две).
        /// </summary>
        [Fact]
        public void E32_Client_FlightMatchesCore_PredictedReplaced()
        {
            var map = MapShape.World(64);
            var cp = new ClientProjectiles(map);
            var p = new Projectile { Id = 3, Owner = 9, Origin = new WorldPos(62 * U, 10 * U), AimRaw = 0, SpeedX256 = 28 * U * 256 / 30, RangeUnits = 30 * U };
            var shot = new BowAction { ShotDraw = BowAction.FullDrawFrames, ShotAim = 0, ShotX = p.Origin.X, ShotY = p.Origin.Y };
            cp.AddPredicted(shot, Items.SimpleBow, 1_000_000);
            Assert.Single(cp.Items);
            Assert.False(cp.Items[0].Confirmed);
            Assert.Equal(p.SpeedX256, cp.Items[0].P.SpeedX256);

            cp.OnMessage(ProjectileMessage.ShotOf(p), 9, 1_050_000);
            Assert.Single(cp.Items); // подменила, не добавила
            Assert.True(cp.Items[0].Confirmed);
            Assert.Equal(1, cp.OwnConfirmed);

            long at10 = 1_000_000 + (long)(10 * ClientProjectiles.TickUs);
            WorldPos client = cp.PositionOf(cp.Items[0], at10);
            WorldPos core = Torus.Wrap(map, p.PositionAt(10));
            Assert.True(Torus.Dist2(map, client, core) <= 4, $"{client} ≠ {core}");
            Assert.True(client.X < 10 * U, "через шов — обёрнута");

            var stopAt = Torus.Wrap(map, p.PositionAt(5));
            cp.OnMessage(ProjectileMessage.StopOf(new ProjectileStop(3, 9, ProjectileEnd.Wall, stopAt, -1, false)), 9, 1_100_000);
            Assert.True(cp.Arrived(cp.Items[0], at10));
            Assert.True(Torus.Dist2(map, cp.PositionOf(cp.Items[0], at10), stopAt) <= 4);
            cp.Update(at10);
            Assert.Single(cp.Items); // воткнулась — висит
            cp.Update(at10 + ClientProjectiles.StuckUs + 1);
            Assert.Empty(cp.Items);

            cp.AddPredicted(shot, Items.SimpleBow, 5_000_000); // сервер не подтвердил — гаснет
            cp.Update(5_000_000 + ClientProjectiles.UnconfirmedUs + 1);
            Assert.Empty(cp.Items);
            Assert.Equal(1, cp.OwnExpired);
        }
    }
}
