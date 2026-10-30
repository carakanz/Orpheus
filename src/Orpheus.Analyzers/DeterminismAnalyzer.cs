using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Orpheus.Analyzers
{
    /// <summary>
    /// Правила повторяемости и тора для ядра (docs/07, 3.2 и 11.5).
    /// ORP001 — прямое вычитание координат; ORP002 — тригонометрия ОС; ORP003 — GetHashCode/HashCode;
    /// ORP004 — System.Random; ORP005 — настенные часы; ORP006 — обход Dictionary/HashSet;
    /// ORP007 — [AllowNondeterministic] без причины; ORP900 — сводка обходов (предупреждение).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DeterminismAnalyzer : DiagnosticAnalyzer
    {
        public const string AllowAttributeName = "Orpheus.Core.AllowNondeterministicAttribute";
        public const string CoordinateAttributeName = "Orpheus.Core.Geometry.TorusCoordinateAttribute";
        public const string TorusTypeName = "Orpheus.Core.Geometry.Torus";

        private const string Category = "Orpheus.Determinism";

        public static readonly DiagnosticDescriptor CoordinateSubtraction = Error(
            "ORP001",
            "Прямое вычитание координат",
            "Вычитание координат «{0}» — у шва тора даст неверный знак и длину. Вместо: Torus.Delta / Torus.DeltaX / Torus.DeltaY (docs/07, 3.2).");

        public static readonly DiagnosticDescriptor OsMath = Error(
            "ORP002",
            "Тригонометрия и степени из библиотеки ОС",
            "{0} берётся из библиотеки C ОС и расходится в последнем знаке. Вместо: Angle16 + TrigTable (синус/косинус), свои целочисленные функции (docs/07, 11.5).");

        public static readonly DiagnosticDescriptor HashCodeUse = Error(
            "ORP003",
            "Недетерминированный хэш",
            "{0} — случайное зерно в каждом процессе. Вместо: SplitMix64.Hash(x, y, зерно) / SplitMix64.Mix (docs/07, 11.5).");

        public static readonly DiagnosticDescriptor RandomUse = Error(
            "ORP004",
            "System.Random",
            "{0}: System.Random без зерна случаен, а с зерном может смениться с версией .NET. Вместо: DetRandom.ForSystem(зерно мира, номер системы) (docs/07, 11.5).");

        public static readonly DiagnosticDescriptor WallClock = Error(
            "ORP005",
            "Настенные часы в симуляции",
            "{0} — настенные часы. Вместо: номер такта (docs/07, 11.5); для логов и метрик вне симуляции — [AllowNondeterministic(\"причина\")].");

        public static readonly DiagnosticDescriptor UnorderedForeach = Error(
            "ORP006",
            "Обход неупорядоченной коллекции",
            "foreach по {0}: порядок обхода не гарантирован. Вместо: список по номеру сущности или массив по индексу тайла (docs/07, 11.5).");

        public static readonly DiagnosticDescriptor AllowWithoutReason = Error(
            "ORP007",
            "Обход правил без причины",
            "[AllowNondeterministic] на «{0}» без причины: укажите, зачем методу недетерминированный код (только логи и метрики вне симуляции).");

        public static readonly DiagnosticDescriptor AllowUsed = new DiagnosticDescriptor(
            "ORP900",
            "Обход правил повторяемости",
            "«{0}» обходит правила ORP: {1}",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Сводка мест с [AllowNondeterministic]: проверяющий смотрит, что это логи или метрики вне симуляции.");

        private static readonly ImmutableHashSet<string> OsMathNames = ImmutableHashSet.Create(
            "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2", "Sinh", "Cosh", "Tanh",
            "Asinh", "Acosh", "Atanh", "SinCos", "Pow", "Exp", "Log", "Log10", "Log2", "Cbrt");

        private static readonly ImmutableHashSet<string> ClockProperties = ImmutableHashSet.Create("Now", "UtcNow", "Today");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
            CoordinateSubtraction, OsMath, HashCodeUse, RandomUse, WallClock, UnorderedForeach, AllowWithoutReason, AllowUsed);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var types = new KnownTypes(start.Compilation);
                if (types.Allow != null)
                {
                    start.RegisterSymbolAction(c => AnalyzeAllowUsage(c, types), SymbolKind.Method);
                }

                start.RegisterOperationAction(c => AnalyzeSubtraction(c, types), OperationKind.Binary, OperationKind.CompoundAssignment);
                start.RegisterOperationAction(c => AnalyzeMemberUse(c, types),
                    OperationKind.Invocation, OperationKind.MethodReference, OperationKind.PropertyReference,
                    OperationKind.FieldReference, OperationKind.ObjectCreation);
                start.RegisterOperationAction(c => AnalyzeForeach(c, types), OperationKind.Loop);
            });
        }

        // ---------- ORP001 ----------

        private static void AnalyzeSubtraction(OperationAnalysisContext c, KnownTypes types)
        {
            IOperation left;
            IOperation right;
            switch (c.Operation)
            {
                case IBinaryOperation b when b.OperatorKind == BinaryOperatorKind.Subtract:
                    left = b.LeftOperand;
                    right = b.RightOperand;
                    break;
                case ICompoundAssignmentOperation a when a.OperatorKind == BinaryOperatorKind.Subtract:
                    left = a.Target;
                    right = a.Value;
                    break;
                default:
                    return;
            }

            if (types.Coordinate == null || !IsCoordinate(left, types) || !IsCoordinate(right, types))
            {
                return;
            }

            if (IsInsideTorus(c.ContainingSymbol, types) || IsAllowed(c.ContainingSymbol, types))
            {
                return;
            }

            c.ReportDiagnostic(Diagnostic.Create(CoordinateSubtraction, c.Operation.Syntax.GetLocation(), c.Operation.Syntax.ToString()));
        }

        private static bool IsCoordinate(IOperation op, KnownTypes types)
        {
            op = StripConversions(op);
            if (IsCoordinateType(op.Type, types))
            {
                return true;
            }

            ISymbol? member = op switch
            {
                IFieldReferenceOperation f when f.Instance != null => f.Field,
                IPropertyReferenceOperation p when p.Instance != null => p.Property,
                _ => null,
            };
            return member != null && IsCoordinateType(member.ContainingType, types);
        }

        private static bool IsCoordinateType(ITypeSymbol? type, KnownTypes types)
        {
            if (type == null)
            {
                return false;
            }

            foreach (AttributeData a in type.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(a.AttributeClass, types.Coordinate))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsInsideTorus(ISymbol? symbol, KnownTypes types)
        {
            for (ISymbol? s = symbol; s != null; s = s.ContainingSymbol)
            {
                if (s is INamedTypeSymbol t && SymbolEqualityComparer.Default.Equals(t, types.Torus))
                {
                    return true;
                }
            }

            return false;
        }

        // ---------- ORP002–ORP005 ----------

        private static void AnalyzeMemberUse(OperationAnalysisContext c, KnownTypes types)
        {
            ISymbol? member = c.Operation switch
            {
                IInvocationOperation i => i.TargetMethod,
                IMethodReferenceOperation m => m.Method,
                IPropertyReferenceOperation p => p.Property,
                IFieldReferenceOperation f => f.Field,
                IObjectCreationOperation o => (ISymbol?)o.Constructor ?? o.Type,
                _ => null,
            };
            if (member == null)
            {
                return;
            }

            DiagnosticDescriptor? rule = Classify(member, types);
            if (rule == null || IsAllowed(c.ContainingSymbol, types))
            {
                return;
            }

            INamedTypeSymbol? owner = member as INamedTypeSymbol ?? member.ContainingType;
            string name = member is IMethodSymbol { MethodKind: MethodKind.Constructor }
                ? "new " + owner?.Name
                : owner?.Name + "." + member.Name;
            c.ReportDiagnostic(Diagnostic.Create(rule, c.Operation.Syntax.GetLocation(), name));
        }

        private static DiagnosticDescriptor? Classify(ISymbol member, KnownTypes types)
        {
            INamedTypeSymbol? owner = member as INamedTypeSymbol ?? member.ContainingType;
            if (owner == null)
            {
                return null;
            }

            if ((Same(owner, types.Math) || Same(owner, types.MathF)) && OsMathNames.Contains(member.Name))
            {
                return OsMath;
            }

            if ((member is IMethodSymbol && member.Name == "GetHashCode") || Same(owner, types.HashCode))
            {
                return HashCodeUse;
            }

            if (InheritsFrom(owner, types.Random))
            {
                return RandomUse;
            }

            if (Same(owner, types.Stopwatch)
                || ((Same(owner, types.DateTime) || Same(owner, types.DateTimeOffset)) && ClockProperties.Contains(member.Name))
                || (Same(owner, types.Environment) && member.Name.StartsWith("TickCount", System.StringComparison.Ordinal)))
            {
                return WallClock;
            }

            return null;
        }

        // ---------- ORP006 ----------

        private static void AnalyzeForeach(OperationAnalysisContext c, KnownTypes types)
        {
            if (!(c.Operation is IForEachLoopOperation loop))
            {
                return;
            }

            IOperation collection = StripConversions(loop.Collection);
            string? what = UnorderedCollection(collection.Type, types);
            if (what == null && collection is IPropertyReferenceOperation p
                && (p.Property.Name == "Keys" || p.Property.Name == "Values"))
            {
                string? owner = UnorderedCollection(p.Property.ContainingType, types);
                what = owner == null ? null : owner + "." + p.Property.Name;
            }

            if (what == null || IsAllowed(c.ContainingSymbol, types))
            {
                return;
            }

            c.ReportDiagnostic(Diagnostic.Create(UnorderedForeach, loop.Collection.Syntax.GetLocation(), what));
        }

        private static string? UnorderedCollection(ITypeSymbol? type, KnownTypes types)
        {
            if (!(type is INamedTypeSymbol named))
            {
                return null;
            }

            INamedTypeSymbol def = named.OriginalDefinition;
            foreach (INamedTypeSymbol? unordered in types.Unordered)
            {
                if (Same(def, unordered))
                {
                    return named.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                }
            }

            return null;
        }

        // ---------- ORP007, ORP900 ----------

        private static void AnalyzeAllowUsage(SymbolAnalysisContext c, KnownTypes types)
        {
            foreach (AttributeData a in c.Symbol.GetAttributes())
            {
                if (!Same(a.AttributeClass, types.Allow))
                {
                    continue;
                }

                Location location = a.ApplicationSyntaxReference?.GetSyntax(c.CancellationToken).GetLocation()
                    ?? c.Symbol.Locations[0];
                string name = c.Symbol.ContainingType?.Name + "." + c.Symbol.Name;
                string? reason = a.ConstructorArguments.Length > 0 ? a.ConstructorArguments[0].Value as string : null;
                if (string.IsNullOrWhiteSpace(reason))
                {
                    c.ReportDiagnostic(Diagnostic.Create(AllowWithoutReason, location, name));
                }
                else
                {
                    c.ReportDiagnostic(Diagnostic.Create(AllowUsed, location, name, reason));
                }
            }
        }

        // ---------- Общее ----------

        private static bool IsAllowed(ISymbol? symbol, KnownTypes types)
        {
            if (types.Allow == null)
            {
                return false;
            }

            for (ISymbol? s = symbol; s != null; s = s.ContainingSymbol)
            {
                if (s is IMethodSymbol)
                {
                    foreach (AttributeData a in s.GetAttributes())
                    {
                        if (Same(a.AttributeClass, types.Allow)
                            && a.ConstructorArguments.Length > 0
                            && !string.IsNullOrWhiteSpace(a.ConstructorArguments[0].Value as string))
                        {
                            return true;
                        }
                    }
                }

                if (s is INamedTypeSymbol)
                {
                    break; // обход ставится на метод, не на тип
                }
            }

            return false;
        }

        private static IOperation StripConversions(IOperation op)
        {
            while (op is IConversionOperation conv)
            {
                op = conv.Operand;
            }

            return op;
        }

        private static bool Same(ISymbol? a, ISymbol? b) => a != null && b != null && SymbolEqualityComparer.Default.Equals(a, b);

        private static bool InheritsFrom(INamedTypeSymbol? type, INamedTypeSymbol? baseType)
        {
            for (INamedTypeSymbol? t = type; t != null; t = t.BaseType)
            {
                if (Same(t.OriginalDefinition, baseType))
                {
                    return true;
                }
            }

            return false;
        }

        private static DiagnosticDescriptor Error(string id, string title, string message) =>
            new DiagnosticDescriptor(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        private sealed class KnownTypes
        {
            public readonly INamedTypeSymbol? Allow;
            public readonly INamedTypeSymbol? Coordinate;
            public readonly INamedTypeSymbol? Torus;
            public readonly INamedTypeSymbol? Math;
            public readonly INamedTypeSymbol? MathF;
            public readonly INamedTypeSymbol? HashCode;
            public readonly INamedTypeSymbol? Random;
            public readonly INamedTypeSymbol? Stopwatch;
            public readonly INamedTypeSymbol? DateTime;
            public readonly INamedTypeSymbol? DateTimeOffset;
            public readonly INamedTypeSymbol? Environment;
            public readonly ImmutableArray<INamedTypeSymbol?> Unordered;

            public KnownTypes(Compilation c)
            {
                Allow = c.GetTypeByMetadataName(AllowAttributeName);
                Coordinate = c.GetTypeByMetadataName(CoordinateAttributeName);
                Torus = c.GetTypeByMetadataName(TorusTypeName);
                Math = c.GetTypeByMetadataName("System.Math");
                MathF = c.GetTypeByMetadataName("System.MathF");
                HashCode = c.GetTypeByMetadataName("System.HashCode");
                Random = c.GetTypeByMetadataName("System.Random");
                Stopwatch = c.GetTypeByMetadataName("System.Diagnostics.Stopwatch");
                DateTime = c.GetTypeByMetadataName("System.DateTime");
                DateTimeOffset = c.GetTypeByMetadataName("System.DateTimeOffset");
                Environment = c.GetTypeByMetadataName("System.Environment");
                Unordered = ImmutableArray.Create(
                    c.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2"),
                    c.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2+KeyCollection"),
                    c.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2+ValueCollection"),
                    c.GetTypeByMetadataName("System.Collections.Generic.HashSet`1"),
                    c.GetTypeByMetadataName("System.Collections.Concurrent.ConcurrentDictionary`2"),
                    c.GetTypeByMetadataName("System.Collections.Hashtable"));
            }
        }
    }
}
