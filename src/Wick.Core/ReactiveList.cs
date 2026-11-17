using System.Collections;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Wick.Core;

/// <summary>
/// Thread-safe list whose reads are tracked and whose mutations invalidate only the observers that depend on them.
/// <list type="bullet">
/// <item><see cref="Count"/> readers depend on the number of items: replacing or moving an item does not affect them.</item>
/// <item>Enumeration, the indexer, <see cref="Contains"/> and <see cref="IndexOf"/> depend on the contents.</item>
/// </list>
/// Enumeration iterates an immutable snapshot, so a render can loop over the list while other threads mutate it.
/// The snapshot is built at most once per change, on the first enumeration after it.
/// </summary>
/// <remarks>
/// Each call is atomic on its own; a sequence such as <c>list[list.Count - 1]</c> is not. For a consistent view
/// across several reads, read <see cref="Snapshot"/> once. Items are not observed: make them reactive themselves
/// (for example with <see cref="ReactiveStateAttribute"/>) so that editing an item re-renders only its readers.
/// Mutations throw <see cref="InvalidOperationException"/> during a component render or a derived computation, like
/// any reactive write.
/// </remarks>
public sealed class ReactiveList<T> : IList<T>, IReadOnlyList<T>
{
    private const string CountNodeName = "ReactiveList<T>.Count";
    private const string ItemsNodeName = "ReactiveList<T>";
    private const string ChangesNodeName = "ReactiveList<T>.Changes";

    private readonly Lock _gate = new();
    private readonly List<T> _items;
    private T[]? _snapshot;
    private ReactiveNode? _countNode;
    private ReactiveNode? _itemsNode;
    private CollectionChanges<ListChange<T>>? _changes;

    /// <summary>Creates an empty list.</summary>
    public ReactiveList()
    {
        _items = [];
    }

