using Wick.Blazor.Tests.Components;

namespace Wick.Blazor.Tests;

public sealed class LocalStateTests : ReactiveTestContext
{
    [Fact]
    public async Task A_reactive_value_passed_to_a_child_re_renders_only_the_child()
    {
        var host = Render<LocalCountHost>();
        var hostRenders = host.Instance.Renders;
        var childRenders = Probe.Renders;

        await Task.Run(() => host.Instance.Count.Value = 5);
        await ReactiveRendering.WhenIdleAsync();

        Assert.Equal("5", host.Find("#count").TextContent);
        Assert.Equal(hostRenders, host.Instance.Renders);
        Assert.Equal(1, Probe.Renders - childRenders);
        AssertRenderedOnDispatcherOnly();
    }

    [Fact]
    public async Task A_reactive_value_written_by_an_event_handler_updates_its_reader()
    {
        var host = Render<LocalCountHost>();

        host.Find("#increment").Click();
        await ReactiveRendering.WhenIdleAsync();

        Assert.Equal("1", host.Find("#count").TextContent);
    }

    [Fact]
    public async Task Partial_properties_of_a_ReactiveState_component_re_render_it()
    {
        var cut = Render<LocalStateView>(parameters => parameters.Add(p => p.Title, "Local"));
        var before = Probe.Renders;

        await Task.Run(() => cut.Instance.Count = 3);
        await ReactiveRendering.WhenIdleAsync();

        cut.MarkupMatches("<h2 id=\"title\">Local</h2><p id=\"count\">3</p>");
        Assert.Equal(1, Probe.Renders - before);
        AssertRenderedOnDispatcherOnly();
    }
}
