using System;
using System.Collections.Generic;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using Xunit;
using Xunit.Abstractions;
using TileHash = Orpheus.Core.Rng.SplitMix64;

namespace Orpheus.Tests
{
    /// <summary>
    /// Приёмка мира в памяти (карточка 0-005; docs/07 3.3–3.7, 3.13 п. 6, 9; числа — work/refs/world.md).
    /// Тесты на мирах 4096² и больше — с префиксом D00_World_Big и [Trait("Category", "Big")]:
    /// tools/ci.ps1 их не гоняет (фильтр Category!=Big), ночной прогон — гоняет (tools/ci.ps1 -All).
    /// </summary>
    public class WorldTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const int CS = Torus.ChunkSize;
        private const ulong Seed = 0x0E5_2026UL;

        private readonly ITestOutputHelper _out;

        public WorldTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- Эталонные паттерны (генератора в этой карточке нет) ----------

        /// <summary>
        /// «Поле»: трава с пятнами земли (пятна 4 × 4 тайла), редкие деревья (3 %), камни (1 %), пни (0,5 %).
        /// Вид объекта не хранится (3.3) — только id.
        /// </summary>
        private static void FieldTile(int x, int y, out byte floor, out ushort obj)
        {
            ulong patch = TileHash.Hash(x >> 2, y >> 2, Seed ^ 0xF1);
            floor = patch % 8 == 0 ? FloorId.Dirt : FloorId.Grass;
            ulong h = TileHash.Hash(x, y, Seed) % 1000;
            obj = h < 30 ? ObjectId.Tree : h < 40 ? ObjectId.Rock : h < 45 ? ObjectId.Stump : ObjectId.Empty;
        }

        private static void FillField(WorldModel world)
        {
            world.CreateAllChunks();
            for (int y = 0; y < world.Shape.Height; y++)
            {
                for (int x = 0; x < world.Shape.Width; x++)
                {
                    FieldTile(x, y, out byte floor, out ushort obj);
                    world.SetBaseTile(new TileCoord(x, y), floor, obj);
                }
            }
        }

        private static Chunk FieldChunk(int cx, int cy)
        {
            var chunk = new Chunk();
            for (int i = 0; i < Chunk.TileCount; i++)
            {
                FieldTile(cx * CS + (i & (CS - 1)), cy * CS + (i >> Torus.ChunkBits), out byte floor, out ushort obj);
                chunk.SetBaseTile(i, floor, obj);
            }

            return chunk;
        }

        private static TileEdit Wall(int index, ushort owner = 0) =>
            new TileEdit((ushort)index, FloorId.WoodFloor, ObjectId.WoodWall, BuildRecord.OfPlayer(owner, FloorId.Grass));

        // ---------- Размер ----------

        [Fact]
        public void D00_World_Size_TileIs3Bytes_ChunkIs3072_World4096Is48MB()
        {
            Assert.Equal(1024, Chunk.FloorLayerBytes);
            Assert.Equal(2048, Chunk.ObjectLayerBytes);
            Assert.Equal(3072, Chunk.LayerBytes);
            Assert.Equal(3072, new Chunk().Layers.Length);
            Assert.Equal(3, Chunk.LayerBytes / Chunk.TileCount);
            Assert.Equal(8, BuildRecord.SizeBytes);

            var world = new WorldModel(MapShape.World(4096));
            Assert.Equal(128 * 128, world.TotalChunks);
            Assert.Equal(48L << 20, world.FullLayerBytes);
            Assert.Equal(3L << 20, new WorldModel(MapShape.World(1024)).FullLayerBytes);
        }

        [Fact]
        public void D00_World_Memory_1024_Measured()
        {
            long bytes = MeasureWorldBytes(1024, out int chunks);
            _out.WriteLine($"1024²: {chunks} чанков, {bytes} байт = {bytes / 1048576.0:F2} МБ (модель 3 МБ), {bytes / (double)chunks:F0} байт на чанк");
            Assert.InRange(bytes, 3L << 20, (long)((3L << 20) * 1.10));
        }

        [Fact]
        [Trait("Category", "Big")]
        public void D00_World_BigMemory_4096_Measured()
        {
            long bytes = MeasureWorldBytes(4096, out int chunks);
            _out.WriteLine($"4096²: {chunks} чанков, {bytes} байт = {bytes / 1048576.0:F2} МБ (модель 48 МБ), {bytes / (double)chunks:F0} байт на чанк");
            Assert.InRange(bytes, 48L << 20, (long)((48L << 20) * 1.10));
        }

