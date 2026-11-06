using System;
using Orpheus.Core.Movement;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Кадр ввода сущности на такт (docs/07, 4.2 и 11.5): направление из 8 (или стойка) + кнопки.
    /// Это всё, что пишется в журнал ввода: по журналу и зерну такт повторяется побитово.
    /// <para>
    /// В журнале — 3 байта: направление (0 — стойка, 1…8 — <see cref="Input8.DirectionIndex"/> + 1)
    /// и кнопки (u16, младший байт первым). Значение битов кнопок задают боевые и строительные задачи
    /// (`02`, `05`); ядро такта их пока только переносит и хэширует.
    /// </para>
    /// </summary>
    public readonly struct InputFrame : IEquatable<InputFrame>
    {
        /// <summary>Байт кадра в журнале: направление 1 + кнопки 2.</summary>
        public const int WireBytes = 3;

        /// <summary>Наибольший код направления: 8 (северо-восток).</summary>
        public const byte MaxDirectionCode = Input8.Count;

        /// <summary>Ничего не нажато.</summary>
        public static readonly InputFrame None = default;

        /// <summary>Направление движения (8 направлений или стойка).</summary>
        public readonly Input8 Direction;

        /// <summary>Кнопки — битовая маска.</summary>
        public readonly ushort Buttons;

        public InputFrame(Input8 direction, ushort buttons = 0)
        {
            Direction = direction;
            Buttons = buttons;
        }

        /// <summary>Код направления: 0 — стойка, 1…8 — номер направления + 1.</summary>
        public byte DirectionCode => Direction.IsMoving ? (byte)(Direction.DirectionIndex + 1) : (byte)0;

        /// <summary>Кадр по коду направления и кнопкам; код вне 0…8 — исключение (журнал испорчен).</summary>
        public static InputFrame FromCode(byte directionCode, ushort buttons)
        {
            if (directionCode > MaxDirectionCode)
            {
                throw new ArgumentOutOfRangeException(nameof(directionCode), directionCode, "Код направления — 0…8.");
            }

            return new InputFrame(directionCode == 0 ? Input8.None : Input8.FromIndex(directionCode - 1), buttons);
        }

        /// <summary>Записать кадр (3 байта).</summary>
        public void Write(Span<byte> dest)
        {
            dest[0] = DirectionCode;
            dest[1] = (byte)Buttons;
            dest[2] = (byte)(Buttons >> 8);
        }

        /// <summary>Прочитать кадр (3 байта).</summary>
        public static InputFrame Read(ReadOnlySpan<byte> src) =>
            FromCode(src[0], (ushort)(src[1] | (src[2] << 8)));

        public bool Equals(InputFrame other) => Direction == other.Direction && Buttons == other.Buttons;
        public override bool Equals(object? obj) => obj is InputFrame other && Equals(other);
        public override int GetHashCode() => unchecked((DirectionCode * 73856093) ^ (Buttons * 19349663));
        public static bool operator ==(InputFrame a, InputFrame b) => a.Equals(b);
        public static bool operator !=(InputFrame a, InputFrame b) => !a.Equals(b);
        public override string ToString() => Buttons == 0 ? $"in({DirectionCode})" : $"in({DirectionCode}, 0x{Buttons:X4})";
    }
}
