using System;
using System.Diagnostics;
using Orpheus.Core.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Rng;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Server;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Интерес по окну чанков (карточка 0-022; docs/07 4.4, 3.5, 4.3, 4.11 п. 1; риск 08 4.3 «сеть на торе»).
    /// </summary>
    public class InterestTests
    {
        private const int U = Torus.SubTilesPerTile;

        private readonly ITestOutputHelper _out;

        public InterestTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- Замер ----------

        /// <summary>
        /// Замер карточки 0-022: время <c>SendSnapshots</c> (сборка обзора + запись снимков) на 8 игроках × 64 сущностях —
        /// все в одной окрестности («плотно») и враги по всей карте («врозь»). Только вывод, без порога.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void D07_S4_4_Measure_ViewBuild_8Players64Entities(bool dense)
        {
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            var sim = new Simulation(world, maxEntities: 64);
            var t = new NetTests.ScriptedTransport();
            var server = new NetServer(t, sim, n => ServerRun.SpawnPoint(world.Shape, n), 8, NetModule.Version);
            byte[] login = NetTests.Encode(b => new LoginMessage { Version = NetModule.Version, Name = "p" }.Write(b));
            for (int peer = 1; peer <= 8; peer++)
            {
                t.Connect(peer);
                t.Push(peer, NetChannel.Facts, login);
            }

            server.Poll(0);
            Assert.Equal(8, server.PlayersInGame);

            var rng = new DetRandom(7);
            var bots = new SimBot[56];
            for (int i = 0; i < bots.Length; i++)
            {
                TileCoord tile = dense
                    ? new TileCoord(512 - 48 + rng.NextInt(96), 512 - 32 + rng.NextInt(64))
                    : new TileCoord(rng.NextInt(1024), rng.NextInt(1024));
                int id = sim.AddEntity(new WorldPos(tile.X * U + U / 2, tile.Y * U + U / 2));
                bots[i] = SimBot.Create(WanderBot.Name, id, i, world.Seed);
            }

            const int Warmup = 60;
            const int Measured = 900;
            var sw = new Stopwatch();
            for (int k = 0; k < Warmup + Measured; k++)
            {
                for (int i = 0; i < bots.Length; i++)
                {
                    int slot = sim.SlotOf(bots[i].EntityId);
                    sim.SetInputAt(slot, bots[i].Think(sim.Tick + 1, sim.Entities[slot]));
                }

                server.Poll(sim.Tick);
                sim.Step();
                if (k >= Warmup)
                {
                    sw.Start();
                }

                server.SendSnapshots();
                sw.Stop();
            }

            double usPerTick = sw.Elapsed.TotalMilliseconds * 1000 / Measured;
            _out.WriteLine($"{(dense ? "плотно" : "врозь")}: SendSnapshots {usPerTick:0.0} мкс/такт ({usPerTick / 8:0.0} на игрока), " +
                           $"наибольший полный {server.MaxSnapshotBytes} байт, дельта {server.MaxDeltaBytes}, обрезано {server.SnapshotsTruncated}");
        }
    }
}
