namespace Wick.Core;

/// <summary>
/// A single trackable unit of reactive state, typically one property of one object.
/// Reads register the node with the observer that is currently tracking on this thread;
/// writes invalidate every subscribed observer.
/// </summary>
public sealed class ReactiveNode
{
    // Smallest per-item node table that is swept, see SweepUnobserved.
    private const int MinSweepAt = 64;

    private readonly Lock _gate = new();
    private readonly HashSet<ReactiveObserver> _observers = [];

    // A copy of _observers handed to notifications; never modified once published, rebuilt after the set changed.
    private ReactiveObserver[]? _snapshot;

    /// <summary>Creates an unnamed node.</summary>
    public ReactiveNode()
    {
    }

    /// <summary>
    /// Creates a node with a diagnostic name such as <c>"CounterState.Count"</c>, reported by <see cref="WickDiagnostics"/>.
    /// Pass a constant or otherwise preformatted string: the name is stored as is.
    /// </summary>
    public ReactiveNode(string? name)
    {
        Name = name;
    }

    /// <summary>The diagnostic name given at creation, if any.</summary>
    public string? Name { get; }

    internal int ObserverCount
    {
        get
        {
            lock (_gate)
            {
                return _observers.Count;
            }
        }
    }

    /// <summary>
    /// Records a read of this node. Must be called before the underlying value is read so that
    /// a concurrent write cannot slip between the read and the subscription.
    /// </summary>
    public void Track() => ReactiveObserver.Current?.RecordDependency(this);

    /// <summary>
    /// Signals that the underlying value changed. Deferred until the outermost batch completes
    /// when called inside <see cref="Reactive.Batch(Action)"/>.
    /// </summary>
    public void Notify()
    {
        ReactiveObserver.ThrowIfTracking();

        if (ReactiveBatch.TryDefer(this))
        {
            return;
        }

        ReactiveObserver[] snapshot;
        lock (_gate)
        {
            if (_observers.Count == 0)
            {
                return;
            }

            snapshot = _snapshot ??= [.. _observers];
        }

        if (WickDiagnostics.Enabled)
        {
            RecordAsCause(snapshot);
        }

        ReactiveObserver.InvalidateAll(snapshot);
    }

    /// <inheritdoc />
    public override string ToString() => Name ?? nameof(ReactiveNode);

    internal void CollectObservers(HashSet<ReactiveObserver> target)
    {
        lock (_gate)
        {
            // Batched notifications are delivered through here, so a deferred change is attributed like a direct one.
            var recordCause = WickDiagnostics.Enabled;

            // A plain loop: UnionWith takes an IEnumerable and would box the set's enumerator.
            foreach (var observer in _observers)
            {
                target.Add(observer);

                if (recordCause)
                {
                    observer.RecordCause(this);
                }
            }
        }
    }

    private void RecordAsCause(ReadOnlySpan<ReactiveObserver> observers)
    {
        foreach (var observer in observers)
        {
            observer.RecordCause(this);
        }
    }

    internal void Subscribe(ReactiveObserver observer)
    {
        lock (_gate)
        {
            if (_observers.Add(observer))
            {
                _snapshot = null;
            }
        }
    }

    internal void Unsubscribe(ReactiveObserver observer)
    {
        lock (_gate)
        {
            if (_observers.Remove(observer))
            {
                _snapshot = null;
            }
        }
    }

    /// <summary>
    /// Drops the nodes nobody observes from a collection's per-item table once it has doubled since the last sweep,
    /// so items read once, or removed since, are not kept alive and the table stays proportional to what is read.
    /// </summary>
    /// <remarks>
    /// Call under the collection's lock, which must also cover the subscription of every node it hands out: otherwise
    /// a sweep could drop a node before its reader subscribed, and later writes would notify a new node instead.
    /// </remarks>
    internal static void SweepUnobserved<TKey>(Dictionary<TKey, ReactiveNode> nodes, ref int sweepAt)
        where TKey : notnull
    {
        if (nodes.Count < sweepAt)
        {
            return;
        }

        foreach (var (key, node) in nodes)
        {
            if (node.ObserverCount == 0)
            {
                nodes.Remove(key);
            }
        }

        sweepAt = Math.Max(MinSweepAt, nodes.Count * 2);
    }
}
