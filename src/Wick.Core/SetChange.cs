namespace Wick.Core;

/// <summary>
/// One change made to a <see cref="ReactiveSet{T}"/>, delivered through <see cref="ReactiveSet{T}.Changes"/>:
/// <see cref="Item"/> was <see cref="CollectionChangeKind.Added"/> or <see cref="CollectionChangeKind.Removed"/>.
/// Clearing the set and the bulk operations report one change per item they added or removed.
/// </summary>
public readonly record struct SetChange<T>
{
    private SetChange(CollectionChangeKind kind, T item)
    {
        Kind = kind;
        Item = item;
    }

    /// <summary><see cref="CollectionChangeKind.Added"/> or <see cref="CollectionChangeKind.Removed"/>.</summary>
    public CollectionChangeKind Kind { get; }

    /// <summary>The item added or removed.</summary>
    public T Item { get; }

    internal static SetChange<T> Added(T item) => new(CollectionChangeKind.Added, item);

    internal static SetChange<T> Removed(T item) => new(CollectionChangeKind.Removed, item);
}
