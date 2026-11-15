namespace Wick.Core.Tests;

public sealed class GeneratedStateTests
{
    [Fact]
    public void Derived_state_tracks_inherited_and_own_properties_separately()
    {
        var state = new DerivedState();
        var observer = new CountingObserver();

        observer.Track(() => (state.A, state.B));
        state.A = 1;
        state.B = 1;

        Assert.Equal(2, observer.Invalidations);
        Assert.Equal(1, state.Node(nameof(DerivedState.A))!.ObserverCount);
        Assert.Equal(1, state.Node(nameof(DerivedState.B))!.ObserverCount);
        Assert.NotSame(state.Node(nameof(DerivedState.A)), state.Node(nameof(DerivedState.B)));
    }

    [Fact]
    public void Nodes_are_created_only_for_properties_read_while_tracking()
    {
        var state = new AppState { Count = 1, Label = "x" };

        _ = state.Label;
        new CountingObserver().Track(() => state.Count);

        Assert.NotNull(state.Node(nameof(AppState.Count)));
        Assert.Null(state.Node(nameof(AppState.Label)));
        Assert.Null(state.Node("Unknown"));
    }
}
