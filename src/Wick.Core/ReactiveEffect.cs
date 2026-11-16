using System.Runtime.ExceptionServices;

namespace Wick.Core;

/// <summary>
/// Side effect that re-runs when reactive state it read changes; created with <see cref="Reactive.Effect(Action, EffectOptions?)"/>,
/// or <see cref="Reactive.Effect(Func{CancellationToken, Task}, EffectOptions?)"/> for asynchronous work. Re-runs happen on the thread
/// pool, or through <see cref="EffectOptions.Dispatcher"/>, never concurrently with each other, and changes made while a run is in progress
/// or delayed coalesce into one more run. Dispose to stop it: the effect is referenced by the state it reads and lives as long as that
/// state otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Unlike renders and derived values, an effect may write reactive state. An effect that keeps changing what it reads is stopped after
/// <see cref="MaxConsecutiveSelfTriggeredRuns"/> consecutive self-triggered runs and reports an <see cref="InvalidOperationException"/>; it runs
/// again on the next outside change.
/// </para>
/// <para>
/// <see cref="EffectOptions.Debounce"/> and <see cref="EffectOptions.Throttle"/> delay re-runs, never the first run. <see cref="FlushAsync"/>
/// starts a delayed run right away.
/// </para>
/// </remarks>
public abstract class ReactiveEffect : IDisposable, IReactiveObserverOwner
{
    public const int MaxConsecutiveSelfTriggeredRuns = 100;

    private const int Idle = 0;
    private const int Running = 1;
    private const int RerunRequested = 2;

    // A re-run is due once its debounce or throttle delay has passed.
    private const int Waiting = 3;

    private static readonly EffectOptions s_defaultOptions = new();

    // The longest due time a timer accepts.
    private static readonly TimeSpan s_maxDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly Action<Exception>? _onError;
    private readonly Func<Func<Task>, Task>? _dispatcher;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _throttle;
    private readonly bool _flushOnDispose;
    private readonly TimeProvider _timeProvider;
    private readonly EffectObserver _observer;

    // Only for debounced and throttled effects. Never disposed: arming it may race with Dispose, every callback checks
    // the state first, and an unarmed timer holds nothing.
    private readonly ITimer? _timer;

    // The creating thread runs the first run.
    private int _state = Running;
    private volatile bool _selfInvalidated;
    private volatile bool _flushRequested;
    private long _lastChange;
    private long _lastRunStart;

    // Guarded by the private observer, which never locks itself.
    private long _completedRuns;
    private List<(long Target, TaskCompletionSource Done)>? _flushWaiters;

    private protected ReactiveEffect(EffectOptions? options)
    {
        options ??= s_defaultOptions;

        ThrowIfInvalidDelay(options.Debounce, nameof(EffectOptions.Debounce));
        ThrowIfInvalidDelay(options.Throttle, nameof(EffectOptions.Throttle));

        if (options.Debounce > TimeSpan.Zero && options.Throttle > TimeSpan.Zero)
        {
            throw new ArgumentException("An effect can be debounced or throttled, not both.", nameof(options));
        }

        _onError = options.OnError;
        _dispatcher = options.Dispatcher;
        _debounce = options.Debounce;
        _throttle = options.Throttle;
        _flushOnDispose = options.FlushOnDispose;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _observer = new EffectObserver(this);

        if (_debounce > TimeSpan.Zero || _throttle > TimeSpan.Zero)
        {
            _timer = CreateTimer();
            _lastRunStart = _timeProvider.GetTimestamp();
        }
    }

    internal int DependencyCount => _observer.DependencyCount;

    ReactiveObserver IReactiveObserverOwner.Observer => _observer;

    private protected bool IsDisposed => _observer.Disposed;

    /// <summary>True once disposed without <see cref="EffectOptions.FlushOnDispose"/>: no run may start any more.</summary>
    private protected bool IsStopped => _observer.Disposed && !_flushOnDispose;

    private protected bool FlushOnDispose => _flushOnDispose;

    /// <summary>Set when the current run changed state it read; reset by each run when it starts.</summary>
    private protected bool SelfInvalidated
    {
        get => _selfInvalidated;
        set => _selfInvalidated = value;
    }

