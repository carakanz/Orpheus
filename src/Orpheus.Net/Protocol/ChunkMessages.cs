using System;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Net.Protocol
{
    /// <summary>
    /// Мир по сети (docs/07, 3.5–3.7; 0-026): чанк целиком, пачка правок чанка, центр окна, запрос чанка. Всё — канал 1
    /// (надёжный, по порядку): копия чанка и пачки к нему идут одним упорядоченным потоком, разрыва нет (3.7).
    /// <para>
    /// Заголовок чанка и пачки один (3.6): карта (1), чанк x (2), y (2), ревизия (4) — 9 байт, младший байт первым,
    /// как у <see cref="TilePatch"/>. Перед ним — байт типа сообщения.
    /// </para>
    /// </summary>
    public static class ChunkWire
    {
        /// <summary>Карта поверхности (docs/07, 3.10: 0 — поверхность, 1…K — отдельные карты данжей — потом).</summary>
        public const byte SurfaceMap = 0;

        /// <summary>Чанк вне карты или карта не поверхность — <see cref="ProtocolException"/>.</summary>
        public static void CheckChunk(in MapShape shape, byte map, ChunkCoord c, string what)
        {
            if (map != SurfaceMap)
            {
                throw new ProtocolException($"{what}: карта {map} (отдельных карт пока нет).");
            }

            if ((uint)c.X >= (uint)shape.ChunksX || (uint)c.Y >= (uint)shape.ChunksY)
            {
                throw new ProtocolException($"{what}: чанк ({c.X}, {c.Y}) вне карты {shape.ChunksX}×{shape.ChunksY} чанков.");
            }
        }
    }

    /// <summary>
    /// Чанк целиком (3.6): тип 1 + заголовок 9 + сжатый блок до конца сообщения. Сжатый блок — копия чанка
    /// (<see cref="Chunk.FreshCopy"/>, Deflate-6 блока <see cref="Chunk.WriteBlob"/>: пол плоскостью, объект двумя
    /// плоскостями, записи построек) — та же, что в сохранении (3.4), сжимается один раз на ревизию.
    /// Ячеек макрокарты (48 байт по 3.6) нет: макрокарты в вехе 0 нет (0-026, «Решения», backlog [док]).
    /// </summary>
    public readonly ref struct ChunkMessage
    {
        /// <summary>Тип 1 + карта 1 + x 2 + y 2 + ревизия 4.</summary>
        public const int HeaderBytes = 1 + TilePatch.HeaderBytes;

        public readonly byte Map;
        public readonly ChunkCoord Chunk;
        public readonly uint Revision;

        /// <summary>Сжатый блок чанка (живёт, пока живёт буфер сообщения).</summary>
        public readonly ReadOnlySpan<byte> Compressed;

        public ChunkMessage(byte map, ChunkCoord chunk, uint revision, ReadOnlySpan<byte> compressed)
        {
            Map = map;
            Chunk = chunk;
            Revision = revision;
            Compressed = compressed;
        }

        /// <summary>Записать и вернуть число байт.</summary>
        public static int Write(Span<byte> dest, byte map, ChunkCoord chunk, uint revision, ReadOnlySpan<byte> compressed)
        {
            int total = HeaderBytes + compressed.Length;
            if (dest.Length < total)
            {
                throw new ArgumentException($"Чанк: нужно {total} байт, есть {dest.Length}.", nameof(dest));
            }

            var w = new BitWriter(dest.Slice(0, HeaderBytes));
            w.WriteByte((byte)MessageType.Chunk);
            w.WriteByte(map);
            w.WriteUInt16((ushort)chunk.X);
            w.WriteUInt16((ushort)chunk.Y);
            w.WriteUInt32(revision);
            compressed.CopyTo(dest.Slice(HeaderBytes));
            return total;
        }

        /// <summary>Прочитать; сжатый блок пуст или чанк вне карты — <see cref="ProtocolException"/>.</summary>
        public static ChunkMessage Read(ReadOnlySpan<byte> src, in MapShape shape)
        {
            if (src.Length <= HeaderBytes)
            {
                throw new ProtocolException($"Чанк: {src.Length} байт, а заголовок — {HeaderBytes} и блок не пуст.");
            }

            var r = Codec.Begin(src.Slice(0, HeaderBytes), MessageType.Chunk);
            byte map = r.ReadByte();
            var c = new ChunkCoord(r.ReadUInt16(), r.ReadUInt16());
            uint revision = r.ReadUInt32();
            ChunkWire.CheckChunk(shape, map, c, "Чанк");
            return new ChunkMessage(map, c, revision, src.Slice(HeaderBytes));
        }
    }

    /// <summary>
    /// Пачка правок чанка за такт (3.7): тип 1 + <see cref="TilePatch"/> (заголовок 9, число 2, правки по 5…13 байт).
    /// Сервер пишет её из содержимого чанка после такта по индексам принятых правок: тайл в пачке правится один раз
    /// (вторая правка — «занято»), поэтому тайл после такта — ровно правка.
    /// </summary>
    public static class ChunkPatchMessage
    {
        /// <summary>Наибольший размер: тип 1 + заголовок 9 + число 2 + 1024 правки по 13 байт.</summary>
        public const int MaxBytes = 1 + TilePatch.HeaderBytes + TilePatch.CountBytes + TilePatch.MaxEdits * TileEdit.WireBytesWithRecord;

        /// <summary>Сервер: пачка из тайлов <paramref name="indices"/> чанка <paramref name="content"/> как они есть сейчас.</summary>
        public static int Write(Span<byte> dest, byte map, ChunkCoord chunk, uint revision, Chunk content, ReadOnlySpan<ushort> indices)
        {
            if (indices.Length == 0 || indices.Length > TilePatch.MaxEdits)
            {
                throw new ArgumentException($"Пачка: {indices.Length} правок, нужно 1…{TilePatch.MaxEdits}.", nameof(indices));
            }

            int need = 1 + TilePatch.HeaderBytes + TilePatch.CountBytes + indices.Length * TileEdit.WireBytesWithRecord;
            if (dest.Length < need)
            {
                throw new ArgumentException($"Пачка: нужно до {need} байт, есть {dest.Length}.", nameof(dest));
            }

            var w = new BitWriter(dest.Slice(0, 1 + TilePatch.HeaderBytes + TilePatch.CountBytes));
            w.WriteByte((byte)MessageType.ChunkPatch);
            w.WriteByte(map);
            w.WriteUInt16((ushort)chunk.X);
            w.WriteUInt16((ushort)chunk.Y);
            w.WriteUInt32(revision);
            w.WriteUInt16((ushort)indices.Length);
            int at = w.BytesWritten;
            for (int i = 0; i < indices.Length; i++)
            {
                int index = indices[i];
                byte floor = content.GetFloor(index);
                ushort obj = content.GetObject(index);
                TileEdit edit = content.TryGetBuild(index, out BuildRecord record)
                    ? new TileEdit((ushort)index, floor, obj, record)
                    : new TileEdit((ushort)index, floor, obj);
                at += edit.Write(dest.Slice(at));
            }

            return at;
        }

        /// <summary>Записать готовую пачку (тесты, «золотые» байты).</summary>
        public static int Write(Span<byte> dest, in TilePatch patch)
        {
            dest[0] = (byte)MessageType.ChunkPatch;
            return 1 + patch.Write(dest.Slice(1));
        }

        /// <summary>Прочитать; испорченная пачка или чанк вне карты — <see cref="ProtocolException"/>.</summary>
        public static TilePatch Read(ReadOnlySpan<byte> src, in MapShape shape)
        {
            Codec.Begin(src, MessageType.ChunkPatch);
            TilePatch patch;
            int n;
            try
            {
                n = TilePatch.Read(src.Slice(1), out patch);
            }
            catch (Exception e) when (e is InvalidDataException || e is ArgumentException || e is IndexOutOfRangeException)
            {
                throw new ProtocolException("Пачка правок: " + e.Message);
            }

            if (1 + n != src.Length)
            {
                throw new ProtocolException($"Пачка правок: лишние данные — прочитано {1 + n} байт из {src.Length}.");
            }

            if (patch.Edits.Length == 0)
            {
                throw new ProtocolException("Пачка правок без правок.");
            }

            ChunkWire.CheckChunk(shape, patch.MapId, patch.Chunk, "Пачка правок");
            return patch;
        }
    }

    /// <summary>
    /// Центр окна (3.5; 0-026): сервер сменил блок окна и убрал из окна чанки вне 8 × 6 вокруг нового центра — клиент
    /// выбрасывает ровно их (<see cref="ChunkWindow.InKeep"/>). Идёт тем же каналом 1 перед новыми чанками, поэтому набор
    /// чанков у клиента всегда равен набору, который сервер считает отправленным: предсказание позиции на клиенте
    /// не может выбросить чанк, который сервер считает доставленным.
    /// </summary>
    public struct ChunkWindowMessage
    {
        /// <summary>Тип 1 + карта 1 + центр x 2 + y 2.</summary>
        public const int Bytes = 6;

        public byte Map;
        public ChunkCoord Center;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest.Slice(0, Bytes));
            w.WriteByte((byte)MessageType.ChunkWindow);
            w.WriteByte(Map);
            w.WriteUInt16((ushort)Center.X);
            w.WriteUInt16((ushort)Center.Y);
            return w.BytesWritten;
        }

        public static ChunkWindowMessage Read(ReadOnlySpan<byte> src, in MapShape shape)
        {
            var r = Codec.Begin(src, MessageType.ChunkWindow);
            var m = new ChunkWindowMessage { Map = r.ReadByte(), Center = new ChunkCoord(r.ReadUInt16(), r.ReadUInt16()) };
            r.ExpectEnd();
            ChunkWire.CheckChunk(shape, m.Map, m.Center, "Центр окна");
            return m;
        }
    }

    /// <summary>
    /// Клиент → сервер, канал 1: «пришли чанк целиком» (3.7 «Порядок»: пачка пришла не к той ревизии). Сервер шлёт
    /// текущую копию, если чанк в окне клиента; иначе запрос молча пропускается.
    /// </summary>
    public struct ChunkRequestMessage
    {
        /// <summary>Тип 1 + карта 1 + x 2 + y 2.</summary>
        public const int Bytes = 6;

        public byte Map;
        public ChunkCoord Chunk;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest.Slice(0, Bytes));
            w.WriteByte((byte)MessageType.ChunkRequest);
            w.WriteByte(Map);
            w.WriteUInt16((ushort)Chunk.X);
            w.WriteUInt16((ushort)Chunk.Y);
            return w.BytesWritten;
        }

        public static ChunkRequestMessage Read(ReadOnlySpan<byte> src, in MapShape shape)
        {
            var r = Codec.Begin(src, MessageType.ChunkRequest);
            var m = new ChunkRequestMessage { Map = r.ReadByte(), Chunk = new ChunkCoord(r.ReadUInt16(), r.ReadUInt16()) };
            r.ExpectEnd();
            ChunkWire.CheckChunk(shape, m.Map, m.Chunk, "Запрос чанка");
            return m;
        }
    }
}
