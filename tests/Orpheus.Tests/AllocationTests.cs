using System;
using Orpheus.Core.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Server;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Коллекция тестов «без выделений» (07 11.6): идут одни, после всех параллельных коллекций (0-020, замечание 2
    /// проверки 0-016). <see cref="GC.GetAllocatedBytesForCurrentThread"/> считает и то, что на этом потоке выделил
    /// рантайм: первый JIT метода (строки <c>ldstr</c>, статические поля), загрузку типа. Кто из параллельных тестов
    /// первым дошёл до общего кода — тот и платит, отсюда редкий красный (6064 байта в 2 из 16 прогонов).
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class AllocationCollection
    {
        public const string Name = "Без выделений (по одному)";
    }

    /// <summary>
    /// Тесты <c>*_NoAllocations</c>, <c>*_AllocatesNothing</c>: перенесены из <c>WorldTests</c>, <c>NetTests</c>,
    /// <c>SimTests</c> без изменения проверяемого. Каждый сначала прогревается теми же проходами, что и замеряет
    /// (все ветви кода уже пройдены и скомпилированы), затем замер одного прохода — должен быть 0 байт.
    /// </summary>
    [Collection(AllocationCollection.Name)]
    public sealed class AllocationTests
    {
        private const int U = Torus.SubTilesPerTile;

        /// <summary>Проходов прогрева перед замером.</summary>
        private const int WarmupPasses = 3;

        private readonly ITestOutputHelper _out;

        public AllocationTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary><paramref name="warmupPasses"/> проходов прогрева, затем байты, выделенные этим потоком за один проход.</summary>
        private static long AllocatedInPass(Action pass, int warmupPasses = WarmupPasses)
        {
            for (int i = 0; i < warmupPasses; i++)
            {
                pass();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            pass();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // ---------- Мир (из WorldTests) ----------

        [Fact]
        public void D00_World_Edits_NoAllocationsInTick()
        {
            var chunk = new Chunk();
            var edits = new TileEdit[64];
            for (int i = 0; i < edits.Length; i++)
            {
                edits[i] = new TileEdit((ushort)(i * 7), FloorId.Dirt, ObjectId.Rock);
            }

            var results = new EditResult[edits.Length];
            var wire = new byte[1024];
            Action pass = () =>
            {
                for (int k = 0; k < 100; k++)
                {
                    uint rev = chunk.ApplyEdits(edits, results);
                    TilePatch.WriteAccepted(wire, 0, new ChunkCoord(0, 0), rev, edits, results);
                }
            };

            Assert.Equal(0, AllocatedInPass(pass));
        }

        [Fact]
        public void D00_World_WindowAndZone_NoAllocations()
        {
            var map = MapShape.World(4096);
            var players = new[] { TileCenter(10, 10), TileCenter(4000, 4000) };
            var a = new ChunkCoord[ActiveZone.MaxChunks(map, players.Length)];
            var b = new ChunkCoord[a.Length];
            var wa = new ChunkCoord[ChunkWindow.MaxChunks];
            var wb = new ChunkCoord[ChunkWindow.MaxChunks];
            int na = ActiveZone.Update(map, players, ReadOnlySpan<ChunkCoord>.Empty, a);
            int nw = ChunkWindow.Update(map, players[0], ReadOnlySpan<ChunkCoord>.Empty, wa);
            Action pass = () =>
            {
                for (int k = 0; k < 200; k++)
                {
                    players[0] = Torus.Offset(map, players[0], new Int2(U, U / 3));
                    int nb = ActiveZone.Update(map, players, a.AsSpan(0, na), b);
                    (a, b, na) = (b, a, nb);
                    int nwb = ChunkWindow.Update(map, players[0], wa.AsSpan(0, nw), wb);
                    (wa, wb, nw) = (wb, wa, nwb);
                }
            };

            Assert.Equal(0, AllocatedInPass(pass));
        }

        private static WorldPos TileCenter(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        // ---------- Сеть (из NetTests) ----------

        /// <summary>
        /// 11.6 «без выделений в такте»: сервер по сети в установившейся игре (8 игроков, ввод 60 Гц, снимок 30 Гц) —
        /// разбор ввода и сборка снимков не выделяют памяти (транспорт — заглушка: свой расход у LiteNetLib).
        /// </summary>
        [Fact]
        public void D00_Net_Server_SteadyState_NoAllocations()
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

            // Пакеты ввода заранее: на каждый кадр — свой номер. Проход — 300 тактов; прогрев — те же проходы
            // (смена направления каждые 60 кадров, все ветви приёма и снимка) до замера.
            const int Ticks = 300;
            const int Passes = WarmupPasses + 1;
            var inputs = new byte[Passes * Ticks * 2][];
            for (int f = 0; f < inputs.Length; f++)
            {
                var m = new InputMessage { FirstFrame = (uint)f + 1, Frame3 = WireFrame.From(new InputFrame(Input8.FromIndex(f / 60 % 8))) };
                inputs[f] = NetTests.Encode(b => m.Write(b));
            }

            int tick = 0;
            Action pass = () =>
            {
                for (int i = 0; i < Ticks; i++, tick++)
                {
                    for (int peer = 1; peer <= 8; peer++)
                    {
                        t.Push(peer, NetChannel.Unreliable, inputs[2 * tick]);
                        t.Push(peer, NetChannel.Unreliable, inputs[2 * tick + 1]);
                    }

                    server.Poll(tick);
                    sim.Step();
                    server.SendSnapshots();
                }
            };

            long allocated = AllocatedInPass(pass);
            Assert.Equal(8 * Passes * Ticks, server.SnapshotsSent);
            Assert.Equal(Passes * Ticks, t.Flushes); // один сброс отправки на такт, а не на каждый снимок (0-015)
            Assert.Equal(8 * 2 * Passes * Ticks, server.InputsAccepted);
            Assert.Equal(0, allocated);
        }

        /// <summary>
        /// 0-022 (docs/07, 4.4; 11.6): сетка сущностей по чанкам и обзор по окну чанков в установившемся режиме без
        /// выделений — 8 игроков бегают туда-обратно по 30 тайлов (окно пересчитывается, сущности уходят и держатся
        /// гистерезисом), 48 сущностей бродят через границы чанков (переносы в сетке).
        /// </summary>
        [Fact]
        public void D07_S4_4_GridAndView_SteadyState_NoAllocations()
        {
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            var sim = new Simulation(world, maxEntities: 64);
            var npcs = new SimBot[48];
            for (int i = 0; i < npcs.Length; i++)
            {
                WorldPos p = ServerRun.SpawnPoint(world.Shape, i, new TileCoord(512 + (i % 6) * 12 - 30, 512 + (i / 6) * 8 - 32));
                npcs[i] = SimBot.Create(WanderBot.Name, sim.AddEntity(p), i, 1);
            }

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

            // Проход — 300 тактов: 150 на восток, 150 на запад (30 тайлов туда и обратно) — проходы одинаковы.
            const int Ticks = 300;
            const int Passes = WarmupPasses + 1;
            var inputs = new byte[Passes * Ticks * 2][];
            for (int f = 0; f < inputs.Length; f++)
            {
                Input8 d = f % (2 * Ticks) < Ticks ? Input8.East : Input8.West;
                var m = new InputMessage { FirstFrame = (uint)f + 1, Frame3 = WireFrame.From(new InputFrame(d)) };
                inputs[f] = NetTests.Encode(b => m.Write(b));
            }

            int tick = 0;
            Action pass = () =>
            {
                for (int i = 0; i < Ticks; i++, tick++)
                {
                    for (int peer = 1; peer <= 8; peer++)
                    {
                        t.Push(peer, NetChannel.Unreliable, inputs[2 * tick]);
                        t.Push(peer, NetChannel.Unreliable, inputs[2 * tick + 1]);
                    }

                    for (int k = 0; k < npcs.Length; k++)
                    {
                        int slot = sim.SlotOf(npcs[k].EntityId);
                        sim.SetInputAt(slot, npcs[k].Think(sim.Tick + 1, sim.Entities[slot]));
                    }

                    server.Poll(tick);
                    sim.Step();
                    server.SendSnapshots();
                }
            };

            for (int i = 0; i < WarmupPasses; i++)
            {
                pass();
            }

            long moves = sim.Grid.Moves;
            long recalcs = server.ViewWindowRecalcs;
            long kept = server.ViewKeptByHysteresis;
            long before = GC.GetAllocatedBytesForCurrentThread();
            pass();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            _out.WriteLine($"проход: переносов в сетке {sim.Grid.Moves - moves}, пересчётов окна {server.ViewWindowRecalcs - recalcs}, " +
                           $"оставлено гистерезисом {server.ViewKeptByHysteresis - kept}, непустых чанков {sim.Grid.ChunkCount}; выделено {allocated} байт");
            Assert.True(sim.Grid.Moves - moves > 50);
            Assert.True(server.ViewWindowRecalcs - recalcs >= 8);
            Assert.True(server.ViewKeptByHysteresis - kept > 0);
            Assert.Equal(0, allocated);
        }

        // ---------- Такт сервера (из SimTests) ----------

        [Fact]
        public void D00_Sim_TickWithBotsEditsAndHash_AllocatesNothing()
        {
            const ulong Seed = 42;
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), Seed);
            var sim = new Simulation(world);
            var bots = new SimBot[8];
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i] = SimBot.Create(WanderBot.Name, sim.AddEntity(ServerRun.SpawnPoint(world.Shape, i)), i, Seed);
            }

            var pending = new EntityInput[16];
            ulong sink = 0;
            long inTick = 0;
            void Ticks(int n)
            {
                for (int k = 0; k < n; k++)
                {
                    for (int i = 0; i < bots.Length; i++)
                    {
                        int slot = sim.SlotOf(bots[i].EntityId);
                        sim.SetInputAt(slot, bots[i].Think(sim.Tick + 1, sim.Entities[slot]));
                    }

                    sim.CopyPendingInputs(pending);

                    // Правки — каждый 7-й такт, в разные чанки; каждая вторая — постройка с записью:
                    // первая постройка в чанке берёт словарь записей из пула (backlog [такт сервера]).
                    if (sim.Tick % 7 == 0)
                    {
                        int t = (int)(sim.Tick % 1000);
                        sim.RequestEdit(new TileCoord(t, t / 3), FloorId.Dirt, ObjectId.Rock);
                        sim.RequestEdit(new TileCoord(t * 5 + 1, t / 3), FloorId.Grass, ObjectId.WoodWall, BuildRecord.OfPlayer(1, FloorId.Grass));
                    }

                    // В такте (шаг и хэш) — ни байта; пополнение пула — между тактами, вне замера.
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    sim.Step();
                    if (sim.Tick % Simulation.HashInterval == 0)
                    {
                        sink ^= StateHash.Compute(sim).Total;
                    }

                    inTick += GC.GetAllocatedBytesForCurrentThread() - before;
                    sim.RefillPools();
                }
            }

            Ticks(900); // прогрев: JIT, статические таблицы, хэш (каждые HashInterval тактов), правки и постройки
            inTick = 0;
            int misses = world.BuildMaps.Misses;
            long total = GC.GetAllocatedBytesForCurrentThread();
            Ticks(3000);
            total = GC.GetAllocatedBytesForCurrentThread() - total;
            _out.WriteLine($"3000 тактов: в такте выделено {inTick} байт, с пополнением пула {total} байт (хэш {sink:x16})");
            Assert.Equal(0, inTick);
            Assert.Equal(misses, world.BuildMaps.Misses);
            Assert.True(total > 0, "постройки в новых чанках должны были брать словари из пула");
        }
    }
}
