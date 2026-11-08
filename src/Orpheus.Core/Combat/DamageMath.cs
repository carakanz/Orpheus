using System;

namespace Orpheus.Core.Combat
{
    /// <summary>
    /// Формула урона (`02` 6.1; карточка 1-001, шаг 2): после_брони = max(сырой × 0,25; сырой − броня × (1 − пробой)),
    /// округление к ближайшему, не меньше 1. Только целые: проценты через ×100, округление «на 0,5 вверх».
    /// Примеры приёмки 6.10 п. 1–2: 10/бр0 → 10; 10/бр3 → 7; 25/бр3 → 22; 5/бр3 → 2; 5/бр5 → 1.
    /// </summary>
    public static class DamageMath
    {
        /// <summary>Пробой «нет» (в вехе 1 все удары без пробоя, `02` 6.9).</summary>
        public const int PierceNoneX100 = 0;

        /// <summary>
        /// Урон после брони: <paramref name="raw"/> ≥ 1, броня ≥ 0, пробой <paramref name="pierceX100"/> 0…100.
        /// Сырой ≤ 0 — урона нет (0): минимум 1 относится к удару с положительной сырой величиной.
        /// </summary>
        public static int Apply(int raw, int armor, int pierceX100 = PierceNoneX100)
        {
            if (raw <= 0)
            {
                return 0;
            }

            if (pierceX100 < 0 || pierceX100 > 100 || armor < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(armor), armor, $"Урон: броня {armor}, пробой {pierceX100}.");
            }

            // Четверть сырого, округление к ближайшему (1.25 → 1; 6.25 → 6).
            int quarter = (raw * 25 + 50) / 100;

            // Броня с учётом пробоя, округление к ближайшему.
            int armorCut = (armor * (100 - pierceX100) + 50) / 100;
            int through = raw - armorCut;

            int after = Math.Max(quarter, through);
            return Math.Max(1, after);
        }

        /// <summary>Доля от максимума здоровья: ≥ pct % (прерывание 15 % / 10 %, `02` 6.6; целые без float).</summary>
        public static bool AtLeastPercent(int damage, int maxHealth, int pct)
        {
            return damage * 100 >= maxHealth * pct;
        }
    }
}
