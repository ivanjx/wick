using Microsoft.Extensions.Time.Testing;

namespace Wick.Core.Tests;

public sealed class EffectRerunTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    [Fact]
    public void Rerunning_an_idle_effect_runs_it_once_and_counts_as_pending_work()
    {
        var state = new AppState();
        var runs = 0;
        using var effect = Reactive.Effect(() =>
        {
            _ = state.Count;
            Interlocked.Increment(ref runs);
        });

        effect.Rerun();
        WaitForIdle();

        Assert.Equal(2, Volatile.Read(ref runs));
        Assert.Equal(1, effect.DependencyCount);
    }

    [Fact]
    public void Rerunning_cancels_the_async_run_in_progress_and_the_next_run_starts_after_it_completed()
    {
        var state = new AppState();
        var started = new ManualResetEventSlim();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        var superseded = CancellationToken.None;
        var runs = 0;
        using var effect = Reactive.Effect(async token =>
        {
            var run = Interlocked.Increment(ref runs);
            Add(log, $"start {run}");

            if (run == 2)
            {
                superseded = token;
                started.Set();

                // Ignores its cancellation on purpose: its late write must still land before the next run's.
                await release.Task;
            }

            state.Label = $"run {run}";
            Add(log, $"end {run}");
        });

        effect.Rerun();
        Assert.True(started.Wait(Timeout));

        effect.Rerun();

        Assert.True(superseded.IsCancellationRequested);
        Thread.Sleep(100);
        Assert.Equal(["start 1", "end 1", "start 2"], Snapshot(log));

        release.SetResult();
        WaitForIdle();

        Assert.Equal(["start 1", "end 1", "start 2", "end 2", "start 3", "end 3"], Snapshot(log));
        Assert.Equal("run 3", state.Label);
    }

    [Fact]
    public void Reruns_and_changes_made_before_the_next_run_starts_coalesce_into_one_run()
    {
        var state = new AppState();
        var started = new ManualResetEventSlim();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<int>();
        using var effect = Reactive.Effect(async () =>
        {
            var count = state.Count;
            Add(seen, count);

            if (count == 1)
            {
                started.Set();
                await release.Task;
            }
        });

        state.Count = 1;
        Assert.True(started.Wait(Timeout));

        effect.Rerun();
        effect.Rerun();
        state.Count = 2;
        effect.Rerun();

        release.SetResult();
        WaitForIdle();

        Assert.Equal([0, 1, 2], Snapshot(seen));
    }

    [Fact]
    public void A_rerun_inside_a_batch_waits_for_the_batch_and_coalesces_with_its_changes()
    {
        var state = new AppState();
        var seen = new List<int>();
        using var effect = Reactive.Effect(() => Add(seen, state.Count));

        Reactive.Batch(() =>
        {
            effect.Rerun();

            Assert.False(SpinWait.SpinUntil(() => Snapshot(seen).Length > 1, TimeSpan.FromMilliseconds(100)));

            state.Count = 1;
        });

        WaitForIdle();

        Assert.Equal([0, 1], Snapshot(seen));
    }

    [Fact]
    public async Task A_debounced_effect_waits_out_its_debounce_after_a_rerun_unless_flushed()
    {
        var time = new FakeTimeProvider();
        var runs = 0;
        using var effect = Reactive.Effect(
            () => Interlocked.Increment(ref runs),
            new EffectOptions { Debounce = Delay, TimeProvider = time });

        effect.Rerun();
        time.Advance(Delay - TimeSpan.FromMilliseconds(1));

        Assert.Equal(1, Volatile.Read(ref runs));

        // Timer callbacks run inside Advance, and without a dispatcher the run happens right there.
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(2, Volatile.Read(ref runs));

        effect.Rerun();
        await effect.FlushAsync().WaitAsync(Timeout);

        Assert.Equal(3, Volatile.Read(ref runs));
        WaitForIdle();
    }

    [Fact]
    public void Rerunning_a_disposed_effect_does_nothing()
    {
        var state = new AppState();
        var runs = 0;
        var effect = Reactive.Effect(() =>
        {
            _ = state.Count;
            Interlocked.Increment(ref runs);
        });

        effect.Dispose();
        effect.Rerun();
        WaitForIdle();

        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public void A_rerun_racing_with_dispose_leaves_no_pending_work()
    {
        for (var i = 0; i < 200; i++)
        {
            var effect = Reactive.Effect(async token => await Task.Delay(1, token));
            using var go = new ManualResetEventSlim();

            var rerun = Task.Run(() =>
            {
                go.Wait();
                effect.Rerun();
                effect.Rerun();
            });

            go.Set();
            effect.Dispose();

            Assert.True(rerun.Wait(Timeout));
        }

        WaitForIdle();
    }

    [Fact]
    public void An_effect_that_keeps_rerunning_itself_is_stopped_and_reported()
    {
        var errors = new List<Exception>();
        var runs = 0;
        ReactiveEffect? effect = null;
        effect = Reactive.Effect(
            () =>
            {
                Interlocked.Increment(ref runs);
                Volatile.Read(ref effect)?.Rerun();
            },
            new EffectOptions { OnError = exception => Add(errors, exception) });

        using (effect)
        {
            effect.Rerun();

            WaitUntil(() => Snapshot(errors).Length == 1);
            WaitForIdle();

            Assert.IsType<InvalidOperationException>(errors[0]);
            Assert.Equal(ReactiveEffect.MaxConsecutiveSelfTriggeredRuns + 1, Volatile.Read(ref runs));
        }
    }

    [Fact]
    public void Rerunning_a_watch_re_evaluates_its_source_without_calling_the_handler_for_an_unchanged_value()
    {
        var state = new AppState();
        var evaluations = 0;
        var calls = new List<(int Value, int Previous)>();
        using var watch = Reactive.Watch(
            () =>
            {
                Interlocked.Increment(ref evaluations);
                return state.Count;
            },
            (value, previous) => Add(calls, (value, previous)));

        watch.Rerun();
        WaitForIdle();

        Assert.Equal(2, Volatile.Read(ref evaluations));
        Assert.Empty(Snapshot(calls));
    }

    private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, Timeout), "Timed out waiting for the effect.");

    /// <summary>Waits until no effect run is scheduled, delayed or in progress anywhere in the process.</summary>
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
