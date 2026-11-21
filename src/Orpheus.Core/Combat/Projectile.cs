using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;

namespace Orpheus.Core.Combat
{
    /// <summary>
    /// Строка таблицы луков (E3-2, `02` 4.1): урон и скорость линейно по натяжению 0,2…0,6 с, дальность. Числа боя — данными.
    /// Простой лук — B акта I (10): урон 5…10, 18…28 т/с, 30 т. Медный лук — *(прикидка E3-2)* ×1,2 урона и +10 % дальности;
    /// окончательно — `06` (ярус I-2).
    /// </summary>
    public readonly struct BowDef
    {
        public readonly byte Item;
        public readonly byte DamageMin;
        public readonly byte DamageMax;

        /// <summary>Скорость стрелы при 0,2 с и при полном натяжении, т/с.</summary>
        public readonly byte SpeedMinTps;
        public readonly byte SpeedMaxTps;

        /// <summary>Дальность, тайлов.</summary>
        public readonly byte RangeTiles;

        /// <summary>Предмет-снаряд (стрелы).</summary>
        public readonly byte Ammo;

        public BowDef(byte item, byte damageMin, byte damageMax, byte speedMinTps, byte speedMaxTps, byte rangeTiles, byte ammo)
        {
            Item = item;
            DamageMin = damageMin;
            DamageMax = damageMax;
            SpeedMinTps = speedMinTps;
            SpeedMaxTps = speedMaxTps;
            RangeTiles = rangeTiles;
            Ammo = ammo;
        }

        /// <summary>Сырой урон по доле натяжения 0…256 (<see cref="BowAction.PowerQ8"/>), с округлением к ближайшему.</summary>
        public int DamageAt(int powerQ8) => DamageMin + ((DamageMax - DamageMin) * powerQ8 + 128) / 256;

        /// <summary>Скорость по доле натяжения, unit/такт × 256 (1/65 536 тайла за такт 30 Гц).</summary>
        public int SpeedX256At(int powerQ8)
        {
            int tpsQ8 = SpeedMinTps * 256 + (SpeedMaxTps - SpeedMinTps) * powerQ8; // т/с × 256
            return (int)((long)tpsQ8 * Torus.SubTilesPerTile / Projectiles.TicksPerSecond);
        }

        /// <summary>Дальность в unit.</summary>
        public int RangeUnits => RangeTiles * Torus.SubTilesPerTile;
    }

    /// <summary>Таблица луков и константы снарядов (E3-2, `02` 4.1–4.2).</summary>
    public static class Projectiles
    {
        /// <summary>Тактов в секунду.</summary>
        public const int TicksPerSecond = 30;

        /// <summary>Радиус стрелы: 0,2 т = 51 unit (`02` 4.2).</summary>
        public const int ArrowRadiusUnits = 51;

        /// <summary>Отбрасывание стрелой — половина базового (0,5 т, `02` 4.2): делитель скорости отбрасывания.</summary>
        public const int ArrowKnockDivisor = 2;

        /// <summary>
        /// Конец снаряда видят игроки дальше окна урона (56 т) на дальность стрелы с запасом — те, кто мог получить выстрел
        /// (выстрел — в 56 т от точки выстрела), получают и конец.
        /// </summary>
        public const int StopViewExtraTiles = 40;

        /// <summary>Тип урона стрелы — колющий (`02` 6.2).</summary>
        public const byte DamagePierce = 2;

        /// <summary>Вид снаряда: стрела простого лука.</summary>
        public const byte KindArrow = 1;

        /// <summary>Вид снаряда: стрела медного лука (картинка та же, числа — из строки лука).</summary>
        public const byte KindCopperArrow = 2;

        private static readonly BowDef[] Bows =
        {
            new BowDef(Items.SimpleBow, 5, 10, 18, 28, 30, Items.Arrows),
            new BowDef(Items.CopperBow, 6, 12, 18, 28, 33, Items.Arrows),
        };

