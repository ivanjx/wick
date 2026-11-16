namespace Wick.Core;

/// <summary>
/// One change made to a <see cref="ReactiveDictionary{TKey, TValue}"/>, delivered through
/// <see cref="ReactiveDictionary{TKey, TValue}.Changes"/>.
/// <list type="bullet">
/// <item><see cref="CollectionChangeKind.Added"/>: the entry <see cref="Key"/>, <see cref="Value"/> was added.</item>
/// <item><see cref="CollectionChangeKind.Replaced"/>: the value of <see cref="Key"/> went from <see cref="OldValue"/> to <see cref="Value"/>.</item>
/// <item><see cref="CollectionChangeKind.Removed"/>: the entry <see cref="Key"/>, <see cref="Value"/> was removed.</item>
/// </list>
/// Clearing the dictionary reports one removal per entry.
/// </summary>
public readonly record struct DictionaryChange<TKey, TValue>
{
    private DictionaryChange(CollectionChangeKind kind, TKey key, TValue value, TValue oldValue)
    {
        Kind = kind;
        Key = key;
        Value = value;
        OldValue = oldValue;
    }

    /// <summary><see cref="CollectionChangeKind.Added"/>, <see cref="CollectionChangeKind.Replaced"/> or <see cref="CollectionChangeKind.Removed"/>.</summary>
    public CollectionChangeKind Kind { get; }

    /// <summary>The key of the entry.</summary>
    public TKey Key { get; }

    /// <summary>The value added, the new value, or the value removed.</summary>
    public TValue Value { get; }

    /// <summary>The value replaced; <c>default</c> unless <see cref="Kind"/> is <see cref="CollectionChangeKind.Replaced"/>.</summary>
    public TValue OldValue { get; }

    internal static DictionaryChange<TKey, TValue> Added(TKey key, TValue value)
        => new(CollectionChangeKind.Added, key, value, default!);

    internal static DictionaryChange<TKey, TValue> Replaced(TKey key, TValue value, TValue oldValue)
        => new(CollectionChangeKind.Replaced, key, value, oldValue);

    internal static DictionaryChange<TKey, TValue> Removed(TKey key, TValue value)
        => new(CollectionChangeKind.Removed, key, value, default!);
}
