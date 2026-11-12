using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Свет-сетка источников (карточка 1-014, <c>01</c> 6 «Источники света»): сумма вклада факелов,
    /// костров (и очага, когда он придёт с генератором) по тайлам, целая в 1/1024, без плавающей точки.
    /// <para>
    /// Сетка <see cref="ushort"/>[32 × 32] на чанк; пересчёт чанка — при изменении ревизии любого чанка
    /// окна 3 × 3 (радиус источника 8 &lt; 32 — дальше окна вклад в чанк не долетает). Окно меньше чанка
    /// по ширине, поэтому шов тора перекрывается окном целиком.
    /// </para>
    /// <para>
    /// Вклад источника: полный (1024) при d ≤ r0, далее линейно 1024 × (r − d) / (r − r0), за r — 0;
    /// d — кратчайшее расстояние по тору (<see cref="Torus"/>), целое (√d² округлено вниз).
    /// «Построенный свет» (запрет появления, 1-009) — <see cref="SourceLightX1024"/>, без фона.
    /// </para>
    /// </summary>
    public sealed class LightIndex
    {
        /// <summary>Ячейка — факел: полный свет 3 тайла, затухает до 8-го.</summary>
        public const int TorchInner = 3;
        public const int TorchOuter = 6;

        /// <summary>Костёр и очаг (<c>01</c> 6 «Костёр 4 / 8»): полный свет 4 тайла, затухает до 8-го.</summary>
        public const int CampfireInner = 4;
        public const int CampfireOuter = 8;

        /// <summary>Наибольший радиус источника — задаёт окно пересчёта (3 × 3 чанка).</summary>
        public const int MaxRadius = CampfireOuter;

        private readonly WorldModel _world;
        private readonly MapShape _shape;

        // Сетка на чанк: пересобирается на месте (массив не выделяется повторно).
        private sealed class Entry
        {
            public long Stamp;
            public readonly ushort[] Grid = new ushort[Torus.ChunkSize * Torus.ChunkSize];
        }

        private readonly Dictionary<ChunkCoord, Entry> _entries = new Dictionary<ChunkCoord, Entry>();

        public LightIndex(WorldModel world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            _world = world;
            _shape = world.Shape;
        }

        /// <summary>Сколько раз сетка чанка пересобрана (тест бюджета: правка одного тайла — не больше 9).</summary>
        public long GridRebuilds { get; private set; }

        /// <summary>Свет источников в тайле, 1/1024 (без фона; «построенный свет» — порог 512 для запрета появления, <c>01</c> 6).</summary>
        public int SourceLightX1024(TileCoord tile)
        {
            TileCoord w = Torus.Wrap(_shape, tile);
            ushort[] grid = GridOf(Torus.ChunkOf(_shape, w));
            return grid[(w.Y & (Torus.ChunkSize - 1)) * Torus.ChunkSize + (w.X & (Torus.ChunkSize - 1))];
        }

        /// <summary>Освещённость тайла (<c>01</c> 6): L = clamp(фон × k_биома + свет источников, 0, 1024), всё в 1/1024.</summary>
        public int LightX1024(TileCoord tile, int bgX1024, int biomeKX1024) =>
            DayClock.TileLightX1024(bgX1024, biomeKX1024, SourceLightX1024(tile));

        // ---------- Пересчёт ----------

        private ushort[] GridOf(ChunkCoord center)
        {
            long stamp = StampOf(center);
            if (_entries.TryGetValue(center, out Entry? entry) && entry.Stamp == stamp)
            {
                return entry.Grid;
            }

            if (entry == null)
            {
                entry = new Entry();
                _entries[center] = entry;
            }

            entry.Stamp = stamp;
            Rebuild(center, entry.Grid);
            GridRebuilds++;
            return entry.Grid;
        }

        /// <summary>Штамп окна 3 × 3: ревизии чанков (несозданный — 0). Ревизия растёт на каждой правке тайла.</summary>
        private long StampOf(ChunkCoord center)
        {
            Span<ChunkCoord> neigh = stackalloc ChunkCoord[Torus.MaxNeighborChunks(1, 1)];
            int n = Torus.NeighborChunks(_shape, center, 1, 1, neigh);
            long h = -7012172313846466560L; // FNV-1a offset64
            for (int i = 0; i < n; i++)
            {
                Chunk? c = _world.FindChunk(neigh[i]);
                h = (h ^ (c?.Revision ?? 0) * 1000003 + neigh[i].X) * 1099511628211L;
                h ^= neigh[i].Y * 1099511628211L;
            }

            return h;
        }

        private void Rebuild(ChunkCoord center, ushort[] grid)
        {
            Array.Clear(grid, 0, grid.Length);

            Span<ChunkCoord> neigh = stackalloc ChunkCoord[Torus.MaxNeighborChunks(1, 1)];
            int n = Torus.NeighborChunks(_shape, center, 1, 1, neigh);
            for (int i = 0; i < n; i++)
            {
                Chunk? c = _world.FindChunk(neigh[i]);
                if (c == null)
                {
                    continue;
                }

                TileCoord corner = Torus.CornerOf(neigh[i]);
                for (int idx = 0; idx < grid.Length; idx++)
                {
                    ushort id = ObjectId.IdOf(c.GetObject(idx));
                    int r0 = InnerRadius(id);
                    if (r0 == 0)
                    {
                        continue;
                    }

                    StampSource(center, grid, corner.X + (idx & (Torus.ChunkSize - 1)), corner.Y + (idx >> Torus.ChunkBits), r0, OuterRadius(id));
                }
            }
        }

        /// <summary>Разложить вклад источника по тайлам окна (± r) и сложить в сетку центра.</summary>
        private void StampSource(ChunkCoord center, ushort[] grid, int sx, int sy, int r0, int r)
        {
            int r2 = r * r;
            int inner2 = r0 * r0;
            for (int dy = -r; dy <= r; dy++)
            {
                int ty = sy + dy;
                if (!_shape.Wraps && (ty < 0 || ty >= _shape.Height))
                {
                    continue; // плоская карта (данж): за краем тайлов нет — не дублируем край
                }

                for (int dx = -r; dx <= r; dx++)
                {
                    int d2 = dx * dx + dy * dy;
                    if (d2 > r2)
                    {
                        continue;
                    }

                    int tx = sx + dx;
                    if (!_shape.Wraps && (tx < 0 || tx >= _shape.Width))
                    {
                        continue;
                    }

                    int d = ISqrt(d2);
                    int v = d2 <= inner2 ? DayClock.LightScale : DayClock.LightScale * (r - d) / (r - r0);

                    TileCoord t = Torus.Wrap(_shape, new TileCoord(tx, ty));
                    if (!Torus.ChunkOf(_shape, t).Equals(center))
                    {
                        continue;
                    }

                    ref ushort cell = ref grid[(t.Y & (Torus.ChunkSize - 1)) * Torus.ChunkSize + (t.X & (Torus.ChunkSize - 1))];
                    int sum = cell + v;
                    cell = (ushort)(sum > ushort.MaxValue ? ushort.MaxValue : sum);
                }
            }
        }

        /// <summary>Радиус полного света источника; 0 — не источник. Очаг: тот же 4 / 8, id придёт с генератором.</summary>
        private static int InnerRadius(ushort id) =>
            id == ObjectId.Torch ? TorchInner : id == ObjectId.Campfire ? CampfireInner : 0;

        /// <summary>Радиус затухания источника (0 — не источник).</summary>
        private static int OuterRadius(ushort id) =>
            id == ObjectId.Torch ? TorchOuter : id == ObjectId.Campfire ? CampfireOuter : 0;

        private static int ISqrt(int v)
        {
            int r = 0;
            while ((r + 1) * (r + 1) <= v)
            {
                r++;
            }

            return r;
        }
    }
}
