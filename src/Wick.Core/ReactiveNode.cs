namespace Wick.Core;

/// <summary>
/// A single trackable unit of reactive state, typically one property of one object.
/// Reads register the node with the observer that is currently tracking on this thread;
/// writes invalidate every subscribed observer.
/// </summary>
public sealed class ReactiveNode
{
    private readonly Lock _gate = new();
    private readonly HashSet<ReactiveObserver> _observers = [];

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

            snapshot = [.. _observers];
        }

        ReactiveObserver.InvalidateAll(snapshot);
    }

    internal void CollectObservers(HashSet<ReactiveObserver> target)
    {
        lock (_gate)
        {
            target.UnionWith(_observers);
        }
    }

    internal void Subscribe(ReactiveObserver observer)
    {
        lock (_gate)
        {
            _observers.Add(observer);
        }
    }

    internal void Unsubscribe(ReactiveObserver observer)
    {
        lock (_gate)
        {
            _observers.Remove(observer);
        }
    }
}
