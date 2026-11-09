using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Orpheus.Core.Bots;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.Trig;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Hosting;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Save;
using Orpheus.WorldGen;

namespace Orpheus.Server
{
    /// <summary>Итог запуска сервера (для тестов и кода выхода).</summary>
    public sealed class ServerResult
    {
        /// <summary>0 — всё хорошо; 1 — повтор разошёлся с записью; 2 — ошибка ключей или данных.</summary>
        public int ExitCode { get; set; }

        /// <summary>Выполнено тактов в этом запуске.</summary>
        public long Ticks { get; set; }

        /// <summary>Такт, с которого начат запуск (0 — новый мир, иначе — такт загруженного сохранения).</summary>
        public long StartTick { get; set; }

        /// <summary>Номер загруженного поколения (0 — сохранений не было или не грузились).</summary>
        public ulong LoadedNumber { get; set; }

        /// <summary>Сохранений записано за запуск.</summary>
        public int SavesWritten { get; set; }

        /// <summary>Номер последнего записанного поколения.</summary>
        public ulong LastSavedNumber { get; set; }

        /// <summary>Из записанных сохранений — заменили последнее поколение серии поводов «вход/уход» (0-037).</summary>
        public int SavesReplaced { get; set; }

        /// <summary>Хэш состояния в конце запуска (после сохранения при остановке).</summary>
        public ulong FinalHash { get; set; }

        /// <summary>Хэши на каждом 300-м такте (если <c>--hash</c> или повтор).</summary>
        public List<StateHashResult> Hashes { get; } = new List<StateHashResult>();

        /// <summary>Секунд на цикл тактов (только такты, без создания мира).</summary>
        public double Seconds { get; set; }

        /// <summary>Во сколько раз быстрее реального времени: такты ÷ 30 ÷ секунды.</summary>
        public double SpeedX => Seconds > 0 ? Ticks / (double)Simulation.TickRate / Seconds : double.PositiveInfinity;

        /// <summary>Байт выделено потоком такта на такт в среднем (11.6: в установившейся игре — 0).</summary>
        public double AllocatedBytesPerTick { get; set; }

        /// <summary>Повтор: такт первого расхождения (−1 — не разошлось).</summary>
        public long DivergedTick { get; set; } = -1;

        /// <summary>Повтор: номер сущности, где разошлось (−1 — сущности сошлись, разошёлся мир).</summary>
        public int DivergedEntity { get; set; } = -1;

        /// <summary>Повтор: точек сверки проверено.</summary>
        public int CheckpointsVerified { get; set; }

        /// <summary>Сеть: порт, который слушал сервер (−1 — сети не было).</summary>
        public int Port { get; set; } = -1;

        /// <summary>Сеть: входов принято.</summary>
        public int LoginsAccepted { get; set; }

        /// <summary>Сеть: входов отклонено.</summary>
        public int LoginsRejected { get; set; }

        /// <summary>Сеть: отключений игроков, бывших в игре.</summary>
        public int Disconnects { get; set; }

        /// <summary>Сеть: снимков отправлено.</summary>
        public long SnapshotsSent { get; set; }

        /// <summary>Сеть: снимков, обрезанных по бюджету 1200 байт.</summary>
        public long SnapshotsTruncated { get; set; }

        /// <summary>Сеть: наибольший снимок, байт.</summary>
        public int MaxSnapshotBytes { get; set; }

        /// <summary>Сеть: дельт отправлено (0-014).</summary>
        public long DeltasSent { get; set; }

        /// <summary>Сеть: байт в дельтах (0-014).</summary>
        public long DeltaBytes { get; set; }

        /// <summary>Сеть: контрольных полных кадров отправлено (0-014).</summary>
        public long FullFramesSent { get; set; }

        /// <summary>Сервер игры (0-024): вышел, потому что хозяин не вернулся за отведённое время («хост вышел»).</summary>
        public bool HostLeft { get; set; }

        /// <summary>Записей <c>host.lock</c> удалось (0-024).</summary>
        public int HostLockWrites { get; set; }

        /// <summary>Попыток записи <c>host.lock</c> не удалось — каждая повторена вне такта (0-024).</summary>
        public int HostLockFailures { get; set; }

        /// <summary>Сервер игры: хозяин возвращался после ухода (раз).</summary>
        public int HostReturns { get; set; }

        /// <summary>Сторона мира, тайлов (для отчёта; 0 — мир не открыт).</summary>
        public int WorldSize { get; set; }

        /// <summary>Хэш состояния сразу после загрузки поколения (0 — новый мир): «сохранили — загрузили — то же» (0-031).</summary>
        public ulong LoadedHash { get; set; }

        /// <summary>Сервер игры: порт «Открыть для друзей»; не открывали — −1.</summary>
        public int OpenedPort { get; set; } = -1;

        /// <summary>Метрики такта, памяти, сборок мусора и трафика за весь цикл тактов (0-030); null — цикла не было.</summary>
        public MetricsSample? Metrics { get; set; }

        /// <summary>Окна метрик, выведенные в лог (раз в <c>--metrics</c> с).</summary>
        public List<MetricsSample> MetricsWindows { get; } = new List<MetricsSample>();

        /// <summary>От запуска процесса до готовности (порт слушается; без сети — начало цикла тактов), с (0-030, приёмка 0.8).</summary>
        public double ReadySeconds { get; set; }

        /// <summary>Рабочий набор и его пик в миг готовности, МБ (0-030, приёмка 0.8).</summary>
        public double ReadyWorkingSetMb { get; set; }

        /// <inheritdoc cref="ReadyWorkingSetMb"/>
        public double ReadyPeakWorkingSetMb { get; set; }
    }

