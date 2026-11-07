using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Orpheus.Core.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Hosting;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Platform.Pc;
using Orpheus.Server;
using Orpheus.WorldGen;

namespace Orpheus.Bots
{
    /// <summary>Итог одного бота.</summary>
    public sealed class BotStats
    {
        public int Index { get; set; }
        public string State { get; set; } = "";
        public int EntityId { get; set; }
        public string? RejectText { get; set; }
        public string? DisconnectReason { get; set; }
        public double SecondsInGame { get; set; }
        public long SnapshotsApplied { get; set; }
        public long SnapshotsStale { get; set; }
        public long SnapshotsMissed { get; set; }
        public long DeltasApplied { get; set; }
        public long DeltasUnmerged { get; set; }
        public double SnapshotsPerSecond { get; set; }
        public long InputsSent { get; set; }
        public long BadMessages { get; set; }
        public int RttMs { get; set; }
        public long SimSent { get; set; }
        public long SimSendLost { get; set; }
        public long SimReceived { get; set; }
        public long SimReceiveLost { get; set; }
        public double SimMeanDelayMs { get; set; }

        /// <summary>Предсказание (11.3; 0-025): сверок со сравнением в тот же кадр / из них больше 0,5 тайла.</summary>
        public long PredCompared { get; set; }

        /// <inheritdoc cref="PredCompared"/>
        public long PredOverHalfTile { get; set; }

        /// <summary>Наибольшее расхождение предсказания с сервером, unit (1/256 тайла).</summary>
        public int PredMaxDevUnits { get; set; }

        /// <summary>Правок предсказания (сверка не совпала).</summary>
        public long PredResynced { get; set; }

        /// <summary>Часы клиента: перестановок вперёд, назад, по «часам после фриза»; перенумераций кадров; owd, мс.</summary>
        public long ClockStepsForward { get; set; }

        /// <inheritdoc cref="ClockStepsForward"/>
        public long ClockStepsBack { get; set; }

        /// <inheritdoc cref="ClockStepsForward"/>
        public long ClockServerResets { get; set; }

        /// <inheritdoc cref="ClockStepsForward"/>
        public long FrameRenumbers { get; set; }

        /// <inheritdoc cref="ClockStepsForward"/>
        public double OwdMs { get; set; }

        /// <summary>Сообщений «часы» принято.</summary>
        public long ClocksReceived { get; set; }

        /// <summary><c>run_seam</c> (0-027): круги; не бегун — null.</summary>
        public List<RunSeamLap>? Laps { get; set; }

        /// <summary><c>run_seam</c>: точка появления (старт), unit.</summary>
        public WorldPos RunOrigin { get; set; }

        /// <summary><c>run_seam</c>: позиция в конце (последний снимок, <c>Self</c>), unit.</summary>
        public WorldPos RunEnd { get; set; }

        /// <summary><c>run_seam</c>: секунд от первого решения до конца четвёртого круга (0 — не дошёл).</summary>
        public double RunSeconds { get; set; }

        /// <summary><c>seam_watch</c> (0-027): замер наблюдателя; не наблюдатель — null.</summary>
        public SeamWatch? Watch { get; set; }

        /// <summary>
        /// Сценарий не выполнен: <c>run_seam</c> — не все 4 круга или невязка ≠ 0; <c>seam_watch</c> — скачок &gt; 0,22 тайла,
        /// нет замеров или бегущего не видно с какой-то стороны шва.
        /// </summary>
        public bool ScenarioFailed { get; set; }
    }

    /// <summary>Итог запуска ботов.</summary>
    public sealed class BotReport
    {
        public string Server { get; set; } = "";
        public string Script { get; set; } = "";
        public int Count { get; set; }
        public string Simulator { get; set; } = "";
        public double Seconds { get; set; }
        public int InGame { get; set; }
        public int Rejected { get; set; }

        /// <summary>Сервер, запущенный ботами (<c>--launch</c>) или найденный (<c>--find</c>): процесс; нет — 0.</summary>
        public int ServerPid { get; set; }

        /// <summary>Секунд от запуска сервера до готовности (<c>--launch</c>).</summary>
        public double ServerReadySeconds { get; set; }

