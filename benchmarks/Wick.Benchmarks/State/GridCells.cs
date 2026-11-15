using Wick.Core;

namespace Wick.Benchmarks.State;

/// <summary>Same shape as the sample's reactive grid cell.</summary>
[ReactiveState]
public sealed partial class GridCell
{
    public partial int Value { get; set; }
}

/// <summary>Same shape as the sample's conventional grid cell: a plain value plus a change event the UI subscribes to.</summary>
public sealed class ClassicGridCell
{
    public int Value { get; private set; }

    public event Action? Changed;

    public void Increment()
    {
        Value++;
        Changed?.Invoke();
    }
}
