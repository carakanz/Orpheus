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
    /// (`02`, `05`) — <see cref="InputButtons"/>; уворот (бит 0, 0-029) ядро такта применяет в покадровом шаге
    /// (<see cref="Mover.Dodge"/>), остальные пока только переносит и хэширует.
    /// </para>
    /// </summary>
    /// <summary>
    /// Биты кнопок кадра ввода (<see cref="InputFrame.Buttons"/>). Формат кадра от них не меняется: кнопки — u16 и в журнале
    /// (3 байта), и в сети (<c>WireFrame</c>, 6 байт).
    /// </summary>
    public static class InputButtons
    {
        /// <summary>
        /// Уворот (`02` 2.2; 0-029): бит — <b>нажатие в этом кадре</b>, а не удержание. Клиент ставит его в одном кадре на
        /// нажатие клавиши; удержание не повторяет рывок. Нажатие в рывке, в откате или без направления — без действия.
        /// </summary>
        public const ushort Dodge = 1 << 0;

        /// <summary>
        /// Основное действие — копание (`05` 1.3, `07` 3.7; 0-028): бит — <b>удержание</b> в этом кадре. Прогресс копания на
        /// сервере копится по кадрам ввода с этим битом (<c>EditRules.DigFrames</c>); какой тайл — запрос «копать: начало».
        /// </summary>
        public const ushort Dig = 1 << 1;

        /// <summary>В кадре зажато копание.</summary>
        public static bool DigHeld(InputFrame f) => (f.Buttons & Dig) != 0;
    }

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

        /// <summary>В этом кадре нажат уворот (<see cref="InputButtons.Dodge"/>).</summary>
        public bool DodgePressed => (Buttons & InputButtons.Dodge) != 0;

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
