using Microsoft.Extensions.Time.Testing;

namespace Wick.Core.Tests;

public sealed class EffectTimingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    [Fact]
    public void A_debounced_effect_re_runs_once_its_state_has_been_quiet_for_the_delay()
    {
        var time = new FakeTimeProvider();
        var state = new AppState();
        var runs = new List<int>();
        using var effect = Reactive.Effect(
            () => Add(runs, state.Count),
            new EffectOptions { Debounce = Delay, TimeProvider = time });

        state.Count = 1;
        time.Advance(TimeSpan.FromMilliseconds(600));
        state.Count = 2;
        time.Advance(TimeSpan.FromMilliseconds(600));

        Assert.Equal([0], Snapshot(runs));

        // Timer callbacks run inside Advance, and without a dispatcher the run happens right there.
        time.Advance(TimeSpan.FromMilliseconds(400));

        Assert.Equal([0, 2], Snapshot(runs));
        WaitForIdle();
    }

    [Fact]
    public void A_throttled_effect_runs_the_first_change_right_away_and_coalesces_the_next_ones_until_the_interval_ends()
    {
        var time = new FakeTimeProvider();
        var state = new AppState();
        var runs = new List<int>();
        using var effect = Reactive.Effect(
            () => Add(runs, state.Count),
            new EffectOptions { Throttle = Delay, TimeProvider = time });

        time.Advance(Delay);
        state.Count = 1;
        WaitUntil(() => Snapshot(runs).Length == 2);

        state.Count = 2;
        state.Count = 3;
        time.Advance(Delay - TimeSpan.FromMilliseconds(1));

        Assert.Equal([0, 1], Snapshot(runs));

        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal([0, 1, 3], Snapshot(runs));
        WaitForIdle();
    }

    [Fact]
    public async Task Flushing_starts_a_delayed_run_now_and_completes_once_it_has_ended()
    {
        var time = new FakeTimeProvider();
        var state = new AppState();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<int>();
        using var effect = Reactive.Effect(
            async () =>
            {
                var count = state.Count;
                if (count > 0)
                {
                    await release.Task;
                }

                Add(saved, count);
            },
            new EffectOptions { Debounce = TimeSpan.FromHours(1), TimeProvider = time });

        Assert.True(effect.FlushAsync().IsCompletedSuccessfully);

        state.Count = 1;
        var flushed = effect.FlushAsync();

        Assert.False(flushed.IsCompleted);

        release.SetResult();
        await flushed.WaitAsync(Timeout);

        Assert.Equal([0, 1], Snapshot(saved));
        WaitForIdle();
    }

    [Fact]
    public void Disposing_drops_a_delayed_run_unless_the_effect_flushes_on_dispose()
    {
        var time = new FakeTimeProvider();
        var state = new AppState();
        var dropped = new List<int>();
        var flushed = new List<int>();
        var dropping = Reactive.Effect(
            () => Add(dropped, state.Count),
            new EffectOptions { Debounce = Delay, TimeProvider = time });
        var flushing = Reactive.Effect(
            () => Add(flushed, state.Count),
            new EffectOptions { Debounce = Delay, TimeProvider = time, FlushOnDispose = true });

        state.Count = 1;
        dropping.Dispose();
        flushing.Dispose();
        time.Advance(Delay);
        WaitForIdle();

        Assert.Equal([0], Snapshot(dropped));
        Assert.Equal([0, 1], Snapshot(flushed));
        Assert.Equal(0, state.Node(nameof(AppState.Count))!.ObserverCount);
    }

    [Fact]
    public async Task Disposing_an_effect_that_flushes_on_dispose_lets_its_asynchronous_run_complete()
    {
        var state = new AppState();
        var started = new ManualResetEventSlim();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = CancellationToken.None;
        var saved = new List<int>();
        var effect = Reactive.Effect(
            async token =>
            {
                var count = state.Count;
                if (count == 1)
                {
                    running = token;
                    started.Set();
                    await release.Task;
                }

                Add(saved, count);
            },
            new EffectOptions { FlushOnDispose = true });

        state.Count = 1;
        Assert.True(started.Wait(Timeout));

        effect.Dispose();
        state.Count = 2;

        Assert.False(running.IsCancellationRequested);

        release.SetResult();
        await PendingWork.WhenIdle().WaitAsync(Timeout);

        Assert.Equal([0, 1], Snapshot(saved));
    }

    [Fact]
    public void A_debounced_value_catches_up_with_its_source_once_the_source_is_quiet()
    {
        var time = new FakeTimeProvider();
        var state = new AppState { Label = "a" };
        using var label = Reactive.Debounced(() => state.Label, Delay, time);
        var seen = new List<string>();
        using var reader = Reactive.Effect(() => Add(seen, label.Value));

        state.Label = "ab";
        state.Label = "abc";
        time.Advance(Delay);
        WaitUntil(() => Snapshot(seen).Length == 2);
        WaitForIdle();

        Assert.Equal("abc", label.Value);
        Assert.Equal(["a", "abc"], Snapshot(seen));
    }

    [Fact]
    public void Invalid_timing_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Reactive.Effect(() => { }, new EffectOptions { Debounce = Delay, Throttle = Delay }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Reactive.Effect(() => { }, new EffectOptions { Debounce = -Delay }));
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
