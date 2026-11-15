using Microsoft.Extensions.DependencyInjection;
using Wick.Blazor.Tests.Components;

namespace Wick.Blazor.Tests;

public abstract class ReactiveTestContext : BunitContext
{
    protected static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    protected ReactiveTestContext(AppState? sharedState = null)
    {
        State = sharedState ?? new AppState();
        Services.AddSingleton(State);
        Services.AddSingleton(Probe);
        Services.AddSingleton(Registry);
        Probe.Dispatcher = Renderer.Dispatcher;
    }

    public AppState State { get; }

    public RenderProbe Probe { get; } = new();

    public ComponentRegistry Registry { get; } = new();

    /// <summary>
    /// Waits with the public <see cref="ReactiveRendering.WhenIdleAsync"/> until every scheduled reactive render and
    /// effect has completed, then checks that the given components indeed have no flush left.
    /// </summary>
    protected static void Settle(params ReactiveComponentCore[] components)
    {
        var settled = ReactiveRendering.WhenIdleAsync().Wait(Timeout);

        Assert.True(settled, "Reactive flushes did not complete.");
        Assert.All(components, component => Assert.Equal(0, component.PendingFlushes));
    }

    public void AssertRenderedOnDispatcherOnly()
    {
        Assert.Equal(0, Probe.OffDispatcher);
        Assert.Equal(0, Probe.Overlaps);
    }

    protected static void Log(string message) => Xunit.TestContext.Current.TestOutputHelper?.WriteLine(message);
}
