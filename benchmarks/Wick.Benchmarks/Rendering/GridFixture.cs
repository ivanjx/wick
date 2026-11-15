using Wick.Benchmarks.Components;
using Wick.Benchmarks.State;

namespace Wick.Benchmarks.Rendering;

public enum GridImplementation
{
    /// <summary>Change events plus <c>InvokeAsync(StateHasChanged)</c> on the grid, as in the sample's classic grid.</summary>
    Classic,

    /// <summary>Reactive cells read by <c>ReactiveComponentBase</c> cell components.</summary>
    Reactive,
}

/// <summary>A rendered grid of <see cref="CellCount"/> cells whose values can be changed one at a time.</summary>
internal abstract class GridFixture : IDisposable
{
    private int _next;

    private protected GridFixture(int cellCount)
    {
        CellCount = cellCount;
    }

    public int CellCount { get; }

    protected HeadlessRenderer Renderer { get; } = new();

    public static GridFixture Create(GridImplementation implementation, int cellCount) => implementation switch
    {
        GridImplementation.Classic => new ClassicGridFixture(cellCount),
        GridImplementation.Reactive => new ReactiveGridFixture(cellCount),
        _ => throw new ArgumentOutOfRangeException(nameof(implementation)),
    };

    /// <summary>Changes the next cell, cycling through the grid, and waits until the resulting render has settled.</summary>
    public void UpdateNextCell()
    {
        var settled = Renderer.SettledBatches;

        Increment(_next);
        _next = _next + 1 == CellCount ? 0 : _next + 1;

        Renderer.WaitForRenderAfter(settled);
    }

    /// <summary>Counts the component renders caused by one cell update.</summary>
    public long MeasureRenderedComponentsPerUpdate()
    {
        var rendered = Renderer.RenderedComponents;

        UpdateNextCell();

        // Leave time for any render that would follow the first one, so the count covers the whole update.
        Thread.Sleep(TimeSpan.FromMilliseconds(100));

        return Renderer.RenderedComponents - rendered;
    }

    public void Dispose() => Renderer.Dispatcher.InvokeAsync(Renderer.Dispose).GetAwaiter().GetResult();

    protected abstract void Increment(int index);

    private sealed class ClassicGridFixture : GridFixture
    {
        private readonly ClassicGridCell[] _cells;

        public ClassicGridFixture(int cellCount)
            : base(cellCount)
        {
            _cells = [.. Enumerable.Range(0, cellCount).Select(_ => new ClassicGridCell())];
            Renderer.RenderRoot<ClassicGridView>(new Dictionary<string, object?> { [nameof(ClassicGridView.Cells)] = _cells });
        }

        protected override void Increment(int index) => _cells[index].Increment();
    }

    private sealed class ReactiveGridFixture : GridFixture
    {
        private readonly GridCell[] _cells;

        public ReactiveGridFixture(int cellCount)
            : base(cellCount)
        {
            _cells = [.. Enumerable.Range(0, cellCount).Select(_ => new GridCell())];
            Renderer.RenderRoot<ReactiveGridView>(new Dictionary<string, object?> { [nameof(ReactiveGridView.Cells)] = _cells });
        }

        protected override void Increment(int index) => _cells[index].Value++;
    }
}
