namespace Wick.Core;

/// <summary>
/// Something that depends on reactive state (a component render, later derived state or effects).
/// Dependencies are collected while a <see cref="TrackingScope"/> is active on the current thread
/// and reconciled when the scope ends: nodes that were not read again are unsubscribed.
/// </summary>
/// <remarks>
/// The tracking context is thread-static on purpose. Tracking covers only synchronous reads made
/// between <see cref="BeginTracking"/> and the scope's disposal; it must not flow into async
/// continuations or work started on other threads, which <see cref="AsyncLocal{T}"/> would do.
/// </remarks>
public abstract class ReactiveObserver : IDisposable
{
    [ThreadStatic]
    private static ReactiveObserver? t_current;

    private readonly Lock _gate = new();
    private HashSet<ReactiveNode> _dependencies = [];
    private HashSet<ReactiveNode> _collecting = [];
    private bool _isTracking;
    private volatile bool _disposed;

    /// <summary>The observer collecting dependencies on the current thread, if any.</summary>
    internal static ReactiveObserver? Current => t_current;

    /// <summary>True while dependencies are being collected on the current thread.</summary>
    public static bool IsTracking => t_current is not null;

    internal int DependencyCount
    {
        get
        {
            lock (_gate)
            {
                return _dependencies.Count;
            }
        }
    }

    protected bool IsDisposed => _disposed;

    /// <summary>
    /// Starts collecting dependencies on the current thread until the returned scope is disposed.
    /// </summary>
    public TrackingScope BeginTracking()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_isTracking)
            {
                throw new InvalidOperationException("The observer is already tracking dependencies.");
            }

            _isTracking = true;
            _collecting.Clear();
        }

        var previous = t_current;
        t_current = this;

        return new TrackingScope(this, previous);
    }

    internal void RecordDependency(ReactiveNode node)
    {
        lock (_gate)
        {
            if (!_collecting.Add(node) || _dependencies.Contains(node))
            {
                return;
            }
        }

        // Subscribe at read time rather than at the end of tracking: a write that happens after
        // this read but before tracking ends must still invalidate the observer.
        node.Subscribe(this);
    }

    internal void EndTracking(ReactiveObserver? previous)
    {
        t_current = previous;

        List<ReactiveNode>? stale = null;
        lock (_gate)
        {
            if (!_isTracking)
            {
                return;
            }

            _isTracking = false;

            foreach (var node in _dependencies)
            {
                if (!_collecting.Contains(node))
                {
                    (stale ??= []).Add(node);
                }
            }

            (_dependencies, _collecting) = (_collecting, _dependencies);
            _collecting.Clear();
        }

        if (stale is null)
        {
            return;
        }

        foreach (var node in stale)
        {
            node.Unsubscribe(this);
        }
    }

    /// <summary>
    /// True for derived values: they are re-evaluated synchronously during a notification and are
    /// therefore invalidated before observers that schedule work (components, effects).
    /// </summary>
    internal virtual bool IsComputed => false;

    /// <summary>True when the tracked code may write reactive state (effects); renders and derived values may not.</summary>
    internal virtual bool AllowsMutation => false;

    /// <summary>Marks a computed observer out of date without re-evaluating it.</summary>
    internal virtual void MarkStale()
    {
    }

    internal void Invalidate()
    {
        if (_disposed)
        {
            return;
        }

        OnInvalidated();
    }

    /// <summary>
    /// Invalidates computed observers before the rest. Every computed observer is marked stale first, so one that
    /// re-evaluates and reads a sibling that depends on the same change recomputes that sibling instead of reading
    /// its old value. Renders are scheduled only after the derived values they may read are current.
    /// </summary>
    internal static void InvalidateAll(ReadOnlySpan<ReactiveObserver> observers)
    {
        var hasComputed = false;
        foreach (var observer in observers)
        {
            if (observer.IsComputed)
            {
                hasComputed = true;
                observer.MarkStale();
            }
        }

        if (hasComputed)
        {
            foreach (var observer in observers)
            {
                if (observer.IsComputed)
                {
                    observer.Invalidate();
                }
            }
        }

        foreach (var observer in observers)
        {
            if (!observer.IsComputed)
            {
                observer.Invalidate();
            }
        }
    }

    internal ReactiveNode[] GetDependencies()
    {
        lock (_gate)
        {
            return [.. _dependencies];
        }
    }

    /// <summary>
    /// Called when any dependency changed. May run on any thread and must not throw or block.
    /// </summary>
    protected abstract void OnInvalidated();

    /// <summary>Releases every subscription held by this observer.</summary>
    public void Dispose()
    {
        ReactiveNode[] dependencies;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dependencies = [.. _dependencies, .. _collecting];
            _dependencies.Clear();
            _collecting.Clear();
        }

        foreach (var node in dependencies)
        {
            node.Unsubscribe(this);
        }

        GC.SuppressFinalize(this);
    }

    internal static void ThrowIfTracking()
    {
        if (t_current is not null && !t_current.AllowsMutation)
        {
            throw new InvalidOperationException(
                "Reactive state cannot be mutated while dependencies are being tracked (for example during component rendering).");
        }
    }
}
