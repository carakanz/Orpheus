using System;
using System.Collections.Generic;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;

namespace Orpheus.Save
{
    /// <summary>
    /// Состояние мира, снятое на границе такта (docs/07, 6.4): не ссылается на живой мир, поэтому сжатие
    /// и запись идут в фоновом потоке, пока такт работает дальше. Чанк со свежей сжатой копией берётся
    /// ссылкой на копию (она неизменна: пересборка создаёт новый массив), остальные — копией сырого блока
    /// (3 КБ + записи построек), сжимаются при сборке файла.
    /// </summary>
    public sealed class WorldSaveData
    {
        internal WorldSaveData(
            string name, ulong seed, MapShape shape, int generatorVersion, byte[] baseSha256, long tick, int nextEntityId,
            ChunkCoord[] coords, uint[] revisions, byte[]?[] compressed, byte[]?[] raw, SimEntity[] entities)
        {
            Name = name;
            Seed = seed;
            Shape = shape;
            GeneratorVersion = generatorVersion;
            BaseSha256 = baseSha256;
            Tick = tick;
            NextEntityId = nextEntityId;
            ChunkCoords = coords;
            Revisions = revisions;
            Compressed = compressed;
            RawBlobs = raw;
            Entities = entities;
        }

        public string Name { get; }
        public ulong Seed { get; }
        public MapShape Shape { get; }
        public int GeneratorVersion { get; }
        public byte[] BaseSha256 { get; }

        /// <summary>Такт, на границе которого снято состояние.</summary>
        public long Tick { get; }

        public int NextEntityId { get; }

        /// <summary>Изменённые после генерации чанки по порядку (Y, X).</summary>
        public ChunkCoord[] ChunkCoords { get; }

        internal uint[] Revisions { get; }
        internal byte[]?[] Compressed { get; }
        internal byte[]?[] RawBlobs { get; }

        /// <summary>Сущности по номеру (копии).</summary>
        public SimEntity[] Entities { get; }
    }

    /// <summary>Загруженное из сохранения состояние такта (мир уже положен в <see cref="WorldModel"/>).</summary>
    public sealed class LoadedWorldState
    {
        internal LoadedWorldState(ulong number, long tick, string name, int nextEntityId, SimEntity[] entities, int chunks)
        {
            Number = number;
            Tick = tick;
            Name = name;
            NextEntityId = nextEntityId;
            Entities = entities;
            ChunkCount = chunks;
        }

        /// <summary>Номер сохранения.</summary>
        public ulong Number { get; }

        public long Tick { get; }

        public string Name { get; }

        public int NextEntityId { get; }

        public SimEntity[] Entities { get; }

        /// <summary>Сколько изменённых чанков положено поверх снимка генерации.</summary>
        public int ChunkCount { get; }

        /// <summary>Положить сущности и такт в пустую симуляцию этого мира.</summary>
        public void RestoreInto(Simulation sim)
        {
            if (sim == null)
            {
                throw new ArgumentNullException(nameof(sim));
            }

            sim.Restore(Tick, Entities, NextEntityId);
        }
    }

