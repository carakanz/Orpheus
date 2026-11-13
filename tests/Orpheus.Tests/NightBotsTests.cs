using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Orpheus.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Server;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Карточка 1-026 «ночные боты» (приёмка `08` 3.1 1.5 и 1.6; docs/07, 11.3): клиентские сценарии тем же
    /// протоколом, что игрок.
    /// <para>
    /// 1.5 — <c>build_room</c>: бот добывает камень и древесину, строит комнату 5 × 5 (пол 3 × 3, стены по
    /// периметру, дверь в центре южной стены), входит и закрывает дверь; ночь при включённом спавнере —
    /// ни удара сквозь стены/дверь, ни смерти, рассвет дождён.
    /// </para>
    /// <para>
    /// 1.6 — та же хижина в осаде 10 разбойников (модель <c>EnemyWallsTests.W13</c>): никто не проходит и не
    /// застревает; и <c>night_camp</c>: без укрытия разбойник убивает персонажа «первого уровня» за минуту
    /// (<c>02</c> 8.2), убийца — из «Смерти».
    /// </para>
    /// </summary>
    public class NightBotsTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const int Size = 1024;
        private const int SiteX = 512, SiteY = 512;

        private readonly ITestOutputHelper _out;

        public NightBotsTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static WorldPos At(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        private static int Hp(Simulation sim, int id) => sim.GetEntity(id).Health;

        private static int Enemies(Simulation sim)
        {
            int n = 0;
            foreach (SimEntity e in sim.Entities)
            {
                if (e.Kind == SimEntity.KindEnemy)
                {
                    n++;
                }
            }

            return n;
        }

        private static TileCoord TileOf(Simulation sim, int id) =>
            Torus.TileOf(sim.World.Shape, sim.SlotOf(id) >= 0 ? sim.Entities[sim.SlotOf(id)].Mover.Position : default);

        /// <summary>
        /// Поляна для бота: плоский луг, центр (512, 512); скалы кольцами 6 и 7 тайлов (камня ≥ 48),
        /// деревья кольцом 10 тайлов (древесины ≥ 70) — целей добычи с запасом, радиус добычи бота 20.
        /// </summary>
        private static WorldModel SiteWorld(ulong seed, out MapShape shape)
        {
            shape = MapShape.World(Size);
            WorldModel w = FlatWorldGenerator.Instance.Generate(shape, seed);
            for (int dy = -10; dy <= 10; dy++)
            {
                for (int dx = -10; dx <= 10; dx++)
                {
                    int r = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    var t = Torus.Wrap(shape, new TileCoord(SiteX + dx, SiteY + dy));
                    if (r == 6 || r == 7)
                    {
                        w.SetBaseTile(t, FloorId.Grass, ObjectId.Rock);
                    }
                    else if (r == 10 && (dx + 10) % 3 == 0 && (dy + 10) % 3 == 0)
                    {
                        w.SetBaseTile(t, FloorId.Grass, ObjectId.Tree);
                    }
                }
            }

            return w;
        }

        /// <summary>Стенд: сервер + один клиент, ввод клиента — <see cref="BuildRoomBot"/>; выход — в тайл центра.</summary>
        private BuildRoomBot BuildRig(EditTests.EditRig rig, NetClient client)
        {
            var bot = new BuildRoomBot(rig.Sim.World.Shape, At(SiteX, SiteY));
            client.EditReplied = bot.OnReply;
            rig.InputOf = v => v == client && v.HasSnapshot && v.Latest.TryFind(v.EntityId, out SnapshotEntity se)
                ? bot.Next(v, se.Position, rig.NowMicros)
                : InputFrame.None;
            return bot;
        }

        /// <summary>Добыча + стройка до закрытой двери; <c>false</c> — не успел за лимит (тогда диагноз в журнал).</summary>
        private bool DriveToClosedDoor(EditTests.EditRig rig, BuildRoomBot bot, int limit)
        {
            long start = rig.Ticks;
            while (!bot.DoorClosed && rig.Ticks - start < limit)
            {
                rig.Run(25);
            }

            _out.WriteLine(Diag(bot, rig.Ticks - start));
            return bot.DoorClosed;
        }

        private static string Diag(BuildRoomBot bot, long ticks) =>
            $"этап {bot.Stage}, тиков {ticks}, добыча {bot.GatherTicks} (камня {bot.StoneAtGatherEnd}, древесины {bot.WoodAtGatherEnd}, " +
            $"скал {bot.RocksDug}, деревьев {bot.TreesChopped}), полов {bot.FloorsPlaced}/9, стен {bot.WallsPlaced}/15, " +
            $"дверь поставлена {bot.DoorPlaced}, закрыта {bot.DoorClosed}, отказов правки {bot.Refused} [{bot.RefusalsText}], смертей {bot.Deaths}" +
            (bot.FailReason != null ? $", отказ: {bot.FailReason}" : string.Empty);

        // ------------------------------------------------ приёмка 1.5: комната держит ночь

        /// <summary>
        /// 1.5: бот строит комнату 5 × 5, входит и закрывает дверь; дальше сутки идут в 30 раз быстрее, спавнер
        /// включён — ночь под стенами: ни удара сквозь дверь/стены (<see cref="BuildRoomBot.DamageThroughDoor"/>),
        /// ни смерти, рассвет дождён, враги ночь под стенами были.
        /// </summary>
        [Fact]
        public void D08_3_1_1_5_BuildRoomBot_Closes_Door_Night_Holds_Dawn()
        {
            WorldModel w = SiteWorld(1, out _);
            var sim = new Simulation(w, maxEntities: 64);
            using var rig = new EditTests.EditRig(sim, n => At(SiteX, SiteY), null);
            NetClient client = rig.Join();
            rig.Run(3);
            BuildRoomBot bot = BuildRig(rig, client);

            Assert.True(DriveToClosedDoor(rig, bot, 14000), Diag(bot, rig.Ticks));
            Assert.Equal(9, bot.FloorsPlaced);
            Assert.Equal(15, bot.WallsPlaced);
            Assert.Equal(100, Hp(sim, client.EntityId));
            Assert.Equal(0, bot.Deaths);

            sim.DayScale = 30; // ночь 10 800 тактов часов → 360 тактов симуляции
            sim.SpawnEnabled = true;
            int toDawn = (DayClock.DawnStartTick - sim.WorldTime + DayClock.TicksPerDay) % DayClock.TicksPerDay / 30 + 120;
            long t0 = rig.Ticks;
            int enemiesSeen = 0;
            while (!bot.DawnReached && rig.Ticks - t0 < toDawn)
            {
                rig.Run(10);
                enemiesSeen = Math.Max(enemiesSeen, Enemies(sim));
            }

            _out.WriteLine($"ночь: тиков {rig.Ticks - t0}, врагов в ночи {enemiesSeen}, урон за дверью {bot.DamageThroughDoor} кадров, " +
                           $"смертей {bot.Deaths}, рассвет {bot.DawnReached}, часы мира {sim.WorldTime}");
            Assert.True(bot.DawnReached, $"рассвет не дождались за {rig.Ticks - t0} тактов: {Diag(bot, rig.Ticks)}");
            Assert.True(enemiesSeen >= 1, "за ночь к постройке никто не пришёл — спавнер не работал");
            Assert.Equal(0, bot.DamageThroughDoor);
            Assert.Equal(0, bot.Deaths);
            Assert.Equal(100, Hp(sim, client.EntityId));
        }

        // ------------------------------------------------ приёмка 1.6: хижина в осаде

        /// <summary>
        /// 1.6 (модель <c>EnemyWallsTests.W13</c> на постройке бота): 10 разбойников по кругу, 900 тактов —
        /// персонаж цел (никто не прошёл сквозь стену и закрытую дверь), у каждого эпизод «упёрся»
        /// прерывается (подряд ≤ 610 тактов, 9.3), никто не внутри периметра.
        /// </summary>
        [Fact]
        public void D08_3_1_1_6_BuildRoomBot_Hut_Siege_10_Rogues_NoOnePasses()
        {
            WorldModel w = SiteWorld(2, out _);
            var sim = new Simulation(w, maxEntities: 64);
            using var rig = new EditTests.EditRig(sim, n => At(SiteX, SiteY), null);
            NetClient client = rig.Join();
            rig.Run(3);
            BuildRoomBot bot = BuildRig(rig, client);

            Assert.True(DriveToClosedDoor(rig, bot, 14000), Diag(bot, rig.Ticks));

            int[] at =
            {
                6, 0, 6, 3, 6, -3, 0, 6, 3, 6, -6, 3, -6, 0, -6, -3, 0, -6, -3, -6,
            };
            var ring = new int[10];
            for (int i = 0; i < ring.Length; i++)
            {
                ring[i] = sim.AddMeleeRogue(At(SiteX + at[i * 2], SiteY + at[i * 2 + 1]));
            }

            var blockedRun = new int[ring.Length];
            for (int t = 0; t < 900; t++)
            {
                rig.Run(1);
                Assert.Equal(100, Hp(sim, client.EntityId)); // «0 проходов»: сквозь стену/дверь никто не прошёл и не ударил
                for (int i = 0; i < ring.Length; i++)
                {
                    SimEntity e = sim.GetEntity(ring[i]);
                    blockedRun[i] = e.Brain.State == EnemyState.Blocked ? blockedRun[i] + 1 : 0;
                    Assert.True(blockedRun[i] <= 610, $"враг {ring[i]} застревает: {blockedRun[i]} тактов подряд в Blocked (предел 610)");
                    int cheb = Cheb(TileOf(sim, ring[i]), new TileCoord(SiteX, SiteY));
                    Assert.True(cheb >= 3, $"враг {ring[i]} внутри периметра: тайл {TileOf(sim, ring[i])}, центр {SiteX},{SiteY}");
                }
            }

            _out.WriteLine($"осада: урон за дверью {bot.DamageThroughDoor}, смертей {bot.Deaths}, врагов живо " +
                           $"{ring.Count(id => sim.GetEntity(id).Alive)}/10, максимум Blocked подряд {blockedRun.Max()}");
            Assert.Equal(0, bot.DamageThroughDoor);
            Assert.Equal(0, bot.Deaths);
        }

        private int Cheb(TileCoord a, TileCoord b)
        {
            Int2 d = Torus.Delta(MapShape.World(Size), a, b);
            return Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
        }

        // ------------------------------------------------ приёмка 1.6: стоящий в поле гибнет

        /// <summary>
        /// 1.6: <c>night_camp</c> стоит в поле без укрытия — разбойник в 3 тайлах убивает персонажа
        /// «первого уровня» за минуту (≤ 1 800 тактов, `02` 8.2); убийца и тип урона — из «Смерти».
        /// </summary>
        [Fact]
        public void D08_3_1_1_6_NightCampBot_InField_Killed_By_Rogue_Within_Minute()
        {
            var shape = MapShape.World(Size);
            WorldModel w = FlatWorldGenerator.Instance.Generate(shape, 3);
            var sim = new Simulation(w, maxEntities: 32);
            using var rig = new EditTests.EditRig(sim, n => At(SiteX, SiteY), null);
            NetClient client = rig.Join();
            var camp = new NightCampBot();
            client.DeathSeen = camp.OnDeath;
            rig.InputOf = v => v == client ? camp.Next(v) : InputFrame.None;
            rig.Run(3);

            int rogue = sim.AddMeleeRogue(At(SiteX + 3, SiteY));
            long t0 = rig.Ticks;
            while (camp.Alive && rig.Ticks - t0 < 3000)
            {
                rig.Run(10);
            }

            _out.WriteLine($"смерть: {camp.TicksToDeath} тактов ({camp.TicksToDeath / (double)Simulation.TickRate:0.#} с), " +
                           $"убийца #{camp.KillerId}, тип урона {camp.KillerDamageType}, ударов {camp.HitsTaken}, " +
                           $"минимум здоровья {camp.MinHealthPct} %; враг жив {sim.GetEntity(rogue).Alive}");
            Assert.False(camp.Alive, $"разбойник не убил стоящего за {rig.Ticks - t0} тактов");
            Assert.True(camp.TicksToDeath <= 1800, $"смерть за {camp.TicksToDeath} тактов > 1 800 (минута, `02` 8.2)");
            Assert.True(camp.KillerId >= 0, "убийца не виден в «Смерти»");
            Assert.True(camp.HitsTaken >= 1, "ударов не видно — полоска здоровья не падала");
        }

        /// <summary>
        /// 1.5 на 10 зёрнах (<c>`08` 3.1</c> «10 из 10»): комната, закрытая дверь, ночь (×30, спавнер) — в каждом
        /// зерне рассвет дождён, урона за дверью нет, смертей нет, здоровье целое.
        /// </summary>
        [Fact]
        public void D08_3_1_1_5_Ten_Seeds_Room_Holds_Night()
        {
            for (int seed = 1; seed <= 10; seed++)
            {
                WorldModel w = SiteWorld((ulong)seed, out _);
                var sim = new Simulation(w, maxEntities: 64);
                using var rig = new EditTests.EditRig(sim, n => At(SiteX, SiteY), null);
                NetClient client = rig.Join();
                rig.Run(3);
                BuildRoomBot bot = BuildRig(rig, client);

                Assert.True(DriveToClosedDoor(rig, bot, 14000), $"зерно {seed}: {Diag(bot, rig.Ticks)}");
                sim.DayScale = 30;
                sim.SpawnEnabled = true;
                int toDawn = (DayClock.DawnStartTick - sim.WorldTime + DayClock.TicksPerDay) % DayClock.TicksPerDay / 30 + 120;
                long t0 = rig.Ticks;
                while (!bot.DawnReached && rig.Ticks - t0 < toDawn)
                {
                    rig.Run(10);
                }

                _out.WriteLine($"зерно {seed}: рассвет {bot.DawnReached}, урон за дверью {bot.DamageThroughDoor} кадров, " +
                               $"смертей {bot.Deaths}, здоровье {Hp(sim, client.EntityId)}");
                Assert.True(bot.DawnReached, $"зерно {seed}: рассвет не дождались: {Diag(bot, rig.Ticks)}");
                Assert.Equal(0, bot.DamageThroughDoor);
                Assert.Equal(0, bot.Deaths);
                Assert.Equal(100, Hp(sim, client.EntityId));
            }
        }

        /// <summary>
        /// 1.6 в поле на 10 зёрнах (<c>`08` 3.1</c> «10 из 10»): разбойник в 3 тайлах убивает стоящего за
        /// ≤ 1 800 тактов в каждом зерне; медиана смерти — в журнал приёмки.
        /// </summary>
        [Fact]
        public void D08_3_1_1_6_Ten_Seeds_Field_Kills_Within_Minute()
        {
            var deaths = new int[10];
            for (int seed = 1; seed <= 10; seed++)
            {
                var shape = MapShape.World(Size);
                WorldModel w = FlatWorldGenerator.Instance.Generate(shape, (ulong)seed);
                var sim = new Simulation(w, maxEntities: 32);
                using var rig = new EditTests.EditRig(sim, n => At(SiteX, SiteY), null);
                NetClient client = rig.Join();
                var camp = new NightCampBot();
                client.DeathSeen = camp.OnDeath;
                rig.InputOf = v => v == client ? camp.Next(v) : InputFrame.None;
                rig.Run(3);

                sim.AddMeleeRogue(At(SiteX + 3, SiteY));
                long t0 = rig.Ticks;
                while (camp.Alive && rig.Ticks - t0 < 3000)
                {
                    rig.Run(10);
                }

                deaths[seed - 1] = camp.TicksToDeath;
                Assert.False(camp.Alive, $"зерно {seed}: стоящий выжил {rig.Ticks - t0} тактов");
                Assert.True(camp.TicksToDeath <= 1800, $"зерно {seed}: смерть за {camp.TicksToDeath} тактов > 1 800 (минута)");
            }

            Array.Sort(deaths);
            double median = (deaths[4] + deaths[5]) / 2.0;
            _out.WriteLine($"10 зёрен: смерть тактами [{string.Join(", ", deaths)}], медиана {median:0.#} тактов " +
                           $"({median / Simulation.TickRate:0.#} с, расчёт `02` 8.2 — 20–40 с)");
        }

        // ------------------------------------------------ UDP: полная ночь по сети, RTT 100

        /// <summary>
        /// Прогон по сети (образец <c>NetTests.RunUdp</c>): бот <c>build_room</c> в мире генератора v1, RTT 100 мс,
        /// сутки в 30 раз быстрее, старт на рассвете: стройка при свете (~6 мин реального времени: ресурсы редки),
        /// ночь (780-я секунда прогона) — под стенами. Критерий: дверь закрыта, урона за дверью нет, смертей нет.
        /// </summary>
        [Fact]
        [Trait("Category", "Big")]
        public void D08_3_1_BuildRoom_Udp_Rtt100_Night_Under_Walls()
        {
            BotReport report = RunBuildRoomUdp();
            string line = report.Bots.Single().BuildRoom ?? "(строки нет)";
            _out.WriteLine(line);
            Assert.Equal("InGame", report.Bots.Single().State);
            Assert.Contains("закрыта да", line);
            Assert.Contains("урон за дверью 0 кадров", line);
            Assert.Contains("смертей 0", line);
        }

        /// <summary>Прогон вне метода-теста (xUnit1031: ожидание сервера — не в тесте; образец <c>NetTests.RunUdp</c>).</summary>
        private static BotReport RunBuildRoomUdp()
        {
            int port = FreeUdpPort();
            ServerOptions so = ServerOptions.Parse(new[]
            {
                "--create", "--seed", "42", "--size", "1024", "--port", port.ToString(),
                "--debug-day-scale", "30",
            });
            using var stopServer = new CancellationTokenSource();
            Task<ServerResult> server = Task.Run(() => ServerRun.Run(so, null, stopServer.Token));

            var bo = BotOptions.Parse(new[]
            {
                "--connect", $"127.0.0.1:{port}", "--count", "1", "--script", BuildRoomBot.Name,
                "--rtt", "100", "--loss", "0", "--seconds", "800", "--seed", "7",
            });
            BotReport report;
            try
            {
                report = BotRun.Run(bo, null);
            }
            finally
            {
                Thread.Sleep(300); // отключение бота доходит до сервера
                stopServer.Cancel();
                Assert.Equal(0, server.GetAwaiter().GetResult().ExitCode);
            }

            return report;
        }

        private static int FreeUdpPort()
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)s.LocalEndPoint!).Port;
        }
    }
}
