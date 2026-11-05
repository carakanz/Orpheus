using System;
using System.IO;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Пачка правок одного чанка за такт — одно сообщение в канал 1 (docs/07, 3.7):
    /// заголовок как у чанка по сети (карта 1, чанк x и y по 2, новая ревизия 4 = 9 байт, 3.6),
    /// число правок (2) и правки по 5…13 байт (<see cref="TileEdit"/>).
    /// Клиент применяет пачку, только если его ревизия чанка — ровно предыдущая (<see cref="Chunk.ApplyPatch"/>).
    /// </summary>
    public readonly struct TilePatch
    {
        /// <summary>Заголовок: карта (1) + чанк x (2) + чанк y (2) + ревизия (4) = 9 байт (docs/07, 3.6).</summary>
        public const int HeaderBytes = 9;

        /// <summary>Поле «число правок»: 2 байта.</summary>
        public const int CountBytes = 2;

        /// <summary>Наибольшее число правок в пачке: по одной на тайл чанка (вторая на тот же тайл — «занято»).</summary>
        public const int MaxEdits = World.Chunk.TileCount;

        /// <summary>Карта: 0 — поверхность, 1…K — отдельная карта данжа (docs/07, 3.10).</summary>
        public readonly byte MapId;

        /// <summary>Чанк пачки.</summary>
        public readonly ChunkCoord Chunk;

        /// <summary>Ревизия чанка после пачки.</summary>
        public readonly uint Revision;

        /// <summary>Принятые сервером правки по порядку.</summary>
        public readonly TileEdit[] Edits;

        public TilePatch(byte mapId, ChunkCoord chunk, uint revision, TileEdit[] edits)
        {
            MapId = mapId;
            Chunk = chunk;
            Revision = revision;
            Edits = edits ?? Array.Empty<TileEdit>();
        }

        /// <summary>Размер пачки в байтах.</summary>
        public int WireBytes => HeaderBytes + CountBytes + EditBytes(Edits, default);

        /// <summary>
        /// Сумма байт правок: только принятых, если <paramref name="results"/> не пуст.
        /// </summary>
        public static int EditBytes(ReadOnlySpan<TileEdit> edits, ReadOnlySpan<EditResult> results)
        {
            int bytes = 0;
            for (int i = 0; i < edits.Length; i++)
            {
                if (results.IsEmpty || results[i] == EditResult.Applied)
                {
                    bytes += edits[i].WireBytes;
                }
            }

            return bytes;
        }

        /// <summary>
        /// Сервер: записать пачку из принятых правок такта (исход <see cref="EditResult.Applied"/>)
        /// прямо в буфер отправки, без выделений. Возвращает число байт; 0 — принятых правок нет, пачки нет.
        /// </summary>
        public static int WriteAccepted(
            Span<byte> dest, byte mapId, ChunkCoord chunk, uint revision,
            ReadOnlySpan<TileEdit> edits, ReadOnlySpan<EditResult> results)
        {
            if (results.Length < edits.Length)
            {
                throw new ArgumentException($"Нужно {edits.Length} исходов, есть {results.Length}.", nameof(results));
            }

            int accepted = 0;
            for (int i = 0; i < edits.Length; i++)
            {
                if (results[i] == EditResult.Applied)
                {
                    accepted++;
                }
            }

            if (accepted == 0)
            {
                return 0;
            }

            int total = HeaderBytes + CountBytes + EditBytes(edits, results);
            if (dest.Length < total)
            {
                throw new ArgumentException($"Нужно {total} байт, есть {dest.Length}.", nameof(dest));
            }

            WriteHeader(dest, mapId, chunk, revision, accepted);
            int at = HeaderBytes + CountBytes;
            for (int i = 0; i < edits.Length; i++)
            {
                if (results[i] == EditResult.Applied)
                {
                    at += edits[i].Write(dest.Slice(at));
                }
            }

            return at;
        }

        /// <summary>Записать пачку целиком и вернуть число байт.</summary>
        public int Write(Span<byte> dest)
        {
            int total = WireBytes;
            if (dest.Length < total)
            {
                throw new ArgumentException($"Нужно {total} байт, есть {dest.Length}.", nameof(dest));
            }

            WriteHeader(dest, MapId, Chunk, Revision, Edits.Length);
            int at = HeaderBytes + CountBytes;
            for (int i = 0; i < Edits.Length; i++)
            {
                at += Edits[i].Write(dest.Slice(at));
            }

            return at;
        }

        /// <summary>Клиент: прочитать пачку и вернуть число прочитанных байт.</summary>
        public static int Read(ReadOnlySpan<byte> src, out TilePatch patch)
        {
            if (src.Length < HeaderBytes + CountBytes)
            {
                throw new InvalidDataException($"Пачка правок: заголовок {HeaderBytes + CountBytes} байт, есть {src.Length}.");
            }

            byte mapId = src[0];
            var chunk = new ChunkCoord(Bytes.ReadUInt16(src, 1), Bytes.ReadUInt16(src, 3));
            uint revision = Bytes.ReadUInt32(src, 5);
            int count = Bytes.ReadUInt16(src, 9);
            if (count > MaxEdits)
            {
                throw new InvalidDataException($"Пачка правок: {count} правок, больше {MaxEdits}.");
            }

            var edits = new TileEdit[count];
            int at = HeaderBytes + CountBytes;
            for (int i = 0; i < count; i++)
            {
                at += TileEdit.Read(src.Slice(at), out edits[i]);
            }

            patch = new TilePatch(mapId, chunk, revision, edits);
            return at;
        }

        private static void WriteHeader(Span<byte> dest, byte mapId, ChunkCoord chunk, uint revision, int count)
        {
            dest[0] = mapId;
            Bytes.WriteUInt16(dest.Slice(1), (ushort)chunk.X);
            Bytes.WriteUInt16(dest.Slice(3), (ushort)chunk.Y);
            Bytes.WriteUInt32(dest.Slice(5), revision);
            Bytes.WriteUInt16(dest.Slice(9), (ushort)count);
        }

        public override string ToString() =>
            $"пачка chunk({Chunk.X}, {Chunk.Y}) рев. {Revision}: {Edits.Length} правок, {WireBytes} байт";
    }
}
