using Orpheus.Core.Sim;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-008 (окна как в Terraria): «куда идёт щелчок» — <see cref="UiPointer"/>, та же функция, что зовёт клиент
    /// (<c>GameUi.Frame</c>). С открытой сумкой щелчок по миру вне окон идёт в мир (копание / постановка тем же путём,
    /// что без сумки), по ячейке — в окно; с предметом «в руке курсора» щелчок в мир выбрасывает предмет. Плюс кнопки
    /// сундука «Взять всё» / «Положить всё» — пачка обычных заявок переноса через <see cref="BagHand"/>.
    /// </summary>
    public class C008PointerTests
    {
        // Раскладка холста 1920 × 1080, как в клиенте: сумка 8 × 4 ячейки 48 в левом верхнем углу, столбик изготовления
        // в левом нижнем, панель со сферами внизу по центру.
        private static UiPointer OpenWindows()
        {
            var p = new UiPointer();
            p.Clear();
            p.Add(20f, 1080f - 24f - 204f - 24f, 412f, 204f); // сетка сумки
            p.Add(20f, 30f, 48f, 360f);                       // столбик рецептов
            p.Add(76f, 200f, 64f, 64f);                       // выбранный рецепт
            p.Add(20f, 486f, 320f, 36f);                      // значки разделов
            p.Add(640f, 6f, 640f, 70f);                       // панель со сферами
            return p;
        }

        [Fact]
        public void C008_BagOpen_ClickOnWorld_GoesToWorld()
        {
            UiPointer p = OpenWindows();

            // Центр экрана и правая половина — мир: копание / постановка идут тем же путём, что при закрытых окнах.
            Assert.Equal(PointerRoute.World, p.RouteAt(960f, 540f, holding: false, captured: false));
            Assert.Equal(PointerRoute.World, p.RouteAt(1700f, 900f, holding: false, captured: false));
            Assert.False(UiPointer.Blocks(p.RouteAt(960f, 540f, false, false)));

            // Рядом с окнами, но не над ячейкой: справа от сумки и над пустым местом справа от столбика — тоже мир.
            Assert.Equal(PointerRoute.World, p.RouteAt(450f, 950f, false, false));
            Assert.Equal(PointerRoute.World, p.RouteAt(300f, 100f, false, false));
        }

        [Fact]
        public void C008_BagOpen_ClickOnCell_GoesToWindow()
        {
            UiPointer p = OpenWindows();
            Assert.Equal(PointerRoute.Ui, p.RouteAt(40f, 1000f, false, false));  // ячейка сумки
            Assert.Equal(PointerRoute.Ui, p.RouteAt(44f, 100f, false, false));   // значок рецепта
            Assert.Equal(PointerRoute.Ui, p.RouteAt(100f, 230f, false, false));  // выбранный рецепт
            Assert.Equal(PointerRoute.Ui, p.RouteAt(960f, 40f, false, false));   // быстрая панель
            Assert.True(UiPointer.Blocks(p.RouteAt(40f, 1000f, false, false)));
        }

        [Fact]
        public void C008_HeldItem_OverWorld_Drops_AndCaptureKeepsUi()
        {
            UiPointer p = OpenWindows();

            // Предмет «в руке курсора» (взят из ячейки): щелчок в мир — выбросить (допущение C-008, как в Terraria), не копать.
            Assert.Equal(PointerRoute.Drop, p.RouteAt(960f, 540f, holding: true, captured: false));
            Assert.True(UiPointer.Blocks(PointerRoute.Drop));
            Assert.Equal(PointerRoute.Ui, p.RouteAt(40f, 1000f, holding: true, captured: false)); // в ячейку — положить

            // ЛКМ нажата на ячейке и тянется в мир — пока не отпущена, мир её не получает.
            Assert.Equal(PointerRoute.Ui, p.RouteAt(960f, 540f, holding: false, captured: true));
        }

        [Fact]
        public void C008_NoZones_EverythingIsWorld()
        {
            var p = new UiPointer();
            p.Add(0f, 0f, 10f, 10f);
            p.Clear();
            Assert.Equal(0, p.Count);
            Assert.Equal(PointerRoute.World, p.RouteAt(5f, 5f, false, false));
        }

        private sealed class LocalBag : IBagRequests
        {
            public Inventory Bag;
            public Inventory Chest;
            public int Moves;

            public bool RequestBagMove(int from, int to)
            {
                Moves++;
                bool fc = from >= BagHand.ChestBase, tc = to >= BagHand.ChestBase;
                int f = fc ? from - BagHand.ChestBase : from, t = tc ? to - BagHand.ChestBase : to;
                if (fc && tc)
                {
                    return Chest.Move(f, t);
                }

                // Те же функции ядра, что у сервера (Simulation.ApplyChestOp).
                return fc ? Inventory.Move(ref Chest, f, ref Bag, t) : tc ? Inventory.Move(ref Bag, f, ref Chest, t) : Bag.Move(f, t);
            }

            public bool RequestBagSplit(int from, int to, int count) => false;

            public bool RequestBagDrop(int cell, int count = 0) => false;
        }

        [Fact]
        public void C008_Chest_TakeAll_And_PutAll_ByOrdinaryMoves()
        {
            var r = new LocalBag();
            r.Bag.SetCell(0, Items.Torch, 10);     // быстрая панель — «Положить всё» её не трогает
            r.Bag.SetCell(8, Items.Stone, 30);
            r.Bag.SetCell(9, Items.Wood, 5);
            r.Chest.SetCell(0, Items.Wood, 20);
            r.Chest.SetCell(3, Items.CopperOre, 7);
            r.Chest.SetCell(29, Items.Stone, 4);
            var hand = new BagHand(r);

            int wood = r.Bag.Count(Items.Wood) + r.Chest.Count(Items.Wood);
            hand.Sync(r.Bag, false);
            hand.SyncChest(true, r.Chest);
            Assert.True(hand.TakeAll() > 0);
            for (int i = 0; i < Simulation.ChestCells; i++)
            {
                Assert.Equal(Items.None, r.Chest.ItemAt(i));
            }

            Assert.Equal(wood, r.Bag.Count(Items.Wood));
            Assert.Equal(7, r.Bag.Count(Items.CopperOre));
            Assert.Equal(34, r.Bag.Count(Items.Stone));

            hand.Sync(r.Bag, false);
            hand.SyncChest(true, r.Chest);
            Assert.True(hand.PutAll() > 0);
            Assert.Equal(10, r.Bag.CountIn(0)); // панель на месте
            for (int i = Inventory.Slots; i < Inventory.Cells; i++)
            {
                Assert.Equal(Items.None, r.Bag.ItemAt(i));
            }

            Assert.Equal(wood, r.Chest.Count(Items.Wood));
            Assert.Equal(34, r.Chest.Count(Items.Stone));

            // Сундук закрыт или в руке предмет — заявок нет.
            hand.SyncChest(false, r.Chest);
            Assert.Equal(0, hand.TakeAll());
        }
    }
}