    /// <summary>Creates a list holding <paramref name="items"/>.</summary>
    public ReactiveList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _items = [.. items];
    }

    /// <summary>The number of items; readers are not invalidated by changes that keep it.</summary>
    public int Count
    {
        get
        {
            ReactiveProperty.Track(ref _countNode, CountNodeName);

            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    bool ICollection<T>.IsReadOnly => false;

    /// <summary>
    /// The changes made to the list from now on, in order, for
    /// <see cref="Reactive.Watch{TChange}(CollectionChanges{TChange}, Action{IReadOnlyList{TChange}}, EffectOptions?)"/>:
    /// <c>Reactive.Watch(list.Changes, changes =&gt; ...)</c>. <see cref="Clear"/> and <see cref="ReplaceAll"/>
    /// report a <see cref="CollectionChangeKind.Reset"/>; the other mutations report one change per item.
    /// </summary>
    /// <remarks>Reading it is not tracked. Changes are recorded only while watched.</remarks>
    public CollectionChanges<ListChange<T>> Changes
        => Volatile.Read(ref _changes) ?? CreateChanges();

    /// <summary>The change stream while watched, else null; read under the lock, before recording.</summary>
    private CollectionChanges<ListChange<T>>? WatchedChanges
    {
        get
        {
            var changes = _changes;

            return changes is not null && changes.IsWatched ? changes : null;
        }
    }

    /// <summary>
    /// Gets the item at <paramref name="index"/>, tracked like enumeration, or replaces it; writing an equal item by
    /// <see cref="EqualityComparer{T}.Default"/> notifies nobody, and <see cref="Count"/> readers are never invalidated.
    /// </summary>
    public T this[int index]
    {
        get
        {
            ReactiveProperty.Track(ref _itemsNode, ItemsNodeName);

            lock (_gate)
            {
                return _items[index];
            }
        }
        set
        {
            ReactiveObserver.ThrowIfTracking();

            lock (_gate)
            {
                var oldItem = _items[index];
                if (EqualityComparer<T>.Default.Equals(oldItem, value))
                {
                    return;
                }

                _items[index] = value;
                _snapshot = null;
                WatchedChanges?.Record(ListChange<T>.Replaced(value, oldItem, index));
            }

            NotifyItems();
        }
    }

    /// <summary>The current items as an immutable array; tracked like enumeration.</summary>
    public ImmutableArray<T> Snapshot()
    {
        ReactiveProperty.Track(ref _itemsNode, ItemsNodeName);

        return ImmutableCollectionsMarshal.AsImmutableArray(Volatile.Read(ref _snapshot) ?? CreateSnapshot());
    }

    /// <summary>Enumerates a snapshot: mutations made during the loop, on any thread, are not observed by it.</summary>
    public ImmutableArray<T>.Enumerator GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Snapshot()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)Snapshot()).GetEnumerator();

    /// <summary>Whether the list contains <paramref name="item"/>; tracked like enumeration.</summary>
    public bool Contains(T item)
    {
        ReactiveProperty.Track(ref _itemsNode, ItemsNodeName);

        lock (_gate)
        {
            return _items.Contains(item);
        }
    }

    /// <summary>The index of the first occurrence of <paramref name="item"/>, or -1; tracked like enumeration.</summary>
    public int IndexOf(T item)
    {
        ReactiveProperty.Track(ref _itemsNode, ItemsNodeName);

        lock (_gate)
        {
            return _items.IndexOf(item);
        }
    }

    /// <summary>Copies the items to <paramref name="array"/> starting at <paramref name="arrayIndex"/>; tracked like enumeration.</summary>
    public void CopyTo(T[] array, int arrayIndex)
    {
        ReactiveProperty.Track(ref _itemsNode, ItemsNodeName);

        lock (_gate)
        {
            _items.CopyTo(array, arrayIndex);
        }
    }

    /// <summary>Appends <paramref name="item"/>.</summary>
    public void Add(T item)
    {
        ReactiveObserver.ThrowIfTracking();

        lock (_gate)
        {
            _items.Add(item);
            _snapshot = null;
            WatchedChanges?.Record(ListChange<T>.Added(item, _items.Count - 1));
        }

        NotifyCountAndItems();
    }

    /// <summary>Appends <paramref name="items"/> with a single notification; an empty sequence notifies nobody.</summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ReactiveObserver.ThrowIfTracking();

        // Materialize outside the lock: the sequence may be lazy and read this list.
        T[] added = [.. items];
        if (added.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            var start = _items.Count;
            _items.AddRange(added);
            _snapshot = null;
            RecordAdded(added, start);
        }

        NotifyCountAndItems();
    }

    /// <summary>Inserts <paramref name="item"/> at <paramref name="index"/>.</summary>
    public void Insert(int index, T item)
    {
        ReactiveObserver.ThrowIfTracking();

        lock (_gate)
        {
            _items.Insert(index, item);
            _snapshot = null;
            WatchedChanges?.Record(ListChange<T>.Added(item, index));
        }

        NotifyCountAndItems();
    }

    /// <summary>Removes the first occurrence of <paramref name="item"/>; returns whether it was found.</summary>
    public bool Remove(T item)
    {
        ReactiveObserver.ThrowIfTracking();

        lock (_gate)
        {
            // What List<T>.Remove does, keeping the index for the change.
            var index = _items.IndexOf(item);
            if (index < 0)
            {
                return false;
            }

            var removed = _items[index];
            _items.RemoveAt(index);
            _snapshot = null;
            WatchedChanges?.Record(ListChange<T>.Removed(removed, index));
        }

        NotifyCountAndItems();

        return true;
    }

    /// <summary>Removes the item at <paramref name="index"/>.</summary>
    public void RemoveAt(int index)
    {
        ReactiveObserver.ThrowIfTracking();

        lock (_gate)
        {
            var changes = WatchedChanges;
            var removed = changes is null ? default! : _items[index];

            _items.RemoveAt(index);
            _snapshot = null;
            changes?.Record(ListChange<T>.Removed(removed, index));
        }

        NotifyCountAndItems();
    }

    /// <summary>Removes every item matching <paramref name="match"/>, which runs under the list's lock and must not access the list.</summary>
    public int RemoveAll(Predicate<T> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        ReactiveObserver.ThrowIfTracking();

        int removed;
        lock (_gate)
        {
            var changes = WatchedChanges;
            removed = changes is null ? _items.RemoveAll(match) : RemoveAll(match, changes);
            if (removed == 0)
            {
                return 0;
            }

            _snapshot = null;
        }

        NotifyCountAndItems();

        return removed;
    }

    /// <summary>Removes every item; an empty list notifies nobody.</summary>
    public void Clear()
    {
        ReactiveObserver.ThrowIfTracking();

        lock (_gate)
        {
            if (_items.Count == 0)
            {
                return;
            }

            _items.Clear();
            _snapshot = null;
            WatchedChanges?.Record(ListChange<T>.Reset([]));
        }

        NotifyCountAndItems();
    }

    /// <summary>Moves the item at <paramref name="oldIndex"/> to <paramref name="newIndex"/>; <see cref="Count"/> readers are not invalidated.</summary>
    public void Move(int oldIndex, int newIndex)
    {
        ReactiveObserver.ThrowIfTracking();

        lock (_gate)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)oldIndex, (uint)_items.Count, nameof(oldIndex));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)newIndex, (uint)_items.Count, nameof(newIndex));

            if (oldIndex == newIndex)
            {
                return;
            }

            var item = _items[oldIndex];
            _items.RemoveAt(oldIndex);
            _items.Insert(newIndex, item);
            _snapshot = null;
            WatchedChanges?.Record(ListChange<T>.Moved(item, newIndex, oldIndex));
        }

        NotifyItems();
    }

    /// <summary>Replaces all items in one change; <see cref="Count"/> readers are invalidated only when the count differs.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ReactiveObserver.ThrowIfTracking();

        T[] replacement = [.. items];

        bool countChanged;
        lock (_gate)
        {
            countChanged = _items.Count != replacement.Length;

            _items.Clear();
            _items.AddRange(replacement);
            _snapshot = null;

            // The array is never written again, so the change can share it.
            WatchedChanges?.Record(ListChange<T>.Reset(ImmutableCollectionsMarshal.AsImmutableArray(replacement)));
        }

        if (countChanged)
        {
            NotifyCountAndItems();
            return;
        }

        NotifyItems();
    }

    private T[] CreateSnapshot()
    {
        lock (_gate)
        {
            return _snapshot ??= _items.ToArray();
        }
    }

    private CollectionChanges<ListChange<T>> CreateChanges()
    {
        var created = new CollectionChanges<ListChange<T>>(_gate, ChangesNodeName);

        return Interlocked.CompareExchange(ref _changes, created, null) ?? created;
    }

    /// <summary>Records the items just added from <paramref name="start"/> on; called under the lock.</summary>
    private void RecordAdded(T[] added, int start)
    {
        var changes = WatchedChanges;
        if (changes is null)
        {
            return;
        }

        for (var i = 0; i < added.Length; i++)
        {
            changes.Record(ListChange<T>.Added(added[i], start + i));
        }
    }

    /// <summary>
    /// <see cref="List{T}.RemoveAll"/> that records each removal, at its index once the earlier ones are removed;
    /// called under the lock. The list is left unchanged when <paramref name="match"/> throws.
    /// </summary>
    private int RemoveAll(Predicate<T> match, CollectionChanges<ListChange<T>> changes)
    {
        var matches = new bool[_items.Count];
        var removed = 0;
        for (var i = 0; i < matches.Length; i++)
        {
            if (match(_items[i]))
            {
                matches[i] = true;
                removed++;
            }
        }

        if (removed == 0)
        {
            return 0;
        }

        var kept = 0;
        for (var i = 0; i < matches.Length; i++)
        {
            var item = _items[i];
            if (matches[i])
            {
                changes.Record(ListChange<T>.Removed(item, kept));
                continue;
            }

            _items[kept++] = item;
        }

        _items.RemoveRange(kept, _items.Count - kept);

        return removed;
    }

    private void NotifyItems()
    {
        var changes = Volatile.Read(ref _changes);
        if (changes is null)
        {
            ReactiveProperty.Notify(ref _itemsNode);
            return;
        }

        ReactiveBatch.Enter();

        try
        {
            ReactiveProperty.Notify(ref _itemsNode);
            changes.Notify();
        }
        finally
        {
            ReactiveBatch.Exit();
        }
    }

    private void NotifyCountAndItems()
    {
        ReactiveBatch.Enter();

        try
        {
            ReactiveProperty.Notify(ref _countNode);
            ReactiveProperty.Notify(ref _itemsNode);
            Volatile.Read(ref _changes)?.Notify();
        }
        finally
        {
            ReactiveBatch.Exit();
        }
    }
}
