using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Карточка E1-8 (план 1.8 «тон суток»): L тайла для рендера = фон × k(пол) + свет источников.
    /// k по полу (у клиента байта биома нет): лесная подстилка — лес (870), остальные полы — 1024.
    /// Приёмка: ночью у факела/костра светло (L → 1024), открытая земля ночью — фон 307,
    /// лес ночью — ниже порога видимости (260 &lt; 277), днём — 870.
    /// </summary>
    public class DayTintTests
    {
        private static WorldModel Flat() => new WorldModel(MapShape.World(1024), seed: 7, generatorVersion: 1);

        private static void Put(WorldModel w, int x, int y, byte floor, ushort obj) =>
            w.SetBaseTile(new TileCoord(x, y), floor, obj);

        // ---------- k по полу ----------

        [Fact]
        public void E18_K_By_Floor()
        {
            Assert.Equal(870, DayTint.BiomeKX1024ForFloor(FloorId.ForestFloor)); // лес 0,85
            Assert.Equal(1024, DayTint.BiomeKX1024ForFloor(FloorId.Grass));
            Assert.Equal(1024, DayTint.BiomeKX1024ForFloor(FloorId.Water));
            Assert.Equal(1024, DayTint.BiomeKX1024ForFloor(FloorId.Empty));
            Assert.Equal(1024, DayTint.BiomeKX1024ForFloor(FloorId.StoneFloor));
        }

        // ---------- Ночь: у источника светло, без источника — фон ----------

        [Fact]
        public void E18_Torch_Bright_At_Night()
        {
            WorldModel w = Flat();
            Put(w, 100, 100, FloorId.Grass, ObjectId.Torch);
            int night = DayClock.NightBgX1024; // 307

            // Сам факел и радиус 3: свет 1024 → L = 1024 («ночью у факела светло»).
            Assert.Equal(1024, DayTint.TileX1024(w, new TileCoord(100, 100), night));
            Assert.Equal(1024, DayTint.TileX1024(w, new TileCoord(103, 100), night));
            Assert.Equal(0, DayTint.DarknessX1024(DayTint.TileX1024(w, new TileCoord(101, 101), night)));

            // За радиусом (d = 6 → вклад 0): ночь на открытой земле = фон 307 (виден, темно).
            int far = DayTint.TileX1024(w, new TileCoord(106, 100), night);
            Assert.Equal(night, far);
            Assert.True(DayClock.IsVisible(far));
            Assert.Equal(1024 - night, DayTint.DarknessX1024(far));
        }

        [Fact]
        public void E18_Campfire_Lights_Forest_At_Night()
        {
            WorldModel w = Flat();
            Put(w, 60, 60, FloorId.Sand, ObjectId.Campfire);      // костёр на песке
            Put(w, 62, 60, FloorId.ForestFloor, ObjectId.Empty);  // лес рядом

            int night = DayClock.NightBgX1024;
            // Костёр: полный свет до 4 тайлов → лес в двух тайлах ярко (307×0,85 + 1024 → 1024).
            Assert.Equal(1024, DayTint.TileX1024(w, new TileCoord(62, 60), night));
            // Лес без света ночью — ниже порога видимости (01, 6): 307×870/1024 = 260.
            Put(w, 200, 200, FloorId.ForestFloor, ObjectId.Empty);
            int darkForest = DayTint.TileX1024(w, new TileCoord(200, 200), night);
            Assert.Equal(260, darkForest);
            Assert.False(DayClock.IsVisible(darkForest));
        }

        // ---------- Фазы различимы ----------

        [Fact]
        public void E18_Phases_Differ()
        {
            WorldModel w = Flat();
            Put(w, 10, 10, FloorId.Grass, ObjectId.Empty);
            Put(w, 11, 10, FloorId.ForestFloor, ObjectId.Empty);
            TileCoord grass = new TileCoord(10, 10), forest = new TileCoord(11, 10);

            int day = DayClock.BackgroundX1024(DayClock.TicksPerDay / 2);         // день: 1024
            int dusk = DayClock.BackgroundX1024((DayClock.DuskStartTick + DayClock.NightStartTick) / 2); // середина сумерек
            int night = DayClock.BackgroundX1024((DayClock.NightStartTick + DayClock.DawnStartTick) / 2); // 307
            int dawn = DayClock.BackgroundX1024(DayClock.DawnStartTick + 900);    // середина рассвета

            Assert.Equal(1024, day);
            Assert.Equal(307, night);
            Assert.True(dusk > night && dusk < day);
            Assert.True(dawn > night && dawn < day);

            // Открытая земля: четыре разных L; лес темнее открытой земли в каждой фазе.
            int lgDay = DayTint.TileX1024(w, grass, day), lgDusk = DayTint.TileX1024(w, grass, dusk),
                lgNight = DayTint.TileX1024(w, grass, night), lgDawn = DayTint.TileX1024(w, grass, dawn);
            Assert.True(lgNight < lgDusk && lgDusk < lgDay);
            Assert.True(lgNight < lgDawn && lgDawn < lgDay);
            Assert.True(DayTint.TileX1024(w, forest, day) < lgDay);
            Assert.True(DayTint.TileX1024(w, forest, night) < lgNight);
        }

        [Fact]
        public void E18_Darkness_Clamps()
        {
            Assert.Equal(0, DayTint.DarknessX1024(1024));
            Assert.Equal(1024, DayTint.DarknessX1024(0));
            Assert.Equal(0, DayTint.DarknessX1024(5000));  // выше полного — не отрицательная тьма
            Assert.Equal(1024, DayTint.DarknessX1024(-10));
        }
    }
}
