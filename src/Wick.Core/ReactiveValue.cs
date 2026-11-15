namespace Wick.Core;

/// <summary>
/// A single reactive value that needs no <see cref="ReactiveStateAttribute"/> type: reads of <see cref="Value"/> are
/// tracked and writes notify, like a generated property. Meant for state local to a component, declared in its
/// <c>@code</c> block, where the source generator cannot implement partial properties:
/// <code>
/// private readonly ReactiveValue&lt;int&gt; _count = new(0);
/// </code>
/// A component that rendered <c>_count.Value</c> re-renders when it changes, whichever thread writes it. Passed to a
/// child component, it re-renders only the child that reads it.
/// </summary>
/// <remarks>
/// Writes of a value equal by <see cref="EqualityComparer{T}.Default"/> are ignored. Like generated properties, it
/// guards no more than the field: reading a large struct while another thread writes it can observe a torn value.
/// </remarks>
public sealed class ReactiveValue<T>
{
    private const string DefaultName = "ReactiveValue<T>";

    private readonly string _name;
    private ReactiveNode? _node;
    private T _value;

    /// <param name="value">The initial value.</param>
    /// <param name="name">The name <see cref="WickDiagnostics"/> reports for changes to this value.</param>
    public ReactiveValue(T value, string? name = null)
    {
        _value = value;
        _name = name ?? DefaultName;
    }

    /// <summary>The current value. Reads are tracked; setting a different value notifies its readers.</summary>
    public T Value
    {
        get => ReactiveProperty.Get(ref _value, ref _node, _name);
        set => ReactiveProperty.Set(ref _value, value, ref _node);
    }
}
