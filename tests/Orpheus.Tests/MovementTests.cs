using System;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Rng;
using Orpheus.Core.Trig;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Приёмка движения (карточка 0-004, числа — work/refs/movement.md): бег 6 тайлов/с целыми
    /// числами, оборот тора, 8 направлений, коллизия круга 0,4 с сеткой тайлов (стена, скольжение,
    /// угол, зазор 1 тайл), повторяемость 20 × 10 000 тактов с детерминированным вводом.
    /// <para>
    /// Большие миры (1024², 4096²) в памяти не выделяются: проходимость тайла — формула
    /// (проходимая карта-лабиринт, стена-колонка, угол), мир задаёт только <see cref="MapShape"/>.
    /// </para>
    /// </summary>
    public class MovementTests
    {
        private const int U = Torus.SubTilesPerTile; // 256 unit в тайле
        private const int Base = MovementTick.BaseSpeedX10; // 512 unit × 10 за такт

        // ---------- Формулы проходимости (мир в памяти не выделяем) ----------

        /// <summary>Зерно шума проходимой карты <see cref="Maze"/>.</summary>
        private const ulong MazeSeed = 0x0004_0611_0000_0000UL;

        /// <summary>
        /// Проходимая карта для серий (тор 1024² = 32 × 32 квартала по 32 тайла): проспекты шириной
        /// 2 тайла через каждые 32 (x &amp; 31 &lt; 2, y &amp; 31 &lt; 2 — в том числе по шву тора по обеим
        /// осям), в квартале — крест из двух длинных стен (колонна x = 16 и строка y = 16, вдоль них
        /// скольжение), коридор высотой 1 тайл (строка 9, x 18…30, стены на строках 8 и 10) и шум
        /// SplitMix64: 15 % твёрдых тайлов. Мир в памяти не выделяется — всё формулой.
        /// </summary>
        private static bool Maze(MapShape map, TileCoord t)
        {
            int lx = t.X & 31;
            int ly = t.Y & 31;
            if (lx < 2 || ly < 2)
            {
                return false; // проспект
            }

            if ((lx == 16 && ly >= 4 && ly <= 27) || (ly == 16 && lx >= 4 && lx <= 27))
            {
                return true; // крест длинных стен
            }

            if (lx >= 18 && lx <= 30 && ly >= 8 && ly <= 10)
            {
                return ly != 9; // коридор в 1 тайл между двумя стенами
            }

            return Orpheus.Core.Rng.SplitMix64.Hash(t.X, t.Y, MazeSeed) % 100 < 15; // шум
        }

        /// <summary>Сплошная стена-колонка на x = 20 (плоская карта).</summary>
        private static bool WallColumn(MapShape map, TileCoord t) => t.X == 20;

        /// <summary>Угол: стена-колонка x = 20 (y ≤ 30) и стена-строка y = 30 (x ≤ 20).</summary>
        private static bool Corner(MapShape map, TileCoord t) => (t.X == 20 && t.Y <= 30) || (t.Y == 30 && t.X <= 20);

        /// <summary>Коридор высотой в 1 тайл: твёрдые чётные строки, проходы — нечётные.</summary>
        private static bool Corridor(MapShape map, TileCoord t) => (t.Y & 1) == 0;

        /// <summary>Стена x = 20 с зазором в один тайл на строке 30.</summary>
        private static bool WallWithGap(MapShape map, TileCoord t) => t.X == 20 && t.Y != 30;

        // ---------- Числа карточки ----------

        [Fact]
        public void D00_Movement_Constants_MatchRefs()
        {
            Assert.Equal(512, MovementTick.BaseSpeedX10);        // 6 т/с ÷ 30 Гц = 51,2 unit → unit × 10
            Assert.Equal(362, MovementTick.DiagonalPerAxisX10);  // 512 ÷ √2 = 362,04
            Assert.Equal(10, MovementTick.SpeedScale);
            Assert.Equal(5, MovementTick.TicksPerTile);
            Assert.Equal(20480, MovementTick.TicksPerLap(4096));
            Assert.Equal(102, Collision.RadiusUnits);            // 0,4 × 256 = 102,4
            Assert.Equal(10404, Collision.RadiusSquared);        // 102²
            Assert.Equal(362, Input8.DiagonalSpeedX10(512));     // база → диагональ на ось
            Assert.Equal(724, Input8.DiagonalSpeedX10(1024));    // 1024 ÷ √2 = 724,08
        }

        // ---------- Ввод: 8 направлений ----------

        [Fact]
        public void D00_Movement_Input8_KeyboardCombos_GiveEightDirections()
        {
            var combos = new[]
            {
                new { h = 1, v = 0, name = "восток" },
                new { h = 1, v = 1, name = "юго-восток" },
                new { h = 0, v = 1, name = "юг" },
                new { h = -1, v = 1, name = "юго-запад" },
                new { h = -1, v = 0, name = "запад" },
                new { h = -1, v = -1, name = "северо-запад" },
                new { h = 0, v = -1, name = "север" },
                new { h = 1, v = -1, name = "северо-восток" },
            };

            var seen = new int[8];
            for (int i = 0; i < combos.Length; i++)
            {
                Input8 input = Input8.FromKeys(combos[i].h, combos[i].v);
                Assert.True(input.IsMoving, combos[i].name);
                int index = input.DirectionIndex;
                Assert.Equal(i, index);                       // порядок секторов: восток → по часовой
                Assert.Equal(input, Input8.FromIndex(index)); // номер сектора ↔ направление
                Assert.Equal(input, Input8.FromAngle(input.ToAngle()));
                seen[index]++;
            }

            for (int i = 0; i < 8; i++)
            {
                Assert.Equal(1, seen[i]);
            }

            // «два клавишы в одну сторону» и избыточная величина — то же направление
            Assert.Equal(Input8.East, Input8.FromKeys(3, 0));
            Assert.Equal(Input8.None, Input8.FromKeys(0, 0));
            Assert.False(Input8.None.IsMoving);
        }

        [Fact]
        public void D00_Movement_Input8_KeysByPress_MatchCombos()
        {
            Assert.Equal(Input8.West, Input8.FromKeys(true, false, false, false));
            Assert.Equal(Input8.East, Input8.FromKeys(false, true, false, false));
            Assert.Equal(Input8.North, Input8.FromKeys(false, false, true, false));
            Assert.Equal(Input8.South, Input8.FromKeys(false, false, false, true));
            Assert.Equal(Input8.SouthEast, Input8.FromKeys(false, true, false, true));
            Assert.Equal(Input8.NorthWest, Input8.FromKeys(true, false, true, false));
            Assert.Equal(Input8.None, Input8.FromKeys(false, false, false, false));
            Assert.Equal(Input8.None, Input8.FromKeys(true, true, false, false)); // влево и вправо — стойка
        }

        [Theory]
        [InlineData(0, 0)]       // восток: 0°
        [InlineData(4095, 0)]    // до середины сектора — ещё восток
        [InlineData(4096, 1)]    // ровно середина между секторами — к следующему (юго-восток)
        [InlineData(8191, 1)]
        [InlineData(8192, 1)]    // юго-восток: 45°
        [InlineData(12287, 1)]
        [InlineData(12288, 2)]
        [InlineData(16384, 2)]   // юг: 90°
        [InlineData(24576, 3)]   // юго-запад: 135°
        [InlineData(32768, 4)]   // запад: 180°
        [InlineData(40960, 5)]   // северо-запад: 225°
        [InlineData(49152, 6)]   // север: 270°
        [InlineData(53247, 6)]
        [InlineData(53248, 7)]   // середина сектора — к северо-востоку
        [InlineData(57344, 7)]   // северо-восток: 315°
        [InlineData(61439, 7)]
        [InlineData(61440, 0)]   // середина сектора — заворот к востоку
        [InlineData(65535, 0)]
        public void D00_Movement_Input8_StickAngle_PicksNearestOfEight(int raw, int expectedIndex)
        {
            Input8 input = Input8.FromAngle(Angle16.FromRaw(raw));
            Assert.Equal(expectedIndex, input.DirectionIndex);
            Assert.Equal(input, Input8.FromStick(
                input.DX * 1000,
                input.DY * 1000)); // свободный вектор стика → то же направление
        }

        [Fact]
        public void D00_Movement_Input8_StickZeroVector_IsStand()
        {
            Assert.Equal(Input8.None, Input8.FromStick(0, 0));
            Assert.Equal(0, Input8.None.SpeedX10(Base));
            Assert.Equal(0, Input8.None.SpeedY10(Base));
        }

        [Fact]
        public void D00_Movement_Input8_Speeds_Straight512Diagonal362()
        {
            Assert.Equal(512, Input8.East.SpeedX10(Base));
            Assert.Equal(0, Input8.East.SpeedY10(Base));
            Assert.Equal(-512, Input8.West.SpeedX10(Base));
            Assert.Equal(-512, Input8.North.SpeedY10(Base));
            Assert.Equal(362, Input8.SouthEast.SpeedX10(Base));
            Assert.Equal(362, Input8.SouthEast.SpeedY10(Base));
            Assert.Equal(-362, Input8.NorthWest.SpeedX10(Base));
            Assert.Equal(-362, Input8.NorthWest.SpeedY10(Base));
            Assert.Equal(1024, Input8.East.SpeedX10(1024)); // снаряжение: та же схема на другой скорости
            Assert.Equal(724, Input8.SouthEast.SpeedX10(1024));
        }

        // ---------- Скорость: целый бег ----------

        [Fact]
        public void D00_Movement_300Ticks_RunExactly60Tiles()
        {
            var map = MapShape.World(1024);
            var start = new WorldPos(128, 128);
            Int2 carry;
            WorldPos end = Run(map, start, Input8.East, 300, Collision.Empty, out carry);

            Int2 d = Torus.Delta(map, start, end);
            Assert.Equal(15360, d.X);                      // 300 × 51,2 unit
            Assert.Equal(0, d.Y);
            Assert.Equal(60 * U, d.X);                     // ровно 60 тайлов
            Assert.Equal(300 * Base / MovementTick.SpeedScale, d.X); // 300 × 512 ÷ 10
            Assert.Equal(0, carry.X);
            Assert.Equal(0, carry.Y);                      // остаток закрылся: путь кратен тайлу
        }

        [Theory]
        [InlineData(5, 256, 0)]
        [InlineData(10, 512, 0)]
        [InlineData(25, 1280, 0)]
        public void D00_Movement_StraightRun_NTilesExactly(int ticks, int expectedUnits, int expectedCarry)
        {
            var map = MapShape.World(1024);
            var start = new WorldPos(128, 128);
            Int2 carry;
            WorldPos end = Run(map, start, Input8.East, ticks, Collision.Empty, out carry);
            Int2 d = Torus.Delta(map, start, end);
            Assert.Equal(expectedUnits, d.X);
            Assert.Equal(expectedCarry, carry.X);
        }

        [Fact]
        public void D00_Movement_EightDirections_DiagonalNotFasterThanStraight()
        {
            var map = MapShape.World(1024);
            var start = new WorldPos(64 * U, 64 * U);
            long straight = 0;

            for (int index = 0; index < Input8.Count; index++)
            {
                Input8 input = Input8.FromIndex(index);
                Int2 carry;
                WorldPos end = Run(map, start, input, 5, Collision.Empty, out carry);
                Int2 d = Torus.Delta(map, start, end);
                Assert.Equal(0, carry.X);
                Assert.Equal(0, carry.Y);

                long dist2 = d.LengthSquared;
                if (!input.IsDiagonal)
                {
                    straight = dist2;
                    Assert.Equal((long)U * U, dist2);          // 256 unit за 5 тактов
                    Assert.Equal(U, Math.Abs(d.X) + Math.Abs(d.Y));
                }
                else
                {
                    Assert.Equal(181, Math.Abs(d.X));         // 5 × 362 ÷ 10 = 181 unit на ось
                    Assert.Equal(181, Math.Abs(d.Y));
                    Assert.Equal(181L * 181 + 181L * 181, dist2); // 255,97² unit
                    Assert.True(dist2 < straight, $"диагональ быстрее прямой: {dist2} ≥ {straight}");
                }
            }
        }

        // ---------- Оборот тора: экватор и меридиан ----------

        [Theory]
        [InlineData(1024, 0)]
        [InlineData(1024, 1)]
        [InlineData(4096, 0)]
        [InlineData(4096, 1)]
        public void D00_Movement_RunAroundWorld_LapEndsInSamePosition(int size, int axis)
        {
            var map = MapShape.World(size);
            Input8 input = axis == 0 ? Input8.East : Input8.South;
            int ticks = MovementTick.TicksPerLap(size); // 512 unit × 10 → W × 256 unit за W × 5 тактов

            for (ulong seed = 1; seed <= 3; seed++)
            {
                var rng = new Core.Rng.SplitMix64(seed); // генератор из ядра (docs/07, 11.5)
                var start = new WorldPos(rng.NextInt(map.WidthUnits), rng.NextInt(map.HeightUnits));
                Int2 carry;
                WorldPos end = Run(map, start, input, ticks, Collision.Empty, out carry);

                Assert.True(start == end, $"сторона {size}, ось {axis}, зерно {seed}: {start} → {end}");
                Assert.Equal(0, carry.X);
                Assert.Equal(0, carry.Y);
            }
        }

        [Fact]
        public void D00_Movement_TorusSeam_CrossesWithoutJump()
        {
            var map = MapShape.World(1024);
            var start = new WorldPos(map.WidthUnits - 128, 128); // центр последнего тайла
            var mover = new Mover(start);

            for (int tick = 0; tick < 5; tick++)
            {
                WorldPos prev = mover.Position;
                WorldPos next = mover.Step(map, Input8.East, Collision.Empty);
                Int2 d = Torus.Delta(map, prev, next); // через шов — разность по тору, без скачка
                Assert.InRange(d.X, 51, 52);
                Assert.Equal(0, d.Y);
                Assert.InRange(next.X, 0, map.WidthUnits - 1);
            }

            Assert.Equal(128, mover.Position.X); // 5 тактов × 51,2 = 256 unit через шов
            Assert.Equal(128, mover.Position.Y);
        }

        // ---------- Коллизия: стена, скольжение, угол, зазор ----------

        [Fact]
        public void D00_Movement_Wall_StopsCircleAtTileFace()
        {
            var map = MapShape.Flat(64, 64);
            var start = Center(10, 10);
            int face = 20 * U - Collision.RadiusUnits; // грань стены − радиус круга

            var mover = new Mover(start);
            int maxX = start.X;
            for (int tick = 0; tick < 200; tick++)
            {
                WorldPos pos = mover.Step(map, Input8.East, WallColumn);
                Assert.False(Collision.TouchesSolid(map, pos, WallColumn), $"такт {tick}: круг в стене");
                Assert.True(pos.X <= face, $"такт {tick}: {pos.X} прошёл грань {face}");
                maxX = Math.Max(maxX, pos.X);
            }

            Assert.Equal(face, maxX);                     // упор в стену: касание, 102 unit от грани
            Assert.Equal(face, mover.Position.X);
            Assert.Equal(10 * U + 128, mover.Position.Y);
            Assert.Equal(19, Torus.TileOf(map, mover.Position).X); // стоит в тайле перед стеной

            for (int tick = 0; tick < 100; tick++)
            {
                Assert.Equal(face, mover.Step(map, Input8.East, WallColumn).X);
            }
        }

        [Fact]
        public void D00_Movement_Wall_SlidesAlongWall()
        {
            var map = MapShape.Flat(64, 64);
            int face = 20 * U - Collision.RadiusUnits;
            var mover = new Mover(Center(10, 10));

            // упёрся по X, продолжая на юг
            for (int tick = 0; tick < 200; tick++)
            {
                WorldPos pos = mover.Step(map, Input8.SouthEast, WallColumn);
                Assert.False(Collision.TouchesSolid(map, pos, WallColumn), $"такт {tick}: круг в стене");
            }

            Assert.Equal(face, mover.Position.X);
            int yBefore = mover.Position.Y;
            for (int tick = 0; tick < 5; tick++)
            {
                mover.Step(map, Input8.SouthEast, WallColumn);
            }

            Assert.Equal(face, mover.Position.X);
            Assert.Equal(181, mover.Position.Y - yBefore); // скольжение идёт со скоростью диагонали: 181 unit за 5 тактов
        }

        [Fact]
        public void D00_Movement_Corner_StandsStill()
        {
            var map = MapShape.Flat(64, 64);
            int faceX = 20 * U - Collision.RadiusUnits;
            int faceY = 30 * U - Collision.RadiusUnits;
            var mover = new Mover(Center(15, 25));

            for (int tick = 0; tick < 200; tick++)
            {
                WorldPos pos = mover.Step(map, Input8.SouthEast, Corner);
                Assert.False(Collision.TouchesSolid(map, pos, Corner), $"такт {tick}: круг в стене");
            }

            Assert.Equal(faceX, mover.Position.X);
            Assert.Equal(faceY, mover.Position.Y);

            var locked = mover.Position;
            for (int tick = 0; tick < 100; tick++)
            {
                Assert.True(locked == mover.Step(map, Input8.SouthEast, Corner), $"такт {tick}: уполз из угла");
            }
        }

        [Fact]
        public void D00_Movement_OneTileCorridor_PassesThrough()
        {
            var map = MapShape.Flat(64, 64);
            var mover = new Mover(Center(0, 31)); // коридор высотой в 1 тайл: строка 31

            for (int tick = 0; tick < 300; tick++) // 60 тайлов на восток
            {
                WorldPos pos = mover.Step(map, Input8.East, Corridor);
                Assert.False(Collision.TouchesSolid(map, pos, Corridor), $"такт {tick}: круг в стене");
            }

            Assert.Equal(128 + 60 * U, mover.Position.X);
            Assert.Equal(31 * U + 128, mover.Position.Y);
        }

        [Fact]
        public void D00_Movement_OneTileCorridor_DiagonalSlidesThrough()
        {
            var map = MapShape.Flat(64, 64);
            var mover = new Mover(Center(0, 31));
            int low = 32 * U - Collision.RadiusUnits; // грань нижней стены прохода

            for (int tick = 0; tick < 100; tick++)
            {
                WorldPos pos = mover.Step(map, Input8.SouthEast, Corridor);
                Assert.False(Collision.TouchesSolid(map, pos, Corridor), $"такт {tick}: круг в стене");
                Assert.InRange(pos.Y, 31 * U + Collision.RadiusUnits, low);
            }

            Assert.Equal(128 + 362 * 100 / MovementTick.SpeedScale, mover.Position.X); // 3620 unit на восток
            Assert.Equal(low, mover.Position.Y);                                        // и скольжение по нижней стене
        }

        [Fact]
        public void D00_Movement_OneTileGapInWall_PassesThrough()
        {
            var map = MapShape.Flat(64, 64);
            var mover = new Mover(Center(10, 30)); // зазор в стене x = 20 на строке 30

            for (int tick = 0; tick < 100; tick++) // 20 тайлов на восток
            {
                WorldPos pos = mover.Step(map, Input8.East, WallWithGap);
                Assert.False(Collision.TouchesSolid(map, pos, WallWithGap), $"такт {tick}: круг в стене");
            }

            Assert.Equal(10 * U + 128 + 20 * U, mover.Position.X); // прошёл стену сквозь зазор в 1 тайл
            Assert.Equal(30 * U + 128, mover.Position.Y);
        }

        [Fact]
        public void D00_Movement_WallInBigWorld_StopsAtFace()
        {
            var map = MapShape.World(4096); // 16,8 млн тайлов — в памяти только MapShape
            var mover = new Mover(Center(90, 90));

            for (int tick = 0; tick < MovementTick.TicksPerLap(4096); tick++)
            {
                WorldPos pos = mover.Step(map, Input8.East, WallColumn); // на пути стена-колонка x = 20
                Assert.False(Collision.TouchesSolid(map, pos, WallColumn), $"такт {tick}: круг в стене");
            }

            // полный оборот (20 480 тактов) не состоялся: круг упёрся в грань стены и стоит
            Assert.Equal(20 * U - Collision.RadiusUnits, mover.Position.X);
            Assert.Equal(90 * U + 128, mover.Position.Y);
        }

        // ---------- Коллизия: углы тайлов, порядок осей, диагональный зазор ----------

        [Fact]
        public void D00_Movement_TileCorner_PushNeverGoesBehindStart()
        {
            // замечание проверки 1: тайл (10, 10), круг в (2470, 2510), шаг +10 по X. До угла
            // (2560, 2560) — (90, 50), 102,96 unit: старт свободен. Упор по грани (2560 − 102 = 2458)
            // увёл бы круг на 12 unit назад; касание угла — ContactReach(50) = 89 → X = 2471.
            var map = MapShape.Flat(32, 32);
            TileSolid block = (m, t) => t.X == 10 && t.Y == 10;
            var start = new WorldPos(2470, 2510);
            Assert.False(Collision.TouchesSolid(map, start, block));

            WorldPos q = Collision.MoveX(map, start, 10, block);
            Assert.Equal(2471, q.X);
            Assert.Equal(2510, q.Y);
            Assert.False(Collision.TouchesSolid(map, q, block));

            Assert.Equal(Collision.RadiusUnits, Collision.ContactReach(0));
            Assert.Equal(89, Collision.ContactReach(50));
            Assert.Equal(0, Collision.ContactReach(Collision.RadiusUnits));
            for (int across = 0; across < Collision.RadiusUnits; across++)
            {
                int h = Collision.ContactReach(across); // наименьшее h: h² + across² ≥ r²
                Assert.True((long)h * h + (long)across * across >= Collision.RadiusSquared);
                Assert.True((long)(h - 1) * (h - 1) + (long)across * across < Collision.RadiusSquared);
            }
        }

        [Fact]
        public void D00_Movement_RunPastCornerOfSingleBlock_NeverStepsBack()
        {
            // бег на юго-восток мимо угла одиночного блока: смещение по каждой оси ввода за такт ≥ 0
            var map = MapShape.Flat(32, 32);
            TileSolid block = (m, t) => t.X == 10 && t.Y == 10;
            for (int dy = -160; dy <= 160; dy += 4)
            {
                var mover = new Mover(new WorldPos(8 * U, 8 * U + dy));
                for (int tick = 0; tick < 120; tick++)
                {
                    WorldPos prev = mover.Position;
                    WorldPos next = mover.Step(map, Input8.SouthEast, block);
                    Assert.True(next.X >= prev.X && next.Y >= prev.Y, $"сдвиг {dy}, такт {tick}: шаг назад {prev} → {next}");
                    Assert.False(Collision.TouchesSolid(map, next, block), $"сдвиг {dy}, такт {tick}: круг в стене");
                }
            }
        }

        [Fact]
        public void D00_Movement_TileCorners_8MillionSteps_StayBetweenStartAndTarget()
        {
            // окрестности 5 × 5 тайлов со случайными твёрдыми (≈ 40 %), центр (2, 2) свободен;
            // все свободные старты в центральном тайле с шагом 4 unit × 8 направлений, шаг — один
            // такт MovementTick.Tick (прямая 51, диагональ 36 unit). После шага по каждой оси —
            // между стартом и целью включительно, круг ни в один твёрдый тайл не вошёл.
            var map = MapShape.Flat(5, 5);
            var cells = new bool[25];
            TileSolid solid = (m, t) => cells[t.Y * 5 + t.X];
            var rng = DetRandom.ForSystem(0x0004_0611_0000_0002UL, 7);

            long steps = 0;
            long blocked = 0;
            int hoods = 0;
            while (steps < 8_000_000)
            {
                hoods++;
                for (int i = 0; i < cells.Length; i++)
                {
                    cells[i] = i != 12 && rng.NextInt(100) < 40;
                }

                for (int sy = 2 * U; sy < 3 * U; sy += 4)
                {
                    for (int sx = 2 * U; sx < 3 * U; sx += 4)
                    {
                        var start = new WorldPos(sx, sy);
                        if (Collision.TouchesSolid(map, start, solid))
                        {
                            continue;
                        }

                        for (int dir = 0; dir < Input8.Count; dir++)
                        {
                            Input8 input = Input8.FromIndex(dir);
                            int tx = sx + input.SpeedX10(Base) / MovementTick.SpeedScale;
                            int ty = sy + input.SpeedY10(Base) / MovementTick.SpeedScale;
                            WorldPos q = MovementTick.Tick(map, start, input, solid);
                            if (q.X < Math.Min(sx, tx) || q.X > Math.Max(sx, tx) || q.Y < Math.Min(sy, ty) || q.Y > Math.Max(sy, ty))
                            {
                                Assert.Fail($"окрестность {hoods}, старт ({sx}, {sy}), {input}: {q} вне отрезка старт → ({tx}, {ty})");
                            }

                            if (Collision.TouchesSolid(map, q, solid))
                            {
                                Assert.Fail($"окрестность {hoods}, старт ({sx}, {sy}), {input}: круг в стене в {q}");
                            }

                            if (q.X != tx || q.Y != ty)
                            {
                                blocked++;
                            }

                            steps++;
                        }
                    }
                }
            }

            Assert.True(blocked > steps / 10, $"упоров мало: {blocked} из {steps}"); // проверка видела стены, а не пустоту
        }

        [Fact]
        public void D00_Movement_StartInsideBlock_WalksOutWithoutStepBack()
        {
            // блок поставили на персонажа: круг уже касается тайла (10, 10) — 52 unit внутрь грани.
            // Упора от такого тайла нет (откат от него увёл бы за старт): идёт на запад и выходит.
            var map = MapShape.Flat(32, 32);
            TileSolid block = (m, t) => t.X == 10 && t.Y == 10;
            var mover = new Mover(new WorldPos(10 * U - 50, 10 * U + 128));
            Assert.True(Collision.TouchesSolid(map, mover.Position, block));

            for (int tick = 0; tick < 5; tick++)
            {
                WorldPos prev = mover.Position;
                WorldPos next = mover.Step(map, Input8.West, block);
                Assert.Equal(tick == 4 ? -52 : -51, next.X - prev.X); // 51,2 unit за такт (остаток), без упора
            }

            Assert.False(Collision.TouchesSolid(map, mover.Position, block)); // 256 unit на запад — вышел
        }

        [Fact]
        public void D00_Movement_AxisOrder_XThenY()
        {
            // подход к углу блока (10, 10) по диагонали: порядок осей меняет исход.
            // X, потом Y: X упирается в угол (касание при across = 30 → 98 от грани) → 2462,
            //             затем Y упирается при across = 98 → 29 от грани → 2531.
            // Y, потом X было бы: Y проходит (до тайла по X 112 > r) → 2566, затем X — грань → 2458.
            var map = MapShape.Flat(32, 32);
            TileSolid block = (m, t) => t.X == 10 && t.Y == 10;
            var start = new WorldPos(2448, 2530);

            WorldPos q = Collision.Move(map, start, 36, 36, block);
            Assert.Equal(new WorldPos(2462, 2531), q);
            Assert.Equal(q, MovementTick.Tick(map, start, Input8.SouthEast, block));

            WorldPos yFirst = Collision.MoveX(map, Collision.MoveY(map, start, 36, block), 36, block);
            Assert.Equal(new WorldPos(2458, 2566), yFirst); // другой исход — порядок значим
        }

        [Fact]
        public void D00_Movement_DiagonalGap_CornerTouchingTiles_CircleDoesNotPass()
        {
            // тайлы (10, 10) и (11, 11) касаются углом в точке (2816, 2816): зазор между ними
            // по диагонали — ширины 0, круг r = 102 из (10, 11) в (11, 10) не проходит
            // (в отличие от прямого зазора в 1 тайл, см. D00_Movement_OneTileGapInWall_PassesThrough).
            var map = MapShape.Flat(32, 32);
            TileSolid pair = (m, t) => (t.X == 10 && t.Y == 10) || (t.X == 11 && t.Y == 11);
            var blockedTile = new TileCoord(10, 11);

            for (int sy = 11 * U + Collision.RadiusUnits; sy <= 12 * U - 1; sy += 16)
            {
                for (int sx = 10 * U; sx <= 11 * U - Collision.RadiusUnits; sx += 16)
                {
                    var mover = new Mover(new WorldPos(sx, sy));
                    for (int tick = 0; tick < 200; tick++)
                    {
                        WorldPos pos = mover.Step(map, Input8.NorthEast, pair);
                        Assert.False(Collision.TouchesSolid(map, pos, pair));
                        Assert.False(pos.X >= 11 * U && pos.Y < 11 * U, $"старт ({sx}, {sy}), такт {tick}: прошёл в зазор, {pos}");
                    }

                    // стоит в углу тайла (10, 11), упёршись в оба блока
                    Assert.Equal(blockedTile, Torus.TileOf(map, mover.Position));
                    Assert.Equal(new WorldPos(11 * U - Collision.RadiusUnits, 11 * U + Collision.RadiusUnits), mover.Position);
                }
            }
        }

        [Fact]
        public void D00_Movement_AllSolid_KeepsDeterministicPosition()
        {
            var map = MapShape.Flat(32, 32);
            var a = new Mover(Center(5, 5));
            var b = new Mover(Center(5, 5));

            for (int tick = 0; tick < 50; tick++)
            {
                WorldPos pa = a.Step(map, Input8.SouthEast, Collision.AllSolid);
                WorldPos pb = b.Step(map, Input8.SouthEast, Collision.AllSolid);
                Assert.True(pa == pb);                       // исход из тупика определён, без ветров по памяти
                Assert.InRange(pa.X, 0, map.WidthUnits - 1);
                Assert.InRange(pa.Y, 0, map.HeightUnits - 1);
            }
        }

        // ---------- Повторяемость ----------

        [Fact]
        public void D00_Movement_RandomInput_20Series10000Ticks_RepeatIsByteIdentical()
        {
            var map = MapShape.World(1024);
            const ulong worldSeed = 0x0511_0004_0000_0000UL;
            int seamX = 0;
            int seamY = 0;
            long slides = 0;

            for (int series = 0; series < 20; series++)
            {
                byte[] first = Simulate(map, worldSeed, series, 10_000, out SeriesStats stats);
                byte[] second = Simulate(map, worldSeed, series, 10_000, out SeriesStats again);
                Assert.Equal(first.Length, second.Length);
                Assert.Equal(first, second); // побайтно та же траектория
                Assert.Equal(stats, again);

                // серия реально ходит: длинный путь, пересечение шва, упоры и скольжение
                Assert.True(stats.PathUnits >= 1000L * U, $"серия {series}: путь {stats.PathUnits / U} тайлов < 1000");
                Assert.True(stats.SeamX + stats.SeamY >= 1, $"серия {series}: шов тора не пересечён");
                Assert.True(stats.Blocked > 0, $"серия {series}: ни одного упора");
                seamX += stats.SeamX;
                seamY += stats.SeamY;
                slides += stats.Slides;
            }

            Assert.True(seamX >= 1, "шов по X не пересечён ни в одной серии");
            Assert.True(seamY >= 1, "шов по Y не пересечён ни в одной серии");
            Assert.True(slides >= 100, $"скольжений вдоль стен мало: {slides}");
        }

        [Fact]
        public void D00_Movement_ClientAndServer_SameFunction_SameResult()
        {
            var map = MapShape.World(1024);
            const ulong worldSeed = 0x0511_0004_0000_0001UL;
            const int ticks = 10_000;

            // журнал ввода клиента: направление меняется раз в 10 тактов (8 направлений + стойка)
            var codes = new byte[ticks];
            var gen = DetRandom.ForSystem(worldSeed, 42);
            WorldPos start = StartNearSeam(map, ref gen);
            Input8 input = Input8.None;
            for (int tick = 0; tick < ticks; tick++)
            {
                if ((tick % 10) == 0)
                {
                    input = Input8.FromIndex(gen.NextInt(Input8.Count + 1));
                }

                codes[tick] = Code(input); // код направления 0…7, стойка — 8
            }

            // клиент: локальный ввод → предсказание своим Mover
            var client = new MemoryStream();
            using (var writer = new BinaryWriter(client, System.Text.Encoding.UTF8, true))
            {
                var mover = new Mover(start);
                for (int tick = 0; tick < ticks; tick++)
                {
                    WorldPos pos = mover.Step(map, Decode(codes[tick]), Maze);
                    writer.Write(codes[tick]);
                    writer.Write(pos.X);
                    writer.Write(pos.Y);
                }
            }

            // сервер: тот же журнал ввода через ту же чистую функцию MovementTick.Tick
            var server = new MemoryStream();
            using (var writer = new BinaryWriter(server, System.Text.Encoding.UTF8, true))
            {
                WorldPos pos = start;
                Int2 carry = default;
                for (int tick = 0; tick < ticks; tick++)
                {
                    pos = MovementTick.Tick(map, pos, carry, Decode(codes[tick]), Base, Maze, out carry);
                    writer.Write(codes[tick]);
                    writer.Write(pos.X);
                    writer.Write(pos.Y);
                }
            }

            Assert.Equal(client.ToArray(), server.ToArray());
        }

        // ---------- Помощники ----------

        /// <summary>Центр тайла (x, y) как позиция существа.</summary>
        private static WorldPos Center(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        /// <summary>Код ввода в журнале (docs/07, 4.3): 0…7 — направление, 8 — стойка.</summary>
        private static byte Code(Input8 input) => (byte)(input.IsMoving ? input.DirectionIndex : 8);

        /// <summary>Направление из кода журнала ввода.</summary>
        private static Input8 Decode(byte code) => Input8.FromIndex(code);

        private static WorldPos Run(in MapShape map, WorldPos start, Input8 input, int ticks, TileSolid solid, out Int2 carry)
        {
            var mover = new Mover(start);
            for (int tick = 0; tick < ticks; tick++)
            {
                mover.Step(map, input, solid);
            }

            carry = mover.Carry;
            return mover.Position;
        }

        /// <summary>Итоги серии: путь (по Чебышёву, unit), пересечения шва по осям, упоры, скольжения.</summary>
        private readonly struct SeriesStats : IEquatable<SeriesStats>
        {
            public readonly long PathUnits;
            public readonly int SeamX;
            public readonly int SeamY;
            public readonly long Blocked;
            public readonly long Slides;

            public SeriesStats(long pathUnits, int seamX, int seamY, long blocked, long slides)
            {
                PathUnits = pathUnits;
                SeamX = seamX;
                SeamY = seamY;
                Blocked = blocked;
                Slides = slides;
            }

            public bool Equals(SeriesStats o) =>
                PathUnits == o.PathUnits && SeamX == o.SeamX && SeamY == o.SeamY && Blocked == o.Blocked && Slides == o.Slides;

            public override bool Equals(object? obj) => obj is SeriesStats o && Equals(o);
            public override int GetHashCode() => (int)PathUnits ^ (SeamX << 16) ^ SeamY;
            public override string ToString() => $"путь {PathUnits / U} т, шов X {SeamX}, Y {SeamY}, упоров {Blocked}, скольжений {Slides}";
        }

        /// <summary>
        /// Серия из <paramref name="ticks"/> тактов с детерминированным вводом на проходимой карте
        /// <see cref="Maze"/>: старт на проспекте у шва тора, направление (8 + стойка) держится
        /// 10…60 тактов (упёрся намертво — сразу новое). По ходу проверяет: позиция внутри мира, круг ни в один твёрдый тайл
        /// не вошёл, по каждой оси шаг между стартом и целью (назад не уводит). Пишет траекторию
        /// побайтно и считает путь, пересечения шва, упоры и скольжения.
        /// </summary>
        private static byte[] Simulate(MapShape map, ulong worldSeed, int series, int ticks, out SeriesStats stats)
        {
            var rng = DetRandom.ForSystem(worldSeed, series);
            var mover = new Mover(StartNearSeam(map, ref rng));
            Assert.False(Collision.TouchesSolid(map, mover.Position, Maze), $"серия {series}: старт в стене");
            Input8 input = Input8.None;
            int hold = 0;
            long path = 0;
            int seamX = 0;
            int seamY = 0;
            long blocked = 0;
            long slides = 0;

            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream))
                {
                    for (int tick = 0; tick < ticks; tick++)
                    {
                        if (hold == 0)
                        {
                            input = Input8.FromIndex(rng.NextInt(Input8.Count + 1)); // 8 направлений + стойка
                            hold = rng.NextInt(10, 61);
                        }

                        hold--;
                        WorldPos prev = mover.Position;
                        int wantX = (mover.Carry.X + input.SpeedX10(mover.UnitsPerTickX10)) / MovementTick.SpeedScale;
                        int wantY = (mover.Carry.Y + input.SpeedY10(mover.UnitsPerTickX10)) / MovementTick.SpeedScale;
                        WorldPos pos = mover.Step(map, input, Maze);
                        Int2 d = Torus.Delta(map, prev, pos);

                        Assert.InRange(pos.X, 0, map.WidthUnits - 1);
                        Assert.InRange(pos.Y, 0, map.HeightUnits - 1);
                        if (Collision.TouchesSolid(map, pos, Maze))
                        {
                            Assert.Fail($"серия {series}, такт {tick}: круг в стене в {pos}");
                        }

                        if (d.X < Math.Min(0, wantX) || d.X > Math.Max(0, wantX) || d.Y < Math.Min(0, wantY) || d.Y > Math.Max(0, wantY))
                        {
                            Assert.Fail($"серия {series}, такт {tick}: шаг {d} вне отрезка (0, 0) → ({wantX}, {wantY})");
                        }

                        path += Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
                        seamX += prev.X + d.X != pos.X ? 1 : 0; // разность по тору не равна сырой — прошли шов
                        seamY += prev.Y + d.Y != pos.Y ? 1 : 0;
                        if (input.IsMoving && d.X == 0 && d.Y == 0)
                        {
                            hold = 0; // упёрся намертво — ввод «игрока» сменит направление на следующем такте
                        }

                        if (d.X != wantX || d.Y != wantY)
                        {
                            blocked++;
                            if (input.IsDiagonal && (d.X != 0) != (d.Y != 0))
                            {
                                slides++; // одна ось упёрта, другая идёт: скольжение вдоль стены
                            }
                        }

                        writer.Write(pos.X);
                        writer.Write(pos.Y);
                    }
                }

                stats = new SeriesStats(path, seamX, seamY, blocked, slides);
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Старт у шва тора: проспект y ∈ {0, 1} (в 128…384 unit от шва по Y), x — в 24 тайлах
        /// от шва по X. Проспекты <see cref="Maze"/> свободны всегда.
        /// </summary>
        private static WorldPos StartNearSeam(MapShape map, ref DetRandom rng)
        {
            int tx = (rng.NextInt(48) - 24) & (map.Width - 1);
            int ty = rng.NextInt(2);
            return Center(tx, ty);
        }
    }
}
