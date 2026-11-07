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
using Orpheus.Platform.Pc;
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

        /// <summary>Сервер игры: порт «Открыть для друзей»; не открывали — −1.</summary>
        public int OpenedPort { get; set; } = -1;
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
    public static class ServerRun
    {
        /// <summary>Файл снимка генерации в папке мира (docs/07, 6.3).</summary>
        public const string BaseFileName = "world.base";

        /// <summary>
        /// Строка готовности в stdout (0-024): сервер слушает порт — лаунчер игры и скрипты ждут её, а не паузу
        /// «на всякий случай». Вид: <c>orpheus-ready port=P</c>.
        /// </summary>
        public const string ReadyMarker = "orpheus-ready";

        /// <summary>Генератор при <c>--create</c> и при повторе без папки мира: генератор сборки (<see cref="WorldGenModule.Current"/>, 0-007).</summary>
        public static IWorldGenerator Generator { get; set; } = WorldGenModule.Current;

        public static ServerResult Run(ServerOptions o, TextWriter? console, CancellationToken stop = default)
        {
            string? logsDir = o.LogsDir ?? (o.WorldDir != null ? Path.Combine(o.WorldDir, "logs") : null);
            using var log = new JsonLog(logsDir, console, o.LogLevels);
            var result = new ServerResult();
            try
            {
                RunCore(o, log, console, result, stop);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException || e is UnauthorizedAccessException)
            {
                log.Error(result.Ticks, "admin", e.Message, ("exception", e.GetType().Name));
                result.ExitCode = 2;
            }

            return result;
        }

        private static void RunCore(ServerOptions o, JsonLog log, TextWriter? console, ServerResult result, CancellationToken stop)
        {
            InputJournalReader? replay = o.ReplayPath != null
                ? new InputJournalReader(new FileStream(o.ReplayPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                : null;
            try
            {
                using OpenedWorld opened = OpenWorld(o, replay?.Header, log);
                WorldModel world = opened.World;
                if (o.DebugRunLanes)
                {
                    // Тестовый мир run_seam (0-027): полосы на тайлах генерации — хэш генерации считается заново, бот
                    // с тем же расчищением получает тот же мир.
                    int cleared = RunSeamLanes.Clear(world);
                    opened.GenerationHash = StateHash.Generation(world);
                    log.Warn(0, "world", "отладка: полосы run_seam расчищены, появление у угла (0, 0)", ("tiles", cleared));
                }

                int players = o.Port.HasValue ? o.MaxPlayers : 0;
                int loadedEntities = opened.Loaded?.Entities.Length ?? 0;
                var sim = new Simulation(world, maxEntities: Math.Max(64, Math.Max(CountBots(o), loadedEntities) + players + 1));
                if (opened.Loaded != null)
                {
                    opened.Loaded.RestoreInto(sim);
                    result.LoadedNumber = opened.Loaded.Number;
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
                    ? new InputJournalWriter(CreateFile(o.RecordPath), JournalHeader.Of(sim))
                    : null;

                log.Info(sim.Tick, "world", o.Sim ? "старт: режим симуляции" : "старт: обычный режим, такт по часам",
                    ("size", world.Shape.Width), ("seed", world.Seed), ("generator", world.GeneratorVersion),
                    ("entities", sim.EntityCount), ("bots", BotsText(o)),
                    ("record", o.RecordPath), ("replay", o.ReplayPath));

                WorldSaver? saver = null;
                if (opened.Folder != null && replay == null)
                {
                    SaveCrashHook? crash = null;
                    if (o.DebugSaveCrashSeed.HasValue || o.DebugSavePlan != null)
                    {
                        crash = o.DebugSavePlan != null
                            ? SaveCrashHook.Fixed(o.DebugSavePlan, Console.Error)
                            : new SaveCrashHook(o.DebugSaveCrashSeed!.Value, Console.Error);
                        crash.Attach(opened.Storage!);
                        crash.Line($"loaded {result.LoadedNumber} {sim.Tick} {StateHash.Compute(sim).Total:x16}");
                        log.Warn(sim.Tick, "save", "отладочный крючок: процесс замрёт посреди записи", ("plan", crash.Plan));
                    }

                    saver = new WorldSaver(opened.Folder, opened.BaseSha!, opened.Name, log, o.AutosaveSeconds, sim.Tick, opened.Loaded != null, crash, o.DebugSavePadKb)
                    {
                        WaitForPrevious = o.Sim,
                    };
                }

                LiteNetTransport? transport = o.Port.HasValue ? LiteNetTransport.Listen(o.Port.Value, o.MaxPlayers, o.LoopbackOnly) : null;
                // Сервер игры: петля + «Открыть для друзей» вторым сокетом под тем же NetServer (0-024).
                MuxTransport? mux = transport != null && o.Mode.HasValue ? new MuxTransport(transport) : null;
                using ITransport? netTransport = (ITransport?)mux ?? transport;
                NetServer? net = transport != null ? CreateNetServer(o, sim, bots.Length, spawnCenter, netTransport!, transport.LocalPort, log, result) : null;
                if (net != null)
                {
                    net.GenerationHash = opened.GenerationHash;
                }

                HostSession? host = net != null && mux != null ? SetUpHost(o, opened, sim, net, mux, transport!.LocalPort, () => saver, log, result) : null;
                if (net != null)
                {
                    Ready(console, transport!.LocalPort);
                }

                try
                {
                    Loop(o, sim, bots, record, replay, net, host, saver, log, result, stop);
                }
                finally
                {
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
                            ("inputs", net.InputsAccepted), ("inputsDropped", net.InputsDropped));
                    }
                }

                record?.WriteEnd(sim.Tick);
                if (saver != null && result.ExitCode == 0)
                {
                    saver.SaveOnStop(sim);
                    result.SavesWritten = saver.Written;
                    result.LastSavedNumber = saver.LastNumber;
                }

                if (opened.HostLockWriter != null)
                {
                    result.HostLockWrites = opened.HostLockWriter.Written;
                    result.HostLockFailures = opened.HostLockWriter.Failures;
                }

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

        // ---------- Сервер игры (0-024, docs/07 2.3, 2.13 п. 9, 7.6) ----------

        /// <summary>
        /// Токен хозяина, «Открыть для друзей» (второй сокет в <paramref name="mux"/>), ожидание упавшей игры хоста и
        /// файл-замок <c>host.lock</c> с портом и токеном.
        /// </summary>
        private static HostSession SetUpHost(
            ServerOptions o, OpenedWorld opened, Simulation sim, NetServer net, MuxTransport mux, int port,
            Func<WorldSaver?> saver, JsonLog log, ServerResult result)
        {
            var host = new HostSession(o.HostGraceSeconds * 1_000_000L, 0);
            var lockInfo = new HostLock
            {
                Port = port,
                Token = o.HostToken.ToString(),
                ProcessId = Environment.ProcessId,
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
                    saver()?.SaveNow(sim, "хозяин ушёл"); // 2.3: других игроков нет — сохранить сразу и ждать те же 60 с
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

        private static void CloseWorld(NetServer net, ITransport transport, ClosingReason reason, JsonLog log, long tick)
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

        private static NetServer CreateNetServer(ServerOptions o, Simulation sim, int firstPlayer, TileCoord center, ITransport transport, int port, JsonLog log, ServerResult result)
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
            log.Info(sim.Tick, "net", "слушаю UDP", ("port", port), ("loopbackOnly", o.LoopbackOnly), ("maxPlayers", o.MaxPlayers),
                ("version", NetModule.Version.ToString()), ("mtu", NetSettings.Mtu));
            return net;
        }

        // ---------- Цикл ----------

        private static void Loop(
            ServerOptions o, Simulation sim, SimBot[] bots, InputJournalWriter? record, InputJournalReader? replay,
            NetServer? net, HostSession? host, WorldSaver? saver, JsonLog log, ServerResult result, CancellationToken stop)
        {
            long startTick = sim.Tick;
            var pending = new EntityInput[sim.EntityCount + 1];
            JournalRecord next = replay?.Next() ?? JournalRecord.None;
            bool replayEnded = false;
            using TickClock? clock = o.Sim ? null : new TickClock(Simulation.TickRate);
            if (clock != null)
            {
                log.Info(sim.Tick, "perf", "часы такта", ("method", clock.Method), ("hz", Simulation.TickRate));
            }

            long allocated0 = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            while (sim.Tick - startTick < o.Ticks && !stop.IsCancellationRequested && !replayEnded)
            {
                long tick = sim.Tick + 1;

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

                if (record != null)
                {
                    int n = sim.CopyPendingInputs(pending);
                    record.WriteInputs(tick, new ReadOnlySpan<EntityInput>(pending, 0, n));
                }

                sim.Step();
                net?.SendSnapshots(); // снимок каждому игроку каждый такт — 30 Гц: дельта к подтверждённой базе или полный (2.4, 4.3; 0-014)
                sim.RefillPools(); // вне такта: запас словарей построек (backlog [такт сервера])
                saver?.AfterTick(sim); // граница такта: автосохранение (0-009)
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
                ? (GC.GetAllocatedBytesForCurrentThread() - allocated0) / (double)result.Ticks
                : 0;

            if (replay != null)
            {
                log.Info(sim.Tick, "world", "повтор сошёлся с записью", ("checkpoints", result.CheckpointsVerified));
            }

            log.Info(sim.Tick, "perf", o.Sim ? "симуляция закончена" : "сервер остановлен",
                ("ticks", result.Ticks), ("seconds", result.Seconds), ("ticksPerSecond", result.Ticks / Math.Max(result.Seconds, 1e-9)),
                ("speedX", result.SpeedX), ("allocatedBytesPerTick", result.AllocatedBytesPerTick));
        }

        private static bool Verify(Simulation sim, InputJournalReader replay, JsonLog log, ServerResult result)
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
            public PcStorage? Storage;
            public WorldFolder? Folder;
            public IDisposable? Lock;
            public byte[]? BaseSha;

            /// <summary>Хэш снимка генерации до наложения сохранения (0-017): уходит клиентам во «вход принят».</summary>
            public ulong GenerationHash;
            public LoadedWorldState? Loaded;
            public string Name = string.Empty;

            /// <summary>Сервер игры записал <c>host.lock</c> (0-024): удалить при выходе, пока замок мира ещё наш.</summary>
            public JsonLog? HostLockLog;

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

        private static OpenedWorld OpenWorld(ServerOptions o, JournalHeader? replay, JsonLog log)
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
                    opened.Storage = new PcStorage();
                    opened.Folder = new WorldFolder(opened.Storage, Path.GetFullPath(o.WorldDir));
                    opened.Lock = opened.Folder.Lock(); // второй сервер на ту же папку — «мир уже открыт» (6.10 п. 7)
                    opened.Name = new DirectoryInfo(opened.Folder.Dir).Name;
                }

                var watch = Stopwatch.StartNew();
                if (o.Create || opened.Folder == null)
                {
                    opened.World = Generator.Generate(MapShape.World(size), seed);
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
        private static void LoadLatest(OpenedWorld opened, JsonLog log)
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

            foreach (string name in rec.Pruned)
            {
                log.Info(0, "save", "поколение старше трёх последних — удалено при старте", ("file", name));
            }

            if (rec.Latest != null)
            {
                opened.Loaded = WorldSave.Load(rec.Latest, opened.World, opened.BaseSha!);
            }
        }

        private static FileStream CreateFile(string path)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        }

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

        private static TileCoord SpawnCenter(Simulation sim, ServerOptions o, JsonLog log)
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