    /// <summary>
    /// Мир в контейнер и обратно (docs/07, 6.2–6.3; версия формата 1). Разделы:
    /// <list type="bullet">
    /// <item>МИР — имя, зерно, форма, версия генератора, SHA-256 снимка генерации (<c>world.base</c>), следующий номер сущности;</item>
    /// <item>ЧАНКИ — изменённые после генерации чанки (0-005: <see cref="Chunk.ChangedSinceBase"/>): карта, x, y, ревизия, сжатый блок;</item>
    /// <item>СУЩЕСТВА — минимально: сущности такта со всеми полями хэша состояния (0-006).</item>
    /// </list>
    /// Загрузка: снимок генерации → поверх изменённые чанки → сущности и такт. Хэш состояния после загрузки
    /// равен хэшу на такте сохранения (тест <c>D00_Save_RoundTrip_*</c>).
    /// </summary>
    public static class WorldSave
    {
        /// <summary>
        /// Снять состояние на границе такта — между <see cref="Simulation.Step"/> (основной поток, быстро:
        /// ссылки на свежие копии, копии сырых блоков остальных изменённых чанков, копия списка сущностей).
        /// </summary>
        public static WorldSaveData Capture(Simulation sim, byte[] baseSha256, string name)
        {
            if (sim == null)
            {
                throw new ArgumentNullException(nameof(sim));
            }

            if (baseSha256 == null || baseSha256.Length != 32)
            {
                throw new ArgumentException("Нужен SHA-256 снимка генерации (32 байта).", nameof(baseSha256));
            }

            WorldModel world = sim.World;
            ChunkCoord[] coords = world.GetChangedSinceBase();
            var revisions = new uint[coords.Length];
            var compressed = new byte[]?[coords.Length];
            var raw = new byte[]?[coords.Length];
            for (int i = 0; i < coords.Length; i++)
            {
                Chunk chunk = world.FindChunk(coords[i])!;
                revisions[i] = chunk.Revision;
                compressed[i] = chunk.FreshCopy;
                if (compressed[i] == null)
                {
                    var blob = new byte[chunk.BlobBytes];
                    chunk.WriteBlob(blob);
                    raw[i] = blob;
                }
            }

            return new WorldSaveData(
                name ?? string.Empty, world.Seed, world.Shape, world.GeneratorVersion, (byte[])baseSha256.Clone(),
                sim.Tick, sim.NextEntityId, coords, revisions, compressed, raw, sim.Entities.ToArray());
        }

        /// <summary>Собрать файл сохранения (любой поток). Дата — мс Unix UTC от вызывающего.</summary>
        public static byte[] Build(WorldSaveData d, ulong number, long dateUnixMs)
        {
            if (d == null)
            {
                throw new ArgumentNullException(nameof(d));
            }

            var w = new RecordWriter(256);
            w.WriteText(SaveSections.WorldField.Name, d.Name);
            w.WriteUInt(SaveSections.WorldField.Seed, d.Seed);
            w.WriteUInt(SaveSections.WorldField.Width, (ulong)d.Shape.Width);
            w.WriteUInt(SaveSections.WorldField.Height, (ulong)d.Shape.Height);
            w.WriteBool(SaveSections.WorldField.Wraps, d.Shape.Wraps);
            w.WriteUInt(SaveSections.WorldField.GeneratorVersion, (ulong)d.GeneratorVersion);
            w.WriteBytes(SaveSections.WorldField.BaseSha256, d.BaseSha256);
            w.WriteUInt(SaveSections.WorldField.NextEntityId, (ulong)d.NextEntityId);
            byte[] worldSection = w.ToArray();

            var c = new RecordWriter(d.ChunkCoords.Length * 320 + 16);
            for (int i = 0; i < d.ChunkCoords.Length; i++)
            {
                byte[] data = d.Compressed[i] ?? Chunk.CompressBlob(d.RawBlobs[i]!);
                int mark = c.BeginRecord(SaveSections.ChunksField.Chunk);
                c.WriteUInt(SaveSections.ChunkField.Map, 0);
                c.WriteUInt(SaveSections.ChunkField.X, (ulong)d.ChunkCoords[i].X);
                c.WriteUInt(SaveSections.ChunkField.Y, (ulong)d.ChunkCoords[i].Y);
                c.WriteUInt(SaveSections.ChunkField.Revision, d.Revisions[i]);
                c.WriteBytes(SaveSections.ChunkField.Data, data);
                c.EndRecord(mark);
            }

            var e = new RecordWriter(d.Entities.Length * 48 + 16);
            for (int i = 0; i < d.Entities.Length; i++)
            {
                SimEntity x = d.Entities[i];
                int mark = e.BeginRecord(SaveSections.CreaturesField.Entity);
                e.WriteUInt(SaveSections.EntityField.Id, (ulong)(uint)x.Id);
                e.WriteUInt(SaveSections.EntityField.Kind, x.Kind);
                e.WriteInt(SaveSections.EntityField.X, x.Mover.Position.X);
                e.WriteInt(SaveSections.EntityField.Y, x.Mover.Position.Y);
                e.WriteInt(SaveSections.EntityField.CarryX, x.Mover.Carry.X);
                e.WriteInt(SaveSections.EntityField.CarryY, x.Mover.Carry.Y);
                e.WriteInt(SaveSections.EntityField.SpeedX10, x.Mover.UnitsPerTickX10);
                e.WriteUInt(SaveSections.EntityField.Direction, x.Input.DirectionCode);
                e.WriteUInt(SaveSections.EntityField.Buttons, x.Input.Buttons);
                e.EndRecord(mark);
            }

            var header = new SaveHeader(SaveModule.FormatVersion, SaveModule.BuildVersion, number, d.Tick, dateUnixMs);
            return SaveContainer.Write(header, new[]
            {
                new SaveSection(SaveSections.World, worldSection),
                new SaveSection(SaveSections.Chunks, c.ToArray()),
                new SaveSection(SaveSections.Creatures, e.ToArray()),
            });
        }

