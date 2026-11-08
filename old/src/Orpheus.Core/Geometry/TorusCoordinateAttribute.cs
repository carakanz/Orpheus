using System;

namespace Orpheus.Core.Geometry
{
    /// <summary>
    /// Помечает координатный тип тора (позиция, тайл, чанк). Вычитать два таких значения
    /// или их поля напрямую запрещено (ORP001): только через <see cref="Torus.Delta(in MapShape, WorldPos, WorldPos)"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
    public sealed class TorusCoordinateAttribute : Attribute
    {
    }
}
