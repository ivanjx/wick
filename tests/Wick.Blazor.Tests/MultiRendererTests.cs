using Wick.Blazor.Tests.Components;

namespace Wick.Blazor.Tests;

/// <summary>
/// Two renderers with their own dispatchers model two Blazor Server circuits sharing one singleton state.
/// </summary>
public sealed class MultiRendererTests
{
    [Fact]
    public async Task Shared_state_updates_components_in_independent_renderers_concurrently()
    {
        var shared = new AppState();
        await using var first = new Circuit(shared);
        await using var second = new Circuit(shared);
        var a = first.Render<CountView>();
        var b = second.Render<CountView>();

        // Producers on several threads; each renderer flushes on its own dispatcher, so renders of the
        // two renderers can run concurrently on different threads and tracking must stay per thread.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 2_500; i++)
            {
                lock (shared)
                {
                    shared.Count++;
                }
            }
        })));

        a.WaitForAssertion(() => Assert.Equal("10000", a.Find("#count").TextContent), TimeSpan.FromSeconds(10));
        b.WaitForAssertion(() => Assert.Equal("10000", b.Find("#count").TextContent), TimeSpan.FromSeconds(10));

        first.AssertRenderedOnDispatcherOnly();
        second.AssertRenderedOnDispatcherOnly();
        Assert.Equal(1, a.Instance.DependencyCount);
        Assert.Equal(1, b.Instance.DependencyCount);
        Assert.Equal(2, shared.ObserversOf(nameof(AppState.Count)));
    }

    private sealed class Circuit(AppState state) : ReactiveTestContext(state);
}