    /// <summary>True when the invalidation being delivered on this thread comes from this effect's own run.</summary>
    private protected abstract bool IsOwnWrite { get; }

    /// <summary>
    /// Stops the effect. A delayed run, or one requested by a change during the run in progress, is dropped, and an
    /// asynchronous run in progress is cancelled; with <see cref="EffectOptions.FlushOnDispose"/> both still complete.
    /// </summary>
    public void Dispose()
    {
        // From here on no change requests a run.
        _observer.Dispose();

        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ResolveDelayedRunAfterDispose();

        OnDisposed();
    }

    /// <summary>
    /// Starts a run delayed by <see cref="EffectOptions.Debounce"/> or <see cref="EffectOptions.Throttle"/> right away,
    /// or lets a run requested by a change during the run in progress follow without delay. The task completes once
    /// the runs pending at the call have ended, failed or been superseded; failures are reported as usual, not through
    /// the task. Completes immediately when nothing is pending.
    /// </summary>
    public Task FlushAsync()
    {
        while (true)
        {
            var completedRuns = Interlocked.Read(ref _completedRuns);

            switch (Volatile.Read(ref _state))
            {
                case Idle:
                    return Task.CompletedTask;

                case Waiting:
                    if (Interlocked.CompareExchange(ref _state, Running, Waiting) == Waiting)
                    {
                        // Registered first: the run may end before Dispatch returns.
                        var flushed = WaitForRuns(completedRuns + 1);
                        Dispatch();

                        return flushed;
                    }

                    break;

                case Running:
                    return WaitForRuns(completedRuns + 1);

                default:
                    _flushRequested = true;

                    return WaitForRuns(completedRuns + 2);
            }
        }
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

    /// <summary>Tracks the run's reads; once disposed, a final flushed run is not tracked.</summary>
    private protected TrackingScope BeginTracking() => _observer.BeginTrackingUnlessDisposed();

    private protected void Schedule()
    {
        PendingWork.Add();
        ThreadPool.UnsafeQueueUserWorkItem(static effect => effect.Dispatch(), this, preferLocal: false);
    }

    /// <summary>Marks a run that found the effect disposed as finished.</summary>
    private protected void StopRunning() => BecomeIdle();

    /// <summary>
    /// Ends a run. Returns true when another run was requested meanwhile and should start now; a debounced or
    /// throttled one is delayed instead, and this method schedules it.
    /// </summary>
    private protected bool Complete(int selfTriggeredRuns)
    {
        CountCompletedRun();

        if (Interlocked.CompareExchange(ref _state, Idle, Running) == Running)
        {
            ReleaseFlushWaiters();
            return false;
        }

        if (selfTriggeredRuns >= MaxConsecutiveSelfTriggeredRuns)
        {
            BecomeIdle();
            Report(new InvalidOperationException(
                $"The reactive effect invalidated itself {selfTriggeredRuns} times in a row and was stopped; it runs again when state it reads changes elsewhere."));

            return false;
        }

        // Invalidations arriving from now on request yet another run.
        Volatile.Write(ref _state, Running);

        if (TryDelay())
        {
            return false;
        }

        MarkRunStart();

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

    private static void ThrowIfInvalidDelay(TimeSpan delay, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delay, s_maxDelay, name);
    }

    private ITimer CreateTimer()
    {
        // Timer callbacks must not inherit the creator's asynchronous context, such as an enclosing BatchAsync.
        if (ExecutionContext.IsFlowSuppressed())
        {
            return _timeProvider.CreateTimer(static effect => ((ReactiveEffect)effect!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        using (ExecutionContext.SuppressFlow())
        {
            return _timeProvider.CreateTimer(static effect => ((ReactiveEffect)effect!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void MarkRunStart()
    {
        if (_timer is not null)
        {
            Interlocked.Exchange(ref _lastRunStart, _timeProvider.GetTimestamp());
        }
    }

    /// <summary>Time left before a delayed run is due: since the last change when debounced, since the last run's start when throttled.</summary>
    private TimeSpan RemainingDelay()
        => _debounce > TimeSpan.Zero
            ? _debounce - _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastChange))
            : _throttle - _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastRunStart));

    /// <summary>
    /// Delays the run that the caller, owning the state as <see cref="Running"/>, is about to start until it is due.
    /// Returns false when it should start now.
    /// </summary>
    private bool TryDelay()
    {
        if (_timer is null || IsDisposed)
        {
            return false;
        }

        if (_flushRequested)
        {
            _flushRequested = false;
            return false;
        }

        var remaining = RemainingDelay();
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        PendingWork.Add();
        Volatile.Write(ref _state, Waiting);
        _timer.Change(remaining, Timeout.InfiniteTimeSpan);

        // Dispose may have looked for a delayed run before this one was published.
        if (IsDisposed)
        {
            ResolveDelayedRunAfterDispose();
        }

        return true;
    }

    private void OnTimer()
    {
        if (Volatile.Read(ref _state) != Waiting)
        {
            return;
        }

        // A debounced effect that changed again since the timer was armed waits for the rest of its quiet period.
        var remaining = RemainingDelay();
        if (remaining > TimeSpan.Zero)
        {
            _timer!.Change(remaining, Timeout.InfiniteTimeSpan);
            return;
        }

        if (Interlocked.CompareExchange(ref _state, Running, Waiting) == Waiting)
        {
            Dispatch();
        }
    }

    /// <summary>A delayed run happens now with <see cref="EffectOptions.FlushOnDispose"/>, and never otherwise.</summary>
    private void ResolveDelayedRunAfterDispose()
    {
        if (Interlocked.CompareExchange(ref _state, Running, Waiting) != Waiting)
        {
            return;
        }

        if (_flushOnDispose)
        {
            Dispatch();
            return;
        }

        BecomeIdle();
        PendingWork.Remove();
    }

    private void BecomeIdle()
    {
        _flushRequested = false;
        Volatile.Write(ref _state, Idle);
        ReleaseFlushWaiters();
    }

    private Task WaitForRuns(long target)
    {
        lock (_observer)
        {
            if (_completedRuns >= target)
            {
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (_flushWaiters ??= []).Add((target, done));

            return done.Task;
        }
    }

    private void CountCompletedRun()
    {
        lock (_observer)
        {
            _completedRuns++;

            if (_flushWaiters is null)
            {
                return;
            }

            for (var i = _flushWaiters.Count - 1; i >= 0; i--)
            {
                if (_flushWaiters[i].Target <= _completedRuns)
                {
                    _flushWaiters[i].Done.TrySetResult();
                    _flushWaiters.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>Completes every flush: no run is pending any more.</summary>
    private void ReleaseFlushWaiters()
    {
        lock (_observer)
        {
            if (_flushWaiters is null)
            {
                return;
            }

            foreach (var (_, done) in _flushWaiters)
            {
                done.TrySetResult();
            }

            _flushWaiters = null;
        }
    }

    private void Dispatch()
    {
        MarkRunStart();

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
        BecomeIdle();

        if (!IsStopped)
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

        if (_timer is not null)
        {
            Interlocked.Exchange(ref _lastChange, _timeProvider.GetTimestamp());
        }

        while (true)
        {
            switch (Volatile.Read(ref _state))
            {
                case Idle:
                    if (Interlocked.CompareExchange(ref _state, Running, Idle) == Idle)
                    {
                        if (!TryDelay())
                        {
                            Schedule();
                        }

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

                // A requested or delayed run is pending; it sees this change.
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
    private readonly IDisposable? _resource;

    private SyncReactiveEffect(Action effect, EffectOptions? options, IDisposable? resource)
        : base(options)
    {
        _effect = effect;
        _resource = resource;
    }

    private protected override bool IsOwnWrite => t_running == this;

    /// <param name="effect">The code to run.</param>
    /// <param name="options">Error handling, dispatcher and timing.</param>
    /// <param name="resource">Disposed with the effect, for example the subscription whose changes it delivers.</param>
    internal static ReactiveEffect Start(Action effect, EffectOptions? options, IDisposable? resource = null)
    {
        var instance = new SyncReactiveEffect(effect, options, resource);

        var error = instance.Execute();
        if (error is not null)
        {
            var onError = options?.OnError;
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

    private protected override void OnDisposed() => _resource?.Dispose();

    private protected override void RunScheduled()
    {
        var selfTriggeredRuns = 0;

        try
        {
            do
            {
                if (IsStopped)
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
