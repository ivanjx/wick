namespace Wick.Core.Tests;

public sealed class ResourceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void The_first_fetch_starts_immediately_and_its_result_becomes_the_value()
    {
        var state = new AppState { Count = 1 };
        var fetches = new FetchLog();
        using var resource = Reactive.Resource(() => state.Count, fetches.Start);

        Assert.Equal(1, fetches[0].Source);
        Assert.True(resource.IsLoading);
        Assert.False(resource.HasValue);
        Assert.Null(resource.Value);

        var settled = resource.WhenSettled;
        Assert.False(settled.IsCompleted);

        fetches[0].Completion.SetResult("one");

        Assert.True(settled.IsCompleted);
        Assert.False(resource.IsLoading);
        Assert.True(resource.HasValue);
        Assert.Equal("one", resource.Value);
        Assert.Null(resource.Error);
    }

    [Fact]
    public void A_resource_without_a_source_fetches_once_and_again_on_reload()
    {
        var fetches = 0;
        using var resource = Reactive.Resource(_ => Task.FromResult(Interlocked.Increment(ref fetches)));

        Assert.False(resource.IsLoading);
        Assert.Equal(1, resource.Value);

        resource.Reload();

        Assert.Equal(2, resource.Value);
        Assert.Equal(2, fetches);
    }

    [Fact]
    public void Readers_are_notified_once_when_a_fetch_completes()
    {
        var fetches = new FetchLog();
        using var resource = Reactive.Resource(() => 1, fetches.Start);
        var observer = new CountingObserver();

        observer.Track(() => (resource.Value, resource.HasValue, resource.IsLoading, resource.Error));

        fetches[0].Completion.SetResult("one");

        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void Value_readers_are_not_notified_by_a_reload_that_keeps_the_value()
    {
        var fetches = new FetchLog();
        using var resource = Reactive.Resource(() => 1, fetches.Start);
        fetches[0].Completion.SetResult("one");
        var observer = new CountingObserver();

        observer.Track(() => resource.Value);

        resource.Reload();
        fetches[1].Completion.SetResult("one");

        Assert.Equal(0, observer.Invalidations);
    }

    [Fact]
    public void A_source_change_cancels_the_fetch_in_flight_and_fetches_the_new_value()
    {
        var state = new AppState { Count = 1 };
        var fetches = new FetchLog();
        using var resource = Reactive.Resource(() => state.Count, fetches.Start);
        fetches[0].Completion.SetResult("one");

        state.Count = 2;

        Assert.Equal(2, fetches[1].Source);
        Assert.True(resource.IsLoading);
        Assert.Equal("one", resource.Value);

        state.Count = 3;

        Assert.True(fetches[1].Token.IsCancellationRequested);
        Assert.Equal(3, fetches[2].Source);

        fetches[2].Completion.SetResult("three");

        Assert.False(resource.IsLoading);
        Assert.Equal("three", resource.Value);
    }

    [Fact]
    public void A_stale_completion_is_ignored_even_when_the_fetch_ignores_its_cancellation()
    {
        var state = new AppState { Count = 1 };
        var fetches = new FetchLog();
        using var resource = Reactive.Resource(() => state.Count, fetches.Start);

        state.Count = 2;
        fetches[0].Completion.SetResult("one");

        Assert.True(resource.IsLoading);
        Assert.False(resource.HasValue);

        state.Count = 3;
        fetches[2].Completion.SetResult("three");
        fetches[1].Completion.SetException(new InvalidOperationException());

        Assert.False(resource.IsLoading);
        Assert.Equal("three", resource.Value);
        Assert.Null(resource.Error);
    }

    [Fact]
    public void A_fetch_cancelled_by_a_source_change_is_not_an_error()
    {
        var state = new AppState { Count = 1 };
        var fetches = new FetchLog();
        using var resource = Reactive.Resource(() => state.Count, fetches.Start);

        state.Count = 2;
        fetches[0].Completion.SetCanceled(fetches[0].Token);

        Assert.Null(resource.Error);
        Assert.True(resource.IsLoading);
    }

    [Fact]
    public void An_equal_source_value_does_not_fetch_again()
    {
        var state = new AppState { Count = 1 };
        var fetches = new FetchLog();
        using var resource = Reactive.Resource(() => state.Count / 10, fetches.Start);

        state.Count = 2;
        state.Label = "unrelated";

        Assert.Equal(1, fetches.Count);
        Assert.False(fetches[0].Token.IsCancellationRequested);
    }

    [Fact]
    public void A_failure_sets_the_error_and_keeps_the_value_until_a_later_success_clears_it()
    {
        var fetches = new FetchLog();
        using var resource = Reactive.Resource(() => 1, fetches.Start);
        fetches[0].Completion.SetResult("one");

        resource.Reload();
        fetches[1].Completion.SetException(new InvalidOperationException());

        Assert.IsType<InvalidOperationException>(resource.Error);
        Assert.False(resource.IsLoading);
        Assert.True(resource.HasValue);
        Assert.Equal("one", resource.Value);

        resource.Reload();

        Assert.True(resource.IsLoading);
        Assert.NotNull(resource.Error);

        fetches[2].Completion.SetResult("two");

        Assert.Null(resource.Error);
        Assert.Equal("two", resource.Value);
    }

    [Fact]
    public void A_fetch_that_throws_synchronously_sets_the_error()
    {
        using var resource = Reactive.Resource<int>(_ => throw new InvalidOperationException());

        Assert.IsType<InvalidOperationException>(resource.Error);
        Assert.False(resource.IsLoading);
        Assert.False(resource.HasValue);
    }

    [Fact]
    public void A_failing_source_sets_the_error_and_fetches_once_it_recovers()
    {
        var state = new AppState();
        var fetches = new FetchLog();
        using var resource = Reactive.Resource(() => 10 / state.Count, fetches.Start);

        Assert.IsType<DivideByZeroException>(resource.Error);
        Assert.False(resource.IsLoading);
        Assert.Equal(0, fetches.Count);

        state.Count = 2;

        Assert.Equal(5, fetches[0].Source);
        Assert.True(resource.IsLoading);
    }

    [Fact]
    public void Reload_cancels_the_fetch_in_flight_and_fetches_with_the_current_source_value()
    {
        var state = new AppState { Count = 1 };
        var fetches = new FetchLog();
        using var resource = Reactive.Resource(() => state.Count, fetches.Start);

        resource.Reload();

        Assert.True(fetches[0].Token.IsCancellationRequested);
        Assert.Equal(1, fetches[1].Source);

        fetches[1].Completion.SetResult("one");
        state.Count = 2;
        fetches[2].Completion.SetResult("two");
        resource.Reload();

        Assert.Equal(2, fetches[3].Source);
        Assert.True(resource.IsLoading);
        Assert.Equal("two", resource.Value);
    }

    [Fact]
    public void Disposing_cancels_the_fetch_in_flight_and_ignores_late_completions()
    {
        var state = new AppState { Count = 1 };
        var fetches = new FetchLog();
        var resource = Reactive.Resource(() => state.Count, fetches.Start);

        resource.Dispose();
        resource.Dispose();

        Assert.True(fetches[0].Token.IsCancellationRequested);
        Assert.True(resource.WhenSettled.IsCompleted);
        Assert.Equal(0, state.Node(nameof(AppState.Count))!.ObserverCount);

        fetches[0].Completion.SetResult("late");
        state.Count = 2;
        resource.Reload();

        Assert.False(resource.HasValue);
        Assert.Null(resource.Value);
        Assert.Equal(1, fetches.Count);
    }

    [Fact]
    public void Reads_made_by_the_fetch_are_not_tracked()
    {
        var state = new AppState { Count = 1 };
        var fetches = new FetchLog();
        using var resource = Reactive.Resource(() => state.Count, async (count, token) =>
        {
            var label = state.Label;
            var result = await fetches.Start(count, token).ConfigureAwait(false);

            return label + result + state.Details;
        });
        fetches[0].Completion.SetResult("one");

        state.Label = "changed";
        state.Details = "changed";

        Assert.Equal(1, fetches.Count);
        Assert.Equal("one", resource.Value);
    }

    [Fact]
    public void A_resource_created_while_tracking_adds_no_dependencies_to_the_tracking_observer()
    {
        var state = new AppState { Count = 1 };
        var fetches = new FetchLog();
        var observer = new CountingObserver();

        using var resource = observer.Track(() => Reactive.Resource(() => state.Count, (count, token) =>
        {
            _ = state.Label;
            return fetches.Start(count, token);
        }));

        WaitUntil(() => fetches.Count == 1);

        Assert.Equal(0, observer.DependencyCount);
        Assert.True(resource.IsLoading);

        state.Count = 2;

        Assert.Equal(0, observer.Invalidations);
        Assert.Equal(2, fetches[1].Source);
    }

    [Fact]
    public async Task Concurrent_source_changes_settle_on_the_result_for_the_latest_source()
    {
        var state = new AppState();
        using var resource = Reactive.Resource(() => state.Count, async (count, _) =>
        {
            // Ignores its cancellation and completes in random order.
            await Task.Delay(Random.Shared.Next(3)).ConfigureAwait(false);

            return count;
        });

        Parallel.For(1, 200, count => state.Count = count);
        state.Count = -1;

        await resource.WhenSettled.WaitAsync(Timeout);

        Assert.Equal(-1, resource.Value);
        Assert.Null(resource.Error);
    }

    private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, Timeout), "Timed out waiting for the fetch.");

    /// <summary>Records every fetch and leaves it in flight until the test completes it.</summary>
    private sealed class FetchLog
    {
        private readonly List<Fetch> _fetches = [];

        public int Count
        {
            get
            {
                lock (_fetches)
                {
                    return _fetches.Count;
                }
            }
        }

        public Fetch this[int index]
        {
            get
            {
                lock (_fetches)
                {
                    return _fetches[index];
                }
            }
        }

        public Task<string> Start(int source, CancellationToken token)
        {
            var fetch = new Fetch(source, token);
            lock (_fetches)
            {
                _fetches.Add(fetch);
            }

            return fetch.Completion.Task;
        }
    }

    private sealed record Fetch(int Source, CancellationToken Token)
    {
        public TaskCompletionSource<string> Completion { get; } = new();
    }
}
