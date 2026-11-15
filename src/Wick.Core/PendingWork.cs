namespace Wick.Core;

/// <summary>
/// Counts scheduled reactive work that has not completed yet (component re-renders, effect re-runs), so that tests
/// can await the moment nothing is pending. Work scheduled by running work is counted before the running work ends,
/// so a chain of effects and renders keeps the count above zero until its last link completes.
/// </summary>
/// <remarks>
/// Costs one interlocked operation when work is scheduled and one when it completes; nothing is allocated unless
/// someone waits.
/// </remarks>
internal static class PendingWork
{
    private static readonly Lock s_gate = new();
    private static int s_count;
    private static TaskCompletionSource? s_idle;

    internal static void Add() => Interlocked.Increment(ref s_count);

    internal static void Remove()
    {
        // The decrement is a full fence, paired with the one in WhenIdle: either the waiter reads zero or this
        // reads the waiter's task source.
        if (Interlocked.Decrement(ref s_count) == 0 && Volatile.Read(ref s_idle) is not null)
        {
            CompleteIfIdle();
        }
    }

    internal static Task WhenIdle()
    {
        if (Volatile.Read(ref s_count) == 0)
        {
            return Task.CompletedTask;
        }

        Task task;
        lock (s_gate)
        {
            s_idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task = s_idle.Task;
        }

        // Work may have completed before the task source was published and missed it.
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref s_count) == 0)
        {
            CompleteIfIdle();
        }

        return task;
    }

    private static void CompleteIfIdle()
    {
        TaskCompletionSource? idle;
        lock (s_gate)
        {
            // New work arrived meanwhile: its completion releases the waiters instead.
            if (Volatile.Read(ref s_count) != 0)
            {
                return;
            }

            idle = s_idle;
            s_idle = null;
        }

        idle?.TrySetResult();
    }
}
