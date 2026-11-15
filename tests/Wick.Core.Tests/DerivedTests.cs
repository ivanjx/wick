namespace Wick.Core.Tests;

public sealed class DerivedTests
{
    [Fact]
    public void The_value_is_cached_until_a_dependency_changes()
    {
        var state = new AppState { Count = 2 };
        var computations = 0;
        using var doubled = Reactive.Derived(() =>
        {
            computations++;
            return state.Count * 2;
        });

        Assert.Equal(4, doubled.Value);
        Assert.Equal(4, doubled.Value);
        state.Label = "unrelated";
        Assert.Equal(4, doubled.Value);
        Assert.Equal(1, computations);

        state.Count = 5;

        Assert.Equal(10, doubled.Value);
        Assert.Equal(2, computations);
    }

    [Fact]
    public void Readers_are_invalidated_only_when_the_result_changes()
    {
        var state = new AppState { Count = 1 };
        using var isLarge = Reactive.Derived(() => state.Count > 5);
        var observer = new CountingObserver();

        observer.Track(() => isLarge.Value);

        state.Count = 2;
        state.Count = 3;

        Assert.Equal(0, observer.Invalidations);

        state.Count = 6;

        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void An_unobserved_value_is_recomputed_lazily_on_the_next_read()
    {
        var state = new AppState();
        var computations = 0;
        using var label = Reactive.Derived(() =>
        {
            computations++;
            return $"#{state.Count}";
        });

        _ = label.Value;
        state.Count = 1;
        state.Count = 2;

        Assert.Equal(1, computations);
        Assert.Equal("#2", label.Value);
        Assert.Equal(2, computations);
    }

    [Fact]
    public void Derived_values_sharing_a_source_never_combine_old_and_new_values()
    {
        var state = new AppState { Count = 1 };
        using var plusOne = Reactive.Derived(() => state.Count + 1);
        using var twice = Reactive.Derived(() => state.Count * 2);
        var combinations = new List<(int PlusOne, int Twice)>();
        using var both = Reactive.Derived(() =>
        {
            var combination = (plusOne.Value, twice.Value);
            combinations.Add(combination);
            return combination;
        });
        var observer = new CountingObserver();

        observer.Track(() => both.Value);

        for (var count = 2; count <= 5; count++)
        {
            state.Count = count;
        }

        Assert.Equal((6, 10), both.Value);
        Assert.All(combinations, combination => Assert.Equal(combination.PlusOne - 1, combination.Twice / 2));
    }

    [Fact]
    public void An_observer_of_a_source_and_a_derived_value_sees_the_new_derived_value_when_invalidated()
    {
        var state = new AppState { Count = 1 };
        using var doubled = Reactive.Derived(() => state.Count * 2);
        var seen = new List<int>();
        var observer = new CallbackObserver(() => seen.Add(doubled.Value));

        using (observer.BeginTracking())
        {
            _ = (state.Count, doubled.Value);
        }

        state.Count = 7;

        Assert.Equal(14, seen[0]);
    }

    [Fact]
    public void Exceptions_are_cached_and_cleared_by_a_successful_recomputation()
    {
        var state = new AppState();
        var computations = 0;
        using var inverse = Reactive.Derived(() =>
        {
            computations++;
            return 10 / state.Count;
        });

        Assert.Throws<DivideByZeroException>(() => inverse.Value);
        Assert.Throws<DivideByZeroException>(() => inverse.Value);
        Assert.Equal(1, computations);

        state.Count = 2;

        Assert.Equal(5, inverse.Value);
    }

    [Fact]
    public void Reading_itself_while_computing_fails_instead_of_recursing()
    {
        Derived<int>? self = null;
        self = Reactive.Derived(() => self!.Value + 1);

        Assert.Throws<InvalidOperationException>(() => self.Value);
    }

    [Fact]
    public void Writing_state_while_computing_fails()
    {
        var state = new AppState();
        using var writer = Reactive.Derived(() => state.Count = 1);

        Assert.Throws<InvalidOperationException>(() => writer.Value);
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void Disposing_releases_the_subscriptions_and_rejects_reads()
    {
        var state = new AppState();
        var doubled = Reactive.Derived(() => state.Count * 2);

        _ = doubled.Value;

        Assert.Equal(1, state.Node(nameof(AppState.Count))!.ObserverCount);

        doubled.Dispose();

        Assert.Equal(0, state.Node(nameof(AppState.Count))!.ObserverCount);
        Assert.Throws<ObjectDisposedException>(() => doubled.Value);
    }

    private sealed class CallbackObserver(Action onInvalidated) : ReactiveObserver
    {
        protected override void OnInvalidated() => onInvalidated();
    }
}
