namespace Wick.Core.Tests;

public sealed class InvalidateTests
{
    [Fact]
    public void Invalidates_the_readers_of_exactly_the_reactive_values_read()
    {
        var state = new AppState { User = { Name = "Ivan" } };
        var name = new CountingObserver();
        var label = new CountingObserver();

        name.Track(() => state.User.Name);
        label.Track(() => state.Label);

        Reactive.Invalidate(() => state.User.Name);

        Assert.Equal(1, name.Invalidations);
        Assert.Equal(0, label.Invalidations);
        Assert.Equal(1, state.Node(nameof(AppState.User))!.ObserverCount);
    }

    [Fact]
    public void Covers_a_mutable_object_changed_in_place()
    {
        var holder = new ListHolder();
        var observer = new CountingObserver();

        Assert.Equal(0, observer.Track(() => holder.Items.Count));

        holder.Items.Add(1);

        Assert.Equal(0, observer.Invalidations);

        Reactive.Invalidate(() => holder.Items);

        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void Calling_it_while_tracking_throws()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        Assert.Throws<InvalidOperationException>(() => observer.Track(() =>
        {
            Reactive.Invalidate(() => state.Count);
            return 0;
        }));
    }
}

[ReactiveState]
internal sealed partial class ListHolder
{
#pragma warning disable WK0006 // Deliberately mutable: the case Reactive.Invalidate exists for.
    public partial List<int> Items { get; set; } = [];
#pragma warning restore WK0006
}
