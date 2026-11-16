using System.Collections.Immutable;

namespace Wick.Core;

/// <summary>
/// One change made to a <see cref="ReactiveList{T}"/>, delivered through <see cref="ReactiveList{T}.Changes"/>.
/// Applying the changes in order to a copy of the list reproduces it.
/// <list type="bullet">
/// <item><see cref="CollectionChangeKind.Added"/>: <see cref="Item"/> was inserted at <see cref="Index"/>.</item>
/// <item><see cref="CollectionChangeKind.Removed"/>: <see cref="Item"/> was removed from <see cref="Index"/>.</item>
/// <item><see cref="CollectionChangeKind.Replaced"/>: <see cref="Item"/> replaced <see cref="OldItem"/> at <see cref="Index"/>.</item>
/// <item><see cref="CollectionChangeKind.Moved"/>: <see cref="Item"/> moved from <see cref="OldIndex"/> to <see cref="Index"/>.</item>
/// <item><see cref="CollectionChangeKind.Reset"/>: the list was cleared or replaced as a whole and now holds <see cref="Items"/>.</item>
/// </list>
/// </summary>
public readonly record struct ListChange<T>
{
    private readonly ImmutableArray<T> _items;

    private ListChange(CollectionChangeKind kind, T item, T oldItem, int index, int oldIndex, ImmutableArray<T> items)
    {
        Kind = kind;
        Item = item;
        OldItem = oldItem;
        Index = index;
        OldIndex = oldIndex;
        _items = items;
    }

    /// <summary>What happened.</summary>
    public CollectionChangeKind Kind { get; }

    /// <summary>The item added, removed, moved or put in place of <see cref="OldItem"/>; <c>default</c> for a reset.</summary>
    public T Item { get; }

    /// <summary>The item replaced; <c>default</c> unless <see cref="Kind"/> is <see cref="CollectionChangeKind.Replaced"/>.</summary>
    public T OldItem { get; }

    /// <summary>The index of <see cref="Item"/> after the change, or before it for a removal; -1 for a reset.</summary>
    public int Index { get; }

    /// <summary>The index a moved item came from; -1 unless <see cref="Kind"/> is <see cref="CollectionChangeKind.Moved"/>.</summary>
    public int OldIndex { get; }

    /// <summary>The items after a reset, empty after a clear; empty unless <see cref="Kind"/> is <see cref="CollectionChangeKind.Reset"/>.</summary>
    public ImmutableArray<T> Items => _items.IsDefault ? [] : _items;

    internal static ListChange<T> Added(T item, int index) => new(CollectionChangeKind.Added, item, default!, index, -1, default);

    internal static ListChange<T> Removed(T item, int index) => new(CollectionChangeKind.Removed, item, default!, index, -1, default);

    internal static ListChange<T> Replaced(T item, T oldItem, int index) => new(CollectionChangeKind.Replaced, item, oldItem, index, -1, default);

    internal static ListChange<T> Moved(T item, int index, int oldIndex) => new(CollectionChangeKind.Moved, item, default!, index, oldIndex, default);

    internal static ListChange<T> Reset(ImmutableArray<T> items) => new(CollectionChangeKind.Reset, default!, default!, -1, -1, items);
}
