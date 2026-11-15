using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Orpheus.Core.Sim;
using Orpheus.Net;
using Orpheus.Net.Hosting;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Save;
using Orpheus.Server;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-005 (замечание 1 повторной проверки E2-3): перезапуск сервера с очередью изготовления — сквозь настоящий цикл
    /// <see cref="ServerHost"/> (сервер в потоке, локальная связь, хранилище в памяти). Игрок ставит очередь → сохранение
    /// в том же такте → сервер падает без штатного выхода (носитель берётся копией на этот миг, <see cref="MemoryStorage.Snapshot"/>)
    /// → сервер стартует из сохранения → игрок входит. Масса каждого вида (сумка + кучи + очередь) — та же.
    /// </summary>
    public class CraftRestartTests
    {
        private const string Name = "ремесленник";
        private readonly ITestOutputHelper _out;

        public CraftRestartTests(ITestOutputHelper output) => _out = output;

        /// <summary>
        /// Сохранение пишется на каждой границе такта (<c>--debug-save-request 1</c> — тот же путь
        /// <c>WorldSaver.AfterTick</c> → <c>WorldSave.Capture</c>, что у автосохранения), и крючок ждёт конца каждой записи —
        /// так снимок носителя содержит сохранение ровно того такта, в котором заявка изготовления снята с сумки. Именно в этом
        /// такте запись персонажа, обновляемая рассылкой снимков, может отставать от сущности: если сохранение возьмёт старую
        /// сумку (ещё с ингредиентами) и новую очередь, загрузка вернёт ингредиенты второй раз. Тест не зависит от того, идёт
        /// рассылка снимков в цикле сервера раньше сохранения или позже.
        /// </summary>
        [Fact]
        public void C005_Craft_QueueSaved_ServerCrashed_Restarted_PlayerBack_MassConserved()
        {
            var storage = new MemoryStorage();
            string world = Path.Combine(Path.GetTempPath(), "orpheus-c005-" + Guid.NewGuid().ToString("N")); // в памяти, на диске нет
            var first = new FirstRun();
            var hook = new WaitEveryWrite();
            using (var srv = new Run(storage, world, create: true, first.AfterTick, hook, "--debug-save-request", "1"))
            {
                first.Hook = hook;
                first.Storage = storage;
                var clients = new List<NetClient>();
                NetClient c = srv.Connect(Name, clients);
                Assert.True(Pump(() => c.HasSnapshot && c.Latest.Self.Present && c.Inv.Count(Items.Fiber) == 297, 10_000, clients), $"не вошёл или нет набора: {c.State}");

                // Две позиции: верёвка × 60 (180 из 297 волокна) и каменный топор × 6 (18 из 30 камня, 12 из 20 древесины — два вида
                // ингредиентов). Не всё сырьё: лишнее снятие ингредиентов (вдвое) не упирается в остаток сумки и теряет массу.
                Assert.True(c.RequestCraft(RecipeTable.Rope, 60));
                Assert.True(c.RequestCraft(RecipeTable.StoneAxe, 6));
                Assert.True(Pump(() => first.Crashed, 30_000, clients), "очередь не поставлена или на носителе нет записи, начатой с непустой очередью");
                // Падение: дальше этот сервер для теста не существует — его остановка пишет в исходное хранилище, не в снимок.
            }

            // На носителе — сохранение с непустой очередью; масса AtCrash снята в том же такте, что записан в это
            // сохранение (хэш состояния такта = хэш, с которым начата запись). Часть позиций могла успеть изготовиться —
            // изделия пересчитываются в ингредиенты по рецептам (Raw), и масса в сырье точно равна выданной.
            Assert.Null(first.Error);
            Assert.False(first.Queue.IsEmpty);
            _out.WriteLine($"такт падения {first.CrashTick}, очередь {first.Queue}");
            Assert.Equal(297, first.Given[Items.Fiber]);
            int[] rawAtCrash = Raw(first.AtCrash, first.Given);
            for (int item = 1; item < ItemTable.Count; item++)
            {
                Assert.True(first.Given[item] == rawAtCrash[item], $"предмет {item}: выдано {first.Given[item]}, в такте падения в сырье {rawAtCrash[item]}");
            }

            var second = new SecondRun();
            using (var srv = new Run(first.Frozen!, world, create: false, second.AfterTick, null))
            {
                Assert.True(srv.Log.Has("персонажи мира"), "персонажи мира не восстановлены после загрузки");
                var clients = new List<NetClient>();
                NetClient c = srv.Connect(Name, clients);
                Assert.True(Pump(() => c.HasSnapshot && c.Latest.Self.Present && second.Ticks >= 15, 10_000, clients), $"не вошёл снова: {c.State}");
                int[] after = second.Totals!;
                int[] rawAfter = Raw(after, first.Given);
                for (int item = 1; item < ItemTable.Count; item++)
                {
                    Assert.True(first.AtCrash[item] == after[item], $"предмет {item}: в такте падения {first.AtCrash[item]}, после перезапуска {after[item]}");
                    Assert.True(first.Given[item] == rawAfter[item], $"предмет {item}: выдано {first.Given[item]}, после перезапуска в сырье {rawAfter[item]}");
                }

                Assert.Equal(297, rawAfter[Items.Fiber]); // волокно сумки + 3 на каждую изготовленную верёвку
                Assert.True(second.QueueEmpty);
                Assert.Equal(c.Inv.Count(Items.Fiber), second.BagFiber); // вернувшийся видит ту же сумку
                Assert.Equal(0, srv.Stop().ExitCode);
            }

            Assert.False(Directory.Exists(world), "сохранение мимо хранилища: папка мира появилась на диске");
        }

        /// <summary>Набор: стартовый + три стопки волокна, 30 камня, 20 древесины.</summary>
        private static Inventory Kit()
        {
            Inventory bag = Inventory.StartingKit();
            Assert.Equal(297, bag.Add(Items.Fiber, 297));
            Assert.Equal(30, bag.Add(Items.Stone, 30));
            Assert.Equal(20, bag.Add(Items.Wood, 20));
            return bag;
        }

        /// <summary>Персонаж (единственный в мире) или −1.</summary>
        private static int Character(Simulation sim)
        {
            for (int i = 0; i < sim.EntityCount; i++)
            {
                if (sim.Entities[i].IsCharacter)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Масса каждого вида: сумка персонажа + все кучи + ингредиенты очереди изготовления.</summary>
        private static int[] Totals(Simulation sim, int slot)
        {
            var n = new int[ItemTable.Count];
            SimEntity e = sim.Entities[slot];
            for (int item = 1; item < n.Length; item++)
            {
                n[item] = e.Inv.Count((byte)item);
            }

            foreach (GroundPile p in sim.Piles)
            {
                n[p.Item] += p.Count;
            }

            for (int i = 0; i < e.Craft.Count; i++)
            {
                RecipeDef d = RecipeTable.Get(e.Craft.RecipeAt(i));
                for (int k = 0; k < d.IngredientCount; k++)
                {
                    n[d.IngredientItem(k)] += d.IngredientCountOf(k) * e.Craft.LeftAt(i);
                }
            }

            return n;
        }

        /// <summary>
        /// Масса в сырье: <paramref name="totals"/>, где каждое изделие поставленных рецептов (каменный топор, верёвка) сверх
        /// выданного набора <paramref name="given"/> пересчитано в свои ингредиенты по <see cref="RecipeTable"/>. Точно, без допусков.
        /// </summary>
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

        /// <summary>Первый запуск (поток такта): выдать набор, а в первом такте с непустой очередью — «упасть».</summary>
        private sealed class FirstRun
        {
            private volatile bool _crashed;
            private bool _given;
            private readonly Dictionary<long, (CraftQueue Queue, int[] Totals)> _savedTicks = new Dictionary<long, (CraftQueue Queue, int[] Totals)>();

            public WaitEveryWrite? Hook { get; set; }
            public MemoryStorage? Storage { get; set; }
            public bool Crashed => _crashed;
            public string? Error { get; private set; }
            public int[] Given { get; private set; } = Array.Empty<int>();
            public int[] AtCrash { get; private set; } = Array.Empty<int>();
            public CraftQueue Queue { get; private set; }
            public long CrashTick { get; private set; }
            public MemoryStorage? Frozen { get; private set; }

            public void AfterTick(Simulation sim)
            {
                Hook?.WaitWrite(sim.Tick);
                int slot = Character(sim);
                if (slot < 0 || _crashed || Storage == null)
                {
                    return;
                }

                if (!_given)
                {
                    Assert.True(sim.SetInventory(sim.Entities[slot].Id, Kit()));
                    Given = Totals(sim, slot);
                    _given = true;
                    return;
                }

                CraftQueue craft = sim.Entities[slot].Craft;
                if (craft.IsEmpty)
                {
                    return;
                }

                // Такт, в котором начата запись (Capture — в этом же такте, строкой выше o.AfterTick): масса и очередь
                // снимаются здесь и сверяются хэшем состояния с хэшем, с которым начата запись, — это ровно то состояние,
                // что ляжет в файл. Под нагрузкой запись кончается тактами позже (заявки сохранений между ними схлопнуты),
                // поэтому масса берётся из такта записи, а не из такта падения.
                if (Hook!.TryBeginHash(sim.Tick, out ulong savedHash))
                {
                    ulong now = StateHash.Compute(sim).Total;
                    if (now != savedHash)
                    {
                        Error ??= $"такт {sim.Tick}: хэш состояния {now:x16} ≠ хэшу записи {savedHash:x16}";
                    }

                    _savedTicks[sim.Tick] = (craft, Totals(sim, slot));
                }

                // «Падение» — когда незаконченной записи нет (новую запись начинает только поток такта, то есть не во время
                // этого вызова — копия носителя не разойдётся с LastDoneTick), а последняя законченная начата в такте с очередью.
                if (Hook.InFlight || !_savedTicks.TryGetValue(Hook.LastDoneTick, out (CraftQueue Queue, int[] Totals) saved))
                {
                    return;
                }

                AtCrash = saved.Totals;
                Queue = saved.Queue;
                CrashTick = Hook.LastDoneTick;
                Frozen = Storage.Snapshot();
                _crashed = true;
            }
        }

        /// <summary>Второй запуск (поток такта): масса у вошедшего персонажа.</summary>
        private sealed class SecondRun
        {
            private volatile int[]? _totals;
            private volatile int _ticks;

            public int[]? Totals => _totals;
            public int Ticks => _ticks;
            public bool QueueEmpty { get; private set; } = true;
            public int BagFiber { get; private set; }

            public void AfterTick(Simulation sim)
            {
                int slot = Character(sim);
                if (slot < 0)
                {
                    return;
                }

                QueueEmpty &= sim.Entities[slot].Craft.IsEmpty;
                BagFiber = sim.Entities[slot].Inv.Count(Items.Fiber);
                _totals = CraftRestartTests.Totals(sim, slot);
                _ticks++;
            }
        }

        /// <summary>Крючок записи: поток такта ждёт конца записи, начатой в этом такте (сохранение каждого такта — своё).</summary>
        private sealed class WaitEveryWrite : ISaveHook
        {
            private readonly object _sync = new object();
            private long _beginTick = -1;
            private long _doneTick = -1;
            private long _begunTick = -1;
            private ulong _begun, _done;
            private readonly Dictionary<long, ulong> _hashes = new Dictionary<long, ulong>();

            /// <summary>Есть начатая, но не законченная запись.</summary>
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

            /// <summary>Хэш состояния, с которым начата запись в такте <paramref name="tick"/>.</summary>
            public bool TryBeginHash(long tick, out ulong hash)
            {
                lock (_sync)
                {
                    return _hashes.TryGetValue(tick, out hash);
                }
            }

            public string Plan => "C-005: поток такта ждёт конца каждой записи";

            public long LastBeginTick
            {
                get
                {
                    lock (_sync)
                    {
                        return _beginTick;
                    }
                }
            }

            /// <summary>Такт, в котором начата последняя завершившаяся запись на носителе (−1 — записей не было).</summary>
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
                    _beginTick = tick;
                    _begun = number;
                    _begunTick = tick;
                    _hashes[tick] = hash;
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

            public void WaitWrite(long tick)
            {
                lock (_sync)
                {
                    if (_beginTick != tick)
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
        }

        /// <summary>Сервер в потоке (0-031) на локальной связи и хранилище в памяти, с крючком после такта.</summary>
        private sealed class Run : IDisposable
        {
            private readonly LoopbackTransport _link = LoopbackTransport.CreateServer(4);
            private readonly ServerThread _host;
            private readonly List<LoopbackTransport> _clients = new List<LoopbackTransport>();

            public Run(MemoryStorage storage, string world, bool create, Action<Simulation> afterTick, ISaveHook? hook, params string[] extra)
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
                _host = ServerThread.Start(o, services, "server-c005");
                Assert.True(SpinWait.SpinUntil(() => _host.IsReady || _host.HasExited, 20_000), "сервер в потоке не готов");
                Assert.True(_host.IsReady, $"сервер в потоке вышел до готовности: {_host.Error}");
            }

            public ServerLogLines Log { get; } = new ServerLogLines();

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

        /// <summary>Лог сервера в потоке: сообщения.</summary>
        private sealed class ServerLogLines : IServerLog
        {
            private readonly List<string> _lines = new List<string>();

            public bool Enabled(string cat, LogLevel level) => level >= LogLevel.Info;

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
        }
    }
}
