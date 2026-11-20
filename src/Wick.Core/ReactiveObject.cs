using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Wick.Core;

/// <summary>
/// Runtime (non-generated) base class for reactive state. Properties opt in explicitly:
/// <code>
/// public int Count { get => Get(ref field); set => Set(ref field, value); }
/// </code>
/// Nodes are created lazily, only for properties that were read while tracking. Prefer
/// <see cref="ReactiveStateAttribute"/> with partial properties: generated accessors avoid the
/// per-access name lookup and lock.
/// </summary>
public abstract class ReactiveObject : IReactiveState
{
    private readonly Lock _nodesGate = new();
    private Dictionary<string, ReactiveNode>? _nodes;
    private MemoCache? _memos;

    /// <summary>Records the read of <paramref name="property"/> while tracking, then returns <paramref name="field"/>.</summary>
    /// <param name="field">The property's backing field.</param>
    /// <param name="property">The property name; supplied by the compiler.</param>
    protected T Get<T>(ref T field, [CallerMemberName] string property = "")
    {
        var observer = ReactiveObserver.Current;
        if (observer is not null)
        {
            observer.RecordDependency(GetOrCreateNode(property));
        }

        return field;
    }

    /// <summary>
    /// Writes <paramref name="value"/> and notifies the readers of <paramref name="property"/> when it differs from the
    /// current value by <see cref="EqualityComparer{T}.Default"/>.
    /// </summary>
    /// <param name="field">The property's backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="property">The property name; supplied by the compiler.</param>
    /// <returns>Whether the value changed.</returns>
    /// <exception cref="InvalidOperationException">Called during a component render or a derived computation.</exception>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string property = "")
        => !EqualityComparer<T>.Default.Equals(field, value) && Write(ref field, value, property);

    /// <summary>
    /// Like <see cref="Set{T}(ref T, T, string)"/>, but a write notifies only when it differs by
    /// <paramref name="comparer"/>, for example <see cref="ReferenceEqualityComparer.Instance"/> for a large object.
    /// </summary>
    protected bool Set<T>(ref T field, T value, IEqualityComparer<T> comparer, [CallerMemberName] string property = "")
        => !comparer.Equals(field, value) && Write(ref field, value, property);

    private bool Write<T>(ref T field, T value, string property)
    {
        ReactiveObserver.ThrowIfTracking();

        field = value;
        FindNode(property)?.Notify();

        return true;
    }

    /// <summary>
    /// Caches a value computed from reactive state, like <see cref="Reactive.Derived{T}"/> without declaring a field:
    /// <code>
    /// public decimal Total => Memo(() => Price * Quantity);
    /// </code>
    /// The first read creates one <see cref="Derived{T}"/> per instance and call site; later reads return its value
    /// and ignore <paramref name="compute"/>, so it should read only reactive state.
    /// </summary>
    /// <remarks>
    /// Each call allocates the lambda when it captures <c>this</c>; the
    /// <see cref="Memo{TState, TValue}(Func{TState, TValue}, string, string, int, string)"/> form with a static lambda
    /// does not. Call it directly in a property getter; the call site identifies the memo.
    /// </remarks>
    /// <param name="compute">The computation, used on the first read only.</param>
    /// <param name="callerMember">Supplied by the compiler.</param>
    /// <param name="callerFile">Supplied by the compiler.</param>
    /// <param name="callerLine">Supplied by the compiler.</param>
    /// <param name="callerExpression">Supplied by the compiler.</param>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The optional parameters are caller information; the overloads differ by the delegate's arity.")]
    protected TValue Memo<TValue>(
        Func<TValue> compute,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0,
        [CallerArgumentExpression(nameof(compute))] string callerExpression = "")
        => MemoCache.Get(ref _memos, this, compute, callerMember, callerFile, callerLine, callerExpression);

    /// <summary>
    /// Like <see cref="Memo{TValue}(Func{TValue}, string, string, int, string)"/>, for a computation that receives
    /// this object, so that a static lambda reads without allocating:
    /// <code>
    /// public decimal Total => Memo(static (CartState state) => state.Price * state.Quantity);
    /// </code>
    /// </summary>
    /// <param name="compute">The computation, used on the first read only; its parameter must be this object's type.</param>
    /// <param name="callerMember">Supplied by the compiler.</param>
    /// <param name="callerFile">Supplied by the compiler.</param>
    /// <param name="callerLine">Supplied by the compiler.</param>
    /// <param name="callerExpression">Supplied by the compiler.</param>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The optional parameters are caller information; the overloads differ by the delegate's arity.")]
    protected TValue Memo<TState, TValue>(
        Func<TState, TValue> compute,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0,
        [CallerArgumentExpression(nameof(compute))] string callerExpression = "")
        where TState : ReactiveObject
        => MemoCache.Get(ref _memos, this, compute, callerMember, callerFile, callerLine, callerExpression);

    ReactiveNode? IReactiveState.FindNode(string propertyName) => FindNode(propertyName);

    private ReactiveNode? FindNode(string property)
    {
        lock (_nodesGate)
        {
            return _nodes?.GetValueOrDefault(property);
        }
    }

    private ReactiveNode GetOrCreateNode(string property)
    {
        lock (_nodesGate)
        {
            _nodes ??= [];

            if (!_nodes.TryGetValue(property, out var node))
            {
                node = new ReactiveNode(GetType().Name + "." + property);
                _nodes.Add(property, node);
            }

            return node;
        }
    }
}
