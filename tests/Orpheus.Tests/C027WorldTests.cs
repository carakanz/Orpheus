using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-027 (приёмка 2.8, BRIEF 11n; генератор v9): глубокая вода непроходима, отмель у берега — проходима; троп (полос земли)
    /// нет; леса трёх видов кластерами со своим полом; 2–3 выхода меди в 150 от Очага на всех зёрнах 1…30.
    /// </summary>
    public class C027WorldTests
    {
        private const int W = 1024;
        private const int U = Torus.SubTilesPerTile;
        private readonly ITestOutputHelper _out;

        public C027WorldTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static readonly Lazy<TileWorld[]> Worlds = new Lazy<TileWorld[]>(() =>
        {
            var r = new TileWorld[30];
            Parallel.For(0, 30, i => r[i] = GeneratorV1.BuildTiles(MapShape.World(W), (ulong)(i + 1)));
            return r;
        });

        // ---------- Глубокая вода ----------

        /// <summary>Таблицы ядра: глубина не проходима ни для кого, не опора (строить — настил), отмель — как в C-018.</summary>
        [Fact]
        public void C027_DeepWater_Flags()
        {
            Assert.False(FloorId.IsPassable(FloorId.DeepWater));
            Assert.False(FloorId.IsSolid(FloorId.DeepWater));
            Assert.True(FloorId.IsNatural(FloorId.DeepWater));
            Assert.False(TerrainSlow.WalkFloor(FloorId.DeepWater));
            Assert.True(TerrainSlow.WalkFloor(FloorId.Water));
            Assert.True(FloorId.IsLiquid(FloorId.DeepWater));
            Assert.True(FloorId.IsWater(FloorId.DeepWater) && FloorId.IsWater(FloorId.Water) && !FloorId.IsWater(FloorId.Sand));
            Assert.True(FloorId.IsForest(FloorId.PineFloor) && FloorId.IsForest(FloorId.SpruceFloor) && FloorId.IsForest(FloorId.ForestFloor));
            Assert.True(FloorId.IsPassable(FloorId.PineFloor) && FloorId.IsSolid(FloorId.SpruceFloor));
            Assert.True(FloorId.MaxKnown <= 15); // E2-12: пол записи постройки — 4 бита
        }

        /// <summary>Игрок в глубину не входит (стоит у края), по отмели идёт; путь врагов глубину обходит; строить на глубине нельзя без настила.</summary>
        [Fact]
        public void C027_DeepWater_BlocksPlayerAndPath_ShallowWalks()
        {
            var sim = new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.World(64), 2701));
            for (int y = 0; y < 64; y++)
            {
                for (int x = 20; x <= 23; x++)
                {
                    sim.World.SetBaseTile(new TileCoord(x, y), x == 20 ? FloorId.Water : FloorId.DeepWater, ObjectId.Empty);
                }
            }

            Assert.False(sim.IsBlocked(sim.Shape, new TileCoord(20, 10)));
            Assert.True(sim.IsBlocked(sim.Shape, new TileCoord(21, 10)));

            int id = sim.AddEntity(new WorldPos((17 * U) + (U / 2), (10 * U) + (U / 2)));
            for (int t = 0; t < 150; t++)
            {
                sim.SetInputAt(sim.SlotOf(id), new InputFrame(Input8.East));
                sim.Step();
            }

            int x0 = sim.GetEntity(id).Mover.Position.X;
            _out.WriteLine($"встал на x = {x0 / (double)U:F2} тайла");
            Assert.True(x0 >= 20 * U, "по отмели не прошёл");
            Assert.True(x0 < 21 * U, "вошёл в глубину");

            var terrain = new SimulationPathTerrain(sim);
            terrain.BeginSearch();
            Assert.Equal(PathCost.Blocked, terrain.CostOf(new TileCoord(22, 10), PathMode.Walker));
            Assert.True(terrain.CostOf(new TileCoord(20, 10), PathMode.Walker) != PathCost.Blocked);
            Assert.Equal(EditReply.NeedPlatform, EditRules.CheckPlaceTile(sim.World, new WorldPos((21 * U) + 10, (12 * U) + 10), new TileCoord(21, 12), ObjectId.WoodWall));
        }

        /// <summary>
        /// 30 зёрен: вода у суши (суша в квадрате 5 × 5) — отмель, дальше — глубина; глубина есть в каждом мире (озёра и реки
        /// шире 5 тайлов), но не больше половины воды; у старта воды нет.
        /// </summary>
        [Fact]
        public void C027_30Seeds_DeepWater_FarFromShore_ShallowAtShore()
        {
            foreach (TileWorld t in Worlds.Value)
            {
                long deep = 0, shallow = 0;
                for (int y = 0; y < W; y++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        byte f = t.Floor[(y * W) + x];
                        if (!FloorId.IsWater(f))
                        {
                            continue;
                        }

                        bool land = LandWithin(t, x, y, WaterStage.ShallowReach);
                        if (f == FloorId.DeepWater)
                        {
                            deep++;
                            Assert.False(land, $"зерно {t.Macro.Map.Seed}: глубина ({x}, {y}) у берега");
                        }
                        else
                        {
                            shallow++;
                            Assert.True(land, $"зерно {t.Macro.Map.Seed}: отмель ({x}, {y}) вдали от берега");
                        }
                    }
                }

                Assert.True(deep > 1000, $"зерно {t.Macro.Map.Seed}: глубины {deep}");
                Assert.True(deep < shallow + deep, "вся вода — глубина");
                Assert.Equal(deep, t.DeepTiles);
            }
        }

        private static bool LandWithin(TileWorld t, int x, int y, int r)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (!FloorId.IsWater(t.Floor[(((y + dy) & (W - 1)) * W) + ((x + dx) & (W - 1))]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // ---------- Троп нет ----------

        /// <summary>Земля (<see cref="FloorId.Dirt"/>) — только пятнами в горах: на лугу и в лесу её нет (ни троп, ни пятен).</summary>
        [Fact]
        public void C027_30Seeds_NoDirtTrails_OnMeadowAndForest()
        {
            foreach (TileWorld t in Worlds.Value)
            {
                long dirtOpen = 0, open = 0;
                for (int i = 0; i < t.Floor.Length; i++)
                {
                    byte b = TileRules.For(t.Biome[i]).Biome;
                    if (b != Biome.Meadow && b != Biome.Forest)
                    {
                        continue;
                    }

                    open++;
                    dirtOpen += t.Floor[i] == FloorId.Dirt ? 1 : 0;
                }

                // Карман у штампа залежи или брод на краю гор может дать единицы тайлов; полосы — тысячи.
                Assert.True(dirtOpen * 1000 < open, $"зерно {t.Macro.Map.Seed}: земли на лугу и в лесу {dirtOpen} из {open}");
            }
        }

        // ---------- Виды леса ----------

        /// <summary>
        /// 30 зёрен: в лесу три вида пола (дубрава, бор, ельник), каждый ≥ 12 % подстилки леса; кластерами (≥ 95 % тайлов
        /// подстилки с 4 соседями той же подстилки или не-подстилкой); ельник гуще бора.
        /// </summary>
        [Fact]
        public void C027_30Seeds_ForestKinds_Clustered_OwnDensity()
        {
            foreach (TileWorld t in Worlds.Value)
            {
                var floors = new long[3];
                var trees = new long[3];
                long same = 0, total = 0;
                for (int y = 0; y < W; y++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        int i = (y * W) + x;
                        int k = Kind(t.Floor[i]);
                        if (k < 0)
                        {
                            continue;
                        }

                        floors[k]++;
                        trees[k] += t.Objects[i] == ObjectId.Tree ? 1 : 0;
                        total++;
                        bool ok = true;
                        for (int d = 0; d < 4; d++)
                        {
                            int n = (((y + (d == 2 ? 1 : d == 3 ? -1 : 0)) & (W - 1)) * W) + ((x + (d == 0 ? 1 : d == 1 ? -1 : 0)) & (W - 1));
                            int kn = Kind(t.Floor[n]);
                            ok &= kn < 0 || kn == k;
                        }

                        same += ok ? 1 : 0;
                    }
                }

                string line = $"зерно {t.Macro.Map.Seed}: дубрава {Pct(floors[0], total)}, бор {Pct(floors[1], total)}, ельник {Pct(floors[2], total)}; " +
                    $"деревья {Pct(trees[0], floors[0])} / {Pct(trees[1], floors[1])} / {Pct(trees[2], floors[2])}; кластер {Pct(same, total)}";
                _out.WriteLine(line);
                for (int k = 0; k < 3; k++)
                {
                    Assert.True(floors[k] * 100 >= total * 12, line);
                }

                Assert.True(same * 100 >= total * 95, line);
                Assert.True(trees[2] * floors[1] > trees[1] * floors[2], line); // ельник гуще бора
            }
        }

        private static int Kind(byte floor) =>
            floor == FloorId.ForestFloor ? 0 : floor == FloorId.PineFloor ? 1 : floor == FloorId.SpruceFloor ? 2 : -1;

        private static string Pct(long a, long b) => b == 0 ? "—" : $"{100.0 * a / b:F1} %";

        // ---------- Медь у старта ----------

        /// <summary>Радиус «у старта» из карточки (~150 тайлов от Очага).</summary>
        private const int CopperRadius = 150;

        /// <summary>
        /// 30 зёрен: в 150 тайлах от Очага не меньше 2 выходов меди (штамп ставит 2–3; бывают и свои у гор) (связные куски залежей ≥ 6 тайлов, не ближе 40 друг к другу); у каждого
        /// есть залежь, к которой можно подойти от Очага без копания (сосед — проходимый тайл компоненты Очага); вокруг выхода в
        /// 2 тайлах нет деревьев (видно издали). Медь копается каменной киркой.
        /// </summary>
        [Fact]
        public void C027_30Seeds_Copper_2to3Outcrops_Within150_ReachableVisible()
        {
            Assert.True(new DigTimes().RequiredTierOf(ObjectId.Ore, ToolKind.Pick) <= Items.TierStone); // по рецептам — каменной киркой

            foreach (TileWorld t in Worlds.Value)
            {
                int half = W >> 1;
                bool[] reach = Reach(t);
                var seen = new bool[t.Objects.Length];
                var outcrops = new List<(int X, int Y, int Size, bool Reachable, bool Clear)>();
                for (int y = half - CopperRadius; y <= half + CopperRadius; y++)
                {
                    for (int x = half - CopperRadius; x <= half + CopperRadius; x++)
                    {
                        int i = (y * W) + x;
                        if (seen[i] || t.Objects[i] != ObjectId.Ore || Dist2(x - half, y - half) > CopperRadius * CopperRadius)
                        {
                            continue;
                        }

                        // Кусок залежей (8 сторон).
                        var q = new Queue<int>();
                        q.Enqueue(i);
                        seen[i] = true;
                        int size = 0, sx = 0, sy = 0;
                        bool reachable = false, clear = true;
                        while (q.Count > 0)
                        {
                            int u = q.Dequeue();
                            int ux = u % W, uy = u / W;
                            size++;
                            sx += ux;
                            sy += uy;
                            for (int dy = -2; dy <= 2; dy++)
                            {
                                for (int dx = -2; dx <= 2; dx++)
                                {
                                    int v = (((uy + dy) & (W - 1)) * W) + ((ux + dx) & (W - 1));
                                    clear &= t.Objects[v] != ObjectId.Tree;
                                    bool near = Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1;
                                    if (near && Math.Abs(dx) + Math.Abs(dy) == 1 && reach[v])
                                    {
                                        reachable = true;
                                    }

                                    if (near && !seen[v] && t.Objects[v] == ObjectId.Ore)
                                    {
                                        seen[v] = true;
                                        q.Enqueue(v);
                                    }
                                }
                            }
                        }

                        if (size >= 6)
                        {
                            outcrops.Add((sx / size, sy / size, size, reachable, clear));
                        }
                    }
                }

                string line = $"зерно {t.Macro.Map.Seed}: выходов меди в {CopperRadius} — {outcrops.Count}: " +
                    string.Join(", ", outcrops.Select(o => $"({o.X - half:+0;-0}, {o.Y - half:+0;-0}) {o.Size} т."));
                _out.WriteLine(line);
                // Свои пятна у гор могут стоять в лесу; гарантия — 2 выхода, к которым подойти и которые видно.
                outcrops = outcrops.Where(o => o.Reachable && o.Clear).ToList();
                Assert.True(outcrops.Count >= 2, line + " — видимых и доступных меньше 2");

                for (int a = 0; a < outcrops.Count; a++)
                {
                    for (int b = a + 1; b < outcrops.Count; b++)
                    {
                        Assert.True(Dist2(outcrops[a].X - outcrops[b].X, outcrops[a].Y - outcrops[b].Y) >= 40 * 40, line + " — выходы рядом");
                    }
                }
            }
        }

        private static int Dist2(int dx, int dy) => (dx * dx) + (dy * dy);

        // Проходимые без копания тайлы, связанные с Очагом (4 стороны, вода любой глубины — преграда, как у генератора).
        private static bool[] Reach(TileWorld t)
        {
            var r = new bool[t.Floor.Length];
            var q = new Queue<int>();
            int s = ((W >> 1) * W) + (W >> 1);
            r[s] = true;
            q.Enqueue(s);
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                int ux = u % W, uy = u / W;
                for (int d = 0; d < 4; d++)
                {
                    int v = (((uy + (d == 2 ? 1 : d == 3 ? -1 : 0)) & (W - 1)) * W) + ((ux + (d == 0 ? 1 : d == 1 ? -1 : 0)) & (W - 1));
                    if (!r[v] && ConnectStage.Passable(t.Floor[v], t.Objects[v]))
                    {
                        r[v] = true;
                        q.Enqueue(v);
                    }
                }
            }

            return r;
        }
    }
}
