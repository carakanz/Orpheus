using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// «Снимок генерации + изменённые чанки» (docs/07, 3.4): базовый снимок — все чанки сжатыми
    /// плюс версия генератора, зерно и SHA-256 содержимого; в сохранении — только чанки,
    /// изменённые после генерации, целиком и сжатыми. Загрузка: чанки снимка, поверх — изменённые.
    /// <para>
    /// Формат — свой двоичный (без <c>System.Text.Json</c> и без рефлексии), little-endian:
    /// </para>
    /// <list type="bullet">
    /// <item>магия «ORPW» (4) и версия формата (1);</item>
    /// <item>вид (1): 0 — снимок генерации, 1 — изменённые чанки;</item>
    /// <item>версия генератора (4), зерно (8), ширина (4), высота (4), «тор» (1);</item>
    /// <item>число чанков (4) и SHA-256 блока чанков (32);</item>
    /// <item>блок чанков в порядке отсортированных ключей: чанк x (4), чанк y (4), ревизия (4),
    /// длина сжатого блока (4) и сам сжатый блок (Deflate, <see cref="Chunk.CompressBlob"/>).</item>
    /// </list>
    /// Атомарную запись в файл делает карточка сохранений (<c>IPlatformStorage</c>), здесь —
    /// байты в <c>byte[]</c> и <see cref="Stream"/>. Порядок чанков фиксирован (отсортированные
    /// ключи, ORP006), поэтому один и тот же мир даёт побайтно один и тот же снимок и один SHA-256.
    /// </summary>
    public static class WorldSnapshot
    {
        /// <summary>Магия файла снимка: «ORPW».</summary>
        public const uint Magic = 0x5750524F; // «O», «R», «P», «W» в обратном порядке байтов little-endian

        /// <summary>Версия формата снимка (мигрирует вместе с полем, docs/07, 6.7).</summary>
        public const byte FormatVersion = 1;

        /// <summary>Длина SHA-256 содержимого: 32 байта.</summary>
        public const int HashBytes = 32;

        /// <summary>Байт заголовка до хэша: магия 4 + версия 1 + вид 1 + версия генератора 4 + зерно 8 + карта 9 + число чанков 4.</summary>
        public const int HeaderBytes = 4 + 1 + 1 + 4 + 8 + 4 + 4 + 1 + 4;

        /// <summary>Снимок генерации (world.base): все созданные чанки.</summary>
        public const byte KindBase = 0;

        /// <summary>Изменённые чанки сохранения: только грязные.</summary>
        public const byte KindChanges = 1;

        // ---------- Запись ----------

        /// <summary>Снимок генерации в байтах (все созданные чанки, сжатые, с ревизиями).</summary>
        public static byte[] WriteBase(WorldModel model) => WriteToArray(model, KindBase);

        /// <summary>Изменённые чанки сохранения в байтах: только грязные (docs/07, 3.4).</summary>
        public static byte[] WriteChanges(WorldModel model) => WriteToArray(model, KindChanges);

        /// <summary>Снимок генерации в поток.</summary>
        public static void WriteBase(WorldModel model, Stream output) => Write(model, output, KindBase);

        /// <summary>Изменённые чанки в поток.</summary>
        public static void WriteChanges(WorldModel model, Stream output) => Write(model, output, KindChanges);

        private static byte[] WriteToArray(WorldModel model, byte kind)
        {
            using var output = new MemoryStream(model.ChunkCount * 320);
            Write(model, output, kind);
            return output.ToArray();
        }

        private static void Write(WorldModel model, Stream output, byte kind)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            using var payload = new MemoryStream(model.ChunkCount * 320);
            int chunkCount = WriteChunkBlock(model, payload, kind == KindChanges);

            byte[] hash;
            payload.Position = 0;
            using (var sha = SHA256.Create())
            {
                hash = sha.ComputeHash(payload);
            }

            payload.Position = 0;
            Span<byte> scratch = stackalloc byte[8];
            WriteUInt32(scratch, Magic);
            output.Write(scratch.Slice(0, 4));
            output.WriteByte(FormatVersion);
            output.WriteByte(kind);
            WriteInt32(output, model.GeneratorVersion);
            WriteInt64(output, unchecked((long)model.Seed));
            WriteInt32(output, model.Shape.Width);
            WriteInt32(output, model.Shape.Height);
            output.WriteByte(model.Shape.Wraps ? (byte)1 : (byte)0);
            WriteInt32(output, chunkCount);
            output.Write(hash);
            payload.WriteTo(output);
        }

        /// <summary>
        /// Блок чанков в порядке отсортированных ключей: x (4), y (4), ревизия (4), длина (4), сжатый блок.
        /// Свежую копию не пересобирают («свежесть копии», 3.3), отставшую — собирают сразу.
        /// </summary>
        private static int WriteChunkBlock(WorldModel model, Stream payload, bool onlyDirty)
        {
            ChunkCoord[] keys = model.ChunkCoords();
            int n = 0;
            Span<byte> scratch = stackalloc byte[8];
            for (int i = 0; i < keys.Length; i++)
            {
                Chunk? chunk = model.FindChunk(keys[i]);
                if (chunk == null || (onlyDirty && !chunk.IsDirty))
                {
                    continue;
                }

                // «Свежесть копии» (3.3): свежую не пересобирают, отставшую собирают сразу.
                byte[]? compressed = chunk.FreshCopy;
                if (compressed == null)
                {
                    chunk.RebuildCompressed();
                    compressed = chunk.FreshCopy;
                }

                WriteInt32(payload, keys[i].X);
                WriteInt32(payload, keys[i].Y);
                WriteUInt32(scratch, chunk.Revision);
                payload.Write(scratch.Slice(0, 4));
                WriteUInt32(scratch, (uint)compressed.Length);
                payload.Write(scratch.Slice(0, 4));
                payload.Write(compressed, 0, compressed.Length);
                n++;
            }

            return n;
        }

        // ---------- Чтение ----------

        /// <summary>Прочитать снимок (или изменённые чанки) из байтов: магия, версия и SHA-256 проверяются.</summary>
        public static SnapshotData Read(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            using var input = new MemoryStream(data, 0, data.Length, false);
            return Read(input);
        }

        /// <summary>Прочитать снимок (или изменённые чанки) из потока.</summary>
        public static SnapshotData Read(Stream input)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            Span<byte> scratch = stackalloc byte[8];
            ReadFully(input, scratch.Slice(0, 4));
            uint magic = (uint)(scratch[0] | (scratch[1] << 8) | (scratch[2] << 16) | (scratch[3] << 24));
            if (magic != Magic)
            {
                throw new InvalidDataException("Это не снимок мира Orpheus: магия не та.");
            }

            int versionByte = input.ReadByte();
            if (versionByte != FormatVersion)
            {
                throw new InvalidDataException($"Версия формата снимка {versionByte}, сборка понимает {FormatVersion}.");
            }

            int kindByte = input.ReadByte();
            if (kindByte != KindBase && kindByte != KindChanges)
            {
                throw new InvalidDataException($"Неизвестный вид снимка {kindByte}.");
            }

            int generatorVersion = ReadInt32(input);
            ulong seed = unchecked((ulong)ReadInt64(input));
            var shape = new MapShape(ReadInt32(input), ReadInt32(input), ReadByte(input) != 0);
            int chunkCount = ReadInt32(input);

            byte[] storedHash = new byte[HashBytes];
            ReadFully(input, storedHash);

            using var payload = new MemoryStream(Math.Max(chunkCount * 256, 256));
            input.CopyTo(payload);
            byte[] payloadBytes = payload.ToArray();
            using (var sha = SHA256.Create())
            {
                byte[] actual = sha.ComputeHash(payloadBytes);
                for (int i = 0; i < HashBytes; i++)
                {
                    if (actual[i] != storedHash[i])
                    {
                        throw new InvalidDataException("Снимок повреждён: SHA-256 содержимого не совпал.");
                    }
                }
            }

            var chunks = new List<ChunkRecord>(chunkCount);
            ParseChunkBlock(payloadBytes, chunkCount, chunks);
            return new SnapshotData((byte)kindByte, generatorVersion, seed, shape, chunks);
        }

        /// <summary>Создать мир из снимка генерации (чанки создаются сразу, генератор не нужен).</summary>
        public static WorldModel Load(SnapshotData baseSnapshot)
        {
            if (!baseSnapshot.IsBase)
            {
                throw new InvalidDataException("Ожидается снимок генерации (world.base), пришёл блок изменённых чанков.");
            }

            var model = new WorldModel(baseSnapshot.Shape, baseSnapshot.Seed, baseSnapshot.GeneratorVersion);
            ApplyRecords(model, baseSnapshot.Chunks);
            model.ClearAllDirty();
            return model;
        }

        /// <summary>
        /// Загрузка целиком (docs/07, 3.4): мир из снимка генерации, поверх — изменённые чанки
        /// (изменений нет — <paramref name="changes"/> = null).
        /// </summary>
        public static WorldModel LoadWithChanges(SnapshotData baseSnapshot, byte[]? changes)
        {
            WorldModel model = Load(baseSnapshot);
            if (changes != null && changes.Length > 0)
            {
                ApplyChanges(model, Read(changes));
            }

            return model;
        }

        /// <summary>Положить изменённые чанки поверх мира (загрузка: снимок + изменения).</summary>
        public static void ApplyChanges(WorldModel model, SnapshotData changes)
        {
            if (changes.Kind == KindBase)
            {
                throw new InvalidDataException("Ожидаются изменённые чанки, пришёл снимок генерации.");
            }

            if (changes.Shape != model.Shape)
            {
                throw new InvalidDataException($"Изменённые чанки для карты {changes.Shape}, мир — {model.Shape}.");
            }

            if (changes.Seed != model.Seed)
            {
                throw new InvalidDataException("Зерно изменённых чанков не совпадает с зерном мира.");
            }

            ApplyRecords(model, changes.Chunks);
            model.ClearAllDirty();
        }

        private static void ApplyRecords(WorldModel model, List<ChunkRecord> records)
        {
            for (int i = 0; i < records.Count; i++)
            {
                ChunkRecord record = records[i];
                Chunk chunk = model.GetChunk(record.Chunk);
                Chunk.DecompressBlob(record.Compressed, chunk);
                chunk.SetRevision(record.Revision);
            }
        }

        private static void ParseChunkBlock(byte[] payloadBytes, int chunkCount, List<ChunkRecord> chunks)
        {
            ReadOnlySpan<byte> src = payloadBytes;
            int at = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                if (src.Length - at < 16)
                {
                    throw new InvalidDataException($"Блок чанков оборван на чанке № {i}.");
                }

                var chunk = new ChunkCoord(ReadInt32At(src, at), ReadInt32At(src, at + 4));
                uint revision = TilePatch.ReadUInt32(src, at + 8);
                int length = (int)TilePatch.ReadUInt32(src, at + 12);
                at += 16;
                if (length < 0 || src.Length - at < length)
                {
                    throw new InvalidDataException($"Длина сжатого чанка {chunk} не влезает в блок.");
                }

                chunks.Add(new ChunkRecord(chunk, revision, src.Slice(at, length).ToArray()));
                at += length;
            }

            if (at != src.Length)
            {
                throw new InvalidDataException("В конце блока чанков остались лишние байты.");
            }
        }

        // ---------- Хэш содержимого (проверка «тот ли мир», docs/07, 3.4 и 3.13 п. 4) ----------

        /// <summary>
        /// SHA-256 содержимого мира в каноническом порядке: чанки по отсортированным ключам,
        /// у каждого — координаты, ревизия и несжатый блок (слои + записи построек).
        /// Один и тот же мир на любой машине даёт один и тот же хэш.
        /// </summary>
        public static byte[] ContentHash(WorldModel model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            using var content = new MemoryStream(model.ChunkCount * Chunk.LayerBytes);
            Span<byte> scratch = stackalloc byte[8];
            ChunkCoord[] keys = model.ChunkCoords();
            for (int i = 0; i < keys.Length; i++)
            {
                Chunk? chunk = model.FindChunk(keys[i]);
                if (chunk == null)
                {
                    continue;
                }

                WriteInt32(content, keys[i].X);
                WriteInt32(content, keys[i].Y);
                WriteUInt32(scratch, chunk.Revision);
                content.Write(scratch.Slice(0, 4));

                var blob = new byte[chunk.BlobBytes];
                chunk.WriteBlob(blob);
                content.Write(blob, 0, blob.Length);
            }

            content.Position = 0;
            using var sha = SHA256.Create();
            return sha.ComputeHash(content);
        }

        /// <summary>Хэш как строка шестнадцатеричных цифр (для журнала и сверки между машинами).</summary>
        public static string ToHex(byte[] hash)
        {
            if (hash == null)
            {
                throw new ArgumentNullException(nameof(hash));
            }

            var chars = new char[hash.Length * 2];
            for (int i = 0; i < hash.Length; i++)
            {
                chars[i * 2] = HexDigit(hash[i] >> 4);
                chars[i * 2 + 1] = HexDigit(hash[i] & 0xF);
            }

            return new string(chars);

            static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'a' + (value - 10));
        }

        // ---------- Поток: мелкие целые ----------

        private static void WriteUInt32(Span<byte> scratch, uint value) => TilePatch.WriteUInt32(scratch, value);

        private static void WriteInt32(Stream stream, int value)
        {
            Span<byte> scratch = stackalloc byte[4];
            TilePatch.WriteUInt32(scratch, unchecked((uint)value));
            stream.Write(scratch);
        }

        private static void WriteInt64(Stream stream, long value)
        {
            Span<byte> scratch = stackalloc byte[8];
            for (int i = 0; i < 8; i++)
            {
                scratch[i] = (byte)(value >> (8 * i));
            }

            stream.Write(scratch);
        }

        private static int ReadByte(Stream stream)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException("Файл снимка оборвался.");
            }

            return b;
        }

        private static int ReadInt32(Stream stream)
        {
            Span<byte> scratch = stackalloc byte[4];
            ReadFully(stream, scratch);
            return unchecked((int)TilePatch.ReadUInt32(scratch, 0));
        }

        private static long ReadInt64(Stream stream)
        {
            Span<byte> scratch = stackalloc byte[8];
            ReadFully(stream, scratch);
            long value = 0;
            for (int i = 0; i < 8; i++)
            {
                value |= (long)scratch[i] << (8 * i);
            }

            return value;
        }

        private static int ReadInt32At(ReadOnlySpan<byte> src, int at) => unchecked((int)TilePatch.ReadUInt32(src, at));

        private static void ReadFully(Stream stream, Span<byte> dest)
        {
            int filled = 0;
            while (filled < dest.Length)
            {
                int read = stream.Read(dest.Slice(filled));
                if (read <= 0)
                {
                    throw new EndOfStreamException("Файл снимка оборвался.");
                }

                filled += read;
            }
        }

        /// <summary>Запись одного чанка из снимка: координата, ревизия и сжатый блок.</summary>
        public readonly struct ChunkRecord
        {
            public readonly ChunkCoord Chunk;
            public readonly uint Revision;
            public readonly byte[] Compressed;

            public ChunkRecord(ChunkCoord chunk, uint revision, byte[] compressed)
            {
                Chunk = chunk;
                Revision = revision;
                Compressed = compressed;
            }

            public override readonly string ToString() =>
                $"chunk({Chunk.X}, {Chunk.Y}) rev {Revision}, {Compressed.Length} байт";
        }

        /// <summary>Прочитанный снимок: вид, версия генератора, зерно, карта и чанки.</summary>
        public readonly struct SnapshotData
        {
            public readonly byte Kind;
            public readonly int GeneratorVersion;
            public readonly ulong Seed;
            public readonly MapShape Shape;
            public readonly List<ChunkRecord> Chunks;

            public SnapshotData(byte kind, int generatorVersion, ulong seed, MapShape shape, List<ChunkRecord> chunks)
            {
                Kind = kind;
                GeneratorVersion = generatorVersion;
                Seed = seed;
                Shape = shape;
                Chunks = chunks;
            }

            /// <summary>Это снимок генерации (world.base).</summary>
            public readonly bool IsBase => Kind == KindBase;

            /// <summary>Число чанков в блоке.</summary>
            public readonly int ChunkCount => Chunks.Count;

            public override readonly string ToString() =>
                $"снимок {(IsBase ? "генерации" : "изменений")}: {Shape}, зерно {Seed}, генератор v{GeneratorVersion}, чанков {Chunks.Count}";
        }
    }
}
