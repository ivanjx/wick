namespace Wick.Core.Tests;

public sealed class EffectTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Runs_immediately_and_again_after_a_dependency_changes()
    {
        var state = new AppState();
        var seen = new List<int>();
        using var effect = Reactive.Effect(() =>
        {
            lock (seen)
            {
                seen.Add(state.Count);
            }
        });

        Assert.Equal([0], seen);

        state.Label = "unrelated";
        state.Count = 1;

        WaitUntil(() => Last(seen) == 1);
    }

    [Fact]
    public void Changes_made_during_a_run_coalesce_into_one_more_run()
    {
        var state = new AppState();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var seen = new List<int>();
        using var effect = Reactive.Effect(() =>
        {
            var count = state.Count;
            lock (seen)
            {
                seen.Add(count);
            }

            if (count == 1)
            {
                entered.Set();
                release.Wait(Timeout);
            }
        });

        state.Count = 1;
        Assert.True(entered.Wait(Timeout));

        for (var count = 2; count <= 10; count++)
        {
            state.Count = count;
        }

        release.Set();

        WaitUntil(() => Last(seen) == 10);
        Assert.Equal([0, 1, 10], Snapshot(seen));
    }

    [Fact]
    public void May_write_state_that_other_observers_react_to()
    {
        var state = new AppState();
        var observer = new CountingObserver();
        using var effect = Reactive.Effect(() => state.Label = $"count {state.Count}");

        observer.Track(() => state.Label);
        state.Count = 3;

        WaitUntil(() => observer.Invalidations == 1);
        Assert.Equal("count 3", state.Label);
    }

    [Fact]
    public void An_effect_that_keeps_invalidating_itself_is_stopped_and_reported()
    {
        var state = new AppState();
        var errors = new List<Exception>();
        using var effect = Reactive.Effect(
            () => state.Count++,
            exception =>
            {
                lock (errors)
                {
                    errors.Add(exception);
                }
            });

        WaitUntil(() => Snapshot(errors).Length == 1);

        Assert.IsType<InvalidOperationException>(errors[0]);
        Assert.Equal(ReactiveEffect.MaxConsecutiveSelfTriggeredRuns + 1, state.Count);
    }

    [Fact]
    public void Exceptions_of_the_first_run_propagate_unless_a_handler_is_given()
    {
        var state = new AppState();

        Assert.Throws<InvalidOperationException>(() => Reactive.Effect(Fail));

        Exception? reported = null;
        using var effect = Reactive.Effect(
            () =>
            {
                if (state.Count == 0)
                {
                    throw new InvalidOperationException();
                }
            },
            exception => reported = exception);

        Assert.IsType<InvalidOperationException>(reported);
    }

    [Fact]
    public void A_failing_run_keeps_the_dependencies_read_before_the_failure()
    {
        var state = new AppState();
        var errors = 0;
        var runs = 0;
        using var effect = Reactive.Effect(
            () =>
            {
                Interlocked.Increment(ref runs);

                if (state.Count % 2 == 1)
                {
                    throw new InvalidOperationException();
                }
            },
            _ => Interlocked.Increment(ref errors));

        state.Count = 1;
        WaitUntil(() => Volatile.Read(ref errors) == 1);

        state.Count = 2;
        WaitUntil(() => Volatile.Read(ref runs) == 3);

        Assert.Equal(1, Volatile.Read(ref errors));
    }

    [Fact]
    public void Disposing_releases_the_subscriptions()
    {
        var state = new AppState();
        var effect = Reactive.Effect(() => _ = (state.Count, state.Label));

        Assert.Equal(2, effect.DependencyCount);

        effect.Dispose();

        Assert.Equal(0, effect.DependencyCount);
        Assert.Equal(0, state.Node(nameof(AppState.Count))!.ObserverCount);
    }

    // A method group: a lambda that only throws would bind to the asynchronous Func<Task> overload.
    private static void Fail() => throw new InvalidOperationException();

    private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, Timeout), "Timed out waiting for the effect.");

    private static T[] Snapshot<T>(List<T> items)
    {
        lock (items)
        {
            return [.. items];
        }
    }

    private static int Last(List<int> items)
    {
        lock (items)
        {
            return items[^1];
        }
    }
}
