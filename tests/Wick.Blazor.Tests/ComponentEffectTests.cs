using Wick.Blazor.Tests.Components;
using Wick.Core;

namespace Wick.Blazor.Tests;

public sealed class ComponentEffectTests : ReactiveTestContext
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    [Fact]
    public void An_owned_effect_re_runs_while_the_component_is_alive()
    {
        var view = Render<EffectView>().Instance;

        Assert.Equal(1, view.Runs);
        Assert.Equal(0, view.LastSeen);

        State.Count = 5;

        Assert.True(SpinWait.SpinUntil(() => view.LastSeen == 5, Timeout));
        Assert.Equal(2, view.Runs);
    }

    [Fact]
    public void Removing_the_component_stops_its_effects()
    {
        var host = Render<ToggleHost>(parameters => parameters.AddChildContent<EffectView>());
        var view = host.FindComponent<EffectView>().Instance;

        Assert.Equal(1, State.ObserversOf(nameof(AppState.Count)));

        host.Render(parameters => parameters.Add(p => p.Show, false));

        Assert.Equal(0, State.ObserversOf(nameof(AppState.Count)));

        State.Count = 3;

        AssertNoMoreRuns(() => view.Runs, expected: 1);
    }

    [Fact]
    public void Synchronous_disposal_stops_its_effects()
    {
        var view = Render<EffectView>().Instance;

        view.Dispose();

        Assert.Equal(0, State.ObserversOf(nameof(AppState.Count)));

        State.Count = 3;

        AssertNoMoreRuns(() => view.Runs, expected: 1);
    }

    [Fact]
    public void Asynchronous_disposal_stops_its_effects_before_the_async_cleanup_completes()
    {
        var host = Render<ToggleHost>(parameters => parameters.AddChildContent<AsyncEffectView>());
        var view = host.FindComponent<AsyncEffectView>().Instance;

        State.Count = 1;

        Assert.True(SpinWait.SpinUntil(() => view.Runs == 2, Timeout));

        host.Render(parameters => parameters.Add(p => p.Show, false));

        Assert.Equal(0, State.ObserversOf(nameof(AppState.Count)));
        Assert.False(view.Disposed);

        State.Count = 2;

        AssertNoMoreRuns(() => view.Runs, expected: 2);

        view.ReleaseCleanup.SetResult();

        Assert.True(SpinWait.SpinUntil(() => view.Disposed, Timeout));

        State.Count = 3;

        AssertNoMoreRuns(() => view.Runs, expected: 2);
    }

    [Fact]
    public void An_effect_disposed_early_is_harmlessly_disposed_again_with_the_component()
    {
        var view = Render<EffectView>().Instance;

        view.OwnedEffect.Dispose();

        Assert.Equal(0, State.ObserversOf(nameof(AppState.Count)));

        view.Dispose();
        view.Dispose();

        State.Count = 3;

        AssertNoMoreRuns(() => view.Runs, expected: 1);
    }

    [Fact]
    public void An_effect_created_after_disposal_runs_once_and_is_disposed_immediately()
    {
        var view = Render<EffectView>().Instance;
        view.Dispose();

        var runs = 0;
        var effect = view.CreateEffect(() =>
        {
            _ = State.Count;
            Interlocked.Increment(ref runs);
        });

        Assert.NotNull(effect);
        Assert.Equal(1, runs);
        Assert.Equal(0, State.ObserversOf(nameof(AppState.Count)));

        State.Count = 3;

        AssertNoMoreRuns(() => Volatile.Read(ref runs), expected: 1);
    }

    [Fact]
    public void Effects_created_from_many_threads_are_all_disposed_with_the_component()
    {
        var view = Render<EffectView>().Instance;

        Parallel.For(0, 50, i => view.CreateEffect(() => _ = State.Count));

        Assert.Equal(51, State.ObserversOf(nameof(AppState.Count)));

        view.Dispose();

        Assert.Equal(0, State.ObserversOf(nameof(AppState.Count)));
    }

    [Fact]
    public void Effects_re_run_on_the_component_dispatcher_and_async_runs_continue_there()
    {
        var view = Render<DispatcherEffectView>().Instance;

        Assert.True(SpinWait.SpinUntil(() => view.Log.Contains("after 0 True"), Timeout));

        State.Count = 1;

        Assert.True(SpinWait.SpinUntil(() => view.Log.Contains("after 1 True"), Timeout));
        Assert.Contains("sync 0 True", view.Log);
        Assert.Contains("sync 1 True", view.Log);
        Assert.Contains("async 1 True", view.Log);
        Assert.DoesNotContain(view.Log, entry => entry.EndsWith("False", StringComparison.Ordinal));
    }

    [Fact]
    public void Removing_the_component_cancels_its_async_effect_run()
    {
        var host = Render<ToggleHost>(parameters => parameters.AddChildContent<DispatcherEffectView>());
        var view = host.FindComponent<DispatcherEffectView>().Instance;

        State.Count = 2;

        Assert.True(SpinWait.SpinUntil(() => view.Log.Contains("async 2 True"), Timeout));
        Assert.False(view.LastToken.IsCancellationRequested);

        host.Render(parameters => parameters.Add(p => p.Show, false));

        Assert.True(view.LastToken.IsCancellationRequested);
        Assert.DoesNotContain("after 2 True", view.Log);
    }

    [Fact]
    public void A_component_watch_receives_the_changes_of_a_batch_together_on_the_dispatcher()
    {
        var view = Render<ChangesView>().Instance;

        Reactive.Batch(() =>
        {
            State.Items.Add("a");
            State.Items.Add("b");
        });

        Assert.True(SpinWait.SpinUntil(() => view.Log.Length == 1, Timeout));

        State.Items.Remove("a");

        Assert.True(SpinWait.SpinUntil(() => view.Log.Length == 2, Timeout));
        Assert.Equal(["Added a, Added b True", "Removed a True"], view.Log);
    }

    [Fact]
    public void Removing_the_component_stops_its_change_watch()
    {
        var host = Render<ToggleHost>(parameters => parameters.AddChildContent<ChangesView>());
        var view = host.FindComponent<ChangesView>().Instance;

        Assert.True(State.Items.Changes.IsWatched);

        host.Render(parameters => parameters.Add(p => p.Show, false));

        Assert.False(State.Items.Changes.IsWatched);

        State.Items.Add("a");

        AssertNoMoreRuns(() => view.Log.Length, expected: 0);
    }

    /// <summary>Effects re-run asynchronously, so give a run that should not happen time to show up.</summary>
    private static void AssertNoMoreRuns(Func<int> runs, int expected)
    {
        Thread.Sleep(Quiet);

        Assert.Equal(expected, runs());
    }
}
