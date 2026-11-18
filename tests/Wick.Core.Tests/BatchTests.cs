using System.Runtime.CompilerServices;

namespace Wick.Core.Tests;

public sealed class BatchTests
{
    [Fact]
    public void Multiple_writes_in_a_batch_invalidate_each_observer_once()
    {
        var state = new AppState();
        var both = new CountingObserver();
        var labelOnly = new CountingObserver();

        both.Track(() => (state.Count, state.User.Name));
        labelOnly.Track(() => state.Label);

        Reactive.Batch(() =>
        {
            state.Count = 1;
            state.User.Name = "Ivan";
            state.Label = "x";
            state.Count = 2;

            Assert.Equal(0, both.Invalidations);
        });

        Assert.Equal(1, both.Invalidations);
        Assert.Equal(1, labelOnly.Invalidations);
    }

    [Fact]
    public void Nested_batches_flush_when_the_outermost_batch_completes()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        observer.Track(() => (state.Count, state.Label));

        Reactive.Batch(() =>
        {
            state.Count = 1;

            Reactive.Batch(() => state.Label = "inner");

            Assert.Equal(0, observer.Invalidations);
        });

        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void A_failing_batch_still_notifies_for_writes_already_applied()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        observer.Track(() => state.Count);

        Assert.Throws<InvalidOperationException>(() => Reactive.Batch(() =>
        {
            state.Count = 1;
            throw new InvalidOperationException();
        }));

        Assert.Equal(1, observer.Invalidations);

        state.Count = 2;

        Assert.Equal(2, observer.Invalidations);
    }

    [Fact]
    public async Task An_async_batch_defers_writes_across_awaits_and_threads_until_it_completes()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        observer.Track(() => (state.Count, state.Label));

        await Reactive.BatchAsync(async () =>
        {
            state.Count = 1;
            await Task.Yield();

            await Task.Run(() => state.Label = "from another thread");
            Reactive.Batch(() => state.Count = 2);
            await Reactive.BatchAsync(async () =>
            {
                await Task.Yield();
                state.Count = 3;
            });

            Assert.Equal(0, observer.Invalidations);
        });

        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public async Task Writes_outside_the_async_flow_are_not_deferred()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        observer.Track(() => state.Count);

        await Reactive.BatchAsync(async () =>
        {
            Task outside;
            using (ExecutionContext.SuppressFlow())
            {
                outside = Task.Run(() => state.Count = 1);
            }

            await outside;

            Assert.Equal(1, observer.Invalidations);
        });
    }

    [Fact]
    public async Task A_failing_async_batch_still_notifies_and_later_writes_are_immediate()
    {
        var state = new AppState();
        var observer = new CountingObserver();

        observer.Track(() => state.Count);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Reactive.BatchAsync(async () =>
        {
            state.Count = 1;
            await Task.Yield();
            throw new InvalidOperationException();
        }));

        Assert.Equal(1, observer.Invalidations);

        state.Count = 2;

        Assert.Equal(2, observer.Invalidations);
    }

    [Fact]
    public void A_completed_async_batch_kept_reachable_by_work_it_started_does_not_keep_its_state_alive()
    {
        var pending = new TaskCompletionSource();

        var node = BatchAsyncThenForget(pending.Task);
        CollectGarbage();

        Assert.False(node.IsAlive);
        GC.KeepAlive(pending);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BatchAsyncThenForget(Task pending)
    {
        var node = new ReactiveNode();
        var observer = new CountingObserver();
        observer.Track(() =>
        {
            node.Track();
            return 0;
        });

        var batch = Reactive.BatchAsync(() =>
        {
            node.Notify();

            // Outlives the batch: its continuation captures the batch's execution context.
            _ = WaitAsync(pending);

            return Task.CompletedTask;
        });

        Assert.True(batch.IsCompletedSuccessfully);
        Assert.Equal(1, observer.Invalidations);

        return new WeakReference(node);

        static async Task WaitAsync(Task task) => await task;
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
