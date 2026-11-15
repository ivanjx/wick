namespace Wick.Core.Tests;

[ReactiveState]
internal sealed partial class UserState
{
    public partial string Name { get; set; } = "";

    public partial string Email { get; set; } = "";
}

[ReactiveState]
internal sealed partial class AppState
{
    public partial int Count { get; set; }

    public partial string Label { get; set; } = "";

    public partial bool ShowDetails { get; set; }

    public partial string Details { get; set; } = "";

    public partial UserState User { get; set; } = new();
}

[ReactiveState]
internal partial class BaseState
{
    public partial int A { get; set; }
}

[ReactiveState]
internal sealed partial class DerivedState : BaseState
{
    public partial int B { get; set; }
}

/// <summary>Runtime (non-generated) reactive state.</summary>
internal sealed class RuntimeState : ReactiveObject
{
    public int Count { get => Get(ref field); set => Set(ref field, value); }

    public string Label { get => Get(ref field); set => Set(ref field, value); } = "";
}

internal static class ReactiveStateExtensions
{
    public static ReactiveNode? Node(this IReactiveState state, string property) => state.FindNode(property);
}

internal sealed class CountingObserver : ReactiveObserver
{
    private int _invalidations;

    public int Invalidations => Volatile.Read(ref _invalidations);

    public T Track<T>(Func<T> read)
    {
        using (BeginTracking())
        {
            return read();
        }
    }

    protected override void OnInvalidated() => Interlocked.Increment(ref _invalidations);
}
