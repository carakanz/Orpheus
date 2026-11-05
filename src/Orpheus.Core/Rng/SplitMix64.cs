namespace Orpheus.Core.Rng
{
    /// <summary>
    /// SplitMix64 — целочисленный генератор с явным зерном (docs/07, 1.3 и 11.5).
    /// Только целочисленные сложение, умножение, сдвиг и XOR: одна и та же последовательность
    /// на Windows и Linux, на x64 и ARM64, в Mono и IL2CPP. <c>System.Random</c> и настенные часы не используются.
    /// <para>
    /// Поток на подсистему симуляции даёт <see cref="DetRandom"/>; хэш тайла, вида объекта и броска —
    /// <see cref="Hash(int, int, ulong)"/> (docs/07, 3.3 и 11.5).
    /// </para>
    /// <para>
    /// Структура: состояние — один <see cref="ulong"/>, в такте памяти не выделяет.
    /// Равномерные числа в <see cref="NextInt(int)"/> берутся остатком от деления: смешение
    /// ≤ bound/2⁶⁴ — на порядки меньше игрового допуска.
    /// </para>
    /// </summary>
    public struct SplitMix64
    {
        /// <summary>Шаг состояния (золотое сечение, 2⁶⁴/φ).</summary>
        public const ulong Step = 0x9E3779B97F4A7C15UL;

        /// <summary>Константа перемешивания A (из эталонного SplitMix64).</summary>
        public const ulong MixA = 0xBF58476D1CE4E5B9UL;

        /// <summary>Константа перемешивания B (из эталонного SplitMix64).</summary>
        public const ulong MixB = 0x94D049BB133111EBUL;

        private ulong _state;

        public SplitMix64(ulong seed)
        {
            _state = seed;
        }

        /// <summary>Текущее состояние — для журнала ввода, сохранения и повтора (docs/07, 11.5).</summary>
        public readonly ulong State => _state;

        /// <summary>Восстановить состояние из журнала ввода или сохранения.</summary>
        public void SetState(ulong state)
        {
            _state = state;
        }

        /// <summary>Перемешивание SplitMix64 (финализатор): без ветвлений, только целые операции.</summary>
        public static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * MixA;
            z = (z ^ (z >> 27)) * MixB;
            return z ^ (z >> 31);
        }

        /// <summary>Следующее 64-битное значение; состояние сдвигается на <see cref="Step"/>.</summary>
        public ulong Next()
        {
            ulong z = _state += Step;
            return Mix(z);
        }

        /// <summary>Равномерно в [0, bound), bound &gt; 0.</summary>
        public int NextInt(int bound)
        {
            if (bound <= 0)
            {
                throw new System.ArgumentOutOfRangeException(nameof(bound), bound, "Граница должна быть больше нуля.");
            }

            return (int)(Next() % (ulong)bound);
        }

        /// <summary>Равномерно в [min, max), max &gt; min.</summary>
        public int NextInt(int min, int max)
        {
            if (max <= min)
            {
                throw new System.ArgumentOutOfRangeException(nameof(max), max, "Верхняя граница должна быть больше нижней.");
            }

            return min + NextInt(unchecked(max - min));
        }

        /// <summary>Любое значение int32 (для зерна подсистемы, идентификаторов, варианта вида).</summary>
        public int NextAnyInt() => unchecked((int)Next());

        /// <summary>Младшие <paramref name="bits"/> бит (1…64).</summary>
        public int NextBits(int bits)
        {
            if (bits < 1 || bits > 64)
            {
                throw new System.ArgumentOutOfRangeException(nameof(bits), bits, "Число бит — от 1 до 64.");
            }

            ulong v = Next();
            return bits == 64 ? unchecked((int)v) : (int)(v & ((1UL << bits) - 1UL));
        }

        /// <summary>Честная монета — младший бит.</summary>
        public bool NextBool() => (Next() & 1UL) != 0;

        /// <summary>
        /// Вещественное в [0, 1) ровно из 24 значащих бит мантиссы float: (Next() &gt;&gt; 40) / 2²⁴.
        /// Одна и та же величина на любой платформе (деление на степень двойки точное).
        /// </summary>
        public float NextFloat01() => (Next() >> 40) * (1f / 16777216f);

        /// <summary>
        /// Хэш пары целых с явным зерном: вариант вида объекта в тайле (x, y), бросок содержимого
        /// сундука (зерно, сундук, персонаж) — docs/07, 3.3 и 11.5. Стабильна между процессами и ОС.
        /// </summary>
        public static ulong Hash(int x, int y, ulong seed) =>
            Mix(seed + (ulong)(uint)x * Step + (ulong)(uint)y * MixA);

        /// <summary>Хэш тройки целых с явным зерном (тайл + номер сущности + такт и т. п.).</summary>
        public static ulong Hash(int x, int y, int z, ulong seed) =>
            Mix(seed + (ulong)(uint)x * Step + (ulong)(uint)y * MixA + (ulong)(uint)z * MixB);
    }
}