    /// <summary>
    /// Запуск сервера без сети (docs/07, 11.2): мир (создать или загрузить), сущности, источник ввода
    /// (боты или журнал), цикл такта — по часам (обычный режим) или подряд (<c>--sim</c>), хэш на каждом
    /// 300-м такте, запись и повтор журнала ввода, лог JSON-строками.
    /// <para>
    /// Папка мира (0-009, docs/07 6.3–6.5): замок <c>world.lock</c> на всё время работы; <c>--create</c> пишет
    /// <c>world.base</c> атомарно (уже есть — отказ); без <c>--create</c> — снимок генерации, проверка поколений
    /// и <c>.tmp</c> (<see cref="WorldFolder.Recover"/>), новейшее целое поколение поверх. Автосохранение и
    /// сохранение при остановке — <see cref="WorldSaver"/>; при повторе журнала мир берётся из снимка генерации
    /// и не сохраняется (повтор не меняет папку мира).
    /// </para>
    /// </summary>
    /// <para>
    /// 0-031 (docs/07 1.6, 2.3): цикл — библиотекой <c>netstandard2.1</c>. Всё платформенное — снаружи
    /// (<see cref="ServerHostServices"/>): хранилище (<c>IPlatformStorage</c>), часы такта, лог, рантайм, журналы. Программа
    /// <c>OrpheusServer</c> — тонкая оболочка <c>ServerRun</c> над этим классом (ПК, дочерний процесс по умолчанию); клиент в
    /// режиме «сервер в потоке» — <see cref="ServerThread"/> с локальной связью вместо UDP.
    /// </para>
    public static class ServerHost
    {
        /// <summary>Файл снимка генерации в папке мира (docs/07, 6.3).</summary>
        public const string BaseFileName = "world.base";

        /// <summary>
        /// Строка готовности в stdout (0-024): сервер слушает порт — лаунчер игры и скрипты ждут её, а не паузу
        /// «на всякий случай». Вид: <c>orpheus-ready port=P</c>.
        /// </summary>
        public const string ReadyMarker = "orpheus-ready";

        /// <summary>
        /// Генератор нового мира (1-017): при <c>--create</c> — по ключу <c>--gen</c> (<see cref="WorldGenModule.ForKey"/>, по умолчанию v1);
        /// при повторе журнала без папки мира — по версии генератора из журнала (старый журнал заглушки повторяется заглушкой).
        /// Загрузка мира генератор не зовёт: снимок генерации читается с диска со своей версией.
        /// </summary>
        public static IWorldGenerator GeneratorFor(ServerOptions o, JournalHeader? replay)
        {
            if (replay == null)
            {
                return WorldGenModule.ForKey(o.Gen);
            }

            return WorldGenModule.ForVersion(replay.GeneratorVersion)
                ?? throw new InvalidDataException($"Журнал записан генератором v{replay.GeneratorVersion}: в этой сборке такого нет.");
        }

        /// <summary>Запуск в текущем потоке до конца (<c>--ticks</c>, конец журнала, «хост вышел») или отмены <paramref name="stop"/>.</summary>
        public static ServerResult Run(ServerOptions o, ServerHostServices services, CancellationToken stop = default)
        {
            IServerLog log = services.Log ?? new TextServerLog(null);
            var result = new ServerResult();
            try
            {
                RunCore(o, log, services, result, stop);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException || e is UnauthorizedAccessException)
            {
                log.Error(result.Ticks, "admin", e.Message, ("exception", e.GetType().Name));
                result.ExitCode = 2;
            }

            return result;
        }

