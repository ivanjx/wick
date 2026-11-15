using System.Runtime.ExceptionServices;

namespace Wick.Core;

/// <summary>
/// Side effect that re-runs when reactive state it read changes; created with <see cref="Reactive.Effect(Action, Action{Exception}?, Func{Func{Task}, Task}?)"/>,
/// or <see cref="Reactive.Effect(Func{CancellationToken, Task}, Action{Exception}?, Func{Func{Task}, Task}?)"/> for asynchronous work.
/// Re-runs happen on the thread pool, or through the dispatcher given at creation, never concurrently with each other, and changes made while a
/// run is in progress coalesce into one more run. Dispose to stop it: the effect is referenced by the state it reads and lives as long as that
/// state otherwise.
/// </summary>
/// <remarks>
/// Unlike renders and derived values, an effect may write reactive state. An effect that keeps changing what it reads is stopped after
/// <see cref="MaxConsecutiveSelfTriggeredRuns"/> consecutive self-triggered runs and reports an <see cref="InvalidOperationException"/>; it runs
/// again on the next outside change.
/// </remarks>
public abstract class ReactiveEffect : IDisposable, IReactiveObserverOwner
{
    public const int MaxConsecutiveSelfTriggeredRuns = 100;

    private const int Idle = 0;
    private const int Running = 1;
    private const int RerunRequested = 2;

    private readonly Action<Exception>? _onError;
    private readonly Func<Func<Task>, Task>? _dispatcher;
    private readonly EffectObserver _observer;

    // The creating thread runs the first run.
    private int _state = Running;
    private volatile bool _selfInvalidated;

    private protected ReactiveEffect(Action<Exception>? onError, Func<Func<Task>, Task>? dispatcher)
    {
        _onError = onError;
        _dispatcher = dispatcher;
        _observer = new EffectObserver(this);
    }

    internal int DependencyCount => _observer.DependencyCount;

    ReactiveObserver IReactiveObserverOwner.Observer => _observer;

    private protected bool IsDisposed => _observer.Disposed;

    /// <summary>Set when the current run changed state it read; reset by each run when it starts.</summary>
    private protected bool SelfInvalidated
    {
        get => _selfInvalidated;
        set => _selfInvalidated = value;
    }

    /// <summary>True when the invalidation being delivered on this thread comes from this effect's own run.</summary>
    private protected abstract bool IsOwnWrite { get; }

    public void Dispose()
    {
        _observer.Dispose();
        OnDisposed();
    }

    private protected virtual void OnDisposed()
    {
    }

    /// <summary>Called when a change arrives while a run is in progress; the next run follows once it completed.</summary>
    private protected virtual void OnRerunRequested()
    {
    }

    /// <summary>
    /// Performs the scheduled run, on the thread pool or through the dispatcher. It must end every run with
    /// <see cref="Complete"/> and then release the pending work counted by <see cref="Schedule"/>.
    /// </summary>
    private protected abstract void RunScheduled();

    private protected TrackingScope BeginTracking() => _observer.BeginTracking();

    private protected void Schedule()
    {
        PendingWork.Add();
        ThreadPool.UnsafeQueueUserWorkItem(static effect => effect.Dispatch(), this, preferLocal: false);
    }

    /// <summary>Marks a run that found the effect disposed as finished.</summary>
    private protected void StopRunning() => Volatile.Write(ref _state, Idle);

    /// <summary>Ends a run. Returns true when another run was requested meanwhile and should follow.</summary>
    private protected bool Complete(int selfTriggeredRuns)
    {
        if (Interlocked.CompareExchange(ref _state, Idle, Running) == Running)
        {
            return false;
        }

        if (selfTriggeredRuns >= MaxConsecutiveSelfTriggeredRuns)
        {
            Volatile.Write(ref _state, Idle);
            Report(new InvalidOperationException(
                $"The reactive effect invalidated itself {selfTriggeredRuns} times in a row and was stopped; it runs again when state it reads changes elsewhere."));

            return false;
        }

        // Invalidations arriving from now on request yet another run.
        Volatile.Write(ref _state, Running);

        return true;
    }

    private protected void Report(Exception exception)
    {
        if (_onError is null)
        {
            Reactive.ReportUnhandledEffectException(exception);
            return;
        }

        _onError(exception);
    }

