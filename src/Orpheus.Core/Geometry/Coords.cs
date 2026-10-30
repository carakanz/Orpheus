using System;

namespace Orpheus.Core.Geometry
{
    // Координаты мира (docs/07, 3.2). Арифметики между позициями нет намеренно:
    // разность считает только Torus.Delta, сдвиг — только Torus.Offset / Torus.Step.

    /// <summary>
    /// Позиция существа в 1/256 тайла (как в Factorio): x ∈ [0, W·256) на заворачиваемой карте.
    /// </summary>
    public readonly struct WorldPos : IEquatable<WorldPos>
    {
        public readonly int X;
        public readonly int Y;

        public WorldPos(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(WorldPos other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is WorldPos other && Equals(other);
        public override int GetHashCode() => unchecked((X * 73856093) ^ (Y * 19349663));
        public static bool operator ==(WorldPos a, WorldPos b) => a.Equals(b);
        public static bool operator !=(WorldPos a, WorldPos b) => !a.Equals(b);
        public override string ToString() => $"({X}/256, {Y}/256)";
    }

    /// <summary>Целочисленные координаты тайла: (x, y) ∈ [0, W) × [0, H).</summary>
    public readonly struct TileCoord : IEquatable<TileCoord>
    {
        public readonly int X;
        public readonly int Y;

        public TileCoord(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(TileCoord other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is TileCoord other && Equals(other);
        public override int GetHashCode() => unchecked((X * 73856093) ^ (Y * 19349663));
        public static bool operator ==(TileCoord a, TileCoord b) => a.Equals(b);
        public static bool operator !=(TileCoord a, TileCoord b) => !a.Equals(b);
        public override string ToString() => $"tile({X}, {Y})";
    }

    /// <summary>Координаты чанка 32 × 32 тайла (docs/00, 5.1): чанк = тайл >> 5.</summary>
    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        public readonly int X;
        public readonly int Y;

        public ChunkCoord(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(ChunkCoord other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is ChunkCoord other && Equals(other);
        public override int GetHashCode() => unchecked((X * 73856093) ^ (Y * 19349663));
        public static bool operator ==(ChunkCoord a, ChunkCoord b) => a.Equals(b);
        public static bool operator !=(ChunkCoord a, ChunkCoord b) => !a.Equals(b);
        public override string ToString() => $"chunk({X}, {Y})";
    }

    /// <summary>
    /// Целочисленный вектор: дельта между позициями (в 1/256 тайла) или тайлами (в тайлах).
    /// </summary>
    public readonly struct Int2 : IEquatable<Int2>
    {
        public readonly int X;
        public readonly int Y;

        public Int2(int x, int y)
        {
            X = x;
            Y = y;
        }

        /// <summary>X² + Y² без переполнения.</summary>
        public long LengthSquared => (long)X * X + (long)Y * Y;

        public static Int2 operator +(Int2 a, Int2 b) => new Int2(a.X + b.X, a.Y + b.Y);
        public static Int2 operator -(Int2 a, Int2 b) => new Int2(a.X - b.X, a.Y - b.Y);
        public static Int2 operator -(Int2 a) => new Int2(-a.X, -a.Y);

        public bool Equals(Int2 other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is Int2 other && Equals(other);
        public override int GetHashCode() => unchecked((X * 73856093) ^ (Y * 19349663));
        public static bool operator ==(Int2 a, Int2 b) => a.Equals(b);
        public static bool operator !=(Int2 a, Int2 b) => !a.Equals(b);
        public override string ToString() => $"<{X}, {Y}>";
    }
}
