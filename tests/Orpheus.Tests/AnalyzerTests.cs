using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Orpheus.Analyzers;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Проверка анализатора ORP001–ORP007 и ORP900 (задача 0-003, шаг 4; docs/07, 3.2 и 11.5).
    /// На каждое правило — фрагмент, который обязан дать ошибку, и правильный фрагмент, который молчит.
    /// <para>
    /// Компиляция in-memory своим Roslyn-компилятором (карточка 0-003 допускает «свой компилятор в тесте»):
    /// пакет Microsoft.CodeAnalysis.CSharp 4.3.1 — та же версия, что у анализатора. Ядро в примерах
    /// подменяется мини-заготовками (тот же состав, что в `Orpheus.Core`), чтобы анализатор видел
    /// атрибуты и класс `Torus` внутри компиляции и чтобы проверка не зависела от собранной DLL ядра.
    /// </para>
    /// </summary>
    public class AnalyzerTests
    {
        // ---------- Каркас примеров: то, что анализатор ищет в компиляции ----------

        private const string Header = @"
using System;
using Orpheus.Core;
using Orpheus.Core.Geometry;
using Orpheus.Core.Rng;
using Orpheus.Core.Trig;

namespace Orpheus.Core
{
    public sealed class AllowNondeterministicAttribute : System.Attribute
    {
        public AllowNondeterministicAttribute(string reason) { Reason = reason; }
        public string Reason { get; }
    }
}

namespace Orpheus.Core.Geometry
{
    public sealed class TorusCoordinateAttribute : System.Attribute { }

    [TorusCoordinate]
    public readonly struct WorldPos
    {
        public readonly int X;
        public readonly int Y;
        public WorldPos(int x, int y) { X = x; Y = y; }
    }

    public static class Torus
    {
        public static int DeltaX(int a, int b) { return b - a; }
        public static int InsideTorusSubtraction(int a, int b) { return a - b; }
    }
}

namespace Orpheus.Core.Rng
{
    public struct SplitMix64
    {
        private ulong _state;
        public SplitMix64(ulong seed) { _state = seed; }
        public static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
        public ulong Next() { ulong z = _state += 0x9E3779B97F4A7C15UL; return Mix(z); }
        public int NextInt(int bound) { return (int)(Next() % (ulong)bound); }
        public static ulong Hash(int x, int y, ulong seed)
        {
            return Mix(seed + (ulong)(uint)x * 0x9E3779B97F4A7C15UL + (ulong)(uint)y * 0xBF58476D1CE4E5B9UL);
        }
    }

    public struct DetRandom
    {
        private SplitMix64 _rng;
        public DetRandom(ulong seed) { _rng = new SplitMix64(seed); }
        public static DetRandom ForSystem(ulong worldSeed, int systemId)
        {
            return new DetRandom(SplitMix64.Hash(systemId, 0, worldSeed));
        }
        public int NextInt(int bound) { return _rng.NextInt(bound); }
    }
}

namespace Orpheus.Core.Trig
{
    public readonly struct Angle16
    {
        public readonly ushort Raw;
        public Angle16(ushort raw) { Raw = raw; }
        public static Angle16 Atan2(int y, int x) { return new Angle16((ushort)(y + x)); }
    }

    public static class TrigTable
    {
        public static int Sin(Angle16 a) { return a.Raw; }
        public static Angle16 Atan2(int y, int x) { return Angle16.Atan2(y, x); }
    }
}
";

        private static readonly IReadOnlyList<MetadataReference> References = BuildReferences();

        private static IReadOnlyList<MetadataReference> BuildReferences()
        {
            string tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
            var list = new List<MetadataReference>();
            foreach (string path in tpa.Split(Path.PathSeparator))
            {
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(MetadataReference.CreateFromFile(path));
                }
            }

            return list;
        }

        private static async Task<ImmutableArray<Diagnostic>> DiagnoseAsync(string body)
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(Header + body, new CSharpParseOptions(LanguageVersion.CSharp9));
            var compilation = CSharpCompilation.Create(
                "Orpheus.Sample",
                new[] { tree },
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            string[] errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Id + " " + d.GetMessage())
                .ToArray();
            Assert.True(errors.Length == 0, "Пример не компилируется: " + string.Join(" | ", errors));

            var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new DeterminismAnalyzer()));
            return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        }

        private static int Count(ImmutableArray<Diagnostic> diagnostics, string id) => diagnostics.Count(d => d.Id == id);

        private static void AssertReported(ImmutableArray<Diagnostic> diagnostics, string id, int expected)
        {
            int actual = Count(diagnostics, id);
            string all = string.Join(" | ", diagnostics.Where(d => d.Id == id).Select(d => d.GetMessage()));
            Assert.True(actual == expected, $"{id}: ожидалось {expected}, получено {actual}. {all}");
        }

        private static void AssertNotReported(ImmutableArray<Diagnostic> diagnostics, string id)
        {
            Assert.Equal(0, Count(diagnostics, id));
        }

        // ---------- Каркас молчит: заголовки и «внутри Torus» ----------

        [Fact]
        public async Task D00_Analyzer_HeaderHasNoDiagnostics()
        {
            // В каркасе вычитание координат есть только внутри Torus — ORP001 там разрешён (шаг 2 карточки).
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(string.Empty);
            Assert.Empty(diagnostics);
        }

        // ---------- ORP001: прямое вычитание координат ----------

        [Fact]
        public async Task D00_Analyzer_ORP001_FlagsDirectSubtraction()
        {
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Bad001
{
    public static int Direct(WorldPos a, WorldPos b) { return a.X - b.X; }
    public static int Fields(WorldPos a, WorldPos b) { return (a.X - b.X) + (a.Y - b.Y); }
    public static int Whole(int ax, int ay, int bx, int by) { return (ax - bx) + (ay - by); }
}
");
            AssertReported(diagnostics, "ORP001", 3);
            Assert.Equal(DiagnosticSeverity.Error, diagnostics.First(d => d.Id == "ORP001").Descriptor.DefaultSeverity);
            Assert.Contains("Torus.Delta", diagnostics.First(d => d.Id == "ORP001").GetMessage());
        }

        [Fact]
        public async Task D00_Analyzer_ORP001_SilentForTorusDelta()
        {
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Good001
{
    public static int ViaTorus(WorldPos a, WorldPos b) { return Torus.DeltaX(a.X, b.X); }
    public static int Sum(WorldPos a, WorldPos b) { return a.X + b.X; }
    public static int Plain(int a, int b) { return a - b; }
}
");
            AssertNotReported(diagnostics, "ORP001");
            Assert.Empty(diagnostics);
        }

        [Fact]
        public async Task D00_Analyzer_ORP001_CoordinateCopiedIntoIntIsNotVisible()
        {
            // Граница правила: анализатор видит типы координат, а не значения. Скопированная в
            // обычное int координата для него исчезает — это покрытие ревью и Torus.Delta, а не анализатора.
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Copied
{
    public static int DeltaCopy(WorldPos a, WorldPos b) { int x = a.X; x -= b.X; return x; }
    public static int Whole(WorldPos a, WorldPos b) { int ax = a.X; int bx = b.X; return ax - bx; }
}
");
            AssertNotReported(diagnostics, "ORP001");
            Assert.Empty(diagnostics);
        }

        // ---------- ORP002: тригонометрия и степени из библиотеки ОС ----------

        [Fact]
        public async Task D00_Analyzer_ORP002_FlagsOsMath()
        {
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Bad002
{
    public static System.Func<double, double> Reference = Math.Sin;
    public static double Sin(double x) { return Math.Sin(x); }
    public static double Cos(double x) { return Math.Cos(x); }
    public static double Tan(double x) { return Math.Tan(x); }
    public static double Atan(double x) { return Math.Atan(x); }
    public static double Atan2(double y, double x) { return Math.Atan2(y, x); }
    public static double Pow(double a, double b) { return Math.Pow(a, b); }
    public static double Exp(double x) { return Math.Exp(x); }
    public static double Log(double x) { return Math.Log(x); }
    public static double Log10(double x) { return Math.Log10(x); }
    public static double Cbrt(double x) { return Math.Cbrt(x); }
    public static float PowF(float x) { return MathF.Pow(x, 3f); }
    public static float SinF(float x) { return MathF.Sin(x); }
}
");
            AssertReported(diagnostics, "ORP002", 13);
            Assert.Equal(DiagnosticSeverity.Error, diagnostics.First(d => d.Id == "ORP002").Descriptor.DefaultSeverity);
            Assert.Contains("TrigTable", diagnostics.First(d => d.Id == "ORP002").GetMessage());
        }

        [Fact]
        public async Task D00_Analyzer_ORP002_SilentForAllowedMathAndTrigTable()
        {
            // Сложение, умножение, деление и Math.Sqrt/Math.Round разрешены (docs/07, 11.5).
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Good002
{
    public static double Sqrt(double x) { return Math.Sqrt(x); }
    public static double Round(double x) { return Math.Round(x); }
    public static double Floor(double x) { return Math.Floor(x); }
    public static int Abs(int x) { return Math.Abs(x); }
    public static int Max(int a, int b) { return Math.Max(a, b); }
    public static float SqrtF(float x) { return MathF.Sqrt(x); }
    public static float RoundF(float x) { return MathF.Round(x); }
    public static int Aim(int dy, int dx) { return TrigTable.Sin(Angle16.Atan2(dy, dx)); }
}
");
            AssertNotReported(diagnostics, "ORP002");
            Assert.Empty(diagnostics);
        }

        // ---------- ORP003: недетерминированный хэш ----------

        [Fact]
        public async Task D00_Analyzer_ORP003_FlagsGetHashCodeAndCombine()
        {
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Bad003
{
    public static int FromString(string s) { return s.GetHashCode(); }
    public static int FromObject(object o) { return o.GetHashCode(); }
    public static int Combine(int a, int b) { return HashCode.Combine(a, b); }
}
");
            AssertReported(diagnostics, "ORP003", 3);
            Assert.Contains("SplitMix64", diagnostics.First(d => d.Id == "ORP003").GetMessage());
        }

        [Fact]
        public async Task D00_Analyzer_ORP003_SilentForSplitMix64AndOwnOverride()
        {
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public class Good003
{
    public static ulong TileHash(int x, int y, ulong seed) { return SplitMix64.Hash(x, y, seed); }
    public static ulong Mixed(ulong v) { return SplitMix64.Mix(v); }
    public static int FieldMath(int x, int y) { return unchecked(x * 73856093 ^ y * 19349663); }
    public override int GetHashCode() { return 17; }
}
");
            AssertNotReported(diagnostics, "ORP003");
            Assert.Empty(diagnostics);
        }

        // ---------- ORP004: System.Random ----------

        [Fact]
        public async Task D00_Analyzer_ORP004_FlagsSystemRandom()
        {
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Bad004
{
    public static int NoSeed() { return new System.Random().Next(); }
    public static int WithSeed() { return new System.Random(42).Next(); }
    public static int Shared() { return System.Random.Shared.Next(); }
    public static int Derived() { return new MyRandom(1).Next(); }
}

public class MyRandom : System.Random
{
    public MyRandom(int seed) : base(seed) { }
}
");
            // 2 на каждый способ обращения: создание объекта и вызов члена (Next/Shared) — 4 × 2 + 1 на base(seed).
            AssertReported(diagnostics, "ORP004", 9);
            Assert.Contains("DetRandom", diagnostics.First(d => d.Id == "ORP004").GetMessage());
        }

        [Fact]
        public async Task D00_Analyzer_ORP004_SilentForDetRandom()
        {
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Good004
{
    public static int Roll(ulong worldSeed, int system) { return DetRandom.ForSystem(worldSeed, system).NextInt(100); }
    public static int TileVariant(ulong worldSeed, int x, int y) { return (int)(SplitMix64.Hash(x, y, worldSeed) % 100UL); }
}
");
            AssertNotReported(diagnostics, "ORP004");
            Assert.Empty(diagnostics);
        }

        // ---------- ORP005: настенные часы ----------

        [Fact]
        public async Task D00_Analyzer_ORP005_FlagsClocks()
        {
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Bad005
{
    public static System.DateTime Now() { return System.DateTime.Now; }
    public static System.DateTime Utc() { return System.DateTime.UtcNow; }
    public static System.DateTime Today() { return System.DateTime.Today; }
    public static System.DateTimeOffset Dto() { return System.DateTimeOffset.Now; }
    public static int Ticks() { return System.Environment.TickCount; }
    public static long Ticks64() { return System.Environment.TickCount64; }
    public static long Watch() { return System.Diagnostics.Stopwatch.StartNew().ElapsedMilliseconds; }
}
");
            AssertReported(diagnostics, "ORP005", 8);
            Assert.Contains("номер такта", diagnostics.First(d => d.Id == "ORP005").GetMessage());
        }

        [Fact]
        public async Task D00_Analyzer_ORP005_SilentForTickCountAndAllow()
        {
            // Такт симуляции — счётчик; часы допустимы в логе вне симуляции с указанием причины (ORP900).
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Good005
{
    public static long Distance(long tick, int unitsPerTick) { return tick * unitsPerTick; }

    [AllowNondeterministic(""лог производительности вне симуляции"")]
    public static void Log(string message) { System.Console.WriteLine(System.DateTime.Now + "" "" + message); }
}
");
            AssertNotReported(diagnostics, "ORP005");
            AssertNotReported(diagnostics, "ORP007");
            AssertReported(diagnostics, "ORP900", 1);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostics.First(d => d.Id == "ORP900").Descriptor.DefaultSeverity);
        }

        // ---------- ORP006: обход неупорядоченных коллекций ----------

        [Fact]
        public async Task D00_Analyzer_ORP006_FlagsUnorderedForeach()
        {
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Bad006
{
    public static int Keys() { int n = 0; foreach (var k in new System.Collections.Generic.Dictionary<int, int>().Keys) { n += k; } return n; }
    public static int Values() { int n = 0; foreach (var v in new System.Collections.Generic.Dictionary<int, int>().Values) { n += v; } return n; }
    public static int Pairs() { int n = 0; foreach (var p in new System.Collections.Generic.Dictionary<int, int>()) { n += p.Key; } return n; }
    public static int Set() { int n = 0; foreach (var v in new System.Collections.Generic.HashSet<int>()) { n += v; } return n; }
    public static int Concurrent() { int n = 0; foreach (var p in new System.Collections.Concurrent.ConcurrentDictionary<int, int>()) { n += p.Key; } return n; }
    public static int Table() { int n = 0; foreach (var e in new System.Collections.Hashtable()) { n++; } return n; }
}
");
            AssertReported(diagnostics, "ORP006", 6);
            Assert.Contains("порядок обхода", diagnostics.First(d => d.Id == "ORP006").GetMessage());
        }

        [Fact]
        public async Task D00_Analyzer_ORP006_SilentForOrderedCollections()
        {
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Good006
{
    public static int List() { int n = 0; var list = new System.Collections.Generic.List<int> { 1, 2, 3 }; foreach (var v in list) { n += v; } return n; }
    public static int Array() { int n = 0; int[] a = { 1, 2, 3 }; foreach (var v in a) { n += v; } return n; }
    public static int Sorted() { int n = 0; var d = new System.Collections.Generic.SortedDictionary<int, int>(); d[2] = 1; d[1] = 2; foreach (var p in d) { n += p.Key; } return n; }
    public static int Span() { int n = 0; var cells = new System.Collections.Generic.Queue<int>(); cells.Enqueue(1); foreach (var v in cells) { n += v; } return n; }
}
");
            AssertNotReported(diagnostics, "ORP006");
            Assert.Empty(diagnostics);
        }

        // ---------- ORP007 и ORP900: обход правил ----------

        [Fact]
        public async Task D00_Analyzer_ORP007_FlagsAllowWithoutReason()
        {
            // Причина обязательна: без неё метод не получает поблажки — часы в нём по-прежнему ORP005.
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Bad007
{
    [AllowNondeterministic("""")]
    public static void EmptyReason() { System.Console.WriteLine(System.DateTime.Now); }

    [AllowNondeterministic(""   "")]
    public static void BlankReason() { System.Console.WriteLine(System.DateTime.Now); }
}
");
            AssertReported(diagnostics, "ORP007", 2);
            AssertReported(diagnostics, "ORP005", 2);
            AssertNotReported(diagnostics, "ORP900");
            Assert.Equal(DiagnosticSeverity.Error, diagnostics.First(d => d.Id == "ORP007").Descriptor.DefaultSeverity);
        }

        [Fact]
        public async Task D00_Analyzer_ORP900_ReportsAllowedPlaces()
        {
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Good900
{
    [AllowNondeterministic(""метрика за пределами симуляции"")]
    public static long Metric() { return System.Diagnostics.Stopwatch.StartNew().ElapsedMilliseconds; }

    [AllowNondeterministic(""часы только в отладочном логе"")]
    public static void Log() { System.Console.WriteLine(System.DateTime.UtcNow); }
}
");
            AssertReported(diagnostics, "ORP900", 2);
            AssertNotReported(diagnostics, "ORP005");
            AssertNotReported(diagnostics, "ORP007");

            // Порядок диагностики не детерминирован (анализатор работает параллельно), поэтому
            // проверяют наличие каждой причины в списке, а не «первое сообщение».
            string all = string.Join(" | ", diagnostics.Where(d => d.Id == "ORP900").Select(d => d.GetMessage()));
            Assert.Contains("метрика за пределами симуляции", all);
            Assert.Contains("часы только в отладочном логе", all);
            Assert.Contains("Good900.Metric", all);
            Assert.Contains("Good900.Log", all);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostics.First(d => d.Id == "ORP900").Descriptor.DefaultSeverity);
        }

        [Fact]
        public async Task D00_Analyzer_OrdinaryCodeIsSilent()
        {
            // Обычный детерминированный код ядра не должен давать ни одной диагностики.
            ImmutableArray<Diagnostic> diagnostics = await DiagnoseAsync(@"
public static class Ordinary
{
    public static int Move(WorldPos from, int unitsPerTick, long tick)
    {
        int dx = unitsPerTick * (int)(tick % 2);
        return from.X + dx;
    }

    public static int Area(int widthTiles, int heightTiles) { return widthTiles * heightTiles; }
}
");
            Assert.Empty(diagnostics);
        }
    }
}