        /// <summary>«Открыть для друзей»: порт; не открывали или не вышло — −1.</summary>
        public int OpenedPort { get; set; } = -1;

        /// <summary>Текст отключения у бота 0 (0-024: «Хост вышел…», «Связь с хостом потеряна»).</summary>
        public string? DisconnectText { get; set; }

        /// <summary>Отключений после входа (не по нашей воле) и неудачных подключений.</summary>
        public int Disconnects { get; set; }

        public List<BotStats> Bots { get; } = new List<BotStats>();

        /// <summary>Доля сверок предсказания с расхождением ≤ 0,5 тайла по всем ботам (2.13 п. 1); сверок нет — 0.</summary>
        public double PredWithinHalfTile
        {
            get
            {
                long all = 0, over = 0;
                foreach (BotStats b in Bots)
                {
                    all += b.PredCompared;
                    over += b.PredOverHalfTile;
                }

                return all > 0 ? (all - over) / (double)all : 0;
            }
        }

        /// <summary>Ботов, у которых сценарий не выполнен (<see cref="BotStats.ScenarioFailed"/>).</summary>
        public int ScenarioFailures
        {
            get
            {
                int n = 0;
                foreach (BotStats b in Bots)
                {
                    n += b.ScenarioFailed ? 1 : 0;
                }

                return n;
            }
        }

        /// <summary>0 — все вошли, ни одного отключения, сценарии (run_seam, seam_watch) выполнены; 1 — иначе.</summary>
        public int ExitCode => Disconnects == 0 && Rejected == 0 && InGame == Count && ScenarioFailures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Боты по сети (docs/07, 11.3): N клиентов в одном потоке, кадр 60 Гц на таймере высокого разрешения
    /// (<see cref="TickClock"/> сервера), у каждого — свой имитатор связи (2.10) со своим зерном.
    /// </summary>
    public static class BotRun
    {
        /// <summary>Кадров ввода в секунду (docs/07, 2.4).</summary>
        public const int FrameRate = 60;

        public static BotReport Run(BotOptions o, TextWriter? console, CancellationToken stop = default)
        {
            // «Игра хоста» (0-024): сервер — дочерним процессом лаунчера или найденный по файлу-замку.
            IServerInstance? server = null;
            if (o.LaunchServer != null)
            {
                server = new PcServerLauncher(o.LaunchServer).Start(o.Launch);
                UseEndpoint(o, server.Endpoint);
                console?.WriteLine($"server pid={server.Endpoint.ProcessId} port={server.Endpoint.Port} ready={server.ReadySeconds.ToString("0.000", CultureInfo.InvariantCulture)}");
                console?.Flush();
            }
            else if (o.FindWorld != null)
            {
                var found = new PcServerLauncher(PcServerLauncher.DefaultPath(AppContext.BaseDirectory)).FindRunning(o.FindWorld);
                if (found == null)
                {
                    throw new IOException($"Работающего сервера мира нет: {o.FindWorld} (файл-замок host.lock).");
                }

                UseEndpoint(o, found.Value);
                console?.WriteLine($"found pid={found.Value.ProcessId} port={found.Value.Port}");
                console?.Flush();
            }

            try
            {
                BotReport report = RunBots(o, console, stop);
                report.ServerPid = server?.Endpoint.ProcessId ?? report.ServerPid;
                report.ServerReadySeconds = server?.ReadySeconds ?? 0;
                return report;
            }
            finally
            {
                server?.Dispose(); // бот-хост закончил штатно — сервер останавливается штатно (сохраняет мир)
            }
        }

        private static void UseEndpoint(BotOptions o, ServerEndpoint e)
        {
            o.Host = e.Host;
            o.Port = e.Port;
            o.HostToken = e.Token;
        }

