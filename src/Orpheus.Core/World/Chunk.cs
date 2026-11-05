using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Чанк 32 × 32 тайла (docs/00, 5.1; docs/07, 3.3): два плотных массива — пол (1024 байта)
    /// и объект (2048 байта) = 3072 байта на чанк, плюс словарь записей построек
    /// «индекс тайла → <see cref="BuildRecord"/>» (8 байт на запись, только у тайлов с постройками игроков).
    /// <para>
    /// Ревизия (<see cref="Revision"/>) — счётчик: +1 на каждую пачку правок (<see cref="ApplyEdits"/>),
    /// одна пачка за такт = одна операция (docs/07, 3.7).
    /// </para>
    /// <para>
    /// Сжатая копия чанка помечена ревизией, с которой собрана (<see cref="CopyRevision"/>).
    /// «Свежесть копии» (docs/07, 3.3): копией пользуются, только если её ревизия равна ревизии чанка,
    /// иначе её пересобирают (<see cref="RebuildCompressed"/>). Копия — Deflate
    /// (<see cref="CompressionLevel.Optimal"/>, один раз на ревизию).
    /// </para>
    /// Индекс тайла в чанке: <c>(x &amp; 31) | ((y &amp; 31) &lt;&lt; 5)</c>, строками сверху вниз.
    /// </summary>
    public sealed class Chunk
    {
        /// <summary>Тайлов в чанке: 32 × 32 = 1024.</summary>
        public const int TileCount = Torus.ChunkSize * Torus.ChunkSize;

        /// <summary>Байт слоя «пол»: 1 байт на тайл.</summary>
        public const int FloorLayerBytes = TileCount;

        /// <summary>Байт слоя «объект»: 2 байта на тайл (сырое значение <see cref="ObjectId"/>).</summary>
        public const int ObjectLayerBytes = TileCount * 2;

        /// <summary>Байт плотных слоёв чанка: 1024 + 2048 = 3072 (docs/07, 3.3).</summary>
        public const int LayerBytes = FloorLayerBytes + ObjectLayerBytes;

        /// <summary>Наибольший индекс тайла в чанке: 1023.</summary>
        public const int MaxIndex = TileCount - 1;

        /// <summary>Поле «число записей построек» в блоке чанка: 2 байта.</summary>
        public const int BuildCountBytes = 2;

        /// <summary>Байт на запись постройки в блоке чанка: индекс (2) + запись (8) = 10.</summary>
        public const int BuildEntryBytes = 2 + BuildRecord.SizeBytes;

        private readonly byte[] _floor;
        private readonly byte[] _obj;
        private readonly Dictionary<ushort, BuildRecord> _builds;

        private uint _revision;
        private uint _copyRevision;
        private byte[]? _copy;

        private bool _dirty;
        private int _dirtyMin;
        private int _dirtyMax;

        public Chunk()
        {
            _floor = new byte[FloorLayerBytes];
            _obj = new byte[ObjectLayerBytes];
            _builds = new Dictionary<ushort, BuildRecord>();
            _dirtyMin = -1;
            _dirtyMax = -1;
        }

        /// <summary>Ревизия чанка: +1 на каждую пачку правок (docs/07, 3.3).</summary>
        public uint Revision => _revision;

        /// <summary>Чанк меняли после сохранения/отправки (для <c>DirtyChunks</c> и сохранения).</summary>
        public bool IsDirty => _dirty;

        /// <summary>Меньший индекс грязного тайла, −1 если чанк чистый (прямоугольник правки для фоновых комнат, 3.3).</summary>
        public int DirtyTileMin => _dirtyMin;

        /// <summary>Больший индекс грязного тайла, −1 если чанк чистый.</summary>
        public int DirtyTileMax => _dirtyMax;

        /// <summary>Число записей построек в чанке.</summary>
        public int BuildCount => _builds.Count;

        /// <summary>Записи построек: индекс тайла → запись (только у тайлов с постройками).</summary>
        public IReadOnlyDictionary<ushort, BuildRecord> Builds => _builds;

        /// <summary>Слой пола (1024 байта) для чтения.</summary>
        public ReadOnlySpan<byte> FloorBytes => _floor;

        /// <summary>Слой объекта (2048 байта, пары байтов) для чтения.</summary>
        public ReadOnlySpan<byte> ObjectBytes => _obj;

        /// <summary>Размер несжатого блока чанка (слои + записи построек): 3072 + 2 + 10·n.</summary>
        public int BlobBytes => LayerBytes + BuildCountBytes + BuildEntryBytes * _builds.Count;

        /// <summary>Ревизия, с которой собрана сжатая копия (0 — копии нет).</summary>
        public uint CopyRevision => _copyRevision;

        /// <summary>Копия свежая: она собрана с текущей ревизией (правило 3.3).</summary>
        public bool HasFreshCopy => _copy != null && _copyRevision == _revision;

        /// <summary>Свежая сжатая копия чанка и null, если копия отстала (её надо пересобрать).</summary>
        public byte[]? FreshCopy => HasFreshCopy ? _copy : null;

        // ---------- Чтение тайлов ----------

        /// <summary>Пол тайла по индексу 0…1023.</summary>
        public byte GetFloor(int index)
        {
            CheckIndex(index);
            return _floor[index];
        }

        /// <summary>Объект тайла по индексу 0…1023 (сырое значение <see cref="ObjectId"/>).</summary>
        public ushort GetObject(int index)
        {
            CheckIndex(index);
            int at = index * 2;
            return (ushort)(_obj[at] | (_obj[at + 1] << 8));
        }

        /// <summary>Запись постройки тайла.</summary>
        public bool TryGetBuild(ushort index, out BuildRecord record) => _builds.TryGetValue(index, out record);

        // ---------- Базовое содержимое (генерация и снимок) ----------

        /// <summary>
        /// Записать тайл как базовое содержимое чанка: пол и объект, без правки и без роста ревизии
        /// (так заполняет мир генератор и загрузка снимка, docs/07, 3.4).
        /// </summary>
        public void WriteBase(int index, byte floor, ushort obj)
        {
            CheckIndex(index);
            _floor[index] = floor;
            _obj[index * 2] = (byte)obj;
            _obj[index * 2 + 1] = (byte)(obj >> 8);
        }

        /// <summary>Запись постройки базового содержимого (мир, реестры, загрузка снимка).</summary>
        public void WriteBaseBuild(ushort index, BuildRecord record)
        {
            CheckIndex(index);
            _builds[index] = record;
        }

        /// <summary>Снять запись постройки базового содержимого.</summary>
        public bool RemoveBaseBuild(ushort index) => _builds.Remove(index);

        /// <summary>Поставить ревизию (загрузка снимка и изменённых чанков, docs/07, 3.4).</summary>
        public void SetRevision(uint revision)
        {
            _revision = revision;
            _copy = null;
            _copyRevision = 0;
        }

        // ---------- Правки (docs/07, 3.7) ----------

        /// <summary>
        /// Применить пачку правок одного чанка за такт: одна операция, ревизия +1 (3.7).
        /// Правки идут по порядку массива; две правки на один тайл в одной пачке — первая применяется,
        /// вторая получает «занято» (конфликт, 3.7). Снятый объект (объект 0) снимает и запись постройки.
        /// </summary>
        /// <param name="edits">Правки в порядке их прохождения.</param>
        /// <param name="count">Сколько первых правок применить.</param>
        /// <param name="results">Массив не короче <paramref name="count"/> — исход каждой правки (можно null).</param>
        /// <returns>Новая ревизия чанка (поднялась ровно на 1, если применилась хотя бы одна правка).</returns>
        public uint ApplyEdits(TileEdit[] edits, int count, EditResult[]? results)
        {
            if (edits == null)
            {
                throw new ArgumentNullException(nameof(edits));
            }

            if (count < 0 || count > edits.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Число правок — от 0 до длины массива.");
            }

            if (results != null && results.Length < count)
            {
                throw new ArgumentException($"Нужно место на {count} исходов, есть {results.Length}.", nameof(results));
            }

            var applied = count > 0 ? new ushort[count] : Array.Empty<ushort>();
            int appliedCount = 0;

            for (int i = 0; i < count; i++)
            {
                TileEdit edit = edits[i];
                int index = edit.Index;
                if (index > MaxIndex)
                {
                    if (results != null)
                    {
                        results[i] = EditResult.InvalidIndex;
                    }

                    continue;
                }

                bool busy = false;
                for (int j = 0; j < appliedCount; j++)
                {
                    if (applied[j] == index)
                    {
                        busy = true;
                        break;
                    }
                }

                if (busy)
                {
                    if (results != null)
                    {
                        results[i] = EditResult.Busy;
                    }

                    continue;
                }

                _floor[index] = edit.Floor;
                _obj[index * 2] = (byte)edit.Obj;
                _obj[index * 2 + 1] = (byte)(edit.Obj >> 8);
                if (edit.Obj == ObjectId.Empty)
                {
                    _builds.Remove(index);
                }
                else if (edit.HasRecord)
                {
                    _builds[index] = edit.Record;
                }

                MarkDirty(index);
                applied[appliedCount++] = (ushort)index;
                if (results != null)
                {
                    results[i] = EditResult.Applied;
                }
            }

            if (appliedCount > 0)
            {
                _revision++;
            }

            return _revision;
        }

        /// <summary>
        /// Отметить чанк грязным и расширить прямоугольник изменённых тайлов
        /// (для сохранения и отправки; основной поток копирует прямоугольник вокруг правки, 3.3).
        /// </summary>
        public void MarkDirty(int index)
        {
            CheckIndex(index);
            _dirty = true;
            if (_dirtyMin < 0 || index < _dirtyMin)
            {
                _dirtyMin = index;
            }

            if (_dirtyMax < 0 || index > _dirtyMax)
            {
                _dirtyMax = index;
            }
        }

        /// <summary>Чанк сохранён и отправлен: больше не грязный (прямоугольник правок забывается).</summary>
        public void ClearDirty()
        {
            _dirty = false;
            _dirtyMin = -1;
            _dirtyMax = -1;
        }

        // ---------- Сжатая копия (docs/07, 3.3) ----------

        /// <summary>
        /// Пересобрать сжатую копию чанка (Deflate, <see cref="CompressionLevel.Optimal"/>, один раз на ревизию)
        /// и пометить её текущей ревизией. Копия — слои (3072 байта) + записи построек, отсортированные по индексу.
        /// </summary>
        /// <returns>Длина сжатой копии в байтах.</returns>
        public int RebuildCompressed()
        {
            _copy = CompressBlob(this);
            _copyRevision = _revision;
            return _copy.Length;
        }

        /// <summary>Сжать несжатый блок чанка (слои + записи построек) Deflate-ом уровня <see cref="CompressionLevel.Optimal"/>.</summary>
        public static byte[] CompressBlob(Chunk chunk)
        {
            if (chunk == null)
            {
                throw new ArgumentNullException(nameof(chunk));
            }

            var blob = new byte[chunk.BlobBytes];
            chunk.WriteBlob(blob);

            using var output = new MemoryStream(Math.Min(blob.Length, 1024));
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
            {
                deflate.Write(blob, 0, blob.Length);
            }

            return output.ToArray();
        }

        /// <summary>Развернуть сжатый блок чанка в чанк <paramref name="target"/> (слои + записи; ревизия и грязь не меняются).</summary>
        public static void DecompressBlob(byte[] compressed, Chunk target)
        {
            if (compressed == null)
            {
                throw new ArgumentNullException(nameof(compressed));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            using (var input = new MemoryStream(compressed, 0, compressed.Length, false))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(LayerBytes + 4096))
            {
                deflate.CopyTo(output);
                target.ReadBlob(output.ToArray());
            }
        }

        // ---------- Блок чанка (слои + записи) для снимка и сети ----------

        /// <summary>
        /// Записать несжатый блок чанка: пол (1024), объект (2048), число записей (2),
        /// записи «индекс (2) + запись (8)» в порядке индекса. Возвращает число байт.
        /// </summary>
        public int WriteBlob(Span<byte> dest)
        {
            int total = BlobBytes;
            if (dest.Length < total)
            {
                throw new ArgumentException($"Нужно {total} байт, есть {dest.Length}.", nameof(dest));
            }

            _floor.CopyTo(dest);
            _obj.CopyTo(dest.Slice(FloorLayerBytes));

            int at = LayerBytes;
            TilePatch.WriteUInt16(dest.Slice(at), (ushort)_builds.Count);
            at += BuildCountBytes;
            foreach (ushort index in SortedBuildKeys())
            {
                TilePatch.WriteUInt16(dest.Slice(at), index);
                _builds[index].Write(dest.Slice(at + 2));
                at += BuildEntryBytes;
            }

            return total;
        }

        /// <summary>Прочитать несжатый блок чанка (см. <see cref="WriteBlob"/>): содержимое заменяется, копия и грязь сбрасываются.</summary>
        public void ReadBlob(ReadOnlySpan<byte> src)
        {
            if (src.Length < LayerBytes + BuildCountBytes)
            {
                throw new ArgumentException(
                    $"Нужно не меньше {LayerBytes + BuildCountBytes} байт, есть {src.Length}.", nameof(src));
            }

            src.Slice(0, FloorLayerBytes).CopyTo(_floor);
            src.Slice(FloorLayerBytes, ObjectLayerBytes).CopyTo(_obj);

            int at = LayerBytes;
            int count = TilePatch.ReadUInt16(src, at);
            at += BuildCountBytes;
            if (src.Length < at + BuildEntryBytes * (long)count)
            {
                throw new ArgumentException(
                    $"Нужно {at + BuildEntryBytes * (long)count} байт на {count} записей, есть {src.Length}.", nameof(src));
            }

            _builds.Clear();
            for (int i = 0; i < count; i++)
            {
                ushort index = (ushort)TilePatch.ReadUInt16(src, at);
                _builds[index] = BuildRecord.Read(src.Slice(at + 2));
                at += BuildEntryBytes;
            }

            _copy = null;
            _copyRevision = 0;
            ClearDirty();
        }

        // ---------- Сравнение содержимого (приёмка 3.13 п. 6: побайтное совпадение) ----------

        /// <summary>Совпадают ли слои, записи построек и ревизия с чанком <paramref name="other"/> (побайтно).</summary>
        public bool ContentEquals(Chunk other)
        {
            if (other == null)
            {
                return false;
            }

            if (_revision != other._revision || _builds.Count != other._builds.Count)
            {
                return false;
            }

            if (!ToRawBytes().AsSpan().SequenceEqual(other.ToRawBytes()))
            {
                return false;
            }

            ushort[] keys = SortedBuildKeys();
            for (int i = 0; i < keys.Length; i++)
            {
                if (!_builds.TryGetValue(keys[i], out BuildRecord mine))
                {
                    return false;
                }

                if (!other._builds.TryGetValue(keys[i], out BuildRecord theirs) || mine != theirs)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Плотные слои одним массивом: пол (1024) + объект (2048) = 3072 байта.</summary>
        public byte[] ToRawBytes()
        {
            var raw = new byte[LayerBytes];
            _floor.CopyTo(raw, 0);
            _obj.CopyTo(raw, FloorLayerBytes);
            return raw;
        }

        /// <summary>Записать плотные слои в <paramref name="dest"/> (нужно 3072 байта).</summary>
        public void WriteLayers(Span<byte> dest)
        {
            if (dest.Length < LayerBytes)
            {
                throw new ArgumentException($"Нужно {LayerBytes} байт, есть {dest.Length}.", nameof(dest));
            }

            _floor.CopyTo(dest);
            _obj.CopyTo(dest.Slice(FloorLayerBytes));
        }

        /// <summary>Прочитать плотные слои из <paramref name="src"/> (нужно 3072 байта); записи построек не меняются.</summary>
        public void ReadLayers(ReadOnlySpan<byte> src)
        {
            if (src.Length < LayerBytes)
            {
                throw new ArgumentException($"Нужно {LayerBytes} байт, есть {src.Length}.", nameof(src));
            }

            src.Slice(0, FloorLayerBytes).CopyTo(_floor);
            src.Slice(FloorLayerBytes, ObjectLayerBytes).CopyTo(_obj);
            _copy = null;
            _copyRevision = 0;
        }

        /// <summary>Индексы записей построек, отсортированные (обход словаря запрещён правилом ORP006).</summary>
        public ushort[] SortedBuildKeys()
        {
            var keys = new ushort[_builds.Count];
            _builds.Keys.CopyTo(keys, 0);
            Array.Sort(keys);
            return keys;
        }

        private static void CheckIndex(int index)
        {
            if (index < 0 || index > MaxIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Индекс тайла в чанке — 0…{MaxIndex}.");
            }
        }
    }
}
