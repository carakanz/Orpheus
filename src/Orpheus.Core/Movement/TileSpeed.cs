using Orpheus.Core.Geometry;

namespace Orpheus.Core.Movement
{
    /// <summary>
    /// Владелец функции «тайл твёрдый?» (<see cref="TileSolid"/>, его <c>Target</c>), знающий замедляющую местность (C-018):
    /// множитель скорости тайла в 1/256 (256 — обычный ход; вода, куст — меньше, таблица <c>World.TerrainSlow</c>). Так одна и
    /// та же <see cref="TileSolid"/> (<c>Simulation.IsBlocked</c>) даёт замедление серверу, предсказанию клиента и ботам;
    /// функции без такого владельца (лямбды тестов, <see cref="Collision.Empty"/>) — обычный ход.
    /// </summary>
    public interface ITileSpeed
    {
        /// <summary>Множитель скорости тайла (свёрнутого или нет — владелец сворачивает сам) в 1/256.</summary>
        int SpeedQ8(MapShape map, TileCoord tile);
    }

    /// <summary>Множитель скорости под центром (ногами) существа (C-018).</summary>
    public static class TileSpeed
    {
        /// <summary>Обычный ход: 256/256.</summary>
        public const int One = 256;

        /// <summary>Множитель тайла под точкой <paramref name="pos"/>; владелец <paramref name="solid"/> без <see cref="ITileSpeed"/> — 256.</summary>
        public static int At(in MapShape map, WorldPos pos, TileSolid solid) =>
            solid.Target is ITileSpeed speed ? speed.SpeedQ8(map, Torus.Wrap(map, Torus.TileOf(map, pos))) : One;

        /// <summary>Скорость в unit × 10 с множителем (целые, сдвиг; 256 — без изменений).</summary>
        public static int Scale(int speedX10, int q8) => q8 == One ? speedX10 : (speedX10 * q8) >> 8;
    }
}
