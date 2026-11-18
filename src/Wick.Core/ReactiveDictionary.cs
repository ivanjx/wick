using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Wick.Core;

/// <summary>
/// Thread-safe dictionary whose reads are tracked per key and whose mutations invalidate only the observers that
/// depend on them.
/// <list type="bullet">
/// <item>Key lookups (indexer, <see cref="TryGetValue"/>, <see cref="ContainsKey"/>) depend on that key only, including
/// a key that is missing: adding it later invalidates the reader.</item>
/// <item><see cref="Count"/> readers depend on the number of entries.</item>
/// <item>Enumeration, <see cref="Keys"/> and <see cref="Values"/> depend on all entries.</item>
/// </list>
/// Enumeration iterates an immutable snapshot, so a render can loop over the dictionary while other threads mutate it.
/// </summary>
/// <remarks>
/// A key that was read while tracking keeps a small subscription node for the lifetime of the dictionary, even after
/// the key is removed; avoid tracked lookups of unbounded sets of distinct missing keys. Such a node is named after its
/// key (<c>ReactiveDictionary[AAPL]</c>) only when created while <see cref="WickDiagnostics.Enabled"/> is set.
/// Mutations throw <see cref="InvalidOperationException"/> during a component render or a derived computation, like
/// any reactive write.
/// </remarks>
public sealed class ReactiveDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    private const string CountNodeName = "ReactiveDictionary<TKey, TValue>.Count";
    private const string EntriesNodeName = "ReactiveDictionary<TKey, TValue>";
    private const string KeyNodeName = "ReactiveDictionary[key]";
    private const string ChangesNodeName = "ReactiveDictionary<TKey, TValue>.Changes";

    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, TValue> _entries;
    private readonly Dictionary<TKey, ReactiveNode> _keyNodes;
    private KeyValuePair<TKey, TValue>[]? _snapshot;
    private ReactiveNode? _countNode;
    private ReactiveNode? _entriesNode;
    private CollectionChanges<DictionaryChange<TKey, TValue>>? _changes;

    /// <summary>Creates an empty dictionary that compares keys with <paramref name="comparer"/>, or the default comparer when <see langword="null"/>.</summary>
    public ReactiveDictionary(IEqualityComparer<TKey>? comparer = null)
    {
        _entries = new Dictionary<TKey, TValue>(comparer);
        _keyNodes = new Dictionary<TKey, ReactiveNode>(comparer);
    }

    /// <summary>Creates a dictionary holding <paramref name="entries"/>, whose keys are compared with <paramref name="comparer"/> or the default comparer.</summary>
    /// <exception cref="ArgumentException"><paramref name="entries"/> contains a duplicate key.</exception>
    public ReactiveDictionary(IEnumerable<KeyValuePair<TKey, TValue>> entries, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _entries = new Dictionary<TKey, TValue>(entries, comparer);
        _keyNodes = new Dictionary<TKey, ReactiveNode>(comparer);
    }

    /// <summary>The number of entries; replacing a value does not invalidate its readers.</summary>
    public int Count
    {
        get
        {
            ReactiveProperty.Track(ref _countNode, CountNodeName);

            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;

    /// <summary>The keys of a snapshot; tracked like enumeration.</summary>
    public ImmutableArray<TKey> Keys => ImmutableArray.CreateRange(Snapshot(), static entry => entry.Key);

    /// <summary>The values of a snapshot; tracked like enumeration.</summary>
    public ImmutableArray<TValue> Values => ImmutableArray.CreateRange(Snapshot(), static entry => entry.Value);

    ICollection<TKey> IDictionary<TKey, TValue>.Keys => Keys;

    ICollection<TValue> IDictionary<TKey, TValue>.Values => Values;

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

    /// <summary>
    /// The changes made to the dictionary from now on, in order, for
    /// <see cref="Reactive.Watch{TChange}(CollectionChanges{TChange}, Action{IReadOnlyList{TChange}}, EffectOptions?)"/>:
    /// <c>Reactive.Watch(dictionary.Changes, changes =&gt; ...)</c>. <see cref="Clear"/> reports one removal per entry.
    /// </summary>
    /// <remarks>Reading it is not tracked. Changes are recorded only while watched.</remarks>
    public CollectionChanges<DictionaryChange<TKey, TValue>> Changes
        => Volatile.Read(ref _changes) ?? CreateChanges();

    /// <summary>The change stream while watched, else null; read under the lock, before recording.</summary>
    private CollectionChanges<DictionaryChange<TKey, TValue>>? WatchedChanges
    {
        get
        {
            var changes = _changes;

            return changes is not null && changes.IsWatched ? changes : null;
        }
    }

    /// <summary>Gets the value of <paramref name="key"/>, or adds or replaces it; readers of other keys are not invalidated.</summary>
    public TValue this[TKey key]
    {
        get
        {
            if (!TryGetValue(key, out var value))
            {
                throw new KeyNotFoundException($"The key '{key}' was not present in the dictionary.");
            }

            return value;
        }
        set => Set(key, value, throwIfExists: false);
    }

    /// <summary>The entries as an immutable array; tracked like enumeration.</summary>
    public ImmutableArray<KeyValuePair<TKey, TValue>> Snapshot()
    {
        ReactiveProperty.Track(ref _entriesNode, EntriesNodeName);

        return ImmutableCollectionsMarshal.AsImmutableArray(Volatile.Read(ref _snapshot) ?? CreateSnapshot());
    }

    /// <summary>Enumerates a snapshot: mutations made during the loop, on any thread, are not observed by it.</summary>
    public ImmutableArray<KeyValuePair<TKey, TValue>>.Enumerator GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
        => ((IEnumerable<KeyValuePair<TKey, TValue>>)Snapshot()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)Snapshot()).GetEnumerator();

    /// <summary>Gets the value of <paramref name="key"/> if present; readers depend on that key only.</summary>
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        TrackKey(key);

        lock (_gate)
        {
            return _entries.TryGetValue(key, out value);
        }
    }

    /// <summary>Whether <paramref name="key"/> is present; readers depend on that key only.</summary>
    public bool ContainsKey(TKey key)
    {
        TrackKey(key);

        lock (_gate)
        {
            return _entries.ContainsKey(key);
        }
    }

    bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item)
        => TryGetValue(item.Key, out var value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);

    void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        => Snapshot().CopyTo(array, arrayIndex);

    /// <summary>Adds an entry for a key that is not present yet.</summary>
    /// <exception cref="ArgumentException">The key already exists.</exception>
    public void Add(TKey key, TValue value) => Set(key, value, throwIfExists: true);

    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

    /// <summary>Adds the entry unless the key exists; returns whether it was added.</summary>
    public bool TryAdd(TKey key, TValue value)
    {
        ReactiveObserver.ThrowIfTracking();

        ReactiveNode? keyNode;
        lock (_gate)
        {
            if (!_entries.TryAdd(key, value))
            {
                return false;
            }

            _snapshot = null;
            keyNode = _keyNodes.GetValueOrDefault(key);
            WatchedChanges?.Record(DictionaryChange<TKey, TValue>.Added(key, value));
        }

        NotifyChange(keyNode, countChanged: true);

        return true;
    }

    /// <summary>Removes the entry of <paramref name="key"/>; returns whether it was present.</summary>
    public bool Remove(TKey key)
    {
        ReactiveObserver.ThrowIfTracking();

        ReactiveNode? keyNode;
        lock (_gate)
        {
            if (!_entries.Remove(key, out var value))
            {
                return false;
            }

            _snapshot = null;
            keyNode = _keyNodes.GetValueOrDefault(key);
            WatchedChanges?.Record(DictionaryChange<TKey, TValue>.Removed(key, value));
        }

        NotifyChange(keyNode, countChanged: true);

        return true;
    }

    bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item)
    {
        ReactiveObserver.ThrowIfTracking();

        ReactiveNode? keyNode;
        lock (_gate)
        {
            if (!_entries.TryGetValue(item.Key, out var value) || !EqualityComparer<TValue>.Default.Equals(value, item.Value))
            {
                return false;
            }

            _entries.Remove(item.Key);
            _snapshot = null;
            keyNode = _keyNodes.GetValueOrDefault(item.Key);
            WatchedChanges?.Record(DictionaryChange<TKey, TValue>.Removed(item.Key, value));
        }

        NotifyChange(keyNode, countChanged: true);

        return true;
    }

    /// <summary>Removes every entry; an empty dictionary notifies nobody.</summary>
    public void Clear()
    {
        ReactiveObserver.ThrowIfTracking();

        List<ReactiveNode> removedKeyNodes = [];
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            var changes = WatchedChanges;
            foreach (var (key, value) in _entries)
            {
                if (_keyNodes.TryGetValue(key, out var node))
                {
                    removedKeyNodes.Add(node);
                }

                changes?.Record(DictionaryChange<TKey, TValue>.Removed(key, value));
            }

            _entries.Clear();
            _snapshot = null;
        }

        ReactiveBatch.Enter();

        try
        {
            foreach (var node in removedKeyNodes)
            {
                node.Notify();
            }

            ReactiveProperty.Notify(ref _countNode);
            ReactiveProperty.Notify(ref _entriesNode);
            Volatile.Read(ref _changes)?.Notify();
        }
        finally
        {
            ReactiveBatch.Exit();
        }
    }

    private void Set(TKey key, TValue value, bool throwIfExists)
    {
        ArgumentNullException.ThrowIfNull(key);
        ReactiveObserver.ThrowIfTracking();

        ReactiveNode? keyNode;
        bool added;
        lock (_gate)
        {
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, key, out var exists);
            if (exists)
            {
                if (throwIfExists)
                {
                    throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
                }

                if (EqualityComparer<TValue>.Default.Equals(slot, value))
                {
                    return;
                }
            }

            var oldValue = slot;
            slot = value;
            added = !exists;
            _snapshot = null;
            keyNode = _keyNodes.GetValueOrDefault(key);
            WatchedChanges?.Record(added
                ? DictionaryChange<TKey, TValue>.Added(key, value)
                : DictionaryChange<TKey, TValue>.Replaced(key, value, oldValue!));
        }

        NotifyChange(keyNode, countChanged: added);
    }

    private void TrackKey(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var observer = ReactiveObserver.Current;
        if (observer is null)
        {
            return;
        }

        // Created and looked up under the lock that guards the entries: a writer either runs before this
        // lookup (and the read that follows the subscription sees its value) or finds the node and notifies it.
        ReactiveNode node;
        lock (_gate)
        {
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_keyNodes, key, out _);
            node = slot ??= new ReactiveNode(WickDiagnostics.Enabled ? $"ReactiveDictionary[{key}]" : KeyNodeName);
        }

        observer.RecordDependency(node);
    }

    private KeyValuePair<TKey, TValue>[] CreateSnapshot()
    {
        lock (_gate)
        {
            return _snapshot ??= [.. _entries];
        }
    }

    private CollectionChanges<DictionaryChange<TKey, TValue>> CreateChanges()
    {
        var created = new CollectionChanges<DictionaryChange<TKey, TValue>>(_gate, ChangesNodeName);

        return Interlocked.CompareExchange(ref _changes, created, null) ?? created;
    }

    private void NotifyChange(ReactiveNode? keyNode, bool countChanged)
    {
        ReactiveBatch.Enter();

        try
        {
            keyNode?.Notify();

            if (countChanged)
            {
                ReactiveProperty.Notify(ref _countNode);
            }

            ReactiveProperty.Notify(ref _entriesNode);
            Volatile.Read(ref _changes)?.Notify();
        }
        finally
        {
            ReactiveBatch.Exit();
        }
    }
}
