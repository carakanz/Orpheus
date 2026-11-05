using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Мир целиком в памяти (docs/07, 3.3): форма карты + словарь «координата чанка → чанк».
    /// Чанк создаётся по требованию — при первой записи; чтение несозданного чанка даёт пустой тайл
    /// и чанк не создаёт. Координаты сворачиваются тором (<see cref="Torus.Wrap(in MapShape, TileCoord)"/>):
    /// тайл за краем — тайл с другого края.
    /// <para>
    /// Обход — только по отсортированным ключам (<see cref="GetChunkCoords"/>, <see cref="GetDirtyChunks"/>),
    /// никогда по обходу словаря (ORP006): порядок не зависит от истории вставок.
    /// </para>
    /// </summary>
    public sealed class WorldModel
    {
        // Ёмкость — все чанки карты сразу: поверхность целиком в памяти (3.3), словарь не перестраивается.
        private readonly Dictionary<ChunkCoord, Chunk> _chunks;

        // Отсортированные ключи: пересобираются при появлении нового чанка, а не на каждый обход.
        private ChunkCoord[] _sortedKeys = Array.Empty<ChunkCoord>();
        private bool _keysStale;

        public WorldModel(MapShape shape, ulong seed = 0, int generatorVersion = 0)
        {
            if (generatorVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(generatorVersion), generatorVersion, "Версия генератора не отрицательная.");
            }

            Shape = shape;
            Seed = seed;
            GeneratorVersion = generatorVersion;
            _chunks = new Dictionary<ChunkCoord, Chunk>(shape.ChunksX * shape.ChunksY);
        }

        /// <summary>Форма карты: мир — тор, данж — плоская карта.</summary>
        public MapShape Shape { get; }

        /// <summary>Зерно мира (пишется в снимок генерации, 3.4).</summary>
        public ulong Seed { get; }

        /// <summary>Версия генератора (3.4).</summary>
        public int GeneratorVersion { get; }

        /// <summary>Создано чанков.</summary>
        public int ChunkCount => _chunks.Count;

        /// <summary>Всего чанков на карте (4096² → 128 × 128 = 16 384).</summary>
        public int TotalChunks => Shape.ChunksX * Shape.ChunksY;

        /// <summary>Байт плотных слоёв всей карты: 3072 · чанков (4096² → 48 МБ).</summary>
        public long FullLayerBytes => (long)TotalChunks * Chunk.LayerBytes;

        // ---------- Чанки ----------

        /// <summary>Чанк по координате (свёрнутой); нет — создаётся пустым.</summary>
        public Chunk GetOrCreateChunk(ChunkCoord coord)
        {
            ChunkCoord key = CheckedWrap(coord);
            if (!_chunks.TryGetValue(key, out Chunk? chunk))
            {
                chunk = new Chunk();
                _chunks.Add(key, chunk);
                _keysStale = true;
            }

            return chunk;
        }

        /// <summary>Чанк, если создан; иначе null.</summary>
        public Chunk? FindChunk(ChunkCoord coord) =>
            _chunks.TryGetValue(CheckedWrap(coord), out Chunk? chunk) ? chunk : null;

        /// <summary>Создать все чанки карты (генератор заполняет весь мир, 3.3: «вся поверхность в памяти»).</summary>
        public void CreateAllChunks()
        {
            for (int y = 0; y < Shape.ChunksY; y++)
            {
                for (int x = 0; x < Shape.ChunksX; x++)
                {
                    GetOrCreateChunk(new ChunkCoord(x, y));
                }
            }
        }

        /// <summary>Координаты созданных чанков, отсортированные (Y, затем X). Массив — внутренний, не менять.</summary>
        public ReadOnlySpan<ChunkCoord> GetChunkCoords()
        {
            if (_keysStale)
            {
                var keys = new ChunkCoord[_chunks.Count];
                _chunks.Keys.CopyTo(keys, 0);
                Array.Sort(keys, ChunkCoordOrder.Instance);
                _sortedKeys = keys;
                _keysStale = false;
            }

            return _sortedKeys;
        }

        /// <summary>Грязные чанки (менялись с последнего <see cref="Chunk.ClearDirty"/>): для сохранения и отправки.</summary>
        public ChunkCoord[] GetDirtyChunks() => Select(changedSinceBase: false);

        /// <summary>Чанки, изменённые после генерации: они идут в сохранение поверх снимка (3.4).</summary>
        public ChunkCoord[] GetChangedSinceBase() => Select(changedSinceBase: true);

        /// <summary>Сбросить «грязь» у всех чанков (сохранение собрано).</summary>
        public void ClearAllDirty()
        {
            ReadOnlySpan<ChunkCoord> keys = GetChunkCoords();
            for (int i = 0; i < keys.Length; i++)
            {
                _chunks[keys[i]].ClearDirty();
            }
        }

        // ---------- Тайлы ----------

        /// <summary>Пол тайла; несозданный чанк — пусто (0).</summary>
        public byte GetFloor(TileCoord tile)
        {
            TileCoord t = CheckedWrap(tile);
            Chunk? chunk = FindChunk(Torus.ChunkOf(Shape, t));
            return chunk == null ? FloorId.Empty : chunk.GetFloor(Chunk.IndexOf(t.X, t.Y));
        }

        /// <summary>Объект тайла (сырое значение <see cref="ObjectId"/>); несозданный чанк — пусто (0).</summary>
        public ushort GetObject(TileCoord tile)
        {
            TileCoord t = CheckedWrap(tile);
            Chunk? chunk = FindChunk(Torus.ChunkOf(Shape, t));
            return chunk == null ? ObjectId.Empty : chunk.GetObject(Chunk.IndexOf(t.X, t.Y));
        }

        /// <summary>Запись постройки тайла.</summary>
        public bool TryGetBuild(TileCoord tile, out BuildRecord record)
        {
            TileCoord t = CheckedWrap(tile);
            Chunk? chunk = FindChunk(Torus.ChunkOf(Shape, t));
            if (chunk == null)
            {
                record = default;
                return false;
            }

            return chunk.TryGetBuild(Chunk.IndexOf(t.X, t.Y), out record);
        }

        /// <summary>
        /// Тайл якоря: для «части» многотайлового объекта — тайл со смещением назад (по тору, через шов),
        /// для остального — сам тайл.
        /// </summary>
        public TileCoord AnchorOf(TileCoord tile)
        {
            ushort raw = GetObject(tile);
            if (!ObjectId.IsPart(raw))
            {
                return Torus.Wrap(Shape, tile);
            }

            return Torus.Wrap(Shape, new TileCoord(tile.X - ObjectId.PartOffsetX(raw), tile.Y - ObjectId.PartOffsetY(raw)));
        }

        /// <summary>Тайл генерации: без ревизии и «грязи» (заполнение мира, 3.4).</summary>
        public void SetBaseTile(TileCoord tile, byte floor, ushort obj)
        {
            TileCoord t = CheckedWrap(tile);
            GetOrCreateChunk(Torus.ChunkOf(Shape, t)).SetBaseTile(Chunk.IndexOf(t.X, t.Y), floor, obj);
        }

        // ---------- Правки (3.7) ----------

        /// <summary>
        /// Пачка правок одного чанка за такт — одна операция: ревизия +1 (<see cref="Chunk.ApplyEdits"/>).
        /// Возвращает новую ревизию чанка.
        /// </summary>
        public uint ApplyEdits(ChunkCoord chunk, ReadOnlySpan<TileEdit> edits, Span<EditResult> results) =>
            GetOrCreateChunk(chunk).ApplyEdits(edits, results);

        /// <summary>Клиент: пачка с сервера (false — разрыв ревизий, нужен чанк целиком).</summary>
        public bool ApplyPatch(in TilePatch patch) => GetOrCreateChunk(patch.Chunk).ApplyPatch(patch);

        // ---------- Внутреннее ----------

        private ChunkCoord[] Select(bool changedSinceBase)
        {
            ReadOnlySpan<ChunkCoord> keys = GetChunkCoords();
            int n = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                if (Matches(_chunks[keys[i]], changedSinceBase))
                {
                    n++;
                }
            }

            var result = new ChunkCoord[n];
            n = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                if (Matches(_chunks[keys[i]], changedSinceBase))
                {
                    result[n++] = keys[i];
                }
            }

            return result;
        }

        private static bool Matches(Chunk chunk, bool changedSinceBase) =>
            changedSinceBase ? chunk.ChangedSinceBase : chunk.IsDirty;

        // На плоской карте Wrap прижимает к краю — для данных мира это ошибка, а не «тайл у края».
        private ChunkCoord CheckedWrap(ChunkCoord c)
        {
            if (!Shape.Wraps && ((uint)c.X >= (uint)Shape.ChunksX || (uint)c.Y >= (uint)Shape.ChunksY))
            {
                throw new ArgumentOutOfRangeException(nameof(c), c, $"Чанк вне карты {Shape}.");
            }

            return Torus.Wrap(Shape, c);
        }

        private TileCoord CheckedWrap(TileCoord t)
        {
            if (!Shape.Wraps && ((uint)t.X >= (uint)Shape.Width || (uint)t.Y >= (uint)Shape.Height))
            {
                throw new ArgumentOutOfRangeException(nameof(t), t, $"Тайл вне карты {Shape}.");
            }

            return Torus.Wrap(Shape, t);
        }
    }
}
