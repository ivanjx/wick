using Wick.Core;

namespace Wick.Blazor.Tests.Components;

[ReactiveState]
public sealed partial class UserState
{
    public partial string Name { get; set; } = "";

    public partial string Email { get; set; } = "";
}

[ReactiveState]
public sealed partial class AppState
{
    public partial int Count { get; set; }

    public partial string Label { get; set; } = "";

    public partial bool ShowDetails { get; set; }

    public partial string Details { get; set; } = "";

    public partial UserState User { get; set; } = new();
}

[ReactiveState]
public sealed partial class CellState
{
    public partial int Value { get; set; }
}

internal static class ReactiveStateExtensions
{
    public static int ObserversOf(this IReactiveState state, string property) => state.FindNode(property)?.ObserverCount ?? 0;
}

/// <summary>Conventional Blazor state service: an event the UI subscribes to and re-renders from.</summary>
public sealed class ClassicCellState
{
    public int Value { get; private set; }

    public event Action? Changed;

    public void Set(int value)
    {
        Value = value;
        Changed?.Invoke();
    }
}
