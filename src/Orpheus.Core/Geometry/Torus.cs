using System;

namespace Orpheus.Core.Geometry
{
    /// <summary>
    /// Общий код тора (docs/07, 3.2; docs/01, 1). Любой код, который вычитает координаты,
    /// обязан делать это через <see cref="Delta(in MapShape, WorldPos, WorldPos)"/>.
    /// <para>
    /// Тор (<see cref="MapShape.Wraps"/> = true): заворот — битовая маска x &amp; (W·256 − 1);
    /// тайл — (x &gt;&gt; 8) &amp; (W − 1); чанк — тайл &gt;&gt; 5;
    /// Δ = ((b − a + W·128) &amp; (W·256 − 1)) − W·128 ∈ [−W·128, W·128). Ровно половина мира
    /// даёт −W·128 (как X ∈ [−R, R) в docs/01).
    /// </para>
    /// <para>
    /// Плоская карта (данж): заворота нет. <c>Wrap</c> прижимает координату к краю карты,
    /// <c>Delta</c> — обычная разность b − a, <c>NeighborChunks</c> пропускает чанки за краем.
    /// </para>
    /// Без выделений памяти: все методы работают со структурами и <see cref="Span{T}"/>.
    /// </summary>
    public static class Torus
    {
        /// <summary>Позиция хранится в 1/256 тайла.</summary>
        public const int SubTileBits = 8;

        /// <summary>256 единиц позиции на тайл.</summary>
        public const int SubTilesPerTile = 1 << SubTileBits;

        /// <summary>Чанк — 32 × 32 тайла (docs/00, 5.1).</summary>
        public const int ChunkBits = 5;

        /// <summary>Сторона чанка в тайлах.</summary>
        public const int ChunkSize = 1 << ChunkBits;

        // ---------- Заворот ----------

        /// <summary>Сворачивает X позиции (1/256 тайла) в [0, W·256).</summary>
        public static int WrapX(in MapShape map, int x) => WrapAxis(x, map.WidthUnits, map.Wraps);

        /// <summary>Сворачивает Y позиции (1/256 тайла) в [0, H·256).</summary>
        public static int WrapY(in MapShape map, int y) => WrapAxis(y, map.HeightUnits, map.Wraps);

        /// <summary>Сворачивает позицию существа в пределы карты.</summary>
        public static WorldPos Wrap(in MapShape map, WorldPos p) =>
            new WorldPos(WrapAxis(p.X, map.WidthUnits, map.Wraps), WrapAxis(p.Y, map.HeightUnits, map.Wraps));

        /// <summary>Сворачивает координаты тайла в [0, W) × [0, H).</summary>
        public static TileCoord Wrap(in MapShape map, TileCoord t) =>
            new TileCoord(WrapAxis(t.X, map.Width, map.Wraps), WrapAxis(t.Y, map.Height, map.Wraps));

        /// <summary>Сворачивает координаты чанка в [0, ChunksX) × [0, ChunksY).</summary>
        public static ChunkCoord Wrap(in MapShape map, ChunkCoord c) =>
            new ChunkCoord(WrapAxis(c.X, map.ChunksX, map.Wraps), WrapAxis(c.Y, map.ChunksY, map.Wraps));

        // ---------- Дельта и расстояние ----------

        /// <summary>Кратчайшая дельта от a до b по X, в 1/256 тайла.</summary>
        public static int DeltaX(in MapShape map, int a, int b) => DeltaAxis(a, b, map.WidthUnits, map.Wraps);

        /// <summary>Кратчайшая дельта от a до b по Y, в 1/256 тайла.</summary>
        public static int DeltaY(in MapShape map, int a, int b) => DeltaAxis(a, b, map.HeightUnits, map.Wraps);

        /// <summary>Кратчайший вектор от a до b, в 1/256 тайла.</summary>
        public static Int2 Delta(in MapShape map, WorldPos a, WorldPos b) =>
            new Int2(DeltaAxis(a.X, b.X, map.WidthUnits, map.Wraps), DeltaAxis(a.Y, b.Y, map.HeightUnits, map.Wraps));

        /// <summary>Кратчайший вектор от тайла a до тайла b, в тайлах.</summary>
        public static Int2 Delta(in MapShape map, TileCoord a, TileCoord b) =>
            new Int2(DeltaAxis(a.X, b.X, map.Width, map.Wraps), DeltaAxis(a.Y, b.Y, map.Height, map.Wraps));

        /// <summary>Кратчайший вектор от чанка a до чанка b, в чанках.</summary>
        public static Int2 Delta(in MapShape map, ChunkCoord a, ChunkCoord b) =>
            new Int2(DeltaAxis(a.X, b.X, map.ChunksX, map.Wraps), DeltaAxis(a.Y, b.Y, map.ChunksY, map.Wraps));

        /// <summary>Квадрат кратчайшего расстояния, в (1/256 тайла)².</summary>
        public static long Dist2(in MapShape map, WorldPos a, WorldPos b) => Delta(map, a, b).LengthSquared;

        /// <summary>Квадрат кратчайшего расстояния между тайлами, в тайлах².</summary>
        public static long Dist2(in MapShape map, TileCoord a, TileCoord b) => Delta(map, a, b).LengthSquared;

        // ---------- Сдвиг ----------

