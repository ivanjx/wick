using System.Runtime.ExceptionServices;

namespace Wick.Core;

/// <summary>
/// A cached value computed from reactive state, created with <see cref="Reactive.Derived{T}"/>. Reading
/// <see cref="Value"/> is tracked like a reactive property. The computation re-runs only after something it read
/// changed, and readers are invalidated only when the new result differs:
/// <code>
/// private readonly Derived&lt;bool&gt; _isExpensive = Reactive.Derived(() => State.Price > 100);
/// </code>
/// A component reading <c>_isExpensive.Value</c> re-renders when the comparison flips, not on every price change.
/// </summary>
/// <remarks>
/// While observed, the value is recomputed synchronously on the thread that changed a dependency (at the end of
/// its batch); while unobserved, it is only marked stale and recomputed on the next read. Exceptions thrown by the
/// computation are cached and rethrown by <see cref="Value"/>. The computation must not write reactive state.
/// Dispose to release the subscriptions early; an unreferenced instance is released after garbage collection.
/// </remarks>
public sealed class Derived<T> : IDisposable, IReactiveObserverOwner
{
    private readonly Lock _gate = new();
    private readonly Func<T> _compute;
    private readonly IEqualityComparer<T> _comparer;
    private readonly ReactiveNode _node = new("Derived<T>");
    private readonly SourceObserver _observer;
    private T _value = default!;
    private ExceptionDispatchInfo? _error;
    private bool _hasValue;
    private bool _stale = true;
    private bool _computing;
    private bool _disposed;

    // Incremented whenever a recomputation changes the result. A change notification compares the version at the
    // time the value went stale with the version after refreshing, so a recomputation done lazily by a reader in
    // between still notifies the other readers, while the first computation never does.
    private int _version;
    private int _versionWhenStale;
    private bool _refreshPending;

    internal Derived(Func<T> compute, IEqualityComparer<T>? comparer)
    {
        _compute = compute;
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _observer = new SourceObserver(this);
    }

    internal ReactiveNode Node => _node;

    ReactiveObserver IReactiveObserverOwner.Observer => _observer;

    /// <summary>The current value; recomputed first if a dependency changed since the last computation.</summary>
    public T Value
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_computing)
                {
                    throw new InvalidOperationException("A derived value cannot read itself while it is being computed.");
                }

                // Subscribe before reading, like a property: a refresh that completes after this point notifies us.
                _node.Track();

                if (_stale)
                {
                    Recompute();
                }

                _error?.Throw();

                return _value;
            }
        }
    }

    /// <summary>
    /// Releases the subscriptions to the state the computation read; reading <see cref="Value"/> afterwards throws
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _observer.Dispose();
    }

    // Called with _gate held.
    private void Recompute()
    {
        T value = default!;
        ExceptionDispatchInfo? error = null;

        _computing = true;

        try
        {
            using (_observer.BeginTracking())
            {
                value = _compute();
            }
        }
        catch (Exception exception)
        {
            error = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            _computing = false;
        }

        var changed = error is not null || _error is not null || !_hasValue || !_comparer.Equals(_value, value);

        _stale = false;
        _value = value;
        _error = error;
        _hasValue = error is null;

        if (changed)
        {
            _version++;
        }
    }

    private void MarkStale()
    {
        lock (_gate)
        {
            _stale = true;

            if (!_refreshPending)
            {
                _refreshPending = true;
                _versionWhenStale = _version;
            }
        }
    }

    // ReactiveObserver.InvalidateAll has already marked this value stale (MarkStale), before any sibling ran.
    private void OnSourcesChanged()
    {
        bool notify;
        lock (_gate)
        {
            // Unobserved: recompute lazily on the next read. A reader subscribing concurrently either is seen
            // here or reads after the stale flag was set, so it recomputes itself.
            if (_disposed || _node.ObserverCount == 0)
            {
                _refreshPending = false;
                return;
            }

            if (_stale)
            {
                Recompute();
            }

            notify = _refreshPending && _version != _versionWhenStale;
            _refreshPending = false;
        }

        // Outside the lock: readers re-entering Value on this thread must not deadlock with other derived values.
        if (notify)
        {
            _node.Notify();
        }
    }

    /// <summary>Holds the derived value weakly so that the subscriptions of a forgotten instance cannot keep it alive.</summary>
    private sealed class SourceObserver(Derived<T> owner) : ReactiveObserver
    {
        private readonly WeakReference<Derived<T>> _owner = new(owner);

        internal override bool IsComputed => true;

        internal override void MarkStale()
        {
            if (_owner.TryGetTarget(out var target))
            {
                target.MarkStale();
            }
        }

        protected override void OnInvalidated()
        {
            if (_owner.TryGetTarget(out var target))
            {
                target.OnSourcesChanged();
                return;
            }

            Dispose();
        }
    }
}
