using System.Diagnostics;

namespace Wick.Core;

public static class Reactive
{
    /// <summary>
    /// Raised when a <see cref="ReactiveEffect"/> created without an error handler throws while re-running.
    /// Without a subscriber the exception is written to <see cref="Trace"/>; it is never rethrown on the thread pool.
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
    /// Runs <paramref name="effect"/> now and again whenever reactive state it read changes, until disposed.
    /// The first run is synchronous on the calling thread; re-runs happen on the thread pool, coalesced.
    /// </summary>
    /// <param name="onError">
    /// Receives exceptions thrown by any run. Without it, the first run's exception propagates to the caller and
    /// later ones are reported through <see cref="UnhandledEffectException"/>.
    /// </param>
    public static ReactiveEffect Effect(Action effect, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(effect);

        return ReactiveEffect.Start(effect, onError);
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
