using System;
using System.Diagnostics;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
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
    /// E2-3 (шаг 2.3 плана), условие «готово»: из стартового набора бот <c>craft_chain</c> доходит до медной кирки —
    /// рубит дерево, копает валун, идёт к меди, ставит верстак, плавит слитки, делает кирку и берёт гранит.
    /// </summary>
    public class CraftChainTests
    {
        private readonly ITestOutputHelper _out;

        public CraftChainTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>Зерно тяжёлого прогона на настоящем мире (генератор v7, 1024²; с C-016 — v8): из двенадцати (1…12) бот доходит на всех; 3 — одно из самых быстрых (карточка E2-3).</summary>
        public const ulong RealSeed = 3;

        private sealed class Outcome
        {
            public CraftChainBot Bot = null!;
            public NetClient Client = null!;
            public Simulation Sim = null!;
            public long Ticks;
        }

        private Outcome Run(WorldModel world, WorldPos spawn, int limitTicks, int rttMs = 0)
        {
            var wall = Stopwatch.StartNew();
            var rig = new EditTests.EditRig(new Simulation(world, maxEntities: 32), _ => spawn, null);
            try
            {
                NetClient c = rig.Join(rttMs > 0 ? new NetSimSettings { DelayMs = rttMs / 2, JitterMs = 5, Seed = 23 } : (NetSimSettings?)null);
                CraftChainBot? bot = null;
                rig.InputOf = cl =>
                {
                    if (!cl.HasSnapshot || !cl.Latest.TryFind(cl.EntityId, out SnapshotEntity se))
                    {
                        return InputFrame.None;
                    }

                    if (bot == null)
                    {
                        bot = new CraftChainBot(world.Shape, se.Position);
                        cl.EditReplied = bot.OnReply;
                        cl.CraftReplied = bot.OnCraftReply;
                    }

                    return bot.Next(cl, se.Position);
                };

                rig.Run(10);
                long start = rig.Ticks;
                while ((bot == null || !bot.Done) && rig.Ticks - start < limitTicks)
                {
                    rig.Run(30);
                }

                rig.Run(20);
                long ticks = rig.Ticks - start;
                Assert.NotNull(bot);
                _out.WriteLine($"craft_chain: {bot!.Summary}");
                _out.WriteLine($"тактов {ticks} ({ticks / 30.0:0.0} с игры), на часах {wall.Elapsed.TotalSeconds:0.0} с; " +
                               $"заявок крафта серверу {rig.Server.CraftRequestsReceived}, отказов {rig.Server.CraftRequestsRefused}, плохих сообщений {c.BadMessages}; " +
                               $"клиент {c.State}, чанков {c.World?.ChunkCount ?? -1}, снимков {(c.HasSnapshot ? c.Latest.Tick : 0)}, кадров бота {bot.Frames}, копает {c.Digging}, куч {c.Piles.Count}");
                return new Outcome { Bot = bot, Client = c, Sim = rig.Sim, Ticks = ticks };
            }
            finally
            {
                rig.Dispose();
            }
        }

        private void AssertChain(Outcome o)
        {
            CraftChainBot bot = o.Bot;
            Assert.True(bot.Done, $"бот не дошёл до гранита за {o.Ticks} тактов: {bot.Summary}");
            SimEntity e = o.Sim.GetEntity(o.Client.EntityId);

            // Медная кирка — в сумке сервера, на быстрой панели; сделана крафтом (в стартовом наборе её нет).
            Assert.Equal(0, Inventory.StartingKit().Count(Items.CopperPickaxe));
            Assert.Equal(1, e.Inv.Count(Items.CopperPickaxe));
            bool onPanel = false;
            for (int s = 0; s < Inventory.Slots; s++)
            {
                onPanel |= e.Inv.SlotAt(s) == Items.CopperPickaxe;
            }

            Assert.True(onPanel, "медная кирка не на быстрой панели");
            Assert.Equal(e.Inv, o.Client.Inv); // своя сумка = сумка сервера

            // Цепочка: дерево и камень добыты, верстак стоит настоящим объектом мира, руда добыта и переплавлена.
            Assert.True(bot.TreesCut >= 1 && bot.RocksDug >= 1, bot.Summary);
            Assert.True(bot.OresDug >= CraftChainBot.NeedOre / 3, bot.Summary); // залежь даёт 2–3 руды
            Assert.NotNull(bot.BenchTile);
            Assert.Equal(ObjectId.Workbench, ObjectId.IdOf(o.Sim.World.GetObject(bot.BenchTile!.Value)));
            Assert.True(o.Sim.World.TryGetBuild(bot.BenchTile.Value, out BuildRecord rec));
            Assert.Equal(100, rec.Integrity);
            Assert.True(e.Craft.IsEmpty);
            Assert.Equal(0, e.Inv.Count(Items.CopperIngot)); // 8 слитков ушли в кирку

            // Гранит взят: тайл пуст на сервере (без медной кирки он не берётся — E26_Granite_OnlyCopperPickaxe).
            Assert.True(bot.GraniteDug >= 1, bot.Summary); // C-028: взмах медной кирки может снять и соседний гранит (было ровно 1)
            Assert.NotNull(bot.GraniteTile);
            Assert.Equal(ObjectId.Empty, o.Sim.World.GetObject(bot.GraniteTile!.Value));
            Assert.Equal(0, o.Client.BadMessages);
            Assert.Equal(0, bot.CraftRefused);
        }

        private static WorldPos Center(int x, int y) => EditTests.TileCenter(x, y);

        /// <summary>
        /// Быстрый вариант: маленький подготовленный мир (плоский; рощица, валуны, пятно меди, гранит — в 6–20 тайлах от
        /// старта), стенд в одном процессе. Стартовый набор → медная кирка → гранит.
        /// </summary>
        [Fact]
        public void E23_CraftChain_PreparedWorld_StartingKit_To_CopperPickaxe_To_Granite()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            void Put(int x, int y, ushort obj) => world.SetBaseTile(new TileCoord(x, y), FloorId.Grass, obj);
            for (int i = 0; i < 6; i++)
            {
                Put(306 + 2 * (i % 3), 196 + 2 * (i / 3), ObjectId.Tree); // рощица: 6 деревьев = 18 древесины
            }

            for (int i = 0; i < 8; i++)
            {
                Put(294 - i % 4, 203 + i / 4, ObjectId.Rock); // 8 валунов = 8–16 камня
            }

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    Put(312 + x, 208 + y, ObjectId.Ore); // пятно меди 4 × 4 = 32–48 руды
                }
            }

            for (int i = 0; i < 3; i++)
            {
                Put(320 + i, 214, ObjectId.Granite);
            }

            Outcome o = Run(world, Center(300, 200), limitTicks: 6000);
            AssertChain(o);
            Assert.Equal(0, o.Bot.Explores); // всё в окне — наугад не ходил
        }

        /// <summary>
        /// Тяжёлый: настоящий мир (генератор v8 — горы-лабиринт, 1024², зерно <see cref="RealSeed"/>), старт у Очага, задержка сети 100 мс.
        /// </summary>
        [Fact]
        [Trait("Category", "Big")]
        public void E23_CraftChain_Big_RealWorldV8_Rtt100_StartingKit_To_CopperPickaxe_To_Granite()
        {
            MapShape shape = MapShape.World(1024);
            WorldModel world = WorldGenModule.Current.Generate(shape, RealSeed);
            Assert.Equal(8, world.GeneratorVersion);
            Outcome o = Run(world, ServerRun.SpawnPoint(shape, 0), limitTicks: 60_000, rttMs: 100);
            AssertChain(o);
        }

        /// <summary>
        /// Тяжёлый: то же на двенадцати зёрнах настоящего мира без задержки сети — бот не подогнан под одно зерно
        /// (медь и гранит он ищет сам: разведка по клеткам 16 × 16).
        /// </summary>
        [Theory]
        [Trait("Category", "Big")]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(10)]
        [InlineData(11)]
        [InlineData(12)]
        public void E23_CraftChain_Big_RealWorldV8_Seeds1To12(int seed)
        {
            MapShape shape = MapShape.World(1024);
            WorldModel world = WorldGenModule.Current.Generate(shape, (ulong)seed);
            Outcome o = Run(world, ServerRun.SpawnPoint(shape, 0), limitTicks: 40_000);
            _out.WriteLine($"зерно {seed}: {(o.Bot.Done ? "ДОШЁЛ" : "нет")}, тактов {o.Ticks}");
            AssertChain(o);
        }
    }
}
