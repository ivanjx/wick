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

    public static readonly DiagnosticDescriptor InvalidEquality = new(
        id: "WK0009",
        title: "Invalid [ReactiveEquality]",
        messageFormat: "[ReactiveEquality] on property '{0}' is invalid: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EqualityHasNoEffect = new(
        id: "WK0010",
        title: "[ReactiveEquality] has no effect",
        messageFormat: "[ReactiveEquality] on property '{0}' has no effect: only the setters of generated partial properties in [ReactiveState] types use it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RedundantPropertyChangedSubscription = new(
        id: "WK0011",
        title: "PropertyChanged subscription only re-renders a reactive component",
        messageFormat: "This PropertyChanged subscription only re-renders '{0}', which already re-renders when the reactive state its rendering reads changes; remove it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Reported when the handler only calls StateHasChanged, directly or through InvokeAsync, optionally behind a condition on the event's PropertyName, and the object is a [ReactiveState] type whose PropertyChanged event Wick generates. State the component hands to non-reactive child components is not read by its own rendering; wrap such content in <ReactiveRegion> before removing the subscription.");

    public static readonly DiagnosticDescriptor ObservablePropertyCanBeReactive = new(
        id: "WK0012",
        title: "CommunityToolkit.Mvvm observable properties can become reactive properties",
        messageFormat: "'{0}' declares [ObservableProperty] members; it can become a [ReactiveState] class with partial properties",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PropertyClaimedByOtherGenerator = new(
        id: "WK0013",
        title: "Partial property is implemented by another source generator",
        messageFormat: "Partial property '{0}' is implemented by another source generator ([{1}]); it is not reactive",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor StateHasChangedInReactiveComponent = new(
        id: "WK0014",
        title: "StateHasChanged in a reactive component",
        messageFormat: "'{0}' re-renders when the reactive state its rendering reads changes; StateHasChanged is only needed for changes to non-reactive state",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: false);

    public static readonly DiagnosticDescriptor InvalidNotifyPropertyChanged = new(
        id: "WK0015",
        title: "Generated properties cannot raise PropertyChanged",
        messageFormat: "[ReactiveState(NotifyPropertyChanged = true)] cannot be used on '{0}': {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MemoNotGenerated = new(
        id: "WK0016",
        title: "Memo helpers are not generated",
        messageFormat: "The Memo helpers are not generated for '{0}' because '{1}' already uses the name; rename it to use Memo, or keep using Reactive.Derived",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MemoOutsideGetter = new(
        id: "WK0017",
        title: "Memo is called outside a property getter",
        messageFormat: "Memo is cached per call site and keeps the computation of its first call; call it directly in the getter of a property without parameters, not in {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Each Memo call site owns one cached value for the lifetime of the object. Calls from methods, indexers or lambdas share that value across different arguments and invocations; use Reactive.Derived for those cases.");

    public static readonly DiagnosticDescriptor MemoCapturesLocal = new(
        id: "WK0018",
        title: "Memo computation captures a local variable or parameter",
        messageFormat: "The Memo computation captures '{0}', but only its first value is used; read reactive state inside the computation instead",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