        // Память мира — байты, выделенные этим потоком на создание мира со всеми чанками (модель, словарь, чанки).
        // Мусора при этом нет (словарь сразу нужной ёмкости), поэтому это и есть живая память мира;
        // GC.GetTotalMemory не годится — xUnit гоняет классы тестов параллельно.
        private static long MeasureWorldBytes(int size, out int chunks)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var world = new WorldModel(MapShape.World(size));
            world.CreateAllChunks();
            long after = GC.GetAllocatedBytesForCurrentThread();
            chunks = world.ChunkCount;
            GC.KeepAlive(world);
            return after - before;
        }

        // ---------- Типы тайла ----------

        [Fact]
        public void D00_World_Ids_TablesAndMultiTilePart()
        {
            Assert.True(FloorId.IsNatural(FloorId.Grass));
            Assert.True(FloorId.IsBuilt(FloorId.WoodFloor));
            Assert.False(FloorId.IsSolid(FloorId.Water));
            Assert.False(FloorId.IsPassable(FloorId.Lava));

            Assert.True(ObjectId.IsPassable(ObjectId.Empty));
            Assert.True(ObjectId.IsPassable(ObjectId.Torch));
            Assert.True(ObjectId.IsSolid(ObjectId.MakeAnchor(ObjectId.WoodWall, 2)));
            Assert.True(ObjectId.IsNatural(ObjectId.Tree));
            Assert.False(ObjectId.IsPassable(ObjectId.Door, (byte)BuildFlags.None));
            Assert.True(ObjectId.IsPassable(ObjectId.Door, (byte)BuildFlags.DoorOpen));

            ushort part = ObjectId.MakePart(15, 15);
            Assert.Equal(0xF0FF, part);
            Assert.True(ObjectId.IsPart(part));
            Assert.Equal(15, ObjectId.PartOffsetX(part));
            Assert.Equal(15, ObjectId.PartOffsetY(part));
            Assert.Equal(ObjectId.Properties.Part, ObjectId.Of(part));
            Assert.False(ObjectId.IsPart(ObjectId.PartMarker));
            Assert.False(ObjectId.IsValidRaw(0x1000));
            Assert.False(ObjectId.IsValidRaw(0xF100));
            Assert.Throws<ArgumentException>(() => ObjectId.MakePart(0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ObjectId.MakePart(16, 0));
        }

        [Fact]
        public void D00_World_MultiTileOnSeam_PartFindsAnchorAcrossSeam()
        {
            var shape = MapShape.World(1024);
            var world = new WorldModel(shape);
            var anchor = new TileCoord(1023, 1023);
            ushort forge = ObjectId.MakeAnchor(ObjectId.Forge, 1);
            world.SetBaseTile(anchor, FloorId.StoneFloor, forge);
            world.SetBaseTile(new TileCoord(1024, 1023), FloorId.StoneFloor, ObjectId.MakePart(1, 0));
            world.SetBaseTile(new TileCoord(1023, 1024), FloorId.StoneFloor, ObjectId.MakePart(0, 1));
            world.SetBaseTile(new TileCoord(1024, 1024), FloorId.StoneFloor, ObjectId.MakePart(1, 1));

            Assert.Equal(ObjectId.MakePart(1, 1), world.GetObject(new TileCoord(0, 0)));
            Assert.Equal(anchor, world.AnchorOf(new TileCoord(0, 0)));
            Assert.Equal(anchor, world.AnchorOf(new TileCoord(0, 1023)));
            Assert.Equal(anchor, world.AnchorOf(new TileCoord(1023, 0)));
            Assert.Equal(anchor, world.AnchorOf(anchor));
            Assert.Equal(4, world.ChunkCount);
        }

        [Fact]
        public void D00_World_ReadingMissingChunk_IsEmptyAndDoesNotCreateIt()
        {
            var world = new WorldModel(MapShape.World(1024));
            Assert.Equal(FloorId.Empty, world.GetFloor(new TileCoord(500, 500)));
            Assert.Equal(ObjectId.Empty, world.GetObject(new TileCoord(-1, -1)));
            Assert.False(world.TryGetBuild(new TileCoord(3, 3), out _));
            Assert.Equal(0, world.ChunkCount);

            var dungeon = new WorldModel(MapShape.Flat(100, 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => dungeon.GetFloor(new TileCoord(100, 0)));
        }

        // ---------- Правки (3.7) ----------

        [Fact]
        public void D00_World_Edits_BatchRaisesRevisionByExactlyOne()
        {
            var world = new WorldModel(MapShape.World(1024));
            var c = new ChunkCoord(3, 4);
            var edits = new TileEdit[32];
            for (int i = 0; i < edits.Length; i++)
            {
                edits[i] = Wall(i * 3);
            }

            var results = new EditResult[edits.Length];
            Assert.Equal(1u, world.ApplyEdits(c, edits, results));
            Assert.All(results, r => Assert.Equal(EditResult.Applied, r));
            Assert.Equal(2u, world.ApplyEdits(c, new[] { new TileEdit(5, FloorId.Dirt, ObjectId.Empty) }, results));

            // Пустая пачка и пачка из одних отказов ревизию не поднимают.
            Assert.Equal(2u, world.ApplyEdits(c, ReadOnlySpan<TileEdit>.Empty, results));
            Assert.Equal(2u, world.ApplyEdits(c, new[] { new TileEdit(1024, 1, 0), new TileEdit(7, 1, 0x1234) }, results));
            Assert.Equal(EditResult.InvalidIndex, results[0]);
            Assert.Equal(EditResult.InvalidObject, results[1]);

            var tile = new TileCoord(3 * CS + 7, 4 * CS + 1); // индекс 39 = 13 · 3
            Assert.Equal(ObjectId.WoodWall, world.GetObject(tile));
            Assert.True(world.TryGetBuild(tile, out BuildRecord rec));
            Assert.Equal(FloorId.Grass, rec.NaturalFloor);
        }

        [Fact]
        public void D00_World_Edits_TileFullyReplaced_RecordRemovedWithoutOne()
        {
            var chunk = new Chunk();
            var results = new EditResult[1];
            chunk.ApplyEdits(new[] { Wall(10) }, results);
            Assert.Equal(1, chunk.BuildCount);

            // Объект без записи на месте постройки — запись снимается (правка задаёт тайл целиком).
            chunk.ApplyEdits(new[] { new TileEdit(10, FloorId.Grass, ObjectId.Rock) }, results);
            Assert.Equal(0, chunk.BuildCount);
            Assert.Equal(ObjectId.Rock, chunk.GetObject(10));
            Assert.Equal(FloorId.Grass, chunk.GetFloor(10));
        }

        [Fact]
        public void D00_World_Conflict_TwoEditsSameTileSameTick_FirstWinsSecondBusy()
        {
            var chunk = new Chunk();
            TileEdit first = Wall(100, owner: 1);
            TileEdit second = new TileEdit(100, FloorId.StoneFloor, ObjectId.StoneWall, BuildRecord.OfPlayer(2, FloorId.Grass));
            TileEdit other = Wall(101, owner: 2);
            var results = new EditResult[3];

            Assert.Equal(1u, chunk.ApplyEdits(new[] { first, second, other }, results));
            Assert.Equal(new[] { EditResult.Applied, EditResult.Busy, EditResult.Applied }, results);
            Assert.Equal(ObjectId.WoodWall, chunk.GetObject(100));
            Assert.True(chunk.TryGetBuild(100, out BuildRecord rec));
            Assert.Equal((ushort)1, rec.Owner);

            // В пачку для клиентов «занятый» запрос не попадает.
            var buffer = new byte[256];
            int n = TilePatch.WriteAccepted(buffer, 0, new ChunkCoord(0, 0), 1, new[] { first, second, other }, results);
            TilePatch.Read(buffer.AsSpan(0, n), out TilePatch patch);
            Assert.Equal(new[] { first, other }, patch.Edits);
        }

        [Fact]
        public void D00_World_EditWireSize_5To13BytesPerTile()
        {
            var buffer = new byte[64];
            var applied = new[] { EditResult.Applied };
            int plain = TilePatch.WriteAccepted(buffer, 0, new ChunkCoord(1, 2), 7,
                new[] { new TileEdit(1023, FloorId.Dirt, ObjectId.Empty) }, applied);
            int withRecord = TilePatch.WriteAccepted(buffer, 0, new ChunkCoord(1, 2), 7, new[] { Wall(1023) }, applied);
            int perTilePlain = plain - TilePatch.HeaderBytes - TilePatch.CountBytes;
            int perTileRecord = withRecord - TilePatch.HeaderBytes - TilePatch.CountBytes;
            _out.WriteLine($"правка тайла: {perTilePlain} байт без записи, {perTileRecord} с записью; пачка из одной правки — {plain} / {withRecord} байт");
            Assert.Equal(5, perTilePlain);
            Assert.Equal(13, perTileRecord);
            Assert.Equal(11, TilePatch.HeaderBytes + TilePatch.CountBytes);
        }

        [Fact]
        public void D00_World_ServerAndClient_ByteIdenticalAfterPatches()
        {
            var shape = MapShape.World(1024);
            var server = new WorldModel(shape, Seed);
            var client = new WorldModel(shape, Seed);
            var c = new ChunkCoord(31, 0); // у шва
            Chunk sc = server.GetOrCreateChunk(c);
            for (int i = 0; i < Chunk.TileCount; i++)
            {
                FieldTile(c.X * CS + (i & 31), c.Y * CS + (i >> 5), out byte f, out ushort o);
                sc.SetBaseTile(i, f, o);
            }

            // Клиент получает чанк целиком: свежая сжатая копия с ревизией (3.5, 3.7).
            sc.RebuildCompressed();
            client.GetOrCreateChunk(c).Load(Chunk.Decompress(sc.FreshCopy!), sc.FreshCopy, sc.Revision, false);
            Assert.True(client.FindChunk(c)!.ContentEquals(sc));

            var rng = new SplitMix64(42);
            var edits = new TileEdit[40];
            var results = new EditResult[40];
            var wire = new byte[TilePatch.HeaderBytes + TilePatch.CountBytes + 40 * TileEdit.WireBytesWithRecord];
            for (int tick = 0; tick < 300; tick++)
            {
                int count = 1 + rng.NextInt(edits.Length);
                for (int i = 0; i < count; i++)
                {
                    // Узкий диапазон индексов — чтобы были и конфликты в такте.
                    ushort index = (ushort)rng.NextInt(256);
                    edits[i] = rng.NextInt(3) switch
                    {
                        0 => new TileEdit(index, FloorId.Grass, ObjectId.Empty),
                        1 => new TileEdit(index, FloorId.Dirt, ObjectId.MakeAnchor(ObjectId.Rock, (byte)rng.NextInt(4))),
                        _ => new TileEdit(index, FloorId.WoodFloor, ObjectId.Door,
                            new BuildRecord((ushort)rng.NextInt(8), (ushort)rng.Next(), (byte)rng.NextInt(8), FloorId.Grass, (ushort)tick)),
                    };
                }

                uint revision = server.ApplyEdits(c, edits.AsSpan(0, count), results);
                int bytes = TilePatch.WriteAccepted(wire, 0, c, revision, edits.AsSpan(0, count), results);
                Assert.True(bytes > 0);
                TilePatch.Read(wire.AsSpan(0, bytes), out TilePatch patch);
                Assert.True(client.ApplyPatch(patch));
                Assert.True(client.FindChunk(c)!.ContentEquals(sc), $"такт {tick}");
            }

            Assert.Equal(300u, sc.Revision);
            Assert.Equal(sc.Compress(), client.FindChunk(c)!.Compress());
            Assert.Equal(WorldSnapshot.ContentHash(server), WorldSnapshot.ContentHash(client));
        }

        [Fact]
        public void D00_World_Patch_RevisionGap_IsRefusedAndChunkUntouched()
        {
            var chunk = new Chunk();
            var gap = new TilePatch(0, new ChunkCoord(0, 0), 2, new[] { Wall(1) });
            Assert.False(chunk.ApplyPatch(gap));
            Assert.Equal(0u, chunk.Revision);
            Assert.Equal(ObjectId.Empty, chunk.GetObject(1));
            Assert.True(chunk.ApplyPatch(new TilePatch(0, new ChunkCoord(0, 0), 1, new[] { Wall(1) })));
            Assert.Equal(1u, chunk.Revision);
        }

        // Тесты «без выделений» (D00_World_Edits_NoAllocationsInTick, D00_World_WindowAndZone_NoAllocations) —
        // в AllocationTests: отдельная непараллельная коллекция с прогревом (0-020).

        // ---------- Сжатая копия (3.3, 3.6) ----------

        [Fact]
        public void D00_World_CompressedCopy_MarkedWithRevision_StaleAfterEdit_RebuildSameLength()
        {
            Chunk chunk = FieldChunk(5, 5);
            Assert.False(chunk.HasFreshCopy);
            int len = chunk.RebuildCompressed();
            byte[] copy = chunk.FreshCopy!;
            Assert.Equal(0u, chunk.CopyRevision);
            Assert.True(chunk.HasFreshCopy);

            ushort old = chunk.GetObject(77);
            byte oldFloor = chunk.GetFloor(77);
            var results = new EditResult[1];
            chunk.ApplyEdits(new[] { new TileEdit(77, FloorId.Sand, ObjectId.Torch) }, results);
            Assert.False(chunk.HasFreshCopy);
            Assert.Null(chunk.FreshCopy);
            Assert.Equal(0u, chunk.CopyRevision);

            // Вернули тайл — ревизия 2, содержимое прежнее: пересборка даёт ту же длину и те же байты.
            chunk.ApplyEdits(new[] { new TileEdit(77, oldFloor, old) }, results);
            Assert.Equal(len, chunk.RebuildCompressed());
            Assert.Equal(2u, chunk.CopyRevision);
            Assert.Equal(copy, chunk.FreshCopy);

            var back = new Chunk();
            back.ReadBlob(Chunk.Decompress(copy));
            Assert.True(back.Layers.SequenceEqual(chunk.Layers));
        }

        [Fact]
        public void D00_World_CompressedFieldChunk_ComparableToModel236()
        {
            var sizes = new List<int>();
            for (int i = 0; i < 64; i++)
            {
                sizes.Add(FieldChunk(i * 3, i * 5).RebuildCompressed());
            }

            sizes.Sort();
            double mean = 0;
            foreach (int s in sizes)
            {
                mean += s;
            }

            mean /= sizes.Count;
            int p95 = sizes[(int)(sizes.Count * 0.95)];
            int empty = new Chunk().RebuildCompressed();
            Chunk built = FieldChunk(0, 0);
            var results = new EditResult[Chunk.TileCount];
            var room = new List<TileEdit>();
            for (int y = 4; y < 20; y++)
            {
                for (int x = 4; x < 28; x++)
                {
                    bool wall = y == 4 || y == 19 || x == 4 || x == 27;
                    room.Add(wall ? Wall(x | (y << 5)) : new TileEdit((ushort)(x | (y << 5)), FloorId.WoodFloor, ObjectId.Empty));
                }
            }

            built.ApplyEdits(room.ToArray(), results);
            int roomBytes = built.RebuildCompressed();
            _out.WriteLine($"поле, 64 чанка: среднее {mean:F0}, 95-й {p95}, наибольший {sizes[sizes.Count - 1]} байт (модель 3.6: 236 / 341 / 425)");
            _out.WriteLine($"пустой чанк {empty} байт; поле + двор 24 × 16 со стенами ({built.BuildCount} записей) — {roomBytes} байт (модель базы 564)");
            Assert.InRange(mean, 120, 425);
            Assert.True(sizes[sizes.Count - 1] <= 600);
        }

        // ---------- Снимок генерации + изменения (3.4) ----------

        [Fact]
        public void D00_World_Snapshot_1024_SaveLoad_ByteIdentical()
        {
            var world = new WorldModel(MapShape.World(1024), Seed, generatorVersion: 3);
            FillField(world);
            byte[] snapshot = WorldSnapshot.WriteBase(world);
            byte[] hash = WorldSnapshot.ContentHash(world);
            _out.WriteLine($"world.base 1024²: {snapshot.Length} байт ({snapshot.Length / 1024.0:F0} КБ), чанков {world.ChunkCount}");

            WorldModel loaded = WorldSnapshot.LoadBase(snapshot);
            Assert.Equal(world.Shape, loaded.Shape);
            Assert.Equal(Seed, loaded.Seed);
            Assert.Equal(3, loaded.GeneratorVersion);
            Assert.Equal(world.ChunkCount, loaded.ChunkCount);
            ReadOnlySpan<ChunkCoord> keys = world.GetChunkCoords();
            for (int i = 0; i < keys.Length; i++)
            {
                Assert.True(loaded.FindChunk(keys[i])!.ContentEquals(world.FindChunk(keys[i])!), keys[i].ToString());
            }

            Assert.Equal(hash, WorldSnapshot.StoredHash(snapshot));
            Assert.Equal(hash, WorldSnapshot.ContentHash(loaded));
            Assert.Equal(snapshot, WorldSnapshot.WriteBase(loaded));
            Assert.Empty(loaded.GetChangedSinceBase());
            Assert.Empty(loaded.GetDirtyChunks());
        }

        [Fact]
        public void D00_World_Snapshot_ChangesHoldOnlyEditedChunks_BasePlusChangesIsSameWorld()
        {
            var world = new WorldModel(MapShape.World(1024), Seed, 1);
            FillField(world);
            byte[] snapshot = WorldSnapshot.WriteBase(world);
            Assert.Empty(world.GetChangedSinceBase());

            var edited = new[] { new ChunkCoord(0, 0), new ChunkCoord(31, 31), new ChunkCoord(7, 12) };
            var results = new EditResult[2];
            foreach (ChunkCoord c in edited)
            {
                world.ApplyEdits(c, new[] { Wall(5), new TileEdit(900, FloorId.Sand, ObjectId.Torch) }, results);
            }

            world.ApplyEdits(new ChunkCoord(7, 12), new[] { Wall(6) }, results);
            ChunkCoord[] changed = world.GetChangedSinceBase();
            Assert.Equal(new[] { new ChunkCoord(0, 0), new ChunkCoord(7, 12), new ChunkCoord(31, 31) }, changed);
            Assert.Equal(changed, world.GetDirtyChunks());

            byte[] changes = WorldSnapshot.WriteChanges(world);
            _out.WriteLine($"изменения: {changed.Length} чанка — {changes.Length} байт");
            world.ClearAllDirty();
            Assert.Empty(world.GetDirtyChunks());
            Assert.Equal(changed, world.GetChangedSinceBase()); // и в следующее сохранение

            WorldModel loaded = WorldSnapshot.Load(snapshot, changes);
            Assert.Equal(WorldSnapshot.ContentHash(world), WorldSnapshot.ContentHash(loaded));
            Assert.Equal(2u, loaded.FindChunk(new ChunkCoord(7, 12))!.Revision);
            Assert.Equal(changed, loaded.GetChangedSinceBase());
            Assert.Empty(loaded.GetDirtyChunks());
            Assert.Equal(changes, WorldSnapshot.WriteChanges(loaded));
        }

        [Fact]
        public void D00_World_Snapshot_DamageAndForeignChanges_AreRejected()
        {
            var world = new WorldModel(MapShape.World(256), Seed, 1);
            FillField(world);
            byte[] snapshot = WorldSnapshot.WriteBase(world);

            byte[] damaged = (byte[])snapshot.Clone();
            damaged[damaged.Length - 40] ^= 0x5A;
            Assert.Throws<InvalidDataException>(() => WorldSnapshot.LoadBase(damaged));
            Assert.Throws<InvalidDataException>(() => WorldSnapshot.LoadBase(snapshot.AsSpan(0, snapshot.Length - 1).ToArray()));
            byte[] badHash = (byte[])snapshot.Clone();
            badHash[WorldSnapshot.HeaderBytes] ^= 1;
            Assert.Throws<InvalidDataException>(() => WorldSnapshot.LoadBase(badHash));

            var other = new WorldModel(MapShape.World(256), Seed + 1, 1);
            other.ApplyEdits(new ChunkCoord(1, 1), new[] { Wall(3) }, new EditResult[1]);
            byte[] foreign = WorldSnapshot.WriteChanges(other);
            WorldModel loaded = WorldSnapshot.LoadBase(snapshot);
            Assert.Throws<InvalidDataException>(() => WorldSnapshot.ApplyChanges(loaded, foreign));
            Assert.Throws<InvalidDataException>(() => WorldSnapshot.LoadBase(foreign));
            Assert.Equal(WorldSnapshot.ContentHash(world), WorldSnapshot.ContentHash(loaded));
        }

        [Fact]
        [Trait("Category", "Big")]
        public void D00_World_BigSnapshot_4096_RoundTrip()
        {
            var world = new WorldModel(MapShape.World(4096), Seed, 1);
            FillField(world);
            byte[] snapshot = WorldSnapshot.WriteBase(world);
            _out.WriteLine($"world.base 4096² (поле): {snapshot.Length} байт = {snapshot.Length / 1048576.0:F2} МБ (расчёт 3.4: 3,7 МБ)");
            WorldModel loaded = WorldSnapshot.LoadBase(snapshot);
            Assert.Equal(WorldSnapshot.ContentHash(world), WorldSnapshot.ContentHash(loaded));
        }

        // ---------- Окно клиента (3.5) ----------

        private static WorldPos TileCenter(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private static ChunkCoord[] RunWindow(in MapShape map, WorldPos p, ChunkCoord[] current)
        {
            var next = new ChunkCoord[ChunkWindow.MaxChunks];
            int n = ChunkWindow.Update(map, p, current, next);
            return next.AsSpan(0, n).ToArray();
        }

        private static void AssertSortedUnique(ChunkCoord[] set)
        {
            for (int i = 1; i < set.Length; i++)
            {
                Assert.True(ChunkCoordOrder.Compare(set[i - 1], set[i]) < 0, $"не по порядку или дубль: {set[i - 1]}, {set[i]}");
            }
        }

        [Theory]
        [InlineData(1024)]
        [InlineData(4096)]
        public void D00_World_Window_AtWorldCorner_BothEdgesNoDuplicates(int size)
        {
            var map = MapShape.World(size);
            int last = map.ChunksX - 1;
            ChunkCoord[] window = RunWindow(map, TileCenter(5, 5), Array.Empty<ChunkCoord>());
            Assert.Equal(24, window.Length);
            AssertSortedUnique(window);
            Assert.Contains(new ChunkCoord(0, 0), window);
            Assert.Contains(new ChunkCoord(last, last), window);
            Assert.Contains(new ChunkCoord(last - 2, 0), window);
            Assert.Contains(new ChunkCoord(2, last - 1), window);

            // Запас до края блока: ≥ 80 тайлов по X и ≥ 48 по Y при любом положении в чанке.
            for (int t = 0; t < 2 * CS; t++)
            {
                WorldPos p = TileCenter(size - CS + t, size - CS + t);
                ChunkCoord center = ChunkWindow.BlockCenter(map, p);
                var left = new TileCoord((center.X - 3) * CS, (center.Y - 2) * CS);
                Int2 d = Torus.Delta(map, Torus.CornerOf(left), p);
                Assert.InRange(d.X, 80 * U, 112 * U);
                Assert.InRange(d.Y, 48 * U, 80 * U);
            }
        }

        [Theory]
        [InlineData(1024, 1, 0)]
        [InlineData(4096, 1, 0)]
        [InlineData(1024, 0, -1)]
        [InlineData(1024, 1, 1)]
        public void D00_World_Window_RunAcrossSeam_ShiftsByOneChunk_OldBlockKept_AtMost48(int size, int dirX, int dirY)
        {
            var map = MapShape.World(size);
            // 6 т/с при 30 Гц: 51/256 тайла за такт по оси, по диагонали — 36/256 по каждой оси.
            int step = dirX != 0 && dirY != 0 ? 36 : 51;
            WorldPos p = TileCenter(size - 150, size - 150);
            ChunkCoord[] window = RunWindow(map, p, Array.Empty<ChunkCoord>());
            ChunkCoord[] block = window;
            var added = new ChunkCoord[48];
            var removed = new ChunkCoord[48];
            int totalAdded = 0;
            int ticks = 3000; // 100 с, ≈ 600 тайлов: через шов и дальше
            int maxSeen = 0;
            for (int tick = 0; tick < ticks; tick++)
            {
                p = Torus.Offset(map, p, new Int2(dirX * step, dirY * step));
                ChunkCoord[] next = RunWindow(map, p, window);
                AssertSortedUnique(next);
                Assert.True(next.Length <= ChunkWindow.MaxChunks);
                maxSeen = Math.Max(maxSeen, next.Length);
                ChunkSets.Diff(window, next, added, out int a, removed, out int r);
                totalAdded += a;

                // Блок входа сдвинулся на 1 чанк — прежний блок 6 × 4 остаётся целиком (гистерезис).
                ChunkCoord[] newBlock = RunWindow(map, p, Array.Empty<ChunkCoord>());
                if (!newBlock.AsSpan().SequenceEqual(block))
                {
                    foreach (ChunkCoord c in block)
                    {
                        Assert.Contains(c, next);
                    }

                    Assert.True(a <= ChunkWindow.Width + ChunkWindow.Height - 1, $"вошло {a}");
                    block = newBlock;
                }
                else
                {
                    Assert.Equal(0, a);
                }

                foreach (ChunkCoord c in newBlock)
                {
                    Assert.Contains(c, next);
                }

                window = next;
            }

            double seconds = ticks / 30.0;
            double perSecond = totalAdded / seconds;
            _out.WriteLine($"{size}², направление ({dirX}, {dirY}): вошло {totalAdded} чанков за {seconds:F0} с — {perSecond:F2} чанка/с, " +
                           $"{perSecond * 236 / 1024:F2} КБ/с при 236 байтах; окно не больше {maxSeen}");
            Assert.True(perSecond <= 1.45, $"{perSecond:F2} чанка/с");
            Assert.True(perSecond * 236 <= 512);
        }

        [Fact]
        public void D00_World_Window_BackAndForthOverBoundary_NoChurn()
        {
            var map = MapShape.World(1024);
            // Граница смены центра блока по X — середина чанка (x = 16 тайлов в чанке 10).
            int switchX = 10 * CS + CS / 2;
            ChunkCoord[] window = RunWindow(map, TileCenter(switchX - 1, 300), Array.Empty<ChunkCoord>());
            window = RunWindow(map, TileCenter(switchX + 1, 300), window);
            for (int k = 0; k < 50; k++)
            {
                ChunkCoord[] next = RunWindow(map, TileCenter(k % 2 == 0 ? switchX - 1 : switchX + 1, 300), window);
                Assert.Equal(window, next);
                window = next;
            }

            Assert.Equal(28, window.Length);
        }

        [Fact]
        public void D00_World_Window_WiderThanMap_EachChunkOnce_FlatMapClipped()
        {
            var tiny = MapShape.World(128); // 4 × 4 чанка
            ChunkCoord[] all = RunWindow(tiny, TileCenter(10, 120), Array.Empty<ChunkCoord>());
            Assert.Equal(16, all.Length);
            AssertSortedUnique(all);

            var dungeon = MapShape.Flat(300, 200); // 10 × 7 чанков
            ChunkCoord[] corner = RunWindow(dungeon, TileCenter(1, 1), Array.Empty<ChunkCoord>());
            Assert.Equal(3 * 2, corner.Length); // центр блока (0, 0): от [−3, 2] × [−2, 1] на карте x 0…2, y 0…1
            foreach (ChunkCoord c in corner)
            {
                Assert.InRange(c.X, 0, dungeon.ChunksX - 1);
                Assert.InRange(c.Y, 0, dungeon.ChunksY - 1);
            }
        }

        [Fact]
        public void D00_World_Window_MatchesReferenceOnRandomWalk()
        {
            var map = MapShape.World(1024);
            var rng = new SplitMix64(7);
            WorldPos p = TileCenter(1000, 20);
            var window = Array.Empty<ChunkCoord>();
            var reference = new HashSet<ChunkCoord>();
            for (int tick = 0; tick < 3000; tick++)
            {
                p = Torus.Offset(map, p, new Int2(rng.NextInt(4 * U + 1) - 2 * U, rng.NextInt(4 * U + 1) - 2 * U));
                window = RunWindow(map, p, window);

                // Эталон «в лоб»: координаты тайлов, без Torus.Delta по чанкам.
                TileCoord t = Torus.TileOf(map, p);
                int cx = (t.X + CS / 2) / CS;
                int cy = (t.Y + CS / 2) / CS;
                reference.RemoveWhere(c => !InBlock(map, c, cx, cy, 4, 3));
                for (int dy = -2; dy < 2; dy++)
                {
                    for (int dx = -3; dx < 3; dx++)
                    {
                        reference.Add(Torus.Wrap(map, new ChunkCoord(cx + dx, cy + dy)));
                    }
                }

                Assert.Equal(reference.Count, window.Length);
                foreach (ChunkCoord c in window)
                {
                    Assert.Contains(c, reference);
                }
            }
        }

        // Чанк c — в блоке [cx − hx, cx + hx − 1] × [cy − hy, cy + hy − 1] по тору (перебор сдвигов на размер карты).
        private static bool InBlock(MapShape map, ChunkCoord c, int cx, int cy, int hx, int hy)
        {
            for (int dx = -hx; dx < hx; dx++)
            {
                for (int dy = -hy; dy < hy; dy++)
                {
                    if (Torus.Wrap(map, new ChunkCoord(cx + dx, cy + dy)) == c)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // ---------- Активная зона (3.5) ----------

        private static ChunkCoord[] RunZone(in MapShape map, WorldPos[] players, ChunkCoord[] current)
        {
            var next = new ChunkCoord[ActiveZone.MaxChunks(map, players.Length)];
            int n = ActiveZone.Update(map, players, current, next);
            return next.AsSpan(0, n).ToArray();
        }

        private static WorldPos ChunkCenter(int cx, int cy) => TileCenter(cx * CS + CS / 2, cy * CS + CS / 2);

        [Fact]
        public void D00_World_ActiveZone_Enter7x7_Exit9x9()
        {
            var map = MapShape.World(1024);
            ChunkCoord[] zone = RunZone(map, new[] { ChunkCenter(10, 10) }, Array.Empty<ChunkCoord>());
            Assert.Equal(49, zone.Length);
            AssertSortedUnique(zone);
            Assert.Contains(new ChunkCoord(7, 7), zone);
            Assert.Contains(new ChunkCoord(13, 13), zone);
            Assert.DoesNotContain(new ChunkCoord(14, 10), zone);

            // На 1 чанк восточнее: вошёл столбец x = 14, столбец x = 7 остался (он в 9 × 9).
            zone = RunZone(map, new[] { ChunkCenter(11, 10) }, zone);
            Assert.Equal(56, zone.Length);
            Assert.Contains(new ChunkCoord(7, 10), zone);
            Assert.Contains(new ChunkCoord(14, 10), zone);

            // Ещё на 1: столбец x = 7 вышел из 9 × 9 — ушёл.
            zone = RunZone(map, new[] { ChunkCenter(12, 10) }, zone);
            Assert.DoesNotContain(new ChunkCoord(7, 10), zone);
            Assert.Contains(new ChunkCoord(8, 10), zone);
            Assert.Equal(56, zone.Length);

            // Нет игроков — зона пуста.
            Assert.Empty(RunZone(map, Array.Empty<WorldPos>(), zone));
        }

        [Fact]
        public void D00_World_ActiveZone_TwoPlayers_Union_NoDuplicates()
        {
            var map = MapShape.World(1024);
            ChunkCoord[] far = RunZone(map, new[] { ChunkCenter(5, 5), ChunkCenter(20, 20) }, Array.Empty<ChunkCoord>());
            Assert.Equal(98, far.Length);

            ChunkCoord[] near = RunZone(map, new[] { ChunkCenter(5, 5), ChunkCenter(8, 6) }, Array.Empty<ChunkCoord>());
            AssertSortedUnique(near);
            Assert.Equal(49 + 49 - 4 * 6, near.Length); // пересечение x 5…8, y 3…8

            // Второй ушёл — его чанки, что в 9 × 9 первого, остаются; остальные уходят.
            ChunkCoord[] after = RunZone(map, new[] { ChunkCenter(5, 5) }, near);
            Assert.Contains(new ChunkCoord(9, 9), after);
            Assert.DoesNotContain(new ChunkCoord(10, 5), after);
            int expected = 0;
            foreach (ChunkCoord c in near)
            {
                if (Math.Abs(c.X - 5) <= 4 && Math.Abs(c.Y - 5) <= 4)
                {
                    expected++;
                }
            }

            Assert.Equal(expected, after.Length);
            Assert.Equal(49 + 7 + 4, expected); // свои 49 + чанки второго в 9 × 9 первого: x 9 при y 3…9, y 9 при x 5…8
        }

        [Fact]
        public void D00_World_ActiveZone_OnTorus_CornerWraps_SmallMapEachChunkOnce()
        {
            var map = MapShape.World(1024);
            ChunkCoord[] zone = RunZone(map, new[] { ChunkCenter(0, 0) }, Array.Empty<ChunkCoord>());
            Assert.Equal(49, zone.Length);
            AssertSortedUnique(zone);
            Assert.Contains(new ChunkCoord(29, 29), zone);
            Assert.Contains(new ChunkCoord(31, 3), zone);

            var tiny = MapShape.World(128);
            ChunkCoord[] all = RunZone(tiny, new[] { ChunkCenter(1, 1), ChunkCenter(3, 3) }, Array.Empty<ChunkCoord>());
            Assert.Equal(16, all.Length);
            AssertSortedUnique(all);
        }

        [Fact]
        public void D00_World_ActiveZone_MatchesReferenceOnRandomWalk()
        {
            var map = MapShape.World(1024);
            var rng = new SplitMix64(11);
            var players = new[] { TileCenter(5, 5), TileCenter(1000, 1000), TileCenter(40, 1010) };
            var zone = Array.Empty<ChunkCoord>();
            var reference = new HashSet<ChunkCoord>();
            for (int tick = 0; tick < 1500; tick++)
            {
                for (int i = 0; i < players.Length; i++)
                {
                    players[i] = Torus.Offset(map, players[i], new Int2(rng.NextInt(8 * U + 1) - 4 * U, rng.NextInt(8 * U + 1) - 4 * U));
                }

                zone = RunZone(map, players, zone);
                AssertSortedUnique(zone);

                reference.RemoveWhere(c =>
                {
                    foreach (WorldPos p in players)
                    {
                        ChunkCoord pc = Torus.ChunkOf(map, p);
                        if (InSquare(map, c, pc, 4))
                        {
                            return false;
                        }
                    }

                    return true;
                });
                foreach (WorldPos p in players)
                {
                    ChunkCoord pc = Torus.ChunkOf(map, p);
                    for (int dy = -3; dy <= 3; dy++)
                    {
                        for (int dx = -3; dx <= 3; dx++)
                        {
                            reference.Add(Torus.Wrap(map, new ChunkCoord(pc.X + dx, pc.Y + dy)));
                        }
                    }
                }

                Assert.Equal(reference.Count, zone.Length);
                foreach (ChunkCoord c in zone)
                {
                    Assert.Contains(c, reference);
                }
            }
        }

        private static bool InSquare(MapShape map, ChunkCoord c, ChunkCoord center, int r)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Torus.Wrap(map, new ChunkCoord(center.X + dx, center.Y + dy)) == c)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
