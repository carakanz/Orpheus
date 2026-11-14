using System;
using System.Collections.Generic;
using System.Linq;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Save;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Карточка E1-2 (план 1.2 и 1.3): вода, обрывы-границы, лес массивами, виды земли. Числа приёмки — на 30 зёрнах 1024²
    /// (один общий замер на класс: зёрна считаются параллельно, как в <see cref="GenV1Tests"/>); правила проходимости и
    /// копания — на маленьких мирах; сохранение — на мире генератора.
    /// </summary>
    public class ReliefTests
    {
        private const int W = 1024;
        private const int U = Torus.SubTilesPerTile;
        private readonly ITestOutputHelper _out;

        public ReliefTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private sealed class Seed
        {
            public int Id;
            public TileWorldStats S = null!;
            public int ToForest;
            public int ToMountain;
            public int ToWater;
            public int LongestRun;
            public double RunsOver60;
            public int Runs;
        }

        private static readonly Lazy<Seed[]> All = new Lazy<Seed[]>(
            () => Enumerable.Range(1, 30).AsParallel().Select(Measure).OrderBy(s => s.Id).ToArray());

        private static Seed Measure(int seed)
        {
            TileWorld t = GeneratorV1.BuildTiles(MapShape.World(W), (ulong)seed);
            var r = new Seed { Id = seed, S = TileWorldStats.Of(t) };
            Walk(t, r);
            CliffRuns(t, r);
            return r;
        }

        // Путь пешком от Очага (4 стороны, без копания): шагов до ближайшего тайла леса, гор и до берега (сосед воды).
        private static void Walk(TileWorld t, Seed r)
        {
            int n = W * W;
            var dist = new int[n];
            Array.Fill(dist, -1);
            var queue = new int[n];
            int qt = 0;
            int start = (W / 2 * W) + (W / 2);
            dist[start] = 0;
            queue[qt++] = start;
            r.ToForest = r.ToMountain = r.ToWater = int.MaxValue;
            for (int qh = 0; qh < qt; qh++)
            {
                int u = queue[qh];
                int d = dist[u];
                if (t.Biome[u] == Biome.Forest)
                {
                    r.ToForest = Math.Min(r.ToForest, d);
                }
                else if (t.Biome[u] == Biome.Mountain)
                {
                    r.ToMountain = Math.Min(r.ToMountain, d);
                }

                if (t.Shore[u] == 1)
                {
                    r.ToWater = Math.Min(r.ToWater, d);
                }

                int ux = u % W;
                int uy = u / W;
                for (int k = 0; k < 4; k++)
                {
                    int vx = (ux + (k == 0 ? 1 : k == 1 ? -1 : 0)) & (W - 1);
                    int vy = (uy + (k == 2 ? 1 : k == 3 ? -1 : 0)) & (W - 1);
                    int v = (vy * W) + vx;
                    if (dist[v] < 0 && ConnectStage.Passable(t.Floor[v], t.Objects[v]))
                    {
                        dist[v] = d + 1;
                        queue[qt++] = v;
                    }
                }
            }
        }

        // Сплошные куски обрыва (8 соседей): самый длинный и доля кусков длиннее 60 тайлов.
        private static void CliffRuns(TileWorld t, Seed r)
        {
            int n = W * W;
            var seen = new bool[n];
            var stack = new Stack<int>();
            int over = 0;
            for (int s = 0; s < n; s++)
            {
                if (t.Objects[s] != ObjectId.Cliff || seen[s])
                {
                    continue;
                }

                int size = 0;
                seen[s] = true;
                stack.Push(s);
                while (stack.Count > 0)
                {
                    int u = stack.Pop();
                    size++;
                    for (int k = 0; k < 9; k++)
                    {
                        int v = ((((u / W) + (k / 3) - 1) & (W - 1)) * W) + (((u % W) + (k % 3) - 1) & (W - 1));
                        if (!seen[v] && t.Objects[v] == ObjectId.Cliff)
                        {
                            seen[v] = true;
                            stack.Push(v);
                        }
                    }
                }

                r.Runs++;
                r.LongestRun = Math.Max(r.LongestRun, size);
                over += size > 60 ? 1 : 0;
            }

            r.RunsOver60 = r.Runs == 0 ? 0 : (double)over / r.Runs;
        }

        // ---------- 30 зёрен ----------

        /// <summary>Вода 5–12 % площади; в 150 тайлах от Очага нет ни воды, ни обрывов.</summary>
        [Fact]
        public void E12_30Seeds_Water5to12Percent_NoneNearStart()
        {
            foreach (Seed s in All.Value)
            {
                _out.WriteLine($"зерно {s.Id}: вода {s.S.WaterPercent:F2} %, песок {100.0 * s.S.Sand / s.S.Tiles:F2} %, у старта воды {s.S.WaterNearStart}, обрывов {s.S.CliffNearStart}");
            }

            Assert.All(All.Value, s => Assert.InRange(s.S.WaterPercent, 5.0, 12.0));
            Assert.All(All.Value, s => Assert.True(s.S.Sand > 0, $"зерно {s.Id}: берега нет"));
            Assert.All(All.Value, s => Assert.Equal(0, s.S.WaterNearStart));
            Assert.All(All.Value, s => Assert.Equal(0, s.S.CliffNearStart));
        }

        /// <summary>
        /// Обрывы — 1,5–5 % тайлов, и это линии: у ≥ 80 % тайлов обрыва не больше 4 соседей-обрывов из 8. Проходы: сплошной
        /// кусок линии длиннее 60 тайлов — редкость (≤ 10 % кусков; длиннее бывает только на развилках линий), а длиннее 160 нет вовсе.
        /// </summary>
        [Fact]
        public void E12_30Seeds_Cliffs_ThinLines_WithGaps()
        {
            foreach (Seed s in All.Value)
            {
                _out.WriteLine($"зерно {s.Id}: обрывы {s.S.CliffPercent:F2} %, тонких {s.S.CliffThinPercent:F1} %, кусков {s.Runs}, " +
                               $"самый длинный {s.LongestRun}, длиннее 60 — {100 * s.RunsOver60:F1} %");
            }

            Assert.All(All.Value, s => Assert.InRange(s.S.CliffPercent, 1.5, 5.0));
            Assert.All(All.Value, s => Assert.True(s.S.CliffThinPercent >= 80.0, $"зерно {s.Id}: тонких {s.S.CliffThinPercent:F1} %"));
            Assert.All(All.Value, s => Assert.True(s.RunsOver60 <= 0.10, $"зерно {s.Id}: кусков длиннее 60 — {100 * s.RunsOver60:F1} %"));
            Assert.All(All.Value, s => Assert.True(s.LongestRun <= 160, $"зерно {s.Id}: кусок {s.LongestRun} тайлов без прохода"));
        }

        /// <summary>От Очага без копания достижимы ≥ 90 % всех проходимых тайлов мира (вода и обрывы не режут мир на части).</summary>
        [Fact]
        public void E12_30Seeds_Reach90Percent_WithoutDigging()
        {
            foreach (Seed s in All.Value)
            {
                _out.WriteLine($"зерно {s.Id}: достижимо {100 * s.S.Reach:F2} %");
            }

            Assert.All(All.Value, s => Assert.True(s.S.Reach >= 0.90, $"зерно {s.Id}: достижимо {100 * s.S.Reach:F2} %"));
        }

        /// <summary>
        /// Лес — 35–55 % тайлов под деревьями, луг — ≤ 3 %; сухая трава — 10–20 % луга; валуны гор — 5–12 % (плиты нет),
        /// скальная масса пещер — ≤ 25 %.
        /// </summary>
        [Fact]
        public void E12_30Seeds_Forest35to55_MeadowUnder3_NoRockSlab()
        {
            foreach (Seed s in All.Value)
            {
                _out.WriteLine($"зерно {s.Id}: лес {s.S.ForestTreePercent:F1} %, луг {s.S.MeadowTreePercent:F2} %, сухая трава {s.S.MeadowDryPercent:F1} %, " +
                               $"валуны гор {s.S.MountainBoulderPercent:F1} %, масса пещер {s.S.CaveMassPercent:F1} %");
            }

            Assert.All(All.Value, s => Assert.InRange(s.S.ForestTreePercent, 35.0, 55.0));
            Assert.All(All.Value, s => Assert.InRange(s.S.MeadowTreePercent, 0.3, 3.0));
            Assert.All(All.Value, s => Assert.InRange(s.S.MeadowDryPercent, 10.0, 20.0));
            Assert.All(All.Value, s => Assert.InRange(s.S.MountainBoulderPercent, 5.0, 12.0));
            Assert.All(All.Value, s => Assert.True(s.S.CaveMassPercent <= 25.0, $"зерно {s.Id}: масса пещер {s.S.CaveMassPercent:F1} %"));
        }

        /// <summary>До леса, гор и берега воды от Очага — не больше 1080 тайлов пути пешком без копания (3 мин бега); луг — под ногами.</summary>
        [Fact]
        public void E12_30Seeds_EveryRegionWithin1080TilesOfStart()
        {
            foreach (Seed s in All.Value)
            {
                _out.WriteLine($"зерно {s.Id}: до леса {s.ToForest}, до гор {s.ToMountain}, до воды {s.ToWater}");
            }

            Assert.All(All.Value, s => Assert.InRange(s.ToForest, 1, 1080));
            Assert.All(All.Value, s => Assert.InRange(s.ToMountain, 1, 1080));
            Assert.All(All.Value, s => Assert.InRange(s.ToWater, 1, 1080));
        }

        // ---------- Детерминизм и рельеф ----------

        /// <summary>Два прогона одного зерна — те же тайлы; другое зерно — другие. Уровень высоты — 0…3, одинаков при повторе.</summary>
        [Fact]
        public void E12_SameSeedTwice_SameTiles_LevelsStable()
        {
            TileWorld a = GeneratorV1.BuildTiles(MapShape.World(W), 0xE50007);
            TileWorld b = GeneratorV1.BuildTiles(MapShape.World(W), 0xE50007);
            Assert.Equal(a.Floor, b.Floor);
            Assert.Equal(a.Objects, b.Objects);
            TileWorld c = GeneratorV1.BuildTiles(MapShape.World(W), 0xE50008);
            Assert.NotEqual(a.Floor, c.Floor);

            ReliefMap relief = ReliefMap.Create(W, 0xE50007);
            var levels = new int[ReliefMap.Levels + 1];
            for (int y = 0; y < W; y += 3)
            {
                for (int x = 0; x < W; x += 3)
                {
                    int level = relief.LevelAt(x, y);
                    Assert.InRange(level, 0, ReliefMap.Levels);
                    Assert.Equal(a.Relief.LevelAt(x, y), level);
                    Assert.Equal(level, relief.LevelAt(x + W, y - W)); // тор
                    levels[level]++;
                }
            }

            _out.WriteLine("тайлов по уровням: " + string.Join(", ", levels));
            Assert.All(levels, n => Assert.True(n > W * W / 9 / 20, "уровень почти пуст"));
        }

        /// <summary>Обрыв стоит только там, где рядом (в своей ячейке 4 × 4 и вокруг) рельеф меняет уровень; в воде и на берегу обрывов нет.</summary>
        [Fact]
        public void E12_Cliffs_OnLevelBorders_NotInWater()
        {
            TileWorld t = GeneratorV1.BuildTiles(MapShape.World(W), 2);
            int cliffs = 0;
            for (int y = 0; y < W; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    if (t.Objects[(y * W) + x] != ObjectId.Cliff)
                    {
                        continue;
                    }

                    cliffs++;
                    Assert.NotEqual(FloorId.Water, t.Floor[(y * W) + x]);
                    Assert.NotEqual(FloorId.Sand, t.Floor[(y * W) + x]);
                    int i = x >> 2;
                    int j = y >> 2;
                    int a = t.Relief.NodeLevel(i, j);
                    bool border = t.Relief.NodeLevel(i + 1, j) != a || t.Relief.NodeLevel(i, j + 1) != a || t.Relief.NodeLevel(i + 1, j + 1) != a;
                    Assert.True(border, $"обрыв ({x}, {y}) не на границе уровней");
                }
            }

            Assert.True(cliffs > 1000, $"обрывов {cliffs}");
        }

        // ---------- Проходимость и копание ----------

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

        private static WorldPos Center(int x, int y) => new WorldPos((x * U) + (U / 2), (y * U) + (U / 2));

        /// <summary>Игрок бежит на восток в стену обрыва и в воду — упирается: за 10 с не пересёк столбец преграды.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void E12_Player_StopsAtCliffAndWater(bool cliff)
        {
            WorldModel world = Meadow();
            for (int y = 0; y < 64; y++)
            {
                world.SetBaseTile(new TileCoord(20, y), cliff ? FloorId.Grass : FloorId.Water, cliff ? ObjectId.Cliff : ObjectId.Empty);
            }

            var sim = new Simulation(world);
            int id = sim.AddEntity(Center(15, 10));
            Assert.True(sim.IsBlocked(world.Shape, new TileCoord(20, 10)));
            for (int tick = 0; tick < 300; tick++)
            {
                sim.SetInputAt(sim.SlotOf(id), new InputFrame(Input8.East));
                sim.Step();
            }

            WorldPos at = sim.Entities[sim.SlotOf(id)].Mover.Position;
            _out.WriteLine($"{(cliff ? "обрыв" : "вода")}: игрок остановился на x = {at.X / (double)U:F2}");
            Assert.True(at.X > 18 * U, "игрок не дошёл до преграды");
            Assert.True(at.X < 20 * U, $"игрок прошёл сквозь преграду: x = {at.X / (double)U:F2}");

            // Настил над водой — проходим (правило `05` 1.3 не менялось).
            if (!cliff)
            {
                world.SetBaseTile(new TileCoord(20, 10), FloorId.Platform, ObjectId.Empty);
                Assert.False(sim.IsBlocked(world.Shape, new TileCoord(20, 10)));
            }
        }

        /// <summary>
        /// Путь пешего врага обходит отрезок обрыва и озеро (ни одного шага по преграде, путь длиннее прямого); сплошная линия
        /// поперёк мира — пути нет (частичный, до преграды). Летун над водой и обрывом летит (природное ему не мешает).
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void E12_EnemyPath_GoesAroundCliffAndWater(bool cliff)
        {
            WorldModel world = Meadow();
            byte floor = cliff ? FloorId.Grass : FloorId.Water;
            ushort obj = cliff ? ObjectId.Cliff : ObjectId.Empty;
            for (int y = 5; y <= 15; y++)
            {
                world.SetBaseTile(new TileCoord(20, y), floor, obj);
            }

            var sim = new Simulation(world);
            var terrain = new SimulationPathTerrain(sim);
            var finder = new PathFinder(enclosureFillLimit: 0);
            var path = new PathBuffer();
            Assert.Equal(PathStatus.Found, finder.Find(terrain, new TileCoord(15, 10), new TileCoord(25, 10), PathMode.Walker, path));
            Assert.True(path.Length > 10, $"путь {path.Length} — прямой сквозь преграду");
            foreach (TileCoord step in path.Steps.AsSpan(0, path.Length))
            {
                Assert.False(sim.IsBlocked(world.Shape, step), $"шаг по преграде {step}");
                Assert.False(step.X == 20 && step.Y >= 5 && step.Y <= 15, $"шаг по преграде {step}");
            }

            for (int y = 0; y < 64; y++)
            {
                world.SetBaseTile(new TileCoord(20, y), floor, obj);
            }

            Assert.Equal(PathStatus.Partial, finder.Find(terrain, new TileCoord(15, 10), new TileCoord(25, 10), PathMode.Walker, path));
            Assert.Equal(new TileCoord(19, 10), path.End);
            Assert.Equal(PathStatus.Found, finder.Find(terrain, new TileCoord(15, 10), new TileCoord(25, 10), PathMode.Flyer, path));
        }

        /// <summary>
        /// Обрыв не копается ничем (время копания 0 у любого инструмента любого яруса — «нельзя», а не «нужен инструмент»),
        /// на нём и вместо него не строят; в воду не строят (нужен настил), настил на воду — можно.
        /// </summary>
        [Fact]
        public void E12_Cliff_Undiggable_NoBuildOnCliffOrWater()
        {
            var times = new DigTimes();
            Assert.True(ObjectId.IsSolid(ObjectId.Cliff));
            Assert.True(ObjectId.IsNatural(ObjectId.Cliff));
            Assert.False(ObjectId.IsPassable(ObjectId.Cliff));
            Assert.Equal(0, times.FramesOf(ObjectId.Cliff));
            for (int tool = 0; tool < (int)ToolKind.Count; tool++)
            {
                for (byte tier = 0; tier <= 9; tier++)
                {
                    Assert.Equal(DigTimes.Cannot, times.FramesFor(ObjectId.Cliff, (ToolKind)tool, tier));
                }
            }

            Assert.False(EditRules.IsDiggable(ObjectId.Cliff, times));

            WorldModel world = Meadow();
            world.SetBaseTile(new TileCoord(11, 10), FloorId.Grass, ObjectId.Cliff);
            world.SetBaseTile(new TileCoord(10, 11), FloorId.Water, ObjectId.Empty);
            WorldPos me = Center(10, 10);
            for (int item = 0; item < 64; item++)
            {
                // вплотную к обрыву: досягаемость у предметов разная, а ответ должен быть «нельзя», не «далеко»
                Assert.Equal(EditReply.NotAllowed, EditRules.CheckDig(world, new WorldPos((11 * U) - 8, (10 * U) + (U / 2)), new TileCoord(11, 10), times, (byte)item));
            }

            Assert.Equal(EditReply.DigFirst, EditRules.CheckPlaceTile(world, me, new TileCoord(11, 10), ObjectId.WoodWall));
            Assert.Equal(EditReply.DigFirst, EditRules.CheckPlaceTile(world, me, new TileCoord(11, 10), FloorId.WoodFloor));
            Assert.Equal(EditReply.NeedPlatform, EditRules.CheckPlaceTile(world, me, new TileCoord(10, 11), ObjectId.WoodWall));
            Assert.Equal(EditReply.NeedPlatform, EditRules.CheckPlaceTile(world, me, new TileCoord(10, 11), FloorId.WoodFloor));
            Assert.Equal(EditReply.Accepted, EditRules.CheckPlaceTile(world, me, new TileCoord(10, 11), FloorId.Platform));

            Assert.False(FloorId.IsPassable(FloorId.Water));
            Assert.True(FloorId.IsPassable(FloorId.DryGrass));
            Assert.True(FloorId.IsSolid(FloorId.DryGrass));
            Assert.True(FloorId.IsNatural(FloorId.DryGrass));
        }

        // ---------- Сохранение ----------

        /// <summary>
        /// Мир генератора с водой, обрывами и сухой травой: снимок генерации читается тайл в тайл; правки с новыми тайлами
        /// сохраняются и читаются (хэш состояния тот же, файл побайтно тот же). Версия формата сохранения не менялась.
        /// </summary>
        [Fact]
        public void E12_Save_WorldWithCliffWaterDryGrass_RoundTrip()
        {
            WorldModel world = new GeneratorV1().Generate(MapShape.World(W), 1);
            byte[] baseBytes = WorldSnapshot.WriteBase(world);
            WorldModel read = WorldSnapshot.LoadBase(baseBytes);
            int water = 0;
            int cliff = 0;
            int dry = 0;
            for (int y = 0; y < W; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    var t = new TileCoord(x, y);
                    byte floor = world.GetFloor(t);
                    ushort obj = world.GetObject(t);
                    Assert.Equal(floor, read.GetFloor(t));
                    Assert.Equal(obj, read.GetObject(t));
                    water += floor == FloorId.Water ? 1 : 0;
                    dry += floor == FloorId.DryGrass ? 1 : 0;
                    cliff += obj == ObjectId.Cliff ? 1 : 0;
                }
            }

            _out.WriteLine($"в снимке генерации: воды {water}, сухой травы {dry}, обрывов {cliff}; {baseBytes.Length} байт");
            Assert.True(water > 50000 && dry > 30000 && cliff > 10000);

            var sim = new Simulation(world);
            sim.RequestEdit(new TileCoord(500, 500), FloorId.DryGrass, ObjectId.Cliff);
            sim.RequestEdit(new TileCoord(501, 500), FloorId.Water, ObjectId.Empty);
            sim.RequestEdit(new TileCoord(40, 900), FloorId.Sand, ObjectId.Cliff);
            sim.Step();
            byte[] sha = WorldSnapshot.StoredHash(baseBytes);
            byte[] save = WorldSave.Build(WorldSave.Capture(sim, sha, "E1-2"), 7, 1_762_400_000_000);
            Simulation loaded = SaveFixtures.Load(baseBytes, save);
            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total);
            Assert.Equal(FloorId.DryGrass, loaded.World.GetFloor(new TileCoord(500, 500)));
            Assert.Equal(ObjectId.Cliff, loaded.World.GetObject(new TileCoord(500, 500)));
            Assert.Equal(FloorId.Water, loaded.World.GetFloor(new TileCoord(501, 500)));
            Assert.Equal(ObjectId.Cliff, loaded.World.GetObject(new TileCoord(40, 900)));
            Assert.Equal(save, WorldSave.Build(WorldSave.Capture(loaded, sha, "E1-2"), 7, 1_762_400_000_000));
        }
    }
}
