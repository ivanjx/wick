using System.Globalization;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Wick.Benchmarks.Rendering;

/// <summary>
/// Shows how many components one cell update re-renders. Columns are computed in the host process while the
/// benchmarks run in a child process, so the column renders its own grid once per case instead of reading a result.
/// </summary>
internal sealed class RenderedComponentsColumn : IColumn
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(GridImplementation, int), long> _counts = [];

    public string Id => nameof(RenderedComponentsColumn);

    public string ColumnName => "Renders/update";

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => 0;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Dimensionless;

    public string Legend => "Components re-rendered by one cell update";

    public bool IsAvailable(Summary summary) => true;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, summary.Style);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var implementation = Enum.Parse<GridImplementation>(benchmarkCase.Descriptor.WorkloadMethod.Name);
        var cellCount = (int)benchmarkCase.Parameters[nameof(GridRenderBenchmarks.CellCount)]!;

        lock (_gate)
        {
            if (!_counts.TryGetValue((implementation, cellCount), out var count))
            {
                using var fixture = GridFixture.Create(implementation, cellCount);
                count = fixture.MeasureRenderedComponentsPerUpdate();
                _counts.Add((implementation, cellCount), count);
            }

            return count.ToString(CultureInfo.InvariantCulture);
        }
    }

    public override string ToString() => ColumnName;
}
