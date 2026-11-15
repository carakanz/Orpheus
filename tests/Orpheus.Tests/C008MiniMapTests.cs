using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-008 (п. 8, слово Автора: «справа вверху мини-карту»): цвета мини-карты — таблицей по полу и объекту, север вверху,
    /// через шов мира — по тору, точки сущностей — по кратчайшему смещению.
    /// </summary>
    public class C008MiniMapTests
    {
        private static WorldModel Flat()
        {
            WorldModel w = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            w.CreateAllChunks();
            return w;
        }

        [Fact]
        public void C008_MiniMap_Colors_FloorObjectBuilding()
        {
            WorldModel w = Flat();
            var t = new TileCoord(100, 100);
            w.SetBaseTile(t, FloorId.Water, ObjectId.Empty);
            Assert.Equal(MiniMap.FloorColors[FloorId.Water], MiniMap.ColorOf(w, t));
            w.SetBaseTile(t, FloorId.Grass, ObjectId.Tree);
            Assert.Equal(MiniMap.ObjectColors[ObjectId.Tree], MiniMap.ColorOf(w, t));
            w.SetBaseTile(t, FloorId.Grass, ObjectId.StoneWall);
            Assert.Equal(MiniMap.Building, MiniMap.ColorOf(w, t));

            var empty = new WorldModel(MapShape.World(1024));
            Assert.Equal(MiniMap.Unknown, MiniMap.ColorOf(empty, t)); // мир ещё не пришёл
        }

        [Fact]
        public void C008_MiniMap_NorthUp_AcrossSeam()
        {
            WorldModel w = Flat();
            // У шва: центр (1, 1), вода на (1023, 1) — западнее через шов, лава на (1, 1022) — севернее через шов.
            w.SetBaseTile(new TileCoord(1023, 1), FloorId.Water, ObjectId.Empty);
            w.SetBaseTile(new TileCoord(1, 1022), FloorId.Lava, ObjectId.Empty);
            const int radius = 8, side = 16;
            var px = new uint[side * side];
            MiniMap.Paint(w, new TileCoord(1, 1), radius, side, px);

            // Пиксель тайла (dx, dy): столбец = dx + radius, строка снизу = side − 1 − (dy + radius).
            Assert.Equal(MiniMap.FloorColors[FloorId.Water], px[(side - 1 - (0 + radius)) * side + (-2 + radius)]);
            Assert.Equal(MiniMap.FloorColors[FloorId.Lava], px[(side - 1 - (-3 + radius)) * side + (0 + radius)]);
            // Север (меньший Y) — в верхней половине картинки (большая строка).
            Assert.True(side - 1 - (-3 + radius) > side / 2);
        }

        [Fact]
        public void C008_MiniMap_Marker_ShortestWay()
        {
            MapShape shape = MapShape.World(1024);
            int u = Torus.SubTilesPerTile;
            var self = new WorldPos(2 * u, 2 * u);
            var other = new WorldPos(1020 * u, 2 * u); // 6 тайлов западнее через шов
            Assert.True(MiniMap.Marker(shape, self, other, 64, out float x, out float y));
            Assert.InRange(x, -6.5f / 64f, -5.5f / 64f);
            Assert.Equal(0f, y);
            Assert.True(MiniMap.Marker(shape, self, new WorldPos(2 * u, 1000 * u), 64, out _, out float yn)); // 26 тайлов севернее
            Assert.True(yn > 0f);
            Assert.False(MiniMap.Marker(shape, self, new WorldPos(500 * u, 500 * u), 64, out _, out _));
        }
    }
}