        /// <summary>
        /// Положить сохранение поверх мира, загруженного из снимка генерации. Сначала всё разбирается
        /// и проверяется, потом трогается мир: ошибка не оставляет мир наполовину загруженным.
        /// Несовпадение с миром (зерно, форма, версия генератора, SHA-256 снимка) — <see cref="InvalidDataException"/>.
        /// </summary>
        public static LoadedWorldState Load(SaveFile file, WorldModel world, byte[] baseSha256)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            file.EnsureOk();

            // МИР.
            string name = string.Empty;
            ulong seed = 0;
            int width = 0;
            int height = 0;
            bool wraps = false;
            int generatorVersion = 0;
            byte[] sha = Array.Empty<byte>();
            int nextId = 1;
            var r = new RecordReader(file.Section(SaveSections.World));
            while (r.Next())
            {
                switch (r.Field)
                {
                    case SaveSections.WorldField.Name:
                        name = r.Text();
                        break;
                    case SaveSections.WorldField.Seed:
                        seed = r.UInt();
                        break;
                    case SaveSections.WorldField.Width:
                        width = r.UIntAsInt();
                        break;
                    case SaveSections.WorldField.Height:
                        height = r.UIntAsInt();
                        break;
                    case SaveSections.WorldField.Wraps:
                        wraps = r.Bool();
                        break;
                    case SaveSections.WorldField.GeneratorVersion:
                        generatorVersion = r.UIntAsInt();
                        break;
                    case SaveSections.WorldField.BaseSha256:
                        sha = r.Bytes().ToArray();
                        break;
                    case SaveSections.WorldField.NextEntityId:
                        nextId = r.UIntAsInt();
                        break;
                }
            }

            MapShape shape = world.Shape;
            if (seed != world.Seed || width != shape.Width || height != shape.Height || wraps != shape.Wraps || generatorVersion != world.GeneratorVersion)
            {
                throw new InvalidDataException(
                    $"Сохранение от другого мира: зерно {seed}, {width}×{height}{(wraps ? " тор" : "")}, генератор v{generatorVersion}; " +
                    $"снимок генерации — зерно {world.Seed}, {shape}, генератор v{world.GeneratorVersion}.");
            }

            if (baseSha256 != null && !((ReadOnlySpan<byte>)sha).SequenceEqual(baseSha256))
            {
                throw new InvalidDataException("Сохранение от другого снимка генерации: SHA-256 world.base не совпал.");
            }

