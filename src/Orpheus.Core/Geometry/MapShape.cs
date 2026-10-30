using System;

namespace Orpheus.Core.Geometry
{
    /// <summary>
    /// Описание карты для <see cref="Torus"/> (docs/07, 3.2): ширина и высота в тайлах и «заворачивать или нет».
    /// Мир — тор, стороны — степени двойки (docs/01, 1). Отдельные карты данжей — не тор:
    /// тот же код с <see cref="Wraps"/> = false, стороны любые.
    /// </summary>
    public readonly struct MapShape : IEquatable<MapShape>
    {
        /// <summary>Наибольшая сторона: W·256 должно помещаться в int32 с запасом (2^30).</summary>
        public const int MaxSize = 1 << 22;

        /// <summary>Ширина в тайлах.</summary>
        public readonly int Width;

        /// <summary>Высота в тайлах.</summary>
        public readonly int Height;

        /// <summary>true — тор (заворот по обеим осям), false — плоская карта с краями.</summary>
        public readonly bool Wraps;

        /// <summary>Ширина в 1/256 тайла: W·256.</summary>
        public readonly int WidthUnits;

        /// <summary>Высота в 1/256 тайла: H·256.</summary>
        public readonly int HeightUnits;

        /// <summary>Чанков по X (неполный чанк у края плоской карты считается).</summary>
        public readonly int ChunksX;

        /// <summary>Чанков по Y.</summary>
        public readonly int ChunksY;

        public MapShape(int width, int height, bool wraps)
        {
            Validate(width, nameof(width), wraps);
            Validate(height, nameof(height), wraps);
            Width = width;
            Height = height;
            Wraps = wraps;
            WidthUnits = width << Torus.SubTileBits;
            HeightUnits = height << Torus.SubTileBits;
            ChunksX = (width + Torus.ChunkSize - 1) >> Torus.ChunkBits;
            ChunksY = (height + Torus.ChunkSize - 1) >> Torus.ChunkBits;
        }

        /// <summary>Квадратный мир-тор W × W (W — степень двойки, ≥ 32).</summary>
        public static MapShape World(int size) => new MapShape(size, size, true);

        /// <summary>Плоская карта без заворота (данж).</summary>
        public static MapShape Flat(int width, int height) => new MapShape(width, height, false);

        private static void Validate(int size, string name, bool wraps)
        {
            if (size <= 0 || size > MaxSize)
            {
                throw new ArgumentOutOfRangeException(name, size, $"Сторона карты — от 1 до {MaxSize} тайлов.");
            }

            if (wraps && ((size & (size - 1)) != 0 || size < Torus.ChunkSize))
            {
                throw new ArgumentOutOfRangeException(name, size, "Сторона тора — степень двойки не меньше 32.");
            }
        }

        public bool Equals(MapShape other) => Width == other.Width && Height == other.Height && Wraps == other.Wraps;
        public override bool Equals(object? obj) => obj is MapShape other && Equals(other);
        public override int GetHashCode() => unchecked((Width * 73856093) ^ (Height * 19349663) ^ (Wraps ? 1 : 0));
        public static bool operator ==(MapShape a, MapShape b) => a.Equals(b);
        public static bool operator !=(MapShape a, MapShape b) => !a.Equals(b);
        public override string ToString() => $"{Width}×{Height}{(Wraps ? " тор" : " плоская")}";
    }
}
