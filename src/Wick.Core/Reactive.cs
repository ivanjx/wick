using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Wick.Core;

/// <summary>
/// Entry points for working with reactive state; components have owned forms of the effect helpers.
/// <list type="bullet">
/// <item><see cref="Effect(Action, EffectOptions?)"/> and <c>Watch</c> run code when state they read changes.</item>
/// <item><see cref="Derived{T}(Func{T}, IEqualityComparer{T}?)"/> and
/// <see cref="Debounced{T}(Func{T}, TimeSpan, TimeProvider?)"/> compute values that are tracked like properties.</item>
/// <item><see cref="Batch"/> and <see cref="BatchAsync"/> deliver several writes as one notification.</item>
/// <item><see cref="Untracked{T}"/> reads state without depending on it.</item>
/// <item><see cref="Invalidate{T}"/> notifies the readers of state that changed behind Wick's back.</item>
/// </list>
/// </summary>
public static class Reactive
{
    /// <summary>
    /// Raised when a <see cref="ReactiveEffect"/> created without an error handler fails while re-running, or an
    /// asynchronous one fails at all. Without a subscriber the exception is written to <see cref="Trace"/>; it is never
    /// rethrown on the thread pool.
    /// </summary>
    public static event Action<Exception>? UnhandledEffectException;

