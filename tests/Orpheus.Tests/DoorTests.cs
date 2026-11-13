using System.Linq;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Двери (1-012; docs/05, 1.3, docs/02, 2.2): автооткрытие шагом к двери, автозакрытие через 30 тактов
    /// свободы, F (защёлка переднего фронта) и отказ в проёме, выталкивание врага при закрытии,
    /// проход врага через открытую и блок закрытой, индекс дверей и его восстановление из мира.
    /// Базовый бег — 51,2 unit/такт (MovementTick.BaseSpeedX10 = 512).
    /// </summary>
    public class DoorTests
    {
        private const int U = 1 << Torus.SubTileBits;

        private static WorldPos TileCenter(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private static WorldModel Flat(int size = 1024) => FlatWorldGenerator.Instance.Generate(MapShape.World(size), 1);

        private static void PlaceDoor(Simulation sim, int x, int y, bool open = false)
        {
            BuildRecord rec = BuildRecord.OfPlayer(1, FloorId.Grass);
            if (open)
            {
                rec = rec.WithFlags(BuildFlags.DoorOpen);
            }

            sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, ObjectId.Door, rec);
            sim.Step();
            Assert.Equal(ObjectId.Door, sim.World.GetObject(new TileCoord(x, y)));
        }

        private static bool IsOpen(Simulation sim, int x, int y)
        {
            Assert.True(sim.World.TryGetBuild(new TileCoord(x, y), out BuildRecord rec), "дверь без записи");
            return rec.IsDoorOpen;
        }

        private static TileCoord EntityTile(Simulation sim, int id) =>
            Torus.TileOf(sim.World.Shape, sim.Entities[sim.SlotOf(id)].Mover.Position);

        /// <summary>
        /// Автооткрытие и автозакрытие (1-012): бег к двери открывает её, когда персонаж соседний тайл и
        /// движется к проёму; проход сквозь открытую не блокирует; через 30 тактов свободы тайла и соседей —
        /// автозакрытие; шаг обратно открывает снова; F закрывает, замок 30 тактов не даёт открыться от стояния.
        /// </summary>
        [Fact]
        public void D12_AutoOpen_AutoClose_And_F()
        {
            var sim = new Simulation(Flat());
            PlaceDoor(sim, 110, 100);
            int me = sim.AddEntity(TileCenter(100, 100));
            Assert.Equal(1, sim.DoorCount);
            Assert.False(IsOpen(sim, 110, 100));

            sim.SetInput(me, new InputFrame(Input8.East));
            int openedTick = -1;
            for (int t = 0; t < 60 && openedTick < 0; t++)
            {
                sim.Step();
                if (IsOpen(sim, 110, 100))
                {
                    openedTick = (int)sim.Tick;
                }
            }

            Assert.True(openedTick >= 0, "дверь не открылась подбеганием");
            Assert.True(EntityTile(sim, me).X <= 110, "открылась до прихода");

            // Проход сквозь открытую дверь — без остановки (51,2 unit/такт: проём 256 unit ≈ 5 тактов).
            for (int t = 0; t < 15; t++)
            {
                sim.Step();
            }

            Assert.True(EntityTile(sim, me).X >= 111, "застряла на проходе");

            // Автозакрытие: игрок остановился вдали (chebyshev ≥ 2) — 30 тактов свободы, и дверь закрыта.
            sim.SetInput(me, InputFrame.None);
            int closedTick = -1;
            for (int t = 0; t < 80 && closedTick < 0; t++)
            {
                sim.Step();
                if (!IsOpen(sim, 110, 100))
                {
                    closedTick = (int)sim.Tick;
                }
            }

            Assert.True(closedTick >= 0, "не закрылась");

            // Шаг обратно открывает; F закрывает; в замке стояние рядом не открывает.
            sim.SetInput(me, new InputFrame(Input8.West));
            for (int t = 0; t < 80 && !IsOpen(sim, 110, 100); t++)
            {
                sim.Step();
            }

            Assert.True(IsOpen(sim, 110, 100), "шаг к двери не открыл");
            sim.SetInput(me, InputFrame.None); // встаём у двери
            for (int t = 0; t < 2; t++)
            {
                sim.Step();
            }

            sim.SetInput(me, new InputFrame(Input8.None, InputButtons.CloseDoor));
            sim.Step();
            sim.SetInput(me, InputFrame.None);
            sim.Step();
            Assert.False(IsOpen(sim, 110, 100), "F не закрыла");

            for (int t = 0; t < Simulation.DoorLockTicks - 1; t++)
            {
                sim.Step();
            }

            Assert.False(IsOpen(sim, 110, 100), "открылась в замке F");
        }

        /// <summary>F в проёме — отказ: персонаж на тайле открытой двери не закрывает её собой.</summary>
        [Fact]
        public void D12_F_InDoorway_Refused()
        {
            var sim = new Simulation(Flat());
            PlaceDoor(sim, 110, 100);
            int me = sim.AddEntity(TileCenter(110, 100));
            sim.Step(); // страховка автооткрытия: персонаж на тайле
            Assert.True(IsOpen(sim, 110, 100));

            sim.SetInput(me, new InputFrame(Input8.None, InputButtons.CloseDoor));
            sim.Step();
            sim.SetInput(me, InputFrame.None);
            sim.Step();
            Assert.True(IsOpen(sim, 110, 100), "F в проёме закрыла дверь под собой");
        }

        /// <summary>F вне радиуса 1 тайл — отказ (дальняя дверь не закрывается).</summary>
        [Fact]
        public void D12_F_Out_Of_Range_Refused()
        {
            var sim = new Simulation(Flat());
            PlaceDoor(sim, 110, 100, open: true);
            int me = sim.AddEntity(TileCenter(107, 100)); // chebyshev 3
            sim.SetInput(me, new InputFrame(Input8.None, InputButtons.CloseDoor));
            sim.Step();
            Assert.True(IsOpen(sim, 110, 100), "закрылась издалека");
        }

        /// <summary>F рядом с врагом на тайле: враг выталкивается в первый свободный сосед (E,S,W,N), дверь закрыта.</summary>
        [Fact]
        public void D12_F_Pushes_Enemy_Off_Door()
        {
            var sim = new Simulation(Flat());
            PlaceDoor(sim, 110, 100, open: true);
            int enemy = sim.AddEnemy(TileCenter(110, 100));
            int me = sim.AddEntity(TileCenter(110, 101)); // соседний тайл, вне проёма
            Assert.Equal(new TileCoord(110, 100), EntityTile(sim, enemy));

            sim.SetInput(me, new InputFrame(Input8.None, InputButtons.CloseDoor));
            sim.Step();
            sim.SetInput(me, InputFrame.None);
            sim.Step();

            Assert.False(IsOpen(sim, 110, 100), "F не закрыла");
            Assert.Equal(new TileCoord(111, 100), EntityTile(sim, enemy)); // вытолкнут на E
        }

        /// <summary>Открытая дверь проходима врагом (движение по такту), закрытая — блок; враг дверь не открывает.</summary>
        [Fact]
        public void D12_Enemy_Passes_Open_Blocked_By_Closed()
        {
            var sim = new Simulation(Flat());
            PlaceDoor(sim, 110, 100, open: true);
            int enemy = sim.AddEnemy(TileCenter(108, 100));
            sim.SetInput(enemy, new InputFrame(Input8.East));
            for (int t = 0; t < 30; t++)
            {
                sim.Step();
            }

            Assert.True(EntityTile(sim, enemy).X >= 111, "застрял на открытой двери");

            // Закрытая: тот же заход — блок на подходе, за 60 тактов враг дверь не открывает.
            var sim2 = new Simulation(Flat());
            PlaceDoor(sim2, 110, 100);
            int enemy2 = sim2.AddEnemy(TileCenter(108, 100));
            sim2.SetInput(enemy2, new InputFrame(Input8.East));
            for (int t = 0; t < 60; t++)
            {
                sim2.Step();
            }

            Assert.True(EntityTile(sim2, enemy2).X <= 109, "прошёл закрытую");
            Assert.False(IsOpen(sim2, 110, 100), "враг открыл дверь");
        }

        /// <summary>Открытая дверь не закрывается, пока игрок стоит в соседнем тайле (закрываться — «упираться в нос»).</summary>
        [Fact]
        public void D12_Stays_Open_While_Player_Neighbor()
        {
            var sim = new Simulation(Flat());
            PlaceDoor(sim, 110, 100, open: true);
            int me = sim.AddEntity(TileCenter(109, 100)); // соседний тайл, не проём; стоит (ввода нет)
            for (int t = 0; t < 3 * Simulation.DoorAutoCloseTicks; t++)
            {
                sim.Step();
            }

            Assert.True(IsOpen(sim, 110, 100), "закрылась при игроке в соседнем тайле");
        }

        /// <summary>Индекс дверей: регистрация при постановке, снос снимает (лениво), новая Simulation строит индекс из мира.</summary>
        [Fact]
        public void D12_Door_Index_Registers_Removes_Reindexes()
        {
            WorldModel world = Flat();
            var sim = new Simulation(world);
            PlaceDoor(sim, 110, 100);
            PlaceDoor(sim, 112, 100);
            Assert.Equal(2, sim.DoorCount);

            sim.RequestEdit(new TileCoord(110, 100), FloorId.Grass, ObjectId.Empty);
            sim.Step();
            sim.Step(); // ленивое снятие в StepDoors
            Assert.Equal(1, sim.DoorCount);

            var reloaded = new Simulation(world);
            Assert.Equal(1, reloaded.DoorCount);
        }

        /// <summary>F по закрытой двери (1-028): после истечения замка F продлевает его — автооткрытие шагом
        /// молчит всё время продления; прекращаем F — истёкший замок открывается шагом (вечной блокировки нет).</summary>
        [Fact]
        public void D12_F_On_Closed_Door_Extends_Lock()
        {
            var sim = new Simulation(Flat());
            PlaceDoor(sim, 110, 100, open: true);
            int me = sim.AddEntity(TileCenter(110, 101));
            sim.Step();
            sim.SetInput(me, new InputFrame(Input8.None, InputButtons.CloseDoor));
            sim.Step();
            sim.SetInput(me, InputFrame.None);
            sim.Step();
            Assert.False(IsOpen(sim, 110, 100), "F не закрыла");
            Assert.Equal(sim.Tick + Simulation.DoorLockTicks, sim.DoorLockUntil(0));

            // Замок первого F истекает; жмём F каждый кадр шагая к двери — продление, автооткрытие молчит.
            for (int t = 0; t < Simulation.DoorLockTicks - 1; t++)
            {
                sim.Step();
            }

            sim.SetInput(me, new InputFrame(Input8.North, InputButtons.CloseDoor));
            for (int t = 0; t < 20; t++)
            {
                sim.Step();
                if (t == 1)
                {
                    // Каждый F-такт вне проёма — продление: замок = такт продления + 1 + DoorLockTicks
                    // (после Step sim.Tick на один такт позже такта правки).
                    Assert.Equal(sim.Tick + Simulation.DoorLockTicks, sim.DoorLockUntil(0));
                }

                sim.SetInput(me, new InputFrame(Input8.North, InputButtons.CloseDoor));
            }

            Assert.False(IsOpen(sim, 110, 100), "открылась в продлённом замке");

            // F убрали — замок (последнее продление) истекает, шаг к двери открывает её.
            sim.SetInput(me, new InputFrame(Input8.North));
            for (int t = 0; t < Simulation.DoorLockTicks + 10; t++)
            {
                sim.Step();
            }

            Assert.True(IsOpen(sim, 110, 100), "не открылась шагом после истечения замка");
        }
    }
}
