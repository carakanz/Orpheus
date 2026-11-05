using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Orpheus.Core.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Server;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Такт сервера, режим симуляции, журнал ввода и хэш (карточка 0-006; docs/07 2.4, 11.2, 11.5, 11.8 п. 2–3;
    /// docs/08 3.0 приёмка 0.3).
    /// </summary>
    public class SimTests : IDisposable
    {
        private const int U = Torus.SubTilesPerTile;
        private const ulong Seed = 42;

        private readonly ITestOutputHelper _out;
        private readonly string _dir;

        public SimTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "orpheus-sim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        // ---------- Приёмка 0.3 (07 11.8 п. 2–3) ----------

        [Fact]
        public void D00_Sim_S0_3_EightWanderBots_54000Ticks_AtLeast10xRealtime()
        {
            ServerResult r = Run("--create", "--seed", "42", "--size", "1024", "--sim", "--bots", "8:wander", "--ticks", "54000", "--hash");
            Assert.Equal(0, r.ExitCode);
            Assert.Equal(54000, r.Ticks);
            Assert.Equal(54000 / Simulation.HashInterval, r.Hashes.Count);
            _out.WriteLine($"0.3: 8 ботов wander, 54 000 тактов на 1024²: {r.Seconds:F3} с, ×{r.SpeedX:F0} реального времени, " +
                           $"{r.AllocatedBytesPerTick:F2} байт/такт (лог хэша и список хэшей)");
            Assert.True(r.SpeedX >= 10, $"×{r.SpeedX:F1} — медленнее ×10 реального времени");
        }

        [Fact]
        public void D00_Sim_S0_3_RecordReplay_SameHashEvery300thTick()
        {
            string world = Path.Combine(_dir, "w");
            string journal = Path.Combine(_dir, "inputs.bin");
            ServerResult rec = Run("--world", world, "--create", "--seed", "42", "--size", "1024", "--sim",
                "--bots", "8:wander", "--ticks", "54000", "--hash", "--record", journal);
            Assert.Equal(0, rec.ExitCode);
            // Мир — генератора сборки (StubGenerator 0-007): луга, скалы, деревья; боты упираются в них.
            WorldModel created = WorldSnapshot.LoadBase(File.ReadAllBytes(Path.Combine(world, ServerRun.BaseFileName)));
            Assert.Equal(WorldGenModule.GeneratorVersion, created.GeneratorVersion);
            Assert.True(WorldGenModule.GeneratorVersion >= 1);
            _out.WriteLine($"журнал 54 000 тактов, 8 ботов: {new FileInfo(journal).Length} байт");

            // Повтор: мир загружается из папки, ввод — из журнала, ботов нет.
            ServerResult rep = Run("--world", world, "--sim", "--replay", journal, "--hash");
            Assert.Equal(0, rep.ExitCode);
            Assert.Equal(-1, rep.DivergedTick);
            Assert.Equal(54000, rep.Ticks);
            Assert.Equal(180, rep.CheckpointsVerified);
            Assert.Equal(rec.Hashes.Count, rep.Hashes.Count);
            for (int i = 0; i < rec.Hashes.Count; i++)
            {
                Assert.Equal(rec.Hashes[i].Tick, rep.Hashes[i].Tick);
                Assert.Equal(rec.Hashes[i].Total, rep.Hashes[i].Total);
            }

            // Хэши меняются по ходу: боты бродят, состояние не стоит.
            Assert.NotEqual(rec.Hashes[0].Total, rec.Hashes[rec.Hashes.Count - 1].Total);

            // Повтор без папки мира: мир по заголовку журнала (зерно, размер, генератор).
            ServerResult mem = Run("--sim", "--replay", journal);
            Assert.Equal(0, mem.ExitCode);
            Assert.Equal(180, mem.CheckpointsVerified);
        }

        [Fact]
        public void D00_Sim_S0_3_TwoRunsSameSeed_SameHashes_OtherSeed_Different()
        {
            ServerResult a = Run("--create", "--seed", "7", "--sim", "--bots", "8:wander", "--ticks", "3000", "--hash");
            ServerResult b = Run("--create", "--seed", "7", "--sim", "--bots", "8:wander", "--ticks", "3000", "--hash");
            ServerResult c = Run("--create", "--seed", "8", "--sim", "--bots", "8:wander", "--ticks", "3000", "--hash");
            Assert.Equal(10, a.Hashes.Count);
            for (int i = 0; i < a.Hashes.Count; i++)
            {
                Assert.Equal(a.Hashes[i].Total, b.Hashes[i].Total);
                Assert.NotEqual(a.Hashes[i].Total, c.Hashes[i].Total);
            }
        }

        // ---------- Мутации: хэш замечает ----------

        [Fact]
        public void D00_Sim_Mutation_BotOrderChangesHash()
        {
            ulong straight = RunBots(3000, reversed: false);
            ulong again = RunBots(3000, reversed: false);
            ulong reversed = RunBots(3000, reversed: true);
            Assert.Equal(straight, again);
            Assert.NotEqual(straight, reversed);
        }

        [Fact]
        public void D00_Sim_Mutation_ChangedInputInJournal_ReplayNamesTickAndEntity()
        {
            string journal = Path.Combine(_dir, "inputs.bin");
            string mutated = Path.Combine(_dir, "mutated.bin");
            ServerResult rec = Run("--create", "--seed", "42", "--sim", "--bots", "8:wander", "--ticks", "3000", "--record", journal);
            Assert.Equal(0, rec.ExitCode);

            // Первая запись ввода после такта 1000: у первой в ней сущности — другой кадр.
            int victim = -1;
            long victimTick = -1;
            using (var reader = new InputJournalReader(File.OpenRead(journal)))
            using (var writer = new InputJournalWriter(File.Create(mutated), reader.Header))
            {
                var sim = new Simulation(new WorldModel(reader.Header.Shape, reader.Header.Seed));
                JournalRecord kind;
                while ((kind = reader.Next()) != JournalRecord.None)
                {
                    switch (kind)
                    {
                        case JournalRecord.Inputs:
                        {
                            EntityInput[] inputs = reader.Inputs.ToArray();
                            if (victim < 0 && reader.Tick > 1000)
                            {
                                victim = inputs[0].EntityId;
                                victimTick = reader.Tick;
                                InputFrame old = inputs[0].Frame;
                                Input8 other = old.Direction == Input8.East ? Input8.West : Input8.East;
                                inputs[0] = new EntityInput(victim, new InputFrame(other, old.Buttons));
                            }

                            writer.WriteInputs(reader.Tick, inputs);
                            break;
                        }

                        case JournalRecord.Checkpoint:
                            WriteCheckpointCopy(writer, reader);
                            break;
                        case JournalRecord.End:
                            writer.WriteEnd(reader.Tick);
                            break;
                    }

                    if (kind == JournalRecord.End)
                    {
                        break;
                    }
                }
            }

            Assert.True(victim > 0);
            ServerResult rep = Run("--sim", "--replay", mutated);
            Assert.Equal(1, rep.ExitCode);
            long expectedTick = (victimTick + Simulation.HashInterval - 1) / Simulation.HashInterval * Simulation.HashInterval;
            Assert.Equal(expectedTick, rep.DivergedTick);
            Assert.Equal(victim, rep.DivergedEntity);
            _out.WriteLine($"ввод сущности #{victim} изменён на такте {victimTick}; повтор: расхождение на такте {rep.DivergedTick}, сущность #{rep.DivergedEntity}");
        }

        [Fact]
        public void D00_Sim_Hash_CoversEntityFieldsChangedTilesAndTick()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(64), Seed);
            var sim = new Simulation(world);
            int id = sim.AddEntity(new WorldPos(10 * U, 10 * U));
            ulong h0 = StateHash.Compute(sim).Total;
            ulong w0 = StateHash.World(world);

            // Ввод входит в хэш (после такта); движение меняет позицию и остаток.
            sim.SetInput(id, new InputFrame(Input8.East));
            sim.Step();
            ulong h1 = StateHash.Compute(sim).Total;
            Assert.NotEqual(h0, h1);

            // Кнопки — тоже поле сущности.
            var e = sim.GetEntity(id);
            var withButton = e;
            withButton.Input = new InputFrame(e.Input.Direction, 1);
            Assert.NotEqual(StateHash.Entity(e), StateHash.Entity(withButton));
            var withCarry = e;
            withCarry.Mover.Carry = new Int2(e.Mover.Carry.X + 1, e.Mover.Carry.Y);
            Assert.NotEqual(StateHash.Entity(e), StateHash.Entity(withCarry));

            // Мир: неизменённые после генерации чанки в хэш не идут; правка — идёт.
            Assert.Equal(w0, StateHash.World(world));
            sim.RequestEdit(new TileCoord(40, 40), FloorId.Grass, ObjectId.StoneWall);
            sim.Step();
            ulong w1 = StateHash.World(world);
            Assert.NotEqual(w0, w1);

            // Та же правка, но запись постройки другая — другой хэш мира.
            WorldModel world2 = FlatWorldGenerator.Instance.Generate(MapShape.World(64), Seed);
            var sim2 = new Simulation(world2);
            sim2.RequestEdit(new TileCoord(40, 40), FloorId.Grass, ObjectId.StoneWall, BuildRecord.OfPlayer(3, FloorId.Grass));
            sim2.Step();
            Assert.NotEqual(w1, StateHash.World(world2));
        }

        // ---------- Порядок такта ----------

        [Fact]
        public void D00_Sim_TickOrder_EditsOneBatchPerChunkPerTick()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(128), Seed);
            var sim = new Simulation(world);

            // Три правки в чанк (1, 0), одна в (0, 1), одна в (0, 0); вторая правка того же тайла — «занято».
            Assert.True(sim.RequestEdit(new TileCoord(33, 1), FloorId.Grass, ObjectId.WoodWall));
            Assert.True(sim.RequestEdit(new TileCoord(2, 33), FloorId.Grass, ObjectId.Torch));
            Assert.True(sim.RequestEdit(new TileCoord(34, 1), FloorId.Grass, ObjectId.WoodWall));
            Assert.True(sim.RequestEdit(new TileCoord(1, 1), FloorId.Dirt, ObjectId.Empty));
            Assert.True(sim.RequestEdit(new TileCoord(33, 1), FloorId.Grass, ObjectId.StoneWall));
            Assert.Equal(5, sim.PendingEditCount);
            sim.Step();
            Assert.Equal(0, sim.PendingEditCount);

            // Каждый чанк — одна пачка: ревизия ровно +1, сколько бы правок ни пришло.
            Assert.Equal(1u, world.FindChunk(new ChunkCoord(1, 0))!.Revision);
            Assert.Equal(1u, world.FindChunk(new ChunkCoord(0, 1))!.Revision);
            Assert.Equal(1u, world.FindChunk(new ChunkCoord(0, 0))!.Revision);
            Assert.Equal(0u, world.FindChunk(new ChunkCoord(1, 1))!.Revision);

            // Первый пришедший побеждает: на (33, 1) — деревянная стена, каменная — «занято».
            Assert.Equal(ObjectId.WoodWall, world.GetObject(new TileCoord(33, 1)));

            // События по порядку чанков (Y, затем X), внутри чанка — по приходу; после пачки — ревизия.
            var got = new List<string>();
            foreach (SimEvent ev in sim.Events)
            {
                Assert.Equal(1, ev.Tick);
                got.Add(ev.Kind == SimEventKind.TileEdit ? $"{ev.Chunk.X},{ev.Chunk.Y}#{ev.TileIndex}:{ev.Result}" : $"{ev.Chunk.X},{ev.Chunk.Y}=r{ev.Revision}");
            }

            Assert.Equal(
                new[]
                {
                    "0,0#33:Applied", "0,0=r1",
                    "1,0#33:Applied", "1,0#34:Applied", "1,0#33:Busy", "1,0=r1",
                    "0,1#34:Applied", "0,1=r1",
                },
                got);

            // Следующий такт без правок — событий нет, ревизии стоят.
            sim.Step();
            Assert.Equal(0, sim.Events.Length);
            Assert.Equal(1u, world.FindChunk(new ChunkCoord(1, 0))!.Revision);

            // Новый такт — новая пачка: ревизия +1 ещё раз.
            sim.RequestEdit(new TileCoord(35, 1), FloorId.Grass, ObjectId.WoodWall);
            sim.RequestEdit(new TileCoord(36, 1), FloorId.Grass, ObjectId.WoodWall);
            sim.Step();
            Assert.Equal(2u, world.FindChunk(new ChunkCoord(1, 0))!.Revision);
        }

        [Fact]
        public void D00_Sim_TickOrder_InputThenMovementThenEdits()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(64), Seed);
            var sim = new Simulation(world);

            // Сущность у тайла (12, 10): центр в 11,1 тайла; круг 0,4 — до тайла 0,5 тайла.
            int id = sim.AddEntity(new WorldPos(11 * U + 26, 10 * U + U / 2));

            // Ввод на такт применяется в этом же такте: без SetInput стоит.
            sim.Step();
            Assert.Equal(11 * U + 26, sim.GetEntity(id).Mover.Position.X);

            // Стена запрошена в такте, где сущность идёт к ней: движение — по миру начала такта (стены ещё нет).
            sim.SetInput(id, new InputFrame(Input8.East));
            sim.RequestEdit(new TileCoord(12, 10), FloorId.Grass, ObjectId.StoneWall);
            sim.Step();
            int x1 = sim.GetEntity(id).Mover.Position.X;
            Assert.Equal(11 * U + 26 + 51, x1);
            Assert.Equal(ObjectId.StoneWall, world.GetObject(new TileCoord(12, 10)));

            // Дальше стена держит: круг встаёт вплотную (центр на 102 unit левее грани).
            for (int i = 0; i < 10; i++)
            {
                sim.Step();
            }

            Assert.Equal(12 * U - Collision.RadiusUnits, sim.GetEntity(id).Mover.Position.X);

            // Ввод держится, пока не придёт новый; стойка — останавливает.
            Assert.Equal(Input8.East, sim.GetEntity(id).Input.Direction);
            sim.SetInput(id, InputFrame.None);
            sim.Step();
            Assert.Equal(InputFrame.None, sim.GetEntity(id).Input);
        }

        [Fact]
        public void D00_Sim_Blocking_LavaWallsPartsAndDoors()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(64), Seed);
            var sim = new Simulation(world);
            MapShape map = world.Shape;
            sim.RequestEdit(new TileCoord(5, 5), FloorId.Lava, ObjectId.Empty);
            sim.RequestEdit(new TileCoord(6, 5), FloorId.Water, ObjectId.Empty);
            sim.RequestEdit(new TileCoord(7, 5), FloorId.Grass, ObjectId.Torch);
            sim.RequestEdit(new TileCoord(8, 5), FloorId.Grass, ObjectId.Door, BuildRecord.OfPlayer(0, FloorId.Grass));
            sim.RequestEdit(new TileCoord(9, 5), FloorId.Grass, ObjectId.Door,
                new BuildRecord(0, BuildRecord.MaxIntegrity, (byte)BuildFlags.DoorOpen, FloorId.Grass, 0));
            sim.RequestEdit(new TileCoord(10, 5), FloorId.Grass, ObjectId.MakeAnchor(ObjectId.Table, 0), BuildRecord.OfPlayer(0, FloorId.Grass));
            sim.RequestEdit(new TileCoord(11, 5), FloorId.Grass, ObjectId.MakePart(1, 0));
            sim.Step();

            Assert.True(sim.IsBlocked(map, new TileCoord(5, 5)));   // лава
            Assert.False(sim.IsBlocked(map, new TileCoord(6, 5)));  // вода проходима
            Assert.False(sim.IsBlocked(map, new TileCoord(7, 5)));  // факел
            Assert.True(sim.IsBlocked(map, new TileCoord(8, 5)));   // закрытая дверь
            Assert.False(sim.IsBlocked(map, new TileCoord(9, 5)));  // открытая дверь
            Assert.True(sim.IsBlocked(map, new TileCoord(10, 5)));  // якорь стола
            Assert.True(sim.IsBlocked(map, new TileCoord(11, 5)));  // часть стола — по якорю
            Assert.False(sim.IsBlocked(map, new TileCoord(12, 5))); // трава
        }

        // ---------- Без выделений в такте (07 11.6) ----------

        [Fact]
        public void D00_Sim_TickWithBotsEditsAndHash_AllocatesNothing()
        {
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

            Ticks(900); // прогрев: JIT, статические таблицы
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

        // ---------- Журнал, боты, ключи ----------

        [Fact]
        public void D00_Sim_Journal_RoundTrip()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(64), 99);
            var sim = new Simulation(world);
            sim.AddEntity(new WorldPos(100, 200));
            sim.AddEntityWithId(17, new WorldPos(300, 400), 700);
            StateHashResult hash = StateHash.Compute(sim);

            var ms = new MemoryStream();
            using (var w = new InputJournalWriter(ms, JournalHeader.Of(sim), ownsStream: false))
            {
                w.WriteInputs(1, new[] { new EntityInput(1, new InputFrame(Input8.SouthWest, 0xBEEF)), new EntityInput(17, InputFrame.None) });
                w.WriteInputs(2, ReadOnlySpan<EntityInput>.Empty); // пусто — не пишется
                w.WriteCheckpoint(hash, sim.Entities);
                w.WriteEnd(300);
            }

            ms.Position = 0;
            using var r = new InputJournalReader(ms);
            Assert.Equal(99UL, r.Header.Seed);
            Assert.Equal(MapShape.World(64), r.Header.Shape);
            Assert.Equal(2, r.Header.Spawns.Length);
            Assert.Equal(17, r.Header.Spawns[1].EntityId);
            Assert.Equal(new WorldPos(300, 400), r.Header.Spawns[1].Position);
            Assert.Equal(700, r.Header.Spawns[1].UnitsPerTickX10);

            Assert.Equal(JournalRecord.Inputs, r.Next());
            Assert.Equal(1, r.Tick);
            Assert.Equal(2, r.Inputs.Length);
            Assert.Equal(new InputFrame(Input8.SouthWest, 0xBEEF), r.Inputs[0].Frame);
            Assert.Equal(17, r.Inputs[1].EntityId);
            Assert.Equal(JournalRecord.Checkpoint, r.Next());
            Assert.Equal(hash.Total, r.CheckpointHash.Total);
            Assert.Equal(hash.World, r.CheckpointHash.World);
            Assert.Equal(2, r.CheckpointEntityCount);
            Assert.Equal(StateHash.Entity(sim.Entities[1]), r.CheckpointEntityHash(1));
            Assert.Equal(JournalRecord.End, r.Next());
            Assert.Equal(300, r.Tick);
            Assert.Equal(JournalRecord.None, r.Next());
        }

        [Fact]
        public void D00_Sim_Journal_BrokenFilesAreRejected()
        {
            Assert.Throws<InvalidDataException>(() => new InputJournalReader(new MemoryStream(Encoding.ASCII.GetBytes("NOTAJOURNAL-NOTAJOURNAL-NOTAJOURNAL-"))));

            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(64), 1);
            var sim = new Simulation(world);
            sim.AddEntity(new WorldPos(0, 0));
            var ms = new MemoryStream();
            using (var w = new InputJournalWriter(ms, JournalHeader.Of(sim), ownsStream: false))
            {
                w.WriteInputs(5, new[] { new EntityInput(1, new InputFrame(Input8.North)) });
            }

            byte[] full = ms.ToArray();
            using (var r = new InputJournalReader(new MemoryStream(full, 0, full.Length - 2)))
            {
                Assert.Throws<EndOfStreamException>(() => r.Next()); // оборванная запись
            }

            full[full.Length - 3] = 9; // код направления 9 — нет такого
            using (var r = new InputJournalReader(new MemoryStream(full)))
            {
                Assert.Throws<InvalidDataException>(() => r.Next());
            }
        }

        [Fact]
        public void D00_Sim_InputFrame_AllDirectionsRoundTrip()
        {
            Span<byte> buf = stackalloc byte[InputFrame.WireBytes];
            var frames = new List<InputFrame> { InputFrame.None, new InputFrame(Input8.None, 0xFFFF) };
            foreach (Input8 d in Input8.Directions)
            {
                frames.Add(new InputFrame(d, 5));
            }

            foreach (InputFrame f in frames)
            {
                f.Write(buf);
                Assert.Equal(f, InputFrame.Read(buf));
            }
        }

        [Fact]
        public void D00_Sim_Bots_WanderDeterministicIdleStands()
        {
            var self = default(SimEntity);
            var a = new WanderBot(1, 0, Seed);
            var b = new WanderBot(1, 0, Seed);
            var other = new WanderBot(1, 1, Seed);
            int differs = 0;
            var seen = new HashSet<byte>();
            for (int t = 1; t <= 3000; t++)
            {
                InputFrame fa = a.Think(t, self);
                Assert.Equal(fa, b.Think(t, self));
                if (fa != other.Think(t, self))
                {
                    differs++;
                }

                seen.Add(fa.DirectionCode);
            }

            Assert.True(differs > 1000, $"разные боты совпали на {3000 - differs} тактах из 3000");
            Assert.Equal(9, seen.Count); // 8 направлений и стойка

            var idle = SimBot.Create(IdleBot.Name, 1, 0, Seed);
            Assert.Equal(InputFrame.None, idle.Think(1, self));
            Assert.Throws<ArgumentException>(() => SimBot.Create("night_camp", 1, 0, Seed));
        }

        [Fact]
        public void D00_Sim_Options_ParseAndReject()
        {
            ServerOptions o = ServerOptions.Parse(new[]
            {
                "--world", "C:\\tmp\\w2", "--create", "--seed", "42", "--size", "1024", "--sim", "--bots", "8:wander,2:idle",
                "--ticks", "54000", "--hash", "--record", "x.bin",
            });
            Assert.Equal("C:\\tmp\\w2", o.WorldDir);
            Assert.True(o.Create && o.Sim && o.Hash);
            Assert.Equal(42UL, o.Seed);
            Assert.Equal(2, o.Bots.Count);
            Assert.Equal(54000, o.Ticks);

            Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--create", "--size", "2048" }));
            Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--create", "--bots", "8:night_camp" }));
            Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--create", "--bots", "wander" }));
            Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--sim", "--replay", "a", "--record", "b" }));
            Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--replay", "a" }));
            Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--sim" }));
            Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--create", "--frobnicate" }));
        }

        [Fact]
        public void D00_Sim_ReplayOnOtherWorld_Refused()
        {
            string journal = Path.Combine(_dir, "inputs.bin");
            Assert.Equal(0, Run("--create", "--seed", "1", "--sim", "--bots", "2:wander", "--ticks", "600", "--record", journal).ExitCode);
            Assert.Equal(2, Run("--create", "--seed", "2", "--sim", "--replay", journal).ExitCode);
        }

        // ---------- Обычный режим: такт по часам (07 2.4) ----------

        [Fact]
        public void D00_Sim_RealtimeMode_Runs30TicksPerSecond()
        {
            ServerResult r = Run("--create", "--size", "1024", "--bots", "2:wander", "--ticks", "15");
            Assert.Equal(0, r.ExitCode);
            Assert.Equal(15, r.Ticks);
            _out.WriteLine($"15 тактов по часам: {r.Seconds:F3} с");
            Assert.InRange(r.Seconds, 0.45, 2.0);
        }

        // ---------- Лог JSON-строками (07 11.6) ----------

        [Fact]
        public void D00_Sim_Log_JsonLinesWithRequiredFields()
        {
            string world = Path.Combine(_dir, "w");
            var console = new StringWriter();
            ServerResult r = ServerRun.Run(
                ServerOptions.Parse(new[] { "--world", world, "--create", "--sim", "--bots", "1:idle", "--ticks", "600", "--hash" }),
                console);
            Assert.Equal(0, r.ExitCode);

            string[] files = Directory.GetFiles(Path.Combine(world, "logs"), "server-*.jsonl");
            Assert.Single(files);
            string[] lines = File.ReadAllLines(files[0], Encoding.UTF8);
            Assert.True(lines.Length >= 4);
            foreach (string line in lines)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var root = doc.RootElement;
                foreach (string field in new[] { "time", "tick", "level", "cat", "msg" })
                {
                    Assert.True(root.TryGetProperty(field, out _), $"нет поля {field}: {line}");
                }

                Assert.Contains(root.GetProperty("cat").GetString(), JsonLog.Categories);
            }

            Assert.Contains(lines, l => l.Contains("\"msg\":\"хэш\"") && l.Contains("\"tick\":600"));
            Assert.Equal(console.ToString().Trim().Replace("\r\n", "\n"), string.Join("\n", lines));
        }

        // ---------- Помощники ----------

        private ServerResult Run(params string[] args) => ServerRun.Run(ServerOptions.Parse(args), null);

        private static ulong RunBots(int ticks, bool reversed)
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), Seed);
            var sim = new Simulation(world);
            var ids = new int[8];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = sim.AddEntity(ServerRun.SpawnPoint(world.Shape, i));
            }

            var bots = new SimBot[8];
            for (int i = 0; i < bots.Length; i++)
            {
                int entity = reversed ? ids[ids.Length - 1 - i] : ids[i];
                bots[i] = SimBot.Create(WanderBot.Name, entity, i, Seed);
            }

            for (int t = 0; t < ticks; t++)
            {
                foreach (SimBot bot in bots)
                {
                    int slot = sim.SlotOf(bot.EntityId);
                    sim.SetInputAt(slot, bot.Think(sim.Tick + 1, sim.Entities[slot]));
                }

                sim.Step();
            }

            return StateHash.Compute(sim).Total;
        }

        private static void WriteCheckpointCopy(InputJournalWriter writer, InputJournalReader reader)
        {
            int n = reader.CheckpointEntityCount;
            var ids = new int[n];
            var hashes = new ulong[n];
            for (int i = 0; i < n; i++)
            {
                ids[i] = reader.CheckpointEntityId(i);
                hashes[i] = reader.CheckpointEntityHash(i);
            }

            writer.WriteCheckpoint(reader.CheckpointHash, ids, hashes);
        }
    }
}
