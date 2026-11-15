using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;

namespace Wick.Blazor.Tests.Components;

/// <summary>
/// Records every render: total count, rendering thread, whether it ran inside the renderer's dispatcher,
/// and whether two renders of the same renderer ever overlapped.
/// </summary>
public sealed class RenderProbe
{
    private int _renders;
    private int _active;
    private int _overlaps;
    private int _offDispatcher;

    public Dispatcher? Dispatcher { get; set; }

    public int Renders => Volatile.Read(ref _renders);

    public int Overlaps => Volatile.Read(ref _overlaps);

    public int OffDispatcher => Volatile.Read(ref _offDispatcher);

    public ConcurrentQueue<int> RenderThreads { get; } = new();

    public Scope Render()
    {
        Interlocked.Increment(ref _renders);
        RenderThreads.Enqueue(Environment.CurrentManagedThreadId);

        if (Interlocked.Increment(ref _active) > 1)
        {
            Interlocked.Increment(ref _overlaps);
        }

        if (Dispatcher is null || !Dispatcher.CheckAccess())
        {
            Interlocked.Increment(ref _offDispatcher);
        }

        return new Scope(this);
    }

    public readonly struct Scope(RenderProbe probe) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref probe._active);
    }
}
