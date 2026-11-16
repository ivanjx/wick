namespace Wick.Core;

/// <summary>What a <see cref="ListChange{T}"/>, <see cref="SetChange{T}"/> or <see cref="DictionaryChange{TKey, TValue}"/> did.</summary>
public enum CollectionChangeKind
{
    /// <summary>An item or entry was added.</summary>
    Added,

    /// <summary>An item or entry was removed.</summary>
    Removed,

    /// <summary>A list item or a dictionary value was replaced by another one.</summary>
    Replaced,

    /// <summary>A list item was moved to another index.</summary>
    Moved,

    /// <summary>A list was cleared or had all its items replaced.</summary>
    Reset,
}
