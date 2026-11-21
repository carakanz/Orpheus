using System;
using System.Collections.Generic;
using System.Linq;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Карточка E2-6 (план 2.6): залежи пятнами (медь, россыпи валунов, ядра гранита) и ярусы инструментов — что чем
    /// копается. Числа генератора — на 30 зёрнах 1024² (общий замер на класс, зёрна параллельно, как в
    /// <see cref="ReliefTests"/>); правила копания — на таблицах и маленьком мире.
    /// </summary>
    public class DepositTests
    {
        private const int W = 1024;
        private const int U = Torus.SubTilesPerTile;
        private readonly ITestOutputHelper _out;

        public DepositTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private sealed class Seed
        {
            public int Id;
            public DepositStats Ore = null!;
            public DepositStats Rocks = null!;
            public DepositStats Granite = null!;
            public List<DepositPatch> Patches = null!;
            public double Reach;
            public long OreOnMarksOrShore;
            public long RocksOnMarksOrShore;
            public long GraniteOffRocky;
            public bool OreReachableIn600;
            public bool Stamped;
        }

        private static readonly Lazy<Seed[]> All = new Lazy<Seed[]>(
            () => Enumerable.Range(1, 30).AsParallel().Select(Measure).OrderBy(s => s.Id).ToArray());

        private static Seed Measure(int seed)
        {
            // Те же шаги, что GeneratorV1.BuildTiles; пометки троп и проходов снимаются до этапа 12 — он добавляет свои
            // проходы в обрывах уже после залежей (рядом с готовым пятном), это не «залежь на тропе».
            var t = new TileWorld(MacroGenerator.Generate(W, (ulong)seed, BiomeSet.Slice));
            t.ForestStamped = HearthStage.RunMacro(t.Macro);
            TileStage.Run(t);
            HearthStage.StampCopper(t);
            byte[] marks = (byte[])t.Marks.Clone();
            ConnectStage.Run(t);
            CliffStage.DropStubs(t);
            bool[] rockMask = DepositStats.MaskOf(t, DepositKind.Rocks);
            bool[] graniteMask = DepositStats.MaskOf(t, DepositKind.Granite);
            var r = new Seed
            {
                Id = seed,
                Ore = DepositStats.Of(t, ObjectId.Ore),
                Rocks = DepositStats.Of(t, ObjectId.Rock, rockMask),
                Granite = DepositStats.Of(t, ObjectId.Granite, graniteMask),
                Patches = t.Deposits.ToList(),
                Reach = ConnectStage.ReachableShare(t),
                Stamped = t.CopperStamp.HasValue,
            };

            int[] label = ConnectStage.Label(t, out _);
            int main = label[((W / 2) * W) + (W / 2)];
            long r600 = (long)HearthStage.CopperWithin * HearthStage.CopperWithin;
            for (int i = 0; i < t.Objects.Length; i++)
            {
                ushort o = t.Objects[i];
                bool marked = marks[i] != 0 || t.Shore[i] != WaterStage.ShoreFar;
                if (o == ObjectId.Ore)
                {
                    r.OreOnMarksOrShore += marked && !r.Stamped ? 1 : 0;
                    int x = i % W;
                    int y = i / W;
                    long dx = IntMath.Wrap(x - (W / 2), W);
                    long dy = IntMath.Wrap(y - (W / 2), W);
                    if ((dx * dx) + (dy * dy) <= r600 && !r.OreReachableIn600)
                    {
                        r.OreReachableIn600 = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }
                            .Any(d => label[(((y + d.Item2) & (W - 1)) * W) + ((x + d.Item1) & (W - 1))] == main);
                    }
                }
                else if (o == ObjectId.Rock && rockMask[i])
                {
                    // в круге россыпи лежат и валуны прежних этапов (гряды пещер подходят к воде на 3 тайла) — для них
                    // правило прежнее: не на тропе и не на песке берега
                    r.RocksOnMarksOrShore += marks[i] != 0 || t.Floor[i] == FloorId.Sand ? 1 : 0;
                }
                else if (o == ObjectId.Granite && graniteMask[i])
                {
                    byte b = TileRules.For(t.Biome[i]).Biome;
                    r.GraniteOffRocky += b == Biome.Mountain || b == Biome.Cave ? 0 : 1;
                }
            }

            return r;
        }

        // ---------- Генератор: 30 зёрен ----------

        /// <summary>
        /// Медь — пятнами: на мир 1024² не меньше 10 пятен (связный кусок ≥ 12 залежей), пятно — от 12 до 500 тайлов и не
        /// шире 48 тайлов (два слившихся соседа), а не жила через полмира; вне пятен — не больше 12 % залежей (крошки у края).
        /// </summary>
        [Fact]
        public void E26_30Seeds_Copper_Patches_CountAndSize()
        {
            foreach (Seed s in All.Value)
            {
                _out.WriteLine($"зерно {s.Id}: пятен меди {s.Ore.Patches}, залежей {s.Ore.Tiles}, пятно {s.Ore.Smallest}–{s.Ore.Largest} тайлов, " +
                               $"размах ≤ {s.Ore.WidestSpan}, вне пятен {s.Ore.Loose}, в 600 от Очага {s.Ore.Within600}{(s.Stamped ? " (штамп)" : "")}");
            }

            Assert.All(All.Value, s =>
            {
                Assert.True(s.Ore.Patches >= 10, $"зерно {s.Id}: пятен меди {s.Ore.Patches}");
                Assert.InRange(s.Ore.Smallest, DepositStats.PatchMinTiles, 500);
                Assert.InRange(s.Ore.Largest, 40, 500);
                Assert.InRange(s.Ore.WidestSpan, 8, 48);
                Assert.True(s.Ore.Loose * 100 <= s.Ore.Tiles * 12, $"зерно {s.Id}: вне пятен {s.Ore.Loose} из {s.Ore.Tiles}");
            });
        }

        /// <summary>Пятно по данным генератора: радиус 4–9 тайлов, объектов — не больше площади круга с дрожанием края.</summary>
        [Fact]
        public void E26_30Seeds_Patches_Radius4to9_AllKindsPresent()
        {
            foreach (Seed s in All.Value)
            {
                Assert.All(s.Patches, p =>
                {
                    Assert.InRange(p.Radius, DepositStage.RadiusMin, DepositStage.RadiusMax);
                    int r = p.Radius + DepositStage.EdgeJagTiles;
                    Assert.InRange(p.Tiles, 1, (r * r * 355 / 113) + 1);
                });
                int copper = s.Patches.Count(p => p.Kind == DepositKind.Copper);
                int rocks = s.Patches.Count(p => p.Kind == DepositKind.Rocks);
                int granite = s.Patches.Count(p => p.Kind == DepositKind.Granite);
                _out.WriteLine($"зерно {s.Id}: центров — медь {copper}, россыпи {rocks}, гранит {granite}");
                Assert.True(copper >= 10, $"зерно {s.Id}: центров меди {copper}");
                Assert.True(rocks >= 10, $"зерно {s.Id}: россыпей {rocks}");
                Assert.True(granite >= 3, $"зерно {s.Id}: ядер гранита {granite}");
            }
        }

        /// <summary>
        /// Залежи — именно пятна, а не шум: не меньше 70 % тайлов залежи имеют среди 8 соседей ≥ 2 таких же. Медь — по всему
        /// миру; валуны и гранит — внутри кругов своих пятен (вне них остались одиночные валуны и кучки гор).
        /// </summary>
        [Fact]
        public void E26_30Seeds_Deposits_AreClustered_70Percent()
        {
            foreach (Seed s in All.Value)
            {
                _out.WriteLine($"зерно {s.Id}: с ≥ 2 соседями — медь {s.Ore.ClusteredPercent:F1} %, россыпи {s.Rocks.ClusteredPercent:F1} % " +
                               $"({s.Rocks.Tiles} валунов), гранит {s.Granite.ClusteredPercent:F1} % ({s.Granite.Tiles})");
            }

            Assert.All(All.Value, s =>
            {
                Assert.True(s.Ore.ClusteredPercent >= 70, $"зерно {s.Id}: медь {s.Ore.ClusteredPercent:F1} %");
                Assert.True(s.Rocks.ClusteredPercent >= 70, $"зерно {s.Id}: россыпи {s.Rocks.ClusteredPercent:F1} %");
                Assert.True(s.Granite.ClusteredPercent >= 70, $"зерно {s.Id}: гранит {s.Granite.ClusteredPercent:F1} %");
            });
        }

        /// <summary>
        /// Пятно меди гарантировано в 600 тайлах от Очага: не меньше <see cref="HearthStage.CopperNeeded"/> залежей, и к
        /// пятну можно подойти без копания (сосед залежи — в области Очага).
        /// </summary>
        [Fact]
        public void E26_30Seeds_CopperPatchWithin600OfStart_Reachable()
        {
            Assert.All(All.Value, s =>
            {
                Assert.True(s.Ore.Within600 >= HearthStage.CopperNeeded, $"зерно {s.Id}: меди в 600 — {s.Ore.Within600}");
                Assert.True(s.OreReachableIn600, $"зерно {s.Id}: к меди в 600 не подойти");
            });
        }

        /// <summary>
        /// Залежей нет в воде, на берегу, на тропе и в проходе обрыва, ближе 24 тайлов к Очагу; гранит пятен — только в горах
        /// и пещерах. Связность мира прежняя: ≥ 90 % проходимых тайлов достижимы от Очага без копания.
        /// </summary>
        [Fact]
        public void E26_30Seeds_Deposits_NotInWater_NotNearStart_WorldStillConnected()
        {
            foreach (Seed s in All.Value)
            {
                _out.WriteLine($"зерно {s.Id}: достижимо {100 * s.Reach:F2} %");
            }

            Assert.All(All.Value, s =>
            {
                Assert.True(s.Ore.OnWater + s.Rocks.OnWater + s.Granite.OnWater == 0, $"зерно {s.Id}: залежей в воде {s.Ore.OnWater}/{s.Rocks.OnWater}/{s.Granite.OnWater}");
                Assert.True(s.Ore.NearStart + s.Rocks.NearStart + s.Granite.NearStart == 0, $"зерно {s.Id}: залежи у Очага");
                Assert.True(s.OreOnMarksOrShore == 0, $"зерно {s.Id}: меди на берегу или тропе {s.OreOnMarksOrShore}");
                Assert.True(s.RocksOnMarksOrShore == 0, $"зерно {s.Id}: валунов россыпей на берегу или тропе {s.RocksOnMarksOrShore}");
                Assert.True(s.GraniteOffRocky == 0, $"зерно {s.Id}: гранита пятен вне гор {s.GraniteOffRocky}");
                Assert.True(s.Reach >= 0.90, $"зерно {s.Id}: достижимо {100 * s.Reach:F2} %");
            });
        }

        /// <summary>
        /// Дальше от Очага медь крупнее: радиус ближних пятен (до <see cref="DepositStage.FarFrom"/> тайлов) — 4–7, дальних —
        /// 5–9, средний радиус дальних по 30 зёрнам больше, чем ближних. «Чаще» — шанс ячейки (константы генератора).
        /// </summary>
        [Fact]
        public void E26_30Seeds_Copper_FartherIsBiggerAndMoreFrequent()
        {
            var near = new List<int>();
            var far = new List<int>();
            foreach (Seed s in All.Value)
            {
                foreach (DepositPatch p in s.Patches.Where(p => p.Kind == DepositKind.Copper && !p.Stamped))
                {
                    long dx = IntMath.Wrap(p.X - (W / 2), W);
                    long dy = IntMath.Wrap(p.Y - (W / 2), W);
                    bool isFar = IntMath.Isqrt((dx * dx) + (dy * dy)) >= DepositStage.FarFrom;
                    (isFar ? far : near).Add(p.Radius);
                    Assert.InRange(p.Radius, isFar ? DepositStage.RadiusFarMin : DepositStage.RadiusMin, isFar ? DepositStage.RadiusMax : DepositStage.RadiusNearMax);
                }
            }

            _out.WriteLine($"ближних пятен {near.Count}, средний радиус {near.Average():F2}; дальних {far.Count}, средний радиус {far.Average():F2}");
            Assert.True(near.Count >= 100 && far.Count >= 100);
            Assert.True(far.Average() > near.Average() + 1);
            Assert.True(DepositStage.CopperRockyFar > DepositStage.CopperRockyNear);
            Assert.True(DepositStage.CopperFoothillFar > DepositStage.CopperFoothillNear);
        }

        /// <summary>Детерминизм: два прогона одного зерна — те же тайлы и те же пятна; другое зерно — другие пятна.</summary>
        [Fact]
        public void E26_SameSeedTwice_SameDeposits()
        {
            TileWorld a = GeneratorV1.BuildTiles(MapShape.World(512), 77);
            TileWorld b = GeneratorV1.BuildTiles(MapShape.World(512), 77);
            TileWorld c = GeneratorV1.BuildTiles(MapShape.World(512), 78);
            Assert.Equal(a.Objects, b.Objects);
            Assert.Equal(a.Floor, b.Floor);
            Assert.Equal(a.Deposits.Select(p => (p.X, p.Y, p.Radius, p.Kind, p.Tiles)), b.Deposits.Select(p => (p.X, p.Y, p.Radius, p.Kind, p.Tiles)));
            Assert.NotEmpty(a.Deposits);
            Assert.NotEqual(a.Deposits.Select(p => (p.X, p.Y)), c.Deposits.Select(p => (p.X, p.Y)));
        }

        // ---------- Ярусы инструментов: что чем копается ----------

        private static WorldModel Meadow(int size = 64)
        {
            var world = new WorldModel(MapShape.World(size), 1);
            world.CreateAllChunks();
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    world.SetBaseTile(new TileCoord(x, y), FloorId.Grass, ObjectId.Empty);
                }
            }

            return world;
        }

        private static EditReply Dig(ushort obj, byte item)
        {
            WorldModel world = Meadow();
            world.SetBaseTile(new TileCoord(11, 10), FloorId.Grass, obj);
            // вплотную к тайлу: досягаемость у предметов разная, а проверяется инструмент и ярус
            return EditRules.CheckDig(world, new WorldPos((11 * U) - 8, (10 * U) + (U / 2)), new TileCoord(11, 10), new DigTimes(), item);
        }

        /// <summary>
        /// Условие «готово» шага 2.6: без медной кирки гранит не берётся. Руками, каменной киркой, топором, молотом, мечом,
        /// факелом — отказ с причиной («нужна кирка: медная» у кирки яруса 1, «нужна кирка» у остального); медной — принято.
        /// </summary>
        [Fact]
        public void E26_Granite_OnlyCopperPickaxe()
        {
            var times = new DigTimes();
            Assert.Equal(EditReply.TierRequired, Dig(ObjectId.Granite, Items.StonePickaxe));
            Assert.Equal("нужна кирка: медная", EditRules.Describe(EditReply.TierRequired, ObjectId.Granite, times));
            Assert.Equal(EditReply.Accepted, Dig(ObjectId.Granite, Items.CopperPickaxe));
            for (int item = 0; item < Items.Count; item++)
            {
                if (item == Items.CopperPickaxe || item == Items.StonePickaxe)
                {
                    continue;
                }

                EditReply reply = Dig(ObjectId.Granite, (byte)item);
                Assert.Equal(EditReply.ToolRequired, reply);
                Assert.Equal("нужна кирка", EditRules.Describe(reply, ObjectId.Granite, times));
            }

            Assert.Equal(Items.TierCopper, Items.TierOf(Items.CopperPickaxe));
            Assert.Equal(ToolKind.Pick, Items.DigToolOf(Items.CopperPickaxe));
            Assert.True(Items.Exists(Items.CopperPickaxe));
        }

        /// <summary>
        /// Таблица «что чем копается» (`06` 2.4, `01` 5): валун и медная залежь — кирка ≥ 1 (каменная берёт, медная вдвое
        /// быстрее, руки и топор — «нужна кирка»); дерево и пень — топор ≥ 1 (кирка — «нужен топор»); куст — топор или руки;
        /// обрыв — ничем.
        /// </summary>
        [Fact]
        public void E26_DigTable_WhatDigsWhat()
        {
            foreach (ushort obj in new[] { ObjectId.Rock, ObjectId.Ore })
            {
                Assert.Equal(EditReply.Accepted, Dig(obj, Items.StonePickaxe));
                Assert.Equal(EditReply.Accepted, Dig(obj, Items.CopperPickaxe));
                Assert.Equal(EditReply.ToolRequired, Dig(obj, Items.None));
                Assert.Equal(EditReply.ToolRequired, Dig(obj, Items.StoneAxe));
                Assert.Equal(EditReply.ToolRequired, Dig(obj, Items.Hammer));
            }

            foreach (ushort obj in new[] { ObjectId.Tree, ObjectId.Stump })
            {
                Assert.Equal(EditReply.Accepted, Dig(obj, Items.StoneAxe));
                Assert.Equal(EditReply.ToolRequired, Dig(obj, Items.StonePickaxe));
                Assert.Equal(EditReply.ToolRequired, Dig(obj, Items.CopperPickaxe));
                Assert.Equal(EditReply.ToolRequired, Dig(obj, Items.None));
            }

            Assert.Equal(EditReply.Accepted, Dig(ObjectId.Bush, Items.StoneAxe));
            Assert.Equal(EditReply.Accepted, Dig(ObjectId.Bush, Items.None));
            Assert.Equal(EditReply.ToolRequired, Dig(ObjectId.Bush, Items.StonePickaxe));

            for (int item = 0; item < Items.Count; item++)
            {
                Assert.Equal(EditReply.NotAllowed, Dig(ObjectId.Cliff, (byte)item));
            }

            var t = new DigTimes();
            Assert.Equal((ToolKind.Pick, (byte)1), (t.ToolKindFor(ObjectId.Rock), t.RequiredTierOf(ObjectId.Rock, ToolKind.Pick)));
            Assert.Equal((ToolKind.Pick, (byte)1), (t.ToolKindFor(ObjectId.Ore), t.RequiredTierOf(ObjectId.Ore, ToolKind.Pick)));
            Assert.Equal((ToolKind.Pick, (byte)2), (t.ToolKindFor(ObjectId.Granite), t.RequiredTierOf(ObjectId.Granite, ToolKind.Pick)));
            Assert.Equal((ToolKind.Axe, (byte)1), (t.ToolKindFor(ObjectId.Tree), t.RequiredTierOf(ObjectId.Tree, ToolKind.Axe)));
            Assert.Equal(ToolKind.Hands, t.ToolKindFor(ObjectId.Cliff));
        }

        /// <summary>
        /// Время копания = база × множитель объекта ÷ 2^min(2, ярус − нужный), к ближайшему кадру, не меньше одного
        /// (правило Автора «время = база × модификатор, 0 = неразрушим»; `06` 2.3). Проверяется по всем природным объектам,
        /// видам инструмента и ярусам 0…5 — формулой, а не списком чисел; смена базы масштабирует всё, база 0 — «нельзя».
        /// </summary>
        [Theory]
        [InlineData(DigTimes.DefaultBaseFrames)]
        [InlineData(60)]
        public void E26_DigFrames_BaseTimesModifier_HalvedPerTier(int baseFrames)
        {
            var t = new DigTimes { BaseFrames = baseFrames };
            int rows = 0;
            foreach (ushort obj in new[] { ObjectId.Rock, ObjectId.Granite, ObjectId.Ore, ObjectId.Tree, ObjectId.Stump, ObjectId.Bush, ObjectId.Cliff })
            {
                for (int tool = 1; tool < (int)ToolKind.Count; tool++)
                {
                    byte req = t.RequiredTierOf(obj, (ToolKind)tool);
                    int mod = t.ModifierOf(obj, (ToolKind)tool);
                    for (byte tier = 0; tier <= 5; tier++)
                    {
                        int frames = t.FramesFor(obj, (ToolKind)tool, tier);
                        if (req == 0)
                        {
                            Assert.True(frames == DigTimes.Cannot || frames == DigTimes.NeedTool);
                        }
                        else if (tier < req)
                        {
                            Assert.Equal(DigTimes.NeedTier, frames);
                        }
                        else
                        {
                            int div = 100 << Math.Min(2, tier - req);
                            Assert.Equal(Math.Max(1, ((baseFrames * mod) + (div / 2)) / div), frames);
                            rows++;
                        }
                    }
                }
            }

            Assert.True(rows >= 25);
            // Отношения времён по `01` 5: валун 1 : залежь 2 : гранит 1,5 (своим ярусом).
            Assert.Equal(2 * t.FramesFor(ObjectId.Rock, ToolKind.Pick, 1), t.FramesFor(ObjectId.Ore, ToolKind.Pick, 1));
            Assert.Equal(t.FramesFor(ObjectId.Rock, ToolKind.Pick, 1), t.FramesFor(ObjectId.Ore, ToolKind.Pick, 2));
            Assert.Equal(DigTimes.Cannot, t.FramesFor(ObjectId.Cliff, ToolKind.Pick, 5));

            t.BaseFrames = 0;
            Assert.Equal(DigTimes.Cannot, t.FramesFor(ObjectId.Rock, ToolKind.Pick, 2));
        }

        /// <summary>
        /// Таблица добычи по `06` 1.4 (набор среза): дерево — 3 древесины, валун и гранит — 1–2 камня, медная залежь — 2–3
        /// руды, куст — 1 волокно, пень — 1 древесина; обрыв не даёт ничего. Бросок детерминирован по тайлу и зерну.
        /// </summary>
        [Fact]
        public void E26_DigLoot_TableMatchesDesign()
        {
            (ushort Obj, Material M, int Min, int Max)[] rows =
            {
                (ObjectId.Tree, Material.Wood, 3, 3),
                (ObjectId.Stump, Material.Wood, 1, 1),
                (ObjectId.Rock, Material.Stone, 4, 8), // C-028: ×4 (BRIEF 11n) [док] к `06` 1.4
                (ObjectId.Granite, Material.Stone, 4, 8),
                (ObjectId.Ore, Material.Copper, 2, 4), // C-028: 2…4
                (ObjectId.Bush, Material.Fiber, 1, 1),
            };
            foreach (var row in rows)
            {
                Assert.True(DigLoot.TryGetDrop(row.Obj, out Material m, out int min, out int max));
                Assert.Equal((row.M, row.Min, row.Max), (m, min, max));
                var seen = new HashSet<int>();
                for (int x = 0; x < 200; x++)
                {
                    int n = DigLoot.DropFor(row.Obj, x, 7, 12345, out Material got);
                    Assert.Equal(row.M, got);
                    Assert.InRange(n, row.Min, row.Max);
                    Assert.Equal(n, DigLoot.DropFor(row.Obj, x, 7, 12345, out _));
                    seen.Add(n);
                }

                Assert.Equal(row.Max - row.Min + 1, seen.Count);
            }

            Assert.False(DigLoot.TryGetDrop(ObjectId.Cliff, out _, out _, out _));
            Assert.Equal(0, DigLoot.DropFor(ObjectId.Cliff, 1, 1, 1, out _));
        }
    }
}
