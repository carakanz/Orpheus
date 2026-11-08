using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.Trig
{
    /// <summary>
    /// Своя таблица синусов на четверть круга в фиксированной точке и целочисленный atan2
    /// (docs/07, 11.5: «Math.Sin/Cos/Atan2 — из библиотеки C ОС, расходятся в последнем знаке»).
    /// Угол — <see cref="Angle16"/> (u16, 65 536 шагов на круг), значения синуса и косинуса —
    /// Q15: 32 768 = 1,0, диапазон −32 768…32 767.
    /// <para>
    /// Таблица строится один раз при первом обращении: целые углы 2^k шагов от 90° половинным
    /// углом (сложение, деление пополам, <see cref="Math.Sqrt"/> — разрешены) и повороты по битам угла.
    /// Числа от настенных часов и от порядка вызовов не зависят, одинаковы на Windows и Linux.
    /// </para>
    /// </summary>
    public static class TrigTable
    {
        /// <summary>1,0 в фиксированной точке: 2¹⁵ = 32 768.</summary>
        public const int Scale = 1 << 15;

        /// <summary>Шагов угла в четверти круга (90°).</summary>
        public const int Quarter = Angle16.Quarter;

        /// <summary>Шагов угла в половине круга (180°).</summary>
        public const int Half = Angle16.Half;

        /// <summary>Шагов угла на полный круг.</summary>
        public const int Steps = Angle16.Steps;

        /// <summary>
        /// Синус на четверти круга: индекс 0…16 384 (0…90°), значение Q15. Занесена целиком,
        /// читается по индексу — без выделений в такте.
        /// </summary>
        public static readonly int[] QuarterSin = BuildQuarterSine();

        /// <summary>Синус угла, Q15 (−32 768…32 767).</summary>
        public static int Sin(Angle16 a) => SinRaw(a.Raw);

        /// <summary>Косинус угла, Q15.</summary>
        public static int Cos(Angle16 a) => SinRaw(a.Raw + Quarter);

        /// <summary>Косинус по сырому углу (u16 как int), Q15.</summary>
        public static int CosRaw(int raw) => SinRaw((raw + Quarter) & 0xFFFF);

        /// <summary>Синус по сырому углу (u16 как int), Q15.</summary>
        public static int SinRaw(int raw)
        {
            int r = raw & 0xFFFF;
            if (r <= Quarter)
            {
                return QuarterSin[r];
            }

            if (r <= Half)
            {
                return QuarterSin[Half - r]; // sin(180° − x) = sin x
            }

            if (r <= Half + Quarter)
            {
                return -QuarterSin[r - Half]; // sin(180° + x) = −sin x
            }

            return -QuarterSin[Steps - r]; // sin(360° − x) = −sin x
        }

        /// <summary>amplitude · sin(угол), целое; округление к ближайшему, половина — к чётному.</summary>
        public static int SinTimes(Angle16 a, int amplitude) => MulQ15(Sin(a), amplitude);

        /// <summary>amplitude · cos(угол), целое; округление к ближайшему, половина — к чётному.</summary>
        public static int CosTimes(Angle16 a, int amplitude) => MulQ15(Cos(a), amplitude);

        /// <summary>
        /// Вектор длины <paramref name="amplitude"/> в направлении угла: (cos, sin) · amplitude,
        /// целые единицы — для позиции это 1/256 тайла (docs/07, 3.2).
        /// </summary>
        public static Int2 Direction(Angle16 a, int amplitude) =>
            new Int2(CosTimes(a, amplitude), SinTimes(a, amplitude));

        // ---------- atan2: целочисленный CORDIC ----------

        // atan(2^-k) в 1/16 шага угла: шаг угла = 360°/65 536. Константы зафиксированы (round(atan(2^-k) · 65 536 · 16 / 2π)),
        // их больше 18 слагаемых смысла нет: слагаемое меньше 1/16 шага.
        private static readonly int[] AtanTab =
        {
            131072, 77376, 40884, 20753, 10417, 5213, 2607, 1304, 652, 326, 163, 81, 41, 20, 10, 5, 3, 1,
        };

        // Число итераций = длине AtanTab. Запас по покрытию: 45° + Σ atan(2^-k), k = 1…17 ≈ 99,99° > 90°.
        private const int AtanIterations = 18;

        // Вектор домножается на 2²⁴ перед итерациями: сдвиг vx >> k тогда почти не теряет разряды,
        // и ошибка целочисленного сдвига в угле меньше 1/16 шага. 2³¹ · 2²⁴ = 2⁵⁵ < long.MaxValue.
        private const int AtanPreScale = 24;

        /// <summary>
        /// Угол вектора (x, y): целочисленный atan2, тот же результат на всех платформах.
        /// Точность — до 1 шага угла (0,0055°). (0, 0) даёт 0.
        /// </summary>
        public static Angle16 Atan2(int y, int x)
        {
            if (x == 0 && y == 0)
            {
                return new Angle16(0);
            }

            if (x == 0)
            {
                return Angle16.FromRaw(y > 0 ? Quarter : -Quarter);
            }

            long vx = (x < 0 ? -(long)x : x) << AtanPreScale;
            long vy = (y < 0 ? -(long)y : y) << AtanPreScale;
            long angle = 0; // в 1/16 шага угла, со знаком накопленного поворота

            for (int k = 0; k < AtanIterations; k++)
            {
                if (vy >= 0)
                {
                    long nx = vx + (vy >> k);
                    vy = vy - (vx >> k);
                    vx = nx;
                    angle -= AtanTab[k];
                }
                else
                {
                    long nx = vx - (vy >> k);
                    vy = vy + (vx >> k);
                    vx = nx;
                    angle += AtanTab[k];
                }
            }

            // Итоговый поворот равен −исходному углу в первой четверти.
            int a = (int)((-angle + 8) >> 4); // 1/16 шага → шаги, половина — к большему
            int raw = x > 0
                ? (y >= 0 ? a : -a)
                : (y >= 0 ? Half - a : a - Half);
            return Angle16.FromRaw(raw);
        }

        // ---------- Внутреннее ----------

        // Q15 · целое → целое с округлением к ближайшему (половина — к чётному), целыми операциями.
        private static int MulQ15(int valueQ15, int amplitude)
        {
            long v = (long)valueQ15 * amplitude;
            long q = v >> 15;
            long rest = v - (q << 15); // 0…32 767
            if (rest > Scale / 2)
            {
                return (int)(q + 1);
            }

            if (rest < Scale / 2)
            {
                return (int)q;
            }

            return (int)(q + (q & 1));
        }

        private static int[] BuildQuarterSine()
        {
            const int quarterBits = 14; // Quarter = 2¹⁴ шагов

            // (sin, cos) углов 2^k шагов, k = 0…14: старт 90° = (1, 0), дальше половинный угол.
            double[] sinPow = new double[quarterBits + 1];
            double[] cosPow = new double[quarterBits + 1];
            sinPow[quarterBits] = 1.0;
            cosPow[quarterBits] = 0.0;
            double s = 1.0, c = 0.0;
            for (int k = quarterBits - 1; k >= 0; k--)
            {
                double ch = Math.Sqrt((1.0 + c) * 0.5);
                double sh = Math.Sqrt((1.0 - c) * 0.5);
                c = ch;
                s = sh;
                sinPow[k] = s;
                cosPow[k] = c;
            }

            int[] table = new int[Quarter + 1];
            for (int a = 0; a <= Quarter; a++)
            {
                // Поворот на угол a = сумма поворотов на 2^k для установленных битов a.
                double sa = 0.0, ca = 1.0;
                for (int k = 0; k <= quarterBits; k++)
                {
                    if ((a & (1 << k)) == 0)
                    {
                        continue;
                    }

                    double ns = sa * cosPow[k] + ca * sinPow[k];
                    double nc = ca * cosPow[k] - sa * sinPow[k];
                    sa = ns;
                    ca = nc;
                }

                long v = (long)Math.Round(sa * Scale);
                if (v > Scale - 1)
                {
                    v = Scale - 1; // 1,0 в Q15 = 32 767
                }

                if (v < -(Scale - 1))
                {
                    v = -(Scale - 1);
                }

                table[a] = (int)v;
            }

            return table;
        }
    }
}
