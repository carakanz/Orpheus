using Orpheus.Core.Movement;

namespace Orpheus.Core.Combat
{
    /// <summary>
    /// Удар мечом персонажа (1-004, `02` 3.2 строка «Меч», деревянный): сектор от центра персонажа —
    /// радиус «персонаж + оружие» (<see cref="Reach.SwordStrikeUnits"/>, 1,4 т, слово Автора 08.11), дуга 120°,
    /// ось по прицелу. Урон 10 / 10 / 13 (серия ×1,0 / ×1,0 / ×1,3, база деревянного меча 10 — вопрос карточки),
    /// отбрасывание 1,0 тайла, третий удар ×2. Фазы удара шагает <see cref="MeleeAction"/> (замах 9 / удар 9 /
    /// отход 21·31 кадров 60 Гц).
    /// </summary>
    public static class SwordStrike
    {
        /// <summary>Досягаемость — тело + меч: 1,4 т = 358 unit (та же, что у разбойника, `02` 8.1).</summary>
        public const int ReachUnits = Reach.SwordStrikeUnits;

        /// <summary>Половина дуги 120°: 60° = 10922 шага угла (65536 / 6).</summary>
        public const int HalfArcSteps = 10922;

        /// <summary>База урона деревянного меча (ярус ×1,0 — до ответа Автора, `06` 4.4).</summary>
        public const int BaseDamage = 10;

        /// <summary>Урон удара серии по индексу 0…2: 10 / 10 / 13 (третий ×1,3).</summary>
        public static int DamageFor(int index) => index == MeleeAction.SeriesSize - 1 ? 13 : BaseDamage;

        /// <summary>Отбрасывание по индексу, тайлов: 1 / 1 / 2 (третий ×2, `02` 3.2).</summary>
        public static int KnockTilesFor(int index) => index == MeleeAction.SeriesSize - 1 ? 2 : 1;

        /// <summary>Скорость отбрасывания за кадр в unit × 10: 1 тайл ÷ 9 кадров ÷ масса × тайлов удара.</summary>
        public static int KnockSpeedX10(int index, int mass) =>
            Mover.KnockBaseX10 * KnockTilesFor(index) / (Mover.KnockFrames * mass);
    }
}