    /// <summary>
    /// Runs <paramref name="action"/> and defers all notifications raised on this thread until the
    /// outermost batch completes; each affected observer is then invalidated once.
    /// </summary>
    public static void Batch(Action action)
    {
        ReactiveBatch.Enter();

        try
        {
            action();
        }
        finally
        {
            ReactiveBatch.Exit();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> and defers the notifications raised by its asynchronous flow, on any thread,
    /// until the returned task completes. Writes made by code outside that flow are not deferred, and a nested
    /// <see cref="BatchAsync"/> or <see cref="Batch"/> joins the outermost asynchronous batch.
    /// </summary>
    /// <remarks>Observers do not see intermediate values, so keep the awaited work short: a long batch delays every update it contains.</remarks>
    public static Task BatchAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return ReactiveBatch.RunAsync(action);
    }

    /// <summary>
    /// Notifies the observers of every reactive value that <paramref name="read"/> reads. Escape hatch for state
    /// mutated without a reactive setter, for example a mutable object changed in place:
    /// <code>
    /// State.Settings.Values.Add(item);
    /// Reactive.Invalidate(() => State.Settings);
    /// </code>
    /// Only reactive reads count: <c>() => State.Native.Value</c> invalidates readers of <c>State.Native</c>.
    /// </summary>
    public static void Invalidate<T>(Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        ReactiveObserver.ThrowIfTracking();

        ReactiveNode[] nodes;
        var collector = new DependencyCollector();
        try
        {
            using (collector.BeginTracking())
            {
                _ = read();
            }

            nodes = collector.GetDependencies();
        }
        finally
        {
            collector.Dispose();
        }

        ReactiveBatch.Enter();

        try
        {
            foreach (var node in nodes)
            {
                node.Notify();
            }
        }
        finally
        {
            ReactiveBatch.Exit();
        }
    }

    /// <summary>
    /// Reads reactive state without making it a dependency of the render, effect or derived value running on this
    /// thread, for example a setting an effect uses but should not re-run for:
    /// <code>
    /// Effect(() => Notify(State.Message, Reactive.Untracked(() => Settings.Language)));
    /// </code>
    /// Writes inside <paramref name="read"/> are allowed only where they are allowed outside it.
    /// </summary>
    /// <remarks>Only the synchronous part of <paramref name="read"/> is untracked; reads after an <c>await</c> never are.</remarks>
    public static T Untracked<T>(Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        using (ReactiveObserver.SuspendTracking())
        {
            return read();
        }
    }

    /// <summary>
    /// Creates a cached value computed from reactive state. It is recomputed only after something it read changed,
    /// and its readers are invalidated only when the result differs by <paramref name="comparer"/>.
    /// </summary>
    public static Derived<T> Derived<T>(Func<T> compute, IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(compute);

        return new Derived<T>(compute, comparer);
    }

    /// <summary>
    /// Creates a value that follows what <paramref name="read"/> returns, but only once it has stopped changing for
    /// <paramref name="delay"/>, for example a search query that a component should not act on per keystroke:
    /// <code>
    /// private readonly Debounced&lt;string&gt; _query = Reactive.Debounced(() => State.Query, TimeSpan.FromMilliseconds(300));
    /// </code>
    /// </summary>
    /// <param name="read">
    /// Reads the source; reactive state it reads is tracked. An exception on its first call propagates; later ones are
    /// reported through <see cref="UnhandledEffectException"/> and leave the value unchanged.
    /// </param>
    /// <param name="delay">How long the source must be quiet before the value catches up.</param>
    /// <param name="timeProvider">The clock for the delay; <see cref="TimeProvider.System"/> by default.</param>
    public static Debounced<T> Debounced<T>(Func<T> read, TimeSpan delay, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(read);

        return new Debounced<T>(read, delay, timeProvider);
    }

    /// <summary>
    /// Runs <paramref name="effect"/> now and again whenever reactive state it read changes, until disposed.
    /// The first run is synchronous on the calling thread; re-runs happen on the thread pool, or through
    /// <see cref="EffectOptions.Dispatcher"/>, coalesced, and are delayed by <see cref="EffectOptions.Debounce"/> or
    /// <see cref="EffectOptions.Throttle"/>.
    /// </summary>
    /// <param name="effect">The code to run; it may write reactive state, and its writes are batched per run.</param>
    /// <param name="options">
    /// Error handling, dispatcher and timing. Without <see cref="EffectOptions.OnError"/>, the first run's exception
    /// propagates to the caller and later ones are reported through <see cref="UnhandledEffectException"/>.
    /// </param>
    public static ReactiveEffect Effect(Action effect, EffectOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(effect);

        return SyncReactiveEffect.Start(effect, options);
    }

    /// <summary>
    /// Runs the asynchronous <paramref name="effect"/> now and again whenever reactive state it read before its first
    /// incomplete <c>await</c> changes, until disposed. Reads after that point are not tracked.
    /// </summary>
    /// <remarks>
    /// Runs never overlap. A change arriving during a run cancels the run's token, and the next run starts once the
    /// current one has completed; changes arriving meanwhile coalesce into that one run. A slow run therefore cannot
    /// overwrite the writes of a newer one, as long as it observes its token. The first run starts on the calling
    /// thread; later runs start on the thread pool, or through <see cref="EffectOptions.Dispatcher"/>, and continue
    /// wherever their awaits resume. Disposing the effect cancels the run in progress, unless
    /// <see cref="EffectOptions.FlushOnDispose"/> is set.
    /// </remarks>
    /// <param name="effect">
    /// The code to run, with a token cancelled when the run is superseded or the effect is disposed. It may write
    /// reactive state; the writes of its synchronous part are batched.
    /// </param>
    /// <param name="options">
    /// Error handling, dispatcher and timing. <see cref="EffectOptions.OnError"/> receives the failures of runs,
    /// including a cancellation the effect did not request; without it they are reported through
    /// <see cref="UnhandledEffectException"/>. The outcome of a cancelled run is never reported.
    /// </param>
    public static ReactiveEffect Effect(Func<CancellationToken, Task> effect, EffectOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(effect);

        return AsyncReactiveEffect.Start(effect, options);
    }

    /// <summary>
    /// Asynchronous effect that does not need a cancellation token; see
    /// <see cref="Effect(Func{CancellationToken, Task}, EffectOptions?)"/>. Also keeps an <c>async () =&gt;</c> lambda
    /// from becoming an <c>async void</c> synchronous effect.
    /// </summary>
    public static ReactiveEffect Effect(Func<Task> effect, EffectOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(effect);

        return AsyncReactiveEffect.Start(_ => effect(), options);
    }

    /// <summary>
    /// Calls <paramref name="onChange"/> with the new and the previous value whenever the value of
    /// <paramref name="source"/> changes, until disposed. Unlike an effect it does not act on the current value: the
    /// first call follows the first change.
    /// <code>
    /// Reactive.Watch(() => Auth.CurrentUser, (user, previous) => Cart.Clear());
    /// </code>
    /// </summary>
    /// <remarks>
    /// Only reads made by <paramref name="source"/> are tracked; <paramref name="onChange"/> runs untracked and may
    /// write reactive state. It is an effect that reads <paramref name="source"/>: re-runs happen on the thread pool, or
    /// through <see cref="EffectOptions.Dispatcher"/>, coalesced, and <see cref="EffectOptions.Debounce"/> or
    /// <see cref="EffectOptions.Throttle"/> delay them. Values the source had only between two runs are never seen, so
    /// the previous value is the one passed last time, not necessarily the one just before.
    /// </remarks>
    /// <param name="source">Computes the watched value; reactive state it reads is tracked. It runs once immediately.</param>
    /// <param name="onChange">Receives the new value, then the previous one.</param>
    /// <param name="options">Error handling, dispatcher and timing, as for <see cref="Effect(Action, EffectOptions?)"/>.</param>
    /// <param name="comparer">Decides whether the value changed; <see cref="EqualityComparer{T}.Default"/> by default.</param>
    public static ReactiveEffect Watch<T>(
        Func<T> source,
        Action<T, T> onChange,
        EffectOptions? options = null,
        IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onChange);

        var watched = new WatchedSource<T>(source, comparer);

        return SyncReactiveEffect.Start(
            () =>
            {
                if (!watched.TryChange(out var value, out var previous))
                {
                    return;
                }

                using (ReactiveObserver.SuspendTracking())
                {
                    onChange(value, previous);
                }
            },
            options);
    }

