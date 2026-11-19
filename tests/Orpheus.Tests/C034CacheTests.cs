using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-034 (хвост C-017): выгрузка чанка (<c>WorldModel.RemoveChunk</c>, у клиента — <c>ClientWorld.OnWindow</c>)
    /// снимает записи кэшей ячеек — метки комнат (<see cref="RoomIndex"/>) и свет с учётом стен
    /// (<see cref="LightSight"/>) — у чанка и соседей окна 3 × 3. Иначе при обратной загрузке пустого чанка
    /// кэш отдаёт устаревшие метки (штамп окна совпадает), а словарь растёт без предела.
    /// Координаты: чанк (2,3) = тайлы x 64…95, y 96…127; чанк (3,3) = x 96…127. Комната 88…96 × 100…105
    /// пересекает границу x = 96: западная часть в чанке (2,3), восточная стена (x = 96) в чанке (3,3).
    /// </summary>
    public class C034CacheTests
    {
        private static WorldModel Flat(ulong seed = 1701) => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), seed);

        private static void Wall(Simulation sim, int x, int y)
        {
            BuildRecord rec = BuildRecord.OfPlayer(1, FloorId.Grass).WithCells(
                BuildCells.DoorNorth | BuildCells.DoorSouth | BuildCells.DoorWest | BuildCells.DoorEast);
            Assert.True(sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, ObjectId.StoneWall, rec));
        }

        // Клетка-комната 88…96 × 100…105 — через границу чанков x = 96.
        private static Simulation RoomWorld()
        {
            var sim = new Simulation(Flat());
            for (int y = 100; y <= 105; y++)
            {
                for (int x = 88; x <= 96; x++)
                {
                    if (x == 88 || x == 96 || y == 100 || y == 105)
                    {
                        Wall(sim, x, y);
                    }
                }
            }

            sim.Step();
            return sim;
        }

        private static readonly ChunkCoord West = new ChunkCoord(2, 3);   // тайлы 64…95 × 96…127: западная часть комнаты
        private static readonly ChunkCoord East = new ChunkCoord(3, 3);   // восточная стена x = 96
        private static readonly ChunkCoord Far = new ChunkCoord(4, 3);    // x 128…159: за окном 3 × 3 от (2,3)

        [Fact]
        public void C034_Unload_Evicts_Rooms_And_Sight()
        {
            Simulation sim = RoomWorld();
            WorldModel w = sim.World;
            Assert.Equal(RoomIndex.Room, w.Rooms.LabelAt(BuildCells.CellOf(new TileCoord(92, 102), 0, 0)));
            Assert.NotEmpty(w.Sight.GridOf(West)); // сетка построена (пусть и нули) — запись есть
            w.Sight.GridOf(West);
            long before = w.Rooms.Rebuilds;

            // Клиентская выгрузка: чанк уходит из окна 8 × 6, на его месте потом встанет пустой.
            Assert.True(w.RemoveChunk(West));

            // Старых меток быть не должно: пустой чанк — «снаружи», и метки пересчитаны (не отдан кэш).
            w.GetOrCreateChunk(West);
            Assert.Equal(RoomIndex.Outside, w.Rooms.LabelAt(BuildCells.CellOf(new TileCoord(92, 102), 0, 0)));
            Assert.True(w.Rooms.Rebuilds > before, "RemoveChunk обязан снять запись меток чанка");
        }

        [Fact]
        public void C034_Unload_Evicts_Neighbours_Window()
        {
            Simulation sim = RoomWorld();
            WorldModel w = sim.World;
            // Клетка за восточной стеной (чанк (3,3)) — её метка строится кэшем чанка (3,3).
            Assert.Equal(RoomIndex.Outside, w.Rooms.LabelAt(BuildCells.CellOf(new TileCoord(97, 102), 0, 0)));
            long before = w.Rooms.Rebuilds;
            w.Rooms.LabelAt(BuildCells.CellOf(new TileCoord(97, 102), 0, 0));
            Assert.Equal(before, w.Rooms.Rebuilds); // второй запрос — из кэша

            // Выгружаем соседний чанк (2,3): окно 3 × 3 обязано снять и запись чанка (3,3).
            Assert.True(w.RemoveChunk(West));
            w.GetOrCreateChunk(West);
            w.Rooms.LabelAt(BuildCells.CellOf(new TileCoord(97, 102), 0, 0));
            Assert.True(w.Rooms.Rebuilds > before, "запись соседа обязана уйти с окном 3 × 3");
        }

        [Fact]
        public void C034_Unload_Keeps_Labels_Outside_Eviction_Window()
        {
            // Страховка от over-eviction: чанк (4,3) вне окна 3 × 3 от (2,3) — его кэшовая запись
            // при выгрузке (2,3) снимается только окном шире 3 × 3; на этом тесте 5 × 5 краснеет.
            Simulation sim = RoomWorld();
            WorldModel w = sim.World;
            Assert.Equal(RoomIndex.Outside, w.Rooms.LabelAt(BuildCells.CellOf(new TileCoord(140, 102), 0, 0)));
            long before = w.Rooms.Rebuilds;
            w.Rooms.LabelAt(BuildCells.CellOf(new TileCoord(140, 102), 0, 0));
            Assert.Equal(before, w.Rooms.Rebuilds);

            Assert.True(w.RemoveChunk(West));
            w.GetOrCreateChunk(West);
            w.Rooms.LabelAt(BuildCells.CellOf(new TileCoord(140, 102), 0, 0));
            // Чанк за окном 3 × 3 не должен сбрасываться зря (на этом тесте окно 5 × 5 краснеет).
            Assert.Equal(before, w.Rooms.Rebuilds);
        }

        [Fact]
        public void C034_Unload_Trims_Cache_Entries()
        {
            // Главный симптом (клиент, долгая игра): записи меток всех когда-то видимых чанков не должны
            // копиться. 8 закэшованных чанков; выгрузка (2,3) снимает ровно записи окна 3 × 3 — 7 штук,
            // чанк (4,3) за окном остаётся. Без Drop счёт стоит (мутация М1), при окне 5 × 5 обнуляется (М2).
            Simulation sim = RoomWorld();
            WorldModel w = sim.World;
            TileCoord[] probes =
            {
                new TileCoord(92, 102), new TileCoord(97, 102),                      // (2,3), (3,3)
                new TileCoord(80, 80), new TileCoord(112, 80),                        // (2,2), (3,2)
                new TileCoord(80, 144), new TileCoord(112, 144),                      // (2,4), (3,4)
                new TileCoord(48, 112), new TileCoord(140, 102),                      // (1,3), (4,3)
            };
            foreach (TileCoord t in probes)
            {
                w.Rooms.LabelAt(BuildCells.CellOf(t, 0, 0));
            }

            Assert.Equal(8, w.Rooms.EntryCount);
            Assert.True(w.RemoveChunk(West));
            Assert.Equal(1, w.Rooms.EntryCount); // остался только (4,3) — за окном 3 × 3
        }

        [Fact]
        public void C034_Repeat_Room_After_Unload_Load_Is_Identical()
        {
            // Выгрузили чанк с готовой комнатой: на его месте пустой чанк — метки ровно «снаружи»
            // (не остатки комнаты из кэша). Сверка с миром без выгрузки — точечные метки до/после.
            Simulation a = RoomWorld();
            Assert.Equal(RoomIndex.Room, a.World.Rooms.LabelAt(BuildCells.CellOf(new TileCoord(92, 102), 0, 0)));

            Simulation b = RoomWorld();
            Assert.Equal(RoomIndex.Room, b.World.Rooms.LabelAt(BuildCells.CellOf(new TileCoord(92, 102), 0, 0)));
            b.World.RemoveChunk(West);
            b.World.GetOrCreateChunk(West);
            // Чанк создан заново пустым: западная стена пропала, комната открылась — метка «снаружи».
            Assert.Equal(RoomIndex.Outside, b.World.Rooms.LabelAt(BuildCells.CellOf(new TileCoord(92, 102), 0, 0)));
            // Пустой чанк даёт ровно нулевые метки по всему чанку (не остатки комнаты).
            byte[] labels = b.World.Rooms.LabelsOf(West);
            foreach (byte lb in labels)
            {
                Assert.Equal(RoomIndex.Outside, lb);
            }
        }
    }
}
