using System.Linq;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>C-029 п. 8: карта на весь экран — открытые чанки помнятся (цвета и кровати), неоткрытое тёмное.</summary>
    public sealed class C029MapTests
    {
        [Fact]
        public void C029_Explored_Records_Chunks_Beds_And_Paints()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 7);
            var bed = new TileCoord(40, 41);
            world.SetBaseTile(bed, FloorId.Grass, ObjectId.Bed);
            var map = new ExploredMap(world.Shape);
            Assert.Equal(MiniMap.Unknown, map.ColorAt(bed));
            Assert.False(map.Explored(bed));

            map.MarkDirty(new ChunkCoord(1, 1));
            map.MarkDirty(new ChunkCoord(1, 1)); // дважды — одна запись
            Assert.Equal(1, map.Pending);
            Assert.Equal(1, map.Update(world, 4));
            Assert.Equal(0, map.Pending);
            Assert.True(map.Explored(bed));
            Assert.Equal(MiniMap.ColorOf(world, bed), map.ColorAt(bed));
            Assert.NotEqual(MiniMap.Unknown, map.ColorAt(bed));
            Assert.Equal(new[] { bed }, map.Beds().ToArray());
            Assert.Equal(MiniMap.Unknown, map.ColorAt(new TileCoord(10, 10))); // чанк (0, 0) не открыт

            // Картинка 4 × 2 с центром в тайле кровати, 1 тайл на пиксель: строка 1 (север) — y 40, строка 0 — y 41.
            var px = new uint[8];
            map.Paint(bed.X, bed.Y, 1.0, 4, 2, px); // центр — угол тайлов (40, 41)
            Assert.Equal(map.ColorAt(new TileCoord(38, 41)), px[0]);
            Assert.Equal(map.ColorAt(bed), px[2]);
            Assert.Equal(map.ColorAt(new TileCoord(40, 40)), px[4 + 2]);

            // Кровать снесли — при новой записи чанка её на карте нет.
            world.SetBaseTile(bed, FloorId.Grass, ObjectId.Empty);
            map.MarkDirty(new ChunkCoord(1, 1));
            map.Update(world, 1);
            Assert.Empty(map.Beds());
        }
    }
}
