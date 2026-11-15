using System.Collections.Concurrent;
using Wick.Blazor.Tests.Components;
using Wick.Core;

namespace Wick.Blazor.Tests;

/// <summary><see cref="WickDiagnostics.Enabled"/> is process-wide, so these tests never run alongside others.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticsCollection
{
    public const string Name = "Diagnostics";
}

[Collection(DiagnosticsCollection.Name)]
public sealed class RenderDiagnosticsTests : ReactiveTestContext
{
    private readonly ConcurrentQueue<string> _messages = new();

    [Fact]
    public void A_change_reports_the_component_and_the_property_that_triggered_the_render()
    {
        var cut = Render<CountView>();

        WithDiagnostics(enabled: true, () =>
        {
            State.Count = 5;
            Settle(cut.Instance);
        });

        Assert.Equal(["CountView re-render: AppState.Count changed"], _messages);
    }

    [Fact]
    public void Changes_coalesced_by_a_batch_are_listed_in_one_report()
    {
        State.ShowDetails = true;
        var cut = Render<DetailsView>();

        WithDiagnostics(enabled: true, () =>
        {
            Reactive.Batch(() =>
            {
                State.Details = "b";
                State.ShowDetails = false;
            });
            Settle(cut.Instance);
        });

        var message = Assert.Single(_messages);
        Assert.StartsWith("DetailsView re-render: ", message);
        Assert.Contains("AppState.Details", message);
        Assert.Contains("AppState.ShowDetails", message);
    }

    [Fact]
    public void Changes_made_while_the_dispatcher_is_busy_are_listed_once_each_in_one_report()
    {
        State.ShowDetails = true;
        var cut = Render<DetailsView>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        WithDiagnostics(enabled: true, () =>
        {
            var busy = Task.Run(() => Renderer.Dispatcher.InvokeAsync(() =>
            {
                entered.Set();
                release.Wait();
            }));
            Assert.True(entered.Wait(Timeout));

            State.Details = "b";
            State.Details = "c";
            State.ShowDetails = false;

            release.Set();
            busy.Wait();
            Settle(cut.Instance);
        });

        Assert.Equal(["DetailsView re-render: AppState.Details, AppState.ShowDetails changed"], _messages);
        Assert.Equal(2, cut.RenderCount);
    }

    [Fact]
    public void GetDependencies_lists_what_the_last_render_read()
    {
        State.ShowDetails = true;
        var cut = Render<DetailsView>();

        Assert.Equal<string>(["AppState.Details", "AppState.ShowDetails"], WickDiagnostics.GetDependencies(cut.Instance).Order());

        State.ShowDetails = false;
        Settle(cut.Instance);

        Assert.Equal(["AppState.ShowDetails"], WickDiagnostics.GetDependencies(cut.Instance));
    }

    [Fact]
    public void With_diagnostics_disabled_the_sink_is_never_called()
    {
        var cut = Render<CountView>();

        WithDiagnostics(enabled: false, () =>
        {
            State.Count = 1;
            Settle(cut.Instance);
        });

        Assert.Equal("1", cut.Find("#count").TextContent);
        Assert.Empty(_messages);
    }

    private void WithDiagnostics(bool enabled, Action action)
    {
        WickDiagnostics.Message += _messages.Enqueue;
        WickDiagnostics.Enabled = enabled;

        try
        {
            action();
        }
        finally
        {
            WickDiagnostics.Enabled = false;
            WickDiagnostics.Message -= _messages.Enqueue;
        }
    }
}
