using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Trig;

namespace Orpheus.Core.Combat
{
    /// <summary>
    /// Враг-манекен шипа 0-029: стоит на месте и замахивается ближним ударом по таймеру (без ИИ, здоровья и урона —
    /// «Не трогать» карточки). Сектор — <see cref="EnemyMelee"/> (`02` 3.1, 8.1), ось — заданное направление.
    /// Замах начинается в кадрах «первый» + k × <see cref="PeriodFrames"/>; начало — событие
    /// «замах начат» (<see cref="Threat"/> с кадром начала по времени сервера) для очереди угроз и снимка.
    /// Время — кадры ввода 60 Гц, номера с переполнением (сравнение разностью). Без выделений.
    /// </summary>
    public struct MeleeDummy
    {
        /// <summary>Номер сущности манекена.</summary>
        public int Id;

        /// <summary>Где стоит (центр сектора).</summary>
        public WorldPos Position;

        /// <summary>Куда бьёт (ось сектора).</summary>
        public Angle16 Facing;

        /// <summary>Кадр следующего замаха.</summary>
        public uint NextSwingFrame;

        /// <summary>Кадров между началами замахов, не меньше полного цикла удара (<see cref="EnemyMelee.CycleFrames"/>).</summary>
        public int PeriodFrames;

        /// <summary>Первый замах — в кадре <paramref name="firstSwingFrame"/>, дальше раз в <paramref name="periodFrames"/>.</summary>
        public MeleeDummy(int id, WorldPos position, Angle16 facing, uint firstSwingFrame, int periodFrames = EnemyMelee.CycleFrames)
        {
            if (periodFrames < EnemyMelee.CycleFrames)
            {
                throw new ArgumentOutOfRangeException(nameof(periodFrames), periodFrames, $"Период не короче цикла удара {EnemyMelee.CycleFrames} кадров.");
            }

            Id = id;
            Position = position;
            Facing = facing;
            NextSwingFrame = firstSwingFrame;
            PeriodFrames = periodFrames;
        }

        /// <summary>Сектор удара манекена.</summary>
        public MeleeSector Sector => new MeleeSector(Position, Facing, EnemyMelee.ReachUnits, EnemyMelee.HalfArcSteps);

        /// <summary>
        /// Кадр сервера <paramref name="frame"/>: если в нём начинается замах — true и событие «замах начат»
        /// (<paramref name="swing"/>, кадр начала = <paramref name="frame"/>). Пропущенный кадр начала (манекен шагнули
        /// позже) — замах начинается в первом шагнутом кадре, следующий — через период от него.
        /// </summary>
        public bool Step(uint frame, out Threat swing)
        {
            if (unchecked((int)(frame - NextSwingFrame)) < 0)
            {
                swing = default;
                return false;
            }

            swing = new Threat(ThreatKind.Melee, Id, frame, EnemyMelee.WindupFrames, Sector);
            NextSwingFrame = unchecked(frame + (uint)PeriodFrames);
            return true;
        }

        public override string ToString() => $"манекен #{Id} {Position} ось {Facing.Raw}, следующий замах {NextSwingFrame}";
    }
}
