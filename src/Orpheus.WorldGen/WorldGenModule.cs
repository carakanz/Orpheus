using System;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Модуль генерации мира (docs/01, раздел 2): генераторы сборки и их версии.
    /// <list type="bullet">
    /// <item><c>v1</c> — <see cref="GeneratorV1"/> (1-017): по умолчанию с 1-017, ключ сервера <c>--gen v1</c>;</item>
    /// <item><c>stub</c> — <see cref="StubGenerator"/> (0-007): заглушка для тестов и старых журналов, <c>--gen stub</c>.</item>
    /// </list>
    /// Старые миры не перегенерируются: сервер читает их снимок генерации <c>world.base</c> с версией в заголовке (docs/07, 3.4).
    /// Повтор журнала без папки мира берёт генератор по версии из журнала — <see cref="ForVersion"/>.
    /// </summary>
    public static class WorldGenModule
    {
        /// <summary>Версия генератора сборки по умолчанию (пишется в снимок генерации, docs/07, 3.4).</summary>
        public const int GeneratorVersion = GeneratorV1.GeneratorVersion;

        /// <summary>Ключ по умолчанию.</summary>
        public const string DefaultKey = GeneratorV1.Key;

        /// <summary>Ключ заглушки.</summary>
        public const string StubKey = "stub";

        /// <summary>Генератор сборки по умолчанию (v1).</summary>
        public static IWorldGenerator Current { get; } = new GeneratorV1();

        /// <summary>Заглушка 0-007.</summary>
        public static IWorldGenerator Stub { get; } = new StubGenerator();

        /// <summary>Ключи <c>--gen</c>.</summary>
        public static readonly string[] Keys = { GeneratorV1.Key, StubKey };

        /// <summary>Генератор по ключу <c>--gen</c>; неизвестный ключ — <see cref="ArgumentException"/>.</summary>
        public static IWorldGenerator ForKey(string key)
        {
            switch (key)
            {
                case GeneratorV1.Key: return Current;
                case StubKey: return Stub;
                default: throw new ArgumentException($"--gen: генератора «{key}» нет; есть: {string.Join(", ", Keys)}.");
            }
        }

        /// <summary>Генератор, который строит миры версии <paramref name="version"/>; такой версии в сборке нет — null.</summary>
        public static IWorldGenerator? ForVersion(int version)
        {
            if (version == GeneratorV1.GeneratorVersion)
            {
                return Current;
            }

            return version == StubGenerator.GeneratorVersion ? Stub : null;
        }

        /// <summary>Мир-тор W × W генератором по умолчанию и его снимок генерации (<c>world.base</c>) — байты.</summary>
        public static byte[] GenerateBase(int size, ulong seed) =>
            WorldSnapshot.WriteBase(Current.Generate(Orpheus.Core.Geometry.MapShape.World(size), seed));
    }
}
