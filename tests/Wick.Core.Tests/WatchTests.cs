using Microsoft.Extensions.Time.Testing;

namespace Wick.Core.Tests;

public sealed class WatchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void A_watch_skips_the_current_value_and_passes_each_change_with_the_previous_value()
    {
        var state = new AppState();
        var calls = new List<(int Value, int Previous)>();
        using var watch = Reactive.Watch(() => state.Count, (value, previous) => Add(calls, (value, previous)));

        WaitForIdle();
        Assert.Empty(Snapshot(calls));

        state.Count = 1;
        WaitUntil(() => Snapshot(calls).Length == 1);
        state.Count = 5;
        WaitUntil(() => Snapshot(calls).Length == 2);

        Assert.Equal([(1, 0), (5, 1)], Snapshot(calls));
    }

    [Fact]
    public void A_watch_ignores_changes_that_leave_the_watched_value_equal()
    {
        var state = new AppState();
        var calls = new List<(bool Value, bool Previous)>();
        using var watch = Reactive.Watch(() => state.Count > 2, (value, previous) => Add(calls, (value, previous)));

        state.Count = 1;
        WaitForIdle();

        Assert.Empty(Snapshot(calls));

        state.Count = 3;
        WaitUntil(() => Snapshot(calls).Length == 1);

        Assert.Equal([(true, false)], Snapshot(calls));
    }

    [Fact]
    public void A_watch_handler_is_untracked_and_may_write_state()
    {
        var state = new AppState();
        var calls = new List<string>();
        using var watch = Reactive.Watch(() => state.Count, (value, _) =>
        {
            Add(calls, $"{value} {state.Label}");
            state.Details = $"count {value}";
        });

        state.Count = 1;
        WaitUntil(() => Snapshot(calls).Length == 1);
        state.Label = "changed";
        WaitForIdle();

        Assert.Equal(["1 "], Snapshot(calls));
        Assert.Equal("count 1", state.Details);
    }

    [Fact]
    public async Task A_superseded_async_handler_is_cancelled_and_the_next_one_follows_with_the_last_value_passed()
    {
        var state = new AppState();
        var calls = new List<(int Value, int Previous)>();
        using var watch = Reactive.Watch(() => state.Count, async (value, previous, token) =>
        {
            Add(calls, (value, previous));
            if (value == 1)
            {
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            }
        });

        state.Count = 1;
        WaitUntil(() => Snapshot(calls).Length == 1);
        state.Count = 2;
        await PendingWork.WhenIdle().WaitAsync(Timeout);

        Assert.Equal([(1, 0), (2, 1)], Snapshot(calls));
    }

    [Fact]
    public async Task An_async_handler_without_a_token_is_awaited_rather_than_fire_and_forget()
    {
        var state = new AppState();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = Reactive.Watch(() => state.Count, async (_, _) =>
        {
            started.SetResult();
            await release.Task;
        });

        state.Count = 1;
        await started.Task.WaitAsync(Timeout);

        Assert.False(PendingWork.WhenIdle().IsCompleted);

        release.SetResult();
        await PendingWork.WhenIdle().WaitAsync(Timeout);
    }

    [Fact]
    public void A_debounced_watch_passes_the_settled_value_with_the_value_passed_before()
    {
        var time = new FakeTimeProvider();
        var state = new AppState();
        var calls = new List<(int Value, int Previous)>();
        using var watch = Reactive.Watch(
            () => state.Count,
            (value, previous) => Add(calls, (value, previous)),
            new EffectOptions { Debounce = TimeSpan.FromSeconds(1), TimeProvider = time });

        state.Count = 1;
        state.Count = 2;
        state.Count = 3;
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal([(3, 0)], Snapshot(calls));
    }

    [Fact]
    public void An_untracked_read_does_not_make_an_effect_re_run()
    {
        var state = new AppState { Label = "a" };
        var runs = new List<string>();
        using var effect = Reactive.Effect(() => Add(runs, $"{state.Count} {Reactive.Untracked(() => state.Label)}"));

        state.Label = "b";
        WaitForIdle();

        Assert.Equal(["0 a"], Snapshot(runs));

        state.Count = 1;
        WaitUntil(() => Snapshot(runs).Length == 2);

        Assert.Equal(["0 a", "1 b"], Snapshot(runs));
    }

    [Fact]
    public void An_untracked_block_keeps_forbidding_writes_where_tracking_forbids_them()
    {
        var state = new AppState();
        var derived = Reactive.Derived(() => Reactive.Untracked(() => state.Count = 1));

        Assert.Throws<InvalidOperationException>(() => derived.Value);
        Assert.Equal(0, state.Count);
    }

    private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, Timeout), "Timed out waiting for the watch.");

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
