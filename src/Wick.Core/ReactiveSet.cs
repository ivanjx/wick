using System.Collections;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Wick.Core;

/// <summary>
/// Thread-safe set whose reads are tracked per item and whose mutations invalidate only the observers that depend on
/// them.
/// <list type="bullet">
/// <item><see cref="Contains"/> depends on that item's membership only, including an item that is missing: adding it
/// later invalidates the reader.</item>
/// <item><see cref="Count"/>, enumeration, <see cref="Snapshot"/> and the set comparisons (<see cref="IsSubsetOf"/>,
/// <see cref="Overlaps"/>, <see cref="SetEquals"/>, ...) depend on all items.</item>
/// </list>
/// Mutations that do not change membership invalidate nothing; bulk mutations invalidate each affected reader once.
/// Enumeration iterates an immutable snapshot, so a render can loop over the set while other threads mutate it.
/// </summary>
/// <remarks>
/// An item that was checked while tracking keeps a small subscription node for the lifetime of the set, even after
/// the item is removed; avoid tracked checks of unbounded sets of distinct missing items. Such a node is named after its
/// item (<c>ReactiveSet[tag]</c>) only when created while <see cref="WickDiagnostics.Enabled"/> is set.
/// Mutations throw <see cref="InvalidOperationException"/> during a component render or a derived computation, like
/// any reactive write.
/// </remarks>
public sealed class ReactiveSet<T> : ISet<T>, IReadOnlySet<T>
    where T : notnull
{
    private const string ItemsNodeName = "ReactiveSet<T>";
    private const string ItemNodeName = "ReactiveSet[item]";
    private const string ChangesNodeName = "ReactiveSet<T>.Changes";

    private readonly Lock _gate = new();
    private readonly HashSet<T> _items;
    private readonly Dictionary<T, ReactiveNode> _itemNodes;
    private T[]? _snapshot;
    private ReactiveNode? _itemsNode;
    private CollectionChanges<SetChange<T>>? _changes;

    /// <summary>Creates an empty set that compares items with <see cref="EqualityComparer{T}.Default"/>.</summary>
    public ReactiveSet()
        : this(comparer: null)
    {
    }

    /// <summary>Creates an empty set that compares items with <paramref name="comparer"/>, or the default comparer when <see langword="null"/>.</summary>
    public ReactiveSet(IEqualityComparer<T>? comparer)
    {
        _items = new HashSet<T>(comparer);
        _itemNodes = new Dictionary<T, ReactiveNode>(comparer);
    }

    /// <summary>Creates a set holding the distinct <paramref name="items"/>, compared with <paramref name="comparer"/> or the default comparer.</summary>
    public ReactiveSet(IEnumerable<T> items, IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        _items = new HashSet<T>(items, comparer);
        _itemNodes = new Dictionary<T, ReactiveNode>(comparer);
    }

    /// <summary>The number of items; tracked like enumeration.</summary>
    public int Count
    {
        get
        {
            ReactiveProperty.Track(ref _itemsNode, ItemsNodeName);

            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    bool ICollection<T>.IsReadOnly => false;

    /// <summary>
    /// The changes made to the set from now on, in order, for
    /// <see cref="Reactive.Watch{TChange}(CollectionChanges{TChange}, Action{IReadOnlyList{TChange}}, EffectOptions?)"/>:
    /// <c>Reactive.Watch(set.Changes, changes =&gt; ...)</c>. Every mutation, including <see cref="Clear"/> and the
    /// bulk ones, reports one change per item it added or removed.
    /// </summary>
    /// <remarks>Reading it is not tracked. Changes are recorded only while watched.</remarks>
    public CollectionChanges<SetChange<T>> Changes
        => Volatile.Read(ref _changes) ?? CreateChanges();

    /// <summary>The change stream while watched, else null; read under the lock, before recording.</summary>
    private CollectionChanges<SetChange<T>>? WatchedChanges
    {
        get
        {
            var changes = _changes;

            return changes is not null && changes.IsWatched ? changes : null;
        }
    }

    /// <summary>The items as an immutable array; tracked like enumeration.</summary>
    public ImmutableArray<T> Snapshot()
    {
        ReactiveProperty.Track(ref _itemsNode, ItemsNodeName);

        return ImmutableCollectionsMarshal.AsImmutableArray(Volatile.Read(ref _snapshot) ?? CreateSnapshot());
    }

    /// <summary>Enumerates a snapshot: mutations made during the loop, on any thread, are not observed by it.</summary>
    public ImmutableArray<T>.Enumerator GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Snapshot()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)Snapshot()).GetEnumerator();

    /// <summary>Whether <paramref name="item"/> is in the set; readers are invalidated only when its membership changes.</summary>
    public bool Contains(T item)
    {
        TrackItem(item);

        lock (_gate)
        {
            return _items.Contains(item);
        }
    }

    /// <summary>Copies the items to <paramref name="array"/> starting at <paramref name="arrayIndex"/>; tracked like enumeration.</summary>
    public void CopyTo(T[] array, int arrayIndex) => Snapshot().CopyTo(array, arrayIndex);

    /// <summary>Whether every item is in <paramref name="other"/>; tracked like enumeration.</summary>
    public bool IsSubsetOf(IEnumerable<T> other) => Compare(other, static (items, others) => items.IsSubsetOf(others));

    /// <summary>Whether every item of <paramref name="other"/> is in the set; tracked like enumeration.</summary>
    public bool IsSupersetOf(IEnumerable<T> other) => Compare(other, static (items, others) => items.IsSupersetOf(others));

    /// <summary>Whether the set is a subset of <paramref name="other"/> and smaller than it; tracked like enumeration.</summary>
    public bool IsProperSubsetOf(IEnumerable<T> other)
        => Compare(other, static (items, others) => items.IsProperSubsetOf(others));

    /// <summary>Whether the set is a superset of <paramref name="other"/> and larger than it; tracked like enumeration.</summary>
    public bool IsProperSupersetOf(IEnumerable<T> other)
        => Compare(other, static (items, others) => items.IsProperSupersetOf(others));

    /// <summary>Whether the set and <paramref name="other"/> share an item; tracked like enumeration.</summary>
    public bool Overlaps(IEnumerable<T> other) => Compare(other, static (items, others) => items.Overlaps(others));

    /// <summary>Whether the set and <paramref name="other"/> hold the same items; tracked like enumeration.</summary>
    public bool SetEquals(IEnumerable<T> other) => Compare(other, static (items, others) => items.SetEquals(others));

    /// <summary>Adds <paramref name="item"/> unless present; returns whether it was added.</summary>
    public bool Add(T item)
    {
        ReactiveObserver.ThrowIfTracking();

        ReactiveNode? itemNode;
        lock (_gate)
        {
            if (!_items.Add(item))
            {
                return false;
            }

            _snapshot = null;
            itemNode = _itemNodes.GetValueOrDefault(item);
            WatchedChanges?.Record(SetChange<T>.Added(item));
        }

        NotifyChange(itemNode);

        return true;
    }

    void ICollection<T>.Add(T item) => Add(item);

    /// <summary>Removes <paramref name="item"/>; returns whether it was present.</summary>
    public bool Remove(T item)
    {
        ReactiveObserver.ThrowIfTracking();

        ReactiveNode? itemNode;
        lock (_gate)
        {
            if (!_items.Remove(item))
            {
                return false;
            }

            _snapshot = null;
            itemNode = _itemNodes.GetValueOrDefault(item);
            WatchedChanges?.Record(SetChange<T>.Removed(item));
        }

        NotifyChange(itemNode);

        return true;
    }

    /// <summary>Removes every item; an empty set notifies nobody.</summary>
    public void Clear()
    {
        ReactiveObserver.ThrowIfTracking();

        List<ReactiveNode> changedNodes = [];
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                return;
            }

            var changes = WatchedChanges;
            foreach (var item in _items)
            {
                CollectNode(item, changedNodes);
                changes?.Record(SetChange<T>.Removed(item));
            }

            _items.Clear();
            _snapshot = null;
        }

        NotifyChange(changedNodes);
    }

    /// <summary>Adds every item of <paramref name="other"/>, invalidating each affected reader once.</summary>
    public void UnionWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ReactiveObserver.ThrowIfTracking();

        var others = ReadableUnderLock(other);
        List<ReactiveNode> changedNodes = [];
        var changed = false;
        lock (_gate)
        {
            var changes = WatchedChanges;
            foreach (var item in others)
            {
                if (_items.Add(item))
                {
                    changed = true;
                    CollectNode(item, changedNodes);
                    changes?.Record(SetChange<T>.Added(item));
                }
            }

            if (!changed)
            {
                return;
            }

            _snapshot = null;
        }

        NotifyChange(changedNodes);
    }

    /// <summary>Removes every item of <paramref name="other"/>, invalidating each affected reader once.</summary>
    public void ExceptWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ReactiveObserver.ThrowIfTracking();

        var others = ReadableUnderLock(other);
        List<ReactiveNode> changedNodes = [];
        var changed = false;
        lock (_gate)
        {
            var changes = WatchedChanges;
            foreach (var item in others)
            {
                if (_items.Remove(item))
                {
                    changed = true;
                    CollectNode(item, changedNodes);
                    changes?.Record(SetChange<T>.Removed(item));
                }
            }

            if (!changed)
            {
                return;
            }

            _snapshot = null;
        }

        NotifyChange(changedNodes);
    }

    /// <summary>Keeps only the items also in <paramref name="other"/>, invalidating each affected reader once.</summary>
    public void IntersectWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ReactiveObserver.ThrowIfTracking();

        // Built outside the lock, like the copies made by the other bulk operations; a set with the same comparer
        // already answers the lookups.
        var kept = other is HashSet<T> set && set.Comparer.Equals(_items.Comparer) ? set : new HashSet<T>(other, _items.Comparer);
        List<ReactiveNode> changedNodes = [];
        lock (_gate)
        {
            var changes = WatchedChanges;
            var removed = _items.RemoveWhere(item =>
            {
                if (kept.Contains(item))
                {
                    return false;
                }

                CollectNode(item, changedNodes);
                changes?.Record(SetChange<T>.Removed(item));

                return true;
            });

            if (removed == 0)
            {
                return;
            }

            _snapshot = null;
        }

        NotifyChange(changedNodes);
    }

    /// <summary>
    /// Keeps the items in exactly one of the set and <paramref name="other"/>, invalidating each affected reader once.
    /// </summary>
    public void SymmetricExceptWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ReactiveObserver.ThrowIfTracking();

        var toggled = new HashSet<T>(other, _items.Comparer);
        List<ReactiveNode> changedNodes = [];
        lock (_gate)
        {
            if (toggled.Count == 0)
            {
                return;
            }

            var changes = WatchedChanges;
            foreach (var item in toggled)
            {
                if (_items.Remove(item))
                {
                    changes?.Record(SetChange<T>.Removed(item));
                }
                else
                {
                    _items.Add(item);
                    changes?.Record(SetChange<T>.Added(item));
                }

                CollectNode(item, changedNodes);
            }

            _snapshot = null;
        }

        NotifyChange(changedNodes);
    }

    private bool Compare(IEnumerable<T> other, Func<HashSet<T>, IEnumerable<T>, bool> compare)
    {
        ArgumentNullException.ThrowIfNull(other);

        // Materialized outside the lock: enumerating another reactive set takes its own gate.
        IEnumerable<T> others = other is T[] or List<T> or HashSet<T> ? other : other.ToArray();

        ReactiveProperty.Track(ref _itemsNode, ItemsNodeName);

        lock (_gate)
        {
            return compare(_items, others);
        }
    }

    /// <summary>
    /// The items of <paramref name="other"/>, safe to read under the lock: arrays and lists in place, any other
    /// sequence copied first, since enumerating it (another reactive set, a lazy query) may take locks or read
    /// reactive state.
    /// </summary>
    private static ReadOnlySpan<T> ReadableUnderLock(IEnumerable<T> other) => other switch
    {
        T[] array => array,
        List<T> list => CollectionsMarshal.AsSpan(list),
        _ => other.ToArray(),
    };

    private void CollectNode(T item, List<ReactiveNode> changedNodes)
    {
        if (_itemNodes.TryGetValue(item, out var node))
        {
            changedNodes.Add(node);
        }
    }

    private void TrackItem(T item)
    {
        var observer = ReactiveObserver.Current;
        if (observer is null)
        {
            return;
        }

        // Created and looked up under the lock that guards the items: a writer either runs before this
        // lookup (and the read that follows the subscription sees its membership) or finds the node and notifies it.
        ReactiveNode node;
        lock (_gate)
        {
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_itemNodes, item, out _);
            node = slot ??= new ReactiveNode(WickDiagnostics.Enabled ? $"ReactiveSet[{item}]" : ItemNodeName);
        }

        observer.RecordDependency(node);
    }

    private T[] CreateSnapshot()
    {
        lock (_gate)
        {
            return _snapshot ??= [.. _items];
        }
    }

    private CollectionChanges<SetChange<T>> CreateChanges()
    {
        var created = new CollectionChanges<SetChange<T>>(_gate, ChangesNodeName);

        return Interlocked.CompareExchange(ref _changes, created, null) ?? created;
    }

    private void NotifyChange(ReactiveNode? itemNode)
    {
        ReactiveBatch.Enter();

        try
        {
            itemNode?.Notify();
            ReactiveProperty.Notify(ref _itemsNode);
            Volatile.Read(ref _changes)?.Notify();
        }
        finally
        {
            ReactiveBatch.Exit();
        }
    }

    private void NotifyChange(List<ReactiveNode> changedNodes)
    {
        ReactiveBatch.Enter();

        try
        {
            foreach (var node in changedNodes)
            {
                node.Notify();
            }

            ReactiveProperty.Notify(ref _itemsNode);
            Volatile.Read(ref _changes)?.Notify();
        }
        finally
        {
            ReactiveBatch.Exit();
        }
    }
}
