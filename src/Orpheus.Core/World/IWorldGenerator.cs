using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Точка подключения генератора мира (docs/07, 3.4): по форме карты и зерну — мир в памяти,
    /// содержимое которого — «снимок генерации» (чанки с ревизией 0, не изменённые после генерации).
    /// Интерфейс — в ядре, чтобы сервер, тесты и инструменты подключали генератор, не завися от его модуля:
    /// генератор сборки — <c>StubGenerator</c> из <c>Orpheus.WorldGen</c> (0-007, <c>WorldGenModule.Current</c>),
    /// плоский мир для тестов такта — <see cref="FlatWorldGenerator"/>.
    /// <para>
    /// Требование повторяемости (11.5): одно зерно и одна версия → побайтно один мир.
    /// </para>
    /// </summary>
    public interface IWorldGenerator
    {
        /// <summary>Версия генератора — пишется в снимок генерации (3.4); новая версия — новый мир по тому же зерну.</summary>
        int Version { get; }

        /// <summary>Создать мир формы <paramref name="shape"/> по зерну <paramref name="seed"/>.</summary>
        WorldModel Generate(MapShape shape, ulong seed);
    }
}