        private static BotReport RunBots(BotOptions o, TextWriter? console, CancellationToken stop)
        {
            var bots = new NetBot[o.Count];
            var sims = new NetSimTransport?[o.Count];
            var entered = new double[o.Count];
            var worlds = new Dictionary<ulong, Task<Simulation?>>();
            for (int i = 0; i < o.Count; i++)
            {
                ITransport t = LiteNetTransport.Connect(o.Host, o.Port);
                int serverPeer = ((LiteNetTransport)t).ServerPeer;
                NetSimSettings s = o.SimFor(i);
                if (s.IsActive)
                {
                    sims[i] = new NetSimTransport(t, s);
                    t = sims[i]!;
                }

                var client = new NetClient(t, serverPeer, $"{o.Name}{i}", o.Version);
                if (i == 0)
                {
                    client.HostToken = o.HostToken;
                }

                int index = i;
                if (o.OnSnapshot != null)
                {
                    client.SnapshotApplied = snap => o.OnSnapshot(index, snap, client.EntityId);
                }

                bots[i] = new NetBot(client, o.Script, i) { PredictionFactory = c => Predict(c, worlds, console) };
                entered[i] = -1;
            }

            console?.WriteLine($"боты: {o.Count} × {o.Script} → {o.Host}:{o.Port}, {o.Seconds:0.#} с, связь: {(o.SimFor(0).IsActive ? o.SimFor(0).ToString() : "без имитатора")}");
            var watch = Stopwatch.StartNew();
            var runStart = new double[o.Count];
            var runEnd = new double[o.Count];
            double finishedAt = -1;
            bool openSent = false;
            bool openReported = false;
            using (var clock = new TickClock(FrameRate))
            {
                double nextStatus = 10;
                while (watch.Elapsed.TotalSeconds < o.Seconds && !stop.IsCancellationRequested)
                {
                    long now = watch.Elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
                    o.OnFrame?.Invoke(now);
                    bool anyAlive = false;
                    for (int i = 0; i < bots.Length; i++)
                    {
                        bots[i].Frame(now);
                        NetClientState st = bots[i].Client.State;
                        if (st == NetClientState.InGame && entered[i] < 0)
                        {
                            entered[i] = watch.Elapsed.TotalSeconds;
                        }

                        anyAlive |= st == NetClientState.Connecting || st == NetClientState.LoggingIn || st == NetClientState.InGame;
                    }

                    if (!anyAlive)
                    {
                        break;
                    }

                    // run_seam / seam_watch: сценарий кончился сам у всех — ещё 1 с (последние снимки), и выход.
                    bool allFinished = true;
                    for (int i = 0; i < bots.Length; i++)
                    {
                        RunSeamBot? run = bots[i].Runner;
                        if (run != null && run.Started && runStart[i] == 0)
                        {
                            runStart[i] = watch.Elapsed.TotalSeconds;
                        }

                        if (run != null && run.Done && runEnd[i] == 0)
                        {
                            runEnd[i] = watch.Elapsed.TotalSeconds;
                            console?.WriteLine($"  бот {i}: run_seam — 4 круга за {runEnd[i] - runStart[i]:0.0} с, наибольшая невязка {run.MaxResidualUnits} unit");
                        }

                        allFinished &= bots[i].Finished;
                    }

                    if (allFinished && finishedAt < 0)
                    {
                        finishedAt = watch.Elapsed.TotalSeconds;
                    }

                    if (finishedAt >= 0 && watch.Elapsed.TotalSeconds - finishedAt >= 1)
                    {
                        break;
                    }

                    NetClient first = bots[0].Client;
                    if (o.OpenForFriends && !openSent && first.State == NetClientState.InGame)
                    {
                        first.RequestOpenForFriends();
                        openSent = true;
                    }

                    if (!openReported && first.OpenResult.HasValue)
                    {
                        openReported = true;
                        console?.WriteLine($"open-result opened={(first.OpenResult.Value.Opened ? 1 : 0)} port={first.OpenResult.Value.Port}");
                        console?.Flush();
                    }

                    if (console != null && watch.Elapsed.TotalSeconds >= nextStatus)
                    {
                        nextStatus += 10;
                        console.WriteLine($"  {watch.Elapsed.TotalSeconds:0} с: в игре {Count(bots, NetClientState.InGame)} из {bots.Length}, снимков у бота 0 — {bots[0].Client.SnapshotsApplied}{Progress(bots[0])}");
                    }

                    clock.WaitNext(stop);
                }
            }

            double seconds = watch.Elapsed.TotalSeconds;
            var report = new BotReport
            {
                Server = $"{o.Host}:{o.Port}",
                Script = o.Script,
                Count = o.Count,
                Simulator = o.SimFor(0).IsActive ? o.SimFor(0).ToString() : "нет",
                Seconds = seconds,
                OpenedPort = bots.Length > 0 && bots[0].Client.OpenResult is OpenResultMessage r && r.Opened ? r.Port : -1,
                DisconnectText = bots.Length > 0 ? bots[0].Client.DisconnectText : null,
            };

            for (int i = 0; i < bots.Length; i++)
            {
                NetClient c = bots[i].Client;
                var st = new BotStats
                {
                    Index = i,
                    State = c.State.ToString(),
                    EntityId = c.EntityId,
                    RejectText = c.RejectText,
                    DisconnectReason = c.DisconnectReason?.ToString(),
                    SecondsInGame = entered[i] >= 0 ? seconds - entered[i] : 0,
                    SnapshotsApplied = c.SnapshotsApplied,
                    SnapshotsStale = c.SnapshotsStale,
                    SnapshotsMissed = c.SnapshotsMissed,
                    DeltasApplied = c.DeltasApplied,
                    DeltasUnmerged = c.DeltasUnmerged,
                    InputsSent = c.InputsSent,
                    BadMessages = c.BadMessages,
                    ClockStepsForward = c.Clock.StepsForward,
                    ClockStepsBack = c.Clock.StepsBack,
                    ClockServerResets = c.Clock.ServerResets,
                    FrameRenumbers = c.FrameRenumbers,
                    OwdMs = c.Clock.OwdUs / 1000.0,
                    ClocksReceived = c.ClocksReceived,
                };
                LocalPrediction? pred = bots[i].Prediction;
                if (pred != null)
                {
                    st.PredCompared = pred.Compared;
                    st.PredOverHalfTile = pred.ComparedOverHalfTile;
                    st.PredMaxDevUnits = pred.MaxDevUnits;
                    st.PredResynced = pred.Resynced;
                }

                RunSeamBot? runner = bots[i].Runner;
                if (runner != null || o.Script == RunSeamBot.Name)
                {
                    st.Laps = runner != null ? new List<RunSeamLap>(runner.Laps) : new List<RunSeamLap>();
                    st.RunOrigin = runner?.Origin ?? default;
                    st.RunEnd = c.HasSnapshot ? c.Latest.Self.Position : default;
                    st.RunSeconds = runEnd[i] > 0 ? runEnd[i] - runStart[i] : 0;
                    st.ScenarioFailed = runner == null || !runner.AllExact;
                }

                SeamWatch? sw = bots[i].Watch;
                if (sw != null || o.Script == SeamWatchBot.Name)
                {
                    st.Watch = sw;
                    st.ScenarioFailed = sw == null || !sw.Passed;
                }

                st.SnapshotsPerSecond = st.SecondsInGame > 0 ? st.SnapshotsApplied / st.SecondsInGame : 0;
                if (sims[i] != null)
                {
                    NetSimStats send = sims[i]!.SendStats;
                    NetSimStats recv = sims[i]!.ReceiveStats;
                    st.SimSent = send.Packets;
                    st.SimSendLost = send.Lost;
                    st.SimReceived = recv.Packets;
                    st.SimReceiveLost = recv.Lost;
                    st.SimMeanDelayMs = recv.MeanDelayMs;
                }

                report.Bots.Add(st);
                if (c.State == NetClientState.InGame)
                {
                    report.InGame++;
                }
                else if (c.State == NetClientState.Rejected)
                {
                    report.Rejected++;
                }
                else
                {
                    report.Disconnects++;
                }
            }

            // RTT — до выхода: после отключения транспорт его не знает.
            for (int i = 0; i < bots.Length; i++)
            {
                report.Bots[i].RttMs = RttOf(bots[i], sims[i]);
            }

            Shutdown(bots, watch);
            if (o.ReportPath != null)
            {
                WriteReport(report, o.ReportPath);
            }

            console?.WriteLine($"итог: в игре {report.InGame} из {report.Count}, отказов {report.Rejected}, отключений {report.Disconnects}, " +
                               $"снимков/с у бота 0 — {(report.Bots.Count > 0 ? report.Bots[0].SnapshotsPerSecond : 0):0.0}, " +
                               $"предсказание ≤ 0,5 тайла — {report.PredWithinHalfTile:P3}");
            for (int i = 0; i < report.Bots.Count; i++)
            {
                BotStats b = report.Bots[i];
                if (b.RejectText != null)
                {
                    console?.WriteLine($"  бот {i}: {b.RejectText}");
                }

                if (b.Laps != null)
                {
                    console?.WriteLine($"  бот {i}: run_seam — кругов {b.Laps.Count} из {RunSeamBot.LapCount}, {b.RunSeconds:0.0} с{(b.ScenarioFailed ? " — НЕ ВЫПОЛНЕН" : "")}");
                    foreach (RunSeamLap l in b.Laps)
                    {
                        console?.WriteLine($"    {l}");
                    }
                }

                if (b.Watch != null)
                {
                    console?.WriteLine($"  бот {i}: seam_watch — {WatchText(b.Watch)}{(b.ScenarioFailed ? " — НЕ ВЫПОЛНЕН" : "")}");
                }
            }

            return report;
        }

