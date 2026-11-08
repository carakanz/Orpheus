using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Модуль генерации мира (docs/01, раздел 2): целочисленный шум, версии генератора.
    /// Сейчас — генератор-заглушка <see cref="StubGenerator"/> (задача 0-007, только этап полей).
    /// </summary>
    public static class WorldGenModule
    {
        /// <summary>Версия текущего генератора (пишется в снимок генерации, docs/07, 3.4).</summary>
        public const int GeneratorVersion = StubGenerator.GeneratorVersion;

        /// <summary>Генератор текущей сборки.</summary>
        public static IWorldGenerator Current { get; } = new StubGenerator();

        /// <summary>Мир-тор W × W и его снимок генерации (<c>world.base</c>) — байты.</summary>
        public static byte[] GenerateBase(int size, ulong seed) =>
            WorldSnapshot.WriteBase(Current.Generate(Orpheus.Core.Geometry.MapShape.World(size), seed));
    }
}
