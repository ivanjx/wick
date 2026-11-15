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
/// </remarks>
public sealed class ReactiveSet<T> : ISet<T>, IReadOnlySet<T>
    where T : notnull
{
    private const string ItemsNodeName = "ReactiveSet<T>";
    private const string ItemNodeName = "ReactiveSet[item]";

    private readonly Lock _gate = new();
    private readonly HashSet<T> _items;
    private readonly Dictionary<T, ReactiveNode> _itemNodes;
    private T[]? _snapshot;
    private ReactiveNode? _itemsNode;

    public ReactiveSet()
        : this(comparer: null)
    {
    }

    public ReactiveSet(IEqualityComparer<T>? comparer)
    {
        _items = new HashSet<T>(comparer);
        _itemNodes = new Dictionary<T, ReactiveNode>(comparer);
    }

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

    public void CopyTo(T[] array, int arrayIndex) => Snapshot().CopyTo(array, arrayIndex);

    public bool IsSubsetOf(IEnumerable<T> other) => Compare(other, static (items, others) => items.IsSubsetOf(others));

    public bool IsSupersetOf(IEnumerable<T> other) => Compare(other, static (items, others) => items.IsSupersetOf(others));

    public bool IsProperSubsetOf(IEnumerable<T> other)
        => Compare(other, static (items, others) => items.IsProperSubsetOf(others));

    public bool IsProperSupersetOf(IEnumerable<T> other)
        => Compare(other, static (items, others) => items.IsProperSupersetOf(others));

    public bool Overlaps(IEnumerable<T> other) => Compare(other, static (items, others) => items.Overlaps(others));

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
        }

        NotifyChange(itemNode);

        return true;
    }

    void ICollection<T>.Add(T item) => Add(item);

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
        }

        NotifyChange(itemNode);

        return true;
    }

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

            foreach (var item in _items)
            {
                CollectNode(item, changedNodes);
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

        var others = other.ToArray();
        List<ReactiveNode> changedNodes = [];
        var changed = false;
        lock (_gate)
        {
            foreach (var item in others)
            {
                if (_items.Add(item))
                {
                    changed = true;
                    CollectNode(item, changedNodes);
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

        var others = other.ToArray();
        List<ReactiveNode> changedNodes = [];
        var changed = false;
        lock (_gate)
        {
            foreach (var item in others)
            {
                if (_items.Remove(item))
                {
                    changed = true;
                    CollectNode(item, changedNodes);
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

        var others = other.ToArray();
        List<ReactiveNode> changedNodes = [];
        lock (_gate)
        {
            var kept = new HashSet<T>(others, _items.Comparer);
            var removed = _items.Where(item => !kept.Contains(item)).ToArray();
            if (removed.Length == 0)
            {
                return;
            }

            foreach (var item in removed)
            {
                _items.Remove(item);
                CollectNode(item, changedNodes);
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

            foreach (var item in toggled)
            {
                if (!_items.Remove(item))
                {
                    _items.Add(item);
                }

                CollectNode(item, changedNodes);
            }

            _snapshot = null;
        }

        NotifyChange(changedNodes);
    }

    private bool Compare(IEnumerable<T> other, Func<HashSet<T>, T[], bool> compare)
    {
        ArgumentNullException.ThrowIfNull(other);

        // Materialized outside the lock: enumerating another reactive set takes its own gate.
        var others = other.ToArray();

        ReactiveProperty.Track(ref _itemsNode, ItemsNodeName);

        lock (_gate)
        {
            return compare(_items, others);
        }
    }

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

    private void NotifyChange(ReactiveNode? itemNode)
    {
        ReactiveBatch.Enter();

        try
        {
            itemNode?.Notify();
            ReactiveProperty.Notify(ref _itemsNode);
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
        }
        finally
        {
            ReactiveBatch.Exit();
        }
    }
}
