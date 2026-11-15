namespace Wick.Core.Tests;

public sealed class AsyncEffectTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [ThreadStatic]
    private static bool t_inDispatcher;

    [Fact]
    public void Tracks_only_the_reads_made_before_the_first_await()
    {
        var state = new AppState();
        var runs = 0;
        using var effect = Reactive.Effect(async () =>
        {
            _ = state.Count;
            Interlocked.Increment(ref runs);

            await Task.Yield();

            _ = state.Label;
        });

        Assert.Equal(1, effect.DependencyCount);

        state.Count = 1;
        WaitUntil(() => Volatile.Read(ref runs) == 2);

        state.Label = "untracked";
        WaitForIdle();

        Assert.Equal(2, Volatile.Read(ref runs));
    }

    [Fact]
    public void A_change_cancels_the_run_in_progress_and_the_next_run_starts_after_it_completed()
    {
        var state = new AppState();
        var started = new ManualResetEventSlim();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        var superseded = CancellationToken.None;
        using var effect = Reactive.Effect(async token =>
        {
            var count = state.Count;
            Add(log, $"start {count}");

            if (count == 1)
            {
                superseded = token;
                started.Set();

                // Ignores its cancellation on purpose: the next run must still wait for it.
                await release.Task;
            }

            Add(log, $"end {count}");
        });

        state.Count = 1;
        Assert.True(started.Wait(Timeout));

        for (var count = 2; count <= 5; count++)
        {
            state.Count = count;
        }

        Assert.True(superseded.IsCancellationRequested);
        Thread.Sleep(100);
        Assert.Equal(["start 0", "end 0", "start 1"], Snapshot(log));

        release.SetResult();

        WaitUntil(() => Snapshot(log)[^1] == "end 5");
        Assert.Equal(["start 0", "end 0", "start 1", "end 1", "start 5", "end 5"], Snapshot(log));
    }

    [Fact]
    public void Failures_are_reported_unless_the_run_was_cancelled()
    {
        var state = new AppState();
        var started = new ManualResetEventSlim();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<Exception>();
        using var effect = Reactive.Effect(
            async () =>
            {
                var count = state.Count;
                if (count == 1)
                {
                    started.Set();
                    await release.Task;

                    throw new InvalidOperationException("superseded");
                }

                await Task.Yield();

                if (count == 3)
                {
                    throw new InvalidOperationException("current");
                }
            },
            exception => Add(errors, exception));

        state.Count = 1;
        Assert.True(started.Wait(Timeout));

        state.Count = 2;
        release.SetResult();
        WaitForIdle();

        Assert.Empty(Snapshot(errors));

        state.Count = 3;
        WaitUntil(() => Snapshot(errors).Length == 1);

        Assert.Equal("current", errors[0].Message);
    }

    [Fact]
    public void Disposing_cancels_the_run_in_progress_and_stops_the_effect()
    {
        var state = new AppState();
        var started = new ManualResetEventSlim();
        var running = CancellationToken.None;
        var runs = 0;
        var errors = new List<Exception>();
        var effect = Reactive.Effect(
            async token =>
            {
                Interlocked.Increment(ref runs);
                if (state.Count == 1)
                {
                    running = token;
                    started.Set();
                    await Task.Delay(System.Threading.Timeout.Infinite, token);
                }
            },
            exception => Add(errors, exception));

        state.Count = 1;
        Assert.True(started.Wait(Timeout));

        effect.Dispose();

        Assert.True(running.IsCancellationRequested);
        Assert.Equal(0, state.Node(nameof(AppState.Count))!.ObserverCount);

        state.Count = 2;
        WaitForIdle();

        Assert.Equal(2, Volatile.Read(ref runs));
        Assert.Empty(Snapshot(errors));
    }

    [Fact]
    public void An_effect_that_keeps_invalidating_itself_after_an_await_is_stopped_and_reported()
    {
        var state = new AppState();
        var errors = new List<Exception>();
        using var effect = Reactive.Effect(
            async () =>
            {
                var count = state.Count;
                await Task.Yield();
                state.Count = count + 1;
            },
            exception => Add(errors, exception));

        WaitUntil(() => Snapshot(errors).Length == 1);
        WaitForIdle();

        Assert.IsType<InvalidOperationException>(errors[0]);
        Assert.Equal(ReactiveEffect.MaxConsecutiveSelfTriggeredRuns + 1, state.Count);
    }

    [Fact]
    public void Re_runs_start_through_the_dispatcher()
    {
        var state = new AppState();
        var syncRuns = new List<bool>();
        var asyncRuns = new List<bool>();
        using var syncEffect = Reactive.Effect(
            () =>
            {
                _ = state.Count;
                Add(syncRuns, t_inDispatcher);
            },
            dispatcher: Dispatch);
        using var asyncEffect = Reactive.Effect(
            async () =>
            {
                _ = state.Count;
                Add(asyncRuns, t_inDispatcher);
                await Task.Yield();
            },
            dispatcher: Dispatch);

        state.Count = 1;
        WaitUntil(() => Snapshot(syncRuns).Length == 2 && Snapshot(asyncRuns).Length == 2);

        // The first runs happen on the creating thread.
        Assert.Equal([false, true], Snapshot(syncRuns));
        Assert.Equal([false, true], Snapshot(asyncRuns));
    }

    [Fact]
    public void A_dispatcher_that_refuses_a_run_is_reported_and_tried_again_on_the_next_change()
    {
        var state = new AppState();
        var errors = new List<Exception>();
        var runs = 0;
        using var effect = Reactive.Effect(
            async () =>
            {
                _ = state.Count;
                Interlocked.Increment(ref runs);
                await Task.Yield();
            },
            exception => Add(errors, exception),
            _ => Task.FromException(new ObjectDisposedException("renderer")));

        state.Count = 1;
        WaitUntil(() => Snapshot(errors).Length == 1);

        state.Count = 2;
        WaitUntil(() => Snapshot(errors).Length == 2);
        WaitForIdle();

        Assert.All(Snapshot(errors), error => Assert.IsType<ObjectDisposedException>(error));
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    private static Task Dispatch(Func<Task> run)
    {
        return Task.Run(() =>
        {
            t_inDispatcher = true;
            try
            {
                return run();
            }
            finally
            {
                t_inDispatcher = false;
            }
        });
    }

    private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, Timeout), "Timed out waiting for the effect.");

    /// <summary>Waits until no effect run is scheduled or in progress anywhere in the process.</summary>
    private static void WaitForIdle() => Assert.True(PendingWork.WhenIdle().Wait(Timeout), "Effects did not settle.");

    private static void Add<T>(List<T> items, T item)
    {
        lock (items)
        {
            items.Add(item);
        }
    }

    private static T[] Snapshot<T>(List<T> items)
    {
        lock (items)
        {
            return [.. items];
        }
    }
}
