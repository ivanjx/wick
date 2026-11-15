using Wick.Blazor.Tests.Components;

namespace Wick.Blazor.Tests;

public sealed class ComponentResourceTests : ReactiveTestContext
{
    [Fact]
    public async Task An_owned_resource_renders_its_loading_state_then_its_value()
    {
        var cut = Render<ResourceView>();

        cut.MarkupMatches("<p>loading</p>");

        cut.Instance.Fetches[0].Result.SetResult("zero");
        await ReactiveRendering.WhenIdleAsync();

        cut.MarkupMatches("<p>zero</p>");

        State.Count = 1;
        await ReactiveRendering.WhenIdleAsync();

        cut.MarkupMatches("<p>loading</p>");
        Assert.Equal(1, cut.Instance.Fetches[1].Count);

        cut.Instance.Fetches[1].Result.SetResult("one");
        await ReactiveRendering.WhenIdleAsync();

        cut.MarkupMatches("<p>one</p>");
    }

    [Fact]
    public void Removing_the_component_cancels_its_resource_fetch()
    {
        var host = Render<ToggleHost>(parameters => parameters.AddChildContent<ResourceView>());
        var view = host.FindComponent<ResourceView>().Instance;
        var fetch = view.Fetches[0];

        host.Render(parameters => parameters.Add(p => p.Show, false));

        Assert.True(fetch.Token.IsCancellationRequested);

        State.Count = 1;

        Assert.Single(view.Fetches);
    }
}
