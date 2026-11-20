using System.ComponentModel;

namespace Wick.Core;

/// <summary>
/// Per-instance storage behind the <c>Memo</c> helpers of <see cref="ReactiveStateAttribute"/> types and
/// <see cref="ReactiveObject"/>; not meant to be used directly. Each call site (member, file, line and the
/// computation's source text) owns one <see cref="Derived{T}"/>, created on its first read from the computation
/// passed then. Later calls only look it up and read its <see cref="Derived{T}.Value"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class MemoCache
{
    private readonly Lock _gate = new();

    // Copy-on-write: readers scan a published array without locking. States hold few memos, so a linear scan that
    // compares the line first beats hashing four keys.
    private Entry[] _entries = [];

    private MemoCache()
    {
    }

    /// <summary>Reads the memo of the calling site, creating it from <paramref name="compute"/> on the first call.</summary>
    /// <param name="cache">The owner's cache field; created on first use.</param>
    /// <param name="owner">The object declaring the memoized member; names the memo in diagnostics.</param>
    /// <param name="compute">The computation; ignored once the memo exists.</param>
    /// <param name="member">The calling member.</param>
    /// <param name="file">The calling file.</param>
    /// <param name="line">The calling line.</param>
    /// <param name="expression">The source text of <paramref name="compute"/>; tells apart several memos on one line.</param>
    public static TValue Get<TValue>(ref MemoCache? cache, object owner, Func<TValue> compute, string member, string file, int line, string expression)
    {
        var memos = Volatile.Read(ref cache) ?? Create(ref cache);
        var derived = memos.Find<TValue>(member, file, line, expression);

        if (derived is null)
        {
            ArgumentNullException.ThrowIfNull(compute);
            derived = memos.Add(owner, compute, member, file, line, expression);
        }

        return derived.Value;
    }

    /// <summary>
    /// Like <see cref="Get{TValue}(ref MemoCache?, object, Func{TValue}, string, string, int, string)"/>, for a
    /// computation that receives <paramref name="owner"/>, so that it can be a static lambda the compiler caches.
    /// </summary>
    /// <param name="cache">The owner's cache field; created on first use.</param>
    /// <param name="owner">The object declaring the memoized member, passed to <paramref name="compute"/>.</param>
    /// <param name="compute">The computation; ignored once the memo exists.</param>
    /// <param name="member">The calling member.</param>
    /// <param name="file">The calling file.</param>
    /// <param name="line">The calling line.</param>
    /// <param name="expression">The source text of <paramref name="compute"/>; tells apart several memos on one line.</param>
    /// <exception cref="InvalidOperationException"><paramref name="owner"/> is not a <typeparamref name="TState"/>.</exception>
    public static TValue Get<TState, TValue>(ref MemoCache? cache, object owner, Func<TState, TValue> compute, string member, string file, int line, string expression)
        where TState : class
    {
        var memos = Volatile.Read(ref cache) ?? Create(ref cache);
        var derived = memos.Find<TValue>(member, file, line, expression);

        if (derived is null)
        {
            ArgumentNullException.ThrowIfNull(compute);
            derived = memos.Add(owner, Bind(owner, compute, member), member, file, line, expression);
        }

        return derived.Value;
    }

    private static MemoCache Create(ref MemoCache? cache)
    {
        Interlocked.CompareExchange(ref cache, new MemoCache(), null);

        return cache!;
    }

    // A separate method: a lambda in Get would allocate its closure on every call, hits included.
    private static Func<TValue> Bind<TState, TValue>(object owner, Func<TState, TValue> compute, string member)
        where TState : class
    {
        if (owner is not TState state)
        {
            throw new InvalidOperationException(
                $"The memo in '{member}' takes a '{typeof(TState).Name}', but it is declared by a '{owner.GetType().Name}'; the computation's parameter must be the declaring type.");
        }

        return () => compute(state);
    }

    private Derived<TValue>? Find<TValue>(string member, string file, int line, string expression)
    {
        foreach (var entry in Volatile.Read(ref _entries))
        {
            if (entry.Matches(member, file, line, expression))
            {
                return entry.Derived as Derived<TValue> ?? throw TypeMismatch<TValue>(entry);
            }
        }

        return null;
    }

    // Creating a Derived<T> runs no user code, so the lock never encloses a computation.
    private Derived<TValue> Add<TValue>(object owner, Func<TValue> compute, string member, string file, int line, string expression)
    {
        lock (_gate)
        {
            // Another thread may have published this call site since the unlocked lookup.
            var existing = Find<TValue>(member, file, line, expression);
            if (existing is not null)
            {
                return existing;
            }

            var derived = new Derived<TValue>(compute, comparer: null, name: owner.GetType().Name + "." + member);
            var entries = _entries;
            var grown = new Entry[entries.Length + 1];

            entries.CopyTo(grown, 0);
            grown[^1] = new Entry(member, file, line, expression, derived);
            Volatile.Write(ref _entries, grown);

            return derived;
        }
    }

    private static InvalidOperationException TypeMismatch<TValue>(Entry entry)
        => new($"The memo in '{entry.Member}' ({entry.File}:{entry.Line}) was created as '{entry.Derived.GetType().GetGenericArguments()[0].Name}' and cannot be read as '{typeof(TValue).Name}'.");

    private sealed class Entry(string member, string file, int line, string expression, object derived)
    {
        public string Member { get; } = member;

        public string File { get; } = file;

        public int Line { get; } = line;

        public string Expression { get; } = expression;

        public object Derived { get; } = derived;

        // A call site passes the same string literals every time, so the reference checks short-circuit on a hit.
        public bool Matches(string member, string file, int line, string expression)
            => Line == line
               && Same(Member, member)
               && Same(Expression, expression)
               && Same(File, file);

        private static bool Same(string a, string b) => ReferenceEquals(a, b) || a == b;
    }
}
