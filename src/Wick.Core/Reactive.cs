using System.Diagnostics;

namespace Wick.Core;

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

    internal static void Enter() => t_depth++;

    internal static void Exit()
    {
        var pending = t_pending;
        if (--t_depth > 0 || pending is null || pending.Count == 0)
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

        public ReactiveNode[] Complete()
        {
            lock (_gate)
            {
                _completed = true;
                ReactiveNode[] nodes = [.. _pending];
                _pending.Clear();

                return nodes;
            }
        }
    }
}
