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
WK0011 | Wick | Info | PropertyChanged subscription only re-renders a reactive component
WK0012 | Wick | Info | CommunityToolkit.Mvvm observable properties can become reactive properties
WK0013 | Wick | Warning | Partial property is implemented by another source generator
WK0014 | Wick | Disabled | StateHasChanged in a reactive component
WK0015 | Wick | Error | Generated properties cannot raise PropertyChanged
WK0016 | Wick | Info | Memo helpers are not generated
WK0017 | Wick | Warning | Memo is called outside a property getter
WK0018 | Wick | Warning | Memo computation captures a local variable or parameter
