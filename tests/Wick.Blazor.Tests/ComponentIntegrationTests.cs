using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components.Web;
using Wick.Blazor.Tests.Components;

namespace Wick.Blazor.Tests;

public sealed class ComponentIntegrationTests : ReactiveTestContext
{
    [Fact]
    public void Reads_in_the_Razor_generated_BuildRenderTree_are_tracked_through_the_base_class()
    {
        var cut = Render<CountView>();

        Assert.Equal(1, cut.Instance.DependencyCount);
        Assert.Equal(1, State.ObserversOf(nameof(AppState.Count)));

        // Plain mutation from the test thread: no StateHasChanged, no InvokeAsync.
        State.Count = 5;

        cut.WaitForAssertion(() => Assert.Equal("5", cut.Find("#count").TextContent), Timeout);
        AssertRenderedOnDispatcherOnly();
    }

    [Fact]
    public void A_mutation_in_an_event_handler_renders_the_component_once()
    {
        var cut = Render<CountView>();

        cut.Find("#increment").Click();
        Settle(cut.Instance);

        Assert.Equal("1", cut.Find("#count").TextContent);
        Assert.Equal(2, cut.RenderCount);
    }

    [Fact]
    public async Task Mutations_after_await_in_an_async_handler_add_no_render_beyond_ComponentBase()
    {
        var cut = Render<CountView>();

        // ComponentBase itself renders after the synchronous part of an async handler and again on completion.
        for (var i = 1; i <= 50; i++)
        {
            var before = cut.RenderCount;

            await cut.Find("#increment-async").ClickAsync(new MouseEventArgs());
            Settle(cut.Instance);

            Assert.Equal((i * 3).ToString(), cut.Find("#count").TextContent);
            Assert.Equal(before + 2, cut.RenderCount);
        }
    }

    [Fact]
    public void An_event_in_one_component_renders_another_component_that_reads_the_changed_state()
    {
        var source = Render<CountView>();
        var label = Render<LabelView>();

        source.Find("#label").Click();
        Settle(source.Instance, label.Instance);

        Assert.Equal("!", label.Find("#label").TextContent);
        Assert.Equal(2, label.RenderCount);
    }

    [Fact]
    public void Components_reading_unrelated_state_do_not_render()
    {
        var count = Render<CountView>();
        var label = Render<LabelView>();

        State.Label = "changed";

        label.WaitForAssertion(() => Assert.Equal("changed", label.Find("#label").TextContent), Timeout);
        Settle(count.Instance, label.Instance);

        Assert.Equal(1, count.RenderCount);
        Assert.Equal(2, label.RenderCount);
    }

    [Fact]
    public void Only_the_child_that_reads_the_state_renders_not_its_parent()
    {
        var parent = Render<ParentView>();
        var child = parent.FindComponent<CountView>();

        State.Count = 1;

        child.WaitForAssertion(() => Assert.Equal("1", child.Find("#count").TextContent), Timeout);
        Settle(parent.Instance, child.Instance);

        Assert.Equal(1, parent.Instance.Renders);
        Assert.Equal(2, child.RenderCount);
    }

    [Fact]
    public void Reads_inside_ChildContent_are_attributed_to_the_reactive_component_that_renders_it()
    {
        var parent = Render<ReactiveFragmentParent>();
        var host = parent.FindComponent<ReactiveFragmentHost>();

        State.Count = 7;

        host.WaitForAssertion(() => Assert.Equal("7", host.Find("#fragment").TextContent), Timeout);
        Settle(parent.Instance, host.Instance);

        Assert.Equal(0, parent.Instance.DependencyCount);
        Assert.Equal(1, parent.Instance.Renders);
        Assert.Equal(2, host.RenderCount);
    }

    [Fact]
    public void Reads_inside_content_rendered_by_a_non_reactive_component_are_not_tracked()
    {
        var parent = Render<UntrackedFragmentParent>();

        State.Count = 7;
        Settle(parent.Instance);

        Assert.Equal(0, parent.Instance.DependencyCount);
        Assert.Equal("0", parent.Find("#untracked").TextContent);
    }

    [Fact]
    public void ReactiveRegion_restores_tracking_inside_content_rendered_by_a_non_reactive_component()
    {
        var parent = Render<RegionFragmentParent>();
        var region = parent.FindComponent<ReactiveRegion>();

        State.Count = 7;

        parent.WaitForAssertion(() => Assert.Equal("7", parent.Find("#tracked").TextContent), Timeout);
        Settle(parent.Instance, region.Instance);

        Assert.Equal(1, parent.Instance.Renders);
    }

