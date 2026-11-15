using Wick.Blazor.Tests.Components;

namespace Wick.Blazor.Tests;

/// <summary>Benchmark A from the theory as a deterministic render-count comparison.</summary>
public sealed class FineGrainedRenderingTests : ReactiveTestContext
{
    private const int CellCount = 1_000;

    [Fact]
    public void Changing_one_cell_of_a_reactive_grid_renders_one_component()
    {
        var cells = Enumerable.Range(0, CellCount).Select(_ => new CellState()).ToArray();
        var grid = Render<ReactiveGrid>(parameters => parameters.Add(p => p.Cells, cells));
        var target = grid.FindComponents<ReactiveCell>()[CellCount / 2];
        var before = Probe.Renders;

        cells[CellCount / 2].Value = 1;

        target.WaitForAssertion(() => Assert.Equal("1", target.Find(".cell").TextContent), Timeout);
        Settle(target.Instance);

        Assert.Equal(1, Probe.Renders - before);
        Log($"reactive: {Probe.Renders - before} renders");
    }

    [Fact]
    public void Changing_one_cell_of_a_classic_event_driven_grid_renders_every_component()
    {
        var cells = Enumerable.Range(0, CellCount).Select(_ => new ClassicCellState()).ToArray();
        Render<ClassicGrid>(parameters => parameters.Add(p => p.Cells, cells));
        var before = Probe.Renders;

        // Idle dispatcher: InvokeAsync(StateHasChanged) completes synchronously inside Set.
        cells[CellCount / 2].Set(1);

        Assert.Equal(CellCount + 1, Probe.Renders - before);
        Log($"classic: {Probe.Renders - before} renders");
    }
}
