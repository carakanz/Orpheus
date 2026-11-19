using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Save;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E3-3b («06» 1.6/9): добыча со зверей — личная капля. Зверь (волк): мясо 1–2 (100 %), шкура 1 (70 %);
    /// дальний (лучник): стрелы 2–5 (50 %); ближний (разбойник): дропа нет. Дроп только когда убил персонаж
    /// (LastHitBy), владелец кучи — убийца. Личная капля: видна и подбирается только владельцем, не сливается
    /// с чужими; общие кучи (стрелы, сумка, копка) — как раньше. Сохранение — поле Owner (формат 17).
    /// </summary>
    public class E33bLootTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat(int size = 128) => FlatWorldGenerator.Instance.Generate(MapShape.World(size), 7);

        private static WorldPos Center(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private static Simulation Sim(int maxEntities)
        {
            var sim = new Simulation(Flat(), maxEntities: maxEntities);
            sim.Step();
            return sim;
        }

        private static List<GroundPile> PilesOf(Simulation sim, byte item)
        {
            var list = new List<GroundPile>();
            foreach (GroundPile p in sim.Piles)
                if (p.Item == item)
                    list.Add(p);
            return list;
        }

        // -------------------------------------------------------- таблицы дропа

        /// <summary>
        /// 100 волков, убитых персонажем: мясо 1–2 ровно у 100/100, шкура — у 55–85 (цель 70 %); всё — личная
        /// капля убийцы; мясо и шкура — отдельные кучи.
        /// </summary>
        [Fact]
        public void E33b_Wolf_Meat100_Hide70_OwnedByKiller()
        {
            Simulation sim = Sim(140);
            int ch = sim.AddEntity(Center(10, 10));
            int hides = 0;
            for (int i = 0; i < 100; i++)
            {
                int beforeHides = PilesOf(sim, Items.Hide).Count;
                int wolf = sim.AddBeastWolf(Center(30 + (i % 20) * 2, 10 + (i / 20) * 2)); // вдали друг от друга — капли не сливаются (радиус слияния ±1 тайл)
                sim.ApplyStrike(wolf, ch, 9999);
                sim.Step();
                hides += PilesOf(sim, Items.Hide).Count - beforeHides;
                List<GroundPile> meat = PilesOf(sim, Items.Meat);
                Assert.Equal(i + 1, meat.Count); // мясо — 100 %
                GroundPile m = meat[i];
                Assert.InRange(m.Count, 1, 2);
                Assert.Equal(ch, m.Owner);
            }

            Assert.Equal(100, PilesOf(sim, Items.Meat).Count);
            Assert.InRange(hides, 55, 85);
            foreach (GroundPile h in PilesOf(sim, Items.Hide))
            {
                Assert.Equal(1, h.Count);
                Assert.Equal(ch, h.Owner);
            }

            Assert.Equal(100 + hides, sim.Piles.Length); // только мясо + шкура, ничего лишнего
        }

        /// <summary>
        /// 100 лучников, убитых персонажем: стрелы 2–5 у 40–60 (цель 50 %), личной каплей; мяса от лучника нет.
        /// </summary>
        [Fact]
        public void E33b_Archer_Arrows2to5_50pct_NoMeat()
        {
            Simulation sim = Sim(140);
            int ch = sim.AddEntity(Center(10, 10));
            for (int i = 0; i < 100; i++)
            {
                int archer = sim.AddRangedArcher(Center(30 + (i % 20) * 2, 10 + (i / 20) * 2)); // вдали друг от друга — капли не сливаются
                sim.ApplyStrike(archer, ch, 9999);
                sim.Step();
            }

            List<GroundPile> arrows = PilesOf(sim, Items.Arrows);
            Assert.InRange(arrows.Count, 40, 60);
            foreach (GroundPile p in arrows)
            {
                Assert.InRange(p.Count, 2, 5);
                Assert.Equal(ch, p.Owner);
            }

            Assert.Equal(arrows.Count, sim.Piles.Length); // ни мяса, ни шкур у людей
        }

        /// <summary>100 разбойников, убитых персонажем: дропа нет («06» 1.6 — у людей только оболы, не срез).</summary>
        [Fact]
        public void E33b_Rogue_NoDrop()
        {
            Simulation sim = Sim(140);
            int ch = sim.AddEntity(Center(10, 10));
            for (int i = 0; i < 100; i++)
            {
                int rogue = sim.AddMeleeRogue(Center(30, 10));
                sim.ApplyStrike(rogue, ch, 9999);
                sim.Step();
            }

            Assert.Equal(0, sim.Piles.Length);
        }

        /// <summary>Убило не игроком — дропа нет: заглушка-враг, несуществующий атакующий (LastHitBy = −1),
        /// и «персонаж ранил — добил разбойник» (последний удар важнее).</summary>
        [Fact]
        public void E33b_KilledByNonCharacter_NoDrop()
        {
            Simulation sim = Sim(8);
            int ch = sim.AddEntity(Center(10, 10));
            int dummy = sim.AddEnemy(Center(12, 10)); // заглушка: Kind = враг
            int wolf = sim.AddBeastWolf(Center(30, 10));
            sim.ApplyStrike(wolf, dummy, 9999);
            Assert.Equal(0, sim.Piles.Length);

            sim.Step();
            int wolf2 = sim.AddBeastWolf(Center(30, 12));
            sim.ApplyStrike(wolf2, 0, 9999); // атакующего нет в списке — LastHitBy остаётся −1
            Assert.Equal(0, sim.Piles.Length);

            sim.Step();
            int wolf3 = sim.AddBeastWolf(Center(30, 14));
            sim.ApplyStrike(wolf3, ch, 1); // персонаж ранил
            sim.ApplyStrike(wolf3, dummy, 9999); // добил не персонаж
            sim.Step();
            Assert.Equal(0, sim.Piles.Length);
        }

        /// <summary>Две капли с одного тайла от разных убийц не сливаются (мясо A и мясо B — две кучи).</summary>
        [Fact]
        public void E33b_TwoOwners_SameTile_MeatDoesNotMerge()
        {
            Simulation sim = Sim(8);
            int a = sim.AddEntity(Center(10, 10));
            int b = sim.AddEntity(Center(12, 10));
            int w1 = sim.AddBeastWolf(Center(30, 10));
            sim.ApplyStrike(w1, a, 9999);
            sim.Step();
            int w2 = sim.AddBeastWolf(Center(30, 10));
            sim.ApplyStrike(w2, b, 9999);
            sim.Step();

            List<GroundPile> meat = PilesOf(sim, Items.Meat);
            Assert.Equal(2, meat.Count);
            Assert.Equal(a, meat[0].Owner);
            Assert.Equal(b, meat[1].Owner);
        }

        // -------------------------------------------------------- подбор личной капли

        /// <summary>Личная капля в радиусе обоих: подбирает только владелец. Капля A вплотную к B — B не трогает;
        /// общая куча там же — B подбирает (общие кучи не менялись).</summary>
        [Fact]
        public void E33b_OnlyOwner_PicksPersonalPile()
        {
            Simulation sim = Sim(8);
            int a = sim.AddEntity(Center(10, 10));
            int b = sim.AddEntity(Center(20, 10));
            int woodA = sim.GetEntity(a).Inv.Count(Items.Wood);
            int woodB = sim.GetEntity(b).Inv.Count(Items.Wood);

            // Личная капля A в радиусе подбора обоих (по 1 тайлу).
            sim.AddPile(Items.Wood, 3, Center(10, 11), owner: a);
            for (int t = 0; t < 20; t++)
            {
                sim.Step();
            }

            Assert.Equal(0, sim.Piles.Length);
            Assert.Equal(woodA + 3, sim.GetEntity(a).Inv.Count(Items.Wood));
            Assert.Equal(woodB, sim.GetEntity(b).Inv.Count(Items.Wood));

            // Личная капля A вплотную к B — B не подбирает.
            int idMeat = sim.AddPile(Items.Meat, 2, Center(20, 11), owner: a);
            for (int t = 0; t < 20; t++)
            {
                sim.Step();
            }

            GroundPile p = Assert.Single(sim.Piles.ToArray());
            Assert.Equal(idMeat, p.Id);
            Assert.Equal(a, p.Owner);

            // Общая куча там же — B подбирает; капля A остаётся лежать (B её не трогает).
            sim.AddPile(Items.Stone, 4, Center(20, 11));
            sim.Step();
            sim.Step();
            GroundPile left = Assert.Single(sim.Piles.ToArray());
            Assert.Equal(idMeat, left.Id);
            Assert.Equal(4, sim.GetEntity(b).Inv.Count(Items.Stone));
        }

        // -------------------------------------------------------- сеть

        /// <summary>Два клиента: личная капля A видна A (с номером владельца) и не видна B, хотя тайл в её окне;
        /// общая — обоим. B не получает «кучи нет» по чужой капле — не знает о ней.</summary>
        [Fact]
        public void E33b_Net_PersonalPile_VisibleOnlyToOwner()
        {
            using var rig = new EditTests.EditRig(Flat(1024), i => i == 0 ? Center(300, 200) : Center(300, 206));
            NetClient a = rig.Join();
            NetClient b = rig.Join();
            rig.Run(10);
            Assert.Empty(a.Piles);

            int personal = rig.Sim.AddPile(Items.Meat, 2, Center(310, 200), owner: a.EntityId); // в окне B, вне радиуса подбора обоих
            int common = rig.Sim.AddPile(Items.Hide, 1, Center(310, 206));
            rig.Run(3);

            var aIds = new List<int>();
            NetPile seenPersonal = default;
            foreach (NetPile p in a.Piles)
            {
                aIds.Add(p.Id);
                if (p.Id == personal)
                    seenPersonal = p;
            }

            Assert.Contains(personal, aIds);
            Assert.Contains(common, aIds);
            Assert.Equal(2, a.Piles.Count);
            Assert.Equal(a.EntityId, seenPersonal.Owner); // E3-3b: поле владельца в сообщении

            NetPile seenB = Assert.Single(b.Piles); // личная капля A — не видна B
            Assert.Equal(common, seenB.Id);
            Assert.Equal(0, seenB.Owner); // общая

            var goneAtB = new List<PileGoneMessage>();
            b.PileGone = (m, was) => goneAtB.Add(m);
            rig.InputOf = c => c == a ? new InputFrame(Input8.East, 0) : InputFrame.None;
            for (int t = 0; t < 400 && a.Piles.Count > 1; t++) // A идёт на восток к своей капле; общая у A остаётся
            {
                rig.Run(1);
            }

            rig.InputOf = null;
            rig.Run(5);
            NetPile leftA = Assert.Single(a.Piles); // общая куча у A видна, личная — подобрана
            Assert.Equal(common, leftA.Id);
            foreach (PileGoneMessage g in goneAtB)
                Assert.NotEqual(personal, g.Id); // B о капле A не знал — «кучи нет» по ней не получал
            Assert.Equal(2, a.Inv.Count(Items.Meat)); // A дошёл и подобрал свою каплю
            Assert.Equal(0, b.Inv.Count(Items.Meat));
            Assert.Equal(0, b.Inv.Count(Items.Hide));
            NetPile leftB = Assert.Single(b.Piles); // B по-прежнему видит только общую
            Assert.Equal(common, leftB.Id);
            Assert.Equal(0, a.BadMessages + b.BadMessages);
        }

        // -------------------------------------------------------- сохранение

        /// <summary>Формат 17: Owner личной капли проходит сохранение; у общей поля нет (Owner = 0).</summary>
        [Fact]
        public void E33b_Save_Owner_RoundTrip()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(256, 21, bots: 2);
            Simulation sim = live.Sim;
            int id = sim.Entities[0].Id;
            live.Run(ticks: 30, editsPerTick: 1);
            WorldPos far = Torus.Offset(sim.Shape, sim.Entities[0].Mover.Position, new Int2(40 * U, 0));
            sim.AddPile(Items.Meat, 2, far, owner: id);
            sim.AddPile(Items.Hide, 1, Torus.Offset(sim.Shape, far, new Int2(5 * U, 0)));
            sim.Step();
            Assert.Equal(2, sim.Piles.Length);
            Inventory bag = sim.Entities[0].Inv;

            var chars = new[] { new CharacterRecord { IsHost = true, Name = "Орфей", EntityId = id, Inv = bag } };
            byte[] save = WorldSave.Build(WorldSave.Capture(sim, live.BaseSha, "капля", chars), 3, 0);
            SaveFile f = SaveContainer.Read(save);
            Assert.True(f.IsOk, f.Error);
            Assert.Equal(17u, f.Header.FormatVersion);

            LoadedWorldState st = WorldSave.Load(f, WorldSnapshot.LoadBase(live.BaseBytes), live.BaseSha);
            Assert.Equal(2, st.Piles.Length);
            Assert.Equal(sim.Piles[0].Id, st.Piles[0].Id);
            Assert.Equal(id, st.Piles[0].Owner);
            Assert.Equal(0, st.Piles[1].Owner);
            Assert.Equal(sim.Piles[0].ExpireTick, st.Piles[0].ExpireTick);
        }

        /// <summary>Хэш: капля с владельцем меняет хэш состояния (детерминизм 11.5); общие кучи — эталоны не двигает.</summary>
        [Fact]
        public void E33b_Hash_PersonalPile_ChangesState()
        {
            Simulation a = Sim(4), b = Sim(4);
            int ch = a.AddEntity(Center(10, 10));
            b.AddEntity(Center(10, 10));
            a.AddPile(Items.Meat, 2, Center(40, 10), owner: ch);
            b.AddPile(Items.Meat, 2, Center(40, 10));
            Assert.NotEqual(StateHash.Compute(a).Total, StateHash.Compute(b).Total);

            Simulation c = Sim(4);
            c.AddEntity(Center(10, 10));
            c.AddPile(Items.Meat, 2, Center(40, 10));
            Assert.Equal(StateHash.Compute(b).Total, StateHash.Compute(c).Total); // общие кучи — без владельца, хэш как до E3-3b
        }
    }
}
