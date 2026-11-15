using Wick.Core;

namespace Wick.Sample.State;

[ReactiveState]
public sealed partial class GridCell
{
    public partial int Value { get; set; }
}

/// <summary>Conventional Blazor state: a plain value plus a change event the UI subscribes to.</summary>
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

/// <summary>
/// Two identical grids, one reactive and one event-driven. Every update is applied to both so the
/// render counts can be compared side by side.
/// </summary>
[ReactiveState]
public sealed partial class GridState : IDisposable
{
    public const int Columns = 20;
    public const int Rows = 20;

    private readonly Lock _gate = new();
    private CancellationTokenSource? _autoUpdate;

    public GridCell[] Cells { get; } = [.. Enumerable.Range(0, Columns * Rows).Select(_ => new GridCell())];

    public ClassicGridCell[] ClassicCells { get; } = [.. Enumerable.Range(0, Columns * Rows).Select(_ => new ClassicGridCell())];

    public partial bool IsRunning { get; set; }

    public void IncrementRandomCell()
    {
        // Button clicks and the background loop can update concurrently; the library tracks and
        // notifies from any thread, but keeping the state itself consistent is up to the state.
        lock (_gate)
        {
            var index = Random.Shared.Next(Cells.Length);

            Cells[index].Value++;
            ClassicCells[index].Increment();
        }
    }

    public void StartAutoUpdate()
    {
        if (IsRunning)
        {
            return;
        }

        _autoUpdate = new CancellationTokenSource();
        IsRunning = true;

        // Task.Run so the loop runs on the thread pool rather than on the circuit's dispatcher.
        var token = _autoUpdate.Token;
        _ = Task.Run(() => RunAutoUpdateAsync(token), token);
    }

    public void StopAutoUpdate()
    {
        _autoUpdate?.Cancel();
        _autoUpdate?.Dispose();
        _autoUpdate = null;
        IsRunning = false;
    }

    public void Dispose() => StopAutoUpdate();

    private async Task RunAutoUpdateAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                IncrementRandomCell();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