        private static void RunCore(ServerOptions o, IServerLog log, ServerHostServices services, ServerResult result, CancellationToken stop)
        {
            InputJournalReader? replay = o.ReplayPath != null
                ? new InputJournalReader(Need(services.OpenRead, "--replay")(o.ReplayPath))
                : null;
            try
            {
                using OpenedWorld opened = OpenWorld(o, replay?.Header, services.Storage, log);
                result.WorldSize = opened.World.Shape.Width;
                WorldModel world = opened.World;
                if (o.DebugRunLanes)
                {
                    // Тестовый мир run_seam (0-027): полосы на тайлах генерации — хэш генерации считается заново, бот
                    // с тем же расчищением получает тот же мир.
                    int cleared = RunSeamLanes.Clear(world);
                    opened.GenerationHash = StateHash.Generation(world);
                    log.Warn(0, "world", "отладка: полосы run_seam расчищены, появление у угла (0, 0)", ("tiles", cleared));
                }

                bool network = o.Port.HasValue || services.LocalTransport != null;
                int players = network ? o.MaxPlayers : 0;
                int loadedEntities = opened.Loaded?.Entities.Length ?? 0;
                var sim = new Simulation(world, maxEntities: Math.Max(64, Math.Max(CountBots(o), loadedEntities) + players + 1));
                if (opened.Loaded != null)
                {
                    opened.Loaded.RestoreInto(sim);
                    result.LoadedNumber = opened.Loaded.Number;
                    result.LoadedHash = StateHash.Compute(sim).Total;
                }

                // Персонажи мира (0-035): бывшие в мире на такте сохранения — теперь вне мира, их места — за ключом.
                // До ботов: боты берут загруженные сущности по порядку. После LoadedHash: хэш — состояния из файла.
                CharacterRoster characters = CharacterRoster.Restore(opened.Loaded?.Characters, sim);
                if (characters.Count > 0)
                {
                    log.Info(sim.Tick, "save", "персонажи мира", ("characters", characters.Count), ("entities", sim.EntityCount));
                }

                result.StartTick = sim.Tick;

                SimBot[] bots = Array.Empty<SimBot>();
                TileCoord spawnCenter = DefaultSpawnCenter(world.Shape);
                if (replay != null)
                {
                    foreach (JournalSpawn s in replay.Header.Spawns)
                    {
                        sim.AddEntityWithId(s.EntityId, s.Position, s.UnitsPerTickX10);
                    }

                    if (replay.Header.StartTick != 0)
                    {
                        throw new InvalidDataException($"Журнал начат с такта {replay.Header.StartTick}: повтор с середины пока не умеем (нужно сохранение).");
                    }
                }
                else
                {
                    spawnCenter = SpawnCenter(sim, o, log);
                    bots = SpawnBots(sim, o, spawnCenter);
                }

                using InputJournalWriter? record = o.RecordPath != null
                    ? new InputJournalWriter(Need(services.CreateWrite, "--record")(o.RecordPath), JournalHeader.Of(sim))
                    : null;

                log.Info(sim.Tick, "world", o.Sim ? "старт: режим симуляции" : "старт: обычный режим, такт по часам",
                    ("size", world.Shape.Width), ("seed", world.Seed), ("generator", world.GeneratorVersion),
                    ("entities", sim.EntityCount), ("bots", BotsText(o)),
                    ("record", o.RecordPath), ("replay", o.ReplayPath));

                WorldSaver? saver = null;
                if (opened.Folder != null && replay == null)
                {
                    ISaveHook? crash = services.SaveHook;
                    if (crash != null)
                    {
                        crash.Attach(opened.Storage!);
                        crash.OnLoaded(result.LoadedNumber, sim.Tick, StateHash.Compute(sim).Total);
                        log.Warn(sim.Tick, "save", "отладочный крючок: процесс замрёт посреди записи", ("plan", crash.Plan));
                    }

                    saver = new WorldSaver(opened.Folder, opened.BaseSha!, opened.Name, log, o.AutosaveSeconds, sim.Tick, opened.Loaded != null, crash, o.DebugSavePadKb)
                    {
                        WaitForPrevious = o.Sim,
                        Characters = characters.Capture,
                    };
                }

                // Локальная связь (0-031, «сервер в потоке»): UDP не открывается; иначе — UDP по --port.
                LiteNetTransport? transport = services.LocalTransport == null && o.Port.HasValue
                    ? LiteNetTransport.Listen(o.Port.Value, o.MaxPlayers, o.LoopbackOnly)
                    : null;
                int port = transport?.LocalPort ?? (services.LocalTransport != null ? 0 : -1);
                // Сервер игры: петля + «Открыть для друзей» вторым сокетом под тем же NetServer (0-024).
                MuxTransport? mux = transport != null && o.Mode.HasValue ? new MuxTransport(transport) : null;
                ITransport? rawTransport = services.LocalTransport ?? (ITransport?)mux ?? transport;
                // Счёт байт на клиента (0-030, 11.6) — обёрткой, NetServer не меняется.
                using CountingTransport? traffic = rawTransport != null ? new CountingTransport(rawTransport) : null;
                ITransport? netTransport = traffic;
                NetServer? net = netTransport != null ? CreateNetServer(o, sim, bots.Length, spawnCenter, netTransport, port, log, result) : null;
                if (net != null)
                {
                    net.GenerationHash = opened.GenerationHash;
                    net.Characters = characters;
                    // 6.4, 7.5: вход нового персонажа и уход — в сохранение в течение 1 с: повод пишется на ближайшей границе
                    // такта, а идёт прошлая запись — сразу после её конца (0-037).
                    net.CharacterCreated = _ => saver?.Request("игрок вошёл");
                    net.CharacterLeft = _ => saver?.Request("игрок вышел");
                }

                HostSession? host = net != null && mux != null ? SetUpHost(o, opened, sim, net, mux, port, services.ProcessId, () => saver, log, result) : null;
                if (net != null)
                {
                    Ready(services.ReadyOutput, port);
                }

                IRuntimeStats stats = services.Stats ?? new PortableRuntimeStats();
                ReadyMetrics(network, sim.Tick, stats, log, result);
                services.Ready?.Invoke(port);
                using var metrics = new ServerMetrics(
                    new TickMetrics(o.Sim ? 0 : 1_000_000L / Simulation.TickRate), traffic, () => net?.PlayersInGame ?? 0, o.MetricsSeconds, stats);
                var guard = new StepGuard();
                bool loopDone = false;
                try
                {
                    Loop(o, sim, bots, record, replay, net, host, saver, services.CreateClock, log, result, metrics, stop, guard);
                    loopDone = true;
                }
                finally
                {
                    if (!loopDone)
                    {
                        SaveAfterError(saver, sim, guard, log, result);
                    }

                    if (net != null)
                    {
                        result.LoginsAccepted = net.LoginsAccepted;
                        result.LoginsRejected = net.LoginsRejected;
                        result.Disconnects = net.Disconnects;
                        result.SnapshotsSent = net.SnapshotsSent;
                        result.SnapshotsTruncated = net.SnapshotsTruncated;
                        result.MaxSnapshotBytes = net.MaxSnapshotBytes;
                        result.DeltasSent = net.DeltasSent;
                        result.DeltaBytes = net.DeltaBytes;
                        result.FullFramesSent = net.FullSent;
                        log.Info(sim.Tick, "net", "сеть: итог", ("logins", net.LoginsAccepted), ("rejected", net.LoginsRejected),
                            ("disconnects", net.Disconnects), ("snapshots", net.SnapshotsSent), ("snapshotBytes", net.SnapshotBytes),
                            ("maxSnapshotBytes", net.MaxSnapshotBytes), ("truncated", net.SnapshotsTruncated),
                            ("deltas", net.DeltasSent), ("deltaBytes", net.DeltaBytes), ("fullFrames", net.FullSent),
                            ("maxDeltaBytes", net.MaxDeltaBytes),
                            ("bytesPerDelta", net.DeltasSent > 0 ? net.DeltaBytes / net.DeltasSent : 0),
                            ("bytesPerFull", net.FullSent > 0 ? (net.SnapshotBytes - net.DeltaBytes) / net.FullSent : 0),
                            ("inputs", net.InputsAccepted), ("inputsDropped", net.InputsDropped),
                            ("inventories", net.InventoriesSent));
                    }
                }

                record?.WriteEnd(sim.Tick);
                if (saver != null && result.ExitCode == 0)
                {
                    saver.SaveOnStop(sim);
                    result.SavesWritten = saver.Written;
                    result.LastSavedNumber = saver.LastNumber;
                    result.SavesReplaced = saver.Replaced;
                }

                if (opened.HostLockWriter != null)
                {
                    result.HostLockWrites = opened.HostLockWriter.Written;
                    result.HostLockFailures = opened.HostLockWriter.Failures;
                }

                // Отчёт --report пишет программа OrpheusServer после Run (JSON — не в ядре, 1.3).
                if (host != null)
                {
                    // 7.6: мир сохранён — всем «Хост вышел. Мир сохранён у него» (или «сервер остановлен»), затем выход.
                    CloseWorld(net!, netTransport!, result.HostLeft ? ClosingReason.HostLeft : ClosingReason.Stopped, log, sim.Tick);
                    result.HostReturns = host.Returns;
                }

                result.FinalHash = StateHash.Compute(sim).Total;
            }
            finally
            {
                replay?.Dispose();
            }
        }