            // ЧАНКИ.
            var coords = new List<ChunkCoord>();
            var revisions = new List<uint>();
            var blobs = new List<byte[]>();
            var copies = new List<byte[]>();
            var check = new Chunk();
            int total = shape.ChunksX * shape.ChunksY;
            r = new RecordReader(file.Section(SaveSections.Chunks));
            while (r.Next())
            {
                if (r.Field != SaveSections.ChunksField.Chunk)
                {
                    continue;
                }

                int map = 0;
                int x = -1;
                int y = -1;
                uint revision = 0;
                byte[]? data = null;
                RecordReader cr = r.Record();
                while (cr.Next())
                {
                    switch (cr.Field)
                    {
                        case SaveSections.ChunkField.Map:
                            map = cr.UIntAsInt();
                            break;
                        case SaveSections.ChunkField.X:
                            x = cr.UIntAsInt();
                            break;
                        case SaveSections.ChunkField.Y:
                            y = cr.UIntAsInt();
                            break;
                        case SaveSections.ChunkField.Revision:
                            ulong rv = cr.UInt();
                            if (rv > uint.MaxValue)
                            {
                                throw new InvalidDataException($"Чанк № {coords.Count}: ревизия {rv} больше 32 бит.");
                            }

                            revision = (uint)rv;
                            break;
                        case SaveSections.ChunkField.Data:
                            data = cr.Bytes().ToArray();
                            break;
                    }
                }

                int index = coords.Count;
                if (map != 0)
                {
                    throw new InvalidDataException($"Чанк № {index}: карта {map} — отдельных карт в формате 1 нет.");
                }

                if ((uint)x >= (uint)shape.ChunksX || (uint)y >= (uint)shape.ChunksY || data == null)
                {
                    throw new InvalidDataException($"Чанк № {index}: ({x}, {y}) вне карты {shape} или без данных.");
                }

                var c = new ChunkCoord(x, y);
                if (index > 0 && ChunkCoordOrder.Compare(coords[index - 1], c) >= 0)
                {
                    throw new InvalidDataException($"Чанк № {index} {c} повторён или не по порядку (после {coords[index - 1]}).");
                }

                if (index >= total)
                {
                    throw new InvalidDataException($"Чанков больше, чем на карте ({total}).");
                }

                byte[] blob = Chunk.Decompress(data);
                check.ReadBlob(blob); // проверка блока до того, как трогать мир
                coords.Add(c);
                revisions.Add(revision);
                blobs.Add(blob);
                copies.Add(data);
            }

            // СУЩЕСТВА.
            var entities = new List<SimEntity>();
            r = new RecordReader(file.Section(SaveSections.Creatures));
            while (r.Next())
            {
                if (r.Field != SaveSections.CreaturesField.Entity)
                {
                    continue;
                }

                SimEntity e = ReadEntity(r.Record(), shape);
                if (entities.Count > 0 && e.Id <= entities[entities.Count - 1].Id)
                {
                    throw new InvalidDataException($"Сущность #{e.Id}: номер не больше предыдущего #{entities[entities.Count - 1].Id}.");
                }

                entities.Add(e);
            }

            // Всё разобрано — кладём чанки.
            for (int i = 0; i < coords.Count; i++)
            {
                world.GetOrCreateChunk(coords[i]).Load(blobs[i], copies[i], revisions[i], changedSinceBase: true);
            }

            return new LoadedWorldState(file.Header.Number, file.Header.WorldTick, name, nextId, entities.ToArray(), coords.Count);
        }

        private static SimEntity ReadEntity(RecordReader r, MapShape shape)
        {
            int id = 0;
            int kind = 0;
            int x = 0;
            int y = 0;
            int carryX = 0;
            int carryY = 0;
            int speed = MovementTick.BaseSpeedX10;
            int direction = 0;
            int buttons = 0;
            while (r.Next())
            {
                switch (r.Field)
                {
                    case SaveSections.EntityField.Id:
                        id = r.UIntAsInt();
                        break;
                    case SaveSections.EntityField.Kind:
                        kind = r.UIntAsInt(byte.MaxValue);
                        break;
                    case SaveSections.EntityField.X:
                        x = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.Y:
                        y = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.CarryX:
                        carryX = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.CarryY:
                        carryY = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.SpeedX10:
                        speed = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.Direction:
                        direction = r.UIntAsInt(InputFrame.MaxDirectionCode);
                        break;
                    case SaveSections.EntityField.Buttons:
                        buttons = r.UIntAsInt(ushort.MaxValue);
                        break;
                }
            }

            if (id <= 0)
            {
                throw new InvalidDataException($"Сущность без номера (номер {id}).");
            }

            if ((uint)x >= (uint)shape.WidthUnits || (uint)y >= (uint)shape.HeightUnits)
            {
                throw new InvalidDataException($"Сущность #{id}: позиция ({x}, {y}) вне карты {shape}.");
            }

            return new SimEntity
            {
                Id = id,
                Kind = (byte)kind,
                Mover = new Mover(new WorldPos(x, y), speed) { Carry = new Int2(carryX, carryY) },
                Input = InputFrame.FromCode((byte)direction, (ushort)buttons),
            };
        }
    }
}
