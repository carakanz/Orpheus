namespace Orpheus.Core.Rng
{
    /// <summary>
    /// Поток случайности одной подсистемы симуляции (docs/07, 11.5): зерно —
    /// SplitMix64(зерно мира, номер системы), поэтому потоки не пересекаются и не зависят
    /// от порядка, в котором системы создаются и обновляются. Один и тот же такт и одно и то же
    /// зерно мира дают одинаковые броски на сервере, у клиента в режиме симуляции и у ботов.
    /// </summary>
    public struct DetRandom
    {
        private SplitMix64 _rng;

        public DetRandom(ulong seed)
        {
            _rng = new SplitMix64(seed);
        }

        /// <summary>
        /// Поток системы № <paramref name="systemId"/> при зерне мира <paramref name="worldSeed"/>.
        /// Номер системы — постоянный (перемещение = 1, бой = 2, генерация мира = 3, …), он попадает
        /// в журнал ввода и сохранения; порядок создания систем не влияет на результат.
        /// </summary>
        public static DetRandom ForSystem(ulong worldSeed, int systemId) =>
            new DetRandom(SplitMix64.Hash(systemId, 0, worldSeed));

        /// <summary>Состояние потока — для журнала ввода, сохранения и повтора.</summary>
        public readonly ulong State => _rng.State;

        /// <summary>Восстановить поток из журнала ввода или сохранения.</summary>
        public void SetState(ulong state) => _rng.SetState(state);

        public ulong Next() => _rng.Next();

        /// <summary>Равномерно в [0, bound), bound &gt; 0.</summary>
        public int NextInt(int bound) => _rng.NextInt(bound);

        /// <summary>Равномерно в [min, max), max &gt; min.</summary>
        public int NextInt(int min, int max) => _rng.NextInt(min, max);

        public int NextAnyInt() => _rng.NextAnyInt();

        /// <summary>Младшие <paramref name="bits"/> бит (1…64).</summary>
        public int NextBits(int bits) => _rng.NextBits(bits);

        public bool NextBool() => _rng.NextBool();

        /// <summary>Вещественное в [0, 1) из 24 значащих бит (одинаково на всех платформах).</summary>
        public float NextFloat01() => _rng.NextFloat01();
    }
}
