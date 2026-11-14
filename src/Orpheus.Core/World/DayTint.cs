using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Тон суток для рендера (план 1.8, карточка E1-8): освещённость тайла L для клиента-картинки.
    /// <para>
    /// Формула та же, что у <see cref="LightIndex.LightX1024"/>: L = clamp(фон × k_биома + свет источников, 0, 1024).
    /// Клиент получает от сервера только чанки (docs/07, 3.4), байта биома у него нет, поэтому k берётся по полу:
    /// лесная подстилка (<see cref="FloorId.ForestFloor"/>) — лес, остальные полы — открытая земля (1,0).
    /// Это визуальная приближённая оценка до передачи биома в протокол; правила (порог видимости, спавн) считают
    /// <see cref="LightIndex"/> по настоящим биомам.
    /// </para>
    /// </summary>
    public static class DayTint
    {
        /// <summary>Номер биома «лес» (тот же байт, что <c>Orpheus.WorldGen.Biome</c>).</summary>
        const byte ForestBiome = 1;

        /// <summary>
        /// k_биома (1/1024) по полу тайла: лесная подстилка → поправка леса (870), остальные полы → 1024.
        /// </summary>
        public static int BiomeKX1024ForFloor(byte floor) =>
            floor == FloorId.ForestFloor ? DayClock.BiomeKX1024(ForestBiome) : DayClock.LightScale;

        /// <summary>
        /// L тайла (1/1024) для тона: фон × k(пол) + свет источников тайла.
        /// </summary>
        public static int TileX1024(WorldModel world, TileCoord tile, int bgX1024) =>
            world.Lights.LightX1024(tile, bgX1024, BiomeKX1024ForFloor(world.GetFloor(tile)));

        /// <summary>
        /// Ночная темнота кадра (1/1024): 1024 − L. 0 при полном свете; в shader-е — альфа тёмного слоя.
        /// </summary>
        public static int DarknessX1024(int lightX1024) => DayClock.LightScale - (lightX1024 < 0 ? 0 : lightX1024 > DayClock.LightScale ? DayClock.LightScale : lightX1024);
    }
}
