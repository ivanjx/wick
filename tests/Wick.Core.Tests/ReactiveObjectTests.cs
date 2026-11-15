namespace Wick.Core.Tests;

/// <summary>The runtime base class keeps the same contract as generated state.</summary>
public sealed class ReactiveObjectTests
{
    [Fact]
    public void Read_registers_dependency_and_only_a_changed_value_invalidates()
    {
        var state = new RuntimeState();
        var observer = new CountingObserver();

        observer.Track(() => state.Count);
        state.Label = "unrelated";
        state.Count = 0;

        Assert.Equal(0, observer.Invalidations);

        state.Count = 1;

        Assert.Equal(1, observer.Invalidations);
        Assert.Equal(1, state.Node(nameof(RuntimeState.Count))!.ObserverCount);
        Assert.Null(state.Node(nameof(RuntimeState.Label)));
    }

    [Fact]
    public void Mutating_state_while_tracking_throws_and_leaves_the_value_unchanged()
    {
        var state = new RuntimeState();
        var observer = new CountingObserver();

        Assert.Throws<InvalidOperationException>(() => observer.Track(() => state.Count++));

        Assert.Equal(0, state.Count);
    }
}
