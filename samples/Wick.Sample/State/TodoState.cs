using System.Collections.Immutable;
using Wick.Core;

namespace Wick.Sample.State;

[ReactiveState]
public sealed partial class TodoItem
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public partial string Title { get; set; } = "";

    public partial bool Done { get; set; }
}

public enum TodoFilter
{
    All,
    Active,
    Completed,
}

/// <summary>
/// Items live in a <see cref="ReactiveList{T}"/>: adding or removing re-renders the readers of the list, while
/// toggling or renaming an item re-renders only the components that read that item. <see cref="Visible"/> and
/// <see cref="Remaining"/> are cached derived values, and an effect writes the activity log.
/// </summary>
[ReactiveState]
public sealed partial class TodoState : IDisposable
{
    private const int MaxActivity = 8;

    private static readonly IEqualityComparer<ImmutableArray<TodoItem>> SameItems =
        EqualityComparer<ImmutableArray<TodoItem>>.Create((left, right) => left.SequenceEqual(right));

    private readonly Derived<ImmutableArray<TodoItem>> _visible;
    private readonly Derived<int> _remaining;
    private readonly ReactiveEffect _logRemaining;
    private readonly ReactiveEffect _logFilter;

    public TodoState()
    {
        // Readers of Visible re-render only when the set of shown items changes, not on every toggle.
        _visible = Reactive.Derived(
            () => Filter switch
            {
                TodoFilter.Active => [.. Items.Where(item => !item.Done)],
                TodoFilter.Completed => [.. Items.Where(item => item.Done)],
                _ => Items.Snapshot(),
            },
            SameItems);

        _remaining = Reactive.Derived(() => Items.Count(item => !item.Done));

        // Effects may write state: the log is reactive too. Re-runs happen on the thread pool.
        _logRemaining = Reactive.Effect(() => Log($"{Remaining} of {Items.Count} left"));

        // A watch reacts to changes only, and receives the value it replaces.
        _logFilter = Reactive.Watch(() => Filter, (filter, previous) => Log($"Filter {previous} → {filter}"));
    }

    public ReactiveList<TodoItem> Items { get; } =
    [
        new() { Title = "Read theory.md", Done = true },
        new() { Title = "Prove the assumptions", Done = true },
        new() { Title = "Write the source generator", Done = true },
        new() { Title = "Build reactive collections", Done = true },
        new() { Title = "Add derived state and effects", Done = true },
        new() { Title = "Close the adapter gaps" },
    ];

    public ReactiveList<string> Activity { get; } = [];

    public partial TodoFilter Filter { get; set; }

    public ImmutableArray<TodoItem> Visible => _visible.Value;

    public int Remaining => _remaining.Value;

    public void Add(string title) => Items.Add(new TodoItem { Title = title });

    public void Remove(TodoItem item) => Items.Remove(item);

    public void ClearCompleted() => Items.RemoveAll(item => item.Done);

    public void CompleteAll() => Reactive.Batch(() =>
    {
        foreach (var item in Items)
        {
            item.Done = true;
        }
    });

    private void Log(string entry)
    {
        Activity.Insert(0, $"{DateTime.Now:HH:mm:ss} · {entry}");

        // Untracked: an effect that read the log while writing it would re-run for its own writes.
        if (Reactive.Untracked(() => Activity.Count) > MaxActivity)
        {
            Activity.RemoveAt(MaxActivity);
        }
    }

    // Scoped per circuit: the container disposes the state when the circuit ends.
    public void Dispose()
    {
        _logRemaining.Dispose();
        _logFilter.Dispose();
        _visible.Dispose();
        _remaining.Dispose();
    }
}
