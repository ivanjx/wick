namespace Wick.Core;

/// <summary>
/// The changes made to a reactive collection, in order: <see cref="ReactiveList{T}.Changes"/>,
/// <see cref="ReactiveSet{T}.Changes"/> or <see cref="ReactiveDictionary{TKey, TValue}.Changes"/>. Pass it to
/// <see cref="Reactive.Watch{TChange}(CollectionChanges{TChange}, Action{IReadOnlyList{TChange}}, EffectOptions?)"/>,
/// or to a component's <c>Watch</c>, to receive them.
/// </summary>
/// <remarks>
/// Changes are recorded only while watched, separately for each watch; without a watch, writes create none. A
/// change made inside a batch is held back until the batch ends, and so are the changes recorded after it, so every
/// watch sees the collection's changes in the order they happened.
/// </remarks>
public sealed class CollectionChanges<TChange>
{
    // The collection's lock: changes are recorded in the order the collection applies them.
    private readonly Lock _gate;
    private readonly ReactiveNode _node;
    private Subscription[] _subscriptions = [];

    internal CollectionChanges(Lock gate, string name)
    {
        _gate = gate;
        _node = new ReactiveNode(name);
    }

    /// <summary>Whether a watch exists; read under the collection's lock before recording.</summary>
    internal bool IsWatched => Volatile.Read(ref _subscriptions).Length > 0;

    /// <summary>Records <paramref name="change"/> for every watch; call under the collection's lock, as it is applied.</summary>
    internal void Record(TChange change)
    {
        var subscriptions = _subscriptions;
        if (subscriptions.Length == 0)
        {
            return;
        }

        var entry = new Entry(change, ReactiveBatch.CurrentCommit());
        foreach (var subscription in subscriptions)
        {
            subscription.Pending.Add(entry);
        }
    }

    /// <summary>Wakes the watches up after recording; call once the collection's lock is released.</summary>
    internal void Notify()
    {
        if (IsWatched)
        {
            _node.Notify();
        }
    }

    internal ReactiveEffect Watch(Action<IReadOnlyList<TChange>> onChanges, EffectOptions? options)
    {
        var subscription = new Subscription(this);
        lock (_gate)
        {
            _subscriptions = [.. _subscriptions, subscription];
        }

        try
        {
            return SyncReactiveEffect.Start(
                () =>
                {
                    // Subscribed before taking: a change recorded after the take wakes the effect up again.
                    _node.Track();

                    var changes = Take(subscription);
                    if (changes is null)
                    {
                        return;
                    }

                    using (ReactiveObserver.SuspendTracking())
                    {
                        onChanges(changes);
                    }
                },
                options,
                subscription);
        }
        catch
        {
            subscription.Dispose();
            throw;
        }
    }

    /// <summary>Takes the changes that may be delivered: those up to the first one whose batch has not ended.</summary>
    private TChange[]? Take(Subscription subscription)
    {
        lock (_gate)
        {
            var pending = subscription.Pending;

            var count = 0;
            while (count < pending.Count && (pending[count].Commit?.IsCommitted ?? true))
            {
                count++;
            }

            if (count == 0)
            {
                return null;
            }

            var changes = new TChange[count];
            for (var i = 0; i < count; i++)
            {
                changes[i] = pending[i].Change;
            }

            pending.RemoveRange(0, count);

            return changes;
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_subscriptions, subscription);
            if (index < 0)
            {
                return;
            }

            _subscriptions = [.. _subscriptions[..index], .. _subscriptions[(index + 1)..]];
        }
    }

    /// <summary>A recorded change; <see cref="Commit"/> is set when it was made inside a batch.</summary>
    private readonly record struct Entry(TChange Change, BatchCommit? Commit);

    /// <summary>The changes not yet delivered to one watch, guarded by the collection's lock.</summary>
    private sealed class Subscription(CollectionChanges<TChange> owner) : IDisposable
    {
        public List<Entry> Pending { get; } = [];

        // Pending changes stay: a final run with FlushOnDispose still delivers them.
        public void Dispose() => owner.Unsubscribe(this);
    }
}
