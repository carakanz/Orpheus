using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Orpheus.Core.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.WorldGen;

namespace Orpheus.Server
{
    /// <summary>Итог запуска сервера (для тестов и кода выхода).</summary>
    public sealed class ServerResult
    {
        /// <summary>0 — всё хорошо; 1 — повтор разошёлся с записью; 2 — ошибка ключей или данных.</summary>
        public int ExitCode { get; set; }

        /// <summary>Выполнено тактов.</summary>
        public long Ticks { get; set; }

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
    }

    /// <summary>
    /// Запуск сервера без сети (docs/07, 11.2): мир (создать или загрузить), сущности, источник ввода
    /// (боты или журнал), цикл такта — по часам (обычный режим) или подряд (<c>--sim</c>), хэш на каждом
    /// 300-м такте, запись и повтор журнала ввода, лог JSON-строками.
    /// </summary>
    public static class ServerRun
    {
        /// <summary>Файл снимка генерации в папке мира (docs/07, 6.3).</summary>
        public const string BaseFileName = "world.base";

        /// <summary>Генератор при <c>--create</c> и при повторе без папки мира: генератор сборки (<see cref="WorldGenModule.Current"/>, 0-007).</summary>
        public static IWorldGenerator Generator { get; set; } = WorldGenModule.Current;

        public static ServerResult Run(ServerOptions o, TextWriter? console, CancellationToken stop = default)
        {
            string? logsDir = o.LogsDir ?? (o.WorldDir != null ? Path.Combine(o.WorldDir, "logs") : null);
            using var log = new JsonLog(logsDir, console, o.LogLevels);
            var result = new ServerResult();
            try
            {
                RunCore(o, log, result, stop);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException || e is UnauthorizedAccessException)
            {
                log.Error(result.Ticks, "admin", e.Message, ("exception", e.GetType().Name));
                result.ExitCode = 2;
            }

            return result;
        }

        private static void RunCore(ServerOptions o, JsonLog log, ServerResult result, CancellationToken stop)
        {
            InputJournalReader? replay = o.ReplayPath != null
                ? new InputJournalReader(new FileStream(o.ReplayPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                : null;
            try
            {
                WorldModel world = OpenWorld(o, replay?.Header, log);
                int players = o.Port.HasValue ? o.MaxPlayers : 0;
                var sim = new Simulation(world, maxEntities: Math.Max(64, CountBots(o) + players + 1));

                SimBot[] bots = Array.Empty<SimBot>();
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
                    bots = SpawnBots(sim, o);
                }

                using InputJournalWriter? record = o.RecordPath != null
                    ? new InputJournalWriter(CreateFile(o.RecordPath), JournalHeader.Of(sim))
                    : null;

                log.Info(sim.Tick, "world", o.Sim ? "старт: режим симуляции" : "старт: обычный режим, такт по часам",
                    ("size", world.Shape.Width), ("seed", world.Seed), ("generator", world.GeneratorVersion),
                    ("entities", sim.EntityCount), ("bots", BotsText(o)),
                    ("record", o.RecordPath), ("replay", o.ReplayPath));

                using LiteNetTransport? transport = o.Port.HasValue ? LiteNetTransport.Listen(o.Port.Value, o.MaxPlayers) : null;
                NetServer? net = transport != null ? CreateNetServer(o, sim, bots.Length, transport, log, result) : null;
                try
                {
                    Loop(o, sim, bots, record, replay, net, log, result, stop);
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
                        log.Info(sim.Tick, "net", "сеть: итог", ("logins", net.LoginsAccepted), ("rejected", net.LoginsRejected),
                            ("disconnects", net.Disconnects), ("snapshots", net.SnapshotsSent), ("snapshotBytes", net.SnapshotBytes),
                            ("maxSnapshotBytes", net.MaxSnapshotBytes), ("truncated", net.SnapshotsTruncated),
                            ("inputs", net.InputsAccepted), ("inputsDropped", net.InputsDropped));
                    }
                }

                record?.WriteEnd(sim.Tick);
            }
            finally
            {
                replay?.Dispose();
            }
        }

        // ---------- Сеть ----------

        private static NetServer CreateNetServer(ServerOptions o, Simulation sim, int firstPlayer, LiteNetTransport transport, JsonLog log, ServerResult result)
        {
            var net = new NetServer(transport, sim, n => SpawnPoint(sim.Shape, firstPlayer + n), o.MaxPlayers, NetModule.Version);
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
            result.Port = transport.LocalPort;
            log.Info(sim.Tick, "net", "слушаю UDP", ("port", transport.LocalPort), ("maxPlayers", o.MaxPlayers),
                ("version", NetModule.Version.ToString()), ("mtu", NetSettings.Mtu));
            return net;
        }