        /// <summary>Такт идёт (<see cref="Simulation.Step"/> не вернулся): мир посреди такта — сохранять его нельзя.</summary>
        private sealed class StepGuard
        {
            public bool InStep;
        }

        /// <summary>
        /// Остановка с ошибкой (исключение из цикла такта; 0-037, з. 5 проверки): мир на границе такта цел — сохранить его,
        /// как при штатной остановке: иначе пропали бы все поводы и такты с последнего сохранения. Ошибка посреди такта —
        /// не сохранять: полутакт стал бы новейшим поколением, а последнее целое и так на диске. Своя ошибка сохранения
        /// исходную не заслоняет — только в лог.
        /// </summary>
        private static void SaveAfterError(WorldSaver? saver, Simulation sim, StepGuard guard, IServerLog log, ServerResult result)
        {
            if (saver == null)
            {
                return;
            }

            if (guard.InStep)
            {
                log.Error(sim.Tick, "save", "остановка с ошибкой посреди такта — мир не сохраняю, последнее поколение целое",
                    ("number", saver.LastNumber));
                return;
            }

            try
            {
                saver.SaveOnStop(sim);
                result.SavesWritten = saver.Written;
                result.LastSavedNumber = saver.LastNumber;
                result.SavesReplaced = saver.Replaced;
                log.Warn(sim.Tick, "save", "остановка с ошибкой: мир на границе такта сохранён", ("number", saver.LastNumber));
            }
            catch (Exception e)
            {
                log.Error(sim.Tick, "save", "остановка с ошибкой: сохранить мир не вышло — " + e.Message, ("exception", e.GetType().Name));
            }
        }

        // ---------- Сервер игры (0-024, docs/07 2.3, 2.13 п. 9, 7.6) ----------

        /// <summary>
        /// Токен хозяина, «Открыть для друзей» (второй сокет в <paramref name="mux"/>), ожидание упавшей игры хоста и
        /// файл-замок <c>host.lock</c> с портом и токеном.
        /// </summary>
        private static HostSession SetUpHost(
            ServerOptions o, OpenedWorld opened, Simulation sim, NetServer net, MuxTransport mux, int port, int processId,
            Func<WorldSaver?> saver, IServerLog log, ServerResult result)
        {
            var host = new HostSession(o.HostGraceSeconds * 1_000_000L, 0);
            var lockInfo = new HostLock
            {
                Port = port,
                Token = o.HostToken.ToString(),
                ProcessId = processId,
                Mode = o.Mode == ServerMode.Host ? "host" : "single",
            };
            net.HostToken = o.HostToken;
            net.HostJoined = () =>
            {
                host.Joined();
                log.Info(sim.Tick, "net", host.Returns > 0 ? "хозяин вернулся" : "хозяин вошёл", ("returns", host.Returns));
            };
            net.HostLeft = (lastHeard, now) =>
            {
                host.Left(lastHeard, now);
                int others = net.PlayersInGame;
                log.Warn(sim.Tick, "net", "хозяин ушёл — ждём его", ("graceSec", o.HostGraceSeconds),
                    ("silentMs", (now - lastHeard) / 1000), ("others", others));
                if (others == 0)
                {
                    saver()?.Request("хозяин ушёл"); // 2.3: других игроков нет — сохранить сразу и ждать те же 60 с
                }
            };
            net.OpenForFriends = () =>
            {
                LiteNetTransport pub = LiteNetTransport.Listen(o.OpenPort, o.MaxPlayers, o.OpenAddress);
                mux.Add(pub);
                result.OpenedPort = pub.LocalPort;
                log.Info(sim.Tick, "net", "открыто для друзей: слушаю UDP", ("address", o.OpenAddress?.ToString() ?? "все"), ("port", pub.LocalPort));
                lockInfo.OpenPort = pub.LocalPort;
                // Не в потоке такта и не падая: файл может быть открыт игрой, антивирусом (замечания 1 и 6 проверки).
                opened.HostLockWriter?.Request(lockInfo, sim.Tick);
                return pub.LocalPort;
            };

            opened.HostLockLog = log; // удалить при штатном выходе — до того, как отпущен замок мира
            opened.HostLockWriter = new HostLockWriter(opened.Storage!, Path.GetFullPath(o.WorldDir!), log);
            opened.HostLockWriter.Request(lockInfo, sim.Tick);
            opened.HostLockWriter.WaitWritten(1000); // к строке готовности замок обычно уже на диске; нет — допишется в фоне
            log.Info(sim.Tick, "net", "сервер игры", ("mode", lockInfo.Mode), ("graceSec", o.HostGraceSeconds),
                ("openAddress", o.OpenAddress?.ToString() ?? "все"), ("openPort", o.OpenPort), ("lock", HostLockFile.PathIn(o.WorldDir!)));
            return host;
        }

        /// <summary>
        /// Приёмка 0.8 (`07` 1.9 п. 4): секунды от запуска (процесса — у <c>OrpheusServer</c>; в потоке — от создания хоста) до
        /// готовности и память в этот миг — в лог и итог. С сетью — сразу после строки готовности, без сети — перед циклом тактов.
        /// </summary>
        private static void ReadyMetrics(bool network, long tick, IRuntimeStats stats, IServerLog log, ServerResult result)
        {
            result.ReadySeconds = stats.SecondsSinceStart();
            RuntimeMemory m = stats.Memory();
            result.ReadyWorkingSetMb = m.WorkingSetMb;
            result.ReadyPeakWorkingSetMb = m.PeakWorkingSetMb;
            log.Info(tick, "perf", network ? "готов: принимаю подключения" : "готов: начинаю такты",
                ("sinceProcessStartSec", Math.Round(result.ReadySeconds, 3)), ("workingSetMb", Math.Round(m.WorkingSetMb, 1)),
                ("peakWorkingSetMb", Math.Round(m.PeakWorkingSetMb, 1)), ("privateMb", Math.Round(m.PrivateMb, 1)),
                ("heapMb", Math.Round(m.HeapMb, 1)));
        }

