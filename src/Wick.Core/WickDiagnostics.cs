using System.Diagnostics;

namespace Wick.Core;

/// <summary>
/// Opt-in diagnostics that explain why reactive components re-render and what they depend on:
/// <code>
/// WickDiagnostics.Message += message => logger.LogDebug("{Message}", message);
/// WickDiagnostics.Enabled = true;
/// </code>
/// Every reactive re-render is then reported with the state whose change caused it, for example
/// <c>CountDisplay re-render: CounterState.Count changed</c>. Changes that coalesce into one render are listed together.
/// </summary>
/// <remarks>
/// Names come from the state: <see cref="ReactiveStateAttribute"/> properties and <see cref="ReactiveObject"/>
/// properties are named <c>TypeName.PropertyName</c>, collections <c>ReactiveList&lt;T&gt;</c>,
/// <c>ReactiveList&lt;T&gt;.Count</c> or <c>ReactiveDictionary[key]</c>, derived values <c>Derived&lt;T&gt;</c>, and memos
/// <c>TypeName.PropertyName</c> like properties.
/// Per-key collection nodes include the formatted key only when they are created while diagnostics are enabled.
/// While <see cref="Enabled"/> is false, Wick checks the flag and nothing else: no change is recorded or formatted.
/// </remarks>
public static class WickDiagnostics
{
    /// <summary>Turns render reports on or off; off by default. Meant for development, not production.</summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// Receives each report while <see cref="Enabled"/> is set, on the thread that re-renders (the component's
    /// dispatcher). Without a subscriber, reports are written to <see cref="Trace"/> with the category <c>Wick</c>.
    /// </summary>
    public static event Action<string>? Message;

    /// <summary>
    /// Names of the reactive state that <paramref name="source"/> read during its last render or run, whether or not
    /// diagnostics are enabled. Unnamed nodes are listed as <c>ReactiveNode</c>; a disposed source has none.
    /// </summary>
    /// <param name="source">A reactive component, a <see cref="ReactiveEffect"/>, a <see cref="Derived{T}"/> or a <see cref="ReactiveObserver"/>.</param>
    public static IReadOnlyList<string> GetDependencies(object source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var observer = source switch
        {
            ReactiveObserver reactiveObserver => reactiveObserver,
            IReactiveObserverOwner owner => owner.Observer,
            _ => throw new ArgumentException(
                $"'{source.GetType()}' does not track reactive dependencies; pass a reactive component, an effect, a derived value or an observer.",
                nameof(source)),
        };

        return [.. observer.GetDependencies().Select(static node => node.ToString())];
    }

    /// <summary>Reports a reactive re-render of <paramref name="component"/>; <paramref name="changes"/> comes from <see cref="ChangeCauses.Take"/>.</summary>
    internal static void ReportRender(Type component, string? changes)
        => Report($"{DisplayName(component)} re-render: {changes ?? "dependencies"} changed");

    private static void Report(string message)
    {
        var handler = Message;
        if (handler is null)
        {
            Trace.WriteLine(message, "Wick");
            return;
        }

        handler(message);
    }

    private static string DisplayName(Type type)
    {
        var name = type.Name;
        var arity = name.IndexOf('`');

        return arity < 0 ? name : name[..arity];
    }
}
