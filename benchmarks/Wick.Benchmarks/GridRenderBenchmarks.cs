using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Wick.Benchmarks.Rendering;

namespace Wick.Benchmarks;

/// <summary>
/// The headline: change one cell of a rendered grid and wait until rendering has settled. The classic grid
/// subscribes to change events and re-renders itself and every cell; reactive cells re-render only themselves.
/// </summary>
/// <remarks>
/// The reactive render includes Wick's hop to the thread pool, which lets consecutive writes coalesce: the
/// measurement is the time from the write until the re-rendered batch has been processed.
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class GridRenderBenchmarks
{
    private GridFixture? _fixture;

    [Params(100, 400)]
    public int CellCount { get; set; }

    [GlobalSetup(Target = nameof(Classic))]
    public void SetupClassic() => Setup(GridImplementation.Classic);

    [GlobalSetup(Target = nameof(Reactive))]
    public void SetupReactive() => Setup(GridImplementation.Reactive);

    [GlobalCleanup]
    public void Cleanup() => _fixture?.Dispose();

    [Benchmark(Baseline = true)]
    public void Classic() => _fixture!.UpdateNextCell();

    [Benchmark]
    public void Reactive() => _fixture!.UpdateNextCell();

    private void Setup(GridImplementation implementation)
    {
        _fixture = GridFixture.Create(implementation, CellCount);

        var rendered = _fixture.MeasureRenderedComponentsPerUpdate();
        Console.WriteLine($"// {implementation} grid, {CellCount} cells: {rendered} components re-rendered per update");
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddColumn(new RenderedComponentsColumn());
        }
    }
}
