using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Orpheus.Save;
using Orpheus.Server;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Приёмка 0.4 (docs/08 3.0): «процесс сервера убивается в случайный миг записи 1000 раз — мир
    /// загружается из последнего целого поколения 1000 из 1000» (`07`, 6.10 п. 1 и 6.5 п. 5).
    /// <para>
    /// Дочерний <c>OrpheusServer</c> убивают по отладочному крючку (<see cref="SaveCrashHook"/>): в stderr
    /// появляется строка <c>ORPHEUS-DEBUG crash-point &lt;миг&gt; &lt;смещение&gt; &lt;длина&gt;</c> — процесс снимают
    /// в эту секунду. Миг задаёт тест (<c>--debug-save-plan</c>) или жребий по зерну (<c>--debug-save-crash</c>).
    /// Перезапуск-свидетель (<c>--debug-save-plan moved@99</c>: крючок висит, печатает
    /// <c>ORPHEUS-DEBUG loaded &lt;поколение&gt; &lt;такт&gt; &lt;хэш&gt;</c> и обрыва не делает) показывает, что мир
    /// вправду взят из нужного поколения: сверяют номер, такт и хэш состояния с строкой
    /// <c>save-begin</c> того же сохранения.
    /// </para>
    /// <para>
    /// Наполнение <c>--debug-save-pad</c> — раздел «ОТЛАДКА» (<see cref="SaveSections.DebugPad"/>), который
    /// грузчик пропускает как незнакомый: без него файл поколения (236 байт) короче куска атомарной записи
    /// (512 байта), и обрыв физически не попадает в середину файла — приёмка ничего бы не проверяла.
    /// </para>
    /// </summary>
    public sealed class SaveKillTests : IDisposable
    {
        private const ulong Seed = 0x0404;

        /// <summary>Наполнение сохранения, КБ: файл поколения ≈ 25 КБ — в 48 раз длиннее куска записи.</summary>
        private const int PadKb = 24;

        private const int AutosaveSeconds = 1;

        /// <summary>Тактов в прогоне под обрывом для приёмки 0.4 (автосохранение в секунду мира → 4 сохранения).</summary>
        private const int KillTicks = 120;

        /// <summary>Тактов в прогоне по мигам (6 сохранений: есть где повесить обрыв четвёртого).</summary>
        private const int StageTicks = 200;

        private const int RunTimeoutMs = 120_000;

        /// <summary>Обрывов за приёмку 0.4; <c>ORPHEUS_KILL_RUNS</c> — меньше, для быстрого прогона на разработке.</summary>
        private const int KillRunsDefault = 1000;

        /// <summary>
        /// Обрывов в быстром CI (0-016): 40 ≈ 8 с (100 — 21 с). Тысяча — ночью (<c>Category=Big</c>). Зёрна — те же 1…40,
        /// что открывают ночной прогон: жребий даёт все миги (смещение в файле, «записано», «сброшено», «переименовано»).
        /// </summary>
        private const int KillRunsFast = 40;

        private static readonly string ServerApp = Path.Combine(AppContext.BaseDirectory, "OrpheusServer.exe");
        private static readonly string ServerDll = Path.Combine(AppContext.BaseDirectory, "OrpheusServer.dll");

        /// <summary>
        /// Хост для запуска: приложение-оболочка <c>OrpheusServer.exe</c> рядом с тестами, а если его нет —
        /// <c>dotnet</c> из PATH. <see cref="Environment.ProcessPath"/> не годится: под <c>dotnet test</c>
        /// это <c>testhost.exe</c>, и он решает запускать самого себя.
        /// </summary>
        private static string Host => File.Exists(ServerApp) ? ServerApp : "dotnet";

        private readonly ITestOutputHelper _out;
        private readonly string _root;
        private readonly string _logs;

        public SaveKillTests(ITestOutputHelper output)
        {
            _out = output;
            _root = Path.Combine(Path.GetTempPath(), "orpheus-kill-" + Guid.NewGuid().ToString("N"));
            _logs = Path.Combine(_root, "logs");
            Directory.CreateDirectory(_logs);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        // ---------- Миги записи (задание крючка ключом, а не жребием) ----------

        [Fact]
        public void D00_Save_Kill_PlanSpec_NamesEveryStage()
        {
            foreach (string stage in new[] { "writing", "written", "flushed", "moved", "pruned" })
            {
                SaveCrashHook hook = SaveCrashHook.Fixed(stage + ":40@2", new StringWriter());
                Assert.True(hook.Plan.Contains("сохранение 2", StringComparison.Ordinal), hook.Plan);
                Assert.True(hook.Plan.Contains(stage == "pruned" ? "после удаления старых" : stage, StringComparison.OrdinalIgnoreCase), hook.Plan);
            }
        }

        [Fact]
        public void D00_Save_Kill_PlanSpec_BadSpec_Throws()
        {
            foreach (string bad in new[] { "", "midway", "writing:x", "writing:101", "writing@0", "writing@x" })
            {
                Assert.Throws<ArgumentException>(() => SaveCrashHook.Fixed(bad, new StringWriter()));
            }
        }

        // ---------- Приёмка 0.4: каждый миг обрыва ----------

        /// <param name="plan">Миг обрыва по второму сохранению (<c>pruned</c> — по четвёртому).</param>
        /// <param name="loadIndex">Какой по счёке сохранения этого запуска обязан загрузиться (0 — первое).</param>
        /// <param name="gens">Поколения, лежащие в папке сразу после убийства (перечислить через запятую).</param>
        [Theory]
        [Trait("Category", "Big")]
        [InlineData("writing:10@2", 0, "1")]
        [InlineData("writing:90@2", 0, "1")]
        [InlineData("written@2", 1, "1")]
        [InlineData("flushed@2", 1, "1")]
        [InlineData("moved@2", 1, "1,2")]
        [InlineData("pruned@4", 3, "2,3,4")]
        public void D00_Save_Kill_EachStage_LoadsLastIntactGeneration(string plan, int loadIndex, string gens)
        {
            string world = World("stage-" + plan.Replace(':', '-').Replace('@', '_'));

            var log = new List<string>();
            RunServer(world, RunArgs(StageTicks, create: true, plan: plan), killOnCrash: true, log, out _);

            List<(ulong Number, long Tick, string Hash)> saves = SaveBegins(log);
            Assert.True(saves.Count >= 2, $"сохранений меньше двух: {string.Join(" | ", log)}");
            Assert.True(loadIndex < saves.Count, $"план {plan}: сохранение №{loadIndex + 1} не успело начаться");
            (string stage, long offset, long total) = CrashPoint(log);

            // Наполнение обязано делать файл длиннее куска записи: иначе обрыв не в середине файла.
            // Миг «pruned» — после записи: длины в маркере нет (обрыв между файлами, не в файле).
            if (stage != "pruned")
            {
                Assert.True(total > PadKb * 1024, $"файл поколения {total} байт — наполнение {PadKb} КБ не сработало");
            }
            Assert.Equal(ParseNumbers(gens), Generations(world));

            string[] tmps = TmpFiles(world);
            long doomedLength = tmps.Length == 0 ? 0 : new FileInfo(Path.Combine(world, tmps[0])).Length;
            if (stage == "Writing")
            {
                // Писали на полном пару: переименования не было, на носителе — огрызок.
                Assert.Single(tmps);
                Assert.True(doomedLength > 0 && doomedLength < total,
                    $"«{tmps[0]}» {doomedLength} байт при длине {total}: обрыв в миге «писали» обязан оставить файл надвое");
                Assert.Equal(offset, doomedLength);
            }
            else if (stage == "Moved")
            {
                // Переименование со сбросом успело: огрызка в папке нет, поколение на месте.
                Assert.Empty(tmps);
            }
            else if (stage != "pruned")
            {
                // Файл отдан ОС целиком — на то, что процесс сняли, он не влияет.
                Assert.Single(tmps);
                Assert.Equal(total, doomedLength);
            }

            // Перезапуск-свидетель: мир берём из новейшего целого поколения.
            var witness = new List<string>();
            Assert.Equal(0, RunServer(world, WitnessArgs(), killOnCrash: false, witness, out _));

            (ulong number, long tick, string hash) = LoadedLine(witness);
            (ulong wantNumber, long wantTick, string wantHash) = saves[loadIndex];
            Assert.Equal(wantNumber, number);
            Assert.Equal(wantTick, tick);
            Assert.Equal(wantHash, hash);

            // Битый огрызок убран из папки мира (не удалён), целый .tmp продвинут, .tmp в папке не остаётся.
            Assert.Empty(TmpFiles(world));
            Assert.Equal(new[] { wantNumber }.Union(ParseNumbers(gens)).OrderBy(x => x).ToArray(), Generations(world));
            Assert.Equal(stage == "Writing" ? 1 : 0, DamagedFiles(world).Length);
        }

        [Fact]
        [Trait("Category", "Big")]
        public void D00_Save_Kill_FirstGeneration_RestartRefuses_AndLeavesFilesAlone()
        {
            string world = World("first");

            var log = new List<string>();
            RunServer(world, RunArgs(StageTicks, create: true, plan: "writing:50@1"), killOnCrash: true, log, out _);

            Assert.Empty(Generations(world));
            string[] tmps = TmpFiles(world);
            (string stage, long offset, long total) = CrashPoint(log);
            Assert.Single(tmps);
            Assert.Equal("Writing", stage);
            Assert.True(offset > 0 && offset < total);

            var witness = new List<string>();
            int code = RunServer(world, WitnessArgs(), killOnCrash: false, witness, out string stdout);

            // Единственное поколение битое: отказ, а не молчаливый откат к снимку генерации (6.5 п. 5).
            Assert.Equal(2, code);
            Assert.True(stdout.Contains("Все поколения мира повреждены", StringComparison.Ordinal), stdout);
            Assert.DoesNotContain("loaded", string.Join("\n", witness));
            Assert.Equal(tmps, TmpFiles(world));
            Assert.False(Directory.Exists(Path.Combine(world, WorldFolder.BackupsDir, WorldFolder.DamagedDirName)));
        }

        // ---------- Приёмка 0.4: старт прунит до трёх последних ----------

        /// <summary>
        /// «Три последних поколения» (6.5 п. 5) обязаны считаться по папке, а не только в конце удачной
        /// записи: обрыв сразу после переименования (<c>moved@1</c>) не даёт чистке начаться, и при
        /// повторных обрывах папка бы росла бесконечно. Старт после восстановления удаляет лишние.
        /// В быстром CI (0-016): без него удаление прунинга на старте видно только ночью.
        /// </summary>
        [Fact]
        public void D00_Save_Kill_StartPrune_KeepsThreeNewestGenerations()
        {
            string world = World("prune");

            for (int n = 1; n <= 4; n++)
            {
                var log = new List<string>();
                RunServer(world, RunArgs(40, create: n == 1, plan: "moved@1"), killOnCrash: true, log, out _);
                Assert.Equal(Enumerable.Range(1, n).Select(i => (ulong)i).ToArray(), Generations(world));
            }

            var witness = new List<string>();
            Assert.Equal(0, RunServer(world, WitnessArgs(), killOnCrash: false, witness, out _));
            (ulong number, long tick, string hash) = LoadedLine(witness);

            Assert.Equal(new ulong[] { 2, 3, 4 }, Generations(world));
            Assert.Equal(4UL, number);
            Assert.Empty(TmpFiles(world));
            Assert.Empty(DamagedFiles(world));
        }

        // ---------- Приёмка 0.4: случайные обрывы — 40 в быстром CI, 1000 ночью ----------

        [Fact]
        public void D00_Save_Kill_RandomMoments_Fast_WorldLoadsLastIntactGeneration() =>
            RandomKills(KillRunsFast, "random-fast");

        [Fact]
        [Trait("Category", "Big")]
        public void D00_Save_Kill_RandomMoments_ThousandTimes_WorldLoadsLastIntactGeneration() =>
            RandomKills(KillRuns(), "random");

        /// <summary>
        /// <paramref name="runs"/> обрывов по зёрнам 1…<paramref name="runs"/>; после каждого — свидетель обязан
        /// загрузить последнее целое поколение (номер, такт и хэш — как в <c>save-begin</c>).
        /// </summary>
        private void RandomKills(int runs, string tag)
        {
            string world = World(tag);

            // Известные хэши поколений: что сохранили — то и берём (номер → такт и хэш из save-begin).
            var known = new Dictionary<ulong, (long Tick, string Hash)>();

            var first = new List<string>();
            Assert.Equal(0, RunServer(world, RunArgs(60, create: true, plan: "moved@99"), killOnCrash: false, first, out _));
            Remember(first, known);
            Assert.True(known.Count >= 2, "первый прогон не написал поколений: " + string.Join(" | ", first));

            int loaded = 0;
            for (int i = 1; i <= runs; i++)
            {
                var log = new List<string>();
                RunServer(world, RunArgs(KillTicks, create: false, plan: i.ToString(CultureInfo.InvariantCulture), planBySeed: true),
                    killOnCrash: true, log, out _);
                Remember(log, known);

                List<(ulong Number, long Tick, string Hash)> saves = SaveBegins(log);
                Assert.True(saves.Count >= 1, $"прогон {i}: ни одного сохранения: {string.Join(" | ", log)}");
                ulong target = saves[saves.Count - 1].Number;
                (string stage, long offset, long total) = CrashPoint(log);

                // Обрыв «писали» оставляет огрызок: целое — предыдущее поколение; отданный ОС файл цел.
                ulong expected = stage == "Writing" && offset < total ? target - 1 : target;

                var witness = new List<string>();
                Assert.Equal(0, RunServer(world, WitnessArgs(), killOnCrash: false, witness, out _));
                (ulong number, long tick, string hash) = LoadedLine(witness);

                Assert.Equal(expected, number);
                Assert.Equal(known[number].Tick, tick);
                Assert.Equal(known[number].Hash, hash);
                Assert.Empty(TmpFiles(world));
                // «Три последних» — и после обрыва: старт прунит папку (см. D00_Save_Kill_StartPrune...).
                Assert.InRange(Generations(world).Length, 1, WorldFolder.KeepGenerations);

                loaded++;
            }

            // «Мир загружается из последнего целого поколения 1000 из 1000».
            Assert.Equal(runs, loaded);
            _out.WriteLine($"обрывов записи: {runs}, загрузок из целого поколения: {loaded}");
        }

        // ---------- Запуск дочернего сервера ----------

        /// <summary>
        /// Дочерний сервер: <paramref name="args"/> — ключи после <c>--world</c>; при <paramref name="killOnCrash"/>
        /// ждать маркера <c>crash-point</c> и снять процесс, иначе — до конца. Строки stderr — в <paramref name="log"/>.
        /// </summary>
        private int RunServer(string worldDir, string[] args, bool killOnCrash, List<string> log, out string stdout)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Host,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // Сервер пишет UTF-8 (Program): без этого русские строки лога придут кракозябрами.
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            if (Host != ServerApp)
            {
                psi.ArgumentList.Add(ServerDll);
            }
            psi.ArgumentList.Add("--world");
            psi.ArgumentList.Add(worldDir);
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            psi.ArgumentList.Add("--logs");
            psi.ArgumentList.Add(_logs);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("не запустился " + Host);
            Exception? failure = null;
            try
            {
                // Чтение stderr — с тем же сроком, что и ожидание выхода: зависший сервер с открытым stderr
                // иначе подвесил бы ReadLine навсегда.
                var deadline = Stopwatch.StartNew();
                bool sawCrash = false;
                while (true)
                {
                    var read = process.StandardError.ReadLineAsync();
                    int left = (int)Math.Max(0, RunTimeoutMs - deadline.ElapsedMilliseconds);
                    Assert.True(read.Wait(left), $"сервер молчит {RunTimeoutMs} мс: {string.Join(" | ", log)}");
                    string? line = read.Result;
                    if (line == null)
                    {
                        break;
                    }

                    log.Add(line);
                    if (line.StartsWith(SaveCrashHook.Marker, StringComparison.Ordinal) && line.Contains("crash-point", StringComparison.Ordinal))
                    {
                        sawCrash = true;
                        break;
                    }
                }

                if (killOnCrash)
                {
                    Assert.True(sawCrash, $"крючок не объявился: {string.Join(" | ", log)}");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
                else
                {
                    Assert.True(process.WaitForExit(RunTimeoutMs), $"сервер не завершился за {RunTimeoutMs} мс");
                }

                stdout = process.StandardOutput.ReadToEnd();
                return process.ExitCode;
            }
            catch (Exception e)
            {
                failure = e;
                throw;
            }
            finally
            {
                // Проверка упала (нет маркера, таймаут, молчание) — дочерний сервер не остаётся висеть:
                // крючок после crash-point ждёт вечно (0-016, замечание 5 проверки 0-009).
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit();
                    }
                    catch (Exception stop) when (failure != null)
                    {
                        // Отказ снятия не заслоняет исходное падение: оно — в сообщении и внутренним (0-020, замечание 5 проверки 0-016).
                        throw new InvalidOperationException($"{failure.Message}{Environment.NewLine}(и сервер не снялся: {stop.Message})", failure);
                    }
                }
            }
        }

        /// <param name="plan">Миг обрыва: план (<see cref="SaveCrashHook.Fixed"/>) — или, при <paramref name="planBySeed"/>,
        /// зерно жребия (<c>--debug-save-crash</c>). Нет — крючка нет.</param>
        private static string[] RunArgs(int ticks, bool create, string? plan = null, bool planBySeed = false)
        {
            var args = new List<string>();
            if (create)
            {
                args.Add("--create");
                args.Add("--seed");
                args.Add(Seed.ToString(CultureInfo.InvariantCulture));
                args.Add("--size");
                args.Add("1024");
            }

            args.Add("--sim");
            args.Add("--bots");
            args.Add("1:wander");
            args.Add("--ticks");
            args.Add(ticks.ToString(CultureInfo.InvariantCulture));
            args.Add("--autosave");
            args.Add(AutosaveSeconds.ToString(CultureInfo.InvariantCulture));
            args.Add("--debug-save-pad");
            args.Add(PadKb.ToString(CultureInfo.InvariantCulture));

            if (plan != null)
            {
                args.Add(planBySeed ? "--debug-save-crash" : "--debug-save-plan");
                args.Add(plan);
            }

            return args.ToArray();
        }

        /// <summary>
        /// Перезапуск-свидетель: ни одного такта, ботов нет (иначе в хэш войдут заново посеянные), план
        /// «moved@99» — крючок печатает <c>loaded</c> и никогда не срабатывает.
        /// </summary>
        private static string[] WitnessArgs() => new[]
        {
            "--ticks", "0",
            "--debug-save-plan", "moved@99",
            "--debug-save-pad", PadKb.ToString(CultureInfo.InvariantCulture),
        };

        // ---------- Разбор строк крючка ----------

        /// <summary>Строки <c>save-begin &lt;поколение&gt; &lt;такт&gt; &lt;хэш&gt;</c> по порядку.</summary>
        private static List<(ulong Number, long Tick, string Hash)> SaveBegins(List<string> log)
        {
            var list = new List<(ulong, long, string)>();
            foreach (string line in log)
            {
                string[] p = Split(line);
                if (p.Length == 5 && p[1] == "save-begin")
                {
                    list.Add((ulong.Parse(p[2], CultureInfo.InvariantCulture), long.Parse(p[3], CultureInfo.InvariantCulture), p[4]));
                }
            }

            return list;
        }

        /// <summary>Миг обрыва: <c>crash-point &lt;миг&gt; &lt;смещение&gt; &lt;длина&gt;</c>.</summary>
        private static (string Stage, long Offset, long Total) CrashPoint(List<string> log)
        {
            foreach (string line in log)
            {
                string[] p = Split(line);
                if (p.Length == 5 && p[1] == "crash-point")
                {
                    return (p[2], long.Parse(p[3], CultureInfo.InvariantCulture), long.Parse(p[4], CultureInfo.InvariantCulture));
                }
            }

            throw new InvalidDataException("нет строки crash-point: " + string.Join(" | ", log));
        }

        /// <summary>Что взяло перезапустившийся сервер: <c>loaded &lt;поколение&gt; &lt;такт&gt; &lt;хэш&gt;</c>.</summary>
        private static (ulong Number, long Tick, string Hash) LoadedLine(List<string> log)
        {
            foreach (string line in log)
            {
                string[] p = Split(line);
                if (p.Length == 5 && p[1] == "loaded")
                {
                    return (ulong.Parse(p[2], CultureInfo.InvariantCulture), long.Parse(p[3], CultureInfo.InvariantCulture), p[4]);
                }
            }

            throw new InvalidDataException("свидетель не сообщил loaded: " + string.Join(" | ", log));
        }

        private static void Remember(List<string> log, Dictionary<ulong, (long Tick, string Hash)> known)
        {
            foreach ((ulong n, long tick, string hash) in SaveBegins(log))
            {
                known[n] = (tick, hash);
            }
        }

        private static string[] Split(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // ---------- Папка мира ----------

        private string World(string tag)
        {
            string dir = Path.Combine(_root, tag);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static ulong[] Generations(string world) =>
            Directory.GetFiles(world).Select(n => Path.GetFileName(n)!)
                .Where(n => WorldFolder.TryParseGeneration(n, out _, out bool tmp) && !tmp)
                .Select(n => ParseNumber(n))
                .OrderBy(n => n)
                .ToArray();

        private static string[] TmpFiles(string world) =>
            Directory.GetFiles(world).Select(n => Path.GetFileName(n)!)
                .Where(n => WorldFolder.TryParseGeneration(n, out _, out bool tmp) && tmp)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

        private static string[] DamagedFiles(string world)
        {
            string dir = Path.Combine(world, WorldFolder.BackupsDir, WorldFolder.DamagedDirName);
            return Directory.Exists(dir) ? Directory.GetFiles(dir).Select(n => Path.GetFileName(n)!).ToArray() : Array.Empty<string>();
        }

        private static ulong ParseNumber(string generationFileName)
        {
            if (!WorldFolder.TryParseGeneration(generationFileName, out ulong number, out _))
            {
                throw new ArgumentException("не имя поколения: " + generationFileName);
            }

            return number;
        }

        private static ulong[] ParseNumbers(string list) => list.Split(',')
            .Select(s => ulong.Parse(s.Trim(), CultureInfo.InvariantCulture))
            .OrderBy(n => n)
            .ToArray();

        private static int KillRuns()
        {
            string? raw = Environment.GetEnvironmentVariable("ORPHEUS_KILL_RUNS");
            if (raw == null || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int runs) || runs < 1)
            {
                return KillRunsDefault;
            }

            return Math.Min(runs, 10_000);
        }
    }
}
