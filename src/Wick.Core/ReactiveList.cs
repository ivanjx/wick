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
/// </remarks>
public sealed class ReactiveList<T> : IList<T>, IReadOnlyList<T>
{
    private readonly Lock _gate = new();
    private readonly List<T> _items;
    private T[]? _snapshot;
    private ReactiveNode? _countNode;
    private ReactiveNode? _itemsNode;

    public ReactiveList()
    {
        _items = [];
    }

    public ReactiveList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _items = [.. items];
    }

    public int Count
    {
        get
        {
            ReactiveProperty.Track(ref _countNode);

            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    bool ICollection<T>.IsReadOnly => false;

    public T this[int index]
    {
        get
        {
            ReactiveProperty.Track(ref _itemsNode);

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
                if (EqualityComparer<T>.Default.Equals(_items[index], value))
                {
                    return;
                }

                _items[index] = value;
                _snapshot = null;
            }

            ReactiveProperty.Notify(ref _itemsNode);
        }
    }

    /// <summary>The current items as an immutable array; tracked like enumeration.</summary>
    public ImmutableArray<T> Snapshot()
    {
        ReactiveProperty.Track(ref _itemsNode);

        return ImmutableCollectionsMarshal.AsImmutableArray(Volatile.Read(ref _snapshot) ?? CreateSnapshot());
    }

    /// <summary>Enumerates a snapshot: mutations made during the loop, on any thread, are not observed by it.</summary>
    public ImmutableArray<T>.Enumerator GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Snapshot()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)Snapshot()).GetEnumerator();

    public bool Contains(T item)
    {
        ReactiveProperty.Track(ref _itemsNode);

        lock (_gate)
        {
            return _items.Contains(item);
        }
    }

    public int IndexOf(T item)
    {
        ReactiveProperty.Track(ref _itemsNode);

        lock (_gate)
        {
            return _items.IndexOf(item);
        }
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        ReactiveProperty.Track(ref _itemsNode);

        lock (_gate)
        {
            _items.CopyTo(array, arrayIndex);
        }
    }

    public void Add(T item)
    {
        ReactiveObserver.ThrowIfTracking();

        lock (_gate)
        {
            _items.Add(item);
            _snapshot = null;
        }

        NotifyCountAndItems();
    }

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
            _items.AddRange(added);
            _snapshot = null;
        }

        NotifyCountAndItems();
    }

    public void Insert(int index, T item)
    {
        ReactiveObserver.ThrowIfTracking();

        lock (_gate)
        {
            _items.Insert(index, item);
            _snapshot = null;
        }

        NotifyCountAndItems();
    }

    public bool Remove(T item)
    {
        ReactiveObserver.ThrowIfTracking();

        lock (_gate)
        {
            if (!_items.Remove(item))
            {
                return false;
            }

            _snapshot = null;
        }

        NotifyCountAndItems();

        return true;
    }

    public void RemoveAt(int index)
    {
        ReactiveObserver.ThrowIfTracking();

        lock (_gate)
        {
            _items.RemoveAt(index);
            _snapshot = null;
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
            removed = _items.RemoveAll(match);
            if (removed == 0)
            {
                return 0;
            }

            _snapshot = null;
        }

        NotifyCountAndItems();

        return removed;
    }

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
        }

        ReactiveProperty.Notify(ref _itemsNode);
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
        }

        if (countChanged)
        {
            NotifyCountAndItems();
            return;
        }

        ReactiveProperty.Notify(ref _itemsNode);
    }

    private T[] CreateSnapshot()
    {
        lock (_gate)
        {
            return _snapshot ??= _items.ToArray();
        }
    }

    private void NotifyCountAndItems()
    {
        ReactiveBatch.Enter();

        try
        {
            ReactiveProperty.Notify(ref _countNode);
            ReactiveProperty.Notify(ref _itemsNode);
        }
        finally
        {
            ReactiveBatch.Exit();
        }
    }
}
