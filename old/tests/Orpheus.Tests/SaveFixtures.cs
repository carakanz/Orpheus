using System;
using Orpheus.Core.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Rng;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Save;
using Orpheus.Server;
using Orpheus.WorldGen;

namespace Orpheus.Tests
{
    /// <summary>Общие заготовки тестов сохранений (0-009): живой мир с правками и ботами.</summary>
    internal static class SaveFixtures
    {
        /// <summary>Мир генератора сборки + снимок генерации, в нём боты <c>wander</c> и правки тайлов по всей карте.</summary>
        public sealed class LiveWorld
        {
            public Simulation Sim = null!;
            public byte[] BaseBytes = null!;
            public SimBot[] Bots = Array.Empty<SimBot>();
            public DetRandom Rng;

            public byte[] BaseSha => WorldSnapshot.StoredHash(BaseBytes);

            /// <summary>Такты: ввод ботов, <paramref name="editsPerTick"/> правок по случайным тайлам (часть — с записью постройки).</summary>
            public void Run(int ticks, int editsPerTick)
            {
                MapShape shape = Sim.Shape;
                for (int t = 0; t < ticks; t++)
                {
                    long tick = Sim.Tick + 1;
                    for (int i = 0; i < Bots.Length; i++)
                    {
                        int slot = Sim.SlotOf(Bots[i].EntityId);
                        Sim.SetInputAt(slot, Bots[i].Think(tick, Sim.Entities[slot]));
                    }

                    for (int e = 0; e < editsPerTick; e++)
                    {
                        var tile = new TileCoord(Rng.NextInt(shape.Width), Rng.NextInt(shape.Height));
                        switch (Rng.NextInt(3))
                        {
                            case 0:
                                Sim.RequestEdit(tile, FloorId.Grass, ObjectId.Empty);
                                break;
                            case 1:
                                Sim.RequestEdit(tile, FloorId.Dirt, ObjectId.MakeAnchor(ObjectId.Rock, (byte)Rng.NextInt(4)));
                                break;
                            default:
                                Sim.RequestEdit(tile, FloorId.WoodFloor, ObjectId.Door,
                                    new BuildRecord((ushort)Rng.NextInt(8), (ushort)Rng.NextInt(65536), (byte)Rng.NextInt(8), FloorId.Grass, (ushort)tick));
                                break;
                        }
                    }

                    Sim.Step();
                    Sim.RefillPools();
                }
            }
        }

        public static LiveWorld Create(int size, ulong seed, int bots)
        {
            WorldModel world = WorldGenModule.Current.Generate(MapShape.World(size), seed);
            var live = new LiveWorld
            {
                BaseBytes = WorldSnapshot.WriteBase(world),
                Sim = new Simulation(world),
                Rng = new DetRandom(seed ^ 0x5A5EUL),
                Bots = new SimBot[bots],
            };
            for (int i = 0; i < bots; i++)
            {
                int id = live.Sim.AddEntity(ServerRun.SpawnPoint(world.Shape, i));
                live.Bots[i] = SimBot.Create(WanderBot.Name, id, i, seed);
            }

            return live;
        }

        /// <summary>Загрузить сохранение поверх снимка генерации в новую симуляцию.</summary>
        public static Simulation Load(byte[] baseBytes, byte[] save)
        {
            WorldModel world = WorldSnapshot.LoadBase(baseBytes);
            LoadedWorldState state = WorldSave.Load(SaveContainer.Read(save), world, WorldSnapshot.StoredHash(baseBytes));
            var sim = new Simulation(world, maxEntities: Math.Max(64, state.Entities.Length + 1));
            state.RestoreInto(sim);
            return sim;
        }
    }
}
