using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-017: закрытые комнаты на сетке ячеек 0,5 (<see cref="RoomIndex"/>) — замкнута / дыра / открытая дверь / забор /
    /// больше 32 тайлов / через границу чанка / кэш по ревизиям; правило появления врагов «в комнате с огнём — нет»;
    /// свет для картинки с учётом стен (<see cref="LightSight"/>).
    /// </summary>
    public class C017RoomTests
    {
        private static WorldModel Flat(ulong seed = 1701) => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), seed);

        private static void Cells(Simulation sim, int x, int y, ushort obj, byte mask, bool open = false)
        {
            BuildRecord rec = BuildRecord.OfPlayer(1, FloorId.Grass).WithCells(mask);
            if (open)
            {
                rec = rec.WithFlags(BuildFlags.DoorOpen | BuildFlags.DoorKeepOpen);
            }

            Assert.True(sim.RequestEdit(new TileCoord(x, y), FloorId.Grass, obj, rec));
        }

        /// <summary>
        /// Комната из тонкой стены по внешним половинам тайлов x0…x1 × y0…y1. Дыра (hole) — тайл северной стены (x0 + 2, y0)
        /// пропущен; door = 1 — закрытая дверь на западе (x0, y0 + 2), 2 — открытая; fence — забор вместо стены.
        /// </summary>
        private static void Room(Simulation sim, int x0, int y0, int x1, int y1, bool hole = false, int door = 0, bool fence = false)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    byte m = 0;
                    m |= y == y0 ? BuildCells.DoorNorth : (byte)0;
                    m |= y == y1 ? BuildCells.DoorSouth : (byte)0;
                    m |= x == x0 ? BuildCells.DoorWest : (byte)0;
                    m |= x == x1 ? BuildCells.DoorEast : (byte)0;
                    if (m == 0 || (hole && x == x0 + 2 && y == y0))
                    {
                        continue;
                    }

                    if (door != 0 && x == x0 && y == y0 + 2)
                    {
                        Cells(sim, x, y, ObjectId.Door, BuildCells.DoorWest, door == 2);
                    }
                    else
                    {
                        Cells(sim, x, y, fence ? ObjectId.WoodFence : ObjectId.StoneWall, m);
                    }
                }
            }

            sim.Step();
        }

        private static byte Label(WorldModel w, int tx, int ty, int qx = 0, int qy = 0) =>
            w.Rooms.LabelAt(BuildCells.CellOf(new TileCoord(tx, ty), qx, qy));

        [Fact]
        public void R01_Closed_Room_And_Outside()
        {
            var sim = new Simulation(Flat());
            Room(sim, 100, 100, 106, 105);
            WorldModel w = sim.World;
            Assert.Equal(RoomIndex.Room, Label(w, 103, 103));
            Assert.Equal(RoomIndex.Room, Label(w, 100, 102, 1, 0)); // внутренняя половина тайла стены
            Assert.Equal(RoomIndex.Outside, Label(w, 100, 102, 0, 0)); // сама стена
            Assert.Equal(RoomIndex.Outside, Label(w, 99, 102));
            Assert.Equal(RoomIndex.Outside, Label(w, 103, 107));
            Assert.False(w.Rooms.LitRoomInTile(new TileCoord(103, 103)));
        }

        [Fact]
        public void R02_Hole_Opens_Room()
        {
            var sim = new Simulation(Flat());
            Room(sim, 100, 100, 106, 105, hole: true);
            Assert.Equal(RoomIndex.Outside, Label(sim.World, 103, 103));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void R03_Door_Closed_Or_Open_Keeps_Room(int door)
        {
            var sim = new Simulation(Flat());
            Room(sim, 100, 100, 106, 105, door: door);
            Assert.Equal(RoomIndex.Room, Label(sim.World, 103, 103));
        }

        [Fact]
        public void R04_Fence_Is_Not_A_Wall()
        {
            var sim = new Simulation(Flat());
            Room(sim, 100, 100, 106, 105, fence: true);
            Assert.Equal(RoomIndex.Outside, Label(sim.World, 103, 103));
        }

        [Fact]
        public void R05_Torch_Makes_Lit_Room()
        {
            var sim = new Simulation(Flat());
            Room(sim, 100, 100, 106, 105);
            Assert.True(sim.RequestEdit(new TileCoord(101, 101), FloorId.Grass, ObjectId.Torch));
            sim.Step();
            Assert.Equal(RoomIndex.LitRoom, Label(sim.World, 105, 104));
            Assert.True(sim.World.Rooms.LitRoomInTile(new TileCoord(105, 104)));
            Assert.False(sim.World.Rooms.LitRoomInTile(new TileCoord(110, 104)));
        }

        [Theory]
        [InlineData(33, true)]  // стены по внешним половинам: внутри 2 × 33 − 2 = 64 ячейки — комната
        [InlineData(34, false)] // 66 ячеек — шире 64: открытое место
        public void R06_Size_Limit_32_Tiles(int tiles, bool room)
        {
            var sim = new Simulation(Flat());
            Room(sim, 200, 200, 200 + tiles - 1, 210);
            Assert.Equal(room ? RoomIndex.Room : RoomIndex.Outside, Label(sim.World, 205, 205));
        }

        [Fact]
        public void R07_Across_Chunk_Border_And_Cache()
        {
            // Комната 60…70 × 60…66 лежит в двух чанках (граница x = 64).
            var sim = new Simulation(Flat());
            Room(sim, 60, 60, 70, 66);
            WorldModel w = sim.World;
            Assert.Equal(RoomIndex.Room, Label(w, 62, 63));
            Assert.Equal(RoomIndex.Room, Label(w, 68, 63));

            long before = w.Rooms.Rebuilds;
            Assert.Equal(RoomIndex.Room, Label(w, 61, 62));
            Assert.Equal(before, w.Rooms.Rebuilds); // тот же чанк, те же ревизии — без пересчёта

            // Правка далеко (вне окна 3 × 3) — без пересчёта.
            Assert.True(sim.RequestEdit(new TileCoord(400, 400), FloorId.Grass, ObjectId.Torch));
            sim.Step();
            Assert.Equal(RoomIndex.Room, Label(w, 62, 63));
            Assert.Equal(before, w.Rooms.Rebuilds);

            // Стену в восточном чанке сломали — комната в западном чанке тоже открылась.
            Assert.True(sim.RequestEdit(new TileCoord(70, 63), FloorId.Grass, ObjectId.Empty));
            sim.Step();
            Assert.Equal(RoomIndex.Outside, Label(w, 62, 63));
            Assert.True(w.Rooms.Rebuilds > before);
        }

        [Fact]
        public void R08_Diagonal_Wall_Closes()
        {
            // Ромб из ячеек, касающихся углами: 4-соседство не проходит по диагонали.
            var sim = new Simulation(Flat());
            int c = 300;
            for (int i = 0; i < 4; i++)
            {
                // ячейки (2c + 4 + i, 2c + i) … — квадрат, повёрнутый на 45°, из одиночных ячеек
                PutCell(sim, 2 * c + 4 + i, 2 * c + i);
                PutCell(sim, 2 * c + 8 - i, 2 * c + 4 + i);
                PutCell(sim, 2 * c + 4 - i, 2 * c + 8 - i);
                PutCell(sim, 2 * c + i, 2 * c + 4 - i);
            }

            sim.Step();
            Assert.Equal(RoomIndex.Room, sim.World.Rooms.LabelAt(new TileCoord(2 * c + 4, 2 * c + 4)));
            Assert.Equal(RoomIndex.Outside, sim.World.Rooms.LabelAt(new TileCoord(2 * c - 2, 2 * c + 4)));
        }

        // Одна ячейка каменной стены (накапливает маску тайла).
        private static void PutCell(Simulation sim, int cx, int cy)
        {
            var tile = new TileCoord(cx >> 1, cy >> 1);
            byte mask = BuildCells.Bit(cx & 1, cy & 1);
            if (sim.World.GetObject(tile) == ObjectId.StoneWall && sim.World.TryGetBuild(tile, out BuildRecord had))
            {
                mask |= had.Cells;
            }

            sim.World.SetBaseTile(tile, FloorId.Grass, ObjectId.StoneWall);
            TileCoord t = Torus.Wrap(sim.World.Shape, tile);
            sim.World.FindChunk(Torus.ChunkOf(sim.World.Shape, t))!.SetBaseBuild(Chunk.IndexOf(t.X, t.Y), BuildRecord.OfPlayer(1, FloorId.Grass).WithCells(mask));
        }

        // ---------- Появление врагов ----------

        /// <summary>
        /// Комната 20 × 20 с открытой дверью: старое правило (заливка тайлов до 256) её замкнутой не считает — появление
        /// можно; с факелом в дальнем углу — нельзя (C-017), хотя свет факела до тайла не достаёт (LightIndex — по-старому).
        /// </summary>
        [Fact]
        public void S01_No_Spawn_In_Lit_Room()
        {
            var sim = new Simulation(Flat());
            Room(sim, 700, 700, 719, 719, door: 2);
            var spot = new TileCoord(710, 710);
            Assert.True(sim.IsSpawnTileOk(spot));

            Assert.True(sim.RequestEdit(new TileCoord(701, 701), FloorId.Grass, ObjectId.Torch));
            sim.Step();
            Assert.Equal(0, sim.World.Lights.SourceLightX1024(spot)); // свет ядра без стен — не достаёт
            Assert.False(sim.IsSpawnTileOk(spot));
            Assert.True(sim.IsSpawnTileOk(new TileCoord(730, 710))); // снаружи — можно

            // Стену разобрали (дыра) — комнаты нет, появление снова можно.
            Assert.True(sim.RequestEdit(new TileCoord(710, 700), FloorId.Grass, ObjectId.Empty));
            sim.Step();
            Assert.True(sim.IsSpawnTileOk(spot));
        }

        /// <summary>
        /// Правило «в комнате с факелом враги не появляются» проверяет ВСЮ комнату (ячейки 0,5 тайла), а не одну четверть
        /// тайла (<see cref="RoomIndex.LitRoomInTile"/>): большая комната 30 × 30 (стены по внешним половинам, внутри
        /// 58 × 58 ячеек — меньше предела 64) с факелом в дальнем юго-западном углу. Свет ядра (<see cref="LightIndex"/>:
        /// факел 3 / 6 тайлов) до противоположного угла не достаёт, зато метка LitRoom у комнаты общая — появление запрещено
        /// по всей комнате, включая противоположный угол. Снаружи — можно.
        /// </summary>
        [Fact]
        public void S02_Lit_Room_Rule_Covers_Whole_Room()
        {
            var sim = new Simulation(Flat());
            Room(sim, 700, 700, 729, 729, door: 2); // в стороне от точки старта (правило ≥ 16 тайлов)
            var near = new TileCoord(701, 701);  // северо-западный угол комнаты
            var far = new TileCoord(728, 728);   // противоположный (юго-восточный) угол той же комнаты
            Assert.True(sim.IsSpawnTileOk(near));
            Assert.True(sim.IsSpawnTileOk(far));

            Assert.True(sim.RequestEdit(new TileCoord(701, 728), FloorId.Grass, ObjectId.Torch));
            sim.Step();
            WorldModel w = sim.World;
            // Факел в одном (юго-западном) углу: свет ядра до дальнего угла не доходит, а комната с огнём — вся.
            Assert.Equal(0, w.Lights.SourceLightX1024(far));
            Assert.Equal(RoomIndex.LitRoom, Label(w, 728, 728, 0, 0));
            Assert.Equal(RoomIndex.LitRoom, Label(w, 728, 728, 1, 1));
            Assert.True(w.Rooms.LitRoomInTile(new TileCoord(715, 715))); // середина комнаты — тоже «комната с огнём»
            Assert.False(sim.IsSpawnTileOk(far));
            Assert.False(sim.IsSpawnTileOk(near));
            Assert.True(sim.IsSpawnTileOk(new TileCoord(740, 715))); // снаружи комнаты — можно
        }

        // ---------- Свет для картинки ----------

        [Fact]
        public void L01_Wall_Stops_Light_Open_Door_Passes()
        {
            var sim = new Simulation(Flat());
            Room(sim, 100, 100, 106, 105, door: 1);
            Assert.True(sim.RequestEdit(new TileCoord(101, 102), FloorId.Grass, ObjectId.Torch));
            sim.Step();
            WorldModel w = sim.World;
            ushort[] grid = w.Sight.GridOf(Torus.ChunkOf(w.Shape, new TileCoord(101, 102)));
            int At(int cx, int cy) => grid[(cy & 63) * 64 + (cx & 63)];

            Assert.Equal(1024, At(2 * 102, 2 * 102)); // внутри рядом
            Assert.Equal(0, At(2 * 98 + 1, 2 * 102)); // за западной стеной (2 тайла от факела) — темно
            Assert.Equal(0, At(2 * 99 + 1, 2 * 103)); // за стеной по диагонали — темно
            Assert.Equal(0, w.Sight.Strongest(Torus.ChunkOf(w.Shape, new TileCoord(99, 102)), 99 * 4 + 2, 102 * 4 + 2, new LightSight.Lit[3]));
            // Свет ядра (враги) стен не видит.
            Assert.True(w.Lights.SourceLightX1024(new TileCoord(99, 102)) > 0);

            // Дверь открыли — свет проходит в проём.
            TileCoord door = new TileCoord(100, 102);
            Assert.True(w.TryGetBuild(door, out BuildRecord rec));
            Assert.True(sim.RequestEdit(door, FloorId.Grass, ObjectId.Door, rec.WithFlags(BuildFlags.DoorOpen | BuildFlags.DoorKeepOpen)));
            sim.Step();
            grid = w.Sight.GridOf(Torus.ChunkOf(w.Shape, new TileCoord(101, 102)));
            Assert.True(At(2 * 99 + 1, 2 * 102) > 0); // за открытой дверью — светло
            Assert.Equal(RoomIndex.LitRoom, Label(w, 103, 103)); // а комната с факелом — по-прежнему комната
        }

        /// <summary>Тень от огня не длиннее пути до стены: от середины тайла 102 на запад до ячейки стены 200 — 8 четвертей.</summary>
        [Fact]
        public void L03_FreeRun_Stops_At_Wall()
        {
            var sim = new Simulation(Flat());
            Room(sim, 100, 100, 106, 105);
            Assert.True(sim.RequestEdit(new TileCoord(104, 104), FloorId.Grass, ObjectId.Torch));
            sim.Step();
            WorldModel w = sim.World;
            ChunkCoord c = Torus.ChunkOf(w.Shape, new TileCoord(102, 103));
            Assert.Equal(8, w.Sight.FreeRun4(c, 102 * 4 + 2, 103 * 4 + 2, -4, 0, 40));
            Assert.Equal(40, w.Sight.FreeRun4(c, 102 * 4 + 2, 103 * 4 + 2, 0, 0, 40));
            Assert.Equal(6, w.Sight.FreeRun4(c, 102 * 4 + 2, 103 * 4 + 2, 1, 0, 6)); // до стены дальше — сколько просили
        }

        [Fact]
        public void L02_Strongest_Orders_And_Directions()
        {
            var sim = new Simulation(Flat());
            Assert.True(sim.RequestEdit(new TileCoord(500, 500), FloorId.Grass, ObjectId.Torch));
            Assert.True(sim.RequestEdit(new TileCoord(507, 500), FloorId.Grass, ObjectId.Campfire));
            sim.Step();
            WorldModel w = sim.World;
            var top = new LightSight.Lit[3];
            int n = w.Sight.Strongest(Torus.ChunkOf(w.Shape, new TileCoord(504, 500)), 504 * 4 + 2, 500 * 4 + 2, top);
            Assert.Equal(2, n);
            Assert.True(top[0].X1024 >= top[1].X1024);
            Assert.Equal(1024, top[0].X1024); // костёр в 3 тайлах — полный
            Assert.True(top[0].Dx4 < 0);      // от костра (восток) к точке — на запад
            Assert.True(top[1].Dx4 > 0);      // от факела — на восток

            // Факел к югу от точки — направление «от источника» на север (Y мира вниз: Dy4 < 0).
            Assert.True(sim.RequestEdit(new TileCoord(600, 605), FloorId.Grass, ObjectId.Torch));
            sim.Step();
            n = w.Sight.Strongest(Torus.ChunkOf(w.Shape, new TileCoord(600, 601)), 600 * 4 + 2, 601 * 4 + 2, top);
            Assert.Equal(1, n);
            Assert.Equal(0, top[0].Dx4);
            Assert.Equal(-16, top[0].Dy4);
        }
    }
}
