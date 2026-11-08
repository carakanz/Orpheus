using System;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Целочисленные помощники генератора (docs/07, 11.5: в генераторе нет плавающей математики).
    /// </summary>
    public static class IntMath
    {
        /// <summary>⌊√v⌋ для v ≥ 0: метод Ньютона на целых, результат проверяется в обе стороны.</summary>
        public static long Isqrt(long v)
        {
            if (v < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(v), v, "Корень из отрицательного числа.");
            }

            if (v < 2)
            {
                return v;
            }

            // Начальное приближение сверху: 2^⌈bits/2⌉.
            int bits = 0;
            for (long t = v; t > 0; t >>= 1)
            {
                bits++;
            }

            long x = 1L << ((bits + 1) >> 1);
            while (true)
            {
                long y = (x + v / x) >> 1;
                if (y >= x)
                {
                    break;
                }

                x = y;
            }

            while (x * x > v)
            {
                x--;
            }

            while ((x + 1) * (x + 1) <= v)
            {
                x++;
            }

            return x;
        }

        /// <summary>a / b с округлением половины от нуля; b &gt; 0.</summary>
        public static long DivRound(long a, long b)
        {
            if (b <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(b), b, "Делитель должен быть больше нуля.");
            }

            return a >= 0 ? (a + (b >> 1)) / b : -((-a + (b >> 1)) / b);
        }

        /// <summary>
        /// Свёрнутая разность на кольце длины <paramref name="size"/> (степень двойки): результат в [−size/2, size/2).
        /// Как <c>wrap</c> модели: <c>(d + W/2) mod W − W/2</c> с неотрицательным остатком.
        /// </summary>
        public static int Wrap(int d, int size) => ((d + (size >> 1)) & (size - 1)) - (size >> 1);

        /// <summary>Модуль числа без ветки по переполнению (аргументы генератора далеки от int.MinValue).</summary>
        public static int Abs(int v) => v < 0 ? -v : v;

        /// <summary>log₂ степени двойки.</summary>
        public static int Log2(int pow2)
        {
            int n = 0;
            while ((1 << n) < pow2)
            {
                n++;
            }

            return n;
        }

        /// <summary>Степень двойки ≥ 1.</summary>
        public static bool IsPow2(int v) => v > 0 && (v & (v - 1)) == 0;
    }
}
