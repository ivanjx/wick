using Wick.Core;

namespace Wick.Sample.WinForms.State;

[ReactiveState]
public sealed partial class CounterState
{
    public partial int Count { get; set; }

    public partial int Left { get; set; }

    public partial int Right { get; set; }

    public partial bool Ticking { get; set; } = true;

    public void Increment() => Count++;

    // One notification for both writes: readers of Left and Right re-render once, never seeing a half-update.
    public void BumpBoth() => Reactive.Batch(() =>
    {
        Left++;
        Right += 10;
    });
}
