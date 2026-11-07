using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Чанк 32 × 32 тайла (docs/00, 5.1; docs/07, 3.3).
    /// <para>
    /// Слои — один плотный массив 3072 байта плоскостями, как чанк идёт по сети (docs/07, 3.6):
    /// пол [0, 1024), младшие байты объекта [1024, 2048), старшие байты объекта [2048, 3072).
    /// Плоскости, а не пары байтов: старшие байты почти везде нули и сжимаются в ничто.
    /// </para>
    /// <para>
    /// Записи построек — <c>Dictionary&lt;ushort, BuildRecord&gt;</c> «индекс тайла → запись»,
    /// только у тайлов с постройками; словарь создаётся при первой записи (у чанка дикой природы его нет).
    /// </para>
    /// <para>
    /// Ревизия — +1 на каждую пачку правок (<see cref="ApplyEdits"/>): пачка правок за такт = одна операция (3.7).
    /// Сжатая копия помечена ревизией, с которой собрана; пользуются ею, только если ревизии равны
    /// («свежесть копии», 3.3).
    /// </para>
    /// Индекс тайла в чанке: <c>(x &amp; 31) | ((y &amp; 31) &lt;&lt; 5)</c> — строками сверху вниз.
    /// </summary>
    public sealed class Chunk
    {
        /// <summary>Тайлов в чанке: 32 × 32 = 1024.</summary>
        public const int TileCount = Torus.ChunkSize * Torus.ChunkSize;

        /// <summary>Наибольший индекс тайла в чанке: 1023.</summary>
        public const int MaxIndex = TileCount - 1;

        /// <summary>Байт слоя «пол»: 1 на тайл.</summary>
        public const int FloorLayerBytes = TileCount;

        /// <summary>Байт слоя «объект»: 2 на тайл, двумя плоскостями.</summary>
        public const int ObjectLayerBytes = TileCount * 2;

        /// <summary>Байт плотных слоёв: 1024 + 2048 = 3072 (docs/07, 3.3).</summary>
        public const int LayerBytes = FloorLayerBytes + ObjectLayerBytes;

        /// <summary>Начало плоскости младших байтов объекта.</summary>
        public const int ObjectLowOffset = FloorLayerBytes;

        /// <summary>Начало плоскости старших байтов объекта.</summary>
        public const int ObjectHighOffset = FloorLayerBytes + TileCount;

        /// <summary>Поле «число записей построек» в блоке чанка: 2 байта.</summary>
        public const int BuildCountBytes = 2;

        /// <summary>Байт на запись постройки в блоке чанка: индекс тайла (2) + запись (8).</summary>
        public const int BuildEntryBytes = 2 + BuildRecord.SizeBytes;

        /// <summary>Блок чанка без записей построек: 3072 + 2.</summary>
        public const int MinBlobBytes = LayerBytes + BuildCountBytes;

        /// <summary>
        /// Наибольший блок чанка — запись постройки у каждого тайла: 3074 + 10 · 1024 = 13 314 байт.
        /// Предел при распаковке (backlog [сохранения]): больше — блок испорчен, дальше не разворачиваем.
        /// </summary>
        public const int MaxBlobBytes = MinBlobBytes + BuildEntryBytes * TileCount;

        private readonly byte[] _layers = new byte[LayerBytes];
        private Dictionary<ushort, BuildRecord>? _builds;
        private readonly BuildMapPool? _buildMaps;

        private uint _revision;
        private byte[]? _copy;
        private uint _copyRevision;

        private bool _dirty;
        private bool _changedSinceBase;
        private int _dirtyMinX = Torus.ChunkSize;
        private int _dirtyMinY = Torus.ChunkSize;
        private int _dirtyMaxX = -1;
        private int _dirtyMaxY = -1;

        /// <summary>Чанк без пула словарей построек: словарь создаётся при первой постройке.</summary>
        public Chunk()
        {
        }

        /// <summary>Чанк мира: словарь записей построек при первой постройке берётся из <paramref name="buildMaps"/>.</summary>
        public Chunk(BuildMapPool? buildMaps)
        {
            _buildMaps = buildMaps;
        }

        /// <summary>Ревизия чанка: +1 на каждую пачку правок (docs/07, 3.3).</summary>
        public uint Revision => _revision;

        /// <summary>Чанк менялся после последнего <see cref="ClearDirty"/> (сохранение, отправка).</summary>
        public bool IsDirty => _dirty;

        /// <summary>
        /// Чанк менялся после генерации (docs/07, 3.4): такие чанки целиком идут в каждое сохранение
        /// поверх снимка генерации. Сбрасывается только при записи базового содержимого (<see cref="ResetAsBase"/>).
        /// </summary>
        public bool ChangedSinceBase => _changedSinceBase;

        /// <summary>Прямоугольник изменённых тайлов с последнего <see cref="ClearDirty"/> (в тайлах чанка); пусто — Width ≤ 0.</summary>
        public int DirtyMinX => _dirtyMinX;

        /// <inheritdoc cref="DirtyMinX"/>
        public int DirtyMinY => _dirtyMinY;

        /// <inheritdoc cref="DirtyMinX"/>
        public int DirtyMaxX => _dirtyMaxX;

        /// <inheritdoc cref="DirtyMinX"/>
        public int DirtyMaxY => _dirtyMaxY;

        /// <summary>Число записей построек.</summary>
        public int BuildCount => _builds == null ? 0 : _builds.Count;

        /// <summary>Плотные слои (3072 байта, плоскостями) только для чтения.</summary>
        public ReadOnlySpan<byte> Layers => _layers;

        /// <summary>Размер несжатого блока: слои + число записей + записи (3072 + 2 + 10·n).</summary>
        public int BlobBytes => MinBlobBytes + BuildEntryBytes * BuildCount;

        /// <summary>Ревизия, с которой собрана сжатая копия (смысл есть, только если копия есть).</summary>
        public uint CopyRevision => _copyRevision;

        /// <summary>Копия есть и собрана с текущей ревизией (правило 3.3).</summary>
        public bool HasFreshCopy => _copy != null && _copyRevision == _revision;

        /// <summary>Свежая сжатая копия или null, если её нет или она отстала.</summary>
        public byte[]? FreshCopy => HasFreshCopy ? _copy : null;

        // ---------- Чтение ----------

        /// <summary>Индекс тайла в чанке по координатам тайла (берутся младшие 5 бит).</summary>
        public static int IndexOf(int tileX, int tileY) => (tileX & (Torus.ChunkSize - 1)) | ((tileY & (Torus.ChunkSize - 1)) << Torus.ChunkBits);

        /// <summary>Пол тайла 0…1023.</summary>
        public byte GetFloor(int index)
        {
            CheckIndex(index);
            return _layers[index];
        }

        /// <summary>Объект тайла 0…1023 (сырое значение <see cref="ObjectId"/>).</summary>
        public ushort GetObject(int index)
        {
            CheckIndex(index);
            return (ushort)(_layers[ObjectLowOffset + index] | (_layers[ObjectHighOffset + index] << 8));
        }

        /// <summary>Запись постройки тайла.</summary>
        public bool TryGetBuild(int index, out BuildRecord record)
        {
            CheckIndex(index);
            if (_builds == null)
            {
                record = default;
                return false;
            }

            return _builds.TryGetValue((ushort)index, out record);
        }

        /// <summary>Индексы тайлов с записями построек по возрастанию (обход словаря запрещён, ORP006).</summary>
        public int GetBuildIndices(Span<ushort> dest)
        {
            int count = BuildCount;
            if (dest.Length < count)
            {
                throw new ArgumentException($"Нужно место на {count} индексов, есть {dest.Length}.", nameof(dest));
            }

            if (count == 0)
            {
                return 0;
            }

            // Без обхода словаря: перебор индексов 0…1023 по порядку.
            int n = 0;
            for (int i = 0; i < TileCount && n < count; i++)
            {
                if (_builds!.ContainsKey((ushort)i))
                {
                    dest[n++] = (ushort)i;
                }
            }

            return n;
        }

        // ---------- Базовое содержимое (генерация, загрузка) ----------

        /// <summary>
        /// Записать тайл как содержимое генерации: без ревизии, без «грязи», без «изменён после генерации»
        /// (так мир заполняет генератор, docs/07, 3.4). Сжатая копия становится несвежей.
        /// </summary>
        public void SetBaseTile(int index, byte floor, ushort obj)
        {
            CheckIndex(index);
            WriteTile(index, floor, obj);
            _copy = null;
        }

        /// <summary>Запись постройки в содержимом генерации (постройки мира).</summary>
        public void SetBaseBuild(int index, BuildRecord record)
        {
            CheckIndex(index);
            Builds()[(ushort)index] = record;
            _copy = null;
        }

        /// <summary>Чанк — содержимое генерации: ревизия 0, не грязный, не изменён после генерации.</summary>
        public void ResetAsBase()
        {
            _revision = 0;
            _changedSinceBase = false;
            _copy = null;
            ClearDirty();
        }

        // ---------- Правки (docs/07, 3.7) ----------

        /// <summary>
        /// Пачка правок этого чанка за такт — одна операция: правки применяются по порядку, ревизия +1,
        /// если прошла хоть одна. Тайл после правки — ровно пол, объект и запись из правки (записи нет —
        /// прежняя снимается). Вторая правка того же тайла в пачке получает <see cref="EditResult.Busy"/> —
        /// «побеждает пришедший первым» (3.7). Без выделений памяти.
        /// </summary>
        /// <param name="edits">Правки в порядке прихода запросов.</param>
        /// <param name="results">Исход каждой правки; длина ≥ числа правок.</param>
        /// <returns>Ревизия чанка после пачки.</returns>
        public uint ApplyEdits(ReadOnlySpan<TileEdit> edits, Span<EditResult> results)
        {
            if (results.Length < edits.Length)
            {
                throw new ArgumentException($"Нужно место на {edits.Length} исходов, есть {results.Length}.", nameof(results));
            }

            // Занятые в этой пачке тайлы — 1024 бита на стеке.
            Span<ulong> taken = stackalloc ulong[TileCount / 64];
            taken.Clear();
            int applied = 0;
            for (int i = 0; i < edits.Length; i++)
            {
                TileEdit edit = edits[i];
                int index = edit.Index;
                if (index > MaxIndex)
                {
                    results[i] = EditResult.InvalidIndex;
                    continue;
                }

                if (!ObjectId.IsValidRaw(edit.Obj))
                {
                    results[i] = EditResult.InvalidObject;
                    continue;
                }

                ulong bit = 1UL << (index & 63);
                if ((taken[index >> 6] & bit) != 0)
                {
                    results[i] = EditResult.Busy;
                    continue;
                }

                taken[index >> 6] |= bit;
                Apply(edit);
                results[i] = EditResult.Applied;
                applied++;
            }

            if (applied > 0)
            {
                _revision++;
            }

            return _revision;
        }

        /// <summary>
        /// Клиент: тайл предсказанной правки или отката к копии сервера (docs/07, 3.7 «Ответ»; 0-028) — тайл ровно как в
        /// правке, <b>без ревизии и без «грязи»</b>: ревизия остаётся серверной, следующая пачка сервера ложится как обычно.
        /// </summary>
        public void SetLocalTile(in TileEdit edit)
        {
            CheckIndex(edit.Index);
            WriteTile(edit.Index, edit.Floor, edit.Obj);
            if (edit.HasRecord)
            {
                Builds()[edit.Index] = edit.Record;
            }
            else
            {
                _builds?.Remove(edit.Index);
            }

            _copy = null;
        }

        /// <summary>Тайл как правка (пол, объект, запись постройки, если есть) — для копии сервера под предсказанием.</summary>
        public TileEdit GetTileEdit(int index) =>
            TryGetBuild(index, out BuildRecord record)
                ? new TileEdit((ushort)index, GetFloor(index), GetObject(index), record)
                : new TileEdit((ushort)index, GetFloor(index), GetObject(index));

        /// <summary>
        /// Применить пачку с сервера (клиент): только если ревизия чанка — ровно предыдущая (3.7, «Порядок»).
        /// false — разрыв ревизий, чанк не тронут: клиент просит чанк целиком.
        /// </summary>
        public bool ApplyPatch(in TilePatch patch)
        {
            if (patch.Revision != unchecked(_revision + 1))
            {
                return false;
            }

            TileEdit[] edits = patch.Edits;
            for (int i = 0; i < edits.Length; i++)
            {
                if (edits[i].Index > MaxIndex || !ObjectId.IsValidRaw(edits[i].Obj))
                {
                    throw new InvalidDataException($"Пачка {patch}: правка № {i} вне чанка или с недопустимым объектом.");
                }
            }

            for (int i = 0; i < edits.Length; i++)
            {
                Apply(edits[i]);
            }

            _revision = patch.Revision;
            return true;
        }

        /// <summary>
        /// Отметить тайл изменённым: чанк грязный (сохранение, отправка), изменён после генерации,
        /// прямоугольник правок растёт (по нему фоновый поток считает комнаты, 3.3).
        /// </summary>
        public void MarkDirty(int index)
        {
            CheckIndex(index);
            _dirty = true;
            _changedSinceBase = true;
            int x = index & (Torus.ChunkSize - 1);
            int y = index >> Torus.ChunkBits;
            if (x < _dirtyMinX)
            {
                _dirtyMinX = x;
            }

            if (x > _dirtyMaxX)
            {
                _dirtyMaxX = x;
            }

            if (y < _dirtyMinY)
            {
                _dirtyMinY = y;
            }

            if (y > _dirtyMaxY)
            {
                _dirtyMaxY = y;
            }
        }

        /// <summary>Чанк сохранён и разослан: «грязь» и прямоугольник правок сбрасываются.</summary>
        public void ClearDirty()
        {
            _dirty = false;
            _dirtyMinX = Torus.ChunkSize;
            _dirtyMinY = Torus.ChunkSize;
            _dirtyMaxX = -1;
            _dirtyMaxY = -1;
        }

        // ---------- Сжатая копия (docs/07, 3.3) ----------

        /// <summary>
        /// Пересобрать сжатую копию (Deflate уровня 6) и пометить её текущей ревизией. Возвращает длину копии.
        /// Уровень 6: в .NET Standard 2.1 числового уровня нет, <see cref="CompressionLevel.Optimal"/> — это
        /// уровень zlib по умолчанию, 6 (и в .NET, и в Mono Unity).
        /// </summary>
        public int RebuildCompressed()
        {
            _copy = Compress();
            _copyRevision = _revision;
            return _copy.Length;
        }

        /// <summary>Сжать блок чанка (<see cref="WriteBlob"/>) Deflate-6, не трогая копию.</summary>
        public byte[] Compress()
        {
            var blob = new byte[BlobBytes];
            WriteBlob(blob);
            return CompressBlob(blob);
        }

        /// <summary>
        /// Сжать готовый блок чанка Deflate-6 — без самого чанка: сохранение копирует блок на границе такта
        /// и сжимает его в фоновом потоке (docs/07, 6.4).
        /// </summary>
        public static byte[] CompressBlob(ReadOnlySpan<byte> blob)
        {
            using var output = new MemoryStream(512);
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(blob);
            }

            return output.ToArray();
        }

        /// <summary>
        /// Загрузка (снимок, изменения, чанк по сети): содержимое — несжатый блок <paramref name="blob"/>,
        /// копия — <paramref name="compressed"/> (из неё блок и развёрнут, поэтому она сразу свежая).
        /// Чанк не грязный; «изменён после генерации» — как сказано.
        /// </summary>
        public void Load(ReadOnlySpan<byte> blob, byte[]? compressed, uint revision, bool changedSinceBase)
        {
            ReadBlob(blob);
            _revision = revision;
            _copy = compressed;
            _copyRevision = revision;
            _changedSinceBase = changedSinceBase;
            ClearDirty();
        }

        /// <summary>
        /// Развернуть сжатый блок чанка. Предел — <see cref="MaxBlobBytes"/> (backlog [сохранения]): «бомба»
        /// из пары сотен байт, разворачивающаяся в гигабайты, отвергается на первом лишнем байте.
        /// </summary>
        public static byte[] Decompress(byte[] compressed)
        {
            if (compressed == null)
            {
                throw new ArgumentNullException(nameof(compressed));
            }

            using var input = new MemoryStream(compressed, false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxBlobBytes + 1);
            try
            {
                int total = 0;
                while (true)
                {
                    int n = deflate.Read(buffer, total, MaxBlobBytes + 1 - total);
                    if (n == 0)
                    {
                        break;
                    }

                    total += n;
                    if (total > MaxBlobBytes)
                    {
                        throw new InvalidDataException($"Сжатый чанк разворачивается больше чем в {MaxBlobBytes} байт — испорчен.");
                    }
                }

                var result = new byte[total];
                Buffer.BlockCopy(buffer, 0, result, 0, total);
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // ---------- Блок чанка: слои + записи (сеть 3.6, снимок 3.4) ----------

        /// <summary>
        /// Несжатый блок: слои (3072, плоскостями), число записей (2), записи «индекс (2) + запись (8)»
        /// по возрастанию индекса. Возвращает число байт.
        /// </summary>
        public int WriteBlob(Span<byte> dest)
        {
            int total = BlobBytes;
            if (dest.Length < total)
            {
                throw new ArgumentException($"Нужно {total} байт, есть {dest.Length}.", nameof(dest));
            }

            _layers.CopyTo(dest);
            int count = BuildCount;
            Bytes.WriteUInt16(dest.Slice(LayerBytes), (ushort)count);
            int at = MinBlobBytes;
            for (int i = 0; i < TileCount && count > 0; i++)
            {
                if (_builds!.TryGetValue((ushort)i, out BuildRecord record))
                {
                    Bytes.WriteUInt16(dest.Slice(at), (ushort)i);
                    record.Write(dest.Slice(at + 2));
                    at += BuildEntryBytes;
                    count--;
                }
            }

            return total;
        }

        /// <summary>Заменить содержимое блоком (<see cref="WriteBlob"/>). Ревизия и флаги не меняются; копия несвежая.</summary>
        public void ReadBlob(ReadOnlySpan<byte> src)
        {
            if (src.Length < MinBlobBytes)
            {
                throw new InvalidDataException($"Блок чанка: нужно не меньше {MinBlobBytes} байт, есть {src.Length}.");
            }

            int count = Bytes.ReadUInt16(src, LayerBytes);
            int expected = MinBlobBytes + BuildEntryBytes * count;
            if (src.Length != expected)
            {
                throw new InvalidDataException($"Блок чанка: на {count} записей нужно {expected} байт, есть {src.Length}.");
            }

            src.Slice(0, LayerBytes).CopyTo(_layers);
            _builds?.Clear();
            int at = MinBlobBytes;
            for (int i = 0; i < count; i++)
            {
                int index = Bytes.ReadUInt16(src, at);
                if (index > MaxIndex)
                {
                    throw new InvalidDataException($"Блок чанка: запись постройки с индексом {index}.");
                }

                Builds()[(ushort)index] = BuildRecord.Read(src.Slice(at + 2));
                at += BuildEntryBytes;
            }

            _copy = null;
        }

        /// <summary>Побайтное равенство содержимого и ревизии (приёмка 3.13 п. 6).</summary>
        public bool ContentEquals(Chunk other)
        {
            if (other == null || _revision != other._revision || BuildCount != other.BuildCount)
            {
                return false;
            }

            if (!((ReadOnlySpan<byte>)_layers).SequenceEqual(other._layers))
            {
                return false;
            }

            for (int i = 0; i < TileCount && _builds != null; i++)
            {
                bool mine = _builds.TryGetValue((ushort)i, out BuildRecord a);
                bool theirs = other.TryGetBuild(i, out BuildRecord b);
                if (mine != theirs || (mine && a != b))
                {
                    return false;
                }
            }

            return true;
        }

        // ---------- Внутреннее ----------

        private void Apply(in TileEdit edit)
        {
            int index = edit.Index;
            WriteTile(index, edit.Floor, edit.Obj);
            if (edit.HasRecord)
            {
                Builds()[(ushort)index] = edit.Record;
            }
            else
            {
                _builds?.Remove((ushort)index);
            }

            MarkDirty(index);
        }

        private void WriteTile(int index, byte floor, ushort obj)
        {
            _layers[index] = floor;
            _layers[ObjectLowOffset + index] = (byte)obj;
            _layers[ObjectHighOffset + index] = (byte)(obj >> 8);
        }

        private Dictionary<ushort, BuildRecord> Builds() =>
            _builds ??= _buildMaps != null ? _buildMaps.Rent() : new Dictionary<ushort, BuildRecord>();

        private static void CheckIndex(int index)
        {
            if ((uint)index > MaxIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Индекс тайла в чанке — 0…{MaxIndex}.");
            }
        }
    }
}