        private static void Ready(TextWriter? console, int port)
        {
            try
            {
                console?.WriteLine($"{ReadyMarker} port={port}");
                console?.Flush();
            }
            catch (IOException)
            {
                // вывода нет (игра-родитель закрыла трубу) — сервер работает дальше
            }
        }

        private static void CloseWorld(NetServer net, ITransport transport, ClosingReason reason, IServerLog log, long tick)
        {
            log.Info(tick, "net", reason == ClosingReason.HostLeft ? "хост вышел: мир сохранён, игроки отключены" : "сервер остановлен: игроки отключены",
                ("players", net.PlayersInGame));
            net.CloseAll(reason);

            // Дать отключениям с причиной уйти до закрытия сокетов (без этого клиенты узнают лишь по тайм-ауту).
            var w = Stopwatch.StartNew();
            var sink = new NullHandler();
            while (w.ElapsedMilliseconds < 50)
            {
                transport.Poll(w.Elapsed.Ticks / 10, sink);
                transport.Flush();
                Thread.Sleep(10);
            }
        }

        private sealed class NullHandler : ITransportHandler
        {
            public void OnConnected(int peer)
            {
            }

            public void OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload)
            {
            }

            public void OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
            }
        }

        // ---------- Сеть ----------

        /// <summary>Номер манекена шипа 0-029 (не сущность симуляции — только в сообщениях «замах начат»).</summary>
        public const int DummyEntityId = 0x7FFF0001;

        /// <summary>Период замахов манекена: 90 кадров (1,5 с) — рывок, откат и путь бота домой укладываются.</summary>
        public const int DummyPeriodFrames = 90;

        private static NetServer CreateNetServer(ServerOptions o, Simulation sim, int firstPlayer, TileCoord center, ITransport transport, int port, IServerLog log, ServerResult result)
        {
            var net = new NetServer(transport, sim, n => SpawnPoint(sim.Shape, firstPlayer + n, center), o.MaxPlayers, NetModule.Version);
            net.Log = (level, msg) =>
            {
                switch (level)
                {
                    case NetLogLevel.Error:
                        log.Error(sim.Tick, "net", msg);
                        break;
                    case NetLogLevel.Warn:
                        log.Warn(sim.Tick, "net", msg);
                        break;
                    default:
                        log.Info(sim.Tick, "net", msg);
                        break;
                }
            };
            if (o.Dummy)
            {
                // Шип 0-029: манекен у первой точки появления (стартовая зона пуста), первый замах через 1 с.
                WorldPos at = Torus.Offset(sim.Shape, SpawnPoint(sim.Shape, firstPlayer, center), new Int2(Torus.SubTilesPerTile, 0));
                net.InputWaitFrames = o.DummyWaitFrames;
                net.SetDummy(new MeleeDummy(DummyEntityId, at, Angle16.FromDirection(new Int2(-1, 0)), net.ServerFrame + 60, DummyPeriodFrames));
                log.Info(sim.Tick, "net", "манекен шипа 0-029", ("x", at.X), ("y", at.Y), ("periodFrames", DummyPeriodFrames), ("waitFrames", o.DummyWaitFrames));
            }

            result.Port = port;
            log.Info(sim.Tick, "net", port == 0 ? "локальная связь (сервер в потоке)" : "слушаю UDP", ("port", port), ("loopbackOnly", o.LoopbackOnly), ("maxPlayers", o.MaxPlayers),
                ("version", NetModule.Version.ToString()), ("mtu", NetSettings.Mtu));
            return net;
        }

        // ---------- Цикл ----------

        private static void Loop(
            ServerOptions o, Simulation sim, SimBot[] bots, InputJournalWriter? record, InputJournalReader? replay,
            NetServer? net, HostSession? host, WorldSaver? saver, Func<int, TickPacer>? createClock, IServerLog log, ServerResult result,
            ServerMetrics metrics, CancellationToken stop, StepGuard guard)
        {
            long startTick = sim.Tick;
            TickMetrics tm = metrics.Tick;
            var pending = new EntityInput[sim.EntityCount + 1];
            JournalRecord next = replay?.Next() ?? JournalRecord.None;
            bool replayEnded = false;
            using TickPacer? clock = o.Sim ? null : createClock?.Invoke(Simulation.TickRate) ?? new TickPacer(Simulation.TickRate);
            if (clock != null)
            {
                log.Info(sim.Tick, "perf", "часы такта", ("method", clock.Method), ("hz", Simulation.TickRate));
            }

            long allocated0 = HostRuntime.AllocatedBytes();
            var watch = Stopwatch.StartNew();
            metrics.Start();
            while (sim.Tick - startTick < o.Ticks && !stop.IsCancellationRequested && !replayEnded)
            {
                long tick = sim.Tick + 1;
                tm.BeginTick();

                // Сеть: события транспорта, вход, ввод игроков — в потоке такта, до такта (2.10).
                long nowMicros = watch.Elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
                net?.Poll(nowMicros);
                if (host != null && host.Expired(nowMicros))
                {
                    // 7.6: игра хоста не вернулась за отведённое время — «хост вышел»: сохранить, разослать, завершиться.
                    result.HostLeft = true;
                    log.Warn(sim.Tick, "net", host.EverJoined ? "хост не вернулся — закрываю мир" : "хозяин так и не вошёл — закрываю мир",
                        ("graceSec", host.GraceMicros / 1_000_000), ("players", net!.PlayersInGame));
                    break;
                }

                if (net != null && net.Paused && clock != null)
                {
                    // 0-036: пауза — такт мира стоит (боты, журнал, такт, автосохранение по тактам), сеть живёт: разбор пакетов
                    // выше, снимок такта паузы — вошедшим в паузу, ожидание следующего шага часов. Сохранение, начатое до
                    // паузы или по уходу игрока, доводится (Poll сохранения внутри AfterTick; интервал по тактам стоит).
                    net.SendPausedSnapshots();
                    saver?.AfterTick(sim);
                    tm.CancelTick(); // цикл паузы — не такт: в метрики времени такта и простоев не идёт
                    clock.WaitNext(stop); // брошенные такты в паузе не в счёт: мир не отстаёт, он стоит — «часы» не нужны
                    continue;
                }

                tm.Mark(TickPart.Input);

                // Источник ввода: боты или журнал.
                for (int i = 0; i < bots.Length; i++)
                {
                    int slot = sim.SlotOf(bots[i].EntityId);
                    sim.SetInputAt(slot, bots[i].Think(tick, sim.Entities[slot]));
                }

                while (next == JournalRecord.Inputs)
                {
                    if (replay!.Tick < tick)
                    {
                        throw new InvalidDataException($"Журнал ввода: запись такта {replay.Tick} после такта {tick - 1}.");
                    }

                    if (replay.Tick > tick)
                    {
                        break;
                    }

                    foreach (EntityInput input in replay.Inputs)
                    {
                        if (!sim.SetInput(input.EntityId, input.Frame))
                        {
                            throw new InvalidDataException($"Журнал ввода, такт {tick}: нет сущности #{input.EntityId}.");
                        }
                    }

                    next = replay.Next();
                }

                tm.Mark(TickPart.Ai);
                if (record != null)
                {
                    int n = sim.CopyPendingInputs(pending);
                    record.WriteInputs(tick, new ReadOnlySpan<EntityInput>(pending, 0, n));
                }

                tm.Mark(TickPart.Other);
                guard.InStep = true;
                sim.Step();
                guard.InStep = false;
                tm.Mark(TickPart.Sim);
                net?.SendSnapshots(); // снимок каждому игроку каждый такт — 30 Гц: дельта к подтверждённой базе или полный (2.4, 4.3; 0-014)
                tm.Mark(TickPart.Net);
                sim.RefillPools(); // вне такта: запас словарей построек (backlog [такт сервера])
                if (o.DebugSaveRequestTicks > 0 && tick % o.DebugSaveRequestTicks == 0)
                {
                    saver?.Request("отладка: повод"); // --debug-save-request (0-037): серии замен без сети
                }

                saver?.AfterTick(sim); // граница такта: автосохранение (0-009)
                tm.Mark(TickPart.Save);
                o.AfterTick?.Invoke(sim);

                bool checkpoint = tick % Simulation.HashInterval == 0;
                if (checkpoint && (o.Hash || record != null || replay != null))
                {
                    StateHashResult hash = StateHash.Compute(sim);
                    result.Hashes.Add(hash);
                    record?.WriteCheckpoint(hash, sim.Entities);
                    if (o.Hash)
                    {
                        log.Info(tick, "world", "хэш", ("hash", hash.Total.ToString("x16")), ("world", hash.World.ToString("x16")));
                    }
                }

                // Повтор: сверка с записью.
                while (next == JournalRecord.Checkpoint && replay!.Tick <= tick)
                {
                    if (replay.Tick == tick && !Verify(sim, replay, log, result))
                    {
                        result.Ticks = sim.Tick - startTick;
                        result.Seconds = watch.Elapsed.TotalSeconds;
                        tm.EndTick();
                        FinishMetrics(metrics, sim.Tick, log, result);
                        return;
                    }

                    next = replay.Next();
                }

                if (replay != null && (next == JournalRecord.End && tick >= replay.Tick || next == JournalRecord.None))
                {
                    replayEnded = true;
                    if (next == JournalRecord.None)
                    {
                        log.Warn(tick, "world", "журнал ввода оборван без записи «конец»");
                    }
                }

                tm.EndTick(); // хвост (хэш, сверка, крючок тестов) — в «прочее»; заморозка и ожидание часов — вне такта
                if (metrics.Due())
                {
                    MetricsSample w = metrics.TakeWindow();
                    log.Info(tick, "perf", "метрики такта", w.ToLogData());
                }

                if (clock != null)
                {
                    foreach ((long Tick, int Ms) f in o.DebugFreezes)
                    {
                        if (f.Tick == tick)
                        {
                            log.Warn(tick, "perf", "отладочная заморозка такта", ("ms", f.Ms)); // 2.13 п. 5 (0-025)
                            Thread.Sleep(f.Ms);
                        }
                    }

                    long dropped = clock.WaitNext(stop);
                    if (dropped > 0)
                    {
                        log.Warn(tick, "perf", "перегрузка: такты не догоняются", ("dropped", dropped));
                        net?.BroadcastClock(); // 2.4: «часы после фриза»
                    }
                }
            }

            result.Seconds = watch.Elapsed.TotalSeconds;
            result.Ticks = sim.Tick - startTick;
            result.AllocatedBytesPerTick = result.Ticks > 0
                ? (HostRuntime.AllocatedBytes() - allocated0) / (double)result.Ticks
                : 0;

            if (replay != null)
            {
                log.Info(sim.Tick, "world", "повтор сошёлся с записью", ("checkpoints", result.CheckpointsVerified));
            }

            log.Info(sim.Tick, "perf", o.Sim ? "симуляция закончена" : "сервер остановлен",
                ("ticks", result.Ticks), ("seconds", result.Seconds), ("ticksPerSecond", result.Ticks / Math.Max(result.Seconds, 1e-9)),
                ("speedX", result.SpeedX), ("allocatedBytesPerTick", result.AllocatedBytesPerTick));
            FinishMetrics(metrics, sim.Tick, log, result);
        }

        private static void FinishMetrics(ServerMetrics metrics, long tick, IServerLog log, ServerResult result)
        {
            MetricsSample total = metrics.TakeTotal();
            result.Metrics = total;
            result.MetricsWindows.AddRange(metrics.Windows);
            log.Info(tick, "perf", "метрики такта: итог", total.ToLogData());
        }

        private static bool Verify(Simulation sim, InputJournalReader replay, IServerLog log, ServerResult result)
        {
            StateHashResult mine = result.Hashes[result.Hashes.Count - 1];
            StateHashResult theirs = replay.CheckpointHash;
            if (mine.Total == theirs.Total)
            {
                result.CheckpointsVerified++;
                return true;
            }

            result.DivergedTick = sim.Tick;
            ReadOnlySpan<SimEntity> entities = sim.Entities;
            for (int i = 0; i < replay.CheckpointEntityCount; i++)
            {
                int id = replay.CheckpointEntityId(i);
                int slot = sim.SlotOf(id);
                if (slot < 0 || StateHash.Entity(entities[slot]) != replay.CheckpointEntityHash(i))
                {
                    result.DivergedEntity = id;
                    break;
                }
            }

            log.Error(sim.Tick, "world", result.DivergedEntity >= 0 ? "повтор разошёлся: сущность" : "повтор разошёлся: мир",
                ("entity", result.DivergedEntity), ("hash", mine.Total.ToString("x16")), ("recorded", theirs.Total.ToString("x16")),
                ("world", mine.World.ToString("x16")), ("recordedWorld", theirs.World.ToString("x16")));
            result.ExitCode = 1;
            return false;
        }

        // ---------- Мир ----------

        /// <summary>Открытый мир: модель, папка с замком, SHA-256 снимка генерации, загруженное поколение.</summary>
        private sealed class OpenedWorld : IDisposable
        {
            public WorldModel World = null!;
            public IPlatformStorage? Storage;
            public WorldFolder? Folder;
            public IDisposable? Lock;
            public byte[]? BaseSha;

            /// <summary>Хэш снимка генерации до наложения сохранения (0-017): уходит клиентам во «вход принят».</summary>
            public ulong GenerationHash;
            public LoadedWorldState? Loaded;
            public string Name = string.Empty;

            /// <summary>Сервер игры записал <c>host.lock</c> (0-024): удалить при выходе, пока замок мира ещё наш.</summary>
            public IServerLog? HostLockLog;

            /// <summary>Запись <c>host.lock</c> в фоне; останавливается до удаления замка.</summary>
            public HostLockWriter? HostLockWriter;

            public void Dispose()
            {
                HostLockWriter?.Dispose();
                if (HostLockLog != null && Storage != null && Folder != null)
                {
                    try
                    {
                        HostLockFile.Delete(Storage, Folder.Dir);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        HostLockLog.Warn(0, "save", "файл-замок хоста не удалён", ("error", e.Message));
                    }
                }

                Lock?.Dispose();
            }
        }

        private static OpenedWorld OpenWorld(ServerOptions o, JournalHeader? replay, IPlatformStorage? storage, IServerLog log)
        {
            ulong seed = o.Seed;
            int size = o.Size;
            if (replay != null)
            {
                if (o.SeedGiven && o.Seed != replay.Seed || o.SizeGiven && o.Size != replay.Shape.Width)
                {
                    throw new ArgumentException($"Журнал записан на мире зерно {replay.Seed}, {replay.Shape}; ключи --seed/--size с ним не сходятся.");
                }

                seed = replay.Seed;
                size = replay.Shape.Width;
            }

            var opened = new OpenedWorld();
            try
            {
                if (o.WorldDir != null)
                {
                    // Файлы мира — только через хранилище платформы (1.6, 0-031): на ПК — PcStorage, в потоке игры — оно же.
                    opened.Storage = storage ?? throw new ArgumentException("--world: нет хранилища платформы (ServerHostServices.Storage).");
                    opened.Folder = new WorldFolder(opened.Storage, Path.GetFullPath(o.WorldDir));
                    opened.Lock = opened.Folder.Lock(); // второй сервер на ту же папку — «мир уже открыт» (6.10 п. 7)
                    opened.Name = Path.GetFileName(opened.Folder.Dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }

                var watch = Stopwatch.StartNew();
                if (o.Create || opened.Folder == null)
                {
                    opened.World = GeneratorFor(o, replay).Generate(MapShape.World(size), seed);
                    opened.GenerationHash = StateHash.Generation(opened.World);
                    if (opened.Folder != null)
                    {
                        byte[] baseBytes = WorldSnapshot.WriteBase(opened.World);
                        opened.Folder.CreateBase(baseBytes);
                        opened.BaseSha = WorldSnapshot.StoredHash(baseBytes);
                        log.Info(0, "save", "мир создан", ("path", opened.Folder.BasePath), ("ms", watch.Elapsed.TotalMilliseconds));
                    }
                }
                else
                {
                    WorldFolder folder = opened.Folder;
                    if (!folder.HasBase)
                    {
                        throw new IOException($"Нет мира: {folder.BasePath}. Создать — ключ --create.");
                    }

                    byte[] baseBytes = folder.ReadBase();
                    opened.World = WorldSnapshot.LoadBase(baseBytes);
                    opened.BaseSha = WorldSnapshot.StoredHash(baseBytes);
                    opened.GenerationHash = StateHash.Generation(opened.World);
                    if (replay == null)
                    {
                        LoadLatest(opened, log);
                    }

                    log.Info(opened.Loaded?.Tick ?? 0, "save", "мир загружен", ("path", folder.Dir), ("ms", watch.Elapsed.TotalMilliseconds),
                        ("number", opened.Loaded?.Number ?? 0), ("tick", opened.Loaded?.Tick ?? 0),
                        ("chunks", opened.Loaded?.ChunkCount ?? 0), ("entities", opened.Loaded?.Entities.Length ?? 0));
                }

                WorldModel world = opened.World;
                if (replay != null && (world.Seed != replay.Seed || world.Shape != replay.Shape || world.GeneratorVersion != replay.GeneratorVersion))
                {
                    throw new InvalidDataException(
                        $"Журнал записан на мире зерно {replay.Seed}, {replay.Shape}, генератор {replay.GeneratorVersion}; " +
                        $"мир — зерно {world.Seed}, {world.Shape}, генератор {world.GeneratorVersion}.");
                }

                return opened;
            }
            catch
            {
                opened.Dispose();
                throw;
            }
        }

        /// <summary>Проверка поколений и .tmp (6.5 п. 5), новейшее целое — поверх снимка генерации.</summary>
        private static void LoadLatest(OpenedWorld opened, IServerLog log)
        {
            WorldRecovery rec = opened.Folder!.Recover();
            foreach (DamagedFile d in rec.Damaged)
            {
                log.Warn(0, "save", "повреждённое поколение перенесено", ("file", d.Name), ("to", d.MovedTo), ("reason", d.Reason));
            }

            if (rec.PromotedTmp)
            {
                log.Warn(0, "save", "целый .tmp без переименования (обрыв после сброса) — продвинут", ("file", rec.LatestName));
            }

            string leftover = rec.ReplacedLeftover != 0 ? WorldFolder.GenerationFileName(rec.ReplacedLeftover) : string.Empty;
            foreach (string name in rec.Pruned)
            {
                if (name == leftover)
                {
                    log.Warn(0, "save", "заменённое поколение серии осталось после обрыва — удалено при старте", ("file", name), ("replacedBy", rec.LatestName));
                }
                else
                {
                    log.Info(0, "save", "поколение старше трёх последних — удалено при старте", ("file", name));
                }
            }

            if (rec.Latest != null)
            {
                opened.Loaded = WorldSave.Load(rec.Latest, opened.World, opened.BaseSha!);
            }
        }

        private static Func<string, Stream> Need(Func<string, Stream>? open, string key) =>
            open ?? throw new ArgumentException($"{key}: журнал ввода на этой платформе недоступен (нет ServerHostServices.OpenRead/CreateWrite).");

        // ---------- Боты ----------

        /// <summary>
        /// Точка появления бота № <paramref name="index"/>: центр карты, ряды по 8 через 2 тайла
        /// (в стартовой зоне 32 × 32 у центра, которую генератор 0-007 держит пустой).
        /// </summary>
        public static WorldPos SpawnPoint(MapShape shape, int index) => SpawnPoint(shape, index, DefaultSpawnCenter(shape));

        /// <summary>
        /// Точка появления № <paramref name="index"/> от центра <paramref name="center"/>: тайлы (cx − 7 + 2·(i mod 8),
        /// cy − 1 + 2·(i div 8)) по тору.
        /// </summary>
        public static WorldPos SpawnPoint(MapShape shape, int index, TileCoord center)
        {
            var t = Torus.Wrap(shape, new TileCoord(center.X + (index % 8) * 2 - 7, center.Y + (index / 8) * 2 - 1));
            return new WorldPos((t.X << Torus.SubTileBits) + Torus.SubTilesPerTile / 2, (t.Y << Torus.SubTileBits) + Torus.SubTilesPerTile / 2);
        }

        /// <summary>Центр появления по умолчанию — середина карты.</summary>
        public static TileCoord DefaultSpawnCenter(MapShape shape) => new TileCoord(shape.Width / 2, shape.Height / 2);

        /// <summary>
        /// Центр появления <c>--spawn-seam</c> (0-017, net-smoke шва): первый ряд (cy − 1) ставится у шва x = 0 — ряд
        /// появления x от W − 8 через 2 тайла, — на строке, где тайлы x ∈ [W − <see cref="SeamLaneBefore"/>,
        /// <see cref="SeamLaneAfter"/>) проходимы: боты <c>shuttle</c> и игрок на ней ходят через шов без препятствий.
        /// Строки ищутся от середины наружу (повторяемо для зерна); чистой нет — середина карты и предупреждение.
        /// </summary>
        public static TileCoord SeamSpawnCenter(Simulation sim, out bool clear)
        {
            MapShape shape = sim.Shape;
            int w = shape.Width;
            int h = shape.Height;
            for (int k = 0; k < h; k++)
            {
                int row = (h / 2 + ((k & 1) == 0 ? k / 2 : -(k / 2 + 1)) + h) % h;
                clear = true;
                for (int x = -SeamLaneBefore; x < SeamLaneAfter && clear; x++)
                {
                    clear = !sim.IsBlocked(shape, Torus.Wrap(shape, new TileCoord(x, row)));
                }

                if (clear)
                {
                    return new TileCoord(w - 1, row + 1);
                }
            }

            clear = false;
            return DefaultSpawnCenter(shape);
        }

        /// <summary>Чистая полоса шва: от W − 12 до 16 — ряд появления W − 8…, ход shuttle 12 тайлов, игрок net-smoke 12 тайлов на восток.</summary>
        public const int SeamLaneBefore = 12;

        /// <inheritdoc cref="SeamLaneBefore"/>
        public const int SeamLaneAfter = 16;

        private static TileCoord SpawnCenter(Simulation sim, ServerOptions o, IServerLog log)
        {
            if (o.DebugRunLanes)
            {
                return RunSeamLanes.SpawnCenter;
            }

            if (!o.SpawnSeam)
            {
                return DefaultSpawnCenter(sim.Shape);
            }

            TileCoord c = SeamSpawnCenter(sim, out bool clear);
            if (clear)
            {
                log.Info(sim.Tick, "world", "появление у шва x = 0", ("row", c.Y - 1), ("fromX", c.X - 7));
            }
            else
            {
                log.Warn(sim.Tick, "world", "чистой полосы у шва нет — появление в середине");
            }

            return c;
        }

        private static SimBot[] SpawnBots(Simulation sim, ServerOptions o, TileCoord center)
        {
            var bots = new SimBot[CountBots(o)];
            int index = 0;
            foreach (BotGroup g in o.Bots)
            {
                for (int k = 0; k < g.Count; k++, index++)
                {
                    // Загруженный мир: боты берут сущности из сохранения по порядку, недостающих — ставят.
                    int id = index < sim.EntityCount ? sim.Entities[index].Id : sim.AddEntity(SpawnPoint(sim.Shape, index, center));
                    bots[index] = SimBot.Create(g.Script, id, index, sim.World.Seed, sim.Shape);
                }
            }

            return bots;
        }

        private static int CountBots(ServerOptions o)
        {
            int n = 0;
            foreach (BotGroup g in o.Bots)
            {
                n += g.Count;
            }

            return n;
        }

        private static string BotsText(ServerOptions o) => o.Bots.Count == 0 ? "" : string.Join(",", o.Bots);
    }
}
