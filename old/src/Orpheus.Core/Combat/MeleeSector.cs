using Orpheus.Core.Geometry;
using Orpheus.Core.Trig;

namespace Orpheus.Core.Combat
{
    /// <summary>
    /// Сектор удара ближнего боя (docs/02, 3.1): центр — центр атакующего, радиус — «досягаемость», угол — «дуга», ось — по
    /// прицелу. Существо поражено, если его коллизия (круг) пересекает сектор; касание — не пересечение (как у
    /// <see cref="Movement.Collision"/>). Линия удара (стены между) — не здесь: в шипе 0-029 только размер сектора.
    /// <para>
    /// Целочисленно: расстояния — квадраты в long, угол — <see cref="Angle16"/> и целый atan2, края сектора — отрезки
    /// <see cref="TrigTable.Direction"/>; разности — <see cref="Torus.Delta(in MapShape, WorldPos, WorldPos)"/> (шов тора).
    /// </para>
    /// </summary>
    public readonly struct MeleeSector
    {
        /// <summary>Центр сектора — центр атакующего, 1/256 тайла.</summary>
        public readonly WorldPos Origin;

        /// <summary>Ось сектора (прицел атакующего).</summary>
        public readonly Angle16 Axis;

        /// <summary>Досягаемость, unit (1/256 тайла).</summary>
        public readonly int ReachUnits;

        /// <summary>Половина дуги в шагах угла (дуга 90° — 8192).</summary>
        public readonly int HalfArcSteps;

        public MeleeSector(WorldPos origin, Angle16 axis, int reachUnits, int halfArcSteps)
        {
            Origin = origin;
            Axis = axis;
            ReachUnits = reachUnits;
            HalfArcSteps = halfArcSteps;
        }

        /// <summary>Пересекает ли круг с центром <paramref name="center"/> и радиусом <paramref name="radiusUnits"/> сектор.</summary>
        public bool Overlaps(in MapShape map, WorldPos center, int radiusUnits)
        {
            Int2 d = Torus.Delta(map, Origin, center);
            long far = (long)ReachUnits + radiusUnits;
            if (d.LengthSquared >= far * far)
            {
                return false; // дальше досягаемости + радиуса
            }

            if (d.X == 0 && d.Y == 0)
            {
                return true;
            }

            // Направление на центр в пределах дуги: ближайшая точка сектора — на этом луче, расстояние |d| − R < r.
            int diff = (short)(Angle16.Atan2(d.Y, d.X).Raw - Axis.Raw);
            if ((diff < 0 ? -diff : diff) <= HalfArcSteps)
            {
                return true;
            }

            // Вне дуги: круг задевает один из краёв сектора (отрезок от центра длиной досягаемости).
            long r2 = (long)radiusUnits * radiusUnits;
            return NearSegment(d, Angle16.FromRaw(Axis.Raw + HalfArcSteps).Direction(ReachUnits), r2)
                || NearSegment(d, Angle16.FromRaw(Axis.Raw - HalfArcSteps).Direction(ReachUnits), r2);
        }

        // Квадрат расстояния от точки p до отрезка [0, e] меньше r2 (без деления: поперечное — через векторное произведение).
        private static bool NearSegment(Int2 p, Int2 e, long r2)
        {
            long ee = e.LengthSquared;
            long dot = (long)p.X * e.X + (long)p.Y * e.Y;
            if (dot <= 0 || ee == 0)
            {
                return p.LengthSquared < r2;
            }

            if (dot >= ee)
            {
                return (p - e).LengthSquared < r2;
            }

            long cross = (long)p.X * e.Y - (long)p.Y * e.X;
            return cross * cross < r2 * ee;
        }

        public override string ToString() => $"сектор {Origin} ось {Axis.Raw} R {ReachUnits} ±{HalfArcSteps}";
    }
}
