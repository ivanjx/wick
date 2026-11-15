using System.Runtime.ExceptionServices;

namespace Wick.Core;

/// <summary>
/// Side effect that re-runs when reactive state it read changes; created with <see cref="Reactive.Effect"/>.
/// Re-runs happen on the thread pool, never concurrently with each other, and changes made while a run is in
/// progress coalesce into one more run. Dispose to stop it: the effect is referenced by the state it reads and
/// lives as long as that state otherwise.
/// </summary>
/// <remarks>
/// Unlike renders and derived values, an effect may write reactive state; its writes are batched per run. An effect
/// that keeps changing what it reads is stopped after <see cref="MaxConsecutiveSelfTriggeredRuns"/> consecutive
/// self-triggered runs and reports an <see cref="InvalidOperationException"/>; it runs again on the next outside change.
/// </remarks>
public sealed class ReactiveEffect : IDisposable, IReactiveObserverOwner
{
    public const int MaxConsecutiveSelfTriggeredRuns = 100;

    private const int Idle = 0;
    private const int Running = 1;
    private const int RerunRequested = 2;

    [ThreadStatic]
    private static ReactiveEffect? t_running;

    private readonly Action _effect;
    private readonly Action<Exception>? _onError;
    private readonly EffectObserver _observer;
    private int _state = Running;
    private volatile bool _selfInvalidated;

    private ReactiveEffect(Action effect, Action<Exception>? onError)
    {
        _effect = effect;
        _onError = onError;
        _observer = new EffectObserver(this);
    }

    internal int DependencyCount => _observer.DependencyCount;

    ReactiveObserver IReactiveObserverOwner.Observer => _observer;

    public void Dispose() => _observer.Dispose();

    internal static ReactiveEffect Start(Action effect, Action<Exception>? onError)
    {
        var instance = new ReactiveEffect(effect, onError);

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

    private void Schedule()
    {
        PendingWork.Add();
        ThreadPool.UnsafeQueueUserWorkItem(static effect => effect.RunPending(), this, preferLocal: false);
    }

    private void RunPending()
    {
        var selfTriggeredRuns = 0;

        try
        {
            do
            {
                if (_observer.Disposed)
                {
                    Volatile.Write(ref _state, Idle);
                    return;
                }

                var error = Execute();
                if (error is not null)
                {
                    Report(error.SourceException);
                }

                selfTriggeredRuns = _selfInvalidated ? selfTriggeredRuns + 1 : 0;
            }
            while (Complete(selfTriggeredRuns));
        }
        finally
        {
            // After the runs: renders and effects they triggered were counted first, so idle waiters see no gap.
            PendingWork.Remove();
        }
    }

    /// <summary>Ends a run. Returns true when another run was requested meanwhile and should follow.</summary>
    private bool Complete(int selfTriggeredRuns)
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

    private ExceptionDispatchInfo? Execute()
    {
        var previous = t_running;
        t_running = this;
        _selfInvalidated = false;

        // Batch the run's own writes: their notifications, including the ones that re-trigger this effect,
        // are delivered once the run and its tracking scope have ended.
        ReactiveBatch.Enter();

        try
        {
            using (_observer.BeginTracking())
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

    private void Report(Exception exception)
    {
        if (_onError is null)
        {
            Reactive.ReportUnhandledEffectException(exception);
            return;
        }

        _onError(exception);
    }

    private void OnInvalidated()
    {
        if (t_running == this)
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
                        return;
                    }

                    break;

                default:
                    return;
            }
        }
    }

    private sealed class EffectObserver(ReactiveEffect owner) : ReactiveObserver
    {
        internal bool Disposed => IsDisposed;

        internal override bool AllowsMutation => true;

        protected override void OnInvalidated() => owner.OnInvalidated();
    }
}
