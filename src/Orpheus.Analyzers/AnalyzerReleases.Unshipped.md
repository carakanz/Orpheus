; Правила, ещё не вошедшие в выпуск. Формат — https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ORP001 | Orpheus.Determinism | Error | прямое вычитание координат (docs/07, 3.2)
ORP002 | Orpheus.Determinism | Error | тригонометрия и степени ОС (docs/07, 11.5)
ORP003 | Orpheus.Determinism | Error | GetHashCode, HashCode
ORP004 | Orpheus.Determinism | Error | System.Random
ORP005 | Orpheus.Determinism | Error | настенные часы
ORP006 | Orpheus.Determinism | Error | foreach по Dictionary/HashSet
ORP007 | Orpheus.Determinism | Error | [AllowNondeterministic] без причины
ORP900 | Orpheus.Determinism | Warning | сводка обходов [AllowNondeterministic]
