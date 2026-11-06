using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Orpheus.Core.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
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
            if (o.Port.HasValue)
            {
                log.Warn(0, "net", "сети пока нет: --port принят и не используется", ("port", o.Port.Value));
            }

            InputJournalReader? replay = o.ReplayPath != null
                ? new InputJournalReader(new FileStream(o.ReplayPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                : null;
            try
            {
                using OpenedWorld opened = OpenWorld(o, replay?.Header, log);
                WorldModel world = opened.World;
                int loadedEntities = opened.Loaded?.Entities.Length ?? 0;
                var sim = new Simulation(world, maxEntities: Math.Max(64, Math.Max(CountBots(o), loadedEntities) + 1));
                if (opened.Loaded != null)
                {
                    opened.Loaded.RestoreInto(sim);
                    result.LoadedNumber = opened.Loaded.Number;
                }

                result.StartTick = sim.Tick;

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

                WorldSaver? saver = null;
                if (opened.Folder != null && replay == null)
                {
                    SaveCrashHook? crash = null;
                    if (o.DebugSaveCrashSeed.HasValue)
                    {
                        crash = new SaveCrashHook(o.DebugSaveCrashSeed.Value, Console.Error);
                        crash.Attach(opened.Storage!);
                        crash.Line($"loaded {result.LoadedNumber} {sim.Tick} {StateHash.Compute(sim).Total:x16}");
                        log.Warn(sim.Tick, "save", "отладочный крючок: процесс замрёт посреди записи", ("plan", crash.Plan));
                    }

                    saver = new WorldSaver(opened.Folder, opened.BaseSha!, opened.Name, log, o.AutosaveSeconds, sim.Tick, opened.Loaded != null, crash);
                }

                Loop(o, sim, bots, record, replay, saver, log, result, stop);
                record?.WriteEnd(sim.Tick);
                if (saver != null && result.ExitCode == 0)
                {
                    saver.SaveOnStop(sim);
                    result.SavesWritten = saver.Written;
                    result.LastSavedNumber = saver.LastNumber;
                }

                result.FinalHash = StateHash.Compute(sim).Total;
            }
            finally
            {
                replay?.Dispose();
            }
        }

        // ---------- Цикл ----------

        private static void Loop(
            ServerOptions o, Simulation sim, SimBot[] bots, InputJournalWriter? record, InputJournalReader? replay,
            WorldSaver? saver, JsonLog log, ServerResult result, CancellationToken stop)
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
                sim.RefillPools(); // вне такта: запас словарей построек (backlog [такт сервера])
                saver?.AfterTick(sim); // граница такта: автосохранение (0-009)

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
                    long dropped = clock.WaitNext(stop);
                    if (dropped > 0)
                    {
                        log.Warn(tick, "perf", "перегрузка: такты не догоняются", ("dropped", dropped));
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
            public LoadedWorldState? Loaded;
            public string Name = string.Empty;

            public void Dispose() => Lock?.Dispose();
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
                    // Загруженный мир: боты берут сущности из сохранения по порядку, недостающих — ставят.
                    int id = index < sim.EntityCount ? sim.Entities[index].Id : sim.AddEntity(SpawnPoint(sim.Shape, index));
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
