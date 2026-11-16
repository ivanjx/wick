namespace Wick.Core.Tests;

public sealed class EqualityTests
{
    [Fact]
    public void A_reference_equality_property_notifies_for_an_equal_new_instance_but_not_for_the_same_one()
    {
        var state = new EqualityState { Content = new string('a', 3) };
        var observer = new CountingObserver();
        observer.Track(() => state.Content);

        state.Content = state.Content;
        Assert.Equal(0, observer.Invalidations);

        state.Content = new string('a', 3);
        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void A_never_equal_property_notifies_for_every_write()
    {
        var state = new EqualityState();
        var observer = new CountingObserver();
        observer.Track(() => state.Pulse);

        state.Pulse = 0;
        state.Pulse = 0;

        Assert.Equal(2, observer.Invalidations);
    }

    [Fact]
    public void A_property_with_a_comparer_notifies_only_for_values_it_considers_different()
    {
        var state = new EqualityState { Search = "wick" };
        var observer = new CountingObserver();
        observer.Track(() => state.Search);

        state.Search = "WICK";
        Assert.Equal(0, observer.Invalidations);
        Assert.Equal("wick", state.Search);

        state.Search = "wicks";
        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void A_reactive_value_uses_its_comparer()
    {
        var content = new ReactiveValue<string>(new string('a', 3), comparer: ReferenceEqualityComparer.Instance);
        var observer = new CountingObserver();
        observer.Track(() => content.Value);

        content.Value = content.Value;
        Assert.Equal(0, observer.Invalidations);

        content.Value = new string('a', 3);
        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void A_reactive_object_property_uses_the_comparer_passed_to_Set()
    {
        var state = new ComparedObject { Search = "wick" };
        var observer = new CountingObserver();
        observer.Track(() => state.Search);

        state.Search = "WICK";
        Assert.Equal(0, observer.Invalidations);

        state.Search = "wicks";
        Assert.Equal(1, observer.Invalidations);
    }

    private sealed class ComparedObject : ReactiveObject
    {
        private static readonly IgnoreCaseComparer s_comparer = new();

        public string Search { get => Get(ref field); set => Set(ref field, value, s_comparer); } = "";
    }
}
