using System;
using Orpheus.Core.Movement;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Кадр ввода сущности на такт (docs/07, 4.2 и 11.5): направление из 8 (или стойка) + кнопки +
    /// угол прицела + ячейка быстрой панели (1-002).
    /// Это всё, что пишется в журнал ввода: по журналу и зерну такт повторяется побитово.
    /// <para>
    /// В журнале — 6 байт (формат 2): направление (0 — стойка, 1…8 — <see cref="Input8.DirectionIndex"/> + 1),
    /// кнопки (u16, младший байт первым), угол прицела (u16, 65 536 шагов на круг) и ячейка (0…<see cref="MaxSlots"/>−1).
    /// Формат 1 (3 байта: направление + кнопки) читается — угол и ячейка по умолчанию.
    /// Значение битов кнопок задают боевые и строительные задачи (`02`, `05`) — <see cref="InputButtons"/>;
    /// уворот (бит 0, 0-029) ядро такта применяет в покадровом шаге (<see cref="Mover.Dodge"/>), остальные пока только переносит и хэширует.
    /// </para>
    /// </summary>
    /// <summary>
    /// Биты кнопок кадра ввода (<see cref="InputFrame.Buttons"/>). Формат кадра от них не меняется: кнопки — u16 и в журнале
    /// (6 байт), и в сети (<c>WireFrame</c>, 7 байт).
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
        /// сервере копится по кадрам ввода с этим битом (<c>DigTimes.FramesOf</c> — «база × модификатор», 0-034); какой тайл — запрос «копать: начало».
        /// </summary>
        public const ushort Dig = 1 << 1;

        /// <summary>
        /// Основное действие — удар мечом (`02` 3.2, 1.2; 1-004): бит — нажатие/удержание в этом кадре (клиент ставит
        /// его на всех кадрах удержания ЛКМ — «удержание = повтор»). В покое начинает замах (ось — прицел кадра),
        /// в последних 12 кадрах отхода — продолжение серии; без ближнего оружия в руках бит не действует (гейт в такте).
        /// </summary>
        public const ushort Attack = 1 << 2;

        /// <summary>
        /// Закрыть ближайшую дверь (1-012, `02` 2.2): бит — <b>нажатие в этом кадре</b>, как уворот: клиент ставит его
        /// в одном кадре на нажатие F, удержание не повторяет. Такт срабатывает на передний фронт (защёлка в <c>Simulation</c>).
        /// </summary>
        public const ushort CloseDoor = 1 << 3;

        /// <summary>В кадре зажато копание.</summary>
        public static bool DigHeld(InputFrame f) => (f.Buttons & Dig) != 0;

        /// <summary>В кадре нажата/зажата кнопка удара.</summary>
        public static bool AttackHeld(InputFrame f) => (f.Buttons & Attack) != 0;
    }

    public readonly struct InputFrame : IEquatable<InputFrame>
    {
        /// <summary>Байт кадра в журнале (формат 2): направление 1 + кнопки 2 + угол 2 + ячейка 1.</summary>
        public const int WireBytes = 6;

        /// <summary>Байт кадра в журнале формата 1: направление 1 + кнопки 2.</summary>
        public const int LegacyWireBytes = 3;

        /// <summary>Ячеек быстрой панели (1-002): 0…7.</summary>
        public const byte MaxSlots = 8;

        /// <summary>Наибольший код направления: 8 (северо-восток).</summary>
        public const byte MaxDirectionCode = Input8.Count;

        /// <summary>Ничего не нажато.</summary>
        public static readonly InputFrame None = default;

        /// <summary>Направление движения (8 направлений или стойка).</summary>
        public readonly Input8 Direction;

        /// <summary>Кнопки — битовая маска.</summary>
        public readonly ushort Buttons;

        /// <summary>Угол прицела, u16 (65 536 шагов на круг, 0,0055°); у врагов и ботов — по умолчанию 0 (1-002).</summary>
        public readonly ushort Angle16;

        /// <summary>Ячейка быстрой панели 0…<see cref="MaxSlots"/>−1 — предмет в руках (1-002).</summary>
        public readonly byte Slot;

        public InputFrame(Input8 direction, ushort buttons = 0, ushort angle16 = 0, byte slot = 0)
        {
            Direction = direction;
            Buttons = buttons;
            Angle16 = angle16;
            Slot = slot;
        }

        /// <summary>В этом кадре нажат уворот (<see cref="InputButtons.Dodge"/>).</summary>
        public bool DodgePressed => (Buttons & InputButtons.Dodge) != 0;

        /// <summary>В этом кадре нажата/зажата кнопка удара (<see cref="InputButtons.Attack"/>; 1-004).</summary>
        public bool AttackPressed => (Buttons & InputButtons.Attack) != 0;

        /// <summary>Код направления: 0 — стойка, 1…8 — номер направления + 1.</summary>
        public byte DirectionCode => Direction.IsMoving ? (byte)(Direction.DirectionIndex + 1) : (byte)0;

        /// <summary>
        /// Кадр по коду направления, кнопкам, углу и ячейке; код направления вне 0…8 или ячейка вне 0…7 — исключение
        /// (журнал испорчен / кадр по сети от чужого клиента).
        /// </summary>
        public static InputFrame FromCode(byte directionCode, ushort buttons, ushort angle16 = 0, byte slot = 0)
        {
            if (directionCode > MaxDirectionCode)
            {
                throw new ArgumentOutOfRangeException(nameof(directionCode), directionCode, "Код направления — 0…8.");
            }

            if (slot >= MaxSlots)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot, "Ячейка быстрой панели — 0…7.");
            }

            return new InputFrame(directionCode == 0 ? Input8.None : Input8.FromIndex(directionCode - 1), buttons, angle16, slot);
        }

        /// <summary>Записать кадр (6 байт): направление, кнопки (u16), угол (u16), ячейка — младший байт первым.</summary>
        public void Write(Span<byte> dest)
        {
            dest[0] = DirectionCode;
            dest[1] = (byte)Buttons;
            dest[2] = (byte)(Buttons >> 8);
            dest[3] = (byte)Angle16;
            dest[4] = (byte)(Angle16 >> 8);
            dest[5] = Slot;
        }

        /// <summary>Прочитать кадр (6 байт, формат 2).</summary>
        public static InputFrame Read(ReadOnlySpan<byte> src) =>
            FromCode(src[0], (ushort)(src[1] | (src[2] << 8)), (ushort)(src[3] | (src[4] << 8)), src[5]);

        /// <summary>Прочитать кадр формата 1 (3 байта: направление + кнопки); угол и ячейка — по умолчанию.</summary>
        public static InputFrame ReadV1(ReadOnlySpan<byte> src) =>
            FromCode(src[0], (ushort)(src[1] | (src[2] << 8)));

        public bool Equals(InputFrame other) =>
            Direction == other.Direction && Buttons == other.Buttons && Angle16 == other.Angle16 && Slot == other.Slot;
        public override bool Equals(object? obj) => obj is InputFrame other && Equals(other);
        public override int GetHashCode() =>
            unchecked((DirectionCode * 73856093) ^ (Buttons * 19349663) ^ (Angle16 * 83492791) ^ (Slot * 455016879));
        public static bool operator ==(InputFrame a, InputFrame b) => a.Equals(b);
        public static bool operator !=(InputFrame a, InputFrame b) => !a.Equals(b);
        public override string ToString()
        {
            string s = $"in({DirectionCode}";
            if (Buttons != 0)
            {
                s += $", 0x{Buttons:X4}";
            }

            if (Angle16 != 0)
            {
                s += $", aim {Angle16}";
            }

            if (Slot != 0)
            {
                s += $", slot {Slot}";
            }

            return s + ")";
        }
    }
}