    private void Dispatch()
    {
        if (_dispatcher is null)
        {
            RunScheduled();
            return;
        }

        var run = new DispatchedRun(this);

        Task dispatched;
        try
        {
            dispatched = _dispatcher(run.Start);
        }
        catch (Exception exception)
        {
            dispatched = Task.FromException(exception);
        }

        if (!dispatched.IsCompletedSuccessfully)
        {
            _ = run.ObserveAsync(dispatched);
        }
    }

    /// <summary>Called when the dispatcher failed without starting the run, for example because its renderer is gone.</summary>
    private void OnDispatchRefused(Task dispatched)
    {
        // The changes that requested this run are dropped with it; the next change dispatches again.
        Volatile.Write(ref _state, Idle);

        if (!IsDisposed)
        {
            Report(dispatched.Exception?.InnerException ?? new TaskCanceledException(dispatched));
        }

        PendingWork.Remove();
    }

    private void OnInvalidated()
    {
        if (IsOwnWrite)
        {
            _selfInvalidated = true;
        }

        while (true)
        {
            switch (Volatile.Read(ref _state))
            {
                case Idle:
                    if (Interlocked.CompareExchange(ref _state, Running, Idle) == Idle)
                    {
                        Schedule();
                        return;
                    }

                    break;

                case Running:
                    if (Interlocked.CompareExchange(ref _state, RerunRequested, Running) == Running)
                    {
                        OnRerunRequested();
                        return;
                    }

                    break;

                default:
                    return;
            }
        }
    }

    /// <summary>One run handed to the dispatcher.</summary>
    private sealed class DispatchedRun(ReactiveEffect owner)
    {
        private volatile bool _started;

        public Task Start()
        {
            _started = true;
            owner.RunScheduled();

            return Task.CompletedTask;
        }

        public async Task ObserveAsync(Task dispatched)
        {
            await dispatched.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            // A dispatcher may also queue the run and complete right away; only a failure before the start loses it.
            if (_started || dispatched.IsCompletedSuccessfully)
            {
                return;
            }

            owner.OnDispatchRefused(dispatched);
        }
    }

    private sealed class EffectObserver(ReactiveEffect owner) : ReactiveObserver
    {
        internal bool Disposed => IsDisposed;

        internal override bool AllowsMutation => true;

        protected override void OnInvalidated() => owner.OnInvalidated();
    }
}

/// <summary>An effect whose runs are synchronous; its writes are batched per run.</summary>
internal sealed class SyncReactiveEffect : ReactiveEffect
{
    [ThreadStatic]
    private static SyncReactiveEffect? t_running;

    private readonly Action _effect;

    private SyncReactiveEffect(Action effect, Action<Exception>? onError, Func<Func<Task>, Task>? dispatcher)
        : base(onError, dispatcher)
    {
        _effect = effect;
    }

    private protected override bool IsOwnWrite => t_running == this;

    internal static ReactiveEffect Start(Action effect, Action<Exception>? onError, Func<Func<Task>, Task>? dispatcher)
    {
        var instance = new SyncReactiveEffect(effect, onError, dispatcher);

        var error = instance.Execute();
        if (error is not null)
        {
            if (onError is null)
            {
                instance.Dispose();
                error.Throw();
            }

            onError(error.SourceException);
        }

        if (instance.Complete(selfTriggeredRuns: 0))
        {
            instance.Schedule();
        }

        return instance;
    }

    private protected override void RunScheduled()
    {
        var selfTriggeredRuns = 0;

        try
        {
            do
            {
                if (IsDisposed)
                {
                    StopRunning();
                    return;
                }

                var error = Execute();
                if (error is not null)
                {
                    Report(error.SourceException);
                }

                selfTriggeredRuns = SelfInvalidated ? selfTriggeredRuns + 1 : 0;
            }
            while (Complete(selfTriggeredRuns));
        }
        finally
        {
            // After the runs: renders and effects they triggered were counted first, so idle waiters see no gap.
            PendingWork.Remove();
        }
    }

    private ExceptionDispatchInfo? Execute()
    {
        var previous = t_running;
        t_running = this;
        SelfInvalidated = false;

        // Batch the run's own writes: their notifications, including the ones that re-trigger this effect,
        // are delivered once the run and its tracking scope have ended.
        ReactiveBatch.Enter();

        try
        {
            using (BeginTracking())
            {
                _effect();
            }

            return null;
        }
        catch (Exception exception)
        {
            return ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            try
            {
                ReactiveBatch.Exit();
            }
            finally
            {
                t_running = previous;
            }
        }
    }
}
