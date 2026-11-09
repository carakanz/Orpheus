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
            // Мир — генератора сборки (v1 с 1-017): луга, лес, горы, пещеры с объектами; боты упираются в них.
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

        // ---------- Полнота хэша: каждое поле канонического вида (07 11.5) ----------

        private static readonly SimEntity HashBaseEntity = new SimEntity
        {
            Id = 3,
            Kind = SimEntity.KindCharacter,
            Mover = new Mover(new WorldPos(1000, 2000), 512) { Carry = new Int2(4, -3) },
            Input = new InputFrame(Input8.East, 0x0010),
        };

        /// <summary>Поле сущности → копия базовой сущности, где изменено только оно.</summary>
        public static IEnumerable<object[]> EntityFields() => new[]
        {
            EntityCase("Id", e => { e.Id = 4; return e; }),
            EntityCase("Kind", e => { e.Kind = 2; return e; }),
            EntityCase("Position.X", e => { e.Mover.Position = new WorldPos(1001, 2000); return e; }),
            EntityCase("Position.Y", e => { e.Mover.Position = new WorldPos(1000, 2001); return e; }),
            EntityCase("Carry.X", e => { e.Mover.Carry = new Int2(5, -3); return e; }),
            EntityCase("Carry.Y", e => { e.Mover.Carry = new Int2(4, -2); return e; }),
            EntityCase("UnitsPerTickX10", e => { e.Mover.UnitsPerTickX10 = 513; return e; }),
            EntityCase("Input.Direction", e => { e.Input = new InputFrame(Input8.West, 0x0010); return e; }),
            EntityCase("Input.Buttons", e => { e.Input = new InputFrame(Input8.East, 0x0011); return e; }),
        };

        [Theory]
        [MemberData(nameof(EntityFields))]
        public void D00_Sim_Hash_EveryEntityFieldCounts(string field, Func<SimEntity> mutated)
        {
            SimEntity changed = mutated();
            Assert.NotEqual(StateHash.Entity(HashBaseEntity), StateHash.Entity(changed));

            // И в общем хэше: сущность с другим полем — другое состояние.
            Assert.NotEqual(TotalWith(HashBaseEntity), TotalWith(changed));
            _out.WriteLine($"{field}: ок");
        }

        /// <summary>Поле заголовка или мира → (мир, тактов до хэша): меняется ровно одно.</summary>
        public static IEnumerable<object[]> HeaderFields() => new[]
        {
            new object[] { "Tick", 1, Seed, 64, 64, true, 0 },
            new object[] { "Seed", 0, Seed + 1, 64, 64, true, 0 },
            new object[] { "Shape.Width", 0, Seed, 128, 64, true, 0 },
            new object[] { "Shape.Height", 0, Seed, 64, 128, true, 0 },
            new object[] { "Shape.Wraps", 0, Seed, 64, 64, false, 0 },
            new object[] { "GeneratorVersion", 0, Seed, 64, 64, true, 1 },
        };

        [Theory]
        [MemberData(nameof(HeaderFields))]
        public void D00_Sim_Hash_EveryHeaderFieldCounts(string field, int ticks, ulong seed, int width, int height, bool wraps, int generatorVersion)
        {
            ulong baseline = HeaderHash(0, Seed, 64, 64, true, 0);
            Assert.Equal(baseline, HeaderHash(0, Seed, 64, 64, true, 0));
            Assert.NotEqual(baseline, HeaderHash(ticks, seed, width, height, wraps, generatorVersion));
            _out.WriteLine($"{field}: ок");
        }

        private static readonly BuildRecord HashBaseRecord = new BuildRecord(7, 900, (byte)BuildFlags.DoorOpen, FloorId.Grass, 1234);

        /// <summary>Поле изменённого чанка (слои, запись постройки, координата, ревизия) → правки другого мира.</summary>
        public static IEnumerable<object[]> TileFields()
        {
            BuildRecord r = HashBaseRecord;
            return new[]
            {
                TileCase("Floor", sim => sim.RequestEdit(new TileCoord(5, 5), FloorId.Dirt, ObjectId.Door, r)),
                TileCase("Object (младший байт)", sim => sim.RequestEdit(new TileCoord(5, 5), FloorId.Grass, ObjectId.Gate, r)),
                TileCase("Object (старший байт: поворот)", sim => sim.RequestEdit(new TileCoord(5, 5), FloorId.Grass, ObjectId.MakeAnchor(ObjectId.Door, 1), r)),
                TileCase("Record.Owner", sim => sim.RequestEdit(new TileCoord(5, 5), FloorId.Grass, ObjectId.Door,
                    new BuildRecord(8, r.Integrity, r.Flags, r.NaturalFloor, r.DebrisEndTick))),
                TileCase("Record.Integrity", sim => sim.RequestEdit(new TileCoord(5, 5), FloorId.Grass, ObjectId.Door,
                    new BuildRecord(r.Owner, 901, r.Flags, r.NaturalFloor, r.DebrisEndTick))),
                TileCase("Record.Flags", sim => sim.RequestEdit(new TileCoord(5, 5), FloorId.Grass, ObjectId.Door,
                    new BuildRecord(r.Owner, r.Integrity, (byte)BuildFlags.DoorKeepOpen, r.NaturalFloor, r.DebrisEndTick))),
                TileCase("Record.NaturalFloor", sim => sim.RequestEdit(new TileCoord(5, 5), FloorId.Grass, ObjectId.Door,
                    new BuildRecord(r.Owner, r.Integrity, r.Flags, FloorId.Dirt, r.DebrisEndTick))),
                TileCase("Record.DebrisEndTick", sim => sim.RequestEdit(new TileCoord(5, 5), FloorId.Grass, ObjectId.Door,
                    new BuildRecord(r.Owner, r.Integrity, r.Flags, r.NaturalFloor, 1235))),
                TileCase("Record есть / нет", sim => sim.RequestEdit(new TileCoord(5, 5), FloorId.Grass, ObjectId.Door)),
                TileCase("Record на другом тайле (индекс)", sim =>
                {
                    sim.RequestEdit(new TileCoord(5, 5), FloorId.Grass, ObjectId.Door);
                    sim.RequestEdit(new TileCoord(6, 5), FloorId.Grass, ObjectId.Door, r);
                }, sameLayersAsBase: true),
                TileCase("Chunk.X", sim => sim.RequestEdit(new TileCoord(5 + 32, 5), FloorId.Grass, ObjectId.Door, r)),
                TileCase("Chunk.Y", sim => sim.RequestEdit(new TileCoord(5, 5 + 32), FloorId.Grass, ObjectId.Door, r)),
                TileCase("Revision", sim =>
                {
                    sim.RequestEdit(new TileCoord(5, 5), FloorId.Grass, ObjectId.Door, r);
                    sim.Step();
                    sim.RequestEdit(new TileCoord(5, 5), FloorId.Grass, ObjectId.Door, r);
                }),
            };
        }

        [Theory]
        [MemberData(nameof(TileFields))]
        public void D00_Sim_Hash_EveryChangedTileFieldCounts(string field, Action<Simulation> edits, bool sameLayersAsBase)
        {
            ulong baseline = WorldHashAfter(sim =>
            {
                sim.RequestEdit(new TileCoord(5, 5), FloorId.Grass, ObjectId.Door, HashBaseRecord);
                if (sameLayersAsBase)
                {
                    sim.RequestEdit(new TileCoord(6, 5), FloorId.Grass, ObjectId.Door);
                }
            });
            Assert.Equal(baseline, WorldHashAfter(sim =>
            {
                sim.RequestEdit(new TileCoord(5, 5), FloorId.Grass, ObjectId.Door, HashBaseRecord);
                if (sameLayersAsBase)
                {
                    sim.RequestEdit(new TileCoord(6, 5), FloorId.Grass, ObjectId.Door);
                }
            }));
            Assert.NotEqual(baseline, WorldHashAfter(edits));
            _out.WriteLine($"{field}: ок");
        }

        [Fact]
        public void D00_Sim_Hash_UnchangedChunksOutWorldHashInTotal_NoWorldFlagsYet()
        {
            // Неизменённые после генерации чанки — это снимок генерации (зерно и версия — в заголовке): в хэш не идут.
            WorldModel a = FlatWorldGenerator.Instance.Generate(MapShape.World(64), Seed);
            WorldModel b = FlatWorldGenerator.Instance.Generate(MapShape.World(64), Seed);
            b.FindChunk(new ChunkCoord(1, 1))!.SetBaseTile(0, FloorId.Dirt, ObjectId.Empty);
            Assert.Equal(StateHash.World(a), StateHash.World(b));

            // Хэш мира входит в общий: правка тайла меняет общий хэш при тех же сущностях и такте.
            var sa = new Simulation(a);
            var sb = new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.World(64), Seed));
            sa.Step();
            sb.RequestEdit(new TileCoord(1, 1), FloorId.Dirt, ObjectId.Empty);
            sb.Step();
            Assert.NotEqual(StateHash.Compute(sa).World, StateHash.Compute(sb).World);
            Assert.NotEqual(StateHash.Compute(sa).Total, StateHash.Compute(sb).Total);

            // Флагов мира в коде ещё нет (07 5.2 — с сюжетом): строка «флаги — число 0» в хэше есть,
            // табличный случай «флаг мира» добавится вместе с самими флагами.
        }

        private static object[] EntityCase(string field, Func<SimEntity, SimEntity> change) =>
            new object[] { field, new Func<SimEntity>(() => change(HashBaseEntity)) };

        private static object[] TileCase(string field, Action<Simulation> edits, bool sameLayersAsBase = false) =>
            new object[] { field, edits, sameLayersAsBase };

        private static ulong TotalWith(SimEntity e)
        {
            var world = new WorldModel(MapShape.World(64), Seed);
            return StateHash.Compute(world, 0, new[] { e }).Total;
        }

        private static ulong HeaderHash(int ticks, ulong seed, int width, int height, bool wraps, int generatorVersion)
        {
            var shape = new MapShape(width, height, wraps);
            var sim = new Simulation(new WorldModel(shape, seed, generatorVersion));
            for (int i = 0; i < ticks; i++)
            {
                sim.Step();
            }

            return StateHash.Compute(sim).Total;
        }

        private static ulong WorldHashAfter(Action<Simulation> edits)
        {
            var sim = new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.World(128), Seed));
            edits(sim);
            sim.Step();
            return StateHash.World(sim.World);
        }

        /// <summary>
        /// Правки мира в симуляции (замечание П: в CLI хэш мира постоянен — правок из ввода ещё нет):
        /// боты + правки по зерну каждые 30 тактов. Два прогона — одни хэши на каждом 300-м такте, хэш мира
        /// растёт с правками; одна другая правка на такте 1500 — хэши расходятся с первой точки после неё.
        /// </summary>
        [Fact]
        public void D00_Sim_EditsInSimulation_SameHashes_WorldHashMoves_OneEditDiverges()
        {
            List<StateHashResult> a = RunWithEdits(mutateAtTick: -1);
            List<StateHashResult> b = RunWithEdits(mutateAtTick: -1);
            List<StateHashResult> c = RunWithEdits(mutateAtTick: 1500);
            Assert.Equal(10, a.Count);
            var worlds = new HashSet<ulong>();
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].Total, b[i].Total);
                worlds.Add(a[i].World);
                if (a[i].Tick < 1500)
                {
                    Assert.Equal(a[i].Total, c[i].Total);
                }
                else
                {
                    Assert.NotEqual(a[i].World, c[i].World);
                    Assert.NotEqual(a[i].Total, c[i].Total);
                }
            }

            Assert.Equal(a.Count, worlds.Count); // хэш мира на каждой точке новый
        }

        private static List<StateHashResult> RunWithEdits(long mutateAtTick)
        {
            WorldModel world = WorldGenModule.Current.Generate(MapShape.World(1024), Seed);
            var sim = new Simulation(world);
            var bots = new SimBot[8];
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i] = SimBot.Create(WanderBot.Name, sim.AddEntity(ServerRun.SpawnPoint(world.Shape, i)), i, Seed);
            }

            var rng = new Orpheus.Core.Rng.DetRandom(Seed ^ 0xED17);
            var hashes = new List<StateHashResult>();
            while (sim.Tick < 3000)
            {
                foreach (SimBot bot in bots)
                {
                    int slot = sim.SlotOf(bot.EntityId);
                    sim.SetInputAt(slot, bot.Think(sim.Tick + 1, sim.Entities[slot]));
                }

                if ((sim.Tick + 1) % 30 == 0)
                {
                    var tile = new TileCoord(rng.NextInt(1024), rng.NextInt(1024));
                    ushort obj = sim.Tick + 1 == mutateAtTick ? ObjectId.StoneWall : ObjectId.WoodWall;
                    sim.RequestEdit(tile, FloorId.Grass, obj, BuildRecord.OfPlayer(1, FloorId.Grass));
                }

                sim.Step();
                sim.RefillPools();
                if (sim.Tick % Simulation.HashInterval == 0)
                {
                    hashes.Add(StateHash.Compute(sim));
                }
            }

            return hashes;
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

        /// <summary>
        /// Порядок фаз (ввод → движение → правки) виден по одному такту: сущность в 20 unit от грани тайла
        /// (12, 10) шагает на восток на 51 unit, в этом же такте запрошена стена на (12, 10).
        /// <list type="bullet">
        /// <item>правильно: ввод уже действует, стены в начале такта нет — центр сдвинулся ровно на 51;</item>
        /// <item>«правки до движения»: стена уже стоит — круг упирается, сдвиг 20;</item>
        /// <item>«движение до ввода»: в фазе движения ввода ещё нет — сдвиг 0.</item>
        /// </list>
        /// Мутации переставленных фаз в <see cref="Simulation.Step"/> роняют этот тест (проверено, откачено).
        /// </summary>
        [Fact]
        public void D00_Sim_TickOrder_InputThenMovementThenEdits()
        {
            const int gap = 20;
            int startX = 12 * U - Collision.RadiusUnits - gap;
            int y = 10 * U + U / 2;

            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(64), Seed);
            var sim = new Simulation(world);
            int id = sim.AddEntity(new WorldPos(startX, y));

            sim.SetInput(id, new InputFrame(Input8.East));
            sim.RequestEdit(new TileCoord(12, 10), FloorId.Grass, ObjectId.StoneWall);
            sim.Step();

            int moved = sim.GetEntity(id).Mover.Position.X - startX;
            Assert.NotEqual(0, moved);   // «движение до ввода» дало бы 0
            Assert.NotEqual(gap, moved); // «правки до движения» дали бы упор через 20
            Assert.Equal(51, moved);
            Assert.Equal(ObjectId.StoneWall, world.GetObject(new TileCoord(12, 10)));
            Assert.Equal(Input8.East, sim.GetEntity(id).Input.Direction);

            // Контроль сценария: если стена стоит до такта, тот же шаг упирается ровно через 20 unit.
            WorldModel walled = FlatWorldGenerator.Instance.Generate(MapShape.World(64), Seed);
            var control = new Simulation(walled);
            control.RequestEdit(new TileCoord(12, 10), FloorId.Grass, ObjectId.StoneWall);
            control.Step();
            int cid = control.AddEntity(new WorldPos(startX, y));
            control.SetInput(cid, new InputFrame(Input8.East));
            control.Step();
            Assert.Equal(gap, control.GetEntity(cid).Mover.Position.X - startX);

            // Ввод держится, пока не придёт новый; стойка — останавливает в этом же такте.
            int x1 = control.GetEntity(cid).Mover.Position.X;
            control.SetInput(cid, InputFrame.None);
            control.Step();
            Assert.Equal(InputFrame.None, control.GetEntity(cid).Input);
            Assert.Equal(x1, control.GetEntity(cid).Mover.Position.X);
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

        // D00_Sim_TickWithBotsEditsAndHash_AllocatesNothing (07 11.6) — в AllocationTests: непараллельная коллекция (0-020).

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

            full[full.Length - InputFrame.WireBytes] = 9; // код направления 9 — нет такого
            using (var r = new InputJournalReader(new MemoryStream(full)))
            {
                Assert.Throws<InvalidDataException>(() => r.Next());
            }
        }

        /// <summary>
        /// Журнал формата 1 (кадр 3 байта, без прицела и ячейки — 1-002): читается, угол и ячейка — по умолчанию,
        /// повтор проходит такты без падения. Код направления вне 0…8 в кадре формата 1 — тот же отказ.
        /// </summary>
        [Fact]
        public void D00_Sim_Journal_V1_ReplaysWithDefaultAngleAndSlot()
        {
            var ms = new MemoryStream();
            void U16(ushort v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); }
            void U32(uint v) { U16((ushort)v); U16((ushort)(v >> 16)); }
            void U64(ulong v) { U32((uint)v); U32((uint)(v >> 32)); }

            ms.Write(new byte[] { (byte)'O', (byte)'R', (byte)'P', (byte)'J' });
            ms.WriteByte(1); // версия формата 1
            U64(42); U32(64); U32(64); ms.WriteByte(0); U32(4); U64(0); U16(1);
            U32(1); U32(100); U32(200); U32(700); // одна сущность: id 1, (100, 200), 700
            ms.WriteByte((byte)'I'); U32(5); U16(1);
            U32(1); ms.WriteByte(3); ms.WriteByte(0xEF); ms.WriteByte(0xBE); // кадр v1: код 3, кнопки 0xBEEF
            ms.WriteByte((byte)'E'); U32(6);

            ms.Position = 0;
            using (var r = new InputJournalReader(ms))
            {
                Assert.Equal(42UL, r.Header.Seed);
                Assert.Single(r.Header.Spawns);
                Assert.Equal(JournalRecord.Inputs, r.Next());
                Assert.Equal(5, r.Tick);
                InputFrame f = r.Inputs[0].Frame;
                Assert.Equal(Input8.South, f.Direction); // код 3 = направление 2
                Assert.Equal(0xBEEF, f.Buttons);
                Assert.Equal((ushort)0, f.Angle16);
                Assert.Equal((byte)0, f.Slot);
                Assert.Equal(JournalRecord.End, r.Next());
            }

            // Повтор: те же кадры в живой симуляции — такты идут, кадр с кнопками принят.
            var sim = new Simulation(new WorldModel(MapShape.World(64), 42));
            sim.AddEntityWithId(1, new WorldPos(100, 200), 700);
            sim.SetInput(1, InputFrame.ReadV1(stackalloc byte[] { 3, 0xEF, 0xBE }));
            for (int t = 0; t < 30; t++)
            {
                sim.Step();
            }
        }

        /// <summary>Журнал формата 2 (1-002): прицел и ячейка кадра доходят до повтора побитово.</summary>
        [Fact]
        public void D00_Sim_Journal_V2_AngleAndSlotRoundTrip()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(64), 5);
            var sim = new Simulation(world);
            sim.AddEntity(new WorldPos(100, 200));

            var frames = new[]
            {
                new InputFrame(Input8.East, 0, 1, 0), // шаг прицела 1/65 536
                new InputFrame(Input8.East, 0, 65535, 7),
                new InputFrame(Input8.East, InputButtons.Dig, 32768, 3),
            };
            var ms = new MemoryStream();
            using (var w = new InputJournalWriter(ms, JournalHeader.Of(sim), ownsStream: false))
            {
                for (int i = 0; i < frames.Length; i++)
                {
                    w.WriteInputs((long)i + 1, new[] { new EntityInput(1, frames[i]) });
                }

                w.WriteEnd(99);
            }

            ms.Position = 0;
            using var r = new InputJournalReader(ms);
            for (int i = 0; i < frames.Length; i++)
            {
                Assert.Equal(JournalRecord.Inputs, r.Next());
                Assert.Equal(frames[i], r.Inputs[0].Frame);
            }

            Assert.Equal(JournalRecord.End, r.Next());
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
