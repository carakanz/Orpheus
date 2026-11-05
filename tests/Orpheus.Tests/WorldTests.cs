using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orpheus.Core.Geometry;
using Orpheus.Core.Rng;
using Orpheus.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Приёмка мира (= docs/07 3.3, 3.4, 3.6, 3.7, 3.8). Числа — из world.md.
    /// Тяжёлые тесты помечены [Trait("Category","Big")]: в CI запускаются только
    /// если укладываются в лимит (см. -SkipBig в tools/ci.ps1).
    /// </summary>
    public class WorldTests
    {
        private const int C = Torus.ChunkSize;      // 32 чанка => мир 1024x1024
        private const int TilesPerChunk = C * C;    // 1024 тайла в чанке

        private readonly ITestOutputHelper _out;

        public WorldTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static MapShape World1024 => MapShape.World(1024);

        /// <summary>Заполняет чанк детерминированным узором по координатам тайла.</summary>
        private static void FillPattern(Chunk chunk, ulong seed)
        {
            for (int i = 0; i < TilesPerChunk; i++)
            {
                int tx = chunk.X * C + (i % C);
                int ty = chunk.Y * C + (i / C);
                ulong h = SplitMix64.Hash((ulong)tx, (ulong)ty, seed);
                byte floor = (byte)(1 + (int)(h % 4ul));
                ushort obj = (h >> 17) % 23ul == 0 ? ObjectId.Torch : (ushort)0;
                chunk.SetTile(i, floor, obj);
            }
        }

        private static HashSet<ChunkCoord> ToSet(List<ChunkCoord> list) => new HashSet<ChunkCoord>(list);

        // ---------- размеры (world.md: 3 байта на тайл, 5..13 на правку) ----------

        [Fact]
        public void D00_World_TileAndEditSizesMatchWorldRef()
        {
            Assert.Equal(2, ObjectId.ObjectBytesPerTile);
            Assert.Equal(1024, Chunk.FloorBytes);
            Assert.Equal(2048, Chunk.ObjectBytes);
            Assert.Equal(3072, Chunk.FullLayersBytes);
            Assert.Equal(5, TileEdit.PackSizeBytes);
            Assert.Equal(8, BuildRecord.RecordSizeBytes);
            Assert.Equal(32, TilePatch.MaxChunksPerPatch);
            Assert.Equal(256, Chunk.MaxRecordedTilesPerChunk);
        }

        [Fact]
        public void D00_World_ChunkFullWrite_Is3072BytesPlusRevision()
        {
            var chunk = new Chunk(new ChunkCoord(3, 5));
            byte[] bytes = chunk.WriteFull();
            Assert.Equal(4 + Chunk.FullLayersBytes, bytes.Length);
            Assert.Equal(0, chunk.Revision);

            chunk.SetTile(100, FloorId.Grass, ObjectId.Torch);
            chunk.SetTile(1023, FloorId.Stone, ObjectId.Rock);
            byte[] b = chunk.WriteFull();
            Assert.Equal(4 + 3072, b.Length);
            ushort rev = (ushort)(b[0] | (b[1] << 8));
            Assert.Equal(chunk.Revision, rev);
            Assert.Equal(FloorId.Grass, chunk.GetFloorId(100));
            Assert.Equal(ObjectId.Torch, chunk.GetObject(100));
            Assert.Equal(FloorId.Stone, chunk.GetFloorId(1023));
            Assert.Equal(ObjectId.Rock, chunk.GetObject(1023));
        }

        [Fact]
        public void D00_World_TileEdit_InvalidIndex_IsRejected()
        {
            var chunk = new Chunk(new ChunkCoord(1, 1));
            Assert.Equal(EditResult.InvalidIndex, chunk.TryApplyEdit(1024, 1, 3, out _));
            var record = new BuildRecord(1, 12, 2, 1, 1, 1, BuildFlags.Production);
            Assert.Equal(EditResult.InvalidIndex, chunk.TryApplyRecord(1024, record, out _));
        }

        [Fact]
        public void D00_World_SingleEdit_AppliesDataAndRecord()
        {
            var chunk = new Chunk(new ChunkCoord(2, 2));
            var record = new BuildRecord(100, ObjectId.Table, 2, 1, 0, 50, BuildFlags.Production);
            Assert.Equal(EditResult.Applied, chunk.TryApplyEdit(100, FloorId.Dirt, ObjectId.Table, record, out _));
            Assert.Equal(FloorId.Dirt, chunk.GetFloorId(100));
            Assert.Equal(ObjectId.Table, chunk.GetObject(100));
            Assert.True(chunk.HasRecord(100));
            BuildRecord got = chunk.GetRecord(100);
            Assert.Equal(1, got.Kind);
            Assert.Equal((ushort)50, got.Quality);
        }

        [Fact]
        public void D00_World_ClearObject_RemovesBuildRecord()
        {
            var chunk = new Chunk(new ChunkCoord(2, 2));
            var record = new BuildRecord(64, ObjectId.Chest, 1, 1, 0, 10, BuildFlags.None);
            chunk.TryApplyEdit(64, FloorId.Grass, ObjectId.Chest, record, out _);
            Assert.True(chunk.HasRecord(64));

            // снятие объекта (obj = 0) снимает и запись постройки (3.3)
            Assert.Equal(EditResult.Applied, chunk.TryApplyEdit(64, FloorId.Grass, ObjectId.None, null, out _));
            Assert.False(chunk.HasRecord(64));
        }

        // ---------- пачка правок = одна операция: ревизия +1 (3.3) ----------

        [Fact]
        public void D00_World_BatchOfEdits_RaisesRevisionExactlyOnce()
        {
            var model = new WorldModel(World1024, 777, 1);
            var patch = new TilePatch();
            var edit = new TileEdit(50, FloorId.Stone, ObjectId.Rock, null);
            patch.AddEdit(new ChunkCoord(4, 4), 10, edit);
            patch.AddEdit(new ChunkCoord(4, 4), 11, edit);
            var record = new BuildRecord(12, ObjectId.Table, 2, 1, 0, 33, BuildFlags.Production);
            patch.AddEdit(new ChunkCoord(4, 4), 12, new TileEdit(12, FloorId.Dirt, ObjectId.Table, record));
            patch.AddEdit(new ChunkCoord(9, 1), 200, new TileEdit(200, FloorId.Grass, ObjectId.Torch, record));

            IReadOnlyList<ChunkEditOutcome> results = model.ApplyPatch(patch);
            Assert.Equal(2, results.Count); // одна пачка => два чанка, не четыре события
            Assert.Equal(4, results.Sum(r => r.Results.Count(x => x.Status == EditResult.Applied)));
            Assert.Equal(1, model.GetChunk(new ChunkCoord(4, 4)).Revision);
            Assert.Equal(1, model.GetChunk(new ChunkCoord(9, 1)).Revision);
        }

        [Fact]
        public void D00_World_BatchWithSameTileConflict_AppliesFirstMarksSecondBusy()
        {
            var model = new WorldModel(World1024, 777, 1);
            var c = new ChunkCoord(4, 4);

            // сервер уже поставил сундук на тайл 100
            var chest = new BuildRecord(100, ObjectId.Chest, 1, 1, 0, 20, BuildFlags.None);
            model.ApplyEdit(c, 100, new TileEdit(100, FloorId.Grass, ObjectId.Chest, chest));
            byte floorBefore = model.GetChunk(c).GetFloorId(100);
            ushort objBefore = model.GetChunk(c).GetObject(100);
            int revBefore = model.GetChunk(c).Revision;

            // пачка: [тайл 100 - конфликт, тайл 101 - проходит]
            var patch = new TilePatch();
            patch.AddEdit(c, 100, new TileEdit(100, FloorId.Stone, ObjectId.WoodWall, null));
            patch.AddEdit(c, 101, new TileEdit(101, FloorId.Dirt, ObjectId.Torch, null));
            IReadOnlyList<ChunkEditOutcome> results = model.ApplyPatch(patch);

            Assert.Equal(1, results.Count);
            Assert.Equal(EditResult.Busy, results[0].Results[0].Status);
            Assert.Equal(EditResult.Applied, results[0].Results[1].Status);
            // первая правка не изменилась, вторая применилась, ревизия +1
            Assert.Equal(floorBefore, model.GetChunk(c).GetFloorId(100));
            Assert.Equal(objBefore, model.GetChunk(c).GetObject(100));
            Assert.Equal(FloorId.Dirt, model.GetChunk(c).GetFloorId(101));
            Assert.Equal(revBefore + 1, model.GetChunk(c).Revision);
        }

        [Fact]
        public void D00_World_PatchWireSize_StaysWithin13BytesPerTile()
        {
            var patch = new TilePatch();
            var record = new BuildRecord(0, ObjectId.Table, 2, 1, 0, 9, BuildFlags.Production);
            // 2 правки без записи (по 5) + 2 с записью (по 13) = 36 байт на 4 правки
            patch.AddEdit(new ChunkCoord(1, 1), 10, new TileEdit(10, FloorId.Grass, ObjectId.Torch, null));
            patch.AddEdit(new ChunkCoord(1, 1), 11, new TileEdit(11, FloorId.Grass, ObjectId.Rock, null));
            patch.AddEdit(new ChunkCoord(1, 1), 12, new TileEdit(12, FloorId.Dirt, ObjectId.Table, record));
            patch.AddEdit(new ChunkCoord(1, 1), 13, new TileEdit(13, FloorId.Dirt, ObjectId.Chest, record));

            byte[] wire;
            using (var ms = new MemoryStream())
            {
                patch.WriteTo(ms);
                wire = ms.ToArray();
            }
            int payload = wire.Length - (2 + 1 + TilePatch.HeaderBytesPerChunk);
            Assert.Equal(36, payload);
            Assert.True(payload <= 4 * 13);
        }

        // ---------- сервер и клиент дают одинаковые байты ----------

        [Fact]
        public void D00_World_ServerAndClient_ApplySamePatchToIdenticalBytes()
        {
            var map = World1024;
            var server = new WorldModel(map, 1234, 1);
            var client = new WorldModel(map, 1234, 1);

            var c = new ChunkCoord(6, 7);
            var record = new BuildRecord(300, ObjectId.Table, 2, 1, 0, 77, BuildFlags.Production);
            var patch = new TilePatch();
            patch.AddEdit(c, 300, new TileEdit(300, FloorId.Dirt, ObjectId.Table, record));
            patch.AddEdit(c, 301, new TileEdit(301, FloorId.Dirt, ObjectId.Torch, null));
            patch.AddEdit(c, 302, new TileEdit(302, FloorId.Stone, ObjectId.WoodWall, null));

            // сериализация пачки (как по сети) и чтение на клиенте
            byte[] wire;
            using (var ms = new MemoryStream())
            {
                patch.WriteTo(ms);
                wire = ms.ToArray();
            }
            server.ApplyPatch(patch);
            client.ApplyPatch(TilePatch.Read(wire));

            byte[] a = server.GetChunk(c).WriteFull();
            byte[] b = client.GetChunk(c).WriteFull();
            Assert.Equal(a, b);                       // байт в байт
            Assert.Equal(1, server.GetChunk(c).Revision);
            Assert.Equal(1, client.GetChunk(c).Revision);
        }

        [Fact]
        public void D00_World_PatchWriteRead_RoundTripsByteIdentical()
        {
            var patch = new TilePatch();
            var record = new BuildRecord(500, ObjectId.Furnace, 1, 1, 0, 42, BuildFlags.Storage);
            patch.AddEdit(new ChunkCoord(2, 3), 500, new TileEdit(500, FloorId.Stone, ObjectId.Furnace, record));
            patch.AddEdit(new ChunkCoord(2, 30), 8, new TileEdit(8, FloorId.Grass, ObjectId.Tree, null));

            byte[] first, second;
            using (var ms = new MemoryStream())
            {
                patch.WriteTo(ms);
                first = ms.ToArray();
            }
            TilePatch copy = TilePatch.Read(first);
            using (var ms = new MemoryStream())
            {
                copy.WriteTo(ms);
                second = ms.ToArray();
            }
            Assert.Equal(first, second);
            Assert.True(copy.HasRecord(500));
            Assert.False(copy.HasRecord(8));
        }

        // ---------- свежесть сжатой копии (3.4) ----------

        [Fact]
        public void D00_World_CopyBecomesStaleAfterEdit_AndFreshAfterRebuild()
        {
            var chunk = new Chunk(new ChunkCoord(5, 5));
            FillPattern(chunk, 4242);
            Assert.True(chunk.RebuildCompressed() > 0);
            Assert.True(chunk.HasFreshCopy);

            // любое изменение данных делает копию неактуальной
            chunk.TryApplyEdit(10, FloorId.Grass, ObjectId.Torch, null, out _);
            Assert.False(chunk.HasFreshCopy);
            Assert.Null(chunk.FreshCopy);

            int len = chunk.RebuildCompressed();
            Assert.True(len > 0);
            Assert.True(chunk.HasFreshCopy);

            // одинаковые чанки дают побайтно одинаковую сжатую копию
            var twin = new Chunk(new ChunkCoord(5, 5));
            FillPattern(twin, 4242);
            twin.TryApplyEdit(10, FloorId.Grass, ObjectId.Torch, null, out _);
            twin.RebuildCompressed();
            Assert.Equal(chunk.FreshCopy, twin.FreshCopy);
        }

        [Fact]
        [Trait("Category", "Big")]
        public void D00_World_CompressedChunkBytes_ReferencePattern()
        {
            // эталонный чанк (world.md): узор пола по хэшу тайла + редкие объекты.
            // Компрессор - System.IO.Compression.DeflateStream, Optimal (уровень 6).
            int[] sizes = new int[8];
            for (int k = 0; k < 8; k++)
            {
                var chunk = new Chunk(new ChunkCoord(k, k * 3 + 1));
                FillPattern(chunk, (ulong)(1000 + k));
                sizes[k] = chunk.RebuildCompressed();
                _out.WriteLine($"чанк ({chunk.X},{chunk.Y}): {sizes[k]} байт из {Chunk.FullLayersBytes}");
            }
            double average = sizes.Average();
            Assert.True(average >= 100 && average <= 600,
                $"средний сжатый чанк {average:F0} байт вне ожидаемого коридора 100..600");
            Assert.True(sizes.Max() < Chunk.FullLayersBytes);
        }

        [Fact]
        public void D00_World_ParallelAndSerialChunkCreation_AreByteIdentical()
        {
            var map = World1024;
            var coords = new List<ChunkCoord>();
            for (int i = 0; i < 32; i++)
                coords.Add(new ChunkCoord(i, (i * 7 + 3) % 32));

            var serial = new WorldModel(map, 9090, 1);
            byte[][] expected = coords.Select(c => serial.GetChunk(c).WriteCompressed()).ToArray();

            byte[][] parallel = new byte[coords.Count][];
            Parallel.For(0, coords.Count, i =>
            {
                var model = new WorldModel(map, 9090, 1);
                parallel[i] = model.GetChunk(coords[i]).WriteCompressed();
            });

            for (int i = 0; i < coords.Count; i++)
                Assert.Equal(expected[i], parallel[i]);
        }

        // ---------- снимок: генерация + изменённые чанки (3.6) ----------

        [Fact]
        [Trait("Category", "Big")]
        public void D00_World_SnapshotSaveLoad_Roundtrip_IsByteIdentical_1024()
        {
            var map = World1024;
            var server = new WorldModel(map, 55555, 1);
            for (int x = 0; x < C; x++)
                for (int y = 0; y < C; y++)
                    FillPattern(server.GetChunk(new ChunkCoord(x, y)), 31337);

            byte[] snapshot = WorldSnapshot.SaveBase(server);
            WorldModel restored = WorldSnapshot.Load(snapshot);

            Assert.Equal(server.ChunkCount, restored.ChunkCount);
            Assert.Equal(C * C, restored.ChunkCount);

            // побайтное совпадение всех чанков (пол+объект+ревизия)
            foreach (ChunkCoord key in server.SortedChunkKeys())
            {
                Assert.Equal(server.GetChunk(key).WriteFull(), restored.GetChunk(key).WriteFull());
            }

            // накладной расход снимка = заголовок + 16 байт на чанк, не больше
            long blockBytes = server.SortedChunkKeys().Sum(key =>
                WorldSnapshot.CompressedChunkLength(server.GetChunk(key)) + WorldSnapshot.ChunkHeaderBytes);
            long overhead = snapshot.Length - blockBytes;
            _out.WriteLine($"снимок 1024x1024: {snapshot.Length} байт, чанков {server.ChunkCount}, " +
                           "обслуживающие байты " + overhead);
            Assert.True(overhead <= WorldSnapshot.HeaderBytes, $"обслуживающие байты {overhead} > {WorldSnapshot.HeaderBytes}");
        }

        [Fact]
        [Trait("Category", "Big")]
        public void D00_World_SnapshotChangesOnly_BasePlusChangesRebuildsIdenticalWorld()
        {
            var map = World1024;
            var server = new WorldModel(map, 60606, 1);
            for (int x = 0; x < C; x++)
                for (int y = 0; y < C; y++)
                    server.GetChunk(new ChunkCoord(x, y));
            byte[] baseSnapshot = WorldSnapshot.SaveBase(server);

            // правки затронули ровно один чанк
            var edited = new ChunkCoord(3, 4);
            var record = new BuildRecord(400, ObjectId.Table, 2, 1, 0, 55, BuildFlags.Production);
            server.ApplyPatch(new[]
            {
                new ChunkEdit(edited, new[]
                {
                    new TileEdit(400, FloorId.Dirt, ObjectId.Table, record),
                    new TileEdit(401, FloorId.Dirt, ObjectId.Torch, null),
                }),
            });

            byte[] changes = WorldSnapshot.SaveChanges(server);
            Assert.True(changes.Length < baseSnapshot.Length,
                $"снимок правок {changes.Length} не меньше полного {baseSnapshot.Length}");

            WorldModel rebuilt = WorldSnapshot.LoadWithChanges(baseSnapshot, changes);
            Assert.Equal(1, rebuilt.ChunkCount); // только изменённые чанки
            Assert.Equal(server.GetChunk(edited).WriteFull(), rebuilt.GetChunk(edited).WriteFull());

            // чанк без правок восстанавливается из генерации побайтно
            var untouched = new ChunkCoord(10, 11);
            Assert.Equal(server.GetChunk(untouched).WriteFull(), rebuilt.GetChunk(untouched).WriteFull());

            _out.WriteLine($"база {baseSnapshot.Length} байт, правки {changes.Length} байт");
        }

        [Fact]
        public void D00_World_SnapshotChanges_EmptyWhenNoEdits_Throws()
        {
            var server = new WorldModel(MapShape.World(256), 1, 1);
            Assert.Throws<InvalidOperationException>(() => WorldSnapshot.SaveChanges(server));
        }

        [Fact]
        public void D00_World_SnapshotCorruption_IsDetected()
        {
            var server = new WorldModel(MapShape.World(256), 4242, 1);
            for (int x = 0; x < 8; x++)
                for (int y = 0; y < 8; y++)
                    FillPattern(server.GetChunk(new ChunkCoord(x, y)), 7);
            byte[] snapshot = WorldSnapshot.SaveBase(server);

            // повреждение полезной нагрузки (последний байт) ловится хэшем
            var tampered = (byte[])snapshot.Clone();
            tampered[tampered.Length - 1] ^= 0xFF;
            Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(tampered));

            // обрезанный буфер ловится на чтении
            var truncated = new byte[snapshot.Length - 4];
            Array.Copy(snapshot, truncated, truncated.Length);
            Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(truncated));

            Assert.Equal(server.GetChunk(new ChunkCoord(1, 2)).WriteFull(),
                WorldSnapshot.Load(snapshot).GetChunk(new ChunkCoord(1, 2)).WriteFull());
        }

        // ---------- окно чанков: 6x4 -> 8x6, <=48, без дублей на шве (3.7) ----------

        [Fact]
        public void D00_World_Window_OutputIsSubsetOf8x6_AndWithin48()
        {
            var map = World1024;
            var probe = new int[] { 0, 15, 16, 31, 511, 512, 1023 };
            foreach (int px in probe)
            {
                foreach (int py in probe)
                {
                    TileCoord player = Torus.Wrap(new TileCoord(px, py), map);
                    var center = Torus.TileOf(player, map);
                    List<ChunkCoord> window = ChunkWindow.Compute(map, player, new[] { center });
                    Assert.True(window.Count <= 48, $"окно {window.Count} чанков > 48");
                    Assert.Contains(center, window);

                    // всё, что отдали, лежит в блоке выхода 8x6
                    foreach (ChunkCoord w in window)
                    {
                        int sx = Torus.DeltaX(center, w, map);
                        int sy = Torus.DeltaY(center, w, map);
                        Assert.True(sx >= -3 && sx <= 4 && sy >= -3 && sy <= 4,
                            $"чанк ({w.X},{w.Y}) вне блока 8x6: сдвиг ({sx},{sy})");
                    }
                }
            }
        }

        [Fact]
        [Trait("Category", "Big")]
        public void D00_World_Window_NoDuplicatesWhenWalkingAcrossSeam_1024()
        {
            var map = World1024;
            // обход вокруг тора по x (через восточный шов) и по диагонали через оба шва
            int[] ys = { 16, 512, 1007 };
            int steps = 1024 + 24;
            int maxSeen = 0;
            for (int step = 0; step < steps; step++)
            {
                foreach (int py in ys)
                {
                    TileCoord player = Torus.Wrap(new TileCoord(step, py), map);
                    var center = Torus.TileOf(player, map);
                    List<ChunkCoord> window = ChunkWindow.Compute(map, player, new[] { center });
                    HashSet<ChunkCoord> unique = ToSet(window);
                    Assert.Equal(window.Count, unique.Count); // ни одного дубля на шве
                    Assert.Contains(center, unique);
                    Assert.True(window.Count >= 24 && window.Count <= 48, $"размер окна {window.Count}");
                    maxSeen = Math.Max(maxSeen, window.Count);
                }
            }
            _out.WriteLine($"обход {steps} шагов x 3 высоты: максимальное окно {maxSeen} чанков");
            Assert.True(maxSeen <= 48);
        }

        [Fact]
        public void D00_World_Window_Hysteresis_NoJumpsWhileMovingOneTile()
        {
            var map = World1024;
            TileCoord prev = Torus.Wrap(new TileCoord(16 * C + 8, 16 * C + 8), map);
            List<ChunkCoord> prevWindow = ChunkWindow.Compute(map, prev, new[] { Torus.TileOf(prev, map) });

            int maxDelta = 0;
            for (int step = 1; step <= 40; step++)
            {
                TileCoord here = Torus.Wrap(new TileCoord(prev.X + step, prev.Y), map);
                var center = Torus.TileOf(here, map);
                List<ChunkCoord> window = ChunkWindow.Compute(map, here, new[] { center });

                HashSet<ChunkCoord> a = ToSet(prevWindow);
                HashSet<ChunkCoord> b = ToSet(window);
                int removed = a.Count(c => !b.Contains(c));
                int added = b.Count(c => !a.Contains(c));
                maxDelta = Math.Max(maxDelta, removed + added);
                Assert.True(removed <= 4 && added <= 4,
                    $"прыжок окна: выросло {removed}, стало {added} (на {removed + added})");
                prevWindow = window;
            }
            _out.WriteLine($"пошаговый сдвиг окна: максимум {maxDelta} чанков за шаг");

            // в середине чанка (тайлы 4..15 внутри чанка) окно не меняется вовсе
            TileCoord start = Torus.Wrap(new TileCoord(20 * C + 4, 20 * C + 4), map);
            List<ChunkCoord> fixed1 = ChunkWindow.Compute(map, start, new[] { Torus.TileOf(start, map) });
            TileCoord mid = Torus.Wrap(new TileCoord(20 * C + 15, 20 * C + 4), map);
            List<ChunkCoord> fixed2 = ChunkWindow.Compute(map, mid, new[] { Torus.TileOf(mid, map) });
            Assert.Equal(ToSet(fixed1), ToSet(fixed2));
        }

        // ---------- активная зона: вход 7x7, выход 9x9 (3.8) ----------

        [Fact]
        public void D00_World_ActiveZone_EntersAt7x7_AroundPlayer()
        {
            var map = World1024;
            int[] probe = { 0, 1, 16, 31, 1023 };   // включая углы тора
            foreach (int px in probe)
            {
                foreach (int py in probe)
                {
                    TileCoord player = Torus.Wrap(new TileCoord(px, py), map);
                    var center = Torus.TileOf(player, map);
                    List<ChunkCoord> window = ActiveZone.Compute(map, new[] { player });

                    // вход: весь邻居 7x7 обязан быть в окне (на торе без дублей)
                    foreach (ChunkCoord n in Torus.NeighborChunks(map, center, 3))
                        Assert.Contains(n, window);
                    Assert.Equal(49, window.Count); // 7x7 на торе: 49 различных чанков
                }
            }
        }

        [Fact]
        public void D00_World_ActiveZone_ExitsBeyond9x9()
        {
            var map = World1024;
            TileCoord player = Torus.Wrap(new TileCoord(16 * C + 16, 16 * C + 16), map);
            var center = Torus.TileOf(player, map);
            List<ChunkCoord> window = ActiveZone.Compute(map, new[] { player });

            // выход: чанк в 5 чанках по X (за пределами 9x9) обязан выпасть
            var far = Torus.Wrap(new ChunkCoord(center.X + 5, center.Y), map);
            Assert.DoesNotContain(far, window);

            // и чанк по диагонали (4,4) - на границе 9x9 - обязан остаться
            var corner = Torus.Wrap(new ChunkCoord(center.X + 4, center.Y + 4), map);
            Assert.Contains(corner, window);
        }

        [Fact]
        public void D00_World_ActiveZone_TwoPlayers_UnionCoverage()
        {
            var map = World1024;
            TileCoord p1 = Torus.Wrap(new TileCoord(10 * C + 16, 10 * C + 16), map);
            TileCoord p2 = Torus.Wrap(new TileCoord(26 * C + 16, 10 * C + 16), map);
            List<ChunkCoord> window = ActiveZone.Compute(map, new[] { p1, p2 });

            var c1 = Torus.TileOf(p1, map);
            var c2 = Torus.TileOf(p2, map);
            foreach (ChunkCoord n in Torus.NeighborChunks(map, c1, 3))
                Assert.Contains(n, window);
            foreach (ChunkCoord n in Torus.NeighborChunks(map, c2, 3))
                Assert.Contains(n, window);
            Assert.Equal(ToSet(window).Count, window.Count);

            // между игроками (9 чанков) зона не тянется: середина выпадает
            var middle = new ChunkCoord(18, 10);
            Assert.DoesNotContain(middle, window);
            Assert.True(window.Count >= 98, $"окно {window.Count} чанков уже двух зон 7x7");
        }

        [Fact]
        public void D00_World_ActiveZone_TorusCornerWrapsToOppositeCorner()
        {
            var map = World1024;
            TileCoord player = Torus.Wrap(new TileCoord(0, 0), map);
            List<ChunkCoord> window = ActiveZone.Compute(map, new[] { player });

            // угол тора: соседство уходит за шов в противоположный угол карты
            Assert.Contains(new ChunkCoord(31, 31), window);
            Assert.Contains(new ChunkCoord(29, 30), window);
            Assert.DoesNotContain(new ChunkCoord(28, 29), window); // 4 шага за 9x9 по обеим осям
        }

        // ---------- память: 1024x1024 тайлов = 48 МБ (3.2) ----------

        [Fact]
        public void D00_World_FullLayersBytes_4096_IsExactly48Megabytes()
        {
            var model = new WorldModel(MapShape.World(4096), 1, 1);
            Assert.Equal(128, model.ChunksPerSide);
            Assert.Equal(16384, model.TotalChunks);
            Assert.Equal(48L * 1024 * 1024, model.FullBytes);
        }

        [Fact]
        [Trait("Category", "Big")]
        public void D00_World_MeasuredMemory_4096_ThreeBytesPerTile()
        {
            var model = new WorldModel(MapShape.World(4096), 1, 1);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = GC.GetTotalMemory(true);

            for (int x = 0; x < model.ChunksPerSide; x++)
                for (int y = 0; y < model.ChunksPerSide; y++)
                    model.GetChunk(new ChunkCoord(x, y));

            long after = GC.GetTotalMemory(false);
            long delta = after - before;
            long expected = model.FullBytes;
            _out.WriteLine($"материализовано {model.ChunkCount} чанков: прирост кучи {delta / 1024.0 / 1024.0:F2} МБ " +
                           $"(слои {expected / 1024.0 / 1024.0:F2} МБ)");

            Assert.Equal(16384, model.ChunkCount);
            Assert.True(delta >= expected, $"прирост {delta} меньше слоёв {expected}");
            Assert.True(delta <= (long)(expected * 1.35), $"прирост {delta} > 135% от слоёв {expected}");
        }
    }
}
