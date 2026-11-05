using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Мир целиком в памяти (docs/07, 3.3): форма карты + словарь «координата чанка → чанк»,
    /// чанк создаётся по требованию. Пол и объект читаются по координате тайла, правки идут
    /// пачками на чанк (<see cref="ApplyEdits"/>, docs/07, 3.7).
    /// <para>
    /// Обход — только по отсортированным ключам (<see cref="ChunkCoords"/>, <see cref="DirtyChunks"/>):
    /// обход словаря запрещён правилом ORP006, чтобы порядок не зависел от вставки и от процесса.
    /// </para>
    /// Координаты тайла и чанка сворачиваются тором (<see cref="Torus.Wrap"/>), поэтому мир на торе
    /// не имеет края: тайл за краем — тайл с другого края.
    /// </summary>
    public sealed class WorldModel
    {
        /// <summary>Маска индекса тайла в чанке по оси: 31.</summary>
        public const int TileIndexMask = Torus.ChunkSize - 1;

        private readonly Dictionary<ChunkCoord, Chunk> _chunks;

        public WorldModel(MapShape shape, ulong seed, int generatorVersion = 1)
        {
            if (generatorVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(generatorVersion), generatorVersion, "Версия генератора — не отрицательная.");
            }

            Shape = shape;
            Seed = seed;
            GeneratorVersion = generatorVersion;
            _chunks = new Dictionary<ChunkCoord, Chunk>();
        }

        /// <summary>Форма карты (мир — тор, данж — плоская карта).</summary>
        public MapShape Shape { get; }

        /// <summary>Зерно мира (пишется в снимок генерации, docs/07, 3.4).</summary>
        public ulong Seed { get; }

        /// <summary>Версия генератора (3.4): растёт при любом изменении результата хотя бы одного тайла.</summary>
        public int GeneratorVersion { get; }

        /// <summary>Созданных чанков (чанк создаётся по требованию).</summary>
        public int ChunkCount => _chunks.Count;

        /// <summary>Всего чанков на карте: ChunksX · ChunksY (4096² → 128 × 128 = 16 384).</summary>
        public int TotalChunks => Shape.ChunksX * Shape.ChunksY;

        /// <summary>Байт плотных слоёв во всей карте, если материализованы все чанки: 3072 · чанков.</summary>
        public long FullLayersBytes => (long)TotalChunks * Chunk.LayerBytes;

        /// <summary>
        /// Чанк по координате, создаётся по требованию (пустой, ревизия 0).
        /// Координата сворачивается тором: на торе чанк за краем — чанк с другого края.
        /// </summary>
        public Chunk GetChunk(ChunkCoord chunk)
        {
            ChunkCoord key = Torus.Wrap(Shape, chunk);
            if (!_chunks.TryGetValue(key, out Chunk? chunkRef))
            {
                chunkRef = new Chunk();
                _chunks[key] = chunkRef;
            }

            return chunkRef;
        }

        /// <summary>Чанк, если он уже создан (null — чанка нет).</summary>
        public Chunk? FindChunk(ChunkCoord chunk)
        {
            ChunkCoord key = Torus.Wrap(Shape, chunk);
            return _chunks.TryGetValue(key, out Chunk? chunkRef) ? chunkRef : null;
        }

        /// <summary>Чанк уже создан.</summary>
        public bool HasChunk(ChunkCoord chunk) => _chunks.ContainsKey(Torus.Wrap(Shape, chunk));

        // ---------- Тайлы ----------

        /// <summary>Индекс тайла в чанке (0…1023) по координате тайла: строками сверху вниз.</summary>
        public static int IndexOf(TileCoord tile) => (tile.X & TileIndexMask) | ((tile.Y & TileIndexMask) << Torus.ChunkBits);

        /// <summary>Координата тайла в мире по чанку и индексу в нём (свёрнутая).</summary>
        public TileCoord TileOf(ChunkCoord chunk, int index)
        {
            if (index < 0 || index > Chunk.MaxIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Индекс тайла в чанке — 0…{Chunk.MaxIndex}.");
            }

            TileCoord corner = Torus.CornerOf(Torus.Wrap(Shape, chunk));
            return Torus.Wrap(Shape, new TileCoord(corner.X + (index & TileIndexMask), corner.Y + ((index >> Torus.ChunkBits) & TileIndexMask)));
        }

        /// <summary>Пол тайла (чанк создаётся по требованию).</summary>
        public byte GetFloor(TileCoord tile) => GetChunk(Torus.ChunkOf(Shape, tile)).GetFloor(IndexOf(Torus.Wrap(Shape, tile)));

        /// <summary>Объект тайла (чанк создаётся по требованию).</summary>
        public ushort GetObject(TileCoord tile) => GetChunk(Torus.ChunkOf(Shape, tile)).GetObject(IndexOf(Torus.Wrap(Shape, tile)));

        /// <summary>Запись постройки тайла (null, если чанка или записи нет).</summary>
        public BuildRecord? BuildAt(TileCoord tile)
        {
            Chunk? chunk = FindChunk(Torus.ChunkOf(Shape, tile));
            if (chunk == null)
            {
                return null;
            }

            return chunk.TryGetBuild((ushort)IndexOf(Torus.Wrap(Shape, tile)), out BuildRecord record) ? record : (BuildRecord?)null;
        }

        // ---------- Правки (docs/07, 3.7) ----------

        /// <summary>
        /// Пачка правок одного чанка за такт = одна операция: ревизия +1 (3.7).
        /// Две правки на один тайл в одной пачке — первая применяется, вторая получает <see cref="EditResult.Busy"/>.
        /// Пустая пачка ревизию не поднимает.
        /// </summary>
        public uint ApplyEdits(ChunkCoord chunk, TileEdit[] edits, int count, EditResult[]? results) =>
            GetChunk(chunk).ApplyEdits(edits, count, results);

        /// <summary>
        /// Пачка правок по глобальным координатам тайлов: правки раскладываются по чанкам
        /// (за один такт на чанк — своя пачка, своя ревизия), порядок чанков — по отсортированным ключам.
        /// </summary>
        /// <param name="tiles">Координаты тайлов правок (в порядке прохождения).</param>
        /// <param name="edits">Правки: <see cref="TileEdit.Index"/> заполняется по <paramref name="tiles"/>.</param>
        /// <param name="results">Исход каждой правки (не короче числа правок).</param>
        /// <returns>Число чанков, у которых поднялась ревизия.</returns>
        public int ApplyTileEdits(TileCoord[] tiles, TileEdit[] edits, EditResult[] results)
        {
            if (tiles == null)
            {
                throw new ArgumentNullException(nameof(tiles));
            }

            if (edits == null)
            {
                throw new ArgumentNullException(nameof(edits));
            }

            if (edits.Length < tiles.Length)
            {
                throw new ArgumentException($"Правок ({edits.Length}) меньше, чем тайлов ({tiles.Length}).", nameof(edits));
            }

            if (results == null || results.Length < tiles.Length)
            {
                throw new ArgumentException("Нужно место на исход каждой правки.", nameof(results));
            }

            int count = tiles.Length;
            var local = new TileEdit[count];
            var chunks = new ChunkCoord[count];
            for (int i = 0; i < count; i++)
            {
                TileCoord wrapped = Torus.Wrap(Shape, tiles[i]);
                chunks[i] = Torus.ChunkOf(Shape, wrapped);
                ushort index = (ushort)IndexOf(wrapped);
                local[i] = edits[i].HasRecord
                    ? new TileEdit(index, edits[i].Floor, edits[i].Obj, edits[i].Record)
                    : new TileEdit(index, edits[i].Floor, edits[i].Obj);
            }

            // Порядок чанков фиксирован: отсортированные ключи (ORP006), а не порядок правок.
            var order = new ChunkCoord[count];
            var keys = new ChunkCoord[count];
            int keyCount = 0;
            for (int i = 0; i < count; i++)
            {
                bool seen = false;
                for (int j = 0; j < keyCount; j++)
                {
                    if (keys[j] == chunks[i])
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen)
                {
                    keys[keyCount++] = chunks[i];
                }
            }

            Array.Sort(keys, 0, keyCount, ChunkCoordOrder.Instance);
            for (int i = 0; i < keyCount; i++)
            {
                order[i] = keys[i];
            }

            int touched = 0;
            for (int i = 0; i < keyCount; i++)
            {
                ChunkCoord target = order[i];
                var batch = new TileEdit[count];
                var batchSource = new int[count];
                int n = 0;
                for (int j = 0; j < count; j++)
                {
                    if (chunks[j] == target)
                    {
                        batch[n] = local[j];
                        batchSource[n] = j;
                        n++;
                    }
                }

                var batchResults = new EditResult[n];
                GetChunk(target).ApplyEdits(batch, n, batchResults);
                for (int k = 0; k < n; k++)
                {
                    results[batchSource[k]] = batchResults[k];
                }

                touched++;
            }

            return touched;
        }

        // ---------- Обход (по отсортированным ключам) ----------

        /// <summary>Координаты всех созданных чанков, отсортированные (X, затем Y).</summary>
        public ChunkCoord[] ChunkCoords()
        {
            var keys = new ChunkCoord[_chunks.Count];
            _chunks.Keys.CopyTo(keys, 0);
            Array.Sort(keys, ChunkCoordOrder.Instance);
            return keys;
        }

        /// <summary>
        /// Координаты грязных чанков (для сохранения и отправки), отсортированные.
        /// Обход — по отсортированному массиву ключей, никогда по обходу словаря (ORP006).
        /// </summary>
        public ChunkCoord[] DirtyChunks()
        {
            var keys = new ChunkCoord[_chunks.Count];
            _chunks.Keys.CopyTo(keys, 0);
            Array.Sort(keys, ChunkCoordOrder.Instance);

            var dirty = new ChunkCoord[keys.Length];
            int n = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                if (_chunks[keys[i]].IsDirty)
                {
                    dirty[n++] = keys[i];
                }
            }

            Array.Resize(ref dirty, n);
            return dirty;
        }

        /// <summary>Отметить чанки сохранёнными и отправленными.</summary>
        public void ClearDirty(ChunkCoord chunk)
        {
            Chunk? found = FindChunk(chunk);
            found?.ClearDirty();
        }

        /// <summary>Отметить все чанки сохранёнными и отправленными.</summary>
        public void ClearAllDirty()
        {
            ChunkCoord[] keys = ChunkCoords();
            for (int i = 0; i < keys.Length; i++)
            {
                _chunks[keys[i]].ClearDirty();
            }
        }
    }
}
