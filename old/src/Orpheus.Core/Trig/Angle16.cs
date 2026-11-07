using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.Trig
{
    /// <summary>
    /// Угол как u16: 65 536 шагов на полный круг (docs/07, 11.5: «угол — u16»).
    /// 0 = восток (+X), 16 384 = юг (+Y, ось Y смотрит вниз), 32 768 = запад, 49 152 = север.
    /// Сложение и вычитание — по модулю 65 536, переполнение u16 и есть нужный оборот.
    /// Синус и косинус — <see cref="TrigTable"/>, atan2 — целочисленный CORDIC в <see cref="TrigTable.Atan2"/>.
    /// </summary>
    public readonly struct Angle16 : IEquatable<Angle16>
    {
        /// <summary>Шагов на полный круг.</summary>
        public const int Steps = 1 << 16;

        /// <summary>Шагов на четверть круга (90°).</summary>
        public const int Quarter = 1 << 14;

        /// <summary>Шагов на половину круга (180°).</summary>
        public const int Half = 1 << 15;

        /// <summary>Сырое значение 0…65 535.</summary>
        public readonly ushort Raw;

        public Angle16(ushort raw)
        {
            Raw = raw;
        }

        /// <summary>Взять значение по модулю 65 536 (из int32, в том числе отрицательного).</summary>
        public static Angle16 FromRaw(int raw) => new Angle16((ushort)(raw & 0xFFFF));

        /// <summary>Прямая (вектор) → угол: целочисленный atan2 (docs/07, 11.5).</summary>
        public static Angle16 Atan2(int y, int x) => TrigTable.Atan2(y, x);

        /// <summary>Угол направления шага (dx, dy) в тайлах.</summary>
        public static Angle16 FromDirection(Int2 d) => TrigTable.Atan2(d.Y, d.X);

        /// <summary>Синус, фиксированная точка Q15 (32 768 = 1,0).</summary>
        public int Sin => TrigTable.Sin(this);

        /// <summary>Косинус, Q15.</summary>
        public int Cos => TrigTable.Cos(this);

        /// <summary>amplitude · sin(угол), округление к ближайшему (половина — к чётному), целое.</summary>
        public int SinBy(int amplitude) => TrigTable.SinTimes(this, amplitude);

        /// <summary>amplitude · cos(угол), округление к ближайшему (половина — к чётному), целое.</summary>
        public int CosBy(int amplitude) => TrigTable.CosTimes(this, amplitude);

        /// <summary>
        /// Вектор длиной <paramref name="amplitude"/> в этом направлении: (cos, sin) · amplitude,
        /// целые единицы (для позиции — 1/256 тайла).
        /// </summary>
        public Int2 Direction(int amplitude) => TrigTable.Direction(this, amplitude);

        /// <summary>Четверть круга: 0 = восток, 1 = юг, 2 = запад, 3 = север.</summary>
        public int QuarterTurn => Raw >> 14;

        public static Angle16 operator +(Angle16 a, Angle16 b) => FromRaw(a.Raw + b.Raw);
        public static Angle16 operator -(Angle16 a, Angle16 b) => FromRaw(a.Raw - b.Raw);
        public static Angle16 operator -(Angle16 a) => FromRaw(-a.Raw);

        public bool Equals(Angle16 other) => Raw == other.Raw;
        public override bool Equals(object? obj) => obj is Angle16 other && Equals(other);
        public override int GetHashCode() => Raw;
        public static bool operator ==(Angle16 a, Angle16 b) => a.Raw == b.Raw;
        public static bool operator !=(Angle16 a, Angle16 b) => a.Raw != b.Raw;
        public override string ToString() => $"∠{Raw} ({QuarterTurn * 90}°)";
    }
}
