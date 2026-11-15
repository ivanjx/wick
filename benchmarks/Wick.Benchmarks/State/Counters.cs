using Wick.Core;

namespace Wick.Benchmarks.State;

[ReactiveState]
public sealed partial class ReactiveCounter
{
    public partial int Value { get; set; }
}

/// <summary>The baseline: a plain auto-property, no tracking and no notification.</summary>
public sealed class PlainCounter
{
    public int Value { get; set; }
}

/// <summary>The cheapest public observer: collects dependencies and ignores invalidations.</summary>
public sealed class NoopObserver : ReactiveObserver
{
    protected override void OnInvalidated()
    {
    }
}
