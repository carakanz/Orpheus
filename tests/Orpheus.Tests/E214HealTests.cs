using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using Orpheus.Save;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-14: постройки сами восстанавливают 3 % максимальной прочности в секунду через 3 с после последнего урона
    /// (слово Автора 15.11). Запись в мир — раз в секунду на постройку или на такте «стала целой».
    /// </summary>
    public class E214HealTests
    {
        private readonly ITestOutputHelper _out;

        public E214HealTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static readonly TileCoord T = new TileCoord(310, 210);

        /// <summary>
        /// Независимая запись правила (для чужих тестов, где стена между делом восстанавливается): прочность в мире через
        /// <paramref name="ticksSinceHit"/> тактов после удара, если её больше не трогали. Восстановление — с 90-го такта,
        /// 3 % максимума в секунду; в мир — целыми секундами, а целой — на том такте, когда набралось.
        /// </summary>
        public static int HealedAfter(int integrity, int max, long ticksSinceHit)
        {
            long t = ticksSinceHit - BuildDefs.HealDelayTicks;
            if (t <= 0 || integrity >= max)
            {
                return integrity;
            }

            long exact = integrity + t * 3 * max / 3000;
            if (exact >= max)
            {
                return max;
            }

            return (int)(integrity + t / 30 * 30 * 3 * max / 3000);
        }

        private static Simulation Core(ushort obj = ObjectId.StoneWall, ushort integrity = 300)
        {
            var sim = new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1), maxEntities: 4);
            Assert.True(sim.RequestEdit(T, FloorId.Grass, obj, new BuildRecord(1, integrity, 0, FloorId.Grass, 0)));
            sim.Step();
            return sim;
        }

        private static int Integrity(Simulation sim, TileCoord t) => sim.World.TryGetBuild(t, out BuildRecord r) ? r.Integrity : -1;

        private static void Steps(Simulation sim, int n)
        {
            for (int i = 0; i < n; i++)
            {
                sim.Step();
            }
        }

        /// <summary>
        /// Каменная стена (300) на половине: 3 с без урона — не растёт; потом +9 (3 %) раз в секунду, ровно на секундах; целая —
        /// через 16,7 с восстановления (на 590-м такте после удара, не на 600-м). Сумка и кучи не меняются, список пуст.
        /// </summary>
        [Fact]
        public void E214_Wall_Half_Waits3s_Then3PercentPerSecond_FullIn17s()
        {
            Simulation sim = Core();
            int id = sim.AddEntity(new WorldPos(300 * 256, 200 * 256));
            Inventory bag = sim.GetEntity(id).Inv;
            Assert.True(sim.DamageBuilding(T, 150));
            long hit = sim.Tick;
            int changes = 0, prev = -1;
            long lastChange = 0;
            while (sim.Tick < hit + 700)
            {
                sim.Step();
                long since = sim.Tick - hit;
                int now = Integrity(sim, T);
                Assert.Equal(HealedAfter(150, 300, since), now);
                if (since <= BuildDefs.HealDelayTicks + 29)
                {
                    Assert.Equal(150, now); // 3 с и первую секунду восстановления — без изменений в мире
                }

                if (now != prev && prev >= 0)
                {
                    Assert.True(now == 300 || sim.Tick - lastChange >= BuildDefs.HealStepTicks || lastChange == 0, $"такт {since}: {prev} → {now}");
                    changes++;
                    lastChange = sim.Tick;
                }

                prev = now;
            }

            Assert.Equal(294, HealedAfter(150, 300, 589));
            Assert.Equal(300, HealedAfter(150, 300, 590));
            Assert.Equal(17, changes); // 16 ступеней по 9 и последняя +6
            Assert.True(sim.Heals.IsEmpty);
            Assert.Equal(bag, sim.GetEntity(id).Inv);
            Assert.True(sim.Piles.IsEmpty);
            Assert.Equal(ObjectId.StoneWall, ObjectId.IdOf(sim.World.GetObject(T)));
            Assert.True(sim.World.TryGetBuild(T, out BuildRecord rec));
            Assert.Equal(0, rec.Flags); // флаги урона сняты, как и без восстановления
        }

        /// <summary>
        /// Урон сбрасывает задержку: удар посреди восстановления — снова 3 с без роста. Прибавка, набранная с последней записи
        /// (полсекунды), не теряется: удар 10 по стене 159 + 4,5 — 153.
        /// </summary>
        [Fact]
        public void E214_Damage_ResetsDelay_AccruedHalfSecondKept()
        {
            Simulation sim = Core();
            Assert.True(sim.DamageBuilding(T, 150));
            Steps(sim, BuildDefs.HealDelayTicks + 45); // в мире 159 (одна ступень), набрано ещё 15 тактов × 0,3 = 4,5
            Assert.Equal(159, Integrity(sim, T));
            Assert.True(sim.DamageBuilding(T, 10));
            long hit = sim.Tick;
            sim.Step();
            Assert.Equal(153, Integrity(sim, T));
            Steps(sim, BuildDefs.HealDelayTicks + 28);
            Assert.Equal(153, Integrity(sim, T)); // снова ждёт 3 с (+ первую секунду)
            Steps(sim, 1);
            Assert.Equal(hit + BuildDefs.HealDelayTicks + 30, sim.Tick);
            Assert.Equal(153 + 9, Integrity(sim, T)); // 0,5 остатка + 9 = 9,5 → 9, остаток 0,5 едет дальше (не сброшен ударом)
            Steps(sim, 30);
            Assert.Equal(153 + 18, Integrity(sim, T)); // 9 ровно за секунду: остаток 0,5 так и едет

            // Снос ударом в обломки — из списка (без записи о восстановлении обломков).
            Assert.True(sim.DamageBuilding(T, 1000));
            Steps(sim, BuildDefs.HealDelayTicks + 40);
            Assert.True(sim.Heals.IsEmpty);
        }

        /// <summary>
        /// Остаток накопителя не теряется: деревянная стена (120) — 3,6 в секунду; с 1 прочности за 10 с — 37 (без остатка было
        /// бы 31), целая — на 992-м такте восстановления (119 × 3000 / 360 = 991,7).
        /// </summary>
        [Fact]
        public void E214_Accumulator_Remainder_Kept_WoodWall120()
        {
            Simulation sim = Core(ObjectId.WoodWall, 120);
            Assert.True(sim.DamageBuilding(T, 119));
            long start = sim.Tick + BuildDefs.HealDelayTicks;
            Steps(sim, BuildDefs.HealDelayTicks + 300);
            Assert.Equal(1 + 36, Integrity(sim, T));
            while (sim.Tick < start + 991)
            {
                sim.Step();
            }

            Assert.Equal(1 + 118, Integrity(sim, T)); // 1 + ⌊3,6 × 33⌋ = 119
            sim.Step();
            Assert.Equal(120, Integrity(sim, T));
            Assert.True(sim.Heals.IsEmpty);
        }

        /// <summary>
        /// Детерминизм: два прогона — один хэш на каждом такте. Восстановление входит в хэш: мир байт в байт тот же (удар 10 на
        /// такте 1 или 6, прочность 290, флаги те же), а расписание восстановления разное — общий хэш разный.
        /// </summary>
        [Fact]
        public void E214_Deterministic_TwoRuns_SameHash_HealStateInHash()
        {
            Simulation a = Core(), b = Core();
            Assert.True(a.DamageBuilding(T, 150));
            Assert.True(b.DamageBuilding(T, 150));
            for (int i = 0; i < 700; i++)
            {
                if (i == 200)
                {
                    Assert.True(a.DamageBuilding(T, 40));
                    Assert.True(b.DamageBuilding(T, 40));
                }

                a.Step();
                b.Step();
                Assert.Equal(StateHash.Compute(a).Total, StateHash.Compute(b).Total);
            }

            Simulation c = Core(), d = Core();
            Assert.True(c.DamageBuilding(T, 10));
            Steps(c, 20);
            Steps(d, 5);
            Assert.True(d.DamageBuilding(T, 10));
            Steps(d, 15);
            Assert.Equal(c.Tick, d.Tick);
            Assert.Equal(Integrity(c, T), Integrity(d, T));
            StateHashResult hc = StateHash.Compute(c), hd = StateHash.Compute(d);
            Assert.Equal(hc.World, hd.World);
            Assert.NotEqual(hc.Total, hd.Total);
        }

        /// <summary>
        /// Сохранение посреди восстановления (формат 13) и загрузка. Стены после флагов урона (10 с), посреди секунды: дальше —
        /// тот же хэш на каждом такте, что у мира без перезапуска (остаток накопителя и такт записи едут в файле). Стена, по
        /// которой били 1,7 с назад: восстановление начинается на том же такте и идёт так же — прочность, список и полный хэш
        /// те же на каждом такте (очередь истечений флагов урона тоже в файле, З2 проверки).
        /// </summary>
        [Fact]
        public void E214_Save_MidHeal_Load_ContinuesSameHash()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(256, 21, 0);
            Simulation sim = live.Sim;
            var walls = new[] { new TileCoord(100, 100), new TileCoord(101, 100), new TileCoord(140, 90) };
            foreach (TileCoord w in walls)
            {
                Assert.True(sim.RequestEdit(w, FloorId.Grass, ObjectId.WoodWall, new BuildRecord(1, 120, 0, FloorId.Grass, 0)));
            }

            sim.Step();
            Assert.True(sim.DamageBuilding(walls[0], 110));
            Assert.True(sim.DamageBuilding(walls[1], 20));
            Steps(sim, BuildDefs.DamageFlagTicks + 15); // флаги сняты; [0] восстанавливается посреди секунды, [1] уже цела
            Assert.Equal(1, sim.Heals.Length);
            Assert.NotEqual(0, sim.Heals[0].Acc);

            Simulation loaded = SaveAndLoad(live, sim, out LoadedWorldState st);
            Assert.False(st.HealScan);
            Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total);
            for (int i = 0; i < 300; i++)
            {
                sim.Step();
                loaded.Step();
                Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total);
            }

            Assert.True(sim.DamageBuilding(walls[2], 70));
            Assert.True(sim.DamageBuilding(walls[0], 20));
            Steps(sim, 50);
            loaded = SaveAndLoad(live, sim, out _);
            for (int i = 0; i < 900; i++)
            {
                sim.Step();
                loaded.Step();
                Assert.Equal(sim.Heals.ToArray(), loaded.Heals.ToArray());
                Assert.Equal(StateHash.Compute(sim).Total, StateHash.Compute(loaded).Total); // очередь истечений тоже в файле (З2)
                foreach (TileCoord w in walls)
                {
                    Assert.Equal(Integrity(sim, w), Integrity(loaded, w));
                }
            }

            foreach (TileCoord w in walls)
            {
                Assert.Equal(120, Integrity(loaded, w));
                Assert.True(loaded.World.TryGetBuild(w, out BuildRecord rec));
                Assert.Equal(0, rec.Flags);
            }
        }

        /// <summary>
        /// З2 проверки: малый урон (3 из 300) заживает за 3,3 с — раньше флага «урон 10 с». Сохранение в этом окне → загрузка:
        /// через 10 с от удара флаг снят (очередь истечений в файле, формат 13), снос молотом даёт предмет-стену, а не обломки.
        /// Обломки после загрузки исчезают в срок (10 с от разрушения), запись снимается.
        /// </summary>
        [Fact]
        public void E214_Save_HealedBefore10s_FlagClears_DemolishRefunds_DebrisExpires()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(256, 23, 0);
            Simulation sim = live.Sim;
            var wall = new TileCoord(100, 100);
            var broken = new TileCoord(110, 100);
            int id = sim.AddEntity(new WorldPos(103 * 256 + 128, 103 * 256 + 128));
            foreach (TileCoord w in new[] { wall, broken })
            {
                Assert.True(sim.RequestEdit(w, FloorId.Grass, ObjectId.StoneWall, new BuildRecord(1, 300, 0, FloorId.Grass, 0)));
            }

            sim.Step();
            Assert.True(sim.DamageBuilding(wall, 3));
            Assert.True(sim.DamageBuilding(broken, 1000));
            long hit = sim.Tick;
            Steps(sim, 150); // 5 с: стена уже цела (на 100-м такте), флаг «урон 10 с» ещё стоит; обломки лежат
            Assert.Equal(300, Integrity(sim, wall));
            Assert.True(sim.Heals.IsEmpty);
            Assert.True(sim.World.TryGetBuild(wall, out BuildRecord rec));
            Assert.True(rec.IsDamagedRecent);
            Assert.True(sim.World.TryGetBuild(broken, out BuildRecord deb));
            Assert.True(deb.IsDebris);

            Simulation loaded = SaveAndLoad(live, sim, out LoadedWorldState st);
            Assert.NotEmpty(st.Expiries);
            while (loaded.Tick < hit + BuildDefs.DamageFlagTicks)
            {
                loaded.Step();
            }

            Assert.True(loaded.World.TryGetBuild(wall, out rec));
            Assert.False(rec.IsDamagedRecent);
            Assert.Equal(0, rec.Flags);
            Assert.False(loaded.World.TryGetBuild(broken, out _)); // обломки сняты: запись ушла
            Assert.Equal(ObjectId.Empty, ObjectId.IdOf(loaded.World.GetObject(broken)));

            // Снос целой постройки — возврат предмета кучей на тайл (не обломки).
            Assert.True(loaded.RequestPlayerEdit(wall, FloorId.Grass, ObjectId.Empty, false, default, id, 1));
            loaded.Step();
            Assert.Equal(ObjectId.Empty, ObjectId.IdOf(loaded.World.GetObject(wall)));
            Assert.False(loaded.World.TryGetBuild(wall, out _));
            Assert.Contains(loaded.Piles.ToArray(), p => p.Item == Items.StoneWall && p.Count == 4); // стена во весь тайл — 4 ячейки (E2-12)
        }

        /// <summary>
        /// Многоклеточная мебель (верстак 2 × 2, флаг <see cref="BuildFlags.Wide"/>): прочность одна — у якоря; в списке одна
        /// запись, лечится по правилу один раз (не по числу тайлов); удар по свесу — отказ.
        /// </summary>
        [Fact]
        public void E214_Wide_Workbench_HealsOnceAtAnchor()
        {
            var sim = new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1), maxEntities: 4);
            Assert.True(sim.RequestEdit(T, FloorId.Grass, ObjectId.Workbench,
                new BuildRecord(1, 100, (byte)BuildFlags.Wide, FloorId.Grass, 0)));
            sim.Step();
            Assert.True(sim.World.TryGetBuild(T, out BuildRecord rec));
            Assert.True(rec.Overhangs);
            Assert.False(sim.DamageBuilding(new TileCoord(T.X + 1, T.Y), 30)); // свес — без записи
            Assert.True(sim.DamageBuilding(T, 70));
            long hit = sim.Tick;
            for (int i = 0; i < 1200; i++)
            {
                sim.Step();
                Assert.True(sim.Heals.Length <= 1);
                Assert.Equal(HealedAfter(30, 100, sim.Tick - hit), Integrity(sim, T));
            }

            Assert.Equal(100, Integrity(sim, T));
        }

        /// <summary>З3: порченый файл — дубль тайла в восстановлении — ошибка чтения (InvalidDataException), а не ArgumentException.</summary>
        [Fact]
        public void E214_Save_DuplicateHealTile_InvalidData()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(256, 25, 0);
            Simulation sim = live.Sim;
            var w = new TileCoord(100, 100);
            Assert.True(sim.RequestEdit(w, FloorId.Grass, ObjectId.StoneWall, new BuildRecord(1, 300, 0, FloorId.Grass, 0)));
            sim.Step();
            Assert.True(sim.DamageBuilding(w, 100));
            sim.Step();
            WorldSaveData d = WorldSave.Capture(sim, live.BaseSha, "дубль");
            BuildHeal h = sim.Heals[0];
            typeof(WorldSaveData).GetProperty(nameof(WorldSaveData.Heals))!.SetValue(d, new[] { h, h });
            byte[] bad = WorldSave.Build(d, 4, 0);
            Assert.Throws<InvalidDataException>(() => WorldSave.Load(SaveContainer.Read(bad), WorldSnapshot.LoadBase(live.BaseBytes), live.BaseSha));
        }

        private static Simulation SaveAndLoad(SaveFixtures.LiveWorld live, Simulation sim, out LoadedWorldState st)
        {
            byte[] save = WorldSave.Build(WorldSave.Capture(sim, live.BaseSha, "восстановление"), 3, 0);
            Assert.Equal(17u, SaveContainer.Read(save).Header.FormatVersion);
            st = WorldSave.Load(SaveContainer.Read(save), WorldSnapshot.LoadBase(live.BaseBytes), live.BaseSha);
            Assert.Equal(sim.Heals.ToArray(), st.Heals);
            return SaveFixtures.Load(live.BaseBytes, save);
        }

        /// <summary>
        /// Старый мир (настоящий файл формата 10, стена 300 → 150): хэш сразу после загрузки — прежний; пересохранение до
        /// первого такта не теряет «найти повреждённые»; первым тактом стена — в списке и через 3 с + 16,7 с — целая.
        /// </summary>
        [Fact]
        public void E214_OldFormat10_DamagedWall_FoundFirstTick_Heals()
        {
            byte[] v10 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format10_build.save"));
            byte[] baseBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format10_build.base"));
            Simulation sim = SaveFixtures.Load(baseBytes, v10);
            Assert.Equal("0329ad4c20838f97", StateHash.Compute(sim).Total.ToString("x16")); // E3-6: формат 16
            Assert.True(sim.HealScanPending);
            Assert.True(sim.Heals.IsEmpty);

            byte[] again = WorldSave.Build(WorldSave.Capture(sim, WorldSnapshot.StoredHash(baseBytes), "формат10→13"), 6, 0);
            Simulation re = SaveFixtures.Load(baseBytes, again);
            Assert.True(re.HealScanPending);

            var wall = new TileCoord(124, 129);
            foreach (Simulation s in new[] { sim, re })
            {
                s.Step();
                Assert.Equal(1, s.Heals.Length);
                Assert.Equal(wall, s.Heals[0].Tile);
                Steps(s, BuildDefs.HealDelayTicks + 498); // поиск — на такте загрузки: 3 с и 500 тактов (150 × 3000 / 900) от него
                Assert.Equal(294, Integrity(s, wall));
                s.Step();
                Assert.Equal(300, Integrity(s, wall));
                Assert.True(s.World.TryGetBuild(wall, out BuildRecord rec));
                Assert.Equal(0, rec.Flags & (byte)(BuildFlags.RepairBlocked | BuildFlags.DamagedRecent)); // застрявшие флаги сняты
            }
        }

        /// <summary>
        /// Сеть: прочность доходит до клиента патчами чанка; у одной постройки — не чаще раза в секунду (кроме такта «целая»);
        /// в конце у клиента — как у сервера. Удар и починка молотом — как раньше (тесты C-005).
        /// </summary>
        [Fact]
        public void E214_Net_ClientSeesHeal_AtMostOncePerSecond()
        {
            using var rig = new EditTests.EditRig(FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1),
                _ => new WorldPos(300 * 256 + 128, 200 * 256 + 128));
            NetClient c = rig.Join();
            rig.Run(10);
            var t = new TileCoord(303, 200);
            Assert.True(rig.Sim.RequestEdit(t, FloorId.Grass, ObjectId.StoneWall, new BuildRecord(1, 300, 0, FloorId.Grass, 0)));
            rig.Run(5);
            Assert.True(rig.Sim.DamageBuilding(t, 200));
            var seen = new List<(long Tick, int Value)>();
            rig.AfterTick = () =>
            {
                int v = c.World!.World.TryGetBuild(t, out BuildRecord r) ? r.Integrity : -1;
                if (seen.Count == 0 || seen[^1].Value != v)
                {
                    seen.Add((rig.Sim.Tick, v));
                }
            };
            rig.Run(BuildDefs.HealDelayTicks + 1200);
            Assert.Equal(300, Integrity(rig.Sim, t));
            Assert.Equal(300, seen[^1].Value);
            Assert.True(seen.Count >= 20, $"изменений у клиента {seen.Count}");
            for (int i = 2; i < seen.Count - 1; i++)
            {
                Assert.True(seen[i].Tick - seen[i - 1].Tick >= BuildDefs.HealStepTicks - 1, $"{seen[i - 1]} → {seen[i]}");
                Assert.True(seen[i].Value > seen[i - 1].Value);
            }
        }

        /// <summary>
        /// Нагрузка: 500 повреждённых построек разом — шаг такта в бюджете (33 мс; среднее — меньше 2 мс), правок восстановления
        /// за такт не больше 64, все 500 становятся целыми; список пуст.
        /// </summary>
        [Fact]
        public void E214_Load_500Damaged_StepWithinTickBudget()
        {
            var sim = new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1), maxEntities: 4);
            var tiles = new List<TileCoord>();
            for (int i = 0; i < 500; i++)
            {
                var t = new TileCoord(200 + (i % 50) * 3, 200 + (i / 50) * 3);
                tiles.Add(t);
                Assert.True(sim.RequestEdit(t, FloorId.Grass, ObjectId.StoneWall, new BuildRecord(1, 300, 0, FloorId.Grass, 0)));
                if (i % 100 == 99)
                {
                    sim.Step();
                }
            }

            sim.Step();
            foreach (TileCoord t in tiles)
            {
                Assert.True(sim.DamageBuilding(t, 1 + (t.X * 7 + t.Y) % 290));
            }

            Steps(sim, 3); // прогрев JIT
            var last = new int[tiles.Count];
            for (int k = 0; k < tiles.Count; k++)
            {
                last[k] = Integrity(sim, tiles[k]);
            }

            var sw = new Stopwatch();
            double worst = 0;
            int ticks = 0, maxEdits = 0;
            while (sim.Heals.Length > 0)
            {
                long before = sw.ElapsedTicks;
                sw.Start();
                sim.Step();
                sw.Stop();
                worst = Math.Max(worst, (sw.ElapsedTicks - before) * 1000.0 / Stopwatch.Frequency);
                int edits = 0; // правки восстановления — постройки, у которых прочность в мире выросла в этом такте
                for (int k = 0; k < tiles.Count; k++)
                {
                    int v = Integrity(sim, tiles[k]);
                    edits += v != last[k] ? 1 : 0;
                    last[k] = v;
                }

                maxEdits = Math.Max(maxEdits, edits);
                Assert.True(++ticks < 2000, "восстановление не кончилось за 2000 тактов");
            }

            double avg = sw.Elapsed.TotalMilliseconds / ticks;
            _out.WriteLine($"тактов {ticks}, среднее {avg:F3} мс, худший {worst:F3} мс, правок за такт до {maxEdits}");
            Assert.True(avg < 2.0, $"среднее {avg:F3} мс");
            Assert.True(worst < 1000.0 / Simulation.TickRate, $"худший такт {worst:F3} мс");
            Assert.True(maxEdits <= BuildDefs.HealEditsPerTick, $"правок за такт {maxEdits}");
            foreach (TileCoord t in tiles)
            {
                Assert.Equal(300, Integrity(sim, t));
            }
        }
    }
}
