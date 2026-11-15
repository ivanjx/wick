namespace Wick.Core.Tests;

/// <summary><see cref="WickDiagnostics.Enabled"/> is process-wide, so these tests never run alongside others.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticsCollection
{
    public const string Name = "Diagnostics";
}

[Collection(DiagnosticsCollection.Name)]
public sealed class DiagnosticsTests
{
    [Fact]
    public void Generated_properties_are_named_after_their_type_and_property()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        observer.Track(() => (state.Count, state.User.Name));

        Assert.Equal(["AppState.Count", "AppState.User", "UserState.Name"], WickDiagnostics.GetDependencies(observer));
        Assert.Equal("AppState.Count", state.Node(nameof(AppState.Count))!.Name);
    }

    [Fact]
    public void ReactiveObject_properties_are_named_after_the_runtime_type_and_property()
    {
        var state = new RuntimeState();
        var observer = new CountingObserver();

        observer.Track(() => state.Count);

        Assert.Equal(["RuntimeState.Count"], WickDiagnostics.GetDependencies(observer));
    }

    [Fact]
    public void Collection_and_derived_nodes_have_generic_names()
    {
        var list = new ReactiveList<int>();
        var tags = new ReactiveSet<string>();
        var prices = new ReactiveDictionary<string, int>();
        using var doubled = Reactive.Derived(() => list.Count * 2);
        var observer = new CountingObserver();

        observer.Track(() => (list.Count, list.Snapshot(), tags.Count, prices.Count, prices.Snapshot(), doubled.Value));

        Assert.Equal(
            ["ReactiveList<T>.Count", "ReactiveList<T>", "ReactiveSet<T>", "ReactiveDictionary<TKey, TValue>.Count", "ReactiveDictionary<TKey, TValue>", "Derived<T>"],
            WickDiagnostics.GetDependencies(observer));
        Assert.Equal(["ReactiveList<T>.Count"], WickDiagnostics.GetDependencies(doubled));
    }

    [Fact]
    public void Per_key_nodes_include_the_key_only_when_created_with_diagnostics_enabled()
    {
        var prices = new ReactiveDictionary<string, int> { ["AAPL"] = 1, ["MSFT"] = 2 };
        var tags = new ReactiveSet<string> { "a", "b" };
        var observer = new CountingObserver();

        observer.Track(() => (prices["AAPL"], tags.Contains("a")));

        WickDiagnostics.Enabled = true;
        try
        {
            observer.Track(() => (prices["AAPL"], prices["MSFT"], tags.Contains("a"), tags.Contains("b")));
        }
        finally
        {
            WickDiagnostics.Enabled = false;
        }

        Assert.Equal(
            ["ReactiveDictionary[key]", "ReactiveDictionary[MSFT]", "ReactiveSet[item]", "ReactiveSet[b]"],
            WickDiagnostics.GetDependencies(observer));
    }

    [Fact]
    public void GetDependencies_follows_effects_and_rejects_objects_that_track_nothing()
    {
        var state = new AppState();
        using var effect = Reactive.Effect(() => _ = state.Label);

        Assert.Equal(["AppState.Label"], WickDiagnostics.GetDependencies(effect));
        Assert.Throws<ArgumentException>(() => WickDiagnostics.GetDependencies(state));
    }

    [Fact]
    public void Unnamed_nodes_and_disposed_observers_are_reported_plainly()
    {
        var node = new ReactiveNode();
        var observer = new CountingObserver();

        observer.Track(() =>
        {
            node.Track();
            return 0;
        });

        Assert.Equal(["ReactiveNode"], WickDiagnostics.GetDependencies(observer));

        observer.Dispose();

        Assert.Empty(WickDiagnostics.GetDependencies(observer));
    }

    [Fact]
    public void Change_causes_are_distinct_bounded_and_reset_when_taken()
    {
        var causes = new ChangeCauses();
        var nodes = Enumerable.Range(0, ChangeCauses.MaxListed + 2).Select(i => new ReactiveNode($"N{i}")).ToArray();

        causes.Add(nodes[0]);
        causes.Add(nodes[0]);
        Assert.Equal("N0", causes.Take());
        Assert.Null(causes.Take());

        foreach (var node in nodes)
        {
            causes.Add(node);
        }

        Assert.Equal("N0, N1, N2, N3, N4, N5, N6, N7 and 2 more", causes.Take());
    }
}
