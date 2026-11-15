namespace Wick.Core.Tests;

public sealed class ReactiveValueTests
{
    [Fact]
    public void Readers_are_notified_once_per_distinct_write()
    {
        var count = new ReactiveValue<int>(1);
        var observer = new CountingObserver();

        Assert.Equal(1, observer.Track(() => count.Value));

        count.Value = 1;
        Assert.Equal(0, observer.Invalidations);

        count.Value = 2;
        Assert.Equal(1, observer.Invalidations);
        Assert.Equal(2, count.Value);
    }

    [Fact]
    public void Derived_values_recompute_from_it()
    {
        var count = new ReactiveValue<int>(2);
        using var doubled = Reactive.Derived(() => count.Value * 2);

        Assert.Equal(4, doubled.Value);

        count.Value = 5;

        Assert.Equal(10, doubled.Value);
    }

    [Fact]
    public void Writing_while_tracking_throws()
    {
        var count = new ReactiveValue<int>(0);

        Assert.Throws<InvalidOperationException>(() => new CountingObserver().Track(() => count.Value = 1));
    }

    [Fact]
    public void Its_node_has_the_given_name_or_a_generic_one()
    {
        var named = new ReactiveValue<string>("", "Counter.Filter");
        var unnamed = new ReactiveValue<int>(0);
        var observer = new CountingObserver();

        observer.Track(() => (named.Value, unnamed.Value));

        Assert.Equal(["Counter.Filter", "ReactiveValue<T>"], WickDiagnostics.GetDependencies(observer));
    }
}
