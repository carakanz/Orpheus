using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Генератор мира (docs/07, 3.4): только сервер, один раз при создании мира; одно зерно и одна версия —
    /// побайтно один мир. Результат — <see cref="WorldModel"/> со всеми чанками поверхности как содержимым
    /// генерации (ревизия 0, не грязные); снимок генерации — <see cref="WorldSnapshot.WriteBase(WorldModel)"/>.
    /// </summary>
    public interface IWorldGenerator
    {
        /// <summary>Версия генератора: растёт при любом изменении результата хотя бы одного тайла (3.4).</summary>
        int Version { get; }

        /// <summary>Мир-тор формы <paramref name="shape"/> по зерну <paramref name="seed"/>.</summary>
        WorldModel Generate(MapShape shape, ulong seed);
    }
}
