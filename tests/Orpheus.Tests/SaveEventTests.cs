using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Hosting;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Save;
using Orpheus.Server;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Сохранение по поводу «вход/уход» (карточка 0-037; docs/07 6.4 «в течение 1 с», 6.5, 6.6): повод во время записи
    /// не пропадает, а пишется сразу после неё; частые поводы заменяют последнее поколение своей серии и не вытесняют
    /// старые; вход нового персонажа тоже сохраняется. Запись «держится» хранилищем с задвижкой — долгая запись без
    /// гонки со временем.
    /// </summary>
    public class SaveEventTests
    {
        private const ulong Seed = 37;
        private readonly ITestOutputHelper _out;

        public SaveEventTests(ITestOutputHelper output) => _out = output;

        // ---------- WorldSaver напрямую ----------

        /// <summary>
        /// Уход во время долгой записи: повод не пропадает до автосохранения, а пишется первым же тактом после конца
        /// прошлой записи (≤ 1 с); два ухода за время записи — одно сохранение; в файле оба ушедших — вне мира.
        /// </summary>
        [Fact]
        public void D07_S6_4_Saver_LeaveDuringWrite_SavedRightAfter_Coalesced()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, bots: 2);
            var storage = new GatedStorage();
            string dir = MemoryDir();
            var roster = new CharacterRoster();
            roster.AddInWorld(false, "первый", live.Bots[0].EntityId);
            roster.AddInWorld(false, "второй", live.Bots[1].EntityId);
            var log = new SaveLog();
            var saver = new WorldSaver(new WorldFolder(storage, dir), live.BaseSha, "t", log, 120, live.Sim.Tick, false, null)
            {
                Characters = roster.Capture,
            };

            storage.Hold();
            saver.Request("игрок вошёл");
            Tick(live.Sim, saver);
            Assert.True(storage.WaitEntered(10_000), "запись не началась");

            // Двое уходят, пока первая запись висит на диске.
            roster.Park(0, live.Sim);
            saver.Request("игрок вышел");
            roster.Park(1, live.Sim);
            saver.Request("игрок вышел");
            for (int t = 0; t < 30; t++)
            {
                Tick(live.Sim, saver);
            }

            Assert.True(saver.Writing && saver.HasRequest, "повод должен ждать конца записи");
            Assert.Equal(0, saver.Written);

            storage.Release();
            var w = Stopwatch.StartNew();
            while (saver.Written < 2 && w.ElapsedMilliseconds < 10_000)
            {
                Tick(live.Sim, saver);
                Thread.Sleep(1000 / Simulation.TickRate);
            }

            Assert.Equal(2, saver.Written);
            Assert.False(saver.HasRequest);
            GatedStorage.Write[] writes = storage.Generations();
            Assert.Equal(2, writes.Length);
            double after = writes[1].EndMs - writes[0].EndMs;
            _out.WriteLine($"уход в файле через {after:F1} мс после конца прошлой записи");
            Assert.True(after <= 1000, $"уход записан через {after:F0} мс после конца прошлой записи (> 1 с)");

            SaveRecord second = log.Saves[1];
            Assert.Equal("игрок вышел", second.Reason);
            Assert.Equal(2, second.Requests);
            Assert.Equal(1ul, second.Replaces); // та же серия поводов: поколение входа заменено поколением ухода
            Assert.Equal(new ulong[] { 2 }, new WorldFolder(storage, dir).ListGenerations());
            Assert.All(CharactersIn(storage, dir, 2, live), c => Assert.Equal(0, c.EntityId)); // оба вне мира
        }

        /// <summary>
        /// Частые поводы (переподключения): каждый пишется, но поколения серии заменяют друг друга — два старых
        /// поколения автосохранения остаются в папке; автосохранение заканчивает серию; серия старше интервала начинает
        /// новое поколение.
        /// </summary>
        [Fact]
        public void D07_S6_6_Saver_FrequentRequests_ReplaceSeries_OldGenerationsKept()
        {
            const int intervalSec = 2; // 60 тактов
            int interval = intervalSec * Simulation.TickRate;
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, bots: 1);
            var storage = new GatedStorage();
            string dir = MemoryDir();
            var folder = new WorldFolder(storage, dir);
            var log = new SaveLog();
            var saver = new WorldSaver(folder, live.BaseSha, "t", log, intervalSec, live.Sim.Tick, false, null);

            Run(live.Sim, saver, 2 * interval); // два автосохранения
            Assert.Equal(new ulong[] { 1, 2 }, folder.ListGenerations());

            for (int i = 0; i < 10; i++)
            {
                saver.Request(i % 2 == 0 ? "игрок вошёл" : "игрок вышел");
                Run(live.Sim, saver, 3);
            }

            Assert.Equal(12, saver.Written);
            Assert.Equal(9, saver.Replaced);
            Assert.Equal(new ulong[] { 1, 2, 12 }, folder.ListGenerations()); // старые на месте, серия — одним поколением
            Assert.Equal(11ul, log.Saves[11].Replaces);
            Assert.Equal(0ul, log.Saves[2].Replaces);

            // Тишина — автосохранение: серия кончилась, следующий повод пишет новое поколение, а не заменяет автосохранение.
            Run(live.Sim, saver, interval + 5);
            Assert.Equal(new ulong[] { 2, 12, 13 }, folder.ListGenerations());
            Assert.Equal("автосохранение", log.Saves[12].Reason);
            saver.Request("игрок вошёл");
            Run(live.Sim, saver, 40);
            Assert.Equal(new ulong[] { 12, 13, 14 }, folder.ListGenerations());

            // Через 40 тактов — та же серия (заменяет 14); через 80 от начала серии — новое поколение.
            saver.Request("игрок вышел");
            Run(live.Sim, saver, 40);
            Assert.Equal(new ulong[] { 12, 13, 15 }, folder.ListGenerations());
            saver.Request("игрок вошёл");
            Run(live.Sim, saver, 3);
            Assert.Equal(new ulong[] { 13, 15, 16 }, folder.ListGenerations());
            Assert.Equal(10, saver.Replaced);

            // Папка после замен читается как обычно: новейшее целое — 16.
            WorldRecovery rec = new WorldFolder(storage, dir).Recover();
            Assert.Equal(16ul, rec.LatestNumber);
            Assert.Empty(rec.Damaged);
        }

        /// <summary>Остановка сразу после повода в том же такте, что и прошлое сохранение, — повод не теряется.</summary>
        [Fact]
        public void D07_S6_4_Saver_RequestThenStopSameTick_Saved()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, bots: 1);
            var storage = new GatedStorage();
            var log = new SaveLog();
            var saver = new WorldSaver(new WorldFolder(storage, MemoryDir()), live.BaseSha, "t", log, 120, live.Sim.Tick, false, null);
            saver.Request("игрок вошёл");
            Run(live.Sim, saver, 1);
            Assert.Equal(1, saver.Written);
            saver.Request("игрок вышел"); // уход после снимка этого такта
            saver.SaveOnStop(live.Sim);
            Assert.Equal(2, saver.Written);
            Assert.Equal("остановка", log.Saves[1].Reason);
        }

        // ---------- Обрыв питания внутри заменяющего сохранения (з. 1 проверки 0-037) ----------

        /// <summary>
        /// Обрыв в каждый миг заменяющего сохранения (серия поводов, поколение 5 заменяет 4; в папке ещё автосохранения 1 и 2).
        /// Хранилище раскладывает запись на шаги носителя — половина <c>.tmp</c>, целый <c>.tmp</c>, перенос в <c>.save</c>,
        /// каждое удаление — и в k-й шаг «гаснет свет»: снимок носителя, дальше любая запись — отказ. Старт на снимке обязан
        /// взять новейшее поколение, целиком побывавшее на носителе (такт и хэш — как в начале его записи), и оставить в
        /// папке оба автосохранения: заменённое, не успевшее удалиться, убирается первым (з. 2). Порядок «сначала новое,
        /// потом удаление» держит этот тест: удаление заменённого до записи (мутация М1) — на шаге 1 новейшего целого нет.
        /// </summary>
        [Fact]
        public void D07_S6_5_Saver_CrashInsideReplacingSave_EveryMoment_LoadsNewestIntact()
        {
            const int intervalSec = 2;
            int interval = intervalSec * Simulation.TickRate;
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, bots: 1);
            int moments = -1;
            for (int k = 0; k < 50 && moments < 0; k++)
            {
                var storage = new CrashStorage();
                string dir = MemoryDir();
                var hook = new BeginHook();
                var log = new SaveLog();
                var saver = new WorldSaver(new WorldFolder(storage, dir), live.BaseSha, "t", log, intervalSec, live.Sim.Tick, false, hook);
                RunLive(live, saver, 2 * interval); // автосохранения 1, 2
                saver.Request("игрок вошёл");
                RunLive(live, saver, 3); // 3 — начало серии
                saver.Request("игрок вышел");
                RunLive(live, saver, 3); // 4 заменяет 3
                Assert.Equal(new ulong[] { 1, 2, 4 }, new WorldFolder(storage, dir).ListGenerations());

                storage.Arm(k);
                saver.Request("игрок вошёл");
                RunLive(live, saver, 3); // 5 заменяет 4 — обрыв на шаге k
                if (!storage.Crashed)
                {
                    // Шагов меньше k: запись дошла до конца без обрыва.
                    moments = k;
                    Assert.Equal(4ul, log.Saves[log.Saves.Count - 1].Replaces);
                    Assert.Equal(new ulong[] { 1, 2, 5 }, new WorldFolder(storage, dir).ListGenerations());
                    _out.WriteLine($"шагов носителя в заменяющем сохранении: {k} — {string.Join(" → ", storage.Trace)}");
                    break;
                }

                MemoryStorage disk = storage.Frozen!;
                ulong expected = storage.NewestDurable;
                WorldRecovery rec = new WorldFolder(disk, dir).Recover();
                _out.WriteLine($"обрыв перед шагом {k} ({storage.CrashedBefore}): загружено {rec.LatestNumber}, ждали {expected}; " +
                               $"битых {rec.Damaged.Count}, лишнее заменённое {rec.ReplacedLeftover}");
                Assert.Equal(expected, rec.LatestNumber);
                Assert.Equal(hook.Begins[expected].Tick, rec.Latest!.Header.WorldTick);
                Simulation loaded = SaveFixtures.Load(live.BaseBytes, disk.ReadAllBytes(Path.Combine(dir, rec.LatestName!)));
                Assert.Equal(hook.Begins[expected].Tick, loaded.Tick);
                Assert.Equal(hook.Begins[expected].Hash, StateHash.Compute(loaded).Total);

                // Оба поколения автосохранения на месте; лишнего заменённого нет (з. 2).
                Assert.Equal(new ulong[] { 1, 2, expected }, new WorldFolder(disk, dir).ListGenerations());
                Assert.DoesNotContain(disk.ListFiles(dir), n => n.EndsWith(WorldFolder.TmpSuffix, StringComparison.Ordinal));
            }

            // Половина .tmp, целый .tmp, перенос, удаление заменённого — все миги пройдены.
            Assert.True(moments >= 4, $"шагов носителя {moments}: тест не дошёл до всех мигов");
        }

        /// <summary>
        /// З. 2: заменённое поколение серии не удалилось (файл держит антивирус). Очистка не удаляет вместо него самое старое
        /// автосохранение; следующая замена убирает и его. После перезапуска заменённое, оставшееся рядом с заменившим,
        /// узнаётся по полю файла и тоже убирается первым.
        /// </summary>
        [Fact]
        public void D07_S6_6_Saver_ReplacedNotDeleted_LeftoverRemovedFirst_AutosavesKept()
        {
            const int intervalSec = 2;
            int interval = intervalSec * Simulation.TickRate;
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, bots: 1);
            var storage = new CrashStorage();
            string dir = MemoryDir();
            var folder = new WorldFolder(storage, dir);
            var log = new SaveLog();
            var saver = new WorldSaver(folder, live.BaseSha, "t", log, intervalSec, live.Sim.Tick, false, null);
            RunLive(live, saver, 2 * interval);
            saver.Request("игрок вошёл");
            RunLive(live, saver, 3);
            Assert.Equal(new ulong[] { 1, 2, 3 }, folder.ListGenerations());

            storage.Locked.Add(WorldFolder.GenerationFileName(3));
            saver.Request("игрок вышел");
            RunLive(live, saver, 3); // 4 заменяет 3, удалить 3 не вышло
            Assert.Equal(3ul, log.Saves[log.Saves.Count - 1].Replaces);
            Assert.Equal(new ulong[] { 1, 2, 3, 4 }, folder.ListGenerations()); // 1 не тронут

            saver.Request("игрок вошёл");
            RunLive(live, saver, 3); // 5 заменяет 4; 3 всё ещё держат
            Assert.Equal(new ulong[] { 1, 2, 3, 5 }, folder.ListGenerations());

            storage.Locked.Clear();
            saver.Request("игрок вышел");
            RunLive(live, saver, 3); // 6 заменяет 5, и 3 наконец убран
            Assert.Equal(new ulong[] { 1, 2, 6 }, folder.ListGenerations());

            // Перезапуск: заменённое 6-м — рядом (обрыв до удаления) — старт убирает его, а не 1.
            storage.Locked.Add(WorldFolder.GenerationFileName(6));
            saver.Request("игрок вошёл");
            RunLive(live, saver, 3); // 7 заменяет 6, 6 держат
            Assert.Equal(new ulong[] { 1, 2, 6, 7 }, folder.ListGenerations());
            storage.Locked.Clear();
            WorldRecovery rec = new WorldFolder(storage, dir).Recover();
            Assert.Equal(7ul, rec.LatestNumber);
            Assert.Equal(6ul, rec.ReplacedLeftover);
            Assert.Equal(new ulong[] { 1, 2, 7 }, new WorldFolder(storage, dir).ListGenerations());
        }

        /// <summary>
        /// Поле «заменяет» не действует на копию из резервных, положенную руками под большим номером (6.6): номер в
        /// заголовке не совпал с именем — старт не удаляет по нему поколение, чистка идёт как обычно.
        /// </summary>
        [Fact]
        public void D07_S6_6_Folder_ManualCopyOfReplacingSave_DoesNotDeleteByField()
        {
            const int intervalSec = 2;
            int interval = intervalSec * Simulation.TickRate;
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, bots: 1);
            var storage = new CrashStorage();
            string dir = MemoryDir();
            var saver = new WorldSaver(new WorldFolder(storage, dir), live.BaseSha, "t", new SaveLog(), intervalSec, live.Sim.Tick, false, null);
            RunLive(live, saver, 2 * interval);
            saver.Request("игрок вошёл");
            RunLive(live, saver, 3);
            saver.Request("игрок вышел");
            RunLive(live, saver, 3); // 4 заменяет 3: в папке 1, 2, 4
            byte[] four = storage.ReadAllBytes(Path.Combine(dir, WorldFolder.GenerationFileName(4)));
            Assert.Equal(3ul, WorldSave.ReadReplaces(SaveContainer.Read(four)));

            // Восстановление по 6.6: копия 4 под номером 9 — «заменяет 3», а рядом ниже — 4.
            storage.WriteAtomic(Path.Combine(dir, WorldFolder.GenerationFileName(9)), four);
            WorldRecovery rec = new WorldFolder(storage, dir).Recover();
            Assert.Equal(9ul, rec.LatestNumber);
            Assert.Equal(0ul, rec.ReplacedLeftover);
            Assert.Equal(new ulong[] { 2, 4, 9 }, new WorldFolder(storage, dir).ListGenerations());
        }

        /// <summary>
        /// З. 5: остановка с ошибкой (исключение из цикла такта на границе такта, код 2) — мир цел, и он сохраняется, как при
        /// штатной остановке: такт ошибки — в новейшем поколении, а не только то, что успело автосохранение.
        /// </summary>
        [Fact]
        public void D07_S6_4_ServerRun_StopWithError_AtTickBoundary_WorldSaved()
        {
            var storage = new MemoryStorage();
            string world = MemoryDir();
            var log = new SaveLog();
            ServerOptions o = ServerOptions.Parse(new[]
            {
                "--world", world, "--create", "--seed", "11", "--size", "1024", "--sim", "--bots", "1:wander", "--ticks", "200",
                "--autosave", "120", "--metrics", "0",
            });
            o.AfterTick = sim =>
            {
                if (sim.Tick == 50)
                {
                    throw new IOException("тест: отказ посреди работы");
                }
            };

            ServerResult r = ServerHost.Run(o, new ServerHostServices { Log = log, Storage = storage });
            Assert.Equal(2, r.ExitCode);
            Assert.Equal(1, r.SavesWritten);
            Assert.Equal("остановка", log.Saves.Single().Reason);
            WorldRecovery rec = new WorldFolder(storage, world).Recover();
            Assert.Equal(1ul, rec.LatestNumber);
            Assert.Equal(50, rec.Latest!.Header.WorldTick);
        }

        // ---------- Сервер в потоке: настоящие вход и уход ----------

        /// <summary>
        /// Сервер в потоке (локальная связь, хранилище с задвижкой, отладочное наполнение 512 КБ): вход нового персонажа
        /// сохраняется; уход во время долгой записи — в файле ≤ 1 с после её конца; вернувшийся вход не сохраняется;
        /// частые переподключения не плодят поколений; после перезапуска ушедший — на своём месте.
        /// </summary>
        [Fact]
        public void D07_S6_4_ServerThread_JoinSaved_LeaveDuringLongWrite_ReconnectsKeepGenerations()
        {
            var storage = new GatedStorage();
            string world = MemoryDir();
            var clients = new List<NetClient>();
            using (var srv = new Server(storage, world, create: true, "--debug-save-pad", "512"))
            {
                NetClient a = srv.Connect("первый", clients);
                Assert.True(Pump(() => a.State == NetClientState.InGame && srv.Log.Count("игрок вошёл") == 1, 10_000, clients), "вход первого не сохранён");
                NetClient b = srv.Connect("второй", clients);
                Assert.True(Pump(() => b.State == NetClientState.InGame && srv.Log.Count("игрок вошёл") == 2, 10_000, clients), "вход второго не сохранён");

                // Третий входит — запись повисает; второй уходит во время неё.
                storage.Hold();
                NetClient c = srv.Connect("третий", clients);
                Assert.True(Pump(() => storage.Entered, 10_000, clients), "запись по входу третьего не началась");
                b.Disconnect();
                Pump(() => false, 300, clients);
                Assert.Equal(2, srv.Log.Saves.Count);
                storage.Release();
                Assert.True(Pump(() => srv.Log.Count("игрок вышел") == 1, 5_000, clients), "уход во время записи не сохранён");
                GatedStorage.Write[] writes = storage.Generations();
                double after = writes[writes.Length - 1].EndMs - writes[writes.Length - 2].EndMs;
                _out.WriteLine($"уход в файле через {after:F1} мс после конца прошлой записи; записей {writes.Length}");
                Assert.True(after <= 1000, $"уход записан через {after:F0} мс после конца прошлой записи");
                CharacterRecord[] chars = CharactersIn(storage, world, srv.Log.Saves[srv.Log.Saves.Count - 1].Number, null);
                Assert.Equal(0, chars.Single(x => x.Name == "второй").EntityId);
                Assert.True(chars.Single(x => x.Name == "третий").EntityId > 0);

                // Второй переподключается 5 раз: вход вернувшегося не сохраняется, уход — да, поколение одно.
                for (int i = 0; i < 5; i++)
                {
                    NetClient again = srv.Connect("второй", clients);
                    Assert.True(Pump(() => again.State == NetClientState.InGame, 10_000, clients), $"не вошёл снова: {again.State}");
                    again.Disconnect();
                    int left = i + 2;
                    Assert.True(Pump(() => srv.Log.Count("игрок вышел") == left, 5_000, clients), $"уход № {left} не сохранён");
                }

                Assert.Equal(3, srv.Log.Count("игрок вошёл"));
                ulong[] gens = new WorldFolder(storage, world).ListGenerations();
                _out.WriteLine($"сохранений {srv.Log.Saves.Count}, поколений {gens.Length}: {string.Join(",", gens)}");
                Assert.Single(gens); // вся серия входов и уходов за 2 мин — одно поколение
                ServerResult r = srv.Stop();
                Assert.Equal(0, r.ExitCode);
                Assert.Equal(srv.Log.Saves.Count - 2, r.SavesReplaced); // все поводы, кроме первого, заменили; остановка — новое
                Assert.Equal(2, new WorldFolder(storage, world).ListGenerations().Length);
            }

            foreach (NetClient cl in clients)
            {
                cl.Transport.Dispose();
            }

            Assert.False(Directory.Exists(world), "сохранение мимо хранилища");
        }

        // ---------- Помощники ----------

        private static string MemoryDir() => Path.Combine(Path.GetTempPath(), "orpheus-0037-" + Guid.NewGuid().ToString("N")); // в памяти

        private static void Tick(Simulation sim, WorldSaver saver)
        {
            sim.Step();
            sim.RefillPools();
            saver.AfterTick(sim);
        }

        /// <summary><paramref name="ticks"/> тактов; после каждого — дождаться фоновой записи (такты без часов).</summary>
        private static void Run(Simulation sim, WorldSaver saver, int ticks)
        {
            for (int t = 0; t < ticks; t++)
            {
                Tick(sim, saver);
                var w = Stopwatch.StartNew();
                while (saver.Writing && w.ElapsedMilliseconds < 10_000)
                {
                    Thread.Sleep(1);
                    saver.AfterTick(sim); // тот же такт: забрать итог; повод уже снят, нового не начнёт
                }

                Assert.False(saver.Writing, "запись не кончилась");
            }
        }

        /// <summary>
        /// <paramref name="ticks"/> тактов живого мира (ввод бота, правки тайлов — снимки разные); после каждого — граница
        /// такта и ожидание фоновой записи.
        /// </summary>
        private static void RunLive(SaveFixtures.LiveWorld live, WorldSaver saver, int ticks)
        {
            for (int t = 0; t < ticks; t++)
            {
                live.Run(1, 2);
                saver.AfterTick(live.Sim);
                var w = Stopwatch.StartNew();
                while (saver.Writing && w.ElapsedMilliseconds < 10_000)
                {
                    Thread.Sleep(1);
                    saver.AfterTick(live.Sim);
                }

                Assert.False(saver.Writing, "запись не кончилась");
            }
        }

        /// <summary>Крючок записи: номер поколения → такт и хэш состояния в начале записи (как строка <c>save-begin</c>).</summary>
        private sealed class BeginHook : ISaveHook
        {
            public Dictionary<ulong, (long Tick, ulong Hash)> Begins { get; } = new Dictionary<ulong, (long, ulong)>();

            public string Plan => "тест";

            public void Attach(IPlatformStorage storage)
            {
            }

            public void OnLoaded(ulong number, long tick, ulong hash)
            {
            }

            public void OnBegin(ulong number, long tick, ulong hash) => Begins[number] = (tick, hash);

            public void OnDone(ulong number)
            {
            }
        }

        /// <summary>Персонажи из поколения <paramref name="number"/> в хранилище.</summary>
        private static CharacterRecord[] CharactersIn(IPlatformStorage storage, string dir, ulong number, SaveFixtures.LiveWorld? live)
        {
            byte[] baseBytes = live?.BaseBytes ?? storage.ReadAllBytes(Path.Combine(dir, WorldFolder.BaseFileName));
            SaveFile f = SaveContainer.Read(storage.ReadAllBytes(Path.Combine(dir, WorldFolder.GenerationFileName(number))));
            Assert.True(f.IsOk, f.Error);
            WorldModel model = WorldSnapshot.LoadBase(baseBytes);
            return WorldSave.Load(f, model, WorldSnapshot.StoredHash(baseBytes)).Characters;
        }

        private static bool Pump(Func<bool> done, int timeoutMs, List<NetClient> clients)
        {
            var w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < timeoutMs)
            {
                foreach (NetClient c in clients)
                {
                    c.Poll(Environment.TickCount64 * 1000);
                    if (c.State == NetClientState.InGame)
                    {
                        c.SendFrame(InputFrame.None);
                    }
                }

                if (done())
                {
                    return true;
                }

                Thread.Sleep(15);
            }

            return false;
        }

        /// <summary>Сохранение из лога: номер, причина, поводов в нём, какое поколение заменило.</summary>
        internal readonly struct SaveRecord
        {
            public SaveRecord(ulong number, string reason, int requests, ulong replaces)
            {
                Number = number;
                Reason = reason;
                Requests = requests;
                Replaces = replaces;
            }

            public ulong Number { get; }
            public string Reason { get; }
            public int Requests { get; }
            public ulong Replaces { get; }
        }

        /// <summary>Лог: только строки «сохранено» — по порядку.</summary>
        internal sealed class SaveLog : IServerLog
        {
            private readonly List<SaveRecord> _saves = new List<SaveRecord>();

            public List<SaveRecord> Saves
            {
                get
                {
                    lock (_saves)
                    {
                        return new List<SaveRecord>(_saves);
                    }
                }
            }

            public int Count(string reason) => Saves.Count(s => s.Reason == reason);

            public bool Enabled(string cat, LogLevel level) => level >= LogLevel.Info;

            public void Write(LogLevel level, long tick, string cat, string msg, (string Key, object? Value)[] data)
            {
                if (msg != "сохранено")
                {
                    return;
                }

                ulong number = 0, replaces = 0;
                string reason = string.Empty;
                int requests = 0;
                foreach ((string key, object? value) in data)
                {
                    switch (key)
                    {
                        case "number": number = Convert.ToUInt64(value); break;
                        case "reason": reason = value?.ToString() ?? string.Empty; break;
                        case "requests": requests = Convert.ToInt32(value); break;
                        case "replaces": replaces = Convert.ToUInt64(value); break;
                    }
                }

                lock (_saves)
                {
                    _saves.Add(new SaveRecord(number, reason, requests, replaces));
                }
            }
        }

        /// <summary>Сервер в потоке (0-031) на локальной связи; хранилище — с задвижкой.</summary>
        private sealed class Server : IDisposable
        {
            private readonly LoopbackTransport _link = LoopbackTransport.CreateServer(8);
            private readonly ServerThread _host;
            private readonly List<LoopbackTransport> _clients = new List<LoopbackTransport>();

            public Server(IPlatformStorage storage, string world, bool create, params string[] extra)
            {
                var args = new List<string> { "--world", world, "--metrics", "0" };
                if (create)
                {
                    args.AddRange(new[] { "--create", "--seed", "11", "--size", "1024" });
                }

                args.AddRange(extra);
                var services = new ServerHostServices { Log = Log, Storage = storage, LocalTransport = _link };
                _host = ServerThread.Start(ServerOptions.Parse(args.ToArray()), services, "server-0-037");
                Assert.True(SpinWait.SpinUntil(() => _host.IsReady || _host.HasExited, 20_000), "сервер в потоке не готов");
                Assert.True(_host.IsReady, $"сервер в потоке вышел до готовности: {_host.Error}");
            }

            public SaveLog Log { get; } = new SaveLog();

            public NetClient Connect(string name, List<NetClient> all)
            {
                LoopbackTransport t = LoopbackTransport.ConnectTo(_link);
                _clients.Add(t);
                var c = new NetClient(t, LoopbackTransport.ServerPeer, name, NetModule.Version);
                all.Add(c);
                return c;
            }

            public ServerResult Stop()
            {
                Assert.True(_host.Stop(30_000), "сервер в потоке не остановился");
                return _host.Result ?? throw new Xunit.Sdk.XunitException("нет итога: " + _host.Error);
            }

            public void Dispose()
            {
                _host.Dispose();
                foreach (LoopbackTransport t in _clients)
                {
                    t.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Хранилище в памяти с задвижкой (0-037): пока задвижка закрыта, запись поколения <c>.save</c> висит внутри
    /// <see cref="WriteAtomic"/> — долгая запись на диск без гонки со временем. Запоминает начало и конец каждой записи.
    /// </summary>
    internal sealed class GatedStorage : IPlatformStorage
    {
        private readonly MemoryStorage _inner = new MemoryStorage();
        private readonly ManualResetEventSlim _gate = new ManualResetEventSlim(true);
        private readonly ManualResetEventSlim _entered = new ManualResetEventSlim(false);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Write> _writes = new List<Write>();

        /// <summary>Запись поколения уже висит на задвижке.</summary>
        public bool Entered => _entered.IsSet;

        /// <summary>Закрыть задвижку: следующая запись поколения повиснет до <see cref="Release"/>.</summary>
        public void Hold()
        {
            _entered.Reset();
            _gate.Reset();
        }

        public void Release() => _gate.Set();

        public bool WaitEntered(int ms) => _entered.Wait(ms);

        /// <summary>Записи поколений <c>.save</c> по порядку окончания.</summary>
        public Write[] Generations()
        {
            lock (_writes)
            {
                return _writes.ToArray();
            }
        }

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public string[] ListFiles(string directory) => _inner.ListFiles(directory);

        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

        public void WriteAtomic(string path, ReadOnlySpan<byte> data)
        {
            bool generation = path.EndsWith(WorldFolder.GenerationSuffix, StringComparison.Ordinal);
            double begin = _clock.Elapsed.TotalMilliseconds;
            if (generation && !_gate.IsSet)
            {
                _entered.Set();
                _gate.Wait();
            }

            _inner.WriteAtomic(path, data);
            if (generation)
            {
                lock (_writes)
                {
                    _writes.Add(new Write(Path.GetFileName(path), begin, _clock.Elapsed.TotalMilliseconds));
                }
            }
        }

        public void MoveDurable(string from, string to, bool replaceExisting) => _inner.MoveDurable(from, to, replaceExisting);

        public void Delete(string path) => _inner.Delete(path);

        public IDisposable? TryLock(string path) => _inner.TryLock(path);

        internal readonly struct Write
        {
            public Write(string name, double beginMs, double endMs)
            {
                Name = name;
                BeginMs = beginMs;
                EndMs = endMs;
            }

            public string Name { get; }
            public double BeginMs { get; }
            public double EndMs { get; }
        }
    }

    /// <summary>
    /// Хранилище с обрывом питания (0-037, з. 1 проверки): носитель — <see cref="MemoryStorage"/>, а атомарная запись
    /// разложена на шаги, как на диске: половина <c>.tmp</c> → целый <c>.tmp</c> (сброшен) → перенос в конечное имя; каждое
    /// удаление и перенос — тоже шаг. <see cref="Arm"/>(k): перед k-м шагом «гаснет свет» — снимок носителя
    /// (<see cref="Frozen"/>), и дальше любая запись — отказ (<see cref="IOException"/>: процесса уже нет). Помнит, какие
    /// поколения целиком побывали на носителе: новейшее из них старт обязан загрузить (6.5).
    /// <see cref="Locked"/> — файлы, которые «держит антивирус»: удаление — отказ.
    /// </summary>
    internal sealed class CrashStorage : IPlatformStorage
    {
        private readonly MemoryStorage _disk = new MemoryStorage();
        private readonly object _sync = new object();
        private readonly List<string> _trace = new List<string>();
        private int _crashAt = -1;
        private int _steps;
        private bool _dead;
        private ulong _durable;

        /// <summary>Снимок носителя в миг обрыва; null — обрыва не было.</summary>
        public MemoryStorage? Frozen { get; private set; }

        public bool Crashed => Frozen != null;

        /// <summary>Перед каким шагом погас свет.</summary>
        public string? CrashedBefore { get; private set; }

        /// <summary>Новейшее поколение, целиком побывавшее на носителе к мигу обрыва (сейчас, если обрыва не было).</summary>
        public ulong NewestDurable { get; private set; }

        /// <summary>Файлы (имена), удаление которых — отказ.</summary>
        public HashSet<string> Locked { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Шаги носителя после <see cref="Arm"/>.</summary>
        public string[] Trace
        {
            get
            {
                lock (_sync)
                {
                    return _trace.ToArray();
                }
            }
        }

        /// <summary>Обрыв перед шагом <paramref name="crashAt"/> (с 0), считая от этого вызова.</summary>
        public void Arm(int crashAt)
        {
            lock (_sync)
            {
                _crashAt = crashAt;
                _steps = 0;
                _trace.Clear();
            }
        }

        public bool FileExists(string path) => _disk.FileExists(path);

        public bool DirectoryExists(string path) => _disk.DirectoryExists(path);

        public void CreateDirectory(string path) => _disk.CreateDirectory(path);

        public string[] ListFiles(string directory) => _disk.ListFiles(directory);

        public byte[] ReadAllBytes(string path) => _disk.ReadAllBytes(path);

        public void WriteAtomic(string path, ReadOnlySpan<byte> data)
        {
            byte[] bytes = data.ToArray();
            string tmp = path + WorldFolder.TmpSuffix;
            string name = Path.GetFileName(path);
            bool generation = WorldFolder.TryParseGeneration(name, out ulong number, out bool isTmp) && !isTmp;
            Step("пишем " + name + WorldFolder.TmpSuffix, () => _disk.WriteAtomic(tmp, bytes.AsSpan(0, bytes.Length / 2)));
            Step("сброшен " + name + WorldFolder.TmpSuffix, () =>
            {
                _disk.WriteAtomic(tmp, bytes);
                if (generation && number > _durable)
                {
                    _durable = number;
                    NewestDurable = number;
                }
            });
            Step("перенос в " + name, () => _disk.MoveDurable(tmp, path, true));
        }

        public void MoveDurable(string from, string to, bool replaceExisting) =>
            Step("перенос " + Path.GetFileName(from), () => _disk.MoveDurable(from, to, replaceExisting));

        public void Delete(string path)
        {
            string name = Path.GetFileName(path);
            if (Locked.Contains(name))
            {
                throw new IOException($"{name} держит другой процесс (тест)");
            }

            Step("удаляем " + name, () => _disk.Delete(path));
        }

        public IDisposable? TryLock(string path) => _disk.TryLock(path);

        private void Step(string what, Action apply)
        {
            lock (_sync)
            {
                if (_dead)
                {
                    throw new IOException("обрыв питания (тест): процесса уже нет — " + what);
                }

                if (_crashAt >= 0 && _steps == _crashAt)
                {
                    Frozen = _disk.Snapshot();
                    CrashedBefore = what;
                    _dead = true;
                    throw new IOException("обрыв питания (тест) перед шагом: " + what);
                }

                apply();
                if (_crashAt >= 0)
                {
                    _steps++;
                    _trace.Add(what);
                }
            }
        }
    }
}