        /// <summary>Сдвигает позицию на целый вектор (1/256 тайла) и сворачивает.</summary>
        public static WorldPos Offset(in MapShape map, WorldPos p, Int2 d) =>
            Wrap(map, new WorldPos(p.X + d.X, p.Y + d.Y));

        /// <summary>
        /// Шаг на (dx, dy) тайлов, заданных float (скорость × время): итог округляется в 1/256 тайла
        /// (к ближайшему, половина — к чётному) и сворачивается (docs/07, 3.2).
        /// </summary>
        public static WorldPos Step(in MapShape map, WorldPos p, float dxTiles, float dyTiles) =>
            Offset(map, p, new Int2(ToUnits(dxTiles), ToUnits(dyTiles)));

        /// <summary>Тайлы (float) → 1/256 тайла с округлением к ближайшему.</summary>
        public static int ToUnits(float tiles) => (int)MathF.Round(tiles * SubTilesPerTile);

        // ---------- Тайлы и чанки ----------

        /// <summary>Тайл, в котором стоит позиция: (x &gt;&gt; 8) &amp; (W − 1).</summary>
        public static TileCoord TileOf(in MapShape map, WorldPos p) =>
            new TileCoord(
                WrapAxis(p.X >> SubTileBits, map.Width, map.Wraps),
                WrapAxis(p.Y >> SubTileBits, map.Height, map.Wraps));

        /// <summary>Чанк тайла: тайл &gt;&gt; 5 (тайл сначала сворачивается).</summary>
        public static ChunkCoord ChunkOf(in MapShape map, TileCoord t)
        {
            TileCoord w = Wrap(map, t);
            return new ChunkCoord(w.X >> ChunkBits, w.Y >> ChunkBits);
        }

        /// <summary>Чанк, в котором стоит позиция.</summary>
        public static ChunkCoord ChunkOf(in MapShape map, WorldPos p) => ChunkOf(map, TileOf(map, p));

        /// <summary>Левый верхний угол тайла как позиция существа.</summary>
        public static WorldPos CornerOf(TileCoord t) => new WorldPos(t.X << SubTileBits, t.Y << SubTileBits);

        /// <summary>Левый верхний тайл чанка.</summary>
        public static TileCoord CornerOf(ChunkCoord c) => new TileCoord(c.X << ChunkBits, c.Y << ChunkBits);

        /// <summary>Наибольшее число чанков, которое вернёт <see cref="NeighborChunks(in MapShape, ChunkCoord, int, int, Span{ChunkCoord})"/>.</summary>
        public static int MaxNeighborChunks(int radiusX, int radiusY) => (2 * radiusX + 1) * (2 * radiusY + 1);

        /// <summary>Чанки квадрата (2r + 1) × (2r + 1) вокруг центра, центр включён.</summary>
        public static int NeighborChunks(in MapShape map, ChunkCoord center, int radius, Span<ChunkCoord> result) =>
            NeighborChunks(map, center, radius, radius, result);

        /// <summary>
        /// Чанки прямоугольника (2·rx + 1) × (2·ry + 1) вокруг центра, центр включён; пишет в <paramref name="result"/>
        /// и возвращает их число. Порядок — по строкам сверху вниз, в строке слева направо, от (центр − r).
        /// На торе координаты свёрнуты; если окно шире карты, каждый чанк выдаётся один раз.
        /// На плоской карте чанки за краем пропускаются.
        /// </summary>
        public static int NeighborChunks(in MapShape map, ChunkCoord center, int radiusX, int radiusY, Span<ChunkCoord> result)
        {
            if (radiusX < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(radiusX), radiusX, "Радиус не может быть отрицательным.");
            }

            if (radiusY < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(radiusY), radiusY, "Радиус не может быть отрицательным.");
            }

            AxisRange(center.X, radiusX, map.ChunksX, map.Wraps, out int startX, out int countX);
            AxisRange(center.Y, radiusY, map.ChunksY, map.Wraps, out int startY, out int countY);
            int total = countX * countY;
            if (result.Length < total)
            {
                throw new ArgumentException($"Нужно место на {total} чанков, есть {result.Length}.", nameof(result));
            }

            int n = 0;
            for (int j = 0; j < countY; j++)
            {
                int y = WrapAxis(startY + j, map.ChunksY, map.Wraps);
                for (int i = 0; i < countX; i++)
                {
                    result[n++] = new ChunkCoord(WrapAxis(startX + i, map.ChunksX, map.Wraps), y);
                }
            }

            return n;
        }

        // ---------- Внутреннее ----------

        // size — степень двойки на торе; на плоской карте — любое положительное.
        private static int WrapAxis(int v, int size, bool wraps)
        {
            if (wraps)
            {
                return v & (size - 1);
            }

            if (v < 0)
            {
                return 0;
            }

            return v >= size ? size - 1 : v;
        }

        private static int DeltaAxis(int a, int b, int size, bool wraps)
        {
            if (!wraps)
            {
                return b - a;
            }

            int half = size >> 1;
            return ((b - a + half) & (size - 1)) - half;
        }

        private static void AxisRange(int center, int radius, int size, bool wraps, out int start, out int count)
        {
            long span = 2L * radius + 1;
            if (wraps)
            {
                start = center - radius;
                count = span >= size ? size : (int)span;
                return;
            }

            long lo = Math.Max(0L, (long)center - radius);
            long hi = Math.Min(size - 1L, (long)center + radius);
            start = (int)lo;
            count = hi >= lo ? (int)(hi - lo + 1) : 0;
        }
    }
}
