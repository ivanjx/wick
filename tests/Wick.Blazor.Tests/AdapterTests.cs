using Microsoft.AspNetCore.Components;
using Wick.Blazor.Tests.Components;

namespace Wick.Blazor.Tests;

public sealed class AdapterTests : ReactiveTestContext
{
    [Fact]
    public void A_reactive_layout_tracks_its_own_reads_and_renders_the_body()
    {
        RenderFragment body = builder => builder.AddContent(0, "page body");
        var cut = Render<CountLayout>(parameters => parameters.Add(layout => layout.Body, body));

        Assert.Equal("page body", cut.Find("main").TextContent);

        State.Count = 4;

        cut.WaitForAssertion(() => Assert.Equal("4", cut.Find("#layout-count").TextContent), Timeout);
    }

    [Fact]
    public async Task Removal_runs_the_async_and_sync_cleanup_hooks_and_releases_subscriptions()
    {
        var host = Render<ToggleHost>(parameters => parameters.AddChildContent<CleanupView>());
        var view = host.FindComponent<CleanupView>().Instance;

        Assert.Equal(1, State.ObserversOf(nameof(AppState.Count)));

        host.Render(parameters => parameters.Add(p => p.Show, false));

        // Subscriptions go first, before the asynchronous cleanup completes.
        Assert.Equal(0, State.ObserversOf(nameof(AppState.Count)));

        await view.AsyncCleanup.Task.WaitAsync(Timeout, Xunit.TestContext.Current.CancellationToken);

        Assert.True(SpinWait.SpinUntil(() => view.Disposed, Timeout));
    }

    [Fact]
    public void Re_implementing_IDisposable_cannot_skip_the_release_of_subscriptions()
    {
        var host = Render<ToggleHost>(parameters => parameters.AddChildContent<ImplementsDisposableView>());
        var view = host.FindComponent<ImplementsDisposableView>().Instance;

        host.Render(parameters => parameters.Add(p => p.Show, false));

        Assert.Equal(0, State.ObserversOf(nameof(AppState.Count)));
        Assert.False(view.OwnDisposeCalled);
    }
}
