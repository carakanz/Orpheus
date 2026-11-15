using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-008 (окна как в Terraria): «куда идёт щелчок» — <see cref="UiPointer"/>, та же функция, что зовёт клиент
    /// (<c>GameUi.Frame</c>). С открытой сумкой щелчок по миру вне окон идёт в мир (копание / постановка тем же путём,
    /// что без сумки), по ячейке — в окно; с предметом «в руке курсора» щелчок в мир выбрасывает предмет. Нижняя панель,
    /// сферы и мини-карта — по настоящей раскладке клиента (<see cref="HudLayout"/>). Плюс кнопки сундука «Взять всё» /
    /// «Положить всё» — обычные заявки переноса через <see cref="BagHand"/> пачками по лимиту сервера.
    /// </summary>
    public class C008PointerTests
    {
        private const float W = 1920f, H = 1080f;

        // Холст 1920 × 1080: нижняя панель со сферами и мини-карта — настоящая раскладка клиента (HudLayout, её же зовут
        // BottomBar и MiniMapView); окна сумки и изготовления — прямоугольниками примерно как в клиенте (их раскладка
        // держится на Unity-группах и проверяется снимками).
        private static UiPointer OpenWindows()
        {
            var p = new UiPointer();
            p.Clear();
            p.Add(20f, H - 24f - 204f - 24f, 412f, 204f); // сетка сумки
            p.Add(20f, 30f, 48f, 360f);                   // столбик рецептов
            p.Add(76f, 200f, 64f, 64f);                   // выбранный рецепт
            p.Add(20f, 486f, 320f, 36f);                  // значки разделов
            HudLayout.AddHud(p, W, H, mapOpen: true);     // панель, сферы, мини-карта
            return p;
        }

        [Fact]
        public void C008_BagOpen_ClickOnWorld_GoesToWorld()
        {
            UiPointer p = OpenWindows();

            // Центр экрана и правая половина — мир: копание / постановка идут тем же путём, что при закрытых окнах.
            Assert.Equal(PointerRoute.World, p.RouteAt(960f, 540f, holding: false, captured: false));
            Assert.Equal(PointerRoute.World, p.RouteAt(1700f, 600f, holding: false, captured: false));
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
            (float sx, float sy) = HudLayout.SlotCenter(W, 0);
            Assert.Equal(PointerRoute.Ui, p.RouteAt(sx, sy, false, false));      // быстрая панель
            Assert.True(UiPointer.Blocks(p.RouteAt(40f, 1000f, false, false)));
        }

        [Fact]
        public void C008_BottomBar_Orbs_Map_AreInterface_ForWheelAndShift()
        {
            // Проверка C-008 п. 2: при открытой сумке ячейки 0…7 нижней панели — верхний ряд сумки; панель, сферы и
            // мини-карта — интерфейс (GameUi.OverWindow = курсор над набранными зонами): колесо над ними — не обзор,
            // Shift — не уворот. Раскладка — HudLayout, та же, что у клиента; холст и 1920 × 1080, и 1280 × 720.
            foreach ((float w, float h) in new[] { (1920f, 1080f), (1280f, 720f) })
            {
                var p = new UiPointer();
                HudLayout.AddHud(p, w, h, mapOpen: true);
                for (int slot = 0; slot < Inventory.Slots; slot++)
                {
                    (float x, float y) = HudLayout.SlotCenter(w, slot);
                    Assert.True(p.Over(x, y), $"ячейка {slot} при {w}");
                    Assert.True(p.Over(x - HudLayout.Cell / 2f + 1f, y + HudLayout.Cell / 2f - 1f)); // угол ячейки
                }

                (float hx, float hy) = HudLayout.OrbCenter(w, stamina: false);
                (float tx, float ty) = HudLayout.OrbCenter(w, stamina: true);
                Assert.True(p.Over(hx, hy));
                Assert.True(p.Over(tx, ty));
                Assert.True(p.Over(w - HudLayout.MapMargin - HudLayout.MapSize / 2f, h - HudLayout.MapMargin - HudLayout.MapSize / 2f));

                // Над панелью между сферами и центр экрана — мир; свёрнутая карта — тоже мир.
                (float mx, float my) = HudLayout.SlotCenter(w, 3);
                Assert.False(p.Over(mx, my + HudLayout.Cell * 2f));
                Assert.False(p.Over(w / 2f, h / 2f));
                var closed = new UiPointer();
                HudLayout.AddHud(closed, w, h, mapOpen: false);
                Assert.False(closed.Over(w - HudLayout.MapMargin - 10f, h - HudLayout.MapMargin - 10f));

                // Панель целиком на экране.
                Assert.True(HudLayout.BarX(w) > 0f && HudLayout.BarX(w) + HudLayout.BarWidth < w);
            }
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

        private static void PumpLocal(BagHand hand, LocalBag r)
        {
            for (int f = 0; f < 100 && hand.MovingAll; f++)
            {
                hand.Sync(r.Bag, false);
                hand.SyncChest(true, r.Chest);
                hand.Pump(f / 60.0);
            }

            Assert.False(hand.MovingAll);
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
            PumpLocal(hand, r);
            Assert.Equal(MoveAllStop.Done, hand.LastStop);
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
            PumpLocal(hand, r);
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

        private const int U = Torus.SubTilesPerTile;

        private static WorldPos Center(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private static (int Stone, int Wood, int Ore, int Fiber, int Torch) Mass(in Inventory a, in Inventory b) =>
            (a.Count(Items.Stone) + b.Count(Items.Stone), a.Count(Items.Wood) + b.Count(Items.Wood),
             a.Count(Items.CopperOre) + b.Count(Items.CopperOre), a.Count(Items.Fiber) + b.Count(Items.Fiber),
             a.Count(Items.Torch) + b.Count(Items.Torch));

        private static int CountCells(in Inventory inv)
        {
            int n = 0;
            for (int i = 0; i < Inventory.Cells; i++)
            {
                n += inv.ItemAt(i) != Items.None ? 1 : 0;
            }

            return n;
        }

        private static void RunUntilStopped(EditTests.EditRig rig, BagHand hand, int maxTicks)
        {
            for (int t = 0; t < maxTicks && hand.MovingAll; t++)
            {
                rig.Run(1);
            }

            Assert.False(hand.MovingAll);
        }

        /// <summary>
        /// Проверка C-008 п. 1, по НАСТОЯЩЕМУ серверу: полный сундук (30 ячеек) → «Взять всё» в сумку с местом — перенесено
        /// всё (сервер берёт 8 заявок игрока за такт; рука шлёт пачками и ждёт ответа), масса сходится, отказов нет;
        /// обратно «Положить всё» — сумка (кроме панели) пуста. Потом: места мало — стоп «нет места»; пауза сервера
        /// (заявки отклоняются молча) — после повторов стоп «сервер не принял»; ничего не теряется.
        /// </summary>
        [Fact]
        public void C008_Net_FullChest_TakeAll_PutAll_InBatches()
        {
            var chestTile = new TileCoord(301, 201);
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            world.SetBaseTile(chestTile, FloorId.Grass, ObjectId.Chest);
            using var rig = new EditTests.EditRig(world, i => Center(300 + 3 * i, 200));
            NetClient a = rig.Join();
            rig.Run(10);

            var full = new Inventory();
            byte[] kinds = { Items.Stone, Items.Wood, Items.CopperOre, Items.Fiber };
            for (int i = 0; i < Simulation.ChestCells; i++)
            {
                full.SetCell(i, kinds[i % kinds.Length], 3 + i % 5);
            }

            var bag = new Inventory();
            for (int i = 0; i < Inventory.Slots; i++)
            {
                bag.SetCell(i, Items.Torch, 1); // панель занята — «Взять всё» кладёт в 8…39, «Положить всё» панель не трогает
            }
            Assert.True(rig.Sim.SetInventory(a.EntityId, bag));
            Assert.True(rig.Sim.SetChest(chestTile, full));
            Assert.True(a.RequestUse(chestTile));
            rig.Run(4);
            Assert.True(a.ChestOpen);
            Assert.Equal(full, a.Chest);

            var hand = new BagHand(a);
            rig.AfterClientPoll = c =>
            {
                hand.Sync(c.Inv, false); // кадр окна: свежая сумка и сундук сервера, потом пачка
                hand.SyncChest(c.ChestOpen, c.Chest);
                hand.Pump(rig.NowMicros / 1e6);
            };
            rig.Run(1);

            var mass = Mass(a.Inv, a.Chest);
            long refused = rig.Server.BagRequestsRefused;
            Assert.Equal(BagHand.MoveAllBatch, hand.TakeAll()); // первая пачка — не больше 8
            RunUntilStopped(rig, hand, 60);
            Assert.Equal(MoveAllStop.Done, hand.LastStop);
            for (int i = 0; i < Simulation.ChestCells; i++)
            {
                Assert.Equal(Items.None, a.Chest.ItemAt(i));
            }

            Assert.Equal(mass, Mass(a.Inv, a.Chest));
            Assert.Equal(a.Inv, rig.Sim.GetEntity(a.EntityId).Inv);
            Assert.Equal(refused, rig.Server.BagRequestsRefused); // ни одного отказа по лимиту
            Assert.Equal(Inventory.Slots, a.Inv.Count(Items.Torch));

            // Обратно: «Положить всё» — всё, кроме панели.
            Assert.True(hand.PutAll() > 0);
            RunUntilStopped(rig, hand, 60);
            Assert.Equal(MoveAllStop.Done, hand.LastStop);
            for (int i = Inventory.Slots; i < Inventory.Cells; i++)
            {
                Assert.Equal(Items.None, a.Inv.ItemAt(i));
            }

            Assert.Equal(mass, Mass(a.Inv, a.Chest));
            Assert.Equal(Inventory.Slots, a.Inv.Count(Items.Torch));
            Assert.Equal(refused, rig.Server.BagRequestsRefused);

            // Места мало: сумка забита факелами по одному, кроме двух ячеек — переносится, сколько влезло, стоп «нет места».
            var crowded = new Inventory();
            for (int i = 0; i < Inventory.Cells - 2; i++)
            {
                crowded.SetCell(i, Items.Torch, 1);
            }

            Assert.True(rig.Sim.SetInventory(a.EntityId, crowded));
            Assert.True(rig.Sim.SetChest(chestTile, full));
            rig.Run(4);
            mass = Mass(a.Inv, a.Chest);
            hand.TakeAll();
            RunUntilStopped(rig, hand, 60);
            Assert.Equal(MoveAllStop.NoRoom, hand.LastStop);
            Assert.Equal(mass, Mass(a.Inv, a.Chest));
            Assert.Equal(Inventory.Cells, CountCells(a.Inv));

            // Пауза сервера: заявки отклоняются молча — после повторов стоп «сервер не принял», ничего не потеряно.
            Assert.True(rig.Sim.SetInventory(a.EntityId, new Inventory()));
            rig.Run(4);
            mass = Mass(a.Inv, a.Chest);
            rig.Server.SetPaused(true);
            Assert.True(hand.TakeAll() > 0);
            RunUntilStopped(rig, hand, 120);
            rig.Server.SetPaused(false);
            Assert.Equal(MoveAllStop.NotAccepted, hand.LastStop);
            Assert.Equal(mass, Mass(a.Inv, a.Chest));
        }
    }
}