        /// <summary>Строка лука по предмету; не лук — false.</summary>
        public static bool TryGetBow(byte item, out BowDef def)
        {
            for (int i = 0; i < Bows.Length; i++)
            {
                if (Bows[i].Item == item)
                {
                    def = Bows[i];
                    return true;
                }
            }

            def = default;
            return false;
        }

        /// <summary>Вид снаряда лука.</summary>
        public static byte KindOfBow(byte item) => item == Items.CopperBow ? KindCopperArrow : KindArrow;
    }

    /// <summary>Почему снаряд кончился.</summary>
    public enum ProjectileEnd : byte
    {
        /// <summary>Попал в существо.</summary>
        Hit = 1,

        /// <summary>Остановлен препятствием (стена, закрытая дверь, скала, дерево, обрыв).</summary>
        Wall = 2,

        /// <summary>Долетел до конца дальности.</summary>
        Range = 3,

        /// <summary>
        /// Выстрела не было (П E3-2): автомат лука выстрелил, а сервер — нет (стрел не осталось: выбросил в натяжении, или потолок
        /// снарядов). Номер 0, только владельцу — клиент сразу гасит свою предсказанную стрелу.
        /// </summary>
        Refused = 4,
    }

    /// <summary>
    /// Снаряд в такте (E3-2, `02` 4.2): серверный объект — номер, владелец, сторона, позиция, скорость, пройденный путь,
    /// урон. Полёт прямолинейный, детерминированный: позиция в 1/256 тайла плюс дробная часть 1/256 unit; полёт
    /// клиента по событию выстрела повторяет его тем же счётом (<see cref="PositionAt"/>).
    /// </summary>
    public struct Projectile
    {
        /// <summary>Номер: растёт, не переиспользуется.</summary>
        public int Id;

        /// <summary>Владелец (номер сущности; может уже не существовать — `02` 4.3).</summary>
        public int Owner;

        /// <summary>Снаряд врага (вредит персонажам); иначе — персонажа (вредит врагам). Дружественного огня нет.</summary>
        public bool Hostile;

        /// <summary>Вид (<see cref="Projectiles.KindArrow"/>…).</summary>
        public byte Kind;

        /// <summary>Сырой урон.</summary>
        public byte Damage;

        /// <summary>Сильный выстрел (полное натяжение): цифра урона крупнее (E3-1, «сильный»).</summary>
        public bool Strong;

        /// <summary>Предмет, который остаётся при остановке (стрелы) с шансом; 0 — ничего.</summary>
        public byte DropItem;

        /// <summary>Такт выстрела.</summary>
        public long SpawnTick;

        /// <summary>Точка выстрела (обёрнута).</summary>
        public WorldPos Origin;

        /// <summary>Ось (u16).</summary>
        public ushort AimRaw;

        /// <summary>Скорость, unit × 256 за такт.</summary>
        public int SpeedX256;

        /// <summary>Дальность, unit.</summary>
        public int RangeUnits;

        /// <summary>Тактов полёта прошло.</summary>
        public int Age;

        /// <summary>Позиция после <paramref name="age"/> тактов полёта (без обёртки): Origin + dir × speed × age.</summary>
        public readonly WorldPos PositionAt(int age) => PositionAtTravel((long)SpeedX256 * age);

        /// <summary>Позиция на пути <paramref name="travelX256"/> (unit × 256) от точки выстрела (без обёртки).</summary>
        public readonly WorldPos PositionAtTravel(long travelX256)
        {
            Trig.Angle16 a = Trig.Angle16.FromRaw(AimRaw);
            long cx = Trig.TrigTable.Cos(a), sy = Trig.TrigTable.Sin(a); // Q15
            long dx = (cx * travelX256) >> 23; // Q15 × unit×256 → unit
            long dy = (sy * travelX256) >> 23;
            return new WorldPos(Origin.X + (int)dx, Origin.Y + (int)dy);
        }
    }
}
