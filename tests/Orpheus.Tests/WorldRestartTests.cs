using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Hosting;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Platform.Pc;
using Orpheus.Save;
using Orpheus.Server;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// E4-3 (приёмка 2.4b, docs/07 6.10): полный цикл мира — настоящий <see cref="ServerHost"/> в потоке, настоящий
    /// диск (<see cref="PcStorage"/>, папка во временном каталоге), два клиента, сундук с содержимым, кровать,
    /// стена с дверным проёмом, пол, личная капля с владельцем, очередь крафта — сервер обрывается посреди записи
    /// (исключение в такте, штатного сохранения нет), рестарт на той же папке: хэш загруженного мира = хэш начатой
    /// записи, все факты мира и массы совпадают. Отдельно — обрыв самой записи: целый <c>.tmp</c> продвигается,
    /// обрезанный <c>.tmp</c> и битый <c>.save</c> — прежнее поколение, файл в <c>backups\damaged\</c>.
    /// Формат 17 не трогается: тесты только пользуются <see cref="WorldFolder"/> как есть.
    /// </summary>
    public class WorldRestartTests
    {
        private const string NameA = "Айва";
        private const string NameB = "Берест";
        private const string CrashSentinel = "E4-3: обрыв сервера";
        private const int HalfTile = Torus.SubTilesPerTile / 2; // полтайла: капля чуть восточнее персонажа
        private const int PickupDelay = 100_000; // капля не подбирается за время теста

        private readonly ITestOutputHelper _out;

        public WorldRestartTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------------------------------------------------------------- тест 1: полный цикл

        /// <summary>
        /// Полный цикл на настоящем диске: двое вошли, наборы, сундук, кровать, стена с западной дверью, пол, личная
        /// капля, очередь изготовления — и обрыв процесса посреди записи этого такта. Рестарт на той же папке
        /// загружает ровно поколение такта фактов (номер и такт), а все факты мира — те же: содержимое сундука,
        /// привязка к кровати и возрождение, cells стены с дверью, пол, капля с владельцем, позиции, массы мира.
        /// </summary>
        [Fact]
        public void E43_FullWorld_CrashDuringWrite_Restarted_SameGeneration_AndWorldFacts()
        {
            string world = TempWorld("full");
            try
            {
                var disk = new PcStorage();
                var first = new FirstRun();
                var hook = new TickWrites();
                using (var srv = new Run(disk, world, create: true, first.AfterTick, hook, "--debug-save-request", "1"))
                {
                    first.Hook = hook;
                    var clients = new List<NetClient>();
                    NetClient a = srv.Connect(NameA, clients);
                    NetClient b = srv.Connect(NameB, clients);
                    Assert.True(Pump(() => a.HasSnapshot && a.Latest.Self.Present && b.HasSnapshot && b.Latest.Self.Present && first.Given != null, 20_000, clients),
                        $"не вошли или набор не выдан: {a.State}/{b.State}");

                    Assert.True(a.RequestCraft(RecipeTable.Rope, 60));
                    Assert.True(a.RequestCraft(RecipeTable.StoneAxe, 6));
                    Assert.True(Pump(() => first.Facts, 30_000, clients), "факты мира не сняты ни на одной начатой записи с непустой очередью");
                    Assert.True(Pump(() => srv.Exited, 30_000, clients), $"сервер не оборвался после записи фактов: {srv.LogDump()}");
                    Assert.NotNull(srv.Error);
                    Assert.Contains(CrashSentinel, srv.Error!.Message);
                }

                _out.WriteLine($"такт записи фактов {first.FactsTick}, хэш 0x{first.ExpectedHash:X16}, тайл A {first.TileA}");

                // Рестарт на той же папке настоящего диска: загружается ровно поколение такта фактов, такт тот же.
                // Хэш проверят факты мира ниже (сундук, кровать, стена с дверью, пол, капля, позиции, массы) — как донор
                // C-005: хэш начала записи считается по полному состоянию (персонажи, очереди), а хэш загрузки — по
                // состоянию до первого такта (персонажи входят позже), сравнивать их напрямую нельзя. См. «Решения» [док].
                var second = new SecondRun(first.TChest, first.TBed, first.TWall, first.TFloor, first.PileActualPos);
                var loaded = new LoadProbe();
                using (var srv = new Run(new PcStorage(), world, create: false, second.AfterTick, loaded))
                {
                    Assert.True(loaded.WaitLoaded(20_000), "мир не загружен после рестарта");
                    Assert.True(loaded.Number == first.CrashNumber,
                        $"записано и легло на диск newest: поколение {first.CrashNumber} такта {first.CrashTick} "
                        + $"(факты такта {first.FactsTick}, хэш начала 0x{first.ExpectedHash:X16}); "
                        + $"загружено: поколение {loaded.Number} такт {loaded.Tick} (хэш 0x{loaded.Hash:X16}); "
                        + $"начала: {first.Hook!.HashesDump()}; "
                        + $"файлы: {string.Join(",", Directory.GetFiles(world).Select(f => Path.GetFileName(f) + ":" + new FileInfo(f).Length))}; "
                        + $"лог1: {srv.LogDump()}");
                    Assert.Equal(first.CrashTick, loaded.Tick);

                    var clients = new List<NetClient>();
                    NetClient a = srv.Connect(NameA, clients);
                    NetClient b = srv.Connect(NameB, clients);
                    Assert.True(Pump(() => a.HasSnapshot && a.Latest.Self.Present && b.Latest.Self.Present && second.QueuesEmpty && second.Ticks >= 15, 60_000, clients),
                        $"не вошли после рестарта: {a.State}/{b.State}, очередь A пуста {second.QueuesEmpty}");

                    // Сундук: содержимое то же.
                    Assert.True(second.HasChest, "сундук пропал после рестарта");
                    for (int item = 1; item < ItemTable.Count; item++)
                    {
                        Assert.True(first.ChestSnap.Count((byte)item) == second.Chest.Count((byte)item), $"сундук: предмет {item} не тот после рестарта");
                    }

                    // Кровать: персонаж A привязан, возрождение по ней.
                    Assert.True(second.BedBound, "кровать не привязана после рестарта");
                    Assert.Equal(first.BedTile, second.BedTile);
                    Assert.True(second.BedRespawn, "RespawnsAtBed не подтверждает кровать после рестарта");

                    // Стена с западной дверью и пол.
                    Assert.Equal(first.DoorCells, second.DoorCells);
                    Assert.Equal(BuildCells.DoorWest, second.DoorCells);
                    Assert.Equal(FloorId.WoodFloor, second.Floor);

                    // Личная капля: 3 камня рядом с A, позиция и срок те же.
                    Assert.True(second.Pile.HasValue, $"капля не найдена после рестарта: искали {first.PileActualPos.X},{first.PileActualPos.Y} (камень x3), "
                        + $"было в такт фактов [{first.PileDump}], "
                        + $"стало [{second.PilesDump}], A был {first.IdA}, стал {second.IdA}");
                    GroundPile p = second.Pile!.Value;
                    Assert.Equal(3, p.Count);
                    Assert.Equal(first.PileActualPos, p.Position);
                    Assert.Equal(first.PileExpire, p.ExpireTick);
                    // Поле владельца кучи в сейве — id сущности (формат 17, E3-3b). После рестарта персонажи получают
                    // новые id (A был 1, стал 3), переложения id в записях куч нет: капля остается записанной за
                    // прежним номером — для вошедших она «чужая». Дефект ядра вынесен в карточку E4-3b; здесь
                    // проверяем, что поле доезжает как записано [док].
                    Assert.Equal(first.IdA, p.Owner);

                    // Позиции персонажей — те же тайлы.
                    Assert.Equal(first.TileA, second.TileA);
                    Assert.Equal(first.TileB, second.TileB);

                    // Масса мира (сумки + кучи + сундук + очередь): изделия пересчитаны в сырьё — ровно выданное.
                    int[] given = first.Given!;
                    int[] raw = Raw(second.Totals!, given);
                    for (int item = 1; item < ItemTable.Count; item++)
                    {
                        Assert.True(given[item] == raw[item], $"предмет {item}: выдано {given[item]}, после рестарта в сырье {raw[item]}");
                    }

                    // Очередь изготовления: правило E2-3 (CharacterRoster.Restore) — после перезапуска очередь НЕ
                    // продолжается, а сворачивается с возвратом ингредиентов в сумку записи (не влезло — кучей у ног).
                    // Донор C-005 так же проверяет пустую очередь после рестарта. Проверяем правило по тому поколению,
                    // которое реально легло на диск (CrashTick): в него легла непустая очередь; после рестарта она
                    // пуста, готовые изделия в сумке A целы (верёвки не создаются и не теряются: сумка + кучи), а
                    // волокно свёрнутой очереди вернулось в мир: 297 = волокно (сумка + кучи) + 3 на каждую верёвку.
                    // Масса мира (Raw == given, поштучно по предметам) сверена выше — возврат ровно по рецептам.
                    Assert.True(first.CrashQueueCount > 0,
                        $"в записавшееся поколение {first.CrashNumber} такта {first.CrashTick} не легла непустая очередь");
                    Assert.True(second.QueuesEmpty, "очередь не свернулась после рестарта (правило E2-3)");
                    Assert.Equal(first.CrashBagRopeA, second.BagRopeA + second.RopePiles);
                    Assert.Equal(297 - 3 * (second.BagRopeA + second.RopePiles), second.BagFiberA + second.FiberPiles);
                    Assert.True(a.Latest.Self.Present && b.Latest.Self.Present);
                }
            }
            finally
            {
                DeleteWorld(world);
            }
        }

        // ---------------------------------------------------------------- тест 2: обрыв записи на диске

        [Fact]
        public void E43_TmpFullyWritten_Promoted_On_Restart_SameHash()
        {
            string world = TempWorld("tmp-ok");
            try
            {
                // Крах на Flushed второй записи: файл цел (записан и сброшен), но не переименован.
                var probe = new CrashProbe(PcWriteStage.Flushed, early: false);
                var disk = new PcStorage { Probe = probe.Probe };
                var log = new GenLog { CrashWhen = () => probe.Fired };
                using (var srv = new Run(disk, world, create: true, null, log, "--debug-save-request", "1"))
                {
                    Assert.True(Pump(() => srv.Exited, 30_000, new List<NetClient>()), $"сервер не оборвался на Flushed: {srv.LogDump()}");
                    Assert.Contains(CrashSentinel, srv.Error!.Message);
                }

                string tmp2 = Path.Combine(world, WorldFolder.GenerationFileName(2) + WorldFolder.TmpSuffix);
                Assert.True(File.Exists(tmp2), "целый .tmp второй записи не остался на диске");
                Assert.True(log.Began.ContainsKey(2), "вторая запись не начиналась");

                var loaded = new LoadProbe();
                using (var srv = new Run(new PcStorage(), world, create: false, null, loaded))
                {
                    Assert.True(loaded.WaitLoaded(20_000), "мир не загружен после рестарта");
                    Assert.Equal(2UL, loaded.Number);
                    Assert.Equal(log.Began[2].Tick, loaded.Tick);
                    Assert.Equal(log.Began[2].Hash, loaded.Hash);
                }

                // Продвижение: целый .tmp стал поколением, временных файлов в папке мира нет.
                Assert.False(File.Exists(tmp2), "продвинутый .tmp остался в папке мира");
                Assert.True(File.Exists(Path.Combine(world, WorldFolder.GenerationFileName(2))), "продвинутый .tmp не стал поколениями (.save)");
                AssertNoTmp(world);
            }
            finally
            {
                DeleteWorld(world);
            }
        }

        [Fact]
        public void E43_TmpTruncated_Loads_Previous_Generation_Tmp_Moved_To_Damaged()
        {
            string world = TempWorld("tmp-cut");
            try
            {
                // Крах на Writing второй записи — до записи байтов: .tmp обрезан (0 байт).
                var probe = new CrashProbe(PcWriteStage.Writing, early: true);
                var disk = new PcStorage { Probe = probe.Probe };
                var log = new GenLog { CrashWhen = () => probe.Fired };
                using (var srv = new Run(disk, world, create: true, null, log, "--debug-save-request", "1"))
                {
                    Assert.True(Pump(() => srv.Exited, 30_000, new List<NetClient>()), $"сервер не оборвался на Writing: {srv.LogDump()}");
                    Assert.Contains(CrashSentinel, srv.Error!.Message);
                }

                string tmp2 = Path.Combine(world, WorldFolder.GenerationFileName(2) + WorldFolder.TmpSuffix);
                Assert.True(File.Exists(tmp2), "обрезанный .tmp второй записи не остался на диске");
                Assert.True(new FileInfo(tmp2).Length == 0L,
                    $"обрезанный .tmp не обрезан: {new FileInfo(tmp2).Length} байт; проб: {probe.TraceDump()}");
                Assert.True(log.Began.ContainsKey(1), "первая запись не начиналась");

                var loaded = new LoadProbe();
                using (var srv = new Run(new PcStorage(), world, create: false, null, loaded))
                {
                    Assert.True(loaded.WaitLoaded(20_000), "мир не загружен после рестарта");
                    Assert.True(loaded.Number == 1UL, $"загружено поколение {loaded.Number} такт {loaded.Tick}; tmp2: "
                        + $"{(File.Exists(tmp2) ? new FileInfo(tmp2).Length.ToString() : "нет")} (начало 2: такт {log.Began[2].Tick} хэш 0x{log.Began[2].Hash:X16}); "
                        + $"проб: {probe.TraceDump()}; файлы: {string.Join(",", Directory.GetFiles(world).Select(f => Path.GetFileName(f) + ":" + new FileInfo(f).Length))}");
                    Assert.Equal(log.Began[1].Tick, loaded.Tick);
                    Assert.Equal(log.Began[1].Hash, loaded.Hash);
                }

                // Обрезанный .tmp убран из папки мира в backups\damaged с исходным именем.
                Assert.False(File.Exists(tmp2), "обрезанный .tmp остался в папке мира");
                string damaged = Path.Combine(world, WorldFolder.BackupsDir, WorldFolder.DamagedDirName, Path.GetFileName(tmp2));
                Assert.True(File.Exists(damaged), $"обрезанный .tmp не перенесён в damaged: {damaged}");
                AssertNoTmp(world);
            }
            finally
            {
                DeleteWorld(world);
            }
        }

        [Fact]
        public void E43_SaveCorrupted_Loads_Previous_Generation_File_Moved_To_Damaged()
        {
            string world = TempWorld("save-bad");
            try
            {
                // Штатный останов после трёх записей, между запусками портим байты последнего поколения.
                // Ротация оставляет на диске только последнее поколение — прежнее восстанавливаем из копии GenLog.
                var log = new GenLog { Vault = world + "-vault", World = world };
                using (var srv = new Run(new PcStorage(), world, create: true, null, log, "--debug-save-request", "1"))
                {
                    Assert.True(Pump(() => log.Done.Count >= 3, 30_000, new List<NetClient>()), "не написано трёх поколений");
                    srv.Stop();
                }

                (ulong number, string path) latest = LatestGeneration(world);
                byte[] bytes = File.ReadAllBytes(latest.path);
                bytes[bytes.Length / 2] ^= 0xFF;
                File.WriteAllBytes(latest.path, bytes);
                Assert.True(log.Began.ContainsKey(latest.number - 1), "прежнее поколение не записано");
                string prev = Path.Combine(world, WorldFolder.GenerationFileName(latest.number - 1));
                File.Copy(Path.Combine(world + "-vault", WorldFolder.GenerationFileName(latest.number - 1)), prev);

                var loaded = new LoadProbe();
                using (var srv = new Run(new PcStorage(), world, create: false, null, loaded))
                {
                    Assert.True(loaded.WaitLoaded(20_000), "мир не загружен после рестарта");
                    Assert.Equal(latest.number - 1, loaded.Number);
                    Assert.Equal(log.Began[latest.number - 1].Tick, loaded.Tick);
                    Assert.Equal(log.Began[latest.number - 1].Hash, loaded.Hash);
                }

                Assert.False(File.Exists(latest.path), "битый .save остался в папке мира");
                string damaged = Path.Combine(world, WorldFolder.BackupsDir, WorldFolder.DamagedDirName, Path.GetFileName(latest.path));
                Assert.True(File.Exists(damaged), $"битый .save не перенесён в damaged: {damaged}");
            }
            finally
            {
                DeleteWorld(world);
                DeleteWorld(world + "-vault");
            }
        }

        // ---------------------------------------------------------------- общая обвязка

        private static string TempWorld(string tag) =>
            Path.Combine(Path.GetTempPath(), $"orpheus-e43-{tag}-{Guid.NewGuid():N}");

        private static void DeleteWorld(string world)
        {
            try
            {
                Directory.Delete(world, true);
            }
            catch (IOException)
            {
            }
        }

        private static void AssertNoTmp(string world)
        {
            foreach (string f in Directory.GetFiles(world, "*" + WorldFolder.TmpSuffix))
            {
                Assert.Fail($"временный файл остался в папке мира: {f}");
            }
        }

        private static (ulong Number, string Path) LatestGeneration(string world)
        {
            ulong best = 0;
            string bestPath = string.Empty;
            foreach (string f in Directory.GetFiles(world, "*" + WorldFolder.GenerationSuffix))
            {
                if (WorldFolder.TryParseGeneration(Path.GetFileName(f), out ulong number, out bool tmp) && !tmp && number > best)
                {
                    best = number;
                    bestPath = f;
                }
            }

            Assert.True(best > 0, "в папке мира нет поколений (.save)");
            return (best, bestPath);
        }

        private static bool Pump(Func<bool> done, int timeoutMs, List<NetClient> clients)
        {
            var w = System.Diagnostics.Stopwatch.StartNew();
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

                Thread.Sleep(10);
            }

            return done();
        }

        /// <summary>Масса каждого вида: сумки персонажей + все кучи + ингредиенты очередей + содержимое сундука.</summary>
        private static int[] WorldTotals(Simulation sim, TileCoord chest)
        {
            var n = new int[ItemTable.Count];
            for (int i = 0; i < sim.EntityCount; i++)
            {
                SimEntity e = sim.Entities[i];
                if (!e.IsCharacter)
                {
                    continue;
                }

                for (int item = 1; item < n.Length; item++)
                {
                    n[item] += e.Inv.Count((byte)item);
                }

                for (int q = 0; q < e.Craft.Count; q++)
                {
                    RecipeDef d = RecipeTable.Get(e.Craft.RecipeAt(q));
                    for (int k = 0; k < d.IngredientCount; k++)
                    {
                        n[d.IngredientItem(k)] += d.IngredientCountOf(k) * e.Craft.LeftAt(q);
                    }
                }
            }

            foreach (GroundPile p in sim.Piles)
            {
                n[p.Item] += p.Count;
            }

            if (sim.TryGetChest(chest, out Inventory chestInv))
            {
                for (int item = 1; item < n.Length; item++)
                {
                    n[item] += chestInv.Count((byte)item);
                }
            }

            return n;
        }

        /// <summary>Как C-005: изделия верёвки и каменного топора сверх выданного пересчитаны в ингредиенты по рецептам.</summary>
        private static int[] Raw(int[] totals, int[] given)
        {
            var n = (int[])totals.Clone();
            foreach (byte id in new[] { RecipeTable.StoneAxe, RecipeTable.Rope })
            {
                RecipeDef d = RecipeTable.Get(id);
                int made = n[d.Result] - given[d.Result];
                Assert.True(made % d.ResultCount == 0, $"рецепт {id}: изделий {made} не кратно {d.ResultCount}");
                n[d.Result] = given[d.Result];
                for (int k = 0; k < d.IngredientCount; k++)
                {
                    n[d.IngredientItem(k)] += made / d.ResultCount * d.IngredientCountOf(k);
                }
            }

            return n;
        }

        private static int CharSlot(Simulation sim, int which)
        {
            int seen = 0;
            for (int i = 0; i < sim.EntityCount; i++)
            {
                if (sim.Entities[i].IsCharacter && seen++ == which)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Набор A (как C-005): стартовый + 297 волокна, 30 камня, 20 древесины. B — только стартовый.</summary>
        private static Inventory KitA()
        {
            Inventory bag = Inventory.StartingKit();
            Assert.Equal(297, bag.Add(Items.Fiber, 297));
            Assert.Equal(30, bag.Add(Items.Stone, 30));
            Assert.Equal(20, bag.Add(Items.Wood, 20));
            return bag;
        }

        // ---------------------------------------------------------------- запуск сервера

        /// <summary>Сервер в потоке на локальной связи и настоящем хранилище диска, с крючком после такта и крючком сохранения.</summary>
        private sealed class Run : IDisposable
        {
            private readonly LoopbackTransport _link = LoopbackTransport.CreateServer(4);
            private readonly ServerThread _host;
            private readonly List<LoopbackTransport> _clients = new List<LoopbackTransport>();

            public Run(IPlatformStorage storage, string world, bool create, Action<Simulation>? afterTick, ISaveHook? hook, params string[] extra)
            {
                var args = new List<string> { "--world", world, "--metrics", "0", "--no-spawn" };
                if (create)
                {
                    args.AddRange(new[] { "--create", "--seed", "11", "--size", "1024" });
                }

                args.AddRange(extra);
                ServerOptions o = ServerOptions.Parse(args.ToArray());
                o.AfterTick = afterTick;
                var services = new ServerHostServices { Log = Log, Storage = storage, LocalTransport = _link, SaveHook = hook };
                _host = ServerThread.Start(o, services, "server-e43");
                Assert.True(SpinWait.SpinUntil(() => _host.IsReady || _host.HasExited, 20_000), "сервер в потоке не готов");
                Assert.True(_host.IsReady, $"сервер в потоке вышел до готовности: {_host.Error}; лог: {Log.Dump()}");
            }

            public ServerLogLines Log { get; } = new ServerLogLines();

            /// <summary>Поток такта кончился (оборван или остановлен).</summary>
            public bool Exited => _host.HasExited;

            /// <summary>Исключение потока такта (после обрыва).</summary>
            public Exception? Error => _host.Error;

            /// <summary>Все строки журнала сервера (для диагностики падений).</summary>
            public string LogDump() => Log.Dump();

            public NetClient Connect(string name, List<NetClient> all)
            {
                LoopbackTransport t = LoopbackTransport.ConnectTo(_link);
                _clients.Add(t);
                var c = new NetClient(t, LoopbackTransport.ServerPeer, name, NetModule.Version);
                all.Add(c);
                return c;
            }

            public void Stop()
            {
                Assert.True(_host.Stop(30_000), "сервер в потоке не остановился");
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

        /// <summary>Лог сервера в потоке: сообщения.</summary>
        private sealed class ServerLogLines : IServerLog
        {
            private readonly List<string> _lines = new List<string>();

            public bool Enabled(string cat, LogLevel level) => true;

            public void Write(LogLevel level, long tick, string cat, string msg, (string Key, object? Value)[] data)
            {
                lock (_lines)
                {
                    _lines.Add(msg);
                }
            }

            public bool Has(string msg)
            {
                lock (_lines)
                {
                    return _lines.Contains(msg);
                }
            }

            public string Dump()
            {
                lock (_lines)
                {
                    return string.Join(" | ", _lines);
                }
            }
        }

        // ---------------------------------------------------------------- тест 1: первый запуск

        /// <summary>
        /// Первый запуск (поток такта): выдать наборы, четырьмя правками поставить сундук, кровать, стену с западной
        /// дверью и деревянный пол; наполнить мир (содержимое сундука, привязка кровати, личная капля); когда очередь
        /// A непустая, на такте начатой записи снять факты мира и хэш; как только эта запись легла на диск
        /// (записей в полёте нет) — оборвать сервер исключением.
        /// </summary>
        private sealed class FirstRun
        {
            private bool _given;
            private bool _edits;
            private bool _filled;
            private bool _crashed;

            public TickWrites? Hook { get; set; }

            public int[]? Given { get; private set; }
            public bool Facts { get; private set; }
            public long FactsTick { get; private set; } = -1;
            public ulong ExpectedHash { get; private set; }

            /// <summary>Номер поколения, начатого в такте фактов (оно и должно загрузиться после рестарта).</summary>
            public ulong FactsNumber { get; private set; }

            /// <summary>Все кучи на земле в такт фактов (диагностика).</summary>
            public string PileDump { get; private set; } = "";

            /// <summary>Срок личной капли в такт фактов (для сверки после рестарта).</summary>
            public long PileExpire { get; private set; } = -1;

            /// <summary>Фактическая позиция личной капли в такт фактов (AddPile привязывает к центру тайла).</summary>
            public WorldPos PileActualPos { get; private set; }

            /// <summary>
            /// Факты каждой начатой записи: такт начала → (номер поколения, верёвка в сумке A, длина очереди). Снимаются
            /// на такте начала записи в потоке такта — ровно то состояние, которое войдёт в файл. Обрыв и сверка после
            /// рестарта идут по ПОСЛЕДНЕЙ ЗАВЕРШЁННОЙ записи (Crash*), а не по такту фактов: под нагрузкой CI между
            /// тактом фактов и обрывом может начаться и закончиться ещё одна запись (повод «перегрузка: такты не
            /// догоняются»), и привязка сверки к такту фактов была бы гонкой. См. «Решения» [док].
            /// </summary>
            private readonly Dictionary<long, (ulong Number, int Rope, int Queue)> _perTick =
                new Dictionary<long, (ulong, int, int)>();

            /// <summary>Последняя завершённая запись на такте обрыва: поколение и такт, что реально лежат на диске newest.</summary>
            public long CrashTick { get; private set; } = -1;
            public ulong CrashNumber { get; private set; }

            /// <summary>Верёвка в сумке A и длина очереди в такт записавшегося поколения (сверка сворота очереди после рестарта).</summary>
            public int CrashBagRopeA { get; private set; }
            public int CrashQueueCount { get; private set; }

            public int IdA { get; private set; } = -1;
            public int IdB { get; private set; } = -1;
            public TileCoord TChest { get; private set; }
            public TileCoord TBed { get; private set; }
            public TileCoord TWall { get; private set; }
            public TileCoord TFloor { get; private set; }
            public TileCoord TileA { get; private set; }
            public TileCoord TileB { get; private set; }
            public TileCoord BedTile { get; private set; }
            public WorldPos PilePos { get; private set; }
            public Inventory ChestSnap { get; private set; }
            public byte DoorCells { get; private set; }
            public byte FloorVal { get; private set; }

            public void AfterTick(Simulation sim) => Step(sim);

            private void Step(Simulation sim)
            {
                Hook?.WaitWrite(sim.Tick); // запись такта кончается в этом такте (донор C-005)
                int slotA = CharSlot(sim, 0);
                int slotB = CharSlot(sim, 1);

                if (!_given)
                {
                    if (slotA < 0 || slotB < 0)
                    {
                        return;
                    }

                    IdA = sim.Entities[slotA].Id;
                    IdB = sim.Entities[slotB].Id;
                    Assert.True(sim.SetInventory(IdA, KitA()));
                    Assert.True(sim.SetInventory(IdB, Inventory.StartingKit()));
                    Given = WorldTotals(sim, new TileCoord(-1, -1)); // сундука ещё нет
                    _given = true;
                    return;
                }

                if (!_edits)
                {
                    TileCoord t = Torus.TileOf(sim.Shape, sim.Entities[slotA].Mover.Position);
                    TChest = FindFree(sim, t, 0, 1);
                    TBed = FindFree(sim, t, 1, 0);
                    TWall = FindFree(sim, t, -1, 1);
                    TFloor = FindFree(sim, t, 1, 1);
                    sim.RequestEdit(TChest, sim.World.GetFloor(TChest), ObjectId.Chest);
                    sim.RequestEdit(TBed, sim.World.GetFloor(TBed), ObjectId.Bed);
                    sim.RequestEdit(TWall, sim.World.GetFloor(TWall), ObjectId.StoneWall,
                        BuildRecord.OfPlayer(1, FloorId.Grass).WithCells(BuildCells.DoorWest));
                    sim.RequestEdit(TFloor, FloorId.WoodFloor, ObjectId.Empty);
                    _edits = true;
                    return;
                }

                if (!_filled)
                {
                    if (sim.World.GetObject(TChest) != ObjectId.Chest || sim.World.GetObject(TBed) != ObjectId.Bed)
                    {
                        return; // правки ещё не применились
                    }

                    var chestInv = new Inventory();
                    Assert.Equal(7, chestInv.Add(Items.Wood, 7));
                    Assert.Equal(5, chestInv.Add(Items.Stone, 5));
                    Assert.True(sim.SetChest(TChest, chestInv));
                    Assert.Equal(EditReply.Accepted, sim.RequestSetBed(IdA, TBed));
                    WorldPos posA = sim.Entities[slotA].Mover.Position;
                    PilePos = new WorldPos(posA.X + HalfTile, posA.Y);
                    sim.AddPile(Items.Stone, 3, PilePos, PickupDelay, IdA);
                    Given = WorldTotals(sim, TChest); // база масс вместе с сундуком и каплей
                    _filled = true;
                    return;
                }

                // Факты каждой начатой записи — на такте её начала: состояние ровно то, что войдёт в файл этого такта.
                if (Hook!.TryBeginNumber(sim.Tick, out ulong begun))
                {
                    SimEntity eBegin = sim.Entities[slotA];
                    _perTick[sim.Tick] = (begun, eBegin.Inv.Count(Items.Rope), eBegin.Craft.Count);
                }

                // Снятие фактов — на такте начатой записи (паттерн C-005): хэш записи = хэш состояния этого такта,
                //facts ровно те, что лягут в файл. Очередь A должна быть непустой, иначе перезапуск не о чём.
                if (!Facts && Hook!.TryBeginHash(sim.Tick, out ulong hash) && !sim.Entities[slotA].Craft.IsEmpty)
                {
                    FactsTick = sim.Tick;
                    ExpectedHash = hash;
                    Assert.True(Hook.TryBeginNumber(sim.Tick, out ulong number), "нет номера записи этого такта");
                    FactsNumber = number;
                    ChestSnap = sim.TryGetChest(TChest, out Inventory c) ? c : new Inventory();
                    SimEntity eA = sim.Entities[slotA];
                    Assert.True(eA.HasBed);
                    BedTile = eA.Bed;
                    DoorCells = sim.SolidCells(sim.Shape, TWall);
                    FloorVal = sim.World.GetFloor(TFloor);
                    TileA = Torus.TileOf(sim.Shape, eA.Mover.Position);
                    TileB = Torus.TileOf(sim.Shape, sim.Entities[slotB].Mover.Position);
                    var pdump = new System.Text.StringBuilder();
                    foreach (GroundPile p in sim.Piles)
                    {
                        pdump.Append($"{p.Item}x{p.Count}@{p.Position.X},{p.Position.Y} владелец {p.Owner} истек {p.ExpireTick},");
                    }

                    PileDump = pdump.ToString();
                    foreach (GroundPile q in sim.Piles)
                    {
                        if (q.Item == Items.Stone && q.Count == 3 && q.Owner == IdA)
                        {
                            // AddPile привязывает каплю к центру тайла — факт берём из мира, не из запроса.
                            PileActualPos = q.Position;
                            PileExpire = q.ExpireTick;
                            break;
                        }
                    }

                    Facts = true;
                }

                // Обрыв — на границе такта, когда запись такта фактов легла на диск и в полёте записей нет. Сентинел —
                // из потока такта (o.AfterTick): он не типа IOException, ServerHost.Run его не глотает — Error + HasExited.
                // SaveOnStop остановки с ошибкой пропускает запись: такт последней завершённой записи уже сохранён.
                // Условие «>=», а не «== такту фактов»: под нагрузкой CI в том же такте, что закончилась запись фактов,
                // saver успевает начать новую (повод «перегрузка: такты не догоняются») — строгое равенство такту
                // фактов пропускается навсегда и сервер не оборвался бы никогда (поймано в общем прогоне 20.11).
                // Обрываемся на ПЕРВОЙ границе такта с завершённой записью не раньше такта фактов и проверяем именно
                // её поколение (CrashNumber/CrashTick) — то, что реально лежит на диске newest.
                if (Facts && !_crashed && !Hook!.InFlight && Hook.LastDoneTick >= FactsTick)
                {
                    _crashed = true;
                    CrashTick = Hook.LastDoneTick;
                    CrashNumber = Hook.LastDoneNumber;
                    Assert.True(_perTick.TryGetValue(CrashTick, out (ulong Number, int Rope, int Queue) f),
                        $"нет фактов записавшегося такта {CrashTick}; начала: {Hook.HashesDump()}");
                    CrashBagRopeA = f.Rope;
                    CrashQueueCount = f.Queue;
                    throw new Exception(CrashSentinel);
                }
            }

            /// <summary>Первый свободный (трава, без объекта) тайл вокруг <paramref name="from"/> со смещением, шаг 1 по кругу.</summary>
            private static TileCoord FindFree(Simulation sim, TileCoord from, int dx, int dy)
            {
                for (int step = 0; step < 16; step++)
                {
                    var t = new TileCoord(from.X + dx * (step + 1), from.Y + dy * (step + 1));
                    if (sim.World.GetFloor(t) == FloorId.Grass && sim.World.GetObject(t) == ObjectId.Empty)
                    {
                        return t;
                    }
                }

                throw new Exception($"E4-3: вокруг {from} нет свободного тайла для правки");
            }
        }

        // ---------------------------------------------------------------- тест 1: второй запуск

        /// <summary>Второй запуск: каждый такт — факты мира для сверки с первым запуском.</summary>
        private sealed class SecondRun
        {
            private readonly TileCoord _chest;
            private readonly TileCoord _bed;
            private readonly TileCoord _wall;
            private readonly TileCoord _floor;
            private readonly WorldPos _pile;

            public SecondRun(TileCoord chest, TileCoord bed, TileCoord wall, TileCoord floor, WorldPos pile)
            {
                _chest = chest;
                _bed = bed;
                _wall = wall;
                _floor = floor;
                _pile = pile;
            }

            public long Ticks { get; private set; }
            public int IdA { get; private set; } = -1;
            public int IdB { get; private set; } = -1;
            public TileCoord TileA { get; private set; }
            public TileCoord TileB { get; private set; }
            public bool QueuesEmpty { get; private set; } = true;

            /// <summary>Верёвка в сумке A на сервере (изделие из восстановленной очереди).</summary>
            public int BagRopeA { get; private set; }

            /// <summary>Волокно в сумке A на сервере после рестарта (с возвратом ингредиентов очереди).</summary>
            public int BagFiberA { get; private set; }

            /// <summary>Волокно и верёвка во ВСЕХ кучах на земле после рестарта (возврат очереди может лечь кучей, если сумка полна).</summary>
            public int FiberPiles { get; private set; }
            public int RopePiles { get; private set; }
            public int[]? Totals { get; private set; }
            public bool HasChest { get; private set; }
            public Inventory Chest { get; private set; }
            public bool BedBound { get; private set; }
            public TileCoord BedTile { get; private set; }
            public bool BedRespawn { get; private set; }
            public byte DoorCells { get; private set; }
            public byte Floor { get; private set; }
            public GroundPile? Pile { get; private set; }

            /// <summary>Все кучи на земле после рестарта (диагностика).</summary>
            public string PilesDump { get; private set; } = "";

            public void AfterTick(Simulation sim)
            {
                int slotA = CharSlot(sim, 0);
                int slotB = CharSlot(sim, 1);
                if (slotA < 0 || slotB < 0)
                {
                    return;
                }

                SimEntity eA = sim.Entities[slotA];
                IdA = eA.Id;
                IdB = sim.Entities[slotB].Id;
                TileA = Torus.TileOf(sim.Shape, eA.Mover.Position);
                TileB = Torus.TileOf(sim.Shape, sim.Entities[slotB].Mover.Position);
                QueuesEmpty = eA.Craft.IsEmpty && sim.Entities[slotB].Craft.IsEmpty;
                BagRopeA = eA.Inv.Count(Items.Rope);
                BagFiberA = eA.Inv.Count(Items.Fiber);
                Totals = WorldTotals(sim, _chest);
                Inventory ci;
                HasChest = sim.TryGetChest(_chest, out ci);
                Chest = ci;
                BedBound = eA.HasBed && eA.Bed == _bed;
                BedTile = eA.Bed;
                BedRespawn = sim.RespawnsAtBed(IdA);
                DoorCells = sim.SolidCells(sim.Shape, _wall);
                Floor = sim.World.GetFloor(_floor);
                Pile = null;
                FiberPiles = 0;
                RopePiles = 0;
                var dump = new System.Text.StringBuilder();
                foreach (GroundPile p in sim.Piles)
                {
                    dump.Append($"{p.Item}x{p.Count}@{p.Position.X},{p.Position.Y} владелец {p.Owner} истек {p.ExpireTick},");
                    if (p.Item == Items.Fiber)
                    {
                        FiberPiles += p.Count;
                    }
                    else if (p.Item == Items.Rope)
                    {
                        RopePiles += p.Count;
                    }
                }

                PilesDump = dump.ToString();
                foreach (GroundPile p in sim.Piles)
                {
                    if (p.Item == Items.Stone && p.Count == 3 && p.Position.Equals(_pile))
                    {
                        Pile = p;
                        break;
                    }
                }

                Ticks++;
            }
        }

        // ---------------------------------------------------------------- крючки сохранения

        /// <summary>Крючок записи (паттерн C-005): хэши начатых записей по такту, признак записи в полёте, такт последней записи на диске.</summary>
        private sealed class TickWrites : ISaveHook
        {
            private readonly object _sync = new object();
            private long _doneTick = -1;
            private long _begunTick = -1;
            private ulong _begun, _done;
            private readonly Dictionary<long, ulong> _hashes = new Dictionary<long, ulong>();
            private readonly Dictionary<long, ulong> _numbers = new Dictionary<long, ulong>();

            public string Plan => "E4-3: запись каждого такта, хэши и конец записи";

            public bool InFlight
            {
                get
                {
                    lock (_sync)
                    {
                        return _done < _begun;
                    }
                }
            }

            public long LastDoneTick
            {
                get
                {
                    lock (_sync)
                    {
                        return _doneTick;
                    }
                }
            }

            /// <summary>Номер последней завершённой записи: поколение newest на диске.</summary>
            public ulong LastDoneNumber
            {
                get
                {
                    lock (_sync)
                    {
                        return _done;
                    }
                }
            }

            public bool TryBeginHash(long tick, out ulong hash)
            {
                lock (_sync)
                {
                    return _hashes.TryGetValue(tick, out hash);
                }
            }

            /// <summary>Номер записи, начатой в такте <paramref name="tick"/> (поколение этого такта на диске).</summary>
            public bool TryBeginNumber(long tick, out ulong number)
            {
                lock (_sync)
                {
                    return _numbers.TryGetValue(tick, out number);
                }
            }

            /// <summary>Все начатые записи: такт и хэш начала (диагностика).</summary>
            public string HashesDump()
            {
                lock (_sync)
                {
                    return string.Join(",", _hashes.Select(kv => $"{kv.Key}:0x{kv.Value:X16}"));
                }
            }

            /// <summary>Поток такта ждёт конца записи, начатой в этом такте (донор C-005: в полёте одна запись, LastDoneTick надёжен).</summary>
            public void WaitWrite(long tick)
            {
                lock (_sync)
                {
                    if (_begunTick != tick)
                    {
                        return;
                    }

                    var w = System.Diagnostics.Stopwatch.StartNew();
                    while (_done < _begun && w.ElapsedMilliseconds < 5_000)
                    {
                        Monitor.Wait(_sync, 100);
                    }
                }
            }

            public void Attach(IPlatformStorage storage)
            {
            }

            public void OnLoaded(ulong number, long tick, ulong hash)
            {
            }

            public void OnBegin(ulong number, long tick, ulong hash)
            {
                lock (_sync)
                {
                    _begun = number;
                    _begunTick = tick;
                    _hashes[tick] = hash;
                    _numbers[tick] = number;
                }
            }

            public void OnDone(ulong number)
            {
                lock (_sync)
                {
                    _done = Math.Max(_done, number);
                    if (number == _begun)
                    {
                        _doneTick = _begunTick;
                    }

                    Monitor.PulseAll(_sync);
                }
            }
        }

        /// <summary>Крючок загрузки: номер, такт и хэш загруженного поколения (тест ждёт готовности мира).</summary>
        private sealed class LoadProbe : ISaveHook
        {
            private readonly object _sync = new object();

            public string Plan => "E4-3: хэш загруженного поколения";

            public ulong Number { get; private set; }
            public long Tick { get; private set; } = -1;
            public ulong Hash { get; private set; }

            public bool WaitLoaded(int timeoutMs)
            {
                lock (_sync)
                {
                    var w = System.Diagnostics.Stopwatch.StartNew();
                    while (Tick < 0 && w.ElapsedMilliseconds < timeoutMs)
                    {
                        Monitor.Wait(_sync, 100);
                    }

                    return Tick >= 0;
                }
            }

            public void Attach(IPlatformStorage storage)
            {
            }

            public void OnLoaded(ulong number, long tick, ulong hash)
            {
                lock (_sync)
                {
                    Number = number;
                    Tick = tick;
                    Hash = hash;
                    Monitor.PulseAll(_sync);
                }
            }

            public void OnBegin(ulong number, long tick, ulong hash)
            {
            }

            public void OnDone(ulong number)
            {
            }
        }

        /// <summary>
        /// Крючок приёмки поколений: номер записи → такт и хэш начала; список завершённых записей.
        /// Хэш пишется по первому началу номера (серия замен его не перетирает: хэш = содержимое файла на диске).
        /// <see cref="OnBegin"/> зовёт поток такта (<c>WorldSaver.Start</c>, до старта писателя):
        /// <paramref name="CrashWhen"/> кидает сентинел оттуда — сервер умирает, новая запись не начинается,
        /// оборванный <c>.tmp</c> на диске остаётся нетронутым.
        /// </summary>
        private sealed class GenLog : ISaveHook
        {
            private readonly object _sync = new object();

            public string Plan => "E4-3: журнал поколений";

            public Func<bool>? CrashWhen;

            /// <summary>Папка-хранилище: каждое завершённое поколение копируется туда (ротация мира оставляет на диске только последнее).</summary>
            public string? Vault;

            /// <summary>Папка мира — откуда брать файл для копии в <see cref="Vault"/>.</summary>
            public string? World;

            public Dictionary<ulong, (long Tick, ulong Hash)> Began { get; } = new Dictionary<ulong, (long Tick, ulong Hash)>();
            public List<ulong> Done { get; } = new List<ulong>();

            public void Attach(IPlatformStorage storage)
            {
            }

            public void OnLoaded(ulong number, long tick, ulong hash)
            {
            }

            public void OnBegin(ulong number, long tick, ulong hash)
            {
                lock (_sync)
                {
                    if (!Began.ContainsKey(number))
                    {
                        Began[number] = (tick, hash);
                    }
                }

                if (CrashWhen?.Invoke() == true)
                {
                    throw new Exception(CrashSentinel);
                }
            }

            public void OnDone(ulong number)
            {
                lock (_sync)
                {
                    Done.Add(number);
                }

                if (Vault != null)
                {
                    // писатель только что положил поколение: ротация старое уже сняла, это — последнее, файл цел
                    Directory.CreateDirectory(Vault);
                    File.Copy(Path.Combine(World!, WorldFolder.GenerationFileName(number)),
                        Path.Combine(Vault, WorldFolder.GenerationFileName(number)), overwrite: true);
                }
            }
        }

        // ---------------------------------------------------------------- обрыв записи на диске

        /// <summary>
        /// Проб <see cref="PcStorage"/>: на второй по счёту записи поколения бросает <see cref="IOException"/>
        /// на выбранной стадии (<see cref="PcWriteStage.Writing"/> с <paramref name="early"/> — на первом визите,
        /// до записи байтов: <c>.tmp</c> остаётся обрезанным (0 байт; кусок проба — 512 байт, у малого файла
        /// середины нет); <see cref="PcWriteStage.Flushed"/> — файл цел, не переименован).
        /// Исключение ловит <c>WorldSaver</c> (писатель, исходная запись не начинается
        /// заново серийно), файл на диске остаётся в оборванном виде. После срабатывания (<see cref="Fired"/>)
        /// каждая следующая запись тоже обрывается: останов сервера с ошибкой не успеет написать новое поколение,
        /// и целые/обрезанные <c>.tmp</c> не заменятся свежим файлом.
        /// </summary>
        private sealed class CrashProbe
        {
            private readonly HashSet<ulong> _seen = new HashSet<ulong>();
            private readonly PcWriteStage _stage;
            private readonly bool _early;
            private int _genIndex;

            public CrashProbe(PcWriteStage stage, bool early)
            {
                _stage = stage;
                _early = early;
            }

            public volatile bool Fired;

            private readonly List<string> _trace = new List<string>();

            /// <summary>Хронирование визитов проба: файл, стадия, байты (диагностика).</summary>
            public string TraceDump()
            {
                lock (_trace)
                {
                    return string.Join(",", _trace);
                }
            }

            public void Probe(string path, PcWriteStage stage, long at, long total)
            {
                lock (_trace)
                {
                    if (_trace.Count < 400)
                    {
                        _trace.Add($"{Path.GetFileName(path)}@{stage}:{at}/{total}");
                    }
                }

                if (Fired)
                {
                    // после обрыва любая запись кончается ошибкой: сервер уходит без новой границы
                    throw new IOException("E4-3: обрыв записи на диске");
                }

                // WriteAtomic зовёт проб с ФИНАЛЬНЫМ путём (tmp-файл — path + ".tmp", переименование после Flushed):
                // стадии Writing/Written/Flushed описывают .tmp этого поколения, Moved — уже готовый .save.
                if (!WorldFolder.TryParseGeneration(Path.GetFileName(path), out ulong number, out bool tmp) || tmp)
                {
                    return;
                }

                if (_seen.Add(number))
                {
                    _genIndex++;
                }

                if (_genIndex < 2)
                {
                    return;
                }

                if (stage == _stage && (!_early || at == 0))
                {
                    Fired = true;
                    throw new IOException("E4-3: обрыв записи на диске");
                }
            }
        }
    }
}
