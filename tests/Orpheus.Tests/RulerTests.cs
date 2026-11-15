using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-13 — линейка (слова Автора: «Линейка будет стены укладывать линией, пол — квадратом»; решение 15.11: режим, по
    /// умолчанию включён). <see cref="BuildRuler"/> по настоящему серверу: линия стен ставит N ячеек и снимает ровно N
    /// предметов, при нехватке — сколько хватает по порядку от A; прямоугольник пола W × H; клетки «нельзя» пропускаются;
    /// через шов мира; без предметов и после отмены — ничего; поштучный режим — одна. Предсказание клиента = мир сервера.
    /// </summary>
    public class RulerTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const int Slot = 3;

        private static WorldPos At(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        private sealed class Rig : System.IDisposable
        {
            public readonly EditTests.EditRig Edit;
            public readonly NetClient C;
            public readonly BuildHand Hand;
            public readonly BuildRuler Ruler = new BuildRuler();
            public readonly List<TileReplyMessage> Replies = new List<TileReplyMessage>();

            public Rig(WorldModel world, int x, int y, byte item, int count)
            {
                Edit = new EditTests.EditRig(world, _ => At(x, y));
                C = Edit.Join();
                C.EditReplied = Replies.Add;
                Edit.Run(5);
                var inv = Edit.Sim.GetEntity(C.EntityId).Inv;
                if (count > 0)
                {
                    inv.SetCell(Slot, item, count);
                }

                Assert.True(Edit.Sim.SetInventory(C.EntityId, inv));
                Edit.Run(3);
                Hand = new BuildHand(C);
            }

            public WorldPos Self => Edit.Sim.GetEntity(C.EntityId).Mover.Position;

            public int Bag => Edit.Sim.GetEntity(C.EntityId).Inv.CountIn(Slot);

            public void Drag(TileCoord a, int aq, TileCoord b, int bq, bool diagonal = false)
            {
                Ruler.Begin(a, aq);
                Ruler.Update(C.World!.World, Self, C.Inv, Slot, true, b, bq, diagonal);
            }

            public int Release()
            {
                int sent = Ruler.Release(Hand, Self, C.Inv, Slot);
                Edit.Run(6);
                return sent;
            }

            public void Dispose() => Edit.Dispose();
        }

        private static WorldModel Flat() => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1313);

        // Ячеек стен (камень) в тайлах ряда y от x0 до x1 (с обёрткой) — у сервера; клиент обязан видеть то же.
        private static int WallCells(Rig r, int x0, int x1, int y)
        {
            int n = 0;
            for (int x = x0; x <= x1; x++)
            {
                var t = Torus.Wrap(r.Edit.Sim.Shape, new TileCoord(x, y));
                ushort obj = r.Edit.Sim.World.GetObject(t);
                Assert.Equal(obj, r.C.World!.World.GetObject(t));
                Assert.Equal(BuildCells.MaskAt(r.Edit.Sim.World, t), BuildCells.MaskAt(r.C.World.World, t));
                if (obj == ObjectId.StoneWall)
                {
                    n += BuildCells.Count(BuildCells.MaskAt(r.Edit.Sim.World, t));
                }
            }

            return n;
        }

        [Fact]
        public void E213_WallLine_PlacesN_TakesExactlyN_AxisByLargerOffset()
        {
            using var r = new Rig(Flat(), 300, 200, Items.StoneWall, 30);
            // A — нижняя левая ячейка тайла 298, B — нижняя правая тайла 303 рядом ниже (смещение по Y меньше — ось X).
            r.Drag(new TileCoord(298, 203), 2, new TileCoord(303, 204), 3);
            Assert.Equal(RulerKind.Line, r.Ruler.Kind);
            Assert.Equal(12, r.Ruler.LengthCells);
            Assert.Equal(12, r.Ruler.Need);
            Assert.Equal(30, r.Ruler.Have);
            Assert.Equal(1, r.Release());
            Assert.Equal(12, WallCells(r, 296, 306, 203));
            Assert.Equal(0, WallCells(r, 296, 306, 204));
            Assert.Equal(30 - 12, r.Bag);
            Assert.Equal(30 - 12, r.C.Inv.CountIn(Slot));
            Assert.Equal(BuildCells.Bit(0, 1) | BuildCells.Bit(1, 1), BuildCells.MaskAt(r.Edit.Sim.World, new TileCoord(300, 203)));
        }

        [Fact]
        public void E213_WallLine_ShortOfItems_PlacesWhatFits_FromA()
        {
            using var r = new Rig(Flat(), 300, 200, Items.StoneWall, 5);
            // Справа налево: A — тайл 303, B — тайл 298.
            r.Drag(new TileCoord(303, 203), 3, new TileCoord(298, 203), 2);
            Assert.Equal(12, r.Ruler.Need);
            Assert.Equal(5, r.Ruler.Have);
            int shortSpots = 0;
            foreach (RulerSpot s in r.Ruler.Spots)
            {
                shortSpots += s.Short ? 1 : 0;
            }

            Assert.Equal(7, shortSpots);
            r.Release();
            Assert.Equal(5, WallCells(r, 296, 306, 203));
            Assert.Equal(0, r.Bag);
            Assert.Equal(BuildCells.Bit(0, 1) | BuildCells.Bit(1, 1), BuildCells.MaskAt(r.Edit.Sim.World, new TileCoord(303, 203)));
            Assert.Equal(BuildCells.Bit(1, 1), BuildCells.MaskAt(r.Edit.Sim.World, new TileCoord(301, 203)));
            Assert.Equal(ObjectId.Empty, r.Edit.Sim.World.GetObject(new TileCoord(300, 203)));
        }

        [Fact]
        public void E213_FloorRect_WxH_TakesExactly()
        {
            using var r = new Rig(Flat(), 300, 200, Items.WoodFloor, 20);
            r.Drag(new TileCoord(302, 199), 0, new TileCoord(299, 201), 0); // 4 × 3, к западу и к югу от A
            Assert.Equal(RulerKind.Rect, r.Ruler.Kind);
            Assert.Equal(4, r.Ruler.Width);
            Assert.Equal(3, r.Ruler.Height);
            Assert.Equal(12, r.Ruler.Need);
            Assert.Equal(3, r.Release());
            for (int y = 198; y <= 202; y++)
            {
                for (int x = 297; x <= 304; x++)
                {
                    var t = new TileCoord(x, y);
                    bool inside = x >= 299 && x <= 302 && y >= 199 && y <= 201;
                    Assert.Equal(inside ? FloorId.WoodFloor : FloorId.Grass, r.Edit.Sim.World.GetFloor(t));
                    Assert.Equal(r.Edit.Sim.World.GetFloor(t), r.C.World!.World.GetFloor(t));
                }
            }

            Assert.Equal(20 - 12, r.Bag);
        }

        [Fact]
        public void E213_ForbiddenCells_Skipped_RestPlaced()
        {
            WorldModel world = Flat();
            world.SetBaseTile(new TileCoord(300, 203), FloorId.Grass, ObjectId.Rock);
            using var r = new Rig(world, 300, 200, Items.StoneWall, 30);
            r.Drag(new TileCoord(298, 203), 2, new TileCoord(303, 203), 3);
            int red = 0;
            foreach (RulerSpot s in r.Ruler.Spots)
            {
                red += s.Ok ? 0 : 1;
            }

            Assert.Equal(2, red);
            Assert.Equal(10, r.Ruler.Need);
            r.Release();
            Assert.Equal(ObjectId.Rock, r.Edit.Sim.World.GetObject(new TileCoord(300, 203)));
            Assert.Equal(10, WallCells(r, 296, 306, 203));
            Assert.Equal(ObjectId.StoneWall, r.Edit.Sim.World.GetObject(new TileCoord(303, 203)));
            Assert.Equal(30 - 10, r.Bag);
        }

        [Fact]
        public void E213_AcrossSeam_Line_And_Rect()
        {
            using var r = new Rig(Flat(), 1023, 200, Items.StoneWall, 30);
            r.Drag(new TileCoord(1020, 203), 2, new TileCoord(2, 203), 3); // 1020…1023, 0…2 — 7 тайлов, 14 ячеек
            Assert.Equal(14, r.Ruler.LengthCells);
            Assert.Equal(14, r.Ruler.Need);
            r.Release();
            Assert.Equal(14, WallCells(r, 1020, 1026, 203));
            Assert.Equal(30 - 14, r.Bag);

            var inv = r.Edit.Sim.GetEntity(r.C.EntityId).Inv;
            inv.SetCell(Slot, Items.WoodFloor, 10);
            Assert.True(r.Edit.Sim.SetInventory(r.C.EntityId, inv));
            r.Edit.Run(3);
            r.Drag(new TileCoord(1022, 198), 0, new TileCoord(1, 199), 0); // 4 × 2 через шов
            Assert.Equal(8, r.Ruler.Need);
            r.Release();
            foreach (int x in new[] { 1022, 1023, 0, 1 })
            {
                Assert.Equal(FloorId.WoodFloor, r.Edit.Sim.World.GetFloor(new TileCoord(x, 198)));
                Assert.Equal(FloorId.WoodFloor, r.Edit.Sim.World.GetFloor(new TileCoord(x, 199)));
            }

            Assert.Equal(2, r.Bag);
        }

        [Fact]
        public void E213_NoItems_Nothing()
        {
            using var r = new Rig(Flat(), 300, 200, Items.StoneWall, 0);
            r.Drag(new TileCoord(298, 203), 2, new TileCoord(303, 203), 3);
            Assert.Equal(0, r.Ruler.Need);
            Assert.Equal(0, r.Ruler.Have);
            Assert.Equal(0, r.Release());
            Assert.Equal(0, WallCells(r, 296, 306, 203));
            Assert.Empty(r.Replies);
        }

        [Fact]
        public void E213_Cancel_NothingPlaced()
        {
            using var r = new Rig(Flat(), 300, 200, Items.StoneWall, 30);
            r.Drag(new TileCoord(298, 203), 2, new TileCoord(303, 203), 3);
            Assert.True(r.Ruler.Dragging);
            Assert.Equal(12, r.Ruler.Need);
            r.Ruler.Cancel();
            Assert.False(r.Ruler.Dragging);
            Assert.Equal(0, r.Release());
            Assert.Equal(0, WallCells(r, 296, 306, 203));
            Assert.Equal(30, r.Bag);
            Assert.Empty(r.Replies);
        }

        [Fact]
        public void E213_Mode_DefaultRuler_Toggle_SingleClickPlacesOne()
        {
            var ruler = new BuildRuler();
            Assert.Equal(PlaceMode.Ruler, ruler.Mode);
            Assert.Equal(PlaceMode.Single, ruler.Toggle());
            Assert.Equal(PlaceMode.Ruler, ruler.Toggle());
            Assert.Equal(RulerKind.Single, BuildRuler.KindOfItem(Items.Door));
            Assert.Equal(RulerKind.Single, BuildRuler.KindOfItem(Items.Torch));
            Assert.Equal(RulerKind.Line, BuildRuler.KindOfItem(Items.WoodFence));
            Assert.Equal(RulerKind.Rect, BuildRuler.KindOfItem(Items.WoodFloor));

            using var r = new Rig(Flat(), 300, 200, Items.StoneWall, 30);
            r.Ruler.Toggle();
            Assert.True(r.Hand.Click(r.C.World!.World, r.Self, r.C.Inv, Slot, true, new TileCoord(301, 203), 2).Ok);
            r.Edit.Run(6);
            Assert.Equal(1, WallCells(r, 296, 306, 203));
            Assert.Equal(29, r.Bag);

            // Линейка без протягивания (A = B) — тоже одна.
            r.Drag(new TileCoord(302, 203), 2, new TileCoord(302, 203), 2);
            Assert.Equal(1, r.Ruler.Need);
            r.Release();
            Assert.Equal(2, WallCells(r, 296, 306, 203));
            Assert.Equal(28, r.Bag);
        }

        [Fact]
        public void E213_Diagonal_Staircase_And_LongLine_OutOfReachSkipped()
        {
            using var r = new Rig(Flat(), 300, 200, Items.StoneWall, 99);
            // Shift: прямая лесенкой 8 ячеек по диагонали.
            r.Drag(new TileCoord(298, 197), 0, new TileCoord(301, 200), 3, diagonal: true);
            Assert.Equal(8, r.Ruler.LengthCells);
            int need = r.Ruler.Need;
            Assert.Equal(8, need);
            r.Release();
            int placed = 0;
            for (int y = 196; y <= 201; y++)
            {
                placed += WallCells(r, 296, 303, y);
            }

            Assert.Equal(8, placed);
            Assert.Equal(99 - 8, r.Bag);

            // 40 ячеек по оси — два отрезка; дальше радиуса постановки (6 тайлов) — красные и пропускаются.
            r.Drag(new TileCoord(290, 205), 2, new TileCoord(309, 205), 3);
            Assert.Equal(40, r.Ruler.LengthCells);
            Assert.Equal(2, r.Ruler.Segments);
            Assert.Equal(26, r.Ruler.Need); // тайлы 294…306
            Assert.Equal(2, r.Release());
            Assert.Equal(26, WallCells(r, 288, 311, 205));
            Assert.Equal(99 - 8 - 26, r.Bag);
        }
    }
}