    /// <summary>
    /// Asynchronous form of <see cref="Watch{T}(Func{T}, Action{T, T}, EffectOptions?, IEqualityComparer{T}?)"/>, run
    /// like an asynchronous effect: calls never overlap, and a change during a call cancels its token, then the next
    /// call follows once it has completed. Disposing the watch cancels the call in progress, unless
    /// <see cref="EffectOptions.FlushOnDispose"/> is set.
    /// </summary>
    /// <param name="source">Computes the watched value; reactive state it reads is tracked. It runs once immediately.</param>
    /// <param name="onChange">
    /// Receives the new value, the previous one, and a token cancelled when the call is superseded or the watch is
    /// disposed.
    /// </param>
    /// <param name="options">Error handling, dispatcher and timing, as for <see cref="Effect(Func{CancellationToken, Task}, EffectOptions?)"/>.</param>
    /// <param name="comparer">Decides whether the value changed; <see cref="EqualityComparer{T}.Default"/> by default.</param>
    public static ReactiveEffect Watch<T>(
        Func<T> source,
        Func<T, T, CancellationToken, Task> onChange,
        EffectOptions? options = null,
        IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onChange);

        var watched = new WatchedSource<T>(source, comparer);

        return AsyncReactiveEffect.Start(
            token =>
            {
                if (!watched.TryChange(out var value, out var previous))
                {
                    return Task.CompletedTask;
                }

                using (ReactiveObserver.SuspendTracking())
                {
                    return onChange(value, previous, token);
                }
            },
            options);
    }

    /// <summary>
    /// Asynchronous watch that does not need a cancellation token; see
    /// <see cref="Watch{T}(Func{T}, Func{T, T, CancellationToken, Task}, EffectOptions?, IEqualityComparer{T}?)"/>.
    /// Also keeps an <c>async (value, previous) =&gt;</c> lambda from becoming an <c>async void</c> handler.
    /// </summary>
    public static ReactiveEffect Watch<T>(
        Func<T> source,
        Func<T, T, Task> onChange,
        EffectOptions? options = null,
        IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(onChange);

        return Watch(source, (value, previous, _) => onChange(value, previous), options, comparer);
    }

    /// <summary>
    /// Calls <paramref name="onChanges"/> with what changed in a reactive collection, in order, until disposed:
    /// <code>
    /// Reactive.Watch(State.Items.Changes, changes =&gt; audit.Record(changes));
    /// </code>
    /// Each call receives every change made since the previous one: none is dropped or merged. The changes of a
    /// <see cref="Batch"/> or <see cref="BatchAsync"/> arrive together, once it has ended.
    /// </summary>
    /// <remarks>
    /// It is an effect that reads nothing but the changes: calls happen on the thread pool, or through
    /// <see cref="EffectOptions.Dispatcher"/>, never concurrently, and changes arriving before a call starts join it.
    /// <see cref="EffectOptions.Debounce"/> and <see cref="EffectOptions.Throttle"/> delay calls and
    /// <see cref="EffectOptions.FlushOnDispose"/> delivers the changes still pending at disposal. Only changes made
    /// after this call are delivered, and none after disposal. <paramref name="onChanges"/> runs untracked and may
    /// write reactive state, including the collection: its own changes are delivered in a later call.
    /// </remarks>
    /// <param name="changes">The collection's <c>Changes</c>, for example <see cref="ReactiveList{T}.Changes"/>.</param>
    /// <param name="onChanges">Receives the changes, never an empty list.</param>
    /// <param name="options">Error handling, dispatcher and timing, as for <see cref="Effect(Action, EffectOptions?)"/>.</param>
    public static ReactiveEffect Watch<TChange>(
        CollectionChanges<TChange> changes,
        Action<IReadOnlyList<TChange>> onChanges,
        EffectOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(onChanges);

        return changes.Watch(onChanges, options);
    }

    internal static void ReportUnhandledEffectException(Exception exception)
    {
        var handler = UnhandledEffectException;
        if (handler is null)
        {
            Trace.TraceError($"Unhandled exception in a reactive effect: {exception}");
            return;
        }

        handler(exception);
    }

    /// <summary>Collects the nodes read by <see cref="Invalidate{T}"/>; it never reacts itself.</summary>
    private sealed class DependencyCollector : ReactiveObserver
    {
        protected override void OnInvalidated()
        {
        }
    }
}

