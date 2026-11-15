using Microsoft.CodeAnalysis;

namespace Wick.Generators;

internal static class Diagnostics
{
    private const string Category = "Wick";

    public static readonly DiagnosticDescriptor TypeMustBePartial = new(
        id: "WK0001",
        title: "Reactive state type and its containing types must be partial",
        messageFormat: "Type '{0}' must be declared 'partial' to use [ReactiveState]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AutoPropertyIsNotReactive = new(
        id: "WK0002",
        title: "Auto-property of a reactive state type is not reactive",
        messageFormat: "Property '{0}' is not reactive; declare it 'partial' so that reads are tracked and writes notify",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedProperty = new(
        id: "WK0003",
        title: "Partial property cannot be reactive",
        messageFormat: "Partial property '{0}' cannot be reactive: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedType = new(
        id: "WK0004",
        title: "Type cannot be a reactive state type",
        messageFormat: "Type '{0}' cannot be a [ReactiveState] type: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RequiresCSharp14 = new(
        id: "WK0005",
        title: "Reactive state requires C# 14",
        messageFormat: "[ReactiveState] requires C# 14 or later; the generated accessors use the 'field' keyword",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MutableCollectionProperty = new(
        id: "WK0006",
        title: "Reactive property holds a mutable collection",
        messageFormat: "Reactive property '{0}' has the mutable collection type '{1}'; changes made in place (Add, Remove, indexer) are not tracked. Use ReactiveList, ReactiveDictionary or ReactiveSet, an immutable collection that is replaced on change, or call Reactive.Invalidate after mutating it.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DisposalBypassesReactiveCleanup = new(
        id: "WK0007",
        title: "Reactive component declares its own disposal",
        messageFormat: "'{0}' in reactive component '{1}' replaces or hides the base disposal: Blazor either never calls it or calls it instead of the cleanup that releases reactive subscriptions. Override 'Dispose(bool)' or 'DisposeAsyncCore()' instead.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UntrackedArrayElementWrite = new(
        id: "WK0008",
        title: "Writing to an element of a reactive array property is not tracked",
        messageFormat: "Writing to an element of reactive property '{0}' is not tracked; assign a new array, use ReactiveList<T>, or call Reactive.Invalidate",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
