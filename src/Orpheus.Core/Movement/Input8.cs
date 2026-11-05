using System;
using Orpheus.Core.Trig;

namespace Orpheus.Core.Movement
{
    /// <summary>
    /// Вектор ввода движения в 8 направлений (docs/00, 5.1: «направлений движения — 8, с клавиш,
    /// свободное со стика»). Направление — целые −1 / 0 / +1 по осям; величины скорости здесь нет,
    /// она в <see cref="Mover.UnitsPerTickX10"/> (docs/07, 4.2: кадр ввода 60 Гц, такт 30 Гц —
    /// клиент опрашивает ввод и складывает его в одно направление такта).
    /// <para>
    /// Скорость на одну ось в unit × 10 за такт (work/refs/movement.md): прямая = базовой скорости
    /// (<see cref="MovementTick.BaseSpeedX10"/> = 512), диагональ = 512 ÷ √2 = 362,04 → 362
    /// (<see cref="DiagonalSpeedX10"/>), застой = 0. Целые числа, float-накопления нет.
    /// </para>
    /// </summary>
    public readonly struct Input8 : IEquatable<Input8>
    {
        /// <summary>Направлений всего.</summary>
        public const int Count = 8;

        /// <summary>Ширина сектора стика в шагах угла <see cref="Angle16"/>: 45° = 65 536 / 8 = 8192.</summary>
        public const int SectorSteps = Angle16.Steps / Count;

        /// <summary>Стоит на месте.</summary>
        public static readonly Input8 None = default;

        /// <summary>Восток (+X).</summary>
        public static readonly Input8 East = new Input8(1, 0);

        /// <summary>Юго-восток (+X, +Y; ось Y смотрит вниз).</summary>
        public static readonly Input8 SouthEast = new Input8(1, 1);

        /// <summary>Юг (+Y).</summary>
        public static readonly Input8 South = new Input8(0, 1);

        /// <summary>Юго-запад (−X, +Y).</summary>
        public static readonly Input8 SouthWest = new Input8(-1, 1);

        /// <summary>Запад (−X).</summary>
        public static readonly Input8 West = new Input8(-1, 0);

        /// <summary>Северо-запад (−X, −Y).</summary>
        public static readonly Input8 NorthWest = new Input8(-1, -1);

        /// <summary>Север (−Y).</summary>
        public static readonly Input8 North = new Input8(0, -1);

        /// <summary>Северо-восток (+X, −Y).</summary>
        public static readonly Input8 NorthEast = new Input8(1, -1);

        /// <summary>Восемь направлений по номеру сектора: 0 = восток, дальше по часовой (экран: Y вниз).</summary>
        public static readonly Input8[] Directions =
        {
            East, SouthEast, South, SouthWest, West, NorthWest, North, NorthEast,
        };

        /// <summary>−1, 0 или +1 по X.</summary>
        public readonly int DX;

        /// <summary>−1, 0 или +1 по Y (ось Y смотрит вниз).</summary>
        public readonly int DY;

        public Input8(int dx, int dy)
        {
            DX = Sign(dx);
            DY = Sign(dy);
        }

        /// <summary>Есть движение.</summary>
        public bool IsMoving => DX != 0 || DY != 0;

        /// <summary>Диагональ (заняты обе оси).</summary>
        public bool IsDiagonal => DX != 0 && DY != 0;

        /// <summary>Номер направления 0…7 (0 = восток, 2 = юг, 4 = запад, 6 = север).</summary>
        public int DirectionIndex
        {
            get
            {
                if (DX > 0)
                {
                    return DY > 0 ? 1 : DY < 0 ? 7 : 0;
                }

                if (DX < 0)
                {
                    return DY > 0 ? 3 : DY < 0 ? 5 : 4;
                }

                return DY > 0 ? 2 : 6;
            }
        }

        /// <summary>Направление как угол: 0 = восток, 16 384 = юг (docs/07, 11.5: угол — u16).</summary>
        public Angle16 ToAngle() => Angle16.FromRaw(DirectionIndex * SectorSteps);

        /// <summary>
        /// Клавиши: 8 комбинаций стрелок/WASD (docs/00, 5.1). Знаки складываются (диагональ — две клавиши),
        /// величина не важна: (3, 0) и (1, 0) — это «восток».
        /// </summary>
        public static Input8 FromKeys(int horizontal, int vertical) => new Input8(horizontal, vertical);

        /// <summary>Клавиши по нажатию: стрелки/WASD, 8 комбинаций; ничего не нажато — <see cref="None"/>.</summary>
        public static Input8 FromKeys(bool left, bool right, bool up, bool down) =>
            new Input8((right ? 1 : 0) - (left ? 1 : 0), (down ? 1 : 0) - (up ? 1 : 0));

        /// <summary>
        /// Свободный угол со стика → ближайший из 8 направлений (docs/00, 5.1): сектор по 45°,
        /// граница сектора — пополам (4096 шагов угла от центра соседнего сектора).
        /// </summary>
        public static Input8 FromAngle(Angle16 angle) =>
            Directions[((angle.Raw + SectorSteps / 2) / SectorSteps) & (Count - 1)];

        /// <summary>Свободный вектор со стика (направление важнее длины) → ближайший из 8; (0, 0) — стойка.</summary>
        public static Input8 FromStick(int x, int y) =>
            x == 0 && y == 0 ? None : FromAngle(Angle16.Atan2(y, x));

        /// <summary>Направление по номеру сектора 0…7 (см. <see cref="Directions"/>); вне диапазона — <see cref="None"/>.</summary>
        public static Input8 FromIndex(int index) =>
            index >= 0 && index < Count ? Directions[index] : None;

        /// <summary>Скорость по X за такт в unit × 10: 0, ±<paramref name="baseX10"/> или ±<see cref="DiagonalSpeedX10"/>.</summary>
        public int SpeedX10(int baseX10) => DX == 0 ? 0 : DX > 0 ? AxisSpeedX10(baseX10) : -AxisSpeedX10(baseX10);

        /// <summary>Скорость по Y за такт в unit × 10 (см. <see cref="SpeedX10"/>).</summary>
        public int SpeedY10(int baseX10) => DY == 0 ? 0 : DY > 0 ? AxisSpeedX10(baseX10) : -AxisSpeedX10(baseX10);

        /// <summary>Скорость на одну ось: прямая — базовая, диагональ — 362 из 512 (work/refs/movement.md).</summary>
        public int AxisSpeedX10(int baseX10) => IsDiagonal ? DiagonalSpeedX10(baseX10) : baseX10;

        /// <summary>
        /// Диагональ из базовой скорости целыми числами: base × 362 / 512 с округлением к ближайшему
        /// (512 → 362; 512 ÷ √2 = 362,04). base ≤ 5 936 000, переполнения int32 нет.
        /// </summary>
        public static int DiagonalSpeedX10(int baseX10) => (baseX10 * MovementTick.DiagonalPerAxisX10 + 256) / 512;

        private static int Sign(int v) => v > 0 ? 1 : v < 0 ? -1 : 0;

        public bool Equals(Input8 other) => DX == other.DX && DY == other.DY;
        public override bool Equals(object? obj) => obj is Input8 other && Equals(other);
        public override int GetHashCode() => unchecked((DX * 73856093) ^ (DY * 19349663));
        public static bool operator ==(Input8 a, Input8 b) => a.Equals(b);
        public static bool operator !=(Input8 a, Input8 b) => !a.Equals(b);
        public override string ToString() => $"input({DX}, {DY})";
    }
}