        /// <summary>
        /// Предсказание бота (11.3): мир того же зерна и размера — проходимость как у сервера; хэш генерации не совпал —
        /// без предсказания (другая сборка генератора). Мир один на всех ботов и строится в фоне (генерация — секунды,
        /// кадр бота ждать не должен): пока не готов — null, бот спросит в следующем кадре.
        /// </summary>
        private static LocalPrediction? Predict(NetClient c, Dictionary<ulong, Task<Simulation?>> worlds, TextWriter? console)
        {
            ulong key = c.Accept.WorldSeed ^ ((ulong)(uint)c.Accept.MapWidth << 40);
            if (!worlds.TryGetValue(key, out Task<Simulation?>? build))
            {
                MapShape shape = c.Shape;
                ulong seed = c.Accept.WorldSeed;
                ulong hash = c.Accept.GenerationHash;
                build = Task.Run(() =>
                {
                    WorldModel world = WorldGenModule.Current.Generate(shape, seed);
                    if (StateHash.Generation(world) != hash)
                    {
                        // Тестовый мир run_seam (сервер --debug-run-lanes, 0-027): те же полосы — тот же хэш.
                        RunSeamLanes.Clear(world);
                    }

                    if (StateHash.Generation(world) != hash)
                    {
                        console?.WriteLine("боты: хэш генерации мира не совпал с сервером — без предсказания");
                        return null;
                    }

                    return (Simulation?)new Simulation(world, maxEntities: 1, maxEditsPerTick: 1, reserveBuildMaps: 0);
                });
                worlds[key] = build;
            }

            if (!build.IsCompleted)
            {
                return null;
            }

            Simulation? rules = build.Result;
            return rules == null ? null : new LocalPrediction(c.Shape, rules.Solid, new WorldPos(c.Shape.WidthUnits / 2, c.Shape.HeightUnits / 2));
        }

