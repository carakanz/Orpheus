namespace Orpheus.Tests
{
    /// <summary>Генератор тестовых данных с явным зерном (docs/07, 11.5: System.Random не используем).</summary>
    internal struct SplitMix64
    {
        private ulong _state;

        public SplitMix64(ulong seed)
        {
            _state = seed;
        }

        public ulong Next()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Равномерно в [0, bound), bound &gt; 0.</summary>
        public int NextInt(int bound) => (int)(Next() % (ulong)bound);

        /// <summary>Любое значение int32.</summary>
        public int NextAnyInt() => unchecked((int)Next());
    }
}
