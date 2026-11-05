using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Точка подключения генератора мира (docs/07, 3.4): по форме карты и зерну — мир в памяти,
    /// содержимое которого — «снимок генерации» (чанки с ревизией 0, не изменённые после генерации).
    /// Сервер (`--create`) знает только этот интерфейс; настоящий генератор — `Orpheus.WorldGen`
    /// (задача 0-007), до него — <see cref="FlatWorldGenerator"/>.
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
