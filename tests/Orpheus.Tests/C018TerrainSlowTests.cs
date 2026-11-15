using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-018 (слово Автора 15.11: «сделай кусты и воду проходимыми, но замедляют скорость»): вода ×0,45, куст ×0,6 по тайлу под
    /// центром существа (таблица <see cref="TerrainSlow"/>), бег и уворот; путь врагов — стоимость ~1/множитель (обход дешевле — в обход).
    /// </summary>
    public class C018TerrainSlowTests
    {
        private const int U = Torus.SubTilesPerTile;
        private readonly ITestOutputHelper _out;

        public C018TerrainSlowTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static Simulation Flat(int size = 64, ulong seed = 1801) =>
            new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.World(size), seed));

        private static WorldPos Center(int x, int y) => new WorldPos((x * U) + (U / 2), (y * U) + (U / 2));

        private static void Fill(Simulation sim, int x0, int y0, int x1, int y1, byte floor, ushort obj)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    sim.World.SetBaseTile(Torus.Wrap(sim.Shape, new TileCoord(x, y)), floor, obj);
                }
            }
        }

        /// <summary>Таблица — числа карточки (предварительные): вода 115/256, куст 154/256; путь — 4 и 3 половинки (≈ 1/множитель).</summary>
        [Fact]
        public void C018_Table_Numbers_And_GeneratorFlagsUnchanged()
        {
            Assert.Equal(256, TerrainSlow.One);
            Assert.Equal(115, TerrainSlow.WaterQ8);   // 0,45 × 256
            Assert.Equal(154, TerrainSlow.BushQ8);    // 0,6 × 256
            Assert.Equal(115, TerrainSlow.TileQ8(FloorId.Water, ObjectId.Empty));
            Assert.Equal(154, TerrainSlow.TileQ8(FloorId.Grass, ObjectId.Bush));
            Assert.Equal(154, TerrainSlow.TileQ8(FloorId.Grass, ObjectId.MakeAnchor(ObjectId.Bush, 2)));
            Assert.Equal((115 * 154) >> 8, TerrainSlow.TileQ8(FloorId.Water, ObjectId.Bush));
            Assert.Equal(256, TerrainSlow.TileQ8(FloorId.Grass, ObjectId.Empty));
            Assert.Equal(256, TerrainSlow.TileQ8(FloorId.Sand, ObjectId.Torch));
            Assert.Equal(256, TerrainSlow.TileQ8(FloorId.Lava, ObjectId.Empty));
            Assert.Equal(PathCost.Normal, TerrainSlow.PathFactor(256));
            Assert.Equal(4, TerrainSlow.PathFactor(TerrainSlow.WaterQ8));
            Assert.Equal(3, TerrainSlow.PathFactor(TerrainSlow.BushQ8));

            // Флаги таблиц, по которым работает генератор, не менялись: его вывод тот же.
            Assert.False(FloorId.IsPassable(FloorId.Water));
            Assert.False(FloorId.IsSolid(FloorId.Water));
            Assert.True(ObjectId.IsSolid(ObjectId.Bush));
            Assert.True(TerrainSlow.WalkFloor(FloorId.Water));
            Assert.False(TerrainSlow.WalkFloor(FloorId.Lava));
            Assert.True(TerrainSlow.WalkObject(ObjectId.Bush));
            Assert.False(TerrainSlow.WalkObject(ObjectId.Rock));
        }

        /// <summary>Вода и куст не твёрдые (столкновения, путь); лава, обрыв, дерево — по-прежнему.</summary>
        [Fact]
        public void C018_WaterAndBush_NotSolid_OthersStill()
        {
            Simulation sim = Flat();
            sim.World.SetBaseTile(new TileCoord(5, 5), FloorId.Water, ObjectId.Empty);
            sim.World.SetBaseTile(new TileCoord(6, 5), FloorId.Grass, ObjectId.Bush);
            sim.World.SetBaseTile(new TileCoord(7, 5), FloorId.Water, ObjectId.Bush);
            sim.World.SetBaseTile(new TileCoord(8, 5), FloorId.Lava, ObjectId.Empty);
            sim.World.SetBaseTile(new TileCoord(9, 5), FloorId.Grass, ObjectId.Tree);
            sim.World.SetBaseTile(new TileCoord(10, 5), FloorId.Grass, ObjectId.Cliff);
            Assert.False(sim.IsBlocked(sim.Shape, new TileCoord(5, 5)));
            Assert.False(sim.IsBlocked(sim.Shape, new TileCoord(6, 5)));
            Assert.False(sim.IsBlocked(sim.Shape, new TileCoord(7, 5)));
            Assert.Equal(0, sim.SolidCells(sim.Shape, new TileCoord(6, 5)));
            Assert.True(sim.IsBlocked(sim.Shape, new TileCoord(8, 5)));
            Assert.True(sim.IsBlocked(sim.Shape, new TileCoord(9, 5)));
            Assert.True(sim.IsBlocked(sim.Shape, new TileCoord(10, 5)));
            Assert.Equal(115, sim.SpeedQ8(sim.Shape, new TileCoord(5, 5)));
            Assert.Equal(154, sim.SpeedQ8(sim.Shape, new TileCoord(6, 5)));
            Assert.Equal(256, sim.SpeedQ8(sim.Shape, new TileCoord(11, 5)));

            var terrain = new SimulationPathTerrain(sim);
            terrain.BeginSearch();
            Assert.Equal(4, terrain.CostOf(new TileCoord(5, 5), PathMode.Walker));
            Assert.Equal(3, terrain.CostOf(new TileCoord(6, 5), PathMode.Walker));
            Assert.Equal(PathCost.Normal, terrain.CostOf(new TileCoord(11, 5), PathMode.Walker));
            Assert.Equal(PathCost.Blocked, terrain.CostOf(new TileCoord(8, 5), PathMode.Walker));
            Assert.Equal(PathCost.Normal, terrain.CostOf(new TileCoord(5, 5), PathMode.Flyer)); // летуну вода не мешает и не дорога
        }

        /// <summary>Пробег на восток за 60 тактов (такт 30 Гц, враги и боты) и 120 кадров (60 Гц, сетевой персонаж): трава, вода, куст.</summary>
        [Theory]
        [InlineData(FloorId.Grass, ObjectId.Empty, 3072)]  // 60 × 51,2
        [InlineData(FloorId.Water, ObjectId.Empty, 1380)]  // 60 × (512·115 >> 8 = 230) / 10
        [InlineData(FloorId.Grass, ObjectId.Bush, 1848)]   // 60 × (512·154 >> 8 = 308) / 10
        public void C018_RunSpeed_GrassWaterBush(byte floor, ushort obj, int expected)
        {
            Simulation sim = Flat();
            Fill(sim, 0, 0, 63, 20, floor, obj);
            int id = sim.AddEntity(Center(5, 10));
            for (int t = 0; t < 60; t++)
            {
                sim.SetInputAt(sim.SlotOf(id), new InputFrame(Input8.East));
                sim.Step();
            }

            int tick = sim.GetEntity(id).Mover.Position.X - Center(5, 10).X;

            var m = Mover.At(sim.Shape, Center(5, 10));
            for (int f = 0; f < 120; f++)
            {
                m.StepFrame(sim.Shape, Input8.East, sim.Solid);
            }

            int frame = m.Position.X - Center(5, 10).X;
            _out.WriteLine($"пол {floor}, объект {obj}: такт {tick}, кадр {frame}");
            Assert.Equal(expected, tick);
            Assert.Equal(expected, frame);
            Assert.Equal(Center(5, 10).Y, m.Position.Y);
        }

        /// <summary>
        /// Переход границы: замедление начинается в такте, когда центр (ноги) уже в воде, а не когда круг её коснулся; на суше
        /// за водой — снова полный ход. Река шириной 3 тайла пересекается насквозь.
        /// </summary>
        [Fact]
        public void C018_Boundary_SlowsWhenCenterInWater_FullSpeedAfter()
        {
            Simulation sim = Flat();
            Fill(sim, 20, 0, 22, 63, FloorId.Water, ObjectId.Empty);
            int id = sim.AddEntity(Center(15, 10));
            int prev = Center(15, 10).X;
            int fast = 0, slow = 0;
            for (int t = 0; t < 200; t++)
            {
                int tileBefore = prev >> Torus.SubTileBits;
                sim.SetInputAt(sim.SlotOf(id), new InputFrame(Input8.East));
                sim.Step();
                int x = sim.GetEntity(id).Mover.Position.X;
                int d = x - prev;
                bool inWater = tileBefore >= 20 && tileBefore <= 22;
                Assert.True(inWater ? d >= 22 && d <= 24 : d >= 51 && d <= 52, $"такт {t}: x {prev}→{x} (тайл {tileBefore}), шаг {d}");
                if (inWater)
                {
                    slow++;
                }
                else
                {
                    fast++;
                }

                prev = x;
            }

            Assert.True(prev > 26 * U, $"не перешёл реку: x = {prev / (double)U:F2}");
            Assert.True(slow > 30 && fast > 30, $"медленных {slow}, быстрых {fast}");
        }

        /// <summary>Уворот в воде короче тем же множителем (та же длительность рывка), на траве — как был.</summary>
        [Fact]
        public void C018_Dodge_InWater_Shorter()
        {
            Simulation sim = Flat();
            Fill(sim, 30, 0, 63, 63, FloorId.Water, ObjectId.Empty);
            int Dash(WorldPos start)
            {
                var m = Mover.At(sim.Shape, start);
                m.StepFrame(sim.Shape, Input8.East, true, sim.Solid);
                Assert.True(m.Dodge.Dashing);
                for (int f = 1; f < Dodge.DashFrames; f++)
                {
                    m.StepFrame(sim.Shape, Input8.East, false, sim.Solid);
                }

                Assert.False(m.Dodge.Dashing);
                return m.Position.X - start.X;
            }

            int grass = Dash(Center(5, 10));
            int water = Dash(Center(40, 10));
            _out.WriteLine($"уворот: трава {grass}, вода {water}");
            Assert.Equal(Dodge.DashFrames * Dodge.SpeedX10 / 10, grass);
            Assert.Equal(Dodge.DashFrames * ((Dodge.SpeedX10 * 115) >> 8) / 10, water);
        }

        /// <summary>Сквозь полосу кустов игрок проходит (раньше куст — стена), медленнее, чем по траве; куст на месте.</summary>
        [Fact]
        public void C018_Bush_Passable_Slower()
        {
            Simulation sim = Flat();
            Fill(sim, 20, 0, 21, 63, FloorId.Grass, ObjectId.Bush);
            int id = sim.AddEntity(Center(15, 10));
            int t = 0;
            while (sim.GetEntity(id).Mover.Position.X < Center(25, 10).X && t < 400)
            {
                sim.SetInputAt(sim.SlotOf(id), new InputFrame(Input8.East));
                sim.Step();
                t++;
            }

            // 10 тайлов: 8 по траве (40 тактов) + 2 в кусте (≈ 2 × 256 / 30,8 ≈ 17).
            _out.WriteLine($"10 тайлов сквозь кусты: {t} тактов");
            Assert.InRange(t, 54, 60);
            Assert.Equal(ObjectId.Bush, ObjectId.IdOf(sim.World.GetObject(new TileCoord(20, 10))));
        }

        /// <summary>
        /// Путь врага: озеро с обходом — в обход (ни одной точки в воде); река поперёк мира — вброд (путь найден, а не «упёрся»);
        /// одна полоса воды поперёк — напрямую дешевле обхода.
        /// </summary>
        [Fact]
        public void C018_EnemyPath_AroundLake_ThroughRiver()
        {
            Simulation sim = Flat();
            Fill(sim, 18, 7, 22, 13, FloorId.Water, ObjectId.Empty);
            var terrain = new SimulationPathTerrain(sim);
            var finder = new PathFinder(enclosureFillLimit: 0);
            var path = new PathBuffer();
            Assert.Equal(PathStatus.Found, finder.Find(terrain, new TileCoord(15, 10), new TileCoord(25, 10), PathMode.Walker, path));
            for (int i = 0; i < path.Length; i++)
            {
                TileCoord t = Torus.TileOf(sim.Shape, path.Points[i]);
                Assert.NotEqual(FloorId.Water, sim.World.GetFloor(t));
            }

            Fill(sim, 18, 0, 22, 63, FloorId.Water, ObjectId.Empty);
            Assert.Equal(PathStatus.Found, finder.Find(terrain, new TileCoord(15, 10), new TileCoord(25, 10), PathMode.Walker, path));
            int wet = 0;
            for (int i = 0; i < path.Length; i++)
            {
                wet += sim.World.GetFloor(Torus.TileOf(sim.Shape, path.Points[i])) == FloorId.Water ? 1 : 0;
            }

            Assert.True(wet > 0, "путь через реку без воды");
        }

        /// <summary>Враг гонится за игроком через реку без обхода: входит в воду, переходит её (медленно) и выходит на другой берег.</summary>
        [Fact]
        public void C018_Enemy_WadesRiver_ToPlayer()
        {
            var sim = new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1802));
            Fill(sim, 30, 0, 32, 1023, FloorId.Water, ObjectId.Empty);
            int ch = sim.AddEntity(Center(36, 20));
            int rogue = sim.AddMeleeRogue(Center(26, 20));
            bool wet = false;
            int slowStep = int.MaxValue;
            int prevX = sim.GetEntity(rogue).Mover.Position.X;
            for (int i = 0; i < 600; i++)
            {
                sim.Step();
                WorldPos e = sim.GetEntity(rogue).Mover.Position;
                TileCoord tile = Torus.TileOf(sim.Shape, e);
                if (sim.World.GetFloor(tile) == FloorId.Water)
                {
                    wet = true;
                    slowStep = Math.Min(slowStep, Math.Abs(e.X - prevX));
                }

                prevX = e.X;
                if (e.X > 34 * U)
                {
                    break; // перешёл реку (дальше — бой, затем уход домой обратно через воду)
                }
            }

            WorldPos end = sim.GetEntity(rogue).Mover.Position;
            _out.WriteLine($"враг: x = {end.X / (double)U:F2}, в воде был: {wet}");
            Assert.True(wet, "враг не входил в воду");
            Assert.True(end.X > 34 * U, $"враг не перешёл реку: x = {end.X / (double)U:F2}");
            int slowMax = ((MeleeRogue.ChaseSpeedX10 * TerrainSlow.WaterQ8) >> 8) / 10 + 1;
            Assert.True(slowStep <= slowMax, $"в воде шаг {slowStep} > {slowMax}: не замедлился");
        }

        /// <summary>Детерминизм: два одинаковых прогона по воде и кустам дают один и тот же хэш состояния.</summary>
        [Fact]
        public void C018_Deterministic_SameHash()
        {
            ulong Run()
            {
                var sim = new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1803));
                Fill(sim, 30, 0, 32, 1023, FloorId.Water, ObjectId.Empty);
                Fill(sim, 24, 15, 25, 25, FloorId.Grass, ObjectId.Bush);
                int ch = sim.AddEntity(Center(36, 20));
                sim.AddMeleeRogue(Center(20, 20));
                for (int i = 0; i < 400; i++)
                {
                    sim.SetInputAt(sim.SlotOf(ch), new InputFrame(i % 90 < 45 ? Input8.West : Input8.SouthWest));
                    sim.Step();
                }

                return StateHash.Compute(sim).Total;
            }

            Assert.Equal(Run(), Run());
        }
    }
}
