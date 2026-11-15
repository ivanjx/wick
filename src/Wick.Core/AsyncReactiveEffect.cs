namespace Wick.Core;

/// <summary>
/// An effect whose runs are asynchronous. Only reads made before the run's first incomplete <c>await</c> are tracked.
/// A change arriving while a run is in progress cancels that run's token; the next run starts once it completed, so
/// runs never overlap and their writes land in order.
/// </summary>
internal sealed class AsyncReactiveEffect : ReactiveEffect
{
    // Flows into the run's continuations, so that writes made after an await are recognized as the effect's own.
    private static readonly AsyncLocal<AsyncReactiveEffect?> s_running = new();

    private readonly Func<CancellationToken, Task> _effect;
    private readonly Lock _gate = new();

    // The token source of the run in progress. Never disposed: the callbacks of a cancellation run on the thread
    // pool and may still be executing, and a source without a timer or linked token holds nothing to release.
    private CancellationTokenSource? _cancellation;

    // The first run's self-invalidation is not counted, like a synchronous effect's.
    private int _selfTriggeredRuns = -1;

    private AsyncReactiveEffect(Func<CancellationToken, Task> effect, Action<Exception>? onError, Func<Func<Task>, Task>? dispatcher)
        : base(onError, dispatcher)
    {
        _effect = effect;
    }

    private protected override bool IsOwnWrite => s_running.Value == this;

    internal static ReactiveEffect Start(Func<CancellationToken, Task> effect, Action<Exception>? onError, Func<Func<Task>, Task>? dispatcher)
    {
        var instance = new AsyncReactiveEffect(effect, onError, dispatcher);

        // Like a synchronous effect, the first run starts on the calling thread.
        PendingWork.Add();
        instance.RunScheduled();

        return instance;
    }

    private protected override void OnRerunRequested() => CancelRun();

    private protected override void OnDisposed() => CancelRun();

    private protected override void RunScheduled()
    {
        if (IsDisposed)
        {
            StopRunning();
            PendingWork.Remove();

            return;
        }

        var cancellation = new CancellationTokenSource();
        var run = Execute(cancellation);

        if (run.IsCompleted)
        {
            Settle(run, cancellation);
            return;
        }

        _ = SettleWhenCompletedAsync(run, cancellation);
    }

    /// <summary>Starts a run; returns once its synchronous part, which is tracked, has ended.</summary>
    private Task Execute(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            // Dispose may have looked for a run to cancel before this one was published.
            if (IsDisposed)
            {
                return Task.CompletedTask;
            }

            _cancellation = cancellation;
        }

        SelfInvalidated = false;

        var previous = s_running.Value;
        s_running.Value = this;

        // Batch the writes of the synchronous part, like a synchronous effect's; later writes notify right away.
        ReactiveBatch.Enter();

        try
        {
            Task? run;
            using (BeginTracking())
            {
                run = _effect(cancellation.Token);
            }

            return run ?? Task.FromException(new InvalidOperationException("The asynchronous effect returned null instead of a task."));
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
        finally
        {
            try
            {
                ReactiveBatch.Exit();
            }
            finally
            {
                // Restored here because this method is synchronous; the run's continuations keep the value they captured.
                s_running.Value = previous;
            }
        }
    }

    private async Task SettleWhenCompletedAsync(Task run, CancellationTokenSource cancellation)
    {
        await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        Settle(run, cancellation);
    }

    private void Settle(Task run, CancellationTokenSource cancellation)
    {
        try
        {
            lock (_gate)
            {
                if (_cancellation == cancellation)
                {
                    _cancellation = null;
                }
            }

            // A run cancelled because it was superseded or the effect was disposed may end in any way, for example
            // with an exception other than OperationCanceledException; only the outcome of the others is reported.
            if (!run.IsCompletedSuccessfully && !cancellation.IsCancellationRequested)
            {
                Report(run.IsCanceled ? new TaskCanceledException(run) : Unwrap(run.Exception!));
            }

            var selfTriggeredRuns = SelfInvalidated ? _selfTriggeredRuns + 1 : 0;
            _selfTriggeredRuns = 0;

            if (Complete(selfTriggeredRuns))
            {
                _selfTriggeredRuns = selfTriggeredRuns;
                Schedule();
            }
        }
        finally
        {
            // After scheduling the next run: idle waiters see no gap.
            PendingWork.Remove();
        }
    }

    private void CancelRun()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _cancellation;
        }

        if (cancellation is null)
        {
            return;
        }

        // Asynchronously: callbacks registered on the token are user code and must not run on the thread that changed
        // the state or disposed the effect. Their exceptions are ignored, like the outcome of the cancelled run.
        var cancelled = cancellation.CancelAsync();
        if (!cancelled.IsCompletedSuccessfully)
        {
            cancelled.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static Exception Unwrap(AggregateException exception)
        => exception.InnerExceptions.Count == 1 ? exception.InnerExceptions[0] : exception;
}
