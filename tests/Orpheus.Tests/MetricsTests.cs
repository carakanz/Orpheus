using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Orpheus.Platform.Pc;
using Orpheus.Server;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Метрики такта сервера (0-030, docs/07 11.6): процентили по гистограмме, части такта, простои по часам, итог и отчёт
    /// <c>--report</c>. Без выделений в такте — <see cref="AllocationTests"/>.
    /// </summary>
    [Collection(HeavyCollection.Name)]
    public sealed class MetricsTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _dir;

        public MetricsTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "orpheus-metrics-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void D07_S11_6_TickStats_MeanPercentilesMax()
        {
            var s = new TickStats();
            var parts = new long[TickMetrics.PartCount];
            var alloc = new long[TickMetrics.PartCount];

            // 990 тактов по 1 мс, 9 по 15 мс, 1 по 250 мс (за краем гистограммы — наибольшее точно).
            for (int i = 0; i < 990; i++)
            {
                s.AddTick(1000, parts, alloc, 0);
            }

            for (int i = 0; i < 9; i++)
            {
                s.AddTick(15_000, parts, alloc, 0);
            }

            s.AddTick(250_000, parts, alloc, 64);

            Assert.Equal(1000, s.Ticks);
            Assert.Equal((990 * 1.0 + 9 * 15 + 250) / 1000, s.MeanMs, 6);
            Assert.Equal(1.01, s.PercentileMs(0.5), 6); // верхняя граница корзины 10 мкс
            Assert.Equal(1.01, s.PercentileMs(0.99), 6); // 990-й такт — ещё 1 мс
            Assert.Equal(15.01, s.PercentileMs(0.995), 6);
            Assert.Equal(250, s.PercentileMs(0.9999), 6); // последняя корзина — не больше наибольшего
            Assert.Equal(250, s.MaxMs, 6);
            Assert.Equal(64, s.AllocatedInTicks);
            Assert.Equal(1, s.TicksWithAllocations);
        }

        /// <summary>Режим симуляции: части такта складываются в такт, отчёт <c>--report</c> пишется и читается.</summary>
        [Fact]
        public void D07_S11_6_Sim_PartsSumToTick_ReportWritten()
        {
            string report = Path.Combine(_dir, "r.json");
            ServerResult r = ServerRun.Run(
                ServerOptions.Parse(new[] { "--create", "--seed", "42", "--size", "1024", "--sim", "--bots", "8:wander", "--ticks", "600", "--metrics", "0", "--report", report }),
                null);
            Assert.Equal(0, r.ExitCode);
            MetricsSample m = r.Metrics!;
            Assert.NotNull(m);
            Assert.Equal(600, m.Ticks);
            Assert.Equal(TickMetrics.PartCount, m.Parts.Length);
            double parts = m.Parts.Sum(p => p.MeanMs);
            _out.WriteLine($"такт ср. {m.TickMeanMs:F4} мс, части {parts:F4} мс: " + string.Join("; ", m.Parts.Select(p => $"{p.Name} {p.MeanMs:F4} ({p.Percent:F0} %)")));
            Assert.InRange(parts, m.TickMeanMs * 0.95 - 0.001, m.TickMeanMs * 1.05 + 0.001);
            Assert.True(m.Parts[(int)TickPart.Sim].MeanMs > 0, "шаг симуляции не измерен");
            Assert.Equal(0, m.Stalls); // без часов простоев нет
            Assert.True(m.WorkingSetMb > 0 && m.HeapMb > 0);
            Assert.True(r.ReadySeconds > 0);

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(report));
            JsonElement total = doc.RootElement.GetProperty("Total");
            Assert.Equal(600, total.GetProperty("Ticks").GetInt64());
            Assert.Equal("sim", doc.RootElement.GetProperty("Mode").GetString());
            Assert.Equal(1024, doc.RootElement.GetProperty("Size").GetInt32());
            Assert.Equal(TickMetrics.PartCount, total.GetProperty("Parts").GetArrayLength());
        }

        /// <summary>
        /// Простои по часам (0-030, «Добавлено Продюсером»): заморозка потока такта на 150 мс — один простой, промежуток
        /// ≥ 150 мс; само время такта заморозку не включает (она между тактами, как сборка мусора или антивирус).
        /// </summary>
        [Fact]
        public void D07_S11_6_Clock_FreezeIsStall_NotTickTime()
        {
            ServerResult r = ServerRun.Run(
                ServerOptions.Parse(new[] { "--create", "--seed", "42", "--size", "1024", "--bots", "2:wander", "--ticks", "30", "--metrics", "0", "--debug-freeze", "10:150" }),
                null);
            Assert.Equal(0, r.ExitCode);
            MetricsSample m = r.Metrics!;
            _out.WriteLine($"тактов {m.Ticks}, промежутков {m.Gaps}, простоев {m.Stalls} ({m.StallPercent:F1} %), промежуток max {m.MaxGapMs:F1} мс, такт max {m.TickMaxMs:F2} мс");
            Assert.Equal(30, m.Ticks);
            Assert.Equal(29, m.Gaps);
            // P-003 (08.11, «время хрупко под нагрузкой»): правило 0-030 — заморозка = простой, промежуток ≥ 150 мс,
            // в время такта не входит. Прежние проверки считали МОЛЧАНИЕ ПЛАНИРОВЩИКА машины: порог простоя =
            // такт (33 мс) + 5 мс, под `-All` в 30-секундном прогоне зазоры > 38 мс случаются десятки раз →
            // «простоев > 3» краснел без дефекта продукта. Теперь — событие: заморозка посчитана простым (≥ 1);
            // число посторонних простоев печатается. Порог времени такта = длина заморозки (150 мс): во время такта
            // она попадает только при дефекте учёта (такт ≈ 151+ мс); шум планировщика 100–150 мс — не дефект.
            Assert.True(m.Stalls >= 1, $"простоев {m.Stalls} — заморозка не посчитана простым");
            Assert.True(m.MaxGapMs >= 150, $"{m.MaxGapMs}");
            Assert.True(m.TickMaxMs < 150, $"заморозка попала во время такта: {m.TickMaxMs} мс");
        }

        // ---------- Приёмка 0.8: старт и память сервера (07 1.9 п. 4) ----------

        /// <summary>Старт на 1024² до приёма подключений, с (07 1.9 п. 4).</summary>
        private const double ReadyMaxSeconds = 5;

        /// <summary>Память сервера 1024² после старта, МБ (07 1.9 п. 4).</summary>
        private const double MemoryMaxMb = 128;

        /// <summary>
        /// Приёмка 0.8, серверная часть (0-030): собранный <c>OrpheusServer</c> отдельным процессом на 1024² — новый мир
        /// (<c>--create</c>) и загрузка того же мира; от запуска процесса до готовности ≤ 5 с (секунды меряет сам сервер —
        /// <c>sinceProcessStartSec</c> в логе, P-003), рабочий набор через 3 с после готовности ≤ 128 МБ. Пик рабочего набора
        /// (создание мира) и загрузка машины — в вывод. Процесс без сети наружу (петля), снимается по своему объекту процесса.
        /// </summary>
        [Fact]
        [Trait("Category", "Big")]
        public void D07_S1_9_4_Big_Server1024_ReadyWithin5s_Under128Mb()
        {
            string world = Path.Combine(_dir, "w1024");
            using var cpu = new CpuLoad();
            (double createWall, double createSec, double createWs, double createPeak) = StartServer("--world", world, "--create", "--seed", "42", "--size", "1024", "--port", "0", "--metrics", "0");
            (double loadWall, double loadSec, double loadWs, double loadPeak) = StartServer("--world", world, "--port", "0", "--metrics", "0");
            CpuLoad.Summary load = cpu.Stop();
            _out.WriteLine($"новый мир: сервер готов за {createSec:F3} с (wall-clock {createWall:F2} с), рабочий набор {createWs:F0} МБ, пик {createPeak:F0} МБ");
            _out.WriteLine($"загрузка мира: сервер готов за {loadSec:F3} с (wall-clock {loadWall:F2} с), рабочий набор {loadWs:F0} МБ, пик {loadPeak:F0} МБ");
            _out.WriteLine($"загрузка: {load}");
            // P-003: проверяется серверное число (от запуска процесса, его меряет сам сервер), порог 5 с не ослаблен.
            Assert.True(createSec <= ReadyMaxSeconds, $"новый мир: серверный старт {createSec:F2} с > {ReadyMaxSeconds} с (wall-clock {createWall:F2} с); {load}");
            Assert.True(loadSec <= ReadyMaxSeconds, $"загрузка: серверный старт {loadSec:F2} с > {ReadyMaxSeconds} с (wall-clock {loadWall:F2} с); {load}");
            Assert.True(createWs <= MemoryMaxMb, $"новый мир: {createWs:F0} МБ > {MemoryMaxMb} МБ");
            Assert.True(loadWs <= MemoryMaxMb, $"загрузка: {loadWs:F0} МБ > {MemoryMaxMb} МБ");
        }

        /// <summary>Запустить сервер, дождаться строки готовности (маркер + серверные секунды), через 3 с снять память и снять процесс.</summary>
        /// <remarks>
        /// P-003 (Big, случай 13.11 «ReadyWithin5s»): правило 1.9 п. 4 — секунды ОТ ЗАПУСКА ПРОЦЕССА до готовности, их меряет
        /// сам сервер и пишет в лог «готов: принимаю подключения» (<c>sinceProcessStartSec</c>, см. <c>ServerHost.ReadyMetrics</c>).
        /// Wall-clock от <c>Process.Start</c> до маркера включает создание процесса ОС и планировщик — под <c>-All</c> это шум;
        /// он печатается для справки, проверка идёт по числу самого процесса, порог 5 с не ослаблен.
        /// </remarks>
        private static (double WallClockSec, double ServerReadySec, double WorkingSetMb, double PeakMb) StartServer(params string[] args)
        {
            string exe = PcServerLauncher.DefaultPath(AppContext.BaseDirectory);
            bool dll = exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            using var ready = new ManualResetEventSlim();
            var watch = Stopwatch.StartNew();
            double readySec = -1;
            double serverSec = -1;
            // C-022: на невидимом рабочем столе (не мигать экраном Автора).
            using HiddenProcess p = HiddenProcess.Start(dll ? "dotnet" : exe, dll ? new[] { exe }.Concat(args) : args, AppContext.BaseDirectory);
            p.BeginReadLines(data =>
            {
                if (data == null)
                {
                    return;
                }

                if (data.StartsWith(ServerRun.ReadyMarker, StringComparison.Ordinal) && readySec < 0)
                {
                    readySec = watch.Elapsed.TotalSeconds; // справка: wall-clock лаунчера (не проверяется)
                }

                // P-003: строка «готов: принимаю подключения» — JSON-строка JsonLog в консоли (ServerRun.cs:41), поле
                // data.sinceProcessStartSec — секунды от запуска процесса, их меряет сам сервер (ServerHost.ReadyMetrics).
                if (data.Contains("sinceProcessStartSec", StringComparison.Ordinal) && serverSec < 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (doc.RootElement.TryGetProperty("data", out JsonElement d) &&
                            d.TryGetProperty("sinceProcessStartSec", out JsonElement v) &&
                            v.TryGetDouble(out double s))
                        {
                            serverSec = s;
                            ready.Set();
                        }
                    }
                    catch (JsonException)
                    {
                        // строка не JSON (например, маркер готовности) — пропускаем
                    }
                }
            }, _ => { });
            try
            {
                Assert.True(ready.Wait(TimeSpan.FromSeconds(60)) || p.HasExited, "сервер не готов за 60 с");
                Assert.False(p.HasExited && readySec < 0, $"сервер вышел до готовности, код {(p.HasExited ? p.ExitCode : 0)}");
                Assert.True(serverSec >= 0, $"сервер не написал sinceProcessStartSec (wall-clock {readySec:F2} с)");
                Thread.Sleep(3000);
                using Process mem = Process.GetProcessById(p.Id); // ручка процесса у нас — PID не переиспользован
                return (readySec, serverSec, mem.WorkingSet64 / 1048576.0, mem.PeakWorkingSet64 / 1048576.0);
            }
            finally
            {
                if (!p.HasExited)
                {
                    p.Kill(); // свой процесс (объект процесса — не чужой PID); замок мира отпустит ОС
                    p.WaitForExit(5000);
                }
            }
        }
    }
}
