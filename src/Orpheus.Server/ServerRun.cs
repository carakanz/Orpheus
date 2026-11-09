using System;
using System.IO;
using System.Threading;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Platform.Pc;
using Orpheus.Core.World;

namespace Orpheus.Server
{
    /// <summary>
    /// Программа <c>OrpheusServer</c> (ПК): тонкая оболочка над <see cref="ServerHost"/> (0-031, docs/07 1.3 и 1.6). Подставляет
    /// платформу «ПК»: хранилище <see cref="PcStorage"/> (атомарная запись 6.5), часы <see cref="PcTickClock"/> (таймер высокого
    /// разрешения), лог JSON-строками <see cref="JsonLog"/> (файл и консоль), память процесса <see cref="ProcessRuntimeStats"/>,
    /// журналы ввода файлами, отладочный крючок сохранений <see cref="SaveCrashHook"/>, отчёт <c>--report</c>. Ключи и поведение —
    /// прежние (0-006 … 0-030).
    /// </summary>
    public static class ServerRun
    {
        /// <inheritdoc cref="ServerHost.BaseFileName"/>
        public const string BaseFileName = ServerHost.BaseFileName;

        /// <inheritdoc cref="ServerHost.ReadyMarker"/>
        public const string ReadyMarker = ServerHost.ReadyMarker;

        /// <inheritdoc cref="ServerHost.DummyEntityId"/>
        public const int DummyEntityId = ServerHost.DummyEntityId;

        /// <inheritdoc cref="ServerHost.DummyPeriodFrames"/>
        public const int DummyPeriodFrames = ServerHost.DummyPeriodFrames;

        /// <inheritdoc cref="ServerHost.SeamLaneBefore"/>
        public const int SeamLaneBefore = ServerHost.SeamLaneBefore;

        /// <inheritdoc cref="ServerHost.SeamLaneAfter"/>
        public const int SeamLaneAfter = ServerHost.SeamLaneAfter;

        public static ServerResult Run(ServerOptions o, TextWriter? console, CancellationToken stop = default)
        {
            string? logsDir = o.LogsDir ?? (o.WorldDir != null ? Path.Combine(o.WorldDir, "logs") : null);
            using var log = new JsonLog(logsDir, console, o.LogLevels);
            using var stats = new ProcessRuntimeStats();
            var storage = new PcStorage();
            var services = new ServerHostServices
            {
                Log = log,
                Storage = storage,
                CreateClock = rate => new PcTickClock(rate),
                Stats = stats,
                ReadyOutput = console,
                OpenRead = path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16),
                CreateWrite = CreateFile,
                ProcessId = Environment.ProcessId,
            };

            if (o.DebugSaveCrashSeed.HasValue || o.DebugSavePlan != null)
            {
                services.SaveHook = o.DebugSavePlan != null
                    ? SaveCrashHook.Fixed(o.DebugSavePlan, Console.Error)
                    : new SaveCrashHook(o.DebugSaveCrashSeed!.Value, Console.Error);
            }

            ServerResult result = ServerHost.Run(o, services, stop);
            if (o.ReportPath != null && result.ExitCode != 2)
            {
                WriteReport(o, result);
            }

            return result;
        }

        /// <inheritdoc cref="ServerHost.SpawnPoint(MapShape, int)"/>
        public static WorldPos SpawnPoint(MapShape shape, int index) => ServerHost.SpawnPoint(shape, index);

        /// <inheritdoc cref="ServerHost.SpawnPoint(MapShape, int, TileCoord)"/>
        public static WorldPos SpawnPoint(MapShape shape, int index, TileCoord center) => ServerHost.SpawnPoint(shape, index, center);

        /// <inheritdoc cref="ServerHost.DefaultSpawnCenter"/>
        public static TileCoord DefaultSpawnCenter(MapShape shape) => ServerHost.DefaultSpawnCenter(shape);

        /// <inheritdoc cref="ServerHost.SeamSpawnCenter"/>
        public static TileCoord SeamSpawnCenter(Simulation sim, out bool clear) => ServerHost.SeamSpawnCenter(sim, out clear);

        private static void WriteReport(ServerOptions o, ServerResult result)
        {
            var report = new ServerReport
            {
                Mode = o.Sim ? "sim" : o.Port.HasValue ? "udp" : "clock",
                Size = result.WorldSize,
                Bots = o.Bots.Count == 0 ? "" : string.Join(",", o.Bots),
                Port = result.Port,
                Machine = $"{Environment.MachineName}, {Environment.ProcessorCount} логич. ядер, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}",
#if DEBUG
                Configuration = "Debug",
#else
                Configuration = "Release",
#endif
                ReadySeconds = result.ReadySeconds,
                ReadyWorkingSetMb = result.ReadyWorkingSetMb,
                ReadyPeakWorkingSetMb = result.ReadyPeakWorkingSetMb,
                Total = result.Metrics ?? new MetricsSample(),
                Windows = result.MetricsWindows,
            };
            report.Write(o.ReportPath!);
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
    }
}