internal static class ReactiveBatch
{
    [ThreadStatic]
    private static int t_depth;

    [ThreadStatic]
    private static HashSet<ReactiveNode>? t_pending;

    [ThreadStatic]
    private static BatchCommit? t_commit;

    private static readonly AsyncLocal<AsyncBatch?> s_asyncBatch = new();

    // Lets every notification skip the AsyncLocal lookup while no asynchronous batch exists anywhere.
    private static int s_activeAsyncBatches;

    internal static bool TryDefer(ReactiveNode node)
    {
        // The asynchronous batch wins over a synchronous one nested inside it, so that its writes are
        // released together with the rest of the asynchronous flow.
        if (Volatile.Read(ref s_activeAsyncBatches) > 0)
        {
            var asyncBatch = s_asyncBatch.Value;
            if (asyncBatch is not null && asyncBatch.TryAdd(node))
            {
                return true;
            }
        }

        if (t_depth == 0)
        {
            return false;
        }

        (t_pending ??= []).Add(node);

        return true;
    }

    /// <summary>
    /// The batch that would defer a notification raised here now, as a <see cref="BatchCommit"/> committed when it
    /// ends, before its notifications are released; null outside any batch. Mirrors <see cref="TryDefer"/>.
    /// </summary>
    internal static BatchCommit? CurrentCommit()
    {
        if (Volatile.Read(ref s_activeAsyncBatches) > 0)
        {
            var asyncBatch = s_asyncBatch.Value;
            if (asyncBatch is not null && asyncBatch.TryGetCommit(out var commit))
            {
                return commit;
            }
        }

        if (t_depth == 0)
        {
            return null;
        }

        return t_commit ??= new BatchCommit();
    }

    internal static void Enter() => t_depth++;

    internal static void Exit()
    {
        if (--t_depth > 0)
        {
            return;
        }

        var commit = t_commit;
        if (commit is not null)
        {
            t_commit = null;
            commit.Commit();
        }

        var pending = t_pending;
        if (pending is null || pending.Count == 0)
        {
            return;
        }

        ReactiveNode[] nodes = [.. pending];
        pending.Clear();

        Release(nodes);
    }

    internal static async Task RunAsync(Func<Task> action)
    {
        var current = s_asyncBatch.Value;
        if (current is not null && !current.IsCompleted)
        {
            await action().ConfigureAwait(false);
            return;
        }

        var batch = new AsyncBatch();
        Interlocked.Increment(ref s_activeAsyncBatches);
        s_asyncBatch.Value = batch;

        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            s_asyncBatch.Value = null;
            Interlocked.Decrement(ref s_activeAsyncBatches);

            Release(batch.Complete());
        }
    }

    private static void Release(ReactiveNode[] nodes)
    {
        if (nodes.Length == 0)
        {
            return;
        }

        var observers = new HashSet<ReactiveObserver>();
        foreach (var node in nodes)
        {
            node.CollectObservers(observers);
        }

        ReactiveObserver.InvalidateAll([.. observers]);
    }

    /// <summary>
    /// Pending notifications of one <see cref="Reactive.BatchAsync"/>. Continuations of its flow may run on several
    /// threads at once; work that outlives the batch (fire-and-forget tasks) notifies directly once it completed.
    /// </summary>
    private sealed class AsyncBatch
    {
        private readonly Lock _gate = new();
        private readonly HashSet<ReactiveNode> _pending = [];
        private BatchCommit? _commit;
        private bool _completed;

        public bool IsCompleted
        {
            get
            {
                lock (_gate)
                {
                    return _completed;
                }
            }
        }

        public bool TryAdd(ReactiveNode node)
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return false;
                }

                _pending.Add(node);

                return true;
            }
        }

        public bool TryGetCommit([NotNullWhen(true)] out BatchCommit? commit)
        {
            lock (_gate)
            {
                if (_completed)
                {
                    commit = null;
                    return false;
                }

                commit = _commit ??= new BatchCommit();

                return true;
            }
        }

        public ReactiveNode[] Complete()
        {
            lock (_gate)
            {
                _completed = true;
                _commit?.Commit();
                ReactiveNode[] nodes = [.. _pending];
                _pending.Clear();

                return nodes;
            }
        }
    }
}

/// <summary>
/// Marks the changes recorded during one batch (see <see cref="CollectionChanges{TChange}"/>) as deliverable: they
/// must not reach a listener before the batch ends, together with the batch's notifications.
/// </summary>
internal sealed class BatchCommit
{
    private volatile bool _committed;

    public bool IsCommitted => _committed;

    public void Commit() => _committed = true;
}
