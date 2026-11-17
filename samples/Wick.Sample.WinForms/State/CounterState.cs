using Wick.Core;

namespace Wick.Sample.WinForms.State;

[ReactiveState]
public sealed partial class CounterState
{
    public partial int Count { get; set; }

    public partial int Left { get; set; }

    public partial int Right { get; set; }

    public partial bool Ticking { get; set; } = true;

    // Thread of the last write, set in the same batch as the value so readers see a matching pair.
    public partial int WriterThread { get; private set; }

    public void Increment() => Reactive.Batch(() =>
    {
        WriterThread = Environment.CurrentManagedThreadId;
        Count++;
    });

    // One notification for both writes: readers of Left and Right re-render once, never seeing a half-update.
    public void BumpBoth() => Reactive.Batch(() =>
    {
        WriterThread = Environment.CurrentManagedThreadId;
        Left++;
        Right += 10;
    });
}
