using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Сумка, быстрая панель и предмет в руках (карточка 1-002; `06` 0 и 2.3, `02` 1.1 «Быстрая панель»):
    /// таблица предметов данными, стартовый набор, счётчики материалов, смена ячейки кадром ввода —
    /// в том же такте на сервере и в том же кадре у клиента (предсказание), сумка в хэше состояния
    /// и в записи персонажа (ушёл — осталась за ключом, вернулся — та же).
    /// </summary>
    public class InventoryTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const long TickMicros = 1_000_000 / Simulation.TickRate;

        private static WorldModel Flat() => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);

        private static WorldPos Center(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        // ---------- Таблица предметов ----------

        /// <summary>
        /// Предметы данными (`06` 2.3): вид, ярус, копать умеет только кирка, длина для досягаемости
        /// «персонаж + предмет» (<see cref="Reach.Of"/>).
        /// </summary>
        [Fact]
        public void D06_Items_Table_Kinds_Tiers_Reach()
        {
            Assert.False(Items.Exists(Items.None));
            Assert.True(Items.Exists(Items.StonePickaxe));
            Assert.False(Items.Exists((byte)Items.Count)); // вне таблицы

            Assert.Equal(Items.KindTool, Items.KindOf(Items.StonePickaxe));
            Assert.Equal(Items.KindTool, Items.KindOf(Items.StoneAxe));
            Assert.Equal(Items.KindHammer, Items.KindOf(Items.Hammer));
            Assert.Equal(Items.KindMelee, Items.KindOf(Items.WoodenSword));
            Assert.Equal(Items.KindRanged, Items.KindOf(Items.SimpleBow));
            Assert.Equal(Items.KindConsumable, Items.KindOf(Items.Torch));
            Assert.Equal(Items.KindNone, Items.KindOf(Items.None));

            Assert.Equal(Items.ToolPick, Items.ToolKindOf(Items.StonePickaxe));
            Assert.Equal(Items.ToolAxe, Items.ToolKindOf(Items.StoneAxe));
            Assert.Equal(Items.ToolNone, Items.ToolKindOf(Items.Hammer));
            Assert.Equal(Items.TierStone, Items.TierOf(Items.StonePickaxe));
            Assert.Equal(Items.TierStone, Items.TierOf(Items.StoneAxe));
            Assert.Equal(Items.TierNone, Items.TierOf(Items.Hammer));

            // Копать — только киркой; рубить — топором (`06` 2.3: руками не умеют ни того, ни другого).
            Assert.True(Items.IsDiggingTool(Items.StonePickaxe));
            Assert.False(Items.IsDiggingTool(Items.StoneAxe));
            Assert.False(Items.IsDiggingTool(Items.Hammer));
            Assert.False(Items.IsDiggingTool(Items.WoodenSword));
            Assert.False(Items.IsDiggingTool(Items.None));
            Assert.True(Items.IsChoppingTool(Items.StoneAxe));
            Assert.False(Items.IsChoppingTool(Items.StonePickaxe));

            // Длины: кирка 0,75 т, меч 1,0 т; досягаемость = тело + предмет (0-034).
            Assert.Equal(Reach.PickaxeUnits, Items.LengthUnits(Items.StonePickaxe));
            Assert.Equal(Reach.PickaxeUnits, Items.LengthUnits(Items.StoneAxe));
            Assert.Equal(Reach.PickaxeUnits, Items.LengthUnits(Items.Hammer));
            Assert.Equal(Reach.SwordUnits, Items.LengthUnits(Items.WoodenSword));
            Assert.Equal(0, Items.LengthUnits(Items.SimpleBow));
            Assert.Equal(0, Items.LengthUnits(Items.None));
            Assert.Equal(Reach.DigUnits, Reach.Of(Items.LengthUnits(Items.StonePickaxe)));
            Assert.Equal(294, Reach.DigUnits);
            Assert.Equal(Reach.SwordStrikeUnits, Reach.Of(Items.LengthUnits(Items.WoodenSword)));
        }

        // ---------- Стартовый набор и счётчики ----------

        /// <summary>
        /// Стартовый набор нового персонажа (`06` 0 + карточка 1-002): кирка, топор, молот, меч, лук, факел;
        /// в руках — кирка; стрел 50, факелов 3. Пустая сумка — «в руках ничего».
        /// </summary>
        [Fact]
        public void D06_Inventory_StartingKit_EmptyBag()
        {
            Inventory kit = Inventory.StartingKit();
            Assert.Equal(Items.StonePickaxe, kit.SlotAt(0));
            Assert.Equal(Items.StoneAxe, kit.SlotAt(1));
            Assert.Equal(Items.Hammer, kit.SlotAt(2));
            Assert.Equal(Items.WoodenSword, kit.SlotAt(3));
            Assert.Equal(Items.SimpleBow, kit.SlotAt(4));
            Assert.Equal(Items.Torch, kit.SlotAt(5));
            Assert.Equal(Items.None, kit.SlotAt(6));
            Assert.Equal(Items.None, kit.SlotAt(7));
            Assert.Equal(0, kit.Selected);
            Assert.Equal(Items.StonePickaxe, kit.Held);
            Assert.Equal(50, kit.CountAt(Material.Arrows));
            Assert.Equal(3, kit.CountAt(Material.Torch));

            // Пустая сумка (враг, заглушка).
            var empty = new Inventory();
            Assert.Equal(Items.None, empty.Held);
            Assert.Equal(0, empty.CountAt(Material.Wood));
            Assert.Equal(Items.None, empty.SlotAt(99)); // вне панели — пусто

            // Ячейка выбирается только своя; чужая — сумку не меняет.
            Assert.True(kit.Select(3));
            Assert.Equal(Items.WoodenSword, kit.Held);
            Assert.False(kit.Select(Inventory.Slots));
            Assert.Equal(3, kit.Selected);
            Assert.False(kit.Select(-1));
            Assert.False(kit.SetSlotAt(Inventory.Slots, Items.Torch));
            Assert.True(kit.SetSlotAt(6, Items.Torch));
            Assert.Equal(Items.Torch, kit.SlotAt(6));
        }

        /// <summary>Счётчики материалов: сложение с насыщением, списание целиком или никак (операция атомарна).</summary>
        [Fact]
        public void D06_Inventory_Counters_Add_Spend()
        {
            var inv = new Inventory();
            Assert.Equal(5, inv.Add(Material.Wood, 5));
            Assert.Equal(5, inv.CountAt(Material.Wood));
            Assert.Equal(0, inv.Add(Material.Wood, 0));
            Assert.Equal(0, inv.Add(Material.Wood, -3));
            Assert.Equal(7, inv.Add(Material.Stone, 7));

            // Переполнение — насыщение, легло ровно столько, сколько влезло.
            inv.Add(Material.Stone, Inventory.MaxCount - 7);
            Assert.Equal(Inventory.MaxCount, inv.CountAt(Material.Stone));
            Assert.Equal(0, inv.Add(Material.Stone, 10));

            // Списали нет за что — false, счётчик не тронут (рецепт 1-010 не «съедает» половину).
            Assert.False(inv.Spend(Material.Wood, 6));
            Assert.Equal(5, inv.CountAt(Material.Wood));
            Assert.False(inv.Spend(Material.Wood, -1));
            Assert.True(inv.Spend(Material.Wood, 0));
            Assert.True(inv.Spend(Material.Wood, 5));
            Assert.Equal(0, inv.CountAt(Material.Wood));

            // Каждый счётчик сам по себе.
            Assert.Equal(0, inv.CountAt(Material.Fiber));
            inv.Add(Material.Copper, 4);
            inv.Add(Material.Resin, 2);
            inv.Add(Material.Arrows, 1);
            Assert.Equal(4, inv.CountAt(Material.Copper));
            Assert.Equal(2, inv.CountAt(Material.Resin));
            Assert.Equal(1, inv.CountAt(Material.Arrows));
            Assert.Equal(Inventory.MaxCount, inv.CountAt(Material.Stone));
        }

        // ---------- Ячейка едет кадром ввода ----------

        /// <summary>
        /// Смена ячейки — кадром ввода и в том же такте (<see cref="Simulation.Step"/>): сервер и предсказание
        /// сходятся, «предмет в руках» меняется сразу, не следующим кадром.
        /// </summary>
        [Fact]
        public void D06_Sim_Slot_FromInputFrame_AppliesInSameTick()
        {
            var sim = new Simulation(Flat(), maxEntities: 8);
            int id = sim.AddEntity(Center(10, 10));
            Assert.True(sim.SetInventory(id, Inventory.StartingKit()));
            Assert.Equal(Items.StonePickaxe, sim.GetEntity(id).Inv.Held);

            Assert.True(sim.SetInput(id, new InputFrame(Input8.East, 0, 1234, 3)));
            sim.Step();

            SimEntity e = sim.GetEntity(id);
            Assert.Equal(3, e.Inv.Selected);
            Assert.Equal(Items.WoodenSword, e.Inv.Held);
            Assert.Equal(3, e.Input.Slot);
            Assert.Equal(1234, e.Input.Angle16);

            // Кадры сетевого персонажа — ячейка меняется каждым кадром, итог такта — последний кадр.
            var frames = new[]
            {
                new InputFrame(Input8.East, 0, 0, 5),
                new InputFrame(Input8.East, 0, 0, 2),
            };
            sim.SetFramesAt(sim.SlotOf(id), frames);
            sim.Step();
            e = sim.GetEntity(id);
            Assert.Equal(2, e.Inv.Selected);
            Assert.Equal(Items.Hammer, e.Inv.Held);
            Assert.Equal(2, e.Input.Slot);

            // Кадр с ячейкой 0 — возвращает в руки кирку; стойка без смены ячейки — сумка стоит.
            sim.SetInput(id, new InputFrame(Input8.None, 0, 0, 0));
            sim.Step();
            Assert.Equal(0, sim.GetEntity(id).Inv.Selected);

            sim.SetInput(id, new InputFrame(Input8.North, 0, 777, 0));
            sim.Step();
            SimEntity same = sim.GetEntity(id);
            Assert.Equal(0, same.Inv.Selected);
            Assert.Equal(777, same.Input.Angle16);
        }

        /// <summary>Ячейка вне панели (кадр по сети или из журнала) — сумку не портит.</summary>
        [Fact]
        public void D06_Sim_Slot_OutOfPanel_KeepsBag()
        {
            var sim = new Simulation(Flat(), maxEntities: 4);
            int id = sim.AddEntity(Center(4, 4));
            sim.SetInventory(id, Inventory.StartingKit());
            Assert.Throws<ArgumentOutOfRangeException>(() => InputFrame.FromCode(0, 0, 0, Inventory.Slots));
            sim.SetInput(id, InputFrame.FromCode(0, 0, 0, 0));
            sim.Step();
            Assert.Equal(0, sim.GetEntity(id).Inv.Selected);
            sim.SetInput(id, InputFrame.FromCode(1, 0, 0, 7));
            sim.Step();
            Assert.Equal(7, sim.GetEntity(id).Inv.Selected);
            Assert.Equal(Items.None, sim.GetEntity(id).Inv.Held); // ячейка пустая — в руках ничего
        }

        /// <summary>Сумка целиком — в хэше состояния: любой её бит (ячейка, выбранная, счётчик) меняет хэш сущности.</summary>
        [Fact]
        public void D06_Sim_StateHash_CoversInventory()
        {
            var sim = new Simulation(Flat(), maxEntities: 4);
            int id = sim.AddEntity(Center(6, 6));
            sim.SetInventory(id, Inventory.StartingKit());
            SimEntity a = sim.GetEntity(id);
            ulong h0 = StateHash.Entity(a);

            SimEntity b = a;
            b.Inv.Selected = 4;
            Assert.NotEqual(h0, StateHash.Entity(b));

            SimEntity c = a;
            c.Inv.SetSlotAt(6, Items.Torch);
            Assert.NotEqual(h0, StateHash.Entity(c));

            SimEntity d = a;
            d.Inv.Add(Material.Wood, 1);
            Assert.NotEqual(h0, StateHash.Entity(d));

            SimEntity e = a;
            e.Inv.Spend(Material.Arrows, 1);
            Assert.NotEqual(h0, StateHash.Entity(e));

            SimEntity f = a;
            f.Input = new InputFrame(Input8.None, 0, 0, 4);
            Assert.NotEqual(h0, StateHash.Entity(f));

            Assert.Equal(h0, StateHash.Entity(sim.GetEntity(id))); // совпадение — тот же набор слов
            Assert.Equal(16, Inventory.HashWords); // 1 выбранная + 8 ячеек + 7 счётчиков
        }

        // ---------- Сеть: выдача, сохранение за ключом, ячейка в том же кадре ----------

        /// <summary>
        /// Вошёл — стартовый набор; сумки игроков независимы; ушёл — сумка осталась за записью персонажа (0-035);
        /// вернулся — та же сумка, что была в миг ухода.
        /// </summary>
        [Fact]
        public void D06_Net_Join_StartKit_Leave_Return_SameBag()
        {
            using var h = new Harness();
            NetClient a = h.Connect("Орфей");
            NetClient b = h.Connect("Эвридика");
            h.Run(3);
            Assert.Equal(NetClientState.InGame, a.State);
            Assert.Equal(NetClientState.InGame, b.State);

            Inventory kit = Inventory.StartingKit();
            Assert.Equal(kit, h.BagOf(a.EntityId));
            Assert.Equal(kit, h.BagOf(b.EntityId));

            // Добыча Орфея — только у Орфея; в руках — ячейка 2 (её шлют его кадры).
            var mine = Inventory.StartingKit();
            mine.Add(Material.Wood, 7);
            mine.Add(Material.Stone, 3);
            Assert.True(h.Sim.SetInventory(a.EntityId, mine));
            h.Run(2, cl => cl == a ? new InputFrame(Input8.None, 0, 0, 2) : InputFrame.None);
            mine.Select(2);
            Assert.Equal(mine, h.BagOf(a.EntityId));
            Assert.Equal(kit, h.BagOf(b.EntityId));

            a.Disconnect();
            h.Run(3);
            Assert.True(h.Sim.SlotOf(a.EntityId) < 0, "сущность ушедшего осталась в такте");
            int rec = h.Server.Characters.FindPlayer("Орфей");
            Assert.True(rec >= 0);
            Assert.Equal(0, h.Server.Characters[rec].EntityId);
            Assert.Equal(mine, h.Server.Characters[rec].Inv);

            NetClient back = h.Connect("орфей"); // то же имя: пробелов и регистра в счёте нет
            h.Run(1);
            Assert.Equal(NetClientState.InGame, back.State);
            Assert.Equal(1, h.Server.CharactersReturned);
            Assert.Equal(mine, h.BagOf(back.EntityId));
            Assert.Equal(Items.Hammer, h.BagOf(back.EntityId).Held);
        }

        /// <summary>
        /// Ячейка едет кадром ввода: клиент видит её в кадре отправки (не ждёт снимка), сервер — в том же кадре;
        /// после того как все кадры дошли, сервер и предсказание называют одну ячейку (RTT 100, σ 20).
        /// </summary>
        [Fact]
        public void D06_Net_Slot_TravelsWithFrame_PredictionAtOnce()
        {
            using var h = new Harness();
            NetClient c = h.Connect("Орфей", NetSimSettings.FromRtt(100, 20, 0.0, 7));
            var pred = new LocalPrediction(c.Shape, h.Sim.Solid, Center(512, 512));
            c.SnapshotApplied = snap => pred.Reconcile(snap.AckFrame, snap.Self, h.Now);
            h.AfterSend = (cl, f) => pred.Sent(cl.Frame, f);

            // Перебор ячеек 1…4: сервер их и применит, пока кадры в дороге.
            byte sent = 0;
            h.Run(60, cl =>
            {
                sent = (byte)(1 + cl.Frame % 4);
                return new InputFrame(Input8.East, 0, 0, sent);
            });

            // Последний кадр послан, снимка нет (RTT 100 → ~6 кадров в дороге) — клиент уже знает свою ячейку.
            Assert.Equal(sent, pred.SelectedSlot);
            byte onServer = h.BagOf(c.EntityId).Selected;
            Assert.True(onServer <= 4, $"сервер применил ячейку {onServer} — раньше, чем дошёл кадр");

            // Кадры с ячейкой 6 дошли — сервер на той же ячейке, что и предсказание.
            h.Run(30, _ => new InputFrame(Input8.None, 0, 0, 6));
            Assert.Equal(6, pred.SelectedSlot);
            Assert.Equal(6, h.BagOf(c.EntityId).Selected);
            Assert.Equal(pred.SelectedSlot, h.BagOf(c.EntityId).Selected);
            Assert.True(pred.Reconciled > 0, "сверок не было — тест не про сеть");
        }

        // ---------- Стенд ----------

        /// <summary>Сервер и клиенты в памяти: такт — по два кадра на клиента (60 Гц), <c>Poll</c> сервера, такт, рассылка.</summary>
        private sealed class Harness : IDisposable
        {
            private readonly LoopbackTransport _server;
            private readonly List<NetClient> _clients = new List<NetClient>();
            private long _now;

            public Harness()
            {
                Sim = new Simulation(Flat(), maxEntities: 16);
                _server = LoopbackTransport.CreateServer(8);
                Server = new NetServer(_server, Sim, _ => Center(512, 512), 8, NetModule.Version);
            }

            public Simulation Sim { get; }
            public NetServer Server { get; }
            public long Now => _now;
            public Action<NetClient, InputFrame>? AfterSend { get; set; }

            public NetClient Connect(string name, NetSimSettings? sim = null)
            {
                ITransport t = LoopbackTransport.ConnectTo(_server);
                if (sim.HasValue)
                {
                    t = new NetSimTransport(t, sim.Value);
                }

                var c = new NetClient(t, LoopbackTransport.ServerPeer, name, NetModule.Version);
                _clients.Add(c);
                return c;
            }

            public Inventory BagOf(int entityId)
            {
                int slot = Sim.SlotOf(entityId);
                Assert.True(slot >= 0, $"сущности #{entityId} нет в такте");
                return Sim.Entities[slot].Inv;
            }

            public void Run(int ticks, Func<NetClient, InputFrame>? input = null)
            {
                for (int t = 0; t < ticks; t++)
                {
                    for (int half = 0; half < MovementTick.FramesPerTick; half++)
                    {
                        _now += TickMicros / MovementTick.FramesPerTick;
                        foreach (NetClient c in _clients)
                        {
                            c.Poll(_now);
                            if (c.State == NetClientState.InGame)
                            {
                                InputFrame f = input?.Invoke(c) ?? InputFrame.None;
                                c.SendFrame(f);
                                AfterSend?.Invoke(c, f);
                            }
                        }
                    }

                    Server.Poll(_now);
                    Sim.Step();
                    Server.SendSnapshots();
                }

                foreach (NetClient c in _clients)
                {
                    c.Poll(_now);
                }
            }

            public void Dispose()
            {
                foreach (NetClient c in _clients)
                {
                    c.Transport.Dispose();
                }

                _server.Dispose();
            }
        }
    }
}
