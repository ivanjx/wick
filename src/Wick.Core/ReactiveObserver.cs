namespace Wick.Core;

/// <summary>
/// Base class for code that depends on reactive state. Reads made between <see cref="BeginTracking"/> and the
/// disposal of the returned <see cref="TrackingScope"/> become the observer's dependencies, and
/// <see cref="OnInvalidated"/> is called when one of them changes. Each tracking pass replaces the previous
/// dependencies: nodes that were not read again are unsubscribed.
/// </summary>
/// <remarks>
/// Wick's components, effects and derived values are built on this class; prefer them unless you integrate another
/// UI framework. Reactive state cannot be written while an observer of your own is tracking.
/// <para>
/// The tracking context is thread-static on purpose. Tracking covers only synchronous reads made
/// between <see cref="BeginTracking"/> and the scope's disposal; it must not flow into async
/// continuations or work started on other threads, which <see cref="AsyncLocal{T}"/> would do.
/// </para>
/// </remarks>
public abstract class ReactiveObserver : IDisposable
{
    [ThreadStatic]
    private static ReactiveObserver? t_current;

    // The observer hidden by Untracked: reads skip it, but its rule against writes still applies.
    [ThreadStatic]
    private static ReactiveObserver? t_untracked;

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

    /// <summary>Whether <see cref="Dispose"/> was called; a disposed observer is no longer invalidated.</summary>
    protected bool IsDisposed => _disposed;

    /// <summary>
    /// Starts collecting dependencies on the current thread until the returned scope is disposed.
    /// </summary>
    public TrackingScope BeginTracking()
    {
        ObjectDisposedException.ThrowIf(!TryBeginTracking(out var scope), this);

        return scope;
    }

    /// <summary>Like <see cref="BeginTracking"/>, but returns an inactive scope instead of throwing once disposed.</summary>
    internal TrackingScope BeginTrackingUnlessDisposed()
    {
        TryBeginTracking(out var scope);

        return scope;
    }

    private bool TryBeginTracking(out TrackingScope scope)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                scope = default;
                return false;
            }

            if (_isTracking)
            {
                throw new InvalidOperationException("The observer is already tracking dependencies.");
            }

            _isTracking = true;
            _collecting.Clear();
        }

        var previous = t_current;
        t_current = this;

        scope = new TrackingScope(this, previous);
        return true;
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
    /// Stops collecting dependencies on the current thread until the returned scope is disposed. Writes stay forbidden
    /// where the hidden observer forbids them.
    /// </summary>
    internal static UntrackedScope SuspendTracking()
    {
        var current = t_current;
        if (current is null)
        {
            return default;
        }

        var scope = new UntrackedScope(current, t_untracked);
        t_current = null;
        t_untracked = current;

        return scope;
    }

    internal readonly struct UntrackedScope(ReactiveObserver? current, ReactiveObserver? untracked) : IDisposable
    {
        public void Dispose()
        {
            if (current is null)
            {
                return;
            }

            t_current = current;
            t_untracked = untracked;
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

    /// <summary>
    /// Receives a node whose change is about to invalidate this observer, before <see cref="OnInvalidated"/> runs.
    /// Called only while <see cref="WickDiagnostics.Enabled"/> is set; observers that report why they re-ran keep it.
    /// </summary>
    internal virtual void RecordCause(ReactiveNode node)
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
        var observer = t_current ?? t_untracked;
        if (observer is not null && !observer.AllowsMutation)
        {
            throw new InvalidOperationException(
                "Reactive state cannot be mutated while dependencies are being tracked (for example during component rendering).");
        }
    }
}