        /// <summary>Строка замера наблюдателя для консоли.</summary>
        public static string WatchText(SeamWatch w) => string.Format(CultureInfo.InvariantCulture,
            "кадров {0}, в показе {1} (x: восток {2} / запад {3}; y: юг {4} / север {5}), переходов шва x {6}, y {7}; " +
            "за 1/30 с: замеров {8}, наибольший сдвиг {9:0.000} т (окно {10:0.0} мс), сверх 0,22 — {11} (из них окно > 36,7 мс — {12}); " +
            "за 1/60 с: наибольший {13:0.000} т, сверх 0,11 — {14}; показ: на новейшем {15}, догон {16}, перестановок {17}",
            w.Frames, w.FramesInView, w.ViewEastOfX, w.ViewWestOfX, w.ViewSouthOfY, w.ViewNorthOfY, w.CrossingsX, w.CrossingsY,
            w.Steps30, w.MaxStep30Tiles, w.MaxStep30WindowUs / 1000.0, w.Over30, w.Over30LateWindow, w.MaxStep60Tiles, w.Over60,
            w.Interp.HoldFrames, w.Interp.CatchUpSteps, w.Interp.ClockResets);

        private static string Progress(NetBot b)
        {
            RunSeamBot? r = b.Runner;
            if (r != null && r.Started)
            {
                return r.Done ? ", run_seam: все круги" : $", run_seam: круг {r.Lap + 1}, пройдено {r.Travelled / (double)Torus.SubTilesPerTile:0} т";
            }

            SeamWatch? w = b.Watch;
            return w != null ? $", seam_watch: в показе {w.FramesInView} кадров, наибольший сдвиг {w.MaxStep30Tiles:0.000} т" : "";
        }