    [Fact]
    public void Dependencies_from_a_branch_that_is_no_longer_rendered_are_released()
    {
        State.ShowDetails = true;
        State.Details = "a";
        var cut = Render<DetailsView>();

        Assert.Equal(2, cut.Instance.DependencyCount);

        State.ShowDetails = false;

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#details")), Timeout);
        Settle(cut.Instance);
        var renders = cut.RenderCount;

        Assert.Equal(1, cut.Instance.DependencyCount);
        Assert.Equal(0, State.ObserversOf(nameof(AppState.Details)));

        State.Details = "b";

        Assert.Equal(0, cut.Instance.PendingFlushes);
        Assert.Equal(renders, cut.RenderCount);
    }

    [Fact]
    public void Nested_state_invalidates_only_on_the_property_that_was_read()
    {
        var cut = Render<UserNameView>();

        State.User.Email = "x@example.com";

        Assert.Equal(0, cut.Instance.PendingFlushes);

        State.User.Name = "Ivan";

        cut.WaitForAssertion(() => Assert.Equal("Ivan", cut.Find("#name").TextContent), Timeout);

        State.User = new UserState { Name = "Replaced" };

        cut.WaitForAssertion(() => Assert.Equal("Replaced", cut.Find("#name").TextContent), Timeout);
    }

    [Fact]
    public void A_removed_component_releases_its_subscriptions_and_can_be_collected()
    {
        var host = Render<ToggleHost>(parameters => parameters.AddChildContent<CountView>());
        var component = Registry.Single<CountView>();

        Assert.Equal(1, State.ObserversOf(nameof(AppState.Count)));

        host.Render(parameters => parameters.Add(p => p.Show, false));
        host.Render(parameters => parameters.Add(p => p.Show, false));

        Assert.Equal(0, State.ObserversOf(nameof(AppState.Count)));

        CollectGarbage();

        Assert.False(component.TryGetTarget(out _));
    }

    [Fact]
    public void A_component_that_bypasses_base_Dispose_is_still_collected_and_its_subscription_pruned()
    {
        var host = Render<ToggleHost>(parameters => parameters.AddChildContent<SelfDisposingView>());

        RemoveThenInvalidate(host);
        var component = Registry.Single<SelfDisposingView>();

        CollectGarbage();

        Assert.False(component.TryGetTarget(out _));
        Assert.Equal(1, State.ObserversOf(nameof(AppState.Count)));

        // The next notification finds the dead component and drops the subscription.
        State.Count++;

        Assert.Equal(0, State.ObserversOf(nameof(AppState.Count)));
    }

    [Fact]
    public void Lifecycle_and_parameters_keep_working()
    {
        var host = Render<LifecycleHost>();
        var child = host.FindComponent<LifecycleView>();

        child.WaitForState(() => child.Instance.InitializedAsync == 1, Timeout);

        // Host reads Count and passes it down as a normal parameter.
        State.Count = 3;

        child.WaitForAssertion(() => Assert.Equal("3", child.Find("#value").TextContent), Timeout);
        Settle(host.Instance, child.Instance);
        var hostRenders = host.Instance.Renders;

        Assert.Equal(1, child.Instance.Initialized);
        Assert.Equal(2, child.Instance.ParametersSet);
        Assert.True(child.Instance.AfterRender >= 2);

        // The child reads Label itself, so the host does not render.
        State.Label = "own";

        child.WaitForAssertion(() => Assert.Equal("own", child.Find("#label").TextContent), Timeout);
        Settle(host.Instance, child.Instance);

        Assert.Equal(hostRenders, host.Instance.Renders);
        Assert.Equal(2, child.Instance.ParametersSet);
    }

    [Fact]
    public void ShouldRender_returning_false_does_not_stop_later_invalidations()
    {
        var cut = Render<ShouldRenderView>();

        cut.Instance.AllowRender = false;
        State.Count = 1;
        Settle(cut.Instance);

        Assert.Equal("0", cut.Find("#count").TextContent);

        cut.Instance.AllowRender = true;
        State.Count = 2;

        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("#count").TextContent), Timeout);
    }

    [Fact]
    public void Mutating_reactive_state_during_render_throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Render<MutatingRenderView>());

        Assert.Contains("cannot be mutated", exception.Message);
        Assert.Equal(0, State.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RemoveThenInvalidate(IRenderedComponent<ToggleHost> host)
    {
        Assert.True(Registry.Single<SelfDisposingView>().TryGetTarget(out var target));
        var component = (SelfDisposingView)target;

        // Second render releases Blazor's previous render tree, which still references the child.
        host.Render(parameters => parameters.Add(p => p.Show, false));
        host.Render(parameters => parameters.Add(p => p.Show, false));

        Assert.True(component.OwnDisposeCalled);

        // Base Dispose was bypassed, so the subscription is still there.
        Assert.Equal(1, State.ObserversOf(nameof(AppState.Count)));

        // The stale subscription schedules a render for a disposed component: must be a harmless no-op.
        State.Count++;
        Settle(component);

        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
