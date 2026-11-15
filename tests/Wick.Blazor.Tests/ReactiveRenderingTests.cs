using Wick.Blazor.Tests.Components;

namespace Wick.Blazor.Tests;

/// <summary>The pattern a user's bUnit test follows: change state, await <see cref="ReactiveRendering.WhenIdleAsync"/>, assert the markup.</summary>
public sealed class ReactiveRenderingTests : ReactiveTestContext
{
    [Fact]
    public async Task A_change_from_another_thread_is_rendered_once_WhenIdleAsync_completes()
    {
        var cut = Render<CountView>();

        await Task.Run(() => State.Count = 5);
        await ReactiveRendering.WhenIdleAsync().WaitAsync(Timeout);

        Assert.Equal("5", cut.Find("#count").TextContent);
        Assert.Equal(2, cut.RenderCount);
    }

    [Fact]
    public async Task A_click_is_rendered_once_WhenIdleAsync_completes()
    {
        var cut = Render<CountView>();

        cut.Find("#increment").Click();
        await ReactiveRendering.WhenIdleAsync().WaitAsync(Timeout);

        Assert.Equal("1", cut.Find("#count").TextContent);
    }

    [Fact]
    public async Task Reactive_children_of_the_rendered_component_are_awaited()
    {
        var cut = Render<ParentView>();

        await Task.Run(() => State.Count = 3);
        await ReactiveRendering.WhenIdleAsync().WaitAsync(Timeout);

        Assert.Equal("3", cut.Find("#count").TextContent);
        Assert.Equal(1, cut.Instance.Renders);
    }

    [Fact]
    public async Task A_render_triggered_by_an_effect_that_a_change_re_ran_is_awaited()
    {
        var label = Render<LabelView>();
        var effects = Render<EffectView>().Instance;
        effects.CreateEffect(() => State.Label = $"count {State.Count}");

        // The effect re-runs on the thread pool, and only its write schedules the label's render.
        await Task.Run(() => State.Count = 7);
        await ReactiveRendering.WhenIdleAsync().WaitAsync(Timeout);

        Assert.Equal("count 7", label.Find("#label").TextContent);
    }

    [Fact]
    public async Task Bursts_from_several_threads_are_fully_rendered()
    {
        var cut = Render<CountView>();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                State.Count++;
            }
        })));
        await ReactiveRendering.WhenIdleAsync().WaitAsync(Timeout);

        Assert.Equal(State.Count.ToString(), cut.Find("#count").TextContent);
        Assert.Equal(0, cut.Instance.PendingFlushes);
    }
}
