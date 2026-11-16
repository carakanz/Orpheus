using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-2 (шаг 2.2): «рука» окна сумки (<see cref="BagHand"/>) — общий путь заявок окна игрока и ботов. Тесты
    /// щёлкают по ячейкам так же, как окно (ЛКМ, ПКМ, Shift+ЛКМ, мимо окна), заявки идут по сети настоящему серверу.
    /// </summary>
    public class BagHandTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat() => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);

        private static WorldPos Center(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private static void Give(EditTests.EditRig rig, NetClient c, int cell, byte item, int count)
        {
            Inventory bag = rig.Sim.GetEntity(c.EntityId).Inv;
            bag.SetCell(cell, item, count);
            rig.Sim.SetInventory(c.EntityId, bag);
            rig.Run(3);
        }

        /// <summary>Кадр окна: свежая сумка сервера в руку, как делает окно перед щелчками.</summary>
        private static void Sync(BagHand hand, NetClient c, bool dead = false) => hand.Sync(c.Inv, dead);

        [Fact]
        public void E22_Net_Hand_Move_Split_Swap_Drop_LikeWindow()
        {
            using var rig = new EditTests.EditRig(Flat(), i => Center(300 + 3 * i, 200));
            NetClient a = rig.Join();
            NetClient b = rig.Join();
            rig.Run(10);
            Give(rig, a, 10, Items.Stone, 40);
            var hand = new BagHand(a);

            // ЛКМ: взять стопку — заявки нет, ячейка «пустеет» только на экране.
            Sync(hand, a);
            Assert.Equal(BagClick.Taken, hand.Left(10));
            Assert.True(hand.Holding);
            Assert.Equal(40, hand.Count);
            Assert.Equal(0, hand.ShownCount(10));
            Assert.Equal(0, rig.Server.BagRequestsReceived);

            // ЛКМ в пустую ячейку: перенос.
            Assert.Equal(BagClick.Moved, hand.Left(20));
            Assert.False(hand.Holding);
            Assert.Equal(40, a.Inv.CountIn(10)); // не предсказывает — ждёт сервер
            rig.Run(4);
            Assert.Equal(Items.None, a.Inv.ItemAt(10));
            Assert.Equal(40, a.Inv.CountIn(20));

            // ПКМ: половина; ЛКМ в пустую — деление.
            Sync(hand, a);
            Assert.Equal(BagClick.Taken, hand.Half(20));
            Assert.Equal(20, hand.Count);
            Assert.Equal(20, hand.ShownCount(20));
            Assert.Equal(BagClick.Split, hand.Left(21));
            rig.Run(4);
            Assert.Equal(20, a.Inv.CountIn(20));
            Assert.Equal(20, a.Inv.CountIn(21));

            // Половина нечётной стопки — с округлением вверх; доукладка в стопку того же предмета.
            Give(rig, a, 22, Items.Stone, 5);
            Sync(hand, a);
            Assert.Equal(BagClick.Taken, hand.Half(22));
            Assert.Equal(3, hand.Count);
            Assert.Equal(BagClick.Split, hand.Left(21));
            rig.Run(4);
            Assert.Equal(23, a.Inv.CountIn(21));
            Assert.Equal(2, a.Inv.CountIn(22));

            // Обмен с другим предметом (кирка в ячейке 0 стартового набора).
            byte first = a.Inv.ItemAt(0);
            Assert.NotEqual(Items.None, first);
            Sync(hand, a);
            hand.Left(20);
            Assert.Equal(BagClick.Moved, hand.Left(0));
            rig.Run(4);
            Assert.Equal(Items.Stone, a.Inv.ItemAt(0));
            Assert.Equal(first, a.Inv.ItemAt(20));

            // Мимо окна с половиной в руке — выброс части; оба клиента видят кучу.
            Sync(hand, a);
            Assert.Equal(BagClick.None, hand.Outside()); // пустой рукой — ничего
            hand.Half(21);
            Assert.Equal(12, hand.Count);
            Assert.Equal(BagClick.Dropped, hand.Outside());
            rig.Run(4);
            Assert.Equal(11, a.Inv.CountIn(21));
            Assert.Equal(12, Assert.Single(a.Piles).Count);
            Assert.Equal(12, Assert.Single(b.Piles).Count);

            Assert.Equal(a.Inv, rig.Sim.GetEntity(a.EntityId).Inv);
            Assert.Equal(hand.RequestsSent, rig.Server.BagRequestsReceived);
            Assert.Equal(0, rig.Server.BagRequestsRefused);
            Assert.Equal(0, a.BadMessages + b.BadMessages);
        }

        [Fact]
        public void C007_Hand_RightTakesOne_RepeatAddsOne_ShiftHalf()
        {
            // Слово Автора 15.11: ПКМ берёт 1 (ещё ПКМ по той же ячейке — ещё 1), Shift+ЛКМ — половину.
            using var rig = new EditTests.EditRig(Flat(), i => Center(300 + 3 * i, 200));
            NetClient a = rig.Join();
            rig.Run(10);
            Give(rig, a, 10, Items.Stone, 3);
            var hand = new BagHand(a);
            Sync(hand, a);

            Assert.Equal(BagClick.Taken, hand.Right(10));
            Assert.Equal(1, hand.Count);
            Assert.Equal(BagClick.Taken, hand.Right(10));
            Assert.Equal(2, hand.Count);
            Assert.Equal(BagClick.Taken, hand.Right(10));
            Assert.Equal(3, hand.Count);
            Assert.Equal(BagClick.None, hand.Right(10)); // вся стопка уже в руке
            Assert.Equal(3, hand.Count);
            Assert.Equal(BagClick.Moved, hand.Left(20)); // в руке вся стопка — это перенос
            rig.Run(4);
            Assert.Equal(3, a.Inv.CountIn(20) + a.Inv.CountIn(10));

            Give(rig, a, 30, Items.Wood, 7);
            Sync(hand, a);
            Assert.Equal(BagClick.Taken, hand.Half(30));
            Assert.Equal(4, hand.Count); // половина нечётной — вверх
            Assert.Equal(BagClick.None, hand.Half(31)); // с предметом в руке Shift+ЛКМ половину не берёт
            Assert.Equal(BagClick.Cancelled, hand.Right(31)); // ПКМ по другой ячейке — вернуть
            Assert.False(hand.Holding);
        }

        [Fact]
        public void E22_Net_Hand_Quick_BetweenBarAndBag()
        {
            using var rig = new EditTests.EditRig(Flat(), i => Center(300 + 3 * i, 200));
            NetClient a = rig.Join();
            rig.Run(10);
            var empty = new Inventory();
            rig.Sim.SetInventory(a.EntityId, empty);
            Give(rig, a, 12, Items.Wood, 30);
            Give(rig, a, 2, Items.Wood, 90);
            Give(rig, a, 15, Items.Stone, 7);
            var hand = new BagHand(a);

            // Из сумки в панель: сначала в стопку того же предмета (доукладка 9, остаток 21 остаётся).
            Sync(hand, a);
            Assert.Equal(2, BagHand.QuickTarget(a.Inv, 12));
            Assert.Equal(BagClick.Moved, hand.Quick(12));
            rig.Run(4);
            Assert.Equal(99, a.Inv.CountIn(2));
            Assert.Equal(21, a.Inv.CountIn(12));

            // Стопка панели полна — следующая заявка идёт в первую пустую ячейку панели.
            Sync(hand, a);
            Assert.Equal(BagClick.Moved, hand.Quick(12));
            rig.Run(4);
            Assert.Equal(21, a.Inv.CountIn(0));
            Assert.Equal(Items.None, a.Inv.ItemAt(12));

            // Из панели в сумку: первая пустая ячейка сумки (8).
            Sync(hand, a);
            Assert.Equal(BagClick.Moved, hand.Quick(0));
            rig.Run(4);
            Assert.Equal(21, a.Inv.CountIn(8));
            Sync(hand, a);
            Assert.Equal(BagClick.None, hand.Quick(0)); // пустая ячейка
            Assert.Equal(a.Inv, rig.Sim.GetEntity(a.EntityId).Inv);
        }

        [Fact]
        public void E22_Hand_FollowsServerBag_Cancel_Dead_NoRoom()
        {
            using var rig = new EditTests.EditRig(Flat(), i => Center(300 + 3 * i, 200));
            NetClient a = rig.Join();
            rig.Run(10);
            Give(rig, a, 10, Items.Stone, 40);
            Give(rig, a, 11, Items.Stone, 95);
            Give(rig, a, 12, Items.Wood, 5);
            var hand = new BagHand(a);
            Sync(hand, a);

            // Положить обратно и ПКМ с предметом — отмена без заявок.
            hand.Left(10);
            Assert.Equal(BagClick.Cancelled, hand.Left(10));
            hand.Left(10);
            Assert.Equal(BagClick.Cancelled, hand.Right(30));
            Assert.False(hand.Holding);

            // Часть стопки на чужой предмет — нельзя, рука остаётся; в почти полную стопку — сколько влезает.
            hand.Half(10);
            Assert.Equal(BagClick.None, hand.Left(12));
            Assert.True(hand.Holding);
            Assert.Equal(BagClick.Split, hand.Left(11));
            rig.Run(4);
            Assert.Equal(99, a.Inv.CountIn(11));
            Assert.Equal(36, a.Inv.CountIn(10));

            // Сервер изменил взятую стопку: стало меньше — рука урезается; предмет исчез — отмена.
            Sync(hand, a);
            hand.Left(10);
            Give(rig, a, 10, Items.Stone, 9);
            Sync(hand, a);
            Assert.Equal(9, hand.Count);
            Give(rig, a, 10, Items.None, 0);
            Sync(hand, a);
            Assert.False(hand.Holding);

            // Мёртвому — ни взять, ни заявок.
            long before = rig.Server.BagRequestsReceived;
            Sync(hand, a, dead: true);
            Assert.Equal(BagClick.None, hand.Left(11));
            Assert.Equal(BagClick.None, hand.Quick(11));
            Assert.Equal(BagClick.None, hand.Outside());
            rig.Run(3);
            Assert.Equal(before, rig.Server.BagRequestsReceived);
            Assert.Equal(0, rig.Server.BagRequestsRefused);
        }
    }
}
