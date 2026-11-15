using Wick.Core;

namespace Wick.Sample.State;

[ReactiveState]
public sealed partial class CounterState
{
    public partial int Count { get; set; }

    public partial int Step { get; set; } = 1;

    // Computed properties need nothing special: they read Count, so whoever reads them depends on Count.
    // They are recomputed on every read; use Reactive.Derived to cache an expensive computation.
    public int Doubled => Count * 2;

    public bool IsEven => Count % 2 == 0;

    public void Increment() => Count += Step;

    public void Reset() => Count = 0;
}
