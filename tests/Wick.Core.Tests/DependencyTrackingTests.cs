namespace Wick.Core.Tests;

public sealed class DependencyTrackingTests
{
    [Fact]
    public void Read_registers_dependency_and_write_invalidates()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        observer.Track(() => state.Count);
        state.Count = 1;

        Assert.Equal(1, observer.Invalidations);
        Assert.Equal(1, state.Node(nameof(AppState.Count))!.ObserverCount);
    }

    [Fact]
    public void Writing_an_unrelated_property_does_not_invalidate()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        observer.Track(() => state.Count);
        state.Label = "changed";

        Assert.Equal(0, observer.Invalidations);
    }

    [Fact]
    public void Writing_an_equal_value_does_not_invalidate()
    {
        var state = new AppState { Count = 10 };
        var observer = new CountingObserver();

        observer.Track(() => state.Count);
        state.Count = 10;

        Assert.Equal(0, observer.Invalidations);
    }

    [Fact]
    public void Writing_a_sibling_nested_property_does_not_invalidate()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        observer.Track(() => state.User.Name);
        state.User.Email = "x@example.com";

        Assert.Equal(0, observer.Invalidations);

        state.User.Name = "Ivan";

        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void Replacing_a_nested_object_invalidates_readers_of_its_descendants()
    {
        var state = new AppState();
        var observer = new CountingObserver();
        var oldUser = state.User;

        observer.Track(() => state.User.Name);
        state.User = new UserState { Name = "New" };

        Assert.Equal(1, observer.Invalidations);

        // After re-tracking, the detached object no longer reaches the observer.
        observer.Track(() => state.User.Name);
        oldUser.Name = "stale";

        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void Dependencies_not_read_in_the_latest_tracking_pass_are_released()
    {
        var state = new AppState { ShowDetails = true, Details = "d" };
        var observer = new CountingObserver();

        observer.Track(() => state.ShowDetails ? state.Details : "");
        Assert.Equal(2, observer.DependencyCount);

        state.ShowDetails = false;
        observer.Track(() => state.ShowDetails ? state.Details : "");

        Assert.Equal(1, observer.DependencyCount);
        Assert.Equal(0, state.Node(nameof(AppState.Details))!.ObserverCount);

        state.Details = "ignored";

        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void Disposing_an_observer_releases_every_subscription()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        observer.Track(() => (state.Count, state.Label, state.User.Name));
        observer.Dispose();

        Assert.Equal(0, state.Node(nameof(AppState.Count))!.ObserverCount);
        Assert.Equal(0, state.Node(nameof(AppState.Label))!.ObserverCount);
        Assert.Equal(0, state.User.Node(nameof(UserState.Name))!.ObserverCount);

        state.Count = 1;

        Assert.Equal(0, observer.Invalidations);
    }

    [Fact]
    public void Reads_after_an_observer_was_disposed_during_its_tracking_pass_do_not_subscribe()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        // For example an effect disposed from another thread while its run is still reading state.
        observer.Track(() =>
        {
            _ = state.Count;
            observer.Dispose();

            return state.Label;
        });

        Assert.Equal(0, state.Node(nameof(AppState.Count))!.ObserverCount);
        Assert.Equal(0, state.Node(nameof(AppState.Label))!.ObserverCount);
    }

    [Fact]
    public void An_exception_during_tracking_restores_the_tracking_context()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        Assert.Throws<InvalidOperationException>(() => observer.Track<int>(() =>
        {
            _ = state.Count;
            throw new InvalidOperationException("render failed");
        }));

        Assert.False(ReactiveObserver.IsTracking);

        // The observer is reusable and kept what it read before the failure.
        state.Count = 1;
        Assert.Equal(1, observer.Invalidations);
        observer.Track(() => state.Label);
    }

    [Fact]
    public void Mutating_state_while_tracking_throws_and_leaves_the_value_unchanged()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        Assert.Throws<InvalidOperationException>(() => observer.Track(() => state.Count++));

        Assert.Equal(0, state.Count);
        Assert.False(ReactiveObserver.IsTracking);
    }

    [Fact]
    public void Tracking_does_not_flow_to_other_threads()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        // A dedicated thread: Task.Wait/Result may inline a queued task onto the waiting thread.
        observer.Track(() => RunOnOtherThread(() => state.Label));
        state.Label = "changed";

        Assert.Equal(0, observer.Invalidations);
        Assert.Equal(0, observer.DependencyCount);
    }

    [Fact]
    public async Task Tracking_does_not_flow_into_async_continuations()
    {
        var state = new AppState();
        var observer = new CountingObserver();
        Task continuation;

        using (observer.BeginTracking())
        {
            continuation = ReadLaterAsync(state);
        }

        await continuation;
        state.Label = "changed";

        Assert.Equal(0, observer.Invalidations);

        static async Task ReadLaterAsync(AppState state)
        {
            await Task.Yield();
            _ = state.Label;
        }
    }

    [Fact]
    public void A_write_racing_with_tracking_is_not_lost()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        // Another thread writes after the value was read but before tracking ends.
        observer.Track(() =>
        {
            var value = state.Count;
            RunOnOtherThread(() => state.Count = 42);
            return value;
        });

        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void Concurrent_trackers_on_different_threads_collect_only_their_own_reads()
    {
        var state = new AppState();
        var first = new CountingObserver();
        var second = new CountingObserver();
        using var barrier = new Barrier(2);

        var a = Task.Run(() => TrackRepeatedly(first, () => state.Count));
        var b = Task.Run(() => TrackRepeatedly(second, () => state.Label.Length));
        Task.WaitAll(a, b);

        state.Count++;

        Assert.Equal(1, first.Invalidations);
        Assert.Equal(0, second.Invalidations);

        void TrackRepeatedly(CountingObserver observer, Func<int> read)
        {
            barrier.SignalAndWait();

            for (var i = 0; i < 10_000; i++)
            {
                observer.Track(read);
            }
        }
    }

    [Fact]
    public void Concurrent_writes_and_retracking_keep_the_graph_consistent()
    {
        var users = Enumerable.Range(0, 8).Select(_ => new UserState()).ToArray();
        var observer = new CountingObserver();
        using var stop = new CancellationTokenSource();

        // Subscribed before the first write, even if the tracker task below only starts after the writers finished.
        observer.Track(() => users.Sum(user => user.Name.Length));

        var tracker = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                observer.Track(() => users.Sum(user => user.Name.Length));
            }
        });

        var writers = users.Select((user, index) => Task.Run(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                user.Name = $"{index}:{i}";
            }
        })).ToArray();

        Task.WaitAll(writers);
        stop.Cancel();
        tracker.Wait();

        Assert.True(observer.Invalidations > 0);
        Assert.Equal(users.Length, observer.DependencyCount);
        Assert.All(users, user => Assert.Equal(1, user.Node(nameof(UserState.Name))!.ObserverCount));
    }

    [Fact]
    public void Concurrent_first_reads_share_one_lazily_created_node()
    {
        for (var round = 0; round < 100; round++)
        {
            var state = new AppState();
            var observers = Enumerable.Range(0, 8).Select(_ => new CountingObserver()).ToArray();
            using var barrier = new Barrier(observers.Length);

            RunConcurrently(observers.Select<CountingObserver, Action>(observer => () =>
            {
                barrier.SignalAndWait();
                observer.Track(() => state.Count);
            }));

            state.Count = round + 1;

            Assert.Equal(observers.Length, state.Node(nameof(AppState.Count))!.ObserverCount);
            Assert.All(observers, observer => Assert.Equal(1, observer.Invalidations));
        }
    }

    [Fact]
    public void A_write_racing_with_the_first_read_is_either_seen_or_notified()
    {
        for (var round = 0; round < 1_000; round++)
        {
            var state = new AppState();
            var observer = new CountingObserver();
            using var barrier = new Barrier(2);
            var seen = 0;

            // The node does not exist yet: the writer may find no node while the reader is creating it.
            RunConcurrently(
            [
                () =>
                {
                    barrier.SignalAndWait();
                    seen = observer.Track(() => state.Count);
                },
                () =>
                {
                    barrier.SignalAndWait();
                    state.Count = 1;
                },
            ]);

            Assert.True(seen == 1 || observer.Invalidations == 1, $"Round {round}: stale read without a notification.");
        }
    }

    private static void RunConcurrently(IEnumerable<Action> actions)
    {
        var threads = actions.Select(action => new Thread(() => action())).ToArray();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }
    }

    private static T RunOnOtherThread<T>(Func<T> func)
    {
        T result = default!;
        var thread = new Thread(() => result = func());
        thread.Start();
        thread.Join();

        return result;
    }
}
