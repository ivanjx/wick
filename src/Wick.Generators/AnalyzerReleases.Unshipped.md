; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
WK0001 | Wick | Error | Reactive state type and its containing types must be partial
WK0002 | Wick | Warning | Auto-property of a reactive state type is not reactive
WK0003 | Wick | Error | Partial property cannot be reactive
WK0004 | Wick | Error | Type cannot be a reactive state type
WK0005 | Wick | Error | Reactive state requires C# 14
WK0006 | Wick | Warning | Reactive property holds a mutable collection
WK0007 | Wick | Warning | Reactive component declares its own disposal
WK0008 | Wick | Warning | Writing to an element of a reactive array property is not tracked
WK0009 | Wick | Error | Invalid [ReactiveEquality]
WK0010 | Wick | Warning | [ReactiveEquality] has no effect
