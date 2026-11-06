using System;
using System.IO;
using System.Security.Cryptography;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// «Снимок генерации + изменённые чанки» (docs/07, 3.4). Снимок генерации (<c>world.base</c>) — все чанки
    /// сжатыми, версия генератора, зерно и SHA-256 содержимого. Изменения — чанки, изменённые после генерации,
    /// целиком и сжатыми, в том же формате. Загрузка: чанки снимка, поверх — изменённые.
    /// <para>Формат — свой двоичный, младший байт первым, без рефлексии и без JSON:</para>
    /// <list type="bullet">
    /// <item>«ORPW» (4), версия формата (1), вид (1: 0 — снимок генерации, 1 — изменения);</item>
    /// <item>версия генератора (4), зерно (8), ширина (4), высота (4), тор (1), число чанков (4);</item>
    /// <item>SHA-256 заголовка и содержимого (32);</item>
    /// <item>чанки строго по возрастанию (Y, затем X): x (4), y (4), ревизия (4), длина (4), сжатый блок (Deflate-6).</item>
    /// </list>
    /// <para>
    /// SHA-256 считается по заголовку (31 байт: метка, версия, вид, версия генератора, зерно, форма, число чанков —
    /// backlog [сохранения], с версии формата 2) и по несжатому содержимому — по каждому чанку x, y, ревизия
    /// и блок <see cref="Chunk.WriteBlob"/>. Так перевёрнутый бит зерна не грузится молча, а хэш не зависит
    /// от версии zlib: «удалили world.base — генерация повторилась, хэш совпал» (3.13 п. 5) работает и при
    /// другом сжатии. Чанк дважды или не по порядку — отказ (backlog [сохранения]). Атомарная запись в файл —
    /// <c>Orpheus.Save</c> (<c>IPlatformStorage</c>), здесь — только байты.
    /// </para>
    /// </summary>
    public static class WorldSnapshot
    {
        /// <summary>Метка «ORPW».</summary>
        public static ReadOnlySpan<byte> Magic => new byte[] { (byte)'O', (byte)'R', (byte)'P', (byte)'W' };

        /// <summary>Версия формата: 2 — SHA-256 покрывает и заголовок (0-009).</summary>
        public const byte FormatVersion = 2;

        /// <summary>Вид: снимок генерации.</summary>
        public const byte KindBase = 0;

        /// <summary>Вид: изменённые чанки.</summary>
        public const byte KindChanges = 1;

        /// <summary>Заголовок до хэша: 4 + 1 + 1 + 4 + 8 + 4 + 4 + 1 + 4 = 31 байт.</summary>
        public const int HeaderBytes = 31;

        /// <summary>SHA-256: 32 байта.</summary>
        public const int HashBytes = 32;

        /// <summary>Заголовок чанка в блоке: x, y, ревизия, длина — по 4 байта.</summary>
        public const int ChunkHeaderBytes = 16;

        private const int MaxBlobBytes = Chunk.MaxBlobBytes;

        // ---------- Запись ----------

        /// <summary>Снимок генерации: все созданные чанки.</summary>
        public static byte[] WriteBase(WorldModel model) => ToArray(model, KindBase);

        /// <summary>Изменённые после генерации чанки (<see cref="Chunk.ChangedSinceBase"/>).</summary>
        public static byte[] WriteChanges(WorldModel model) => ToArray(model, KindChanges);

        /// <summary>Снимок генерации в поток.</summary>
        public static void WriteBase(WorldModel model, Stream output) => Write(model, KindBase, output);

        /// <summary>Изменённые чанки в поток.</summary>
        public static void WriteChanges(WorldModel model, Stream output) => Write(model, KindChanges, output);

        /// <summary>SHA-256 содержимого всего мира — тот же, что пишется в снимок генерации.</summary>
        public static byte[] ContentHash(WorldModel model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var blob = new byte[MaxBlobBytes];
            Span<byte> head = stackalloc byte[12];
            ReadOnlySpan<ChunkCoord> keys = model.GetChunkCoords();
            var header = new byte[HeaderBytes];
            WriteHeader(header, model, KindBase, keys.Length);
            hash.AppendData(header);
            for (int i = 0; i < keys.Length; i++)
            {
                AppendToHash(hash, keys[i], model.FindChunk(keys[i])!, blob, head);
            }

            return hash.GetHashAndReset();
        }

        private static byte[] ToArray(WorldModel model, byte kind)
        {
            using var output = new MemoryStream();
            Write(model, kind, output);
            return output.ToArray();
        }

        private static void Write(WorldModel model, byte kind, Stream output)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            ReadOnlySpan<ChunkCoord> all = model.GetChunkCoords();
            ChunkCoord[] keys = kind == KindBase ? all.ToArray() : model.GetChangedSinceBase();

            var header = new byte[HeaderBytes];
            WriteHeader(header, model, kind, keys.Length);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(header);
            using var body = new MemoryStream(keys.Length * 256 + 64);
            var blob = new byte[MaxBlobBytes];
            Span<byte> head = stackalloc byte[ChunkHeaderBytes];
            for (int i = 0; i < keys.Length; i++)
            {
                Chunk chunk = model.FindChunk(keys[i])!;
                AppendToHash(hash, keys[i], chunk, blob, head);

                // Свежесть копии (3.3): свежую берём как есть, отставшую собираем сразу.
                byte[]? compressed = chunk.FreshCopy;
                if (compressed == null)
                {
                    chunk.RebuildCompressed();
                    compressed = chunk.FreshCopy!;
                }

                WriteChunkHead(head, keys[i], chunk.Revision);
                Bytes.WriteUInt32(head.Slice(12), (uint)compressed.Length);
                body.Write(head);
                body.Write(compressed, 0, compressed.Length);
            }

            output.Write(header);
            output.Write(hash.GetHashAndReset());
            body.Position = 0;
            body.CopyTo(output);
        }

        // ---------- Чтение ----------

        /// <summary>Мир из снимка генерации (генератор не нужен).</summary>
        public static WorldModel LoadBase(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            Header h = ReadHeader(data, KindBase);
            var model = new WorldModel(h.Shape, h.Seed, h.GeneratorVersion);
            ReadChunks(data, h, model, changedSinceBase: false);
            return model;
        }

        /// <summary>Мир из снимка генерации в потоке.</summary>
        public static WorldModel LoadBase(Stream input) => LoadBase(ReadAll(input));

        /// <summary>Положить изменённые чанки поверх мира (загрузка: снимок + изменения).</summary>
        public static void ApplyChanges(WorldModel model, byte[] data)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            Header h = ReadHeader(data, KindChanges);
            if (h.Shape != model.Shape || h.Seed != model.Seed || h.GeneratorVersion != model.GeneratorVersion)
            {
                throw new InvalidDataException(
                    $"Изменения от другого мира: {h.Shape}, зерно {h.Seed}, v{h.GeneratorVersion}; мир — {model.Shape}, зерно {model.Seed}, v{model.GeneratorVersion}.");
            }

            ReadChunks(data, h, model, changedSinceBase: true);
        }

        /// <summary>Загрузка целиком (3.4): снимок генерации, поверх — изменения (null — изменений нет).</summary>
        public static WorldModel Load(byte[] baseData, byte[]? changes)
        {
            WorldModel model = LoadBase(baseData);
            if (changes != null)
            {
                ApplyChanges(model, changes);
            }

            return model;
        }

        /// <summary>SHA-256 содержимого, записанный в заголовке снимка (сверка с перегенерацией, 3.4).</summary>
        public static byte[] StoredHash(byte[] data)
        {
            if (data == null || data.Length < HeaderBytes + HashBytes)
            {
                throw new InvalidDataException("Снимок короче заголовка.");
            }

            return data.AsSpan(HeaderBytes, HashBytes).ToArray();
        }

        private readonly struct Header
        {
            public readonly MapShape Shape;
            public readonly ulong Seed;
            public readonly int GeneratorVersion;
            public readonly int ChunkCount;

            public Header(MapShape shape, ulong seed, int generatorVersion, int chunkCount)
            {
                Shape = shape;
                Seed = seed;
                GeneratorVersion = generatorVersion;
                ChunkCount = chunkCount;
            }
        }

        private static Header ReadHeader(ReadOnlySpan<byte> data, byte expectedKind)
        {
            if (data.Length < HeaderBytes + HashBytes)
            {
                throw new InvalidDataException("Снимок короче заголовка.");
            }

            if (!data.Slice(0, 4).SequenceEqual(Magic))
            {
                throw new InvalidDataException("Это не снимок мира Orpheus: нет метки ORPW.");
            }

            if (data[4] != FormatVersion)
            {
                throw new InvalidDataException($"Версия формата снимка {data[4]}, сборка понимает {FormatVersion}.");
            }

            if (data[5] != expectedKind)
            {
                throw new InvalidDataException(expectedKind == KindBase
                    ? "Ожидается снимок генерации, пришли изменённые чанки."
                    : "Ожидаются изменённые чанки, пришёл снимок генерации.");
            }

            int version = (int)Bytes.ReadUInt32(data, 6);
            ulong seed = Bytes.ReadUInt64(data, 10);
            int width = (int)Bytes.ReadUInt32(data, 18);
            int height = (int)Bytes.ReadUInt32(data, 22);
            bool wraps = data[26] != 0;
            int count = (int)Bytes.ReadUInt32(data, 27);
            MapShape shape;
            try
            {
                shape = new MapShape(width, height, wraps);
            }
            catch (ArgumentOutOfRangeException e)
            {
                throw new InvalidDataException("Снимок: недопустимая форма карты. " + e.Message);
            }

            if (version < 0 || count < 0 || count > shape.ChunksX * shape.ChunksY)
            {
                throw new InvalidDataException($"Снимок: версия {version}, чанков {count} — повреждён.");
            }

            return new Header(shape, seed, version, count);
        }

        private static void ReadChunks(byte[] data, Header h, WorldModel model, bool changedSinceBase)
        {
            // Сначала всё разобрать и проверить хэш, потом трогать мир: повреждённые изменения не портят загруженный снимок.
            var coords = new ChunkCoord[h.ChunkCount];
            var revisions = new uint[h.ChunkCount];
            var blobs = new byte[h.ChunkCount][];
            var compressed = new byte[h.ChunkCount][];

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(data, 0, HeaderBytes);
            Span<byte> head = stackalloc byte[12];
            int at = HeaderBytes + HashBytes;
            for (int i = 0; i < h.ChunkCount; i++)
            {
                if (data.Length - at < ChunkHeaderBytes)
                {
                    throw new InvalidDataException($"Снимок оборван на чанке № {i}.");
                }

                var c = new ChunkCoord((int)Bytes.ReadUInt32(data, at), (int)Bytes.ReadUInt32(data, at + 4));
                uint revision = Bytes.ReadUInt32(data, at + 8);
                uint length = Bytes.ReadUInt32(data, at + 12);
                at += ChunkHeaderBytes;
                if ((uint)c.X >= (uint)h.Shape.ChunksX || (uint)c.Y >= (uint)h.Shape.ChunksY || length > data.Length - at)
                {
                    throw new InvalidDataException($"Снимок: чанк № {i} {c} вне карты или длиннее файла.");
                }

                // Строго по возрастанию: повтор чанка или чужой порядок — отказ (backlog [сохранения]).
                if (i > 0 && ChunkCoordOrder.Compare(coords[i - 1], c) >= 0)
                {
                    throw new InvalidDataException($"Снимок: чанк № {i} {c} повторён или не по порядку (после {coords[i - 1]}).");
                }

                compressed[i] = data.AsSpan(at, (int)length).ToArray();
                at += (int)length;
                try
                {
                    blobs[i] = Chunk.Decompress(compressed[i]);
                }
                catch (InvalidDataException e)
                {
                    throw new InvalidDataException($"Снимок: сжатый чанк {c} повреждён. " + e.Message);
                }

                coords[i] = c;
                revisions[i] = revision;
                WriteChunkHead(head, c, revision);
                hash.AppendData(head.ToArray());
                hash.AppendData(blobs[i]);
            }

            if (at != data.Length)
            {
                throw new InvalidDataException("Снимок: после чанков лишние байты.");
            }

            if (!hash.GetHashAndReset().AsSpan().SequenceEqual(data.AsSpan(HeaderBytes, HashBytes)))
            {
                throw new InvalidDataException("Снимок повреждён: SHA-256 содержимого не совпал.");
            }

            for (int i = 0; i < coords.Length; i++)
            {
                model.GetOrCreateChunk(coords[i]).Load(blobs[i], compressed[i], revisions[i], changedSinceBase);
            }
        }

        // ---------- Общее ----------

        private static void WriteHeader(Span<byte> header, WorldModel model, byte kind, int count)
        {
            Magic.CopyTo(header);
            header[4] = FormatVersion;
            header[5] = kind;
            Bytes.WriteUInt32(header.Slice(6), (uint)model.GeneratorVersion);
            Bytes.WriteUInt64(header.Slice(10), model.Seed);
            Bytes.WriteUInt32(header.Slice(18), (uint)model.Shape.Width);
            Bytes.WriteUInt32(header.Slice(22), (uint)model.Shape.Height);
            header[26] = model.Shape.Wraps ? (byte)1 : (byte)0;
            Bytes.WriteUInt32(header.Slice(27), (uint)count);
        }

        private static void WriteChunkHead(Span<byte> head, ChunkCoord c, uint revision)
        {
            Bytes.WriteUInt32(head, (uint)c.X);
            Bytes.WriteUInt32(head.Slice(4), (uint)c.Y);
            Bytes.WriteUInt32(head.Slice(8), revision);
        }

        private static void AppendToHash(IncrementalHash hash, ChunkCoord c, Chunk chunk, byte[] blob, Span<byte> head)
        {
            WriteChunkHead(head, c, chunk.Revision);
            hash.AppendData(head.Slice(0, 12).ToArray());
            int n = chunk.WriteBlob(blob);
            hash.AppendData(blob, 0, n);
        }

        private static byte[] ReadAll(Stream input)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            using var copy = new MemoryStream();
            input.CopyTo(copy);
            return copy.ToArray();
        }
    }
}