        // ---------- Цикл ----------

        private static void Loop(
            ServerOptions o, Simulation sim, SimBot[] bots, InputJournalWriter? record, InputJournalReader? replay,
            NetServer? net, JsonLog log, ServerResult result, CancellationToken stop)
        {
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
            while (sim.Tick < o.Ticks && !stop.IsCancellationRequested && !replayEnded)
            {
                long tick = sim.Tick + 1;

                // Сеть: события транспорта, вход, ввод игроков — в потоке такта, до такта (2.10).
                net?.Poll(watch.Elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000));

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
                net?.SendSnapshots(); // полный снимок каждый такт — 30 Гц (2.4, 4.3)
                sim.RefillPools(); // вне такта: запас словарей построек (backlog [такт сервера])
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
                        result.Ticks = sim.Tick;
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
                    long dropped = clock.WaitNext(stop);
                    if (dropped > 0)
                    {
                        log.Warn(tick, "perf", "перегрузка: такты не догоняются", ("dropped", dropped));
                        net?.BroadcastClock(); // 2.4: «часы после фриза»
                    }
                }
            }

            result.Seconds = watch.Elapsed.TotalSeconds;
            result.Ticks = sim.Tick;
            result.AllocatedBytesPerTick = sim.Tick > 0
                ? (GC.GetAllocatedBytesForCurrentThread() - allocated0) / (double)sim.Tick
                : 0;

            if (replay != null)
            {
                log.Info(sim.Tick, "world", "повтор сошёлся с записью", ("checkpoints", result.CheckpointsVerified));
            }

            log.Info(sim.Tick, "perf", o.Sim ? "симуляция закончена" : "сервер остановлен",
                ("ticks", sim.Tick), ("seconds", result.Seconds), ("ticksPerSecond", sim.Tick / Math.Max(result.Seconds, 1e-9)),
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

        private static WorldModel OpenWorld(ServerOptions o, JournalHeader? replay, JsonLog log)
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

            WorldModel world;
            var watch = Stopwatch.StartNew();
            if (o.Create || o.WorldDir == null)
            {
                world = Generator.Generate(MapShape.World(size), seed);
                if (o.WorldDir != null)
                {
                    Directory.CreateDirectory(o.WorldDir);
                    string path = Path.Combine(o.WorldDir, BaseFileName);
                    using (FileStream f = CreateFile(path))
                    {
                        WorldSnapshot.WriteBase(world, f);
                    }

                    log.Info(0, "save", "мир создан", ("path", path), ("ms", watch.Elapsed.TotalMilliseconds));
                }
            }
            else
            {
                string path = Path.Combine(o.WorldDir, BaseFileName);
                if (!File.Exists(path))
                {
                    throw new IOException($"Нет мира: {path}. Создать — ключ --create.");
                }

                world = WorldSnapshot.LoadBase(File.ReadAllBytes(path));
                log.Info(0, "save", "мир загружен", ("path", path), ("ms", watch.Elapsed.TotalMilliseconds));
            }

            if (replay != null && (world.Seed != replay.Seed || world.Shape != replay.Shape || world.GeneratorVersion != replay.GeneratorVersion))
            {
                throw new InvalidDataException(
                    $"Журнал записан на мире зерно {replay.Seed}, {replay.Shape}, генератор {replay.GeneratorVersion}; " +
                    $"мир — зерно {world.Seed}, {world.Shape}, генератор {world.GeneratorVersion}.");
            }

            return world;
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
        public static WorldPos SpawnPoint(MapShape shape, int index)
        {
            int tx = shape.Width / 2 + (index % 8) * 2 - 7;
            int ty = shape.Height / 2 + (index / 8) * 2 - 1;
            return new WorldPos((tx << Torus.SubTileBits) + Torus.SubTilesPerTile / 2, (ty << Torus.SubTileBits) + Torus.SubTilesPerTile / 2);
        }

        private static SimBot[] SpawnBots(Simulation sim, ServerOptions o)
        {
            var bots = new SimBot[CountBots(o)];
            int index = 0;
            foreach (BotGroup g in o.Bots)
            {
                for (int k = 0; k < g.Count; k++, index++)
                {
                    int id = sim.AddEntity(SpawnPoint(sim.Shape, index));
                    bots[index] = SimBot.Create(g.Script, id, index, sim.World.Seed);
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