        private static int RttOf(NetBot bot, NetSimTransport? sim) => bot.Client.Transport.RoundTripMs(bot.Client.ServerPeer);

        private static int Count(NetBot[] bots, NetClientState state)
        {
            int n = 0;
            foreach (NetBot b in bots)
            {
                if (b.Client.State == state)
                {
                    n++;
                }
            }

            return n;
        }

        private static void Shutdown(NetBot[] bots, Stopwatch watch)
        {
            foreach (NetBot b in bots)
            {
                if (b.Client.State == NetClientState.InGame)
                {
                    b.Client.Disconnect();
                }
            }

            // Дать отключениям уйти, затем закрыть сокеты.
            var until = watch.Elapsed.TotalSeconds + 0.2;
            while (watch.Elapsed.TotalSeconds < until)
            {
                foreach (NetBot b in bots)
                {
                    b.Client.Poll(watch.Elapsed.Ticks / 10);
                }

                Thread.Sleep(10);
            }

            foreach (NetBot b in bots)
            {
                b.Client.Transport.Dispose();
            }
        }

        private static void WriteReport(BotReport r, string path)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var w = new Utf8JsonWriter(f, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            w.WriteStartObject();
            w.WriteString("server", r.Server);
            w.WriteString("script", r.Script);
            w.WriteNumber("count", r.Count);
            w.WriteString("simulator", r.Simulator);
            w.WriteNumber("seconds", Math.Round(r.Seconds, 3));
            w.WriteNumber("inGame", r.InGame);
            w.WriteNumber("rejected", r.Rejected);
            w.WriteNumber("disconnects", r.Disconnects);
            w.WriteNumber("exitCode", r.ExitCode);
            w.WriteNumber("scenarioFailures", r.ScenarioFailures);
            w.WriteNumber("predWithinHalfTile", Math.Round(r.PredWithinHalfTile, 6));
            w.WriteNumber("serverPid", r.ServerPid);
            w.WriteNumber("serverReadySeconds", Math.Round(r.ServerReadySeconds, 3));
            w.WriteNumber("openedPort", r.OpenedPort);
            if (r.DisconnectText != null)
            {
                w.WriteString("disconnectText", r.DisconnectText);
            }

            w.WriteStartArray("bots");
            foreach (BotStats b in r.Bots)
            {
                w.WriteStartObject();
                w.WriteNumber("index", b.Index);
                w.WriteString("state", b.State);
                w.WriteNumber("entityId", b.EntityId);
                if (b.RejectText != null)
                {
                    w.WriteString("reject", b.RejectText);
                }

                if (b.DisconnectReason != null)
                {
                    w.WriteString("disconnectReason", b.DisconnectReason);
                }

                w.WriteNumber("secondsInGame", Math.Round(b.SecondsInGame, 3));
                w.WriteNumber("snapshotsApplied", b.SnapshotsApplied);
                w.WriteNumber("snapshotsStale", b.SnapshotsStale);
                w.WriteNumber("snapshotsMissed", b.SnapshotsMissed);
                w.WriteNumber("deltasApplied", b.DeltasApplied);
                w.WriteNumber("deltasUnmerged", b.DeltasUnmerged);
                w.WriteNumber("snapshotsPerSecond", Math.Round(b.SnapshotsPerSecond, 2));
                w.WriteNumber("inputsSent", b.InputsSent);
                w.WriteNumber("badMessages", b.BadMessages);
                w.WriteNumber("rttMs", b.RttMs);
                w.WriteNumber("simSent", b.SimSent);
                w.WriteNumber("simSendLost", b.SimSendLost);
                w.WriteNumber("simReceived", b.SimReceived);
                w.WriteNumber("simReceiveLost", b.SimReceiveLost);
                w.WriteNumber("simMeanDelayMs", Math.Round(b.SimMeanDelayMs, 2));
                w.WriteNumber("predCompared", b.PredCompared);
                w.WriteNumber("predOverHalfTile", b.PredOverHalfTile);
                w.WriteNumber("predMaxDevUnits", b.PredMaxDevUnits);
                w.WriteNumber("predResynced", b.PredResynced);
                w.WriteNumber("clockStepsForward", b.ClockStepsForward);
                w.WriteNumber("clockStepsBack", b.ClockStepsBack);
                w.WriteNumber("clockServerResets", b.ClockServerResets);
                w.WriteNumber("clocksReceived", b.ClocksReceived);
                w.WriteNumber("frameRenumbers", b.FrameRenumbers);
                w.WriteNumber("owdMs", Math.Round(b.OwdMs, 2));
                w.WriteBoolean("scenarioFailed", b.ScenarioFailed);
                if (b.Laps != null)
                {
                    w.WriteStartObject("runSeam");
                    w.WriteNumber("seconds", Math.Round(b.RunSeconds, 3));
                    w.WriteNumber("originX", b.RunOrigin.X);
                    w.WriteNumber("originY", b.RunOrigin.Y);
                    w.WriteNumber("endX", b.RunEnd.X);
                    w.WriteNumber("endY", b.RunEnd.Y);
                    w.WriteStartArray("laps");
                    foreach (RunSeamLap l in b.Laps)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("index", l.Index);
                        w.WriteString("direction", l.DirectionName);
                        w.WriteNumber("frames", l.Steps);
                        w.WriteNumber("residualX", l.Residual.X);
                        w.WriteNumber("residualY", l.Residual.Y);
                        w.WriteNumber("approaches", l.Approaches);
                        w.WriteNumber("maxCrossUnits", l.MaxCrossUnits);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                if (b.Watch != null)
                {
                    SeamWatch s = b.Watch;
                    w.WriteStartObject("seamWatch");
                    w.WriteBoolean("passed", s.Passed);
                    w.WriteNumber("frames", s.Frames);
                    w.WriteNumber("framesInView", s.FramesInView);
                    w.WriteNumber("viewEastOfX", s.ViewEastOfX);
                    w.WriteNumber("viewWestOfX", s.ViewWestOfX);
                    w.WriteNumber("viewSouthOfY", s.ViewSouthOfY);
                    w.WriteNumber("viewNorthOfY", s.ViewNorthOfY);
                    w.WriteNumber("crossingsX", s.CrossingsX);
                    w.WriteNumber("crossingsY", s.CrossingsY);
                    w.WriteNumber("steps30", s.Steps30);
                    w.WriteNumber("maxStep30Tiles", Math.Round(s.MaxStep30Tiles, 4));
                    w.WriteNumber("maxStep30WindowMs", Math.Round(s.MaxStep30WindowUs / 1000.0, 2));
                    w.WriteNumber("over30", s.Over30);
                    w.WriteNumber("over30LateWindow", s.Over30LateWindow);
                    w.WriteNumber("maxWindowMs", Math.Round(s.MaxWindowUs / 1000.0, 2));
                    w.WriteNumber("steps60", s.Steps60);
                    w.WriteNumber("maxStep60Tiles", Math.Round(s.MaxStep60Tiles, 4));
                    w.WriteNumber("over60", s.Over60);
                    w.WriteNumber("interpHoldFrames", s.Interp.HoldFrames);
                    w.WriteNumber("interpCatchUpSteps", s.Interp.CatchUpSteps);
                    w.WriteNumber("interpClockResets", s.Interp.ClockResets);
                    w.WriteNumber("interpStepViolations", s.Interp.StepViolations);
                    w.WriteNumber("interpSeamSamples", s.Interp.SeamSamples);
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }
    }
}
